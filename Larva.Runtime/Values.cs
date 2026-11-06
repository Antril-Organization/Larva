using System.Buffers.Binary;
using System.Numerics;
using Larva.IR;

namespace Larva.Runtime
{
    /// <summary>
    /// Little-endian encoding of scalar values in memory. .NET values map as: integers up to 64 bits ↔ <see cref="long"/> /
    /// <see cref="ulong"/>, wider ones ↔ <see cref="BigInteger"/>; f16/f32/f64 ↔ <see cref="Half"/>/<see cref="float"/>/<see cref="double"/>,
    /// f128/f256 ↔ their bit pattern as <see cref="BigInteger"/>; bool ↔ <see cref="bool"/>; pointers and handles ↔ <see cref="ulong"/>.
    /// </summary>
    internal static class Values
    {
        public static object Read(IrType type, ReadOnlySpan<byte> memory) => type switch
        {
            PrimitiveType { Kind: Primitive.Bool } => memory[0] != 0,
            PrimitiveType { Kind: Primitive.F16 } => BinaryPrimitives.ReadHalfLittleEndian(memory),
            PrimitiveType { Kind: Primitive.F32 } => BinaryPrimitives.ReadSingleLittleEndian(memory),
            PrimitiveType { Kind: Primitive.F64 } => BinaryPrimitives.ReadDoubleLittleEndian(memory),
            PrimitiveType { Kind: Primitive.Unit } => Unit.Value,
            PrimitiveType p when p.BitWidth > 64 => new BigInteger(memory[..(p.BitWidth!.Value / 8)], isUnsigned: !p.IsSigned),
            PrimitiveType p when p.IsSigned => p.BitWidth switch
            {
                8 => (long)(sbyte)memory[0],
                16 => (long)BinaryPrimitives.ReadInt16LittleEndian(memory),
                32 => (long)BinaryPrimitives.ReadInt32LittleEndian(memory),
                _ => BinaryPrimitives.ReadInt64LittleEndian(memory),
            },
            PrimitiveType p => p.BitWidth switch
            {
                8 => (ulong)memory[0],
                16 => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(memory),
                32 => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(memory),
                _ => BinaryPrimitives.ReadUInt64LittleEndian(memory),
            },
            ReferenceType or GcType or BoxType or PointerType or OptionalType => BinaryPrimitives.ReadUInt64LittleEndian(memory),
            _ => throw new ReflectionException($"cannot read a value of type `{type}` directly"),
        };

        public static void Write(IrType type, Span<byte> memory, object value, string what)
        {
            switch (type)
            {
                case PrimitiveType { Kind: Primitive.Bool }:
                    memory[0] = value is bool b ? (byte)(b ? 1 : 0) : throw Mismatch(type, value, what);
                    break;
                case PrimitiveType { Kind: Primitive.F16 }:
                    BinaryPrimitives.WriteHalfLittleEndian(memory, value switch { Half h => h, float f => (Half)f, double d => (Half)d, _ => throw Mismatch(type, value, what) });
                    break;
                case PrimitiveType { Kind: Primitive.F32 }:
                    BinaryPrimitives.WriteSingleLittleEndian(memory, value switch { float f => f, double d => (float)d, Half h => (float)h, _ => throw Mismatch(type, value, what) });
                    break;
                case PrimitiveType { Kind: Primitive.F64 }:
                    BinaryPrimitives.WriteDoubleLittleEndian(memory, value switch { double d => d, float f => f, Half h => (double)h, _ => throw Mismatch(type, value, what) });
                    break;
                case PrimitiveType { IsInteger: true } p:
                    WriteInteger(p, memory, ToBigInteger(value) ?? throw Mismatch(type, value, what), what);
                    break;
                case PrimitiveType { IsFloat: true } p: // f128 / f256: raw bit pattern
                    WriteBits(memory, ToBigInteger(value) ?? throw Mismatch(type, value, what), p.BitWidth!.Value / 8);
                    break;
                case ReferenceType or GcType or BoxType or PointerType or OptionalType:
                    BinaryPrimitives.WriteUInt64LittleEndian(memory, value is ulong u ? u : throw Mismatch(type, value, what));
                    break;
                default:
                    throw new ReflectionException($"cannot write `{what}` of type `{type}` directly");
            }
        }

        /// <summary>The 8-byte invoke slot for a scalar argument of class <c>i</c>, <c>d</c>, <c>s</c> or <c>h</c>.</summary>
        public static ulong Slot(IrType type, object value, string what)
        {
            Span<byte> bytes = stackalloc byte[32];
            bytes.Clear();
            Write(type, bytes, value, what);
            ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            // Sign-extend narrow signed integers so the callee sees the same 64-bit value.
            if (type is PrimitiveType { IsSigned: true } p && p.BitWidth < 64)
            {
                int shift = 64 - p.BitWidth!.Value;
                raw = (ulong)((long)(raw << shift) >> shift);
            }
            return raw;
        }

        /// <summary>Bytes of a value passed by pointer (class <c>x</c>): wide numbers or structs.</summary>
        public static byte[] Indirect(IrType type, int size, object value, string what)
        {
            if (value is RuntimeObject obj)
                return obj.Type.Type == type ? obj.ToArray() : throw Mismatch(type, value, what);
            var bytes = new byte[size];
            Write(type, bytes, value, what);
            return bytes;
        }

        private static void WriteInteger(PrimitiveType type, Span<byte> memory, BigInteger value, string what)
        {
            int bits = type.BitWidth!.Value;
            var (min, max) = type.IsSigned
                ? (-(BigInteger.One << (bits - 1)), (BigInteger.One << (bits - 1)) - 1)
                : (BigInteger.Zero, (BigInteger.One << bits) - 1);
            if (value < min || value > max)
                throw new ReflectionException($"{value} does not fit `{what}` ({type})");
            WriteBits(memory, value, bits / 8);
        }

        private static void WriteBits(Span<byte> memory, BigInteger value, int bytes)
        {
            var target = memory[..bytes];
            target.Fill(value.Sign < 0 ? (byte)0xFF : (byte)0);
            value.TryWriteBytes(target, out _, isUnsigned: false, isBigEndian: false);
        }

        private static BigInteger? ToBigInteger(object value) => value switch
        {
            BigInteger big => big,
            long l => l,
            ulong u => u,
            int i => i,
            uint u => u,
            short s => s,
            ushort u => u,
            sbyte s => s,
            byte b => b,
            _ => null,
        };

        private static ReflectionException Mismatch(IrType type, object value, string what) =>
            new($"`{what}` expects `{type}`, got {value.GetType().Name} {value}");
    }

    /// <summary>The value of type <c>()</c>.</summary>
    public sealed class Unit
    {
        public static readonly Unit Value = new();
        private Unit() { }
        public override string ToString() => "()";
    }
}
