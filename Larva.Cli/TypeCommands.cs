using System.Text;
using System.Text.Json.Nodes;
using Larva.Hir;
using Larva.Hir.Encoding;
using Larva.Hir.Services;

/// <summary>
/// Looking at types from the command line, in any language's view:
/// <list type="bullet">
/// <item><c>larva types [namespace]</c>: the namespaces and their types (all of them, or one namespace's).</item>
/// <item><c>larva show Type</c>: a type decompiled to the language (<c>--no-bodies</c>: declarations only).</item>
/// <item><c>larva search query</c>: types and members by name (<c>sb</c> finds StringBuilder).</item>
/// </list>
/// The catalog is System.Core plus <c>--lib X.dll</c> libraries and the program's source files given. The view is
/// Larva's own (<c>--lang hir</c>) or an installed language's (its pack writes it; by default the language of the sources
/// given, or the only one installed).
/// </summary>
internal static class TypeCommands
{
    public static int Run(string command, string[] args)
    {
        var libraries = new List<string>();
        var sources = new List<string>();
        var words = new List<string>();
        string? language = null;
        string? core = null;
        bool bodies = true, nonPublic = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lib" when i + 1 < args.Length:
                    libraries.Add(args[++i]);
                    break;
                case "--lang" when i + 1 < args.Length:
                    language = args[++i];
                    break;
                case "--core" when i + 1 < args.Length:
                    core = args[++i];
                    break;
                case "--no-bodies":
                    bodies = false;
                    break;
                case "--public":
                    nonPublic = false;
                    break;
                default:
                    if (LanguageRegistry.IsSource(args[i]))
                        sources.Add(args[i]);
                    else if (Larva.Build.QueenProject.IsAssembly(args[i]))
                        libraries.Add(args[i]);
                    else if (args[i].EndsWith(".queen", StringComparison.OrdinalIgnoreCase))
                    {
                        // A project: its sources (and its dependencies') and the Larva libraries it names.
                        var diagnostics = new List<Larva.Build.QueenDiagnostic>();
                        var project = Larva.Build.QueenProject.Load(Path.GetFullPath(args[i]), null, diagnostics, Hive.HiveResolver.Create());
                        foreach (var diagnostic in diagnostics)
                            Console.Error.WriteLine(diagnostic);
                        if (project is null)
                            return 1;
                        sources.AddRange(project.AllSources());
                        libraries.AddRange(project.AllLibraries().Where(Larva.Build.QueenProject.IsAssembly));
                    }
                    else
                        words.Add(args[i]);
                    break;
            }
        }
        // The program's sources, compiled by their language: the catalog's first assembly.
        LanguagePack? sourceLanguage = null;
        if (sources.Count > 0 && (sourceLanguage = SourceBuild.Language(sources)) is null)
            return 1;
        LanguagePack? view = null;
        if (language is null)
            view = sourceLanguage ?? (LanguageRegistry.All.Count == 1 ? LanguageRegistry.All[0] : null);
        else if (!language.Equals("hir", StringComparison.OrdinalIgnoreCase) && (view = LanguageRegistry.Named(language)) is null)
        {
            Console.Error.WriteLine($"larva: no view for the language `{language}` (hir; {LanguageRegistry.Installed()})");
            return 2;
        }
        if (SourceBuild.CoreLibrary(core, noCache: false, out _, out string? corePath) is not { } coreBytes)
            return 1;
        var assemblies = new List<JsonObject> { SourceBuild.CoreReference(coreBytes, corePath) };
        foreach (var library in libraries)
        {
            if (!File.Exists(library))
            {
                Console.Error.WriteLine($"larva: cannot read {library}: not found");
                return 1;
            }
            assemblies.Add(LanguagePack.Reference(library));
        }
        if (sourceLanguage is not null)
        {
            var program = SourceBuild.Compile(sourceLanguage, sources, "Program", assemblies);
            if (program?.Assembly is not { } bytes)
                return 1;
            assemblies.Insert(0, LanguagePack.Reference(bytes));
        }
        Console.OutputEncoding = new UTF8Encoding(false);
        var options = new DecompileOptions { Bodies = bodies, NonPublic = nonPublic };
        if (view is null)
            return Hir(command, assemblies, words, options);
        JsonNode? result;
        try
        {
            result = view.Call("catalog", new JsonObject
            {
                ["command"] = command,
                ["words"] = new JsonArray([.. words.Select(w => (JsonNode)w)]),
                ["bodies"] = bodies,
                ["nonPublic"] = nonPublic,
                ["assemblies"] = new JsonArray([.. assemblies.Select(a => a.DeepClone())]), // each may be in an earlier request already
            });
        }
        catch (LanguagePackException e)
        {
            Console.Error.WriteLine($"larva: {e.Message}");
            return 1;
        }
        Console.Write(result?["output"]?.GetValue<string>());
        Console.Error.Write(result?["error"]?.GetValue<string>());
        return result?["exit"]?.GetValue<int>() ?? 1;
    }

    /// <summary>The command through Larva's own view (HIR), here.</summary>
    private static int Hir(string command, List<JsonObject> references, List<string> words, DecompileOptions options)
    {
        var assemblies = new List<HirAssembly>();
        foreach (var reference in references)
        {
            try
            {
                assemblies.Add(reference["data"]?.GetValue<string>() is { } data
                    ? LvaFormat.Read(Convert.FromBase64String(data))
                    : LvaFormat.Load(reference["path"]!.GetValue<string>()));
            }
            catch (Exception e) when (e is IOException or HirFormatException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"larva: cannot read {reference["path"]}: {e.Message}");
                return 1;
            }
        }
        return CatalogCommands.Run(command, HirCatalog.Create(assemblies), LanguageViews.Get("hir")!, words, options, Console.Out, Console.Error);
    }
}
