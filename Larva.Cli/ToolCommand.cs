using System.Diagnostics;
using Hive;
using Larva.Build;

/// <summary>
/// <c>larva tool</c>: programs installed into larva from Hive. A tool is a project of <c>type: tool</c> published as a
/// package; installing it builds it once for this machine (native code, kept in the tool store), and <c>larva
/// &lt;command&gt;</c> runs it. Tools are global (this user's, <c>-g</c>) or a project's own (its <c>TOOLS</c> chunk,
/// pinned in its hive.lock with its packages): in a project, its tools come first.
/// A tool is told where it runs: <c>LARVA_EXE</c> (the larva that runs it, to build or ask for more) and
/// <c>LARVA_PROJECT</c> (the project's .queen, when there is one).
/// </summary>
internal static class ToolCommand
{
    private const string Usage = """
        usage: larva tool install Author/Name[@version] [-g]   installs a tool (-g: for this user; else for the project you are in)
               larva tool uninstall <command|Author/Name> [-g]  takes a tool out
               larva tool update [<command|Author/Name>] [-g]   the newest version each one's requirement accepts
               larva tool restore                               installs the tools of the project's TOOLS chunk
               larva tool list [-g]                             the tools installed (the project's and this user's)
               larva tool run <command> [arguments...]          runs a tool (also: larva <command> [arguments...])
        """;

