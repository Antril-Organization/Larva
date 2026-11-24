using System.Diagnostics;

namespace Larva.Native
{
    /// <summary>
    /// Links native executables: the program's object (from either back end) plus the C runtime
    /// (<c>Runtime/larva_rt.c</c>, embedded in this assembly), compiled and linked with MSVC (<c>cl</c> / <c>link</c>).
    /// <para>
    /// MSVC is found with <c>vswhere</c> (any Visual Studio or Build Tools with the "Desktop development with C++" workload)
    /// (stable or preview/Insiders) or through <c>LARVA_VCVARS</c> (the path of <c>vcvars64.bat</c>). Executables are linked with <c>/CETCOMPAT:NO</c>:
    /// SEH continuations into Larva code are not registered as EH continuation targets for the CET shadow stack.
    /// </para>
    /// </summary>
    public sealed class Toolchain
    {
        public string VcVars { get; }

        private Toolchain(string vcvars) => VcVars = vcvars;

        /// <summary>MSVC, if installed; otherwise <c>null</c>.</summary>
        public static Toolchain? Find()
        {
            string? configured = Environment.GetEnvironmentVariable("LARVA_VCVARS");
            if (!string.IsNullOrEmpty(configured))
                return File.Exists(configured) ? new Toolchain(configured) : null;
            string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere))
                return null;
            var (code, output) = Run(vswhere, ["-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"]);
            string installation = output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
            string vcvars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
            return code == 0 && installation.Length > 0 && File.Exists(vcvars) ? new Toolchain(vcvars) : null;
        }

        public const string Missing =
            "no C toolchain: install Visual Studio Build Tools with the \"Desktop development with C++\" workload (MSVC and the Windows SDK), " +
            "or set LARVA_VCVARS to the path of vcvars64.bat";

        /// <summary>The C runtime's source.</summary>
        public static string RuntimeSource()
        {
            using var stream = typeof(Toolchain).Assembly.GetManifestResourceStream("Larva.Native.Runtime.larva_rt.c")
                ?? throw new InvalidOperationException("the C runtime is not embedded");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>With <c>LARVA_MAP=1</c>, the linker also writes <c>output.map</c> (function addresses, for crash reports).</summary>
        private static string? MapOption(string output) =>
            Environment.GetEnvironmentVariable("LARVA_MAP") is { Length: > 0 } map && map != "0"
                ? $"/MAP:{Path.ChangeExtension(Path.GetFullPath(output), ".map")}" : null;

        /// <summary>
        /// Links <paramref name="objects"/> with the runtime into <paramref name="output"/>. The runtime is compiled once per
        /// version of its source (kept in the toolchain cache), and the compiler's environment is read once (see
        /// <see cref="Developer"/>): a link is then the linker alone.
        /// </summary>
        public (bool Success, string Log) Link(IReadOnlyList<string> objects, string output)
        {
            var (runtime, compileLog) = CachedRuntime();
            if (runtime is null)
                return (false, compileLog);
            var (options, system, systemLog) = ProgramLinking();
            if (system is null)
                return (false, systemLog);
            List<string> arguments = ["/nologo", "/CETCOMPAT:NO", .. options, $"/OUT:{Path.GetFullPath(output)}",
                .. objects.Select(Path.GetFullPath), runtime, .. system];
            if (MapOption(output) is { } map)
                arguments.Add(map);
            return RunCommands(_ => [new Command("link", arguments)], output);
        }

        /// <summary>
        /// Links <paramref name="objects"/> with the runtime into a DLL that C code can load, exporting
        /// <paramref name="exports"/> (the C names of the program's <c>[Export]</c> functions).
        /// </summary>
        public (bool Success, string Log) LinkDll(IReadOnlyList<string> objects, string output, IReadOnlyList<string> exports)
        {
            var (runtime, compileLog) = CachedRuntime();
            if (runtime is null)
                return (false, compileLog);
            var (system, systemLog) = SystemLibraries();
            if (system is null)
                return (false, systemLog);
            List<string> arguments = ["/nologo", "/DLL", "/ENTRY:DllMain", "/CETCOMPAT:NO", .. SmallImage, $"/OUT:{Path.GetFullPath(output)}",
                .. objects.Select(Path.GetFullPath), runtime, .. system, .. exports.Select(e => $"/EXPORT:{e}")];
            return RunCommands(_ => [new Command("link", arguments)], output);
        }

        /// <summary>
        /// What keeps executables small: unused functions and data dropped and identical ones folded, and the C library
        /// (UCRT) taken from Windows (a part of it since Windows 10) instead of copied into every program. That copy was
        /// most of a program (printf, strtod, locale and logarithm tables); calls into the system's are just as fast.
        /// The rest of the C runtime (startup, vcruntime) stays static, so a program needs no redistributable.
        /// </summary>
        private static readonly string[] SmallImage = ["/OPT:REF", "/OPT:ICF", "/INCREMENTAL:NO", "/NODEFAULTLIB"];

        /// <summary>
        /// What ntdll.dll exports for C code (the SDK's ntdll.lib leaves these out): the SEH handler and stack probe compiled
        /// code uses, and a few string functions (memcpy, memmove and memset are the runtime's own: faster than ntdll's).
        /// </summary>
        private const string NtdllExports = "__C_specific_handler __chkstk memcmp strlen wcslen strrchr";

        /// <summary>
        /// The only libraries a program links: the runtime takes nothing from a C library (see larva_rt.c, "Without the C
        /// runtime"), only Windows itself (kernel32, and ntdll through an import library made once from a .def).
        /// </summary>
        private (string[]? Libraries, string Log) SystemLibraries()
        {
            string target = Path.Combine(CacheFolder, $"ntdll-crt-{Hash(NtdllExports + VcVars + InstallationStamp())}.lib");
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(CacheFolder);
                string partial = target + $".{Guid.NewGuid():N}.partial";
                var (ok, log) = RunCommands(directory =>
                {
                    string def = Path.Combine(directory, "ntdll.def");
                    File.WriteAllLines(def, ["LIBRARY ntdll.dll", "EXPORTS", .. NtdllExports.Split(' ')]);
                    return [new Command("lib", ["/nologo", "/machine:x64", $"/def:{def}", $"/out:{partial}"])];
                }, partial);
                if (!ok)
                    return (null, log);
                if (!Keep(partial, target))
                    return (null, $"cannot keep ntdll's import library in {CacheFolder}");
            }
            return (["kernel32.lib", "ws2_32.lib", target], "");
        }

        /// <summary>A program on the environment's PATH.</summary>
        private static string? Find(IReadOnlyDictionary<string, string> environment, string program)
        {
            if (!environment.TryGetValue("PATH", out var path))
                return null;
            foreach (var folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(folder.Trim(), program);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        // ── Caches ─────────────────────────────────────────────

        /// <summary>Where compiled runtimes and the compiler's environment are kept (per user).</summary>
        private static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "toolchain");

        private static string Hash(string text) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..20];

        /// <summary>
        /// <c>LARVA_WITH_CRT=1</c>: programs use the C library as before (the runtime built with it, linked with the
        /// Windows UCRT): to compare against the runtime without it.
        /// </summary>
        public static bool WithCrt { get; } = Environment.GetEnvironmentVariable("LARVA_WITH_CRT") == "1";

        /// <summary>
        /// <c>LARVA_DIAGNOSTICS=1</c>: programs are built with the runtime's diagnostics (LARVA_GC_STRESS, LARVA_GC_VERIFY,
        /// LARVA_STATS… at run time); without it they are left out (smaller programs).
        /// </summary>
        public static bool Diagnostics { get; } = Environment.GetEnvironmentVariable("LARVA_DIAGNOSTICS") == "1";

        private static string RuntimeFlags => "/nologo /O2 /Gy /Gw /GS- /W3 /c" + (WithCrt ? " /DLARVA_WITH_CRT" : "") + (Diagnostics ? " /DLARVA_DIAGNOSTICS" : "");

        /// <summary>The linker's options for a console program: its entry and what it links besides objects and the runtime.</summary>
        private (string[] Options, string[]? Libraries, string Log) ProgramLinking()
        {
            if (WithCrt)
                return (["/SUBSYSTEM:CONSOLE", "/OPT:REF", "/OPT:ICF", "/INCREMENTAL:NO", "/NODEFAULTLIB:libucrt.lib", "ucrt.lib"], ["kernel32.lib", "ws2_32.lib"], "");
            var (system, log) = SystemLibraries();
            return (["/SUBSYSTEM:CONSOLE", "/ENTRY:larva_crt_start", .. SmallImage], system, log);
        }

        /// <summary>The runtime's object for executables: compiled the first time its source (or these flags) is seen.</summary>
        private (string? Path, string Log) CachedRuntime()
        {
            string source = RuntimeSource();
            string target = Path.Combine(CacheFolder, $"larva_rt-{Hash(source + RuntimeFlags + VcVars + InstallationStamp())}.obj");
            if (File.Exists(target))
                return (target, "");
            Directory.CreateDirectory(CacheFolder);
            string partial = target + $".{Guid.NewGuid():N}.partial";
            var (ok, log) = RunCommands(directory =>
            {
                string file = Path.Combine(directory, "larva_rt.c");
                File.WriteAllText(file, source);
                return [new Command("cl", [.. RuntimeFlags.Split(' '), $"/Fo{partial}", file])];
            }, partial);
            if (!ok)
                return (null, log);
            if (!Keep(partial, target))
                return (null, $"cannot keep the compiled runtime in {CacheFolder}");
            return (target, log);
        }

        /// <summary>
        /// A file made under a name of its own (<paramref name="partial"/>) put in its place in the cache. Builds running at
        /// once may make the same file: the first one keeps it, the others find it there (Windows refuses to replace a file
        /// another process has open: access denied) and drop their copy. Whether <paramref name="target"/> is there.
        /// </summary>
        private static bool Keep(string partial, string target)
        {
            try
            {
                File.Move(partial, target, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(partial);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                }
                return File.Exists(target);
            }
        }

        private Dictionary<string, string>? developer;

        /// <summary>
        /// The environment <c>vcvars64.bat</c> makes (paths of the compiler, linker and SDK), read once and kept in the
        /// toolchain cache (it takes seconds to compute); null when it cannot be read, and scripts then call vcvars.
        /// </summary>
        private Dictionary<string, string>? Developer()
        {
            if (developer is not null)
                return developer;
            string file = Path.Combine(CacheFolder, $"vcvars-{Hash(VcVars + InstallationStamp())}.env");
            string text;
            if (File.Exists(file) && File.ReadAllText(file) is { } cached && Find(Parse(cached), "cl.exe") is not null)
            {
                text = cached;
            }
            else
            {
                string script = Path.Combine(Path.GetTempPath(), $"larva-vcvars-{Guid.NewGuid():N}.bat");
                File.WriteAllText(script, $"@echo off\r\ncall {Quote(VcVars)} >nul || exit /b 1\r\nset\r\n");
                int code;
                string output;
                try
                {
                    (code, output) = Run("cmd.exe", ["/d", "/c", script]);
                }
                finally
                {
                    File.Delete(script);
                }
                if (code != 0)
                    return null;
                text = output;
                try
                {
                    Directory.CreateDirectory(CacheFolder);
                    string partial = file + $".{Guid.NewGuid():N}.partial";
                    File.WriteAllText(partial, text);
                    Keep(partial, file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Kept by another build, or not at all: this one has it.
                }
            }
            var variables = Parse(text);
            return developer = variables.ContainsKey("PATH") && variables.ContainsKey("INCLUDE") ? variables : null;
        }

        /// <summary>The output of <c>set</c> as variables.</summary>
        private static Dictionary<string, string> Parse(string text)
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split('\n'))
            {
                int equals = line.IndexOf('=');
                if (equals > 0)
                    variables[line[..equals]] = line[(equals + 1)..].TrimEnd('\r');
            }
            return variables;
        }

        /// <summary>
        /// What identifies the installed toolchain, for the cached environment: vcvars64.bat's last write and size, and
        /// the default tools version next to it (<c>Microsoft.VCToolsVersion.default.txt</c>: it changes with every update,
        /// vcvars64.bat itself usually does not). The environment names the tools' folders, so it goes stale with them.
        /// </summary>
        private string InstallationStamp()
        {
            var stamp = new System.Text.StringBuilder();
            if (File.Exists(VcVars))
            {
                var info = new FileInfo(VcVars);
                stamp.Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length);
                foreach (var name in new[] { "Microsoft.VCToolsVersion.default.txt", "Microsoft.VCRedistVersion.default.txt" })
                {
                    string version = Path.Combine(info.DirectoryName ?? "", name);
                    if (File.Exists(version))
                        stamp.Append(':').Append(File.ReadAllText(version).Trim()).Append(':').Append(File.GetLastWriteTimeUtc(version).Ticks);
                }
            }
            return stamp.ToString();
        }

        // ── Native libraries (DLLs) ─────────────────────────────

        /// <summary>The runtime as a DLL that a program and its Larva DLLs share.</summary>
        public const string RuntimeDll = "larva_rt.dll";

        /// <summary>
        /// The start of a program linked against <see cref="RuntimeDll"/>: no C library of its own, it hands its entry
        /// wrappers to the runtime, which parses the command line and runs it.
        /// </summary>
        private const string StartSource = """
            #define WIN32_LEAN_AND_MEAN
            #include <windows.h>
            #include <stdint.h>
            __declspec(dllimport) int larva_run_main(void *entry, void *entry_f64, void *entry_info);
            extern int64_t larva_entry(int64_t, int64_t, int64_t, int64_t);
            extern double larva_entry_f64(int64_t, int64_t, int64_t, int64_t);
            extern int64_t larva_entry_info(void);
            int _fltused = 0;
            void larva_start(void) { ExitProcess((UINT)larva_run_main((void *)larva_entry, (void *)larva_entry_f64, (void *)larva_entry_info)); }
            """;

        /// <summary>
        /// Builds <see cref="RuntimeDll"/> and its import library into <paramref name="folder"/> (the DLL carries the C
        /// library it uses, so programs and Larva DLLs need none).
        /// </summary>
        public (bool Success, string Log) BuildRuntime(string folder)
        {
            Directory.CreateDirectory(folder);
            return RunCommands(directory =>
            {
                string source = Path.Combine(directory, "larva_rt.c");
                File.WriteAllText(source, RuntimeSource());
                return [new Command("cl", ["/nologo", "/O2", "/GS-", "/W3", "/MT", "/DLARVA_SHARED_RUNTIME", "/D_CRT_SECURE_NO_WARNINGS", "/LD", source,
                    $"/Fo{Path.Combine(directory, "larva_rt.obj")}", $"/Fe{Path.Combine(Path.GetFullPath(folder), RuntimeDll)}",
                    "/link", "/NOLOGO", "/CETCOMPAT:NO", "/OPT:REF", "/OPT:ICF", "/INCREMENTAL:NO", "/NODEFAULTLIB:libucrt.lib", "ucrt.lib", "kernel32.lib", "shell32.lib"])];
            }, Path.Combine(folder, RuntimeDll));
        }

        /// <summary>
        /// An import library for <paramref name="dll"/> exporting <paramref name="exports"/> (object-file symbols): what code
        /// that calls into that DLL links against. Made from the export list alone, so DLLs can depend on each other.
        /// </summary>
        public (bool Success, string Log) ImportLibrary(string dll, IReadOnlyList<string> exports, string output) =>
            RunCommands(directory =>
            {
                string response = Path.Combine(directory, "exports.rsp");
                File.WriteAllText(response, Exports(exports));
                return [new Command("lib", ["/nologo", "/machine:x64", "/def", $"/name:{Path.GetFileName(dll)}", $"@{response}", $"/out:{Path.GetFullPath(output)}"])];
            }, output);

        /// <summary>
        /// A Larva DLL: <paramref name="objects"/> exporting <paramref name="exports"/>, importing from the runtime, the
        /// other DLLs (<paramref name="libraries"/>: import libraries) and Windows. No C library and no entry point.
        /// LLVM marks code using floats with a reference to the C library's <c>_fltused</c>, which nothing reads: it is
        /// pointed at a runtime symbol.
        /// </summary>
        public (bool Success, string Log) LinkLibrary(IReadOnlyList<string> objects, string output, IReadOnlyList<string> exports,
            string runtimeLibrary, IReadOnlyList<string> libraries) =>
            RunCommands(directory =>
            {
                string response = Path.Combine(directory, "exports.rsp");
                File.WriteAllText(response, Exports(exports));
                return [new Command("link", ["/nologo", "/DLL", "/NOENTRY", "/NODEFAULTLIB", "/NOIMPLIB", "/NOEXP", "/CETCOMPAT:NO", "/OPT:REF", "/OPT:ICF", "/INCREMENTAL:NO",
                    "/ALTERNATENAME:_fltused=larva_poll", $"@{response}", $"/OUT:{Path.GetFullPath(output)}",
                    .. objects.Concat(libraries).Append(runtimeLibrary).Select(Path.GetFullPath), "kernel32.lib", "ntdll.lib", "libcmt.lib"])];
            }, output);

        /// <summary>A program that runs on <see cref="RuntimeDll"/> and calls into Larva DLLs (<paramref name="libraries"/>: import libraries).</summary>
        public (bool Success, string Log) LinkProgram(IReadOnlyList<string> objects, string output, string runtimeLibrary, IReadOnlyList<string> libraries) =>
            RunCommands(directory =>
            {
                string start = Path.Combine(directory, "larva_start.c");
                File.WriteAllText(start, StartSource);
                string startObject = Path.Combine(directory, "larva_start.obj");
                List<string> link = ["/nologo", "/SUBSYSTEM:CONSOLE", "/ENTRY:larva_start", "/NODEFAULTLIB", "/CETCOMPAT:NO"];
                if (MapOption(output) is { } map)
                    link.Add(map);
                link.AddRange(["/OPT:REF", "/OPT:ICF", "/INCREMENTAL:NO", $"/OUT:{Path.GetFullPath(output)}",
                    .. objects.Concat(libraries).Append(runtimeLibrary).Select(Path.GetFullPath), startObject, "kernel32.lib", "ntdll.lib", "libcmt.lib"]);
                return [new Command("cl", ["/nologo", "/O1", "/GS-", "/c", $"/Fo{startObject}", start]), new Command("link", link)];
            }, output);

        /// <summary>A response file of <c>/EXPORT</c> options, each quoted whole so any symbol (spaces, <c>@</c>, <c>=</c>) passes as is.</summary>
        private static string Exports(IReadOnlyList<string> exports) => string.Concat(exports.Select(e => $"\"/EXPORT:{e}\"\n"));

        /// <summary>An MSVC tool (<c>cl</c>, <c>link</c>, <c>lib</c>) and its arguments, each one whole (a path with spaces needs no quotes).</summary>
        private sealed record Command(string Program, IReadOnlyList<string> Arguments);

        /// <summary>
        /// Runs MSVC commands in a scratch folder, stopping at the first that fails; succeeds when <paramref name="expected"/>
        /// exists. With the compiler's environment known (see <see cref="Developer"/>) each tool runs directly, its
        /// arguments passed whole: nothing in between re-reads them. Only without it does a batch file call vcvars and
        /// then the tools; cmd reads that file in the console's code page (not UTF-8), so it is written in it, or a path
        /// with `ñ` or an accent would name a file that does not exist.
        /// </summary>
        private (bool Success, string Log) RunCommands(Func<string, IReadOnlyList<Command>> commands, string expected)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"larva-link-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var environment = Developer();
                var list = commands(directory);
                if (environment is not null && list.All(c => Find(environment, c.Program + ".exe") is not null))
                {
                    var log = new System.Text.StringBuilder();
                    foreach (var command in list)
                    {
                        var (code, output) = Run(Find(environment, command.Program + ".exe")!, command.Arguments, environment);
                        log.Append(output);
                        if (code != 0)
                            return (false, log.ToString().Trim());
                    }
                    return (File.Exists(expected), log.ToString().Trim());
                }
                string script = Path.Combine(directory, "build.bat");
                var text = new System.Text.StringBuilder("@echo off\n");
                if (environment is null)
                    text.Append($"call {Quote(VcVars)} >nul || exit /b 1\n");
                foreach (var command in list)
                    text.Append($"{command.Program} {string.Join(" ", command.Arguments.Select(ScriptArgument))} || exit /b 1\n");
                File.WriteAllText(script, text.ToString(), ConsoleEncoding());
                var (scriptCode, scriptLog) = Run("cmd.exe", ["/d", "/c", script], environment);
                return (scriptCode == 0 && File.Exists(expected), scriptLog.Trim());
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }

        /// <summary>An argument on a batch file's command line: quoted when a space or a special character would split or change it.</summary>
        private static string ScriptArgument(string argument) =>
            argument.Length > 0 && argument.All(c => c > ' ' && c < 127 && c is not ('"' or '&' or '|' or '<' or '>' or '^' or '%' or '(' or ')'))
                ? argument : Quote(argument);

        private static string Quote(string path) => $"\"{path}\"";

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetConsoleOutputCP();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        private static readonly Lazy<bool> codePagesRegistered = new(() =>
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return true;
        });

        /// <summary>
        /// The encoding cmd and the MSVC tools use for what they read (batch files) and write (their output): the
        /// console's output code page, or the OEM code page without a console (cmd gets one with it).
        /// </summary>
        private static System.Text.Encoding ConsoleEncoding()
        {
            _ = codePagesRegistered.Value;
            uint codePage = GetConsoleOutputCP();
            if (codePage == 0)
                codePage = GetOEMCP();
            try
            {
                return System.Text.Encoding.GetEncoding((int)codePage);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                return new System.Text.UTF8Encoding(false);
            }
        }

        private static (int Code, string Output) Run(string program, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
        {
            var encoding = ConsoleEncoding();
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = encoding, StandardErrorEncoding = encoding };
            if (environment is not null)
            {
                start.Environment.Clear();
                foreach (var (name, value) in environment)
                    start.Environment[name] = value;
            }
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output + errors.Result);
        }
    }
}
