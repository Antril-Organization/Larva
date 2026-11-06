using System.Runtime.InteropServices;
using System.Text;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Linking;
using Larva.IR.Lowering;
using LLVMSharp.Interop;
using Larva.Native;

namespace Larva.Llvm
{
    public sealed record LlvmOptions
    {
        public static readonly LlvmOptions Default = new();

        /// <summary>GC mode and checks. Generics are always specialized for native code.</summary>
        public CompilationOptions Compilation { get; init; } = CompilationOptions.Default;

        /// <summary>0 = no optimization; 1-3 = LLVM's <c>default&lt;On&gt;</c> pipelines.</summary>
        public int OptimizationLevel { get; init; } = 2;

        /// <summary>Target triple; the host's when <c>null</c>.</summary>
        public string? Triple { get; init; }

        /// <summary>
        /// The processor the code is made for: <c>null</c> or <c>generic</c> (any of the triple's: portable), <c>native</c>
        /// (this machine's, with every feature it has: AVX2 and so on; the program may not run on an older one), or an LLVM
        /// processor name (<c>x86-64-v3</c>, <c>znver3</c>…). WebAssembly ignores it.
        /// </summary>
        public string? Cpu { get; init; }

        /// <summary>Drop the bounds checks <see cref="BoundsAnalysis"/> proves unnecessary.</summary>
        public bool BoundsCheckElimination { get; init; } = true;

        /// <summary>
        /// Prefix for the symbols of Larva functions (not for <c>larva_*</c> names, reserved for the runtime), so objects
        /// linked into executables cannot clash with C runtime symbols such as <c>main</c>. None for the JIT.
        /// </summary>
        public string? SymbolPrefix { get; init; }

        /// <summary>Polyhedral loop optimization with Polly (optional; needs an external <c>opt</c>). Off when <c>null</c>.</summary>
        public PollyOptions? Polly { get; init; }

        /// <summary>
        /// Keep every local that may hold a gc handle in a stack slot the collector finds through stack maps (the JIT's
        /// managed runtime needs them). The C runtime scans the stack and the registers itself, as a non-moving collector
        /// can: with false, handles live in registers and are not reloaded after every call.
        /// </summary>
        public bool PreciseStackRoots { get; init; } = true;
    }

    /// <summary>
    /// Polly is not part of the LLVM library LLVMSharp ships, and the C API cannot load pass plugins, so the optimization
    /// pipeline runs in an external <c>opt</c> of the same LLVM version: either one built with Polly, or any <c>opt</c>
    /// plus the <c>LLVMPolly</c> plugin. The module goes out and comes back as textual IR.
    /// </summary>
    /// <param name="Tool"><c>opt</c> executable (looked up on PATH when not a path).</param>
    /// <param name="Plugin">Path to <c>LLVMPolly.dll</c> / <c>.so</c> when Polly is a plugin of that <c>opt</c>.</param>
    /// <param name="Arguments">Extra arguments, e.g. <c>-polly-vectorizer=stripmine</c> or <c>-polly-parallel</c>.</param>
    public sealed record PollyOptions(string Tool = "opt", string? Plugin = null, IReadOnlyList<string>? Arguments = null);

    public sealed record LlvmResult(LlvmProgram? Program, IReadOnlyList<TypeError> TypeErrors, IReadOnlyList<BorrowError> Diagnostics, string? Error)
    {
        public bool Success => Program is not null;

        /// <summary>Functions left out because they use a type this back end lacks (and what reaches them), with why.</summary>
        public IReadOnlyDictionary<string, string> Skipped { get; init; } = new Dictionary<string, string>();


        public override string ToString() => Success ? "ok" : string.Join("\n",
            TypeErrors.Select(e => e.ToString()).Concat(Diagnostics.Select(d => d.ToString())).Append(Error ?? ""));
    }

