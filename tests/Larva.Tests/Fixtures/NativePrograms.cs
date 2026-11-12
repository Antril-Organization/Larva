using Larva.IR;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>Small programs over GcPrograms' Point that the native back ends (LLVM, x64) run alike.</summary>
internal static class NativePrograms
{
    private static readonly IrType Point = GcPrograms.PointType;

    /// <summary>give(b: box Point) {} and maybe_give_int(c: i32) { let b = box …; if c != 0 { give(b) } }</summary>
    public static IrModule MaybeGive()
    {
        var give = new FunctionBuilder("give", IrType.Unit);
        give.Argument(IrType.Box(Point), "b");
        give.Return();

        var f = new FunctionBuilder("maybe_give_int", IrType.Unit);
        int c = f.Argument(IrType.I32, "c");
        int p = f.Local(Point, "p", mutable: true);
        int bx = f.Local(IrType.Box(Point), "b");
        int test = f.Temp(IrType.Bool);
        int unit = f.Temp(IrType.Unit);
        int then = f.NewBlock(), join = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Assign(bx, Rvalue.Box(Move(p)))
         .Assign(test, Rvalue.Binary(BinaryOp.Ne, Copy(c), Int(0)))
         .If(Move(test), then, join);
        f.At(then).Call(0, [Move(bx)], unit, join);
        f.At(join).Return();
        return new IrModule([GcPrograms.StructDefs[0]], [give.Build(), f.Build()]);
    }

    /// <summary>The reference-counting program of the interpreter tests: make → handles → peek.</summary>
    public static IrModule RcProgram()
    {
        var main = new FunctionBuilder("rc_main", IrType.I32);
        var gcPoint = IrType.Gc(Point);
        int a = main.Local(gcPoint, "a");
        int b = main.Local(gcPoint, "b");
        int b1 = main.NewBlock(), b2 = main.NewBlock(), b3 = main.NewBlock();
        main.Call(4, [], a, b1);
        main.At(b1).Call(5, [Move(a)], b, b2);
        main.At(b2).Call(7, [Copy(b)], FunctionBuilder.ReturnPlace, b3);
        main.At(b3).Return();
        return new IrModule(GcPrograms.StructDefs, [.. GcPrograms.ManagedModule().Functions, main.Build()]);
    }

