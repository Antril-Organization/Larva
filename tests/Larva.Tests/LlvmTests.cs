using System.Numerics;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.Llvm;
using Larva.Runtime.Vm;
using static Larva.IR.Operand;
using Larva.Native;
using static Larva.Tests.NativePrograms;

namespace Larva.Tests;

/// <summary>Native code through LLVM: the test programs compiled, run with the JIT, and compared with the VM.</summary>
public sealed unsafe class LlvmTests : CheckedTest
{
    // ── Programs from the other suites ─────────────────────────────

    [Fact]
    public void Basic_program_runs_natively_like_the_vm()
    {
        var module = BasicProgram.Module();
        using var program = Compile(module);
        using var jit = program.Jit();
        var sum = (delegate* unmanaged<int, int>)jit.Address("sum");
        var main = (delegate* unmanaged<int>)jit.Address("main");
        Check("sum(10) = 45, same as the VM", sum(10) == 45 && Equals(Vm(module).Run("sum", 10), 45L));
        Check("main() = 5 (struct fields, &mut, calls)", main() == 5);
        Check("sum(100000) = 704982704 (i32 wraps like the VM)", sum(100000) == 704982704 && Equals(Vm(module).Run("sum", 100000), 704982704L));
    }

    [Fact]
    public void Generic_programs_run_specialized()
    {
        using (var program = Compile(GenericsPrograms.ProgramModule()))
        using (var jit = program.Jit())
            Check("generics main() = 8", ((delegate* unmanaged<int>)jit.Address("main"))() == 8);

        using (var program = Compile(SharedGenericsPrograms.ProgramModule()))
        using (var jit = program.Jit())
            Check("shared-generics main() = size_of<f32> = 4 (specialized natively)", ((delegate* unmanaged<ulong>)jit.Address("main"))() == 4);
    }

    [Fact]
    public void Memory_modes_free_release_and_close_regions()
    {
        NativeRuntime.Reset();
        using (var program = Compile(GcPrograms.BoxOnlyModule(), GcConfig.Manual))
        using (var jit = program.Jit())
        {
            Check("boxes() = 3", ((delegate* unmanaged<int>)jit.Address("boxes"))() == 3);
            ((delegate* unmanaged<void>)jit.Address("take"))();
            Check("raw() = 5", ((delegate* unmanaged<int>)jit.Address("raw"))() == 5);
            Check("every box freed, no double frees", NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0 && NativeRuntime.Frees == NativeRuntime.Allocations,
                $"allocations {NativeRuntime.Allocations}, frees {NativeRuntime.Frees}, errors {NativeRuntime.Errors}");
        }

        // fn maybe_give(c: bool): the box moves to give() on one path only — drop flags decide at run time.
        NativeRuntime.Reset();
        using (var program = Compile(MaybeGive(), GcConfig.Manual))
        using (var jit = program.Jit())
        {
            var maybeGive = (delegate* unmanaged<int, void>)jit.Address("maybe_give_int");
            maybeGive(1);
            maybeGive(0);
            Check("drop flags: freed exactly once on both paths", NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0 && NativeRuntime.Frees == 2,
                $"frees {NativeRuntime.Frees}, errors {NativeRuntime.Errors}");
        }

        NativeRuntime.Reset();
        using (var program = Compile(RcProgram(), GcConfig.RefCount))
        using (var jit = program.Jit())
        {
            Check("RC: rc_main() = 7", ((delegate* unmanaged<int>)jit.Address("rc_main"))() == 7);
            Check("RC: every handle released", NativeRuntime.LiveGc == 0 && NativeRuntime.Errors == 0,
                $"live {NativeRuntime.LiveGc}, retains {NativeRuntime.Retains}, releases {NativeRuntime.Releases}");
        }

        NativeRuntime.Reset();
        using (var program = Compile(RegionProgram(), GcConfig.Arena))
        using (var jit = program.Jit())
        {
            Check("arena: in_region() = 4", ((delegate* unmanaged<int>)jit.Address("in_region"))() == 4);
            Check("arena: region exit freed its objects", NativeRuntime.LiveGc == 0 && NativeRuntime.Frees == 1);
        }
    }

