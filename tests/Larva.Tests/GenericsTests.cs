using Larva.IL;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Encoding;
using static Larva.IR.Operand;
using static Larva.Tests.GenericsPrograms;

namespace Larva.Tests;

/// <summary>Generics, constraints, GC modes and unsafe: end-to-end and negative cases.</summary>
public sealed class GenericsTests : CheckedTest
{
    // ── A valid generic program ────────────────────────────────────

    [Fact]
    public void Generic_program_round_trips_and_monomorphizes()
    {
        var module = ProgramModule();
        var bytes = IrEncoder.Encode(module);
        Check("IR binary round trip", IrDecoder.Decode(bytes).ToString() == module.ToString(), $"{bytes.Length} bytes");

        var result = Compiler.Compile(module);
        Check("generic module compiles", result.Success && result.Diagnostics.Count == 0,
            string.Join(" | ", result.TypeErrors.Select(e => e.ToString()).Concat(result.Diagnostics.Select(d => d.ToString()))));
        if (!result.Success)
            return;

        // Shared mode (default): `first` needs no concrete type, so it is compiled once; the rest do arithmetic on T.
        var names = result.Concrete!.Functions.Select(f => f.Name).ToArray();
        Check("shared instantiations", names.SequenceEqual(["main", "max<i32>", "add<f64>", "add<i128>", "first", "clamp_int<i64>", "max<i64>"]),
            string.Join(", ", names));

        var specialized = Compiler.Compile(module, new CompilationOptions { Generics = GenericsMode.Specialize });
        var specializedNames = specialized.Concrete!.Functions.Select(f => f.Name).ToArray();
        Check("specialized instantiations", specializedNames.SequenceEqual(["main", "max<i32>", "add<f64>", "add<i128>", "first<i32, f64>", "clamp_int<i64>", "max<i64>"]),
            string.Join(", ", specializedNames));

        var addF64 = result.Il!.Functions.Single(f => f.Name == "add<f64>").Body;
        var addI128 = result.Il!.Functions.Single(f => f.Name == "add<i128>").Body;
        Check("add<f64> uses fpu.add", addF64.Operations.Any(o => o.Mnemonic == "fpu.add"));
        Check("add<i128> uses integer add", addI128.Operations.Any(o => o.OpCode == OpCode.Add));
    }

    // ── Constraint evaluation ──────────────────────────────────────

    [Fact]
    public void Constraints_imply_each_other_and_judge_types()
    {
        var context = new TypeContext(new IrModule([Pair, Point], []), []);

        (TypeConstraint A, TypeConstraint B, bool Expected)[] implications =
        [
            (TypeConstraint.Int, TypeConstraint.Numeric, true),
            (TypeConstraint.OneOf(IrType.I32, IrType.I64), TypeConstraint.Int, true),
            (TypeConstraint.Numeric, TypeConstraint.Int, false),
            (TypeConstraint.Copy, TypeConstraint.FixedSize, true),
            (TypeConstraint.Int & !TypeConstraint.Exact(IrType.I256), TypeConstraint.SizeAtMost(128), true),
            (TypeConstraint.Unmanaged, TypeConstraint.Copy, false),
            (TypeConstraint.Managed, !TypeConstraint.Unmanaged, true),
        ];
        foreach (var (a, b, expected) in implications)
            Check($"{a} ⇒ {b} is {expected}", Constraints.Implies(a, b, context) == expected);

        Check("Pair<i32, box i32> rejected (B: copy)",
            !Constraints.Satisfies(IrType.Box(IrType.I32), TypeConstraint.Copy, context));
        Check("struct with gc is managed",
            Constraints.Satisfies(IrType.Struct(PairStruct, IrType.Gc(IrType.I32), IrType.I32), TypeConstraint.Managed, context));
    }

    // ── Rules that must reject programs ────────────────────────────

