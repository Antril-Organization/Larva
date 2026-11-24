using System.Text;
using Larva.Hir;
using Larva.Hir.Encoding;
using Larva.Hir.Services;
using Larva.Polyglot;

/// <summary>
/// <c>larva polyglot</c>: a Larva library offered to other languages, with each language's names (docs/design/polyglot.md).
/// <list type="bullet">
/// <item><c>larva polyglot names &lt;Lib.dll&gt; [--lang c,csharp,python,java,typescript] [filter]</c>: every public
/// declaration and its name in each language, checked (two declarations with one name are an error).</item>
/// <item><c>larva polyglot build &lt;Lib.dll&gt; --lang c [-o folder]</c>: the library for C: a native library whose
/// exports are the shims (<see cref="ShimGenerator"/>), its import library, and its header (<see cref="CHeader"/>).</item>
/// </list>
/// </summary>
internal static class PolyglotCommand
{
    private const string Usage = """
        usage: larva polyglot names <Lib.dll> [--lang <language>[,<language>...]] [--java-package <root>] [--acronyms <language>=keep|short-upper|words] [--core <dir>] [<filter>]
               larva polyglot build [<Lib.dll>|<project.queen>] [--lang c[,csharp]] [-o <folder>] [--backend llvm|x64] [-O0..3] [--exclude <Namespace.Type[.Member]>]...
                                    [--csharp-root <Namespace>] [--csharp-framework <net8.0>] [--core <dir>]
               languages: c, csharp, python, java, typescript (build: c, csharp)
               build takes a compiled library, or a library project (none named: the project of the folder you are in).
               It writes a folder per language in <folder> (default: bin\polyglot in a project, polyglot beside a library): c\, csharp\.
               Each one is whole by itself (the native library is copied into each): copy it, or reference it, as it is.
        """;

    public static int Run(string[] args)
    {
        if (args is ["build", ..])
            return Build(args[1..]);
        if (args.Length == 0 || args[0] != "names")
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string? library = null, filter = null, core = null, javaRoot = "";
        var languages = new List<TargetLanguage>();
        var acronyms = new Dictionary<TargetLanguage, AcronymPolicy>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lang" when i + 1 < args.Length:
                    foreach (string name in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
                            languages.AddRange(Enum.GetValues<TargetLanguage>());
                        else if (TargetRules.Parse(name) is { } language)
                            languages.Add(language);
                        else
                        {
                            Console.Error.WriteLine($"larva polyglot: no language `{name}` (known: {TargetRules.Known})");
                            return 2;
                        }
                    }
                    break;
                case "--java-package" when i + 1 < args.Length:
                    javaRoot = args[++i];
                    break;
                case "--acronyms" when i + 1 < args.Length:
                    string[] pair = args[++i].Split('=', 2);
                    AcronymPolicy? policy = pair.Length == 2 ? pair[1] switch { "keep" => AcronymPolicy.Keep, "short-upper" => AcronymPolicy.ShortUpper, "words" => AcronymPolicy.Words, _ => null } : null;
                    if (pair.Length != 2 || TargetRules.Parse(pair[0]) is not { } target || policy is null)
                    {
                        Console.Error.WriteLine($"larva polyglot: --acronyms takes <language>=keep|short-upper|words, not `{args[i]}`");
                        return 2;
                    }
                    acronyms[target] = policy.Value;
                    break;
                case "--core" when i + 1 < args.Length:
                    core = args[++i];
                    break;
                default:
                    if (library is null && Larva.Build.QueenProject.IsAssembly(args[i]))
                        library = args[i];
                    else if (filter is null && !args[i].StartsWith('-'))
                        filter = args[i];
                    else
                    {
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    break;
            }
        }
        if (library is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (Catalog(library, core) is not { } catalog)
            return 1;
        var model = PolyglotModel.Build(catalog, catalog.Assemblies[0], new PolyglotOptions
        {
            Languages = languages.Count > 0 ? languages.Distinct().ToArray() : Enum.GetValues<TargetLanguage>(),
            Acronyms = acronyms,
            JavaPackageRoot = javaRoot,
        });
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Write(NamesTable.Write(model, filter));
        foreach (var diagnostic in model.Diagnostics)
            Console.Error.WriteLine($"{Path.GetFileName(library)}: {diagnostic}");
        return model.HasErrors ? 1 : 0;
    }

