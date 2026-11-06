using System.Diagnostics;

namespace Larva.Cli.Tests;

/// <summary>
/// The larva command line of this working copy (<c>Larva.Cli/bin/&lt;configuration&gt;/net10.0/larva.dll</c>, run with
/// dotnet) in an isolated world: its own settings file (LARVA_CONFIG), its own user language folder (LARVA_USER_LANGUAGES)
/// and no LARVA_LANGUAGES, so nothing of this machine's is read or changed.
/// </summary>
internal sealed class LarvaProcess
{
    public static string Repository { get; } = FindRepository();

    public static string Configuration { get; } = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!)!;

    public static string Dll => Path.Combine(Repository, "Larva.Cli", "bin", Configuration, "net10.0", "larva.dll");

    public required string WorkingDirectory { get; init; }
    public required string Config { get; init; }
    public required string UserLanguages { get; init; }
    public string? Token { get; set; }

    private static string FindRepository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, "Larva.Cli")))
                return folder.FullName;
        }
        throw new InvalidOperationException($"no Larva.Cli above {AppContext.BaseDirectory}");
    }

    /// <summary>Runs <c>larva arguments…</c>: its exit code and what it wrote (standard output, then standard error).</summary>
    public (int Code, string Output) Run(params string[] arguments)
    {
        Assert.True(File.Exists(Dll), $"the larva command line is built: {Dll}");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = WorkingDirectory,
        };
        start.ArgumentList.Add(Dll);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["LARVA_CONFIG"] = Config;
        start.Environment["LARVA_USER_LANGUAGES"] = UserLanguages;
        start.Environment.Remove("LARVA_LANGUAGES");
        start.Environment.Remove("HIVE_TOKEN");
        if (Token is not null)
            start.Environment["HIVE_TOKEN"] = Token;
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); // both pipes at once: a full one would stop the child
        string output = process.StandardOutput.ReadToEnd() + stderr.Result;
        process.WaitForExit(120_000);
        return (process.ExitCode, output);
    }

    /// <summary>Runs larva and expects it to succeed; what it wrote.</summary>
    public string Ok(params string[] arguments)
    {
        var (code, output) = Run(arguments);
        Assert.True(code == 0, $"larva {string.Join(" ", arguments)} exited {code}:\n{output}");
        return output;
    }

    /// <summary>Runs larva and expects it to fail; what it wrote.</summary>
    public string Fails(params string[] arguments)
    {
        var (code, output) = Run(arguments);
        Assert.True(code != 0, $"larva {string.Join(" ", arguments)} succeeded:\n{output}");
        return output;
    }
}
