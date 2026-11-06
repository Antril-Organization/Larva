using Larva.IR;
using static Larva.IR.Operand;
using static Larva.Tests.GenericsPrograms;

namespace Larva.Tests;

/// <summary>The shared-generics program: generic code compiled once and driven by witness tables (main() = size_of&lt;f32&gt; = 4).</summary>
internal static class SharedGenericsPrograms
{
    public const int IdentityFn = 0, SizeOfFn = 1, PassThroughFn = 2, PairSizeFn = 3, WrapFn = 4, ConsumeFn = 5, AddFn = 6, SumThenSizeFn = 7;
    public const int MixedStruct = 2;

    public static readonly StructDef Mixed = new("Mixed", IrType.U8, IrType.Gc(IrType.I32), IrType.I16);

    /// <summary>identity, size_of, pass_through, pair_size, wrap, consume, add, add_then_size, main.</summary>
    public static IrModule ProgramModule() => new([Pair, Point, Mixed],
        [Identity(), SizeOf(), PassThrough(), PairSize(), Wrap(), Consume(), Add(), AddThenSize(), BuildMain()]);

    // ── Shareable functions ────────────────────────────────────────

    /// <summary>fn identity&lt;T&gt;(x: T) -> T { x }</summary>
    private static IrFunction Identity()
    {
        var f = GenericBuilder("identity", out var t, ("T", TypeConstraint.Any));
        int x = f.Argument(t[0], "x");
        f.Assign(FunctionBuilder.ReturnPlace, Move(x)).Return();
        return f.Build();
    }

    /// <summary>fn size_of&lt;T: fixedsize&gt;() -> u64</summary>
    private static IrFunction SizeOf()
    {
        var f = new FunctionBuilder("size_of", IrType.U64);
        var t = f.Generic("T", TypeConstraint.FixedSize);
        f.Assign(FunctionBuilder.ReturnPlace, Rvalue.SizeOf(t)).Return();
        return f.Build();
    }

    /// <summary>fn pass_through&lt;T&gt;(x: T) -> T { identity::&lt;T&gt;(x) } — forwards its own witness.</summary>
    private static IrFunction PassThrough()
    {
        var f = GenericBuilder("pass_through", out var t, ("T", TypeConstraint.Any));
        int x = f.Argument(t[0], "x");
        int done = f.NewBlock();
        f.Call(IdentityFn, [t[0]], [Move(x)], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    /// <summary>fn pair_size&lt;T: fixedsize&gt;() -> u64 { size_of::&lt;Pair&lt;T, i32&gt;&gt;() } — needs an open witness.</summary>
    private static IrFunction PairSize()
    {
        var f = new FunctionBuilder("pair_size", IrType.U64);
        var t = f.Generic("T", TypeConstraint.FixedSize);
        int done = f.NewBlock();
        f.Call(SizeOfFn, [IrType.Struct(PairStruct, t, IrType.I32)], [], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    /// <summary>fn wrap&lt;T&gt;(x: T) -> Pair&lt;T, i32&gt; — field stores into an open struct.</summary>
    private static IrFunction Wrap()
    {
        var f = new FunctionBuilder("wrap", IrType.Struct(PairStruct, IrType.Param(0), IrType.I32));
        var t = f.Generic("T");
        int x = f.Argument(t, "x");
        f.Assign(FunctionBuilder.ReturnPlace.Field(0), Move(x))
         .Assign(FunctionBuilder.ReturnPlace.Field(1), Int(0))
         .Return();
        return f.Build();
    }

    /// <summary>fn consume&lt;T&gt;(x: T) { drop(x) }</summary>
    private static IrFunction Consume()
    {
        var f = new FunctionBuilder("consume", IrType.Unit);
        var t = f.Generic("T");
        int x = f.Argument(t, "x");
        int done = f.NewBlock();
        f.Drop(x, done);
        f.At(done).Return();
        return f.Build();
    }

    // ── Functions that must be specialized ─────────────────────────

    /// <summary>fn add&lt;T: numeric&gt;(a: T, b: T) -> T — arithmetic on T.</summary>
    private static IrFunction Add()
    {
        var f = GenericBuilder("add", out var t, ("T", TypeConstraint.Numeric));
        int a = f.Argument(t[0], "a"), b = f.Argument(t[0], "b");
        f.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(a), Copy(b))).Return();
        return f.Build();
    }

    /// <summary>fn add_then_size&lt;T: numeric&gt;(x: T) -> u64 — specialized, yet calls shared size_of::&lt;T&gt; with a static witness.</summary>
    private static IrFunction AddThenSize()
    {
        var f = new FunctionBuilder("add_then_size", IrType.U64);
        var t = f.Generic("T", TypeConstraint.Numeric);
        int x = f.Argument(t, "x");
        int sum = f.Temp(t);
        f.Assign(sum, Rvalue.Binary(BinaryOp.Add, Copy(x), Copy(x)));
        int done = f.NewBlock();
        f.Call(SizeOfFn, [t], [], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    private static IrFunction BuildMain()
    {
        var f = new FunctionBuilder("main", IrType.U64);
        var point = IrType.Struct(PointStruct);
        int a = f.Local(IrType.I32, "a");
        int p = f.Local(point, "p", mutable: true);
        int q = f.Local(point, "q");
        int sp = f.Local(IrType.U64, "sp");
        int d = f.Local(IrType.F64, "d");
        int ps = f.Local(IrType.U64, "ps");
        int w = f.Local(IrType.Struct(PairStruct, IrType.Bool, IrType.I32), "w");
        int unit = f.Temp(IrType.Unit);
        int s = f.Local(IrType.F32, "s");
        int[] next = [.. Enumerable.Range(0, 9).Select(_ => f.NewBlock())];

        f.Call(IdentityFn, [IrType.I32], [Int(7)], a, next[0]);
        f.At(next[0]).Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Call(IdentityFn, [point], [Move(p)], q, next[1]);
        f.At(next[1]).Call(SizeOfFn, [point], [], sp, next[2]);
        f.At(next[2]).Call(PassThroughFn, [IrType.F64], [Float(0.5)], d, next[3]);
        f.At(next[3]).Call(PairSizeFn, [IrType.I64], [], ps, next[4]);
        f.At(next[4]).Call(WrapFn, [IrType.Bool], [Bool(true)], w, next[5]);
        f.At(next[5]).Call(ConsumeFn, [point], [Move(q)], unit, next[6]);
        f.At(next[6]).Call(AddFn, [IrType.F32], [Float32(1), Float32(2)], s, next[7]);
        f.At(next[7]).Call(SumThenSizeFn, [IrType.F32], [Copy(s)], FunctionBuilder.ReturnPlace, next[8]);
        f.At(next[8]).Return();
        return f.Build();
    }
}
