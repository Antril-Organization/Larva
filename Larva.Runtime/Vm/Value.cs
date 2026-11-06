using System.Numerics;
using Larva.IR;
using Larva.IR.Text;

namespace Larva.Runtime.Vm
{
    public enum ValueKind : byte
    {
        Unit,
        Bool,
        /// <summary>Any integer; <see cref="Value.Type"/> is <c>null</c> for an untyped <c>ldc</c> literal.</summary>
        Int,
        /// <summary>Any float: a double for f16/f32/f64; f128/f256 keep their bits (in <see cref="Value.Int"/>) and the nearest double.</summary>
        Float,
        /// <summary>An address with its pointer type (<c>&amp;T</c>, <c>gc T</c>, <c>box T</c>, <c>*T</c>).</summary>
        Pointer,
        /// <summary>A struct (or any by-value aggregate): its bytes.</summary>
        Struct,
        /// <summary>The value stored at an address whose type is not known yet (a raw slot read by an invoke stub).</summary>
        Deferred,
        /// <summary>A type argument passed to shared generic code.</summary>
        Witness,
        /// <summary>A function reference (<c>callx.ldfn</c>).</summary>
        Function,
    }

    /// <summary>
    /// A value on the interpreter's operand stack. The IL is untyped, so every value carries the type of the slot, field or
    /// pointer it came from; untyped literals get their type when they are stored or converted.
    /// </summary>
    public readonly record struct Value(ValueKind Kind, IrType? Type, BigInteger Int = default, double Float = 0, Address Address = default,
        byte[]? Bytes = null, IrType? Witness = null, int Function = 0)
    {
        public static readonly Value UnitValue = new(ValueKind.Unit, IrType.Unit);

        public static Value FromBool(bool value) => new(ValueKind.Bool, IrType.Bool, value ? 1 : 0);
        public static Value FromInt(BigInteger value, IrType? type) => new(ValueKind.Int, type, type is null ? value : Numbers.Wrap(value, type));
        public static Value FromFloat(double value, IrType type) => Numbers.IsWide(type)
            ? FromFloatBits(SoftFloat.FromDouble(value, Numbers.FormatOf(type)), type)
            : new(ValueKind.Float, type, Float: Numbers.Round(value, type));

        /// <summary>A float from its IEEE bit pattern (exact for every width).</summary>
        public static Value FromFloatBits(BigInteger bits, IrType type)
        {
            if (!Numbers.IsWide(type))
                return FromFloat(Numbers.FromBits(bits, type), type);
            var format = Numbers.FormatOf(type);
            bits &= format.Mask;
            return new(ValueKind.Float, type, bits, SoftFloat.ToDouble(bits, format));
        }

        /// <summary>A float's IEEE bit pattern.</summary>
        public BigInteger FloatBits => Numbers.IsWide(Type) ? Int : Numbers.ToBits(Float, Type!);

        /// <summary>This float's bits as a value of (float) type <paramref name="type"/>: converted exactly when the width differs.</summary>
        public BigInteger FloatBitsAs(IrType type) => Type == type ? FloatBits : SoftFloat.Convert(FloatBits, Numbers.FormatOf(Type!), Numbers.FormatOf(type));
        public static Value FromPointer(Address address, IrType? type) => new(ValueKind.Pointer, type, Address: address);
        public static Value FromStruct(byte[] bytes, IrType type) => new(ValueKind.Struct, type, Bytes: bytes);
        public static Value Deferred(Address address) => new(ValueKind.Deferred, null, Address: address);
        public static Value FromWitness(IrType type) => new(ValueKind.Witness, null, Witness: type);
        public static Value FromFunction(int index) => new(ValueKind.Function, IrType.U64, Function: index);

        public bool IsTrue => Kind switch
        {
            ValueKind.Bool or ValueKind.Int => !Int.IsZero,
            ValueKind.Float => Float != 0,
            ValueKind.Pointer => !Address.IsNull,
            _ => throw new VmException($"{this} is not a condition"),
        };

        public override string ToString() => Kind switch
        {
            ValueKind.Unit => "()",
            ValueKind.Bool => Int.IsZero ? "false" : "true",
            ValueKind.Int => $"{Int}{(Type is null ? "" : $"_{Type}")}",
            ValueKind.Float => $"{Float.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{Type}",
            ValueKind.Pointer => $"{Address} as {(Type is null ? "?" : IrPrinter.FormatType(Type))}",
            ValueKind.Struct => $"{Type} [{Convert.ToHexString(Bytes!)}]",
            ValueKind.Deferred => $"*{Address}",
            ValueKind.Witness => $"witness {Witness}",
            _ => $"fn.{Function}",
        };
    }

