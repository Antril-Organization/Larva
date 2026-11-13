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
    /// <remarks>
    /// Compact (32 bytes): every scalar up to 64 bits lives in one <see cref="long"/> (an integer or bool, a float's double,
    /// an address, a function index), so the common operations never make a <see cref="BigInteger"/>; only integers that
    /// do not fit 64 bits (wide values of i128, u128, i256, u256, untyped literals beyond them) and the bits of f128/f256
    /// are held as one. Struct bytes and witnesses are the reference.
    /// </remarks>
    public readonly struct Value : IEquatable<Value>
    {
        /// <summary>How an integer is held: in <see cref="bits"/> as a long, as an unsigned long (2^63 and up), or a BigInteger in <see cref="reference"/>.</summary>
        private const byte Signed = 0, Unsigned = 1, Big = 2;

        private readonly long bits;
        private readonly object? reference;
        /// <summary>
        /// The kind (low byte), the integer form (next byte) and whether it is wrapped (bit 16: an integer made for its
        /// 64-bit-or-narrower type by <see cref="FromInt(long, IrType?)"/>, already in that type's range). One field, so the
        /// struct has four and the JIT keeps its fields in registers.
        /// </summary>
        private readonly int tag;

        private ValueKind kind => (ValueKind)(byte)tag;
        private byte form => (byte)(tag >> 8);
        private bool wrapped => (tag & WrappedBit) != 0;
        private const int WrappedBit = 1 << 16;

        private static int Tag(ValueKind kind, byte form, bool wrapped) => (byte)kind | form << 8 | (wrapped ? WrappedBit : 0);

        public ValueKind Kind => kind;
        public IrType? Type { get; }

        private Value(ValueKind kind, IrType? type, long bits, byte form = Signed, object? reference = null, bool wrapped = false)
        {
            tag = Tag(kind, form, wrapped);
            Type = type;
            this.bits = bits;
            this.reference = reference;
        }

        /// <summary>Whether this is an integer of primitive <paramref name="primitive"/> (64 bits or less) already wrapped to it: storing it there changes nothing.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool IsWrappedInteger(Primitive primitive) => wrapped && ((PrimitiveType)Type!).Kind == primitive;

        public Value(ValueKind Kind, IrType? Type, BigInteger Int = default, double Float = 0, Address Address = default,
            byte[]? Bytes = null, IrType? Witness = null, int Function = 0)
        {
            this.Type = Type;
            byte form = Signed;
            reference = null;
            switch (Kind)
            {
                case ValueKind.Float:
                    bits = BitConverter.DoubleToInt64Bits(Float);
                    reference = Numbers.IsWide(Type) ? Int : null;
                    break;
                case ValueKind.Pointer or ValueKind.Deferred:
                    bits = (long)Address.Packed;
                    break;
                case ValueKind.Struct:
                    bits = 0;
                    reference = Bytes;
                    break;
                case ValueKind.Witness:
                    bits = 0;
                    reference = Witness;
                    break;
                case ValueKind.Function:
                    bits = Function;
                    break;
                default:
                    (bits, form, reference) = Hold(Int);
                    break;
            }
            tag = Tag(Kind, form, wrapped: false);
        }

        /// <summary>How <see cref="Value"/> holds this integer.</summary>
        private static (long Bits, byte Form, object? Reference) Hold(BigInteger value) =>
            value >= long.MinValue && value <= long.MaxValue ? ((long)value, Signed, null)
            : value.Sign > 0 && value <= ulong.MaxValue ? ((long)(ulong)value, Unsigned, null)
            : (0, Big, value);

        /// <summary>An integer (or bool) value; 0 for other kinds, except a wide float, whose bits it is.</summary>
        public BigInteger Int => kind switch
        {
            ValueKind.Int or ValueKind.Bool or ValueKind.Unit => form switch
            {
                Signed => bits,
                Unsigned => (ulong)bits,
                _ => (BigInteger)reference!,
            },
            ValueKind.Float when reference is BigInteger wide => wide,
            _ => BigInteger.Zero,
        };

        public double Float => kind == ValueKind.Float ? BitConverter.Int64BitsToDouble(bits) : 0;
        public Address Address => kind is ValueKind.Pointer or ValueKind.Deferred ? Address.Unpack((ulong)bits) : default;
        public byte[]? Bytes => reference as byte[];
        public IrType? Witness => kind == ValueKind.Witness ? reference as IrType : null;
        public int Function => kind == ValueKind.Function ? (int)bits : 0;

        /// <summary>An integer, bool or address held in 64 bits: its two's complement bits (false for wider integers and other kinds).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TryBits(out long value)
        {
            value = bits;
            return form != Big && kind is ValueKind.Int or ValueKind.Bool or ValueKind.Pointer or ValueKind.Deferred;
        }

        /// <summary>An integer or bool whose value fits a long, as that long (false for others, also for u64 values from 2^63).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TryLong(out long value)
        {
            value = bits;
            return form == Signed && kind is ValueKind.Int or ValueKind.Bool;
        }

        /// <summary>For <see cref="TryBits"/> values: whether the value is 2^63 or more (bits read as unsigned; an address's always are).</summary>
        internal bool IsHighUnsigned => form == Unsigned || bits < 0 && kind is ValueKind.Pointer or ValueKind.Deferred;

        /// <summary>A narrow float's double, straight from the bits (no kind check).</summary>
        internal double RawDouble => BitConverter.Int64BitsToDouble(bits);

        public static readonly Value UnitValue = new(ValueKind.Unit, IrType.Unit, 0L);

        public static Value FromBool(bool value) => new(ValueKind.Bool, IrType.Bool, value ? 1L : 0L);

        public static Value FromInt(BigInteger value, IrType? type)
        {
            if (type is PrimitiveType p && Numbers.SmallInteger(p.Kind) && value >= long.MinValue && value <= ulong.MaxValue)
                return FromInt(value <= long.MaxValue ? (long)value : (long)(ulong)value, type);
            var (bits, form, reference) = Hold(type is null ? value : Numbers.Wrap(value, type));
            return new(ValueKind.Int, type, bits, form, reference);
        }

        /// <summary>An integer from a long, wrapped to <paramref name="type"/> (for 64 bits or less without a BigInteger).</summary>
        public static Value FromInt(long value, IrType? type)
        {
            if (type is not PrimitiveType p)
                return new(ValueKind.Int, type, value); // untyped, or not an integer type: nothing to wrap
            switch (p.Kind)
            {
                case Primitive.I8: return new(ValueKind.Int, type, (sbyte)value, wrapped: true);
                case Primitive.I16: return new(ValueKind.Int, type, (short)value, wrapped: true);
                case Primitive.I32: return new(ValueKind.Int, type, (int)value, wrapped: true);
                case Primitive.I64: return new(ValueKind.Int, type, value, wrapped: true);
                case Primitive.I128 or Primitive.I256: return new(ValueKind.Int, type, value);
                case Primitive.U8: return new(ValueKind.Int, type, (byte)value, wrapped: true);
                case Primitive.U16: return new(ValueKind.Int, type, (ushort)value, wrapped: true);
                case Primitive.U32: return new(ValueKind.Int, type, (uint)value, wrapped: true);
                case Primitive.U64: return new(ValueKind.Int, type, value, value < 0 ? Unsigned : Signed, wrapped: true);
                case Primitive.U128 or Primitive.U256:
                {
                    if (value >= 0)
                        return new(ValueKind.Int, type, value);
                    var (bits, form, reference) = Hold(Numbers.Wrap(value, type));
                    return new(ValueKind.Int, type, bits, form, reference);
                }
                default:
                    return new(ValueKind.Int, type, value); // bool, unit, floats: as given (Wrap leaves them)
            }
        }

        /// <summary>An integer from its 64 bits read as unsigned, wrapped to <paramref name="type"/>.</summary>
        internal static Value FromUnsigned(ulong value, IrType? type) =>
            value <= long.MaxValue || type is PrimitiveType p && Numbers.SmallInteger(p.Kind)
                ? FromInt((long)value, type)
                : FromInt((BigInteger)value, type);

        public static Value FromFloat(double value, IrType type) => Numbers.IsWide(type)
            ? FromFloatBits(SoftFloat.FromDouble(value, Numbers.FormatOf(type)), type)
            : new(ValueKind.Float, type, BitConverter.DoubleToInt64Bits(Numbers.Round(value, type)));

        /// <summary>A float from its IEEE bit pattern (exact for every width).</summary>
        public static Value FromFloatBits(BigInteger bits, IrType type)
        {
            if (!Numbers.IsWide(type))
                return FromFloat(Numbers.FromBits(bits, type), type);
            var format = Numbers.FormatOf(type);
            bits &= format.Mask;
            return new(ValueKind.Float, type, BitConverter.DoubleToInt64Bits(SoftFloat.ToDouble(bits, format)), Signed, bits);
        }

        /// <summary>A float's IEEE bit pattern.</summary>
        public BigInteger FloatBits => Numbers.IsWide(Type) ? Int : Numbers.ToBits(Float, Type!);

        /// <summary>This float's bits as a value of (float) type <paramref name="type"/>: converted exactly when the width differs.</summary>
        public BigInteger FloatBitsAs(IrType type) => Type == type ? FloatBits : SoftFloat.Convert(FloatBits, Numbers.FormatOf(Type!), Numbers.FormatOf(type));
        public static Value FromPointer(Address address, IrType? type) => new(ValueKind.Pointer, type, (long)address.Packed);
        public static Value FromStruct(byte[] bytes, IrType type) => new(ValueKind.Struct, type, 0, Signed, bytes);
        public static Value Deferred(Address address) => new(ValueKind.Deferred, null, (long)address.Packed);
        public static Value FromWitness(IrType type) => new(ValueKind.Witness, null, 0, Signed, type);
        public static Value FromFunction(int index) => new(ValueKind.Function, IrType.U64, index);

        public bool IsTrue => kind switch
        {
            ValueKind.Bool or ValueKind.Int => form == Big ? !((BigInteger)reference!).IsZero : bits != 0,
            ValueKind.Float => Float != 0,
            ValueKind.Pointer => !Address.IsNull,
            _ => throw new VmException($"{this} is not a condition"),
        };

        public bool Equals(Value other) =>
            (tag & ~WrappedBit) == (other.tag & ~WrappedBit) && bits == other.bits && Equals(Type, other.Type) && Equals(reference, other.reference);

        public override bool Equals(object? obj) => obj is Value other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(kind, bits, Type, reference);
        public static bool operator ==(Value left, Value right) => left.Equals(right);
        public static bool operator !=(Value left, Value right) => !left.Equals(right);

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

    /// <summary>An operand stack: an array and a count (no version checks, no copies to pop).</summary>
    internal sealed class ValueStack
    {
        private Value[] items = new Value[8];
        private int count;

        public int Count => count;

        public ref Value this[int index] => ref items[index];

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Add(Value value)
        {
            if (count == items.Length)
                Array.Resize(ref items, count * 2);
            items[count++] = value;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public Value Pop()
        {
            if (count == 0)
                throw new VmException("operand stack underflow");
            var value = items[--count];
            items[count] = default; // no stale references for the host collector
            return value;
        }

        public void Clear()
        {
            Array.Clear(items, 0, count);
            count = 0;
        }

        public Span<Value>.Enumerator GetEnumerator() => items.AsSpan(0, count).GetEnumerator();
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

        public static bool IsWide(IrType? type) => type is PrimitiveType { Kind: Primitive.F128 or Primitive.F256 };

        /// <summary>An integer kind of 64 bits or less (its values need no BigInteger).</summary>
        public static bool SmallInteger(Primitive kind) => kind is >= Primitive.I8 and <= Primitive.U64;

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
