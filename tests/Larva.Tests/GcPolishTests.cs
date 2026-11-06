using Larva.IL;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Encoding;
using Larva.IR.Lowering;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>Drop flags, reference transfer at last use, precise safepoint roots and arena regions.</summary>
public sealed class GcPolishTests : CheckedTest
{
    private const int PointId = 0, HolderId = 1;
    private static readonly IrType Point = IrType.Struct(PointId);
    private static readonly IrType Holder = IrType.Struct(HolderId);
    private static readonly IrType GcPoint = IrType.Gc(Point);
    private static readonly StructDef[] Structs =
    [
        new("Point", IrType.I32, IrType.I32),
        new("Holder", IrType.Gc(IrType.Struct(PointId)), IrType.I32),
    ];

    // ── Drop flags ─────────────────────────────────────────────────

    [Fact]
    public void Maybe_moved_boxes_get_drop_flags()
    {
        // fn give(b: box Point) {}
        var give = new FunctionBuilder("give", IrType.Unit);
        give.Argument(IrType.Box(Point), "b");
        give.Return();

        IrFunction Branchy(string name, bool moveOnBothPaths)
        {
            var f = new FunctionBuilder(name, IrType.Unit);
            int c = f.Argument(IrType.Bool, "c");
            int p = f.Local(Point, "p", mutable: true);
            int b = f.Local(IrType.Box(Point), "b");
            int unit = f.Temp(IrType.Unit);
            int then = f.NewBlock(), otherwise = f.NewBlock(), join = f.NewBlock();
            f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
             .Assign(b, Rvalue.Box(Move(p)))
             .If(Copy(c), then, otherwise);
            f.At(then).Call(0, [Move(b)], unit, join);
            if (moveOnBothPaths)
                f.At(otherwise).Call(0, [Move(b)], unit, join);
            else
                f.At(otherwise).Goto(join);
            f.At(join).Return();
            return f.Build();
        }

        var module = new IrModule([Structs[PointId]], [give.Build(), Branchy("maybe_give", false), Branchy("always_give", true)]);
        var il = Compile(module, GcConfig.Manual);
        var maybe = Fn(il, "maybe_give");
        Check("maybe-moved box: one guarded free", Count(maybe, "gc.free") == 1 && Count(maybe, "brfalse") >= 2,
            $"free {Count(maybe, "gc.free")}, brfalse {Count(maybe, "brfalse")}");
        Check("maybe-moved box: has a flag slot", maybe.LocalTypes.Length > module.Functions[1].Locals.Length - module.Functions[1].ArgumentCount);
        var always = Fn(il, "always_give");
        Check("moved on every path: no free, no flag", Count(always, "gc.free") == 0
            && always.LocalTypes.Length == module.Functions[2].Locals.Length - module.Functions[2].ArgumentCount);
    }

    // ── RC transfer ────────────────────────────────────────────────

