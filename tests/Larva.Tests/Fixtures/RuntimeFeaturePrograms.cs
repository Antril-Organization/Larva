using Larva.IR;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>The array and exception programs over Point { x, y }.</summary>
internal static class RuntimeFeaturePrograms
{
    public const int PointId = 0;
    private static readonly IrType Point = IrType.Struct(PointId);
    private static readonly IrType GcPoint = IrType.Gc(Point);
    public static readonly StructDef[] Structs = [new("Point", IrType.I32, IrType.I32) { FieldNames = ["x", "y"] }];

    public const int ThrowerFn = 0, CallerFn = 1, LeakyFn = 2, CatcherFn = 3, RethrowerFn = 4, Caller2Fn = 5,
        GiveThrowFn = 6, OwnerFn = 7, ThrowGcFn = 8, CatchGcFn = 9, CallerBoolFn = 10;

    public static IrModule ArrayModule() => new(Structs, [Squares(), LenOf(), OutOfBounds()]);

    public static IrModule GcArrayModule() => new(Structs, [GcArray()]);

    public static IrModule ExceptionModule() => new(Structs,
    [
        Thrower(), Caller(), Leaky(), Catcher(), Rethrower(), Caller2(), GiveThrow(), Owner(), ThrowGc(), CatchGc(), CallerBool(),
    ]);

    /// <summary>thrower, region_throw and region_catcher (arena mode only).</summary>
    internal static IrModule ArenaExceptionModule() => new(Structs, [Thrower(), RegionThrow(), CatchAll("region_catcher", 1, [Int(-1)])]);

