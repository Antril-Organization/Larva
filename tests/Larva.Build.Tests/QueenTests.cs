using Larva.IR;

namespace Larva.Build.Tests;

/// <summary>Project files (<c>.queen</c>): the format, the project model, <c>$[…]</c> and building a project.</summary>
public sealed class QueenTests(QueenTests.ProjectsFixture projects) : CheckedTest, IClassFixture<QueenTests.ProjectsFixture>
{
    // ── format ──

    [Fact]
    public void Format_reads_values_lists_templates_and_free_chunks()
    {
        var diagnostics = new List<QueenDiagnostic>();
        var document = QueenParser.Parse("""
            queen 1.0;
            // a comment
            chunk PROJ:
               name: System,
               type: library
               version: v1.0.42,
               author: Antril Organization,
               ratio: 0.75, count: -3
               quoted: "a, b", url: https://antril.dev/x
               tags: [one, two words, v2,
                      "three, four"]
               mixed: v$[1 + 1].0 /* a block comment */
            chunk OTHER:
               flag: true
            """, "test.queen", diagnostics);
        Check("parses without errors", document is not null && diagnostics.Count == 0, string.Join("; ", diagnostics));
        if (document is null)
            return;
        var proj = document.Named("PROJ").Single();
        Check("an identifier", proj["name"]?.Value is QueenIdentifier { Name: "System" });
        Check("an entry ends at the end of its line (no comma needed)", proj["type"]?.Value is QueenIdentifier { Name: "library" });
        Check("a version has any number of u32 terms", proj["version"]?.Value is QueenVersion { Terms: [1, 0, 42] });
        Check("unquoted text keeps its inner spaces", proj["author"]?.Value is QueenText { Value: "Antril Organization" });
        Check("numbers with decimals and signs", proj["ratio"]?.Value is QueenNumber { Value: 0.75m } && proj["count"]?.Value is QueenNumber { Value: -3m });
        Check("quotes allow commas; `//` after `:` is text", proj["quoted"]?.Value is QueenText { Value: "a, b" } && proj["url"]?.Value is QueenText { Value: "https://antril.dev/x" });
        Check("lists span lines and mix kinds", proj["tags"]?.Value is QueenList { Items: [QueenIdentifier, QueenText { Value: "two words" }, QueenVersion, QueenText { Value: "three, four" }] });
        Check("$[…] inside text is kept for evaluation", proj["mixed"]?.Value is QueenTemplate { Parts: ["v", QueenExpression { Source: "1 + 1" }, ".0"] });
        Check("free chunks are kept", document.Named("OTHER").Single()["flag"]?.Value is QueenBool { Value: true });
    }

    [Fact]
    public void Format_errors_have_their_codes()
    {
        Expect("queen is the first word", "chunk PROJ:\n name: A", "QN001");
        Expect("a key is set once", "queen 1.0;\nchunk PROJ:\n name: A,\n name: B", "QN011");
        Expect("a chunk appears once", "queen 1.0;\nchunk PROJ:\n name: A\nchunk PROJ:\n name: B", "QN008");
        Expect("lists close", "queen 1.0;\nchunk PROJ:\n tags: [a, b", "QN012");
    }

    /// <summary>A hostile file with 200,000 `[` must not take larva (or the language server) down with the stack.</summary>
    [Fact]
    public void Format_limits_how_deep_lists_nest()
    {
        var diagnostics = new List<QueenDiagnostic>();
        string nested(int depth) => new string('[', depth) + "a" + new string(']', depth);
        var document = QueenParser.Parse($"queen 1.0;\nchunk PROJ:\n name: A,\n tags: {nested(QueenParser.MaxListDepth)},\n type: library", "test.queen", diagnostics);
        Check("lists nest up to the limit", diagnostics.Count == 0 && document?.Named("PROJ").Single()["tags"]?.Value is QueenList, string.Join("; ", diagnostics));

        diagnostics.Clear();
        document = QueenParser.Parse($"queen 1.0;\nchunk PROJ:\n name: A,\n tags: {nested(QueenParser.MaxListDepth + 1)},\n type: library", "test.queen", diagnostics);
        Check("one level more is QN017, and the entries after the list are still read", diagnostics.Count(d => d.Code == "QN017") == 1 && diagnostics.Count == 1
            && document?.Named("PROJ").Single()["type"]?.Value is QueenIdentifier { Name: "library" }, string.Join("; ", diagnostics));

        diagnostics.Clear();
        document = QueenParser.Parse("queen 1.0;\nchunk PROJ:\n name: A,\n tags: " + new string('[', 200_000) + "\n type: library", "test.queen", diagnostics);
        Check("200,000 `[` is one diagnostic, not a crash", diagnostics.Any(d => d.Code == "QN017") && document is not null, string.Join("; ", diagnostics.Take(3)));
    }

