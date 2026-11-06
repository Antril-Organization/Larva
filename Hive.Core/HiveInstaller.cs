using Larva.Build;

namespace Hive
{
    /// <summary>
    /// Chooses package versions and installs them. Choosing: every package gets the newest published version that all its
    /// requirements accept (the project's and those of the other chosen packages), keeping what a lock already chose when it
    /// still fits, until nothing changes. Installing: each package file is downloaded, checked against the registry's hash,
    /// opened and checked (signature, files; see <see cref="HiveArchive"/>), its key checked against the keys trusted for the
    /// package, and unpacked into the cache.
    /// </summary>
    public sealed class HiveInstaller(HiveCache cache, IReadOnlyList<HiveProvider> providers, bool requireSignatures, Action<string>? log = null)
    {
        private readonly Dictionary<string, (HiveIndex Index, HiveProvider Provider)> indexes = [];

        /// <summary>Chooses and installs what <paramref name="roots"/> need (a previous lock's choices kept when they fit).</summary>
        public HiveLock Install(IReadOnlyList<PackageReference> roots, HiveLock? previous)
        {
            var chosen = Choose(roots, previous);
            var entries = new List<HiveLockEntry>();
            foreach (var (id, version) in chosen.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                var (index, provider) = IndexOf(PackageReference.Parse(id)!);
                entries.Add(Fetch(PackageReference.Parse(id)!, version, index, provider));
            }
            return new HiveLock { Packages = entries };
        }

        /// <summary><c>hive restore</c>: installs exactly what a lock names, checking each file's hash against the lock's.</summary>
        public void Restore(HiveLock locked)
        {
            foreach (var entry in locked.Packages)
            {
                var package = PackageReference.Parse(entry.Package) ?? throw new HiveException($"{HiveLock.FileName} names `{entry.Package}`");
                var (index, provider) = IndexOf(package);
                var version = index.Versions.FirstOrDefault(v => v.Version == entry.Version)
                    ?? throw new HiveException($"{provider.Address} no longer has {entry.Package} {entry.Version}");
                if (version.Sha256 != entry.Sha256)
                    throw new HiveException($"{entry.Package} {entry.Version} in {provider.Address} is not the file {HiveLock.FileName} pinned: refusing it");
                Fetch(package, version, index, provider);
            }
        }

        private Dictionary<string, HiveIndexVersion> Choose(IReadOnlyList<PackageReference> roots, HiveLock? previous)
        {
            var chosen = new Dictionary<string, HiveIndexVersion>(StringComparer.Ordinal);
            for (int round = 0; round < 100; round++)
            {
                // Every requirement on every package, from the project and from the packages chosen so far.
                var requirements = new Dictionary<string, List<(VersionRange? Range, string From)>>(StringComparer.Ordinal);
                void Require(string id, VersionRange? range, string from)
                {
                    if (!requirements.TryGetValue(id, out var list))
                        requirements[id] = list = [];
                    list.Add((range, from));
                }
                foreach (var root in roots)
                    Require(root.Id, root.Version, "the project");
                foreach (var (id, version) in chosen)
                {
                    foreach (var dependency in version.Dependencies)
                        Require(dependency.Package, dependency.Version.Length > 0 ? VersionRange.Parse(dependency.Version) : null, $"{id} {version.Version}");
                }

                var next = new Dictionary<string, HiveIndexVersion>(StringComparer.Ordinal);
                foreach (var (id, list) in requirements)
                {
                    var (index, provider) = IndexOf(PackageReference.Parse(id)!);
                    string? locked = previous?.Find(id)?.Version;
                    var fitting = index.Versions
                        .Where(v => VersionRange.Parse(v.Version) is { } parsed && list.All(r => r.Range is null || r.Range.Accepts(parsed.Version)))
                        .Where(v => !v.Yanked || v.Version == locked)
                        .ToList();
                    if (fitting.Count == 0)
                    {
                        throw new HiveException($"no version of {id} in {provider.Address} fits every requirement: " +
                            string.Join("; ", list.Select(r => $"{r.From} wants {(r.Range?.ToString() ?? "any")}")) +
                            $" (published: {string.Join(", ", index.Versions.Select(v => v.Version + (v.Yanked ? " (yanked)" : "")))})");
                    }
                    next[id] = fitting.FirstOrDefault(v => v.Version == locked)
                        ?? fitting.MaxBy(v => VersionRange.Parse(v.Version)!.Version, Comparer<uint[]>.Create(VersionRange.Compare))!;
                }
                if (next.Count == chosen.Count && next.All(n => chosen.TryGetValue(n.Key, out var same) && same.Version == n.Value.Version))
                    return chosen;
                chosen = next;
            }
            throw new HiveException("the packages' requirements do not settle on one choice");
        }

