using System.Text;
using Larva.Native;

namespace Larva.X64
{
    /// <summary>
    /// Writes an x86-64 COFF object (what <c>link.exe</c> / <c>lld-link</c> consume):
    /// <list type="bullet">
    /// <item><c>.text</c>: the code, with <c>IMAGE_REL_AMD64_REL32</c> relocations for every call.</item>
    /// <item><c>.xdata</c> / <c>.pdata</c>: unwind info (with <c>__C_specific_handler</c> scope tables) and the function
    /// table (<c>IMAGE_REL_AMD64_ADDR32NB</c>).</item>
    /// <item><c>.llvm_stackmaps</c> (tracing modes): the stack roots in the same format LLVM emits
    /// (<c>IMAGE_REL_AMD64_ADDR64</c> to each function).</item>
    /// </list>
    /// Functions are external symbols; runtime functions are undefined externals for the linker to resolve.
    /// </summary>
    internal static class CoffWriter
    {
        private const ushort Amd64 = 0x8664;
        private const ushort RelAddr64 = 1, RelAddr32NB = 3, RelRel32 = 4;
        private const uint Text = 0x60500020, ReadOnlyData4 = 0x40300040, ReadOnlyData8 = 0x40400040, ReadOnlyData16 = 0x40500040, WritableData16 = 0xC0500040;
        private const byte External = 2, Static = 3;

        private sealed class Section(string name, uint characteristics, byte[] data)
        {
            public string Name { get; } = name;
            public uint Characteristics { get; } = characteristics;
            public byte[] Data { get; } = data;
            public List<(uint Offset, int Symbol, ushort Type)> Relocations { get; } = [];
            public int Symbol { get; set; }
        }