    [Fact]
    public void Arrays_index_free_and_guard_their_bounds()
    {
        NativeRuntime.Reset();
        using (var program = Compile(RuntimeFeaturePrograms.ArrayModule(), GcConfig.Manual))
        using (var jit = program.Jit())
        {
            Check("squares(10) = 285", ((delegate* unmanaged<long, long>)jit.Address("squares"))(10) == 285);
            Check("Len(box [u8; 7]) = 7", ((delegate* unmanaged<long, ulong>)jit.Address("len_of"))(7) == 7);
            Check("array boxes freed", NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0);
            Check("out-of-bounds access is guarded", program.IrText.Contains("larva_panic"));
        }

        NativeRuntime.Reset();
        using (var program = Compile(RuntimeFeaturePrograms.GcArrayModule(), GcConfig.Default))
        using (var jit = program.Jit())
        {
            Check("gc [gc Point; 20] in Default mode = 19", ((delegate* unmanaged<long, long>)jit.Address("gc_array"))(20) == 19);
            // The runtimes' collector is not generational: the barrier the Default mode allows for is empty, so none is called.
            Check("element stores call no write barrier (the collector needs none)", NativeRuntime.Barriers == 0, $"barriers {NativeRuntime.Barriers}");
        }
    }

    [Fact]
    public void Wide_small_and_float_numbers_compute_exactly()
    {
        using var program = Compile(NumberProgram());
        using var jit = program.Jit();
        Check("i128: (2^100 + 5) >> 64 = 2^36", ((delegate* unmanaged<long>)jit.Address("wide_high"))() == 1L << 36);
        Check("u8 wraps: 250 + 10 = 4", ((delegate* unmanaged<long>)jit.Address("wrap_u8"))() == 4);
        Check("f16: 1.5 + 1.5 = 3", ((delegate* unmanaged<double>)jit.Address("halves"))() == 3.0);
        Check("f128: 1 + 1 = 2", ((delegate* unmanaged<double>)jit.Address("quad"))() == 2.0);
        var unsignedLess = (delegate* unmanaged<uint, uint, int>)jit.Address("less_u32");
        var signedLess = (delegate* unmanaged<int, int, int>)jit.Address("less_i32");
        Check("comparisons honor signedness", unsignedLess(4_000_000_000, 1) == 0 && signedLess(-5, 1) == 1);
    }

    [Fact]
    public void A_linked_program_runs_across_modules()
    {
        var linked = LinkerPrograms.LinkedProgram();
        var result = LlvmBackend.Compile(linked);
        Check("geom + app compile natively", result.Success, result.ToString());
        if (!result.Success)
            return;
        using var program = result.Program!;
        using var jit = program.Jit();
        Check("app::main() = 7 across modules", ((delegate* unmanaged<int>)jit.Address("app::main"))() == 7);
        Check("& parameters are noalias (from the borrow checker)", Compile(GenericsPrograms.ProgramModule(), optimize: 0).IrText.Contains("noalias"));
    }

    // ── Emission ───────────────────────────────────────────────────

    /// <summary>
    /// Zero-cost exceptions (Windows SEH) against the VM's results. Only paths that end up caught are run: a Larva exception
    /// that escapes into the host is a foreign SEH exception there.
    /// </summary>
    [Fact]
    public void Zero_cost_exceptions_match_the_vm()
    {
        var module = RuntimeFeaturePrograms.ExceptionModule();
        var vm = Vm(module);
        NativeRuntime.Reset();
        using (var program = Compile(module))
        {
            Check("no landing pads on calls without handlers or cleanup", !Extract(program.IrText, "define", "@thrower(").Contains("invoke"));
            using var jit = program.Jit();
            var caller = (delegate* unmanaged<int, int>)jit.Address("caller");
            Check("caller(5) = 10, no exception", caller(5) == 10 && Equals(vm.Run("caller", 5), 10L));
            Check("caller(-3) = 3, caught i32 (same as the VM)", caller(-3) == 3 && Equals(vm.Run("caller", -3), 3L));

            var catcher = (delegate* unmanaged<int, int>)jit.Address("catcher");
            Check("catcher(-1) = -1, leaky's box freed by its cleanup pad", catcher(-1) == -1 && NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0,
                $"live {NativeRuntime.LiveHeap}, errors {NativeRuntime.Errors}");
            Check("catcher(1) = 1, normal path frees it too", catcher(1) == 1 && NativeRuntime.LiveHeap == 0);

            Check("resume re-raises to the outer handler: caller2(-4) = 96", ((delegate* unmanaged<int, int>)jit.Address("caller2"))(-4) == 96);
            Check("a box received as argument is freed when the callee unwinds",
                ((delegate* unmanaged<int, int>)jit.Address("owner"))(-1) == -1 && NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0,
                $"live {NativeRuntime.LiveHeap}, errors {NativeRuntime.Errors}");
            Check("catch (e: gc Point) = 9", ((delegate* unmanaged<int>)jit.Address("catch_gc"))() == 9);
            // The thrown gc Point stays until a collection runs (none here: far below the threshold; see Tracing()).
            Check("every exception cell and box freed", NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0 && NativeRuntime.LiveGc == 1,
                $"allocations {NativeRuntime.Allocations}, frees {NativeRuntime.Frees}, live gc {NativeRuntime.LiveGc}, errors {NativeRuntime.Errors}");
        }

        NativeRuntime.Reset();
        using (var program = Compile(RuntimeFeaturePrograms.ArenaExceptionModule(), GcConfig.Arena))
        using (var jit = program.Jit())
        {
            Check("unwinding closes the region it crosses", ((delegate* unmanaged<int>)jit.Address("region_catcher"))() == -1
                && NativeRuntime.LiveGc == 0 && NativeRuntime.Errors == 0, $"live gc {NativeRuntime.LiveGc}, errors {NativeRuntime.Errors}");
        }
    }

