using System.Text;
using Larva.IR;
using Larva.Native;
using Larva.Runtime.Vm;
using Larva.X64;
using LLVMSharp.Interop;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>
/// The direct x86-64 back end (no LLVM): the programs of the LLVM suite run from its own machine code and compared with
/// the VM, Win64 ABI corner cases, and its COFF objects read back and disassembled with LLVM's tools.
/// </summary>
public sealed unsafe class X64Tests : CheckedTest
{
    [Fact]
    public void Basic_and_generic_programs_run_like_the_vm()
    {
        var module = BasicProgram.Module();
        using var image = Load(module);
        var sum = (delegate* unmanaged<int, int>)image.Address("sum");
        var main = (delegate* unmanaged<int>)image.Address("main");
        Check("sum(10) = 45, same as the VM", sum(10) == 45 && Equals(Vm(module).Run("sum", 10), 45L));
        Check("main() = 5 (struct fields, &mut, calls)", main() == 5);
        Check("sum(100000) = 704982704 (i32 wraps like the VM)", sum(100000) == 704982704);

        using (var generics = Load(GenericsPrograms.ProgramModule()))
            Check("generics main() = 8", ((delegate* unmanaged<int>)generics.Address("main"))() == 8);
        using (var shared = Load(SharedGenericsPrograms.ProgramModule()))
            Check("shared-generics main() = size_of<f32> = 4", ((delegate* unmanaged<ulong>)shared.Address("main"))() == 4);
    }

    [Fact]
    public void Memory_modes_free_release_and_close_regions()
    {
        NativeRuntime.Reset();
        using (var image = Load(GcPrograms.BoxOnlyModule(), GcConfig.Manual))
        {
            Check("boxes() = 3", ((delegate* unmanaged<int>)image.Address("boxes"))() == 3);
            ((delegate* unmanaged<void>)image.Address("take"))();
            Check("raw() = 5", ((delegate* unmanaged<int>)image.Address("raw"))() == 5);
            Check("every box freed, no double frees", NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0 && NativeRuntime.Frees == NativeRuntime.Allocations,
                $"allocations {NativeRuntime.Allocations}, frees {NativeRuntime.Frees}, errors {NativeRuntime.Errors}");
        }
        NativeRuntime.Reset();
        using (var image = Load(NativePrograms.MaybeGive(), GcConfig.Manual))
        {
            var maybeGive = (delegate* unmanaged<int, void>)image.Address("maybe_give_int");
            maybeGive(1);
            maybeGive(0);
            Check("drop flags: freed exactly once on both paths", NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0 && NativeRuntime.Frees == 2,
                $"frees {NativeRuntime.Frees}, errors {NativeRuntime.Errors}");
        }
        NativeRuntime.Reset();
        using (var image = Load(NativePrograms.RcProgram(), GcConfig.RefCount))
        {
            Check("RC: rc_main() = 7", ((delegate* unmanaged<int>)image.Address("rc_main"))() == 7);
            Check("RC: every handle released", NativeRuntime.LiveGc == 0 && NativeRuntime.Errors == 0,
                $"live {NativeRuntime.LiveGc}, retains {NativeRuntime.Retains}, releases {NativeRuntime.Releases}");
        }
        NativeRuntime.Reset();
        using (var image = Load(NativePrograms.RegionProgram(), GcConfig.Arena))
        {
            Check("arena: in_region() = 4", ((delegate* unmanaged<int>)image.Address("in_region"))() == 4);
            Check("arena: region exit freed its objects", NativeRuntime.LiveGc == 0 && NativeRuntime.Frees == 1);
        }
    }

