using System.Diagnostics;
using Larva.IR;
using Larva.IR.Linking;
using Larva.Wasm;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>
/// Larva's own WebAssembly back end (no LLVM): programs linked with their entry wrappers, compiled to a module, and run
/// by Node through the JavaScript runtime (<c>larva_rt.js</c>), which also validates the module when it instantiates it.
/// </summary>
public sealed class WasmTests : CheckedTest
{
    /// <summary>
    /// Larva's rule for every back end: a float out of the integer's range gives the nearest limit and NaN gives 0
    /// (<c>i64.trunc_sat</c>, then narrower targets clamped instead of wrapped: 1e30 as i32 was -1).
    /// </summary>
    [Fact]
    public void Float_to_integer_conversions_saturate()
    {
        var f = new FunctionBuilder("main", IrType.Unit);
        int bits = f.Local(IrType.I64, "bits", mutable: true);
        int value = f.Local(IrType.F64, "value", mutable: true);
        int single = f.Local(IrType.F32, "single", mutable: true);
        foreach (double x in new[] { 1e30, -1e30, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 3.9, -3.9 })
        {
            f.Assign(bits, Int(BitConverter.DoubleToInt64Bits(x), IrType.I64))
             .Assign(value, new IntrinsicRvalue("f64.from_bits", [Copy(bits)])) // a runtime call: nothing folds the conversions
             .Assign(single, Rvalue.Cast(Copy(value), IrType.F32));
            foreach (var type in new[] { IrType.I8, IrType.I16, IrType.I32, IrType.I64, IrType.U8, IrType.U16, IrType.U32, IrType.U64 })
            {
                int fromDouble = f.Temp(type), fromSingle = f.Temp(type);
                f.Assign(fromDouble, Rvalue.Cast(Copy(value), type)).Assign(fromSingle, Rvalue.Cast(Copy(single), type));
                Print(f, type.IsSigned ? "print.i64" : "print.u64", Rvalue.Cast(Move(fromDouble), type.IsSigned ? IrType.I64 : IrType.U64));
                Print(f, type.IsSigned ? "print.i64" : "print.u64", Rvalue.Cast(Move(fromSingle), type.IsSigned ? IrType.I64 : IrType.U64));
            }
        }
        f.Return();
        var (exit, output, errors) = Node(new IrModule([], [f.Build()]) { Name = "sat" });
        Check("the program runs", exit == 0, errors);
        var expected = new List<string>();
        foreach (double x in new[] { 1e30, -1e30, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 3.9, -3.9 })
        {
            expected.AddRange([Sat(x, -128, 127), Sat(x, -128, 127), Sat(x, -32768, 32767), Sat(x, -32768, 32767), Sat(x, int.MinValue, int.MaxValue), Sat(x, int.MinValue, int.MaxValue),
                Sat(x, long.MinValue, long.MaxValue), Sat(x, long.MinValue, long.MaxValue), Sat(x, 0, 255), Sat(x, 0, 255), Sat(x, 0, 65535), Sat(x, 0, 65535),
                Sat(x, 0, uint.MaxValue), Sat(x, 0, uint.MaxValue), SatU64(x), SatU64(x)]);
        }
        Check("every width saturates like LLVM (NaN → 0, ±1e30 and infinities → the limits, 3.9 → 3)", Lines(output).SequenceEqual(expected),
            $"got {string.Join(" ", Lines(output))}\nwant {string.Join(" ", expected)}");
    }

