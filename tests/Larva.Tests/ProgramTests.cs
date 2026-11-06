using System.Globalization;
using System.Numerics;
using Larva.IL;
using Larva.IR;
using Larva.IR.Analysis;
using Larva.IR.Encoding;
using Larva.IR.Lowering;
using static Larva.IR.Operand;

namespace Larva.Tests;

/// <summary>
/// The first program (sum / bump / main): IR printing and binary encoding, lowering to IL, the borrow checker's cases and
/// modes, and the wide numeric types. These were the inline tests of the old runner's Program.cs.
/// </summary>
public sealed class ProgramTests : CheckedTest
{
    private static readonly IrModule Module = BasicProgram.Module();
    private static readonly IrType Point = IrType.Struct(BasicProgram.PointStruct);

    // A struct that owns memory (a plain one like Point is copied, so moving it leaves it usable).
    private static readonly StructDef[] BorrowStructs = [.. Module.Structs, new StructDef("Owner", IrType.I32, IrType.Box(IrType.I32))];
    private static readonly IrType Owner = IrType.Struct(Module.Structs.Length);

    [Fact]
    public void Ir_prints_and_round_trips_through_the_binary_encoding()
    {
        string text = Module.ToString();
        Check("IR prints its structs and functions", text.Contains("struct Point") && text.Contains("fn sum(") && text.Contains("fn main()"), text);
        byte[] bytes = IrEncoder.Encode(Module);
        Check("IR round trip", IrDecoder.Decode(bytes).ToString() == text, $"{bytes.Length} bytes ({IrEncoder.SizeInNibbles(Module)} nibbles)");
    }

    [Fact]
    public void Lowering_to_il_peepholes_and_round_trips_each_function()
    {
        var il = IlLowering.Lower(Module);
        var unoptimized = IlLowering.Lower(Module, optimize: false);
        int before = unoptimized.Functions.Sum(f => f.Body.Operations.Length), after = il.Functions.Sum(f => f.Body.Operations.Length);
        Check("peephole does not grow the code", after <= before, $"{before} -> {after} ops");
        foreach (var function in il.Functions)
        {
            byte[] bytes = function.Body.Encode();
            var decoded = OperationBlock.Decode(function.Body.BlockId, bytes);
            Check($"IL {function.Name}: round trip", decoded.Operations.SequenceEqual(function.Body.Operations), $"{bytes.Length} bytes");
        }
    }

    [Fact]
    public void Valid_module_compiles_without_diagnostics()
    {
        var compiled = Compiler.Compile(Module);
        Check("valid module", compiled.Success && compiled.Diagnostics.Count == 0, string.Join(" | ", compiled.Diagnostics));
    }