    [Fact]
    public void Arrays_index_free_and_eliminate_proven_bounds_checks()
    {
        NativeRuntime.Reset();
        var program = Compile(RuntimeFeaturePrograms.ArrayModule(), GcConfig.Manual);
        using (var image = program.Load())
        {
            Check("squares(1000) = 332833500", ((delegate* unmanaged<long, long>)image.Address("squares"))(1000) == 332833500);
            Check("Len(box [u8; 7]) = 7", ((delegate* unmanaged<long, ulong>)image.Address("len_of"))(7) == 7);
            Check("array boxes freed", NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0);
            Check("bounds-check elimination: squares' two loops unchecked, oob checked",
                program.BoundsChecksEliminated == 2 && program.BoundsChecks == 1, $"{program.BoundsChecksEliminated} eliminated, {program.BoundsChecks} kept");
        }
        NativeRuntime.Reset();
        using (var image = Load(RuntimeFeaturePrograms.GcArrayModule()))
        {
            Check("gc [gc Point; 20] in Default mode = 19", ((delegate* unmanaged<long, long>)image.Address("gc_array"))(20) == 19);
            // The runtimes' collector is not generational: the barrier the Default mode allows for is empty, so none is called.
            Check("element stores call no write barrier (the collector needs none)", NativeRuntime.Barriers == 0, $"barriers {NativeRuntime.Barriers}");
        }
    }

    /// <summary>Win64 calling convention: by-value and by-reference structs, hidden result pointer, stack and float arguments.</summary>
    [Fact]
    public void Win64_calling_convention_corner_cases()
    {
        using var image = Load(AbiProgram());
        Check("12-byte struct: returned through a hidden pointer, passed by reference: main3() = 1·1 + 2·2 + 3·3 = 14",
            ((delegate* unmanaged<int>)image.Address("main3"))() == 14);
        Check("8-byte struct by value in a register: swap_sum() = 21", ((delegate* unmanaged<int>)image.Address("swap_sum"))() == 21);
        var many = (delegate* unmanaged<long, long, long, long, long, long, long>)image.Address("many");
        Check("six i64 arguments (two on the stack), called from the test host", many(1, 2, 3, 4, 5, 6) == 1 + 4 + 9 + 16 + 25 + 36);
        Check("... and from Larva: call_many() = 91", ((delegate* unmanaged<long>)image.Address("call_many"))() == 91);
        var mix = (delegate* unmanaged<long, double, long, double, long, double>)image.Address("mix");
        Check("mixed integer / f64 arguments by position: mix(1, 0.5, 2, 0.25, 3) = 6.75", mix(1, 0.5, 2, 0.25, 3) == 6.75);
        var floats = (delegate* unmanaged<float, double>)image.Address("floats");
        Check("f32 → f64, arithmetic and compare: floats(1.5) = 4.25", floats(1.5f) == 4.25);
        var big = (delegate* unmanaged<ulong, double>)image.Address("u64_to_f64");
        Check("u64 ≥ 2^63 converts correctly", big(0x8000_0000_0000_0800UL) == 9.223372036854778E18 && big(5) == 5.0);
        var narrow = (delegate* unmanaged<int, int>)image.Address("narrow");
        Check("i8 wraps and sign-extends: narrow(200) = -56 · 2 = -112", narrow(200) == -112);
        var divide = (delegate* unmanaged<long, long, long>)image.Address("divide");
        Check("signed division: -7 / 2 = -3, MIN / -1 wraps without a trap", divide(-7, 2) == -3 && divide(long.MinValue, -1) == long.MinValue);
    }

    [Fact]
    public void Tracing_gc_finds_rbp_relative_stack_roots()
    {
        NativeRuntime.Reset();
        using var image = Load(NativePrograms.TracingProgram());
        NativeRuntime.StressGc = true;
        long churned = ((delegate* unmanaged<long, long>)image.Address("churn"))(50);
        Check("churn(50) reclaims its garbage", churned == 50 && NativeRuntime.Collections >= 50 && NativeRuntime.LiveGc <= 1,
            $"collections {NativeRuntime.Collections}, collected {NativeRuntime.Collected}, live {NativeRuntime.LiveGc}");
        int kept = ((delegate* unmanaged<long, int>)image.Address("keep"))(30);
        Check("keep(30) = 8: the caller's object survives collections in the callee", kept == 8 && NativeRuntime.Errors == 0,
            $"got {kept}, frames {NativeRuntime.LastFrames}, stack roots {NativeRuntime.LastStackRoots}");
        Check("both frames were walked", NativeRuntime.LastFrames >= 2, $"frames {NativeRuntime.LastFrames}");
        NativeRuntime.StressGc = false;
        NativeRuntime.Collect();
        Check("after returning, everything is garbage", NativeRuntime.LiveGc == 0);
        NativeRuntime.Reset();
    }

