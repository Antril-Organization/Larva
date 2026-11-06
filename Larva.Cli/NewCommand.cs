using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Larva.Hir;
using Larva.Hir.Encoding;

/// <summary>
/// <c>larva new</c>: a project from a template, in the language chosen. A template is a folder <c>Templates/&lt;Name&gt;/</c>
/// whose identifier is its name with <c>-</c> for each space (<c>Console App</c> is <c>Console-App</c>): its manifest
/// (<c>template.json</c>: <c>name</c>, <c>description</c>, <c>author</c>, <c>version</c>, <c>type</c> exe or library,
/// <c>namespace</c>, <c>files</c>) and its code as Larva HIR, before any language (<c>template.lva</c>; or, while it is
/// being written, its sources under <c>src/</c>, compiled by their language). The project's language view writes that
/// HIR as source, its namespace renamed to the project's name, next to a <c>.queen</c> project file.
/// </summary>
internal static class NewCommand
{
    public const string Usage = """
               larva new                                       the templates and the languages installed
               larva new <template> [<Name>] [--lang <language>] [-o <folder>] [--force]
        """;

    public const string ManifestName = "template.json";
    public const string CodeName = "template.lva";

    /// <summary>A template: its folder, its identifier, its manifest.</summary>
    internal sealed record Template(string Folder, string Id, JsonObject Manifest)
    {
        public string Name => Text("name") ?? Path.GetFileName(Folder);
        public string? Description => Text("description");
        public string? Author => Text("author");
        public string? Version => Text("version");
        public string Type => Text("type") ?? "exe";
        public string Namespace => Text("namespace") ?? "App";