    /// <summary>
    /// Native compilation through LLVM (in process, via LLVMSharp): IR → monomorphization (all generics specialized) →
    /// <see cref="LlvmCodegen"/> → LLVM verifier → optimization pipeline → object code, assembly, or JIT execution.
    /// </summary>
    public static unsafe class LlvmBackend
    {
        static LlvmBackend()
        {
            LLVM.LinkInMCJIT();
            LLVM.InitializeX86TargetInfo();
            LLVM.InitializeX86Target();
            LLVM.InitializeX86TargetMC();
            LLVM.InitializeX86AsmPrinter();
            LLVM.InitializeX86AsmParser();
            LLVM.InitializeWebAssemblyTargetInfo();
            LLVM.InitializeWebAssemblyTarget();
            LLVM.InitializeWebAssemblyTargetMC();
            LLVM.InitializeWebAssemblyAsmPrinter();
            LLVM.InitializeAArch64TargetInfo();
            LLVM.InitializeAArch64Target();
            LLVM.InitializeAArch64TargetMC();
            LLVM.InitializeAArch64AsmPrinter();
            // LARVA_LLVM_ARGS: options for LLVM itself (as `llc` takes them), to try code generation settings.
            if (Environment.GetEnvironmentVariable("LARVA_LLVM_ARGS") is { Length: > 0 } extra)
            {
                string[] args = ["larva", .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
                var pointers = args.Select(a => System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(a)).ToArray();
                fixed (nint* argv = pointers)
                    LLVM.ParseCommandLineOptions(args.Length, (sbyte**)argv, null);
            }
        }

        public const string LinuxX64 = "x86_64-unknown-linux-gnu", LinuxArm64 = "aarch64-unknown-linux-gnu";
        public const string MacX64 = "x86_64-apple-macosx11.0.0", MacArm64 = "arm64-apple-macosx11.0.0";

        /// <summary>
        /// Windows code unwinds with SEH and has precise stack maps; everywhere else (Linux, macOS, WebAssembly) exceptions
        /// are a flag checked after calls, and the slots holding handles stay in memory for a conservative stack scan.
        /// </summary>
        internal static bool UsesSeh(string? triple) => triple is null ? OperatingSystem.IsWindows() : triple.Contains("windows", StringComparison.Ordinal);

        /// <summary>WebAssembly with 64-bit memory: pointers keep the 8 bytes Larva's layouts give them.</summary>
        public const string Wasm64 = "wasm64-unknown-unknown";

        internal static bool IsWasm(string? triple) => triple?.StartsWith("wasm", StringComparison.Ordinal) == true;

        /// <summary>Checks and compiles a single, import-free module.</summary>
        public static LlvmResult Compile(IrModule module, LlvmOptions? options = null)
        {
            options ??= LlvmOptions.Default;
            if (module.HasImports)
                return new LlvmResult(null, [], [], "the module has imports: link it first");
            var compilation = options.Compilation with { Generics = GenericsMode.Specialize };
            var (typeErrors, diagnostics, ok) = Compiler.Verify(module, compilation);
            if (!ok)
                return new LlvmResult(null, typeErrors, diagnostics, null);
            return Generate(Monomorphizer.Run(module, GenericsMode.Specialize), options, string.IsNullOrEmpty(module.Name) ? "larva" : module.Name);
        }

        /// <summary>Compiles a linked program (objects were checked when they were compiled).</summary>
        public static LlvmResult Compile(LinkResult linked, LlvmOptions? options = null)
        {
            options ??= LlvmOptions.Default;
            if (!linked.Success || linked.Merged is null)
                return new LlvmResult(null, [], [], "the link failed");
            IrModule concrete;
            using (PhaseTimer.Start("monomorphize (specialize)"))
                concrete = Monomorphizer.Run(linked.Merged, GenericsMode.Specialize, linked.Roots);
            return Generate(concrete, options, "program");
        }

        /// <summary>Compiles a module already monomorphized (native images build theirs: see <c>NativeImages</c>).</summary>
        /// <summary>Whether this back end compiles <paramref name="function"/> (it lacks f256).</summary>
        /// <summary>
        /// Compiles a linked program to object files in <paramref name="directory"/>: one, or (for a large program, without
        /// Polly) several made on as many threads (see <see cref="ParallelCodegen"/>).
        /// </summary>
        public static (List<string>? Objects, string? Error) CompileObjects(LinkResult linked, LlvmOptions options, string directory, string name = "program")
        {
            if (!linked.Success || linked.Merged is null)
                return (null, "the link failed");
            IrModule concrete;
            using (PhaseTimer.Start("monomorphize (specialize)"))
                concrete = Compilable.Stub(Monomorphizer.Run(linked.Merged, GenericsMode.Specialize, linked.Roots), Supports);
            int partitions = options.Polly is null && options.OptimizationLevel > 0 ? ParallelCodegen.Partitions(concrete.Functions.Count(f => !f.IsDeclaration)) : 1;
            try
            {
                string triple = options.Triple ?? LLVMTargetRef.DefaultTriple;
                var compilable = Compilable.Of(concrete, Supports);
                LlvmCodegen Codegen(LLVMContextRef? context = null, Func<int, bool>? defines = null) =>
                    new(concrete, options.Compilation.Gc, options.Compilation.BorrowCheck == BorrowCheckMode.Enforce, name,
                        options.BoundsCheckElimination, options.SymbolPrefix, flagExceptions: !UsesSeh(options.Triple), context, defines, compilable,
                        inlinePolls: !IsWasm(options.Triple), stackRoots: options.PreciseStackRoots);
                if (partitions > 1)
                {
                    using var parallel = PhaseTimer.Start($"llvm: build, optimize and emit ({partitions} threads, {concrete.Functions.Length} functions)");
                    return (ParallelCodegen.Emit(concrete, partitions, (context, defines) => Codegen(context, defines), triple, options.OptimizationLevel, directory, name, options.Cpu), null);
                }
                var codegen = Codegen();
                using (PhaseTimer.Start($"llvm: build IR ({concrete.Functions.Length} functions)"))
                    codegen.Generate();
                using var program = new LlvmProgram(codegen.Module, triple, options.OptimizationLevel, options.Polly, options.Cpu);
                string path = Path.Combine(directory, name + ".obj");
                using (PhaseTimer.Start("llvm: emit object"))
                    program.EmitObject(path);
                return ([path], null);
            }
            catch (LlvmBackendException e)
            {
                return (null, e.Message);
            }
        }

        public static bool Supports(IrFunction function, IrModule module) => !Compilable.Uses(function, module, _ => false);

        public static LlvmResult CompileConcrete(IrModule concrete, LlvmOptions? options = null, string name = "program") =>
            Generate(concrete, options ?? LlvmOptions.Default, name);

        private static LlvmResult Generate(IrModule concrete, LlvmOptions options, string name)
        {
            concrete = Compilable.Stub(concrete, Supports);
            try
            {
                var codegen = new LlvmCodegen(concrete, options.Compilation.Gc, options.Compilation.BorrowCheck == BorrowCheckMode.Enforce, name,
                    options.BoundsCheckElimination, options.SymbolPrefix, flagExceptions: !UsesSeh(options.Triple), inlinePolls: !IsWasm(options.Triple), stackRoots: options.PreciseStackRoots);
                using (PhaseTimer.Start($"llvm: build IR ({concrete.Functions.Length} functions)"))
                    codegen.Generate();
                var program = new LlvmProgram(codegen.Module, options.Triple ?? LLVMTargetRef.DefaultTriple, options.OptimizationLevel, options.Polly, options.Cpu)
                {
                    BoundsChecks = codegen.BoundsChecks,
                    BoundsChecksEliminated = codegen.BoundsChecksEliminated,
                };
                return new LlvmResult(program, [], [], null) { Skipped = codegen.Skipped };
            }
            catch (LlvmBackendException e)
            {
                return new LlvmResult(null, [], [], e.Message);
            }
        }

        internal static string Text(sbyte* message)
        {
            if (message == null)
                return "";
            string text = new(message);
            LLVM.DisposeMessage(message);
            return text;
        }
    }

