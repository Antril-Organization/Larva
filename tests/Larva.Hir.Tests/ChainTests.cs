using System.Diagnostics;
using Larva.Hir.Lowering;
using Larva.Runtime.Vm;
using static Larva.Hir.Tests.E;

namespace Larva.Hir.Tests;

/// <summary>
/// Long left-deep expression chains (<c>a + a + … + a</c> of thousands of terms, a string joined of thousands of pieces,
/// a condition of thousands of <c>&amp;&amp;</c>) lower in a loop, not by recursion per term (which overflowed the host
/// stack at about 1,400 terms); and an expression nested beyond the limit is reported, not a crash.
/// </summary>
public sealed class ChainTests : CheckedTest
{
    private const int Terms = 5000;

    /// <summary>
    /// The condition chain run on the LVM is shorter: the IR build of a function of thousands of blocks takes minutes
    /// (a back-end cost, not lowering's); the 5,000-term one is lowered only.
    /// </summary>
    private const int RunConditionTerms = 400;

    private static void Log(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    /// <summary>The chains as HIR: <c>terms</c> steps each, the condition chains <c>conditionTerms</c>.</summary>
    private static HirProgram Program(int terms, int conditionTerms)
    {
        var p = new HirProgram("Chains");
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        var counter = HirProgram.Field(prog, progDef, "Counter", I, FieldFlags.Static);
        var C = F(null, counter, I);
        MethodBuilder M(string name, params Parameter[] parameters)
        {
            HirProgram.Method(prog, progDef, name, I, MethodFlags.Static, MethodKind.Normal, parameters);
            return new MethodBuilder(progDef.Methods[^1]);
        }

        // Sum(a) = a + a + … + a.
        Expr sum = Arg(0);
        for (int i = 1; i < terms; i++)
            sum = Add(sum, Arg(0));
        M("Sum", HirProgram.P("a", I)).Do(Ret(sum)).Done();

        // Next() = ++Counter; Step(acc, x) = (acc * 2 + x) % 10007: the order of the right operands is visible.
        var next = HirProgram.Method(prog, progDef, "Next", I, MethodFlags.Static);
        new MethodBuilder(progDef.Methods[^1]).Do(S(AddTo(C, Int(1))), Ret(C)).Done();
        var step = HirProgram.Method(prog, progDef, "Step", I, MethodFlags.Static, MethodKind.Normal, HirProgram.P("acc", I), HirProgram.P("x", I));
        new MethodBuilder(progDef.Methods[^1]).Do(Ret(Rem(Add(Mul(Arg(0), Int(2)), Arg(1)), Int(10007)))).Done();

        // Calls() = Step(Step(Step(Next(), Next()), Next()), …): a static call chain through the first argument
        // (what a frontend makes of `s + "a" + "b" + …`: Concat(Concat(…), …)).
        Expr calls = Call(next, I);
        for (int i = 0; i < terms; i++)
            calls = Call(step, I, calls, Call(next, I));
        M("Calls").Do(S(Set(C, Int(0))), Ret(calls)).Done();

        // Mixed() = ((Next() * 2 + Next()) % 10007 * 2 + Next()) % 10007 …: a binary chain whose right operands are calls.
        Expr mixed = Call(next, I);
        for (int i = 0; i < terms; i++)
            mixed = Rem(Add(Mul(mixed, Int(2)), Call(next, I)), Int(10007));
        M("Mixed").Do(S(Set(C, Int(0))), Ret(mixed)).Done();

        // class Builder { i32 Total; Add(x) { Total = (Total * 2 + x) % 10007; return this; } }: b.Add(Next()).Add(Next())…
        var (builder, builderDef) = p.Type("Builder", TypeKind.Class);
        var B = HirProgram.T(builder);
        var total = HirProgram.Field(builder, builderDef, "Total", I);
        var builderNew = HirProgram.Method(builder, builderDef, ".new", HirType.Void, MethodFlags.None, MethodKind.Constructor);
        new MethodBuilder(builderDef.Methods[^1]).Do(S(Set(F(This(B), total, I), Int(0)))).Done();
        var add = HirProgram.Method(builder, builderDef, "Add", B, MethodFlags.None, MethodKind.Normal, HirProgram.P("x", I));
        new MethodBuilder(builderDef.Methods[^1]).Do(S(Set(F(This(B), total, I), Rem(Add(Mul(F(This(B), total, I), Int(2)), Arg(0)), Int(10007)))), Ret(This(B))).Done();
        var mb = M("Receivers");
        var b = mb.Local("b", B);
        Expr chain = b;
        for (int i = 0; i < terms; i++)
            chain = CallOn(chain, add, B, Call(next, I));
        mb.Do(S(Set(C, Int(0))), Let(b, New(B, builderNew)), Ret(F(chain, total, I))).Done();

        // Conditions(a) = (0 < a && 1 < a && … ) ? 1 : (a < 0 || !(1 < a) || …) ? 2 : 0.
        Expr all = Lt(Int(0), Arg(0));
        for (int i = 1; i < conditionTerms; i++)
            all = new BinaryExpr(BinaryOp.And, all, Lt(Int(i), Arg(0)), Bool);
        Expr any = Lt(Arg(0), Int(0));
        for (int i = 1; i < conditionTerms; i++)
            any = new BinaryExpr(BinaryOp.Or, any, Not(Lt(Int(i), Arg(0))), Bool);
        M("Conditions", HirProgram.P("a", I)).Do(If(all, Ret(Int(1))), If(any, Ret(Int(2))), Ret(Int(0))).Done();
        return p;
    }

    [Fact]
    public void Chains_of_5000_terms_lower_and_run_on_the_LVM()
    {
        var watch = Stopwatch.StartNew();
        var full = HirLowerer.Lower(Program(Terms, Terms).Assembly);
        Log($"lowering (all chains of {Terms}) {watch.ElapsedMilliseconds} ms");
        Check("the program with 5000-term chains lowers", full.Success, string.Join("; ", full.Errors));

        watch.Restart();
        var lowered = HirLowerer.Lower(Program(Terms, RunConditionTerms).Assembly);
        Log($"lowering (conditions of {RunConditionTerms}) {watch.ElapsedMilliseconds} ms");
        Check("the program lowers", lowered.Success, string.Join("; ", lowered.Errors));
        Require();
        watch.Restart();
        var vm = Larva.IR.Compiler.Compile(lowered.Module!);
        Log($"LVM build {watch.ElapsedMilliseconds} ms");
        Check("the IR passes the type and borrow checks", vm.Il is not null, string.Join("; ", vm.TypeErrors.Concat<object>(vm.Diagnostics).Take(6)));
        Require();
        var interpreter = new Interpreter(vm.Il!);

        Check($"Sum(3) = {3 * Terms}", Equals(interpreter.Run("Chains.Program.Sum", 3), 3 * Terms), $"{interpreter.Run("Chains.Program.Sum", 3)}");
        long expected = 1;
        for (int i = 0; i < Terms; i++)
            expected = (expected * 2 + i + 2) % 10007;
        Check("Calls() folds Next() in order", Equals(interpreter.Run("Chains.Program.Calls"), (int)expected), $"{interpreter.Run("Chains.Program.Calls")} instead of {expected}");
        Check("Mixed() folds Next() in order", Equals(interpreter.Run("Chains.Program.Mixed"), (int)expected), $"{interpreter.Run("Chains.Program.Mixed")} instead of {expected}");
        expected = 0;
        for (int i = 0; i < Terms; i++)
            expected = (expected * 2 + i + 1) % 10007;
        Check("Receivers() chains the calls in order", Equals(interpreter.Run("Chains.Program.Receivers"), (int)expected), $"{interpreter.Run("Chains.Program.Receivers")} instead of {expected}");
        Check($"Conditions({RunConditionTerms}) = 1", Equals(interpreter.Run("Chains.Program.Conditions", RunConditionTerms), 1), $"{interpreter.Run("Chains.Program.Conditions", RunConditionTerms)}");
        Check($"Conditions({RunConditionTerms - 1}) = 2", Equals(interpreter.Run("Chains.Program.Conditions", RunConditionTerms - 1), 2), $"{interpreter.Run("Chains.Program.Conditions", RunConditionTerms - 1)}");
        Check("Conditions(-1) = 2", Equals(interpreter.Run("Chains.Program.Conditions", -1), 2), $"{interpreter.Run("Chains.Program.Conditions", -1)}");
    }

    [Fact]
    public void An_expression_nested_too_deeply_is_reported_not_a_crash()
    {
        var p = new HirProgram("Deep");
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        HirProgram.Method(prog, progDef, "Deep", I, MethodFlags.Static, MethodKind.Normal, HirProgram.P("a", I));
        // a + (a + (a + …)): right-deep, so no loop lowers it; beyond the limit it is an error.
        Expr deep = Arg(0);
        for (int i = 0; i < BodyLowerer.MaxDepth + 100; i++)
            deep = Add(Arg(0), deep);
        new MethodBuilder(progDef.Methods[^1]).Do(Ret(deep)).Done();
        var lowered = HirLowerer.Lower(p.Assembly);
        Check("lowering fails", !lowered.Success);
        Check("with the nesting diagnostic", lowered.Errors.Any(e => e.Message.Contains("nested too deeply")), string.Join("; ", lowered.Errors));
    }

    private static bool Equals(object? result, int expected) => result is long l ? l == expected : result is int i && i == expected;
}