        private string? Text(string key) => Manifest[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    /// <summary>A template's identifier: its folder's name, a <c>-</c> for each space.</summary>
    public static string IdOf(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Replace(' ', '-');

    /// <summary>
    /// Where templates are: <c>LARVA_TEMPLATES</c> (folders separated by <c>;</c>), <c>Templates/</c> next to larva,
    /// <c>%LOCALAPPDATA%/Larva/Templates</c>, and the <c>Templates/</c> of a working copy of Larva above larva.
    /// </summary>
    public static IReadOnlyList<string> Folders
    {
        get
        {
            var folders = new List<string>();
            if (Environment.GetEnvironmentVariable("LARVA_TEMPLATES") is { Length: > 0 } list)
                folders.AddRange(list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            folders.Add(Path.Combine(AppContext.BaseDirectory, "Templates"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "Templates"));
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory).Parent; dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Larva.slnx")) && Directory.Exists(Path.Combine(dir.FullName, "Templates")))
                {
                    folders.Add(Path.Combine(dir.FullName, "Templates"));
                    break;
                }
            }
            return folders;
        }
    }

    /// <summary>The templates installed (the first of an identifier wins), by identifier.</summary>
    public static List<Template> All()
    {
        var templates = new List<Template>();
        foreach (string folder in Folders.Where(Directory.Exists))
        {
            foreach (string template in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                string manifest = Path.Combine(template, ManifestName);
                if (!File.Exists(manifest))
                    continue;
                string id = IdOf(template);
                if (templates.Any(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    if (JsonNode.Parse(File.ReadAllBytes(manifest)) is JsonObject read)
                        templates.Add(new Template(Path.GetFullPath(template), id, read));
                }
                catch (JsonException e)
                {
                    Console.Error.WriteLine($"larva: {manifest}: {e.Message}");
                }
            }
        }
        return templates;
    }

    public static int Run(string[] args)
    {
        string? output = null, languageName = null, pack = null;
        bool force = false;
        var words = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--lang" when i + 1 < args.Length: languageName = args[++i]; break;
                case "--force": force = true; break;
                case "--pack" when i + 1 < args.Length: pack = args[++i]; break;
                case "--list": break;
                default: words.Add(args[i]); break;
            }
        }
        if (pack is not null)
            return Pack(pack, output);
        var templates = All();
        if (words.Count == 0)
        {
            List(templates);
            return 0;
        }
        if (words.Count > 2)
        {
            Console.Error.WriteLine("usage:\n" + Usage);
            return 2;
        }
        var template = templates.FirstOrDefault(t => t.Id.Equals(words[0].Replace(' ', '-'), StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            Console.Error.WriteLine($"larva new: no template `{words[0]}` ({(templates.Count == 0 ? "none is installed" : "installed: " + string.Join(", ", templates.Select(t => t.Id)))})");
            return 1;
        }
        var language = languageName is not null ? LanguageRegistry.Named(languageName) : LanguageRegistry.All.Count == 1 ? LanguageRegistry.All[0] : null;
        if (language is null)
        {
            Console.Error.WriteLine(languageName is null
                ? $"larva new: in which language? (--lang; {LanguageRegistry.Installed()})"
                : $"larva new: no language `{languageName}` ({LanguageRegistry.Installed()})");
            return 2;
        }
        // The project: NAME in ./NAME (or -o), or the folder's name in it.
        string folder = Path.GetFullPath(output ?? (words.Count > 1 ? words[1] : "."));
        string name = Identifier(words.Count > 1 ? words[1] : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
        try
        {
            return Create(template, language, name, folder, force);
        }
        catch (Exception e) when (e is LanguagePackException or IOException or UnauthorizedAccessException or HirFormatException)
        {
            Console.Error.WriteLine($"larva new: {e.Message}");
            return 1;
        }
    }

    private static void List(List<Template> templates)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine("Templates (larva new <template> [Name] [--lang <language>]):");
        if (templates.Count == 0)
            Console.WriteLine("  none installed");
        int width = templates.Count == 0 ? 0 : templates.Max(t => t.Id.Length);
        foreach (var template in templates)
            Console.WriteLine($"  {template.Id.PadRight(width)}  {template.Description ?? template.Name}");
        Console.WriteLine();
        Console.WriteLine("Languages (--lang):");
        if (LanguageRegistry.All.Count == 0)
            Console.WriteLine("  none installed");
        foreach (var pack in LanguageRegistry.All)
            Console.WriteLine($"  {pack.Id,-10}  {pack.Name} {pack.Version} ({string.Join(" ", pack.Extensions)})");
    }

    /// <summary>A name as an identifier: letters, digits and <c>_</c>, not starting with a digit (<c>my-app</c> is <c>my_app</c>).</summary>
    public static string Identifier(string name)
    {
        var text = new StringBuilder();
        foreach (char c in name)
            text.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (text.Length == 0 || char.IsDigit(text[0]))
            text.Insert(0, '_');
        return text.ToString();
    }

    private static int Create(Template template, LanguagePack language, string name, string folder, bool force)
    {
        if (SourceBuild.CoreLibrary(null, noCache: false, out _, out string? corePath) is not { } coreBytes)
            return 1;
        var core = SourceBuild.CoreReference(coreBytes, corePath);
        if (Code(template, core) is not { } assembly)
            return 1;
        // The template's namespace becomes the project's name.
        string from = template.Namespace;
        foreach (var type in assembly.Types)
        {
            if (type.Namespace == from)
                type.Namespace = name;
            else if (type.Namespace.StartsWith(from + ".", StringComparison.Ordinal))
                type.Namespace = name + type.Namespace[from.Length..];
        }
        assembly.Name = name;
        string extension = language.Extensions.FirstOrDefault() ?? "";
        // Each type in the file the template says (its full name in the template: the file's name), the rest in the main one.
        var fileOf = new Dictionary<string, string>(StringComparer.Ordinal);
        if (template.Manifest["files"] is JsonObject files)
        {
            foreach (var (type, file) in files)
            {
                string renamed = type == from ? name : type.StartsWith(from + ".", StringComparison.Ordinal) ? name + type[from.Length..] : type;
                fileOf[renamed] = file!.GetValue<string>() + extension;
            }
        }
        string main = (template.Manifest["main"]?.GetValue<string>() ?? (template.Type == "library" ? name : "Program")) + extension;
        var sources = BindCommand.Write(language, assembly, [core], fileOf, main, nonPublic: true, header: false);
        if (sources is null)
            return 1;
        string queen = Path.Combine(folder, name + Larva.Build.QueenProject.Extension);
        var written = sources.Keys.Select(f => Path.Combine(folder, f)).Append(queen).ToList();
        if (!force && written.FirstOrDefault(File.Exists) is { } existing)
        {
            Console.Error.WriteLine($"larva new: {existing} exists (--force writes over it)");
            return 1;
        }
        Directory.CreateDirectory(folder);
        foreach (var (file, text) in sources)
            File.WriteAllText(Path.Combine(folder, file), text);
        // BUILD says what this machine's defaults are now (`larva config`), so the project builds the same elsewhere.
        var build = new Dictionary<string, string>(Larva.Build.LarvaDefaults.ForNewProjects);
        foreach (var (key, value) in Larva.Build.LarvaDefaults.Effective())
            build[key] = value;
        string buildChunk = string.Join(",\n", Larva.Build.LarvaDefaults.Keys.Where(k => build.ContainsKey(k) && Larva.Build.LarvaDefaults.IsBuildKey(k)).Select(k => $"   {k}: {build[k]}"));
        File.WriteAllText(queen, $"""
            queen 1.0;

            chunk PROJ:
               name: {name},
               type: {template.Type},
               version: v1.0

            chunk BUILD:

            """.Replace("\r\n", "\n") + buildChunk + "\n");
        Console.WriteLine($"{folder}: {template.Name} in {language.Name} ({string.Join(", ", written.Select(Path.GetFileName))})");
        Console.WriteLine($"  larva build {Path.GetRelativePath(Directory.GetCurrentDirectory(), queen)}");
        return 0;
    }

    /// <summary>The template's code as HIR: its <c>template.lva</c>, or its sources (<c>src/</c>) compiled by their language.</summary>
    private static HirAssembly? Code(Template template, JsonObject core, bool library = false)
    {
        string compiled = Path.Combine(template.Folder, CodeName);
        if (File.Exists(compiled))
            return LvaFormat.Load(compiled);
        string src = Path.Combine(template.Folder, "src");
        var sources = Directory.Exists(src) ? Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Where(LanguageRegistry.IsSource).Order(StringComparer.Ordinal).ToList() : [];
        if (sources.Count == 0)
        {
            Console.Error.WriteLine($"larva new: the template {template.Name} has no code ({CodeName}, or sources in src/ of an installed language)");
            return null;
        }
        if (SourceBuild.Language(sources) is not { } language)
            return null;
        var result = SourceBuild.Compile(language, sources, template.Namespace, [core], library: library);
        return result?.Assembly is { } bytes ? LvaFormat.Read(bytes) : null;
    }

    /// <summary>
    /// <c>larva new --pack &lt;template folder&gt; -o &lt;folder&gt;</c>: the template as an installation ships it, in
    /// <c>folder/&lt;its name&gt;</c>: its manifest and its sources (src/) compiled into <c>template.lva</c> (no sources:
    /// the language of each project writes them).
    /// </summary>
    public static int Pack(string source, string? output)
    {
        string manifest = Path.Combine(source, ManifestName);
        if (output is null || !File.Exists(manifest))
        {
            Console.Error.WriteLine(output is null ? "usage: larva new --pack <template folder> -o <folder>" : $"larva new: {source} has no {ManifestName}");
            return output is null ? 2 : 1;
        }
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)));
        var template = new Template(Path.GetFullPath(source), IdOf(source), JsonNode.Parse(File.ReadAllBytes(manifest)) as JsonObject ?? []);
        if (SourceBuild.CoreLibrary(null, noCache: false, out _, out string? corePath) is not { } coreBytes)
            return 1;
        if (Code(template, SourceBuild.CoreReference(coreBytes, corePath), library: true) is not { } assembly)
            return 1;
        string target = Path.Combine(output, name);
        Directory.CreateDirectory(target);
        File.Copy(manifest, Path.Combine(target, ManifestName), overwrite: true);
        LvaFormat.Save(assembly, Path.Combine(target, CodeName));
        Console.WriteLine($"{target}: {template.Id} ({assembly.Types.Count} types)");
        return 0;
    }
}
