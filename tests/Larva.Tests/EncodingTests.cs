using System.Numerics;
using Larva.IR;
using Larva.IR.Encoding;
using Larva.IR.Lowering;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>The IR and module encodings over values and lengths that do not fit a 64-bit signed varint or a small count.</summary>
public sealed class EncodingTests : CheckedTest
{
    private static readonly BigInteger U128Max = (BigInteger.One << 128) - 1, U256Max = (BigInteger.One << 256) - 1;

    private static IrModule RoundTrip(IrModule module) => IrDecoder.Decode(IrEncoder.Encode(module));

    [Fact]
    public void Static_data_and_globals_of_2_to_the_63_and_more_survive_the_ir_encoding()
    {
        var main = new FunctionBuilder("main", IrType.I32);
        main.Assign(FunctionBuilder.ReturnPlace, Int(0, IrType.I32)).Return();
        var module = new IrModule([], [main.Build()])
        {
            Name = "wide",
            Data =
            [
                new StaticData(IrType.U64, [ulong.MaxValue, 1UL << 63, 7]),
                new StaticData(IrType.I64, [long.MinValue, long.MaxValue, -1]),
                new StaticData(IrType.U128, [U128Max, (BigInteger.One << 100) + 5]),
                new StaticData(IrType.U256, [U256Max]),
                new StaticData(IrType.I256, [-(BigInteger.One << 255)]),
            ],
            Globals =
            [
                new GlobalDef("all_ones", IrType.U64, ulong.MaxValue),
                new GlobalDef("lowest", IrType.I64, long.MinValue),
                new GlobalDef("wide", IrType.U128, U128Max),
                new GlobalDef("wider", IrType.U256, U256Max),
            ],
        };
        var read = RoundTrip(module);
        for (int d = 0; d < module.Data.Length; d++)
            Check($"static data {d} ({module.Data[d].Element}) reads back", read.Data[d].Values.SequenceEqual(module.Data[d].Values), string.Join(", ", read.Data[d].Values));
        for (int g = 0; g < module.Globals.Length; g++)
            Check($"global {module.Globals[g].Name} reads back", read.Globals[g].Initial == module.Globals[g].Initial, $"{read.Globals[g].Initial}");

        var il = Compiler.Compile(module).Il!;
        var reread = ModuleFile.Read(ModuleFile.Write(il));
        Check("the module file keeps the wide static data and globals",
            reread.Data.Select(x => x.Values).SelectMany(v => v).SequenceEqual(module.Data.SelectMany(x => x.Values))
            && reread.Globals.Select(x => x.Initial).SequenceEqual(module.Globals.Select(x => x.Initial)));
    }

    [Fact]
    public void Wide_constants_survive_the_ir_encoding()
    {
        var wide = new FunctionBuilder("wide", IrType.U256);
        int a = wide.Local(IrType.U64, "a"), b = wide.Local(IrType.U128, "b"), c = wide.Local(IrType.I64, "c");
        wide.Assign(a, Int(ulong.MaxValue, IrType.U64)).Assign(b, new ConstOperand(U128Max, IrType.U128)).Assign(c, Int(long.MinValue, IrType.I64))
            .Assign(FunctionBuilder.ReturnPlace, new ConstOperand(U256Max, IrType.U256)).Return();
        var module = new IrModule([], [wide.Build()]) { Name = "constants" };
        var read = RoundTrip(module);
        Check("u64, u128, i64 and u256 constants read back", read.ToString() == module.ToString(), read.ToString());
        var constants = read.Functions[0].Blocks[0].Statements.OfType<AssignStatement>().Select(s => ((UseRvalue)s.Value).Operand).OfType<ConstOperand>().Select(o => o.Bits).ToArray();
        Check("the bits are exact", constants.SequenceEqual([ulong.MaxValue, U128Max, long.MinValue, U256Max]), string.Join(", ", constants));
    }

    [Fact]
    public void Array_types_of_100000_elements_and_large_constraints_survive_the_ir_encoding()
    {
        var buffer = new StructDef("Buffer", new FixedArrayType(IrType.U8, 100_000)) { FieldNames = ["bytes"] };
        var holder = new StructDef("Holder", IrType.U8) { Generics = [new GenericParam("T", new SizeAtMostConstraint(100_000))] };
        var main = new FunctionBuilder("main", IrType.U64);
        int local = main.Local(new FixedArrayType(IrType.U8, 100_000), "big");
        main.Assign(FunctionBuilder.ReturnPlace, Rvalue.SizeOf(new FixedArrayType(IrType.U8, 100_000))).Return();
        var module = new IrModule([buffer, holder], [main.Build()]) { Name = "arrays", Globals = [new GlobalDef("g", IrType.U8) { GenericCount = 300 }] };
        var read = RoundTrip(module);
        Check("a [u8; 100000] field, local and sizeof read back", read.ToString() == module.ToString(), read.ToString());
        Check("the field's length is 100000", read.Structs[0].Fields[0] is FixedArrayType { Count: 100_000 });
        Check("a size constraint of 100000 bits reads back", read.Structs[1].Generics[0].Constraint is SizeAtMostConstraint { Bits: 100_000 });
        Check("a global with 300 type parameters reads back", read.Globals[0].GenericCount == 300, $"{read.Globals[0].GenericCount}");
        Check("the local's type is kept", read.Functions[0].Locals[local].Type is FixedArrayType { Count: 100_000 });
    }
}