    [Fact]
    public void A_linked_program_runs_across_modules()
    {
        var result = X64Backend.Compile(LinkerPrograms.LinkedProgram());
        Check("geom + app compile", result.Success, result.ToString());
        if (!result.Success)
            return;
        using var image = result.Program!.Load();
        Check("app::main() = 7 across modules", ((delegate* unmanaged<int>)image.Address("app::main"))() == 7);
    }

    /// <summary>SEH with __C_specific_handler scope tables, against the VM's results (only paths that end up caught).</summary>
    [Fact]
    public void Seh_exceptions_with_scope_tables_match_the_vm()
    {
        var module = RuntimeFeaturePrograms.ExceptionModule();
        var vm = Vm(module);
        NativeRuntime.Reset();
        var program = Compile(module);
        var caller = program.Function("caller");
        Check("caller's unwind info has an exception handler and a scope table", (caller.UnwindInfo[0] >> 3 & 1) == 1
            && caller.UnwindFixups.Any(f => f.Symbol == "__C_specific_handler") && caller.UnwindFixups.Count(f => f.Symbol is null) % 4 == 0,
            $"{caller.UnwindFixups.Count(f => f.Symbol is null) / 4} scope entries");
        Check("thrower needs no scope table (owns nothing, catches nothing)", program.Function("thrower").UnwindFixups.Length == 0);
        using (var image = program.Load())
        {
            var call = (delegate* unmanaged<int, int>)image.Address("caller");
            Check("caller(5) = 10, no exception", call(5) == 10 && Equals(vm.Run("caller", 5), 10L));
            Check("caller(-3) = 3, caught i32 (same as the VM)", call(-3) == 3 && Equals(vm.Run("caller", -3), 3L));
            var catcher = (delegate* unmanaged<int, int>)image.Address("catcher");
            Check("catcher(-1) = -1, leaky's box freed by its cleanup landing", catcher(-1) == -1 && NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0,
                $"live {NativeRuntime.LiveHeap}, errors {NativeRuntime.Errors}");
            Check("catcher(1) = 1, normal path frees it too", catcher(1) == 1 && NativeRuntime.LiveHeap == 0);
            Check("resume re-raises to the outer handler: caller2(-4) = 96",
                ((delegate* unmanaged<int, int>)image.Address("caller2"))(-4) == 96 && Equals(vm.Run("caller2", -4), 96L));
            Check("a box received as argument is freed when the callee unwinds",
                ((delegate* unmanaged<int, int>)image.Address("owner"))(-1) == -1 && NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0,
                $"live {NativeRuntime.LiveHeap}, errors {NativeRuntime.Errors}");
            NativeRuntime.StressGc = true;
            int caught = ((delegate* unmanaged<int>)image.Address("catch_gc"))();
            NativeRuntime.StressGc = false;
            Check("catch (e: gc Point) = 9 under GC stress", caught == 9 && NativeRuntime.Collections > 0, $"got {caught}, collections {NativeRuntime.Collections}");
            for (int i = 0; i < 1000; i++)
                call(-i - 1);
            Check("1000 more exceptions: every payload cell and box freed", NativeRuntime.LiveHeap == 0 && NativeRuntime.Errors == 0,
                $"allocations {NativeRuntime.Allocations}, frees {NativeRuntime.Frees}, errors {NativeRuntime.Errors}");
        }

        NativeRuntime.Reset();
        using (var image = Load(RuntimeFeaturePrograms.ArenaExceptionModule(), GcConfig.Arena))
        {
            Check("unwinding closes the region it crosses", ((delegate* unmanaged<int>)image.Address("region_catcher"))() == -1
                && NativeRuntime.LiveGc == 0 && NativeRuntime.Errors == 0, $"live gc {NativeRuntime.LiveGc}, errors {NativeRuntime.Errors}");
        }

        var (_, symbols, relocations, error) = ReadObject(program.ToObject());
        Check("the object references __C_specific_handler and RaiseException; LLVM reads it", error is null
            && symbols.Contains("__C_specific_handler") && symbols.Contains("RaiseException"), error);
        NativeRuntime.Reset();
    }