    /// <summary>A verified, optimized LLVM module for one target, ready to be emitted or run.</summary>
    public sealed unsafe class LlvmProgram : IDisposable
    {
        private readonly LLVMTargetMachineRef machine;

        public LLVMModuleRef Module { get; private set; }
        public string Triple { get; }

        /// <summary>Bounds checks emitted / removed by bounds-check elimination (before LLVM's own optimizations).</summary>
        public int BoundsChecks { get; init; }
        public int BoundsChecksEliminated { get; init; }

        /// <summary>Whether the optimization pipeline ran with Polly.</summary>
        public bool Polly { get; }

        internal LlvmProgram(LLVMModuleRef module, string triple, int optimization, PollyOptions? polly = null, string? cpu = null)
        {
            Module = module;
            Triple = triple;
            var target = LLVMTargetRef.GetTargetFromTriple(triple);
            // WebAssembly: bulk memory (memory.copy/fill), sign extension and saturating float conversions (every current
            // browser and Node have them). Not SIMD: its two-lane i64 loops ran slower in V8 than unrolled scalar ones.
            // LARVA_WASM_FEATURES replaces the list (to measure one without another).
            string features = !LlvmBackend.IsWasm(triple) ? ""
                : Environment.GetEnvironmentVariable("LARVA_WASM_FEATURES") ?? "+bulk-memory,+sign-ext,+nontrapping-fptoint,+mutable-globals";
            // The processor: generic (portable) unless one is asked for; `native` is this machine's, with its features.
            string processor = "generic";
            if (!LlvmBackend.IsWasm(triple) && cpu is { Length: > 0 } and not "generic")
            {
                if (cpu == "native")
                {
                    processor = Host(LLVM.GetHostCPUName());
                    features = Host(LLVM.GetHostCPUFeatures());
                }
                else
                    processor = cpu;
            }
            machine = target.CreateTargetMachine(triple, processor, features,
                optimization == 0 ? LLVMCodeGenOptLevel.LLVMCodeGenLevelNone : LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
                // Position-independent code for ELF and Mach-O (executables are PIE, libraries shared).
                LlvmBackend.UsesSeh(triple) || LlvmBackend.IsWasm(triple) ? LLVMRelocMode.LLVMRelocDefault : LLVMRelocMode.LLVMRelocPIC, LLVMCodeModel.LLVMCodeModelDefault);
            module.Target = triple;
            var layout = LLVM.CreateTargetDataLayout(machine);
            LLVM.SetModuleDataLayout(module, layout);

            using (PhaseTimer.Start("llvm: verify"))
                Verify("after code generation");
            if (LlvmBackend.IsWasm(triple) && Environment.GetEnvironmentVariable("LARVA_WASM_HEAP") != "0")
                LinkWasmHeap();
            if (optimization > 0 && LlvmBackend.IsWasm(triple) && Environment.GetEnvironmentVariable("LARVA_ROOT_TBAA") != "0")
                StackMaps.SeparateRootsFromHeap(Module);
            using var optimizing = PhaseTimer.Start($"llvm: optimize (O{optimization}{(polly is not null ? ", Polly" : "")})");
            if (optimization > 0 && polly is not null)
            {
                RunPolly(polly, Math.Clamp(optimization, 1, 3));
                Polly = true;
            }
            else if (optimization > 0)
            {
                RunPasses($"default<O{Math.Clamp(optimization, 1, 3)}>");
            }
            if (!LlvmBackend.UsesSeh(triple))
                StackMaps.KeepInMemory(module);
            else
                StackMaps.Lower(module);
            Verify(optimization > 0 ? "after optimization" : "after stack maps");
        }

