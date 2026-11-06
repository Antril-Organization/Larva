using Larva.IR;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>The valid generic program (max, add, first, clamp_int, main over Pair and Point) and the generic function builder.</summary>
internal static class GenericsPrograms
{
    public const int PairStruct = 0, PointStruct = 1;
    public const int MaxFn = 0, AddFn = 1, FirstFn = 2, ClampFn = 3;

    public static readonly StructDef Pair = new("Pair", IrType.Param(0), IrType.Param(1))
    {
        Generics = [new GenericParam("A"), new GenericParam("B", TypeConstraint.Copy)],
    };

    public static readonly StructDef Point = new("Point", IrType.I32, IrType.I32);

    /// <summary>The valid generic program: max, add, first, clamp_int, main (main() = 8).</summary>
    public static IrModule ProgramModule() => new([Pair, Point], [BuildMax(), BuildAdd(), BuildFirst(), BuildClamp(), BuildMain()]);

    /// <summary>A builder whose return type is generic parameter 0, with the given generic parameters declared.</summary>
    public static FunctionBuilder GenericBuilder(string name, out IrType[] parameters, params (string Name, TypeConstraint Constraint)[] generics)
    {
        // Generic parameter i is always GenericParamType(i), so the return type can name it before declaration.
        var f = new FunctionBuilder(name, IrType.Param(0));
        parameters = [.. generics.Select(g => f.Generic(g.Name, g.Constraint))];
        return f;
    }

    /// <summary>fn max&lt;T: numeric&gt;(a: T, b: T) -> T</summary>
    public static IrFunction BuildMax()
    {
        var f = GenericBuilder("max", out var t, ("T", TypeConstraint.Numeric));
        int a = f.Argument(t[0], "a"), b = f.Argument(t[0], "b");
        int gt = f.Temp(IrType.Bool);
        int takeA = f.NewBlock(), takeB = f.NewBlock();
        f.Assign(gt, Rvalue.Binary(BinaryOp.Gt, Copy(a), Copy(b))).If(Move(gt), takeA, takeB);
        f.At(takeA).Assign(FunctionBuilder.ReturnPlace, Copy(a)).Return();
        f.At(takeB).Assign(FunctionBuilder.ReturnPlace, Copy(b)).Return();
        return f.Build();
    }

    /// <summary>fn add&lt;T: numeric&gt;(a: T, b: T) -> T</summary>
    public static IrFunction BuildAdd()
    {
        var f = GenericBuilder("add", out var t, ("T", TypeConstraint.Numeric));
        int a = f.Argument(t[0], "a"), b = f.Argument(t[0], "b");
        f.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(a), Copy(b))).Return();
        return f.Build();
    }

    private static IrFunction BuildFirst()
    {
        var f = GenericBuilder("first", out var t, ("A", TypeConstraint.Copy), ("B", TypeConstraint.Copy));
        int p = f.Argument(IrType.Ref(IrType.Struct(PairStruct, t[0], t[1])), "p");
        f.Assign(FunctionBuilder.ReturnPlace, Copy(new Place(p).Deref().Field(0))).Return();
        return f.Build();
    }

    private static IrFunction BuildClamp()
    {
        var f = GenericBuilder("clamp_int", out var t, ("U", TypeConstraint.Int));
        int x = f.Argument(t[0], "x");
        int done = f.NewBlock();
        f.Call(MaxFn, [t[0]], [Copy(x), Copy(x)], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    private static IrFunction BuildMain()
    {
        var f = new FunctionBuilder("main", IrType.I32);
        var pairType = IrType.Struct(PairStruct, IrType.I32, IrType.F64);
        int m = f.Local(IrType.I32, "m");
        int s = f.Local(IrType.F64, "s");
        int w = f.Local(IrType.I128, "w");
        int pair = f.Local(pairType, "pair", mutable: true);
        int r = f.Local(IrType.Ref(pairType), "r");
        int head = f.Local(IrType.I32, "head");
        int c = f.Local(IrType.I64, "c");
        int b1 = f.NewBlock(), b2 = f.NewBlock(), b3 = f.NewBlock(), b4 = f.NewBlock(), b5 = f.NewBlock();

        f.Call(MaxFn, [IrType.I32], [Int(3), Int(7)], m, b1);
        f.At(b1).Call(AddFn, [IrType.F64], [Float(1.5), Float(2.5)], s, b2);
        f.At(b2).Call(AddFn, [IrType.I128], [Int(1, IrType.I128), Int(2, IrType.I128)], w, b3);
        f.At(b3).Assign(new Place(pair).Field(0), Int(1))
         .Assign(new Place(pair).Field(1), Float(2.0))
         .Assign(r, Rvalue.Ref(pair))
         .Call(FirstFn, [IrType.I32, IrType.F64], [Move(r)], head, b4);
        f.At(b4).Call(ClampFn, [IrType.I64], [Int(5, IrType.I64)], c, b5);
        f.At(b5).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(m), Copy(head))).Return();
        return f.Build();
    }
}
