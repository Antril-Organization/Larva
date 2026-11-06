using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Encoding;
using Larva.IR.Lowering;
using Larva.Runtime.Vm;
using static Larva.IR.Operand;
using static Larva.Tests.RuntimeFeaturePrograms;

namespace Larva.Tests;

/// <summary>Module files and the CLI, arrays, and exceptions — run on the interpreter.</summary>
public sealed class RuntimeFeatureTests : CheckedTest
{
    // ── Module files and CLI ───────────────────────────────────────

    [Fact]
    public void Module_files_round_trip_and_reject_bad_headers()
    {
        var il = Compile(GenericsPrograms.ProgramModule());
        var bytes = ModuleFile.Write(il);
        var loaded = ModuleFile.Read(bytes);
        Check("write / read round trip", loaded.ToString() == il.ToString(), $"{bytes.Length} bytes");
        Check("loaded module runs: main() = 8", Equals(new Interpreter(loaded).Run("main"), 8L));
        Check("bad header is rejected", Throws<InvalidDataException>(() => ModuleFile.Read("NOPE"u8)));
    }

    [Fact]
    public void Cli_runs_and_disassembles_module_files()
    {
        string directory = TestEnvironment.TempFolder("modules");
        string generics = Path.Combine(directory, "generics" + ModuleFile.Extension);
        ModuleFile.Save(Compile(GenericsPrograms.ProgramModule()), generics);
        var (code, output) = Repository.Cli("run", generics);
        Check("larva run generics.lvm prints 8", code == 0 && output.Trim() == "8", $"exit {code}: {output.Trim()}");
        (code, output) = Repository.Cli("dis", generics);
        Check("larva dis shows the entry and the code", code == 0 && output.Contains(".entry  fn.0 (main)") && output.Contains("call"), $"exit {code}");

        string exceptions = Path.Combine(directory, "exceptions" + ModuleFile.Extension);
        ModuleFile.Save(Compile(ExceptionModule(), entry: "caller"), exceptions);
        (code, output) = Repository.Cli("run", exceptions, "-3");
        Check("larva run exceptions.lvm -3 prints 3 (caught)", code == 0 && output.Trim() == "3", $"exit {code}: {output.Trim()}");

        File.WriteAllBytes(Path.Combine(directory, "junk.lvm"), [1, 2, 3]);
        (code, _) = Repository.Cli("run", Path.Combine(directory, "junk.lvm"));
        Check("larva run on a non-module fails cleanly", code == 1);
    }

    // ── Arrays ─────────────────────────────────────────────────────

    [Fact]
    public void Box_arrays_index_measure_free_and_check_bounds()
    {
        var vm = Vm(ArrayModule());
        Check("squares(10) = 285", Equals(vm.Run("squares", 10), 285L));
        Check("the array box was freed", vm.Heap.Live.Count(a => a.Kind == AllocationKind.Heap) == 0);
        Check("Len(box [u8; 7]) = 7", Equals(vm.Run("len_of", 7), 7UL));
        Check("out of bounds is detected", Throws<VmException>(() => vm.Run("oob"), "out of bounds"));
    }

    [Fact]
    public void Gc_arrays_keep_elements_alive_through_write_barriers()
    {
        var gcVm = Vm(GcArrayModule(), stress: true);
        Check("gc [gc Point; 20]: elements survive stress collections", Equals(gcVm.Run("gc_array", 20), 19L), gcVm.Stats.ToString());
        Check("element stores go through the write barrier", gcVm.Stats.Barriers == 20);
    }

