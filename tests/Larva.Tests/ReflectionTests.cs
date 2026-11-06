using System.Numerics;
using Larva.IL;
using Larva.IR;
using Larva.IR.Reflection;
using Larva.Runtime;

namespace Larva.Tests;

/// <summary>Phase 3: reflection metadata, invoke stubs, run-time generics and dynamic borrows.</summary>
public sealed class ReflectionTests : CheckedTest
{
    /// <summary>The program compiled once (no reflection by default: only what is marked), its metadata encoded.</summary>
    private static readonly Lazy<(CompilationResult Result, byte[] Metadata)> Compiled = new(() =>
    {
        var result = Compiler.Compile(ReflectionPrograms.ProgramModule(), new CompilationOptions { Reflection = ReflectionLevel.None });
        return (result, result.Success ? MetadataEncoding.Encode(result.Il!.Metadata) : []);
    });

    private Larva.IR.Lowering.IlModule Il()
    {
        var result = Compiled.Value.Result;
        Check("compiles", result.Success && result.Diagnostics.Count == 0,
            string.Join(" | ", result.TypeErrors.Select(e => e.ToString()).Concat(result.Diagnostics.Select(d => d.ToString()))));
        Require();
        return result.Il!;
    }

    private RuntimeModule Runtime()
    {
        Il();
        return RuntimeModule.Load(Compiled.Value.Metadata);
    }

    [Fact]
    public void Metadata_round_trips_through_its_binary_encoding()
    {
        var metadata = Il().Metadata;
        var bytes = Compiled.Value.Metadata;
        Check("metadata binary round trip", MetadataEncoding.Decode(bytes).ToString() == metadata.ToString(), $"{bytes.Length} bytes");
    }

    // ── Compiler side ──────────────────────────────────────────────

    [Fact]
    public void Compiler_emits_methods_levels_and_invoke_stubs()
    {
        var il = Il();
        var metadata = il.Metadata;
        Check("only #[reflect(full)] functions are methods",
            metadata.Methods.Select(m => m.Name).SequenceEqual(["add", "identity", "max", "swap_pair", "total"]),
            string.Join(", ", metadata.Methods.Select(m => m.Name)));

        var levels = metadata.Structs.Take(5).Select(s => (s.Name, s.Level)).ToArray();
        Check("levels: explicit, trimmed, gc forced to layout",
            levels is [("Point", ReflectionLevel.Full), ("", ReflectionLevel.None), ("Holder", ReflectionLevel.Layout), ("Pair", ReflectionLevel.Full), ("Wide", ReflectionLevel.Layout)],
            string.Join(", ", levels.Select(l => $"{(l.Name.Length == 0 ? "<trimmed>" : l.Name)}={l.Level}")));

        MethodMetadata M(string name) => metadata.Methods.Single(m => m.Name == name);
        Check("add: direct, shape (id)d", M("add") is { Shared: false, Function: >= 0, Shape: "(id)d" });
        Check("identity: shared, shape w(x)x", M("identity") is { Shared: true, Function: >= 0, Shape: "w(x)x" });
        Check("swap_pair: shared with two witnesses", M("swap_pair") is { Shared: true, Shape: "ww(x)x" });
        Check("max: specialized, instance <i32> only", M("max") is { Function: -1, Instances: [{ Shape: "(ii)i" }] }
            && M("max").Instances[0].Arguments.SequenceEqual([IrType.I32]));
        Check("total: wide argument and result by pointer", M("total").Shape == "(x)x");

        var stubs = il.Functions.Where(f => f.Name.StartsWith("$invoke")).ToArray();
        Check("one stub per shape", stubs.Select(s => s.Name).OrderBy(n => n).SequenceEqual(
            new[] { "$invoke(id)d", "$invoke(ii)i", "$invoke(x)x", "$invoke w(x)x", "$invoke ww(x)x" }.Select(n => n.Replace(" ", "")).OrderBy(n => n)),
            string.Join(", ", stubs.Select(s => s.Name)));
        Check("stubs call indirectly", stubs.All(s => s.Body.Operations.Any(o => o.Mnemonic == "callx.ind")));
        Check("metadata stub indices point at stubs", metadata.Methods.Where(m => m.Stub >= 0).All(m => il.Functions[m.Stub].Name == $"$invoke{m.Shape}"));
        Check("stub IL round trips", stubs.All(s => OperationBlock.Decode(s.Body.BlockId, s.Body.Encode()).Operations.SequenceEqual(s.Body.Operations)));
    }

