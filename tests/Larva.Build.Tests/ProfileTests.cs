using Larva.Build.Profiles;

namespace Larva.Build.Tests;

/// <summary>
/// Queen profiles: a <c>template.tqueen</c> read into a profile (types, modifiers, conditions, rules), a <c>.queen</c>
/// document checked against it, and the profiles larva has built in.
/// </summary>
public sealed class ProfileTests : CheckedTest
{
    private const string Shop = """
        tqueen 1.0;
        profile shop: "A shop's settings."

        /// The shop.
        chunk SHOP required:
           /// Its name.
           name: TEXT = $folder
           kind: [store, kiosk] = store
           size: INT(1..10)
           code: REGEX("^[A-Z]{3}$")
           owner: NAME  required when kind == kiosk
           till: BOOL  only when kind == store
           old: TEXT  deprecated "use `name`"
           tags: LIST(IDENTIFIER)  many
           home: RPATH | URL

        chunk EXTRA open INT:
           base: INT

        rules:
           error QN150 when SHOP.kind == kiosk and SHOP.size == 10: "a kiosk is smaller than 10"
        """;

    private static QueenProfile Profile(string text, string name, List<QueenDiagnostic> problems, Func<string, QueenProfile?>? resolve = null) =>
        ProfileParser.Parse(text, $"{name}/template.tqueen", name, problems, resolve)!;

    private static List<QueenDiagnostic> Check(QueenProfile profile, string queen, string file = "Corner/shop.queen")
    {
        var diagnostics = new List<QueenDiagnostic>();
        var document = QueenParser.Parse(queen, file, diagnostics);
        if (document is not null)
            QueenValidator.Validate(document, profile, diagnostics);
        return diagnostics;
    }

    private static string Codes(List<QueenDiagnostic> diagnostics) => string.Join(" ", diagnostics.Select(d => d.Code + (d.IsError ? "" : "?")));

    [Fact]
    public void A_template_is_read_into_chunks_keys_types_and_rules()
    {
        var problems = new List<QueenDiagnostic>();
        var profile = Profile(Shop, "shop", problems);
        Check("it reads without problems", problems.Count == 0, string.Join("; ", problems));
        var shop = profile.Chunk("SHOP");
        Check("the chunk, required, with its doc", shop is { Required: true, Doc: "The shop." });
        Check("keys with their types", shop?.Key("kind")?.Type is EnumType { Options: ["store", "kiosk"] } && shop.Key("size")?.Type is IntType { Min: 1, Max: 10 }
            && shop.Key("tags")?.Type is ListType { Item: IdentifierType } && shop.Key("home")?.Type is UnionType { Alternatives: [PathType { Kind: PathKind.Relative }, UrlType] });
        Check("defaults, docs and modifiers", shop?.Key("name") is { Default: "$folder", Doc: "Its name." } && shop.Key("tags") is { Many: true }
            && shop.Key("owner")?.RequiredWhen is CompareCondition { Key.Key: "kind", Value: "kiosk" } && shop.Key("old")?.Deprecated == "use `name`");
        Check("an open chunk with the type of its other keys", profile.Chunk("EXTRA") is { Open: true, OtherKeys: IntType });
        Check("a rule with its code", profile.Rules.Single() is { Code: "QN150", IsError: true, When: AndCondition });
    }

