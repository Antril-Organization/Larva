using System.Numerics;

namespace Larva.IL.Encoding
{
    /// <summary>
    /// Writes a stream of 4-bit units, high nibble first. Integers use a nibble-varint:
    /// each nibble carries 3 data bits (little-endian groups) plus a continuation bit (<c>0b1000</c>),
    /// so 0–7 costs 4 bits, 8–63 costs 8 bits, 64–511 costs 12 bits, and so on.
    /// </summary>
    public sealed class NibbleWriter
    {
        private readonly List<byte> bytes = [];

        /// <summary>Number of nibbles written so far.</summary>
        public int Length { get; private set; }

        public void WriteNibble(int nibble)
        {
            nibble &= 0xF;
            if ((Length & 1) == 0)
                bytes.Add((byte)(nibble << 4));
            else
                bytes[^1] |= (byte)nibble;
            Length++;
        }

        public void WriteByte(byte value)
        {
            WriteNibble(value >> 4);
            WriteNibble(value);
        }

        public void WriteUnsigned(ulong value)
        {
            do
            {
                int group = (int)(value & 0b111);
                value >>= 3;
                if (value != 0)
                    group |= 0b1000;
                WriteNibble(group);
            } while (value != 0);
        }

        public void WriteSigned(long value) => WriteUnsigned(ZigZag.Encode(value));

        /// <summary>Arbitrary-size varint (same nibbles as <see cref="WriteUnsigned(ulong)"/> for values that fit).</summary>
        public void WriteUnsigned(BigInteger value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value <= ulong.MaxValue)
            {
                WriteUnsigned((ulong)value);
                return;
            }
            do
            {
                int group = (int)(value & 0b111);
                value >>= 3;
                if (!value.IsZero)
                    group |= 0b1000;
                WriteNibble(group);
            } while (!value.IsZero);
        }

        /// <summary>Arbitrary-size zigzag varint (same nibbles as <see cref="WriteSigned(long)"/> for values that fit).</summary>
        public void WriteSigned(BigInteger value) => WriteUnsigned(ZigZag.Encode(value));

        /// <summary>Packed bytes; an odd trailing nibble is padded with zero.</summary>
        public byte[] ToArray() => [.. bytes];

        /// <summary>Hex digits of the written nibbles, one char per nibble.</summary>
        public string ToHex()
        {
            var chars = new char[Length];
            for (int i = 0; i < Length; i++)
            {
                int nibble = (i & 1) == 0 ? bytes[i >> 1] >> 4 : bytes[i >> 1] & 0xF;
                chars[i] = "0123456789ABCDEF"[nibble];
            }
            return new string(chars);
        }

        public static int UnsignedLength(ulong value)
        {
            int length = 1;
            while ((value >>= 3) != 0)
                length++;
            return length;
        }

        public static int SignedLength(long value) => UnsignedLength(ZigZag.Encode(value));
    }

    internal static class ZigZag
    {
        public static ulong Encode(long value) => (ulong)((value << 1) ^ (value >> 63));

        public static long Decode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

        public static BigInteger Encode(BigInteger value) => value.Sign < 0 ? (-value << 1) - 1 : value << 1;

        public static BigInteger Decode(BigInteger value) => value.IsEven ? value >> 1 : -((value + 1) >> 1);
    }
}
