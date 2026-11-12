using System.Buffers.Binary;
using System.Diagnostics;

namespace Larva.Bindings.Tests;

/// <summary>
/// <see cref="Exports"/> over crafted PE and ELF images: a valid one gives its names, every damaged one (a section table
/// past the end, a <c>.dynsym</c> whose entries are 0 bytes, a truncated file, a name table in no section) is refused as
/// one <see cref="BindingException"/> naming the problem, quickly, and never as another exception.
/// </summary>
public sealed class ExportsTests : CheckedTest
{
    /// <summary>The message of the <see cref="BindingException"/> that <paramref name="read"/> throws, or what went wrong instead.</summary>
    private static string Failure(Func<IReadOnlyList<string>> read)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var names = read();
            return $"no exception ({names.Count} names)";
        }
        catch (BindingException e)
        {
            return watch.ElapsedMilliseconds < 2000 ? e.Message : $"took {watch.ElapsedMilliseconds} ms: {e.Message}";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static bool Refused(string failure) => failure.StartsWith("not a valid ");

    private static void U16(byte[] b, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)value);

    private static void U32(byte[] b, int at, long value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)value);

    private static void U64(byte[] b, int at, long value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), (ulong)value);

    /// <summary>A PE32+ image with one section (RVA 0x1000, at file offset 0x200) holding an export directory of two names.</summary>
    private static byte[] Pe()
    {
        var image = new byte[0x300];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        U32(image, 0x3C, 0x80);
        int pe = 0x80;
        image[pe] = (byte)'P';
        image[pe + 1] = (byte)'E';
        U16(image, pe + 6, 1); // one section
        U16(image, pe + 20, 112 + 16 * 8); // optional header size: PE32+ fixed part + 16 directories
        int optional = pe + 24;
        U16(image, optional, 0x20B);
        U32(image, optional + 112, 0x1000); // export directory RVA
        U32(image, optional + 116, 0x60);
        int section = optional + 112 + 16 * 8;
        U32(image, section + 8, 0x100); // virtual size
        U32(image, section + 12, 0x1000); // virtual address
        U32(image, section + 16, 0x100); // raw size
        U32(image, section + 20, 0x200); // raw offset
        int export = 0x200;
        U32(image, export + 24, 2); // name count
        U32(image, export + 32, 0x1000 + 0x30); // name table RVA
        U32(image, export + 0x30, 0x1000 + 0x40);
        U32(image, export + 0x34, 0x1000 + 0x48);
        "Alpha\0"u8.CopyTo(image.AsSpan(export + 0x40));
        "Beta\0"u8.CopyTo(image.AsSpan(export + 0x48));
        return image;
    }

    /// <summary>An ELF64 image with three sections: null, <c>.dynsym</c> (two symbols: null and a global function) and <c>.dynstr</c>.</summary>
    private static byte[] Elf()
    {
        var image = new byte[0x200];
        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2; // 64-bit
        image[5] = 1; // little-endian
        int sections = 0x40;
        U64(image, 0x28, sections);
        U16(image, 0x3A, 0x40);
        U16(image, 0x3C, 3);
        int dynsym = sections + 0x40, dynstr = sections + 0x80;
        U32(image, dynsym + 4, 11); // SHT_DYNSYM
        U64(image, dynsym + 0x18, 0x100); // offset
        U64(image, dynsym + 0x20, 48); // size: two entries
        U32(image, dynsym + 0x28, 2); // link: .dynstr
        U64(image, dynsym + 0x38, 24); // entry size
        U32(image, dynstr + 4, 3); // SHT_STRTAB
        U64(image, dynstr + 0x18, 0x140);
        U64(image, dynstr + 0x20, 0x10);
        int symbol = 0x100 + 24;
        U32(image, symbol, 1); // name at .dynstr + 1
        image[symbol + 4] = 1 << 4 | 2; // GLOBAL FUNC
        U16(image, symbol + 6, 1); // defined in section 1
        "\0gamma\0"u8.CopyTo(image.AsSpan(0x140));
        return image;
    }

    [Fact]
    public void Crafted_pe_and_elf_images_give_their_exports()
    {
        var pe = Exports.ReadPe(Pe());
        Check("the crafted PE exports Alpha and Beta", pe.SequenceEqual(["Alpha", "Beta"]), string.Join(",", pe));
        var elf = Exports.ReadElf(Elf());
        Check("the crafted ELF exports gamma", elf.SequenceEqual(["gamma"]), string.Join(",", elf));
    }

    [Fact]
    public void Damaged_pe_images_are_refused_with_one_message()
    {
        var truncated = Pe()[..0x90];
        string failure = Failure(() => Exports.ReadPe(truncated));
        Check("a PE cut inside its header is refused", Refused(failure) && failure.Contains("past the end"), failure);

        var farHeader = Pe();
        U32(farHeader, 0x3C, 0x7FFF_FFF0);
        failure = Failure(() => Exports.ReadPe(farHeader));
        Check("a PE header offset past the file is refused", Refused(failure), failure);

        var noSignature = Pe();
        noSignature[0x80] = (byte)'X';
        failure = Failure(() => Exports.ReadPe(noSignature));
        Check("a missing PE signature is refused", Refused(failure) && failure.Contains("signature"), failure);

        var manySections = Pe();
        U16(manySections, 0x80 + 6, 0xFFFF);
        failure = Failure(() => Exports.ReadPe(manySections));
        Check("a section table past the end of the file is refused", Refused(failure), failure);

        var manyNames = Pe();
        U32(manyNames, 0x200 + 24, 0x7FFF_FFFF);
        failure = Failure(() => Exports.ReadPe(manyNames));
        Check("an export directory claiming 2^31 names is refused", Refused(failure) && failure.Contains("names"), failure);

        var lostTable = Pe();
        U32(lostTable, 0x200 + 32, 0x9000);
        failure = Failure(() => Exports.ReadPe(lostTable));
        Check("a name table in no section is refused", Refused(failure) && failure.Contains("no section"), failure);

        var lostName = Pe();
        U32(lostName, 0x80 + 24 + 112 + 16 * 8 + 8, 0x400); // the section's virtual size reaches past the file
        U32(lostName, 0x200 + 0x34, 0x13F0); // inside the section, past the file
        failure = Failure(() => Exports.ReadPe(lostName));
        Check("a name past the end of the file is refused", Refused(failure) && failure.Contains("past the end"), failure);

        var noDirectory = Pe();
        U16(noDirectory, 0x80 + 20, 112); // optional header without the data directories
        failure = Failure(() => Exports.ReadPe(noDirectory));
        Check("an optional header without data directories is refused", Refused(failure) && failure.Contains("export directory entry"), failure);
    }

    [Fact]
    public void Damaged_elf_images_are_refused_with_one_message()
    {
        var zeroEntries = Elf();
        U64(zeroEntries, 0x40 + 0x40 + 0x38, 0); // sh_entsize = 0 looped forever
        string failure = Failure(() => Exports.ReadElf(zeroEntries));
        Check("a .dynsym with 0-byte entries is refused (no endless loop)", Refused(failure) && failure.Contains("entries of 0 bytes"), failure);

        var truncated = Elf()[..0x30];
        failure = Failure(() => Exports.ReadElf(truncated));
        Check("an ELF cut inside its header is refused", Refused(failure) && failure.Contains("past the end"), failure);

        var farTable = Elf();
        U64(farTable, 0x28, 0x1_0000_0000);
        failure = Failure(() => Exports.ReadElf(farTable));
        Check("a section table past the file is refused", Refused(failure), failure);

        var manySections = Elf();
        U16(manySections, 0x3C, 0xFFFF);
        failure = Failure(() => Exports.ReadElf(manySections));
        Check("65,535 section headers in a 512-byte file are refused", Refused(failure), failure);

        var hugeSymbols = Elf();
        U64(hugeSymbols, 0x40 + 0x40 + 0x20, 0x7FFF_FFFF_FFFF);
        failure = Failure(() => Exports.ReadElf(hugeSymbols));
        Check("a .dynsym past the end of the file is refused", Refused(failure) && failure.Contains("past the end"), failure);

        var badLink = Elf();
        U32(badLink, 0x40 + 0x40 + 0x28, 9);
        failure = Failure(() => Exports.ReadElf(badLink));
        Check("a .dynsym linking to a section that does not exist is refused", Refused(failure) && failure.Contains("links to section 9"), failure);

        var lostName = Elf();
        U32(lostName, 0x100 + 24, 0x7FFF_FFFF);
        failure = Failure(() => Exports.ReadElf(lostName));
        Check("a symbol name past .dynstr is refused", Refused(failure) && failure.Contains(".dynstr"), failure);

        var negativeOffset = Elf();
        U64(negativeOffset, 0x40 + 0x40 + 0x18, -8);
        failure = Failure(() => Exports.ReadElf(negativeOffset));
        Check("a negative .dynsym offset is refused", Refused(failure), failure);
    }
}