    [Fact]
    public void Every_primitive_compiles()
    {
        var f = new FunctionBuilder("wide", IrType.I256);
        f.Assign(FunctionBuilder.ReturnPlace, Int(1, IrType.I256)).Return();
        var wide = X64Backend.Compile(new IrModule([], [f.Build()]));
        Check("i256 is compiled (wide integers are limbs in the frame)", !wide.Skipped.ContainsKey("wide") && wide.Error is null, wide.Error);
        var quad = new FunctionBuilder("quad", IrType.F128);
        quad.Assign(FunctionBuilder.ReturnPlace, new ConstOperand(0, IrType.F128)).Return();
        var soft = X64Backend.Compile(new IrModule([], [quad.Build()]));
        Check("f128 is compiled (soft-float routines in the runtime)", !soft.Skipped.ContainsKey("quad") && soft.Error is null, soft.Error);
    }

    /// <summary>The object file, read back with LLVM's object reader and disassembled with LLVM's disassembler.</summary>
    [Fact]
    public void Coff_objects_are_read_and_disassembled_by_llvm()
    {
        var module = BasicProgram.Module();
        var program = Compile(GcPrograms.ManagedModule(), GcConfig.Default);
        var bytes = program.ToObject();
        string directory = TestEnvironment.TempFolder("x64-coff");
        File.WriteAllBytes(Path.Combine(directory, "managed-x64.obj"), bytes);
        Check("COFF header: machine 0x8664", BitConverter.ToUInt16(bytes, 0) == 0x8664, $"{bytes.Length} bytes");

        var (sections, symbols, relocations, error) = ReadObject(bytes);
        Check("LLVM reads the object", error is null, error);
        Check("sections .text, .xdata, .pdata, .llvm_stackmaps", sections.SequenceEqual([".text", ".xdata", ".pdata", ".llvm_stackmaps"]));
        Check("function symbols and undefined runtime symbols", symbols.Contains("boxes") && symbols.Contains("spin") && symbols.Contains("larva_gc_new") && symbols.Contains("larva_poll"),
            string.Join(", ", symbols));
        Check("REL32 calls, ADDR32NB function table, ADDR64 stack maps",
            relocations.Contains("IMAGE_REL_AMD64_REL32") && relocations.Contains("IMAGE_REL_AMD64_ADDR32NB") && relocations.Contains("IMAGE_REL_AMD64_ADDR64"));

        var basics = Compile(module, GcConfig.Default);
        var sum = basics.Function("sum");
        var (listing, invalid) = Disassemble(basics.Code, sum.Offset, sum.Size);
        Check("LLVM decodes every instruction of the generated code", invalid == 0 && Disassemble(basics.Code, 0, basics.Code.Length).Invalid == 0,
            $"{invalid} undecodable");
        Check("prologue push rbp / mov rbp, rsp / sub rsp; lea rsp, [rbp] epilogue",
            listing[0].Contains("push") && listing[1].Contains("mov") && listing[2].Contains("sub") && listing.Any(l => l.Contains("lea") && l.Contains("rsp")),
            string.Join(" / ", listing.Take(24)));
    }

    /// <summary>The CLI: the linker tests' objects (geom.lvo, app.lvo) compiled by the x64 back end into a COFF object.</summary>
    [Fact]
    public void Larva_native_x64_compiles_object_files()
    {
        string directory = TestEnvironment.TempFolder("x64-native");
        var (geom, app) = LinkerPrograms.SaveObjects(directory);
        string output = Path.Combine(directory, "program-x64.obj");
        var (code, text) = Repository.Cli("native", "--backend", "x64", "-o", output, geom, app);
        Check("larva native --backend x64 geom.lvo app.lvo", code == 0 && File.Exists(output) && ReadObject(File.ReadAllBytes(output)).Symbols.Contains("app::main"),
            text.Trim());
    }

    // ── ABI program ────────────────────────────────────────────