    [Fact]
    public void Constraint_violations_are_type_errors()
    {
        IrFunction[] library = [BuildMax(), BuildAdd()];

        { // constraint not met at a call site
            var f = new FunctionBuilder("max_bool", IrType.Bool);
            int done = f.NewBlock();
            f.Call(MaxFn, [IrType.Bool], [Bool(true), Bool(false)], FunctionBuilder.ReturnPlace, done);
            f.At(done).Return();
            ExpectType([.. library, f.Build()], TypeErrorKind.UnsatisfiedConstraint);
        }
        { // operation not allowed by the constraint
            var f = GenericBuilder("shift", out var t, ("T", TypeConstraint.Numeric));
            int x = f.Argument(t[0], "x");
            f.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Shl, Copy(x), Copy(x))).Return();
            ExpectType([f.Build()], TypeErrorKind.OperationNotAllowed);
        }
        { // listed types: Add is common to i32 and f32, Shl is not
            var ok = GenericBuilder("pick_add", out var t, ("T", TypeConstraint.OneOf(IrType.I32, IrType.F32)));
            int x = ok.Argument(t[0], "x");
            ok.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(x), Copy(x))).Return();
            ExpectType([ok.Build()], null);

            var bad = GenericBuilder("pick_shl", out t, ("T", TypeConstraint.OneOf(IrType.I32, IrType.F32)));
            x = bad.Argument(t[0], "x");
            bad.Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Shl, Copy(x), Copy(x))).Return();
            ExpectType([bad.Build()], TypeErrorKind.OperationNotAllowed);
        }
        { // generic caller must prove the callee's constraint
            var f = GenericBuilder("outer", out var t, ("U", TypeConstraint.Copy));
            int x = f.Argument(t[0], "x");
            int done = f.NewBlock();
            f.Call(MaxFn, [t[0]], [Copy(x), Copy(x)], FunctionBuilder.ReturnPlace, done);
            f.At(done).Return();
            ExpectType([.. library, f.Build()], TypeErrorKind.UnsatisfiedConstraint);
        }
        { // copy of an unconstrained T is a borrow error; with T: copy it is fine
            var f = GenericBuilder("dup", out var t, ("T", TypeConstraint.Any));
            int x = f.Argument(t[0], "x");
            f.Assign(FunctionBuilder.ReturnPlace, Copy(x)).Return();
            ExpectBorrow([f.Build()], BorrowErrorKind.CopyOfNonCopy);

            var g = GenericBuilder("dup_copy", out t, ("T", TypeConstraint.Copy));
            x = g.Argument(t[0], "x");
            g.Assign(FunctionBuilder.ReturnPlace, Copy(x)).Return();
            ExpectBorrow([g.Build()], null);
        }
        { // struct argument must satisfy the struct's constraint
            var f = new FunctionBuilder("bad_pair", IrType.Unit);
            f.Local(IrType.Struct(PairStruct, IrType.I32, IrType.Box(IrType.I32)), "p");
            f.Return();
            ExpectType([f.Build()], TypeErrorKind.UnsatisfiedConstraint);
        }
    }

    [Fact]
    public void Gc_handles_unsafe_and_owned_borrows_follow_their_rules()
    {
        { // gc handles: a type in every mode (Manual code may hold another domain's objects); making one needs a collector
            var f = new FunctionBuilder("hold", IrType.Unit);
            f.Argument(IrType.Gc(IrType.I32), "g");
            f.Return();
            var function = f.Build();
            ExpectType([function], null, GcConfig.Manual);
            ExpectType([function], null, GcConfig.Default);
            ExpectType([function], null, GcConfig.Plugin("rc-lite", GcCapabilities.RefCounting));
            var g = new FunctionBuilder("mk", IrType.Gc(IrType.I32));
            g.Assign(FunctionBuilder.ReturnPlace, new AllocRvalue(AllocKind.Gc, Int(1))).Return();
            ExpectType([g.Build()], TypeErrorKind.GcNotAvailable, GcConfig.Manual);
            ExpectType([g.Build()], null, GcConfig.Default);
        }
        { // gc objects are immutable outside unsafe
            foreach (bool isUnsafe in new[] { false, true })
            {
                var f = new FunctionBuilder(isUnsafe ? "gc_write_unsafe" : "gc_write", IrType.Unit) { Unsafe = isUnsafe };
                int g = f.Argument(IrType.Gc(IrType.Struct(PointStruct)), "g");
                f.Assign(new Place(g).Deref().Field(0), Int(1)).Return();
                ExpectBorrow([f.Build()], isUnsafe ? null : BorrowErrorKind.MutationThroughGc);
            }
        }
        { // raw pointer deref needs unsafe
            foreach (bool isUnsafe in new[] { false, true })
            {
                var f = new FunctionBuilder(isUnsafe ? "raw_read_unsafe" : "raw_read", IrType.I32) { Unsafe = isUnsafe };
                int p = f.Argument(IrType.Ptr(IrType.I32), "p");
                f.Assign(FunctionBuilder.ReturnPlace, Copy(new Place(p).Deref())).Return();
                ExpectType([f.Build()], isUnsafe ? null : TypeErrorKind.UnsafeRequired);
            }
        }
        { // a borrow into a box owned by the function cannot be returned
            var f = new FunctionBuilder("leak_box", IrType.Ref(IrType.I32));
            int b = f.Argument(IrType.Box(IrType.I32), "b");
            f.Assign(FunctionBuilder.ReturnPlace, Rvalue.Ref(new Place(b).Deref())).Return();
            ExpectBorrow([f.Build()], BorrowErrorKind.ReturnsLocalReference);
        }
    }

    private void ExpectType(IrFunction[] functions, TypeErrorKind? expected, GcConfig? gc = null)
    {
        var module = new IrModule([Pair, Point], functions);
        var errors = TypeChecker.Check(module, gc ?? GcConfig.Default);
        bool pass = expected is null ? errors.Count == 0 : errors.Count >= 1 && errors.All(e => e.Kind == expected);
        Check($"{functions[^1].Name}{(gc is null ? "" : $" [{gc.Mode}]")}: {expected?.ToString() ?? "no type errors"}", pass,
            errors.Count == 0 ? null : string.Join(" | ", errors.Select(e => e.Message)));
    }

    private void ExpectBorrow(IrFunction[] functions, BorrowErrorKind? expected)
    {
        var module = new IrModule([Pair, Point], functions);
        var typeErrors = TypeChecker.Check(module, GcConfig.Default);
        var errors = BorrowChecker.Check(module);
        bool pass = typeErrors.Count == 0 && (expected is null ? errors.Count == 0 : errors.Count == 1 && errors[0].Kind == expected);
        Check($"{functions[^1].Name}: {expected?.ToString() ?? "no borrow errors"}", pass,
            string.Join(" | ", typeErrors.Select(e => e.Message).Concat(errors.Select(e => e.Message))) is { Length: > 0 } m ? m : null);
    }
}
