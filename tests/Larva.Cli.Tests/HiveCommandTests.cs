namespace Larva.Cli.Tests;

/// <summary><c>larva hive</c> over projects: packing with the project's settings, adding and installing packages.</summary>
public sealed class HiveCommandTests
{
    private const string UnsafeLibrary = "into namespace Raw { public unsafe Peek(i32 v) : i32 { i32* p = &v; return *p; } }\n";

    [Fact]
    public void Pack_compiles_with_the_project_s_BUILD_settings()
    {
        var world = ProjectWorld.Shared;
        // The machine denies unsafe code; the project allows it (its BUILD chunk wins, as in `larva build`).
        string queen = world.Project("packed", "queen 1.0;\n\nchunk PROJ:\n   name: Raw,\n   type: library,\n   version: v1.0,\n   author: Me\n\nchunk BUILD:\n   unsafe: allow\n", ("raw.ant", UnsafeLibrary));
        var larva = world.In(Path.GetDirectoryName(queen)!);
        try
        {
            larva.Ok("config", "unsafe", "deny");
            string output = larva.Ok("hive", "pack");
            Assert.Contains("packed", output);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(queen)!, "bin", "Raw-1.0.hive")));
        }
        finally
        {
            larva.Run("config", "--unset", "unsafe");
        }
    }

    /// <summary>A folder registry of the world with `Me/Raw 1.0` (a source package) published into it; the registry's folder.</summary>
    private static string Registry(ProjectWorld world)
    {
        string registry = Path.Combine(world.World.Root, "registry-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(registry);
        string library = world.Project("raw", "queen 1.0;\n\nchunk PROJ:\n   name: Raw,\n   type: library,\n   version: v1.0,\n   author: Me\n\nchunk HIVE:\n   publish: source\n",
            ("raw.ant", "into namespace Raw { public Peek(i32 v) : i32 { return v; } }\n"));
        Assert.Contains("published Me/Raw 1.0", world.In(Path.GetDirectoryName(library)!).Ok("hive", "publish", "--provider", "file:" + registry));
        return registry;
    }

    /// <summary>A project's HIVE chunk: the registry given, hashes only (nothing is signed); the cache is the world's (HIVE_CACHE).</summary>
    private static string Hive(string registry) =>
        $"chunk HIVE:\n   provider: file:{registry.Replace('\\', '/')},\n   verify: hashes\n";

    [Fact]
    public void Add_installs_first_and_keeps_the_file_s_line_endings()
    {
        var world = ProjectWorld.Shared;
        string registry = Registry(world);
        // LF line endings, a final newline.
        string text = "queen 1.0;\n\nchunk PROJ:\n   name: adder,\n   type: exe\n\n" + Hive(registry);
        string queen = world.Project("adder", text, ("Program.ant", ProjectWorld.Program()));
        var larva = world.In(Path.GetDirectoryName(queen)!);

        // A package the registry does not have: nothing is installed and the project is left as it was.
        Assert.Contains("Me/Nope", larva.Fails("hive", "add", "Me/Nope"));
        Assert.Equal(text, File.ReadAllText(queen));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(queen)!, "hive.lock")));

        string output = larva.Ok("hive", "add", "Me/Raw");
        Assert.Contains("installed 1 package", output);
        Assert.Contains("added `package: Me/Raw`", output);
        string written = File.ReadAllText(queen);
        Assert.DoesNotContain("\r", written);
        Assert.EndsWith("chunk DEPS:\n   package: Me/Raw\n", written);
        Assert.StartsWith(text, written);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(queen)!, "hive.lock")));
        // The project builds with it.
        Assert.Contains("no errors", larva.Ok("build", "--check"));

        // CRLF line endings and no final newline are kept too.
        string crlf = ("queen 1.0;\n\nchunk PROJ:\n   name: crlf,\n   type: exe\n\n" + Hive(registry) + "\nchunk DEPS:\n   package: Me/Raw").Replace("\n", "\r\n");
        string second = world.Project("crlf", crlf, ("Program.ant", ProjectWorld.Program()));
        world.In(Path.GetDirectoryName(second)!).Ok("hive", "add", "Me/Raw@1.0");
        string rewritten = File.ReadAllText(second);
        Assert.DoesNotContain("\r\n\r\n\r\n", rewritten);
        Assert.EndsWith("chunk DEPS:\r\n   package: Me/Raw@1.0", rewritten);
        Assert.DoesNotContain("\n", rewritten.Replace("\r\n", ""));
    }
}