    private void Expect(string name, string text, string code)
    {
        // The format, then the `project` profile (a key set twice that is not `many` is the profile's to say: QN011).
        var diagnostics = new List<QueenDiagnostic>();
        if (QueenParser.Parse(text, "project.queen", diagnostics) is { } document)
            Profiles.QueenValidator.Validate(document, Profiles.ProfileCatalog.Get("project")!, diagnostics);
        Check($"{name}: {code}", diagnostics.Any(d => d.Code == code), string.Join("; ", diagnostics));
    }

    // ── projects ──

    [Fact]
    public void A_project_reads_its_settings_sources_and_dependencies()
    {
        string root = projects.Root;
        var diagnostics = new List<QueenDiagnostic>();
        var project = QueenProject.Load(Path.Combine(root, "App"), null, diagnostics)
            ?? QueenProject.Load(QueenProject.Find(Path.Combine(root, "App"))!, null, diagnostics);
        Check("the project loads", project is not null, string.Join("; ", diagnostics));
        if (project is null)
            return;
        Check("PROJ: name, type, author", project.Name == "App" && project.Kind == ProjectKind.Exe && project.Author == "Antril Organization");
        Check("BUILD: gc, backend, optimization", project.Gc == GcConfig.RefCount && project.Backend == ProjectBackend.X64 && project.Optimization == 1);
        var own = project.Sources.Select(s => Path.GetRelativePath(Path.Combine(root, "App"), s).Replace('\\', '/')).ToList();
        Check("sources: the folder, recursively, minus ignore and nested projects", own.SequenceEqual(["Main.ant", "Tests/Deep/Kept.ant"]), string.Join(", ", own));
        Check("DEPS: a library project, whose sources come first",
            project.Dependencies is [{ Name: "MathLib", Kind: ProjectKind.Library }] && project.AllSources()[0].EndsWith("Area.ant", StringComparison.Ordinal));
        Check("free chunks stay readable", project.Document.Named("NOTES").Single()["reviewed"]?.Value is QueenBool { Value: true });
    }

    /// <summary>DEPS: any number of `local`, `package` noted for the packager; BUILD access.default.</summary>
    [Fact]
    public void DEPS_takes_several_local_dependencies_and_packages()
    {
        var manyDiagnostics = new List<QueenDiagnostic>();
        var many = QueenProject.Load(Path.Combine(projects.Root, "Many/project.queen"), null, manyDiagnostics);
        Check("DEPS: two `local` dependencies and a `package` (skipped with a warning without Hive)", many is { Dependencies.Count: 2, Packages: [{ Id: "Tester/Json", Version.Version: [2, 1] }] }
            && manyDiagnostics.Any(d => d.Code == "QN105" && !d.IsError), string.Join("; ", manyDiagnostics));
        Check("BUILD access.default: public", many?.DefaultAccess == "public");
    }

    [Fact]
    public void Project_errors_have_their_codes()
    {
        string root = projects.Root;
        ExpectLoad("projects cannot depend on each other in a cycle", Path.Combine(root, "Cycle/A/project.queen"), "QN100");
        ExpectLoad("known keys check their values", Path.Combine(root, "Bad/project.queen"), "QN124");
        ExpectLoad("a project has a PROJ chunk", Path.Combine(root, "NoName/project.queen"), "QN120");
        ExpectLoad("dependencies are libraries", Path.Combine(root, "ExeDep/project.queen"), "QN103");
        ExpectLoad("FILES into: cannot climb out with ..", Path.Combine(root, "IntoUp/project.queen"), "QN131");
        ExpectLoad("FILES into: cannot be a drive path", Path.Combine(root, "IntoDrive/project.queen"), "QN131");
        ExpectLoad("FILES into: cannot be a rooted path", Path.Combine(root, "IntoRoot/project.queen"), "QN131");
        var diagnostics = new List<QueenDiagnostic>();
        var project = QueenProject.Load(Path.Combine(root, "IntoOk/project.queen"), null, diagnostics);
        Check("FILES into: a folder under the build's is fine", project is not null && project.CopiedFiles.Any(f => f.Target == "data/conf/Main.ant"), string.Join("; ", diagnostics));
    }

