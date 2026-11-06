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
}
