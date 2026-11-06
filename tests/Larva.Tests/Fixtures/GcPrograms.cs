using Larva.IR;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>
/// The allocation programs over Point and Holder: boxes, give, take, raw, make, handles, store, peek, spin — in that order
/// (function indices 0..8).
/// </summary>
internal static class GcPrograms
{
    public const int PointId = 0, HolderId = 1;
    public const int BoxesFn = 0, GiveFn = 1, TakeFn = 2, RawFn = 3, MakeFn = 4, HandlesFn = 5, StoreFn = 6, PeekFn = 7, SpinFn = 8;

    public static readonly IrType PointType = IrType.Struct(PointId);
    public static readonly IrType HolderType = IrType.Struct(HolderId);

    public static readonly StructDef[] StructDefs =
    [
        new("Point", IrType.I32, IrType.I32),
        new("Holder", IrType.Gc(IrType.Struct(PointId)), IrType.I32),
    ];

    /// <summary>boxes, give, take, raw: no gc handles (compiles in Manual mode too).</summary>
    public static IrModule BoxOnlyModule() => new([StructDefs[PointId]], [Boxes(), Give(), Take(), Raw()]);

    /// <summary>boxes, give, take, raw, make, handles, store, peek, spin — in that order.</summary>
    public static IrModule ManagedModule() => new(StructDefs, [.. BoxOnlyModule().Functions, Make(), Handles(), Store(), Peek(), Spin()]);

    private static readonly IrType Point = PointType;
    private static readonly IrType Holder = HolderType;

    /// <summary>let mut b = box Point{1,2}; b = box Point{3,4}; (*b).0</summary>
    private static IrFunction Boxes()
    {
        var f = new FunctionBuilder("boxes", IrType.I32);
        int p = f.Local(Point, "p", mutable: true);
        int b = f.Local(IrType.Box(Point), "b", mutable: true);
        f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Assign(b, Rvalue.Box(Move(p)))
         .Assign(new Place(p).Field(0), Int(3)).Assign(new Place(p).Field(1), Int(4))
         .Assign(b, Rvalue.Box(Move(p)))
         .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(b).Deref().Field(0)))
         .Return();
        return f.Build();
    }

    /// <summary>fn give(b: box Point) {} — owns and frees its argument.</summary>
    private static IrFunction Give()
    {
        var f = new FunctionBuilder("give", IrType.Unit);
        f.Argument(IrType.Box(Point), "b");
        f.Return();
        return f.Build();
    }

    /// <summary>let b = box 0; give(b) — ownership moves to the callee.</summary>
    private static IrFunction Take()
    {
        var f = new FunctionBuilder("take", IrType.Unit);
        int p = f.Local(Point, "p", mutable: true);
        int b = f.Local(IrType.Box(Point), "b");
        int unit = f.Temp(IrType.Unit);
        int done = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(0)).Assign(new Place(p).Field(1), Int(0))
         .Assign(b, Rvalue.Box(Move(p)))
         .Call(GiveFn, [Move(b)], unit, done);
        f.At(done).Return();
        return f.Build();
    }

    /// <summary>unsafe fn raw() -> i32 { let p = alloc 5; let v = *p; free(p); v }</summary>
    private static IrFunction Raw()
    {
        var f = new FunctionBuilder("raw", IrType.I32) { Unsafe = true };
        int p = f.Local(IrType.PtrMut(IrType.I32), "p");
        f.Assign(p, Rvalue.Alloc(Int(5)))
         .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(p).Deref()))
         .Free(p)
         .Return();
        return f.Build();
    }

    /// <summary>fn make() -> gc Point { gc Point{7,8} }</summary>
    private static IrFunction Make()
    {
        var f = new FunctionBuilder("make", IrType.Gc(Point));
        int p = f.Local(Point, "p", mutable: true);
        f.Assign(new Place(p).Field(0), Int(7)).Assign(new Place(p).Field(1), Int(8))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Gc(Move(p)))
         .Return();
        return f.Build();
    }

    /// <summary>fn handles&lt;'a&gt;(h: gc Point in 'a) -> gc Point in 'a { let c = h; c } — a copy, then the original dies.</summary>
    private static IrFunction Handles()
    {
        var f = new FunctionBuilder("handles", IrType.Gc(Point));
        int a = f.RegionParam("a");
        f.ReturnsIn(a);
        int h = f.Argument(IrType.Gc(Point), "h", region: a);
        int c = f.Local(IrType.Gc(Point), "c");
        f.Assign(c, Copy(h)).Assign(FunctionBuilder.ReturnPlace, Move(c)).Return();
        return f.Build();
    }

    /// <summary>fn store&lt;'a&gt;(holder: &amp;mut Holder in 'a, h: gc Point in 'a) { holder.0 = h }</summary>
    private static IrFunction Store()
    {
        var f = new FunctionBuilder("store", IrType.Unit);
        int a = f.RegionParam("a");
        int holder = f.Argument(IrType.RefMut(Holder), "holder", region: a);
        int h = f.Argument(IrType.Gc(Point), "h", region: a);
        f.Assign(new Place(holder).Deref().Field(0), Move(h)).Return();
        return f.Build();
    }

    /// <summary>fn peek(h: gc Point) -> i32 { let r = &amp;h.0; *r } — interior borrow of a gc object.</summary>
    private static IrFunction Peek()
    {
        var f = new FunctionBuilder("peek", IrType.I32);
        int h = f.Argument(IrType.Gc(Point), "h");
        int r = f.Local(IrType.Ref(IrType.I32), "r");
        f.Assign(r, Rvalue.Ref(new Place(h).Deref().Field(0)))
         .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(r).Deref()))
         .Return();
        return f.Build();
    }

    /// <summary>fn spin(n: i32) { let mut i = 0; while i &lt; n { i += 1 } }</summary>
    private static IrFunction Spin()
    {
        var f = new FunctionBuilder("spin", IrType.Unit);
        int n = f.Argument(IrType.I32, "n");
        int i = f.Local(IrType.I32, "i", mutable: true);
        int c = f.Temp(IrType.Bool);
        int head = f.NewBlock(), body = f.NewBlock(), exit = f.NewBlock();
        f.Assign(i, Int(0)).Goto(head);
        f.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Move(c), body, exit);
        f.At(body).Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1))).Goto(head);
        f.At(exit).Return();
        return f.Build();
    }
}