    private static IrModule AbiProgram()
    {
        // struct Triple { a: i32, b: i32, c: i32 } (12 bytes) and struct Pair { x: i32, y: i32 } (8 bytes)
        var triple = IrType.Struct(0);
        var pair = IrType.Struct(1);
        StructDef[] structs = [new("Triple", IrType.I32, IrType.I32, IrType.I32), new("Pair", IrType.I32, IrType.I32)];
        var functions = new List<IrFunction>();

        // fn make3(a, b, c: i32) -> Triple
        var make3 = new FunctionBuilder("make3", triple);
        int ma = make3.Argument(IrType.I32, "a"), mb = make3.Argument(IrType.I32, "b"), mc = make3.Argument(IrType.I32, "c");
        make3.Assign(FunctionBuilder.ReturnPlace.Field(0), Copy(new Place(ma)))
             .Assign(FunctionBuilder.ReturnPlace.Field(1), Copy(new Place(mb)))
             .Assign(FunctionBuilder.ReturnPlace.Field(2), Copy(new Place(mc))).Return();
        functions.Add(make3.Build());                                                           // 0

        // fn dot3(t: Triple) -> i32 { t.a*t.a + t.b*t.b + t.c*t.c }
        var dot3 = new FunctionBuilder("dot3", IrType.I32);
        int t = dot3.Argument(triple, "t");
        int s1 = dot3.Local(IrType.I32, mutable: true), s2 = dot3.Local(IrType.I32, mutable: true), s3 = dot3.Local(IrType.I32, mutable: true);
        dot3.Assign(s1, Rvalue.Binary(BinaryOp.Mul, Copy(new Place(t).Field(0)), Copy(new Place(t).Field(0))))
            .Assign(s2, Rvalue.Binary(BinaryOp.Mul, Copy(new Place(t).Field(1)), Copy(new Place(t).Field(1))))
            .Assign(s3, Rvalue.Binary(BinaryOp.Mul, Copy(new Place(t).Field(2)), Copy(new Place(t).Field(2))))
            .Assign(s1, Rvalue.Binary(BinaryOp.Add, Copy(s1), Copy(s2)))
            .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(s1), Copy(s3))).Return();
        functions.Add(dot3.Build());                                                            // 1

        // fn main3() -> i32 { dot3(make3(1, 2, 3)) }
        var main3 = new FunctionBuilder("main3", IrType.I32);
        int made = main3.Local(triple, "t");
        int b1 = main3.NewBlock(), b2 = main3.NewBlock();
        main3.Call(0, [Int(1), Int(2), Int(3)], made, b1);
        main3.At(b1).Call(1, [Move(new Place(made))], FunctionBuilder.ReturnPlace, b2);
        main3.At(b2).Return();
        functions.Add(main3.Build());                                                           // 2

        // fn swap(p: Pair) -> Pair { Pair { x: p.y, y: p.x } }; fn swap_sum() -> i32 { let q = swap(Pair{1, 20}); q.x + q.y }
        var swap = new FunctionBuilder("swap", pair);
        int p = swap.Argument(pair, "p");
        swap.Assign(FunctionBuilder.ReturnPlace.Field(0), Copy(new Place(p).Field(1)))
            .Assign(FunctionBuilder.ReturnPlace.Field(1), Copy(new Place(p).Field(0))).Return();
        functions.Add(swap.Build());                                                            // 3
        var swapSum = new FunctionBuilder("swap_sum", IrType.I32);
        int sp = swapSum.Local(pair, "p", mutable: true), sq = swapSum.Local(pair, "q");
        int after = swapSum.NewBlock();
        swapSum.Assign(new Place(sp).Field(0), Int(1)).Assign(new Place(sp).Field(1), Int(20))
               .Call(3, [Move(new Place(sp))], sq, after);
        swapSum.At(after).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(new Place(sq).Field(0)), Copy(new Place(sq).Field(1)))).Return();
        functions.Add(swapSum.Build());                                                         // 4

        // fn many(a..f: i64) -> i64 { a*1 + b*2 + c*3 + d*4 + e*5 + f*6 }
        var many = new FunctionBuilder("many", IrType.I64);
        var args = Enumerable.Range(0, 6).Select(i => many.Argument(IrType.I64, $"a{i}")).ToArray();
        int acc = many.Local(IrType.I64, "acc", mutable: true), term = many.Local(IrType.I64, mutable: true);
        many.Assign(acc, Int(0, IrType.I64));
        for (int i = 0; i < 6; i++)
            many.Assign(term, Rvalue.Binary(BinaryOp.Mul, Copy(new Place(args[i])), Int(i + 1, IrType.I64)))
                .Assign(acc, Rvalue.Binary(BinaryOp.Add, Copy(acc), Copy(term)));
        many.Assign(FunctionBuilder.ReturnPlace, Copy(acc)).Return();
        functions.Add(many.Build());                                                            // 5
        var callMany = new FunctionBuilder("call_many", IrType.I64);
        int done = callMany.NewBlock();
        callMany.Call(5, [.. Enumerable.Range(1, 6).Select(i => Int(i, IrType.I64))], FunctionBuilder.ReturnPlace, done);
        callMany.At(done).Return();
        functions.Add(callMany.Build());                                                        // 6

        // fn mix(a: i64, x: f64, b: i64, y: f64, c: i64) -> f64 { (a + b + c) as f64 + x + y }
        var mix = new FunctionBuilder("mix", IrType.F64);
        int xa = mix.Argument(IrType.I64, "a"), xx = mix.Argument(IrType.F64, "x"), xb = mix.Argument(IrType.I64, "b"),
            xy = mix.Argument(IrType.F64, "y"), xc = mix.Argument(IrType.I64, "c");
        int isum = mix.Local(IrType.I64, mutable: true), fsum = mix.Local(IrType.F64, mutable: true);
        mix.Assign(isum, Rvalue.Binary(BinaryOp.Add, Copy(new Place(xa)), Copy(new Place(xb))))
           .Assign(isum, Rvalue.Binary(BinaryOp.Add, Copy(isum), Copy(new Place(xc))))
           .Assign(fsum, Rvalue.Cast(Copy(isum), IrType.F64))
           .Assign(fsum, Rvalue.Binary(BinaryOp.Add, Copy(fsum), Copy(new Place(xx))))
           .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(fsum), Copy(new Place(xy)))).Return();
        functions.Add(mix.Build());                                                             // 7

        // fn floats(x: f32) -> f64 { let d = x as f64 * 2.5; if d > 3.0 { d + 0.5 } else { 0.0 } }  — 1.5 → 3.75 + 0.5
        var floats = new FunctionBuilder("floats", IrType.F64);
        int fx = floats.Argument(IrType.F32, "x");
        int fd = floats.Local(IrType.F64, "d", mutable: true), fc = floats.Local(IrType.Bool, mutable: true);
        int big = floats.NewBlock(), small = floats.NewBlock();
        floats.Assign(fd, Rvalue.Cast(Copy(new Place(fx)), IrType.F64))
              .Assign(fd, Rvalue.Binary(BinaryOp.Mul, Copy(fd), Float(2.5)))
              .Assign(fc, Rvalue.Binary(BinaryOp.Gt, Copy(fd), Float(3.0))).If(Copy(fc), big, small);
        floats.At(big).Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(fd), Float(0.5))).Return();
        floats.At(small).Assign(FunctionBuilder.ReturnPlace, Float(0.0)).Return();
        functions.Add(floats.Build());                                                          // 8

        var toFloat = new FunctionBuilder("u64_to_f64", IrType.F64);
        int u = toFloat.Argument(IrType.U64, "u");
        toFloat.Assign(FunctionBuilder.ReturnPlace, Rvalue.Cast(Copy(new Place(u)), IrType.F64)).Return();
        functions.Add(toFloat.Build());                                                         // 9

        // fn narrow(n: i32) -> i32 { let b = n as i8; (b * 2) as i32 }   — wraps in i8? no: i8 * 2 wraps too
        var narrow = new FunctionBuilder("narrow", IrType.I32);
        int nn = narrow.Argument(IrType.I32, "n");
        int nb = narrow.Local(IrType.I8, "b", mutable: true), nw = narrow.Local(IrType.I32, mutable: true);
        narrow.Assign(nb, Rvalue.Cast(Copy(new Place(nn)), IrType.I8))
              .Assign(nw, Rvalue.Cast(Copy(nb), IrType.I32))
              .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Mul, Copy(nw), Int(2))).Return();
        functions.Add(narrow.Build());                                                          // 10

        var divide = new FunctionBuilder("divide", IrType.I64);
        int da = divide.Argument(IrType.I64, "a"), db = divide.Argument(IrType.I64, "b");
        divide.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Div, Copy(new Place(da)), Copy(new Place(db)))).Return();
        functions.Add(divide.Build());                                                          // 11

        return new IrModule(structs, [.. functions]);
    }

    // ── LLVM's object reader and disassembler ──────────────────

    private static (List<string> Sections, List<string> Symbols, List<string> Relocations, string? Error) ReadObject(byte[] bytes)
    {
        List<string> sections = [], symbols = [], relocations = [];
        var name = "object\0"u8.ToArray();
        fixed (byte* data = bytes)
        fixed (byte* bufferName = name)
        {
            var buffer = LLVM.CreateMemoryBufferWithMemoryRangeCopy((sbyte*)data, (nuint)bytes.Length, (sbyte*)bufferName);
            sbyte* error;
            var binary = LLVM.CreateBinary(buffer, null, &error);
            if (binary == null)
                return (sections, symbols, relocations, new string(error));
            var section = LLVM.ObjectFileCopySectionIterator(binary);
            for (; LLVM.ObjectFileIsSectionIteratorAtEnd(binary, section) == 0; LLVM.MoveToNextSection(section))
            {
                sections.Add(new string(LLVM.GetSectionName(section)));
                var relocation = LLVM.GetRelocations(section);
                for (; LLVM.IsRelocationIteratorAtEnd(section, relocation) == 0; LLVM.MoveToNextRelocation(relocation))
                {
                    // The C API returns the name without a terminator: keep the known name it starts with.
                    var typeName = new string(LLVM.GetRelocationTypeName(relocation));
                    relocations.Add(new[] { "IMAGE_REL_AMD64_ADDR32NB", "IMAGE_REL_AMD64_ADDR64", "IMAGE_REL_AMD64_REL32" }
                        .FirstOrDefault(typeName.StartsWith) ?? typeName);
                }
                LLVM.DisposeRelocationIterator(relocation);
            }
            LLVM.DisposeSectionIterator(section);
            var symbol = LLVM.ObjectFileCopySymbolIterator(binary);
            for (; LLVM.ObjectFileIsSymbolIteratorAtEnd(binary, symbol) == 0; LLVM.MoveToNextSymbol(symbol))
                symbols.Add(new string(LLVM.GetSymbolName(symbol)));
            LLVM.DisposeSymbolIterator(symbol);
            LLVM.DisposeBinary(binary);
            LLVM.DisposeMemoryBuffer(buffer);
        }
        return (sections, symbols, relocations, null);
    }

    private static (List<string> Lines, int Invalid) Disassemble(byte[] code, int start, int length)
    {
        LLVM.InitializeX86Disassembler();
        var triple = "x86_64-pc-windows-msvc\0"u8.ToArray();
        var lines = new List<string>();
        int invalid = 0;
        fixed (byte* tripleText = triple)
        fixed (byte* bytes = code)
        {
            var disassembler = LLVM.CreateDisasm((sbyte*)tripleText, null, 0, null, null);
            var text = stackalloc sbyte[256];
            int offset = start;
            while (offset < start + length)
            {
                var size = (int)LLVM.DisasmInstruction(disassembler, bytes + offset, (ulong)(start + length - offset), (ulong)offset, text, 256);
                if (size == 0)
                {
                    invalid++;
                    offset++;
                    continue;
                }
                var hex = Convert.ToHexString(code, offset, size).ToLowerInvariant();
                lines.Add($"  {offset - start,4:x}: {hex,-22} {new string(text).Trim()}");
                offset += size;
            }
            LLVM.DisasmDispose(disassembler);
        }
        return (lines, invalid);
    }

    // ── Helpers ────────────────────────────────────────────────

    private static X64Program Compile(IrModule module, GcConfig? gc = null)
    {
        var result = X64Backend.Compile(module, new X64Options { Compilation = CompilationOptions.Default with { Gc = gc ?? GcConfig.Default } });
        if (!result.Success)
            throw new InvalidOperationException(result.ToString());
        return result.Program!;
    }

    private static X64Image Load(IrModule module, GcConfig? gc = null) => Compile(module, gc).Load();

    private static Interpreter Vm(IrModule module) => new(Compiler.Compile(module).Il!);
}
