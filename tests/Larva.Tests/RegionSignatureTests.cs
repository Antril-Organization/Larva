using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Encoding;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>Region parameters on signatures: callee bodies obey them, call sites are checked against them.</summary>
public sealed class RegionSignatureTests : CheckedTest
{
    private const int PointId = 0, HolderId = 1;
    private const int FillFn = 0, IdFn = 1, MakeFn = 2;
    private static readonly IrType Point = IrType.Struct(PointId);
    private static readonly IrType Holder = IrType.Struct(HolderId);
    private static readonly IrType GcPoint = IrType.Gc(Point);
    private static readonly StructDef[] Structs =
    [
        new("Point", IrType.I32, IrType.I32),
        new("Holder", IrType.Gc(IrType.Struct(PointId)), IrType.I32),
    ];

    private static IrFunction[] Library() => [Fill("fill", 0, 0), Id("id", 0, 0), Make()];

    [Fact]
    public void Region_annotations_round_trip_through_the_encoding()
    {
        var shown = new IrModule(Structs, Library());
        Check("IR round trip with region annotations", IrDecoder.Decode(IrEncoder.Encode(shown)).ToString() == shown.ToString());
    }

    // ── callee bodies ──

    [Fact]
    public void Callee_bodies_obey_their_region_signatures()
    {
        Expect(Library(), null, "library");
        Expect([Fill("fill_private", null, null)], TypeErrorKind.RegionEscape);
        Expect([Fill("fill_mixed", 0, 1)], TypeErrorKind.RegionEscape);
        Expect([Id("id_private", null, null)], TypeErrorKind.RegionEscape);
        Expect([Id("id_wrong", 1, 0)], TypeErrorKind.RegionEscape);
        Expect([Fill("fill_private", null, null)], null, "fill_private", GcConfig.Default);
        Expect([Misplaced()], TypeErrorKind.RegionNesting, "misplaced annotation");
    }

    // ── call sites ──

    [Fact]
    public void Call_sites_are_checked_against_region_signatures()
    {
        IrFunction[] library = Library();
        Expect([.. library, CallerOuter()], TypeErrorKind.RegionEscape);
        Expect([.. library, CallerInside()], null);
        Expect([.. library, AmbientResult()], TypeErrorKind.RegionEscape);
        Expect([.. library, IdThrough()], TypeErrorKind.RegionEscape);
        Expect([.. library, IdOutside()], null);
        Expect([.. library, Forward("forward", shareRegion: true)], null);
        Expect([.. library, Forward("forward_private", shareRegion: false)], TypeErrorKind.RegionEscape);
    }

    // ── Library ────────────────────────────────────────────────────

    /// <summary>fn fill&lt;'a, 'b&gt;(holder: &amp;mut Holder [in 'r1], h: gc Point [in 'r2]) { holder.0 = h }</summary>
    private static IrFunction Fill(string name, int? holderRegion, int? valueRegion)
    {
        var f = new FunctionBuilder(name, IrType.Unit);
        f.RegionParam("a");
        f.RegionParam("b");
        int holder = f.Argument(IrType.RefMut(Holder), "holder", region: holderRegion);
        int h = f.Argument(GcPoint, "h", region: valueRegion);
        f.Assign(new Place(holder).Deref().Field(0), Move(h)).Return();
        return f.Build();
    }

    /// <summary>fn id&lt;'a, 'b&gt;(h: gc Point [in 'r1]) -> gc Point [in 'r2] { h }</summary>
    private static IrFunction Id(string name, int? argumentRegion, int? returnRegion)
    {
        var f = new FunctionBuilder(name, GcPoint);
        f.RegionParam("a");
        f.RegionParam("b");
        if (returnRegion is int r)
            f.ReturnsIn(r);
        int h = f.Argument(GcPoint, "h", region: argumentRegion);
        f.Assign(FunctionBuilder.ReturnPlace, Move(h)).Return();
        return f.Build();
    }

