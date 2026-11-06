using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Larva.Hir.Encoding
{
    /// <summary>A growable byte buffer with LEB128 integers.</summary>
    internal sealed class ByteSink
    {
        private byte[] data = new byte[256];
        public int Length { get; private set; }

        private void Ensure(int more)
        {
            if (Length + more > data.Length)
                Array.Resize(ref data, Math.Max(data.Length * 2, Length + more));
        }

        public void Byte(int value)
        {
            Ensure(1);
            data[Length++] = (byte)value;
        }

        public void Bool(bool value) => Byte(value ? 1 : 0);

        public void UInt(ulong value)
        {
            Ensure(10);
            while (value >= 0x80)
            {
                data[Length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            data[Length++] = (byte)value;
        }

        public void UInt(int value) => UInt((ulong)(uint)value);

        /// <summary>A signed integer, zigzag encoded (small negatives stay small: -1 is one byte).</summary>
        public void Int(long value) => UInt((ulong)((value << 1) ^ (value >> 63)));

        public void Big(BigInteger value)
        {
            if (value >= long.MinValue && value <= long.MaxValue)
            {
                Byte(0);
                Int((long)value);
                return;
            }
            var bytes = value.ToByteArray();
            Byte(1);
            UInt(bytes.Length);
            Raw(bytes);
        }

        public void Raw(ReadOnlySpan<byte> bytes)
        {
            Ensure(bytes.Length);
            bytes.CopyTo(data.AsSpan(Length));
            Length += bytes.Length;
        }

        public void Utf8(string value)
        {
            int count = System.Text.Encoding.UTF8.GetByteCount(value);
            UInt(count);
            Ensure(count);
            System.Text.Encoding.UTF8.GetBytes(value, data.AsSpan(Length));
            Length += count;
        }

        public void UInt32At(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);

        public ReadOnlySpan<byte> Span => data.AsSpan(0, Length);
    }

    /// <summary>Reads what <see cref="ByteSink"/> writes.</summary>
    internal ref struct ByteSource
    {
        private readonly ReadOnlySpan<byte> data;
        public int Position;

        public ByteSource(ReadOnlySpan<byte> data, int position = 0)
        {
            this.data = data;
            Position = position;
        }

        public int Remaining => data.Length - Position;

        public byte Byte() => Position < data.Length ? data[Position++] : throw new HirFormatException("unexpected end of file");

        public bool Bool() => Byte() != 0;

        public ulong ULong()
        {
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                byte b = Byte();
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                    return value;
                shift += 7;
                if (shift > 63)
                    throw new HirFormatException("integer too long");
            }
        }

        public int UInt()
        {
            ulong value = ULong();
            return value <= int.MaxValue ? (int)value : throw new HirFormatException("count out of range");
        }

        public long Int()
        {
            ulong raw = ULong();
            return (long)(raw >> 1) ^ -(long)(raw & 1);
        }

        public BigInteger Big()
        {
            if (Byte() == 0)
                return Int();
            return new BigInteger(Raw(UInt()));
        }

        public ReadOnlySpan<byte> Raw(int count)
        {
            if (count > Remaining)
                throw new HirFormatException("unexpected end of file");
            var span = data.Slice(Position, count);
            Position += count;
            return span;
        }

        public string Utf8() => System.Text.Encoding.UTF8.GetString(Raw(UInt()));

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Raw(4));
    }

    public sealed class HirFormatException(string message) : Exception(message);
}
