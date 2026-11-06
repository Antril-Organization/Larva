using System.Numerics;

namespace Larva.IL.Encoding
{
    /// <summary>Reads what <see cref="NibbleWriter"/> writes.</summary>
    public ref struct NibbleReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;

        /// <summary>Current position in nibbles.</summary>
        public int Position { get; private set; }

        public readonly int Length => bytes.Length * 2;

        public int ReadNibble()
        {
            if (Position >= Length)
                throw new EndOfStreamException("Unexpected end of IL stream.");
            byte b = bytes[Position >> 1];
            int nibble = (Position & 1) == 0 ? b >> 4 : b & 0xF;
            Position++;
            return nibble;
        }

        public byte ReadByte() => (byte)(ReadNibble() << 4 | ReadNibble());

        public ulong ReadUnsigned()
        {
            ulong result = 0;
            for (int shift = 0; shift < 64; shift += 3)
            {
                int nibble = ReadNibble();
                result |= (ulong)(nibble & 0b111) << shift;
                if ((nibble & 0b1000) == 0)
                    return result;
            }
            throw new InvalidDataException($"Varint too long at nibble {Position}.");
        }

        public long ReadSigned() => ZigZag.Decode(ReadUnsigned());

        /// <summary>Arbitrary-size varint, rejecting values wider than <paramref name="maxBits"/>.</summary>
        public BigInteger ReadUnsignedBig(int maxBits = 512)
        {
            BigInteger result = BigInteger.Zero;
            for (int shift = 0; shift < maxBits + 3; shift += 3)
            {
                int nibble = ReadNibble();
                result |= (BigInteger)(nibble & 0b111) << shift;
                if ((nibble & 0b1000) == 0)
                    return result;
            }
            throw new InvalidDataException($"Varint wider than {maxBits} bits at nibble {Position}.");
        }

        public BigInteger ReadSignedBig(int maxBits = 512) => ZigZag.Decode(ReadUnsignedBig(maxBits + 1));
    }
}
