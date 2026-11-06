using System.Text.Json;
using Larva.Build;

namespace Hive
{
    /// <summary>
    /// Installed packages, one folder per version (<c>packages/Author/Name/1.2.0</c>, unpacked and checked), and the keys
    /// each package was first installed with (<c>keys.json</c>): a later version signed by another key is refused.
    /// </summary>
    public sealed class HiveCache(string root)
    {
        public string Root { get; } = Path.GetFullPath(root);

        /// <summary>The user's cache: <c>HIVE_CACHE</c>, else <c>Larva/hive</c> in the local application data.</summary>
        public static HiveCache Default() => new(Environment.GetEnvironmentVariable("HIVE_CACHE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "hive"));

        public static HiveCache For(HiveSettings settings) => settings.Cache is { } cache ? new HiveCache(cache) : Default();

        public string Folder(PackageReference package, string version) => Path.Combine(Root, "packages", package.Author, package.Name, version);

        /// <summary>The installed package's project file (what its language compiles with), or null when it is not installed.</summary>
        public string? Project(PackageReference package, string version)
        {
            string folder = Folder(package, version);
            string project = Path.Combine(folder, HivePacker.ProjectFile);
            return File.Exists(project) && File.Exists(Path.Combine(folder, HiveArchive.ManifestEntry)) ? project : null;
        }

        /// <summary>The installed versions of a package, newest first.</summary>
        public List<string> Versions(PackageReference package)
        {
            string folder = Path.Combine(Root, "packages", package.Author, package.Name);
            if (!Directory.Exists(folder))
                return [];
            return [.. Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>()
                .Where(v => !v.EndsWith(".partial", StringComparison.Ordinal) && VersionRange.Parse(v) is not null && Project(package, v) is not null)
                .OrderByDescending(v => VersionRange.Parse(v)!.Version, Comparer<uint[]>.Create(VersionRange.Compare))];
        }

        /// <summary>Every installed package and version.</summary>
        public IEnumerable<(PackageReference Package, string Version)> Installed()
        {
            string packages = Path.Combine(Root, "packages");
            if (!Directory.Exists(packages))
                yield break;
            foreach (var author in Directory.EnumerateDirectories(packages).OrderBy(d => d, StringComparer.Ordinal))
            {
                foreach (var name in Directory.EnumerateDirectories(author).OrderBy(d => d, StringComparer.Ordinal))
                {
                    if (PackageReference.Parse($"{Path.GetFileName(author)}/{Path.GetFileName(name)}") is not { } package)
                        continue;
                    foreach (var version in Versions(package))
                        yield return (package, version);
                }
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
            var keys = LoadKeys();
            if (!keys.TryGetValue(package.Id, out var known))
                keys[package.Id] = known = [];
            if (!known.Contains(fingerprint))
                known.Add(fingerprint);
            SaveKeys(keys);
        }

        private void SaveKeys(Dictionary<string, List<string>> keys)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(KeysFile, JsonSerializer.SerializeToUtf8Bytes(keys, HiveManifest.Json));
        }
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
