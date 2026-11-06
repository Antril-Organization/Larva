using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Larva.Driver;
using Larva.Hir;
using H = Larva.Hir;
using Larva.Hir.Encoding;
using Larva.Hir.Linking;
using Larva.Hir.Lowering;
using Larva.IR;
using Larva.IR.Linking;

/// <summary>
/// Source in an installed language to Larva HIR assemblies and executables (the language's pack compiles the source to
/// HIR; larva links, lowers and compiles the rest):
/// <list type="bullet">
/// <item><c>larva lib -o Name.dll files…</c>: a library, compiled once and referenced as metadata.</item>
/// <item><c>larva build -o app.exe files…</c>: a program against System.Core (precompiled into the cache the first
/// time, again only when its source or the compiler changes), linked with it, lowered, compiled to native code.</item>
/// </list>
/// </summary>
internal static class SourceBuild
{
    private sealed class Timings
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly List<(string Phase, long Ms)> phases = [];
        private long last;

        public void Mark(string phase)
        {
            long now = watch.ElapsedMilliseconds;
            phases.Add((phase, now - last));
            last = now;
        }

        public override string ToString() =>
            string.Join(", ", phases.Select(p => $"{p.Phase} {p.Ms} ms")) + $"; total {watch.ElapsedMilliseconds} ms";
    }

    /// <summary>`--borrowcheck enforce|warn|off`: the mode named, or null (reported).</summary>
    private static BorrowCheckMode? BorrowMode(string word)
    {
        switch (word)
        {
            case "enforce": return BorrowCheckMode.Enforce;
            case "warn": return BorrowCheckMode.Warn;
            case "off": return BorrowCheckMode.Off;
            default:
                Console.Error.WriteLine($"larva: --borrowcheck is enforce, warn or off (not {word})");
                return null;
        }
    }

    public static int Lib(string[] args)
    {
        string? output = null, core = null;
        var inputs = new List<string>();
        var options = Larva.Build.LarvaDefaults.FrontendOptions();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
                output = args[++i];
            else if (args[i] == "--core" && i + 1 < args.Length)
                core = args[++i];
            else if (args[i] == "--borrowcheck" && i + 1 < args.Length)
            {
                if (BorrowMode(args[++i]) is not { } mode)
                    return 2;
                options["borrows"] = mode != BorrowCheckMode.Off;
            }
            else
                inputs.Add(args[i]);
        }
        if (output is null || inputs.Count == 0)
        {
            Console.Error.WriteLine("usage: larva lib -o <Name.dll> [--borrowcheck enforce|warn|off] [--core <dir>] <source file>...");
            return 2;
        }
        // Larva libraries among the inputs (`.dll`, `.lva`) are what it is compiled against, not compiled.
        var libraries = inputs.Where(Larva.Build.QueenProject.IsAssembly).ToList();
        inputs.RemoveAll(libraries.Contains);
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("larva lib: no source file to compile");
            return 2;
        }
        if (Language(inputs) is not { } language)
            return 1;
        var timings = new Timings();
        // A library leaves the machine it was built on: its sources are named by their folder and file
        // (System.Core/List.ant), not by where they were on disk (`library`).
        // Against System.Core, unless it is System.Core itself.
        string name = Path.GetFileNameWithoutExtension(output);
        var references = new List<JsonObject>();
        if (name != "System.Core")
        {
            if (CoreLibrary(core, noCache: false, out _, out string? corePath) is not { } coreBytes)
                return 1;
            references.Add(CoreReference(coreBytes, corePath));
        }
        references.AddRange(libraries.Select(l => LanguagePack.Reference(Path.GetFullPath(l))));
        var result = Compile(language, inputs, name, references, library: true, options: options);
        timings.Mark("frontend");
        if (result?.Assembly is not { } bytes)
            return 1;
        File.WriteAllBytes(output, output.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? LvaContainer.ToDll(bytes) : bytes);
        timings.Mark("write");
        Console.WriteLine($"{output}: {result.Types} types ({timings})");
        return 0;
    }

    /// <summary>A compilation's answer: the assembly's bytes (null when it has errors), how many types.</summary>
    internal sealed record Compiled(byte[]? Assembly, int Types);

    /// <summary>The language pack the sources are written in (one), or null with the reason printed.</summary>
    internal static LanguagePack? Language(IReadOnlyCollection<string> sources)
    {
        if (LanguageRegistry.For(sources, out var problem) is { } pack)
            return pack;
        Console.Error.WriteLine($"larva: {problem}");
        return null;
    }

    /// <summary>
    /// <paramref name="sources"/> compiled by <paramref name="language"/>'s frontend against <paramref name="references"/>
    /// (<see cref="LanguagePack.Reference(string)"/>), its diagnostics printed: null when the pack could not answer.
    /// <paramref name="texts"/> gives the text of sources that are not (or not as they are) on disk.
    /// </summary>
    internal static Compiled? Compile(LanguagePack language, IEnumerable<string> sources, string name, IEnumerable<JsonObject> references,
        IEnumerable<H.HirResource>? resources = null, IEnumerable<string>? external = null, bool library = false, IReadOnlyDictionary<string, string>? texts = null,
        JsonObject? options = null, bool quiet = false)
    {
        var parameters = new JsonObject
        {
            ["name"] = name,
            ["sources"] = new JsonArray([.. sources.Select(s => (JsonNode)(texts is not null && texts.TryGetValue(s, out var text)
                ? new JsonObject { ["path"] = s, ["text"] = text }
                : new JsonObject { ["path"] = Path.GetFullPath(s) }))]),
            ["references"] = new JsonArray([.. references.Select(r => (JsonNode)(r.Parent is null ? r : r.DeepClone()))]),
            ["library"] = library,
        };
        if (resources is not null)
            parameters["resources"] = new JsonArray([.. resources.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["data"] = Convert.ToBase64String(r.Data) })]);
        if (external is not null)
            parameters["external"] = new JsonArray([.. external.Select(e => (JsonNode)e)]);
        if (options is not null)
            parameters["options"] = options.Parent is null ? options : options.DeepClone();
        JsonNode? result;
        try
        {
            result = language.Call("compile", parameters);
        }
        catch (LanguagePackException e)
        {
            Console.Error.WriteLine($"larva: {e.Message}");
            return null;
        }
        foreach (var diagnostic in quiet ? [] : result?["diagnostics"] as JsonArray ?? [])
            Console.Error.WriteLine(diagnostic?["text"]?.GetValue<string>());
        return new Compiled(result?["assembly"]?.GetValue<string>() is { } data ? Convert.FromBase64String(data) : null, result?["types"]?.GetValue<int>() ?? 0);
    }

    /// <summary>Whether a source names System.Bindings (read as text: no language is asked).</summary>
    private static bool UsesBindings(string source)
    {
        try
        {
            return File.ReadAllText(source).Contains("System.Bindings", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static int Build(string[] args)
    {
        string? output = null, core = null;
        string backend = "llvm";
        int optimization = 3;
        bool showTimings = false, noCache = false, dll = false, check = false;
        bool backendGiven = false, targetGiven = false, optimizationGiven = false;
        BorrowCheckMode? borrowGiven = null;
        // The processor (LLVM): `--cpu`, else the project's BUILD `cpu`, else this machine's default (`larva config cpu`).
        string? cpu = null;
        string target = "native";
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--core" when i + 1 < args.Length: core = args[++i]; break;
                case "--backend" when i + 1 < args.Length: backend = args[++i]; backendGiven = true; break;
                case "--timings": showTimings = true; break;
                case "--no-cache": noCache = true; break;
                case "--dll": dll = true; break;
                case "--check": check = true; break;
                case "--cpu" when i + 1 < args.Length: cpu = args[++i]; break;
                case "--borrowcheck" when i + 1 < args.Length:
                    if (BorrowMode(args[++i]) is not { } givenMode)
                        return 2;
                    borrowGiven = givenMode;
                    break;
                case "--target" when i + 1 < args.Length: target = args[++i]; targetGiven = true; break;
                case ['-', 'O', >= '0' and <= '3']: optimization = args[i][2] - '0'; optimizationGiven = true; break;
                default: inputs.Add(args[i]); break;
            }
        }
        // No source named: the project of the folder you are in (as `larva run` finds it).
        if (inputs.Count == 0 && RunCommand.Find(null) is { } here)
            inputs.Add(here);
        CompilationOptions? projectCompilation = null;
        // What the frontend is told: the project's settings, else this machine's defaults (`larva config`).
        var frontendOptions = Larva.Build.LarvaDefaults.FrontendOptions();
        if (borrowGiven is { } wantedMode)
            frontendOptions["borrows"] = wantedMode != BorrowCheckMode.Off;
        // A `.queen` project: its sources, and its RESOURCES (the assembly carries them; System.Resources reads them there).
        // A dependency built for another GC (`gc:` in its BUILD) is not compiled into the program: it becomes an object of
        // its own, a GC domain the program links with (its types are declared here, defined there).
        var resources = new List<H.HirResource>();
        // What goes next to the build: the native libraries of DEPS (`lib:`), and FILES (`copy:`, each at its place).
        var nextToBuild = new List<(string Source, string Target)>();
        var rootGc = GcConfig.Default;
        var externals = new List<Larva.Build.QueenProject>();
        // Compiled libraries (`.dll` or `.lva`: from `larva bind` or `larva lib`): referenced by the frontend and linked, not compiled.
        var assemblies = new List<string>();
        for (int i = inputs.Count - 1; i >= 0; i--)
        {
            if (!inputs[i].EndsWith(".queen", StringComparison.OrdinalIgnoreCase))
                continue;
            var diagnostics = new List<Larva.Build.QueenDiagnostic>();
            var project = Larva.Build.QueenProject.Load(Path.GetFullPath(inputs[i]), null, diagnostics, Hive.HiveResolver.Create());
            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine(diagnostic);
            if (project is null)
                return 1;
            inputs.RemoveAt(i);
            var foreign = project.Dependencies.Where(d => d.Gc != project.Gc).ToList();
            var foreignSources = new HashSet<string>(foreign.SelectMany(d => d.AllSources()), StringComparer.OrdinalIgnoreCase);
            inputs.InsertRange(i, project.AllSources().Where(s => !foreignSources.Contains(s)));
            nextToBuild.AddRange(project.AllNativeLibraries().Select(l => (l, Path.GetFileName(l))));
            nextToBuild.AddRange(project.CopiedFiles);
            foreach (var (resourceName, file) in project.AllResources())
                resources.Add(new H.HirResource(resourceName, File.ReadAllBytes(file)));
            rootGc = project.Gc;
            externals.AddRange(foreign);
            assemblies.AddRange(project.AllLibraries().Where(Larva.Build.QueenProject.IsAssembly));
            // The project's BUILD settings, where the command line does not say otherwise: back end, target,
            // optimization, output (bin/NAME.exe by default), a library as a DLL, reflection, borrow checking, generics.
            if (projectCompilation is null)
            {
                if (!backendGiven && project.Sets("backend") && project.Backend != Larva.Build.ProjectBackend.Vm)
                    backend = project.Backend == Larva.Build.ProjectBackend.X64 ? "x64" : "llvm";
                if (!targetGiven && project.Sets("target") && project.Target == Larva.Build.ProjectTarget.Wasm)
                    target = backend == "x64" ? "wasm" : "wasm-llvm";
                if (!optimizationGiven && project.Sets("optimization"))
                    optimization = project.Optimization;
                if (project.Kind == Larva.Build.ProjectKind.Library)
                    dll = true;
                if (output is null && !check)
                {
                    string chosen = project.Output is { } written ? Path.GetFullPath(Path.Combine(project.Folder, written)) : project.DefaultOutput(native: true);
                    output = target != "native" ? Path.ChangeExtension(chosen, ".js") : dll ? Path.ChangeExtension(chosen, ".dll") : chosen;
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                }
                projectCompilation = project.Compilation;
                frontendOptions = project.FrontendOptions();
                cpu ??= project.Cpu;
                if (borrowGiven is { } overridden)
                {
                    projectCompilation = projectCompilation with { BorrowCheck = overridden };
                    frontendOptions["borrows"] = overridden != BorrowCheckMode.Off;
                }
            }
        }
        for (int i = inputs.Count - 1; i >= 0; i--)
        {
            if (Larva.Build.QueenProject.IsAssembly(inputs[i]))
            {
                assemblies.Insert(0, Path.GetFullPath(inputs[i]));
                inputs.RemoveAt(i);
            }
        }
        if (check)
            output ??= Path.ChangeExtension(inputs.FirstOrDefault() ?? "program", ".exe");
        if (output is null || inputs.Count == 0 || backend is not ("llvm" or "x64") || target is not ("native" or "wasm" or "wasm-llvm"))
        {
            Console.Error.WriteLine("usage: larva build [-o <app.exe|lib.dll|app.js>] [--check] [--dll] [--target native|wasm|wasm-llvm] [--backend llvm|x64] [-O0..3] [--borrowcheck enforce|warn|off] [--cpu generic|native|<name>] [--core <dir>] [--timings] [--no-cache] [<source file|project.queen|Library.dll>...]");
            return 2;
        }
        if (Language(inputs) is not { } language)
            return 1;
        var timings = new Timings();
        if (CoreLibrary(core, noCache, out bool cached, out string? corePath) is not { } coreBytes)
            return 1;
        timings.Mark(cached ? "System.Core (compiled library)" : "System.Core (compiled now)");

        string name = Path.GetFileNameWithoutExtension(output);
        var coreReference = CoreReference(coreBytes, corePath);
        var references = new List<JsonObject> { coreReference };
        var externalObjects = new List<LarvaObject>();
        var externalBytes = new List<byte[]>();
        foreach (var dependency in externals)
        {
            if (DomainLibrary(dependency, coreBytes, coreReference) is not var (externalObject, externalLva))
                return 1;
            externalObjects.Add(externalObject);
            externalBytes.Add(externalLva);
            references.Add(LanguagePack.Reference(externalLva));
            timings.Mark($"{dependency.Name} ({dependency.Gc.Mode})");
        }
        var assemblyBytes = new List<byte[]>();
        foreach (string assembly in assemblies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(assembly))
            {
                Console.Error.WriteLine($"larva: {assembly} not found");
                return 1;
            }
            byte[] bytes = File.ReadAllBytes(assembly);
            assemblyBytes.Add(bytes);
            references.Add(LanguagePack.Reference(assembly));
            timings.Mark(Path.GetFileName(assembly));
        }
        // System.Bindings, when a source uses it (a binding profile kept with the project it binds for).
        if (inputs.Any(UsesBindings))
        {
            if (BindProfile.BindingsLibrary(coreBytes, out string? bindingsPath) is not { } bindingsBytes)
                return 1;
            assemblyBytes.Add(bindingsBytes);
            references.Add(bindingsPath is not null ? LanguagePack.Reference(bindingsPath) : LanguagePack.Reference(bindingsBytes));
        }
        var program = Compile(language, inputs, name, references, resources, externals.Select(e => e.Name), options: frontendOptions);
        timings.Mark("frontend");
        if (program?.Assembly is not { } programBytes)
            return 1;
        var linked = HirLinker.Link(name, [coreBytes, .. externalBytes, .. assemblyBytes, programBytes]);
        timings.Mark("HIR link");
        if (!linked.Success)
        {
            foreach (var error in linked.Errors)
                Console.Error.WriteLine($"larva: {error}");
            return 1;
        }
        var lowered = HirLowerer.Lower(linked.Assembly!, new LoweringOptions
        {
            StaticConstructorsFromEntry = true,
            Gc = rootGc,
            ExternalAssemblies = externals.Select(e => e.Name).ToHashSet(),
        });
        timings.Mark("lowering");
        if (!lowered.Success)
        {
            foreach (var error in lowered.Errors)
                Console.Error.WriteLine($"larva: {error}");
            return 1;
        }
        // The borrow checker, when on (with --check and when building): over the program's own functions (the libraries
        // were checked when built). `enforce` stops the build at its errors; `warn` shows them and builds.
        var borrowMode = projectCompilation?.BorrowCheck ?? borrowGiven ?? Larva.Build.LarvaDefaults.BorrowCheck();
        if (borrowMode != BorrowCheckMode.Off && lowered.Module is { } checkedModule)
        {
            var files = new HashSet<string>(inputs.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            var borrowErrors = checkedModule.Functions
                .Where(f => !f.IsDeclaration && f.Positions is { Count: > 0 } at && at.Values.Any(p => files.Contains(Path.GetFullPath(p.File))))
                .SelectMany(f => Larva.IR.Analysis.BorrowChecker.Check(f, checkedModule)).ToList();
            foreach (var error in borrowErrors)
                Console.Error.WriteLine(borrowMode == BorrowCheckMode.Warn ? error with { Severity = Larva.IR.Analysis.Severity.Warning } : error);
            timings.Mark("borrow check");
            if (borrowMode == BorrowCheckMode.Enforce && borrowErrors.Count > 0)
                return 1;
        }
        // --check: the errors only (nothing is written).
        if (check)
        {
            Console.WriteLine($"no errors ({inputs.Count} file{(inputs.Count == 1 ? "" : "s")}; {timings})");
            return 0;
        }
        H.MemberToken? entryToken = linked.Assembly!.EntryPoint;
        if (entryToken is null && !dll)
        {
            Console.Error.WriteLine("larva: the program has no Main (a library for C: --dll, with [Export] functions)");
            return 1;
        }
        var compilation = (projectCompilation ?? new CompilationOptions { BorrowCheck = borrowGiven ?? Larva.Build.LarvaDefaults.BorrowCheck() }) with { Gc = rootGc };
        var objectFile = Compiler.CompileObject(lowered.Module!, compilation);
        timings.Mark("IR checks");
        if (!objectFile.Success)
        {
            foreach (var error in objectFile.TypeErrors.Concat<object>(objectFile.Diagnostics).Take(20))
                Console.Error.WriteLine($"larva: {error}");
            return 1;
        }
        var ir = Linker.Link([objectFile.Object!, .. externalObjects], dll
            ? new LinkOptions { CLibrary = true, Entry = entryToken is { } libraryEntry ? lowered.FunctionName(libraryEntry) : "$none", LowerToIl = false, Compilation = compilation, MixedDomains = externals.Count > 0 }
            : new LinkOptions { Entry = lowered.FunctionName(entryToken!.Value), LowerToIl = false, Compilation = compilation, MixedDomains = externals.Count > 0 });
        timings.Mark("IR link");
        if (!ir.Success)
        {
            foreach (var error in ir.Errors)
                Console.Error.WriteLine($"larva: {error}");
            return 1;
        }
        if (target != "native")
        {
            // WebAssembly: NAME.wasm, NAME.js (its runtime; `node NAME.js` runs it) and NAME.html.
            var (files, wasmMessage) = WasmBuilder.Build(ir, output, new ExecutableOptions { OptimizationLevel = optimization, Compilation = compilation }, llvm: target == "wasm-llvm");
            timings.Mark($"native ({target})");
            if (files is null)
            {
                Console.Error.WriteLine($"larva: {wasmMessage}");
                return 1;
            }
            Console.WriteLine($"{string.Join(", ", files.Select(Path.GetFileName))} ({wasmMessage}, entry {ir.Entry})");
            CopyNextTo(output, nextToBuild);
            if (showTimings)
                Console.WriteLine($"timings: {timings}");
            return 0;
        }
        var executableOptions = new ExecutableOptions
        {
            Backend = backend == "x64" ? Backend.X64 : Backend.Llvm,
            OptimizationLevel = optimization,
            Compilation = compilation,
            Cpu = cpu ?? (projectCompilation is null ? Larva.Build.LarvaDefaults.Effective().GetValueOrDefault("cpu") : null),
        };
        var (success, message) = dll ? ExecutableBuilder.BuildLibrary(ir, output, executableOptions) : ExecutableBuilder.Build(ir, output, executableOptions);
        timings.Mark($"native ({backend})");
        if (!success)
        {
            Console.Error.WriteLine($"larva: {message}");
            return 1;
        }
        Console.WriteLine(message);
        CopyNextTo(output, nextToBuild);
        if (showTimings)
            Console.WriteLine($"timings: {timings}");
        return 0;
    }

    /// <summary>The project's native libraries and files copied next to <paramref name="output"/> (when they changed).</summary>
    private static void CopyNextTo(string output, List<(string Source, string Target)> files)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        foreach (var (source, target) in files)
        {
            string destination = Path.GetFullPath(Path.Combine(folder, target));
            if (string.Equals(destination, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                continue;
            var from = new FileInfo(source);
            var to = new FileInfo(destination);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc >= from.LastWriteTimeUtc)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    /// <summary>
    /// A dependency with a GC of its own, as an object of that GC domain: its sources (and its own dependencies') compiled
    /// against System.Core, linked with the core at the HIR level, lowered for its GC with everything exported (the program
    /// declares its members), checked for that GC. The object, and the assembly the program compiles against.
    /// </summary>
    private static (LarvaObject Object, byte[] Lva)? DomainLibrary(Larva.Build.QueenProject dependency, byte[] coreBytes, JsonObject coreReference)
    {
        var sources = dependency.AllSources().ToList();
        if (Language(sources) is not { } language)
            return null;
        var compiled = Compile(language, sources, dependency.Name, [coreReference], options: dependency.FrontendOptions());
        if (compiled?.Assembly is not { } lva)
            return null;
        var linked = HirLinker.Link(dependency.Name, [coreBytes, lva]);
        if (!linked.Success)
        {
            foreach (var error in linked.Errors)
                Console.Error.WriteLine($"larva: {dependency.Name}: {error}");
            return null;
        }
        var lowered = HirLowerer.Lower(linked.Assembly!, new LoweringOptions { Gc = dependency.Gc, ExportAll = true });
        if (!lowered.Success)
        {
            foreach (var error in lowered.Errors)
                Console.Error.WriteLine($"larva: {dependency.Name}: {error}");
            return null;
        }
        var objectFile = Compiler.CompileObject(lowered.Module!, new CompilationOptions { Gc = dependency.Gc });
        if (!objectFile.Success)
        {
            foreach (var error in objectFile.TypeErrors.Concat<object>(objectFile.Diagnostics).Take(20))
                Console.Error.WriteLine($"larva: {dependency.Name}: {error}");
            return null;
        }
        return (objectFile.Object!, lva);
    }

    /// <summary>System.Core for a request to a language pack: its file when it has one (the pack reads it once), else its bytes.</summary>
    internal static JsonObject CoreReference(byte[] bytes, string? path) => path is not null ? LanguagePack.Reference(path) : LanguagePack.Reference(bytes);

    internal static byte[]? CoreLibrary(string? location, bool noCache, out bool cached) => CoreLibrary(location, noCache, out cached, out _);

    /// <summary>
    /// System.Core, as an assembly's bytes (and the file they are in, <paramref name="path"/>, when there is one). Installed,
    /// it is the compiled library next to larva (<c>System.Core.dll</c>); <c>--core</c> or LARVA_CORE may name such a
    /// library (<c>.dll</c> or <c>.lva</c>) or a folder of sources. From sources (working on the library itself), it is
    /// compiled once by their language's pack and cached, keyed by the sources and the pack's frontend.
    /// </summary>
    internal static byte[]? CoreLibrary(string? location, bool noCache, out bool cached, out string? path)
    {
        cached = false;
        path = null;
        location ??= Environment.GetEnvironmentVariable("LARVA_CORE") ?? FindCore();
        if (location is not null && File.Exists(location))
        {
            path = Path.GetFullPath(location);
            return CompiledCore(location, out cached);
        }
        string? folder = location;
        if (folder is null || !Directory.Exists(folder))
        {
            Console.Error.WriteLine("larva: System.Core not found (System.Core.dll next to larva, --core <library or folder>, or LARVA_CORE)");
            return null;
        }
        var files = Directory.GetFiles(folder).Where(LanguageRegistry.IsSource).Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"larva: {folder} has no source of System.Core in an installed language ({LanguageRegistry.Installed()})");
            return null;
        }
        if (Language(files) is not { } language)
            return null;
        // The key: every source file, the frontend that reads it (its library's hash), the format it writes.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{language.Id}|{language.Identity}|{LvaFormat.Version}"));
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        string key = Convert.ToHexString(hash.GetHashAndReset())[..16];
        string cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "cache");
        string cachePath = Path.Combine(cacheFolder, $"System.Core-{key}.lva");
        if (!noCache && File.Exists(cachePath))
        {
            cached = true;
            path = cachePath;
            return File.ReadAllBytes(cachePath);
        }
        var result = Compile(language, files, "System.Core", []);
        if (result?.Assembly is not { } bytes)
            return null;
        Directory.CreateDirectory(cacheFolder);
        File.WriteAllBytes(cachePath, bytes);
        path = noCache ? null : cachePath;
        return bytes;
    }

    /// <summary>A compiled System.Core: its assembly's bytes, when this larva reads its format.</summary>
    private static byte[]? CompiledCore(string path, out bool cached)
    {
        cached = true;
        byte[] bytes;
        try
        {
            bytes = LvaContainer.Unwrap(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HirFormatException)
        {
            Console.Error.WriteLine($"larva: {path}: {e.Message}");
            return null;
        }
        // "LVA" and the format version (one byte while it is below 128): a library from another Larva is told, not misread.
        if (bytes.Length < 4 || bytes[0] != 'L' || bytes[1] != 'V' || bytes[2] != 'A' || bytes[3] != LvaFormat.Version)
        {
            Console.Error.WriteLine($"larva: {path} is not a System.Core this larva reads (Larva libraries of format {LvaFormat.Version}); reinstall Larva");
            return null;
        }
        return bytes;
    }

    /// <summary>
    /// Where System.Core is: the compiled library next to larva (an installation), or else the sources of a
    /// <c>Library/System.Core</c> folder above it (a working copy of Larva).
    /// </summary>
    private static string? FindCore()
    {
        string installed = Path.Combine(AppContext.BaseDirectory, "System.Core.dll");
        if (File.Exists(installed))
            return installed;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string folder = Path.Combine(dir.FullName, "Library", "System.Core");
            if (Directory.Exists(folder))
                return folder;
        }
        return null;
    }
}