    /// <summary>Tracing collection with stack maps, under GC stress (a collection at every safepoint).</summary>
    [Fact]
    public void Tracing_gc_finds_roots_through_stack_maps()
    {
        foreach (int level in new[] { 0, 2 })
        {
            NativeRuntime.Reset();
            using var program = Compile(TracingProgram(), optimize: level);
            Check($"O{level}: functions with gc slots get a stack map", program.IrText.Contains("llvm.experimental.stackmap") && !program.IrText.Contains(StackMapsMarker));
            using var jit = program.Jit();
            NativeRuntime.StressGc = true;
            long churned = ((delegate* unmanaged<long, long>)jit.Address("churn"))(50);
            Check($"O{level}: churn(50) reclaims its garbage", churned == 50 && NativeRuntime.Collections >= 50 && NativeRuntime.LiveGc <= 1,
                $"collections {NativeRuntime.Collections}, collected {NativeRuntime.Collected}, live {NativeRuntime.LiveGc}");
            int kept = ((delegate* unmanaged<long, int>)jit.Address("keep"))(30);
            Check($"O{level}: keep(30) = 8, the caller's object survives collections inside the callee", kept == 8 && NativeRuntime.Errors == 0,
                $"got {kept}, frames {NativeRuntime.LastFrames}, stack roots {NativeRuntime.LastStackRoots}");
            NativeRuntime.StressGc = false;
            NativeRuntime.Collect();
            Check($"O{level}: after returning, everything is garbage", NativeRuntime.LiveGc == 0 && NativeRuntime.LiveHeap == 0, $"live gc {NativeRuntime.LiveGc}");
        }

        NativeRuntime.Reset();
        using (var program = Compile(RuntimeFeaturePrograms.GcArrayModule()))
        using (var jit = program.Jit())
        {
            NativeRuntime.StressGc = true;
            long last = ((delegate* unmanaged<long, long>)jit.Address("gc_array"))(20);
            NativeRuntime.StressGc = false;
            Check("gc_array(20) = 19 under stress: elements are traced through the array", last == 19 && NativeRuntime.Collections > 0 && NativeRuntime.Collected == 0,
                $"collections {NativeRuntime.Collections}, collected {NativeRuntime.Collected}");
            NativeRuntime.Collect();
            Check("the array and its 20 points are reclaimed afterwards", NativeRuntime.LiveGc == 0 && NativeRuntime.Collected == 21, $"collected {NativeRuntime.Collected}");
        }

        NativeRuntime.Reset();
        using (var program = Compile(RuntimeFeaturePrograms.ExceptionModule()))
        using (var jit = program.Jit())
        {
            NativeRuntime.StressGc = true;
            int caught = ((delegate* unmanaged<int>)jit.Address("catch_gc"))();
            NativeRuntime.StressGc = false;
            Check("catch (e: gc Point) = 9 under stress: the payload cell keeps the object alive in flight", caught == 9 && NativeRuntime.Collections > 0,
                $"got {caught}, collections {NativeRuntime.Collections}");
            NativeRuntime.Collect();
            Check("the caught object is reclaimed afterwards", NativeRuntime.LiveGc == 0 && NativeRuntime.LiveHeap == 0);
        }
        NativeRuntime.Reset();
    }

