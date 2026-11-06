using Larva.Setup;
using static Larva.Setup.Native;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>
/// The Larva toolchain's installer. With no options it is a wizard that installs for the current user (no administrator
/// rights): larva.exe, its LLVM libraries and System.Core's source into %LOCALAPPDATA%\Programs\Larva, that folder on
/// the user's PATH, and an entry in Settings > Apps whose uninstaller (uninstall.exe, this same program) takes it all back.
/// <code>
/// LarvaSetup.exe [--dir &lt;folder&gt;] [--no-path] [--no-registry] [--no-hive] [--no-types]              the wizard
/// LarvaSetup.exe --silent [--dir &lt;folder&gt;] [--no-path] [--no-registry] [--no-hive] [--no-types]     no questions
/// LarvaSetup.exe --uninstall [--silent] [--dir &lt;folder&gt;] [--no-path] [--no-registry]
/// </code>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = false, uninstall = false, path = true, registry = true, hive = true, types = true;
        string? folder = null, test = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--silent" or "-s" or "/s" or "/silent": silent = true; break;
                case "--uninstall" or "/uninstall": uninstall = true; break;
                case "--no-path": path = false; break;
                case "--no-registry": registry = false; break;
                case "--no-hive": hive = false; break;
                case "--no-types": types = false; break;
                case "--dir" when i + 1 < args.Length: folder = Path.GetFullPath(args[++i]); break;
                case "--types-test" when i + 1 < args.Length: return TypesTest(Path.GetFullPath(args[++i]));
                case "--wizard-test" when i + 1 < args.Length: test = Path.GetFullPath(args[++i]); break;
                case "--help" or "-h" or "/?":
                    AttachConsole(-1);
                    Console.WriteLine($"""

                        Larva {Installer.Version} setup

                          LarvaSetup.exe [--dir <folder>] [--no-path] [--no-registry] [--no-hive] [--no-types]              the wizard
                          LarvaSetup.exe --silent [--dir <folder>] [--no-path] [--no-registry] [--no-hive] [--no-types]     no questions
                          LarvaSetup.exe --uninstall [--silent] [--dir <folder>] [--no-path] [--no-registry]

                        Installs larva for the current user into %LOCALAPPDATA%\Programs\Larva (or --dir), puts that folder on
                        the user's PATH (not with --no-path), registers it in Settings > Apps (not with --no-registry) and
                        makes https://www.antril.org/hive-api larva's default package registry (not with --no-hive) and
                        shows larva's files (.lva, .lvo, .queen, .hive, source files) with their icons (not with --no-types).
                        """);
                    return 0;
                default:
                    AttachConsole(-1);
                    Console.Error.WriteLine($"LarvaSetup: unknown option `{args[i]}` (--help lists them)");
                    return 2;
            }
        }
        if (silent)
            AttachConsole(-1);
        if (uninstall)
            return Uninstall(folder ?? Path.GetDirectoryName(Environment.ProcessPath)!, silent, path, registry);
        if (!silent)
            return Wizard.Run(folder ?? Installer.InstalledFolder() ?? Installer.DefaultFolder, path, registry, hive, types, test);
        return Silent(folder ?? Installer.DefaultFolder, path, registry, hive, types);
    }

    private static int Silent(string folder, bool path, bool registry, bool hive, bool types)
    {
        Console.WriteLine($"Larva {Installer.Version} into {folder}");
        var result = Installer.Install(folder, path, registry, hive, types);
        if (!result.Success)
        {
            Console.Error.WriteLine($"LarvaSetup: {result.Error}");
            Console.Error.WriteLine("Make sure Larva is not in use (an editor's language server, a build) and run the setup again.");
            return 1;
        }
        Console.WriteLine($"Installed: {result.Installed}");
        if (path)
            Console.WriteLine($"{folder} is on your PATH (new terminals see it).");
        var (msvc, clang, node) = Installer.Prerequisites();
        Console.WriteLine(msvc
            ? "  ok   MSVC build tools (native executables)"
            : "  --   MSVC build tools not found: `larva build` of native executables needs them (Visual Studio Build Tools, C++ workload)");
        Console.WriteLine(clang ? "  ok   clang (C headers for `larva bind`)" : "  --   clang not found: `larva bind` reads C headers without preprocessing them (LLVM's installer adds clang)");
        Console.WriteLine(node ? "  ok   Node (runs WebAssembly builds)" : "  --   Node not found: only needed to run WebAssembly builds outside a browser");
        return 0;
    }

    private static int Uninstall(string folder, bool silent, bool path, bool registry)
    {
        var t = Texts.Current;
        string caption = string.Format(t.Window, Installer.Version);
        if (!silent && MessageBox(0, string.Format(t.UninstallConfirm, Installer.InstalledVersion() ?? Installer.Version, folder), caption, MB_YESNO | MB_ICONQUESTION) != IDYES)
            return 1;
        string? error = Installer.Uninstall(folder, path, registry);
        if (silent)
        {
            if (error is null)
                Console.WriteLine("Larva was removed.");
            else
                Console.Error.WriteLine($"LarvaSetup: {error}");
        }
        else
            MessageBox(0, error is null ? t.UninstallDone : string.Format(t.UninstallFailed, error), caption, error is null ? MB_ICONINFORMATION : MB_ICONERROR);
        return error is null ? 0 : 1;
    }

    /// <summary>
    /// A test of the file types (not listed in --help): with HKEY_CURRENT_USER sent to a registry file of its own
    /// (<c>folder	ypes.dat</c>, this process only), what registering larva in <paramref name="folder"/> writes, and what
    /// is left once it is taken back, printed.
    /// </summary>
    private static int TypesTest(string folder)
    {
        AttachConsole(-1);
        string file = Path.Combine(folder, "types.dat");
        int loaded = RegLoadAppKey(file, out nint hive, 0xF003F, 0, 0), overridden = loaded == 0 ? RegOverridePredefKey(unchecked((nint)(int)0x80000001), hive) : -1;
        if (loaded != 0 || overridden != 0)
        {
            Console.Error.WriteLine($"LarvaSetup: no test registry ({loaded}, {overridden})");
            return 1;
        }
        // Someone else's extension, which larva must offer itself for and leave as it was.
        using (var taken = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\.bind"))
            taken.SetValue("", "Other.Program");
        Installer.RegisterFileTypes(folder);
        Console.WriteLine("registered:");
        Dump();
        Installer.UnregisterFileTypes(folder);
        Console.WriteLine("removed:");
        Dump();
        RegOverridePredefKey(unchecked((nint)(int)0x80000001), 0);
        RegCloseKey(hive);
        return 0;

        static void Dump()
        {
            using var classes = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            foreach (string name in (classes?.GetSubKeyNames() ?? []).Order(StringComparer.Ordinal))
            {
                using var key = classes!.OpenSubKey(name)!;
                using var icon = key.OpenSubKey("DefaultIcon");
                using var offered = key.OpenSubKey("OpenWithProgids");
                Console.WriteLine($"  {name} = {key.GetValue("")}"
                    + (icon is null ? "" : $"  icon {icon.GetValue("")}")
                    + (offered is null ? "" : $"  offers {string.Join(",", offered.GetValueNames())}"));
            }
        }
    }
}
