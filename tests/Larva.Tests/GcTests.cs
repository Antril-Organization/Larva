using Larva.IL;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Encoding;
using Larva.IR.Lowering;
using static Larva.IR.Operand;
using static Larva.Tests.GcPrograms;

namespace Larva.Tests;

/// <summary>Allocation (box / gc / raw), ownership release and per-GC-mode code generation.</summary>
public sealed class GcTests : CheckedTest
{
    [Fact]
    public void Managed_module_round_trips_through_the_binary_encoding()
    {
        var managed = ManagedModule();
        var bytes = IrEncoder.Encode(managed);
        Check("IR binary round trip", IrDecoder.Decode(bytes).ToString() == managed.ToString(), $"{bytes.Length} bytes");
    }

    // ── Default (tracing, moving, barriers) ──

    [Fact]
    public void Default_mode_frees_boxes_and_adds_barriers_pins_and_polls()
    {
        var tracing = Compile(ManagedModule(), GcConfig.Default);
        Check("box: alloc twice, freed on overwrite and at return", Count(tracing, "boxes", "gc.alloc") == 2 && Count(tracing, "boxes", "gc.free") == 2);
        Check("callee frees a box it received", Count(tracing, "give", "gc.free") == 1);
        Check("caller does not free a moved box", Count(tracing, "take", "gc.free") == 0);
        Check("raw alloc/free", Count(tracing, "raw", "gc.alloc") == 1 && Count(tracing, "raw", "gc.free") == 1);
        Check("gc value allocation", Count(tracing, "make", "gc.new") == 1);
        Check("write barrier on heap store of a handle", Count(tracing, "store", "gc.wb") == 1);
        Check("pin/unpin around interior borrow", Count(tracing, "peek", "gc.pin") == 1 && Count(tracing, "peek", "gc.unpin") == 1);
        Check("safepoint at loop header", Count(tracing, "spin", "sys.poll") == 1);
        Check("no refcounting ops", Total(tracing, "gc.retain") + Total(tracing, "gc.release") == 0);
        var peekRoots = Fn(tracing, "peek").GcRoots;
        Check("root map: handle argument and pin slot", peekRoots.Length == 2 && peekRoots.Any(r => r.IsArgument) && peekRoots.Any(r => !r.IsArgument),
            string.Join(", ", peekRoots));
    }

    // ── Reference counting ──

    [Fact]
    public void RefCount_mode_transfers_last_uses_and_needs_no_cooperation()
    {
        var rc = Compile(ManagedModule(), GcConfig.RefCount);
        // h and c are each read once, at their last use: the reference is handed over, no retain/release at all.
        Check("RC: last uses transfer the reference", Count(rc, "handles", "gc.retain") == 0 && Count(rc, "handles", "gc.release") == 0,
            Fn(rc, "handles").Body.ToString());
        // Storing h (its last use) transfers it into the field; only the field's old handle is released.
        Check("RC: overwriting a stored handle releases the old one",
            Count(rc, "store", "gc.retain") == 0 && Count(rc, "store", "gc.release") == 1 && Count(rc, "store", "gc.wb") == 0,
            Fn(rc, "store").Body.ToString());
        Check("RC: no pins, barriers or polls", Total(rc, "gc.pin") + Total(rc, "gc.wb") + Total(rc, "sys.poll") == 0);
        Check("RC: no root maps", rc.Functions.All(f => f.GcRoots.Length == 0));
    }

    // ── Arena ──

    [Fact]
    public void Arena_mode_allocates_without_cooperation_ops()
    {
        var managed = ManagedModule();
        var tracing = Compile(managed, GcConfig.Default);
        var arena = Compile(managed, GcConfig.Arena);
        Check("Arena: gc.new only, no cooperation ops",
            Total(arena, "gc.new") == 1 && Total(arena, "gc.retain") + Total(arena, "gc.release") + Total(arena, "gc.pin") + Total(arena, "gc.wb") + Total(arena, "sys.poll") == 0);
        Check("Arena: boxes still freed", Total(arena, "gc.free") == Total(tracing, "gc.free"));
    }

    // ── Plugin declaring its own capabilities ──

    [Fact]
    public void Plugin_mode_follows_its_declared_capabilities()
    {
        var plugin = Compile(ManagedModule(), GcConfig.Plugin("incremental", GcCapabilities.Tracing | GcCapabilities.Barriers));
        Check("Plugin (Tracing|Barriers): barriers and polls, no pins", Total(plugin, "gc.wb") == 1 && Total(plugin, "sys.poll") == 1 && Total(plugin, "gc.pin") == 0);
    }

