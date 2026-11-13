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

    /// <summary>
    /// Reference counting, the order of a store: rc_store_order() overwrites a Holder's counted field and a counted global;
    /// the finalizers of the values that die there look at the field (through global 0) and at the global (2) and
    /// record in global 1 whether they still find the dying object (1) or the new one (2): 22 when the stores happen
    /// before the releases.
    /// </summary>
    public static IrModule RcStoreOrder()
    {
        var gcPoint = IrType.Gc(Point);
        var holderType = IrType.Struct(1);
        var gcHolder = IrType.Gc(holderType);

        // fin_field(dead) / fin_global(dead): seen = seen * 10 + (what is there now is dead ? 1 : 2)
        FunctionBuilder Finalizer(string name, Action<FunctionBuilder, int> current)
        {
            var fin = new FunctionBuilder(name, IrType.Unit) { Unsafe = true };
            int dead = fin.Argument(gcPoint, "dead");
            int now = fin.Local(gcPoint, "now", mutable: true);
            current(fin, now);
            int a = fin.Local(IrType.U64, "a", mutable: true), b = fin.Local(IrType.U64, "b", mutable: true);
            int same = fin.Local(IrType.Bool, "same", mutable: true), bit = fin.Local(IrType.I32, "bit", mutable: true);
            int seen = fin.Local(IrType.I32, "seen", mutable: true);
            fin.Assign(a, Rvalue.Cast(Copy(now), IrType.U64)).Assign(b, Rvalue.Cast(Copy(dead), IrType.U64))
               .Assign(same, Rvalue.Binary(BinaryOp.Eq, Copy(a), Copy(b)))
               .Assign(bit, Rvalue.Cast(Copy(same), IrType.I32))
               .Assign(seen, new GlobalRvalue(1))
               .Assign(seen, Rvalue.Binary(BinaryOp.Mul, Copy(seen), Int(10)))
               .Assign(seen, Rvalue.Binary(BinaryOp.Add, Copy(seen), Int(2)))
               .Assign(seen, Rvalue.Binary(BinaryOp.Sub, Copy(seen), Copy(bit)))
               .StoreGlobal(1, Copy(seen))
               .Return();
            return fin;
        }
        var finField = Finalizer("fin_field", (fin, now) =>
        {
            int holder = fin.Local(gcHolder, "holder", mutable: true);
            fin.Assign(holder, new GlobalRvalue(0)).Assign(now, Copy(new Place(holder).Deref().Field(0)));
        });
        var finGlobal = Finalizer("fin_global", (fin, now) => fin.Assign(now, new GlobalRvalue(2)));

        // make_field(x) / make_global(x): a counted Point {x, x} with that finalizer.
        FunctionBuilder Maker(string name, int finalizer)
        {
            var make = new FunctionBuilder(name, gcPoint);
            int x = make.Argument(IrType.I32, "x");
            int p = make.Local(Point, "p", mutable: true);
            int f = make.Local(new FunctionType([gcPoint], IrType.Unit), "finalizer", mutable: true);
            make.Assign(new Place(p).Field(0), Copy(x)).Assign(new Place(p).Field(1), Copy(x))
                .Assign(f, new FunctionRefRvalue(finalizer, []))
                .Assign(FunctionBuilder.ReturnPlace, new AllocRvalue(AllocKind.Gc, Move(p), Copy(f)))
                .Return();
            return make;
        }
        var makeField = Maker("make_field", 0);
        var makeGlobal = Maker("make_global", 1);

        var main = new FunctionBuilder("rc_store_order", IrType.I32) { Unsafe = true };
        int c = main.Local(gcPoint, "c", mutable: true);
        int value = main.Local(holderType, "value", mutable: true);
        int holder = main.Local(gcHolder, "holder", mutable: true);
        int[] next = [.. Enumerable.Range(0, 4).Select(_ => main.NewBlock())];
        main.Call(2, [Int(1)], c, next[0]);
        main.At(next[0]).Assign(new Place(value).Field(0), Move(c)).Assign(new Place(value).Field(1), Int(0))
            .Assign(holder, new AllocRvalue(AllocKind.Gc, Move(value))).StoreGlobal(0, Copy(holder))
            .Call(2, [Int(2)], c, next[1]);
        // Point 1 dies here: its finalizer must find Point 2 in the field.
        main.At(next[1]).Assign(new Place(holder).Deref().Field(0), Move(c)).Call(3, [Int(3)], c, next[2]);
        main.At(next[2]).StoreGlobal(2, Move(c)).Call(3, [Int(4)], c, next[3]);
        // Point 3 dies here: its finalizer must find Point 4 in the global.
        main.At(next[3]).StoreGlobal(2, Move(c)).Assign(FunctionBuilder.ReturnPlace, new GlobalRvalue(1)).Return();

        return new IrModule(GcPrograms.StructDefs, [finField.Build(), finGlobal.Build(), makeField.Build(), makeGlobal.Build(), main.Build()])
            { Globals = [new GlobalDef("holder", gcHolder), new GlobalDef("seen", IrType.I32), new GlobalDef("g", gcPoint)] };
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
    /// compare(n: i64) -> i64 { let a = gc Point{1,1}; k = 0; for i in 0..n { let c = gc Point{i,i}; if c == a { k += 1 } if c != a { k += 1 } } k }
    /// — reference counting: a comparison keeps no reference, so every c dies at the end of its pass (→ n, nothing live after).
    /// </summary>
    public static IrModule ComparisonProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var f = new FunctionBuilder("compare", IrType.I64);
        int n = f.Argument(IrType.I64, "n");
        int i = f.Local(IrType.I64, "i", mutable: true);
        int k = f.Local(IrType.I64, "k", mutable: true);
        int p = f.Local(Point, "p", mutable: true);
        int a = f.Local(gcPoint, "a");
        int c = f.Local(gcPoint, "c", mutable: true);
        int same = f.Local(IrType.Bool, "same", mutable: true);
        int differ = f.Local(IrType.Bool, "differ", mutable: true);
        int go = f.Local(IrType.Bool, "go", mutable: true);
        int head = f.NewBlock(), body = f.NewBlock(), equal = f.NewBlock(), second = f.NewBlock(), unequal = f.NewBlock(), next = f.NewBlock(), exit = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(1))
         .Assign(a, Rvalue.Gc(Move(p)))
         .Assign(k, Int(0, IrType.I64)).Assign(i, Int(0, IrType.I64)).Goto(head);
        f.At(head).Assign(go, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(go), body, exit);
        f.At(body)
         .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
         .Assign(c, Rvalue.Gc(Move(p)))
         .Assign(same, Rvalue.Binary(BinaryOp.Eq, Copy(c), Copy(a))).If(Copy(same), equal, second);
        f.At(equal).Assign(k, Rvalue.Binary(BinaryOp.Add, Copy(k), Int(1, IrType.I64))).Goto(second);
        f.At(second).Assign(differ, Rvalue.Binary(BinaryOp.Ne, Copy(c), Copy(a))).If(Copy(differ), unequal, next);
        f.At(unequal).Assign(k, Rvalue.Binary(BinaryOp.Add, Copy(k), Int(1, IrType.I64))).Goto(next);
        f.At(next).Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        f.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(k)).Return();
        return new IrModule(GcPrograms.StructDefs, [f.Build()]);
    }

    /// <summary>
    /// A thread blocked reading the standard input while the main thread collects (functions 0..1):
    /// read_worker(env: ptr unit) { read.line }  — blocks until a line comes (the test never sends one);
    /// read_blocked(n: i64) -> i32 { thread.start(read_worker, null); time.sleep(300); for i in 0..n { gc Point{i,i} }; gc.request; n }
    /// — the collections the churn triggers must not wait for the reader (it is out of managed code while it waits).
    /// </summary>
    public static IrModule BlockedReaderProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var environment = IrType.Ptr(IrType.Unit);
        var worker = new FunctionBuilder("read_worker", IrType.Unit);
        worker.Argument(environment, "env");
        int line = worker.Local(IrType.I64, "line", mutable: true);
        worker.Assign(line, new IntrinsicRvalue("read.line", [])).Return();

        var main = new FunctionBuilder("read_blocked", IrType.I32) { Unsafe = true };
        int n = main.Argument(IrType.I64, "n");
        int code = main.Local(new FunctionType([environment], IrType.Unit), "code", mutable: true);
        int zero = main.Local(IrType.U64, "zero");
        int env = main.Local(environment, "env");
        int thread = main.Local(IrType.I64, "thread");
        int unit = main.Local(IrType.Unit, "unit", mutable: true);
        int i = main.Local(IrType.I64, "i", mutable: true);
        int p = main.Local(Point, "p", mutable: true);
        int g = main.Local(gcPoint, "g", mutable: true);
        int c = main.Local(IrType.Bool, "c", mutable: true);
        int head = main.NewBlock(), body = main.NewBlock(), exit = main.NewBlock();
        main.Assign(code, Rvalue.FunctionRef(0))
            .Assign(zero, Int(0, IrType.U64))
            .Assign(env, Rvalue.Cast(Copy(zero), environment))
            .Assign(thread, new IntrinsicRvalue("thread.start", [Copy(code), Copy(env)]))
            .Assign(unit, new IntrinsicRvalue("time.sleep", [Int(300, IrType.I64)]))
            .Assign(i, Int(0, IrType.I64)).Goto(head);
        main.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), body, exit);
        main.At(body)
            .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
            .Assign(g, Rvalue.Gc(Move(p)))
            .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        main.At(exit).Assign(unit, new IntrinsicRvalue("gc.request", [])).Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(n), IrType.I32)).Return();
        return new IrModule(GcPrograms.StructDefs, [worker.Build(), main.Build()]);
    }

    /// <summary>
    /// Two threads listing a folder at once (functions 0..1):
    /// list_worker(env: ptr unit) { let path = gc [u16; 1]; path[0] = '.'; for i in 0..200 { dir.list(&amp;path) } }
    /// list_threads(n: i64) -> i32 { let a = thread.start(list_worker, null); let b = ...; join a; join b; n }
    /// — the listing's buffer is per thread; a capacity shared between threads made the second one write through a null buffer.
    /// </summary>
    public static IrModule DirectoryThreadsProgram()
    {
        var environment = IrType.Ptr(IrType.Unit);
        var units = new ArrayType(IrType.U16);
        var worker = new FunctionBuilder("list_worker", IrType.Unit) { Unsafe = true };
        worker.Argument(environment, "env");
        int path = worker.Local(IrType.Gc(units), "path");
        int zero = worker.Local(IrType.I64, "zero");
        int text = worker.Local(IrType.Ref(units), "text", mutable: true);
        int count = worker.Local(IrType.I64, "count", mutable: true);
        int i = worker.Local(IrType.I64, "i", mutable: true);
        int c = worker.Local(IrType.Bool, "c", mutable: true);
        int head = worker.NewBlock(), body = worker.NewBlock(), exit = worker.NewBlock();
        worker.Assign(zero, Int(0, IrType.I64))
              .Assign(path, Rvalue.NewArray(AllocKind.Gc, IrType.U16, Int(1, IrType.I64)))
              .Assign(new Place(path).Deref().Index(zero), Int('.', IrType.U16))
              .Assign(i, Int(0, IrType.I64)).Goto(head);
        worker.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Int(200, IrType.I64))).If(Copy(c), body, exit);
        worker.At(body)
              .Assign(text, Rvalue.Ref(new Place(path).Deref()))
              .Assign(count, new IntrinsicRvalue("dir.list", [Move(text)]))
              .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        worker.At(exit).Return();

        var main = new FunctionBuilder("list_threads", IrType.I32) { Unsafe = true };
        int n = main.Argument(IrType.I64, "n");
        int code = main.Local(new FunctionType([environment], IrType.Unit), "code", mutable: true);
        int mzero = main.Local(IrType.U64, "zero");
        int env = main.Local(environment, "env");
        int a = main.Local(IrType.I64, "a"), b = main.Local(IrType.I64, "b");
        int joined = main.Local(IrType.I64, "joined", mutable: true);
        main.Assign(code, Rvalue.FunctionRef(0))
            .Assign(mzero, Int(0, IrType.U64))
            .Assign(env, Rvalue.Cast(Copy(mzero), environment))
            .Assign(a, new IntrinsicRvalue("thread.start", [Copy(code), Copy(env)]))
            .Assign(b, new IntrinsicRvalue("thread.start", [Copy(code), Copy(env)]))
            .Assign(joined, new IntrinsicRvalue("thread.join", [Copy(a), Int(-1, IrType.I64)]))
            .Assign(joined, new IntrinsicRvalue("thread.join", [Copy(b), Int(-1, IrType.I64)]))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(n), IrType.I32))
            .Return();
        return new IrModule([], [worker.Build(), main.Build()]);
    }

    /// <summary>
    /// bump(c: &mut Point) { c.y = c.y + 1 } and unsafe pin_loop(n: i64) -> i32 { let h = gc Holder{gc Point{0,0}, 0}; for i in 0..n { bump(&mut *(*h).0) } (*(*h).0).1 }
    /// — a borrow inside a gc object pins it: once per activation of the site, not once per pass (→ n).
    /// </summary>
    public static IrModule PinLoopProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var bump = new FunctionBuilder("bump", IrType.Unit);
        int c = bump.Argument(IrType.RefMut(Point), "c");
        bump.Assign(new Place(c).Deref().Field(1), Rvalue.Binary(BinaryOp.Add, Copy(new Place(c).Deref().Field(1)), Int(1))).Return();

        var f = new FunctionBuilder("pin_loop", IrType.I32) { Unsafe = true };
        int n = f.Argument(IrType.I64, "n");
        int i = f.Local(IrType.I64, "i", mutable: true);
        int p = f.Local(Point, "p", mutable: true);
        int holder = f.Local(GcPrograms.HolderType, "holder", mutable: true);
        int h = f.Local(IrType.Gc(GcPrograms.HolderType), "h");
        int r = f.Local(IrType.RefMut(Point), "r", mutable: true);
        int go = f.Local(IrType.Bool, "go", mutable: true);
        int unit = f.Temp(IrType.Unit);
        int head = f.NewBlock(), body = f.NewBlock(), next = f.NewBlock(), exit = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(0)).Assign(new Place(p).Field(1), Int(0))
         .Assign(new Place(holder).Field(0), Rvalue.Gc(Move(p))).Assign(new Place(holder).Field(1), Int(0))
         .Assign(h, Rvalue.Gc(Move(holder)))
         .Assign(i, Int(0, IrType.I64)).Goto(head);
        f.At(head).Assign(go, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(go), body, exit);
        f.At(body).Assign(r, Rvalue.RefMut(new Place(h).Deref().Field(0).Deref())).Call(0, [Move(r)], unit, next);
        f.At(next).Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        f.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(h).Deref().Field(0).Deref().Field(1))).Return();
        return new IrModule(GcPrograms.StructDefs, [bump.Build(), f.Build()]);
    }

    /// <summary>
    /// put(slot: &mut gc Point, v: gc Point) { *slot = v } — a store through a reference, whose owner put cannot name;
    /// unsafe keep_through_ref(n: i64) -> i32 { let h = gc Holder{gc Point{1,2}, 0}; churn(n); put(&mut (*h).0, gc Point{42,43}); churn(n); (*(*h).0).1 }
    /// — h is old by the time the young Point is stored into it through the reference (→ 43 only if the store was remembered).
    /// </summary>
    public static IrModule BarrierProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var churn = TracingProgram().Functions[0];

        var put = new FunctionBuilder("put", IrType.Unit);
        int slot = put.Argument(IrType.RefMut(gcPoint), "slot");
        int v = put.Argument(gcPoint, "v");
        put.Assign(new Place(slot).Deref(), Move(v)).Return();

        var f = new FunctionBuilder("keep_through_ref", IrType.I32) { Unsafe = true };
        int n = f.Argument(IrType.I64, "n");
        int p = f.Local(Point, "p", mutable: true);
        int holder = f.Local(GcPrograms.HolderType, "holder", mutable: true);
        int h = f.Local(IrType.Gc(GcPrograms.HolderType), "h");
        int young = f.Local(gcPoint, "young");
        int r = f.Local(IrType.RefMut(gcPoint), "r");
        int t = f.Local(IrType.I64, "t", mutable: true);
        int unit = f.Temp(IrType.Unit);
        int b1 = f.NewBlock(), b2 = f.NewBlock(), b3 = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Assign(new Place(holder).Field(0), Rvalue.Gc(Move(p))).Assign(new Place(holder).Field(1), Int(0))
         .Assign(h, Rvalue.Gc(Move(holder)))
         .Call(0, [Copy(n)], t, b1);
        f.At(b1).Assign(new Place(p).Field(0), Int(42)).Assign(new Place(p).Field(1), Int(43))
         .Assign(young, Rvalue.Gc(Move(p)))
         .Assign(r, Rvalue.RefMut(new Place(h).Deref().Field(0)))
         .Call(1, [Move(r), Move(young)], unit, b2);
        f.At(b2).Call(0, [Copy(n)], t, b3);
        f.At(b3).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(h).Deref().Field(0).Deref().Field(1))).Return();
        return new IrModule(GcPrograms.StructDefs, [churn, put.Build(), f.Build()]);
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

    /// <summary>
    /// Finalizers in a program that started a thread (functions 0..3; global 0 = finalized: i32):
    /// fin_count(dead: gc Point) { finalized += 1 } — the finalizer;
    /// noop_worker(env: ptr unit) {} — the thread's code;
    /// make_garbage(n: i64) -> i64 { for i in 0..n { let g = gc Point{i, i} finalized fin_count } n } — garbage with a finalizer;
    /// finalized_after_thread(n: i64) -> i32 { join thread.start(noop_worker, null); make_garbage(n); gc.request; make_garbage(0); finalized }
    /// — once a thread ran, the main thread fills its own nursery block: the objects it allocates there must still be
    /// recognised as gc objects (their finalizers registered) and finalized by the collection (→ about n).
    /// </summary>
    public static IrModule FinalizedAfterThreadProgram()
    {
        var gcPoint = IrType.Gc(Point);
        var environment = IrType.Ptr(IrType.Unit);
        var fin = new FunctionBuilder("fin_count", IrType.Unit);
        fin.Argument(gcPoint, "dead");
        int count = fin.Local(IrType.I32, "count", mutable: true);
        fin.Assign(count, new GlobalRvalue(0)).Assign(count, Rvalue.Binary(BinaryOp.Add, Copy(count), Int(1))).StoreGlobal(0, Copy(count)).Return();

        var worker = new FunctionBuilder("noop_worker", IrType.Unit);
        worker.Argument(environment, "env");
        worker.Return();

        var garbage = new FunctionBuilder("make_garbage", IrType.I64);
        int n = garbage.Argument(IrType.I64, "n");
        int i = garbage.Local(IrType.I64, "i", mutable: true);
        int p = garbage.Local(Point, "p", mutable: true);
        int finalizer = garbage.Local(new FunctionType([gcPoint], IrType.Unit), "finalizer", mutable: true);
        int g = garbage.Local(gcPoint, "g", mutable: true);
        int c = garbage.Local(IrType.Bool, "c", mutable: true);
        int head = garbage.NewBlock(), body = garbage.NewBlock(), exit = garbage.NewBlock();
        garbage.Assign(i, Int(0, IrType.I64)).Assign(finalizer, Rvalue.FunctionRef(0)).Goto(head);
        garbage.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), body, exit);
        garbage.At(body)
               .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
               .Assign(g, Rvalue.Gc(Move(p), Copy(finalizer)))
               .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        garbage.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(n)).Return();

        var main = new FunctionBuilder("finalized_after_thread", IrType.I32) { Unsafe = true };
        int mn = main.Argument(IrType.I64, "n");
        int code = main.Local(new FunctionType([environment], IrType.Unit), "code", mutable: true);
        int zero = main.Local(IrType.U64, "zero");
        int env = main.Local(environment, "env");
        int t = main.Local(IrType.I64, "t");
        int joined = main.Local(IrType.I64, "joined", mutable: true);
        int made = main.Local(IrType.I64, "made", mutable: true);
        int unit = main.Local(IrType.Unit, null, mutable: true);
        int finalized = main.Local(IrType.I32, "finalized", mutable: true);
        int b1 = main.NewBlock(), b2 = main.NewBlock();
        main.Assign(code, Rvalue.FunctionRef(1))
            .Assign(zero, Int(0, IrType.U64))
            .Assign(env, Rvalue.Cast(Copy(zero), environment))
            .Assign(t, new IntrinsicRvalue("thread.start", [Copy(code), Copy(env)]))
            .Assign(joined, new IntrinsicRvalue("thread.join", [Copy(t), Int(-1, IrType.I64)]))
            .Call(2, [Copy(mn)], made, b1);
        main.At(b1).Assign(unit, new IntrinsicRvalue("gc.request", [])).Call(2, [Int(0, IrType.I64)], made, b2);
        main.At(b2).Assign(finalized, new GlobalRvalue(0)).Assign(FunctionBuilder.ReturnPlace, Copy(finalized)).Return();

        return new IrModule(GcPrograms.StructDefs, [fin.Build(), worker.Build(), garbage.Build(), main.Build()])
            { Globals = [new GlobalDef("finalized", IrType.I32)] };
    }

    /// <summary>
    /// A program large enough for the LLVM back end to compile in partitions: <c>twice(x) = x * 2</c> (function 0, small),
    /// <paramref name="count"/> fillers <c>filler_k(n)</c> (functions 1..count; 50 statements each, too large to copy) that
    /// mix <c>n</c> with k and end with a call to twice, and <c>wide_main() = sum of filler_k(k) &amp; 0xFFFFFF</c> (see
    /// <see cref="WideExpected"/>).
    /// </summary>
    public static IrModule WideProgram(int count)
    {
        var twice = new FunctionBuilder("twice", IrType.I32);
        int x = twice.Argument(IrType.I32, "x");
        twice.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Mul, Copy(x), Int(2))).Return();
        var functions = new List<IrFunction> { twice.Build() };
        for (int k = 0; k < count; k++)
        {
            var filler = new FunctionBuilder($"filler_{k}", IrType.I32);
            int n = filler.Argument(IrType.I32, "n");
            int s = filler.Local(IrType.I32, "s", mutable: true);
            int done = filler.NewBlock();
            filler.Assign(s, Copy(n));
            for (int step = 0; step < 25; step++)
            {
                filler.Assign(s, Rvalue.Binary(BinaryOp.Xor, Copy(s), Int(k + step)));
                filler.Assign(s, Rvalue.Binary(BinaryOp.And, Copy(s), Int(0xFFFF)));
            }
            filler.Call(0, [Copy(s)], FunctionBuilder.ReturnPlace, done);
            filler.At(done).Return();
            functions.Add(filler.Build());
        }
        var main = new FunctionBuilder("wide_main", IrType.I32);
        int total = main.Local(IrType.I32, "total", mutable: true);
        int value = main.Local(IrType.I32, "value", mutable: true);
        main.Assign(total, Int(0));
        for (int k = 0; k < count; k++)
        {
            int next = main.NewBlock();
            main.Call(1 + k, [Int(k)], value, next);
            main.At(next).Assign(total, Rvalue.Binary(BinaryOp.Add, Copy(total), Copy(value)));
        }
        main.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.And, Copy(total), Int(0xFFFFFF))).Return();
        functions.Add(main.Build());
        return new IrModule([], [.. functions]) { Name = "wide" };
    }

    /// <summary>What <see cref="WideProgram"/>'s wide_main returns.</summary>
    public static int WideExpected(int count)
    {
        int total = 0;
        for (int k = 0; k < count; k++)
        {
            int s = k;
            for (int step = 0; step < 25; step++)
                s = (s ^ (k + step)) & 0xFFFF;
            total += s * 2;
        }
        return total & 0xFFFFFF;
    }
}