        /// <summary>The module as LLVM IR text (<c>.ll</c>).</summary>
        public string IrText => Module.PrintToString();

        public void EmitObject(string path) => Emit(path, LLVMCodeGenFileType.LLVMObjectFile);

        public void EmitAssembly(string path) => Emit(path, LLVMCodeGenFileType.LLVMAssemblyFile);

        public void EmitIr(string path) => File.WriteAllText(path, IrText);

        /// <summary>Native assembly listing for the target.</summary>
        public string Assembly()
        {
            string path = Path.Combine(Path.GetTempPath(), $"larva-{Guid.NewGuid():N}.s");
            try
            {
                EmitAssembly(path);
                return File.ReadAllText(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Compiles the module to machine code in this process, binding <c>larva_*</c> to <see cref="NativeRuntime"/>.</summary>
        public JitSession Jit() => new(LLVM.CloneModule(Module));

        private void Emit(string path, LLVMCodeGenFileType kind)
        {
            if (!machine.TryEmitToFile(Module, path, kind, out string error))
                throw new LlvmBackendException($"cannot emit {path}: {error}");
        }

        private void Verify(string when)
        {
            if (!Module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out string message))
                throw new LlvmBackendException($"LLVM verifier failed {when}: {message}\n{IrText}");
        }

        /// <summary>Runs <c>default&lt;On&gt;</c> with Polly enabled in an external <c>opt</c> and replaces the module.</summary>
        private void RunPolly(PollyOptions polly, int level)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"larva-polly-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                string input = Path.Combine(directory, "in.ll"), output = Path.Combine(directory, "out.ll");
                File.WriteAllText(input, IrText);
                var start = new System.Diagnostics.ProcessStartInfo(polly.Tool)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                if (polly.Plugin is not null)
                    start.ArgumentList.Add($"-load-pass-plugin={polly.Plugin}");
                start.ArgumentList.Add($"-passes=default<O{level}>");
                start.ArgumentList.Add("-polly");
                foreach (var argument in polly.Arguments ?? [])
                    start.ArgumentList.Add(argument);
                foreach (var argument in new[] { "-S", "-o", output, input })
                    start.ArgumentList.Add(argument);

                System.Diagnostics.Process? process;
                try
                {
                    process = System.Diagnostics.Process.Start(start);
                }
                catch (System.ComponentModel.Win32Exception e)
                {
                    throw new LlvmBackendException($"Polly needs LLVM's `opt` ({polly.Tool}): {e.Message}");
                }
                using (process)
                {
                    var errors = process!.StandardError.ReadToEndAsync();
                    process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0 || !File.Exists(output))
                        throw new LlvmBackendException($"`{polly.Tool}` with Polly failed (exit {process.ExitCode}): {errors.Result.Trim()}");
                }
                Replace(File.ReadAllText(output));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>
        /// WebAssembly: gc allocation without a call to JavaScript (Runtime/wasm_heap.ll, linked in before optimization so
        /// its fast path is inlined where the program allocates). Only when the program allocates gc objects.
        /// </summary>
        private void LinkWasmHeap()
        {
            string[] provided = ["larva_gc_alloc", "larva_gc_array", "larva_array_new"];
            if (!provided.Any(name => Module.GetNamedFunction(name) is { Handle: not 0 } f && f.BasicBlocksCount == 0))
                return;
            using var stream = typeof(LlvmProgram).Assembly.GetManifestResourceStream("wasm_heap.ll")!;
            using var reader = new StreamReader(stream);
            var bytes = Encoding.UTF8.GetBytes(reader.ReadToEnd());
            var name = "wasm_heap "u8.ToArray();
            LLVMOpaqueModule* parsed;
            sbyte* error;
            fixed (byte* data = bytes)
            fixed (byte* bufferName = name)
            {
                var buffer = LLVM.CreateMemoryBufferWithMemoryRangeCopy((sbyte*)data, (nuint)bytes.Length, (sbyte*)bufferName);
                if (LLVM.ParseIRInContext(Module.Context, buffer, &parsed, &error) != 0)
                    throw new LlvmBackendException($"wasm_heap.ll: {LlvmBackend.Text(error)}");
            }
            LLVMModuleRef heap = parsed;
            heap.Target = Triple;
            LLVM.SetModuleDataLayout(heap, LLVM.GetModuleDataLayout(Module));
            if (LLVM.LinkModules2(Module, heap) != 0)
                throw new LlvmBackendException("wasm_heap.ll does not link with the program");
            // The program's own: inlined where it can be, dropped where unused.
            foreach (string function in provided)
                if (Module.GetNamedFunction(function) is { Handle: not 0 } f && f.BasicBlocksCount > 0)
                    f.Linkage = LLVMLinkage.LLVMInternalLinkage;
        }

        /// <summary>Replaces the module by one parsed from IR text, in the same context.</summary>
        private void Replace(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var name = new byte[] { (byte)'p', (byte)'o', (byte)'l', (byte)'l', (byte)'y', 0 };
            LLVMOpaqueModule* parsed;
            sbyte* error;
            fixed (byte* data = bytes)
            fixed (byte* bufferName = name)
            {
                var buffer = LLVM.CreateMemoryBufferWithMemoryRangeCopy((sbyte*)data, (nuint)bytes.Length, (sbyte*)bufferName);
                if (LLVM.ParseIRInContext(Module.Context, buffer, &parsed, &error) != 0)
                    throw new LlvmBackendException($"cannot read the IR from Polly's `opt`: {LlvmBackend.Text(error)}");
            }
            LLVMModuleRef replacement = parsed;
            replacement.Target = Triple;
            var old = Module;
            Module = replacement;
            old.Dispose();
        }

        /// <summary>A string LLVM made (the host's processor and features), copied and given back.</summary>
        private static string Host(sbyte* text)
        {
            try
            {
                return new string(text);
            }
            finally
            {
                LLVM.DisposeMessage(text);
            }
        }

        private void RunPasses(string pipeline)
        {
            var options = LLVM.CreatePassBuilderOptions();
            try
            {
                // Every handle a function keeps is marked as a GC root with a call, which the inliner counts like any
                // call: a higher threshold lets the small methods of classes (getters, List.Add) inline as they would
                // without the markers.
                LLVM.PassBuilderOptionsSetInlinerThreshold(options, 500);
                var bytes = Encoding.ASCII.GetBytes(pipeline + "\0");
                fixed (byte* text = bytes)
                {
                    var error = LLVM.RunPasses(Module, (sbyte*)text, machine, options);
                    if (error != null)
                        throw new LlvmBackendException($"LLVM passes failed: {LlvmBackend.Text(LLVM.GetErrorMessage(error))}");
                }
            }
            finally
            {
                LLVM.DisposePassBuilderOptions(options);
            }
        }

        public void Dispose()
        {
            LLVM.DisposeTargetMachine(machine);
            Module.Dispose();
        }
    }