    /// <summary>larva's own commands: a tool cannot take one of these names.</summary>
    private static readonly HashSet<string> BuiltIn = new(StringComparer.Ordinal)
    {
        "new", "run", "build", "lib", "config", "hive", "bind", "types", "show", "search", "languages", "lsp", "tool",
        "dis", "link", "native", "version", "help",
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var rest = args.Skip(1).ToList();
        bool global = rest.Remove("-g") | rest.Remove("--global");
        try
        {
            switch (args[0])
            {
                case "install":
                    if (rest.Count != 1 || PackageReference.Parse(rest[0]) is not { } package)
                        throw new HiveException("larva tool install Author/Name[@version] [-g]");
                    return global ? InstallGlobal(package) : InstallLocal(package);
                case "uninstall":
                    if (rest.Count != 1)
                        throw new HiveException("larva tool uninstall <command|Author/Name> [-g]");
                    return Uninstall(rest[0], global);
                case "update":
                    return Update(rest.FirstOrDefault(), global);
                case "restore":
                    return Restore();
                case "list":
                    return List(global);
                case "run":
                    if (rest.Count == 0)
                        throw new HiveException("larva tool run <command> [arguments...]");
                    return RunTool(args[1], args[2..]) ?? NotFound(args[1]);
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (HiveException e)
        {
            Console.Error.WriteLine($"larva tool: {e.Message}");
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"larva tool: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// <c>larva &lt;command&gt; …</c> for a word that is not one of larva's commands: the tool of that command (the
    /// project's, else this user's), run; null when there is none.
    /// </summary>
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0 || BuiltIn.Contains(args[0]) || !QueenProject.IsCommandName(args[0]))
            return null;
        try
        {
            return RunTool(args[0], args[1..]);
        }
        catch (HiveException e)
        {
            Console.Error.WriteLine($"larva {args[0]}: {e.Message}");
            return 1;
        }
    }

    private static int NotFound(string command)
    {
        Console.Error.WriteLine($"larva tool: no tool `{command}` (installed tools: larva tool list; install one with larva tool install Author/Name)");
        return 1;
    }

    // ── The store ──────────────────────────────────────────

    /// <summary>Where tools are built and the user's tools listed: <c>LARVA_TOOLS</c>, else <c>Larva/tools</c> in the local application data.</summary>
    private static string Store => Environment.GetEnvironmentVariable("LARVA_TOOLS") is { Length: > 0 } chosen ? Path.GetFullPath(chosen)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "tools");

    /// <summary>The user's tools: one line each, <c>command Author/Name version requirement</c> (requirement <c>*</c>: any).</summary>
    private static string GlobalList => Path.Combine(Store, "tools.txt");

    private sealed record GlobalTool(string Command, PackageReference Package, string Version);

    private static List<GlobalTool> LoadGlobal()
    {
        var tools = new List<GlobalTool>();
        if (!File.Exists(GlobalList))
            return tools;
        foreach (string line in File.ReadAllLines(GlobalList))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !QueenProject.IsCommandName(parts[0]) || PackageReference.Parse(parts[1]) is not { Version: null } package)
                continue;
            var requirement = parts[3] == "*" ? null : VersionRange.Parse(parts[3]);
            tools.Add(new GlobalTool(parts[0], package with { Version = requirement }, parts[2]));
        }
        return tools;
    }

    private static void SaveGlobal(List<GlobalTool> tools)
    {
        Directory.CreateDirectory(Store);
        File.WriteAllLines(GlobalList, tools.OrderBy(t => t.Command, StringComparer.Ordinal)
            .Select(t => $"{t.Command} {t.Package.Id} {t.Version} {t.Package.Version?.ToString() ?? "*"}"));
    }

    /// <summary>
    /// The installed version's tool, built for this machine when it is not yet (or was built by another larva): its
    /// executable and command.
    /// </summary>
    private static (string Executable, string Command) Built(HiveCache cache, PackageReference package, string version)
    {
        string folder = cache.Folder(package, version);
        if (cache.Project(package, version) is not { } queen)
            throw new HiveException($"{package.Id} {version} is not installed (larva tool restore)");
        var manifest = HiveManifest.Parse(File.ReadAllBytes(Path.Combine(folder, HiveArchive.ManifestEntry)));
        if (manifest.Command is not { } command)
            throw new HiveException($"{package.Id} is a library, not a tool (a tool's project says `type: tool`)");
        string built = Path.Combine(Store, "builds", package.Author.ToLowerInvariant(), package.Name.ToLowerInvariant(), version);
        string executable = Path.Combine(built, command + (OperatingSystem.IsWindows() ? ".exe" : ""));
        // What it was built from and by: another package file or another larva builds it again.
        string stamp = Path.Combine(built, ".built");
        string wanted = $"{cache.Stamp(package, version)} {LarvaVersion.Current}";
        if (File.Exists(executable) && File.Exists(stamp) && File.ReadAllText(stamp).Trim() == wanted)
            return (executable, command);
        Directory.CreateDirectory(built);
        Console.WriteLine($"building {command} ({package.Id} {version})");
        int result;
        // The build's own lines go to the error stream: a tool's output is its own.
        var previous = Console.Out;
        Console.SetOut(Console.Error);
        try
        {
            result = SourceBuild.Build([queen, "-o", executable]);
        }
        finally
        {
            Console.SetOut(previous);
        }
        if (result != 0 || !File.Exists(executable))
            throw new HiveException($"{package.Id} {version} did not build for this machine");
        File.WriteAllText(stamp, wanted);
        return (executable, command);
    }

    // ── Installing ─────────────────────────────────────────

    /// <summary>Settings of a command run outside a project: this machine's registries, the user's cache, signatures required.</summary>
    private static HiveInstaller UserInstaller() =>
        new(HiveCache.Default(), [.. HiveSettings.Defaults().Select(p => HiveProvider.Parse(p, Directory.GetCurrentDirectory()))], requireSignatures: true, Console.WriteLine);

    private static int InstallGlobal(PackageReference package)
    {
        var locked = UserInstaller().Install([package], null);
        var entry = locked.Find(package.Id) ?? throw new HiveException($"{package.Id} was not installed");
        var (_, command) = Built(HiveCache.Default(), package, entry.Version);
        if (BuiltIn.Contains(command))
            throw new HiveException($"{package.Id}'s command `{command}` is one of larva's own: it cannot be installed");
        var tools = LoadGlobal();
        if (tools.FirstOrDefault(t => t.Command == command && t.Package.Id != package.Id) is { } taken)
            throw new HiveException($"`{command}` is already {taken.Package.Id}'s (larva tool uninstall {command} -g first)");
        tools.RemoveAll(t => t.Package.Id == package.Id);
        tools.Add(new GlobalTool(command, package, entry.Version));
        SaveGlobal(tools);
        Console.WriteLine($"installed {package.Id} {entry.Version}: run it with `larva {command}`");
        return 0;
    }

    private static int InstallLocal(PackageReference package)
    {
        string queen = HiveCommand.FindQueen(null);
        var project = HiveCommand.LoadProject(queen, resolvePackages: false);
        var lockFile = HiveLock.Load(project.Folder);
        // Installed and built first, written to the project after: a tool that fails leaves the project as it was.
        var others = project.Packages.Concat(project.Tools).Where(p => p.Id != package.Id).ToList();
        var locked = HiveCommand.Installer(project).Install([.. others, package], lockFile);
        var entry = locked.Find(package.Id) ?? throw new HiveException($"{package.Id} was not installed");
        var cache = HiveCache.For(project.Hive);
        var (_, command) = Built(cache, package, entry.Version);
        if (BuiltIn.Contains(command))
            throw new HiveException($"{package.Id}'s command `{command}` is one of larva's own: it cannot be installed");
        foreach (var other in project.Tools.Where(t => t.Id != package.Id))
        {
            if (locked.Find(other.Id) is { } pinned && CommandOf(cache, other, pinned.Version) == command)
                throw new HiveException($"`{command}` is already {other.Id}'s in this project (larva tool uninstall {command} first)");
        }
        locked.Save(project.Folder);
        HiveCommand.AddEntry(queen, "TOOLS", "tool", package.Version is null ? package with { Version = VersionRange.Parse("^" + entry.Version) } : package);
        Console.WriteLine($"installed {package.Id} {entry.Version} for {project.Name} (TOOLS in {Path.GetFileName(queen)}): run it with `larva {command}`");
        return 0;
    }

    private static string? CommandOf(HiveCache cache, PackageReference package, string version)
    {
        string manifest = Path.Combine(cache.Folder(package, version), HiveArchive.ManifestEntry);
        return File.Exists(manifest) ? HiveManifest.Parse(File.ReadAllBytes(manifest)).Command : null;
    }

    private static int Restore()
    {
        string queen = HiveCommand.FindQueen(null);
        var project = HiveCommand.LoadProject(queen, resolvePackages: false);
        if (project.Tools.Count == 0)
        {
            Console.WriteLine($"{project.Name} has no tools (TOOLS)");
            return 0;
        }
        var locked = HiveCommand.Installer(project).Install([.. project.Packages, .. project.Tools.Where(t => !project.Packages.Any(p => p.Id == t.Id))], HiveLock.Load(project.Folder));
        locked.Save(project.Folder);
        var cache = HiveCache.For(project.Hive);
        foreach (var tool in project.Tools)
        {
            var entry = locked.Find(tool.Id)!;
            var (_, command) = Built(cache, tool, entry.Version);
            Console.WriteLine($"  larva {command}: {tool.Id} {entry.Version}");
        }
        return 0;
    }

    private static int Update(string? which, bool global)
    {
        if (global)
        {
            var tools = LoadGlobal().Where(t => which is null || t.Command == which || t.Package.Id == which).ToList();
            if (tools.Count == 0)
                throw new HiveException(which is null ? "no tools installed for this user" : $"no tool `{which}` installed for this user");
            foreach (var tool in tools)
                InstallGlobal(tool.Package);
            return 0;
        }
        string queen = HiveCommand.FindQueen(null);
        var project = HiveCommand.LoadProject(queen, resolvePackages: false);
        var lockFile = HiveLock.Load(project.Folder);
        var cache = HiveCache.For(project.Hive);
        var chosen = project.Tools.Where(t => which is null || t.Id == which
            || lockFile?.Find(t.Id) is { } pin && CommandOf(cache, t, pin.Version) == which).ToList();
        if (chosen.Count == 0)
            throw new HiveException(which is null ? $"{project.Name} has no tools (TOOLS)" : $"{project.Name} has no tool `{which}`");
        // The chosen tools lose their pins: the installer takes the newest version their requirement accepts.
        var kept = lockFile is null ? null : new HiveLock { Packages = [.. lockFile.Packages.Where(p => !chosen.Any(c => c.Id == p.Package))] };
        var locked = HiveCommand.Installer(project).Install([.. project.Packages, .. project.Tools.Where(t => !project.Packages.Any(p => p.Id == t.Id))], kept);
        locked.Save(project.Folder);
        foreach (var tool in chosen)
        {
            var entry = locked.Find(tool.Id)!;
            var (_, command) = Built(cache, tool, entry.Version);
            Console.WriteLine($"  larva {command}: {tool.Id} {entry.Version}");
        }
        return 0;
    }

    private static int Uninstall(string which, bool global)
    {
        if (global)
        {
            var tools = LoadGlobal();
            var gone = tools.Where(t => t.Command == which || t.Package.Id == which).ToList();
            if (gone.Count == 0)
                throw new HiveException($"no tool `{which}` installed for this user (larva tool list -g)");
            tools.RemoveAll(gone.Contains);
            SaveGlobal(tools);
            foreach (var removed in gone)
                Console.WriteLine($"uninstalled {removed.Package.Id} (`larva {removed.Command}`)");
            return 0;
        }
        string queen = HiveCommand.FindQueen(null);
        var project = HiveCommand.LoadProject(queen, resolvePackages: false);
        var lockFile = HiveLock.Load(project.Folder);
        var cache = HiveCache.For(project.Hive);
        var tool = project.Tools.FirstOrDefault(t => t.Id == which || lockFile?.Find(t.Id) is { } pin && CommandOf(cache, t, pin.Version) == which)
            ?? throw new HiveException($"{project.Name} has no tool `{which}` (larva tool list)");
        HiveCommand.RemoveEntry(queen, "TOOLS", "tool", tool);
        // The pin goes too, unless the project also uses the package as a library.
        if (lockFile is not null && !project.Packages.Any(p => p.Id == tool.Id))
            new HiveLock { Packages = [.. lockFile.Packages.Where(p => p.Package != tool.Id)] }.Save(project.Folder);
        Console.WriteLine($"uninstalled {tool.Id} from {project.Name}");
        return 0;
    }

    // ── Listing and running ────────────────────────────────

    private static int List(bool globalOnly)
    {
        int count = 0;
        if (!globalOnly && QueenProject.Find(Directory.GetCurrentDirectory()) is { } queen)
        {
            var project = HiveCommand.LoadProject(queen, resolvePackages: false);
            var lockFile = HiveLock.Load(project.Folder);
            var cache = HiveCache.For(project.Hive);
            foreach (var tool in project.Tools)
            {
                string? version = lockFile?.Find(tool.Id)?.Version;
                string command = version is not null ? CommandOf(cache, tool, version) ?? "?" : "?";
                Console.WriteLine($"larva {command,-16} {tool.Id} {version ?? "(not installed: larva tool restore)"}  [{project.Name}]");
                count++;
            }
        }
        foreach (var tool in LoadGlobal())
        {
            Console.WriteLine($"larva {tool.Command,-16} {tool.Package.Id} {tool.Version}  [user]");
            count++;
        }
        if (count == 0)
            Console.WriteLine("no tools installed (larva tool install Author/Name [-g])");
        return 0;
    }

    /// <summary>Runs the tool of <paramref name="command"/> (the project's, else the user's): its exit code, or null when there is none.</summary>
    private static int? RunTool(string command, string[] arguments)
    {
        string? queen = QueenProject.Find(Directory.GetCurrentDirectory());
        string? executable = null;
        if (queen is not null)
        {
            var diagnostics = new List<QueenDiagnostic>();
            if (QueenProject.Load(queen, null, diagnostics, null) is { Tools.Count: > 0 } project)
            {
                var lockFile = HiveLock.Load(project.Folder);
                var cache = HiveCache.For(project.Hive);
                foreach (var tool in project.Tools)
                {
                    // The version the lock pins; else the newest installed that the requirement accepts.
                    string? version = lockFile?.Find(tool.Id)?.Version
                        ?? cache.Versions(tool).FirstOrDefault(v => tool.Version is null || tool.Version.Accepts(VersionRange.Parse(v)!.Version));
                    if (version is null || CommandOf(cache, tool, version) != command)
                        continue;
                    executable = Built(cache, tool, version).Executable;
                    break;
                }
            }
        }
        if (executable is null && LoadGlobal().FirstOrDefault(t => t.Command == command) is { } global)
            executable = Built(HiveCache.Default(), global.Package, global.Version).Executable;
        if (executable is null)
            return null;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        if (Environment.ProcessPath is { } larva)
            start.Environment["LARVA_EXE"] = larva;
        if (queen is not null)
            start.Environment["LARVA_PROJECT"] = queen;
        using var process = Process.Start(start) ?? throw new HiveException($"{executable} did not start");
        process.WaitForExit();
        return process.ExitCode;
    }
}
