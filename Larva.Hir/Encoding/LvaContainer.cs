using System.Buffers.Binary;

namespace Larva.Hir.Encoding
{
    /// <summary>
    /// A Larva assembly as a <c>.dll</c>: a valid Windows PE image (x64, a DLL with no code and no entry point) whose one
    /// section, <c>.lva</c>, holds the assembly's bytes (metadata and method bodies, as a <c>.lva</c> file has them).
    /// Libraries ship this way; the readers take either form.
    /// </summary>
    public static class LvaContainer
    {
        private const int FileAlignment = 0x200, SectionAlignment = 0x1000;
        private const int PeOffset = 0x80, OptionalHeaderSize = 240, HeadersSize = 0x200;
        private static ReadOnlySpan<byte> SectionName => ".lva\0\0\0\0"u8;

        /// <summary>The assembly's bytes in a PE DLL.</summary>
        public static byte[] ToDll(byte[] lva)
        {
            int raw = Align(lva.Length, FileAlignment);
            int imageSize = SectionAlignment + Align(Math.Max(lva.Length, 1), SectionAlignment);
            var image = new byte[HeadersSize + raw];
            var span = image.AsSpan();

            // DOS header: the magic and where the PE header is; a short stub message for DOS.
            "MZ"u8.CopyTo(span);
            BinaryPrimitives.WriteUInt16LittleEndian(span[0x02..], 0x90);  // bytes on the last page
            BinaryPrimitives.WriteUInt16LittleEndian(span[0x04..], 3);     // pages
            BinaryPrimitives.WriteUInt16LittleEndian(span[0x08..], 4);     // header paragraphs
            BinaryPrimitives.WriteUInt16LittleEndian(span[0x0C..], 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(span[0x10..], 0xB8);
            BinaryPrimitives.WriteUInt16LittleEndian(span[0x18..], 0x40);
            BinaryPrimitives.WriteInt32LittleEndian(span[0x3C..], PeOffset);
            // mov dx, msg; mov ah, 9; int 21h; mov ax, 4C01h; int 21h
            ReadOnlySpan<byte> stub = [0x0E, 0x1F, 0xBA, 0x0E, 0x00, 0xB4, 0x09, 0xCD, 0x21, 0xB8, 0x01, 0x4C, 0xCD, 0x21];
            stub.CopyTo(span[0x40..]);
            "This is a Larva library.\r\r\n$"u8.CopyTo(span[0x4E..]);

            // PE signature and COFF header.
            var pe = span[PeOffset..];
            "PE\0\0"u8.CopyTo(pe);
            BinaryPrimitives.WriteUInt16LittleEndian(pe[4..], 0x8664);              // x64
            BinaryPrimitives.WriteUInt16LittleEndian(pe[6..], 1);                   // one section
            BinaryPrimitives.WriteUInt16LittleEndian(pe[20..], OptionalHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(pe[22..], 0x2022);             // executable image, large addresses, DLL

            // Optional header (PE32+).
            var optional = pe[24..];
            BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x20B);
            optional[2] = 14;                                                         // linker version
            BinaryPrimitives.WriteInt32LittleEndian(optional[8..], raw);              // initialized data
            BinaryPrimitives.WriteUInt64LittleEndian(optional[24..], 0x180000000);    // image base
            BinaryPrimitives.WriteInt32LittleEndian(optional[32..], SectionAlignment);
            BinaryPrimitives.WriteInt32LittleEndian(optional[36..], FileAlignment);
            BinaryPrimitives.WriteUInt16LittleEndian(optional[40..], 6);              // OS 6.0
            BinaryPrimitives.WriteUInt16LittleEndian(optional[48..], 6);              // subsystem 6.0
            BinaryPrimitives.WriteInt32LittleEndian(optional[56..], imageSize);
            BinaryPrimitives.WriteInt32LittleEndian(optional[60..], HeadersSize);
            BinaryPrimitives.WriteUInt16LittleEndian(optional[68..], 2);              // Windows GUI (no console)
            BinaryPrimitives.WriteUInt16LittleEndian(optional[70..], 0x0100);         // NX compatible
            BinaryPrimitives.WriteUInt64LittleEndian(optional[72..], 0x100000);       // stack reserve
            BinaryPrimitives.WriteUInt64LittleEndian(optional[80..], 0x1000);         // stack commit
            BinaryPrimitives.WriteUInt64LittleEndian(optional[88..], 0x100000);       // heap reserve
            BinaryPrimitives.WriteUInt64LittleEndian(optional[96..], 0x1000);         // heap commit
            BinaryPrimitives.WriteInt32LittleEndian(optional[108..], 16);             // data directories (all empty)

            // The section.
            var section = optional[OptionalHeaderSize..];
            SectionName.CopyTo(section);
            BinaryPrimitives.WriteInt32LittleEndian(section[8..], lva.Length);        // virtual size: the assembly's exact length
            BinaryPrimitives.WriteInt32LittleEndian(section[12..], SectionAlignment); // virtual address
            BinaryPrimitives.WriteInt32LittleEndian(section[16..], raw);
            BinaryPrimitives.WriteInt32LittleEndian(section[20..], HeadersSize);
            BinaryPrimitives.WriteUInt32LittleEndian(section[36..], 0x40000040);      // initialized data, readable

            lva.CopyTo(span[HeadersSize..]);
            return image;
        }

        /// <summary>The assembly's bytes: <paramref name="data"/> itself for a <c>.lva</c>, its <c>.lva</c> section for a DLL.</summary>
        public static byte[] Unwrap(byte[] data)
        {
            if (Section(data) is not { } found)
                return data;
            if (found.Offset < 0 || found.Length < 0 || (long)found.Offset + found.Length > data.Length)
                throw new HirFormatException("a Larva library whose .lva section is cut short");
            return data.AsSpan(found.Offset, found.Length).ToArray();
        }

        /// <summary>Whether the file is a Larva assembly: a <c>.lva</c>, or a DLL with a <c>.lva</c> section.</summary>
        public static bool IsAssembly(string path)
        {
            try
            {
                using var file = File.OpenRead(path);
                var head = new byte[Math.Min(file.Length, 4096)];
                file.ReadExactly(head);
                return head.AsSpan().StartsWith(LvaFormat.Magic) || Section(head) is not null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Where the <c>.lva</c> section's bytes are in a PE image (headers read only), or null.</summary>
        private static (int Offset, int Length)? Section(byte[] data)
        {
            var span = data.AsSpan();
            if (span.Length < 0x40 || span[0] != (byte)'M' || span[1] != (byte)'Z')
                return null;
            int pe = BinaryPrimitives.ReadInt32LittleEndian(span[0x3C..]);
            if (pe < 0 || pe + 24 > span.Length || !span.Slice(pe, 4).SequenceEqual("PE\0\0"u8))
                return null;
            int sections = BinaryPrimitives.ReadUInt16LittleEndian(span[(pe + 6)..]);
            int table = pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(span[(pe + 20)..]);
            for (int i = 0; i < sections; i++)
            {
                int header = table + i * 40;
                if (header + 40 > span.Length)
                    return null;
                if (!span.Slice(header, 8).SequenceEqual(SectionName))
                    continue;
                int length = BinaryPrimitives.ReadInt32LittleEndian(span[(header + 8)..]);
                int offset = BinaryPrimitives.ReadInt32LittleEndian(span[(header + 20)..]);
                // A header read alone (IsAssembly) has no body to check against: the section's place is enough there.
                return (offset, length);
            }
            return null;
        }

        private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
    }
}
