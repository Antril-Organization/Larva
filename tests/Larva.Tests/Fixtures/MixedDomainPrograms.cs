using Larva.IR;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>
/// Modules of different GC domains linked together (the GC mixing design): gclib (tracing), mapp (Manual), rclib and rcfin
/// (RefCount), tapp, arenalib (Arena), store, aapp, mfree, mfree2, dapp, rcapp.
/// </summary>
internal static class MixedDomainPrograms
{
    public static readonly IrType Cell = IrType.Struct(0);

    public static StructDef CellDef(Linkage linkage, string? symbol = null) =>
        new("Cell", IrType.I32) { FieldNames = ["v"], Linkage = linkage, Symbol = symbol };

    /// <summary>module gclib (the program's GC): Cell, cell_new, cell_get, churn (allocates garbage under a safepoint loop).</summary>
    public static IrModule GcLib()
    {
        var make = new FunctionBuilder("cell_new", IrType.Gc(Cell)) { Linkage = Linkage.Export };
        int x = make.Argument(IrType.I32, "x");
        int cell = make.Local(Cell, "cell", mutable: true);
        make.Assign(new Place(cell).Field(0), Copy(x)).Assign(FunctionBuilder.ReturnPlace, new AllocRvalue(AllocKind.Gc, Move(cell))).Return();

        var get = new FunctionBuilder("cell_get", IrType.I32) { Linkage = Linkage.Export };
        int c = get.Argument(IrType.Gc(Cell), "c");
        get.Assign(FunctionBuilder.ReturnPlace, Copy(new Place(c).Deref().Field(0))).Return();

        var churn = new FunctionBuilder("churn", IrType.Unit) { Linkage = Linkage.Export };
        int n = churn.Argument(IrType.I32, "n");
        int i = churn.Local(IrType.I32, "i", mutable: true);
        int value = churn.Local(Cell, "value", mutable: true);
        int made = churn.Local(IrType.Gc(Cell), "made", mutable: true);
        int more = churn.Local(IrType.Bool, null, mutable: true);
        int head = churn.NewBlock(), body = churn.NewBlock(), done = churn.NewBlock();
        churn.Assign(i, Int(0)).Goto(head);
        churn.At(head).Assign(more, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(more), body, done);
        churn.At(body).Assign(new Place(value).Field(0), Copy(i)).Assign(made, new AllocRvalue(AllocKind.Gc, Move(value)))
            .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1))).Goto(head);
        churn.At(done).Return();

        return new IrModule([CellDef(Linkage.Export)], [make.Build(), get.Build(), churn.Build()]) { Name = "gclib" };
    }

    /// <summary>
    /// module mapp (Manual): a raw Holder keeps a gc Cell of gclib across two collections with garbage in between; main
    /// reads it back and adds 100 × the cell's domain (0: gclib is the program's own).
    /// </summary>
    public static IrModule ManualApp()
    {
        var make = new FunctionBuilder("cell_new", IrType.Gc(Cell)).ImportFrom("gclib::cell_new");
        make.Argument(IrType.I32, "x");
        var get = new FunctionBuilder("cell_get", IrType.I32).ImportFrom("gclib::cell_get");
        get.Argument(IrType.Gc(Cell), "c");
        var churn = new FunctionBuilder("churn", IrType.Unit).ImportFrom("gclib::churn");
        churn.Argument(IrType.I32, "n");

        var holder = IrType.Struct(1);
        var main = new FunctionBuilder("main", IrType.I32) { Unsafe = true };
        int value = main.Local(holder, "value", mutable: true);
        int h = main.Local(IrType.PtrMut(holder), "h", mutable: true);
        int c = main.Local(IrType.Gc(Cell), "c", mutable: true);
        int r = main.Local(IrType.I32, "r", mutable: true);
        int d = main.Local(IrType.I64, "d", mutable: true);
        int d32 = main.Local(IrType.I32, "d32", mutable: true);
        int raw = main.Local(IrType.PtrMut(IrType.U8), "raw", mutable: true);
        int unit = main.Local(IrType.Unit, null, mutable: true);
        int[] next = [.. Enumerable.Range(0, 4).Select(_ => main.NewBlock())];
        main.Call(0, [Int(5)], c, next[0]);
        main.At(next[0]).Assign(new Place(value).Field(0), Move(c)).Assign(h, new AllocRvalue(AllocKind.Raw, Move(value)))
            .Assign(unit, new IntrinsicRvalue("gc.request", [])).Call(2, [Int(3000)], unit, next[1]);
        main.At(next[1]).Assign(unit, new IntrinsicRvalue("gc.request", [])).Call(2, [Int(3000)], unit, next[2]);
        main.At(next[2]).Call(1, [Copy(new Place(h).Deref().Field(0))], r, next[3]);
        main.At(next[3]).Assign(raw, Rvalue.Cast(Copy(new Place(h).Deref().Field(0)), IrType.PtrMut(IrType.U8)))
            .Assign(d, new IntrinsicRvalue("gc.domain", [Copy(raw)]))
            .Assign(d32, Rvalue.Cast(Copy(d), IrType.I32))
            .Assign(d32, Rvalue.Binary(BinaryOp.Mul, Copy(d32), Int(100)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(r), Copy(d32))).Return();

        var holderDef = new StructDef("Holder", IrType.Gc(Cell)) { FieldNames = ["c"] };
        return new IrModule([CellDef(Linkage.Import, "gclib::Cell"), holderDef], [make.Build(), get.Build(), churn.Build(), main.Build()]) { Name = "mapp" };
    }

    /// <summary>module rclib (RefCount): its own Cell and rc_new.</summary>
    public static IrModule RcLib()
    {
        var make = new FunctionBuilder("rc_new", IrType.Gc(Cell)) { Linkage = Linkage.Export };
        int x = make.Argument(IrType.I32, "x");
        int cell = make.Local(Cell, "cell", mutable: true);
        make.Assign(new Place(cell).Field(0), Copy(x)).Assign(FunctionBuilder.ReturnPlace, new AllocRvalue(AllocKind.Gc, Move(cell))).Return();
        return new IrModule([CellDef(Linkage.Export)], [make.Build()]) { Name = "rclib" };
    }

    /// <summary>
    /// module rcfin (RefCount): RCell with a finalizer that counts the dead in a global; rc_new_fin, rc_get, rc_freed.
    /// </summary>
    public static IrModule RcFin()
    {
        var fin = new FunctionBuilder("rc_fin", IrType.Unit);
        fin.Argument(IrType.Gc(Cell), "dead");
        int count = fin.Local(IrType.I32, "count", mutable: true);
        fin.Assign(count, new GlobalRvalue(0)).Assign(count, Rvalue.Binary(BinaryOp.Add, Copy(count), Int(1))).StoreGlobal(0, Copy(count)).Return();

        var make = new FunctionBuilder("rc_new_fin", IrType.Gc(Cell)) { Linkage = Linkage.Export };
        int x = make.Argument(IrType.I32, "x");
        int cell = make.Local(Cell, "cell", mutable: true);
        int finalizer = make.Local(new FunctionType([IrType.Gc(Cell)], IrType.Unit), "finalizer", mutable: true);
        make.Assign(new Place(cell).Field(0), Copy(x)).Assign(finalizer, new FunctionRefRvalue(0, []))
            .Assign(FunctionBuilder.ReturnPlace, new AllocRvalue(AllocKind.Gc, Move(cell), Copy(finalizer))).Return();

        var get = new FunctionBuilder("rc_get", IrType.I32) { Linkage = Linkage.Export };
        int c = get.Argument(IrType.Gc(Cell), "c");
        get.Assign(FunctionBuilder.ReturnPlace, Copy(new Place(c).Deref().Field(0))).Return();

        var freed = new FunctionBuilder("rc_freed", IrType.I32) { Linkage = Linkage.Export };
        freed.Assign(FunctionBuilder.ReturnPlace, new GlobalRvalue(0)).Return();

        return new IrModule([CellDef(Linkage.Export)], [fin.Build(), make.Build(), get.Build(), freed.Build()])
            { Name = "rcfin", Globals = [new GlobalDef("freed", IrType.I32)] };
    }

    /// <summary>
    /// module tapp (the program's GC, tracing): keeps counted cells of rcfin in a tracing Box; overwriting the field releases
    /// the old cell (its finalizer runs), a copy in a local keeps the next one alive: rc_get(copy) × 100 + freed × 10 + freed → 211.
    /// </summary>
    public static IrModule TracingRcApp()
    {
        var make = new FunctionBuilder("rc_new_fin", IrType.Gc(Cell)).ImportFrom("rcfin::rc_new_fin");
        make.Argument(IrType.I32, "x");
        var get = new FunctionBuilder("rc_get", IrType.I32).ImportFrom("rcfin::rc_get");
        get.Argument(IrType.Gc(Cell), "c");
        var freed = new FunctionBuilder("rc_freed", IrType.I32).ImportFrom("rcfin::rc_freed");

        var boxType = IrType.Struct(1);
        var main = new FunctionBuilder("main", IrType.I32) { Unsafe = true };
        int a = main.Local(IrType.Gc(Cell), "a", mutable: true);
        int value = main.Local(boxType, "value", mutable: true);
        int box = main.Local(IrType.Gc(boxType), "box", mutable: true);
        int c = main.Local(IrType.Gc(Cell), "c", mutable: true);
        int r1 = main.Local(IrType.I32, "r1", mutable: true), r2 = main.Local(IrType.I32, "r2", mutable: true), v = main.Local(IrType.I32, "v", mutable: true);
        int[] next = [.. Enumerable.Range(0, 6).Select(_ => main.NewBlock())];
        main.Call(0, [Int(1)], a, next[0]);
        main.At(next[0]).Assign(new Place(value).Field(0), Move(a)).Assign(box, new AllocRvalue(AllocKind.Gc, Move(value))).Call(0, [Int(2)], a, next[1]);
        // The box's old cell (1) loses its last reference here: its finalizer counts it.
        main.At(next[1]).Assign(new Place(box).Deref().Field(0), Move(a)).Call(2, [], r1, next[2]);
        // A copy keeps cell 2 alive through the next overwrite.
        main.At(next[2]).Assign(c, Copy(new Place(box).Deref().Field(0))).Call(0, [Int(3)], a, next[3]);
        main.At(next[3]).Assign(new Place(box).Deref().Field(0), Move(a)).Call(2, [], r2, next[4]);
        main.At(next[4]).Call(1, [Copy(c)], v, next[5]);
        main.At(next[5]).Assign(v, Rvalue.Binary(BinaryOp.Mul, Copy(v), Int(100)))
            .Assign(r1, Rvalue.Binary(BinaryOp.Mul, Copy(r1), Int(10)))
            .Assign(v, Rvalue.Binary(BinaryOp.Add, Copy(v), Copy(r1)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(v), Copy(r2))).Return();

        var boxDef = new StructDef("Box", IrType.Gc(Cell)) { FieldNames = ["cell"], Mutable = true };
        return new IrModule([CellDef(Linkage.Import, "rcfin::Cell"), boxDef], [make.Build(), get.Build(), freed.Build(), main.Build()]) { Name = "tapp" };
    }

    /// <summary>
    /// module arenalib (Arena): make(x) allocates outside any region (the caller's); sum(n) makes n cells inside a region
    /// and closes it; with <paramref name="leak"/>, leak(n) hands a region's cell to another domain's store::keep.
    /// </summary>
    public static IrModule ArenaLib(bool leak)
    {
        var make = new FunctionBuilder("make", IrType.Gc(Cell)) { Linkage = Linkage.Export };
        int x = make.Argument(IrType.I32, "x");
        int cell = make.Local(Cell, "cell", mutable: true);
        make.Assign(new Place(cell).Field(0), Copy(x)).Assign(FunctionBuilder.ReturnPlace, new AllocRvalue(AllocKind.Gc, Move(cell))).Return();

        var sum = new FunctionBuilder("sum", IrType.I32) { Linkage = Linkage.Export };
        int n = sum.Argument(IrType.I32, "n");
        int i = sum.Local(IrType.I32, "i", mutable: true), acc = sum.Local(IrType.I32, "acc", mutable: true);
        int value = sum.Local(Cell, "value", mutable: true);
        int made = sum.Local(IrType.Gc(Cell), "made", mutable: true);
        int more = sum.Local(IrType.Bool, null, mutable: true);
        int head = sum.NewBlock(), body = sum.NewBlock(), done = sum.NewBlock();
        sum.RegionEnter(0).Assign(i, Int(0)).Assign(acc, Int(0)).Goto(head);
        sum.At(head).Assign(more, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(more), body, done);
        sum.At(body).Assign(new Place(value).Field(0), Copy(i)).Assign(made, new AllocRvalue(AllocKind.Gc, Move(value)))
            .Assign(acc, Rvalue.Binary(BinaryOp.Add, Copy(acc), Copy(new Place(made).Deref().Field(0))))
            .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1))).Goto(head);
        sum.At(done).RegionExit(0).Assign(FunctionBuilder.ReturnPlace, Copy(acc)).Return();

        List<IrFunction> functions = [make.Build(), sum.Build()];
        if (leak)
        {
            var keep = new FunctionBuilder("keep", IrType.Unit).ImportFrom("store::keep");
            keep.Argument(IrType.Gc(Cell), "c");
            var leaking = new FunctionBuilder("leak", IrType.I32) { Linkage = Linkage.Export };
            int ln = leaking.Argument(IrType.I32, "n");
            int lvalue = leaking.Local(Cell, "value", mutable: true);
            int lmade = leaking.Local(IrType.Gc(Cell), "made", mutable: true);
            int unit = leaking.Local(IrType.Unit, null, mutable: true);
            int after = leaking.NewBlock();
            leaking.RegionEnter(0).Assign(new Place(lvalue).Field(0), Copy(ln)).Assign(lmade, new AllocRvalue(AllocKind.Gc, Move(lvalue)))
                .Call(2, [Copy(lmade)], unit, after);
            leaking.At(after).RegionExit(0).Assign(FunctionBuilder.ReturnPlace, Int(0)).Return();
            functions.Add(keep.Build());
            functions.Add(leaking.Build());
        }
        return new IrModule([CellDef(Linkage.Export)], [.. functions]) { Name = "arenalib" };
    }

    /// <summary>module store (the program's GC): keep(c) stores a cell in a global.</summary>
    public static IrModule StoreLib()
    {
        var keep = new FunctionBuilder("keep", IrType.Unit) { Linkage = Linkage.Export };
        int c = keep.Argument(IrType.Gc(Cell), "c");
        keep.StoreGlobal(0, Copy(c)).Return();
        return new IrModule([CellDef(Linkage.Import, "arenalib::Cell")], [keep.Build()]) { Name = "store", Globals = [new GlobalDef("held", IrType.Gc(Cell))] };
    }

    /// <summary>module aapp (the program's GC): main = make(7).v + 10 × sum(3) → 37.</summary>
    public static IrModule ArenaApp()
    {
        var make = new FunctionBuilder("make", IrType.Gc(Cell)).ImportFrom("arenalib::make");
        make.Argument(IrType.I32, "x");
        var sum = new FunctionBuilder("sum", IrType.I32).ImportFrom("arenalib::sum");
        sum.Argument(IrType.I32, "n");
        var main = new FunctionBuilder("main", IrType.I32) { Unsafe = true };
        int n = main.Local(IrType.Gc(Cell), "n", mutable: true);
        int s = main.Local(IrType.I32, "s", mutable: true), v = main.Local(IrType.I32, "v", mutable: true);
        int b1 = main.NewBlock(), b2 = main.NewBlock();
        main.Call(0, [Int(7)], n, b1);
        main.At(b1).Call(1, [Int(3)], s, b2);
        main.At(b2).Assign(v, Copy(new Place(n).Deref().Field(0))).Assign(s, Rvalue.Binary(BinaryOp.Mul, Copy(s), Int(10)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(v), Copy(s))).Return();
        return new IrModule([CellDef(Linkage.Import, "arenalib::Cell")], [make.Build(), sum.Build(), main.Build()]) { Name = "aapp" };
    }

    /// <summary>
    /// module mfree (Manual): fill(h, x) puts a counted cell of rcfin into a raw Holder (the native back ends keep a
    /// reference in the local until fill returns; the VM transfers it); main frees the holder, which releases the cell
    /// (its finalizer runs): rc_freed() before × 10 + after → 1.
    /// </summary>
    public static IrModule ManualFree()
    {
        var make = new FunctionBuilder("rc_new_fin", IrType.Gc(Cell)).ImportFrom("rcfin::rc_new_fin");
        make.Argument(IrType.I32, "x");
        var freed = new FunctionBuilder("rc_freed", IrType.I32).ImportFrom("rcfin::rc_freed");
        var holder = IrType.Struct(1);

        var fill = new FunctionBuilder("fill", IrType.Unit) { Unsafe = true };
        int fh = fill.Argument(IrType.PtrMut(holder), "h");
        int fx = fill.Argument(IrType.I32, "x");
        int fc = fill.Local(IrType.Gc(Cell), "c", mutable: true);
        int filled = fill.NewBlock();
        fill.Call(0, [Copy(fx)], fc, filled);
        fill.At(filled).Assign(new Place(fh).Deref().Field(0), Move(fc)).Return();

        var main = new FunctionBuilder("main", IrType.I32) { Unsafe = true };
        int value = main.Local(holder, "value", mutable: true);
        int h = main.Local(IrType.PtrMut(holder), "h", mutable: true);
        int unit = main.Local(IrType.Unit, null, mutable: true);
        int before = main.Local(IrType.I32, "before", mutable: true), after = main.Local(IrType.I32, "after", mutable: true);
        int b1 = main.NewBlock(), b2 = main.NewBlock(), b3 = main.NewBlock();
        main.Assign(value, Rvalue.ZeroFiller(holder)).Assign(h, new AllocRvalue(AllocKind.Raw, Move(value))).Call(2, [Copy(h), Int(1)], unit, b1);
        main.At(b1).Call(1, [], before, b2);
        main.At(b2).Free(new Place(h)).Call(1, [], after, b3);
        main.At(b3).Assign(before, Rvalue.Binary(BinaryOp.Mul, Copy(before), Int(10)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(before), Copy(after))).Return();
        var holderDef = new StructDef("Holder", IrType.Gc(Cell)) { FieldNames = ["c"], Mutable = true };
        return new IrModule([CellDef(Linkage.Import, "rcfin::Cell"), holderDef], [make.Build(), freed.Build(), fill.Build(), main.Build()]) { Name = "mfree" };
    }

    /// <summary>module dapp (the program's GC): main = domain(gclib cell) + 10 × domain(rclib cell) → 10.</summary>
    public static IrModule DomainApp()
    {
        var make = new FunctionBuilder("cell_new", IrType.Gc(Cell)).ImportFrom("gclib::cell_new");
        make.Argument(IrType.I32, "x");
        var rc = new FunctionBuilder("rc_new", IrType.Gc(IrType.Struct(1))).ImportFrom("rclib::rc_new");
        rc.Argument(IrType.I32, "x");

        var main = new FunctionBuilder("main", IrType.I32) { Unsafe = true };
        int a = main.Local(IrType.Gc(Cell), "a", mutable: true);
        int b = main.Local(IrType.Gc(IrType.Struct(1)), "b", mutable: true);
        int ra = main.Local(IrType.PtrMut(IrType.U8), "ra", mutable: true), rb = main.Local(IrType.PtrMut(IrType.U8), "rb", mutable: true);
        int da = main.Local(IrType.I64, "da", mutable: true), db = main.Local(IrType.I64, "db", mutable: true);
        int sum = main.Local(IrType.I64, "sum", mutable: true);
        int b1 = main.NewBlock(), b2 = main.NewBlock();
        main.Call(0, [Int(1)], a, b1);
        main.At(b1).Call(1, [Int(2)], b, b2);
        main.At(b2).Assign(ra, Rvalue.Cast(Copy(a), IrType.PtrMut(IrType.U8))).Assign(rb, Rvalue.Cast(Copy(b), IrType.PtrMut(IrType.U8)))
            .Assign(da, new IntrinsicRvalue("gc.domain", [Copy(ra)])).Assign(db, new IntrinsicRvalue("gc.domain", [Copy(rb)]))
            .Assign(sum, Rvalue.Binary(BinaryOp.Mul, Copy(db), Operand.Const(10, IrType.I64)))
            .Assign(sum, Rvalue.Binary(BinaryOp.Add, Copy(sum), Copy(da)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(sum), IrType.I32)).Return();

        return new IrModule([CellDef(Linkage.Import, "gclib::Cell"), new StructDef("RcCell", IrType.I32) { FieldNames = ["v"], Linkage = Linkage.Import, Symbol = "rclib::Cell" }],
            [make.Build(), rc.Build(), main.Build()]) { Name = "dapp" };
    }

    /// <summary>
    /// module rcapp (RefCount, the program's GC): keeps a cell of the tracing gclib across two collections its churn
    /// triggers; main = cell.v + 100 × (collections happened) → 107.
    /// </summary>
    public static IrModule RcApp()
    {
        var make = new FunctionBuilder("cell_new", IrType.Gc(Cell)).ImportFrom("gclib::cell_new");
        make.Argument(IrType.I32, "x");
        var churn = new FunctionBuilder("churn", IrType.Unit).ImportFrom("gclib::churn");
        churn.Argument(IrType.I32, "n");
        var main = new FunctionBuilder("main", IrType.I32) { Unsafe = true };
        int n = main.Local(IrType.Gc(Cell), "n", mutable: true);
        int unit = main.Local(IrType.Unit, null, mutable: true);
        int collections = main.Local(IrType.I64, "collections", mutable: true);
        int some = main.Local(IrType.Bool, null, mutable: true);
        int v = main.Local(IrType.I32, "v", mutable: true);
        int b1 = main.NewBlock(), b2 = main.NewBlock(), b3 = main.NewBlock(), yes = main.NewBlock(), no = main.NewBlock();
        main.Call(0, [Int(7)], n, b1);
        main.At(b1).Assign(unit, new IntrinsicRvalue("gc.request", [])).Call(1, [Int(4000)], unit, b2);
        main.At(b2).Assign(unit, new IntrinsicRvalue("gc.request", [])).Call(1, [Int(4000)], unit, b3);
        main.At(b3).Assign(collections, new IntrinsicRvalue("gc.collections", []))
            .Assign(some, Rvalue.Binary(BinaryOp.Gt, Copy(collections), Operand.Const(0, IrType.I64)))
            .Assign(v, Copy(new Place(n).Deref().Field(0))).If(Copy(some), yes, no);
        main.At(yes).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(v), Int(100))).Return();
        main.At(no).Assign(FunctionBuilder.ReturnPlace, Copy(v)).Return();
        return new IrModule([CellDef(Linkage.Import, "gclib::Cell")], [make.Build(), churn.Build(), main.Build()]) { Name = "rcapp" };
    }

    /// <summary>
    /// module mfree2 (Manual): the counted cell goes through a local and a struct into raw memory in main itself; freeing
    /// the memory must run the finalizer at once (the reads transfer the reference, as in the VM) → 1.
    /// </summary>
    public static IrModule ManualFreeInline()
    {
        var make = new FunctionBuilder("rc_new_fin", IrType.Gc(Cell)).ImportFrom("rcfin::rc_new_fin");
        make.Argument(IrType.I32, "x");
        var freed = new FunctionBuilder("rc_freed", IrType.I32).ImportFrom("rcfin::rc_freed");
        var holder = IrType.Struct(1);
        var main = new FunctionBuilder("main", IrType.I32) { Unsafe = true };
        int c = main.Local(IrType.Gc(Cell), "c", mutable: true);
        int value = main.Local(holder, "value", mutable: true);
        int h = main.Local(IrType.PtrMut(holder), "h", mutable: true);
        int before = main.Local(IrType.I32, "before", mutable: true), after = main.Local(IrType.I32, "after", mutable: true);
        int b1 = main.NewBlock(), b2 = main.NewBlock(), b3 = main.NewBlock();
        main.Call(0, [Int(1)], c, b1);
        main.At(b1).Assign(new Place(value).Field(0), Move(c)).Assign(h, new AllocRvalue(AllocKind.Raw, Move(value))).Call(1, [], before, b2);
        main.At(b2).Free(new Place(h)).Call(1, [], after, b3);
        main.At(b3).Assign(before, Rvalue.Binary(BinaryOp.Mul, Copy(before), Int(10)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(before), Copy(after))).Return();
        var holderDef = new StructDef("Holder", IrType.Gc(Cell)) { FieldNames = ["c"] };
        return new IrModule([CellDef(Linkage.Import, "rcfin::Cell"), holderDef], [make.Build(), freed.Build(), main.Build()]) { Name = "mfree2" };
    }
}
