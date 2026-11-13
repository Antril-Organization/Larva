using Larva.Hir.Lowering;
using Larva.Llvm;
using Larva.Runtime.Vm;
using Larva.X64;
using static Larva.Hir.Tests.E;

namespace Larva.Hir.Tests;

/// <summary>
/// Static constructors run from the entry (<see cref="LoweringOptions.StaticConstructorsFromEntry"/>): a type's runs
/// when the type is reachable from the entry, including a type that the entry names only as a type argument
/// (<c>Describe&lt;Config&gt;()</c> makes a <c>Config</c> through <c>T</c>). The program is lowered and run on LVM, x64 and LLVM.
/// </summary>
public sealed unsafe class StaticInitTests : CheckedTest
{
    [Fact]
    public void A_type_used_only_as_a_type_argument_gets_its_static_constructor_run()
    {
        var lowered = HirLowerer.Lower(Program().Assembly, new LoweringOptions { StaticConstructorsFromEntry = true });
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
        const string function = "StaticInit.Program.Main";
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
        const int expected = 42;
        bool same = Equals(onVm, (long)expected) && onX64 == expected && onLlvm == expected;
        Check($"Main() = {expected}: Config's static constructor ran before Describe<Config>() made a Config", same, $"LVM {onVm}, x64 {onX64}, LLVM {onLlvm}");
    }

    private static HirProgram Program()
    {
        var p = new HirProgram("StaticInit");

        // static class Program { static i32 Seen; ... }
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        var seen = F(null, HirProgram.Field(prog, progDef, "Seen", I, FieldFlags.Static), I);

        // class Config { static i32 Count; static new() { Count = 42; } new() { Program.Seen = Count; } }
        var (config, configDef) = p.Type("Config", TypeKind.Class);
        var count = F(null, HirProgram.Field(config, configDef, "Count", I, FieldFlags.Static), I);
        HirProgram.Method(config, configDef, "$static", HirType.Void, MethodFlags.Static, MethodKind.StaticConstructor);
        new MethodBuilder(configDef.Methods[^1]).Do(S(Set(count, Int(42)))).Done();
        HirProgram.Method(config, configDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor);
        new MethodBuilder(configDef.Methods[^1]).Do(S(Set(seen, count))).Done();

        // static Describe<T : new()>() : i32 { T t = new T(); return 0; }
        var describe = HirProgram.Method(prog, progDef, "Describe", I, MethodFlags.Static);
        progDef.Methods[^1].Generics.Add(new GenericParam("T") { Kinds = ConstraintKinds.New });
        var T = new GenericParamType(GenericOwner.Method, 0);
        {
            var m = new MethodBuilder(progDef.Methods[^1]);
            var t = m.Local("t", T);
            m.Do(Let(t, new NewExpr(null, [], [], T)), Ret(Int(0))).Done();
        }

        // Main() : i32 { Describe<Config>(); return Program.Seen; }   (the entry)
        var main = HirProgram.Method(prog, progDef, "Main", I, MethodFlags.Static);
        new MethodBuilder(progDef.Methods[^1]).Do(S(new CallExpr(null, describe, [HirProgram.T(config)], [], I)), Ret(seen)).Done();
        p.Assembly.EntryPoint = main;
        return p;
    }
}
