using System.Numerics;
using Larva.IR;
using Larva.IR.Lowering;
using Larva.Runtime;
using Larva.Runtime.Vm;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>Runs the test programs on the IL interpreter and checks results, memory and GC behavior.</summary>
public sealed class InterpreterTests : CheckedTest
{
    // ── Programs from the other tests ──────────────────────────────

    [Fact]
    public void Basic_program_runs_and_releases_its_frames()
    {
        var module = BasicProgram.Module();
        var vm = Vm(module);
        Check("sum(10) = 45", Equal(vm.Run("sum", 10), 45L));
        Check("main() = 5  (bump(&mut pt) then sum(pt.y) + pt.x)", Equal(vm.Run("main"), 5L), vm.Stats.ToString());
        Check("frames are released", vm.Heap.Live.Count() == 0);

        var stressed = Vm(module, stress: true);
        Check("same under GC stress", Equal(stressed.Run("main"), 5L) && stressed.Stats.Collections > 0, stressed.Stats.ToString());
    }

    [Fact]
    public void Generic_program_runs_in_both_generics_modes()
    {
        var module = GenericsPrograms.ProgramModule();
        Check("main() = max<i32>(3, 7) + first(&pair) = 8", Equal(Vm(module).Run("main"), 8L));
        Check("same with GenericsMode.Specialize", Equal(Vm(module, generics: GenericsMode.Specialize).Run("main"), 8L));
        var vm = Vm(module);
        Check("add<f64>(1.5, 2.25) = 3.75", Equal(vm.Run("add<f64>", 1.5, 2.25), 3.75));
        Check("add<i128>(2^100, 1)", Equal(vm.Run("add<i128>", BigInteger.Pow(2, 100), 1), BigInteger.Pow(2, 100) + 1));
    }

    [Fact]
    public void Shared_generic_code_runs_with_witnesses()
    {
        var module = SharedGenericsPrograms.ProgramModule();
        var vm = Vm(module);
        Check("main() = size_of<f32> = 4", Equal(vm.Run("main"), 4UL), vm.Stats.ToString());

        var il = Compile(module);
        var point = IrType.Struct(GenericsPrograms.PointStruct);
        var shared = new Interpreter(il);
        Check("size_of<Point> through a witness = 8", Equal(Interpreter.ToObject(shared.Call(shared.FunctionIndex("size_of"), [point], [])), 8UL));
        Check("pair_size<i64> builds Pair<i64, i32> = 16", Equal(Interpreter.ToObject(shared.Call(shared.FunctionIndex("pair_size"), [IrType.I64], [])), 16UL));
        var echoed = shared.Call(shared.FunctionIndex("pass_through"), [IrType.F64], [Value.FromFloat(2.5, IrType.F64)]);
        Check("pass_through<f64> forwards its witness", Equal(Interpreter.ToObject(echoed), 2.5));
        var wrapped = shared.Call(shared.FunctionIndex("wrap"), [IrType.Bool], [Value.FromBool(true)]);
        Check("wrap<bool>(true) = Pair { true, 0 }", wrapped.Bytes is [1, _, _, _, 0, 0, 0, 0], wrapped.ToString());
    }

    // ── GC modes ───────────────────────────────────────────────────

