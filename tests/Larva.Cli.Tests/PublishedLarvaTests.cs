using System.Diagnostics;

namespace Larva.Cli.Tests;

/// <summary>
/// The published larva (natively compiled and trimmed: <c>artifacts/larva/larva.exe</c> from <c>tools/publish-larva.ps1</c>,
/// or the one <c>LARVA_PUBLISHED</c> names) compiles like the one the other tests run with dotnet. Trimming once made
/// the coroutine pass rewrite nothing (it walked the IR by reflection), so iterators and async functions ran wrong only
/// there. Skipped when there is no published larva with its language packs.
/// </summary>
public sealed class PublishedLarvaTests
{
    private const string Program = """
        import System;
        import System.Threading.Tasks;

        into namespace App
        {
        	Numbers(i32 to) : Iterator<i32>
        	{
        		loop (i32 i = 1..to)
        			yield i;
        	}

        	async Twice(i32 n) : Task<i32>
        	{
        		await Task.Yield();
        		return n * 2;
        	}

        	async Sum() : Task<i32>
        	{
        		i32 a = await Twice(1);
        		await Task.Delay(1);
        		i32 b = await Twice(20);
        		return a + b;
        	}

        	Main()
        	{
        		i32 total = 0;
        		loop (i32 n in Numbers(5))
        			total += n;
        		Console.WriteLine(total);
        		Console.WriteLine(Sum().Result);
        	}
        }

        """;

    [Fact]
    public void Iterators_and_async_functions_run_right_when_built_by_the_published_larva()
    {
        string exe = Environment.GetEnvironmentVariable("LARVA_PUBLISHED") is { Length: > 0 } named
            ? named
            : Path.Combine(LarvaProcess.Repository, "artifacts", "larva", OperatingSystem.IsWindows() ? "larva.exe" : "larva");
        string home = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        Assert.SkipUnless(File.Exists(exe) && Directory.Exists(Path.Combine(home, "Languages")),
            $"no published larva with its language packs at {exe} (tools/publish-larva.ps1)");

        string folder = TestEnvironment.TempFolder("published");
        File.WriteAllText(Path.Combine(folder, "Program.ant"), Program);
        string output = OperatingSystem.IsWindows() ? "app.exe" : "app";
        var (code, built) = Run(exe, folder, home, "build", "--no-cache", "-o", output, "Program.ant");
        Assert.True(code == 0, $"the published larva builds the program:\n{built}");
        var (exit, printed) = Run(Path.Combine(folder, output), folder, null);
        Assert.True(exit == 0, $"the program exits 0:\n{printed}");
        Assert.Equal("10\n42\n", printed.ReplaceLineEndings("\n"));
    }

    /// <summary>Runs a program in <paramref name="folder"/>, with larva's settings the tests' own and no LARVA_LANGUAGES.</summary>
    private static (int Code, string Output) Run(string file, string folder, string? home, params string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment.Remove("LARVA_LANGUAGES");
        start.Environment["LARVA_USER_LANGUAGES"] = Path.Combine(folder, "user-languages");
        // An installed larva has System.Core.dll beside it; a published one in artifacts compiles it from the sources.
        if (home is not null && !File.Exists(Path.Combine(home, "System.Core.dll")))
            start.Environment["LARVA_CORE"] = Path.Combine(LarvaProcess.Repository, "Library", "System.Core");
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd() + stderr.Result;
        process.WaitForExit(300_000);
        return (process.ExitCode, output);
    }
}
