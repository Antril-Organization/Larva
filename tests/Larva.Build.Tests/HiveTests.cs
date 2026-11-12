using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hive;

namespace Larva.Build.Tests;

/// <summary>
/// Hive, the packager: versions, names and paths, keys, package files (made and checked), registries, choosing versions,
/// installing (hashes, signatures, trusted keys), locks, and projects that use packages; and the project chunks it relies
/// on (typed DEPS, SOURCES, FILES, RESOURCES, HIVE). Everything happens in a temporary folder: its registries and its
/// cache, never this machine's.
/// </summary>
public sealed class HiveTests(HiveTests.HiveFixture hive) : CheckedTest, IClassFixture<HiveTests.HiveFixture>
{
    // ── versions ──

    [Fact]
    public void Version_ranges_accept_what_they_should()
    {
        bool Accepts(string range, string version) => VersionRange.Parse(range)!.Accepts(VersionRange.Parse(version)!.Version);
        Check("^1.2 takes 1.2 and 1.9, not 1.1 or 2.0", Accepts("^1.2", "1.2") && Accepts("^1.2", "1.9.3") && !Accepts("^1.2", "1.1") && !Accepts("^1.2", "2.0"));
        Check("^0.3 takes 0.3.x only; ^0.0.4 only 0.0.4", Accepts("^0.3", "0.3.9") && !Accepts("^0.3", "0.4") && Accepts("^0.0.4", "0.0.4") && !Accepts("^0.0.4", "0.0.5"));
        Check(">=1.2 takes anything newer; 1.2 (exact) only 1.2 (1.2.0 too)", Accepts(">=1.2", "3.0") && !Accepts(">=1.2", "1.1.9") && Accepts("1.2", "1.2.0") && !Accepts("=1.2", "1.2.1"));
        Check("versions with v are read", VersionRange.Parse("v2.0.1") is { Operator: VersionOperator.Exact, Version: [2, 0, 1] } && VersionRange.Parse("^x") is null);
    }

    // ── names and paths ──

    [Fact]
    public void Package_names_and_paths_are_checked()
    {
        Check("Author/Name@range is a package reference", PackageReference.Parse("Antril/Json@^1.0") is { Author: "Antril", Name: "Json", Version.Operator: VersionOperator.Compatible });
        Check("names cannot climb, hold spaces or slashes", PackageReference.Parse("a/../b") is null && PackageReference.Parse("An tril/Json") is null && PackageReference.Parse("a/b/c") is null && PackageReference.Parse("a/..") is null);
        Check("safe package paths", HiveArchive.SafePath("lib/Geo.lvo") && HiveArchive.SafePath("data/sub/x.bin"));
        Check("unsafe package paths: climbing, rooted, drives, devices, the manifest",
            !HiveArchive.SafePath("../x") && !HiveArchive.SafePath("/etc/x") && !HiveArchive.SafePath("C:/x") && !HiveArchive.SafePath("a\\b")
            && !HiveArchive.SafePath("lib/CON.txt") && !HiveArchive.SafePath("hive.json") && !HiveArchive.SafePath("a//b") && !HiveArchive.SafePath("a/./b"));
    }

    // ── package files ──

