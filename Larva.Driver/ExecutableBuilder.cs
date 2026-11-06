using Larva.IR;
using Larva.IR.Linking;
using Larva.Llvm;
using Larva.Native;
using Larva.X64;

namespace Larva.Driver
{
    public enum Backend : byte { Llvm, X64 }

    public sealed record ExecutableOptions
    {
        public CompilationOptions Compilation { get; init; } = CompilationOptions.Default;
        public Backend Backend { get; init; } = Backend.Llvm;
        /// <summary>LLVM only: 0–3.</summary>
        public int OptimizationLevel { get; init; } = 2;
        public bool BoundsCheckElimination { get; init; } = true;
        /// <summary>LLVM only.</summary>
        public PollyOptions? Polly { get; init; }
        /// <summary>LLVM only: the processor (<see cref="LlvmOptions.Cpu"/>): generic, native, or an LLVM name.</summary>
        public string? Cpu { get; init; }
    }

    /// <summary>
    /// A linked Larva program → a native executable: entry wrappers for the C runtime's <c>main</c>, Larva symbols prefixed
    /// with <c>larva.</c>, the object from the chosen back end, and MSVC to compile the runtime and link. Shared by every
    /// front end's command line.
    /// </summary>
    public static class ExecutableBuilder
    {
        public const string SymbolPrefix = "larva.";

        /// <summary>
        /// Builds a DLL that C code loads: the program's <c>[Export]</c> functions under their C names (linked with
        /// <see cref="LinkOptions.CLibrary"/>), with the runtime inside.
        /// </summary>
        public static (bool Success, string Message) BuildLibrary(LinkResult linked, string output, ExecutableOptions? options = null) =>
            Build(linked, output, options, library: true);

        /// <summary>Builds <paramref name="output"/>; on failure the message says why (missing toolchain, compile or link errors).</summary>
        public static (bool Success, string Message) Build(LinkResult linked, string output, ExecutableOptions? options = null) =>
            Build(linked, output, options, library: false);

        private static (bool Success, string Message) Build(LinkResult linked, string output, ExecutableOptions? options, bool library)
        {
            options ??= new ExecutableOptions();
            var toolchain = Toolchain.Find();
            if (toolchain is null)
                return (false, Toolchain.Missing);
            LinkResult program;
            try
            {
                program = library ? ExecutableEntry.AddLibraryStubs(linked) : ExecutableEntry.AddWrappers(linked, gc: options.Compilation.Gc);
            }
            catch (InvalidOperationException e)
            {
                return (false, e.Message);
            }

            string directory = Path.Combine(Path.GetTempPath(), $"larva-exe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                string obj = Path.Combine(directory, "program.obj");
                List<string> objects = [obj];
                if (options.Backend == Backend.X64)
                {
                    using var generating = PhaseTimer.Start("x64: generate");
                    var direct = X64Backend.Compile(program, new X64Options
                    {
                        Compilation = options.Compilation,
                        BoundsCheckElimination = options.BoundsCheckElimination,
                        SymbolPrefix = SymbolPrefix,
                    });
                    if (!direct.Success)
                        return (false, direct.ToString());
                    direct.Program!.EmitObject(obj);
                }
                else
                {
                    // LARVA_DUMP=folder: the optimized LLVM IR and assembly of the whole program, to read what the code became.
                    if (Environment.GetEnvironmentVariable("LARVA_DUMP") is { Length: > 0 } dump)
                    {
                        var whole = LlvmBackend.Compile(program, new LlvmOptions
                        {
                            Compilation = options.Compilation,
                            OptimizationLevel = options.OptimizationLevel,
                            BoundsCheckElimination = options.BoundsCheckElimination,
                            Cpu = options.Cpu,
                            SymbolPrefix = SymbolPrefix,
                            PreciseStackRoots = false,
                        });
                        if (whole.Success)
                        {
                            Directory.CreateDirectory(dump);
                            string stem = Path.Combine(dump, Path.GetFileNameWithoutExtension(output));
                            File.WriteAllText(stem + ".ll", whole.Program!.IrText);
                            File.WriteAllText(stem + ".s", whole.Program!.Assembly());
                            whole.Program!.Dispose();
                        }
                    }
                    // Large programs are optimized and emitted on several threads, one object each.
                    var (compiled, error) = LlvmBackend.CompileObjects(program, new LlvmOptions
                    {
                        Compilation = options.Compilation,
                        OptimizationLevel = options.OptimizationLevel,
                        BoundsCheckElimination = options.BoundsCheckElimination,
                        Polly = options.Polly,
                        Cpu = options.Cpu,
                        SymbolPrefix = SymbolPrefix,
                        // The C runtime finds handles on the stack and in the registers itself.
                        PreciseStackRoots = false,
                    }, directory);
                    if (compiled is null)
                        return (false, error ?? "LLVM failed");
                    objects = compiled;
                }

                using var linking = PhaseTimer.Start("link (runtime and linker)");
                var exports = program.Merged!.Functions.Select(NativeSymbols.ExportName).OfType<string>().ToList();
                var (success, log) = library ? toolchain.LinkDll(objects, output, exports) : toolchain.Link(objects, output);
                if (!success)
                    return (false, $"linking failed\n{log}");
                string backend = options.Backend == Backend.X64 ? "x64 back end" : $"llvm back end, O{options.OptimizationLevel}";
                return (true, library ? $"{output} ({backend}, exports {string.Join(", ", exports)})" : $"{output} ({backend}, entry {linked.Entry})");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }
}