    private const string StackMapsMarker = "larva.gc.root";

    /// <summary>Bounds-check elimination: the analysis on small loops, then its effect on native code.</summary>
    [Fact]
    public void Bounds_checks_are_eliminated_only_when_proven()
    {
        var n = Copy(new Place(1));
        var m = Copy(new Place(2));
        Expect("for i in 0..n { a[i] } with a: [_; n]", Loop(Int(0, IrType.I64), BinaryOp.Lt, n, n), 1);
        Expect("n > i (operands swapped)", Loop(Int(0, IrType.I64), BinaryOp.Gt, n, n, swap: true), 1);
        Expect("constant length 10, i < 10", Loop(Int(0, IrType.I64), BinaryOp.Lt, Int(10, IrType.I64), Int(10, IrType.I64)), 1);
        Expect("kept: i <= n", Loop(Int(0, IrType.I64), BinaryOp.Le, n, n), 0);
        Expect("kept: i starts at -1", Loop(Int(-1, IrType.I64), BinaryOp.Lt, n, n), 0);
        Expect("kept: bound m is not the length n", Loop(Int(0, IrType.I64), BinaryOp.Lt, m, n), 0);
        Expect("kept: constant length 10, i < 11", Loop(Int(0, IrType.I64), BinaryOp.Lt, Int(11, IrType.I64), Int(10, IrType.I64)), 0);
        Expect("kept: n reassigned in the loop", Loop(Int(0, IrType.I64), BinaryOp.Lt, n, n, reassignBound: true), 0);
        Expect("kept: i borrowed mutably", Loop(Int(0, IrType.I64), BinaryOp.Lt, n, n, borrowIndex: true), 0);

        NativeRuntime.Reset();
        using (var program = Compile(RuntimeFeaturePrograms.ArrayModule(), GcConfig.Manual))
        using (var jit = program.Jit())
        {
            string squares = Extract(program.IrText, "define", "@squares(", 200);
            Check("squares: both loops' checks eliminated", program.BoundsChecksEliminated == 2 && !squares.Contains("larva_panic(i32 1"),
                $"{program.BoundsChecksEliminated} eliminated, {program.BoundsChecks} kept");
            Check("oob: the out-of-bounds access keeps its check", Extract(program.IrText, "define", "@oob(", 40).Contains("larva_panic(i32 1"));
            Check("squares(1000) = 332833500 without checks", ((delegate* unmanaged<long, long>)jit.Address("squares"))(1000) == 332833500);
            Check("new arrays reject negative lengths (panic 4)", program.IrText.Contains("larva_panic(i32 4"));
        }
        var all = LlvmBackend.Compile(RuntimeFeaturePrograms.ArrayModule(), new LlvmOptions
        {
            Compilation = CompilationOptions.Default with { Gc = GcConfig.Manual },
            BoundsCheckElimination = false,
        });
        using (var program = all.Program!)
        {
            Check("with elimination off every access is checked", program.BoundsChecksEliminated == 0 && program.BoundsChecks >= 3,
                $"{program.BoundsChecks} checks");
        }
    }

    private void Expect(string name, IrFunction function, int eliminated)
    {
        var analysis = BoundsAnalysis.Run(function, new IrModule([], [function]));
        Check($"BCE {name}: {eliminated} of {analysis.Accesses} eliminated", analysis.Eliminated == eliminated && analysis.Accesses == 1,
            $"{analysis.Eliminated} eliminated");
    }

