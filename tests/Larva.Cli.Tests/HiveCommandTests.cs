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
}