    /// <summary>
    /// <c>larva polyglot build</c>: the shims of the library compiled with it (and what it reaches of System.Core) into a
    /// native library for C, its import library, and its header.
    /// </summary>
    private static int Build(string[] args)
    {
        string? library = null, project = null, folder = null, core = null;
        string backend = "llvm";
        int optimization = 2;
        var excluded = new List<string>();
        bool c = false, csharp = false;
        string csharpRoot = "", framework = "net8.0";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length:
                    folder = args[++i];
                    break;
                case "--core" when i + 1 < args.Length:
                    core = args[++i];
                    break;
                case "--exclude" when i + 1 < args.Length:
                    excluded.Add(args[++i]);
                    break;
                case "--backend" when i + 1 < args.Length && args[i + 1] is "llvm" or "x64":
                    backend = args[++i];
                    break;
                case "--lang" when i + 1 < args.Length:
                    foreach (string asked in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var language = TargetRules.Parse(asked);
                        if (language is not (TargetLanguage.C or TargetLanguage.CSharp))
                        {
                            Console.Error.WriteLine($"larva polyglot build: `{asked}` is not built yet (c and csharp are; larva polyglot names shows the others' names)");
                            return 2;
                        }
                        c |= language == TargetLanguage.C;
                        csharp |= language == TargetLanguage.CSharp;
                    }
                    break;
                case "--csharp-root" when i + 1 < args.Length:
                    csharpRoot = args[++i];
                    break;
                case "--csharp-framework" when i + 1 < args.Length:
                    framework = args[++i];
                    break;
                case var level when level.Length == 3 && level.StartsWith("-O") && char.IsAsciiDigit(level[2]) && level[2] <= '3':
                    optimization = level[2] - '0';
                    break;
                default:
                    if (library is null && project is null && Larva.Build.QueenProject.IsAssembly(args[i]))
                        library = args[i];
                    else if (library is null && project is null && Larva.Build.QueenProject.Find(args[i]) is { } named)
                        project = named;
                    else
                    {
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    break;
            }
        }
        // No library named: the project of the folder you are in.
        if (library is null && project is null)
            project = RunCommand.Find(null);
        if (library is null && project is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        // A project: its sources compiled into a library first (as `larva lib` does), and its native libraries
        // (`lib:` in DEPS) go with the native library into each language's folder.
        var natives = new List<string>();
        if (project is not null)
        {
            var diagnostics = new List<Larva.Build.QueenDiagnostic>();
            var loaded = Larva.Build.QueenProject.Load(project, null, diagnostics, Hive.HiveResolver.Create());
            foreach (var diagnostic in diagnostics)
                Console.Error.WriteLine(diagnostic);
            if (loaded is null)
                return 1;
            if (loaded.Kind != Larva.Build.ProjectKind.Library)
            {
                Console.Error.WriteLine($"larva polyglot build: {Path.GetFileName(project)} is not a library project");
                return 1;
            }
            string compiled = Path.Combine(loaded.Folder, "obj", "polyglot", loaded.Name + ".lva");
            Directory.CreateDirectory(Path.GetDirectoryName(compiled)!);
            var libArgs = new List<string> { "-o", compiled };
            if (core is not null)
                libArgs.AddRange(["--core", core]);
            libArgs.AddRange(loaded.AllLibraries().Where(Larva.Build.QueenProject.IsAssembly));
            libArgs.AddRange(loaded.AllSources());
            if (SourceBuild.Lib([.. libArgs], loaded.FrontendOptions()) != 0)
                return 1;
            library = compiled;
            folder ??= Path.Combine(loaded.Folder, "bin", "polyglot");
            natives.AddRange(loaded.AllNativeLibraries());
        }
        if (library is null || Catalog(library, core) is not { } catalog)
            return 1;
        if (!csharp)
            c = true; // no --lang: C
        // --exclude: declarations left out (a type, or a member: what its full name starts with).
        bool Excluded(string name) => excluded.Any(e => name == e || name.StartsWith(e + ".", StringComparison.Ordinal) || name.StartsWith(e + "(", StringComparison.Ordinal));
        var model = PolyglotModel.Build(catalog, catalog.Assemblies[0], new PolyglotOptions
        {
            Languages = csharp ? [TargetLanguage.C, TargetLanguage.CSharp] : [TargetLanguage.C],
            CSharpNamespaceRoot = csharpRoot,
            IncludeType = t => !Excluded(t.FullName),
            IncludeMember = m => !Excluded($"{m.Type.FullName}.{m.Name}"),
        });
        string name = Path.GetFileName(library);
        if (model.HasErrors)
        {
            foreach (var diagnostic in model.Diagnostics)
                Console.Error.WriteLine($"{name}: {diagnostic}");
            return 1;
        }
        var (shims, api) = ShimGenerator.Generate(model, catalog);
        foreach (var diagnostic in model.Diagnostics)
            Console.Error.WriteLine($"{name}: {diagnostic}");
        // A folder per language, each whole by itself: the native library is built once and copied into each one.
        folder = Path.GetFullPath(folder ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(library))!, "polyglot"));
        string cFolder = Path.Combine(folder, "c"), csharpFolder = Path.Combine(folder, "csharp");
        string building = c ? cFolder : Path.Combine(folder, "obj");
        Directory.CreateDirectory(building);
        string output = Path.Combine(building, api.Prefix + ".dll");

        // HIR: System.Core, the standard libraries, the library and its shims linked; lowered from the exports.
        var lib = catalog.Assemblies[0].Hir;
        var inputs = new List<byte[]>();
        var names = new List<string>();
        foreach (var assembly in catalog.Assemblies.Skip(1).Append(catalog.Assemblies[0]))
        {
            inputs.Add(assembly == catalog.Assemblies[0] ? File.ReadAllBytes(library) : LvaFormat.Write(assembly.Hir));
            names.Add(assembly.Name);
        }
        inputs.Add(LvaFormat.Write(shims));
        names.Add(shims.Name);
        var linked = Larva.Hir.Linking.HirLinker.Link(shims.Name, inputs, names);
        if (!linked.Success)
        {
            foreach (var error in linked.Errors)
                Console.Error.WriteLine($"larva polyglot: {error}");
            return 1;
        }
        var lowered = Larva.Hir.Lowering.HirLowerer.Lower(linked.Assembly!, new Larva.Hir.Lowering.LoweringOptions
        {
            StaticConstructorsFromEntry = true,
            ReachedOnly = catalog.Assemblies.Select(a => a.Name).ToHashSet(),
        });
        if (!lowered.Success)
        {
            foreach (var error in lowered.Errors)
                Console.Error.WriteLine(error.Source is null ? $"larva polyglot: {error}" : error.ToString());
            return 1;
        }
        var compilation = new Larva.IR.CompilationOptions { BorrowCheck = Larva.IR.BorrowCheckMode.Off };
        var objectFile = Larva.IR.Compiler.CompileObject(lowered.Module!, compilation);
        if (!objectFile.Success)
        {
            foreach (var error in objectFile.TypeErrors.Concat<object>(objectFile.Diagnostics).Take(20))
                Console.Error.WriteLine($"larva polyglot: {error}");
            return 1;
        }
        var ir = Larva.IR.Linking.Linker.Link([objectFile.Object!], new Larva.IR.Linking.LinkOptions
        {
            CLibrary = true, Entry = "$none", LowerToIl = false, Compilation = compilation,
        });
        if (!ir.Success)
        {
            foreach (var error in ir.Errors)
                Console.Error.WriteLine($"larva polyglot: {error}");
            return 1;
        }
        var (success, message) = Larva.Driver.ExecutableBuilder.BuildLibrary(ir, output, new Larva.Driver.ExecutableOptions
        {
            Backend = backend == "x64" ? Larva.Driver.Backend.X64 : Larva.Driver.Backend.Llvm,
            OptimizationLevel = optimization,
            Compilation = compilation,
        });
        if (!success)
        {
            Console.Error.WriteLine($"larva polyglot: {message}");
            return 1;
        }
        string version = string.Join(".", lib.Version);
        if (c)
        {
            // C: the library, its import library and its header.
            File.Delete(Path.ChangeExtension(output, ".exp"));
            string header = Path.Combine(cFolder, api.Prefix + ".h");
            File.WriteAllText(header, CHeader.Write(api, lib.Name, version), new UTF8Encoding(false));
            foreach (string native in natives)
                File.Copy(native, Path.Combine(cFolder, Path.GetFileName(native)), overwrite: true);
            Console.WriteLine($"{Path.GetFileName(output)}, {Path.GetFileName(Path.ChangeExtension(output, ".lib"))}, {Path.GetFileName(header)}: " +
                $"{lib.Name} for C ({api.Exports.Count} functions, {api.Handles.Count} handle types, {api.Enums.Count} enums) in {cFolder}");
        }
        if (csharp)
        {
            // C#: the face over the C functions, and a project with its own copy of the native library.
            Directory.CreateDirectory(csharpFolder);
            string assembly = string.Concat(api.Prefix.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
            // The C# assembly and the native library sit side by side in a program's output, and Windows does not tell
            // Geometria.dll from geometria.dll: when the names meet, the native one is <prefix>_native.dll.
            string native = assembly.Equals(api.Prefix, StringComparison.OrdinalIgnoreCase) ? api.Prefix + "_native" : api.Prefix;
            foreach (string stale in Directory.GetFiles(csharpFolder, "*.dll"))
                File.Delete(stale);
            File.Copy(output, Path.Combine(csharpFolder, native + ".dll"), overwrite: true);
            foreach (string dependency in natives)
                File.Copy(dependency, Path.Combine(csharpFolder, Path.GetFileName(dependency)), overwrite: true);
            string source = assembly + ".cs";
            File.WriteAllText(Path.Combine(csharpFolder, source), CSharpEmitter.Write(model, api, native, version), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(csharpFolder, assembly + ".csproj"), CSharpEmitter.Project(assembly, source, native, framework), new UTF8Encoding(false));
            Console.WriteLine($"{native}.dll, {source}, {assembly}.csproj: {lib.Name} for C# ({framework}) in {csharpFolder}");
        }
        if (!c)
            Directory.Delete(building, recursive: true);
        return 0;
    }

    /// <summary>The library first, then System.Core and the standard libraries it was built against.</summary>
    private static HirCatalog? Catalog(string library, string? core)
    {
        HirAssembly lib;
        try
        {
            lib = LvaFormat.Load(library);
        }
        catch (Exception e) when (e is IOException or HirFormatException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"larva polyglot: cannot read {library}: {e.Message}");
            return null;
        }
        var assemblies = new List<HirAssembly> { lib };
        if (lib.Name != "System.Core")
        {
            if (SourceBuild.CoreLibrary(core, noCache: false, out _, out _) is not { } coreBytes)
                return null;
            assemblies.Add(LvaFormat.Read(coreBytes));
            foreach (string name in SourceBuild.StandardLibraries.Where(n => n != lib.Name && lib.References.Any(r => r.Name == n)))
            {
                if (BindProfile.StandardLibrary(name, coreBytes, out _, quiet: true) is { } bytes)
                    assemblies.Add(LvaFormat.Read(bytes));
            }
        }
        return HirCatalog.Create(assemblies);
    }
}