    /// <summary>
    /// Intrinsic arguments are adapted to each import's signature: addresses (pointers, gc handles cast to pointers,
    /// function values) travel as i32 and an i32 result is widened; a pointer passed as an i64 made the module invalid.
    /// </summary>
    [Fact]
    public void Intrinsic_calls_match_the_runtime_imports()
    {
        var f = new FunctionBuilder("main", IrType.Unit) { Unsafe = true };
        int p = f.Local(IrType.PtrMut(IrType.I32), "p");
        int q = f.Local(IrType.PtrMut(IrType.I64), "q");
        int r = f.Local(IrType.PtrMut(IrType.I64), "r");
        int g = f.Local(IrType.Gc(IrType.I64), "g");
        int old = f.Temp(IrType.I32);
        int unit = f.Temp(IrType.Unit);
        f.Assign(p, Rvalue.Alloc(Int(5)))
         .Assign(old, new IntrinsicRvalue("atomic.add.i32", [Copy(p), Int(10)]));
        Print(f, "print.i64", Rvalue.Cast(Move(old), IrType.I64));                                 // 5: an i32 result widened
        Print(f, "print.i64", Rvalue.Cast(Copy(new Place(p).Deref()), IrType.I64));                // 15
        var bytes = IrType.PtrMut(IrType.U8);
        int qBytes = f.Temp(bytes), rBytes = f.Temp(bytes), gBytes = f.Temp(bytes);
        f.Assign(q, Rvalue.Alloc(Int(7, IrType.I64)))
         .Assign(r, Rvalue.Alloc(Int(0, IrType.I64)))
         .Assign(qBytes, Rvalue.Cast(Copy(q), bytes))
         .Assign(rBytes, Rvalue.Cast(Copy(r), bytes))
         .Assign(unit, new IntrinsicRvalue("mem.copy", [Copy(rBytes), Copy(qBytes), Int(8, IrType.I64)]));
        Print(f, "print.i64", Copy(new Place(r).Deref()));                                                   // 7: pointers passed as i32
        Print(f, "print.i64", new IntrinsicRvalue("mem.cmp", [Copy(rBytes), Copy(qBytes), Int(8, IrType.I64)])); // 0
        f.Assign(g, Rvalue.Gc(Int(9, IrType.I64)))
         .Assign(gBytes, Rvalue.Cast(Copy(g), bytes));
        Print(f, "print.i64", new IntrinsicRvalue("gc.domain", [Copy(gBytes)]));                              // 0: a handle cast to a pointer
        f.Free(p).Free(q).Free(r).Return();
        var (exit, output, errors) = Node(new IrModule([], [f.Build()]) { Name = "imports" });
        Check("the module is valid and runs (node instantiates it)", exit == 0, errors);
        Check("atomic.add.i32, mem.copy, mem.cmp and gc.domain give 5 15 7 0 0", Lines(output).SequenceEqual(["5", "15", "7", "0", "0"]), string.Join(" ", Lines(output)));
    }

    /// <summary>Float `%` is fmod (the dividend's sign), through the runtime's `larva_f64_rem`; singles go through doubles exactly.</summary>
    [Fact]
    public void Float_remainder_is_fmod()
    {
        var f = new FunctionBuilder("main", IrType.Unit);
        int bits = f.Local(IrType.I64, "bits", mutable: true);
        int a = f.Local(IrType.F64, "a", mutable: true), b = f.Local(IrType.F64, "b", mutable: true);
        int sa = f.Local(IrType.F32, "sa", mutable: true), sb = f.Local(IrType.F32, "sb", mutable: true);
        var pairs = new (double A, double B)[] { (7.5, 2.0), (-7.5, 2.0), (7.5, -2.0), (1e300, 3.0), (5.0, 0.0), (5.5, double.PositiveInfinity), (0.1, 0.03) };
        foreach (var (x, y) in pairs)
        {
            f.Assign(bits, Int(BitConverter.DoubleToInt64Bits(x), IrType.I64)).Assign(a, new IntrinsicRvalue("f64.from_bits", [Copy(bits)]))
             .Assign(bits, Int(BitConverter.DoubleToInt64Bits(y), IrType.I64)).Assign(b, new IntrinsicRvalue("f64.from_bits", [Copy(bits)]))
             .Assign(sa, Rvalue.Cast(Copy(a), IrType.F32)).Assign(sb, Rvalue.Cast(Copy(b), IrType.F32));
            Print(f, "print.f64", Rvalue.Binary(BinaryOp.Rem, Copy(a), Copy(b)));
            Print(f, "print.f64", Rvalue.Cast(Rem32(f, sa, sb), IrType.F64));
        }
        f.Return();
        var (exit, output, errors) = Node(new IrModule([], [f.Build()]) { Name = "rem" });
        Check("the program runs", exit == 0, errors);
        var expected = pairs.SelectMany(p => new[] { Shown(p.A % p.B), Shown((float)p.A % (float)p.B) }).ToArray();
        Check("f64 and f32 remainders have the dividend's sign (7.5 % 2 = 1.5, -7.5 % 2 = -1.5, 7.5 % -2 = 1.5, 1e300 % 3 = 0, x % 0 = nan, 5.5 % inf = 5.5)",
            Lines(output).SequenceEqual(expected), $"got {string.Join(" ", Lines(output))}\nwant {string.Join(" ", expected)}");
    }

