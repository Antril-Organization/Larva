namespace Larva.IL
{
    public sealed record OperationBlock(string BlockId, params Operation[] Operations)
    {
        public byte[] Encode() => Encoding.IlEncoder.Encode(this);

        public static OperationBlock Decode(string blockId, ReadOnlySpan<byte> bytes) => Encoding.IlDecoder.Decode(blockId, bytes);

        public override string ToString() => Text.Disassembler.Disassemble(this);
    }
}