    // ── Manual ──

    [Fact]
    public void Manual_mode_rejects_gc_handles_and_keeps_alloc_and_free()
    {
        var manualManaged = Compiler.Compile(ManagedModule(), new CompilationOptions { Gc = GcConfig.Manual });
        Check("Manual: gc handles rejected", !manualManaged.Success && manualManaged.TypeErrors.All(e => e.Kind == TypeErrorKind.GcNotAvailable));
        var manual = Compile(BoxOnlyModule(), GcConfig.Manual);
        Check("Manual: box/raw code compiles with alloc/free", Total(manual, "gc.alloc") == 4 && Total(manual, "gc.free") == 4);
    }

    [Fact]
    public void Il_of_each_mode_round_trips()
    {
        var managed = ManagedModule();
        var tracing = Compile(managed, GcConfig.Default);
        var rc = Compile(managed, GcConfig.RefCount);
        foreach (var function in tracing.Functions.Concat(rc.Functions))
        {
            if (!OperationBlock.Decode(function.Body.BlockId, function.Body.Encode()).Operations.SequenceEqual(function.Body.Operations))
                Check($"IL round trip {function.Name}", false);
        }
        Check("IL round trips", true);
    }

    // ── Rejections ─────────────────────────────────────────────────

    [Fact]
    public void Wrong_allocations_unsafe_free_and_moved_boxes_are_rejected()
    {
        { // allocation kind must match the destination type
            var f = new FunctionBuilder("wrong_alloc", IrType.Unit);
            int b = f.Local(IrType.Box(IrType.I32), "b");
            f.Assign(b, Rvalue.Gc(Int(1))).Return();
            var errors = TypeChecker.Check(new IrModule(StructDefs, [f.Build()]), GcConfig.Default);
            Check("gc value into a box local: TypeMismatch", errors.Count == 1 && errors[0].Kind == TypeErrorKind.TypeMismatch, errors.FirstOrDefault()?.Message);
        }
        { // free needs unsafe
            var f = new FunctionBuilder("safe_free", IrType.Unit);
            int p = f.Local(IrType.PtrMut(IrType.I32), "p");
            f.Assign(p, Rvalue.Alloc(Int(1))).Free(p).Return();
            var errors = TypeChecker.Check(new IrModule(StructDefs, [f.Build()]), GcConfig.Default);
            Check("free outside unsafe: UnsafeRequired", errors.Count == 1 && errors[0].Kind == TypeErrorKind.UnsafeRequired, errors.FirstOrDefault()?.Message);
        }
        { // a moved box cannot be used
            var f = new FunctionBuilder("use_moved_box", IrType.I32);
            int a = f.Local(IrType.Box(IrType.I32), "a");
            int b = f.Local(IrType.Box(IrType.I32), "b");
            f.Assign(a, Rvalue.Box(Int(1))).Assign(b, Move(a))
             .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(a).Deref()))
             .Return();
            var errors = BorrowChecker.Check(new IrModule(StructDefs, [f.Build()]));
            Check("use of moved box: UseOfMoved", errors.Count >= 1 && errors.All(e => e.Kind == BorrowErrorKind.UseOfMoved),
                string.Join(" | ", errors.Select(e => e.Message)));
        }
    }

    // ── Helpers ────────────────────────────────────────────────────

    private IlModule Compile(IrModule module, GcConfig gc)
    {
        var result = Compiler.Compile(module, new CompilationOptions { Gc = gc });
        Check($"compiles in {gc.Mode} mode", result.Success && result.Diagnostics.Count == 0,
            string.Join(" | ", result.TypeErrors.Select(e => e.ToString()).Concat(result.Diagnostics.Select(d => d.ToString()))));
        return result.Il ?? new IlModule([], [], [], [], []);
    }

    private static IlFunction Fn(IlModule il, string name) => il.Functions.Single(f => f.Name == name);

    private static int Count(IlModule il, string function, string mnemonic) =>
        il.Functions.Where(f => f.Name == function).Sum(f => f.Body.Operations.Count(o => o.Mnemonic == mnemonic));

    private static int Total(IlModule il, string mnemonic) =>
        il.Functions.Sum(f => f.Body.Operations.Count(o => o.Mnemonic == mnemonic));
}
