using System.Diagnostics;
using System.Text.RegularExpressions;
using Larva.IR;
using Larva.IR.Linking;
using Larva.Llvm;
using Larva.Native;
using Larva.X64;
using LLVMSharp.Interop;

namespace Larva.Tests;

/// <summary>
/// Ahead-of-time executables: the C runtime, the entry wrappers and symbol prefixes, and — when MSVC is installed —
/// executables linked from both back ends and run as separate processes.
/// </summary>
public sealed unsafe class AotTests : CheckedTest
{
    private static readonly Lazy<string> Folder = new(() => TestEnvironment.TempFolder("aot"));
    private static string Directory => Folder.Value;

    [Fact]
    public void C_runtime_defines_every_symbol_and_walks_stack_maps()
    {
        string source = Toolchain.RuntimeSource();
        // Functions are defined with a body; runtime data (the safepoint flag and its pointer) as a variable.
        var missing = NativeRuntime.Symbols.Keys.Where(name => !Regex.IsMatch(source, $@"\b{name}\s*\([^;]*\)\s*\{{")
            && !Regex.IsMatch(source, $@"LARVA_API (volatile int32_t|LarvaHeap) \*?{name}\b")).ToList();
        Check("defines every larva_* symbol the back ends call", missing.Count == 0, missing.Count == 0 ? $"{NativeRuntime.Symbols.Count} symbols" : string.Join(", ", missing));
        Check("finds stack maps in the image and walks frames with RtlVirtualUnwind",
            source.Contains(".llvm_st") && source.Contains("RtlVirtualUnwind") && source.Contains("RtlLookupFunctionEntry"));
        Check("reports uncaught Larva exceptions from main (SEH __try/__except)", source.Contains("__except") && source.Contains("0xE04C5641"));
    }

    [Fact]
    public void Entry_wrappers_are_added_and_larva_symbols_prefixed()
    {
        var linked = ExecutableEntry.AddWrappers(LinkerPrograms.LinkedProgram());
        Check("wrappers added as roots", linked.Merged!.Functions.TakeLast(3).Select(f => f.Name).SequenceEqual(["larva_entry", "larva_entry_f64", "larva_entry_info"])
            && linked.Roots.Count >= 4);

        var direct = X64Backend.Compile(linked, new X64Options { SymbolPrefix = "larva." });
        Check("x64: compiles with the wrappers", direct.Success, direct.ToString());
        if (direct.Success)
        {
            var symbols = ObjectSymbols(direct.Program!.ToObject());
            Check("x64: Larva symbols prefixed, wrappers and runtime not", symbols.Contains("larva.app::main") && symbols.Contains("larva_entry")
                && symbols.Contains("larva_entry_info") && !symbols.Contains("app::main"), string.Join(", ", symbols.Where(s => !s.StartsWith('.'))));
        }

        var llvm = LlvmBackend.Compile(linked, new LlvmOptions { SymbolPrefix = "larva." });
        Check("llvm: compiles with the wrappers", llvm.Success, llvm.ToString());
        if (llvm.Success)
        {
            using var program = llvm.Program!;
            string path = Path.Combine(Directory, "linked-llvm.obj");
            program.EmitObject(path);
            var symbols = ObjectSymbols(File.ReadAllBytes(path));
            Check("llvm: Larva symbols prefixed, wrappers and runtime not", symbols.Contains("larva.app::main") && symbols.Contains("larva_entry")
                && !symbols.Contains("app::main"));
        }

        var unprintable = new FunctionBuilder("main", IrType.Ref(IrType.I32));
        int x = unprintable.Local(IrType.I32, "x");
        unprintable.Assign(x, Operand.Int(1)).Assign(FunctionBuilder.ReturnPlace, Rvalue.Ref(new Place(x))).Return();
        var bad = Linker.Link([Compiler.CompileObject(new IrModule([], [unprintable.Build()]) { Name = "bad" }, new CompilationOptions { BorrowCheck = BorrowCheckMode.Off }).Object!]);
        Check("an entry returning a reference is rejected", Throws(() => ExecutableEntry.AddWrappers(bad), "returns"));
    }

