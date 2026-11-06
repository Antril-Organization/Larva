using System.Diagnostics;

namespace Larva.Native
{
    /// <summary>A Unix-like target: the operating system (and so its file formats) and the processor.</summary>
    public enum PosixSystem : byte { Linux, MacOS }

    /// <summary>
    /// Links executables and shared libraries for Linux and macOS with the system C compiler (<c>cc</c>: gcc or clang),
    /// which also compiles the runtime (<c>larva_rt.c</c>). On Linux or macOS it runs directly; on Windows it runs inside
    /// WSL (<c>wsl.exe -e cc …</c>, paths translated to <c>/mnt/…</c>), so Linux programs can be built and run from Windows.
    /// </summary>
    public sealed class PosixToolchain
    {
        private readonly bool viaWsl;

        public PosixSystem System { get; }

        private PosixToolchain(PosixSystem system, bool viaWsl)
        {
            System = system;
            this.viaWsl = viaWsl;
        }

        public string LibraryExtension => System == PosixSystem.MacOS ? ".dylib" : ".so";

        /// <summary>The toolchain for <paramref name="system"/>, if this machine can build for it; otherwise why not.</summary>
        public static (PosixToolchain? Toolchain, string? Problem) Find(PosixSystem system)
        {
            if (OperatingSystem.IsLinux() && system == PosixSystem.Linux || OperatingSystem.IsMacOS() && system == PosixSystem.MacOS)
                return Run(false, "cc", ["--version"]).Code == 0 ? (new PosixToolchain(system, false), null) : (null, "no C compiler: install gcc or clang (`cc`)");
            if (OperatingSystem.IsWindows() && system == PosixSystem.Linux)
            {
                var (code, output) = Run(true, "cc", ["--version"]);
                if (code == 0)
                    return (new PosixToolchain(system, true), null);
                return (null, output.Contains("cc", StringComparison.Ordinal) && code == 127
                    ? "WSL has no C compiler: run `sudo apt install build-essential` in it"
                    : "Linux programs are built with WSL and a distribution (`wsl --install -d Ubuntu`, then `sudo apt install build-essential`)");
            }
            return (null, system == PosixSystem.MacOS ? "macOS programs are built on a Mac (with Xcode's command line tools)" : "Linux programs are built on Linux, or on Windows with WSL");
        }

        /// <summary>A path as the compiler sees it (inside WSL: <c>C:\x</c> is <c>/mnt/c/x</c>).</summary>
        public string Path(string path)
        {
            string full = global::System.IO.Path.GetFullPath(path);
            if (!viaWsl)
                return full;
            return $"/mnt/{char.ToLowerInvariant(full[0])}{full[2..].Replace('\\', '/')}";
        }

        private string RuntimeSourceFile(string directory)
        {
            string source = global::System.IO.Path.Combine(directory, "larva_rt.c");
            File.WriteAllText(source, Toolchain.RuntimeSource());
            return source;
        }

        /// <summary>A static program: <paramref name="objects"/> plus the runtime, compiled in.</summary>
        public (bool Success, string Log) LinkExecutable(IReadOnlyList<string> objects, string output, string work)
        {
            string runtime = RuntimeSourceFile(work);
            return Cc([.. objects.Select(Path), Path(runtime), "-O2", "-w", "-o", Path(output), .. SystemLibraries], output);
        }

        /// <summary>The shared runtime (<c>liblarva_rt.so</c> / <c>.dylib</c>) in <paramref name="folder"/>.</summary>
        public (bool Success, string Log) BuildRuntime(string folder, string work)
        {
            string runtime = RuntimeSourceFile(work);
            string output = global::System.IO.Path.Combine(folder, "liblarva_rt" + LibraryExtension);
            return Cc([Path(runtime), "-O2", "-w", "-fPIC", "-DLARVA_SHARED_RUNTIME", .. SharedFlags("liblarva_rt" + LibraryExtension), "-o", Path(output), .. SystemLibraries], output);
        }

        /// <summary>A Larva shared library, linked against the runtime and the libraries it calls (all found next to it at run time).</summary>
        public (bool Success, string Log) LinkLibrary(IReadOnlyList<string> objects, string output, IReadOnlyList<string> libraries) =>
            Cc([.. objects.Select(Path), .. SharedFlags(global::System.IO.Path.GetFileName(output)), .. libraries.Select(Path), .. Rpath, "-o", Path(output)], output);

        /// <summary>A program that loads the shared runtime and Larva libraries (its <c>main</c> hands over to the runtime).</summary>
        public (bool Success, string Log) LinkProgram(IReadOnlyList<string> objects, string output, IReadOnlyList<string> libraries, string work)
        {
            string start = global::System.IO.Path.Combine(work, "larva_start.c");
            File.WriteAllText(start, """
                #include <stdint.h>
                int larva_run_main_args(int, char **, void *, void *, void *);
                int64_t larva_entry(int64_t, int64_t, int64_t, int64_t);
                double larva_entry_f64(int64_t, int64_t, int64_t, int64_t);
                int64_t larva_entry_info(void);
                int main(int argc, char **argv) { return larva_run_main_args(argc, argv, (void *)larva_entry, (void *)larva_entry_f64, (void *)larva_entry_info); }
                """);
            return Cc([.. objects.Select(Path), Path(start), "-O2", "-w", .. libraries.Select(Path), .. Rpath, "-o", Path(output)], output);
        }

        private string[] SystemLibraries => System == PosixSystem.Linux ? ["-lpthread", "-lm"] : [];

        private string[] SharedFlags(string name) => System == PosixSystem.MacOS
            ? ["-dynamiclib", "-undefined", "dynamic_lookup", $"-Wl,-install_name,@rpath/{name}"]
            : ["-shared", "-fPIC"];

        /// <summary>Libraries are looked up next to whatever loads them.</summary>
        private string[] Rpath => System == PosixSystem.MacOS ? ["-Wl,-rpath,@loader_path"] : ["-Wl,-rpath,$ORIGIN"];

        private (bool Success, string Log) Cc(string[] arguments, string expected)
        {
            var (code, output) = Run(viaWsl, "cc", arguments);
            return (code == 0 && File.Exists(expected), output.Trim());
        }

        /// <summary>Runs a program built for this target (through WSL on Windows): its exit code and output.</summary>
        public (int Code, string Output) Execute(string program, IEnumerable<string> arguments, IEnumerable<(string Name, string Value)>? environment = null)
        {
            if (!viaWsl)
                return Run(false, program, arguments, environment);
            // WSL passes variables named in WSLENV; the program runs from its own folder.
            var variables = environment?.ToList() ?? [];
            return Run(true, Path(program), arguments, variables);
        }

        private static (int Code, string Output) Run(bool wsl, string program, IEnumerable<string> arguments, IEnumerable<(string Name, string Value)>? environment = null)
        {
            var start = new ProcessStartInfo(wsl ? "wsl.exe" : program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (wsl)
            {
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add(program);
            }
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            var names = new List<string>();
            foreach (var (name, value) in environment ?? [])
            {
                start.Environment[name] = value;
                names.Add(name);
            }
            if (wsl && names.Count > 0)
                start.Environment["WSLENV"] = string.Join(":", names);
            try
            {
                using var process = Process.Start(start)!;
                var errors = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, output + errors.Result);
            }
            catch (global::System.ComponentModel.Win32Exception e)
            {
                return (127, e.Message);
            }
        }
    }
}
