using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace Larva.Setup;

/// <summary>
/// What installing and removing Larva does, for the wizard and the command line alike: the files out of the embedded
/// payload, the user's PATH, the entry in Settings > Apps, and the uninstaller. Per user: no administrator rights.
/// </summary>
internal static class Installer
{
    public const string Product = "Larva";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Larva";

    public static string Version =>
        typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product);

    public static Stream? Payload() => typeof(Installer).Assembly.GetManifestResourceStream("payload.zip");

    /// <summary>The installed size, in bytes (what the payload unpacks to).</summary>
    public static long PayloadSize()
    {
        using var payload = Payload();
        if (payload is null)
            return 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        return archive.Entries.Sum(e => e.Length);
    }

    /// <summary>Where larva is installed now (Settings > Apps knows), or null.</summary>
    public static string? InstalledFolder()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>The version installed now (Settings > Apps knows), or null.</summary>
    public static string? InstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    /// <summary>How many programs run from <paramref name="folder"/> (larva itself, or anything else there).</summary>
    public static int RunningFrom(string folder)
    {
        string inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        int count = 0;
        foreach (var process in Process.GetProcessesByName("larva"))
        {
            try
            {
                if (process.MainModule?.FileName is { } file && file.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Another user's process, or one that just ended: not ours.
            }
            finally
            {
                process.Dispose();
            }
        }
        return count;
    }

    /// <summary>The space free on the drive of <paramref name="folder"/>, in bytes, or null.</summary>
    public static long? FreeSpace(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A size as people read it: 90.4 MB, or 90,4 MB with a decimal comma.</summary>
    public static string Size(long bytes, char decimalSeparator) => Size(bytes).Replace('.', decimalSeparator);

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    /// <summary>The steps <see cref="Install"/> reports that are not a file being copied.</summary>
    public const string StepPath = "<path>", StepApps = "<apps>", StepHive = "<hive>", StepTypes = "<types>", StepCheck = "<check>";

    /// <summary>The package registry the setup offers as larva's default (<c>larva config hive.provider</c>).</summary>
    public const string HiveProvider = "https://www.antril.org/hive-api";

    /// <summary>
    /// larva's settings file (as larva finds it: <c>LARVA_CONFIG</c>, else <c>%APPDATA%/Larva/defaults.json</c>) with
    /// <see cref="HiveProvider"/> among its registries: first, the others it names kept, and the rest of the file kept.
    /// </summary>
    public static void AddHiveProvider()
    {
        string file = Environment.GetEnvironmentVariable("LARVA_CONFIG") is { Length: > 0 } given
            ? Path.GetFullPath(given)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Larva", "defaults.json");
        var settings = new System.Text.Json.Nodes.JsonObject();
        try
        {
            if (File.Exists(file) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) is System.Text.Json.Nodes.JsonObject read)
                settings = read;
        }
        catch (System.Text.Json.JsonException)
        {
            // An unreadable file is replaced by one larva can read.
        }
        var providers = (settings["hive.provider"]?.GetValue<string>() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (providers.Any(p => p.TrimEnd('/').EndsWith("www.antril.org/hive-api", StringComparison.OrdinalIgnoreCase)))
            return;
        providers.Insert(0, HiveProvider);
        settings["hive.provider"] = string.Join(",", providers);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = File.Create(file);
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
            settings.WriteTo(writer);
        stream.Write("\n"u8);
    }

    public sealed record Result(bool Success, string? Installed, string? Error);

    /// <summary>
    /// Installs into <paramref name="folder"/>; <paramref name="progress"/> hears each step (0 to 1, and what it is).
    /// </summary>
    public static Result Install(string folder, bool path, bool registry, bool hive, bool types, Action<double, string>? progress = null)
    {
        void Step(double fraction, string what) => progress?.Invoke(fraction, what);
        try
        {
            using var payload = Payload();
            if (payload is null)
                return new Result(false, null, "this setup carries no larva (build it with tools/build-installer.ps1)");
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            // What an earlier Larva put here and this one does not bring (0.7.0's System.Core sources): only its files.
            RemoveInstalled(folder, keep: Environment.ProcessPath is { } running ? Path.GetFullPath(running) : null);
            string inside = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
            var installed = new List<string>();
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                long total = Math.Max(1, archive.Entries.Sum(e => e.Length)), done = 0;
                foreach (var entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                    if (!target.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                        continue; // never outside the folder
                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Step(0.85 * done / total, entry.FullName.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    installed.Add(Path.GetRelativePath(folder, target));
                    done += entry.Length;
                }
            }
            // This program, as the installed folder's uninstaller.
            Step(0.88, "uninstall.exe");
            string uninstaller = Path.Combine(folder, "uninstall.exe");
            if (Environment.ProcessPath is { } self && !string.Equals(Path.GetFullPath(self), uninstaller, StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstaller, overwrite: true);
            installed.Add("uninstall.exe");
            // The list of what is ours: the uninstaller removes these and nothing else that shares the folder.
            File.WriteAllLines(Path.Combine(folder, ManifestName), installed);
            if (path)
            {
                Step(0.92, StepPath);
                AddToPath(folder);
            }
            if (registry)
            {
                Step(0.95, StepApps);
                Register(folder);
            }
            if (hive)
            {
                Step(0.96, StepHive);
                AddHiveProvider();
            }
            if (types)
            {
                Step(0.97, StepTypes);
                RegisterFileTypes(folder);
            }
            Step(0.98, StepCheck);
            string? answer = Run(Path.Combine(folder, "larva.exe"), "--version");
            Step(1, "");
            return answer is { Length: > 0 }
                ? new Result(true, answer, null)
                : new Result(false, null, $"{Path.Combine(folder, "larva.exe")} did not answer `--version`");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new Result(false, null, e.Message);
        }
    }

    /// <summary>What larva needs from the machine for some commands: each with whether it was found.</summary>
    public static (bool Msvc, bool Clang, bool Node) Prerequisites()
    {
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        string? msvc = File.Exists(vswhere) ? Run(vswhere, "-latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath") : null;
        bool clang = File.Exists(@"C:\Program Files\LLVM\bin\clang.exe") || OnPath("clang.exe");
        return (msvc is { Length: > 0 }, clang, OnPath("node.exe"));
    }

    private static bool OnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => d.Length > 0 && File.Exists(Path.Combine(d, file)));

    public static string? Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(30_000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // ── The user's PATH ────────────────────────────────

    private static List<string> PathEntries(RegistryKey environment) =>
        ((environment.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool AddToPath(string folder)
    {
        using var environment = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        var entries = PathEntries(environment);
        if (entries.Any(e => Same(Environment.ExpandEnvironmentVariables(e), folder)))
            return false;
        entries.Add(folder);
        environment.SetValue("Path", string.Join(';', entries), RegistryValueKind.ExpandString);
        Native.BroadcastEnvironment();
        return true;
    }

    public static bool RemoveFromPath(string folder)
    {
        using var environment = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        if (environment is null)
            return false;
        var entries = PathEntries(environment);
        int before = entries.Count;
        entries.RemoveAll(e => Same(Environment.ExpandEnvironmentVariables(e), folder));
        if (entries.Count == before)
            return false;
        environment.SetValue("Path", string.Join(';', entries), RegistryValueKind.ExpandString);
        Native.BroadcastEnvironment();
        return true;
    }

    // ── Settings > Apps ────────────────────────────────

    private static void Register(string folder)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        string uninstaller = Path.Combine(folder, "uninstall.exe");
        long kilobytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        key.SetValue("DisplayName", $"{Product} {Version}");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Product);
        key.SetValue("InstallLocation", folder);
        key.SetValue("DisplayIcon", uninstaller);
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --silent");
        key.SetValue("EstimatedSize", (int)Math.Min(kilobytes, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // ── File types ─────────────────────────────────────

    private const string Classes = @"Software\Classes";

    /// <summary>
    /// The kinds of file larva reads, each as Explorer will know it (its extension, a name for its class, what Explorer
    /// calls it and its icon): larva's own (icons/ in the folder) and each language pack's source files (its manifest's
    /// <c>extensions</c> and <c>icon</c>).
    /// </summary>
    public static List<(string Extension, string Class, string Name, string Icon)> FileTypes(string folder)
    {
        string icons = Path.Combine(folder, "icons");
        var types = new List<(string, string, string, string)>
        {
            (".lva", "Larva.Library", "Larva library", Path.Combine(icons, "file-lva.ico")),
            (".lvo", "Larva.Object", "Larva object file", Path.Combine(icons, "file-lvo.ico")),
            (".lvm", "Larva.Module", "Larva module", Path.Combine(icons, "file-lvm.ico")),
            (".bind", "Larva.Binding", "Larva binding description", Path.Combine(icons, "file-bind.ico")),
            (".queen", "Larva.Project", "Larva project", Path.Combine(icons, "file-queen.ico")),
            (".hive", "Larva.Package", "Hive package", Path.Combine(icons, "file-hive.ico")),
        };
        string languages = Path.Combine(folder, "Languages");
        if (!Directory.Exists(languages))
            return types;
        foreach (string manifest in Directory.EnumerateFiles(languages, "language.json", SearchOption.AllDirectories))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                var root = json.RootElement;
                string pack = Path.GetDirectoryName(manifest)!;
                string name = root.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString()! : Path.GetFileName(pack);
                string icon = root.TryGetProperty("icon", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.String
                    ? Path.Combine(pack, i.GetString()!)
                    : Path.Combine(icons, "file-lva.ico");
                if (!root.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                string id = new(name.Where(char.IsLetterOrDigit).ToArray());
                foreach (var extension in extensions.EnumerateArray())
                {
                    if (extension.ValueKind == System.Text.Json.JsonValueKind.String && extension.GetString() is { Length: > 1 } e && e[0] == '.')
                        types.Add((e.ToLowerInvariant(), $"Larva.{id}.{e[1..].ToLowerInvariant()}", $"{name} source file", icon));
                }
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                // A pack larva could not load either: no file type.
            }
        }
        return types;
    }

    /// <summary>
    /// Explorer shows larva's files with their icons: a class per kind (HKCU\Software\Classes, this user only), offered
    /// for its extension, and the extension's default only where no other program has taken it.
    /// </summary>
    public static void RegisterFileTypes(string folder)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(Classes, writable: true);
        foreach (var (extension, name, description, icon) in FileTypes(folder))
        {
            if (!File.Exists(icon))
                continue;
            using (var type = classes.CreateSubKey(name, writable: true))
            {
                type.SetValue("", description);
                using var defaultIcon = type.CreateSubKey("DefaultIcon", writable: true);
                defaultIcon.SetValue("", $"\"{icon}\",0");
            }
            using var key = classes.CreateSubKey(extension, writable: true);
            if (string.IsNullOrEmpty(key.GetValue("") as string))
                key.SetValue("", name);
            using var offered = key.CreateSubKey("OpenWithProgids", writable: true);
            offered.SetValue(name, Array.Empty<byte>(), RegistryValueKind.None);
        }
        Native.AssociationsChanged();
    }

    /// <summary>
    /// Takes back <see cref="RegisterFileTypes"/>: the classes whose icon is in <paramref name="folder"/>, and their
    /// extensions' references to them (an extension left with nothing is removed).
    /// </summary>
    public static void UnregisterFileTypes(string folder)
    {
        using var classes = Registry.CurrentUser.OpenSubKey(Classes, writable: true);
        if (classes is null)
            return;
        string inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in classes.GetSubKeyNames().Where(n => n.StartsWith("Larva.", StringComparison.OrdinalIgnoreCase)))
        {
            using var icon = classes.OpenSubKey(name + @"\DefaultIcon");
            if (icon?.GetValue("") is string value && value.TrimStart('"').StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                ours.Add(name);
        }
        if (ours.Count == 0)
            return;
        foreach (string extension in classes.GetSubKeyNames().Where(n => n.StartsWith('.')))
        {
            using var key = classes.OpenSubKey(extension, writable: true);
            if (key is null)
                continue;
            bool changed = false;
            if (key.GetValue("") is string chosen && ours.Contains(chosen))
            {
                key.DeleteValue("", throwOnMissingValue: false);
                changed = true;
            }
            using (var offered = key.OpenSubKey("OpenWithProgids", writable: true))
            {
                if (offered is not null)
                {
                    foreach (string name in offered.GetValueNames().Where(ours.Contains).ToList())
                    {
                        offered.DeleteValue(name);
                        changed = true;
                    }
                }
                if (changed && offered is { ValueCount: 0, SubKeyCount: 0 })
                    key.DeleteSubKey("OpenWithProgids", throwOnMissingSubKey: false);
            }
            if (changed && key.ValueCount == 0 && key.SubKeyCount == 0)
                classes.DeleteSubKey(extension, throwOnMissingSubKey: false);
        }
        foreach (string name in ours)
            classes.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        Native.AssociationsChanged();
    }

    // ── Uninstall ──────────────────────────────────────

    /// <summary>The list of the files an installation put in its folder (relative paths, one per line).</summary>
    private const string ManifestName = "uninstall.files";

    /// <summary>What Larva 0.7.0 installed, for a folder with no list of its own.</summary>
    private static readonly string[] Known =
        ["larva.exe", "LLVM-C.dll", "libLLVMSharp.dll", "System.Core.dll", "README.txt", "uninstall.exe"];

    /// <summary>
    /// The files an earlier installation put in <paramref name="folder"/> (its list, or what 0.7.0 brought), removed, and
    /// the folders they were in when that leaves them empty; nothing else in the folder is touched. The file
    /// <paramref name="keep"/> (the running uninstaller) stays.
    /// </summary>
    private static void RemoveInstalled(string folder, string? keep)
    {
        string manifest = Path.Combine(folder, ManifestName);
        IEnumerable<string> ours = File.Exists(manifest)
            ? File.ReadAllLines(manifest).Where(l => l.Length > 0)
            : File.Exists(Path.Combine(folder, "larva.exe")) ? Known.Concat(OldLibrary(folder)) : [];
        string inside = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in ours)
        {
            string file = Path.GetFullPath(Path.Combine(folder, relative));
            if (!file.StartsWith(inside, StringComparison.OrdinalIgnoreCase) || string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(file))
                File.Delete(file);
            for (string? parent = Path.GetDirectoryName(file); parent is not null && parent.StartsWith(inside, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                parents.Add(parent);
        }
        foreach (string parent in parents.OrderByDescending(p => p.Length))
        {
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        if (File.Exists(manifest))
            File.Delete(manifest);
    }

    /// <summary>0.7.0's System.Core sources (Library/System.Core/*.ant), relative to the folder.</summary>
    private static IEnumerable<string> OldLibrary(string folder)
    {
        string library = Path.Combine(folder, "Library", "System.Core");
        return Directory.Exists(library)
            ? Directory.EnumerateFiles(library, "*.ant").Select(f => Path.GetRelativePath(folder, f)).ToList()
            : [];
    }

    /// <summary>
    /// Takes back what <see cref="Install"/> did in <paramref name="folder"/>: its files only (anything else there stays,
    /// and so does the folder then): null, or what went wrong.
    /// </summary>
    public static string? Uninstall(string folder, bool path, bool registry)
    {
        folder = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(folder, "larva.exe")))
            return $"no larva in {folder}";
        try
        {
            if (path)
                RemoveFromPath(folder);
            if (registry)
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            UnregisterFileTypes(folder);
            string? self = Environment.ProcessPath is { } p ? Path.GetFullPath(p) : null;
            RemoveInstalled(folder, keep: self);
            bool running = self is not null && string.Equals(Path.GetDirectoryName(self), folder, StringComparison.OrdinalIgnoreCase);
            if (running)
            {
                // The uninstaller cannot delete itself while it runs: a moment after it ends, it goes, and the folder
                // with it only if nothing else is left there (rmdir without /s).
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\" & rmdir \"{folder}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            else if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