    // ── Runtime: types, fields, borrows ────────────────────────────

    [Fact]
    public void Types_and_fields_are_read_and_written_with_checks()
    {
        var runtime = Runtime();
        var point = runtime.GetType("Point");
        Check("Point fields", point.Fields.Select(f => (f.Name, f.Offset)).SequenceEqual([("x", 0), ("y", 4)]), string.Join(", ", point.Fields));
        Check("Secret is trimmed", Throws<ReflectionException>(() => runtime.GetType("Secret")));
        var holder = runtime.GetType("Holder");
        Check("Holder keeps its layout for the GC", holder.Layout is { Size: 16, GcOffsets: [0] } && holder.GetField("n").Offset == 8,
            holder.Layout?.ToString());

        var p = runtime.New(point);
        using (var w = p.BorrowMut())
        {
            w["x"] = 3L;
            w["y"] = 4;
            Check("integer range is checked", Throws<ReflectionException>(() => w["x"] = 1L << 40));
            Check("type is checked", Throws<ReflectionException>(() => w["x"] = 1.5));
        }
        using (var r = p.Borrow())
            Check("read back", r["x"] is 3L && r["y"] is 4L);

        var wide = runtime.New(runtime.GetType("Wide"));
        using (var w = wide.BorrowMut())
        {
            w["big"] = BigInteger.Pow(2, 100) + 7;
            w["f"] = 0.25;
            Check("i128 and f64 fields", w["big"] is BigInteger big && big == BigInteger.Pow(2, 100) + 7 && w["f"] is 0.25);
        }
    }

    [Fact]
    public void Dynamic_borrows_conflict_like_static_ones()
    {
        var runtime = Runtime();
        var p = runtime.New(runtime.GetType("Point"));
        var a = p.Borrow();
        var b = p.Borrow();
        Check("two shared borrows coexist", a["x"] is 0L && b["y"] is 0L);
        Check("&mut while shared: conflict", Throws<BorrowConflictException>(() => p.BorrowMut()));
        Check("write through shared: rejected", Throws<BorrowConflictException>(() => a["x"] = 1));
        a.Dispose();
        b.Dispose();
        Check("released borrow is dead", Throws<BorrowConflictException>(() => _ = a["x"]));

        var m = p.BorrowMut();
        Check("shared while &mut: conflict", Throws<BorrowConflictException>(() => p.Borrow()));
        Check("second &mut: conflict", Throws<BorrowConflictException>(() => p.BorrowMut()));
        m["x"] = 9;
        m.Dispose();
        using var again = p.Borrow();
        Check("after release, borrows work again", again["x"] is 9L);
    }

    [Fact]
    public void Generic_types_are_made_at_run_time()
    {
        var runtime = Runtime();
        var pair = runtime.GetType("Pair");
        var i32 = runtime.TypeOf(IrType.I32);
        var f64 = runtime.TypeOf(IrType.F64);
        var pairIF = pair.MakeGeneric(i32, f64);
        Check("Pair<i32, f64> layout", pairIF.Layout is { Size: 16, Align: 8 } && pairIF.Fields.Select(f => f.Offset).SequenceEqual([0, 8]),
            $"{pairIF.Name}: {pairIF.Layout}");
        Check("same instantiation is cached", ReferenceEquals(pairIF, pair.MakeGeneric(i32, f64)));
        Check("B: copy is enforced", Throws<UnsatisfiedConstraintException>(() => pair.MakeGeneric(i32, runtime.TypeOf(IrType.Box(IrType.I32)))));

        // A field of struct type is a view sharing the allocation (and its borrow state).
        var pairPoint = pair.MakeGeneric(runtime.GetType("Point"), i32);
        var obj = runtime.New(pairPoint);
        using (var w = obj.BorrowMut())
        {
            var first = (RuntimeObject)w["first"];
            Check("view shares the borrow state", Throws<BorrowConflictException>(() => first.BorrowMut()));
            w["second"] = 7;
        }
        var view = (RuntimeObject)obj.Borrow().Target.Type.GetField("first").GetValue(obj.Borrow());
        Check("view is still shared-borrowed", Throws<BorrowConflictException>(() => view.BorrowMut()));
    }