    /// <summary>fn make() -> gc Point — allocates in whatever region the caller has open.</summary>
    private static IrFunction Make()
    {
        var f = new FunctionBuilder("make", GcPoint);
        int p = f.Local(Point, "p", mutable: true);
        f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Gc(Move(p)))
         .Return();
        return f.Build();
    }

    private static IrFunction Misplaced()
    {
        var f = new FunctionBuilder("misplaced", IrType.Unit);
        f.RegionParam("a");
        f.Local(GcPoint, "local");
        f.Return();
        var built = f.Build();
        return built with { Locals = [built.Locals[0], built.Locals[1] with { Region = 0 }] };
    }

    // ── Callers ────────────────────────────────────────────────────

    /// <summary>A region value handed to `fill` together with memory from outside the region.</summary>
    private static IrFunction CallerOuter()
    {
        var f = new FunctionBuilder("caller_outer", IrType.Unit);
        int a = f.RegionParam("a");
        int holder = f.Argument(IrType.RefMut(Holder), "holder", region: a);
        int g = f.Local(GcPoint, "g");
        int unit = f.Temp(IrType.Unit);
        int made = f.NewBlock(), done = f.NewBlock();
        f.RegionEnter(0).Call(MakeFn, [], g, made);
        f.At(made).Call(FillFn, [Move(holder), Move(g)], unit, done);
        f.At(done).RegionExit(0).Return();
        return f.Build();
    }

    /// <summary>Both the holder and the value live in r0: fine.</summary>
    private static IrFunction CallerInside()
    {
        var f = new FunctionBuilder("caller_inside", IrType.I32) { Unsafe = true };
        int p = f.Local(Point, "p", mutable: true);
        int g1 = f.Local(GcPoint, "g1");
        int g2 = f.Local(GcPoint, "g2");
        int hv = f.Local(Holder, "hv", mutable: true);
        int a = f.Local(IrType.Gc(Holder), "a");
        int r = f.Local(IrType.RefMut(Holder), "r");
        int unit = f.Temp(IrType.Unit);
        int done = f.NewBlock();
        f.RegionEnter(0)
         .Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Assign(g1, Rvalue.Gc(Move(p)))
         .Assign(new Place(hv).Field(0), Move(g1)).Assign(new Place(hv).Field(1), Int(3))
         .Assign(a, Rvalue.Gc(Move(hv)))
         .Assign(new Place(p).Field(0), Int(4)).Assign(new Place(p).Field(1), Int(5))
         .Assign(g2, Rvalue.Gc(Move(p)))
         .Assign(r, Rvalue.RefMut(new Place(a).Deref()))
         .Call(FillFn, [Move(r), Move(g2)], unit, done);
        f.At(done).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(a).Deref().Field(1))).RegionExit(0).Return();
        return f.Build();
    }

    /// <summary>`make` allocates in r0 (the caller's open region), so its result must not outlive r0.</summary>
    private static IrFunction AmbientResult()
    {
        var f = new FunctionBuilder("ambient_result", GcPoint);
        int x = f.Local(GcPoint, "x");
        int done = f.NewBlock();
        f.RegionEnter(0).Call(MakeFn, [], x, done);
        f.At(done).RegionExit(0).Assign(FunctionBuilder.ReturnPlace, Move(x)).Return();
        return f.Build();
    }

    /// <summary>`id` returns its argument's region: r0 flows out through the call.</summary>
    private static IrFunction IdThrough()
    {
        var f = new FunctionBuilder("id_through", GcPoint);
        int g = f.Local(GcPoint, "g");
        int x = f.Local(GcPoint, "x");
        int b1 = f.NewBlock(), b2 = f.NewBlock();
        f.RegionEnter(0).Call(MakeFn, [], g, b1);
        f.At(b1).Call(IdFn, [Move(g)], x, b2);
        f.At(b2).RegionExit(0).Assign(FunctionBuilder.ReturnPlace, Move(x)).Return();
        return f.Build();
    }

    /// <summary>No region open: everything belongs to the caller's region and may be returned.</summary>
    private static IrFunction IdOutside()
    {
        var f = new FunctionBuilder("id_outside", GcPoint);
        int g = f.Local(GcPoint, "g");
        int b1 = f.NewBlock(), b2 = f.NewBlock();
        f.Call(MakeFn, [], g, b1);
        f.At(b1).Call(IdFn, [Move(g)], FunctionBuilder.ReturnPlace, b2);
        f.At(b2).Return();
        return f.Build();
    }

    /// <summary>Passing its own arguments on to `fill`: needs them in the same region parameter.</summary>
    private static IrFunction Forward(string name, bool shareRegion)
    {
        var f = new FunctionBuilder(name, IrType.Unit);
        int a = f.RegionParam("a");
        int holder = f.Argument(IrType.RefMut(Holder), "holder", region: a);
        int h = f.Argument(GcPoint, "h", region: shareRegion ? a : null);
        int unit = f.Temp(IrType.Unit);
        int done = f.NewBlock();
        f.Call(FillFn, [Move(holder), Move(h)], unit, done);
        f.At(done).Return();
        return f.Build();
    }

    // ── Helpers ────────────────────────────────────────────────────

    private void Expect(IrFunction[] functions, TypeErrorKind? expected, string? label = null, GcConfig? gc = null)
    {
        gc ??= GcConfig.Arena;
        var result = Compiler.Compile(new IrModule(Structs, functions), new CompilationOptions { Gc = gc });
        var errors = result.TypeErrors;
        bool pass = expected is null ? result.Success && result.Diagnostics.Count == 0 : errors.Count >= 1 && errors.All(e => e.Kind == expected);
        string detail = string.Join(" | ", errors.Select(e => e.Message).Concat(result.Diagnostics.Select(d => d.Message)));
        Check($"{label ?? functions[^1].Name} [{gc.Mode}]: {expected?.ToString() ?? "ok"}", pass, detail.Length == 0 ? null : detail);
    }
}