    /// <summary>Tools: `type: tool` with its command (by default the name in lower case), and a project's TOOLS chunk.</summary>
    [Fact]
    public void Tool_projects_and_TOOLS_entries_are_read()
    {
        string root = Path.Combine(projects.Root, "Tools");
        Directory.CreateDirectory(root);
        QueenProject? Load(string name, string text, List<QueenDiagnostic> diagnostics)
        {
            string file = Path.Combine(root, name, "project.queen");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            return QueenProject.Load(file, null, diagnostics);
        }
        var diagnostics = new List<QueenDiagnostic>();
        var nest = Load("Nest", "queen 1.0;\nchunk PROJ:\n name: Nest,\n type: tool,\n command: nest-db,\n version: v1.0\n", diagnostics);
        Check("a tool and its command", nest is { Kind: ProjectKind.Tool, Command: "nest-db" } && diagnostics.Count == 0, string.Join("; ", diagnostics));
        Check("a tool builds an executable", nest is not null && nest.DefaultOutput(native: true).EndsWith(OperatingSystem.IsWindows() ? "Nest.exe" : "Nest", StringComparison.Ordinal));
        var plain = Load("Plain", "queen 1.0;\nchunk PROJ:\n name: Plain,\n type: tool\n", diagnostics);
        Check("the command by default: the name in lower case", plain?.Command == "plain");

        diagnostics.Clear();
        Load("BadCommand", "queen 1.0;\nchunk PROJ:\n name: B,\n type: tool,\n command: \"Not A Word\"\n", diagnostics);
        Check("a command is a word: QN124", diagnostics.Any(d => d.Code == "QN124"), string.Join("; ", diagnostics));
        diagnostics.Clear();
        var exe = Load("ExeCommand", "queen 1.0;\nchunk PROJ:\n name: E,\n command: e\n", diagnostics);
        Check("`command` outside a tool: QN126 (a warning), ignored", exe is { Command: null } && diagnostics.Any(d => d.Code == "QN126" && !d.IsError), string.Join("; ", diagnostics));

        diagnostics.Clear();
        var app = Load("App", "queen 1.0;\nchunk PROJ:\n name: App\nchunk TOOLS:\n tool: Acme/Nest@^1.2,\n tool: Acme/Lint\n", diagnostics);
        Check("TOOLS: each `tool:` entry, with its version", app is not null && app.Tools.Select(t => t.ToString()).SequenceEqual(["Acme/Nest@^1.2", "Acme/Lint"]) && diagnostics.Count == 0,
            string.Join("; ", diagnostics) + " " + string.Join(", ", app?.Tools ?? []));
        diagnostics.Clear();
        Load("BadTool", "queen 1.0;\nchunk PROJ:\n name: T\nchunk TOOLS:\n tool: nope,\n other: Acme/X\n", diagnostics);
        Check("TOOLS: a bad reference is QN104, an unknown key QN121", diagnostics.Any(d => d.Code == "QN104") && diagnostics.Any(d => d.Code == "QN121"), string.Join("; ", diagnostics));
    }

