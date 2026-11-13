using System.Diagnostics;

/// <summary>
/// <c>larva run</c>: the project you are in, built when something changed and run. The project is the nearest
/// <c>.queen</c> from the current folder up (or the <c>.queen</c> / folder given); the program gets the arguments after
/// <c>--</c>, runs in the current folder, and its exit code is larva's. Build options (<c>--backend x64</c>, <c>-O0</c>…)
/// go before <c>--</c>. (<c>larva run module.lvm</c> still runs a Larva module on the VM.)
/// </summary>
internal static class RunCommand
{
    public const string Usage = "usage: larva run [<project.queen|folder>] [--rebuild] [build options] [-- <program arguments>...]";

    /// <summary>Whether the arguments are this command's (not a module for the VM).</summary>
    public static bool Handles(string[] args) =>
        !args.Skip(1).TakeWhile(a => a != "--").Any(a => a.EndsWith(".lvm", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        string? where = null;
        bool rebuild = false;
        var options = new List<string>();
        var programArguments = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                programArguments.AddRange(args[(i + 1)..]);
                break;
            }
            if (args[i] == "--rebuild")
                rebuild = true;
            else if (args[i].StartsWith('-'))
            {
                options.Add(args[i]);
                // Options that take a value.
                if (args[i] is "--backend" or "--core" or "--target" or "--cpu" or "--borrowcheck" && i + 1 < args.Length)
                    options.Add(args[++i]);
            }
            else if (where is null)
                where = args[i];
            else
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }
        string? queen = Find(where);
        if (queen is null)
        {
            Console.Error.WriteLine(where is null
                ? $"larva run: no project here ({Directory.GetCurrentDirectory()} and the folders above it have no .queen); make one with `larva new`"
                : $"larva run: {where} is not a project (a .queen file, or a folder with one)");
            return 1;
        }
        var diagnostics = new List<Larva.Build.QueenDiagnostic>();
        var project = Larva.Build.QueenProject.Load(queen, null, diagnostics, Hive.HiveResolver.Create());
        foreach (var diagnostic in diagnostics)
            Console.Error.WriteLine(diagnostic);
        if (project is null)
            return 1;
        if (project.Kind == Larva.Build.ProjectKind.Library)
        {
            Console.Error.WriteLine($"larva run: {project.Name} is a library (type: library): nothing to run");
            return 1;
        }
        // Where `larva build` puts it (BUILD `output`, else bin/NAME.exe; NAME.js for WebAssembly).
        bool wasm = options.Contains("--target") ? options[options.IndexOf("--target") + 1] != "native"
            : project.Sets("target") && project.Target == Larva.Build.ProjectTarget.Wasm;
        string output = project.Output is { } written ? Path.GetFullPath(Path.Combine(project.Folder, written)) : project.DefaultOutput(native: true);
        if (wasm)
            output = Path.ChangeExtension(output, ".js");

        // What the program was built with, besides its files: the effective BUILD settings (the .queen's merged with
        // `larva config`), the options given here, and the larva that built it. Kept next to the output: any change rebuilds.
        string fingerprint = Fingerprint(project, options);
        string stamp = output + StampExtension;
        if (rebuild || options.Count > 0 || !UpToDate(project, queen, output) || ReadStamp(stamp) != fingerprint)
        {
            // The build's report goes to stderr: stdout is the program's.
            var stdout = Console.Out;
            Console.SetOut(Console.Error);
            int built;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                if (File.Exists(stamp))
                    File.Delete(stamp);
                built = SourceBuild.Build([queen, "-o", output, .. options]);
            }
            finally
            {
                Console.SetOut(stdout);
            }
            if (built != 0)
                return built;
            try
            {
                File.WriteAllText(stamp, fingerprint);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Without the stamp the next run builds again: slower, never wrong.
            }
        }
        var start = wasm ? new ProcessStartInfo("node") { ArgumentList = { output } } : new ProcessStartInfo(output);
        foreach (string argument in programArguments)
            start.ArgumentList.Add(argument);
        start.UseShellExecute = false;
        start.WorkingDirectory = Directory.GetCurrentDirectory();
        try
        {
            using var process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Console.Error.WriteLine($"larva run: cannot start {start.FileName}: {e.Message}");
            return 1;
        }
    }

    /// <summary>The project's <c>.queen</c>: the one given (or in the folder given), else the nearest from here up.</summary>
    internal static string? Find(string? where)
    {
        if (where is not null)
        {
            if (File.Exists(where) && where.EndsWith(".queen", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(where);
            return Directory.Exists(where) ? Directory.EnumerateFiles(where, "*.queen").Order(StringComparer.Ordinal).Select(Path.GetFullPath).FirstOrDefault() : null;
        }
        for (string? folder = Directory.GetCurrentDirectory(); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (Directory.EnumerateFiles(folder, "*.queen").Order(StringComparer.Ordinal).FirstOrDefault() is { } found)
                return Path.GetFullPath(found);
        }
        return null;
    }

    /// <summary>The file next to the output that records what it was built with (<see cref="Fingerprint"/>).</summary>
    internal const string StampExtension = ".build";

    /// <summary>
    /// What a build depends on besides its files: every effective BUILD setting (the project's, with `larva config` filling
    /// in what it does not set), the build options given on the command line, and the larva that builds. A line each, so a
    /// stamp is readable.
    /// </summary>
    internal static string Fingerprint(Larva.Build.QueenProject project, IReadOnlyList<string> options)
    {
        var version = typeof(RunCommand).Assembly.GetName().Version?.ToString() ?? "0";
        var lines = new List<string> { $"larva: {version}", $"format: {Larva.Hir.Encoding.LvaFormat.Version}" };
        lines.AddRange(project.EffectiveBuild().Select(s => $"{s.Key}: {s.Value}"));
        if (options.Count > 0)
            lines.Add("options: " + string.Join(" ", options));
        return string.Join("\n", lines) + "\n";
    }

    private static string? ReadStamp(string stamp)
    {
        try
        {
            return File.Exists(stamp) ? File.ReadAllText(stamp) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the program is newer than everything it is built from: the project file, its sources (its dependencies'
    /// too), resources and libraries, and larva itself with its language packs and System.Core.
    /// </summary>
    private static bool UpToDate(Larva.Build.QueenProject project, string queen, string output)
    {
        if (!File.Exists(output))
            return false;
        var built = File.GetLastWriteTimeUtc(output);
        var inputs = new List<string> { queen, Environment.ProcessPath ?? "" };
        inputs.AddRange(project.AllSources());
        inputs.AddRange(project.AllResources().Select(r => r.File));
        inputs.AddRange(project.AllLibraries());
        inputs.AddRange(project.Dependencies.Select(d => d.File));
        inputs.Add(Path.Combine(AppContext.BaseDirectory, "System.Core.dll"));
        foreach (var pack in LanguageRegistry.All)
            inputs.Add(Path.Combine(pack.Folder, pack.Entry ?? LanguagePack.ManifestName));
        // A source added to (or removed from) the project's folders changes a folder's write time.
        inputs.AddRange(project.AllSources().Select(Path.GetDirectoryName).Distinct()!);
        return inputs.Where(i => i.Length > 0 && (File.Exists(i) || Directory.Exists(i))).All(i => File.GetLastWriteTimeUtc(i) <= built);
    }
}