    [Fact]
    public void A_document_is_checked_against_its_profile()
    {
        var profile = Profile(Shop, "shop", []);
        Check("a good file has nothing to say", Check(profile, "queen 1.0;\nchunk SHOP:\n kind: store,\n size: 3,\n code: ABC,\n till: true,\n tags: [a, b],\n tags: c,\n home: web/site\n").Count == 0,
            Codes(Check(profile, "queen 1.0;\nchunk SHOP:\n kind: store,\n size: 3,\n code: ABC,\n till: true,\n tags: [a, b],\n tags: c,\n home: web/site\n")));
        void Expect(string what, string queen, string code)
        {
            var found = Check(profile, queen);
            Check($"{what}: {code}", found.Any(d => d.Code + (d.IsError ? "" : "?") == code), Codes(found) + " — " + string.Join("; ", found.Select(d => d.Message)));
        }
        Expect("a required chunk is missing", "queen 1.0;\nchunk EXTRA:\n base: 1\n", "QN120");
        Expect("a key the chunk does not have (a warning)", "queen 1.0;\nchunk SHOP:\n colour: red\n", "QN121?");
        Expect("a chunk the profile does not have (a warning)", "queen 1.0;\nchunk SHOP:\n kind: store\nchunk OTHER:\n x: 1\n", "QN141?");
        Expect("a value out of its list", "queen 1.0;\nchunk SHOP:\n kind: market\n", "QN124");
        Expect("a number out of its range", "queen 1.0;\nchunk SHOP:\n size: 11\n", "QN124");
        Expect("text that does not match", "queen 1.0;\nchunk SHOP:\n code: abc\n", "QN124");
        Expect("an absolute path where a relative one goes", "queen 1.0;\nchunk SHOP:\n home: C:/web\n", "QN124");
        Expect("a list item of the wrong type", "queen 1.0;\nchunk SHOP:\n tags: [a, \"b c\"]\n", "QN124");
        Expect("a key given twice that is not `many`", "queen 1.0;\nchunk SHOP:\n kind: store,\n kind: kiosk\n", "QN011");
        Expect("a key a condition requires", "queen 1.0;\nchunk SHOP:\n kind: kiosk\n", "QN125");
        Expect("a key whose condition does not hold (a warning)", "queen 1.0;\nchunk SHOP:\n kind: kiosk,\n owner: Ana,\n till: true\n", "QN126?");
        Expect("a deprecated key (a warning)", "queen 1.0;\nchunk SHOP:\n old: x\n", "QN143?");
        Expect("a rule, with its code", "queen 1.0;\nchunk SHOP:\n kind: kiosk,\n owner: Ana,\n size: 10\n", "QN150");
        Expect("an open chunk's other keys take its type", "queen 1.0;\nchunk SHOP:\n kind: store\nchunk EXTRA:\n anything: nope\n", "QN124");
        var conditionByDefault = Check(profile, "queen 1.0;\nchunk SHOP:\n till: true\n");
        Check("a condition reads a key's default when the file does not set it (`kind` is `store`)", conditionByDefault.Count == 0, Codes(conditionByDefault));
        var template = Check(profile, "queen 1.0;\nchunk SHOP:\n size: $[1 + 1]\n");
        Check("a value with $[…] not evaluated yet is not checked", template.Count == 0, Codes(template));
    }

    [Fact]
    public void Template_problems_have_their_codes()
    {
        void Expect(string what, string text, string code)
        {
            var problems = new List<QueenDiagnostic>();
            ProfileParser.Parse(text, "p/template.tqueen", "p", problems);
            Check($"{what}: {code}", problems.Any(d => d.Code == code), string.Join("; ", problems));
        }
        Expect("the header", "profile p: \"x\"", "TQ001");
        Expect("the profile is named after its folder", "tqueen 1.0;\nprofile q: \"x\"", "TQ003");
        Expect("an unknown base", "tqueen 1.0;\nprofile p extends nothing: \"x\"", "TQ004");
        Expect("a chunk's name", "tqueen 1.0;\nprofile p: \"x\"\nchunk lower:", "TQ010");
        Expect("an unknown type", "tqueen 1.0;\nprofile p: \"x\"\nchunk A:\n k: COLOR", "TQ024");
        Expect("a default of another type", "tqueen 1.0;\nprofile p: \"x\"\nchunk A:\n k: INT(0..3) = 7", "TQ016");
        Expect("a condition on a key that does not exist", "tqueen 1.0;\nprofile p: \"x\"\nchunk A:\n k: TEXT  only when nope == 1", "TQ009");
        Expect("a bad pattern", "tqueen 1.0;\nprofile p: \"x\"\nchunk A:\n k: REGEX(\"[\")", "TQ022");
        Expect("a rule without its message", "tqueen 1.0;\nprofile p: \"x\"\nchunk A:\n k: TEXT\nrules:\n error when A.k == 1", "TQ028");
    }

    [Fact]
    public void A_profile_extends_another()
    {
        var shop = Profile(Shop, "shop", []);
        var problems = new List<QueenDiagnostic>();
        var market = Profile("tqueen 1.0;\nprofile market extends shop: \"x\"\nchunk STALLS:\n count: INT\n", "market", problems, n => n == "shop" ? shop : null);
        Check("it reads", problems.Count == 0, string.Join("; ", problems));
        Check("its own chunks and its base's, and what it is", market.Chunk("SHOP") is not null && market.Chunk("STALLS") is not null && market.Is("shop") && market.Rules.Count() == 1);
    }

