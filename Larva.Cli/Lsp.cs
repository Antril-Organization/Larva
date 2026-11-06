using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// <c>larva lsp</c>: a Language Server Protocol server over standard input and output for the languages installed on
/// Larva. larva keeps the protocol, the documents, the `.queen` projects (and binding profiles, whose features it answers
/// itself) and the decompiled files; each source file's features come from its language's pack (<c>lsp.*</c> requests):
/// errors as you type, completion, hover, go to definition (into decompiled library types when their source is not at
/// hand), signature help, the outline, semantic tokens and workspace symbols. Messages are read and written as JSON nodes
/// (no reflection: the published <c>larva</c> is native code).
/// </summary>
internal sealed class LspServer
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly Dictionary<string, string> documents = new(StringComparer.Ordinal);
    /// <summary>The documents their language has analyzed (as they are now), and which language that is.</summary>
    private readonly Dictionary<string, LanguagePack> analyzed = new(StringComparer.Ordinal);
    /// <summary>The semantic token legend (every language's, one after the other) and where each language's starts.</summary>
    private readonly List<string> tokenTypes = [];
    private readonly Dictionary<LanguagePack, int> tokenOffset = [];
    private string? coreFolder;
    private bool shutdown;

    private LspServer(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    public static int Run(string[] args)
    {
        // Anything written to the console by mistake goes to stderr: stdout carries the protocol.
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        var server = new LspServer(Console.OpenStandardInput(), stdout);
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--core" && i + 1 < args.Length)
                server.coreFolder = args[++i];
        return server.Loop();
    }

    private int Loop()
    {
        while (Read() is { } message)
        {
            try
            {
                if (Handle(message) is false)
                    return shutdown ? 0 : 1;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"larva lsp: {e}");
                if (message["id"] is { } id)
                    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message } });
            }
        }
        return 0;
    }

    // ── Transport ──────────────────────────────────────

    private JsonObject? Read()
    {
        int length = -1;
        while (true)
        {
            string? line = ReadLine();
            if (line is null)
                return null;
            if (line.Length == 0)
                break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line.AsSpan(15).Trim());
        }
        if (length < 0)
            return null;
        var buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = input.Read(buffer, read, length - read);
            if (n == 0)
                return null;
            read += n;
        }
        return JsonNode.Parse(buffer) as JsonObject;
    }

    private string? ReadLine()
    {
        var bytes = new List<byte>();
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0)
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            if (b == '\n')
                return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add((byte)b);
        }
    }

    private void Send(JsonObject message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (output)
        {
            output.Write(header);
            output.Write(body);
            output.Flush();
        }
    }

    private void Reply(JsonNode id, JsonNode? result) =>
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });

    private void Notify(string method, JsonNode parameters) =>
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    // ── Messages ───────────────────────────────────────

    private bool? Handle(JsonObject message)
    {
        string method = message["method"]?.GetValue<string>() ?? "";
        var id = message["id"];
        var parameters = message["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
                Reply(id!, Initialize());
                return true;
            case "initialized":
                return true;
            case "shutdown":
                shutdown = true;
                Reply(id!, null);
                return true;
            case "exit":
                return false;
            case "textDocument/didOpen":
            {
                var document = parameters!["textDocument"]!;
                Changed(document["uri"]!.GetValue<string>(), document["text"]!.GetValue<string>());
                return true;
            }
            case "textDocument/didChange":
            {
                string uri = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                if (parameters["contentChanges"] is JsonArray { Count: > 0 } changes)
                    Changed(uri, changes[^1]!["text"]!.GetValue<string>());
                return true;
            }
            case "workspace/didChangeWatchedFiles":
                // A .queen or source file changed on disk: projects are read again, open documents analyzed again.
                Reload();
                return true;
            case "textDocument/didSave":
                // A project file saved in the editor: what it allows (the borrow checker, the default access…) applies now,
                // whether or not the editor also reports it as a watched file.
                if (parameters!["textDocument"]?["uri"]?.GetValue<string>() is { } saved && saved.EndsWith(".queen", StringComparison.OrdinalIgnoreCase))
                {
                    // Saved: the file is what counts (an open document's text is the file's now).
                    if (documents.ContainsKey(saved) && File.Exists(PathOf(saved)))
                        documents[saved] = File.ReadAllText(PathOf(saved));
                    Reload();
                }
                return true;
            case "textDocument/didClose":
            {
                string uri = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                documents.Remove(uri);
                if (analyzed.Remove(uri, out var pack))
                    Ask(pack, "lsp.close", new JsonObject { ["uri"] = uri });
                Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = new JsonArray() });
                return true;
            }
            case "textDocument/completion" when IsQueen(parameters!):
                Reply(id!, QueenAt(parameters!, (t, l, c) => QueenFiles.Complete(t, l, c)));
                return true;
            case "textDocument/hover" when IsQueen(parameters!):
                Reply(id!, QueenAt(parameters!, (t, l, c) => QueenFiles.Hover(t, l, c)));
                return true;
            case "textDocument/completion":
                Reply(id!, At(parameters!, "lsp.completion"));
                return true;
            case "textDocument/hover":
                Reply(id!, At(parameters!, "lsp.hover"));
                return true;
            case "textDocument/definition" when IsDecompiled(parameters!):
                Reply(id!, DecompiledDefinition(parameters!));
                return true;
            case "textDocument/definition":
                Reply(id!, Definition(At(parameters!, "lsp.definition")));
                return true;
            case "textDocument/signatureHelp":
                Reply(id!, At(parameters!, "lsp.signatures"));
                return true;
            case "textDocument/documentSymbol":
                Reply(id!, Of(parameters!, "lsp.symbols") ?? new JsonArray());
                return true;
            case "textDocument/semanticTokens/full" when IsDecompiled(parameters!):
                Reply(id!, DecompiledTokens(parameters!) ?? new JsonObject { ["data"] = new JsonArray() });
                return true;
            case "textDocument/semanticTokens/full":
                Reply(id!, Tokens(parameters!) ?? new JsonObject { ["data"] = new JsonArray() });
                return true;
            case "workspace/symbol":
                Reply(id!, WorkspaceSymbols(parameters?["query"]?.GetValue<string>() ?? ""));
                return true;
            case "larva/decompile":
            {
                // A type of the catalog as a language's source: { type, language?, bodies? }.
                string? language = parameters!["language"]?.GetValue<string>();
                var pack = (language is null ? null : LanguageRegistry.Named(language)) ?? analyzed.Values.FirstOrDefault() ?? LanguageRegistry.All.FirstOrDefault();
                var decompiled = pack is null ? null : Ask(pack, "lsp.decompile", new JsonObject
                {
                    ["type"] = parameters["type"]!.GetValue<string>(),
                    ["bodies"] = parameters["bodies"]?.GetValue<bool>() ?? true,
                    ["view"] = language,
                });
                Reply(id!, decompiled is null ? null : new JsonObject
                {
                    ["text"] = decompiled["text"]!.GetValue<string>(),
                    ["uri"] = DecompiledUri(Decompiled(decompiled)),
                });
                return true;
            }
            default:
                if (id is not null)
                    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"unknown method {method}" } });
                return true;
        }
    }

    private JsonObject Initialize()
    {
        // The token legend: each language's token types after the ones before (its tokens are moved by its offset).
        foreach (var pack in LanguageRegistry.All)
        {
            try
            {
                tokenOffset[pack] = tokenTypes.Count;
                foreach (var type in pack.Info["tokenTypes"] as JsonArray ?? [])
                    tokenTypes.Add(type!.GetValue<string>());
            }
            catch (LanguagePackException e)
            {
                Console.Error.WriteLine($"larva lsp: {e.Message}");
                tokenOffset.Remove(pack);
            }
        }
        return new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                // Full text on each change, and a notice when a document is saved (a saved .queen reloads its project).
                ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1, ["save"] = new JsonObject { ["includeText"] = false } },
                ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".", ":") },
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["signatureHelpProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ",") },
                ["documentSymbolProvider"] = true,
                ["workspaceSymbolProvider"] = true,
                ["semanticTokensProvider"] = new JsonObject
                {
                    ["legend"] = new JsonObject
                    {
                        ["tokenTypes"] = new JsonArray([.. tokenTypes.Select(t => (JsonNode)t)]),
                        ["tokenModifiers"] = new JsonArray("declaration", "static", "readonly", "defaultLibrary"),
                    },
                    ["full"] = true,
                },
            },
            ["serverInfo"] = new JsonObject { ["name"] = "larva", ["version"] = LarvaVersion.Current },
        };
    }

    /// <summary>A request to a language pack: its answer, or null (what went wrong goes to stderr).</summary>
    private static JsonNode? Ask(LanguagePack pack, string method, JsonObject parameters)
    {
        try
        {
            return pack.Call(method, parameters);
        }
        catch (LanguagePackException e)
        {
            Console.Error.WriteLine($"larva lsp: {e.Message}");
            return null;
        }
    }

    // ── Projects ───────────────────────────────────────

    /// <summary>
    /// What a source file is analyzed with: the other sources of its program (its `.queen` project's, with its same-GC
    /// dependencies', or its folder's) and the libraries of the workspace its language makes once per <c>Key</c>: System.Core,
    /// the Larva libraries the project names (`assembly:`), and its dependencies built for another GC (compiled by the
    /// language as references of their own domain).
    /// </summary>
    private sealed record Project(string Stamp, List<string> Assemblies, List<string> Sources, JsonArray References, JsonArray Foreign, JsonObject Options)
    {
        /// <summary>The project as read (null: a folder without one, built with this machine's defaults).</summary>
        public Larva.Build.QueenProject? Queen { get; init; }

        /// <summary>
        /// The workspace's key: the project as read, what its frontend is told, and its assemblies' write times (one rebuilt
        /// makes a new one).
        /// </summary>
        public string Key => Stamp + "|" + Options.ToJsonString() + string.Concat(Assemblies.Select(a => "|" + File.GetLastWriteTimeUtc(a).Ticks));

        /// <summary>What the project gives its sources (not when it was read): a project file edited to the same changes nothing.</summary>
        public string Settings => Options.ToJsonString() + "|" + string.Join("|", Sources) + "|" + string.Join("|", Assemblies) + "|" + Foreign.ToJsonString();
    }

    private readonly Dictionary<string, Project> projects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>System.Core, as the language packs are given it (found once).</summary>
    private JsonObject? core;
    private string? coreKey;

    private JsonObject? Core()
    {
        if (core is not null)
            return core;
        if (SourceBuild.CoreLibrary(coreFolder, noCache: false, out _, out string? corePath) is not { } bytes)
            return null;
        coreKey = "core|" + (corePath ?? "data");
        // System.Bindings too: a binding profile is analyzed against what it derives from.
        bindings = BindProfile.BindingsLibrary(bytes, out string? bindingsPath, quiet: true) is { } bindingsBytes
            ? bindingsPath is not null ? LanguagePack.Reference(bindingsPath) : LanguagePack.Reference(bindingsBytes)
            : null;
        return core = SourceBuild.CoreReference(bytes, corePath);
    }

    /// <summary>System.Bindings (when there is one), given to every analysis with System.Core.</summary>
    private JsonObject? bindings;

    private JsonArray Libraries(JsonObject coreReference) =>
        bindings is null ? [coreReference.DeepClone()] : [coreReference.DeepClone(), bindings.DeepClone()];

    private static string? FindQueen(string? folder)
    {
        for (; folder is not null && Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
        {
            if (Directory.EnumerateFiles(folder, "*.queen").FirstOrDefault() is { } queen)
                return queen;
        }
        return null;
    }

    private Project? ProjectOf(string path)
    {
        if (Core() is not { } coreReference)
            return null;
        if (FindQueen(Path.GetDirectoryName(path)) is not { } queen)
            return Loose(path, coreReference);
        // The project as the editor has it (an open .queen applies before it is saved), and the machine's defaults
        // (`larva config` changes what a project that does not say gets).
        string? editing = OpenText(queen);
        string stamp = (editing is null ? Stamp(queen) : $"{queen}|open|{editing.GetHashCode()}") + "|" + Stamp(Larva.Build.LarvaDefaults.ConfigPath);
        if (projects.TryGetValue(queen, out var cached) && cached.Stamp == stamp)
            return cached;
        var diagnostics = new List<Larva.Build.QueenDiagnostic>();
        var project = editing is null ? Larva.Build.QueenProject.Load(queen, null, diagnostics, Hive.HiveResolver.Create()) : Larva.Build.QueenProject.LoadText(queen, editing, diagnostics, Hive.HiveResolver.Create());
        // A project file with errors (its own diagnostics say which): the project as it last read, else the file on its own.
        if (project is null)
            return cached ?? Loose(path, coreReference);
        var references = Libraries(coreReference);
        var assemblies = new List<string>();
        foreach (string assembly in project.AllLibraries().Where(Larva.Build.QueenProject.IsAssembly))
        {
            if (File.Exists(assembly))
            {
                references.Add((JsonNode)LanguagePack.Reference(assembly));
                assemblies.Add(assembly);
            }
            else
                Console.Error.WriteLine($"larva lsp: cannot read {assembly}: not found");
        }
        var foreign = project.Dependencies.Where(d => d.Gc != project.Gc).ToList();
        var foreignSources = new HashSet<string>(foreign.SelectMany(d => d.AllSources()), StringComparer.OrdinalIgnoreCase);
        var foreignList = new JsonArray([.. foreign.Select(d => (JsonNode)new JsonObject
        {
            ["name"] = d.Name,
            ["sources"] = new JsonArray([.. d.AllSources().Select(s => (JsonNode)s)]),
        })]);
        var sources = project.AllSources().Where(s => !foreignSources.Contains(s)).ToList();
        var loaded = new Project(stamp, assemblies, sources, references, foreignList, project.FrontendOptions()) { Queen = project };
        projects[queen] = loaded;
        return loaded;
    }

    /// <summary>A file of no project: the other sources of its folder, against System.Core, with this machine's defaults.</summary>
    private Project Loose(string path, JsonObject coreReference)
    {
        string? folder = Path.GetDirectoryName(path);
        var files = folder is not null && Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Where(LanguageRegistry.IsSource).ToList() : [];
        return new Project(coreKey!, [], files, Libraries(coreReference), [], Larva.Build.LarvaDefaults.FrontendOptions());
    }

    /// <summary>The text of a project file open in the editor (null when it is not open).</summary>
    private string? OpenText(string queen)
    {
        foreach (var (uri, text) in documents)
        {
            if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFullPath(PathOf(uri)), queen, StringComparison.OrdinalIgnoreCase))
                return text;
        }
        return null;
    }

    /// <summary>
    /// A project file edited (not saved yet): the open sources of its project analyzed again when what it sets changed
    /// (the borrow checker, unsafe code, the default access, its dependencies…).
    /// </summary>
    private void ProjectEdited(string uri)
    {
        string queen = Path.GetFullPath(PathOf(uri));
        string? before = projects.GetValueOrDefault(queen)?.Settings;
        bool? changed = null;
        foreach (var (openUri, openText) in documents.ToList())
        {
            if (!openUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || IsQueen(openUri))
                continue;
            string path = PathOf(openUri);
            if (FindQueen(Path.GetDirectoryName(path)) is not { } its || !string.Equals(Path.GetFullPath(its), queen, StringComparison.OrdinalIgnoreCase))
                continue;
            changed ??= before is not null && ProjectOf(path)?.Settings != before;
            if (changed == true)
                Changed(openUri, openText);
        }
    }

    /// <summary>The project file and its write time: when it changes, the project is read again.</summary>
    private static string Stamp(string queen) => $"{queen}|{(File.Exists(queen) ? File.GetLastWriteTimeUtc(queen).Ticks : 0)}";

    private static string PathOf(string uri) => uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? FilePath(uri["file:".Length..]) : uri;

    /// <summary>
    /// The file a URI's path names (what follows <c>file:</c> or <c>larva:</c>). Editors escape it as they like: VS Code
    /// sends <c>file:///c%3A/Users/a%20b</c>, which <see cref="Uri.LocalPath"/> reads as <c>/c:/Users/a b</c>.
    /// </summary>
    internal static string FilePath(string rest)
    {
        string path = Uri.UnescapeDataString(rest);
        // `//server/share/…` is a network path; `///c:/…` (or `/c:/…`) a drive's.
        if (path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal))
            return OperatingSystem.IsWindows() ? @"\\" + path[2..].Replace('/', '\\') : path;
        path = "/" + path.TrimStart('/');
        if (OperatingSystem.IsWindows())
        {
            if (path.Length >= 3 && path[2] == ':')
                path = path[1..];
            path = path.Replace('/', '\\');
        }
        return path;
    }

    private static string UriOf(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>A project file (`.queen`).</summary>
    private static bool IsQueen(string uri) => uri.EndsWith(".queen", StringComparison.OrdinalIgnoreCase);

    private static bool IsQueen(JsonObject parameters) => IsQueen(parameters["textDocument"]!["uri"]!.GetValue<string>());

    /// <summary>A `.queen` feature at the request's position, over the document's current text.</summary>
    private JsonNode? QueenAt(JsonObject parameters, Func<string, int, int, JsonNode?> feature)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        if (!documents.TryGetValue(uri, out var text))
            return null;
        var position = parameters["position"]!;
        return feature(text, position["line"]!.GetValue<int>(), position["character"]!.GetValue<int>());
    }

    /// <summary>A `.queen` file: the project reader's diagnostics (QN…) for its current text, never a language's.</summary>
    private void QueenChanged(string uri, string text)
    {
        string path = PathOf(uri);
        var problems = new List<Larva.Build.QueenDiagnostic>();
        Larva.Build.QueenProject.LoadText(path, text, problems);
        var diagnostics = new JsonArray();
        var lines = text.Split('\n');
        foreach (var problem in problems)
        {
            bool here = string.Equals(Path.GetFullPath(problem.File), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            int line = here ? Math.Max(0, problem.Line - 1) : 0;
            int column = here ? Math.Max(0, problem.Column - 1) : 0;
            int end = line < lines.Length ? Math.Max(column + 1, lines[line].TrimEnd('\r').Length) : column + 1;
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["range"] = Range(line, column, line, end),
                ["severity"] = problem.IsError ? 1 : 2,
                ["code"] = problem.Code,
                ["source"] = "queen",
                ["message"] = here ? problem.Message : $"{Path.GetFileName(problem.File)}({problem.Line},{problem.Column}): {problem.Message}",
            });
        }
        Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics });
    }

    /// <summary>Every project read again and every open document analyzed again (a project file changed).</summary>
    private void Reload()
    {
        projects.Clear();
        core = null;
        foreach (var pack in analyzed.Values.Distinct().ToList())
            Ask(pack, "lsp.reset", []);
        analyzed.Clear();
        foreach (var (openUri, openText) in documents.ToList())
            Changed(openUri, openText);
    }

    private void Changed(string uri, string text)
    {
        documents[uri] = text;
        analyzed.Remove(uri);
        // A library type's decompiled source: read-only and not the program's (its names are known from the decompiler).
        if (uri.StartsWith("larva:", StringComparison.OrdinalIgnoreCase))
            return;
        if (IsQueen(uri))
        {
            QueenChanged(uri, text);
            ProjectEdited(uri);
            return;
        }
        if (Analyze(uri) is { } diagnostics)
        {
            Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics });
            BorrowCheckLater(uri, diagnostics);
        }
    }

    // ── The borrow checker ─────────────────────────────

    /// <summary>Each open document's diagnostics from its last analysis (the borrow checker's are added to them).</summary>
    private readonly Dictionary<string, JsonArray> analysisDiagnostics = new(StringComparer.Ordinal);

    /// <summary>Bumped by each edit: a borrow check started before the latest one publishes nothing.</summary>
    private int borrowGeneration;

    /// <summary>
    /// When the document's project checks borrows (BUILD <c>borrowcheck: enforce</c> or <c>warn</c>, or this machine's
    /// default) and its analysis found no errors: the program is compiled, lowered and borrow checked in the background
    /// a moment after the last edit, and each open document of it gets the borrow errors on its lines.
    /// </summary>
    private void BorrowCheckLater(string uri, JsonNode diagnostics)
    {
        int generation = Interlocked.Increment(ref borrowGeneration);
        analysisDiagnostics[uri] = diagnostics as JsonArray ?? [];
        string path = PathOf(uri);
        if (LanguageRegistry.ForFile(path) is not { } pack || ProjectOf(path) is not { } project)
            return;
        var mode = project.Queen?.BorrowCheck ?? Larva.Build.LarvaDefaults.BorrowCheck();
        if (mode == Larva.IR.BorrowCheckMode.Off || project.Foreign.Count > 0
            || analysisDiagnostics[uri].Any(d => d?["severity"]?.GetValue<int>() == 1))
            return;
        // What the editor has now (open documents as they are), taken on this thread.
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (openUri, text) in documents)
        {
            if (openUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                texts[PathOf(openUri)] = text;
        }
        var sources = project.Sources.Where(s => LanguageRegistry.ForFile(s) == pack).ToList();
        if (!sources.Contains(path, StringComparer.OrdinalIgnoreCase))
            sources.Add(path);
        var openInProject = documents.Keys.Where(u => sources.Contains(PathOf(u), StringComparer.OrdinalIgnoreCase)).ToList();
        var analyses = openInProject.ToDictionary(u => u, u => (JsonArray)(analysisDiagnostics.GetValueOrDefault(u) ?? []).DeepClone());
        Task.Run(async () =>
        {
            await Task.Delay(400);
            if (generation != Volatile.Read(ref borrowGeneration))
                return;
            IReadOnlyList<Larva.IR.Analysis.BorrowError> errors;
            try
            {
                errors = BorrowErrors(pack, project, sources, texts);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"larva lsp: borrow check: {e.Message}");
                return;
            }
            if (generation != Volatile.Read(ref borrowGeneration))
                return;
            foreach (var openUri in openInProject)
            {
                string file = Path.GetFullPath(PathOf(openUri));
                var all = analyses[openUri];
                var lines = texts.GetValueOrDefault(PathOf(openUri), "").Split('\n');
                foreach (var error in errors.Where(e => e.Source is { } s && string.Equals(Path.GetFullPath(s.File), file, StringComparison.OrdinalIgnoreCase)))
                {
                    var at = error.Source!.Value;
                    int line = at.Line - 1, start = Math.Max(0, at.Column - 1);
                    int end = line < lines.Length ? Math.Max(start + 1, lines[line].TrimEnd('\r').Length) : start + 1;
                    all.Add((JsonNode)new JsonObject
                    {
                        ["range"] = new JsonObject
                        {
                            ["start"] = new JsonObject { ["line"] = line, ["character"] = start },
                            ["end"] = new JsonObject { ["line"] = line, ["character"] = end },
                        },
                        ["severity"] = mode == Larva.IR.BorrowCheckMode.Enforce ? 1 : 2,
                        ["code"] = error.Kind.ToString(),
                        ["source"] = "borrow checker",
                        ["message"] = error.Message,
                    });
                }
                Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = openUri, ["diagnostics"] = all });
            }
        });
    }

    /// <summary>The program compiled as the editor has it, linked with its libraries, lowered, and its own functions borrow checked.</summary>
    private IReadOnlyList<Larva.IR.Analysis.BorrowError> BorrowErrors(LanguagePack pack, Project project, List<string> sources, Dictionary<string, string> texts)
    {
        if (SourceBuild.CoreLibrary(coreFolder, noCache: false, out _, out _) is not { } coreBytes)
            return [];
        var references = project.References.Select(r => (JsonObject)r!.DeepClone()).ToList();
        var compiled = SourceBuild.Compile(pack, sources, "Program", references, texts: texts, options: project.Options, quiet: true);
        if (compiled?.Assembly is not { } programBytes)
            return [];
        var linked = Larva.Hir.Linking.HirLinker.Link("Program", [coreBytes, .. project.Assemblies.Select(File.ReadAllBytes), programBytes]);
        if (!linked.Success)
            return [];
        var lowered = Larva.Hir.Lowering.HirLowerer.Lower(linked.Assembly!, new Larva.Hir.Lowering.LoweringOptions
        {
            StaticConstructorsFromEntry = true,
            Gc = project.Queen?.Gc ?? Larva.IR.GcConfig.Default,
        });
        if (!lowered.Success || lowered.Module is not { } module)
            return [];
        // Only the program's own functions: the libraries were checked when they were built.
        var files = new HashSet<string>(sources.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var errors = new List<Larva.IR.Analysis.BorrowError>();
        foreach (var function in module.Functions)
        {
            if (function.IsDeclaration || function.Positions is not { Count: > 0 } positions || !positions.Values.Any(p => files.Contains(Path.GetFullPath(p.File))))
                continue;
            errors.AddRange(Larva.IR.Analysis.BorrowChecker.Check(function, module));
        }
        return errors;
    }

    /// <summary>The document analyzed by its language with the rest of its program: the diagnostics, or null.</summary>
    private JsonNode? Analyze(string uri)
    {
        if (!documents.TryGetValue(uri, out var text))
            return null;
        string path = PathOf(uri);
        if (LanguageRegistry.ForFile(path) is not { } pack || ProjectOf(path) is not { } project)
            return null;
        // The other open documents of the program, as they are in the editor.
        var open = new JsonObject();
        foreach (var (otherUri, otherText) in documents)
        {
            if (otherUri != uri && otherUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                open[PathOf(otherUri)] = otherText;
        }
        var diagnostics = Ask(pack, "lsp.analyze", new JsonObject
        {
            ["uri"] = uri,
            ["path"] = path,
            ["text"] = text,
            ["others"] = new JsonArray([.. project.Sources.Where(s => LanguageRegistry.ForFile(s) == pack).Select(s => (JsonNode)s)]),
            ["open"] = open,
            ["workspace"] = new JsonObject
            {
                ["key"] = project.Key,
                ["options"] = project.Options.DeepClone(),
                ["references"] = project.References.DeepClone(),
                ["foreign"] = project.Foreign.DeepClone(),
            },
        });
        if (diagnostics is null)
            return null;
        analyzed[uri] = pack;
        return diagnostics;
    }

    /// <summary>The language that analyzed the document (analyzing it again when it is no longer current).</summary>
    private LanguagePack? PackOf(string uri)
    {
        if (analyzed.TryGetValue(uri, out var pack))
            return pack;
        return Analyze(uri) is not null ? analyzed.GetValueOrDefault(uri) : null;
    }

    /// <summary>A document's feature from its language: <c>{uri}</c>.</summary>
    private JsonNode? Of(JsonObject parameters, string method)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        return PackOf(uri) is { } pack ? Ask(pack, method, new JsonObject { ["uri"] = uri }) : null;
    }

    /// <summary>A feature at the request's position from the document's language: <c>{uri, line, character}</c>.</summary>
    private JsonNode? At(JsonObject parameters, string method)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        if (PackOf(uri) is not { } pack)
            return null;
        var position = parameters["position"]!;
        return Ask(pack, method, new JsonObject
        {
            ["uri"] = uri,
            ["line"] = position["line"]!.GetValue<int>(),
            ["character"] = position["character"]!.GetValue<int>(),
        });
    }

    /// <summary>The document's semantic tokens, their types moved to where its language's are in the legend.</summary>
    private JsonNode? Tokens(JsonObject parameters)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        if (PackOf(uri) is not { } pack || Ask(pack, "lsp.tokens", new JsonObject { ["uri"] = uri }) is not JsonObject tokens)
            return null;
        if (tokenOffset.GetValueOrDefault(pack) is int offset and > 0 && tokens["data"] is JsonArray data)
        {
            for (int i = 3; i < data.Count; i += 5)
                data[i] = data[i]!.GetValue<int>() + offset;
        }
        return tokens;
    }

    private static JsonObject Range(int line, int character, int endLine, int endCharacter) => new()
    {
        ["start"] = new JsonObject { ["line"] = Math.Max(0, line), ["character"] = Math.Max(0, character) },
        ["end"] = new JsonObject { ["line"] = Math.Max(0, endLine), ["character"] = Math.Max(0, endCharacter) },
    };

    private static (int Line, int Character) Position(string text, int offset)
    {
        int line = 0, start = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                start = i + 1;
            }
        }
        return (line, offset - start);
    }

    // ── Features larva completes ───────────────────────

    /// <summary>
    /// A definition as the editor goes to it: in the program's own source (a file, or a document open in the editor), or
    /// in a library type's decompiled source (a file written once, opened read-only), at the name.
    /// </summary>
    private JsonNode? Definition(JsonNode? found)
    {
        if (found is null)
            return null;
        if (found["file"]?.GetValue<string>() is { } file)
        {
            int line = found["line"]!.GetValue<int>(), character = found["character"]!.GetValue<int>();
            if (File.Exists(file))
                return Location(UriOf(file), line, character);
            if (documents.Keys.FirstOrDefault(u => string.Equals(PathOf(u), file, StringComparison.OrdinalIgnoreCase)) is { } open)
                return Location(open, line, character);
            return null;
        }
        if (found["decompiled"] is not JsonObject decompiled)
            return null;
        string path = Decompiled(decompiled);
        string text = decompiled["text"]!.GetValue<string>();
        // Where the language says the name is declared (the member's own overload), else the name found in the text.
        int at = decompiled["at"]?.GetValue<int>() ?? -1;
        if (at < 0)
        {
            string name = found["name"]?.GetValue<string>() ?? "";
            at = text.IndexOf(" " + name, StringComparison.Ordinal);
            at = at < 0 ? 0 : at + 1;
        }
        var (l, c) = Position(text, at);
        return Location(DecompiledUri(path), l, c);
    }

    /// <summary>
    /// A decompiled file as the editor opens it: the `larva` scheme (the extension serves its text), so it is read-only
    /// and is not a document of the program (the type is already in the library: as a file it would exist twice).
    /// </summary>
    private static string DecompiledUri(string file) => "larva:" + new Uri(Path.GetFullPath(file)).AbsoluteUri["file:".Length..];

    // ── Decompiled sources ─────────────────────────────

    /// <summary>What each decompiled file's names are (its <c>tokens</c> and <c>links</c>), by its full path.</summary>
    private static readonly Dictionary<string, JsonNode> decompiledNames = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsDecompiled(JsonObject parameters) =>
        parameters["textDocument"]?["uri"]?.GetValue<string>()?.StartsWith("larva:", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The file a <c>larva:</c> document is.</summary>
    private static string DecompiledPath(string uri) =>
        Path.GetFullPath(FilePath(uri["larva:".Length..]));

    /// <summary>
    /// A decompiled file's names: those its decompilation gave, or (the editor opened it again after a restart) the type
    /// decompiled again, by the file's name, from the library it is in.
    /// </summary>
    private JsonNode? NamesOf(string file)
    {
        if (decompiledNames.TryGetValue(file, out var known))
            return known;
        string type = Path.GetFileNameWithoutExtension(file);
        // Its folder is the library's name (decompiled/System.Core/System.Console.ant).
        if (DecompileType(type, Path.GetFileName(Path.GetDirectoryName(file)), pack: LanguageRegistry.ForFile(file)) is { } again && string.Equals(Decompiled(again), file, StringComparison.OrdinalIgnoreCase))
            return again;
        return null;
    }

    /// <summary>
    /// A type decompiled by a language pack (<paramref name="pack"/>, else the first one the project uses; null when no
    /// catalog is loaded yet or it has no such type): of that library
    /// and arity when known, with where <paramref name="member"/> (a member's key: its overload) is declared.
    /// </summary>
    private JsonNode? DecompileType(string type, string? assembly = null, int? arity = null, string? member = null, LanguagePack? pack = null)
    {
        pack ??= analyzed.Values.FirstOrDefault() ?? LanguageRegistry.All.FirstOrDefault();
        if (pack is null)
            return null;
        var parameters = new JsonObject { ["type"] = type, ["bodies"] = true, ["view"] = pack.Id };
        if (assembly is not null)
            parameters["assembly"] = assembly;
        if (arity is not null)
            parameters["arity"] = arity;
        if (member is not null)
            parameters["member"] = member;
        return Ask(pack, "lsp.decompile", parameters);
    }

    /// <summary>A decompiled file's semantic tokens, from what its decompilation said each name is (in the editor's encoding).</summary>
    private JsonNode? DecompiledTokens(JsonObject parameters)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        string file = DecompiledPath(uri);
        if (NamesOf(file)?["tokens"] is not JsonArray tokens || !documents.TryGetValue(uri, out var text) && !File.Exists(file))
            return null;
        text ??= File.ReadAllText(file);
        int offset = LanguageRegistry.ForFile(file) is { } language ? tokenOffset.GetValueOrDefault(language) : 0;
        var data = new JsonArray();
        int lastLine = 0, lastCharacter = 0;
        for (int i = 0; i + 3 < tokens.Count; i += 4)
        {
            int start = tokens[i]!.GetValue<int>(), length = tokens[i + 1]!.GetValue<int>();
            var (line, character) = Position(text, start);
            data.Add(JsonValue.Create(line - lastLine));
            data.Add(JsonValue.Create(line == lastLine ? character - lastCharacter : character));
            data.Add(JsonValue.Create(length));
            data.Add(JsonValue.Create(tokens[i + 2]!.GetValue<int>() + offset));
            data.Add(JsonValue.Create(tokens[i + 3]!.GetValue<int>()));
            (lastLine, lastCharacter) = (line, character);
        }
        return new JsonObject { ["data"] = data };
    }

    /// <summary>
    /// From a name of a decompiled file to its declaration: the type it names (or the type of the member), decompiled to
    /// its own file, at the declared name.
    /// </summary>
    private JsonNode? DecompiledDefinition(JsonObject parameters)
    {
        string uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        string file = DecompiledPath(uri);
        if (NamesOf(file)?["links"] is not JsonArray links)
            return null;
        string text = documents.TryGetValue(uri, out var open) ? open : File.Exists(file) ? File.ReadAllText(file) : "";
        int at = OffsetOf(text, parameters["position"]!["line"]!.GetValue<int>(), parameters["position"]!["character"]!.GetValue<int>());
        var link = links.FirstOrDefault(l => l!["offset"]!.GetValue<int>() <= at && at <= l["offset"]!.GetValue<int>() + l["length"]!.GetValue<int>());
        if (link is null || DecompileType(link["type"]!.GetValue<string>(), link["assembly"]?.GetValue<string>(), link["arity"]?.GetValue<int>(),
                link["key"]?.GetValue<string>() ?? link["member"]?.GetValue<string>(), LanguageRegistry.ForFile(file)) is not { } target)
            return null;
        string path = Decompiled(target);
        string targetText = target["text"]!.GetValue<string>();
        // The declared name: the member's (its overload), else the type's own.
        var (line, character) = Position(targetText, target["at"]?.GetValue<int>() ?? 0);
        return Location(DecompiledUri(path), line, character);
    }

    private static int OffsetOf(string text, int line, int character)
    {
        int offset = 0;
        for (int l = 0; l < line && offset < text.Length; l++)
        {
            int next = text.IndexOf('\n', offset);
            if (next < 0)
                return text.Length;
            offset = next + 1;
        }
        return Math.Min(text.Length, offset + character);
    }

    /// <summary>The file a type's decompiled source (<c>{assembly, type, text, extension}</c>) is in (written when it changes).</summary>
    private static string Decompiled(JsonNode decompiled)
    {
        string assembly = decompiled["assembly"]!.GetValue<string>();
        string type = decompiled["type"]!.GetValue<string>();
        string text = decompiled["text"]!.GetValue<string>();
        string extension = decompiled["extension"]?.GetValue<string>() ?? ".txt";
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "decompiled", assembly);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, type.Replace('$', '_') + extension);
        if (!File.Exists(file) || File.ReadAllText(file) != text)
            File.WriteAllText(file, text);
        if (decompiled["tokens"] is not null)
            decompiledNames[Path.GetFullPath(file)] = decompiled.DeepClone();
        return file;
    }

    private static JsonObject Location(string uri, int line, int character) =>
        new() { ["uri"] = uri, ["range"] = Range(line, character, line, character) };

    private JsonNode WorkspaceSymbols(string query)
    {
        var result = new JsonArray();
        if (query.Length == 0)
            return result;
        foreach (var pack in analyzed.Values.Distinct().ToList())
        {
            foreach (var hit in Ask(pack, "lsp.workspaceSymbols", new JsonObject { ["query"] = query }) as JsonArray ?? [])
            {
                string uri;
                int line = 0, character = 0;
                if (hit!["file"]?.GetValue<string>() is { } file)
                {
                    uri = UriOf(file);
                    line = hit["line"]!.GetValue<int>();
                    character = hit["character"]!.GetValue<int>();
                }
                else
                    uri = DecompiledUri(Decompiled(hit["decompiled"]!));
                result.Add((JsonNode)new JsonObject
                {
                    ["name"] = hit["name"]!.GetValue<string>(),
                    ["kind"] = hit["kind"]!.GetValue<int>(),
                    ["containerName"] = hit["container"]?.GetValue<string>(),
                    ["location"] = Location(uri, line, character),
                });
            }
        }
        return result;
    }
}