    /// <summary>fn loop(n: i64, m: i64) -> i64 { a = box [i64; length]; i = start; while i OP bound { a[i] = i; i += 1 } 0 }</summary>
    private static IrFunction Loop(Operand start, BinaryOp op, Operand bound, Operand length,
        bool swap = false, bool reassignBound = false, bool borrowIndex = false)
    {
        var f = new FunctionBuilder("loop", IrType.I64);
        int n = f.Argument(IrType.I64, "n");
        f.Argument(IrType.I64, "m");
        int a = f.Local(IrType.Box(new ArrayType(IrType.I64)), "a", mutable: true);
        int i = f.Local(IrType.I64, "i", mutable: true);
        int c = f.Local(IrType.Bool, "c", mutable: true);
        int r = f.Local(IrType.RefMut(IrType.I64), "r");
        int head = f.NewBlock(), body = f.NewBlock(), exit = f.NewBlock();
        f.Assign(a, Rvalue.NewArray(AllocKind.Box, IrType.I64, length)).Assign(i, start);
        if (borrowIndex)
            f.Assign(r, Rvalue.RefMut(new Place(i)));
        f.Goto(head);
        f.At(head).Assign(c, swap ? Rvalue.Binary(op, bound, Copy(i)) : Rvalue.Binary(op, Copy(i), bound)).If(Copy(c), body, exit);
        f.At(body).Assign(new Place(a).Deref().Index(i), Copy(i));
        if (reassignBound)
            f.Assign(n, Rvalue.Binary(BinaryOp.Add, Copy(n), Int(1, IrType.I64)));
        f.Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        f.At(exit).Assign(FunctionBuilder.ReturnPlace, Int(0, IrType.I64)).Return();
        return f.Build();
    }

    /// <summary>Polly through an external <c>opt</c>: a missing tool, and the round trip with a stand-in (this executable).</summary>
    [Fact]
    public void Polly_without_opt_is_a_clear_error()
    {
        var missing = LlvmBackend.Compile(RuntimeFeaturePrograms.ArrayModule(), new LlvmOptions
        {
            Compilation = CompilationOptions.Default with { Gc = GcConfig.Manual },
            Polly = new PollyOptions(Tool: "larva-no-such-opt"),
        });
        Check("without opt, asking for Polly is a clear error", !missing.Success && missing.Error!.Contains("Polly needs LLVM's `opt`"), missing.Error);
    }

    /// <summary>The round trip through an <c>opt</c> stand-in (tests/FakeOpt) that checks the command line and copies the module.</summary>
    [Fact]
    public void Polly_round_trips_through_opt()
    {
        string? standIn = Repository.FakeOpt;
        Assert.SkipWhen(standIn is null, "Polly round trip: tests/FakeOpt is not built (no stand-in for opt)");
        NativeRuntime.Reset();
        var result = LlvmBackend.Compile(RuntimeFeaturePrograms.ArrayModule(), new LlvmOptions
        {
            Compilation = CompilationOptions.Default with { Gc = GcConfig.Manual },
            Polly = new PollyOptions(standIn, Plugin: "LLVMPolly.dll", Arguments: ["-polly-vectorizer=stripmine"]),
        });
        Check("the module goes through opt (plugin, pipeline, extra flags) and comes back", result.Success && result.Program!.Polly, result.ToString());
        if (!result.Success)
            return;
        using var program = result.Program!;
        using var jit = program.Jit();
        Check("and still runs: squares(10) = 285", ((delegate* unmanaged<long, long>)jit.Address("squares"))(10) == 285);
    }

    [Fact]
    public void Optimized_code_emits_assembly_objects_and_ir()
    {
        var module = BasicProgram.Module();
        using var unoptimized = Compile(module, optimize: 0);
        using var optimized = Compile(module, optimize: 2);
        Check("O2 is smaller than O0", optimized.IrText.Length < unoptimized.IrText.Length,
            $"{unoptimized.IrText.Length} → {optimized.IrText.Length} chars of IR");

        using (var boxes = Compile(GcPrograms.BoxOnlyModule(), GcConfig.Manual))
            Check("constant drop flags are folded away at O2", !boxes.IrText.Contains(".owned"));

        string assembly = optimized.Assembly();
        Check("assembly listing for x86-64", assembly.Contains("sum:") && assembly.Contains("ret"));

        string directory = TestEnvironment.TempFolder("llvm-emission");
        string obj = Path.Combine(directory, "basics.obj");
        optimized.EmitObject(obj);
        var bytes = File.ReadAllBytes(obj);
        Check("COFF object for x86-64 (machine 0x8664)", bytes.Length > 20 && BitConverter.ToUInt16(bytes, 0) == 0x8664, $"{bytes.Length} bytes");
        optimized.EmitIr(Path.Combine(directory, "basics.ll"));
        Check(".ll written", File.Exists(Path.Combine(directory, "basics.ll")));
    }

