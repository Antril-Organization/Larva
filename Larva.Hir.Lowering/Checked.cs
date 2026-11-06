namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;

    /// <summary>
    /// <c>checked</c>: integer <c>+ - *</c>, negation, <c>++</c>/<c>--</c>, compound assignments and numeric conversions
    /// that may not keep the value test the result and call <c>System.Fail.Overflow()</c> (which throws
    /// <c>OverflowError</c>) when it does not fit. The tests are plain IR arithmetic (back ends need nothing new):
    /// sums and differences by their signs, products through a wider type (or a division back for 64 bits and more),
    /// conversions by converting back.
    /// </summary>
    internal sealed partial class BodyLowerer
    {
        /// <summary>Whether the code being lowered is in a <c>checked</c> context.</summary>
        private bool overflowChecks;

        private void CheckedStatement(H.CheckedStmt s)
        {
            bool outer = overflowChecks;
            overflowChecks = s.Checked;
            Block(s.Body);
            overflowChecks = outer;
        }

        private void CheckedInto(H.CheckedExpr c, Place target)
        {
            bool outer = overflowChecks;
            overflowChecks = c.Checked;
            Into(target, c.Value);
            overflowChecks = outer;
        }

        /// <summary><c>left op right</c> on <paramref name="type"/>; in a checked context, a failure when it does not fit.</summary>
        private Rvalue Arithmetic(BinaryOp op, Operand left, Operand right, IrType type)
        {
            if (type is Larva.IR.PointerType pointer)
                return PointerOffset(op, left, right, pointer);
            if (!overflowChecks || !type.IsInteger || op is not (BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul))
                return Rvalue.Binary(op, left, right);
            int a = Temp(type), c = Temp(type), r = Temp(type), bad = Temp(IrType.Bool);
            b.Assign(a, left).Assign(c, right).Assign(r, Rvalue.Binary(op, Operand.Copy(a), Operand.Copy(c)));
            bool signed = type.IsSigned;
            var zero = Operand.Const(0, type);
            if (op is BinaryOp.Add or BinaryOp.Sub)
            {
                if (signed)
                {
                    // Overflow flips the sign: a sum leaves both operands' sign, a difference leaves a's while a and c differ.
                    int x = Temp(type), y = Temp(type), both = Temp(type);
                    if (op == BinaryOp.Add)
                        b.Assign(x, Rvalue.Binary(BinaryOp.Xor, Operand.Copy(a), Operand.Copy(r))).Assign(y, Rvalue.Binary(BinaryOp.Xor, Operand.Copy(c), Operand.Copy(r)));
                    else
                        b.Assign(x, Rvalue.Binary(BinaryOp.Xor, Operand.Copy(a), Operand.Copy(c))).Assign(y, Rvalue.Binary(BinaryOp.Xor, Operand.Copy(a), Operand.Copy(r)));
                    b.Assign(both, Rvalue.Binary(BinaryOp.And, Operand.Copy(x), Operand.Copy(y)))
                     .Assign(bad, Rvalue.Binary(BinaryOp.Lt, Operand.Copy(both), zero));
                }
                else
                {
                    b.Assign(bad, op == BinaryOp.Add
                        ? Rvalue.Binary(BinaryOp.Lt, Operand.Copy(r), Operand.Copy(a))
                        : Rvalue.Binary(BinaryOp.Lt, Operand.Copy(a), Operand.Copy(c)));
                }
                OverflowIf(bad);
                return Rvalue.Use(Operand.Copy(r));
            }
            int bits = type.BitWidth ?? 64;
            if (bits <= 32)
            {
                // The product in 64 bits, and whether it survives the trip to the type and back.
                var wide = signed ? IrType.I64 : IrType.U64;
                int wa = Temp(wide), wc = Temp(wide), wr = Temp(wide), narrow = Temp(type), back = Temp(wide);
                b.Assign(wa, Rvalue.Cast(Operand.Copy(a), wide)).Assign(wc, Rvalue.Cast(Operand.Copy(c), wide))
                 .Assign(wr, Rvalue.Binary(BinaryOp.Mul, Operand.Copy(wa), Operand.Copy(wc)))
                 .Assign(narrow, Rvalue.Cast(Operand.Copy(wr), type))
                 .Assign(back, Rvalue.Cast(Operand.Copy(narrow), wide))
                 .Assign(bad, Rvalue.Binary(BinaryOp.Ne, Operand.Copy(back), Operand.Copy(wr)));
                OverflowIf(bad);
                return Rvalue.Use(Operand.Copy(narrow));
            }
            if (right is ConstOperand { Bits: var constant } && constant != 0 && !(signed && constant == -1))
            {
                // Times a constant: a fits when it is between the type's limits divided by the constant (computed now).
                var max = signed ? (System.Numerics.BigInteger.One << (bits - 1)) - 1 : (System.Numerics.BigInteger.One << bits) - 1;
                var min = signed ? -(System.Numerics.BigInteger.One << (bits - 1)) : System.Numerics.BigInteger.Zero;
                // min ≤ a × c ≤ max: dividing by a negative c turns the bounds around.
                var low = CeilDiv(constant > 0 ? min : max, constant);
                var high = FloorDiv(constant > 0 ? max : min, constant);
                int under = Temp(IrType.Bool), over = Temp(IrType.Bool);
                b.Assign(under, Rvalue.Binary(BinaryOp.Lt, Operand.Copy(a), Operand.Const(low, type)))
                 .Assign(over, Rvalue.Binary(BinaryOp.Gt, Operand.Copy(a), Operand.Const(high, type)))
                 .Assign(bad, Rvalue.Binary(BinaryOp.Or, Operand.Copy(under), Operand.Copy(over)));
                OverflowIf(bad);
                return Rvalue.Use(Operand.Copy(r));
            }
            // 64 bits and more: the product divided by a gives c back when it fit (a = 0 always fits; a = -1 overflows
            // only for the smallest c, which must not be divided).
            int nonzero = NewBlock(), divide = NewBlock(), done = NewBlock();
            int test = Temp(IrType.Bool), quotient = Temp(type);
            b.Assign(bad, Operand.Bool(false)).Assign(test, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(a), zero)).If(Operand.Copy(test), done, nonzero);
            At(nonzero);
            if (signed)
            {
                int minusOne = NewBlock();
                b.Assign(test, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(a), Operand.Const(-1, type))).If(Operand.Copy(test), minusOne, divide);
                At(minusOne);
                b.Assign(bad, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(c), Operand.Const(-(System.Numerics.BigInteger.One << (bits - 1)), type)));
                Goto(done);
            }
            else
            {
                Goto(divide);
            }
            At(divide);
            b.Assign(quotient, Rvalue.Binary(BinaryOp.Div, Operand.Copy(r), Operand.Copy(a)))
             .Assign(bad, Rvalue.Binary(BinaryOp.Ne, Operand.Copy(quotient), Operand.Copy(c)));
            Goto(done);
            At(done);
            OverflowIf(bad);
            return Rvalue.Use(Operand.Copy(r));
        }

        /// <summary><c>-x</c>; in a checked context the smallest signed value (or any unsigned one but 0) fails.</summary>
        private Rvalue Negation(Operand value, IrType type)
        {
            if (!overflowChecks || !type.IsInteger)
                return Rvalue.Unary(UnaryOp.Neg, value);
            int x = Temp(type), bad = Temp(IrType.Bool);
            b.Assign(x, value);
            int bits = type.BitWidth ?? 64;
            b.Assign(bad, type.IsSigned
                ? Rvalue.Binary(BinaryOp.Eq, Operand.Copy(x), Operand.Const(-(System.Numerics.BigInteger.One << (bits - 1)), type))
                : Rvalue.Binary(BinaryOp.Ne, Operand.Copy(x), Operand.Const(0, type)));
            OverflowIf(bad);
            return Rvalue.Unary(UnaryOp.Neg, Operand.Copy(x));
        }

        /// <summary>A numeric conversion to an integer; in a checked context, a failure when the value does not survive it.</summary>
        private Rvalue NumericCast(Operand value, IrType from, IrType to)
        {
            if (from == to)
                return Rvalue.Use(value);
            if (!overflowChecks || !to.IsInteger || !(from.IsInteger || from.IsFloat) || Widens(from, to))
                return Rvalue.Cast(value, to);
            int x = Temp(from), r = Temp(to), bad = Temp(IrType.Bool);
            b.Assign(x, value);
            int bits = to.BitWidth ?? 64;
            if (from.IsFloat)
            {
                // In range: above the value below the smallest, below the value past the largest (NaN is neither).
                var low = to.IsSigned ? -System.Numerics.BigInteger.Pow(2, bits - 1) - 1 : System.Numerics.BigInteger.MinusOne;
                var high = to.IsSigned ? System.Numerics.BigInteger.Pow(2, bits - 1) : System.Numerics.BigInteger.Pow(2, bits);
                int above = Temp(IrType.Bool), below = Temp(IrType.Bool), inside = Temp(IrType.Bool);
                b.Assign(above, Rvalue.Binary(BinaryOp.Gt, Operand.Copy(x), FloatConstant((double)low, from)))
                 .Assign(below, Rvalue.Binary(BinaryOp.Lt, Operand.Copy(x), FloatConstant((double)high, from)))
                 .Assign(inside, Rvalue.Binary(BinaryOp.And, Operand.Copy(above), Operand.Copy(below)))
                 .Assign(bad, Rvalue.Unary(UnaryOp.Not, Operand.Copy(inside)));
                OverflowIf(bad);
                return Rvalue.Cast(Operand.Copy(x), to);
            }
            // Integers: the value converts back to itself, and keeps its sign.
            int back = Temp(from), changed = Temp(IrType.Bool), flipped = Temp(IrType.Bool);
            b.Assign(r, Rvalue.Cast(Operand.Copy(x), to))
             .Assign(back, Rvalue.Cast(Operand.Copy(r), from))
             .Assign(changed, Rvalue.Binary(BinaryOp.Ne, Operand.Copy(back), Operand.Copy(x)));
            if (from.IsSigned != to.IsSigned)
            {
                // Signed to unsigned fails for a negative value; unsigned to signed for one that lands negative.
                b.Assign(flipped, from.IsSigned
                    ? Rvalue.Binary(BinaryOp.Lt, Operand.Copy(x), Operand.Const(0, from))
                    : Rvalue.Binary(BinaryOp.Lt, Operand.Copy(r), Operand.Const(0, to)));
                b.Assign(bad, Rvalue.Binary(BinaryOp.Or, Operand.Copy(changed), Operand.Copy(flipped)));
            }
            else
            {
                b.Assign(bad, Operand.Copy(changed));
            }
            OverflowIf(bad);
            return Rvalue.Use(Operand.Copy(r));
        }

        /// <summary>n / d rounded down and up (for any signs).</summary>
        private static System.Numerics.BigInteger FloorDiv(System.Numerics.BigInteger n, System.Numerics.BigInteger d)
        {
            var q = System.Numerics.BigInteger.DivRem(n, d, out var rest);
            return rest != 0 && (rest.Sign < 0) != (d.Sign < 0) ? q - 1 : q;
        }

        private static System.Numerics.BigInteger CeilDiv(System.Numerics.BigInteger n, System.Numerics.BigInteger d) => -FloorDiv(-n, d);

        /// <summary>Whether every value of <paramref name="from"/> is a value of <paramref name="to"/>.</summary>
        private static bool Widens(IrType from, IrType to)
        {
            if (!from.IsInteger || !to.IsInteger)
                return false;
            int f = from.BitWidth ?? 0, t = to.BitWidth ?? 0;
            return from.IsSigned == to.IsSigned ? t >= f : from.IsUnsigned && to.IsSigned && t > f;
        }

        private static Operand FloatConstant(double value, IrType type) =>
            Operand.FloatBits(SoftFloat.FromDouble(value, FloatFormat.Of(type.BitWidth!.Value)), type);

        /// <summary>Calls <c>System.Fail.Overflow()</c> when <paramref name="bad"/> holds.</summary>
        private void OverflowIf(int bad)
        {
            int fail = NewBlock(), ok = NewBlock();
            b.If(Operand.Copy(bad), fail, ok);
            At(fail);
            Emit(lowerer.OverflowFunction(), [], new Place(Temp(IrType.Unit)));
            b.Unreachable();
            open = false;
            At(ok);
        }
    }

    public sealed partial class HirLowerer
    {
        private int overflowFunction = -1;

        /// <summary>The function of <c>System.Fail.Overflow()</c>.</summary>
        internal int OverflowFunction()
        {
            if (overflowFunction >= 0)
                return overflowFunction;
            foreach (var info in Types)
            {
                if (info.Definition.FullName != "System.Fail")
                    continue;
                int m = info.Definition.Methods.FindIndex(x => x.Name == "Overflow");
                if (m >= 0)
                    return overflowFunction = MethodFunctions[H.MemberToken.Def(info.Index, m)];
            }
            throw new LoweringException("`checked` needs System.Fail.Overflow (System.Core)");
        }
    }
}
