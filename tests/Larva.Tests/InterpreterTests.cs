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

    [Fact]
    public void Threads_keep_the_turn_for_a_slice_while_others_spin()
    {
        // Four workers spin on a flag (each poll is a safepoint) while the main thread counts to 20000; then it sets the
        // flag, waits on a condition until every worker has reported under the lock, and joins them. Handing the turn over
        // at every safepoint would make every one of main's polls a host context switch (the shape of a thread pool whose
        // workers spin for work): the slice keeps the handoffs to a small fraction of the safepoints.
        var i32Ptr = IrType.PtrMut(IrType.I32);
        var envStruct = new StructDef("Env", i32Ptr, i32Ptr, IrType.I64, IrType.I64); // flag, done, mutex, cond
        var env = IrType.Struct(0);
        var envPtr = IrType.PtrMut(env);
        var opaque = IrType.Ptr(IrType.Unit);
        var unit = IrType.Unit;

        var worker = new FunctionBuilder("spinner", unit) { Unsafe = true };
        int arg = worker.Argument(opaque, "env");
        int we = worker.Local(envPtr, "e");
        int seen = worker.Local(IrType.I32, "seen", mutable: true);
        int wc = worker.Temp(IrType.Bool);
        int u1 = worker.Temp(unit), u2 = worker.Temp(unit), u3 = worker.Temp(unit);
        int old = worker.Temp(IrType.I32);
        int spin = worker.NewBlock(), report = worker.NewBlock();
        worker.Assign(we, Rvalue.Cast(Copy(arg), envPtr)).Goto(spin);
        worker.At(spin).Assign(seen, new IntrinsicRvalue("atomic.load.i32", [Copy(new Place(we).Deref().Field(0))]))
              .Assign(wc, Rvalue.Binary(BinaryOp.Eq, Copy(seen), Int(0))).If(Move(wc), spin, report);
        worker.At(report).Assign(u1, new IntrinsicRvalue("mutex.lock", [Copy(new Place(we).Deref().Field(2))]))
              .Assign(old, new IntrinsicRvalue("atomic.add.i32", [Copy(new Place(we).Deref().Field(1)), Int(1)]))
              .Assign(u2, new IntrinsicRvalue("cond.signal", [Copy(new Place(we).Deref().Field(3))]))
              .Assign(u3, new IntrinsicRvalue("mutex.unlock", [Copy(new Place(we).Deref().Field(2))]))
              .Return();

        var main = new FunctionBuilder("spin_main", IrType.I32) { Unsafe = true };
        int e = main.Local(env, "e", mutable: true);
        int p = main.Local(envPtr, "p");
        int flag = main.Local(i32Ptr, "flag"), done = main.Local(i32Ptr, "done");
        int code = main.Local(new FunctionType([opaque], unit), "code");
        int handle = main.Local(opaque, "handle");
        int[] threads = [main.Local(IrType.I64, "t0"), main.Local(IrType.I64, "t1"), main.Local(IrType.I64, "t2"), main.Local(IrType.I64, "t3")];
        int i = main.Local(IrType.I32, "i", mutable: true);
        int d = main.Local(IrType.I32, "d", mutable: true);
        int mc = main.Temp(IrType.Bool), wc2 = main.Temp(IrType.Bool);
        int mu1 = main.Temp(unit), mu2 = main.Temp(unit), mu3 = main.Temp(unit);
        int joined = main.Local(IrType.I64, "joined", mutable: true), woken = main.Local(IrType.I64, "woken", mutable: true);
        int count = main.NewBlock(), countBody = main.NewBlock(), release = main.NewBlock(), check = main.NewBlock(), wait = main.NewBlock(), finish = main.NewBlock();
        main.Assign(flag, Rvalue.Alloc(Int(0))).Assign(done, Rvalue.Alloc(Int(0)))
            .Assign(new Place(e).Field(0), Copy(flag)).Assign(new Place(e).Field(1), Copy(done))
            .Assign(new Place(e).Field(2), new IntrinsicRvalue("mutex.new", []))
            .Assign(new Place(e).Field(3), new IntrinsicRvalue("cond.new", []))
            .Assign(p, Rvalue.Alloc(Move(e)))
            .Assign(code, Rvalue.FunctionRef(0))
            .Assign(handle, Rvalue.Cast(Copy(p), opaque));
        foreach (int t in threads)
            main.Assign(t, new IntrinsicRvalue("thread.start", [Copy(code), Copy(handle)]));
        main.Assign(i, Int(0)).Goto(count);
        main.At(count).Assign(mc, Rvalue.Binary(BinaryOp.Lt, Copy(i), Int(20_000))).If(Move(mc), countBody, release);
        main.At(countBody).Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1))).Goto(count);
        main.At(release).Assign(mu1, new IntrinsicRvalue("atomic.store.i32", [Copy(flag), Int(1)]))
            .Assign(mu2, new IntrinsicRvalue("mutex.lock", [Copy(new Place(p).Deref().Field(2))]))
            .Goto(check);
        main.At(check).Assign(d, new IntrinsicRvalue("atomic.load.i32", [Copy(done)]))
            .Assign(wc2, Rvalue.Binary(BinaryOp.Lt, Copy(d), Int(threads.Length))).If(Move(wc2), wait, finish);
        main.At(wait).Assign(woken, new IntrinsicRvalue("cond.wait", [Copy(new Place(p).Deref().Field(3)), Copy(new Place(p).Deref().Field(2)), Int(-1, IrType.I64)]))
            .Goto(check);
        main.At(finish).Assign(mu3, new IntrinsicRvalue("mutex.unlock", [Copy(new Place(p).Deref().Field(2))]));
        foreach (int t in threads)
            main.Assign(joined, new IntrinsicRvalue("thread.join", [Copy(t), Int(-1, IrType.I64)]));
        main.Free(p).Free(flag).Free(done).Assign(FunctionBuilder.ReturnPlace, Copy(d)).Return();

        var vm = Vm(new IrModule([envStruct], [worker.Build(), main.Build()]));
        Check("spin_main() = 4: every worker reported", Equal(vm.Run("spin_main"), 4L), vm.Stats.ToString());
        Check("turn handoffs are a small fraction of the safepoints (a slice per turn, not a handoff per safepoint)",
            vm.Stats.Turns > 0 && vm.Stats.Turns * 50 < vm.Stats.Safepoints, vm.Stats.ToString());
        Check("the environment was freed", Leaks(vm) == 0);
    }

    [Fact]
    public void Deep_recursion_runs_without_the_host_stack_and_the_depth_limit_is_a_vm_error()
    {
        // fn deep(n: i32) -> i32 { if n == 0 { 0 } else { deep(n - 1) + 1 } }
        var deep = new FunctionBuilder("deep", IrType.I32);
        int n = deep.Argument(IrType.I32, "n");
        int c = deep.Temp(IrType.Bool);
        int m = deep.Local(IrType.I32, "m");
        int t = deep.Local(IrType.I32, "t");
        int zero = deep.NewBlock(), recurse = deep.NewBlock(), after = deep.NewBlock();
        deep.Assign(c, Rvalue.Binary(BinaryOp.Eq, Copy(n), Int(0))).If(Move(c), zero, recurse);
        deep.At(zero).Assign(FunctionBuilder.ReturnPlace, Int(0)).Return();
        deep.At(recurse).Assign(m, Rvalue.Binary(BinaryOp.Sub, Copy(n), Int(1))).Call(0, [Copy(m)], t, after);
        deep.At(after).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(t), Int(1))).Return();

        // fn deep_throw(n: i32) -> i32 { if n == 0 { throw 7 } deep_throw(n - 1) }
        var thrower = new FunctionBuilder("deep_throw", IrType.I32);
        int tn = thrower.Argument(IrType.I32, "n");
        int tc = thrower.Temp(IrType.Bool);
        int tm = thrower.Local(IrType.I32, "m");
        int raise = thrower.NewBlock(), descend = thrower.NewBlock(), back = thrower.NewBlock();
        thrower.Assign(tc, Rvalue.Binary(BinaryOp.Eq, Copy(tn), Int(0))).If(Move(tc), raise, descend);
        thrower.At(raise).Throw(Int(7));
        thrower.At(descend).Assign(tm, Rvalue.Binary(BinaryOp.Sub, Copy(tn), Int(1))).Call(1, [Copy(tm)], FunctionBuilder.ReturnPlace, back);
        thrower.At(back).Return();

        // fn catch_deep(n: i32) -> i32 { try { deep_throw(n) } catch (e: i32) { -e } }
        var catcher = new FunctionBuilder("catch_deep", IrType.I32);
        int cn = catcher.Argument(IrType.I32, "n");
        int e = catcher.Local(IrType.I32, "e");
        int ok = catcher.NewBlock(), handler = catcher.NewBlock();
        catcher.Call(1, [], [Copy(cn)], FunctionBuilder.ReturnPlace, ok, [new CatchClause(IrType.I32, handler)]);
        catcher.At(ok).Return();
        catcher.At(handler).Assign(e, Rvalue.Caught).Assign(FunctionBuilder.ReturnPlace, Rvalue.Unary(UnaryOp.Neg, Copy(e))).Return();

        // fn forever(n: i32) -> i32 { forever(n + 1) }
        var forever = new FunctionBuilder("forever", IrType.I32);
        int fn = forever.Argument(IrType.I32, "n");
        int fm = forever.Local(IrType.I32, "m");
        int done = forever.NewBlock();
        forever.Assign(fm, Rvalue.Binary(BinaryOp.Add, Copy(fn), Int(1))).Call(3, [Copy(fm)], FunctionBuilder.ReturnPlace, done);
        forever.At(done).Return();

        var module = new IrModule([], [deep.Build(), thrower.Build(), catcher.Build(), forever.Build()]);
        var vm = Vm(module);
        Check("deep(50000) = 50000 (far beyond what the host stack allows)", Equal(vm.Run("deep", 50_000), 50_000L), vm.Stats.ToString());
        Check("frames released after the recursion", vm.Heap.Live.Count() == 0);
        Check("an exception thrown 20000 frames down is caught at the top: catch_deep(20000) = -7", Equal(vm.Run("catch_deep", 20_000), -7L));
        Check("frames released after unwinding", vm.Heap.Live.Count() == 0);
        Check("uncaught at depth surfaces as UnhandledException", Throws<UnhandledException>(() => vm.Run("deep_throw", 1000), "unhandled"));

        var limited = new Interpreter(Compile(module), options: new InterpreterOptions { MaxCallDepth = 2000 });
        Check("unbounded recursion stops at the depth limit with a VM error, not a host crash",
            Throws<VmException>(() => limited.Run("forever", 0), "call depth"));
        Check("frames released after the depth error", limited.Heap.Live.Count() == 0);
        Check("the interpreter is usable afterwards", Equal(limited.Run("deep", 1500), 1500L));
    }

    [Fact]
    public void A_callee_closing_its_region_keeps_the_callers_region_objects()
    {
        // Both functions number their region 0: the ids are per function, the regions are per activation.
        // fn inner() -> i32 { region { let g = gc Point{1, 2}; g.x } }
        var inner = new FunctionBuilder("inner", IrType.I32);
        int ip = inner.Local(GcPrograms.PointType, "p", mutable: true);
        int ig = inner.Local(IrType.Gc(GcPrograms.PointType), "g");
        int iv = inner.Local(IrType.I32, "v");
        inner.RegionEnter(0)
             .Assign(new Place(ip).Field(0), Int(1)).Assign(new Place(ip).Field(1), Int(2))
             .Assign(ig, Rvalue.Gc(Move(ip)))
             .Assign(iv, Copy(new Place(ig).Deref().Field(0)))
             .RegionExit(0)
             .Assign(FunctionBuilder.ReturnPlace, Copy(iv))
             .Return();

        // fn outer() -> i32 { region { let g = gc Point{40, 0}; let t = inner(); g.x + t } }
        var outer = new FunctionBuilder("outer", IrType.I32);
        int op = outer.Local(GcPrograms.PointType, "p", mutable: true);
        int og = outer.Local(IrType.Gc(GcPrograms.PointType), "g");
        int t = outer.Local(IrType.I32, "t");
        int after = outer.NewBlock();
        outer.RegionEnter(0)
             .Assign(new Place(op).Field(0), Int(40)).Assign(new Place(op).Field(1), Int(0))
             .Assign(og, Rvalue.Gc(Move(op)))
             .Call(0, [], t, after);
        outer.At(after)
             .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(new Place(og).Deref().Field(0)), Copy(t)))
             .RegionExit(0)
             .Return();

        var vm = Vm(new IrModule(GcPrograms.StructDefs, [inner.Build(), outer.Build()]), gc: GcConfig.Arena);
        Check("outer() = 41: inner's region exit freed inner's object only", Equal(vm.Run("outer"), 41L));
        Check("both regions freed their own object", vm.Heap.Live.Count(x => x.Kind == AllocationKind.Gc) == 0 && vm.Heap.TotalFrees == 2, $"frees {vm.Heap.TotalFrees}");
    }

    [Fact]
    public void Marking_a_long_linked_list_does_not_overflow_the_host_stack()
    {
        // fn chain(count: i32) -> i32 { let head: ?gc Node = null; for i in 0..count { head = gc Node{i, head} } head.value }
        var node = IrType.Struct(0);
        var link = new OptionalType(IrType.Gc(node));
        var f = new FunctionBuilder("chain", IrType.I32);
        int count = f.Argument(IrType.I32, "count");
        int head = f.Local(link, "head", mutable: true);
        int n = f.Local(node, "n", mutable: true);
        int h = f.Local(IrType.Gc(node), "h", mutable: true);
        int i = f.Local(IrType.I32, "i", mutable: true);
        int c = f.Temp(IrType.Bool);
        int loop = f.NewBlock(), body = f.NewBlock(), exit = f.NewBlock();
        f.Assign(head, Rvalue.Zero(link)).Assign(i, Int(0)).Goto(loop);
        f.At(loop).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(count))).If(Move(c), body, exit);
        f.At(body).Assign(new Place(n).Field(0), Copy(i)).Assign(new Place(n).Field(1), Move(head))
         .Assign(h, Rvalue.Gc(Move(n))).Assign(head, Rvalue.Wrap(Move(h)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1))).Goto(loop);
        f.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(head).Deref().Field(0))).Return();

        var module = new IrModule([new StructDef("Node", IrType.I32, link)], [f.Build()]);
        var vm = Vm(module, gc: GcConfig.Default);
        Check("chain(30000) = 29999 (the list is marked by collections during the loop)", Equal(vm.Run("chain", 30000), 29999L), vm.Stats.ToString());
        Check("collections happened while the list was live", vm.Stats.Collections > 0, vm.Stats.ToString());
    }

    /// <summary>
    /// Marking drains one work stack: scanning an object only queues what it holds (scanning used to drain the queue
    /// itself, a host call per queued object, so a large array of objects overflowed the host stack). Run on a small
    /// stack; the live set is what collections sweep (garbage from the fill is reclaimed, the array's objects stay).
    /// </summary>
    [Fact]
    public void Marking_a_large_array_of_objects_does_not_overflow_a_small_host_stack()
    {
        // unsafe fn fill(count: i64) -> i32 { let arr = gc [gc Point; count]; for i in 0..count { arr[i] = gc Point{i, i}; gc Point{0, 0}; } arr[count - 1].1 }
        var point = GcPrograms.PointType;
        var gcPoint = IrType.Gc(point);
        var f = new FunctionBuilder("fill", IrType.I32) { Unsafe = true };
        int count = f.Argument(IrType.I64, "count");
        int arr = f.Local(IrType.Gc(new ArrayType(gcPoint)), "arr");
        int i = f.Local(IrType.I64, "i", mutable: true);
        int p = f.Local(point, "p", mutable: true);
        int garbage = f.Local(gcPoint, "garbage", mutable: true);
        int last = f.Local(gcPoint, "last", mutable: true);
        int c = f.Local(IrType.Bool, "c", mutable: true);
        int loop = f.NewBlock(), body = f.NewBlock(), exit = f.NewBlock();
        f.Assign(arr, Rvalue.NewArray(AllocKind.Gc, gcPoint, Copy(count))).Assign(i, Int(0, IrType.I64)).Goto(loop);
        f.At(loop).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(count))).If(Copy(c), body, exit);
        f.At(body).Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
         .Assign(new Place(arr).Deref().Index(i), Rvalue.Gc(Move(p)))
         .Assign(new Place(p).Field(0), Int(0)).Assign(new Place(p).Field(1), Int(0))
         .Assign(garbage, Rvalue.Gc(Move(p)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(loop);
        f.At(exit).Assign(i, Rvalue.Binary(BinaryOp.Sub, Copy(count), Int(1, IrType.I64)))
         .Assign(last, Copy(new Place(arr).Deref().Index(i)))
         .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(last).Deref().Field(1))).Return();

        var vm = Vm(new IrModule(GcPrograms.StructDefs, [f.Build()]), gc: GcConfig.Default);
        object? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = vm.Run("fill", 20000L); }
            catch (Exception e) { failure = e; }
        }, 256 * 1024);
        thread.Start();
        thread.Join();
        Check("fill(20000) = 19999 on a 256 KB host stack", failure is null && Equal(result, 19999L), failure?.Message ?? vm.Stats.ToString());
        Check("collections ran while the array was live and reclaimed the garbage", vm.Stats.Collections > 0 && vm.Stats.Reclaimed > 0, vm.Stats.ToString());
    }

    /// <summary>
    /// mem.copy and mem.cmp work on spans of the backing bytes: overlapping copies forward and backward, the first
    /// difference, a compare that stops at a difference before the end of memory, and a copy past the end that fails
    /// before writing anything.
    /// </summary>
    [Fact]
    public void Mem_copy_and_compare_move_whole_spans()
    {
        var bytes = IrType.PtrMut(IrType.U8);
        var f = new FunctionBuilder("mem", IrType.I64) { Unsafe = true };
        int a = f.Local(IrType.Box(new ArrayType(IrType.U8)), "a", mutable: true);
        int i = f.Local(IrType.I64, "i", mutable: true);
        int c = f.Local(IrType.Bool, "c", mutable: true);
        int unit = f.Local(IrType.Unit, "unit", mutable: true);
        int digits = f.Local(IrType.I64, "digits", mutable: true);
        int fill = f.NewBlock(), store = f.NewBlock(), read = f.NewBlock(), digit = f.NewBlock(), done = f.NewBlock();
        int Pointer(int index)
        {
            int at = f.Temp(IrType.I64), borrow = f.Temp(IrType.RefMut(IrType.U8)), pointer = f.Temp(bytes);
            f.Assign(at, Int(index, IrType.I64)).Assign(borrow, Rvalue.RefMut(new Place(a).Deref().Index(at))).Assign(pointer, Rvalue.Cast(Move(borrow), bytes));
            return pointer;
        }
        void Print(Rvalue value)
        {
            int t = f.Temp(IrType.I64);
            f.Assign(t, value).Assign(unit, new IntrinsicRvalue("print.i64", [Move(t)]));
        }

        f.Assign(a, Rvalue.NewArray(AllocKind.Box, IrType.U8, Int(8, IrType.I64))).Assign(i, Int(0, IrType.I64)).Goto(fill);
        f.At(fill).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Int(8, IrType.I64))).If(Copy(c), store, read);
        int plusOne = f.Temp(IrType.I64);
        f.At(store).Assign(plusOne, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Assign(new Place(a).Deref().Index(i), Rvalue.Cast(Move(plusOne), IrType.U8))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(fill);
        f.At(read);
        int to = Pointer(2), from = Pointer(0);
        f.Assign(unit, new IntrinsicRvalue("mem.copy", [Copy(to), Copy(from), Int(4, IrType.I64)]));      // 1 2 1 2 3 4 7 8 (forward overlap)
        to = Pointer(0);
        from = Pointer(1);
        f.Assign(unit, new IntrinsicRvalue("mem.copy", [Copy(to), Copy(from), Int(4, IrType.I64)]));      // 2 1 2 3 3 4 7 8 (backward overlap)
        int left = Pointer(0), right = Pointer(4);
        Print(new IntrinsicRvalue("mem.cmp", [Copy(left), Copy(right), Int(2, IrType.I64)]));              // 2 - 3 = -1
        left = Pointer(2);
        right = Pointer(0);
        Print(new IntrinsicRvalue("mem.cmp", [Copy(left), Copy(right), Int(2, IrType.I64)]));              // [2 3] vs [2 1]: 3 - 1 = 2
        left = Pointer(4);
        right = Pointer(6);
        Print(new IntrinsicRvalue("mem.cmp", [Copy(left), Copy(right), Int(100, IrType.I64)]));            // differs at once (3 - 7 = -4), never reads past the end
        f.Assign(digits, Int(0, IrType.I64)).Assign(i, Int(0, IrType.I64)).Goto(digit);
        f.At(digit).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Int(8, IrType.I64))).If(Copy(c), done + 1, done);
        f.At(done).Assign(FunctionBuilder.ReturnPlace, Copy(digits)).Return();
        int next = f.NewBlock();
        int element = f.Temp(IrType.U8), wide = f.Temp(IrType.I64), shifted = f.Temp(IrType.I64);
        f.At(next).Assign(element, Copy(new Place(a).Deref().Index(i))).Assign(wide, Rvalue.Cast(Move(element), IrType.I64))
         .Assign(shifted, Rvalue.Binary(BinaryOp.Mul, Copy(digits), Int(10, IrType.I64)))
         .Assign(digits, Rvalue.Binary(BinaryOp.Add, Move(shifted), Move(wide)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(digit);

        var past = new FunctionBuilder("past_the_end", IrType.U8) { Unsafe = true };
        int pa = past.Local(IrType.Box(new ArrayType(IrType.U8)), "a", mutable: true);
        int six = past.Local(IrType.I64, "six"), zero = past.Local(IrType.I64, "zero");
        int r1 = past.Temp(IrType.RefMut(IrType.U8)), r2 = past.Temp(IrType.RefMut(IrType.U8)), p1 = past.Temp(bytes), p2 = past.Temp(bytes);
        int moved = past.Temp(IrType.Unit);
        past.Assign(pa, Rvalue.NewArray(AllocKind.Box, IrType.U8, Int(8, IrType.I64))).Assign(six, Int(6, IrType.I64)).Assign(zero, Int(0, IrType.I64))
            .Assign(r1, Rvalue.RefMut(new Place(pa).Deref().Index(six))).Assign(p1, Rvalue.Cast(Move(r1), bytes))
            .Assign(r2, Rvalue.RefMut(new Place(pa).Deref().Index(zero))).Assign(p2, Rvalue.Cast(Move(r2), bytes))
            .Assign(moved, new IntrinsicRvalue("mem.copy", [Copy(p1), Copy(p2), Int(4, IrType.I64)]))
            .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(pa).Deref().Index(six)))
            .Return();

        var output = new StringWriter();
        // Raw pointers into one array taken one after another: the borrow checker would see overlapping borrows.
        var compiled = Compiler.Compile(new IrModule([], [f.Build(), past.Build()]), new CompilationOptions { BorrowCheck = BorrowCheckMode.Off });
        Check("the program compiles", compiled.Success, string.Join("; ", compiled.TypeErrors.Concat<object>(compiled.Diagnostics)));
        var vm = new Interpreter(compiled.Il!, options: new InterpreterOptions { Output = output });
        Check("overlapping copies forward and backward give 2 1 2 3 3 4 7 8", Equal(vm.Run("mem"), 21233478L));
        Check("mem.cmp gives the first difference: -1, 2, -4", output.ToString() == "-12-4", output.ToString());
        Check("a copy past the end of the array is an error", Throws<VmException>(() => vm.Run("past_the_end"), "overruns"));
    }

    [Fact]
    public void Shift_amounts_are_masked_by_the_operand_width()
    {
        // The native back ends mask the amount (`shl cl` on x64, wasm, LLVM `and`): `1 << -1` is `1 << 31` for i32.
        static IrFunction Shift(string name, BinaryOp op, IrType type, BigInteger left, BigInteger amount)
        {
            var f = new FunctionBuilder(name, type);
            f.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(op, Int(left, type), Int(amount, type))).Return();
            return f.Build();
        }
        var module = new IrModule([],
        [
            Shift("shl_i32_neg", BinaryOp.Shl, IrType.I32, 1, -1),
            Shift("shl_i32_33", BinaryOp.Shl, IrType.I32, 1, 33),
            Shift("shr_i32_neg", BinaryOp.Shr, IrType.I32, -8, -1),
            Shift("shl_i64_neg", BinaryOp.Shl, IrType.I64, 1, -1),
            Shift("shr_u8_9", BinaryOp.Shr, IrType.U8, 0x80, 9),
            Shift("shl_i128_neg", BinaryOp.Shl, IrType.I128, 1, -1),
            Shift("shr_u64_neg", BinaryOp.Shr, IrType.U64, ulong.MaxValue, ulong.MaxValue),
        ]);
        var vm = Vm(module);
        Check("i32: 1 << -1 = 1 << 31", Equal(vm.Run("shl_i32_neg"), (long)int.MinValue));
        Check("i32: 1 << 33 = 1 << 1", Equal(vm.Run("shl_i32_33"), 2L));
        Check("i32: -8 >> -1 = -8 >> 31", Equal(vm.Run("shr_i32_neg"), -1L));
        Check("i64: 1 << -1 = 1 << 63", Equal(vm.Run("shl_i64_neg"), long.MinValue));
        Check("u8: 0x80 >> 9 = 0x80 >> 1", Equal(vm.Run("shr_u8_9"), 64UL));
        Check("i128: 1 << -1 = 1 << 127", Equal(vm.Run("shl_i128_neg"), -(BigInteger.One << 127)));
        Check("u64: max >> max = max >> 63", Equal(vm.Run("shr_u64_neg"), 1UL));
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
