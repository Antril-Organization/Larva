using System.Text.Json;
using Larva.Build;

namespace Hive
{
    /// <summary>
    /// Installed packages, one folder per version (<c>packages/author/name-&lt;hash&gt;/1.2.0</c>, unpacked and checked, with
    /// the package file's SHA-256 in <c>.sha256</c>), and the keys each package was first installed with (<c>keys.json</c>):
    /// a later version signed by another key is refused. Package names are compared exactly (<c>Acme/Json</c> and
    /// <c>acme/json</c> are two packages), but the file system may not: a package's folder is its name in lower case plus a
    /// short hash of the exact name, so two names that differ only in case never share a folder. The exact name is read
    /// back from the installed manifest.
    /// </summary>
    public sealed class HiveCache(string root)
    {
        public string Root { get; } = Path.GetFullPath(root);

        /// <summary>The user's cache: <c>HIVE_CACHE</c>, else <c>Larva/hive</c> in the local application data.</summary>
        public static HiveCache Default() => new(Environment.GetEnvironmentVariable("HIVE_CACHE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "hive"));

        public static HiveCache For(HiveSettings settings) => settings.Cache is { } cache ? new HiveCache(cache) : Default();

        /// <summary>The name of the installed package file's hash, written in a version's folder when it is complete.</summary>
        public const string StampFile = ".sha256";

        /// <summary>The folder of a package's versions: <c>packages/author/name-&lt;8 hex digits of SHA-256(Author/Name)&gt;</c>.</summary>
        public string PackageFolder(PackageReference package) =>
            Path.Combine(Root, "packages", package.Author.ToLowerInvariant(), $"{package.Name.ToLowerInvariant()}-{HiveManifest.Hash(System.Text.Encoding.UTF8.GetBytes(package.Id))[..8]}");

        public string Folder(PackageReference package, string version) => Path.Combine(PackageFolder(package), version);

        /// <summary>The installed package's project file (what its language compiles with), or null when it is not installed.</summary>
        public string? Project(PackageReference package, string version)
        {
            string folder = Folder(package, version);
            string project = Path.Combine(folder, HivePacker.ProjectFile);
            return File.Exists(project) && File.Exists(Path.Combine(folder, HiveArchive.ManifestEntry)) ? project : null;
        }

        /// <summary>The SHA-256 of the package file an installed version was unpacked from, or null when it is not installed (or not completely).</summary>
        public string? Stamp(PackageReference package, string version)
        {
            string stamp = Path.Combine(Folder(package, version), StampFile);
            try
            {
                return File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Whether <paramref name="version"/> is installed from exactly the file whose hash is <paramref name="sha256"/>.</summary>
        public bool Has(PackageReference package, string version, string sha256) =>
            Project(package, version) is not null && string.Equals(Stamp(package, version), sha256, StringComparison.OrdinalIgnoreCase);

        /// <summary>The installed versions of a package, newest first.</summary>
        public List<string> Versions(PackageReference package)
        {
            string folder = PackageFolder(package);
            if (!Directory.Exists(folder))
                return [];
            return [.. Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>()
                .Where(v => !v.EndsWith(".partial", StringComparison.Ordinal) && VersionRange.Parse(v) is not null && Project(package, v) is not null)
                .OrderByDescending(v => VersionRange.Parse(v)!.Version, Comparer<uint[]>.Create(VersionRange.Compare))];
        }

        /// <summary>Every installed package and version (the exact names read from the installed manifests).</summary>
        public IEnumerable<(PackageReference Package, string Version)> Installed()
        {
            string packages = Path.Combine(Root, "packages");
            if (!Directory.Exists(packages))
                yield break;
            foreach (var author in Directory.EnumerateDirectories(packages).OrderBy(d => d, StringComparer.Ordinal))
            {
                foreach (var name in Directory.EnumerateDirectories(author).OrderBy(d => d, StringComparer.Ordinal))
                {
                    PackageReference? package = null;
                    foreach (var folder in Directory.EnumerateDirectories(name).OrderBy(d => d, StringComparer.Ordinal))
                    {
                        if (ManifestIn(folder) is { } manifest && PackageReference.Parse(manifest.Package) is { } named && PackageFolder(named) == name)
                        {
                            package = named;
                            break;
                        }
                    }
                    if (package is null)
                        continue;
                    foreach (var version in Versions(package))
                        yield return (package, version);
                }
            }
        }

        private static HiveManifest? ManifestIn(string folder)
        {
            string path = Path.Combine(folder, HiveArchive.ManifestEntry);
            try
            {
                return File.Exists(path) ? HiveManifest.Parse(File.ReadAllBytes(path)) : null;
            }
            catch (Exception e) when (e is HiveException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // ── Trusted keys ───────────────────────────────────────

        private string KeysFile => Path.Combine(Root, "keys.json");

        private Dictionary<string, List<string>> LoadKeys() =>
            File.Exists(KeysFile) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllBytes(KeysFile), HiveManifest.Json) ?? [] : [];

        /// <summary>
        /// Checks that <paramref name="fingerprint"/> may sign <paramref name="package"/>: the first key a package is
        /// installed with is remembered, and only the keys remembered for it are accepted afterwards.
        /// </summary>
        public void Trust(PackageReference package, string fingerprint)
        {
            using var held = HiveFileLock.Acquire(KeysFile + ".lock");
            var keys = LoadKeys();
            if (keys.TryGetValue(package.Id, out var known))
            {
                if (known.Contains(fingerprint))
                    return;
                throw new HiveException($"{package.Id} is now signed by key {HiveArchive.Short(fingerprint)}, not by the key it was installed with ({string.Join(", ", known.Select(HiveArchive.Short))}). " +
                    $"If its author changed keys, accept the new one with `hive trust {package.Id} {fingerprint}`");
            }
            keys[package.Id] = [fingerprint];
            SaveKeys(keys);
        }

        /// <summary><c>hive trust</c>: accepts another key for a package.</summary>
        public void AddTrusted(PackageReference package, string fingerprint)
        {
            using var held = HiveFileLock.Acquire(KeysFile + ".lock");
            var keys = LoadKeys();
            if (!keys.TryGetValue(package.Id, out var known))
                keys[package.Id] = known = [];
            if (!known.Contains(fingerprint))
                known.Add(fingerprint);
            SaveKeys(keys);
        }

        /// <summary>Written whole or not at all (a reader never sees half a file), under the keys lock.</summary>
        private void SaveKeys(Dictionary<string, List<string>> keys)
        {
            Directory.CreateDirectory(Root);
            string temporary = $"{KeysFile}.{Environment.ProcessId}.partial";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(keys, HiveManifest.Json));
            File.Move(temporary, KeysFile, overwrite: true);
        }
    }

    /// <summary>
    /// What one larva (process or thread) holds while it changes something in the cache others may be changing too: a
    /// <c>.lock</c> file opened for exclusive access. Others wait for it (retrying briefly), then give up with a message.
    /// </summary>
    internal sealed class HiveFileLock : IDisposable
    {
        private readonly FileStream stream;

        private HiveFileLock(FileStream stream) => this.stream = stream;

        public static HiveFileLock Acquire(string path, TimeSpan? patience = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var limit = Environment.TickCount64 + (long)(patience ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
            while (true)
            {
                try
                {
                    return new HiveFileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1));
                }
                catch (IOException e) when (Environment.TickCount64 >= limit)
                {
                    throw new HiveException($"another larva is using the package cache ({path}) and did not finish in time: {e.Message}");
                }
                catch (IOException)
                {
                    Thread.Sleep(Random.Shared.Next(10, 60));
                }
            }
        }

        public void Dispose() => stream.Dispose();
    }

    /// <summary>A package a lock pins: the exact version, the package file's hash, its signer and where it came from.</summary>
    public sealed record HiveLockEntry(string Package, string Version, string Sha256, string? Key, string Provider, HiveDependency[] Dependencies);

    /// <summary>
    /// <c>hive.lock</c>, beside a project's <c>.queen</c>: the exact packages <c>larva hive install</c> chose, so every machine
    /// builds with the same bytes (<c>hive restore</c>).
    /// </summary>
    public sealed record HiveLock
    {
        public const string FileName = "hive.lock";

        public int Format { get; init; } = 1;
        public List<HiveLockEntry> Packages { get; init; } = [];

        public HiveLockEntry? Find(string package) => Packages.FirstOrDefault(p => p.Package == package);

        public static HiveLock? Load(string projectFolder)
        {
            string path = Path.Combine(projectFolder, FileName);
            if (!File.Exists(path))
                return null;
            try
            {
                return JsonSerializer.Deserialize<HiveLock>(File.ReadAllBytes(path), HiveManifest.Json);
            }
            catch (JsonException e)
            {
                throw new HiveException($"{FileName} is not valid: {e.Message}");
            }
        }

        public void Save(string projectFolder)
        {
            var sorted = this with { Packages = [.. Packages.OrderBy(p => p.Package, StringComparer.Ordinal)] };
            File.WriteAllBytes(Path.Combine(projectFolder, FileName), JsonSerializer.SerializeToUtf8Bytes(sorted, HiveManifest.Json));
        }
    }
}