    /// <summary>The CLI: the linker tests' objects (geom.lvo, app.lvo), compiled natively.</summary>
    [Fact]
    public void Larva_native_emits_assembly_for_object_files()
    {
        string directory = TestEnvironment.TempFolder("llvm-native");
        var (geom, app) = LinkerPrograms.SaveObjects(directory);
        string output = Path.Combine(directory, "program.s");
        var (code, text) = Repository.Cli("native", "--emit", "asm", "-O2", "-o", output, geom, app);
        Check("larva native --emit asm geom.lvo app.lvo", code == 0 && File.Exists(output) && File.ReadAllText(output).Contains("app::main"), text.Trim());
    }

    [Fact]
    public void Every_primitive_compiles()
    {
        var f = new FunctionBuilder("f256", IrType.F256);
        f.Assign(FunctionBuilder.ReturnPlace, FloatBits(0, IrType.F256)).Return();
        var f256 = LlvmBackend.Compile(new IrModule([], [f.Build()]));
        Check("f256 is compiled (its bits as i256; the runtime's soft float computes it)", !f256.Skipped.ContainsKey("f256") && f256.Error is null, f256.Error);
    }

    // ── Small programs ─────────────────────────────────────────────

    private static IrModule NumberProgram()
    {
        var high = new FunctionBuilder("wide_high", IrType.I64);
        int x = high.Local(IrType.I128, "x");
        int y = high.Local(IrType.I128, "y");
        high.Assign(x, Rvalue.Binary(BinaryOp.Add, Int(BigInteger.Pow(2, 100), IrType.I128), Int(5, IrType.I128)))
            .Assign(y, Rvalue.Binary(BinaryOp.Shr, Copy(x), Int(64, IrType.I128)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(y), IrType.I64))
            .Return();

        var wrap = new FunctionBuilder("wrap_u8", IrType.I64);
        int u = wrap.Local(IrType.U8, "u");
        wrap.Assign(u, Rvalue.Binary(BinaryOp.Add, Int(250, IrType.U8), Int(10, IrType.U8)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(u), IrType.I64))
            .Return();

        var halves = new FunctionBuilder("halves", IrType.F64);
        int h = halves.Local(IrType.F16, "h");
        int s = halves.Local(IrType.F16, "s");
        halves.Assign(h, Half((Half)1.5)).Assign(s, Rvalue.Binary(BinaryOp.Add, Copy(h), Copy(h)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(s), IrType.F64)).Return();

        var one128 = BigInteger.Parse("3FFF", System.Globalization.NumberStyles.HexNumber) << 112;
        var quad = new FunctionBuilder("quad", IrType.F64);
        int q = quad.Local(IrType.F128, "q");
        int sq = quad.Local(IrType.F128, "sq");
        quad.Assign(q, FloatBits(one128, IrType.F128)).Assign(sq, Rvalue.Binary(BinaryOp.Add, Copy(q), Copy(q)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(sq), IrType.F64)).Return();

        return new IrModule([], [high.Build(), wrap.Build(), halves.Build(), quad.Build(), Less("less_u32", IrType.U32), Less("less_i32", IrType.I32)]);
    }

    private static IrFunction Less(string name, IrType type)
    {
        var f = new FunctionBuilder(name, IrType.I32);
        int a = f.Argument(type, "a"), b = f.Argument(type, "b");
        int r = f.Temp(IrType.Bool);
        f.Assign(r, Rvalue.Binary(BinaryOp.Lt, Copy(a), Copy(b)))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(r), IrType.I32)).Return();
        return f.Build();
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static LlvmProgram Compile(IrModule module, GcConfig? gc = null, int optimize = 2)
    {
        var result = LlvmBackend.Compile(module, new LlvmOptions
        {
            Compilation = CompilationOptions.Default with { Gc = gc ?? GcConfig.Default },
            OptimizationLevel = optimize,
        });
        if (!result.Success)
            throw new InvalidOperationException(result.ToString());
        return result.Program!;
    }

    private static Interpreter Vm(IrModule module) => new(Compiler.Compile(module).Il!);

    /// <summary>The lines of <paramref name="text"/> from the first line containing both markers.</summary>
    private static string Extract(string text, string marker, string? also, int maxLines = 30)
    {
        var lines = text.Split('\n');
        int start = Array.FindIndex(lines, l => l.Contains(marker) && (also is null || l.Contains(also)));
        if (start < 0)
            return "";
        int end = Array.FindIndex(lines, start + 1, l => l.StartsWith('}'));
        int count = Math.Min(maxLines, (end < 0 ? lines.Length : end + 1) - start);
        return string.Join('\n', lines.Skip(start).Take(count));
    }
}
