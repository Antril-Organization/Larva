namespace Larva.IL.Encoding
{
    /// <summary>
    /// Opcode wire format:
    /// <code>
    /// xxxx         → short opcode, 4 bits (0x0 – 0xE)
    /// 1111 xxxx    → extended opcode, 8 bits (0xF0 – 0xFF)
    /// </code>
    /// Family opcodes are followed by one nibble with the sub-operation.
    /// </summary>
    public static class OpCodeEncoding
    {
        public const int ExtendedPrefix = 0xF;

        public static bool IsShort(OpCode opCode) => (byte)opCode < ExtendedPrefix;

        public static void Write(NibbleWriter writer, OpCode opCode)
        {
            byte value = (byte)opCode;
            if (IsShort(opCode))
            {
                writer.WriteNibble(value);
                return;
            }

            writer.WriteNibble(ExtendedPrefix);
            writer.WriteNibble(value);
        }

        public static OpCode Read(ref NibbleReader reader)
        {
            int first = reader.ReadNibble();
            OpCode opCode = first == ExtendedPrefix
                ? (OpCode)(ExtendedPrefix << 4 | reader.ReadNibble())
                : (OpCode)first;

            if (!OpCodes.TryGet(opCode, out _))
                throw new InvalidDataException($"Unknown opcode 0x{(byte)opCode:X} at nibble {reader.Position}.");
            return opCode;
        }
    }
}
