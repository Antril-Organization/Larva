namespace Larva.Cli.Tests;

/// <summary><c>larva build</c> of sources without a project: every `larva config` BUILD key applies, as a project's would.</summary>
public sealed class BuildCommandTests
{
    [Fact]
    public void Build_without_a_project_applies_every_configured_BUILD_key()
    {
        var world = ProjectWorld.Shared;
        string folder = Path.Combine(world.World.Root, "loose-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Program.ant"), ProjectWorld.Program());
        File.WriteAllText(Path.Combine(folder, "Raw.ant"), "into namespace Raw { public unsafe Peek(i32 v) : i32 { i32* p = &v; return *p; } }\n");
        var larva = world.In(folder);
        try
        {
            // Back end and optimization level come from the defaults (the command line still wins).
            larva.Ok("config", "backend", "x64");
            larva.Ok("config", "optimization", "1");
            // (The x64 back end makes Windows code: elsewhere the configured back end is refused, saying so.)
            if (OperatingSystem.IsWindows())
                Assert.Contains("x64 back end", larva.Ok("build", "-o", "p.exe", "Program.ant"));
            else
                Assert.Contains("built with LLVM", larva.Fails("build", "-o", "p.exe", "Program.ant"));
            Assert.Contains("O1", larva.Ok("build", "--backend", "llvm", "-o", "p.exe", "Program.ant"));
            larva.Ok("config", "--unset", "backend");
            larva.Ok("config", "--unset", "optimization");
            // `unsafe: deny` from the defaults applies to the loose sources too.
            larva.Ok("config", "unsafe", "deny");
            Assert.Contains("AN1104", larva.Fails("build", "--check", "Raw.ant"));
            larva.Ok("config", "--unset", "unsafe");
            Assert.Contains("no errors", larva.Ok("build", "--check", "Raw.ant"));
        }
        finally
        {
            foreach (string key in new[] { "backend", "optimization", "unsafe" })
                larva.Run("config", "--unset", key);
        }
    }
}