    [Fact]
    public void Borrow_checker_finds_each_error_kind()
    {
        { // use after move
            var f = Fn("use_after_move", IrType.I32);
            int p = f.Argument(Owner, "p");
            int q = f.Local(Owner, "q");
            f.Assign(q, Move(p)).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(p).Field(0))).Return();
            Expect(f.Build(), BorrowErrorKind.UseOfMoved);
        }
        { // read while mutably borrowed, then the borrow is used again
            var f = Fn("read_while_mut_borrowed", IrType.I32);
            int x = f.Local(IrType.I32, "x", mutable: true);
            int r = f.Local(IrType.RefMut(IrType.I32), "r");
            f.Assign(x, Int(1)).Assign(r, Rvalue.RefMut(x))
             .Assign(FunctionBuilder.ReturnPlace, Copy(x))
             .Assign(new Place(r).Deref(), Int(2))
             .Return();
            Expect(f.Build(), BorrowErrorKind.ConflictingBorrow);
        }
        { // NLL: the borrow is dead before x is used again
            var f = Fn("nll_ok", IrType.I32);
            int x = f.Local(IrType.I32, "x", mutable: true);
            int r = f.Local(IrType.RefMut(IrType.I32), "r");
            f.Assign(x, Int(1)).Assign(r, Rvalue.RefMut(x))
             .Assign(new Place(r).Deref(), Int(2))
             .Assign(FunctionBuilder.ReturnPlace, Copy(x))
             .Return();
            Expect(f.Build(), null);
        }
        { // two shared borrows at once are fine
            var f = Fn("shared_ok", IrType.I32);
            int x = f.Local(IrType.I32, "x");
            int a = f.Local(IrType.Ref(IrType.I32), "a");
            int b = f.Local(IrType.Ref(IrType.I32), "b");
            f.Assign(x, Int(5)).Assign(a, Rvalue.Ref(x)).Assign(b, Rvalue.Ref(x))
             .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(new Place(a).Deref()), Copy(new Place(b).Deref())))
             .Return();
            Expect(f.Build(), null);
        }
        { // two live mutable borrows
            var f = Fn("double_mut", IrType.Unit);
            int x = f.Local(IrType.I32, "x", mutable: true);
            int a = f.Local(IrType.RefMut(IrType.I32), "a");
            int b = f.Local(IrType.RefMut(IrType.I32), "b");
            f.Assign(x, Int(5)).Assign(a, Rvalue.RefMut(x)).Assign(b, Rvalue.RefMut(x))
             .Assign(new Place(a).Deref(), Int(1)).Assign(new Place(b).Deref(), Int(2))
             .Return();
            Expect(f.Build(), BorrowErrorKind.ConflictingBorrow);
        }
        { // immutable assigned twice
            var f = Fn("assign_twice", IrType.Unit);
            int x = f.Local(IrType.I32, "x");
            f.Assign(x, Int(1)).Assign(x, Int(2)).Return();
            Expect(f.Build(), BorrowErrorKind.AssignToImmutable);
        }
        { // &mut of an immutable local
            var f = Fn("borrow_mut_immutable", IrType.Unit);
            int x = f.Local(IrType.I32, "x");
            int r = f.Temp(IrType.RefMut(IrType.I32));
            f.Assign(x, Int(1)).Assign(r, Rvalue.RefMut(x)).Return();
            Expect(f.Build(), BorrowErrorKind.BorrowMutOfImmutable);
        }
        { // write through &
            var f = Fn("write_through_shared", IrType.Unit);
            int p = f.Argument(IrType.Ref(Point), "p");
            f.Assign(new Place(p).Deref().Field(0), Int(1)).Return();
            Expect(f.Build(), BorrowErrorKind.MutationThroughSharedReference);
        }
        { // move out of a reference
            var f = Fn("move_out_of_ref", IrType.Unit);
            int p = f.Argument(IrType.Ref(Owner), "p");
            int q = f.Local(Owner, "q");
            f.Assign(q, Move(new Place(p).Deref())).Return();
            Expect(f.Build(), BorrowErrorKind.MoveOutOfReference);
        }
        { // copy of a struct
            var f = Fn("copy_struct", Owner);
            int p = f.Argument(Owner, "p");
            f.Assign(FunctionBuilder.ReturnPlace, Copy(p)).Return();
            Expect(f.Build(), BorrowErrorKind.CopyOfNonCopy);
        }
        { // uninitialized read on one branch only
            var f = Fn("maybe_uninit", IrType.I32);
            int c = f.Argument(IrType.Bool, "c");
            int x = f.Local(IrType.I32, "x");
            int set = f.NewBlock(), join = f.NewBlock();
            f.If(Copy(c), set, join);
            f.At(set).Assign(x, Int(1)).Goto(join);
            f.At(join).Assign(FunctionBuilder.ReturnPlace, Copy(x)).Return();
            Expect(f.Build(), BorrowErrorKind.UseOfUninitialized);
        }
        { // borrowed local goes out of scope while the reference is still used
            var f = Fn("dangling", IrType.I32);
            int x = f.Local(IrType.I32, "x");
            int r = f.Local(IrType.Ref(IrType.I32), "r");
            f.StorageLive(x).Assign(x, Int(1)).Assign(r, Rvalue.Ref(x))
             .StorageDead(x)
             .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(r).Deref()))
             .Return();
            Expect(f.Build(), BorrowErrorKind.DroppedWhileBorrowed);
        }
        { // returning a reference to a local
            var f = Fn("return_local_ref", IrType.Ref(IrType.I32));
            int x = f.Local(IrType.I32, "x");
            f.Assign(x, Int(1)).Assign(FunctionBuilder.ReturnPlace, Rvalue.Ref(x)).Return();
            Expect(f.Build(), BorrowErrorKind.ReturnsLocalReference);
        }
        { // returning a reborrow of an argument is fine
            var f = Fn("return_arg_field", IrType.Ref(IrType.I32));
            int p = f.Argument(IrType.Ref(Point), "p");
            f.Assign(FunctionBuilder.ReturnPlace, Rvalue.Ref(new Place(p).Deref().Field(1))).Return();
            Expect(f.Build(), null);
        }
        { // loop: a fresh &mut per iteration is fine
            var f = Fn("loop_borrow_ok", IrType.Unit);
            int x = f.Local(IrType.I32, "x", mutable: true);
            int r = f.Local(IrType.RefMut(IrType.I32), "r", mutable: true);
            int head = f.NewBlock(), exit = f.NewBlock();
            f.Assign(x, Int(0)).Goto(head);
            f.At(head).Assign(r, Rvalue.RefMut(x)).Assign(new Place(r).Deref(), Int(1)).If(Bool(true), head, exit);
            f.At(exit).Return();
            Expect(f.Build(), null);
        }
    }

    [Fact]
    public void Borrow_check_mode_decides_between_error_warning_and_nothing()
    {
        // The same broken program with the checker off, warning-only and enforced.
        var brokenFn = new FunctionBuilder("assign_twice", IrType.Unit);
        int brokenX = brokenFn.Local(IrType.I32, "x");
        brokenFn.Assign(brokenX, Int(1)).Assign(brokenX, Int(2)).Return();
        var broken = new IrModule(Module.Structs, [brokenFn.Build()]);

        foreach (var mode in Enum.GetValues<BorrowCheckMode>())
        {
            var result = Compiler.Compile(broken, new CompilationOptions { BorrowCheck = mode });
            string found = $"IL {(result.Success ? "produced" : "refused")}, {result.Diagnostics.Count} diagnostic(s)"
                + (result.Diagnostics.Count > 0 ? $" [{result.Diagnostics[0].Severity}]" : "");
            bool pass = mode switch
            {
                BorrowCheckMode.Off => result.Success && result.Diagnostics.Count == 0,
                BorrowCheckMode.Warn => result.Success && result.Diagnostics.Count == 1 && result.Diagnostics[0].Severity.ToString() == "Warning",
                BorrowCheckMode.Enforce => !result.Success && result.Diagnostics.Count == 1 && result.Diagnostics[0].Severity.ToString() == "Error",
                _ => true,
            };
            Check($"mode {mode}", pass, found);
        }
    }

    [Fact]
    public void Wide_numeric_types_encode_lower_and_check_their_constant_ranges()
    {
        var one128 = BigInteger.Parse("3FFF", NumberStyles.HexNumber) << 112;  // 1.0 in binary128
        var one256 = BigInteger.Parse("3FFFF", NumberStyles.HexNumber) << 236; // 1.0 in binary256
        var u256Max = (BigInteger.One << 256) - 1;
        var i128Min = -(BigInteger.One << 127);

        var f = new FunctionBuilder("wide", IrType.F128);
        int a = f.Argument(IrType.U128, "a");
        int x = f.Local(IrType.I128, "x");
        int y = f.Local(IrType.I256, "y");
        int z = f.Local(IrType.U256, "z");
        int h = f.Local(IrType.F16, "h");
        int q = f.Local(IrType.F128, "q");
        int o = f.Local(IrType.F256, "o");
        int s = f.Local(IrType.U128, "s");
        f.Assign(x, Int(i128Min, IrType.I128))
         .Assign(y, Int(5, IrType.I256))
         .Assign(z, Int(u256Max, IrType.U256))
         .Assign(h, Half((Half)1.5))
         .Assign(q, FloatBits(one128, IrType.F128))
         .Assign(o, FloatBits(one256, IrType.F256))
         .Assign(s, Rvalue.Binary(BinaryOp.Shr, Copy(a), Int(3, IrType.U128)))
         .Assign(FunctionBuilder.ReturnPlace, Rvalue.Binary(BinaryOp.Add, Copy(q), Copy(q)))
         .Return();
        var wide = new IrModule([], [f.Build()]);

        var wideBytes = IrEncoder.Encode(wide);
        Check("wide IR round trip", IrDecoder.Decode(wideBytes).ToString() == wide.ToString(), $"{wideBytes.Length} bytes");

        var wideResult = Compiler.Compile(wide);
        Check("wide module compiles without diagnostics", wideResult.Success && wideResult.Diagnostics.Count == 0, string.Join(" | ", wideResult.Diagnostics));
        Require();
        var body = wideResult.Il!.Functions[0].Body;
        Check("wide IL round trip", OperationBlock.Decode(body.BlockId, body.Encode()).Operations.SequenceEqual(body.Operations));

        foreach (var (value, type) in new[] { (BigInteger.One << 127, IrType.I128), (BigInteger.MinusOne, IrType.U256), (BigInteger.One << 16, IrType.F16) })
        {
            bool rejected;
            try { Int(value, type); rejected = false; }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check($"range check {type}: rejects {value}", rejected);
        }
    }

    private static FunctionBuilder Fn(string name, IrType ret) => new(name, ret);

    private void Expect(IrFunction function, BorrowErrorKind? expected)
    {
        var single = new IrModule(BorrowStructs, [function]);
        var errors = BorrowChecker.Check(single);
        bool pass = expected is null ? errors.Count == 0 : errors.Count == 1 && errors[0].Kind == expected;
        Check($"{function.Name}: expected {expected?.ToString() ?? "no errors"}", pass,
            $"got {errors.Count}: " + string.Join(" | ", errors.Select(e => e.ToString())));
    }
}