    [Fact]
    public void Llvm_executables_run_as_processes() => Executables("llvm");

    [Fact]
    public void X64_executables_run_as_processes() => Executables("x64");

    /// <summary>Executables linked with MSVC from <paramref name="backend"/>'s objects, run as separate processes.</summary>
    private void Executables(string backend)
    {
        var toolchain = Toolchain.Find();
        Assert.SkipWhen(toolchain is null, Toolchain.Missing);
        TestContext.Current.TestOutputHelper?.WriteLine($"MSVC: {toolchain!.VcVars}");

        var (geom, app) = LinkerPrograms.SaveObjects(Directory);
        string exceptions = Save(RuntimeFeaturePrograms.ExceptionModule() with { Name = "exc" }, "exc.lvo");
        string arrays = Save(RuntimeFeaturePrograms.ArrayModule() with { Name = "arrays" }, "arrays.lvo", GcConfig.Manual);
        string tracing = Save(NativePrograms.TracingProgram() with { Name = "trace" }, "trace.lvo");
        string arena = Save(RuntimeFeaturePrograms.ArenaExceptionModule() with { Name = "arena" }, "arena.lvo", GcConfig.Arena);
        string Exe(string name) => Path.Combine(Directory, $"{name}-{backend}.exe");

        var (code, log) = Cli("native", "--exe", "--backend", backend, "-o", Exe("app"), geom, app);
        Check($"{backend}: larva native --exe geom.lvo app.lvo", code == 0, log.Trim());
        if (code != 0)
            return;
        var (exit, output, errors) = Execute(Exe("app"));
        Check($"{backend}: app.exe prints 7", exit == 0 && output.Trim() == "7", $"exit {exit}: {output.Trim()} {errors.Trim()}");

        Cli("native", "--exe", "--backend", backend, "--entry", "caller", "-o", Exe("caller"), exceptions);
        (exit, output, errors) = Execute(Exe("caller"), ["-3"]);
        Check($"{backend}: caller.exe -3 prints 3 (thrown and caught natively)", exit == 0 && output.Trim() == "3", $"exit {exit}: {output.Trim()} {errors.Trim()}");
        Cli("native", "--exe", "--backend", backend, "--entry", "thrower", "-o", Exe("thrower"), exceptions);
        (exit, output, errors) = Execute(Exe("thrower"), ["-1"]);
        Check($"{backend}: thrower.exe -1 reports the uncaught exception", exit == 1 && errors.Contains("unhandled exception"), $"exit {exit}: {errors.Trim()}");
        Cli("native", "--exe", "--backend", backend, "--entry", "catcher", "-o", Exe("catcher"), exceptions);
        (exit, output, errors) = Execute(Exe("catcher"), ["-1"], stats: true);
        Check($"{backend}: catcher.exe -1 = -1, the unwound frame's box freed", exit == 0 && output.Trim() == "-1" && errors.Contains("live heap 0,"),
            $"{output.Trim()} {errors.Trim()}");

        Cli("native", "--exe", "--backend", backend, "--entry", "keep", "-o", Exe("keep"), tracing);
        (exit, output, errors) = Execute(Exe("keep"), ["200"], stress: true, stats: true);
        Check($"{backend}: keep.exe 200 = 8 under GC stress (stack maps read from the image)", exit == 0 && output.Trim() == "8"
            && Regex.IsMatch(errors, @"collections [1-9]\d\d") && errors.Contains("errors 0"), $"{output.Trim()} {errors.Trim()}");

        Cli("native", "--exe", "--backend", backend, "--entry", "squares", "-o", Exe("squares"), arrays);
        (exit, output, errors) = Execute(Exe("squares"), ["1000"], stats: true);
        Check($"{backend}: squares.exe 1000 = 332833500, no leaks", exit == 0 && output.Trim() == "332833500" && errors.Contains("live heap 0,"),
            $"{output.Trim()} {errors.Trim()}");
        Cli("native", "--exe", "--backend", backend, "--entry", "oob", "-o", Exe("oob"), arrays);
        (exit, output, errors) = Execute(Exe("oob"));
        Check($"{backend}: oob.exe panics with exit 101", exit == 101 && errors.Contains("index 5 out of bounds for length 3"), $"exit {exit}: {errors.Trim()}");

        Cli("native", "--exe", "--backend", backend, "--entry", "region_catcher", "-o", Exe("region"), arena);
        (exit, output, errors) = Execute(Exe("region"), stats: true);
        Check($"{backend}: region.exe = -1, unwinding closed the region", exit == 0 && output.Trim() == "-1" && errors.Contains("live gc 0,"),
            $"{output.Trim()} {errors.Trim()}");
    }