    /// <summary>Machine code for a module, in this process. Function addresses can be called through function pointers.</summary>
    public sealed unsafe class JitSession : IDisposable
    {
        private readonly LLVMExecutionEngineRef engine;

        internal JitSession(LLVMModuleRef module)
        {
            var real = DefinePersonalityThunk(module);
            DefineWideDivision(module);

            LLVMMCJITCompilerOptions options;
            LLVM.InitializeMCJITCompilerOptions(&options, (nuint)sizeof(LLVMMCJITCompilerOptions));
            options.MCJMM = JitMemory.Create();
            LLVMOpaqueExecutionEngine* created;
            sbyte* error;
            if (LLVM.CreateMCJITCompilerForModule(&created, module, &options, (nuint)sizeof(LLVMMCJITCompilerOptions), &error) != 0)
                throw new LlvmBackendException($"cannot create the JIT: {LlvmBackend.Text(error)}");
            engine = created;

            foreach (var (name, address) in NativeRuntime.Symbols)
                Map(module, name, address);
            // Soft-float routines code generation calls (f16, f128): not in the module, so made known to the process.
            foreach (var (name, address) in NativeRuntime.SoftFloatSymbols)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(name + " ");
                fixed (byte* text = bytes)
                    LLVM.AddSymbol((sbyte*)text, (void*)address);
            }
            // COFF code loads runtime data through `__imp_` pointers; the JIT makes each one from the bare name's address,
            // looked up in the process's symbols.
            var flagName = System.Text.Encoding.ASCII.GetBytes("larva_poll_pending ");
            fixed (byte* text = flagName)
                LLVM.AddSymbol((sbyte*)text, (void*)NativeRuntime.Symbols["larva_poll_pending"]);
            var heapName = System.Text.Encoding.ASCII.GetBytes("larva_heap ");
            fixed (byte* text = heapName)
                LLVM.AddSymbol((sbyte*)text, (void*)NativeRuntime.Symbols["larva_heap"]);
            Map(module, "RaiseException", NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"), "RaiseException"));
            if (real.Handle != IntPtr.Zero)
                LLVM.AddGlobalMapping(engine, real, (void*)NativeLibrary.GetExport(NativeLibrary.Load("ntdll.dll"), "__C_specific_handler"));
        }

