namespace Larva.Cli.Tests;

/// <summary><c>larva run</c>: when the project is built again.</summary>
public sealed class RunCommandTests
{
    [Fact]
    public void Run_builds_again_when_the_effective_settings_change()
    {
        var world = ProjectWorld.Shared;
        // No BUILD chunk: every setting comes from `larva config`.
        string queen = world.Project("runner", "queen 1.0;\n\nchunk PROJ:\n   name: runner,\n   type: exe\n", ("Program.ant", ProjectWorld.Program()));
        var larva = world.In(Path.GetDirectoryName(queen)!);
        try
        {
            string first = larva.Ok("run");
            Assert.Contains("O3", first);
            Assert.Contains("hello", first);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(queen)!, "bin", "runner" + (OperatingSystem.IsWindows() ? ".exe" : "") + ".build")), "the build's stamp is next to the output");

            // Nothing changed: not built again.
            string again = larva.Ok("run");
            Assert.DoesNotContain("back end", again);
            Assert.Contains("hello", again);

            // A machine default the project does not set changed: built again, with it.
            larva.Ok("config", "optimization", "0");
            string changed = larva.Ok("run");
            Assert.Contains("O0", changed);
            Assert.Contains("hello", changed);

            // Back to the built-in value: built again too; then stable.
            larva.Ok("config", "--unset", "optimization");
            Assert.Contains("O3", larva.Ok("run"));
            Assert.DoesNotContain("back end", larva.Ok("run"));
        }
        finally
        {
            larva.Run("config", "--unset", "optimization");
        }
    }
}
