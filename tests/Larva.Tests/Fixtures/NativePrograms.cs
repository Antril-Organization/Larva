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
}
