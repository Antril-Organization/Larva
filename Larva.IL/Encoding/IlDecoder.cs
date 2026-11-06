namespace Larva.IL.Encoding
{
    /// <summary>Inverse of <see cref="IlEncoder"/>.</summary>
    public static class IlDecoder
    {
        public static OperationBlock Decode(string blockId, ReadOnlySpan<byte> bytes)
        {
            NibbleReader reader = new(bytes);
            return Decode(blockId, ref reader);
        }

        public static OperationBlock Decode(string blockId, ref NibbleReader reader)
        {
            ulong count = reader.ReadUnsigned();
            if (count > (ulong)reader.Length)
                throw new InvalidDataException($"Operation count {count} exceeds stream size.");

            var operations = new Operation[count];
            for (int i = 0; i < operations.Length; i++)
                operations[i] = Decode(ref reader, i);
            return new OperationBlock(blockId, operations);
        }

        public static Operation Decode(ref NibbleReader reader, int index)
        {
            OpCode opCode = OpCodeEncoding.Read(ref reader);
            var info = OpCodes.Get(opCode);

            OperandKind[] shape = info.Operands;
            byte subOp = 0;
            if (info.IsFamily)
            {
                subOp = (byte)reader.ReadNibble();
                if (!info.TryGetSubOp(subOp, out var sub))
                    throw new InvalidDataException($"'{info.Mnemonic}' has no sub-operation {subOp} (nibble {reader.Position}).");
                shape = sub.Operands;
            }

            List<long> operands = new(shape.Length);
            foreach (var kind in shape)
            {
                if (kind == OperandKind.LabelTable)
                {
                    ulong labels = reader.ReadUnsigned();
                    for (ulong j = 0; j < labels; j++)
                        operands.Add(ReadOperand(ref reader, OperandKind.Label, index));
                }
                else
                {
                    operands.Add(ReadOperand(ref reader, kind, index));
                }
            }
            return Operation.FromCode(opCode, subOp, [.. operands]);
        }

        private static long ReadOperand(ref NibbleReader reader, OperandKind kind, int index) => kind switch
        {
            OperandKind.Label => reader.ReadSigned() + index + 1,
            OperandKind.Immediate => reader.ReadSigned(),
            _ => checked((long)reader.ReadUnsigned()),
        };
    }
}