    /// <summary>
    /// churn(n: i64) -> i64 { for i in 0..n { g = gc Point{i, i} } n }  — garbage at every iteration.
    /// keep(n: i64) -> i32 { let g = gc Point{7, 8}; churn(n); g.y }  — g is only on keep's stack while churn collects.
    /// </summary>
    public static IrModule TracingProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var churn = new FunctionBuilder("churn", IrType.I64);
        int n = churn.Argument(IrType.I64, "n");
        int i = churn.Local(IrType.I64, "i", mutable: true);
        int p = churn.Local(Point, "p", mutable: true);
        int g = churn.Local(gcPoint, "g", mutable: true);
        int c = churn.Local(IrType.Bool, "c", mutable: true);
        int head = churn.NewBlock(), body = churn.NewBlock(), exit = churn.NewBlock();
        churn.Assign(i, Int(0, IrType.I64)).Goto(head);
        churn.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), body, exit);
        churn.At(body)
             .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
             .Assign(g, Rvalue.Gc(Move(p)))
             .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        churn.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(n)).Return();

        var keep = new FunctionBuilder("keep", IrType.I32);
        int kn = keep.Argument(IrType.I64, "n");
        int kp = keep.Local(Point, "p", mutable: true);
        int kg = keep.Local(gcPoint, "g");
        int t = keep.Local(IrType.I64, "t");
        int after = keep.NewBlock();
        keep.Assign(new Place(kp).Field(0), Int(7)).Assign(new Place(kp).Field(1), Int(8))
            .Assign(kg, Rvalue.Gc(Move(kp)))
            .Call(0, [Copy(kn)], t, after);
        keep.At(after).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(kg).Deref().Field(1))).Return();
        return new IrModule(GcPrograms.StructDefs, [churn.Build(), keep.Build()]);
    }

    public static IrModule RegionProgram()
    {
        var f = new FunctionBuilder("in_region", IrType.I32);
        int p = f.Local(Point, "p", mutable: true);
        int g = f.Local(IrType.Gc(Point), "g");
        int v = f.Local(IrType.I32, "v");
        f.RegionEnter(0)
         .Assign(new Place(p).Field(0), Int(4)).Assign(new Place(p).Field(1), Int(5))
         .Assign(g, Rvalue.Gc(Move(p)))
         .Assign(v, Copy(new Place(g).Deref().Field(0)))
         .RegionExit(0)
         .Assign(FunctionBuilder.ReturnPlace, Copy(v))
         .Return();
        return new IrModule(GcPrograms.StructDefs, [f.Build()]);
    }

    /// <summary>
    /// Young objects the ambiguous roots pin (functions 0..3; globals 0 = finalized: i32, 1 = latest: gc Point):
    /// pin_fin(dead: gc Point) { finalized += 1 } — the finalizer;
    /// pinned_churn(n: i64) -> i32 { for i in 0..n { latest = gc Point{i, i} } latest.y }  — the global's cell (a root the
    /// collector scans ambiguously) holds the newest object, so every minor collection pins one and retains its block (→ n - 1);
    /// finalized_alive(n: i64) -> i32 { let keep = gc Point{7,8} finalized pin_fin; churn(n); finalized * 10 + keep.y }  — keep is
    /// pinned by the first minor collection and must not be finalized while finalized_alive still holds it (→ 8);
    /// churn(n: i64) -> i64 — garbage at every iteration.
    /// </summary>
    public static IrModule PinnedProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var fin = new FunctionBuilder("pin_fin", IrType.Unit);
        fin.Argument(gcPoint, "dead");
        int count = fin.Local(IrType.I32, "count", mutable: true);
        fin.Assign(count, new GlobalRvalue(0)).Assign(count, Rvalue.Binary(BinaryOp.Add, Copy(count), Int(1))).StoreGlobal(0, Copy(count)).Return();

        var pinned = new FunctionBuilder("pinned_churn", IrType.I32);
        int n = pinned.Argument(IrType.I64, "n");
        int i = pinned.Local(IrType.I64, "i", mutable: true);
        int p = pinned.Local(Point, "p", mutable: true);
        int g = pinned.Local(gcPoint, "g", mutable: true);
        int c = pinned.Local(IrType.Bool, "c", mutable: true);
        int head = pinned.NewBlock(), body = pinned.NewBlock(), exit = pinned.NewBlock();
        pinned.Assign(i, Int(0, IrType.I64)).Goto(head);
        pinned.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), body, exit);
        pinned.At(body)
              .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
              .Assign(g, Rvalue.Gc(Move(p)))
              .StoreGlobal(1, Copy(g))
              .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        pinned.At(exit).Assign(g, new GlobalRvalue(1)).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(g).Deref().Field(1))).Return();

        var alive = new FunctionBuilder("finalized_alive", IrType.I32);
        int an = alive.Argument(IrType.I64, "n");
        int ap = alive.Local(Point, "p", mutable: true);
        int finalizer = alive.Local(new FunctionType([gcPoint], IrType.Unit), "finalizer", mutable: true);
        int keep = alive.Local(gcPoint, "keep");
        int t = alive.Local(IrType.I64, "t");
        int finalized = alive.Local(IrType.I32, "finalized", mutable: true);
        int after = alive.NewBlock();
        alive.Assign(new Place(ap).Field(0), Int(7)).Assign(new Place(ap).Field(1), Int(8))
             .Assign(finalizer, Rvalue.FunctionRef(0))
             .Assign(keep, Rvalue.Gc(Move(ap), Copy(finalizer)))
             .Call(3, [Copy(an)], t, after);
        alive.At(after)
             .Assign(finalized, new GlobalRvalue(0))
             .Assign(finalized, Rvalue.Binary(BinaryOp.Mul, Copy(finalized), Int(10)))
             .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(finalized), Copy(new Place(keep).Deref().Field(1))))
             .Return();

        var churn = new FunctionBuilder("churn", IrType.I64);
        int cn = churn.Argument(IrType.I64, "n");
        int ci = churn.Local(IrType.I64, "i", mutable: true);
        int cp = churn.Local(Point, "p", mutable: true);
        int cg = churn.Local(gcPoint, "g", mutable: true);
        int cc = churn.Local(IrType.Bool, "c", mutable: true);
        int chead = churn.NewBlock(), cbody = churn.NewBlock(), cexit = churn.NewBlock();
        churn.Assign(ci, Int(0, IrType.I64)).Goto(chead);
        churn.At(chead).Assign(cc, Rvalue.Binary(BinaryOp.Lt, Copy(ci), Copy(cn))).If(Copy(cc), cbody, cexit);
        churn.At(cbody)
             .Assign(new Place(cp).Field(0), Rvalue.Cast(Copy(ci), IrType.I32)).Assign(new Place(cp).Field(1), Rvalue.Cast(Copy(ci), IrType.I32))
             .Assign(cg, Rvalue.Gc(Move(cp)))
             .Assign(ci, Rvalue.Binary(BinaryOp.Add, Copy(ci), Int(1, IrType.I64))).Goto(chead);
        churn.At(cexit).Assign(FunctionBuilder.ReturnPlace, Copy(cn)).Return();

        return new IrModule(GcPrograms.StructDefs, [fin.Build(), pinned.Build(), alive.Build(), churn.Build()])
            { Globals = [new GlobalDef("finalized", IrType.I32), new GlobalDef("latest", gcPoint)] };
    }

    /// <summary>
    /// Reference counting from two threads (functions 0..1):
    /// rc_worker(env: ptr unit) { for i in 0..1000000 { let g = gc Point{i, i}; let h = g } }  — every object is retained
    /// (the copy) and released twice, inline in compiled code, and freed by the runtime when its count reaches 0;
    /// rc_threads(n: i64) -> i32 { let a = thread.start(rc_worker, null); let b = thread.start(rc_worker, null); join a; join b; n }
    /// — the counts and the runtime's dead list are shared between the threads.
    /// </summary>
    public static IrModule RcThreadsProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var environment = IrType.Ptr(IrType.Unit);
        var worker = new FunctionBuilder("rc_worker", IrType.Unit);
        worker.Argument(environment, "env");
        int i = worker.Local(IrType.I64, "i", mutable: true);
        int p = worker.Local(Point, "p", mutable: true);
        int g = worker.Local(gcPoint, "g", mutable: true);
        int h = worker.Local(gcPoint, "h", mutable: true);
        int c = worker.Local(IrType.Bool, "c", mutable: true);
        int head = worker.NewBlock(), body = worker.NewBlock(), exit = worker.NewBlock();
        worker.Assign(i, Int(0, IrType.I64)).Goto(head);
        worker.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Int(1000000, IrType.I64))).If(Copy(c), body, exit);
        worker.At(body)
              .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
              .Assign(g, Rvalue.Gc(Move(p)))
              .Assign(h, Copy(g))
              .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        worker.At(exit).Return();

        var main = new FunctionBuilder("rc_threads", IrType.I32) { Unsafe = true };
        int n = main.Argument(IrType.I64, "n");
        int code = main.Local(new FunctionType([environment], IrType.Unit), "code", mutable: true);
        int zero = main.Local(IrType.U64, "zero");
        int env = main.Local(environment, "env");
        int a = main.Local(IrType.I64, "a"), b = main.Local(IrType.I64, "b");
        int joined = main.Local(IrType.I64, "joined", mutable: true);
        main.Assign(code, Rvalue.FunctionRef(0))
            .Assign(zero, Int(0, IrType.U64))
            .Assign(env, Rvalue.Cast(Copy(zero), environment))
            .Assign(a, new IntrinsicRvalue("thread.start", [Copy(code), Copy(env)]))
            .Assign(b, new IntrinsicRvalue("thread.start", [Copy(code), Copy(env)]))
            .Assign(joined, new IntrinsicRvalue("thread.join", [Copy(a), Int(-1, IrType.I64)]))
            .Assign(joined, new IntrinsicRvalue("thread.join", [Copy(b), Int(-1, IrType.I64)]))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(n), IrType.I32))
            .Return();
        return new IrModule(GcPrograms.StructDefs, [worker.Build(), main.Build()]);
    }

    /// <summary>
    /// unsafe copy_churn(n: i64) -> i32 { let arr = gc [gc Point; 100000]; for i in 0..n { let tmp = gc [gc Point; 1]; tmp[0] = gc Point{i,i};
    /// mem.copy(&amp;arr[0], &amp;tmp[0], 8) } arr[0].y }  — every iteration copies a young handle into the old array by a raw copy
    /// (mem.copy, not a store: no write barrier in compiled code), so the runtime's copy must card the array, or the minor
    /// collections the churn triggers leave arr[0] pointing into the nursery (→ n - 1; LARVA_GC_VERIFY=1 reports the slot).
    /// </summary>
    public static IrModule CopyBarrierProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var pointer = IrType.PtrMut(IrType.U8);
        var f = new FunctionBuilder("copy_churn", IrType.I32) { Unsafe = true };
        int n = f.Argument(IrType.I64, "n");
        int i = f.Local(IrType.I64, "i", mutable: true);
        int zero = f.Local(IrType.I64, "zero");
        int p = f.Local(Point, "p", mutable: true);
        int arr = f.Local(IrType.Gc(new ArrayType(gcPoint)), "arr");
        int tmp = f.Local(IrType.Gc(new ArrayType(gcPoint)), "tmp", mutable: true);
        int targetRef = f.Local(IrType.RefMut(gcPoint), "target_ref", mutable: true), sourceRef = f.Local(IrType.RefMut(gcPoint), "source_ref", mutable: true);
        int target = f.Local(pointer, "target", mutable: true), source = f.Local(pointer, "source", mutable: true);
        int moved = f.Local(IrType.Unit, "moved", mutable: true);
        int last = f.Local(gcPoint, "last", mutable: true);
        int c = f.Local(IrType.Bool, "c", mutable: true);
        int head = f.NewBlock(), body = f.NewBlock(), exit = f.NewBlock();
        f.Assign(zero, Int(0, IrType.I64))
         .Assign(arr, Rvalue.NewArray(AllocKind.Gc, gcPoint, Int(100000, IrType.I64)))
         .Assign(i, Int(0, IrType.I64)).Goto(head);
        f.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), body, exit);
        f.At(body)
         .Assign(tmp, Rvalue.NewArray(AllocKind.Gc, gcPoint, Int(1, IrType.I64)))
         .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
         .Assign(new Place(tmp).Deref().Index(zero), Rvalue.Gc(Move(p)))
         .Assign(targetRef, Rvalue.RefMut(new Place(arr).Deref().Index(zero)))
         .Assign(target, Rvalue.Cast(Move(targetRef), pointer))
         .Assign(sourceRef, Rvalue.RefMut(new Place(tmp).Deref().Index(zero)))
         .Assign(source, Rvalue.Cast(Move(sourceRef), pointer))
         .Assign(moved, new IntrinsicRvalue("mem.copy", [Copy(target), Copy(source), Int(8, IrType.I64)]))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        f.At(exit).Assign(last, Copy(new Place(arr).Deref().Index(zero))).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(last).Deref().Field(1))).Return();
        return new IrModule(GcPrograms.StructDefs, [f.Build()]);
    }

    /// <summary>
    /// unsafe remember_churn(n: i64) -> i32 { let mut last; for i in 0..n { let arr = gc [gc Point; 100000]; arr[0] = gc Point{i,i}; last = arr[0] } last.y }
    /// — every iteration stores a handle into a large array (800 KB: old at once, its memory given back to the system when
    /// it dies), so the write barrier lists it (cards), and it dies before the next collection (→ n - 1).
    /// </summary>
    public static IrModule RememberProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var f = new FunctionBuilder("remember_churn", IrType.I32) { Unsafe = true };
        int n = f.Argument(IrType.I64, "n");
        int i = f.Local(IrType.I64, "i", mutable: true);
        int zero = f.Local(IrType.I64, "zero");
        int p = f.Local(Point, "p", mutable: true);
        int arr = f.Local(IrType.Gc(new ArrayType(gcPoint)), "arr", mutable: true);
        int last = f.Local(gcPoint, "last", mutable: true);
        int c = f.Local(IrType.Bool, "c", mutable: true);
        int head = f.NewBlock(), body = f.NewBlock(), exit = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(0)).Assign(new Place(p).Field(1), Int(0))
         .Assign(last, Rvalue.Gc(Move(p)))
         .Assign(zero, Int(0, IrType.I64))
         .Assign(i, Int(0, IrType.I64)).Goto(head);
        f.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), body, exit);
        f.At(body)
         .Assign(arr, Rvalue.NewArray(AllocKind.Gc, gcPoint, Int(100000, IrType.I64)))
         .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
         .Assign(new Place(arr).Deref().Index(zero), Rvalue.Gc(Move(p)))
         .Assign(last, Copy(new Place(arr).Deref().Index(zero)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        f.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(last).Deref().Field(1))).Return();
        return new IrModule(GcPrograms.StructDefs, [f.Build()]);
    }
}
