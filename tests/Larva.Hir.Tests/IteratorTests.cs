using Larva.Hir.Lowering;
using Larva.Llvm;
using Larva.Runtime.Vm;
using Larva.X64;
using static Larva.Hir.Tests.E;

namespace Larva.Hir.Tests;

/// <summary>
/// Iterator steps and <c>try/finally</c>: <c>yield break</c> inside a try runs its finally before the end, and
/// <c>Dispose()</c> on an iterator paused inside a try (one abandoned before its end) runs the finally too. The program
/// is lowered and run on LVM, x64 and LLVM.
/// </summary>
public sealed unsafe class IteratorTests : CheckedTest
{
    [Fact]
    public void Yield_break_and_Dispose_run_the_finally_blocks_around_the_pause()
    {
        InProcess.RequireWindows();
        var lowered = HirLowerer.Lower(Program().Assembly);
        Check("the program lowers", lowered.Success, string.Join("; ", lowered.Errors));
        Require();
        var module = lowered.Module!;
        var vm = Larva.IR.Compiler.Compile(module);
        Check("the IR passes the type and borrow checks (LVM build)", vm.Il is not null, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
        if (vm.Il is null)
            return;
        var interpreter = new Interpreter(vm.Il);
        var x64 = X64Backend.Compile(module);
        Check("x64 compiles it", x64.Success, x64.ToString());
        var llvm = LlvmBackend.Compile(module, new LlvmOptions { OptimizationLevel = 2 });
        Check("LLVM compiles it", llvm.Success, llvm.ToString());
        if (!x64.Success || !llvm.Success)
            return;
        using var image = x64.Program!.Load();
        using var program = llvm.Program!;
        using var jit = program.Jit();
        const string function = "Iterators.Program.Run";
        object? onVm;
        try
        {
            onVm = interpreter.Run(function);
        }
        catch (Exception e)
        {
            onVm = e.GetType().Name + ": " + e.Message;
        }
        int onX64 = ((delegate* unmanaged<int>)image.Address(function))();
        int onLlvm = ((delegate* unmanaged<int>)jit.Address(function))();
        const int expected = 112;
        bool same = Equals(onVm, (long)expected) && onX64 == expected && onLlvm == expected;
        Check($"Run() = {expected}: the values before yield break (1), its finally (1), and Dispose() ran the finally once more (2)", same, $"LVM {onVm}, x64 {onX64}, LLVM {onLlvm}");
    }

    /// <summary>
    /// The frame keeps what lives across a pause: the loop's counter does, a temporary computed and yielded between two
    /// pauses does not and stays a local of the step (a smaller frame); the values come out the same.
    /// </summary>
    [Fact]
    public void Only_locals_live_across_a_yield_go_into_the_frame()
    {
        InProcess.RequireWindows();
        var p = new HirProgram("Counting");
        var iteratorDef = new TypeDef { Namespace = "System", Name = "Iterator", Kind = TypeKind.Class, Flags = TypeFlags.Abstract };
        var iterator = p.Assembly.Add(iteratorDef);
        var It = HirProgram.T(iterator);
        var current = HirProgram.Field(iterator, iteratorDef, "current", I);
        var next = HirProgram.Method(iterator, iteratorDef, "Next", Bool, MethodFlags.Virtual | MethodFlags.Abstract);
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);

        // class Program.Count$iterator0 : Iterator { iterator $Step() : bool { k = 0; while (k < 3) { t = k * 10; yield t + 1; k++; } } override Next() => $Step(); }
        var frameDef = new TypeDef { Namespace = p.Assembly.Name, Name = "Program.Count$iterator0", Kind = TypeKind.Class, Flags = TypeFlags.Final, DeclaringType = prog.Index, Base = It };
        var frame = p.Assembly.Add(frameDef);
        var Fr = HirProgram.T(frame);
        var step = HirProgram.Method(frame, frameDef, "$Step", Bool, MethodFlags.Iterator | MethodFlags.Final);
        {
            var m = new MethodBuilder(frameDef.Methods[^1]);
            var k = m.Local("k", I);
            var t = m.Local("t", I);
            m.Do(Let(k, Int(0)), While(Lt(k, Int(3)), Let(t, Mul(k, Int(10))), new YieldStmt(Add(t, Int(1))), S(Inc(k)))).Done();
        }
        HirProgram.Method(frame, frameDef, "Next", Bool, MethodFlags.Virtual | MethodFlags.Override | MethodFlags.Final);
        new MethodBuilder(frameDef.Methods[^1]).Do(Ret(new CallExpr(This(Fr), step, [], [], Bool) { NonVirtual = true })).Done();

        // Run(): sum = 0; it = new Count$iterator0(); while (it.Next()) sum += it.current; return sum;   (1 + 11 + 21)
        HirProgram.Method(prog, progDef, "Run", I, MethodFlags.Static);
        {
            var m = new MethodBuilder(progDef.Methods[^1]);
            var sum = m.Local("sum", I);
            var it = m.Local("it", It);
            m.Do(Let(sum, Int(0)),
                Let(it, new ConvertExpr(new NewExpr(null, [], [], Fr), ConversionKind.Upcast, It)),
                While(CallOn(it, next, Bool), S(AddTo(sum, F(it, current, I)))),
                Ret(sum)).Done();
        }
        var lowered = HirLowerer.Lower(p.Assembly);
        Check("the program lowers", lowered.Success, string.Join("; ", lowered.Errors));
        Require();
        var resumable = Larva.IR.Lowering.Coroutines.Lower(lowered.Module!);
        var frameStruct = resumable.Structs.Single(s => s.Name == "Counting.Program.Count$iterator0");
        Check("the counter lives in the frame", frameStruct.FieldNames.Any(n => n.StartsWith("$k", StringComparison.Ordinal)), string.Join(", ", frameStruct.FieldNames));
        Check("the temporary between two pauses does not", !frameStruct.FieldNames.Any(n => n.StartsWith("$t", StringComparison.Ordinal)), string.Join(", ", frameStruct.FieldNames));
        var vm = Larva.IR.Compiler.Compile(lowered.Module!);
        Check("the IR passes the type and borrow checks", vm.Il is not null, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
        if (vm.Il is null)
            return;
        var x64 = X64Backend.Compile(lowered.Module!);
        Check("x64 compiles it", x64.Success, x64.ToString());
        if (!x64.Success)
            return;
        using var image = x64.Program!.Load();
        var onVm = new Interpreter(vm.Il).Run("Counting.Program.Run");
        int onX64 = ((delegate* unmanaged<int>)image.Address("Counting.Program.Run"))();
        Check("Run() = 33 on LVM and x64", Equals(onVm, 33L) && onX64 == 33, $"LVM {onVm}, x64 {onX64}");
    }

    private static HirProgram Program()
    {
        var p = new HirProgram("Iterators");

        // abstract class System.Iterator { i32 current; bool disposing; abstract Next() : bool; Dispose() { disposing = true; Next(); } }
        var iteratorDef = new TypeDef { Namespace = "System", Name = "Iterator", Kind = TypeKind.Class, Flags = TypeFlags.Abstract };
        var iterator = p.Assembly.Add(iteratorDef);
        var It = HirProgram.T(iterator);
        var current = HirProgram.Field(iterator, iteratorDef, "current", I);
        var disposing = HirProgram.Field(iterator, iteratorDef, "disposing", Bool);
        var next = HirProgram.Method(iterator, iteratorDef, "Next", Bool, MethodFlags.Virtual | MethodFlags.Abstract);
        var dispose = HirProgram.Method(iterator, iteratorDef, "Dispose", HirType.Void, MethodFlags.None);
        new MethodBuilder(iteratorDef.Methods[^1]).Do(S(Set(F(This(It), disposing, Bool), True)), S(CallOn(This(It), next, Bool))).Done();

        // static class Program { static i32 Closed; ... }
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        var closed = F(null, HirProgram.Field(prog, progDef, "Closed", I, FieldFlags.Static), I);

        // class Program.Values$iterator0 : Iterator { i32 $p0; iterator $Step() : bool { try { yield 1; if ($p0 == 1) yield break; yield 2; } finally { Closed += 1; } } override Next() => $Step(); }
        var frameDef = new TypeDef { Namespace = p.Assembly.Name, Name = "Program.Values$iterator0", Kind = TypeKind.Class, Flags = TypeFlags.Final, DeclaringType = prog.Index, Base = It };
        var frame = p.Assembly.Add(frameDef);
        var Fr = HirProgram.T(frame);
        var stopField = HirProgram.Field(frame, frameDef, "$p0", I);
        var step = HirProgram.Method(frame, frameDef, "$Step", Bool, MethodFlags.Iterator | MethodFlags.Final);
        new MethodBuilder(frameDef.Methods[^1]).Do(
            Try(Blk(
                    new YieldStmt(Int(1)),
                    If(Eq(Arg(0), Int(1)), new YieldStmt(null)),
                    new YieldStmt(Int(2))),
                [], Blk(S(AddTo(closed, Int(1)))))).Done();
        HirProgram.Method(frame, frameDef, "Next", Bool, MethodFlags.Virtual | MethodFlags.Override | MethodFlags.Final);
        new MethodBuilder(frameDef.Methods[^1]).Do(Ret(new CallExpr(This(Fr), step, [], [], Bool) { NonVirtual = true })).Done();

        // static Values(stop) : Iterator => new Values$iterator0 { $p0 = stop };
        var values = HirProgram.Method(prog, progDef, "Values", It, MethodFlags.Static, MethodKind.Normal, HirProgram.P("stop", I));
        new MethodBuilder(progDef.Methods[^1]).Do(Ret(new ConvertExpr(new NewExpr(null, [], [new MemberInit(stopField, Arg(0))], Fr), ConversionKind.Upcast, It))).Done();

        // Run(): sum = 0; it = Values(1); while (it.Next()) sum += it.current; c1 = Closed; it2 = Values(0); it2.Next(); it2.Dispose(); return sum * 100 + c1 * 10 + Closed;   (112)
        HirProgram.Method(prog, progDef, "Run", I, MethodFlags.Static);
        {
            var m = new MethodBuilder(progDef.Methods[^1]);
            var sum = m.Local("sum", I);
            var it = m.Local("it", It);
            var c1 = m.Local("c1", I);
            var it2 = m.Local("it2", It);
            m.Do(
                Let(sum, Int(0)),
                Let(it, Call(values, It, Int(1))),
                While(CallOn(it, next, Bool), S(AddTo(sum, F(it, current, I)))),
                Let(c1, closed),
                Let(it2, Call(values, It, Int(0))),
                S(CallOn(it2, next, Bool)),
                S(CallOn(it2, dispose, HirType.Void)),
                Ret(Add(Add(Mul(sum, Int(100)), Mul(c1, Int(10))), closed))).Done();
        }
        return p;
    }
}
