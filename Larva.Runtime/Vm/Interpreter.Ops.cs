using System.Numerics;
using Larva.IL;
using Larva.IR;

namespace Larva.Runtime.Vm
{
    public sealed partial class Interpreter
    {
        /// <summary><c>add</c>/<c>sub</c>/<c>mul</c>: integers (wrapped to the operand type), floats, or pointer ± offset.</summary>
        private Value Arithmetic(OpCode op, Value a, Value b)
        {
            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Int && op is OpCode.Add or OpCode.Sub)
                return Value.FromPointer(a.Address.Plus((long)(op == OpCode.Add ? b.Int : -b.Int)), a.Type);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
            {
                var type = a.Kind == ValueKind.Float ? a.Type! : b.Type!;
                if (Numbers.IsWide(type))
                    return WideFloat(op switch { OpCode.Add => FloatOp.Add, OpCode.Sub => FloatOp.Sub, _ => FloatOp.Mul }, a, b, type);
                double x = AsDouble(a), y = AsDouble(b);
                return Value.FromFloat(op switch { OpCode.Add => x + y, OpCode.Sub => x - y, _ => x * y }, type);
            }

            var (l, r) = (Heap.AsInteger(a), Heap.AsInteger(b));
            return Value.FromInt(op switch { OpCode.Add => l + r, OpCode.Sub => l - r, _ => l * r }, a.Type ?? b.Type);
        }

        private static double AsDouble(Value value) => value.Kind == ValueKind.Float ? value.Float : (double)Heap.AsInteger(value);

        /// <summary>Whether a float operation on these runs on wide bits (either operand an f128/f256).</summary>
        private static IrType? WideType(Value a, Value b) =>
            a.Kind == ValueKind.Float && Numbers.IsWide(a.Type) ? a.Type : b.Kind == ValueKind.Float && Numbers.IsWide(b.Type) ? b.Type : null;

        /// <summary>A value as the bits of a wide float of <paramref name="type"/> (a narrower float or an integer converts exactly).</summary>
        private static BigInteger WideBits(Value value, IrType type) => value.Kind == ValueKind.Float
            ? value.FloatBitsAs(type)
            : SoftFloat.FromInteger(Heap.AsInteger(value), Numbers.FormatOf(type));

        private static Value WideFloat(FloatOp op, Value a, Value b, IrType type)
        {
            var format = Numbers.FormatOf(type);
            BigInteger x = WideBits(a, type), y = WideBits(b, type);
            return Value.FromFloatBits(op switch
            {
                FloatOp.Add => SoftFloat.Add(x, y, format),
                FloatOp.Sub => SoftFloat.Sub(x, y, format),
                FloatOp.Mul => SoftFloat.Mul(x, y, format),
                FloatOp.Div => SoftFloat.Div(x, y, format),
                FloatOp.Rem => SoftFloat.Rem(x, y, format),
                FloatOp.Min => SoftFloat.Min(x, y, format),
                _ => SoftFloat.Max(x, y, format),
            }, type);
        }

        private static Value WideFloat(FloatOp op, Value value)
        {
            var type = value.Type!;
            var format = Numbers.FormatOf(type);
            var x = value.Int;
            return Value.FromFloatBits(op switch
            {
                FloatOp.Neg => SoftFloat.Neg(x, format),
                FloatOp.Sqrt => SoftFloat.Sqrt(x, format),
                FloatOp.Abs => SoftFloat.Abs(x, format),
                FloatOp.Floor => SoftFloat.RoundToInteger(x, SoftFloat.Rounding.Floor, format),
                FloatOp.Ceil => SoftFloat.RoundToInteger(x, SoftFloat.Rounding.Ceiling, format),
                FloatOp.Round => SoftFloat.RoundToInteger(x, SoftFloat.Rounding.Nearest, format),
                _ => SoftFloat.RoundToInteger(x, SoftFloat.Rounding.Truncate, format),
            }, type);
        }

        private static bool Compare(Condition condition, Value a, Value b)
        {
            // Floats compare as IEEE 754 says: NaN is unordered (every comparison with it is false, except !=).
            if (WideType(a, b) is { } wide)
            {
                int? wideOrder = SoftFloat.Compare(WideBits(a, wide), WideBits(b, wide), Numbers.FormatOf(wide));
                return condition switch
                {
                    Condition.Eq => wideOrder == 0,
                    Condition.Ne => wideOrder != 0,
                    Condition.Lt => wideOrder < 0,
                    Condition.Le => wideOrder <= 0,
                    Condition.Gt => wideOrder > 0,
                    Condition.Ge => wideOrder >= 0,
                    _ => throw new VmException($"cmp.{condition} is not a boolean condition"),
                };
            }
            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
            {
                double x = AsDouble(a), y = AsDouble(b);
                return condition switch
                {
                    Condition.Eq => x == y,
                    Condition.Ne => x != y,
                    Condition.Lt => x < y,
                    Condition.Le => x <= y,
                    Condition.Gt => x > y,
                    Condition.Ge => x >= y,
                    _ => throw new VmException($"cmp.{condition} is not a boolean condition"),
                };
            }
            int order = Ordinal(a, b);
            return condition switch
            {
                Condition.Eq => order == 0,
                Condition.Ne => order != 0,
                Condition.Lt => order < 0,
                Condition.Le => order <= 0,
                Condition.Gt => order > 0,
                Condition.Ge => order >= 0,
                _ => throw new VmException($"cmp.{condition} is not a boolean condition"),
            };
        }

