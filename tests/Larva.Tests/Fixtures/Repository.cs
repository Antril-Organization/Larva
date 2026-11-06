using System.Diagnostics;

namespace Larva.Tests;

/// <summary>Where Larva's working copy and its built tools are, and how to run them.</summary>
internal static class Repository
{
    /// <summary>The working copy's root (the folder with Larva.slnx).</summary>
    public static readonly string Root = FindRoot();

    /// <summary>The CLI as built by Debug (larva.dll, run with dotnet).</summary>
    public static string LarvaCli => Path.Combine(Root, "Larva.Cli", "bin", "Debug", "net10.0", "larva.dll");

    /// <summary>The stand-in for LLVM's <c>opt</c> (tests/FakeOpt, built like this project), or null when it is not built.</summary>
    public static string? FakeOpt
    {
        get
        {
            // This assembly is in tests/Larva.Tests/bin/<Configuration>/<framework>/: FakeOpt builds to the same place in its project.
            var framework = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            string configuration = framework.Parent!.Name;
            return FirstExisting(
                Path.Combine(Root, "tests", "FakeOpt", "bin", configuration, framework.Name, "FakeOpt.exe"),
                Path.Combine(Root, "tests", "FakeOpt", "bin", "Debug", framework.Name, "FakeOpt.exe"));
        }
    }

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Larva.slnx")))
                return folder.FullName;
        }
        throw new InvalidOperationException("Larva.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Runs the Debug CLI with <paramref name="arguments"/>: its exit code and its output (stdout then stderr).</summary>
    public static (int Code, string Output) Cli(params string[] arguments) => Cli(null, 60_000, arguments);

    /// <summary>Runs the Debug CLI with extra environment variables, waiting up to <paramref name="timeoutMs"/>.</summary>
    public static (int Code, string Output) Cli(IReadOnlyDictionary<string, string>? environment, int timeoutMs, params string[] arguments)
    {
        Assert.SkipUnless(File.Exists(LarvaCli), $"the CLI is not built ({LarvaCli})");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        start.ArgumentList.Add(LarvaCli);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); // both pipes at once: a full one would stop the child
        string output = process.StandardOutput.ReadToEnd() + stderr.Result;
        process.WaitForExit(timeoutMs);
        return (process.ExitCode, output);
    }

    /// <summary>The first of <paramref name="paths"/> that exists, or null.</summary>
    public static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
}
