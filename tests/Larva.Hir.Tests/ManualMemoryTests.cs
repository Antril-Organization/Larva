using Larva.Hir.Lowering;
using Larva.IR;
using Larva.Llvm;
using Larva.X64;
using static Larva.Hir.Tests.E;

namespace Larva.Hir.Tests;

/// <summary>
/// Lowering with manual memory, where a class handle or an array is a raw pointer: comparing two of them compares the
/// addresses (it was lowered as moving a pointer by elements, which needs the size of an array and wrongly scaled a
/// handle). Run on x64 and LLVM.
/// </summary>
public sealed unsafe class ManualMemoryTests : CheckedTest
{
    [Fact]
    public void References_compare_by_address_under_manual_memory()
    {
        InProcess.RequireWindows();
        var lowered = HirLowerer.Lower(Program().Assembly, new LoweringOptions { Gc = GcConfig.Manual });
        Check("the program lowers", lowered.Success, string.Join("; ", lowered.Errors));
        Require();
        var module = lowered.Module!;
        var vm = Compiler.Compile(module);
        Check("the IR passes the type check", vm.TypeErrors.Count == 0, string.Join("; ", vm.TypeErrors.Take(6)));
        var x64 = X64Backend.Compile(module);
        Check("x64 compiles it", x64.Success, x64.ToString());
        var llvm = LlvmBackend.Compile(module, new LlvmOptions { OptimizationLevel = 2 });
        Check("LLVM compiles it", llvm.Success, llvm.ToString());
        if (!x64.Success || !llvm.Success)
            return;
        using var image = x64.Program!.Load();
        using var program = llvm.Program!;
        using var jit = program.Jit();
        const string function = "Manual.Program.Compare";
        int onX64 = ((delegate* unmanaged<int>)image.Address(function))();
        int onLlvm = ((delegate* unmanaged<int>)jit.Address(function))();
        Check("a == b, a != c, x == y, x != z hold; a == c and x == z do not: 1111", onX64 == 1111 && onLlvm == 1111, $"x64 {onX64}, LLVM {onLlvm}");
    }

    /// <summary>
    /// <c>class Box { new() { } }</c>; <c>Compare()</c>: <c>a = new Box(); b = a; c = new Box(); x = new i32[3]; y = x;
    /// z = new i32[3]</c>, and a digit per comparison that comes out as it should.
    /// </summary>
    private static HirProgram Program()
    {
        var p = new HirProgram("Manual");
        var (box, boxDef) = p.Type("Box", TypeKind.Class);
        var B = HirProgram.T(box);
        HirProgram.Field(box, boxDef, "V", I);
        var boxNew = HirProgram.Method(box, boxDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor);
        new MethodBuilder(boxDef.Methods[^1]).Do().Done();

        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        HirProgram.Method(prog, progDef, "Compare", I, MethodFlags.Static);
        var m = new MethodBuilder(progDef.Methods[^1]);
        var ints = new ArrayType(I);
        var a = m.Local("a", B);
        var b = m.Local("b", B);
        var c = m.Local("c", B);
        var x = m.Local("x", ints);
        var y = m.Local("y", ints);
        var z = m.Local("z", ints);
        var r = m.Local("r", I);
        m.Do(Let(a, New(B, boxNew)), Let(b, a), Let(c, New(B, boxNew)),
            Let(x, new NewArrayExpr(I, [Int(3)], ints)), Let(y, x), Let(z, new NewArrayExpr(I, [Int(3)], ints)),
            Let(r, Int(0)),
            If(Eq(a, b), S(AddTo(r, Int(1)))),
            If(Ne(a, c), S(AddTo(r, Int(10)))),
            If(Eq(a, c), S(AddTo(r, Int(10000)))),
            If(Eq(x, y), S(AddTo(r, Int(100)))),
            If(Ne(x, z), S(AddTo(r, Int(1000)))),
            If(Eq(x, z), S(AddTo(r, Int(10000)))),
            Ret(r)).Done();
        return p;
    }
}
