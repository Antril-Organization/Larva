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

    /// <summary>
    /// A reference to a local must not outlive the frame: not stored behind a <c>&amp;mut</c> argument (directly, or by a
    /// callee that may keep it there, judged by the callee's signature), not in a <c>gc</c> object or a global; and what a
    /// call may keep behind a <c>&amp;mut</c> argument keeps the borrowed local alive in the caller.
    /// </summary>
    [Fact]
    public void Borrow_checker_rejects_references_escaping_the_frame()
    {
        // Holder { &i32 }: a struct that can hold a reference; Plain { i32 } cannot.
        StructDef[] structs = [.. BorrowStructs, new StructDef("Holder", IrType.Ref(IrType.I32)), new StructDef("Plain", IrType.I32)];
        var holder = IrType.Struct(BorrowStructs.Length);
        var plain = IrType.Struct(BorrowStructs.Length + 1);

        // Callees, judged by their signatures only (their bodies are empty).
        IrFunction Callee(string name, IrType result, params IrType[] parameters)
        {
            var c = Fn(name, result);
            foreach (var parameter in parameters)
                c.Argument(parameter);
            c.Return();
            return c.Build();
        }
        IrFunction[] callees =
        [
            Callee("keep", IrType.Unit, IrType.RefMut(holder), IrType.Ref(IrType.I32)),      // 0: may store its `&i32` into the holder
            Callee("pick", IrType.Ref(IrType.I32), IrType.Ref(IrType.I32)),                 // 1: may return its argument
            Callee("both", IrType.Unit, IrType.RefMut(IrType.I32), IrType.RefMut(IrType.I32)), // 2: numbers hold no references
            Callee("fill", IrType.Unit, IrType.RefMut(plain), IrType.Ref(IrType.I32)),      // 3: a Plain cannot hold the `&i32`
        ];

        void ExpectWith(FunctionBuilder f, BorrowErrorKind? expected)
        {
            var function = f.Build();
            var module = new IrModule(structs, [.. callees, function]) { Globals = [new GlobalDef("kept", holder)] };
            var errors = BorrowChecker.Check(function, module);
            bool pass = expected is null ? errors.Count == 0 : errors.Count == 1 && errors[0].Kind == expected;
            Check($"{function.Name}: expected {expected?.ToString() ?? "no errors"}", pass,
                $"got {errors.Count}: " + string.Join(" | ", errors.Select(e => e.ToString())));
        }

        { // the callee stores a reference to its own local behind its &mut argument: the frame dies at the return
            var f = Fn("store_local_through_arg", IrType.Unit);
            int h = f.Argument(IrType.RefMut(holder), "h");
            int temp = f.Local(IrType.I32, "temp");
            f.Assign(temp, Int(42)).Assign(new Place(h).Deref().Field(0), Rvalue.Ref(temp)).Return();
            ExpectWith(f, BorrowErrorKind.EscapingReference);
        }
        { // ...but a reference into what another argument lends is the caller's business
            var f = Fn("store_arg_through_arg", IrType.Unit);
            int h = f.Argument(IrType.RefMut(holder), "h");
            int v = f.Argument(IrType.Ref(IrType.I32), "v");
            f.Assign(new Place(h).Deref().Field(0), Rvalue.Ref(new Place(v).Deref())).Return();
            ExpectWith(f, null);
        }
        { // a call that may keep the reference behind its &mut argument: the local borrowed must outlive the holder
            var f = Fn("keep_through_call", IrType.I32);
            int h = f.Local(holder, "h", mutable: true);
            int local = f.Local(IrType.I32, "local");
            int r = f.Temp(IrType.RefMut(holder));
            int v = f.Temp(IrType.Ref(IrType.I32));
            int unit = f.Temp(IrType.Unit);
            int after = f.NewBlock();
            f.Assign(h, Rvalue.ZeroFiller(holder)).StorageLive(local).Assign(local, Int(1)).Assign(r, Rvalue.RefMut(h)).Assign(v, Rvalue.Ref(local));
            f.Call(0, [Move(r), Move(v)], new Place(unit), after);
            f.At(after).StorageDead(local).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(h).Field(0).Deref())).Return();
            ExpectWith(f, BorrowErrorKind.DroppedWhileBorrowed);
        }
        { // the same call, lending the caller's holder: the escape is reported at the call
            var f = Fn("keep_through_call_into_caller", IrType.Unit);
            int h = f.Argument(IrType.RefMut(holder), "h");
            int local = f.Local(IrType.I32, "local");
            int r = f.Temp(IrType.RefMut(holder));
            int v = f.Temp(IrType.Ref(IrType.I32));
            int unit = f.Temp(IrType.Unit);
            int after = f.NewBlock();
            f.Assign(local, Int(1)).Assign(r, Rvalue.RefMut(new Place(h).Deref())).Assign(v, Rvalue.Ref(local));
            f.Call(0, [Move(r), Move(v)], new Place(unit), after);
            f.At(after).Return();
            ExpectWith(f, BorrowErrorKind.EscapingReference);
        }
        { // a call whose result may alias its argument, stored where it outlives the local
            var f = Fn("pick_outlives_local", IrType.I32);
            int h = f.Local(holder, "h", mutable: true);
            int other = f.Local(IrType.I32, "other");
            int v = f.Temp(IrType.Ref(IrType.I32));
            int after = f.NewBlock();
            f.StorageLive(other).Assign(other, Int(1)).Assign(v, Rvalue.Ref(other));
            f.Call(1, [Move(v)], new Place(h).Field(0), after);
            f.At(after).StorageDead(other).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(h).Field(0).Deref())).Return();
            ExpectWith(f, BorrowErrorKind.DroppedWhileBorrowed);
        }
        { // &mut arguments whose pointee cannot hold a reference keep nothing: the loans stay apart
            foreach (var (name, callee, lent) in new[] { ("both_numbers", 2, IrType.I32), ("fill_plain", 3, (IrType)plain) })
            {
                var f = Fn(name, IrType.I32);
                int a = f.Local(lent, "a", mutable: true);
                int b = f.Local(IrType.I32, "b", mutable: true);
                int ra = f.Temp(IrType.RefMut(lent));
                int rb = f.Temp(callee == 2 ? IrType.RefMut(IrType.I32) : IrType.Ref(IrType.I32));
                int unit = f.Temp(IrType.Unit);
                int after = f.NewBlock();
                f.StorageLive(b).Assign(a, callee == 2 ? Int(1) : Rvalue.ZeroFiller(lent)).Assign(b, Int(2))
                 .Assign(ra, Rvalue.RefMut(a)).Assign(rb, callee == 2 ? Rvalue.RefMut(b) : Rvalue.Ref(b));
                f.Call(callee, [Move(ra), Move(rb)], new Place(unit), after);
                f.At(after).StorageDead(b).Assign(FunctionBuilder.ReturnPlace, callee == 2 ? Copy(a) : Copy(new Place(a).Field(0))).Return();
                ExpectWith(f, null);
            }
        }
        { // a reference in a gc object: nothing it could borrow lives as long
            var f = new FunctionBuilder("ref_into_gc", IrType.Unit) { Unsafe = true };
            int g = f.Argument(IrType.Gc(holder), "g");
            int v = f.Argument(IrType.Ref(IrType.I32), "v");
            f.Assign(new Place(g).Deref().Field(0), Rvalue.Ref(new Place(v).Deref())).Return();
            ExpectWith(f, BorrowErrorKind.EscapingReference);
        }
        { // a gc object made from a value that borrows
            var f = Fn("gc_of_borrowing_value", IrType.Unit);
            int x = f.Local(IrType.I32, "x");
            int h = f.Local(holder, "h");
            int g = f.Local(IrType.Gc(holder), "g");
            f.Assign(x, Int(1)).Assign(new Place(h).Field(0), Rvalue.Ref(x)).Assign(g, Rvalue.Gc(Move(h))).Return();
            ExpectWith(f, BorrowErrorKind.EscapingReference);
        }
        { // a reference in a global
            var f = Fn("ref_into_global", IrType.Unit);
            int v = f.Argument(IrType.Ref(IrType.I32), "v");
            int h = f.Local(holder, "h");
            f.Assign(new Place(h).Field(0), Rvalue.Ref(new Place(v).Deref())).StoreGlobal(0, Move(h)).Return();
            ExpectWith(f, BorrowErrorKind.EscapingReference);
        }
        { // a box the function owns dies with the frame: storing a reference to a local in it is fine
            var f = Fn("ref_into_own_box", IrType.I32);
            int x = f.Local(IrType.I32, "x");
            int empty = f.Local(holder, "empty");
            int b = f.Local(IrType.Box(holder), "b", mutable: true);
            f.Assign(x, Int(1)).Assign(empty, Rvalue.ZeroFiller(holder)).Assign(b, Rvalue.Box(Move(empty)))
             .Assign(new Place(b).Deref().Field(0), Rvalue.Ref(x))
             .Assign(FunctionBuilder.ReturnPlace, Copy(new Place(b).Deref().Field(0).Deref())).Return();
            ExpectWith(f, null);
        }
    }

    /// <summary>
    /// A reference to a reference (or to a struct holding one) carries the loans of what it points at: what is read back
    /// through it (<c>*rr</c>, <c>(*h).field</c>) still borrows the original place, which can neither change nor die meanwhile.
    /// </summary>
    [Fact]
    public void Borrow_checker_keeps_loans_through_references_to_references()
    {
        StructDef[] structs = [.. BorrowStructs, new StructDef("Holder", IrType.Ref(IrType.I32))];
        var holder = IrType.Struct(BorrowStructs.Length);

        void ExpectWith(FunctionBuilder f, BorrowErrorKind? expected)
        {
            var function = f.Build();
            var errors = BorrowChecker.Check(function, new IrModule(structs, [function]) { Globals = [new GlobalDef("kept", IrType.Ref(IrType.I32))] });
            bool pass = expected is null ? errors.Count == 0 : errors.Count == 1 && errors[0].Kind == expected;
            Check($"{function.Name}: expected {expected?.ToString() ?? "no errors"}", pass,
                $"got {errors.Count}: " + string.Join(" | ", errors.Select(e => e.ToString())));
        }

        { // x = 1; r = &x; rr = &r; p = *rr; x = 2; return *p
            var f = Fn("write_through_ref_to_ref", IrType.I32);
            int x = f.Local(IrType.I32, "x", mutable: true);
            int r = f.Local(IrType.Ref(IrType.I32), "r");
            int rr = f.Local(IrType.Ref(IrType.Ref(IrType.I32)), "rr");
            int p = f.Local(IrType.Ref(IrType.I32), "p");
            f.Assign(x, Int(1)).Assign(r, Rvalue.Ref(x)).Assign(rr, Rvalue.Ref(r)).Assign(p, Copy(new Place(rr).Deref()))
             .Assign(x, Int(2)).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(p).Deref())).Return();
            ExpectWith(f, BorrowErrorKind.ConflictingBorrow);
        }
        { // the same, with x's storage ending while *rr is still used
            var f = Fn("dead_through_ref_to_ref", IrType.I32);
            int x = f.Local(IrType.I32, "x");
            int r = f.Local(IrType.Ref(IrType.I32), "r");
            int rr = f.Local(IrType.Ref(IrType.Ref(IrType.I32)), "rr");
            int p = f.Local(IrType.Ref(IrType.I32), "p");
            f.StorageLive(x).Assign(x, Int(1)).Assign(r, Rvalue.Ref(x)).Assign(rr, Rvalue.Ref(r)).Assign(p, Copy(new Place(rr).Deref()))
             .StorageDead(x).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(p).Deref())).Return();
            ExpectWith(f, BorrowErrorKind.DroppedWhileBorrowed);
        }
        { // through a struct: h.0 = &x; rh = &h; p = (*rh).0; x dies; *p
            var f = Fn("dead_through_ref_to_holder", IrType.I32);
            int x = f.Local(IrType.I32, "x");
            int h = f.Local(holder, "h");
            int rh = f.Local(IrType.Ref(holder), "rh");
            int p = f.Local(IrType.Ref(IrType.I32), "p");
            f.StorageLive(x).Assign(x, Int(1)).Assign(new Place(h).Field(0), Rvalue.Ref(x)).Assign(rh, Rvalue.Ref(h))
             .Assign(p, Copy(new Place(rh).Deref().Field(0)))
             .StorageDead(x).Assign(FunctionBuilder.ReturnPlace, Copy(new Place(p).Deref())).Return();
            ExpectWith(f, BorrowErrorKind.DroppedWhileBorrowed);
        }
        { // a reference read back through a reference to a reference still cannot be kept in a global
            var f = Fn("global_through_ref_to_ref", IrType.Unit);
            int v = f.Argument(IrType.Ref(IrType.I32), "v");
            int rr = f.Local(IrType.Ref(IrType.Ref(IrType.I32)), "rr");
            int p = f.Local(IrType.Ref(IrType.I32), "p");
            f.Assign(rr, Rvalue.Ref(v)).Assign(p, Copy(new Place(rr).Deref())).StoreGlobal(0, Copy(p)).Return();
            ExpectWith(f, BorrowErrorKind.EscapingReference);
        }
        { // a reference to a reference is used, and the original place changes only after both are dead
            var f = Fn("ref_to_ref_then_write", IrType.I32);
            int x = f.Local(IrType.I32, "x", mutable: true);
            int r = f.Local(IrType.Ref(IrType.I32), "r");
            int rr = f.Local(IrType.Ref(IrType.Ref(IrType.I32)), "rr");
            int y = f.Local(IrType.I32, "y");
            f.Assign(x, Int(1)).Assign(r, Rvalue.Ref(x)).Assign(rr, Rvalue.Ref(r)).Assign(y, Copy(new Place(rr).Deref().Deref()))
             .Assign(x, Int(2)).Assign(FunctionBuilder.ReturnPlace, Copy(y)).Return();
            ExpectWith(f, null);
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
