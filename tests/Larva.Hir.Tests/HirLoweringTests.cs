using System.Diagnostics;
using Larva.Hir.Lowering;
using Larva.Llvm;
using Larva.Runtime.Vm;
using Larva.X64;

namespace Larva.Hir.Tests;

/// <summary>
/// Phase 2 of the HIR rebuild: classes, structs, methods, control flow and exceptions written as HIR, lowered to IR and
/// run on the three backends (LVM, x64, LLVM), with the same results; and how fast lowering and the programs are.
/// </summary>
public sealed unsafe class HirLoweringTests : CheckedTest
{
    /// <summary>Each case: the method, its argument (none: -1), and the expected result.</summary>
    private static readonly (string Method, int? Argument, int Expected, string What)[] Cases =
    [
        ("Sum", 10, 45, "while loop, compound assignment, ++"),
        ("Fib", 20, 6765, "recursion"),
        ("Loops", null, 618, "for with continue and break, foreach over 0..=3"),
        ("Classify", 0, 10, "switch: constant"),
        ("Classify", 2, 20, "switch: or-pattern"),
        ("Classify", -5, 30, "switch: relational pattern"),
        ("Classify", 9, 40, "switch: default"),
        ("Grade", 85, 3, "switch expression with ranges"),
        ("Structs", null, 100064, "struct constructor, &mut method, copies"),
        ("Animals", null, 2011115, "virtual calls, overrides, base calls, abstract methods, inherited fields"),
        ("Types", null, 1111, "is, as, null checks on class handles"),
        ("Catches", 5, 10, "try without an exception"),
        ("Catches", -3, -1003, "typed catch binds the exception"),
        ("Finally", 1, 11, "finally on the normal path"),
        ("Finally", -1, 110, "finally while an exception passes, caught outside"),
        ("ReturnFinally", null, 57, "return inside try runs finally first"),
        ("BreakFinally", null, 303, "break inside try runs finally"),
        ("Rethrow", -4, -12, "rethrow from a catch, caught again"),
        ("Nulls", null, 12, "null, ?? and == null"),
        ("Statics", null, 8, "static fields"),
    ];

    /// <summary>The benchmark program lowered once, for the facts that run it and build executables of it.</summary>
    private static readonly Lazy<LoweringResult> LoweredBench = new(() => HirLowerer.Lower(HirPrograms.Bench().Assembly));