    [Fact]
    public void RefCount_transfers_at_last_use_and_guards_releases()
    {
        // fn twice(h: gc Point, holder: &mut Holder) -> gc Point { holder.0 = h; h }
        var twice = new FunctionBuilder("twice", GcPoint);
        int h = twice.Argument(GcPoint, "h");
        int holder = twice.Argument(IrType.RefMut(Holder), "holder");
        twice.Assign(new Place(holder).Deref().Field(0), Copy(h)).Assign(FunctionBuilder.ReturnPlace, Move(h)).Return();

        // fn pair(a: gc Point, b: gc Point) {}  and  fn both(h: gc Point) { pair(h, h) }
        var pair = new FunctionBuilder("pair", IrType.Unit);
        pair.Argument(GcPoint, "a");
        pair.Argument(GcPoint, "b");
        pair.Return();
        var both = new FunctionBuilder("both", IrType.Unit);
        int hb = both.Argument(GcPoint, "h");
        int unit = both.Temp(IrType.Unit);
        int done = both.NewBlock();
        both.Call(1, [Copy(hb), Copy(hb)], unit, done);
        both.At(done).Return();

        // fn maybe_keep(c: bool, h: gc Point, holder: &mut Holder) { if c { holder.0 = h } }
        var keep = new FunctionBuilder("maybe_keep", IrType.Unit);
        int c = keep.Argument(IrType.Bool, "c");
        int hk = keep.Argument(GcPoint, "h");
        int hold = keep.Argument(IrType.RefMut(Holder), "holder");
        int then = keep.NewBlock(), join = keep.NewBlock();
        keep.If(Copy(c), then, join);
        keep.At(then).Assign(new Place(hold).Deref().Field(0), Move(hk)).Goto(join);
        keep.At(join).Return();

        var module = new IrModule(Structs, [twice.Build(), pair.Build(), both.Build(), keep.Build()]);
        var il = Compile(module, GcConfig.RefCount);
        Check("earlier read retains, last read transfers", Count(Fn(il, "twice"), "gc.retain") == 1 && Count(Fn(il, "twice"), "gc.release") == 1);
        // Normal path: both reads retain, the owner releases once. The cleanup pad (taken only if `pair` throws) releases it too.
        var bothIl = Fn(il, "both");
        int padStart = bothIl.Handlers.Length == 1 ? bothIl.Handlers[0].Handler : bothIl.Body.Operations.Length;
        Check("two reads in one call: both retain, owner released",
            Count(bothIl, "gc.retain") == 2 && bothIl.Body.Operations.Take(padStart).Count(o => o.Mnemonic == "gc.release") == 1
            && bothIl.Handlers is [{ CatchType: null }]);
        var mk = Fn(il, "maybe_keep");
        Check("transfer on one branch: release guarded by a drop flag",
            Count(mk, "gc.retain") == 0 && Count(mk, "gc.release") == 2 && Count(mk, "brfalse") >= 2,
            $"retain {Count(mk, "gc.retain")}, release {Count(mk, "gc.release")}, brfalse {Count(mk, "brfalse")}");
    }

    // ── Precise safepoints ─────────────────────────────────────────

