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
