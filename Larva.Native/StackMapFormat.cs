namespace Larva.Native
{
    /// <summary>A stack slot that may hold <c>gc</c> handles: <c>[register + Offset, + Size)</c> in its function's frame.</summary>
    /// <param name="Register">DWARF register number the offset is relative to (7 = RSP, 6 = RBP on x86-64).</param>
    public readonly record struct StackRoot(ushort Register, int Offset, int Size);

    /// <summary>
    /// The <c>.llvm_stackmaps</c> section format (version 3), used by every native back end for the stack roots of tracing
    /// GC modes: LLVM writes it from <c>llvm.experimental.stackmap</c>, the direct x86-64 back end writes it itself. Each
    /// function has records whose locations come in pairs: Direct (slot address) then Constant (slot size).
    /// </summary>
    public static unsafe class StackMapFormat
    {
        public const string Section = ".llvm_stackmaps";

        /// <summary>Reads a version-3 <c>.llvm_stackmaps</c> section: function address → its root slots.</summary>
        public static Dictionary<nint, StackRoot[]> Parse(byte* section, nuint size)
        {
            var result = new Dictionary<nint, StackRoot[]>();
            if (section == null || size < 16 || section[0] != 3)
                return result;
            uint functions = *(uint*)(section + 4), constantCount = *(uint*)(section + 8);
            byte* p = section + 16;
            var functionTable = new (nint Address, ulong Records)[functions];
            for (int f = 0; f < functions; f++, p += 24)
                functionTable[f] = ((nint)(*(ulong*)p), *(ulong*)(p + 16));
            var constants = (ulong*)p;
            p += constantCount * 8;

            foreach (var (address, records) in functionTable)
            {
                var roots = new List<StackRoot>();
                for (ulong r = 0; r < records; r++)
                {
                    ushort locations = *(ushort*)(p + 14);
                    byte* location = p + 16;
                    for (int l = 0; l + 1 < locations; l += 2, location += 24)
                    {
                        // Pairs: Direct (slot address) then Constant / ConstantIndex (slot size).
                        byte kind = location[0], sizeKind = location[12];
                        int sizeValue = *(int*)(location + 12 + 8);
                        long slotSize = sizeKind == 5 ? (long)constants[sizeValue] : sizeValue;
                        if (kind == 2)
                            roots.Add(new StackRoot(*(ushort*)(location + 4), *(int*)(location + 8), (int)slotSize));
                    }
                    p += 16 + locations * 12;
                    p = Align8(p);
                    ushort liveOuts = *(ushort*)(p + 2);
                    p += 4 + liveOuts * 4;
                    p = Align8(p);
                }
                result[address] = [.. roots];
            }
            return result;
        }

        private static byte* Align8(byte* p) => (byte*)(((nint)p + 7) & ~(nint)7);

        /// <summary>
        /// Writes a section with one record per function (<paramref name="functionOffsets"/> are filled in by the caller's
        /// relocations: the returned list gives the byte offset of each function address field).
        /// </summary>
        public static byte[] Write(IReadOnlyList<(long StackSize, StackRoot[] Roots)> functions, out List<int> addressFields)
        {
            var bytes = new List<byte>();
            void U8(int v) => bytes.Add((byte)v);
            void U16(int v) { U8(v); U8(v >> 8); }
            void U32(long v) { U16((int)v); U16((int)(v >> 16)); }
            void U64(long v) { U32(v); U32(v >> 32); }
            void Align8() { while (bytes.Count % 8 != 0) U8(0); }

            addressFields = [];
            U8(3); U8(0); U16(0);
            U32(functions.Count); U32(0); U32(functions.Count);
            foreach (var (stackSize, _) in functions)
            {
                addressFields.Add(bytes.Count);
                U64(0); U64(stackSize); U64(1);
            }
            for (int f = 0; f < functions.Count; f++)
            {
                var roots = functions[f].Roots;
                U64(f); U32(0); U16(0); U16(roots.Length * 2);
                foreach (var root in roots)
                {
                    U8(2); U8(0); U16(8); U16(root.Register); U16(0); U32(root.Offset);
                    U8(4); U8(0); U16(8); U16(0); U16(0); U32(root.Size);
                }
                Align8();
                U16(0); U16(0);
                Align8();
            }
            return [.. bytes];
        }
    }
}