    [Fact]
    public void Safepoints_record_only_live_handles()
    {
        // fn read(h: gc Point) -> i32 { h.0 }
        var read = new FunctionBuilder("read", IrType.I32);
        int rh = read.Argument(GcPoint, "h");
        read.Assign(FunctionBuilder.ReturnPlace, Copy(new Place(rh).Deref().Field(0))).Return();

        // fn roots(h: gc Point, k: gc Point) -> i32 { let x = read(k); let y = read(h); x + y }
        var roots = new FunctionBuilder("roots", IrType.I32);
        int h = roots.Argument(GcPoint, "h");
        int k = roots.Argument(GcPoint, "k");
        int x = roots.Local(IrType.I32, "x");
        int y = roots.Local(IrType.I32, "y");
        int b1 = roots.NewBlock(), b2 = roots.NewBlock();
        roots.Call(0, [Copy(k)], x, b1);
        roots.At(b1).Call(0, [Copy(h)], y, b2);
        roots.At(b2).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(x), Copy(y))).Return();

        // fn spin_hold(h: gc Point, n: i32) -> i32 { let mut i = 0; while i < n { i += 1 } h.1 }
        var spin = new FunctionBuilder("spin_hold", IrType.I32);
        int sh = spin.Argument(GcPoint, "h");
        int n = spin.Argument(IrType.I32, "n");
        int i = spin.Local(IrType.I32, "i", mutable: true);
        int cond = spin.Temp(IrType.Bool);
        int head = spin.NewBlock(), body = spin.NewBlock(), exit = spin.NewBlock();
        spin.Assign(i, Int(0)).Goto(head);
        spin.At(head).Assign(cond, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Move(cond), body, exit);
        spin.At(body).Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1))).Goto(head);
        spin.At(exit).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(sh).Deref().Field(1))).Return();

        var module = new IrModule(Structs, [read.Build(), roots.Build(), spin.Build()]);
        var il = Compile(module, GcConfig.Default);

        var points = Fn(il, "roots").Safepoints;
        Check("call 1: only h survives (k is dead after its last use)",
            points.Length == 2 && points[0].Roots is [{ IsArgument: true, Slot: 0 }], points.Length > 0 ? points[0].ToString() : "none");
        Check("call 2: nothing to scan", points.Length == 2 && points[1].Roots.Length == 0, points.Length > 1 ? points[1].ToString() : "none");
        var poll = Fn(il, "spin_hold").Safepoints;
        Check("loop poll keeps h alive", poll is [{ Roots: [{ IsArgument: true, Slot: 0 }] }], string.Join(" ", poll));
        Check("safepoints point at call/poll after peephole",
            il.Functions.SelectMany(f => f.Safepoints.Select(s => f.Body.Operations[s.Operation].Mnemonic)).All(m => m is "call" or "sys.poll"));
    }

    // ── Arena regions ──────────────────────────────────────────────

    [Fact]
    public void Arena_region_code_compiles_round_trips_and_needs_arena_mode()
    {
        { // values that stay inside are fine; only an i32 leaves
            var f = new FunctionBuilder("in_region", IrType.I32);
            int p = f.Local(Point, "p", mutable: true);
            int g = f.Local(GcPoint, "g");
            int v = f.Local(IrType.I32, "v");
            f.RegionEnter(0)
             .Assign(new Place(p).Field(0), Int(4)).Assign(new Place(p).Field(1), Int(5))
             .Assign(g, Rvalue.Gc(Move(p)))
             .Assign(v, Copy(new Place(g).Deref().Field(0)))
             .RegionExit(0)
             .Assign(FunctionBuilder.ReturnPlace, Copy(v))
             .Return();
            var module = new IrModule(Structs, [f.Build()]);
            var result = Compiler.Compile(module, new CompilationOptions { Gc = GcConfig.Arena });
            Check("in-region code compiles", result.Success, string.Join(" | ", result.TypeErrors.Select(e => e.Message)));
            if (result.Success)
            {
                var ops = Fn(result.Il!, "in_region").Body.Operations;
                Check("emits gc.renter / gc.rexit", ops.Any(o => o.Mnemonic == "gc.renter") && ops.Any(o => o.Mnemonic == "gc.rexit"));
            }

            var bytes = IrEncoder.Encode(module);
            Check("IR round trip with regions", IrDecoder.Decode(bytes).ToString() == module.ToString());
            ExpectRegion(module, GcConfig.Default, TypeErrorKind.RegionsNotAvailable);
        }
    }

    [Fact]
    public void Region_escapes_and_bad_nesting_are_rejected()
    {
        { // the handle itself outlives the region
            var f = new FunctionBuilder("escape_return", GcPoint);
            int p = f.Local(Point, "p", mutable: true);
            int g = f.Local(GcPoint, "g");
            f.RegionEnter(0)
             .Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
             .Assign(g, Rvalue.Gc(Move(p)))
             .RegionExit(0)
             .Assign(FunctionBuilder.ReturnPlace, Move(g))
             .Return();
            ExpectRegion(new IrModule(Structs, [f.Build()]), GcConfig.Arena, TypeErrorKind.RegionEscape);
        }
        { // stored into memory outside the region
            var f = new FunctionBuilder("escape_store", IrType.Unit);
            int holder = f.Argument(IrType.RefMut(Holder), "holder");
            int p = f.Local(Point, "p", mutable: true);
            int g = f.Local(GcPoint, "g");
            f.RegionEnter(0)
             .Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
             .Assign(g, Rvalue.Gc(Move(p)))
             .Assign(new Place(holder).Deref().Field(0), Move(g))
             .RegionExit(0)
             .Return();
            ExpectRegion(new IrModule(Structs, [f.Build()]), GcConfig.Arena, TypeErrorKind.RegionEscape);
        }
        { // a borrow into the region outlives it
            var f = new FunctionBuilder("escape_borrow", IrType.I32);
            int p = f.Local(Point, "p", mutable: true);
            int g = f.Local(GcPoint, "g");
            int r = f.Local(IrType.Ref(IrType.I32), "r");
            f.RegionEnter(0)
             .Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
             .Assign(g, Rvalue.Gc(Move(p)))
             .Assign(r, Rvalue.Ref(new Place(g).Deref().Field(0)))
             .RegionExit(0)
             .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(r).Deref()))
             .Return();
            ExpectRegion(new IrModule(Structs, [f.Build()]), GcConfig.Arena, TypeErrorKind.RegionEscape);
        }
        { // storing into an object of the same region is fine (unsafe: gc objects are otherwise immutable)
            var f = new FunctionBuilder("same_region", IrType.I32) { Unsafe = true };
            int p = f.Local(Point, "p", mutable: true);
            int g1 = f.Local(GcPoint, "g1");
            int g2 = f.Local(GcPoint, "g2");
            int hv = f.Local(Holder, "hv", mutable: true);
            int a = f.Local(IrType.Gc(Holder), "a");
            f.RegionEnter(0)
             .Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
             .Assign(g1, Rvalue.Gc(Move(p)))
             .Assign(new Place(hv).Field(0), Move(g1)).Assign(new Place(hv).Field(1), Int(3))
             .Assign(a, Rvalue.Gc(Move(hv)))
             .Assign(new Place(p).Field(0), Int(5)).Assign(new Place(p).Field(1), Int(6))
             .Assign(g2, Rvalue.Gc(Move(p)))
             .Assign(new Place(a).Deref().Field(0), Move(g2))
             .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(a).Deref().Field(1)))
             .RegionExit(0)
             .Return();
            ExpectRegion(new IrModule(Structs, [f.Build()]), GcConfig.Arena, null);
        }
        { // nesting: left open at return
            var f = new FunctionBuilder("left_open", IrType.Unit);
            f.RegionEnter(0).Return();
            ExpectRegion(new IrModule(Structs, [f.Build()]), GcConfig.Arena, TypeErrorKind.RegionNesting);
        }
        { // nesting: closed out of order
            var f = new FunctionBuilder("crossed", IrType.Unit);
            f.RegionEnter(0).RegionEnter(1).RegionExit(0).RegionExit(1).Return();
            ExpectRegion(new IrModule(Structs, [f.Build()]), GcConfig.Arena, TypeErrorKind.RegionNesting);
        }
        { // nesting: paths disagree
            var f = new FunctionBuilder("diverge", IrType.Unit);
            int c = f.Argument(IrType.Bool, "c");
            int then = f.NewBlock(), join = f.NewBlock();
            f.If(Copy(c), then, join);
            f.At(then).RegionEnter(0).Goto(join);
            f.At(join).Return();
            ExpectRegion(new IrModule(Structs, [f.Build()]), GcConfig.Arena, TypeErrorKind.RegionNesting);
        }
    }

    private void ExpectRegion(IrModule module, GcConfig gc, TypeErrorKind? expected)
    {
        var result = Compiler.Compile(module, new CompilationOptions { Gc = gc });
        var errors = result.TypeErrors;
        bool pass = expected is null ? result.Success : errors.Count >= 1 && errors.All(e => e.Kind == expected);
        Check($"{module.Functions[^1].Name} [{gc.Mode}]: {expected?.ToString() ?? "ok"}", pass,
            errors.Count == 0 ? null : string.Join(" | ", errors.Select(e => e.Message)));
    }

    // ── Helpers ────────────────────────────────────────────────────

    private IlModule Compile(IrModule module, GcConfig gc)
    {
        var result = Compiler.Compile(module, new CompilationOptions { Gc = gc });
        Check($"compiles in {gc.Mode} mode", result.Success && result.Diagnostics.Count == 0);
        return result.Il ?? new IlModule([], [], [], [], []);
    }

    private static IlFunction Fn(IlModule il, string name) => il.Functions.Single(f => f.Name == name);

    private static int Count(IlFunction function, string mnemonic) => function.Body.Operations.Count(o => o.Mnemonic == mnemonic);
}