    /// <summary>
    /// An address in the interpreter's memory: allocation id + byte offset, packed into 64 bits as
    /// <c>id &lt;&lt; 32 | offset</c>. Id 0 is null; function references stored in memory set bit 63.
    /// </summary>
    public readonly record struct Address(int Allocation, int Offset)
    {
        public const ulong FunctionTag = 1UL << 63;

        public bool IsNull => Allocation == 0;
        public ulong Packed => (ulong)(uint)Allocation << 32 | (uint)Offset;
        public static Address Unpack(ulong packed) => new((int)(packed >> 32), (int)(packed & 0xFFFF_FFFF));
        public Address Plus(long bytes) => this with { Offset = checked((int)(Offset + bytes)) };
        public override string ToString() => IsNull ? "null" : $"@{Allocation}+{Offset}";
    }

    /// <summary>A program error detected while interpreting (use after free, bad operand, unsupported operation…).</summary>
    public class VmException(string message) : Exception(message);

    /// <summary>A Larva exception in flight (how the interpreter unwinds frames); not visible to hosts.</summary>
    internal sealed class LarvaException(Value value) : Exception($"Larva exception {value}")
    {
        public Value Value { get; } = value;
    }

    /// <summary>A Larva exception that no frame caught.</summary>
    public sealed class UnhandledException(Value value) : VmException($"unhandled exception: {value}")
    {
        public Value Value { get; } = value;
    }

    /// <summary><c>sys.trap</c> (e.g. an <c>unreachable</c> was reached).</summary>
    public sealed class VmTrapException(long code) : VmException($"trap {code}")
    {
        public long Code { get; } = code;
    }

    /// <summary>Integer wrapping, float rounding and IEEE encodings for every primitive width.</summary>
    internal static class Numbers
    {
        public static BigInteger Wrap(BigInteger value, IrType type)
        {
            if (type is not PrimitiveType { IsInteger: true } p)
                return value;
            int bits = p.BitWidth!.Value;
            var modulus = BigInteger.One << bits;
            var wrapped = ((value % modulus) + modulus) % modulus;
            return p.IsSigned && wrapped >= modulus >> 1 ? wrapped - modulus : wrapped;
        }

        public static bool IsWide(IrType? type) => type is PrimitiveType { IsFloat: true, BitWidth: > 64 };

        public static FloatFormat FormatOf(IrType type) => FloatFormat.Of(type.BitWidth!.Value);

        public static double Round(double value, IrType type) => type switch
        {
            PrimitiveType { Kind: Primitive.F32 } => (float)value,
            PrimitiveType { Kind: Primitive.F16 } => (double)(Half)value,
            _ => value,
        };

        /// <summary>Exponent and fraction widths of an IEEE binary format.</summary>
        public static (int Exponent, int Fraction) Format(IrType type) => type switch
        {
            PrimitiveType { Kind: Primitive.F16 } => (5, 10),
            PrimitiveType { Kind: Primitive.F32 } => (8, 23),
            PrimitiveType { Kind: Primitive.F64 } => (11, 52),
            PrimitiveType { Kind: Primitive.F128 } => (15, 112),
            PrimitiveType { Kind: Primitive.F256 } => (19, 236),
            _ => throw new VmException($"`{type}` is not a float type"),
        };

        public static double FromBits(BigInteger bits, IrType type)
        {
            var (e, m) = Format(type);
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.F64 }: return BitConverter.UInt64BitsToDouble((ulong)(bits & ulong.MaxValue));
                case PrimitiveType { Kind: Primitive.F32 }: return BitConverter.UInt32BitsToSingle((uint)(bits & uint.MaxValue));
                case PrimitiveType { Kind: Primitive.F16 }: return (double)BitConverter.UInt16BitsToHalf((ushort)(bits & ushort.MaxValue));
            }

            _ = (e, m);
            return SoftFloat.ToDouble(bits, FloatFormat.Of(type.BitWidth!.Value));
        }

        public static BigInteger ToBits(double value, IrType type)
        {
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.F64 }: return BitConverter.DoubleToUInt64Bits(value);
                case PrimitiveType { Kind: Primitive.F32 }: return BitConverter.SingleToUInt32Bits((float)value);
                case PrimitiveType { Kind: Primitive.F16 }: return BitConverter.HalfToUInt16Bits((Half)value);
            }

            return SoftFloat.FromDouble(value, FloatFormat.Of(type.BitWidth!.Value));
        }
    }
}