    /// <summary>
    /// The JavaScript runtime refuses a block the memory cannot hold (an array of 2^32 bytes was reserved as one 16-byte
    /// block, by 32-bit truncation of its size, and corrupted the heap) through the panic path: exit code 101.
    /// </summary>
    [Fact]
    public void Huge_arrays_are_refused_as_out_of_memory()
    {
        IrModule Program(long length)
        {
            var f = new FunctionBuilder("main", IrType.Unit);
            int array = f.Local(IrType.Gc(new ArrayType(IrType.I64)), "array", mutable: true);
            int last = f.Local(IrType.I64, "last");
            int count = f.Temp(IrType.I64);
            f.Assign(array, Rvalue.NewArray(AllocKind.Gc, IrType.I64, Int(length, IrType.I64)))
             .Assign(count, Rvalue.Len(new Place(array).Deref()));
            Print(f, "print.i64", Rvalue.Cast(Move(count), IrType.I64));
            f.Assign(last, Int(length - 1, IrType.I64))
             .Assign(new Place(array).Deref().Index(last), Int(3, IrType.I64));
            Print(f, "print.i64", Copy(new Place(array).Deref().Index(last)));
            f.Return();
            return new IrModule([], [f.Build()]) { Name = "huge" };
        }
        var (exit, output, errors) = Node(Program(1L << 29)); // 4 GB of elements
        Check("2^29 i64 (4 GB) is refused: exit code 101, larva: out of memory", exit == 101 && errors.Contains("larva: out of memory") && output.Trim().Length == 0,
            $"exit {exit}, output {output}, errors {errors}");
        (exit, output, errors) = Node(Program(3_000_000)); // 24 MB: grows the memory
        Check("3,000,000 i64 (24 MB) is allocated and its last element written", exit == 0 && Lines(output).SequenceEqual(["3000000", "3"]), $"exit {exit}, {output} {errors}");
    }

    private static Operand Rem32(FunctionBuilder f, int left, int right)
    {
        int temp = f.Temp(IrType.F32);
        f.Assign(temp, Rvalue.Binary(BinaryOp.Rem, Copy(left), Copy(right)));
        return Move(temp);
    }

    /// <summary>How the runtime prints a double (15 significant digits, "nan").</summary>
    private static string Shown(double value) => double.IsNaN(value) ? "nan" : value.ToString("G15", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();

    private static string Sat(double x, long min, long max) => double.IsNaN(x) ? "0" : x >= max ? max.ToString() : x <= min ? min.ToString() : ((long)Math.Truncate(x)).ToString();
    private static string SatU64(double x) => double.IsNaN(x) ? "0" : x >= ulong.MaxValue ? ulong.MaxValue.ToString() : x <= 0 ? "0" : ((ulong)Math.Truncate(x)).ToString();

    // ── Helpers ────────────────────────────────────────────────────

    internal static void Print(FunctionBuilder f, string intrinsic, Rvalue value)
    {
        int temp = f.Temp(intrinsic == "print.u64" ? IrType.U64 : intrinsic == "print.f64" ? IrType.F64 : IrType.I64);
        int unit = f.Temp(IrType.Unit);
        f.Assign(temp, value).Assign(unit, new IntrinsicRvalue(intrinsic, [Move(temp)])).Assign(unit, new IntrinsicRvalue("print.line", []));
    }

    internal static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

    /// <summary>The module linked as a program, compiled by the wasm back end, written next to its runtime and run by Node.</summary>
    internal static (int Exit, string Output, string Errors) Node(IrModule module, GcConfig? gc = null, params string[] arguments)
    {
        var options = new CompilationOptions { Gc = gc ?? GcConfig.Default, BorrowCheck = BorrowCheckMode.Off };
        var compiled = Compiler.CompileObject(module, options);
        if (!compiled.Success)
            throw new InvalidOperationException(string.Join("\n", compiled.TypeErrors.Select(e => e.ToString()).Concat(compiled.Diagnostics.Select(d => d.ToString()))));
        var linked = Linker.Link([compiled.Object!], new LinkOptions { Entry = "main", Compilation = options });
        if (!linked.Success)
            throw new InvalidOperationException(string.Join("\n", linked.Errors));
        var result = WasmBackend.Compile(linked, new WasmOptions { Compilation = options });
        if (!result.Success)
            throw new InvalidOperationException(result.ToString());
        string folder = TestEnvironment.TempFolder("wasm");
        string js = result.Program!.Write(Path.Combine(folder, module.Name + ".wasm"))[1];
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(js);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("node is not installed"); return default; }
        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(120_000))
                process.Kill();
            return (process.ExitCode, output, errors.Result);
        }
    }
}
