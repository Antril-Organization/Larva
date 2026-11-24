using Larva.IR;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>
/// Programs over the runtime's handles (Larva.Polyglot): objects only handles hold, through collections that move them.
/// </summary>
internal static class PolyglotPrograms
{
    private static readonly IrType Point = GcPrograms.PointType;
    private static readonly IrType GcPoint = IrType.Gc(Point);
    private static readonly IrType Raw = IrType.PtrMut(IrType.U8);

    /// <summary>
    /// <c>handles(n: i64) -> i64</c>: n Points (i, 3i), each held only by a handle kept in a box of u64; then 4n garbage
    /// Points (collections happen, and move the young ones); then each Point read back through its handle and the handle
    /// freed. The result is (Σ 3i × 1000 + handles held before the frees) × 10 + handles held after: for n = 200,
    /// 597002000.
    /// </summary>
    public static IrModule Handles()
    {
        var f = new FunctionBuilder("handles", IrType.I64);
        int n = f.Argument(IrType.I64, "n");
        int hs = f.Local(IrType.Box(new ArrayType(IrType.U64)), "hs", mutable: true);
        int i = f.Local(IrType.I64, "i", mutable: true);
        int p = f.Local(Point, "p", mutable: true);
        int g = f.Local(GcPoint, "g", mutable: true);
        int raw = f.Local(Raw, "raw", mutable: true);
        int s = f.Local(IrType.I64, "s", mutable: true);
        int before = f.Local(IrType.I64, "before", mutable: true);
        int after = f.Local(IrType.I64, "after", mutable: true);
        int c = f.Local(IrType.Bool, "c", mutable: true);
        int t = f.Local(IrType.I64, "t", mutable: true);
        int limit = f.Local(IrType.I64, "limit", mutable: true);
        int unit = f.Local(IrType.Unit, null, mutable: true);
        var slot = new Place(hs).Deref().Index(i);
        int fill = f.NewBlock(), store = f.NewBlock(), churnStart = f.NewBlock(), churn = f.NewBlock(), garbage = f.NewBlock(),
            readStart = f.NewBlock(), read = f.NewBlock(), take = f.NewBlock(), done = f.NewBlock();
        f.Assign(hs, Rvalue.NewArray(AllocKind.Box, IrType.U64, Copy(n))).Assign(i, Int(0, IrType.I64)).Goto(fill);
        f.At(fill).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), store, churnStart);
        f.At(store)
         .Assign(new Place(p).Field(0), Rvalue.Cast(Copy(i), IrType.I32))
         .Assign(t, Rvalue.Binary(BinaryOp.Mul, Copy(i), Int(3, IrType.I64)))
         .Assign(new Place(p).Field(1), Rvalue.Cast(Copy(t), IrType.I32))
         .Assign(g, Rvalue.Gc(Move(p)))
         .Assign(raw, Rvalue.Cast(Copy(g), Raw))
         .Assign(slot, new IntrinsicRvalue("handle.new", [Copy(raw)]))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(fill);
        // Garbage: 4n Points nobody keeps, and two requested collections.
        f.At(churnStart).Assign(unit, new IntrinsicRvalue("gc.request", []))
         .Assign(limit, Rvalue.Binary(BinaryOp.Mul, Copy(n), Int(4, IrType.I64))).Assign(i, Int(0, IrType.I64)).Goto(churn);
        f.At(churn).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(limit))).If(Copy(c), garbage, readStart);
        f.At(garbage)
         .Assign(new Place(p).Field(0), Int(-1)).Assign(new Place(p).Field(1), Int(-1))
         .Assign(g, Rvalue.Gc(Move(p)))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(churn);
        f.At(readStart).Assign(unit, new IntrinsicRvalue("gc.request", []))
         .Assign(before, new IntrinsicRvalue("handle.count", []))
         .Assign(i, Int(0, IrType.I64)).Assign(s, Int(0, IrType.I64)).Goto(read);
        f.At(read).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), take, done);
        f.At(take)
         .Assign(raw, new IntrinsicRvalue("handle.get", [Copy(slot)]))
         .Assign(g, Rvalue.Cast(Copy(raw), GcPoint))
         .Assign(t, Rvalue.Cast(Copy(new Place(g).Deref().Field(1)), IrType.I64))
         .Assign(s, Rvalue.Binary(BinaryOp.Add, Copy(s), Copy(t)))
         .Assign(unit, new IntrinsicRvalue("handle.free", [Copy(slot)]))
         .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(read);
        f.At(done)
         .Assign(after, new IntrinsicRvalue("handle.count", []))
         .Assign(s, Rvalue.Binary(BinaryOp.Mul, Copy(s), Int(1000, IrType.I64)))
         .Assign(s, Rvalue.Binary(BinaryOp.Add, Copy(s), Copy(before)))
         .Assign(s, Rvalue.Binary(BinaryOp.Mul, Copy(s), Int(10, IrType.I64)))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(s), Copy(after))).Return();
        return new IrModule([GcPrograms.StructDefs[GcPrograms.PointId]], [f.Build(), Stale()]) { Name = "handles" };
    }

    /// <summary>
    /// A library for C that other languages' threads call, as Larva.Polyglot's shims will be: each export enters, calls
    /// the function that does the work, and leaves; the export itself allocates nothing, so no safepoint runs before it
    /// enters. <c>pg_make(v)</c> gives a handle to a Point (v, 3v), <c>pg_read(h)</c> its second field, <c>pg_churn(n)</c>
    /// asks for a collection and makes n garbage Points, <c>pg_free(h)</c>, <c>pg_count()</c> and <c>pg_detach()</c>.
    /// </summary>
    public static IrModule ThreadLibrary()
    {
        var makeInner = new FunctionBuilder("make_inner", IrType.U64);
        {
            int v = makeInner.Argument(IrType.I64, "v");
            int p = makeInner.Local(Point, "p", mutable: true);
            int g = makeInner.Local(GcPoint, "g", mutable: true);
            int raw = makeInner.Local(Raw, "raw", mutable: true);
            int t = makeInner.Local(IrType.I64, "t", mutable: true);
            makeInner.Assign(new Place(p).Field(0), Rvalue.Cast(Copy(v), IrType.I32))
                .Assign(t, Rvalue.Binary(BinaryOp.Mul, Copy(v), Int(3, IrType.I64)))
                .Assign(new Place(p).Field(1), Rvalue.Cast(Copy(t), IrType.I32))
                .Assign(g, Rvalue.Gc(Move(p)))
                .Assign(raw, Rvalue.Cast(Copy(g), Raw))
                .Assign(FunctionBuilder.ReturnPlace, new IntrinsicRvalue("handle.new", [Copy(raw)])).Return();
        }
        var readInner = new FunctionBuilder("read_inner", IrType.I64);
        {
            int h = readInner.Argument(IrType.U64, "h");
            int raw = readInner.Local(Raw, "raw", mutable: true);
            int g = readInner.Local(GcPoint, "g", mutable: true);
            readInner.Assign(raw, new IntrinsicRvalue("handle.get", [Copy(h)]))
                .Assign(g, Rvalue.Cast(Copy(raw), GcPoint))
                .Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(new Place(g).Deref().Field(1)), IrType.I64)).Return();
        }
        var churnInner = new FunctionBuilder("churn_inner", IrType.Unit);
        {
            int n = churnInner.Argument(IrType.I64, "n");
            int i = churnInner.Local(IrType.I64, "i", mutable: true);
            int p = churnInner.Local(Point, "p", mutable: true);
            int g = churnInner.Local(GcPoint, "g", mutable: true);
            int c = churnInner.Local(IrType.Bool, "c", mutable: true);
            int unit = churnInner.Local(IrType.Unit, null, mutable: true);
            int head = churnInner.NewBlock(), body = churnInner.NewBlock(), exit = churnInner.NewBlock();
            // A collection at the loop's first safepoint: one per call, while the other threads are in C or in Larva code.
            churnInner.Assign(unit, new IntrinsicRvalue("gc.request", [])).Assign(i, Int(0, IrType.I64)).Goto(head);
            churnInner.At(head).Assign(c, Rvalue.Binary(BinaryOp.Lt, Copy(i), Copy(n))).If(Copy(c), body, exit);
            churnInner.At(body).Assign(new Place(p).Field(0), Int(-1)).Assign(new Place(p).Field(1), Int(-1))
                .Assign(g, Rvalue.Gc(Move(p)))
                .Assign(i, Rvalue.Binary(BinaryOp.Add, Copy(i), Int(1, IrType.I64))).Goto(head);
            churnInner.At(exit).Return();
        }
        static IrFunction Export(IrFunction function, string name) =>
            function with { Attributes = [new IrAttribute("Larva.Export", [new TextArgument(name)])] };
        // An export around function `inner`: enter, call, leave.
        static IrFunction Shim(string name, int inner, IrType argument, IrType result)
        {
            var f = new FunctionBuilder(name, result);
            int x = f.Argument(argument, "x");
            int r = f.Local(result, "r", mutable: true);
            int unit = f.Local(IrType.Unit, null, mutable: true);
            int after = f.NewBlock();
            f.Assign(unit, new IntrinsicRvalue("runtime.enter", [])).Call(inner, [Copy(x)], r, after);
            f.At(after).Assign(unit, new IntrinsicRvalue("runtime.leave", []));
            if (!result.IsUnit)
                f.Assign(FunctionBuilder.ReturnPlace, Copy(r));
            f.Return();
            return Export(f.Build(), name);
        }
        var free = new FunctionBuilder("pg_free", IrType.Unit);
        {
            int h = free.Argument(IrType.U64, "h");
            int unit = free.Local(IrType.Unit, null, mutable: true);
            free.Assign(unit, new IntrinsicRvalue("runtime.enter", [])).Assign(unit, new IntrinsicRvalue("handle.free", [Copy(h)]))
                .Assign(unit, new IntrinsicRvalue("runtime.leave", [])).Return();
        }
        var count = new FunctionBuilder("pg_count", IrType.I64);
        count.Assign(FunctionBuilder.ReturnPlace, new IntrinsicRvalue("handle.count", [])).Return();
        var collections = new FunctionBuilder("pg_collections", IrType.I64);
        collections.Assign(FunctionBuilder.ReturnPlace, new IntrinsicRvalue("gc.collections", [])).Return();
        var detach = new FunctionBuilder("pg_detach", IrType.Unit);
        {
            int unit = detach.Local(IrType.Unit, null, mutable: true);
            detach.Assign(unit, new IntrinsicRvalue("thread.detach", [])).Return();
        }
        return new IrModule([GcPrograms.StructDefs[GcPrograms.PointId]],
        [
            makeInner.Build(), readInner.Build(), churnInner.Build(),
            Shim("pg_make", 0, IrType.I64, IrType.U64), Shim("pg_read", 1, IrType.U64, IrType.I64), Shim("pg_churn", 2, IrType.I64, IrType.Unit),
            Export(free.Build(), "pg_free"), Export(count.Build(), "pg_count"), Export(detach.Build(), "pg_detach"),
            Export(collections.Build(), "pg_collections"),
        ]) { Name = "pg" };
    }

    /// <summary>
    /// The C program that loads <see cref="ThreadLibrary"/>: the main thread and four others each make handles, churn
    /// (collections while the others are in C or in Larva code) and read every handle back; then they detach. It prints
    /// the wrong reads (0), the handles left (0) and how many collections ran.
    /// </summary>
    public const string ThreadHost = """
        #include <windows.h>
        #include <stdio.h>
        #include <stdint.h>
        __declspec(dllimport) uint64_t pg_make(int64_t v);
        __declspec(dllimport) int64_t pg_read(uint64_t h);
        __declspec(dllimport) void pg_churn(int64_t n);
        __declspec(dllimport) void pg_free(uint64_t h);
        __declspec(dllimport) int64_t pg_count(void);
        __declspec(dllimport) void pg_detach(void);
        __declspec(dllimport) int64_t pg_collections(void);

        #define ROUNDS 40
        #define HELD 16
        static volatile LONG wrong;

        static DWORD WINAPI work(void *argument) {
            int64_t base = (int64_t)(intptr_t)argument * 100000;
            for (int round = 0; round < ROUNDS; round++) {
                uint64_t held[HELD];
                for (int i = 0; i < HELD; i++)
                    held[i] = pg_make(base + round * HELD + i);
                pg_churn(200);
                for (int i = 0; i < HELD; i++) {
                    if (pg_read(held[i]) != 3 * (base + round * HELD + i))
                        InterlockedIncrement(&wrong);
                    pg_free(held[i]);
                }
                Sleep(0);
            }
            pg_detach();
            return 0;
        }

        int main(void) {
            HANDLE threads[4];
            for (int t = 0; t < 4; t++)
                threads[t] = CreateThread(NULL, 0, work, (void *)(intptr_t)(t + 1), 0, NULL);
            work((void *)(intptr_t)0);
            WaitForMultipleObjects(4, threads, TRUE, INFINITE);
            printf("%ld %lld %lld\n", (long)wrong, (long long)pg_count(), (long long)pg_collections());
            return 0;
        }
        """;

    /// <summary><c>stale() -> i64</c>: a handle freed, then read again: the runtime stops the program.</summary>
    private static IrFunction Stale()
    {
        var f = new FunctionBuilder("stale", IrType.I64);
        int p = f.Local(Point, "p", mutable: true);
        int g = f.Local(GcPoint, "g", mutable: true);
        int raw = f.Local(Raw, "raw", mutable: true);
        int h = f.Local(IrType.U64, "h", mutable: true);
        int unit = f.Local(IrType.Unit, null, mutable: true);
        f.Assign(new Place(p).Field(0), Int(1)).Assign(new Place(p).Field(1), Int(2))
         .Assign(g, Rvalue.Gc(Move(p)))
         .Assign(raw, Rvalue.Cast(Copy(g), Raw))
         .Assign(h, new IntrinsicRvalue("handle.new", [Copy(raw)]))
         .Assign(unit, new IntrinsicRvalue("handle.free", [Copy(h)]))
         .Assign(raw, new IntrinsicRvalue("handle.get", [Copy(h)]))
         .Assign(FunctionBuilder.ReturnPlace, Int(7, IrType.I64)).Return();
        return f.Build();
    }
}