    [Fact]
    public void Package_files_are_made_signed_checked_and_unpacked()
    {
        string root = hive.Root;
        var (privatePem, publicKey, fingerprint) = HiveKeys.Generate();
        byte[] data = Encoding.UTF8.GetBytes("hola");
        Check("keys sign and verify; another key does not verify", HiveKeys.Verify(data, HiveKeys.Sign(data, privatePem), publicKey)
            && !HiveKeys.Verify(data, HiveKeys.Sign(data, HiveKeys.Generate().PrivatePem), publicKey) && HiveKeys.Fingerprint(publicKey) == fingerprint);

        string folder = Path.Combine(root, "files");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.lvo"), "object");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "text");
        string package = Path.Combine(root, "Geo-1.0.0.hive");
        var manifest = HiveArchive.Create(package, new HiveManifest { Package = "T/Geo", Version = "1.0.0" },
            new Dictionary<string, string> { ["lib/a.lvo"] = Path.Combine(folder, "a.lvo"), ["data/b.txt"] = Path.Combine(folder, "b.txt") }, privatePem);
        var opened = HiveArchive.Open(File.ReadAllBytes(package), publicKey, requireSignature: true);
        Check("a package is made and opened: files, hashes, signature", opened.Files.Count == 2 && Encoding.UTF8.GetString(opened.Files["data/b.txt"]) == "text"
            && manifest.Key == fingerprint && opened.Manifest.Files.All(f => f.Sha256.Length == 64));
        Check("the same files make the same package bytes", Same(() => HiveArchive.Create(Path.Combine(root, "again.hive"), new HiveManifest { Package = "T/Geo", Version = "1.0.0" },
            new Dictionary<string, string> { ["lib/a.lvo"] = Path.Combine(folder, "a.lvo"), ["data/b.txt"] = Path.Combine(folder, "b.txt") }, null), Path.Combine(root, "again.hive")));

        Refused("an unsigned package where signatures are required", Rewrite(package, drop: HiveArchive.SignatureEntry), null, true, "not signed");
        Refused("a changed manifest breaks the signature", Rewrite(package, change: (HiveArchive.ManifestEntry, b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("\"1.0.0\"", "\"1.0.0\",\"description\":\"x\"")))), publicKey, true, "signature");
        Refused("a changed file does not match its hash", Rewrite(package, change: ("data/b.txt", _ => Encoding.UTF8.GetBytes("TEXT"))), publicKey, true, "hash");
        Refused("a file the manifest does not list is refused", Rewrite(package, add: ("extra.dll", [1, 2, 3])), publicKey, true, "does not list");
        Refused("a path that climbs out is refused", Rewrite(package, add: ("../evil.txt", [1])), publicKey, true, "does not list");
        Refused("another key's signature is refused", File.ReadAllBytes(package), HiveKeys.Generate().PublicKey, true, "signed by key");