    /// <summary>
    /// FILES patterns share one walk of each folder: a pattern below one walked reads that walk; hidden folders, build
    /// output and packages are not entered, unless a pattern names the packages; a folder named itself is copied whole.
    /// </summary>
    [Fact]
    public void FILES_patterns_skip_hidden_folders_build_output_and_packages()
    {
        string root = Path.Combine(projects.Root, "Patterns");
        void Write(string relative)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative);
        }
        foreach (var file in new[] { "a.txt", "docs/b.txt", "docs/deep/c.txt", ".hidden/d.txt", "bin/e.txt", "obj/f.txt",
                     "node_modules/pkg/g.txt", "docs/node_modules/h.txt", "docs/.cache/i.txt", "assets/j.txt", "assets/sub/k.txt", "assets/.l.txt" })
            Write(file);
        File.WriteAllText(Path.Combine(root, "project.queen"),
            "queen 1.0;\nchunk PROJ:\n   name: Patterns\nchunk BUILD:\n   stdlib: none\nchunk FILES:\n   copy: [**/*.txt, docs/**/*.txt, docs/*.txt, node_modules/**/*.txt, assets, .hidden/*.txt]\n");
        var diagnostics = new List<QueenDiagnostic>();
        var project = QueenProject.Load(Path.Combine(root, "project.queen"), null, diagnostics);
        var copied = project?.CopiedFiles.Select(f => f.Target).Order(StringComparer.Ordinal).ToList() ?? [];
        string[] expected = [".hidden/d.txt", "a.txt", "assets/j.txt", "assets/sub/k.txt", "docs/b.txt", "docs/deep/c.txt", "node_modules/pkg/g.txt"];
        Check("each pattern's files, once", copied.SequenceEqual(expected), string.Join(", ", copied) + " | " + string.Join("; ", diagnostics));
        // Each pattern on its own (a fresh walk) finds what it found in the shared one.
        Check("a pattern below a walked folder finds the same files on its own",
            QueenProject.Expand(root, "docs/**/*.txt").Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).SequenceEqual(["docs/b.txt", "docs/deep/c.txt"]));
        Check("packages are searched by a pattern that names them",
            QueenProject.Expand(root, "**/node_modules/*.txt").Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).SequenceEqual(["docs/node_modules/h.txt"]));
    }

    /// <summary>
    /// A project is `project.queen`: another name is QN140; the name defaults to the folder's; a project is found from a
    /// file or folder up, not above the repository's top (`.git`) or an editor's workspace folder.
    /// </summary>
    [Fact]
    public void A_project_is_project_queen_found_up_to_the_repository()
    {
        string root = Path.Combine(projects.Root, "Named");
        Directory.CreateDirectory(Path.Combine(root, "Old"));
        File.WriteAllText(Path.Combine(root, "Old", "Old.queen"), "queen 1.0;\nchunk PROJ:\n name: Old\n");
        var diagnostics = new List<QueenDiagnostic>();
        Check("a project file named otherwise: QN140, and nothing loads", QueenProject.Load(Path.Combine(root, "Old", "Old.queen"), null, diagnostics) is null
            && diagnostics.Any(d => d.Code == "QN140" && d.Message.Contains("project.queen", StringComparison.Ordinal)), string.Join("; ", diagnostics));
        Check("a folder whose file is named otherwise has no project", QueenProject.Find(Path.Combine(root, "Old")) is null);

        string shop = Path.Combine(root, "Shop");
        Directory.CreateDirectory(Path.Combine(shop, "src", "deep"));
        File.WriteAllText(Path.Combine(shop, "project.queen"), "queen 1.0;\nchunk PROJ:\n type: library\nchunk BUILD:\n stdlib: none\n");
        diagnostics.Clear();
        var project = QueenProject.Load(QueenProject.Find(shop)!, null, diagnostics);
        Check("PROJ without `name`: the folder's name", project is { Name: "Shop", Kind: ProjectKind.Library }, string.Join("; ", diagnostics));
        Check("found from a folder inside it", QueenProject.Nearest(Path.Combine(shop, "src", "deep")) == Path.Combine(shop, "project.queen"));

        // Above the repository's top, or a workspace folder, a project file is not this code's.
        string repository = Path.Combine(root, "Repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(Path.Combine(repository, "loose"));
        File.WriteAllText(Path.Combine(root, "project.queen"), "queen 1.0;\nchunk PROJ:\n name: Above\n");
        Check("not above a folder with .git", QueenProject.Nearest(Path.Combine(repository, "loose")) is null);
        string workspace = Path.Combine(root, "Workspace", "code");
        Directory.CreateDirectory(workspace);
        Check("not above a workspace folder", QueenProject.Nearest(workspace, [Path.Combine(root, "Workspace")]) is null);
        Check("without a stop, the nearest one up", QueenProject.Nearest(workspace) == Path.Combine(root, "project.queen"));
        File.Delete(Path.Combine(root, "project.queen"));
    }

    private void ExpectLoad(string name, string path, string code)
    {
        var diagnostics = new List<QueenDiagnostic>();
        var project = QueenProject.Load(path, null, diagnostics);
        Check($"{name}: {code}", project is null && diagnostics.Any(d => d.Code == code), string.Join("; ", diagnostics));
    }

    /// <summary>The projects the facts load, written once in a temporary folder (deleted at the end).</summary>
    public sealed class ProjectsFixture : IDisposable
    {
        public string Root { get; } = TestEnvironment.TempFolder("queen");

        public ProjectsFixture()
        {
            Write("MathLib/project.queen", "queen 1.0;\nchunk PROJ:\n   name: MathLib,\n   type: library,\n   version: v0.3.1\n");
            Write("MathLib/Geometry/Area.ant", "into namespace MathLib\n{\n    public Square(i32 side) : i32 { return side * side; }\n}\n");
            Write("App/project.queen", """
                queen 1.0;
                chunk PROJ:
                   name: App,
                   type: exe,
                   version: v2.0,
                   author: Antril Organization
                chunk BUILD:
                   gc: refcount,
                   backend: x64,
                   optimization: 1,
                   ignore: [Drafts, Tests/*.ant]
                chunk DEPS:
                   MathLib: ../MathLib
                chunk NOTES:
                   reviewed: true
                """);
            Write("App/Main.ant", "import MathLib;\nMain() : i32 { return Square(3) + Square(4); }\n");
            Write("App/Drafts/Broken.ant", "this is not antril");
            Write("App/Tests/Bad.ant", "nor this");
            Write("App/Tests/Deep/Kept.ant", "KeptHelper() : i32 { return 1; }\n");
            Write("App/Nested/project.queen", "queen 1.0;\nchunk PROJ:\n   name: Nested\n");
            Write("App/Nested/Other.ant", "not part of App");

            Write("Other/project.queen", "queen 1.0;\nchunk PROJ:\n   name: Other,\n   type: library\n");
            Write("Other/Other.ant", "into namespace Other { public Three() : i32 { return 3; } }\n");
            Write("Many/project.queen", "queen 1.0;\nchunk PROJ:\n   name: Many\nchunk BUILD:\n   stdlib: none,\n   access.default: public\nchunk DEPS:\n   local: ../MathLib\n   local: ../Other\n   package: Tester/Json@2.1\n");
            Write("Many/Main.ant", "import MathLib;\nimport Other;\nclass Open { i32 x; new() { x = 4; } }\nMain() : i32 { return Square(3) + Three() + new Open().x; }\n");

            Write("Cycle/A/project.queen", "queen 1.0;\nchunk PROJ:\n   name: A,\n   type: library\nchunk DEPS:\n   B: ../B\n");
            Write("Cycle/B/project.queen", "queen 1.0;\nchunk PROJ:\n   name: B,\n   type: library\nchunk DEPS:\n   A: ../A\n");
            Write("Bad/project.queen", "queen 1.0;\nchunk PROJ:\n   name: Bad,\n   type: plugin\nchunk BUILD:\n   gc: magic\n");
            Write("NoName/project.queen", "queen 1.0;\nchunk BUILD:\n   gc: arena\n");
            Write("ExeDep/project.queen", "queen 1.0;\nchunk PROJ:\n   name: ExeDep\nchunk DEPS:\n   App: ../App\n");
            foreach (var (name, into) in new[] { ("IntoUp", "../../out"), ("IntoDrive", "C:/Windows/Temp"), ("IntoRoot", "/etc"), ("IntoOk", "data/conf") })
            {
                Write($"{name}/project.queen", $"queen 1.0;\nchunk PROJ:\n   name: {name}\nchunk BUILD:\n   stdlib: none\nchunk FILES:\n   copy: Main.ant,\n   into: \"{into}\"\n");
                Write($"{name}/Main.ant", "Main() : i32 => 0;\n");
            }
        }

        private void Write(string relative, string text)
        {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
