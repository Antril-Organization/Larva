using Larva.Hir.Lowering;
using Larva.IR.Text;
using static Larva.Hir.Tests.E;

namespace Larva.Hir.Tests;

/// <summary>
/// Lowering on several threads is reproducible: what the threads add to the module while bodies lower (lambdas,
/// display structs, literals) lands in a fixed order, so the IR, and the binaries built from it, are the same run to
/// run and the same as on one thread. The program has many methods that each make a lambda, so the threads race for
/// the shared tables.
/// </summary>
public sealed class ParallelLoweringTests : CheckedTest
{
    [Fact]
    public void Lowering_on_several_threads_gives_the_same_IR_every_time()
    {
        var program = Program(120).Assembly;
        var sequential = HirLowerer.Lower(program, new LoweringOptions { Parallel = false });
        Check("the program lowers on one thread", sequential.Success, string.Join("; ", sequential.Errors.Take(3)));
        Require();
        string expected = IrPrinter.Print(sequential.Module!);
        // Several lowerings at once, each on several threads, so every one of them races differently.
        var runs = Enumerable.Range(0, 6).Select(_ => Task.Run(() => HirLowerer.Lower(program))).ToArray();
        Task.WaitAll(runs);
        for (int r = 0; r < runs.Length; r++)
        {
            var lowered = runs[r].Result;
            Check($"run {r} lowers", lowered.Success, string.Join("; ", lowered.Errors.Take(3)));
            if (!lowered.Success)
                continue;
            string text = IrPrinter.Print(lowered.Module!);
            Check($"run {r} on several threads gives the IR of one thread", text == expected, FirstDifference(expected, text));
        }
        var module = sequential.Module!;
        Check("every lambda and display struct is named after its method", module.Functions.Count(f => f.Name.Contains("$lambda")) == 120
            && module.Structs.Count(s => s.Name.Contains("$display")) == 120);
        Check("the added functions are in name order", IsSorted(module.Functions.Where(f => f.Name.Contains("$lambda")).Select(f => f.Name)));
        Check("the added structs are in name order", IsSorted(module.Structs.Where(s => s.Name.Contains("$display")).Select(s => s.Name)));
    }

    private static bool IsSorted(IEnumerable<string> names)
    {
        string? previous = null;
        foreach (var name in names)
        {
            if (previous is not null && string.CompareOrdinal(previous, name) > 0)
                return false;
            previous = name;
        }
        return true;
    }

    private static string FirstDifference(string a, string b)
    {
        int at = 0;
        while (at < a.Length && at < b.Length && a[at] == b[at])
            at++;
        int line = a.Take(at).Count(c => c == '\n') + 1;
        return $"first difference at line {line}: expected `{Line(a, at)}`, found `{Line(b, at)}`";
        static string Line(string text, int at)
        {
            int start = text.LastIndexOf('\n', Math.Min(at, text.Length - 1)) + 1;
            int end = text.IndexOf('\n', start);
            return text[start..(end < 0 ? text.Length : end)];
        }
    }

    /// <summary>static class Program { M0(i32 x) : i32 { f = (k) => k * x + 0; return f(x); } … }: one lambda per method.</summary>
    private static HirProgram Program(int methods)
    {
        var p = new HirProgram("Parallel");
        var fII = new FuncType([I], I);
        var (prog, progDef) = p.Type("Program", TypeKind.Class, flags: TypeFlags.Static | TypeFlags.Abstract);
        for (int i = 0; i < methods; i++)
        {
            HirProgram.Method(prog, progDef, $"M{i}", I, MethodFlags.Static, MethodKind.Normal, HirProgram.P("x", I));
            var m = new MethodBuilder(progDef.Methods[^1]);
            var f = m.Local("f", fII);
            var k = m.Local("k", I);
            m.Do(
                Let(f, new LambdaExpr([k.Local], new BlockStmt([Ret(Add(Mul(k, Arg(0)), Int(i)))]), fII)),
                Ret(new InvokeExpr(f, [Arg(0)], I))).Done();
        }
        return p;
    }
}
