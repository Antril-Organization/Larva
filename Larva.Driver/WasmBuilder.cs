using System.Diagnostics;
using Larva.IR.Linking;
using Larva.Llvm;
using Larva.Wasm;

namespace Larva.Driver
{
    /// <summary>
    /// A linked program → WebAssembly for Node and browsers: <c>NAME.wasm</c>, <c>NAME.js</c> (its runtime and loader) and
    /// <c>NAME.html</c>. Two back ends: Larva's own (<see cref="WasmBackend"/>: 32-bit memory, no external tools) and LLVM
    /// (64-bit memory, optimized, linked with <c>wasm-ld</c>). Both import the same JavaScript runtime.
    /// </summary>
    public static class WasmBuilder
    {
        public const string LinkerVariable = "LARVA_WASM_LD";

        /// <summary><c>wasm-ld</c>: <see cref="LinkerVariable"/>, the PATH, or a standard LLVM installation.</summary>
        public static string? FindLinker()
        {
            if (Environment.GetEnvironmentVariable(LinkerVariable) is { Length: > 0 } configured)
                return File.Exists(configured) ? configured : null;
            string exe = OperatingSystem.IsWindows() ? "wasm-ld.exe" : "wasm-ld";
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe)))
                    return Path.Combine(dir, exe);
            }
            foreach (string candidate in new[] { @"C:\Program Files\LLVM\bin\wasm-ld.exe", "/usr/bin/wasm-ld", "/usr/local/bin/wasm-ld", "/opt/homebrew/opt/llvm/bin/wasm-ld" })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        public const string MissingLinker = "no wasm-ld: install LLVM (it comes with it), or set LARVA_WASM_LD to its path";

        /// <summary>Builds the program; the files written, or why it failed.</summary>
        public static (IReadOnlyList<string>? Files, string Message) Build(LinkResult linked, string output, ExecutableOptions? options = null, bool llvm = false)
        {
            options ??= new ExecutableOptions();
            string wasmPath = Path.ChangeExtension(Path.GetFullPath(output), ".wasm");
            if (!llvm)
            {
                var own = WasmBackend.Compile(linked, new WasmOptions { Compilation = options.Compilation, BoundsCheckElimination = options.BoundsCheckElimination });
                if (!own.Success)
                    return (null, own.ToString());
                return (own.Program!.Write(wasmPath), "Larva back end");
            }

            string? linker = FindLinker();
            if (linker is null)
                return (null, MissingLinker);
            LinkResult program;
            try
            {
                program = ExecutableEntry.AddWrappers(linked);
            }
            catch (InvalidOperationException e)
            {
                return (null, e.Message);
            }
            var compiled = LlvmBackend.Compile(program, new LlvmOptions
            {
                Compilation = options.Compilation,
                OptimizationLevel = options.OptimizationLevel,
                BoundsCheckElimination = options.BoundsCheckElimination,
                Triple = LlvmBackend.Wasm64,
            });
            if (!compiled.Success)
                return (null, compiled.ToString());
            string work = Path.Combine(Path.GetTempPath(), $"larva-wasm-{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            try
            {
                string obj = Path.Combine(work, "program.o");
                using (var llvmProgram = compiled.Program!)
                    llvmProgram.EmitObject(obj);
                Directory.CreateDirectory(Path.GetDirectoryName(wasmPath)!);
                var start = new ProcessStartInfo(linker) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string argument in new[]
                {
                    "-mwasm64", "--no-entry", "--allow-undefined", "--export-table", "--stack-first", "-z", "stack-size=1048576",
                    "--gc-sections", "--strip-all", "-O2",
                    "--export=larva_entry", "--export=larva_entry_f64", "--export=larva_entry_info",
                    "--export=larva_exception", "--export=larva_exception_type",
                    "--export=__stack_pointer", "--export=__stack_high", "--export=__data_end", "--export=__heap_base", "--export-if-defined=larva_wasm_heap",
                    "-o", wasmPath, obj,
                })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var errors = process.StandardError.ReadToEndAsync();
                string log = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 || !File.Exists(wasmPath))
                    return (null, $"wasm-ld failed\n{log}{errors.Result}");
                string folder = Path.GetDirectoryName(wasmPath)!;
                string name = Path.GetFileNameWithoutExtension(wasmPath);
                string js = Path.Combine(folder, name + ".js"), html = Path.Combine(folder, name + ".html");
                File.WriteAllText(js, WasmBackend.Glue(name + ".wasm", options.Compilation.Gc, abi: "c64"));
                File.WriteAllText(html, WasmBackend.Page(name));
                return ([wasmPath, js, html], $"LLVM back end, O{options.OptimizationLevel}");
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            }
        }
    }
}
