namespace Larva.IL.Encoding
{
    /// <summary>
    /// Block layout: <c>[count:uvarint] [operation]*</c>, each operation being
    /// <c>[opcode:4|8 bits] [subop:4 bits, families only] [operand:varint]*</c>.
    /// Labels are written relative to the next operation.
    /// </summary>
    public static class IlEncoder
    {
        public static byte[] Encode(OperationBlock block)
        {
            NibbleWriter writer = new();
            Encode(writer, block);
            return writer.ToArray();
        }

        public static void Encode(NibbleWriter writer, OperationBlock block)
        {
            writer.WriteUnsigned((ulong)block.Operations.Length);
            for (int i = 0; i < block.Operations.Length; i++)
                Encode(writer, block.Operations[i], i);
        }

        /// <param name="index">Position of <paramref name="operation"/> in its block, used to make labels relative.</param>
        public static void Encode(NibbleWriter writer, Operation operation, int index)
        {
            OpCodeEncoding.Write(writer, operation.OpCode);
            if (operation.Info.IsFamily)
                writer.WriteNibble(operation.SubOp);

            var shape = operation.Shape;
            for (int i = 0; i < operation.Operands.Length; i++)
            {
                if (operation.HasLabelTable && i == shape.Length - 1)
                    writer.WriteUnsigned((ulong)(operation.Operands.Length - i));

                long value = operation.Operands[i];
                switch (operation.OperandKindAt(i))
                {
                    case OperandKind.Label:
                        writer.WriteSigned(value - (index + 1));
                        break;
                    case OperandKind.Immediate:
                        writer.WriteSigned(value);
                        break;
                    default:
                        writer.WriteUnsigned((ulong)value);
                        break;
                }
            }
        }

        /// <summary>Encoded size of a single operation, in nibbles.</summary>
        public static int SizeInNibbles(Operation operation, int index)
        {
            NibbleWriter writer = new();
            Encode(writer, operation, index);
            return writer.Length;
        }

        /// <summary>Encoded size of the whole block (header included), in nibbles.</summary>
        public static int SizeInNibbles(OperationBlock block)
        {
            NibbleWriter writer = new();
            Encode(writer, block);
            return writer.Length;
        }
    }
}