        private static int Ordinal(Value a, Value b) =>
            WideType(a, b) is { } wide ? SoftFloat.Compare(WideBits(a, wide), WideBits(b, wide), Numbers.FormatOf(wide)) ?? 0 :
            a.Kind == ValueKind.Float || b.Kind == ValueKind.Float
                ? AsDouble(a).CompareTo(AsDouble(b))
                : Heap.AsInteger(a).CompareTo(Heap.AsInteger(b));

        private void Alu(List<Value> stack, AluOp op)
        {
            switch (op)
            {
                case AluOp.Neg or AluOp.Not or AluOp.Inc or AluOp.Dec:
                {
                    var a = Pop(stack);
                    if (op == AluOp.Not && a.Kind == ValueKind.Bool)
                    {
                        stack.Add(Value.FromBool(!a.IsTrue));
                        break;
                    }
                    var v = Heap.AsInteger(a);
                    stack.Add(Value.FromInt(op switch { AluOp.Neg => -v, AluOp.Not => -v - 1, AluOp.Inc => v + 1, _ => v - 1 }, a.Type));
                    break;
                }
                default:
                {
                    var b = Pop(stack);
                    var a = Pop(stack);
                    if (a.Kind == ValueKind.Bool && op is AluOp.And or AluOp.Or or AluOp.Xor)
                    {
                        stack.Add(Value.FromBool(op switch { AluOp.And => a.IsTrue && b.IsTrue, AluOp.Or => a.IsTrue || b.IsTrue, _ => a.IsTrue ^ b.IsTrue }));
                        break;
                    }
                    var (x, y) = (Heap.AsInteger(a), Heap.AsInteger(b));
                    var type = a.Type ?? b.Type;
                    int width = type?.BitWidth ?? 64;
                    var mask = (BigInteger.One << width) - 1;
                    int shift = (int)(y % width);
                    stack.Add(Value.FromInt(op switch
                    {
                        AluOp.Div => y.IsZero ? throw new VmException("division by zero") : BigInteger.Divide(x, y),
                        AluOp.Rem => y.IsZero ? throw new VmException("division by zero") : BigInteger.Remainder(x, y),
                        AluOp.And => x & y,
                        AluOp.Or => x | y,
                        AluOp.Xor => x ^ y,
                        AluOp.Shl => x << shift,
                        AluOp.Shr => x >> shift,
                        AluOp.UShr => (x & mask) >> shift,
                        AluOp.Rol => ((x & mask) << shift | (x & mask) >> (width - shift)) & mask,
                        AluOp.Ror => ((x & mask) >> shift | (x & mask) << (width - shift)) & mask,
                        _ => throw new VmException($"alu.{op} is not supported"),
                    }, type));
                    break;
                }
            }
        }

        private void Fpu(List<Value> stack, FloatOp op)
        {
            if (op is FloatOp.Add or FloatOp.Sub or FloatOp.Mul or FloatOp.Div or FloatOp.Rem or FloatOp.Min or FloatOp.Max)
            {
                var b = Pop(stack);
                var a = Pop(stack);
                if (WideType(a, b) is { } wide)
                {
                    stack.Add(WideFloat(op, a, b, wide));
                    return;
                }
                double x = AsDouble(a), y = AsDouble(b);
                stack.Add(Value.FromFloat(op switch
                {
                    FloatOp.Add => x + y,
                    FloatOp.Sub => x - y,
                    FloatOp.Mul => x * y,
                    FloatOp.Div => x / y,
                    FloatOp.Rem => x % y,
                    FloatOp.Min => Math.Min(x, y),
                    _ => Math.Max(x, y),
                }, a.Type ?? b.Type ?? IrType.F64));
                return;
            }

            var value = Pop(stack);
            if (value.Kind == ValueKind.Float && Numbers.IsWide(value.Type))
            {
                stack.Add(WideFloat(op, value));
                return;
            }
            double v = AsDouble(value);
            stack.Add(Value.FromFloat(op switch
            {
                FloatOp.Neg => -v,
                FloatOp.Sqrt => Math.Sqrt(v),
                FloatOp.Abs => Math.Abs(v),
                FloatOp.Floor => Math.Floor(v),
                FloatOp.Ceil => Math.Ceiling(v),
                FloatOp.Round => Math.Round(v, MidpointRounding.ToEven),
                _ => Math.Truncate(v),
            }, value.Type ?? IrType.F64));
        }
    }
}