    // ── Runtime: methods ───────────────────────────────────────────

    [Fact]
    public void Methods_prepare_invokes_with_checked_arguments()
    {
        var il = Il();
        var runtime = Runtime();
        Check("hidden is not invocable", Throws<ReflectionException>(() => runtime.GetMethod("hidden")));

        var add = runtime.GetMethod("add");
        var plan = add.PrepareInvoke(2, 1.5);
        Check("add slots", plan.Slots.SequenceEqual([2UL, BitConverter.DoubleToUInt64Bits(1.5)]) && il.Functions[plan.Stub].Name == "$invoke(id)d");
        Check("add: wrong type", Throws<ReflectionException>(() => add.PrepareInvoke("2", 1.5)));
        Check("add: wrong count", Throws<ReflectionException>(() => add.PrepareInvoke(2)));
        Check("negative i32 is sign-extended", add.PrepareInvoke(-1, 0.0).Slots[0] == ulong.MaxValue);

        var point = runtime.GetType("Point");
        var identity = runtime.GetMethod("identity");
        Check("generic definition cannot be invoked", Throws<ReflectionException>(() => identity.PrepareInvoke(1)));
        var idPoint = identity.MakeGeneric(point);
        var p = runtime.New(point);
        using (var w = p.BorrowMut())
            w["x"] = 5;
        var idPlan = idPoint.PrepareInvoke(p);
        Check("shared call: witness slot, value by pointer, result buffer",
            idPlan.Slots.Length == 2 && runtime.Witness((int)idPlan.Slots[0]).Size == 8 && idPlan.Buffers[0][0] == 5 && idPlan.ResultBuffer == 1);
        Check("any type works for shared code", identity.MakeGeneric(runtime.TypeOf(IrType.I64)).Function == identity.Function);
        Check("argument type is checked", Throws<ReflectionException>(() => identity.MakeGeneric(runtime.TypeOf(IrType.I64)).PrepareInvoke(p)));

        var max = runtime.GetMethod("max");
        var maxI32 = max.MakeGeneric(runtime.TypeOf(IrType.I32));
        Check("max<i32> resolves to its compiled instance", il.Functions[maxI32.Function].Name == "max<i32>" && maxI32.PrepareInvoke(3, 5).Slots.SequenceEqual([3UL, 5UL]));
        Check("max<i64> was never compiled", Throws<AotInstantiationException>(() => max.MakeGeneric(runtime.TypeOf(IrType.I64))));
        Check("max<bool> violates T: numeric", Throws<UnsatisfiedConstraintException>(() => max.MakeGeneric(runtime.TypeOf(IrType.Bool))));

        var pairType = runtime.GetType("Pair").MakeGeneric(runtime.TypeOf(IrType.I32), runtime.TypeOf(IrType.F64));
        var swap = runtime.GetMethod("swap_pair").MakeGeneric(runtime.TypeOf(IrType.I32), runtime.TypeOf(IrType.F64));
        var pair = runtime.New(pairType);
        var swapPlan = swap.PrepareInvoke(pair);
        Check("swap_pair<i32, f64>: two witnesses, 16-byte argument and result", swapPlan.Slots.Length == 3 && swapPlan.Buffers.All(b => b.Length == 16), swapPlan.ToString());
        Check("passing the wrong struct is rejected", Throws<ReflectionException>(() => swap.PrepareInvoke(p)));

        var wide = runtime.New(runtime.GetType("Wide"));
        var totalPlan = runtime.GetMethod("total").PrepareInvoke(wide);
        Check("total(Wide) -> i128 by pointers", totalPlan.Buffers.Select(b => b.Length).SequenceEqual([32, 16]), totalPlan.ToString());
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }
}