    /// <summary>
    /// Where a file is decides which profiles it sees: its tools' (given by larva), its repository's `.larva/profiles`
    /// (not above `.git`); larva's own names cannot be replaced by either; a tool's profile does not make projects.
    /// </summary>
    [Fact]
    public void A_file_sees_its_tools_and_its_repositorys_profiles()
    {
        string root = TestEnvironment.TempFolder("profiles-places");
        string repository = Path.Combine(root, "repo"), app = Path.Combine(repository, "app"), store = Path.Combine(root, "tool-profiles");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(app);
        void Template(string folder, string name, string text)
        {
            Directory.CreateDirectory(Path.Combine(folder, name));
            File.WriteAllText(Path.Combine(folder, name, ProfileCatalog.TemplateFile), text);
        }
        Template(Path.Combine(repository, ".larva", "profiles"), "local", "tqueen 1.0;\nprofile local: \"x\"\nchunk A:\n   k: INT\n");
        Template(Path.Combine(root, ".larva", "profiles"), "above", "tqueen 1.0;\nprofile above: \"x\"\nchunk A:\n   k: INT\n");
        Template(store, "toolish", "tqueen 1.0;\nprofile toolish extends project: \"x\"\nchunk EXTRA:\n   k: INT\n");
        Template(store, "project", "tqueen 1.0;\nprofile project: \"not larva's\"\nchunk ONLY:\n   k: INT\n");
        var previous = ProfileCatalog.ToolFolders;
        ProfileCatalog.ToolFolders = near => near.StartsWith(repository, StringComparison.OrdinalIgnoreCase) ? [store] : [];
        try
        {
            Check("a repository's own profile, from a folder inside it", ProfileCatalog.ForFile(Path.Combine(app, "local.queen"))?.Name == "local");
            Check("not one above the repository's top", ProfileCatalog.ForFile(Path.Combine(app, "above.queen")) is null);
            Check("a tool's profile, where its tools are", ProfileCatalog.ForFile(Path.Combine(app, "toolish.queen")) is { } toolish && toolish.Chunk("EXTRA") is not null && toolish.Is("project"));
            Check("not elsewhere", ProfileCatalog.ForFile(Path.Combine(root, "toolish.queen")) is null);
            Check("larva's own `project` is not replaced by a tool's", ProfileCatalog.ForFile(Path.Combine(app, "project.queen")) is { } project && project.Chunk("ONLY") is null && project.Chunk("PROJ") is not null);
            Check("a tool's profile that extends `project` does not make its files projects", !ProfileCatalog.IsProjectFile(Path.Combine(app, "toolish.queen")));
            Check("the names a place sees", ProfileCatalog.Names(app).Contains("local") && ProfileCatalog.Names(app).Contains("toolish") && !ProfileCatalog.Names(root).Contains("toolish"));
        }
        finally
        {
            ProfileCatalog.ToolFolders = previous;
        }
    }

    [Fact]
    public void Larva_profiles_are_built_in()
    {
        Check("project and package are built in", ProfileCatalog.BuiltIn.Contains("project") && ProfileCatalog.BuiltIn.Contains("package"), string.Join(", ", ProfileCatalog.BuiltIn));
        foreach (string name in ProfileCatalog.BuiltIn)
            Check($"`{name}` reads without problems", ProfileCatalog.ProblemsOf(name).Count == 0, string.Join("; ", ProfileCatalog.ProblemsOf(name)));
        Check("package extends project", ProfileCatalog.Get("package") is { } package && package.Is("project") && package.Chunk("BUILD") is not null);
        Check("a file's name chooses its profile", ProfileCatalog.ForFile("x/project.queen")?.Name == "project" && ProfileCatalog.ForFile("x/package.queen")?.Name == "package"
            && ProfileCatalog.ForFile("x/MyApp.queen") is null && ProfileCatalog.ForFile("x/project.txt") is null);
        Check("project files: project.queen, then package.queen", ProfileCatalog.ProjectFileNames.Take(2).SequenceEqual(["project.queen", "package.queen"]), string.Join(", ", ProfileCatalog.ProjectFileNames));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var project = ProfileCatalog.Get("project")!;
        for (int i = 0; i < 1000; i++)
        {
            var document = QueenParser.Parse("queen 1.0;\nchunk PROJ:\n name: App,\n type: exe,\n version: v1.0\nchunk BUILD:\n gc: refcount,\n optimization: 2,\n backend: llvm\nchunk DEPS:\n proj: ../Lib\n", "App/project.queen", [])!;
            QueenValidator.Validate(document, project, []);
        }
        Check("reading and checking a typical project.queen takes well under 1 ms", watch.Elapsed.TotalMilliseconds / 1000 < 1, $"{watch.Elapsed.TotalMilliseconds / 1000:0.000} ms");
    }
}
