using Larva.Hir.Lowering;
using Larva.Llvm;
using Larva.Runtime.Vm;
using Larva.X64;
using static Larva.Hir.Tests.E;

namespace Larva.Hir.Tests;

/// <summary>
/// Lambdas and what they capture: a lambda's call keeps its own variables (so the lambdas it makes do not share them
/// with other calls), a pass of a loop inside a lambda keeps its own, and a lambda in a resumable body (an iterator
/// step) reaches the user's <c>this</c>, not the frame object. Each program is lowered and run on LVM, x64 and LLVM.
/// </summary>
public sealed unsafe class ClosureTests : CheckedTest
{
    private static readonly (string Method, int Expected, string What)[] Cases =
    [
        ("Adder", 11015, "a lambda made by a lambda keeps the parameter of the call that made it"),
        ("Passes", 31323, "lambdas made in passes of a loop inside a lambda keep that pass's variable"),
        ("Bump", 312, "an inner lambda changes the parameter of the call that made it"),
        ("Yields", 4207, "a lambda in an iterator step reads a field through the user's `this`"),
    ];

    [Fact]
    public void Closures_keep_a_frame_per_call_and_per_pass_and_reach_this_in_an_iterator()
    {
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
        foreach (var (name, expected, what) in Cases)
        {
            string function = $"Closures.Program.{name}";
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
            bool same = Equals(onVm, (long)expected) && onX64 == expected && onLlvm == expected;
            Check($"{name}() = {expected}: {what}", same, $"LVM {onVm}, x64 {onX64}, LLVM {onLlvm}");
        }
    }

