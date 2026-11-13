using Larva.IL;
using Larva.IR;
using Larva.IR.Encoding;
using Larva.IR.Lowering;
using static Larva.Tests.SharedGenericsPrograms;

namespace Larva.Tests;

/// <summary>Phase 2: generic code compiled once and driven by witness tables.</summary>
public sealed class SharedGenericsTests : CheckedTest
{
    [Fact]
    public void Program_round_trips_and_knows_what_can_be_shared()
    {
        var module = ProgramModule();
        var bytes = IrEncoder.Encode(module);
        Check("IR binary round trip", IrDecoder.Decode(bytes).ToString() == module.ToString(), $"{bytes.Length} bytes");

        var shareable = Monomorphizer.Shareable(module);
        Check("shareability",
            shareable.SequenceEqual([true, true, true, true, true, true, false, false, false]),
            string.Join(", ", module.Functions.Select((f, i) => $"{f.Name}={(shareable[i] ? "shared" : "specialize")}")));
    }

    [Fact]
    public void Shared_mode_passes_and_reads_witnesses()
    {
        var module = ProgramModule();
        var result = Compiler.Compile(module);
        Check("compiles in Shared mode", result.Success && result.Diagnostics.Count == 0);
        Require();

        var il = result.Il!;
        IlFunction Fn(string name) => il.Functions.Single(f => f.Name == name);
        bool Uses(string name, string mnemonic) => Fn(name).Body.Operations.Any(o => o.Mnemonic == mnemonic);

        Check("shared functions take witnesses", Fn("identity").TypeParameterCount == 1 && Fn("pair_size").TypeParameterCount == 1);
        Check("specialized functions take none", Fn("main").TypeParameterCount == 0 && Fn("add_then_size<f32>").TypeParameterCount == 0);
        Check("identity compiled once", il.Functions.Count(f => f.Name.StartsWith("identity")) == 1);
        Check("main passes static witnesses", Uses("main", "gen.wit"));
        Check("pass_through forwards its own witness", Uses("pass_through", "gen.ldtarg"));
        Check("pair_size builds an open witness", Uses("pair_size", "gen.wit")
            && il.Types[(int)Fn("pair_size").Body.Operations.First(o => o.Mnemonic == "gen.wit").Operands[0]].IsGeneric());
        Check("size_of reads the witness", Uses("size_of", "gen.sizeof"));
        Check("consume drops through the witness", Uses("consume", "gen.drop"));
        Check("specialized callee of shared code is still a plain call", Uses("add_then_size<f32>", "fpu.add") && Uses("add_then_size<f32>", "gen.wit"));

        var pointWitness = il.Witnesses.Single(w => il.Types[w.Type] == IrType.Struct(GenericsPrograms.PointStruct));
        Check("Point witness", pointWitness.Layout is { Size: 8, Align: 4, Flags: WitnessFlags.Copy | WitnessFlags.Unmanaged | WitnessFlags.FixedSize, GcOffsets.Length: 0 },
            pointWitness.Layout.ToString());

        foreach (var function in il.Functions)
        {
            var decoded = OperationBlock.Decode(function.Body.BlockId, function.Body.Encode());
            if (!decoded.Operations.SequenceEqual(function.Body.Operations))
                Check($"IL round trip {function.Name}", false);
        }
        Check("IL round trips", true);
    }

    [Fact]
    public void Specialize_mode_has_no_witnesses_and_layouts_place_gc_handles()
    {
        var module = ProgramModule();
        // Same program, fully specialized: no witnesses at all, sizes become constants.
        var specialized = Compiler.Compile(module, new CompilationOptions { Generics = GenericsMode.Specialize });
        Check("Specialize mode has no shared code",
            specialized.Success && specialized.Il!.Functions.All(f => f.TypeParameterCount == 0)
            && specialized.Il.Functions.All(f => f.Body.Operations.All(o => o.OpCode != OpCode.Generic)));
        var sizeOfPoint = specialized.Il!.Functions.Single(f => f.Name == "size_of<Point>").Body.Operations;
        Check("size_of<Point> is a constant", sizeOfPoint.Any(o => o.OpCode == OpCode.Const && o.Operands[0] == 8));

        // Layout with a gc handle: u8 @0, gc @8, i16 @16 → 24 bytes, pointer map [8].
        var mixed = Layouts.Of(IrType.Struct(MixedStruct), module)!;
        Check("Mixed layout", mixed is { Size: 24, Align: 8, GcOffsets: [8] } && mixed.Flags.HasFlag(WitnessFlags.Managed), mixed.ToString());
    }

    [Fact]
    public void Layouts_place_handles_through_inline_arrays_and_measure_deep_nesting_once()
    {
        // Pair { u8, gc i32 }: 16 bytes, handle @8. Holder { i16, [Pair; 3], box i32, &i32 }: the array @8 (handles @16, 32,
        // 48), the box @56, the reference @64: 72 bytes.
        StructDef[] structs =
        [
            new("Pair", IrType.U8, IrType.Gc(IrType.I32)),
            new("Holder", IrType.I16, new FixedArrayType(IrType.Struct(0), 3), IrType.Box(IrType.I32), IrType.Ref(IrType.I32)),
        ];
        var module = new IrModule(structs, []);
        var holder = Layouts.Of(IrType.Struct(1), module)!;
        Check("Holder layout", holder is { Size: 72, Align: 8, GcOffsets: [16, 32, 48] }, holder.ToString());
        Check("Holder field offsets", Layouts.FieldOffsets((StructType)IrType.Struct(1), module) is [0, 8, 56, 64]);
        var map = Layouts.PointerMap(IrType.Struct(1), module);
        Check("Holder pointer map", map is { Handles: [16, 32, 48], Cells: [56], Ambiguous: true },
            map is { } m ? $"[{string.Join(", ", m.Handles)}] [{string.Join(", ", m.Cells)}] {m.Ambiguous}" : "null");
        Check("asked again: the same layout", Layouts.Of(IrType.Struct(1), module) is { Size: 72, GcOffsets: [16, 32, 48] });

        // S0 { i32, i64 }, Sk { S(k-1), i64 }: 40 levels (measuring each field twice took 2^40 steps), and a 64 MB inline array.
        List<StructDef> nested = [new("S0", IrType.I32, IrType.I64)];
        for (int k = 1; k <= 40; k++)
            nested.Add(new StructDef($"S{k}", IrType.Struct(k - 1), IrType.I64));
        var deep = new IrModule([.. nested], []);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var s40 = Layouts.Of(IrType.Struct(40), deep);
        var bytes = Layouts.Of(new FixedArrayType(IrType.U8, 1 << 26), deep);
        var handles = Layouts.PointerMap(new FixedArrayType(IrType.Struct(40), 1 << 20), deep);
        Check("S40 is 16 + 8 * 40 bytes", s40 is { Size: 336, Align: 8, GcOffsets: [] }, s40?.ToString());
        Check("[u8; 64 MB]", bytes is { Size: 1 << 26, Align: 1, GcOffsets: [] }, bytes?.ToString());
        Check("[S40; 1 M] holds no handles", handles is { Handles: [], Cells: [], Ambiguous: false });
        Check("measured at once", watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }
}
