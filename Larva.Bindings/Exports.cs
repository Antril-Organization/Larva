namespace Larva.Bindings
{
    /// <summary>
    /// The functions a native library exports, by name: from a PE file's export directory (<c>.dll</c>) or an ELF file's
    /// dynamic symbol table (<c>.so</c>). A binary gives names only; signatures come from a <see cref="BindingDescription"/>.
    /// Every read is bounded by the file: a damaged or truncated file is one <see cref="BindingException"/>
    /// ("not a valid PE file: …"), never a crash or an endless loop.
    /// </summary>
    public static class Exports
    {
        /// <summary>Names a file may claim to export before it is taken for damaged (kernel32 has about 1,600).</summary>
        private const int MaxNames = 1 << 20;

        /// <summary>The exported function names of <paramref name="path"/>, by its format (PE or ELF).</summary>
        public static IReadOnlyList<string> Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
                return ReadPe(bytes);
            if (bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
                return ReadElf(bytes);
            throw new BindingException($"{path} is neither a PE (.dll) nor an ELF (.so) file");
        }

        // ── PE ──────────────────────────────────────────────────────

        /// <summary>The names in a PE image's export directory (PE32 and PE32+).</summary>
        public static IReadOnlyList<string> ReadPe(byte[] image)
        {
            var file = new Bounded(image, "PE");
            long pe = file.U32(0x3C, "the PE header offset");
            if (file.U8(pe, "the PE signature") != 'P' || file.U8(pe + 1, "the PE signature") != 'E' || file.U8(pe + 2, "the PE signature") != 0 || file.U8(pe + 3, "the PE signature") != 0)
                throw file.Invalid("no PE signature");
            long sectionCount = file.U16(pe + 6, "the section count");
            long optionalSize = file.U16(pe + 20, "the optional header size");
            long optional = pe + 24;
            long magic = file.U16(optional, "the optional header magic");
            long directories = magic == 0x20B ? optional + 112 : magic == 0x10B ? optional + 96 : throw file.Invalid($"unknown optional header magic {magic:x}");
            if (directories + 8 > optional + optionalSize)
                throw file.Invalid("the optional header holds no export directory entry");
            long exportRva = file.U32(directories, "the export directory"), exportSize = file.U32(directories + 4, "the export directory");
            var names = new List<string>();
            if (exportRva == 0 || exportSize == 0)
                return names;
            var sections = new (long Va, long Size, long Raw)[sectionCount];
            long header = optional + optionalSize;
            for (int s = 0; s < sectionCount; s++, header += 40)
            {
                long virtualSize = file.U32(header + 8, "a section header"), va = file.U32(header + 12, "a section header");
                long rawSize = file.U32(header + 16, "a section header"), raw = file.U32(header + 20, "a section header");
                if (raw + rawSize > image.Length)
                    throw file.Invalid($"section {s} runs past the end of the file");
                sections[s] = (va, Math.Max(virtualSize, rawSize), raw);
            }
            long Offset(long rva, string what)
            {
                foreach (var (va, size, raw) in sections)
                {
                    if (rva >= va && rva < va + size)
                        return rva - va + raw;
                }
                throw file.Invalid($"{what}: RVA {rva:x} is in no section");
            }
            long export = Offset(exportRva, "the export directory");
            long nameCount = file.U32(export + 24, "the export directory");
            if (nameCount > MaxNames)
                throw file.Invalid($"the export directory claims {nameCount} names");
            long nameTable = Offset(file.U32(export + 32, "the export directory"), "the export name table");
            for (long n = 0; n < nameCount; n++)
                names.Add(file.String(Offset(file.U32(nameTable + 4 * n, "the export name table"), $"export name {n}"), $"export name {n}"));
            return names;
        }

        // ── ELF ─────────────────────────────────────────────────────

        /// <summary>The defined global (or weak) functions of an ELF64 file's <c>.dynsym</c>.</summary>
        public static IReadOnlyList<string> ReadElf(byte[] image)
        {
            var file = new Bounded(image, "ELF");
            if (file.U8(4, "the ELF class") != 2)
                throw file.Invalid("only 64-bit ELF files are read");
            if (file.U8(5, "the ELF data encoding") != 1)
                throw file.Invalid("only little-endian ELF files are read");
            long sectionsAt = file.I64(0x28, "the section header table offset");
            long sectionSize = file.U16(0x3A, "the section header size"), sectionCount = file.U16(0x3C, "the section header count");
            if (sectionCount > 0 && sectionSize < 0x40)
                throw file.Invalid($"section headers of {sectionSize} bytes (64 expected)");
            if (sectionCount > 0 && (sectionsAt < 0 || sectionsAt + sectionSize * sectionCount > image.Length))
                throw file.Invalid("the section header table runs past the end of the file");
            var names = new List<string>();
            for (int s = 0; s < sectionCount; s++)
            {
                long header = sectionsAt + s * sectionSize;
                long type = file.U32(header + 4, "a section header");
                if (type != 11) // SHT_DYNSYM
                    continue;
                long offset = file.I64(header + 0x18, ".dynsym"), size = file.I64(header + 0x20, ".dynsym");
                long link = file.U32(header + 0x28, ".dynsym");
                long entrySize = file.I64(header + 0x38, ".dynsym");
                if (entrySize < 24)
                    throw file.Invalid($".dynsym has entries of {entrySize} bytes (24 expected)");
                if (offset < 0 || size < 0 || offset + size > image.Length)
                    throw file.Invalid(".dynsym runs past the end of the file");
                if (size / entrySize > MaxNames)
                    throw file.Invalid($".dynsym claims {size / entrySize} symbols");
                if (link >= sectionCount)
                    throw file.Invalid($".dynsym links to section {link} of {sectionCount}");
                long stringsHeader = sectionsAt + link * sectionSize;
                long strings = file.I64(stringsHeader + 0x18, ".dynstr"), stringsSize = file.I64(stringsHeader + 0x20, ".dynstr");
                if (strings < 0 || stringsSize < 0 || strings + stringsSize > image.Length)
                    throw file.Invalid(".dynstr runs past the end of the file");
                for (long at = offset; at + entrySize <= offset + size; at += entrySize)
                {
                    long name = file.U32(at, "a symbol");
                    int info = file.U8(at + 4, "a symbol");
                    long index = file.U16(at + 6, "a symbol");
                    int symbolType = info & 0xF, bind = info >> 4;
                    if (symbolType != 2 || index == 0 || bind is not (1 or 2)) // FUNC, defined, GLOBAL or WEAK
                        continue;
                    if (name >= stringsSize)
                        throw file.Invalid($"a symbol's name is at {name}, past .dynstr's {stringsSize} bytes");
                    names.Add(file.String(strings + name, "a symbol name"));
                }
            }
            return names;
        }

        /// <summary>Reads of fixed-size fields, each checked against the file's end.</summary>
        private readonly struct Bounded(byte[] bytes, string format)
        {
            public BindingException Invalid(string problem) => new($"not a valid {format} file: {problem}");

            private long At(long offset, int size, string what)
            {
                if (offset < 0 || offset > bytes.Length - size)
                    throw Invalid($"{what} is at byte {offset}, past the end of the {bytes.Length}-byte file");
                return offset;
            }

            public int U8(long offset, string what) => bytes[At(offset, 1, what)];

            public int U16(long offset, string what)
            {
                int at = (int)At(offset, 2, what);
                return bytes[at] | bytes[at + 1] << 8;
            }

            public long U32(long offset, string what) => BitConverter.ToUInt32(bytes, (int)At(offset, 4, what));

            public long I64(long offset, string what) => BitConverter.ToInt64(bytes, (int)At(offset, 8, what));

            /// <summary>A NUL-terminated ASCII string; one cut short by the file's end is the bytes that are there.</summary>
            public string String(long offset, string what)
            {
                int start = (int)At(offset, 1, what), end = start;
                while (end < bytes.Length && bytes[end] != 0)
                    end++;
                return System.Text.Encoding.ASCII.GetString(bytes, start, end - start);
            }
        }
    }

    public sealed class BindingException(string message) : Exception(message);
}