        private void Map(LLVMModuleRef module, string name, nint address)
        {
            var declaration = module.GetNamedFunction(name);
            if (declaration.Handle == IntPtr.Zero)
                declaration = module.GetNamedGlobal(name); // runtime data (the safepoint flag's pointer)
            if (declaration.Handle != IntPtr.Zero)
                LLVM.AddGlobalMapping(engine, declaration, (void*)address);
        }

        /// <summary>
        /// Unwind info refers to the personality routine with a 32-bit offset from the code, so the routine must live next to
        /// the JIT code: <c>__C_specific_handler</c> is defined here as a tail jump to the system one.
        /// </summary>
        private static LLVMValueRef DefinePersonalityThunk(LLVMModuleRef module)
        {
            var personality = module.GetNamedFunction("__C_specific_handler");
            if (personality.Handle == IntPtr.Zero || personality.BasicBlocksCount > 0)
                return default;
            var type = LLVM.GlobalGetValueType(personality);
            var real = module.AddFunction("larva_c_specific_handler_real", type);
            using var builder = LLVMBuilderRef.Create(module.Context);
            builder.PositionAtEnd(personality.AppendBasicBlock("entry"));
            var arguments = Enumerable.Range(0, 4).Select(i => personality.GetParam((uint)i)).ToArray();
            var call = builder.BuildCall2(type, real, arguments, "");
            LLVM.SetTailCallKind(call, LLVMTailCallKind.LLVMTailCallKindMustTail);
            builder.BuildRet(call);
            return real;
        }