    /// <summary>fn thrower(n: i32) -> i32 { if n &lt; 0 { throw n } n * 2 }</summary>
    public static IrFunction Thrower()
    {
        var f = new FunctionBuilder("thrower", IrType.I32);
        int n = f.Argument(IrType.I32, "n");
        int c = f.Temp(IrType.Bool);
        int raise = f.NewBlock(), ok = f.NewBlock();
        f.Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(n), Int(0))).If(Move(c), raise, ok);
        f.At(raise).Throw(Copy(n));
        f.At(ok).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Mul, Copy(n), Int(2))).Return();
        return f.Build();
    }

    /// <summary>fn caller(n: i32) -> i32 { try { thrower(n) } catch (e: i32) { -e } }</summary>
    private static IrFunction Caller() => CatchingCaller("caller", IrType.I32);

    /// <summary>Same shape, but only catches bool: an i32 passes through.</summary>
    private static IrFunction CallerBool() => CatchingCaller("caller_bool", IrType.Bool);

    private static IrFunction CatchingCaller(string name, IrType caught)
    {
        var f = new FunctionBuilder(name, IrType.I32);
        int n = f.Argument(IrType.I32, "n");
        int e = f.Local(caught, "e");
        int ok = f.NewBlock(), handler = f.NewBlock();
        f.Call(ThrowerFn, [], [Copy(n)], FunctionBuilder.ReturnPlace, ok, [new CatchClause(caught, handler)]);
        f.At(ok).Return();
        f.At(handler).Assign(e, Rvalue.Caught);
        if (caught == IrType.I32)
            f.Assign(FunctionBuilder.ReturnPlace, Rvalue.Unary(UnaryOp.Neg, Copy(e)));
        else
            f.Assign(FunctionBuilder.ReturnPlace, Int(0));
        f.Return();
        return f.Build();
    }

    /// <summary>fn leaky(n: i32) -> i32 { let b = box Point{1, 2}; thrower(n); b.x }  — b must be freed if thrower throws.</summary>
    private static IrFunction Leaky()
    {
        var f = new FunctionBuilder("leaky", IrType.I32);
        int n = f.Argument(IrType.I32, "n");
        int p = f.Local(Point, "p", mutable: true);
        int b = f.Local(IrType.Box(Point), "b");
        int t = f.Local(IrType.I32, "t");
        int ok = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Assign(b, Rvalue.Box(Move(p)))
         .Call(ThrowerFn, [Copy(n)], t, ok);
        f.At(ok).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(b).Deref().Field(0))).Return();
        return f.Build();
    }

    /// <summary>fn catcher(n: i32) -> i32 { try { leaky(n) } catch _ { -1 } }</summary>
    private static IrFunction Catcher() => CatchAll("catcher", LeakyFn, [ArgN()]);

    /// <summary>fn rethrower(n: i32) -> i32 { try { thrower(n) } catch (e: i32) { resume } }</summary>
    private static IrFunction Rethrower()
    {
        var f = new FunctionBuilder("rethrower", IrType.I32);
        int n = f.Argument(IrType.I32, "n");
        int e = f.Local(IrType.I32, "e");
        int ok = f.NewBlock(), handler = f.NewBlock();
        f.Call(ThrowerFn, [], [Copy(n)], FunctionBuilder.ReturnPlace, ok, [new CatchClause(IrType.I32, handler)]);
        f.At(ok).Return();
        f.At(handler).Assign(e, Rvalue.Caught).Resume();
        return f.Build();
    }

    /// <summary>fn caller2(n: i32) -> i32 { try { rethrower(n) } catch (e: i32) { 100 + e } }</summary>
    private static IrFunction Caller2()
    {
        var f = new FunctionBuilder("caller2", IrType.I32);
        int n = f.Argument(IrType.I32, "n");
        int e = f.Local(IrType.I32, "e");
        int ok = f.NewBlock(), handler = f.NewBlock();
        f.Call(RethrowerFn, [], [Copy(n)], FunctionBuilder.ReturnPlace, ok, [new CatchClause(IrType.I32, handler)]);
        f.At(ok).Return();
        f.At(handler).Assign(e, Rvalue.Caught).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Int(100), Copy(e))).Return();
        return f.Build();
    }

    /// <summary>fn give_throw(b: box Point, n: i32) -> i32 { thrower(n) }  — owns b, which must be freed on either path.</summary>
    private static IrFunction GiveThrow()
    {
        var f = new FunctionBuilder("give_throw", IrType.I32);
        f.Argument(IrType.Box(Point), "b");
        int n = f.Argument(IrType.I32, "n");
        int ok = f.NewBlock();
        f.Call(ThrowerFn, [Copy(n)], FunctionBuilder.ReturnPlace, ok);
        f.At(ok).Return();
        return f.Build();
    }

    /// <summary>fn owner(n: i32) -> i32 { try { give_throw(box Point{0,0}, n) } catch _ { -1 } }</summary>
    private static IrFunction Owner()
    {
        var f = new FunctionBuilder("owner", IrType.I32);
        int n = f.Argument(IrType.I32, "n");
        int p = f.Local(Point, "p", mutable: true);
        int b = f.Local(IrType.Box(Point), "b");
        int ok = f.NewBlock(), handler = f.NewBlock();
        f.Assign(new Place(p).Field(0), Int(0)).Assign(new Place(p).Field(1), Int(0))
         .Assign(b, Rvalue.Box(Move(p)))
         .Call(GiveThrowFn, [], [Move(b), Copy(n)], FunctionBuilder.ReturnPlace, ok, [new CatchClause(null, handler)]);
        f.At(ok).Return();
        f.At(handler).Assign(FunctionBuilder.ReturnPlace, Int(-1)).Return();
        return f.Build();
    }

    /// <summary>fn throw_gc() { throw gc Point{9, 9} }</summary>
    private static IrFunction ThrowGc()
    {
        var f = new FunctionBuilder("throw_gc", IrType.Unit);
        int p = f.Local(Point, "p", mutable: true);
        int g = f.Local(GcPoint, "g");
        f.Assign(new Place(p).Field(0), Int(9)).Assign(new Place(p).Field(1), Int(9))
         .Assign(g, Rvalue.Gc(Move(p)))
         .Throw(Move(g));
        return f.Build();
    }

    /// <summary>fn catch_gc() -> i32 { try { throw_gc() } catch (e: gc Point) { thrower(1); e.x } }  — a collection runs at the call.</summary>
    private static IrFunction CatchGc()
    {
        var f = new FunctionBuilder("catch_gc", IrType.I32);
        int e = f.Local(GcPoint, "e");
        int t = f.Local(IrType.I32, "t");
        int unit = f.Temp(IrType.Unit);
        int ok = f.NewBlock(), handler = f.NewBlock(), after = f.NewBlock();
        f.Call(ThrowGcFn, [], [], unit, ok, [new CatchClause(GcPoint, handler)]);
        f.At(ok).Assign(FunctionBuilder.ReturnPlace, Int(0)).Return();
        f.At(handler).Assign(e, Rvalue.Caught).Call(ThrowerFn, [Int(1)], t, after);
        f.At(after).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(e).Deref().Field(0))).Return();
        return f.Build();
    }

    /// <summary>fn region_throw(n: i32) -> i32 { region_enter(r0); let g = gc Point{1,1}; thrower(n); region_exit(r0); … }</summary>
    private static IrFunction RegionThrow()
    {
        var f = new FunctionBuilder("region_throw", IrType.I32);
        int n = f.Argument(IrType.I32, "n");
        int p = f.Local(Point, "p", mutable: true);
        int g = f.Local(GcPoint, "g");
        int t = f.Local(IrType.I32, "t");
        int ok = f.NewBlock();
        f.RegionEnter(0)
         .Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(1))
         .Assign(g, Rvalue.Gc(Move(p)))
         .Call(ThrowerFn, [Copy(n)], t, ok);
        f.At(ok).RegionExit(0).Assign(FunctionBuilder.ReturnPlace, Copy(t)).Return();
        return f.Build();
    }

    private static Operand ArgN() => Copy(new Place(1));

    /// <summary>fn name(n: i32) -> i32 { try { callee(args) } catch _ { -1 } }</summary>
    private static IrFunction CatchAll(string name, int callee, Operand[] arguments)
    {
        var f = new FunctionBuilder(name, IrType.I32);
        if (arguments.Any(a => a is CopyOperand))
            f.Argument(IrType.I32, "n");
        int ok = f.NewBlock(), handler = f.NewBlock();
        f.Call(callee, [], arguments, FunctionBuilder.ReturnPlace, ok, [new CatchClause(null, handler)]);
        f.At(ok).Return();
        f.At(handler).Assign(FunctionBuilder.ReturnPlace, Int(-1)).Return();
        return f.Build();
    }

    // ── Arrays ─────────────────────────────────────────────────────

    /// <summary>fn squares(n: i64) -> i64 { a = box [i64; n]; for i in 0..n { a[i] = i*i } Σ a[i] }</summary>
    private static IrFunction Squares()
    {
        var f = new FunctionBuilder("squares", IrType.I64);
        int n = f.Argument(IrType.I64, "n");
        int a = f.Local(IrType.Box(new ArrayType(IrType.I64)), "a", mutable: true);
        int i = f.Local(IrType.I64, "i", mutable: true);
        int s = f.Local(IrType.I64, "s", mutable: true);
        int c = f.Local(IrType.Bool, "c", mutable: true);
        var element = new Place(a).Deref().Index(i);
        int fill = f.NewBlock(), store = f.NewBlock(), sum = f.NewBlock(), add = f.NewBlock(), done = f.NewBlock();
        f.Assign(a, Rvalue.NewArray(AllocKind.Box, IrType.I64, Copy(n))).Assign(i, Int(0, IrType.I64)).Goto(fill);
        f.At(fill).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), store, sum);
        f.At(store).Assign(element, Rvalue.Binary(BinaryOp.Mul, Copy(i), Copy(i)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(fill);
        f.At(sum).Assign(i, Int(0, IrType.I64)).Assign(s, Int(0, IrType.I64)).Goto(add);
        f.At(add).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), done + 1, done);
        f.At(done).Assign(FunctionBuilder.ReturnPlace, Copy(s)).Return();
        int body = f.NewBlock();
        f.At(body).Assign(s, Rvalue.Binary(BinaryOp.Add, Copy(s), Copy(element)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(add);
        return f.Build();
    }

    private static IrFunction LenOf()
    {
        var f = new FunctionBuilder("len_of", IrType.U64);
        int n = f.Argument(IrType.I64, "n");
        int a = f.Local(IrType.Box(new ArrayType(IrType.U8)), "a");
        f.Assign(a, Rvalue.NewArray(AllocKind.Box, IrType.U8, Copy(n)))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Len(new Place(a).Deref()))
         .Return();
        return f.Build();
    }

    private static IrFunction OutOfBounds()
    {
        var f = new FunctionBuilder("oob", IrType.I64);
        int a = f.Local(IrType.Box(new ArrayType(IrType.I64)), "a", mutable: true);
        int i = f.Local(IrType.I64, "i");
        f.Assign(a, Rvalue.NewArray(AllocKind.Box, IrType.I64, Int(3, IrType.I64))).Assign(i, Int(5, IrType.I64))
         .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(a).Deref().Index(i)))
         .Return();
        return f.Build();
    }

    /// <summary>unsafe fn gc_array(n: i64) -> i64 { arr = gc [gc Point; n]; arr[i] = gc Point{i, i}…; arr[n-1].x }</summary>
    private static IrFunction GcArray()
    {
        var f = new FunctionBuilder("gc_array", IrType.I64) { Unsafe = true };
        int n = f.Argument(IrType.I64, "n");
        int arr = f.Local(IrType.Gc(new ArrayType(GcPoint)), "arr");
        int i = f.Local(IrType.I64, "i", mutable: true);
        int p = f.Local(Point, "p", mutable: true);
        int last = f.Local(IrType.I64, "last");
        int c = f.Local(IrType.Bool, "c", mutable: true);
        int x = f.Local(IrType.I32, "x");
        int head = f.NewBlock(), body = f.NewBlock(), exit = f.NewBlock();
        f.Assign(arr, Rvalue.NewArray(AllocKind.Gc, GcPoint, Copy(n))).Assign(i, Int(0, IrType.I64)).Goto(head);
        f.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), body, exit);
        f.At(body)
         .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32)).Assign(new Place(p).Field(1), Rvalue.Cast(Copy(i), IrType.I32))
         .Assign(new Place(arr).Deref().Index(i), Rvalue.Gc(Move(p)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
        f.At(exit)
         .Assign(last, Rvalue.Binary(BinaryOp.Sub, Copy(n), Int(1, IrType.I64)))
         .Assign(x, Copy(new Place(arr).Deref().Index(last).Deref().Field(0)))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(x), IrType.I64))
         .Return();
        return f.Build();
    }
}