        string extracted = Path.Combine(root, "extracted");
        HiveArchive.Extract(opened, extracted);
        Check("a checked package unpacks with its manifest and signature", File.ReadAllText(Path.Combine(extracted, "data", "b.txt")) == "text"
            && File.Exists(Path.Combine(extracted, HiveArchive.ManifestEntry)) && File.Exists(Path.Combine(extracted, HiveArchive.SignatureEntry)));
    }

    // ── registries, choosing and installing ──

    [Fact]
    public void Registries_choose_versions_and_install_with_their_keys()
    {
        string root = hive.Root;
        var (provider, privatePem, publicKey, cache, locked) = hive.Registry.Value;
        // B 1.0, 1.1, 1.3, 2.0; A 1.0 needs B ^1.1 (published by the fixture), then installed: A and B@^1.0.
        Check("a folder registry keeps an index per package", provider.Index(PackageReference.Parse("T/B")!) is { Versions.Count: 4, Keys.Count: 1 });
        Check("a version is published once", Throws(() => Publish(provider, root, "T/B", "1.3.0", [], privatePem, publicKey), "already published"));
        var other = HiveKeys.Generate();
        Check("a package keeps its signer: another key cannot publish it", Throws(() => Publish(provider, root, "T/B", "1.4.0", [], other.PrivatePem, other.PublicKey), "other keys"));

        Check("the newest version every requirement accepts (B ^1.0 and A's ^1.1: 1.3)", locked.Find("T/B")?.Version == "1.3.0" && locked.Find("T/A")?.Version == "1.0.0",
            string.Join(", ", locked.Packages.Select(p => $"{p.Package} {p.Version}")));
        Check("installed packages are in the cache", cache.Project(PackageReference.Parse("T/B")!, "1.3.0") is not null && cache.Versions(PackageReference.Parse("T/A")!) is ["1.0.0"]);
        Check("requirements that no version meets are explained", Throws(() => new HiveInstaller(cache, [provider], true)
            .Install([PackageReference.Parse("T/A")!, PackageReference.Parse("T/B@>=2.0")!], null), "fits every requirement"));
        var keep = new HiveLock { Packages = [locked.Find("T/B")! with { Version = "1.1.0" }] };
        Check("a lock's choice is kept while it fits", new HiveInstaller(cache, [provider], true).Install([PackageReference.Parse("T/B@^1.0")!], keep).Find("T/B")?.Version == "1.1.0");

        // A registry that swaps the key (and the index with it): the cache remembers the first key.
        var swapped = new HiveFolderProvider(Path.Combine(root, "swapped"));
        Publish(swapped, root, "T/B", "1.4.0", [], other.PrivatePem, other.PublicKey);
        Check("a package signed by a key other than the one it was installed with is refused",
            Throws(() => new HiveInstaller(cache, [swapped], true).Install([PackageReference.Parse("T/B@1.4")!], null), "not by the key it was installed with"));
        cache.AddTrusted(PackageReference.Parse("T/B")!, HiveKeys.Fingerprint(other.PublicKey));
        Check("…until that key is trusted (`hive trust`)", new HiveInstaller(cache, [swapped], true).Install([PackageReference.Parse("T/B@1.4")!], null).Find("T/B")?.Version == "1.4.0");

        // Unsigned packages: refused under `verify: strict`, accepted under `verify: hashes`.
        var plain = new HiveFolderProvider(Path.Combine(root, "plain"));
        Publish(plain, root, "T/Plain", "1.0.0", [], null, null);
        Check("an unsigned package is refused when signatures are required", Throws(() => new HiveInstaller(cache, [plain], true).Install([PackageReference.Parse("T/Plain")!], null), "not signed"));
        Check("…and accepted with `verify: hashes`", new HiveInstaller(cache, [plain], false).Install([PackageReference.Parse("T/Plain")!], null).Packages.Count == 1);
    }

    // ── projects ──

    [Fact]
    public void Projects_use_packages_and_every_new_chunk_and_libraries_are_packed()
    {
        string root = hive.Root;
        _ = hive.Registry.Value; // the registry, and A and B installed in the cache the project names
        string folder = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(folder, "data", "sub"));
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        Directory.CreateDirectory(Path.Combine(root, "shared"));
        Directory.CreateDirectory(Path.Combine(folder, "native"));
        File.WriteAllText(Path.Combine(folder, "Main.ant"), "Main() : i32 => 0;");
        File.WriteAllText(Path.Combine(folder, "Draft.ant"), "Draft() : i32 => 0;");
        File.WriteAllText(Path.Combine(root, "shared", "Util.ant"), "Util() : i32 => 0;");
        File.WriteAllText(Path.Combine(folder, "data", "a.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "data", "sub", "b.bin"), "b");
        File.WriteAllText(Path.Combine(folder, "assets", "app.cfg"), "c");
        File.WriteAllText(Path.Combine(folder, "native", "zlib.dll"), "z");
        string cache = Path.Combine(root, "cache");
        File.WriteAllText(Path.Combine(folder, "P.queen"), $"""
            queen 1.0;

            chunk PROJ:
               name: P,
               type: exe

            chunk BUILD:
               stdlib: none

            chunk SOURCES:
               include: ../shared/*.ant,
               exclude: Draft.ant

            chunk DEPS:
               package: T/A,
               package: T/B@^1.0,
               lib: native/zlib.dll

            chunk RESOURCES:
               embed: data/**,
               logo: assets/app.cfg

            chunk FILES:
               copy: assets/*.cfg,
               copy: data/a.txt,
               into: conf

            chunk HIVE:
               provider: ../registry,
               cache: "{cache.Replace("\\", "/")}",
               verify: hashes
            """);
        var diagnostics = new List<QueenDiagnostic>();
        var project = QueenProject.Load(Path.Combine(folder, "P.queen"), null, diagnostics, HiveResolver.Create());
        Check("a project with every new chunk loads", project is not null && !diagnostics.Any(d => d.IsError), string.Join("; ", diagnostics));
        if (project is null)
            return;
        Check("SOURCES: include adds files outside the folder, exclude leaves some out",
            project.Sources.Any(s => s.EndsWith("Util.ant", StringComparison.Ordinal)) && !project.Sources.Any(s => s.EndsWith("Draft.ant", StringComparison.Ordinal)) && project.Sources.Any(s => s.EndsWith("Main.ant", StringComparison.Ordinal)));
        Check("RESOURCES: patterns named by path, and named ones",
            project.Resources.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(["data/a.txt", "data/sub/b.bin", "logo"]));
        Check("FILES: copies keep their place under `into`", project.CopiedFiles.Select(f => f.Target).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(["conf/assets/app.cfg", "conf/data/a.txt"]));
        Check("DEPS lib: a native library", project.NativeLibraries is [var native] && native.EndsWith("zlib.dll", StringComparison.Ordinal));
        Check("DEPS package: installed packages become dependencies (A and B, B once)",
            project.Dependencies.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(["A", "B"]), string.Join(", ", project.Dependencies.Select(d => d.Name)));
        Check("HIVE: providers, cache and verify are read", project.Hive is { RequireSignatures: false, Providers: ["../registry"] } && project.Hive.Cache == Path.GetFullPath(cache));

        File.WriteAllText(Path.Combine(folder, "P.queen"), File.ReadAllText(Path.Combine(folder, "P.queen")).Replace("package: T/A,", "package: T/Missing,"));
        diagnostics.Clear();
        QueenProject.Load(Path.Combine(folder, "P.queen"), null, diagnostics, HiveResolver.Create());
        Check("a package that is not installed is an error that says how to install it", diagnostics.Any(d => d.Code == "QN108" && d.Message.Contains("hive install", StringComparison.Ordinal)), string.Join("; ", diagnostics));
        diagnostics.Clear();
        QueenProject.Load(Path.Combine(folder, "P.queen"), null, diagnostics);
        Check("without Hive, packages are skipped with a warning", diagnostics.Any(d => d.Code == "QN105" && !d.IsError) && !diagnostics.Any(d => d.IsError));

        // Packing: a library with compiled objects, a native library and resources makes a package whose project
        // describes it (a library of Larva objects, its resources and native library).
        string library = Path.Combine(root, "lib");
        Directory.CreateDirectory(Path.Combine(library, "data"));
        Directory.CreateDirectory(Path.Combine(library, "bin"));
        File.WriteAllText(Path.Combine(library, "data", "unit.txt"), "u");
        File.WriteAllText(Path.Combine(library, "Geo.ant"), "into namespace Geo { public Area() : i32 => 1; }");
        File.WriteAllText(Path.Combine(library, "bin", "Geo.lvo"), "not really an object");
        File.WriteAllText(Path.Combine(library, "Geo.queen"), """
            queen 1.0;

            chunk PROJ:
               name: Geo,
               type: library,
               version: v1.4.2,
               author: Test Author

            chunk BUILD:
               stdlib: none

            chunk RESOURCES:
               embed: data/unit.txt,
               mark: data/unit.txt

            chunk HIVE:
               package: Tester/Geo,
               license: MIT
            """);
        var geo = QueenProject.Load(Path.Combine(library, "Geo.queen"), null, diagnostics)!;
        string packed = Path.Combine(root, "Geo.hive");
        var manifest = HivePacker.Pack(geo, Path.Combine(library, "bin"), packed, "default", null);
        var files = manifest.Files.Select(f => f.Path).ToHashSet();
        Check("a compiled package carries its objects, resources and project, not its sources",
            files.SetEquals(["lib/Geo.lvo", "data/unit.txt", "named/mark", HivePacker.ProjectFile]) && manifest is { Package: "Tester/Geo", Version: "1.4.2", Kind: "compiled", License: "MIT" },
            string.Join(", ", files));
        var unpacked = Path.Combine(root, "geo-unpacked");
        HiveArchive.Extract(HiveArchive.Open(File.ReadAllBytes(packed), null, false), unpacked);
        var described = QueenProject.Load(Path.Combine(unpacked, HivePacker.ProjectFile), null, diagnostics = []);
        Check("its package.queen is a library of Larva objects with the same resources",
            described is { Kind: ProjectKind.Library, Sources.Count: 0, Libraries.Count: 1 } && described.Resources.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(["data/unit.txt", "mark"]),
            string.Join("; ", diagnostics));
        Check("an exe or a project without a version is not packed", Throws(() => HivePacker.Pack(project, library, packed, null, null), "packages are libraries"));
    }

    // ── publishing ──

    /// <summary>A project's .queen names where it publishes: the shared token must not follow that address.</summary>
    [Fact]
    public void Publishing_sends_the_token_only_to_configured_registries()
    {
        var antril = new Uri("https://antril.org/hive/");
        var evil = new Uri("https://evil.example/hive/");
        var local = new Uri("http://127.0.0.1:8080/");
        string? Env(Dictionary<string, string> variables, string name) => variables.GetValueOrDefault(name);
        var shared = new Dictionary<string, string> { ["HIVE_TOKEN"] = "shared" };
        Check("HIVE_TOKEN goes to a registry configured on this machine (with or without a scheme)",
            HiveHttpProvider.TokenFor(antril, ["antril.org/hive"], n => Env(shared, n)) == "shared"
            && HiveHttpProvider.TokenFor(antril, ["https://antril.org/hive/"], n => Env(shared, n)) == "shared"
            && HiveHttpProvider.TokenFor(antril, HiveSettings.Defaults(), n => Env(shared, n)) == "shared");
        Check("HIVE_TOKEN does not go to a registry a project names", HiveHttpProvider.TokenFor(evil, ["antril.org/hive"], n => Env(shared, n)) is null
            && HiveHttpProvider.TokenFor(evil, [], n => Env(shared, n)) is null && HiveHttpProvider.TokenFor(evil, ["../registry", "file:x", "C:/r"], n => Env(shared, n)) is null);
        Check("nor over another scheme or port of a configured host", HiveHttpProvider.TokenFor(new Uri("http://antril.org/hive/"), ["antril.org/hive"], n => Env(shared, n)) is null
            && HiveHttpProvider.TokenFor(local, ["http://127.0.0.1:9090/"], n => Env(shared, n)) is null);
        var own = new Dictionary<string, string> { ["HIVE_TOKEN"] = "shared", ["HIVE_TOKEN_EVIL_EXAMPLE"] = "mine", ["HIVE_TOKEN_127_0_0_1"] = "loop" };
        Check("a registry's own variable (HIVE_TOKEN_<HOST>) is sent to it wherever it is named",
            HiveHttpProvider.TokenVariableFor(evil) == "HIVE_TOKEN_EVIL_EXAMPLE" && HiveHttpProvider.TokenFor(evil, [], n => Env(own, n)) == "mine"
            && HiveHttpProvider.TokenFor(local, [], n => Env(own, n)) == "loop" && HiveHttpProvider.TokenFor(antril, ["antril.org/hive"], n => Env(own, n)) == "shared");
        Check("publishing without a token the registry may have is refused, naming the variable", Throws(() =>
            new HiveHttpProvider(evil).Publish(PackageReference.Parse("T/X")!, new HiveIndexVersion { Version = "1.0.0", File = "x", Sha256 = new string('0', 64) }, [1], null), "HIVE_TOKEN_EVIL_EXAMPLE"));
    }

    // ── the cache ──

    /// <summary>Several larva processes share the cache: unpacking the same version and remembering keys at once loses nothing.</summary>
    [Fact]
    public void Cache_writes_survive_concurrent_larvas()
    {
        string root = Path.Combine(hive.Root, "concurrent");
        var cache = new HiveCache(root);
        string fingerprint = new string('f', 64);
        var failures = new List<Exception>();
        void Try(Action action)
        {
            try { action(); }
            catch (Exception e) { lock (failures) failures.Add(e); }
        }
        Parallel.For(0, 24, i => Try(() => cache.Trust(PackageReference.Parse($"P/N{i}")!, fingerprint)));
        Parallel.For(0, 24, i => Try(() => cache.AddTrusted(PackageReference.Parse("P/Shared")!, new string((char)('a' + i % 6), 60) + i.ToString("D4"))));
        var keys = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllBytes(Path.Combine(root, "keys.json")), HiveManifest.Json)!;
        Check("keys remembered at once are all kept", failures.Count == 0 && Enumerable.Range(0, 24).All(i => keys.GetValueOrDefault($"P/N{i}") is [var one] && one == fingerprint) && keys["P/Shared"].Count == 24,
            failures.Count > 0 ? failures[0].ToString() : $"{keys.Count} packages, {keys.GetValueOrDefault("P/Shared")?.Count} shared keys");
        Check("…through whole files only (no partial file is left)", !Directory.EnumerateFiles(root, "*.partial").Any());

        string source = Path.Combine(root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.lvo"), new string('x', 200_000));
        File.WriteAllText(Path.Combine(source, "b.txt"), "text");
        string file = Path.Combine(root, "C-1.0.0.hive");
        HiveArchive.Create(file, new HiveManifest { Package = "P/C", Version = "1.0.0" }, new Dictionary<string, string> { ["lib/a.lvo"] = Path.Combine(source, "a.lvo"), ["b.txt"] = Path.Combine(source, "b.txt") }, null);
        var opened = HiveArchive.Open(File.ReadAllBytes(file), null, false);
        string target = cache.Folder(PackageReference.Parse("P/C")!, "1.0.0");
        Parallel.For(0, 8, _ => Try(() => HiveArchive.Extract(opened, target, "stamp")));
        Check("the same version unpacked by several at once ends complete, stamped, with nothing half done beside it",
            failures.Count == 0 && File.ReadAllText(Path.Combine(target, "b.txt")) == "text" && new FileInfo(Path.Combine(target, "lib", "a.lvo")).Length == 200_000
            && cache.Stamp(PackageReference.Parse("P/C")!, "1.0.0") == "stamp" && !Directory.EnumerateDirectories(Path.GetDirectoryName(target)!, "*.partial").Any(),
            failures.Count > 0 ? failures[0].ToString() : string.Join(", ", Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(target)!)));
        Check("a folder another larva left half done is cleared when the version is unpacked again", Cleared());
        bool Cleared()
        {
            string stale = $"{target}.deadbeef.partial";
            Directory.CreateDirectory(stale);
            HiveArchive.Extract(opened, target, "stamp");
            return !Directory.Exists(stale) && File.Exists(Path.Combine(target, "b.txt"));
        }
    }

    /// <summary>The cache never mixes two packages up because the file system would (case, trailing dots), and a lock pins bytes, not just a version number.</summary>
    [Fact]
    public void Cache_folders_tell_names_apart_and_locks_pin_bytes()
    {
        string root = hive.Root;
        var (_, _, _, cache, locked) = hive.Registry.Value;
        Check("names that Windows would fold are refused (trailing dot, device names)",
            PackageReference.Parse("Acme./Json") is null && PackageReference.Parse("Acme/Json.") is null && PackageReference.Parse("CON/Json") is null && PackageReference.Parse("Acme/com1.x") is null
            && PackageReference.Parse("Acme/Json.Net") is not null);
        var upper = PackageReference.Parse("Acme/Json")!;
        var lower = PackageReference.Parse("acme/json")!;
        Check("names that differ in case get different folders (lower case plus a hash of the exact name)",
            !string.Equals(cache.Folder(upper, "1.0.0"), cache.Folder(lower, "1.0.0"), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(cache.Folder(upper, "1.0.0")))!.StartsWith("json-", StringComparison.Ordinal)
            && cache.Folder(upper, "1.0.0") == cache.Folder(PackageReference.Parse("Acme/Json")!, "1.0.0"),
            cache.Folder(upper, "1.0.0"));
        Check("installed packages are listed by their exact names", cache.Installed().Any(i => i.Package.Id == "T/B" && i.Version == "1.3.0") && cache.Installed().All(i => i.Package.Author == "T"),
            string.Join(", ", cache.Installed().Select(i => $"{i.Package.Id} {i.Version}")));
        Check("an installed version carries the hash of the file it came from", cache.Has(PackageReference.Parse("T/B")!, "1.3.0", locked.Find("T/B")!.Sha256)
            && !cache.Has(PackageReference.Parse("T/B")!, "1.3.0", new string('0', 64)) && cache.Stamp(PackageReference.Parse("T/B")!, "9.9.9") is null);

        string folder = Path.Combine(root, "pinned");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Main.ant"), "Main() : i32 => 0;");
        File.WriteAllText(Path.Combine(folder, "Pinned.queen"), $"""
            queen 1.0;

            chunk PROJ:
               name: Pinned

            chunk BUILD:
               stdlib: none

            chunk DEPS:
               package: T/B@^1.0

            chunk HIVE:
               provider: ../registry,
               cache: "{cache.Root.Replace("\\", "/")}",
               verify: hashes
            """);
        locked.Save(folder);
        var diagnostics = new List<QueenDiagnostic>();
        var project = QueenProject.Load(Path.Combine(folder, "Pinned.queen"), null, diagnostics, HiveResolver.Create());
        Check("the pinned version is used when the cache holds the pinned file", project is not null && !diagnostics.Any(d => d.IsError) && project.Dependencies.Any(d => d.Name == "B"), string.Join("; ", diagnostics));

        var tampered = locked with { Packages = [.. locked.Packages.Select(p => p.Package == "T/B" ? p with { Sha256 = new string('a', 64) } : p)] };
        tampered.Save(folder);
        diagnostics.Clear();
        QueenProject.Load(Path.Combine(folder, "Pinned.queen"), null, diagnostics, HiveResolver.Create());
        Check("a cached version that is not the file the lock pins is refused, pointing at `hive restore`",
            diagnostics.Any(d => d.Code == "QN108" && d.IsError && d.Message.Contains("not the file hive.lock pins", StringComparison.Ordinal) && d.Message.Contains("hive restore", StringComparison.Ordinal)),
            string.Join("; ", diagnostics));
    }

    // ── helpers ──

    private static void Publish(HiveProvider provider, string root, string package, string version, HiveDependency[] dependencies, string? privatePem, string? publicKey)
    {
        string folder = Path.Combine(root, "build", package.Replace('/', '_'), version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "x.lvo"), $"{package} {version}");
        var reference = PackageReference.Parse(package)!;
        File.WriteAllText(Path.Combine(folder, HivePacker.ProjectFile), $"queen 1.0;\n\nchunk PROJ:\n   name: {reference.Name},\n   type: library\n");
        string file = Path.Combine(folder, "p.hive");
        var manifest = HiveArchive.Create(file, new HiveManifest { Package = package, Version = version, Dependencies = dependencies },
            new Dictionary<string, string> { ["lib/x.lvo"] = Path.Combine(folder, "x.lvo"), [HivePacker.ProjectFile] = Path.Combine(folder, HivePacker.ProjectFile) }, privatePem);
        byte[] bytes = File.ReadAllBytes(file);
        provider.Publish(reference, new HiveIndexVersion
        {
            Version = version,
            File = $"{version}/{reference.Name}-{version}.hive",
            Sha256 = HiveManifest.Hash(bytes),
            Key = manifest.Key,
            Dependencies = dependencies,
        }, bytes, publicKey);
    }

    /// <summary>The package's entries again, with one dropped, changed or added.</summary>
    private static byte[] Rewrite(string package, string? drop = null, (string Name, Func<byte[], byte[]> Change)? change = null, (string Name, byte[] Bytes)? add = null)
    {
        using var input = ZipFile.OpenRead(package);
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries.Where(e => e.FullName != drop))
            {
                using var stream = entry.Open();
                var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                byte[] data = change is { } c && c.Name == entry.FullName ? c.Change(bytes.ToArray()) : bytes.ToArray();
                using var written = zip.CreateEntry(entry.FullName).Open();
                written.Write(data);
            }
            if (add is { } extra)
            {
                using var written = zip.CreateEntry(extra.Name).Open();
                written.Write(extra.Bytes);
            }
        }
        return output.ToArray();
    }

    private void Refused(string name, byte[] package, string? publicKey, bool requireSignature, string fragment) =>
        Check(name, Throws(() => HiveArchive.Open(package, publicKey, requireSignature), fragment));

    private static bool Same(Action make, string path)
    {
        make();
        byte[] first = File.ReadAllBytes(path);
        make();
        return first.AsSpan().SequenceEqual(File.ReadAllBytes(path));
    }

    private static bool Throws(Action action, string fragment)
    {
        try
        {
            action();
            TestContext.Current.TestOutputHelper?.WriteLine($"(no error; expected one with `{fragment}`)");
            return false;
        }
        catch (HiveException e)
        {
            if (!e.Message.Contains(fragment, StringComparison.Ordinal))
                TestContext.Current.TestOutputHelper?.WriteLine($"(error without `{fragment}`: {e.Message})");
            return e.Message.Contains(fragment, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The temporary folder of the Hive facts, and a folder registry made once: T/B 1.0, 1.1, 1.3, 2.0 and T/A 1.0 (which
    /// needs B ^1.1), signed by one key, with A and B@^1.0 installed into the folder's cache.
    /// </summary>
    public sealed class HiveFixture : IDisposable
    {
        public string Root { get; } = TestEnvironment.TempFolder("hive");

        public Lazy<(HiveFolderProvider Provider, string PrivatePem, string PublicKey, HiveCache Cache, HiveLock Locked)> Registry { get; }

        public HiveFixture()
        {
            Registry = new(() =>
            {
                string root = Root;
                string registry = Path.Combine(root, "registry");
                var provider = new HiveFolderProvider(registry);
                var (privatePem, publicKey, _) = HiveKeys.Generate();
                // B 1.0, 1.1, 1.3, 2.0; A 1.0 needs B ^1.1.
                Publish(provider, root, "T/B", "1.0.0", [], privatePem, publicKey);
                Publish(provider, root, "T/B", "1.1.0", [], privatePem, publicKey);
                Publish(provider, root, "T/B", "1.3.0", [], privatePem, publicKey);
                Publish(provider, root, "T/B", "2.0.0", [], privatePem, publicKey);
                Publish(provider, root, "T/A", "1.0.0", [new HiveDependency("T/B", "^1.1")], privatePem, publicKey);
                var cache = new HiveCache(Path.Combine(root, "cache"));
                var installer = new HiveInstaller(cache, [provider], requireSignatures: true);
                var locked = installer.Install([PackageReference.Parse("T/A")!, PackageReference.Parse("T/B@^1.0")!], null);
                return (provider, privatePem, publicKey, cache, locked);
            });
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