    private static void Log(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    [Fact]
    public void Phase2_program_gives_the_same_results_on_LVM_x64_and_LLVM()
    {
        var lowered = HirLowerer.Lower(HirPrograms.Phase2().Assembly);
        Check("the program lowers", lowered.Success, string.Join("; ", lowered.Errors));
        Require();
        var module = lowered.Module!;
        var watch = Stopwatch.StartNew();
        var vm = Larva.IR.Compiler.Compile(module);
        long vmCompile = watch.ElapsedMilliseconds;
        Check("the IR passes the type and borrow checks (LVM build)", vm.Il is not null, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
        if (vm.Il is null)
            return;
        var interpreter = new Interpreter(vm.Il);
        watch.Restart();
        var x64 = X64Backend.Compile(module);
        long x64Compile = watch.ElapsedMilliseconds;
        Check("x64 compiles it", x64.Success, x64.ToString());
        watch.Restart();
        var llvm = LlvmBackend.Compile(module, new LlvmOptions { OptimizationLevel = 2 });
        long llvmCompile = watch.ElapsedMilliseconds;
        Check("LLVM compiles it", llvm.Success, llvm.ToString());
        Log($"build: LVM {vmCompile} ms, x64 {x64Compile} ms, LLVM -O2 {llvmCompile} ms");
        if (!x64.Success || !llvm.Success)
            return;
        using var image = x64.Program!.Load();
        using var program = llvm.Program!;
        using var jit = program.Jit();
        foreach (var (name, argument, expected, what) in Cases)
        {
            string function = $"Phase2.Program.{name}";
            object? onVm;
            try
            {
                onVm = argument is { } a ? interpreter.Run(function, a) : interpreter.Run(function);
            }
            catch (Exception e)
            {
                onVm = e.GetType().Name + ": " + e.Message;
            }
            int onX64 = argument is { } ax ? ((delegate* unmanaged<int, int>)image.Address(function))(ax) : ((delegate* unmanaged<int>)image.Address(function))();
            int onLlvm = argument is { } al ? ((delegate* unmanaged<int, int>)jit.Address(function))(al) : ((delegate* unmanaged<int>)jit.Address(function))();
            bool same = Equals(onVm, (long)expected) && onX64 == expected && onLlvm == expected;
            Check($"{name}({argument}) = {expected}: {what}", same, $"LVM {onVm}, x64 {onX64}, LLVM {onLlvm}");
        }
    }

    // ── speed ──

    /// <summary>How fast a large program lowers, and how fast the backends take what comes out.</summary>
    [Fact]
    public void A_large_program_lowers_fast_and_the_same_on_several_threads()
    {
        var big = HirPrograms.Synthetic(2000, 10);
        int methods = big.Assembly.Types.Sum(t => t.Methods.Count);
        HirLowerer.Lower(HirPrograms.Synthetic(20, 10).Assembly); // warm the JIT
        var watch = Stopwatch.StartNew();
        var sequential = HirLowerer.Lower(big.Assembly, new LoweringOptions { Parallel = false });
        long oneThread = watch.ElapsedMilliseconds;
        watch.Restart();
        var lowered = HirLowerer.Lower(big.Assembly);
        long parallel = watch.ElapsedMilliseconds;
        Check($"a {methods}-method program lowers", lowered.Success && sequential.Success, string.Join("; ", lowered.Errors.Take(3)));
        if (!lowered.Success)
            return;
        Check("lowering on several threads gives the same IR", Larva.IR.Text.IrPrinter.Print(lowered.Module!) == Larva.IR.Text.IrPrinter.Print(sequential.Module!));
        watch.Restart();
        var vm = Larva.IR.Compiler.Compile(lowered.Module!);
        long vmBuild = watch.ElapsedMilliseconds;
        watch.Restart();
        var x64 = X64Backend.Compile(lowered.Module!);
        long x64Build = watch.ElapsedMilliseconds;
        Check("the LVM and x64 builds take it", vm.Success && x64.Success, string.Join("; ", vm.TypeErrors.Take(3)) + x64.Error);
        Log($"{methods} methods ({lowered.Module!.Functions.Length} IR functions): lowering {oneThread} ms on one thread, {parallel} ms on {Environment.ProcessorCount}; then LVM build {vmBuild} ms, x64 build {x64Build} ms");
        Check($"lowering {methods} methods takes under 1 s", parallel < 1000, $"{parallel} ms");
    }

    /// <summary>The benchmarks on LLVM -O3, x64 and the LVM, against the same programs on .NET.</summary>
    [Fact]
    public void Benchmarks_give_the_same_results_on_every_backend()
    {
        var lowered = LoweredBench.Value;
        Check("the benchmark program lowers", lowered.Success, string.Join("; ", lowered.Errors));
        if (!lowered.Success)
            return;
        var vm = Larva.IR.Compiler.Compile(lowered.Module!);
        var x64 = X64Backend.Compile(lowered.Module!);
        var llvm = LlvmBackend.Compile(lowered.Module!, new LlvmOptions { OptimizationLevel = 3 });
        Check("the benchmarks build on every backend", vm.Success && x64.Success && llvm.Success, $"{string.Join("; ", vm.TypeErrors.Take(2))} {x64.Error} {llvm}");
        if (!vm.Success || !x64.Success || !llvm.Success)
            return;
        var interpreter = new Interpreter(vm.Il!);
        using var image = x64.Program!.Load();
        using var program = llvm.Program!;
        using var jit = program.Jit();
        Log($"{"benchmark",-10} {"LLVM -O3",12} {"x64",12} {"LVM",14} {".NET",12}");
        foreach (var b in Benchmarks.All)
        {
            string function = $"Bench.Program.{b.Name}";
            var llvmCall = (delegate* unmanaged<int, int>)jit.Address(function);
            var x64Call = (delegate* unmanaged<int, int>)image.Address(function);
            llvmCall(Math.Min(b.Native, 1000)); // warm caches
            var (llvmResult, llvmMs) = Benchmarks.Time(() => llvmCall(b.Native));
            var (x64Result, x64Ms) = Benchmarks.Time(() => x64Call(b.Native));
            bool skipVm = Environment.GetEnvironmentVariable("LARVA_BENCH_NOVM") == "1";
            var (vmResult, vmMs) = skipVm ? (b.Reference(b.Vm), 0) : Benchmarks.Time(() => (int)(long)interpreter.Run(function, b.Vm)!);
            b.Reference(Math.Min(b.Native, 1000));
            var (netResult, netMs) = Benchmarks.Time(() => b.Reference(b.Native));
            int vmExpected = b.Reference(b.Vm);
            Check($"{b.Name}: {b.What}, same result everywhere", llvmResult == netResult && x64Result == netResult && vmResult == vmExpected,
                $"LLVM {llvmResult}, x64 {x64Result}, .NET {netResult}; LVM {vmResult} vs {vmExpected}");
            string vmScaled = b.Vm == b.Native ? $"{vmMs:0} ms" : $"{vmMs * b.Native / b.Vm:0} ms*";
            Log($"{b.Name,-10} {llvmMs,9:0} ms {x64Ms,9:0} ms {vmScaled,14} {netMs,9:0} ms");
        }
        Log("(* LVM ran a smaller input; its time is scaled to the same size)");
    }

    /// <summary>The benchmarks as real executables (the C runtime, not the test host's): process time minus an empty run.</summary>
    [Fact]
    public void Benchmarks_build_as_executables_that_give_the_same_results()
    {
        Assert.SkipWhen(Larva.Native.Toolchain.Find() is null, "no MSVC toolchain: executables not measured");
        var lowered = LoweredBench.Value;
        Assert.True(lowered.Success, "the benchmark program lowers: " + string.Join("; ", lowered.Errors));
        var compiled = Larva.IR.Compiler.CompileObject(lowered.Module!);
        var linked = Larva.IR.Linking.Linker.Link([compiled.Object!], new Larva.IR.Linking.LinkOptions { Entry = "Bench.Program.Main", LowerToIl = false });
        string folder = TestEnvironment.TempFolder("bench");
        var exes = new Dictionary<string, string>();
        foreach (var (name, backend, level) in new[] { ("LLVM -O3", Larva.Driver.Backend.Llvm, 3), ("x64", Larva.Driver.Backend.X64, 0) })
        {
            string exe = Path.Combine(folder, $"bench-{backend}.exe".ToLowerInvariant());
            var (ok, message) = Larva.Driver.ExecutableBuilder.Build(linked, exe, new Larva.Driver.ExecutableOptions { Backend = backend, OptimizationLevel = level });
            Check($"the benchmarks build as an executable ({name})", ok, message);
            if (ok)
                exes[name] = exe;
        }
        if (exes.Count == 0)
            return;
        Log($"executables: {"benchmark",-10} {string.Join(" ", exes.Keys.Select(k => $"{k,12}"))} {".NET",12}");
        for (int which = 0; which < Benchmarks.All.Length; which++)
        {
            var b = Benchmarks.All[which];
            var cells = new List<string>();
            bool same = true;
            int expected = b.Reference(b.Native);
            foreach (var (name, exe) in exes)
            {
                var (_, baseline) = Benchmarks.Execute(exe, which, 0);
                var (output, ms) = Benchmarks.Execute(exe, which, b.Native);
                same &= output == expected.ToString();
                cells.Add($"{Math.Max(0, ms - baseline),9:0} ms");
            }
            var (_, netMs) = Benchmarks.Time(() => b.Reference(b.Native));
            Check($"{b.Name} executables give {expected}", same);
            Log($"             {b.Name,-10} {string.Join(" ", cells.Select(c => $"{c,12}"))} {netMs,9:0} ms");
        }
    }
}