    [Fact]
    public void Tracing_gc_frees_boxes_and_keeps_live_objects_under_stress()
    {
        var vm = Vm(GcPrograms.ManagedModule(), gc: GcConfig.Default, stress: true);
        Check("boxes() = 3, both boxes freed", Equal(vm.Run("boxes"), 3L) && Leaks(vm) == 0);
        vm.Run("take");
        Check("take(): the callee freed the box", Leaks(vm) == 0);
        Check("raw() = 5 (alloc / free)", Equal(vm.Run("raw"), 5L) && Leaks(vm) == 0);
        vm.Run("spin", 100);
        Check("spin(100) polls at every iteration", vm.Stats.Safepoints >= 100, vm.Stats.ToString());

        // A live handle survives collections while garbage is reclaimed.
        var keep = new FunctionBuilder("keep", IrType.I32);
        int n = keep.Argument(IrType.I32, "n");
        int p = keep.Local(GcPrograms.PointType, "p", mutable: true);
        int h = keep.Local(IrType.Gc(GcPrograms.PointType), "h");
        int tmp = keep.Local(IrType.Gc(GcPrograms.PointType), "tmp", mutable: true);
        int i = keep.Local(IrType.I32, "i", mutable: true);
        int c = keep.Temp(IrType.Bool);
        int head = keep.NewBlock(), body = keep.NewBlock(), exit = keep.NewBlock();
        keep.Assign(new Place(p).Field(0), Int(7)).Assign(new Place(p).Field(1), Int(8))
            .Assign(h, Rvalue.Gc(Move(p)))
            .Assign(i, Int(0))
            .Goto(head);
        keep.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Move(c), body, exit);
        keep.At(body)
            .Assign(new Place(p).Field(0), Copy(i)).Assign(new Place(p).Field(1), Copy(i))
            .Assign(tmp, Rvalue.Gc(Move(p)))
            .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1)))
            .Goto(head);
        keep.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(h).Deref().Field(0))).Return();

        var gcVm = Vm(new IrModule(GcPrograms.StructDefs, [keep.Build()]), gc: GcConfig.Default, stress: true);
        Check("keep(50) = 7: the live object survives stress collections", Equal(gcVm.Run("keep", 50), 7L));
        Check("garbage was reclaimed", gcVm.Stats.Reclaimed >= 49, gcVm.Stats.ToString());
    }

    [Fact]
    public void Reference_counting_releases_every_handle()
    {
        // fn rc_main() -> i32 { let a = make(); let b = handles(a); peek(b) }  — make=4, handles=5, peek=7 in GcPrograms.
        var main = new FunctionBuilder("rc_main", IrType.I32);
        var gcPoint = IrType.Gc(GcPrograms.PointType);
        int a = main.Local(gcPoint, "a");
        int b = main.Local(gcPoint, "b");
        int b1 = main.NewBlock(), b2 = main.NewBlock(), b3 = main.NewBlock();
        main.Call(4, [], a, b1);
        main.At(b1).Call(5, [Move(a)], b, b2);
        main.At(b2).Call(7, [Copy(b)], FunctionBuilder.ReturnPlace, b3);
        main.At(b3).Return();

        var vm = Vm(new IrModule(GcPrograms.StructDefs, [.. GcPrograms.ManagedModule().Functions, main.Build()]), gc: GcConfig.RefCount);
        Check("rc_main() = 7", Equal(vm.Run("rc_main"), 7L), vm.Stats.ToString());
        Check("every handle released: no gc objects left", vm.Heap.Live.Count(x => x.Kind == AllocationKind.Gc) == 0);
    }

    [Fact]
    public void Drop_flags_free_a_maybe_moved_box_once()
    {
        var give = new FunctionBuilder("give", IrType.Unit);
        give.Argument(IrType.Box(GcPrograms.PointType), "b");
        give.Return();

        var f = new FunctionBuilder("maybe_give", IrType.Unit);
        int c = f.Argument(IrType.Bool, "c");
        int p = f.Local(GcPrograms.PointType, "p", mutable: true);
        int bx = f.Local(IrType.Box(GcPrograms.PointType), "b");
        int unit = f.Temp(IrType.Unit);
        int then = f.NewBlock(), join = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Assign(bx, Rvalue.Box(Move(p)))
         .If(Copy(c), then, join);
        f.At(then).Call(0, [Move(bx)], unit, join);
        f.At(join).Return();

        var vm = Vm(new IrModule([GcPrograms.StructDefs[0]], [give.Build(), f.Build()]), gc: GcConfig.Manual);
        vm.Run("maybe_give", true);
        Check("moved on this path: freed once (by the callee)", Leaks(vm) == 0 && vm.Heap.TotalFrees == 1);
        vm.Run("maybe_give", false);
        Check("kept on this path: freed by its owner", Leaks(vm) == 0 && vm.Heap.TotalFrees == 2);
    }

    [Fact]
    public void Arena_region_exit_frees_its_objects()
    {
        var f = new FunctionBuilder("in_region", IrType.I32);
        int p = f.Local(GcPrograms.PointType, "p", mutable: true);
        int g = f.Local(IrType.Gc(GcPrograms.PointType), "g");
        int v = f.Local(IrType.I32, "v");
        f.RegionEnter(0)
         .Assign(new Place(p).Field(0), Int(4)).Assign(new Place(p).Field(1), Int(5))
         .Assign(g, Rvalue.Gc(Move(p)))
         .Assign(v, Copy(new Place(g).Deref().Field(0)))
         .RegionExit(0)
         .Assign(FunctionBuilder.ReturnPlace, Copy(v))
         .Return();
        var vm = Vm(new IrModule(GcPrograms.StructDefs, [f.Build()]), gc: GcConfig.Arena);
        Check("in_region() = 4", Equal(vm.Run("in_region"), 4L));
        Check("region exit freed its objects", vm.Heap.Live.Count(x => x.Kind == AllocationKind.Gc) == 0 && vm.Heap.TotalFrees == 1);
    }

    [Fact]
    public void Wide_numbers_compute_exactly()
    {
        var f = new FunctionBuilder("wide", IrType.I128);
        int x = f.Local(IrType.I128, "x");
        f.Assign(x, Int(BigInteger.Pow(2, 100), IrType.I128))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(x), Int(5, IrType.I128)))
         .Return();

        var h = new FunctionBuilder("halves", IrType.F16);
        int a = h.Local(IrType.F16, "a");
        h.Assign(a, Half((Half)1.5))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(a), Copy(a)))
         .Return();

        var wrap = new FunctionBuilder("wrap_u8", IrType.U8);
        int u = wrap.Local(IrType.U8, "u");
        wrap.Assign(u, Int(250, IrType.U8))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(u), Int(10, IrType.U8)))
            .Return();

        var one128 = BigInteger.Parse("3FFF", System.Globalization.NumberStyles.HexNumber) << 112;
        var q = new FunctionBuilder("quad", IrType.F128);
        int qq = q.Local(IrType.F128, "q");
        q.Assign(qq, FloatBits(one128, IrType.F128))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(qq), Copy(qq)))
         .Return();

        var vm = Vm(new IrModule([], [f.Build(), h.Build(), wrap.Build(), q.Build()]));
        Check("i128: 2^100 + 5", Equal(vm.Run("wide"), BigInteger.Pow(2, 100) + 5));
        Check("f16: 1.5 + 1.5 = 3", Equal(vm.Run("halves"), 3.0));
        Check("u8 wraps: 250 + 10 = 4", Equal(vm.Run("wrap_u8"), 4UL));
        Check("f128: 1 + 1 = 2", Equal(vm.Run("quad"), 2.0));
    }

    // ── Reflection ─────────────────────────────────────────────────

    [Fact]
    public void Reflective_invoke_calls_direct_shared_and_specialized_code()
    {
        var il = Compile(ReflectionPrograms.ProgramModule());
        var runtime = RuntimeModule.From(il.Metadata);
        var vm = new Interpreter(il, runtime);

        var add = runtime.GetMethod("add");
        Check("add(2, 1.5) = 3.5", Equal(vm.Invoke(add, add.PrepareInvoke(2, 1.5)), 3.5));

        var max = runtime.GetMethod("max").MakeGeneric(runtime.TypeOf(IrType.I32));
        Check("max<i32>(3, 5) = 5", Equal(vm.Invoke(max, max.PrepareInvoke(3, 5)), 5L));

        var point = runtime.GetType("Point");
        var p = runtime.New(point);
        using (var w = p.BorrowMut())
        {
            w["x"] = 5;
            w["y"] = -6;
        }
        var identity = runtime.GetMethod("identity").MakeGeneric(point);
        var echoed = vm.Invoke(identity, identity.PrepareInvoke(p)) as byte[];
        Check("identity<Point>(p) returns p (shared code, witness from the plan)",
            echoed is not null && BitConverter.ToInt32(echoed, 0) == 5 && BitConverter.ToInt32(echoed, 4) == -6);

        var pairType = runtime.GetType("Pair").MakeGeneric(runtime.TypeOf(IrType.I32), runtime.TypeOf(IrType.F64));
        var pair = runtime.New(pairType);
        using (var w = pair.BorrowMut())
        {
            w["first"] = 1;
            w["second"] = 2.5;
        }
        var swap = runtime.GetMethod("swap_pair").MakeGeneric(runtime.TypeOf(IrType.I32), runtime.TypeOf(IrType.F64));
        var swapped = vm.Invoke(swap, swap.PrepareInvoke(pair)) as byte[];
        Check("swap_pair<i32, f64>({1, 2.5}) = {2.5, 1}",
            swapped is not null && BitConverter.ToDouble(swapped, 0) == 2.5 && BitConverter.ToInt32(swapped, 8) == 1);

        var wide = runtime.New(runtime.GetType("Wide"));
        using (var w = wide.BorrowMut())
            w["big"] = BigInteger.Pow(2, 100) + 7;
        var total = runtime.GetMethod("total");
        Check("total(Wide) = 2^100 + 7 (by-pointer argument and result)", Equal(vm.Invoke(total, total.PrepareInvoke(wide)), BigInteger.Pow(2, 100) + 7));
        Check("invoke buffers released", vm.Heap.Live.Count() == 0);
    }

    // ── Runtime errors ─────────────────────────────────────────────

    [Fact]
    public void Use_after_free_and_unreachable_trap()
    {
        var f = new FunctionBuilder("use_after_free", IrType.I32) { Unsafe = true };
        int p = f.Local(IrType.PtrMut(IrType.I32), "p");
        f.Assign(p, Rvalue.Alloc(Int(1))).Free(p)
         .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(p).Deref()))
         .Return();

        var u = new FunctionBuilder("unreachable", IrType.Unit);
        u.Unreachable();

        var vm = Vm(new IrModule([], [f.Build(), u.Build()]));
        Check("use after free is detected", Throws<VmException>(() => vm.Run("use_after_free"), "use after free"));
        Check("unreachable traps", Throws<VmTrapException>(() => vm.Run("unreachable"), "trap"));
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static IlModule Compile(IrModule module, GcConfig? gc = null, GenericsMode generics = GenericsMode.Shared)
    {
        var result = Compiler.Compile(module, new CompilationOptions { Gc = gc ?? GcConfig.Default, Generics = generics });
        if (!result.Success)
            throw new InvalidOperationException(string.Join("\n", result.TypeErrors.Select(e => e.ToString()).Concat(result.Diagnostics.Select(d => d.ToString()))));
        return result.Il!;
    }

    private static Interpreter Vm(IrModule module, GcConfig? gc = null, bool stress = false, GenericsMode generics = GenericsMode.Shared) =>
        new(Compile(module, gc, generics), options: new InterpreterOptions { GcStress = stress });

    /// <summary>Boxes / raw allocations still alive (owned memory that was never released).</summary>
    private static int Leaks(Interpreter vm) => vm.Heap.Live.Count(a => a.Kind == AllocationKind.Heap);

    private static bool Equal(object? actual, object expected) => Equals(actual, expected);

    private static bool Throws<T>(Action action, string fragment) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T e)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"     ({e.Message})");
            return e.Message.Contains(fragment);
        }
    }
}