    [Fact]
    public void Arrays_of_boxes_and_bool_indices_are_rejected()
    {
        var copyOnly = new FunctionBuilder("box_array", IrType.Unit);
        int a = copyOnly.Local(IrType.Box(new ArrayType(IrType.Box(IrType.I32))), "a");
        copyOnly.Assign(a, Rvalue.NewArray(AllocKind.Box, IrType.Box(IrType.I32), Int(1))).Return();
        ExpectType([copyOnly.Build()], TypeErrorKind.TypeMismatch, "array of boxes is rejected (elements start zeroed)");

        var badIndex = new FunctionBuilder("bad_index", IrType.I32);
        int arr = badIndex.Local(IrType.Box(new ArrayType(IrType.I32)), "arr");
        int flag = badIndex.Local(IrType.Bool, "flag");
        badIndex.Assign(arr, Rvalue.NewArray(AllocKind.Box, IrType.I32, Int(1))).Assign(flag, Bool(true))
            .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(arr).Deref().Index(flag))).Return();
        ExpectType([badIndex.Build()], TypeErrorKind.TypeMismatch, "bool index is rejected");
    }

    // ── Exceptions ─────────────────────────────────────────────────

    [Fact]
    public void Exceptions_are_caught_by_type_unwind_owned_memory_and_resume()
    {
        var module = ExceptionModule();
        var bytes = IrEncoder.Encode(module);
        Check("IR round trip with handlers", IrDecoder.Decode(bytes).ToString() == module.ToString());

        var il = Compile(module, GcConfig.Default);
        var vm = new Interpreter(il, options: new InterpreterOptions { GcStress = true });
        Check("caller(5) = 10 (no exception)", Equals(vm.Run("caller", 5), 10L));
        Check("caller(-3) = 3 (caught i32)", Equals(vm.Run("caller", -3), 3L));
        Check("thrower(-1) unhandled", Throws<UnhandledException>(() => vm.Run("thrower", -1), "-1"));
        Check("catch bool does not catch an i32", Throws<UnhandledException>(() => vm.Run("caller_bool", -2), "-2"));
        Check("catcher(-1) = -1, the box in leaky was freed while unwinding",
            Equals(vm.Run("catcher", -1), -1L) && Leaks(vm) == 0);
        Check("catcher(1) = 1, normal path frees it too", Equals(vm.Run("catcher", 1), 1L) && Leaks(vm) == 0);
        Check("resume re-raises to the outer handler: caller2(-4) = 96", Equals(vm.Run("caller2", -4), 96L));
        Check("a box received as argument is freed when the callee unwinds", Equals(vm.Run("owner", -1), -1L) && Leaks(vm) == 0);
        Check("gc exception survives stress collections after being caught", Equals(vm.Run("catch_gc"), 9L), vm.Stats.ToString());
    }

    [Fact]
    public void Unwinding_closes_the_arena_region_it_crosses()
    {
        // Regions only exist in arena mode: thrower, region_throw, region_catcher.
        var arena = new Interpreter(Compile(ArenaExceptionModule(), GcConfig.Arena));
        Check("unwinding closes the region it crosses", Equals(arena.Run("region_catcher"), -1L)
            && arena.Heap.Live.Count(a => a.Kind == AllocationKind.Gc) == 0);
    }

    [Fact]
    public void Misplaced_handlers_and_thrown_local_references_are_rejected()
    {
        var notHandler = new FunctionBuilder("caught_outside", IrType.I32);
        notHandler.Assign(FunctionBuilder.ReturnPlace, Rvalue.Caught).Return();
        ExpectType([notHandler.Build()], TypeErrorKind.InvalidHandler, "`caught` outside a handler");

        var shared = new FunctionBuilder("handler_also_normal", IrType.Unit);
        int unit = shared.Temp(IrType.Unit);
        int next = shared.NewBlock();
        shared.Call(ThrowerFn, [], [Int(1)], unit, next, [new CatchClause(null, next)]);
        shared.At(next).Return();
        ExpectType([Thrower(), shared.Build()], TypeErrorKind.InvalidHandler, "handler block also reached normally");

        var escape = new FunctionBuilder("throw_local_ref", IrType.Unit);
        int x = escape.Local(IrType.I32, "x");
        int r = escape.Local(IrType.Ref(IrType.I32), "r");
        escape.Assign(x, Int(1)).Assign(r, Rvalue.Ref(x)).Throw(Move(r));
        var errors = BorrowChecker.Check(new IrModule(Structs, [escape.Build()]));
        Check("throwing a reference to a local is rejected", errors.Count == 1 && errors[0].Kind == BorrowErrorKind.ReturnsLocalReference, errors.FirstOrDefault()?.Message);
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static IlModule Compile(IrModule module, GcConfig? gc = null, string entry = "main")
    {
        var result = Compiler.Compile(module, new CompilationOptions { Gc = gc ?? GcConfig.Default, Entry = entry });
        if (!result.Success)
            throw new InvalidOperationException(string.Join("\n", result.TypeErrors.Select(e => e.ToString()).Concat(result.Diagnostics.Select(d => d.ToString()))));
        return result.Il!;
    }

    private static Interpreter Vm(IrModule module, bool stress = false) =>
        new(Compile(module), options: new InterpreterOptions { GcStress = stress });

    private static int Leaks(Interpreter vm) => vm.Heap.Live.Count(a => a.Kind == AllocationKind.Heap);

    private void ExpectType(IrFunction[] functions, TypeErrorKind expected, string name)
    {
        var errors = TypeChecker.Check(new IrModule(Structs, functions), GcConfig.Default);
        Check(name, errors.Count >= 1 && errors.All(e => e.Kind == expected), string.Join(" | ", errors.Select(e => e.Message)));
    }

    private static bool Throws<T>(Action action, string? fragment = null) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T e)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"     ({e.Message})");
            return fragment is null || e.Message.Contains(fragment);
        }
    }
}