        /// <summary>
        /// The routines code generation calls for 128-bit division and remainder (<c>__udivti3</c>, <c>__umodti3</c>,
        /// <c>__divti3</c>, <c>__modti3</c>): no C runtime here provides them to JIT code, so the module defines them, each
        /// as the operation on 256 bits (which LLVM expands into instructions itself) truncated back. Defined in IR, they
        /// get the same calling convention the calls use.
        /// </summary>
        private static void DefineWideDivision(LLVMModuleRef module)
        {
            var context = module.Context;
            var i128 = LLVMTypeRef.CreateInt(128);
            var i256 = LLVMTypeRef.CreateInt(256);
            var type = LLVMTypeRef.CreateFunction(i128, [i128, i128]);
            using var builder = LLVMBuilderRef.Create(context);
            foreach (var (name, signed, remainder) in new[] { ("__udivti3", false, false), ("__umodti3", false, true), ("__divti3", true, false), ("__modti3", true, true) })
            {
                if (module.GetNamedFunction(name).Handle != IntPtr.Zero)
                    continue;
                var function = module.AddFunction(name, type);
                builder.PositionAtEnd(function.AppendBasicBlock("entry"));
                LLVMValueRef Widen(LLVMValueRef value) => signed ? builder.BuildSExt(value, i256, "") : builder.BuildZExt(value, i256, "");
                var a = Widen(function.GetParam(0));
                var b = Widen(function.GetParam(1));
                var result = (signed, remainder) switch
                {
                    (false, false) => builder.BuildUDiv(a, b, ""),
                    (false, true) => builder.BuildURem(a, b, ""),
                    (true, false) => builder.BuildSDiv(a, b, ""),
                    _ => builder.BuildSRem(a, b, ""),
                };
                builder.BuildRet(builder.BuildTrunc(result, i128, ""));
            }
        }

        public nint Address(string function)
        {
            ulong address = engine.GetFunctionAddress(function);
            return address != 0 ? (nint)address : throw new LlvmBackendException($"no compiled function `{function}`");
        }

        public void Dispose() => engine.Dispose();
    }
}