    private static HirProgram Program()
    {
        var p = new HirProgram("Closures");
        var fI = new FuncType([], I);                 // func<> : i32
        var fII = new FuncType([I], I);               // func<i32> : i32
        var fIfII = new FuncType([I], fII);           // func<i32> : func<i32> : i32
        var fV = new FuncType([], HirType.Void);      // func<> : void
        var fIV = new FuncType([I], HirType.Void);    // func<i32> : void
        static InvokeExpr Invoke(Expr function, HirType result, params Expr[] args) => new(function, args, result);
        static LambdaExpr Lambda(int[] parameters, HirType type, params Stmt[] body) => new(parameters, new BlockStmt(body), type);

        // abstract class System.Iterator { i32 current; abstract Next() : bool; }  (what an iterator step's class derives from)
        var iteratorDef = new TypeDef { Namespace = "System", Name = "Iterator", Kind = TypeKind.Class, Flags = TypeFlags.Abstract };
        var iterator = p.Assembly.Add(iteratorDef);
        var current = HirProgram.Field(iterator, iteratorDef, "current", I);
        var next = HirProgram.Method(iterator, iteratorDef, "Next", Bool, MethodFlags.Virtual | MethodFlags.Abstract);

        // class Box { i32 Value; Numbers() : Frame => new Frame { $this = this }; }
        var (box, boxDef) = p.Type("Box", TypeKind.Class);
        var Bx = HirProgram.T(box);
        var value = HirProgram.Field(box, boxDef, "Value", I);

        // class Box.Numbers$iterator0 : Iterator { Box $this; iterator $Step() : bool { f = () => this.Value; yield f(); this.Value = 7; yield f(); } override Next() => $Step(); }
        var frameDef = new TypeDef { Namespace = p.Assembly.Name, Name = "Box.Numbers$iterator0", Kind = TypeKind.Class, Flags = TypeFlags.Final, DeclaringType = box.Index, Base = HirType.Named(iterator) };
        var frame = p.Assembly.Add(frameDef);
        var Fr = HirProgram.T(frame);
        var thisField = HirProgram.Field(frame, frameDef, "$this", Bx);
        var step = HirProgram.Method(frame, frameDef, "$Step", Bool, MethodFlags.Iterator | MethodFlags.Final);
        {
            var m = new MethodBuilder(frameDef.Methods[^1]);
            var f = m.Local("f", fI);
            m.Do(
                Let(f, Lambda([], fI, Ret(F(This(Bx), value, I)))),
                new YieldStmt(Invoke(f, I)),
                S(Set(F(This(Bx), value, I), Int(7))),
                new YieldStmt(Invoke(f, I))).Done();
        }
        HirProgram.Method(frame, frameDef, "Next", Bool, MethodFlags.Virtual | MethodFlags.Override | MethodFlags.Final);
        new MethodBuilder(frameDef.Methods[^1]).Do(Ret(new CallExpr(This(Fr), step, [], [], Bool) { NonVirtual = true })).Done();
        var numbers = HirProgram.Method(box, boxDef, "Numbers", Fr, MethodFlags.None);
        new MethodBuilder(boxDef.Methods[^1]).Do(Ret(new NewExpr(null, [], [new MemberInit(thisField, This(Bx))], Fr))).Done();

        // static class Program
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        MethodBuilder M(string name)
        {
            HirProgram.Method(prog, progDef, name, I, MethodFlags.Static);
            return new MethodBuilder(progDef.Methods[^1]);
        }

        // Adder(): adder = n => m => n + m; a = adder(1); b = adder(5); return a(10) * 1000 + b(10);   (11015)
        {
            var m = M("Adder");
            var adder = m.Local("adder", fIfII);
            var n = m.Local("n", I);
            var mm = m.Local("m", I);
            var a = m.Local("a", fII);
            var b = m.Local("b", fII);
            m.Do(
                Let(adder, Lambda([n.Local], fIfII, Ret(Lambda([mm.Local], fII, Ret(Add(n, mm)))))),
                Let(a, Invoke(adder, fII, Int(1))),
                Let(b, Invoke(adder, fII, Int(5))),
                Ret(Add(Mul(Invoke(a, I, Int(10)), Int(1000)), Invoke(b, I, Int(10))))).Done();
        }

        // Passes(): maker = k => { foreach (j in 0..3) { h = () => j * 10 + k; if (j == 0) g0 = h; else if (j == 1) g1 = h; else g2 = h; } };
        //           maker(3); return g0() * 10000 + g1() * 100 + g2();   (3 * 10000 + 13 * 100 + 23 = 31323)
        {
            var m = M("Passes");
            var maker = m.Local("maker", fIV);
            var k = m.Local("k", I);
            var j = m.Local("j", I);
            var h = m.Local("h", fI);
            var g0 = m.Local("g0", fI);
            var g1 = m.Local("g1", fI);
            var g2 = m.Local("g2", fI);
            m.Do(
                Let(g0, Lambda([], fI, Ret(Int(0)))), Let(g1, g0), Let(g2, g0),
                Let(maker, Lambda([k.Local], fIV,
                    new ForeachStmt(j.Local, new RangeExpr(Int(0), Int(3), false, I), Blk(
                        Let(h, Lambda([], fI, Ret(Add(Mul(j, Int(10)), k)))),
                        If(Eq(j, Int(0)), S(Set(g0, h)), If(Eq(j, Int(1)), S(Set(g1, h)), S(Set(g2, h)))))))),
                S(Invoke(maker, HirType.Void, Int(3))),
                Ret(Add(Add(Mul(Invoke(g0, I), Int(10000)), Mul(Invoke(g1, I), Int(100))), Invoke(g2, I)))).Done();
        }

        // Bump(): bump = n => { inc = () => { n += 1; }; inc(); inc(); return n; }; return bump(1) * 100 + bump(10);   (3 * 100 + 12 = 312)
        {
            var m = M("Bump");
            var bump = m.Local("bump", fII);
            var n = m.Local("n", I);
            var inc = m.Local("inc", fV);
            m.Do(
                Let(bump, Lambda([n.Local], fII,
                    Let(inc, Lambda([], fV, S(AddTo(n, Int(1))))),
                    S(Invoke(inc, HirType.Void)),
                    S(Invoke(inc, HirType.Void)),
                    Ret(n))),
                Ret(Add(Mul(Invoke(bump, I, Int(1)), Int(100)), Invoke(bump, I, Int(10))))).Done();
        }

        // Yields(): b = new Box { Value = 42 }; it = b.Numbers(); it.Next(); x = it.current; it.Next(); return x * 100 + it.current;   (4207)
        {
            var m = M("Yields");
            var b = m.Local("b", Bx);
            var it = m.Local("it", Fr);
            var x = m.Local("x", I);
            m.Do(
                Let(b, new NewExpr(null, [], [new MemberInit(value, Int(42))], Bx)),
                Let(it, CallOn(b, numbers, Fr)),
                S(CallOn(it, next, Bool)),
                Let(x, F(it, current, I)),
                S(CallOn(it, next, Bool)),
                Ret(Add(Mul(x, Int(100)), F(it, current, I)))).Done();
        }
        return p;
    }
}