    [Fact]
    public void Llvm_pinned_young_objects_keep_collecting_and_are_not_finalized_alive() => PinnedNursery("llvm");

    /// <summary>
    /// Young objects the stack pins stay in their nursery block, which the old generation keeps until a major collection
    /// frees it: those blocks must weigh on the old generation (else the nursery's reserve runs out after 16384 of them and
    /// nothing collects again), and a pinned object with a finalizer, old in place, must not be finalized while alive.
    /// Only the LLVM back end makes young objects (x64 allocates every object old, without a type map).
    /// </summary>
    private void PinnedNursery(string backend)
    {
        var toolchain = Toolchain.Find();
        Assert.SkipWhen(toolchain is null, Toolchain.Missing);
        string pins = Save(NativePrograms.PinnedProgram() with { Name = "pins" }, "pins.lvo");
        string Exe(string name) => Path.Combine(Directory, $"{name}-{backend}.exe");

        // One block per minor collection: every one pins the newest object and retains its block, 40000 times (2.5 GB of
        // blocks, were none freed); the collections must go on past the 16384 blocks of the reserve, one per iteration.
        var (code, log) = Cli("native", "--exe", "--backend", backend, "--entry", "pinned_churn", "-o", Exe("pinned"), pins);
        Check($"{backend}: larva native --exe --entry pinned_churn", code == 0, log.Trim());
        if (code != 0)
            return;
        var (exit, output, errors) = Execute(Exe("pinned"), ["40000"], stats: true, nursery: 1);
        var collections = Regex.Match(errors, @"collections (\d+) \((\d+) minor\)");
        var pinnedCount = Regex.Match(errors, @"pinned (\d+)");
        Check($"{backend}: pinned.exe 40000 = 39999, 40000 pins and more than 39000 minor collections with a one-block nursery",
            exit == 0 && output.Trim() == "39999" && collections.Success && long.Parse(collections.Groups[2].Value) > 39000
            && pinnedCount.Success && long.Parse(pinnedCount.Groups[1].Value) >= 39000 && errors.Contains("errors 0"), $"exit {exit}: {output.Trim()} {errors.Trim()}");

        Cli("native", "--exe", "--backend", backend, "--entry", "finalized_alive", "-o", Exe("alive"), pins);
        (exit, output, errors) = Execute(Exe("alive"), ["1000000"], stats: true);
        Check($"{backend}: alive.exe 1000000 = 8: keep (pinned by the first minor collection) not finalized while held", exit == 0 && output.Trim() == "8"
            && Regex.IsMatch(errors, @"collections [2-9]\d* \(") && errors.Contains("errors 0"), $"exit {exit}: {output.Trim()} {errors.Trim()}");
    }

    [Fact]
    public void Llvm_major_collection_resets_remembered_objects_before_the_sweep() => RememberedSweep("llvm");

    [Fact]
    public void X64_major_collection_resets_remembered_objects_before_the_sweep() => RememberedSweep("x64");

