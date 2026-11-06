namespace Larva.Bindings
{
    /// <summary>
    /// The functions a native library exports, by name: from a PE file's export directory (<c>.dll</c>) or an ELF file's
    /// dynamic symbol table (<c>.so</c>). A binary gives names only; signatures come from a <see cref="BindingDescription"/>.
    /// </summary>
    public static class Exports
    {
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
            int pe = I32(image, 0x3C);
            if (pe + 24 > image.Length || image[pe] != 'P' || image[pe + 1] != 'E' || image[pe + 2] != 0 || image[pe + 3] != 0)
                throw new BindingException("not a PE image (no PE signature)");
            int sectionCount = U16(image, pe + 6);
            int optionalSize = U16(image, pe + 20);
            int optional = pe + 24;
            int magic = U16(image, optional);
            int directories = magic == 0x20B ? optional + 112 : magic == 0x10B ? optional + 96 : throw new BindingException($"unknown optional header magic {magic:x}");
            int exportRva = I32(image, directories), exportSize = I32(image, directories + 4);
            var names = new List<string>();
            if (exportRva == 0 || exportSize == 0)
                return names;
            var sections = new (int Va, int Size, int Raw)[sectionCount];
            int header = optional + optionalSize;
            for (int s = 0; s < sectionCount; s++, header += 40)
            {
                int virtualSize = I32(image, header + 8), va = I32(image, header + 12), rawSize = I32(image, header + 16), raw = I32(image, header + 20);
                sections[s] = (va, Math.Max(virtualSize, rawSize), raw);
            }
            int Offset(int rva)
            {
                foreach (var (va, size, raw) in sections)
                {
                    if (rva >= va && rva < va + size)
                        return rva - va + raw;
                }
                throw new BindingException($"RVA {rva:x} is in no section");
            }
            int export = Offset(exportRva);
            int nameCount = I32(image, export + 24);
            int nameTable = Offset(I32(image, export + 32));
            for (int n = 0; n < nameCount; n++)
            {
                int at = Offset(I32(image, nameTable + 4 * n));
                int end = at;
                while (end < image.Length && image[end] != 0)
                    end++;
                names.Add(System.Text.Encoding.ASCII.GetString(image, at, end - at));
            }
            return names;
        }

        // ── ELF ─────────────────────────────────────────────────────

        /// <summary>The defined global (or weak) functions of an ELF64 file's <c>.dynsym</c>.</summary>
        public static IReadOnlyList<string> ReadElf(byte[] image)
        {
            if (image[4] != 2)
                throw new BindingException("only 64-bit ELF files are read");
            if (image[5] != 1)
                throw new BindingException("only little-endian ELF files are read");
            long sectionsAt = I64(image, 0x28);
            int sectionSize = U16(image, 0x3A), sectionCount = U16(image, 0x3C);
            var names = new List<string>();
            for (int s = 0; s < sectionCount; s++)
            {
                long header = sectionsAt + (long)s * sectionSize;
                int type = I32(image, (int)header + 4);
                if (type != 11) // SHT_DYNSYM
                    continue;
                long offset = I64(image, (int)header + 0x18), size = I64(image, (int)header + 0x20);
                int link = I32(image, (int)header + 0x28);
                long entrySize = I64(image, (int)header + 0x38);
                long stringsHeader = sectionsAt + (long)link * sectionSize;
                long strings = I64(image, (int)stringsHeader + 0x18);
                for (long at = offset; at + entrySize <= offset + size; at += entrySize)
                {
                    int name = I32(image, (int)at);
                    int info = image[at + 4];
                    int index = U16(image, (int)at + 6);
                    int symbolType = info & 0xF, bind = info >> 4;
                    if (symbolType != 2 || index == 0 || bind is not (1 or 2)) // FUNC, defined, GLOBAL or WEAK
                        continue;
                    int start = (int)(strings + name), end = start;
                    while (end < image.Length && image[end] != 0)
                        end++;
                    names.Add(System.Text.Encoding.ASCII.GetString(image, start, end - start));
                }
            }
            return names;
        }

        private static int U16(byte[] b, int at) => b[at] | b[at + 1] << 8;
        private static int I32(byte[] b, int at) => BitConverter.ToInt32(b, at);
        private static long I64(byte[] b, int at) => BitConverter.ToInt64(b, at);
    }

    public sealed class BindingException(string message) : Exception(message);
}
