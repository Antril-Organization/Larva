using Larva.IR;
using Larva.IR.Encoding;
using Larva.IR.Linking;
using Larva.IR.Lowering;
using static Larva.Tests.LinkerPrograms;

namespace Larva.Tests;

/// <summary>
/// The binary readers (<c>.lvm</c> module files, <c>.lvo</c> objects, encoded IR) over damaged input: a valid file cut
/// short or with bytes changed at many positions either still reads or fails with one <see cref="InvalidDataException"/>
/// naming the problem, never with an out-of-range, a cast, an overflow, a stack overflow or an out-of-memory; and the
/// CLI prints <c>larva: file: message</c> for it.
/// </summary>
public sealed class FormatRobustnessTests : CheckedTest
{
    private static IlModule Il() => Compiler.Compile(GenericsPrograms.ProgramModule()).Il!;

    /// <summary>Every position of a short file, then a spread of positions over the rest (with the tail, where tables end).</summary>
    private static IEnumerable<int> Positions(int length)
    {
        for (int i = 0; i < Math.Min(length, 96); i++)
            yield return i;
        for (int i = 96; i < length; i += Math.Max(1, length / 64))
            yield return i;
        for (int i = Math.Max(96, length - 8); i < length; i++)
            yield return i;
    }

    /// <summary>What <paramref name="read"/> does with every truncated and damaged variant of <paramref name="valid"/>: the failures that are not the format exception.</summary>
    private static string Sweep(byte[] valid, Action<byte[]> read, Func<Exception, bool> expected)
    {
        var wrong = new List<string>();
        int refused = 0, accepted = 0;
        void Try(string what, byte[] bytes)
        {
            try
            {
                read(bytes);
                accepted++;
            }
            catch (Exception e) when (expected(e))
            {
                refused++;
                if (string.IsNullOrWhiteSpace(e.Message))
                    wrong.Add($"{what}: {e.GetType().Name} without a message");
            }
            catch (Exception e)
            {
                wrong.Add($"{what}: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
            }
        }
        foreach (int at in Positions(valid.Length))
        {
            Try($"cut at {at}", valid[..at]);
            foreach (byte value in new byte[] { 0x00, 0xFF, 0x7F, 0x88, (byte)(valid[at] ^ 0x10) })
            {
                var damaged = (byte[])valid.Clone();
                damaged[at] = value;
                Try($"byte {at} = {value:x2}", damaged);
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{valid.Length} bytes: {accepted} variants read, {refused} refused, {wrong.Count} wrong");
        return wrong.Count == 0 ? "" : $"{wrong.Count} wrong: " + string.Join("; ", wrong.Take(6));
    }

    private static bool Format(Exception e) => e is InvalidDataException;

    [Fact]
    public void Damaged_module_files_are_refused_with_one_exception()
    {
        var bytes = ModuleFile.Write(Il());
        string wrong = Sweep(bytes, b => ModuleFile.Read(b), Format);
        Check("every damaged .lvm reads or is an InvalidDataException with a message", wrong == "", wrong);
    }

    [Fact]
    public void Damaged_object_files_are_refused_with_one_exception()
    {
        var bytes = ObjectFile.Write(Object(App()), compress: false);
        string wrong = Sweep(bytes, b => ObjectFile.Read(b), Format);
        Check("every damaged uncompressed .lvo reads or is an InvalidDataException with a message", wrong == "", wrong);
        var packed = ObjectFile.Write(Object(Geom()), compress: true);
        wrong = Sweep(packed, b => ObjectFile.Read(b), Format);
        Check("every damaged compressed .lvo reads or is an InvalidDataException with a message", wrong == "", wrong);
    }

    [Fact]
    public void Damaged_ir_streams_are_refused_with_one_exception()
    {
        var bytes = IrEncoder.Encode(GenericsPrograms.ProgramModule());
        string wrong = Sweep(bytes, b => IrDecoder.Decode(b), Format);
        Check("every damaged IR stream reads or is an InvalidDataException with a message", wrong == "", wrong);
    }

    [Fact]
    public void Decoded_modules_have_their_indexes_checked()
    {
        var module = GenericsPrograms.ProgramModule();
        var badStruct = module with { Globals = [new GlobalDef("g", new StructType(module.Structs.Length + 5, []))] };
        Check("a struct id past the table is refused", Throws(() => IrDecoder.Decode(IrEncoder.Encode(badStruct)), "struct"));
        var function = module.Functions[0];
        var badBlock = module with { Functions = [function with { Blocks = [new BasicBlock([], new GotoTerminator(99))] }, .. module.Functions[1..]] };
        Check("a block target past the function's blocks is refused", Throws(() => IrDecoder.Decode(IrEncoder.Encode(badBlock)), "block"));
        var il = Il();
        var badEntry = il with { Entry = il.Functions.Length + 1 };
        Check("an entry past the functions is refused", Throws(() => ModuleFile.Read(ModuleFile.Write(badEntry)), "entry function"));
        var first = il.Functions[0];
        var badType = il with { Functions = [first with { LocalTypes = [il.Types.Length + 3, .. first.LocalTypes[1..]] }, .. il.Functions[1..]] };
        Check("a type token past the types is refused", Throws(() => ModuleFile.Read(ModuleFile.Write(badType)), "type"));
    }

    [Fact]
    public void Cli_names_the_damaged_file_and_the_problem()
    {
        string directory = TestEnvironment.TempFolder("damaged");
        var bytes = ModuleFile.Write(Il());
        string cut = Path.Combine(directory, "cut.lvm");
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);
        var (code, output) = Repository.Cli("run", cut);
        Check("larva run on a cut module: `larva: <file>: <message>`, exit 1", code == 1 && output.StartsWith($"larva: {cut}: ") && !output.Contains("   at "), $"exit {code}: {output.Trim()}");
        string damaged = Path.Combine(directory, "damaged.lvo");
        var obj = ObjectFile.Write(Object(App()), compress: false);
        obj[^1] ^= 0xFF;
        obj[obj.Length / 2] = 0xFF;
        File.WriteAllBytes(damaged, obj[..(obj.Length - 3)]);
        (code, output) = Repository.Cli("link", "-o", Path.Combine(directory, "out.lvm"), damaged);
        Check("larva link on a damaged object: `larva: <file>: <message>`, exit 1", code == 1 && output.StartsWith($"larva: {damaged}: ") && !output.Contains("   at "), $"exit {code}: {output.Trim()}");
    }

    private static bool Throws(Action action, string fragment)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException e)
        {
            return e.Message.Contains(fragment);
        }
    }
}
