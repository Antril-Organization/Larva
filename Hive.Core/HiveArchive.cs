using System.IO.Compression;

namespace Hive
{
    /// <summary>
    /// A package file (<c>.hive</c>): a zip whose first entry is <c>hive.json</c> (the manifest), then <c>hive.sig</c> (its
    /// signature, when signed), then exactly the files the manifest lists. Reading one checks everything before anything is
    /// written: paths stay inside the package, every file is listed and matches its size and hash, nothing else is there, the
    /// sizes stay within limits (no zip bombs) and, when required, the signature is the expected key's. Packages run no
    /// code when installed.
    /// </summary>
    public static class HiveArchive
    {
        public const string Extension = ".hive";
        public const string ManifestEntry = "hive.json";
        public const string SignatureEntry = "hive.sig";

        /// <summary>The most a package may unpack to, and the most files it may carry.</summary>
        public const long MaxUnpacked = 512L * 1024 * 1024;
        public const int MaxFiles = 20_000;

        /// <summary>
        /// Writes a package: <paramref name="files"/> maps each path in the package to the file it comes from. The manifest's
        /// file list (sizes, hashes) and key are filled in here; with a key, the manifest is signed.
        /// </summary>
        public static HiveManifest Create(string path, HiveManifest manifest, IReadOnlyDictionary<string, string> files, string? privatePem)
        {
            var listed = new List<HiveFile>();
            foreach (var (inPackage, source) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                if (!SafePath(inPackage))
                    throw new HiveException($"`{inPackage}` cannot be a path in a package");
                byte[] bytes = File.ReadAllBytes(source);
                listed.Add(new HiveFile(inPackage, bytes.LongLength, HiveManifest.Hash(bytes)));
            }
            string? fingerprint = privatePem is null ? null : HiveKeys.Fingerprint(HiveKeys.PublicOf(privatePem));
            manifest = manifest with { Files = [.. listed], Key = fingerprint };
            byte[] manifestBytes = manifest.ToBytes();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + ".partial";
            using (var stream = File.Create(temporary))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Write(zip, ManifestEntry, manifestBytes);
                if (privatePem is not null)
                    Write(zip, SignatureEntry, HiveKeys.Sign(manifestBytes, privatePem));
                foreach (var file in listed)
                    Write(zip, file.Path, File.ReadAllBytes(files[file.Path]));
            }
            File.Move(temporary, path, overwrite: true);
            return manifest;

            static void Write(ZipArchive zip, string name, byte[] bytes)
            {
                // The same bytes always make the same package: fixed times, the smallest compression.
                var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes);
            }
        }

        /// <summary>A package read and checked (see the class): its manifest, signature and files, in memory.</summary>
        public sealed record Opened(HiveManifest Manifest, byte[] ManifestBytes, byte[]? Signature, IReadOnlyDictionary<string, byte[]> Files);

        /// <summary>
        /// Reads and checks a package. <paramref name="publicKey"/>: the key that must have signed it (null: a signature is
        /// not checked, but it must be there when <paramref name="requireSignature"/>).
        /// </summary>
        public static Opened Open(byte[] package, string? publicKey, bool requireSignature)
        {
            ZipArchive zip;
            try
            {
                zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
            }
            catch (InvalidDataException e)
            {
                throw new HiveException($"not a Hive package: {e.Message}");
            }
            using (zip)
            {
                if (zip.Entries.Count > MaxFiles + 2)
                    throw new HiveException($"the package has more than {MaxFiles} files");
                var manifestEntry = zip.GetEntry(ManifestEntry) ?? throw new HiveException("the package has no hive.json");
                byte[] manifestBytes = Read(manifestEntry, 1024 * 1024);
                var manifest = HiveManifest.Parse(manifestBytes);
                byte[]? signature = zip.GetEntry(SignatureEntry) is { } signatureEntry ? Read(signatureEntry, 4096) : null;

                if (signature is null && (requireSignature || publicKey is not null))
                    throw new HiveException($"{manifest.Package} {manifest.Version} is not signed (`verify: hashes` in HIVE accepts unsigned packages)");
                if (signature is not null && publicKey is not null)
                {
                    if (manifest.Key != HiveKeys.Fingerprint(publicKey))
                        throw new HiveException($"{manifest.Package} {manifest.Version} says it was signed by key {Short(manifest.Key)}, not by {Short(HiveKeys.Fingerprint(publicKey))}");
                    if (!HiveKeys.Verify(manifestBytes, signature, publicKey))
                        throw new HiveException($"the signature of {manifest.Package} {manifest.Version} does not match: the package was changed or is not its author's");
                }

                long total = manifest.Files.Sum(f => f.Size);
                if (total > MaxUnpacked)
                    throw new HiveException($"the package unpacks to {total} bytes, more than {MaxUnpacked}");
                var listed = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName is ManifestEntry or SignatureEntry)
                        continue;
                    if (entry.FullName.EndsWith('/') && entry.Length == 0)
                        continue; // a folder
                    if (!listed.TryGetValue(entry.FullName, out var file))
                        throw new HiveException($"the package carries `{entry.FullName}`, which its manifest does not list");
                    byte[] bytes = Read(entry, file.Size);
                    if (bytes.LongLength != file.Size || HiveManifest.Hash(bytes) != file.Sha256)
                        throw new HiveException($"`{file.Path}` in {manifest.Package} {manifest.Version} does not match its hash: the package is damaged or was changed");
                    if (!files.TryAdd(file.Path, bytes))
                        throw new HiveException($"the package carries `{file.Path}` twice");
                }
                if (listed.Keys.FirstOrDefault(p => !files.ContainsKey(p)) is { } missing)
                    throw new HiveException($"the package lists `{missing}` but does not carry it");
                return new Opened(manifest, manifestBytes, signature, files);
            }
        }

        /// <summary>Unpacks a checked package into an empty folder (made here), then marks it complete.</summary>
        public static void Extract(Opened package, string folder)
        {
            string root = Path.GetFullPath(folder);
            string partial = root + ".partial";
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            Directory.CreateDirectory(partial);
            foreach (var (path, bytes) in package.Files)
            {
                string target = Path.GetFullPath(Path.Combine(partial, path));
                if (!target.StartsWith(partial + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new HiveException($"`{path}` would leave the package's folder");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            File.WriteAllBytes(Path.Combine(partial, ManifestEntry), package.ManifestBytes);
            if (package.Signature is not null)
                File.WriteAllBytes(Path.Combine(partial, SignatureEntry), package.Signature);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            Directory.Move(partial, root);
        }

        /// <summary>
        /// A path a package may carry: relative, with <c>/</c>, no empty, <c>.</c> or <c>..</c> parts, no drive or device
        /// names, and only plain characters.
        /// </summary>
        public static bool SafePath(string path)
        {
            if (path.Length is 0 or > 240 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':'))
                return false;
            if (path is ManifestEntry or SignatureEntry)
                return false;
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' '))
                    return false;
                if (part.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*'))
                    return false;
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))
                    return false;
            }
            return true;
        }

        private static byte[] Read(ZipArchiveEntry entry, long limit)
        {
            if (entry.Length > limit)
                throw new HiveException($"`{entry.FullName}` is larger than the package says");
            using var input = entry.Open();
            var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                // The real size, not the one the zip claims, is what is limited.
                if (output.Length + read > limit)
                    throw new HiveException($"`{entry.FullName}` is larger than the package says");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }

        public static string Short(string? fingerprint) => fingerprint is null ? "(none)" : fingerprint[..Math.Min(16, fingerprint.Length)];
    }
}