        private (HiveIndex Index, HiveProvider Provider) IndexOf(PackageReference package)
        {
            if (indexes.TryGetValue(package.Id, out var found))
                return found;
            var problems = new List<string>();
            foreach (var provider in providers)
            {
                try
                {
                    if (provider.Index(package) is { } index)
                    {
                        if (index.Package != package.Id)
                            throw new HiveException($"{provider.Address} answered for {package.Id} with the index of {index.Package}");
                        return indexes[package.Id] = (index, provider);
                    }
                    problems.Add($"{provider.Address} does not have it");
                }
                catch (HiveException e)
                {
                    problems.Add(e.Message);
                }
            }
            throw new HiveException($"{package.Id} not found: {string.Join("; ", problems)}");
        }

        /// <summary>Installs one version (unless the cache already has that exact file) and says how it is pinned.</summary>
        private HiveLockEntry Fetch(PackageReference package, HiveIndexVersion version, HiveIndex index, HiveProvider provider)
        {
            var entry = new HiveLockEntry(package.Id, version.Version, version.Sha256, version.Key, provider.Address, version.Dependencies);
            string folder = cache.Folder(package, version.Version);
            string stamp = Path.Combine(folder, ".sha256");
            if (cache.Project(package, version.Version) is not null && File.Exists(stamp) && File.ReadAllText(stamp).Trim() == version.Sha256)
                return entry;

            log?.Invoke($"  fetching {package.Id} {version.Version} from {provider.Address}");
            byte[] file = provider.Download(package, version);
            if (HiveManifest.Hash(file) != version.Sha256)
                throw new HiveException($"{package.Id} {version.Version} from {provider.Address} does not match the hash its index gives: refusing it");
            string? publicKey = null;
            if (version.Key is { } fingerprint)
            {
                if (!index.Keys.TryGetValue(fingerprint, out publicKey) || HiveKeys.Fingerprint(publicKey) != fingerprint)
                    throw new HiveException($"{package.Id} {version.Version} is signed by key {HiveArchive.Short(fingerprint)}, which its index does not give");
            }
            var opened = HiveArchive.Open(file, publicKey, requireSignatures);
            if (opened.Manifest.Package != package.Id || opened.Manifest.Version != version.Version)
                throw new HiveException($"{provider.Address} gave {opened.Manifest.Package} {opened.Manifest.Version} for {package.Id} {version.Version}");
            if (version.Key is { } signer)
                cache.Trust(package, signer);
            HiveArchive.Extract(opened, folder);
            File.WriteAllText(stamp, version.Sha256);
            return entry;
        }
    }

    /// <summary>
    /// What a language's frontend uses to compile with packages (a <see cref="QueenPackageResolver"/>): each <c>package:</c> becomes an
    /// installed version's project, the one the lock pinned when there is a lock, else the newest installed that fits.
    /// It never downloads: <c>larva hive install</c> does.
    /// </summary>
    public sealed class HiveResolver
    {
        private HiveCache? cache;
        private HiveLock? locked;

        public static QueenPackageResolver Create() => new HiveResolver().Resolve;

        private IReadOnlyList<string> Resolve(QueenProject project, IReadOnlyList<PackageReference> packages, List<QueenDiagnostic> diagnostics)
        {
            // The first project asking is the one being built: its cache and lock decide for the packages below it too.
            if (cache is null)
            {
                cache = HiveCache.For(project.Hive);
                try
                {
                    locked = HiveLock.Load(project.Folder);
                }
                catch (HiveException e)
                {
                    diagnostics.Add(new QueenDiagnostic("QN109", e.Message, project.File, 1, 1));
                }
            }
            var found = new List<string>();
            foreach (var package in packages)
            {
                var versions = cache.Versions(package).Where(v => package.Version is null || package.Version.Accepts(VersionRange.Parse(v)!.Version)).ToList();
                string? pinned = locked?.Find(package.Id)?.Version;
                string? version = pinned is not null && versions.Contains(pinned) ? pinned : versions.FirstOrDefault();
                if (version is null || cache.Project(package, version) is not { } path)
                {
                    diagnostics.Add(new QueenDiagnostic("QN108", $"package {package} is not installed: run `larva hive install`{(pinned is not null ? " (or `larva hive restore`)" : "")}",
                        project.File, package.Line, package.Column));
                    continue;
                }
                found.Add(path);
            }
            return found;
        }
    }
}
