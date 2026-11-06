using Hive;
using Larva.Build;

/// <summary>
/// <c>larva hive</c>: the packager. Packages are libraries: compiled Larva objects (the default: no sources, the
/// smallest) or sources, with the native libraries and resources they need, every file hashed and the whole signed.
/// Installing checks all of it, runs no code from the package, and pins what was installed in hive.lock.
/// Registries: the project's HIVE <c>provider</c>s, else this machine's (<c>larva config hive.provider …</c>), else
/// <see cref="HiveSettings.DefaultProvider"/>.
/// </summary>
internal static class HiveCommand
{
    private const string Usage = """
        usage: larva hive key [-o file]                        a signing key (HIVE `sign:` names it)
               larva hive pack [project.queen | folder]        builds the library and writes bin/Name-version.hive
               larva hive publish [project] [--provider p]     packs and publishes to the project's (or the given) registry
               larva hive install [project]                    installs the project's DEPS `package:` entries; writes hive.lock
               larva hive add Author/Name[@version] [project]  adds a `package:` to DEPS and installs it
               larva hive restore [project]                    installs exactly what hive.lock pins
               larva hive verify file.hive [--key public-key]  checks a package file
               larva hive info Author/Name [project]           the versions a registry has
               larva hive list                                 installed packages
               larva hive trust Author/Name fingerprint        accepts another signing key for a package
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string command = args[0];
        var rest = args.Skip(1).ToList();
        string? Option(string name)
        {
            int at = rest.IndexOf(name);
            if (at < 0 || at + 1 >= rest.Count)
                return null;
            string value = rest[at + 1];
            rest.RemoveRange(at, 2);
            return value;
        }

        try
        {
            switch (command)
            {
                case "key":
                {
                    string file = Path.GetFullPath(Option("-o") ?? "hive.key");
                    if (File.Exists(file))
                        throw new HiveException($"{file} exists: a key is not overwritten (name another file with -o)");
                    var (privatePem, publicKey, fingerprint) = HiveKeys.Generate();
                    File.WriteAllText(file, privatePem);
                    Console.WriteLine($"wrote {file}: keep it private (sign packages with `sign: {Path.GetFileName(file)}` in HIVE)");
                    Console.WriteLine($"public key:  {publicKey}");
                    Console.WriteLine($"fingerprint: {fingerprint}");
                    return 0;
                }
                case "pack":
                {
                    var project = LoadProject(rest.FirstOrDefault());
                    string package = Pack(project);
                    Console.WriteLine($"packed {package} ({new FileInfo(package).Length} bytes)");
                    return 0;
                }
                case "publish":
                {
                    string? providerOption = Option("--provider");
                    var project = LoadProject(rest.FirstOrDefault());
                    string file = Pack(project);
                    var manifest = HiveArchive.Open(File.ReadAllBytes(file), null, requireSignature: false).Manifest;
                    var provider = HiveProvider.Parse(providerOption ?? project.Hive.Providers[0], project.Folder);
                    string? publicKey = project.Hive.SigningKey is { } key ? HiveKeys.PublicOf(File.ReadAllText(key)) : null;
                    byte[] bytes = File.ReadAllBytes(file);
                    var reference = manifest.Reference;
                    provider.Publish(reference, new HiveIndexVersion
                    {
                        Version = manifest.Version,
                        File = $"{manifest.Version}/{reference.Name}-{manifest.Version}{HiveArchive.Extension}",
                        Sha256 = HiveManifest.Hash(bytes),
                        Key = manifest.Key,
                        Dependencies = manifest.Dependencies,
                    }, bytes, publicKey);
                    Console.WriteLine($"published {manifest.Package} {manifest.Version} to {provider.Address}{(manifest.Key is null ? " (unsigned)" : $" (signed by {HiveArchive.Short(manifest.Key)})")}");
                    return 0;
                }
                case "add":
                {
                    if (rest.Count == 0 || PackageReference.Parse(rest[0]) is not { } added)
                        throw new HiveException("larva hive add Author/Name[@version]");
                    string queen = FindQueen(rest.ElementAtOrDefault(1));
                    AddDependency(queen, added);
                    Console.WriteLine($"added `package: {added}` to {Path.GetFileName(queen)}");
                    return Install(queen);
                }
                case "install":
                    return Install(FindQueen(rest.FirstOrDefault()));
                case "restore":
                {
                    var project = LoadProject(rest.FirstOrDefault(), resolvePackages: false);
                    var locked = HiveLock.Load(project.Folder) ?? throw new HiveException($"{project.Name} has no {HiveLock.FileName}: run `larva hive install`");
                    Installer(project).Restore(locked);
                    Console.WriteLine($"restored {locked.Packages.Count} package(s)");
                    return 0;
                }
                case "verify":
                {
                    string? publicKey = Option("--key");
                    if (rest.Count == 0)
                        throw new HiveException("larva hive verify file.hive");
                    var opened = HiveArchive.Open(File.ReadAllBytes(rest[0]), publicKey, requireSignature: publicKey is not null);
                    var m = opened.Manifest;
                    Console.WriteLine($"{m.Package} {m.Version} ({m.Kind}{(m.Gc is { } gc ? $", gc {gc}" : "")}): {m.Files.Length} files, {m.Files.Sum(f => f.Size)} bytes");
                    Console.WriteLine(opened.Signature is null ? "  unsigned" : publicKey is null ? $"  signed by {HiveArchive.Short(m.Key)} (not checked: give --key)" : $"  signature good ({HiveArchive.Short(m.Key)})");
                    foreach (var dependency in m.Dependencies)
                        Console.WriteLine($"  needs {dependency.Package}{(dependency.Version.Length > 0 ? "@" + dependency.Version : "")}");
                    Console.WriteLine("  every file matches its hash");
                    return 0;
                }
                case "info":
                {
                    if (rest.Count == 0 || PackageReference.Parse(rest[0]) is not { Version: null } package)
                        throw new HiveException("larva hive info Author/Name");
                    var project = TryLoadProject(rest.ElementAtOrDefault(1));
                    var providers = project?.Hive.Providers is { Length: > 0 } listed ? listed : HiveSettings.Defaults();
                    foreach (var address in providers)
                    {
                        var provider = HiveProvider.Parse(address, project?.Folder ?? Directory.GetCurrentDirectory());
                        if (provider.Index(package) is not { } index)
                        {
                            Console.WriteLine($"{provider.Address}: no {package.Id}");
                            continue;
                        }
                        Console.WriteLine($"{package.Id} in {provider.Address}:");
                        foreach (var version in index.Versions)
                            Console.WriteLine($"  {version.Version}{(version.Yanked ? " (yanked)" : "")}  {(version.Key is null ? "unsigned" : "signed " + HiveArchive.Short(version.Key))}{(version.Dependencies.Length > 0 ? "  needs " + string.Join(", ", version.Dependencies.Select(d => d.Package + (d.Version.Length > 0 ? "@" + d.Version : ""))) : "")}");
                        return 0;
                    }
                    return 1;
                }
                case "list":
                {
                    var cache = HiveCache.Default();
                    int count = 0;
                    foreach (var (package, version) in cache.Installed())
                    {
                        Console.WriteLine($"{package.Id} {version}");
                        count++;
                    }
                    Console.WriteLine(count == 0 ? $"no packages in {cache.Root}" : $"{count} in {cache.Root}");
                    return 0;
                }
                case "trust":
                {
                    if (rest.Count < 2 || PackageReference.Parse(rest[0]) is not { Version: null } package || rest[1].Length != 64)
                        throw new HiveException("larva hive trust Author/Name fingerprint (64 hex digits, from `larva hive info` or the author)");
                    HiveCache.Default().AddTrusted(package, rest[1].ToLowerInvariant());
                    Console.WriteLine($"{package.Id} may now be signed by {HiveArchive.Short(rest[1])}");
                    return 0;
                }
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (HiveException e)
        {
            Console.Error.WriteLine($"larva hive: {e.Message}");
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"larva hive: {e.Message}");
            return 1;
        }
    }

    private static string FindQueen(string? path) =>
        QueenProject.Find(path ?? Directory.GetCurrentDirectory())
        ?? throw new HiveException(path is null ? "no .queen project here" : $"no project at {path}");

    private static QueenProject? TryLoadProject(string? path)
    {
        try
        {
            return LoadProject(path, resolvePackages: false);
        }
        catch (HiveException)
        {
            return null;
        }
    }

    private static QueenProject LoadProject(string? path, bool resolvePackages = true)
    {
        var diagnostics = new List<QueenDiagnostic>();
        // Installing reads the project without needing its packages installed yet.
        var project = QueenProject.Load(FindQueen(path), null, diagnostics, resolvePackages ? HiveResolver.Create() : null);
        foreach (var diagnostic in diagnostics.Where(d => d.IsError || d.Code != "QN105"))
            Console.Error.WriteLine(diagnostic);
        return project ?? throw new HiveException("the project has errors");
    }

    private static HiveInstaller Installer(QueenProject project)
    {
        var providers = project.Hive.Providers.Select(p => HiveProvider.Parse(p, project.Folder)).ToList();
        return new HiveInstaller(HiveCache.For(project.Hive), providers, project.Hive.RequireSignatures, Console.WriteLine);
    }

    private static int Install(string queen)
    {
        // Read without resolving packages: they are what is being installed.
        var project = LoadProject(queen, resolvePackages: false);
        var previous = HiveLock.Load(project.Folder);
        var locked = Installer(project).Install(project.Packages, previous);
        locked.Save(project.Folder);
        foreach (var entry in locked.Packages)
            Console.WriteLine($"  {entry.Package} {entry.Version}{(entry.Key is null ? " (unsigned)" : "")}");
        Console.WriteLine($"installed {locked.Packages.Count} package(s); pinned in {HiveLock.FileName}");
        return 0;
    }

    /// <summary>
    /// Packs the library into bin/: compiled (the default) its sources become a Larva library (<c>Name.dll</c>), packed with
    /// the Larva libraries (<c>assembly:</c>) and native libraries (<c>lib:</c>) it uses; <c>publish: source</c> packs the
    /// sources instead.
    /// </summary>
    private static string Pack(QueenProject project)
    {
        var compilation = project.Compilation;
        string work = Path.Combine(Path.GetTempPath(), $"hive-{Guid.NewGuid():N}");
        try
        {
            if (!project.Hive.PublishSources && project.AllSources() is { Count: > 0 } sources)
            {
                Directory.CreateDirectory(work);
                string library = Path.Combine(work, project.Name + ".dll");
                if (SourceBuild.Lib(["-o", library, .. project.AllLibraries().Where(QueenProject.IsAssembly), .. sources]) != 0)
                    throw new HiveException($"{project.Name} does not compile: nothing was packed");
            }
            var name = HivePacker.NameOf(project);
            string output = Path.Combine(project.Folder, "bin", $"{name.Name}-{VersionRange.Format(project.Version ?? [0])}{HiveArchive.Extension}");
            string? key = project.Hive.SigningKey is { } keyFile
                ? File.Exists(keyFile) ? File.ReadAllText(keyFile) : throw new HiveException($"signing key {keyFile} not found (make one with `larva hive key`)")
                : null;
            var manifest = HivePacker.Pack(project, work, output, compilation.Gc.Mode.ToString().ToLowerInvariant(), key);
            if (key is null)
                Console.WriteLine("note: the package is not signed (set `sign` in HIVE); projects with `verify: strict` refuse it");
            Console.WriteLine($"  {manifest.Files.Length} files, {manifest.Files.Sum(f => f.Size)} bytes before compression");
            return output;
        }
        finally
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>`larva hive add`: a `package:` entry in the project's DEPS chunk (a DEPS chunk at the end when it has none).</summary>
    private static void AddDependency(string queen, PackageReference package)
    {
        var lines = File.ReadAllLines(queen).ToList();
        int deps = lines.FindIndex(l => l.Trim() is "chunk DEPS:");
        if (deps < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);
            lines.AddRange(["", "chunk DEPS:", $"   package: {package}"]);
        }
        else
        {
            // After the chunk's last entry: the line before the next chunk (or the end), skipping blank lines.
            int end = deps + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith("chunk ", StringComparison.Ordinal))
                end++;
            int last = end - 1;
            while (last > deps && lines[last].Trim().Length == 0)
                last--;
            if (last > deps)
                lines[last] = lines[last].TrimEnd().TrimEnd(',') + ",";
            lines.Insert(last + 1, $"   package: {package}");
        }
        File.WriteAllLines(queen, lines);
    }
}