        public static byte[] Write(X64Program program)
        {
            // Unwind info and the function table.
            var xdata = new List<byte>();
            var pdata = new byte[program.Functions.Count * 12];
            var unwindOffsets = new List<int>();
            var xdataFixups = new List<(int Offset, UnwindFixup Fixup)>();
            foreach (var function in program.Functions)
            {
                unwindOffsets.Add(xdata.Count);
                var info = (byte[])function.UnwindInfo.Clone();
                foreach (var fixup in function.UnwindFixups)
                {
                    // Code RVAs: the addend (offset in .text) goes in place; the relocation adds the section's RVA.
                    if (fixup.Symbol is null)
                        BitConverter.TryWriteBytes(info.AsSpan(fixup.At), fixup.CodeOffset);
                    xdataFixups.Add((xdata.Count + fixup.At, fixup));
                }
                xdata.AddRange(info);
            }
            for (int f = 0; f < program.Functions.Count; f++)
            {
                var function = program.Functions[f];
                BitConverter.TryWriteBytes(pdata.AsSpan(f * 12), function.Offset);
                BitConverter.TryWriteBytes(pdata.AsSpan(f * 12 + 4), function.Offset + function.Size);
                BitConverter.TryWriteBytes(pdata.AsSpan(f * 12 + 8), unwindOffsets[f]);
            }

            var sections = new List<Section>
            {
                new(".text", Text, program.Code),
                new(".xdata", ReadOnlyData4, [.. xdata]),
                new(".pdata", ReadOnlyData4, pdata),
            };
            var rooted = program.Functions.Where(f => f.Roots.Length > 0).ToList();
            List<int> addressFields = [];
            int stackMaps = -1, rdata = -1;
            if (rooted.Count > 0)
            {
                stackMaps = sections.Count;
                sections.Add(new(StackMapFormat.Section, ReadOnlyData8, StackMapFormat.Write([.. rooted.Select(f => ((long)f.FrameSize + 8, f.Roots))], out addressFields)));
            }
            // Read-only blobs (static arrays) go to .rdata, writable ones (globals) to .data.
            var dataOffsetsInRdata = new int[program.Data.Count];
            var dataSection = new int[program.Data.Count];
            foreach (bool writable in new[] { false, true })
            {
                var bytes = new List<byte>();
                for (int d = 0; d < program.Data.Count; d++)
                {
                    if (program.Data[d].Writable != writable)
                        continue;
                    while (bytes.Count % 16 != 0)
                        bytes.Add(0);
                    dataOffsetsInRdata[d] = bytes.Count;
                    dataSection[d] = sections.Count;
                    bytes.AddRange(program.Data[d].Bytes);
                }
                if (bytes.Count == 0)
                    continue;
                if (!writable)
                    rdata = sections.Count;
                sections.Add(new(writable ? ".data" : ".rdata", writable ? WritableData16 : ReadOnlyData16, [.. bytes]));
            }

            // Symbols: one per section (with its auxiliary record), the functions, then the undefined runtime functions.
            var symbols = new List<byte[]>();
            var strings = new List<byte>();
            for (int s = 0; s < sections.Count; s++)
            {
                sections[s].Symbol = symbols.Count;
                symbols.Add(SymbolRecord(sections[s].Name, 0, (short)(s + 1), 0, Static, 1, strings));
                var aux = new byte[18];
                BitConverter.TryWriteBytes(aux.AsSpan(0), sections[s].Data.Length);
                symbols.Add(aux); // relocation count is patched below
            }
            var symbolIndex = new Dictionary<string, int>();
            foreach (var function in program.Functions)
            {
                symbolIndex[function.Name] = symbols.Count;
                symbols.Add(SymbolRecord(function.Name, (uint)function.Offset, 1, 0x20, External, 0, strings));
            }
            for (int d = 0; d < program.Data.Count; d++)
            {
                symbolIndex[program.Data[d].Name] = symbols.Count;
                symbols.Add(SymbolRecord(program.Data[d].Name, (uint)dataOffsetsInRdata[d], (short)(dataSection[d] + 1), 0, Static, 0, strings));
            }
            foreach (var external in program.Externals)
            {
                symbolIndex[external] = symbols.Count;
                symbols.Add(SymbolRecord(external, 0, 0, 0x20, External, 0, strings));
            }

            // Relocations.
            foreach (var relocation in program.Relocations)
                sections[0].Relocations.Add(((uint)relocation.Offset, symbolIndex[relocation.Symbol], RelRel32));
            for (int f = 0; f < program.Functions.Count; f++)
            {
                sections[2].Relocations.Add(((uint)(f * 12), sections[0].Symbol, RelAddr32NB));
                sections[2].Relocations.Add(((uint)(f * 12 + 4), sections[0].Symbol, RelAddr32NB));
                sections[2].Relocations.Add(((uint)(f * 12 + 8), sections[1].Symbol, RelAddr32NB));
            }
            foreach (var (offset, fixup) in xdataFixups)
                sections[1].Relocations.Add(((uint)offset, fixup.Symbol is null ? sections[0].Symbol : symbolIndex[fixup.Symbol], RelAddr32NB));
            for (int r = 0; r < rooted.Count; r++)
                sections[stackMaps].Relocations.Add(((uint)addressFields[r], symbolIndex[rooted[r].Name], RelAddr64));
            foreach (var section in sections)
                BitConverter.TryWriteBytes(symbols[section.Symbol + 1].AsSpan(4), (ushort)Math.Min(section.Relocations.Count, ushort.MaxValue));

            // Layout: header, section headers, then each section's data followed by its relocations, then symbols.
            int position = 20 + 40 * sections.Count;
            var dataOffsets = new int[sections.Count];
            var relocationOffsets = new int[sections.Count];
            for (int s = 0; s < sections.Count; s++)
            {
                position = (position + 3) & ~3;
                dataOffsets[s] = position;
                position += sections[s].Data.Length;
                relocationOffsets[s] = position;
                position += 10 * (sections[s].Relocations.Count + (Overflows(sections[s]) ? 1 : 0));
            }
            int symbolTable = position;

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Amd64);
            writer.Write((ushort)sections.Count);
            writer.Write(0u);                     // timestamp: reproducible output
            writer.Write((uint)symbolTable);
            writer.Write((uint)symbols.Count);
            writer.Write((ushort)0);              // no optional header
            writer.Write((ushort)0);              // characteristics
            for (int s = 0; s < sections.Count; s++)
            {
                var name = new byte[8];
                Encoding.ASCII.GetBytes(sections[s].Name.Length <= 8 ? sections[s].Name : $"/{AddString(sections[s].Name, strings)}").CopyTo(name, 0);
                writer.Write(name);
                writer.Write(0u);                 // virtual size
                writer.Write(0u);                 // virtual address
                writer.Write((uint)sections[s].Data.Length);
                writer.Write((uint)dataOffsets[s]);
                writer.Write((uint)(sections[s].Relocations.Count > 0 ? relocationOffsets[s] : 0));
                writer.Write(0u);                 // line numbers
                // More than 65535 relocations: the count says 0xFFFF and a first record holds the real one (the record included).
                writer.Write(Overflows(sections[s]) ? ushort.MaxValue : (ushort)sections[s].Relocations.Count);
                writer.Write((ushort)0);
                writer.Write(sections[s].Characteristics | (Overflows(sections[s]) ? RelocationsOverflow : 0u));
            }
            for (int s = 0; s < sections.Count; s++)
            {
                while (stream.Position < dataOffsets[s])
                    writer.Write((byte)0);
                writer.Write(sections[s].Data);
                if (Overflows(sections[s]))
                {
                    writer.Write((uint)(sections[s].Relocations.Count + 1));
                    writer.Write(0u);
                    writer.Write((ushort)0);
                }
                foreach (var (offset, symbol, type) in sections[s].Relocations)
                {
                    writer.Write(offset);
                    writer.Write((uint)symbol);
                    writer.Write(type);
                }
            }
            foreach (var symbol in symbols)
                writer.Write(symbol);
            writer.Write((uint)(strings.Count + 4));
            writer.Write(strings.ToArray());
            writer.Flush();
            return stream.ToArray();
        }

        /// <summary><c>IMAGE_SCN_LNK_NRELOC_OVFL</c>: the section's relocation count is in its first relocation record.</summary>
        private const uint RelocationsOverflow = 0x01000000;

        private static bool Overflows(Section section) => section.Relocations.Count >= ushort.MaxValue;

        private static byte[] SymbolRecord(string name, uint value, short section, ushort type, byte storage, byte auxiliary, List<byte> strings)
        {
            var record = new byte[18];
            var bytes = Encoding.UTF8.GetBytes(name);
            if (bytes.Length <= 8)
                bytes.CopyTo(record, 0);
            else
                BitConverter.TryWriteBytes(record.AsSpan(4), AddString(name, strings)); // first 4 bytes zero: string table offset
            BitConverter.TryWriteBytes(record.AsSpan(8), value);
            BitConverter.TryWriteBytes(record.AsSpan(12), section);
            BitConverter.TryWriteBytes(record.AsSpan(14), type);
            record[16] = storage;
            record[17] = auxiliary;
            return record;
        }

        /// <summary>Offset in the string table (which starts with its 4-byte size).</summary>
        private static int AddString(string text, List<byte> strings)
        {
            int offset = strings.Count + 4;
            strings.AddRange(Encoding.UTF8.GetBytes(text));
            strings.Add(0);
            return offset;
        }
    }
}
