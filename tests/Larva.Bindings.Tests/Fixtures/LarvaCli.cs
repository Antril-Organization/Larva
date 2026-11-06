using System.Diagnostics;

namespace Larva.Bindings.Tests;

/// <summary>
/// The larva command line of this working copy: <c>Larva.Cli/bin/&lt;configuration&gt;/net10.0/larva.dll</c> (built with
/// the rest of the repository, in the test's own configuration), run with dotnet.
/// </summary>
internal static class LarvaCli
{
    /// <summary>The repository's root: the first folder above the test that holds Larva.Cli.</summary>
    public static string Repository { get; } = FindRepository();

    /// <summary>The configuration the test was built in (Debug, Release): the folder above net10.0.</summary>
    public static string Configuration { get; } = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!)!;

    public static string Dll => Path.Combine(Repository, "Larva.Cli", "bin", Configuration, "net10.0", "larva.dll");

    private static string FindRepository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, "Larva.Cli")))
                return folder.FullName;
        }
        throw new InvalidOperationException($"no Larva.Cli above {AppContext.BaseDirectory}");
    }

    /// <summary>Runs <c>larva arguments…</c>; its exit code, and what it wrote (standard output, then standard error).</summary>
    public static (int Code, string Output) Run(params string[] arguments)
    {
        Assert.True(File.Exists(Dll), $"the larva command line is built: {Dll}");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Dll);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); // both pipes at once: a full one would stop the child
        string output = process.StandardOutput.ReadToEnd() + stderr.Result;
        process.WaitForExit(120_000);
        return (process.ExitCode, output);
    }
}
