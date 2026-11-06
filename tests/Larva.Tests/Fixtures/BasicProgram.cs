using Larva.IR;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>The first IR program: <c>sum</c>, <c>bump</c> and <c>main</c> over a <c>Point</c> (main() = 5).</summary>
internal static class BasicProgram
{
    public const int SumFn = 0, BumpFn = 1;
    public const int PointStruct = 0;
    private static readonly IrType Point = IrType.Struct(PointStruct);

    /// <summary>A new copy of the module: sum, bump, main.</summary>
    public static IrModule Module() => new([new StructDef("Point", IrType.I32, IrType.I32)], [BuildSum(), BuildBump(), BuildMain()]);

    /// <summary>fn sum(n: i32) -> i32 { let mut s = 0; let mut i = 0; while i &lt; n { s += i; i += 1; } s }</summary>
    private static IrFunction BuildSum()
    {
        var f = new FunctionBuilder("sum", IrType.I32);
        int n = f.Argument(IrType.I32, "n");
        int s = f.Local(IrType.I32, "s", mutable: true);
        int i = f.Local(IrType.I32, "i", mutable: true);
        int cond = f.Temp(IrType.Bool);
        int head = f.NewBlock(), body = f.NewBlock(), exit = f.NewBlock();

        f.StorageLive(s).Assign(s, Int(0))
         .StorageLive(i).Assign(i, Int(0))
         .Goto(head);
        f.At(head).Assign(cond, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n)))
         .If(Move(cond), body, exit);
        f.At(body).Assign(s, Rvalue.Binary(BinaryOp.Add, Copy(s), Copy(i)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1)))
         .Goto(head);
        f.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(s))
         .StorageDead(i).StorageDead(s)
         .Return();
        return f.Build();
    }

    /// <summary>fn bump(p: &amp;mut Point) { p.x += 1; }</summary>
    private static IrFunction BuildBump()
    {
        var f = new FunctionBuilder("bump", IrType.Unit);
        int p = f.Argument(IrType.RefMut(Point), "p");
        var x = new Place(p).Deref().Field(0);
        f.Assign(x, Rvalue.Binary(BinaryOp.Add, Copy(x), Int(1))).Return();
        return f.Build();
    }

    /// <summary>fn main() -> i32 { let mut pt = Point { x: 1, y: 3 }; bump(&amp;mut pt); sum(pt.y) + pt.x }</summary>
    private static IrFunction BuildMain()
    {
        var f = new FunctionBuilder("main", IrType.I32);
        int pt = f.Local(Point, "pt", mutable: true);
        int borrow = f.Temp(IrType.RefMut(Point));
        int unit = f.Temp(IrType.Unit);
        int total = f.Temp(IrType.I32);
        int afterBump = f.NewBlock(), afterSum = f.NewBlock();

        f.StorageLive(pt)
         .Assign(new Place(pt).Field(0), Int(1))
         .Assign(new Place(pt).Field(1), Int(3))
         .Assign(borrow, Rvalue.RefMut(pt))
         .Call(BumpFn, [Move(borrow)], unit, afterBump);
        f.At(afterBump).Call(SumFn, [Copy(new Place(pt).Field(1))], total, afterSum);
        f.At(afterSum).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Move(total), Copy(new Place(pt).Field(0))))
         .StorageDead(pt)
         .Return();
        return f.Build();
    }
}
