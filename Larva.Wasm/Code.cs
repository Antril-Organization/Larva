namespace Larva.Wasm
{
    /// <summary>Instruction emitter for one function body (the opcodes the code generator uses).</summary>
    internal sealed class Code
    {
        private readonly Bytes b = new();

        public byte[] ToArray() => b.ToArray();

        private void Op(byte op) => b.Byte(op);

        private void Mem(byte op, int align, int offset)
        {
            b.Byte(op);
            b.U32((uint)align);
            b.U32((uint)offset);
        }

        // ── Control ──
        public void Unreachable() => Op(0x00);
        public void Block() { Op(0x02); Op(0x40); }
        public void Loop() { Op(0x03); Op(0x40); }
        public void If() { Op(0x04); Op(0x40); }
        public void Else() => Op(0x05);
        public void End() => Op(0x0B);
        public void Br(int depth) { Op(0x0C); b.U32((uint)depth); }
        public void BrIf(int depth) { Op(0x0D); b.U32((uint)depth); }

        public void BrTable(int count)
        {
            Op(0x0E);
            b.U32((uint)count);
            for (int i = 0; i < count; i++)
                b.U32((uint)i);
            b.U32((uint)count);
        }

        public void Return() => Op(0x0F);
        public void Call(int function) { Op(0x10); b.U32((uint)function); }
        public void CallIndirect(int type) { Op(0x11); b.U32((uint)type); b.U32(0); }
        public void Drop() => Op(0x1A);

        // ── Variables ──
        public void LocalGet(int local) { Op(0x20); b.U32((uint)local); }
        public void LocalSet(int local) { Op(0x21); b.U32((uint)local); }
        public void LocalTee(int local) { Op(0x22); b.U32((uint)local); }
        public void GlobalGet(int global) { Op(0x23); b.U32((uint)global); }
        public void GlobalSet(int global) { Op(0x24); b.U32((uint)global); }

        // ── Memory ──
        public void I32Load(int offset = 0) => Mem(0x28, 2, offset);
        public void I64Load(int offset = 0) => Mem(0x29, 3, offset);
        public void F32Load(int offset = 0) => Mem(0x2A, 2, offset);
        public void F64Load(int offset = 0) => Mem(0x2B, 3, offset);
        public void I32Load8U(int offset = 0) => Mem(0x2D, 0, offset);
        public void I32Store(int offset = 0) => Mem(0x36, 2, offset);
        public void I64Store(int offset = 0) => Mem(0x37, 3, offset);
        public void F32Store(int offset = 0) => Mem(0x38, 2, offset);
        public void F64Store(int offset = 0) => Mem(0x39, 3, offset);
        public void I32Store8(int offset = 0) => Mem(0x3A, 0, offset);

        /// <summary>Loads <paramref name="size"/> bytes as an i64, extended by <paramref name="signed"/>.</summary>
        public void I64LoadSized(int size, bool signed, int offset = 0)
        {
            switch (size)
            {
                case 1: Mem(signed ? (byte)0x30 : (byte)0x31, 0, offset); break;
                case 2: Mem(signed ? (byte)0x32 : (byte)0x33, 1, offset); break;
                case 4: Mem(signed ? (byte)0x34 : (byte)0x35, 2, offset); break;
                default: Mem(0x29, 3, offset); break;
            }
        }

        /// <summary>Stores the low <paramref name="size"/> bytes of an i64.</summary>
        public void I64StoreSized(int size, int offset = 0)
        {
            switch (size)
            {
                case 1: Mem(0x3C, 0, offset); break;
                case 2: Mem(0x3D, 1, offset); break;
                case 4: Mem(0x3E, 2, offset); break;
                default: Mem(0x37, 3, offset); break;
            }
        }

        public void MemoryCopy() { Op(0xFC); b.U32(10); b.Byte(0); b.Byte(0); }
        public void MemoryFill() { Op(0xFC); b.U32(11); b.Byte(0); }

        // ── Constants ──
        public void I32(int value) { Op(0x41); b.S64(value); }
        public void I64(long value) { Op(0x42); b.S64(value); }

        // ── Numeric ──
        public void I32Eqz() => Op(0x45);
        public void I32Eq() => Op(0x46);
        public void I32Ne() => Op(0x47);
        public void I32LtU() => Op(0x49);
        public void I32GeU() => Op(0x4F);
        public void I64Eqz() => Op(0x50);
        public void I64Op(byte op) => Op(op);
        public void I32Add() => Op(0x6A);
        public void I32Sub() => Op(0x6B);
        public void I32Mul() => Op(0x6C);
        public void I32And() => Op(0x71);
        public void I32Or() => Op(0x72);
        public void I64Add() => Op(0x7C);
        public void I64Sub() => Op(0x7D);
        public void I64Mul() => Op(0x7E);
        public void I64And() => Op(0x83);
        public void I64Or() => Op(0x84);
        public void I64Xor() => Op(0x85);
        public void I64Shl() => Op(0x86);
        public void I64ShrS() => Op(0x87);
        public void I64ShrU() => Op(0x88);
        public void I32WrapI64() => Op(0xA7);
        public void I64ExtendI32U() => Op(0xAD);
        public void I64ExtendI32S() => Op(0xAC);
        public void Raw(byte op) => Op(op);
        public void Prefixed(uint op) { Op(0xFC); b.U32(op); }
    }
}
