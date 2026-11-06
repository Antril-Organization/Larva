using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Encoding;
using Larva.Runtime.Vm;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>
/// Requirements on type parameters (operators, conversions, static members) resolved per instantiation: built-in
/// operations for primitives, the module's implementations for user types, generic implementations included.
/// </summary>
public sealed unsafe class RequirementTests : CheckedTest
{
    private const int Vec = 0, Pair = 1;
    private static readonly IrType VecType = IrType.Struct(Vec);
    private static IrType PairOf(IrType t) => IrType.Struct(Pair, t);

    // Function indices.
    private const int AddFn = 0, TwiceFn = 1, ToFn = 2, ZeroFn = 3, VecAddFn = 4, VecZeroFn = 5, I32ZeroFn = 6, PairAddFn = 7, MainFn = 8;

    private static readonly Requirement PlusT = new("op+", [IrType.Param(0), IrType.Param(0)], IrType.Param(0));

    // ── The program ────────────────────────────────────────────────

    /// <summary>fn add&lt;T: op+(T, T) -> T&gt;(a: T, b: T) -> T { require T.op+(a, b) }</summary>
    private static IrFunction Add()
    {
        var f = new FunctionBuilder("add", IrType.Param(0));
        f.Generic("T");
        int plus = f.Require(0, PlusT);
        int a = f.Argument(IrType.Param(0), "a"), b = f.Argument(IrType.Param(0), "b");
        int done = f.NewBlock();
        f.CallRequired(0, plus, [Move(a), Move(b)], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    /// <summary>fn twice&lt;U: op+(U, U) -> U&gt;(x: U) -> U { add::&lt;U&gt;(x, x) } — passes its own requirement on.</summary>
    private static IrFunction Twice()
    {
        var f = new FunctionBuilder("twice", IrType.Param(0));
        f.Generic("U", TypeConstraint.Copy);
        f.Require(0, PlusT);
        int x = f.Argument(IrType.Param(0), "x");
        int done = f.NewBlock();
        f.Call(AddFn, [IrType.Param(0)], [Copy(x), Copy(x)], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    /// <summary>fn to&lt;T: conv(T) -> U, U&gt;(x: T) -> U { require T.conv(x) }</summary>
    private static IrFunction To()
    {
        var f = new FunctionBuilder("to", IrType.Param(1));
        f.Generic("T");
        f.Generic("U");
        int conv = f.Require(0, new Requirement(Requirement.Conversion, [IrType.Param(0)], IrType.Param(1)));
        int x = f.Argument(IrType.Param(0), "x");
        int done = f.NewBlock();
        f.CallRequired(0, conv, [Move(x)], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    /// <summary>fn zero&lt;T: Zero() -> T&gt;() -> T { require T.Zero() } — a static member.</summary>
    private static IrFunction Zero()
    {
        var f = new FunctionBuilder("zero", IrType.Param(0));
        f.Generic("T");
        int zero = f.Require(0, new Requirement("Zero", [], IrType.Param(0)));
        int done = f.NewBlock();
        f.CallRequired(0, zero, [], FunctionBuilder.ReturnPlace, done);
        f.At(done).Return();
        return f.Build();
    }

    private static IrFunction VecAdd()
    {
        var f = new FunctionBuilder("vec_add", VecType);
        int a = f.Argument(VecType, "a"), b = f.Argument(VecType, "b");
        var r = FunctionBuilder.ReturnPlace;
        f.Assign(r.Field(0), Rvalue.Binary(BinaryOp.Add, Copy(new Place(a).Field(0)), Copy(new Place(b).Field(0))))
         .Assign(r.Field(1), Rvalue.Binary(BinaryOp.Add, Copy(new Place(a).Field(1)), Copy(new Place(b).Field(1)))).Return();
        return f.Build();
    }

    private static IrFunction VecZero()
    {
        var f = new FunctionBuilder("vec_zero", VecType);
        f.Assign(FunctionBuilder.ReturnPlace.Field(0), Int(0)).Assign(FunctionBuilder.ReturnPlace.Field(1), Int(100)).Return();
        return f.Build();
    }

    private static IrFunction I32Zero()
    {
        var f = new FunctionBuilder("i32_zero", IrType.I32);
        f.Assign(FunctionBuilder.ReturnPlace, Int(-7)).Return();
        return f.Build();
    }

    /// <summary>fn pair_add&lt;T: op+(T, T) -> T&gt;(a: Pair&lt;T&gt;, b: Pair&lt;T&gt;) -> Pair&lt;T&gt; — a generic implementation.</summary>
    private static IrFunction PairAdd()
    {
        var t = IrType.Param(0);
        var f = new FunctionBuilder("pair_add", PairOf(t));
        f.Generic("T", TypeConstraint.Copy);
        int plus = f.Require(0, PlusT);
        int a = f.Argument(PairOf(t), "a"), b = f.Argument(PairOf(t), "b");
        int second = f.NewBlock(), done = f.NewBlock();
        f.CallRequired(0, plus, [Copy(new Place(a).Field(0)), Copy(new Place(b).Field(0))], FunctionBuilder.ReturnPlace.Field(0), second);
        f.At(second).CallRequired(0, plus, [Copy(new Place(a).Field(1)), Copy(new Place(b).Field(1))], FunctionBuilder.ReturnPlace.Field(1), done);
        f.At(done).Return();
        return f.Build();
    }

    /// <summary>
    /// add::&lt;i32&gt;(2, 3) = 5 · twice::&lt;i64&gt;(21) = 42 · to::&lt;f64, i32&gt;(7.9) = 7 · add::&lt;Vec&gt;((1,2), (10,20)).y = 22 ·
    /// zero::&lt;Vec&gt;().y = 100 · zero::&lt;i32&gt;() = -7 · add::&lt;Pair&lt;i32&gt;&gt;((3,4), (30,40)).second = 44
    /// </summary>
    private static IrFunction Program_Main()
    {
        var f = new FunctionBuilder("main", IrType.I64);
        int a = f.Local(IrType.I32, "a"), b = f.Local(IrType.I64, "b"), c = f.Local(IrType.I32, "c");
        int v1 = f.Local(VecType, "v1"), v2 = f.Local(VecType, "v2"), v3 = f.Local(VecType, "v3"), z = f.Local(VecType, "z");
        int zi = f.Local(IrType.I32, "zi");
        int p1 = f.Local(PairOf(IrType.I32), "p1"), p2 = f.Local(PairOf(IrType.I32), "p2"), p3 = f.Local(PairOf(IrType.I32), "p3");
        int sum = f.Local(IrType.I64, "sum", mutable: true), wide = f.Local(IrType.I64, "wide", mutable: true);
        int[] next = [.. Enumerable.Range(0, 7).Select(_ => f.NewBlock())];
        f.Call(AddFn, [IrType.I32], [Int(2), Int(3)], new Place(a), next[0]);
        f.At(next[0]).Call(TwiceFn, [IrType.I64], [Int(21, IrType.I64)], new Place(b), next[1]);
        f.At(next[1]).Call(ToFn, [IrType.F64, IrType.I32], [Float(7.9)], new Place(c), next[2]);
        f.At(next[2])
            .Assign(new Place(v1).Field(0), Int(1)).Assign(new Place(v1).Field(1), Int(2))
            .Assign(new Place(v2).Field(0), Int(10)).Assign(new Place(v2).Field(1), Int(20))
            .Call(AddFn, [VecType], [Move(new Place(v1)), Move(new Place(v2))], new Place(v3), next[3]);
        f.At(next[3]).Call(ZeroFn, [VecType], [], new Place(z), next[4]);
        f.At(next[4]).Call(ZeroFn, [IrType.I32], [], new Place(zi), next[5]);
        f.At(next[5])
            .Assign(new Place(p1).Field(0), Int(3)).Assign(new Place(p1).Field(1), Int(4))
            .Assign(new Place(p2).Field(0), Int(30)).Assign(new Place(p2).Field(1), Int(40))
            .Call(AddFn, [PairOf(IrType.I32)], [Move(new Place(p1)), Move(new Place(p2))], new Place(p3), next[6]);
        // sum = a + b*10 + c*1000 + v3.y*10^4 + z.y*10^6 + zi*10^9 + p3.second*10^11
        void AddScaled(Operand value, IrType type, long scale)
        {
            if (type != IrType.I64)
                f.Assign(wide, Rvalue.Cast(value, IrType.I64));
            else
                f.Assign(wide, Rvalue.Use(value));
            f.Assign(wide, Rvalue.Binary(BinaryOp.Mul, Copy(wide), Int(scale, IrType.I64)));
            f.Assign(sum, Rvalue.Binary(BinaryOp.Add, Copy(sum), Copy(wide)));
        }
        f.At(next[6]).Assign(sum, Int(0, IrType.I64));
        AddScaled(Copy(a), IrType.I32, 1);
        AddScaled(Copy(b), IrType.I64, 10);
        AddScaled(Copy(c), IrType.I32, 1000);
        AddScaled(Copy(new Place(v3).Field(1)), IrType.I32, 10_000);
        AddScaled(Copy(new Place(z).Field(1)), IrType.I32, 1_000_000);
        AddScaled(Copy(zi), IrType.I32, 1_000_000_000);
        AddScaled(Copy(new Place(p3).Field(1)), IrType.I32, 100_000_000_000);
        f.Assign(FunctionBuilder.ReturnPlace, Copy(sum)).Return();
        return f.Build();
    }

    private const long Expected = 5 + 420 + 7000 + 220_000 + 100_000_000 - 7_000_000_000 + 4_400_000_000_000;

    internal static IrModule ProgramModule(Implementation[]? implementations = null) =>
        new([new StructDef("Vec", IrType.I32, IrType.I32) { FieldNames = ["x", "y"] },
             new StructDef("Pair", IrType.Param(0), IrType.Param(0)) { Generics = [new GenericParam("T")], FieldNames = ["first", "second"] }],
            [Add(), Twice(), To(), Zero(), VecAdd(), VecZero(), I32Zero(), PairAdd(), Program_Main()])
        {
            Implementations = implementations ??
                [new("op+", VecAddFn), new("Zero", VecZeroFn), new("Zero", I32ZeroFn), new("op+", PairAddFn)],
        };

    [Fact]
    public void Program_type_checks_and_survives_encoding()
    {
        var module = ProgramModule();
        var errors = TypeChecker.Check(module, GcConfig.Default);
        Check("the program type-checks", errors.Count == 0, string.Join("; ", errors));

        var decoded = IrDecoder.Decode(IrEncoder.Encode(module));
        Check("requirements, required calls and implementations survive encoding", decoded.ToString() == module.ToString());
    }

    [Fact]
    public void Program_runs_on_the_vm_in_both_generics_modes()
    {
        var module = ProgramModule();

        foreach (var mode in new[] { GenericsMode.Shared, GenericsMode.Specialize })
        {
            var compiled = Compiler.Compile(module, new CompilationOptions { Generics = mode });
            if (compiled.Il is null)
            {
                Check($"VM ({mode})", false, string.Join("; ", compiled.TypeErrors.Select(e => e.ToString()).Concat(compiled.Diagnostics.Select(d => d.ToString()))));
                continue;
            }
            long vm = System.Convert.ToInt64(new Interpreter(compiled.Il).Run("main"));
            Check($"VM ({mode}) = {Expected}", vm == Expected, $"got {vm}");
        }
    }

    [Fact]
    public void Specialization_turns_required_calls_into_ops_or_direct_calls()
    {
        var module = ProgramModule();

        var concrete = Larva.IR.Lowering.Monomorphizer.Run(module, GenericsMode.Specialize);
        var names = concrete.Functions.Select(f => f.Name).ToList();
        Check("requirement calls become built-in ops (add<i32>) or direct calls (add<Vec> → vec_add, add<Pair<i32>> → pair_add<i32>)",
            names.Contains("add<i32>") && names.Contains("add<Vec>") && names.Contains("add<Pair<i32>>") && names.Contains("pair_add<i32>") && names.Contains("vec_add")
            && concrete.Functions.All(f => f.Blocks.All(b => b.Terminator is not CallTerminator { Required: not null })));
    }

    [Fact]
    public void Program_runs_on_llvm_and_x64()
    {
        var module = ProgramModule();
        long llvm = long.MinValue, x64 = long.MinValue;
        var llvmResult = Larva.Llvm.LlvmBackend.Compile(module);
        if (llvmResult.Success)
        {
            using var program = llvmResult.Program!;
            using var jit = program.Jit();
            llvm = ((delegate* unmanaged<long>)jit.Address("main"))();
        }
        var x64Result = Larva.X64.X64Backend.Compile(module);
        if (x64Result.Success)
        {
            using var image = x64Result.Program!.Load();
            x64 = ((delegate* unmanaged<long>)image.Address("main"))();
        }
        Check($"LLVM and x64 = {Expected}", llvm == Expected && x64 == Expected,
            $"llvm {llvm} {llvmResult.Error}, x64 {x64} {x64Result.Error}");
    }

    // ── Errors ─────────────────────────────────────────────────────

    [Fact]
    public void Missing_ambiguous_and_mistyped_requirements_are_rejected()
    {
        // add::<bool>: bool has no `+`.
        var noPlus = new FunctionBuilder("main", IrType.Bool);
        int done = noPlus.NewBlock();
        noPlus.Call(AddFn, [IrType.Bool], [Bool(true), Bool(false)], FunctionBuilder.ReturnPlace, done);
        noPlus.At(done).Return();
        Expect("a type without the operation", [Add(), noPlus.Build()], [], "`op+(bool, bool) -> bool`, required of `T` by `add`, is not provided");

        // A generic caller must carry the requirement it relies on.
        var lax = new FunctionBuilder("lax", IrType.Param(0));
        lax.Generic("T");
        int x = lax.Argument(IrType.Param(0), "x");
        int laxDone = lax.NewBlock();
        lax.Call(AddFn, [IrType.Param(0)], [Move(x), Move(x)], FunctionBuilder.ReturnPlace, laxDone);
        lax.At(laxDone).Return();
        Expect("a generic caller without the requirement", [Add(), lax.Build()], [], "`op+(T, T) -> T`, required of `T` by `add`, is not provided");

        // Two implementations for the same types.
        var vecMain = new FunctionBuilder("main", VecType);
        int v = vecMain.Local(VecType, "v");
        int vecDone = vecMain.NewBlock();
        vecMain.Assign(new Place(v).Field(0), Int(1)).Assign(new Place(v).Field(1), Int(1))
            .Call(AddFn, [VecType], [Move(new Place(v)), Move(new Place(v))], FunctionBuilder.ReturnPlace, vecDone);
        vecMain.At(vecDone).Return();
        var vecAdd2 = VecAdd() with { Name = "vec_add_again" };
        Expect("two implementations for the same types", [Add(), VecAdd(), vecAdd2, vecMain.Build()], [new("op+", 1), new("op+", 2)], "is ambiguous");

        // A required call with the wrong argument type.
        var wrong = new FunctionBuilder("wrong", IrType.Param(0));
        wrong.Generic("T");
        int plus = wrong.Require(0, PlusT);
        wrong.Argument(IrType.Param(0), "a");
        int wrongDone = wrong.NewBlock();
        wrong.CallRequired(0, plus, [Int(1), Int(2)], FunctionBuilder.ReturnPlace, wrongDone);
        wrong.At(wrongDone).Return();
        Expect("arguments must have the requirement's types", [wrong.Build()], [], "argument 0 of `op+(T, T) -> T` is `i32`");

        // zeroed T needs plain data.
        var zeroed = new FunctionBuilder("zeroed", IrType.Param(0));
        zeroed.Generic("T");
        zeroed.Assign(FunctionBuilder.ReturnPlace, Rvalue.Zero(IrType.Param(0))).Return();
        Expect("`zeroed T` needs `T: plain`", [zeroed.Build()], [], "all-zero bytes are not a valid value");
        var plain = new FunctionBuilder("zeroed", IrType.Param(0));
        plain.Generic("T", TypeConstraint.Plain);
        plain.Assign(FunctionBuilder.ReturnPlace, Rvalue.Zero(IrType.Param(0))).Return();
        Check("`zeroed T` with `T: plain` is accepted", TypeChecker.Check(Module([plain.Build()], []), GcConfig.Default).Count == 0);
    }

    private static IrModule Module(IrFunction[] functions, Implementation[] implementations) =>
        new([new StructDef("Vec", IrType.I32, IrType.I32) { FieldNames = ["x", "y"] }], functions) { Implementations = implementations };

    private void Expect(string name, IrFunction[] functions, Implementation[] implementations, string fragment)
    {
        var errors = TypeChecker.Check(Module(functions, implementations), GcConfig.Default);
        Check($"{name}: rejected", errors.Any(e => e.Message.Contains(fragment)), string.Join(" | ", errors.Select(e => e.Message)));
    }
}
