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
        var diagnostics = new List<QueenDiagnostic>();
        QueenParser.Parse(text, "test.queen", diagnostics);
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
        var many = QueenProject.Load(Path.Combine(projects.Root, "Many/Many.queen"), null, manyDiagnostics);
        Check("DEPS: two `local` dependencies and a `package` (skipped with a warning without Hive)", many is { Dependencies.Count: 2, Packages: [{ Id: "Tester/Json", Version.Version: [2, 1] }] }
            && manyDiagnostics.Any(d => d.Code == "QN105" && !d.IsError), string.Join("; ", manyDiagnostics));
        Check("BUILD access.default: public", many?.DefaultAccess == "public");
    }

    [Fact]
    public void Project_errors_have_their_codes()
    {
        string root = projects.Root;
        ExpectLoad("projects cannot depend on each other in a cycle", Path.Combine(root, "Cycle/A/A.queen"), "QN100");
        ExpectLoad("known keys check their values", Path.Combine(root, "Bad/Bad.queen"), "QN124");
        ExpectLoad("a project has a PROJ chunk", Path.Combine(root, "NoName/NoName.queen"), "QN120");
        ExpectLoad("dependencies are libraries", Path.Combine(root, "ExeDep/ExeDep.queen"), "QN103");
        ExpectLoad("FILES into: cannot climb out with ..", Path.Combine(root, "IntoUp/IntoUp.queen"), "QN131");
        ExpectLoad("FILES into: cannot be a drive path", Path.Combine(root, "IntoDrive/IntoDrive.queen"), "QN131");
        ExpectLoad("FILES into: cannot be a rooted path", Path.Combine(root, "IntoRoot/IntoRoot.queen"), "QN131");
        var diagnostics = new List<QueenDiagnostic>();
        var project = QueenProject.Load(Path.Combine(root, "IntoOk/IntoOk.queen"), null, diagnostics);
        Check("FILES into: a folder under the build's is fine", project is not null && project.CopiedFiles.Any(f => f.Target == "data/conf/Main.ant"), string.Join("; ", diagnostics));
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
        File.WriteAllText(Path.Combine(root, "Patterns.queen"),
            "queen 1.0;\nchunk PROJ:\n   name: Patterns\nchunk BUILD:\n   stdlib: none\nchunk FILES:\n   copy: [**/*.txt, docs/**/*.txt, docs/*.txt, node_modules/**/*.txt, assets, .hidden/*.txt]\n");
        var diagnostics = new List<QueenDiagnostic>();
        var project = QueenProject.Load(Path.Combine(root, "Patterns.queen"), null, diagnostics);
        var copied = project?.CopiedFiles.Select(f => f.Target).Order(StringComparer.Ordinal).ToList() ?? [];
        string[] expected = [".hidden/d.txt", "a.txt", "assets/j.txt", "assets/sub/k.txt", "docs/b.txt", "docs/deep/c.txt", "node_modules/pkg/g.txt"];
        Check("each pattern's files, once", copied.SequenceEqual(expected), string.Join(", ", copied) + " | " + string.Join("; ", diagnostics));
        // Each pattern on its own (a fresh walk) finds what it found in the shared one.
        Check("a pattern below a walked folder finds the same files on its own",
            QueenProject.Expand(root, "docs/**/*.txt").Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).SequenceEqual(["docs/b.txt", "docs/deep/c.txt"]));
        Check("packages are searched by a pattern that names them",
            QueenProject.Expand(root, "**/node_modules/*.txt").Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).SequenceEqual(["docs/node_modules/h.txt"]));
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
            Write("MathLib/MathLib.queen", "queen 1.0;\nchunk PROJ:\n   name: MathLib,\n   type: library,\n   version: v0.3.1\n");
            Write("MathLib/Geometry/Area.ant", "into namespace MathLib\n{\n    public Square(i32 side) : i32 { return side * side; }\n}\n");
            Write("App/App.queen", """
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
            Write("App/Nested/Nested.queen", "queen 1.0;\nchunk PROJ:\n   name: Nested\n");
            Write("App/Nested/Other.ant", "not part of App");

            Write("Other/Other.queen", "queen 1.0;\nchunk PROJ:\n   name: Other,\n   type: library\n");
            Write("Other/Other.ant", "into namespace Other { public Three() : i32 { return 3; } }\n");
            Write("Many/Many.queen", "queen 1.0;\nchunk PROJ:\n   name: Many\nchunk BUILD:\n   stdlib: none,\n   access.default: public\nchunk DEPS:\n   local: ../MathLib\n   local: ../Other\n   package: Tester/Json@2.1\n");
            Write("Many/Main.ant", "import MathLib;\nimport Other;\nclass Open { i32 x; new() { x = 4; } }\nMain() : i32 { return Square(3) + Three() + new Open().x; }\n");

            Write("Cycle/A/A.queen", "queen 1.0;\nchunk PROJ:\n   name: A,\n   type: library\nchunk DEPS:\n   B: ../B\n");
            Write("Cycle/B/B.queen", "queen 1.0;\nchunk PROJ:\n   name: B,\n   type: library\nchunk DEPS:\n   A: ../A\n");
            Write("Bad/Bad.queen", "queen 1.0;\nchunk PROJ:\n   name: Bad,\n   type: plugin\nchunk BUILD:\n   gc: magic\n");
            Write("NoName/NoName.queen", "queen 1.0;\nchunk BUILD:\n   gc: arena\n");
            Write("ExeDep/ExeDep.queen", "queen 1.0;\nchunk PROJ:\n   name: ExeDep\nchunk DEPS:\n   App: ../App\n");
            foreach (var (name, into) in new[] { ("IntoUp", "../../out"), ("IntoDrive", "C:/Windows/Temp"), ("IntoRoot", "/etc"), ("IntoOk", "data/conf") })
            {
                Write($"{name}/{name}.queen", $"queen 1.0;\nchunk PROJ:\n   name: {name}\nchunk BUILD:\n   stdlib: none\nchunk FILES:\n   copy: Main.ant,\n   into: \"{into}\"\n");
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