    /// <summary>
    /// A major collection that no minor one precedes (every object old: x64, or generations off) still holds the objects
    /// the write barrier remembered; those that die in the sweep must not be written afterwards (their age was reset after
    /// the sweep, into freed memory: a crash in collect_major once the memory was given back).
    /// </summary>
    private void RememberedSweep(string backend)
    {
        var toolchain = Toolchain.Find();
        Assert.SkipWhen(toolchain is null, Toolchain.Missing);
        string program = Save(NativePrograms.RememberProgram() with { Name = "remember" }, "remember.lvo");
        string exe = Path.Combine(Directory, $"remember-{backend}.exe");
        var (code, log) = Cli("native", "--exe", "--backend", backend, "--entry", "remember_churn", "-o", exe, program);
        Check($"{backend}: larva native --exe --entry remember_churn", code == 0, log.Trim());
        if (code != 0)
            return;
        foreach (bool generations in new[] { true, false })
        {
            var (exit, output, errors) = Execute(exe, ["300"], stats: true, generations: generations);
            Check($"{backend}: remember.exe 300 = 299 (generations {(generations ? "on" : "off")}), the collections after remembered objects died",
                exit == 0 && output.Trim() == "299" && Regex.IsMatch(errors, @"collections [1-9]\d* \(") && errors.Contains("errors 0"),
                $"exit {exit}: {output.Trim()} {errors.Trim()}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────

    private static string Save(IrModule module, string file, GcConfig? gc = null)
    {
        var result = Compiler.CompileObject(module, new CompilationOptions { Gc = gc ?? GcConfig.Default });
        if (!result.Success)
            throw new InvalidOperationException(string.Join("\n", result.TypeErrors.Select(e => e.ToString()).Concat(result.Diagnostics.Select(d => d.ToString()))));
        string path = Path.Combine(Directory, file);
        ObjectFile.Save(result.Object!, path);
        return path;
    }

    private static (int Exit, string Output, string Errors) Execute(string exe, string[]? arguments = null, bool stress = false, bool stats = false, int nursery = 0,
        bool generations = true)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments ?? [])
            start.ArgumentList.Add(argument);
        if (stress)
            start.Environment["LARVA_GC_STRESS"] = "1";
        if (stats)
            start.Environment["LARVA_STATS"] = "1";
        if (nursery > 0)
            start.Environment["LARVA_GC_NURSERY"] = nursery.ToString();
        if (!generations)
            start.Environment["LARVA_GC_GENERATIONS"] = "0";
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.ExitCode, output, errors.Result);
    }

    /// <summary>larva with LARVA_DIAGNOSTICS (the programs run under LARVA_GC_STRESS and print LARVA_STATS).</summary>
    private static (int Code, string Output) Cli(params string[] arguments) =>
        Repository.Cli(new Dictionary<string, string> { ["LARVA_DIAGNOSTICS"] = "1" }, 300_000, arguments);

    private static List<string> ObjectSymbols(byte[] bytes)
    {
        var symbols = new List<string>();
        var name = "object\0"u8.ToArray();
        fixed (byte* data = bytes)
        fixed (byte* bufferName = name)
        {
            var buffer = LLVM.CreateMemoryBufferWithMemoryRangeCopy((sbyte*)data, (nuint)bytes.Length, (sbyte*)bufferName);
            sbyte* error;
            var binary = LLVM.CreateBinary(buffer, null, &error);
            if (binary == null)
                return symbols;
            var symbol = LLVM.ObjectFileCopySymbolIterator(binary);
            for (; LLVM.ObjectFileIsSymbolIteratorAtEnd(binary, symbol) == 0; LLVM.MoveToNextSymbol(symbol))
                symbols.Add(new string(LLVM.GetSymbolName(symbol)));
            LLVM.DisposeSymbolIterator(symbol);
            LLVM.DisposeBinary(binary);
            LLVM.DisposeMemoryBuffer(buffer);
        }
        return symbols;
    }

    private static bool Throws(Action action, string fragment)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException e)
        {
            return e.Message.Contains(fragment);
        }
    }
}
