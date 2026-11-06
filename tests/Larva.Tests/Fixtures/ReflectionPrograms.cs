using Larva.IR;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>The reflection program: add, identity, max, swap_pair, hidden, main, total over Point, Secret, Holder, Pair, Wide.</summary>
internal static class ReflectionPrograms
{
    public const int PointId = 0, SecretId = 1, HolderId = 2, PairId = 3, WideId = 4;
    public const int MaxFn = 2;
    private static readonly IrType Point = IrType.Struct(PointId);

    public static readonly StructDef[] Structs =
    [
        new("Point", IrType.I32, IrType.I32) { FieldNames = ["x", "y"], Reflect = ReflectionLevel.Full },
        new("Secret", IrType.I32) { FieldNames = ["code"] },
        new("Holder", IrType.Gc(IrType.Struct(PointId)), IrType.I32) { FieldNames = ["h", "n"] },
        new("Pair", IrType.Param(0), IrType.Param(1))
        {
            FieldNames = ["first", "second"],
            Generics = [new GenericParam("A"), new GenericParam("B", TypeConstraint.Copy)],
            Reflect = ReflectionLevel.Full,
        },
        new("Wide", IrType.I128, IrType.F64) { FieldNames = ["big", "f"], Reflect = ReflectionLevel.Layout },
    ];

    public static IrModule ProgramModule() => new(Structs, [Add(), Identity(), Max(), SwapPair(), Hidden(), BuildMain(), Total()]);

    // ── Functions ──────────────────────────────────────────────────

    /// <summary>#[reflect(full)] fn add(a: i32, b: f64) -> f64 { a as f64 + b }</summary>
    private static IrFunction Add()
    {
        var f = new FunctionBuilder("add", IrType.F64) { Reflect = ReflectionLevel.Full };
        int a = f.Argument(IrType.I32, "a");
        int b = f.Argument(IrType.F64, "b");
        int t = f.Temp(IrType.F64);
        f.Assign(t, Rvalue.Cast(Copy(a), IrType.F64))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(t), Copy(b)))
         .Return();
        return f.Build();
    }

    /// <summary>#[reflect(full)] fn identity&lt;T&gt;(x: T) -> T — shared.</summary>
    private static IrFunction Identity()
    {
        var f = new FunctionBuilder("identity", IrType.Param(0)) { Reflect = ReflectionLevel.Full };
        var t = f.Generic("T");
        int x = f.Argument(t, "x");
        f.Assign(FunctionBuilder.ReturnPlace, Move(x)).Return();
        return f.Build();
    }

    /// <summary>#[reflect(full)] fn max&lt;T: numeric&gt;(a: T, b: T) -> T — needs specialization.</summary>
    private static IrFunction Max()
    {
        var f = new FunctionBuilder("max", IrType.Param(0)) { Reflect = ReflectionLevel.Full };
        var t = f.Generic("T", TypeConstraint.Numeric);
        int a = f.Argument(t, "a"), b = f.Argument(t, "b");
        int gt = f.Temp(IrType.Bool);
        int takeA = f.NewBlock(), takeB = f.NewBlock();
        f.Assign(gt, Rvalue.Binary(BinaryOp.Gt, Copy(a), Copy(b))).If(Move(gt), takeA, takeB);
        f.At(takeA).Assign(FunctionBuilder.ReturnPlace, Copy(a)).Return();
        f.At(takeB).Assign(FunctionBuilder.ReturnPlace, Copy(b)).Return();
        return f.Build();
    }

    /// <summary>#[reflect(full)] fn swap_pair&lt;A: copy, B: copy&gt;(p: Pair&lt;A, B&gt;) -> Pair&lt;B, A&gt; — shared, open structs.</summary>
    private static IrFunction SwapPair()
    {
        var f = new FunctionBuilder("swap_pair", IrType.Struct(PairId, IrType.Param(1), IrType.Param(0))) { Reflect = ReflectionLevel.Full };
        var a = f.Generic("A", TypeConstraint.Copy);
        var b = f.Generic("B", TypeConstraint.Copy);
        int p = f.Argument(IrType.Struct(PairId, a, b), "p");
        f.Assign(FunctionBuilder.ReturnPlace.Field(0), Copy(new Place(p).Field(1)))
         .Assign(FunctionBuilder.ReturnPlace.Field(1), Copy(new Place(p).Field(0)))
         .Return();
        return f.Build();
    }

    /// <summary>fn hidden() -> i32 — not reflected.</summary>
    private static IrFunction Hidden()
    {
        var f = new FunctionBuilder("hidden", IrType.I32);
        f.Assign(FunctionBuilder.ReturnPlace, Int(42)).Return();
        return f.Build();
    }

    /// <summary>fn main() -> i32 { max::&lt;i32&gt;(1, 2) } — the only static use of max.</summary>
    private static IrFunction BuildMain()
    {
        var f = new FunctionBuilder("main", IrType.I32);
        int done = f.NewBlock();
        f.Call(MaxFn, [IrType.I32], [Int(1), Int(2)], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    /// <summary>#[reflect(full)] fn total(w: Wide) -> i128 { w.big }</summary>
    private static IrFunction Total()
    {
        var f = new FunctionBuilder("total", IrType.I128) { Reflect = ReflectionLevel.Full };
        int w = f.Argument(IrType.Struct(WideId), "w");
        f.Assign(FunctionBuilder.ReturnPlace, Copy(new Place(w).Field(0))).Return();
        return f.Build();
    }
}
