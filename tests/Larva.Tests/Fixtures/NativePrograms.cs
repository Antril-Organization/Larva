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
}
