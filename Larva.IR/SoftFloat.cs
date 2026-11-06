using System.Numerics;

namespace Larva
{
    /// <summary>An IEEE 754 binary format: the widths of its exponent and fraction fields (the sign takes one more bit).</summary>
    internal readonly record struct FloatFormat(int ExponentBits, int FractionBits)
    {
        public int Bits => 1 + ExponentBits + FractionBits;
        public int Bias => (1 << (ExponentBits - 1)) - 1;
        /// <summary>The exponent of the leading bit of the smallest normal number (subnormals share it).</summary>
        public int MinExponent => 1 - Bias;
        public int MaxExponent => Bias;
        public int ExponentMask => (1 << ExponentBits) - 1;
        public BigInteger SignBit => BigInteger.One << (Bits - 1);
        public BigInteger FractionMask => (BigInteger.One << FractionBits) - 1;
        public BigInteger Mask => (BigInteger.One << Bits) - 1;
        /// <summary>The canonical quiet NaN (positive, the top fraction bit set).</summary>
        public BigInteger NaN => (BigInteger)ExponentMask << FractionBits | BigInteger.One << (FractionBits - 1);
        public BigInteger Infinity(bool negative) => (negative ? SignBit : BigInteger.Zero) | (BigInteger)ExponentMask << FractionBits;
        public BigInteger Zero(bool negative) => negative ? SignBit : BigInteger.Zero;

        public static readonly FloatFormat Half = new(5, 10);
        public static readonly FloatFormat Single = new(8, 23);
        public static readonly FloatFormat Double = new(11, 52);
        public static readonly FloatFormat Quad = new(15, 112);
        public static readonly FloatFormat Octuple = new(19, 236);

        public static FloatFormat Of(int bits) => bits switch
        {
            16 => Half, 32 => Single, 64 => Double, 128 => Quad, 256 => Octuple,
            _ => throw new ArgumentException($"there is no {bits}-bit float format"),
        };
    }

    /// <summary>
    /// Floating-point arithmetic in software on bit patterns held as <see cref="BigInteger"/>, for every width (f16 to f256):
    /// what the VM computes for f128 and f256, what the JIT's runtime routines compute for the direct x86-64 back end (the C
    /// runtime, larva_rt.c, mirrors this code for ahead-of-time executables), and how the compiler reads wide literals.
    /// Every result is correctly rounded (to nearest, ties to even); NaNs come out as the canonical quiet NaN; the sign of a
    /// zero follows IEEE 754 (x - x is +0, -0 + -0 is -0).
    /// </summary>
    internal static class SoftFloat
    {
        private enum Class : byte { Zero, Finite, Infinity, NaN }

        /// <summary>A number taken apart: <c>Significand × 2^Exponent</c> for finite ones (the significand holds the hidden bit).</summary>
        private readonly record struct Unpacked(bool Negative, Class Class, BigInteger Significand, int Exponent)
        {
            public Unpacked Negated() => this with { Negative = !Negative };
        }

        private static Unpacked Unpack(BigInteger bits, FloatFormat f)
        {
            bits &= f.Mask;
            bool negative = !(bits & f.SignBit).IsZero;
            int e = (int)((bits >> f.FractionBits) & f.ExponentMask);
            var fraction = bits & f.FractionMask;
            if (e == f.ExponentMask)
                return new(negative, fraction.IsZero ? Class.Infinity : Class.NaN, BigInteger.Zero, 0);
            if (e == 0)
                return fraction.IsZero ? new(negative, Class.Zero, BigInteger.Zero, 0) : new(negative, Class.Finite, fraction, f.MinExponent - f.FractionBits);
            return new(negative, Class.Finite, fraction | BigInteger.One << f.FractionBits, e - f.Bias - f.FractionBits);
        }

        /// <summary>
        /// <c>magnitude × 2^exponent</c> rounded to nearest even into the format (<paramref name="sticky"/>: the exact value has
        /// more, nonzero bits below the magnitude's lowest one), with overflow to infinity and gradual underflow.
        /// </summary>
        private static BigInteger Pack(bool negative, BigInteger magnitude, int exponent, bool sticky, FloatFormat f)
        {
            if (magnitude.IsZero)
                return f.Zero(negative);
            int msb = (int)magnitude.GetBitLength() - 1;
            int e = exponent + msb;              // the exponent of the leading bit
            int shift = msb - f.FractionBits;    // bits to drop (negative: to make room for)
            if (e < f.MinExponent)
            {
                shift += f.MinExponent - e;      // a subnormal keeps fewer bits
                e = f.MinExponent;
            }
            if (shift <= 0)
            {
                magnitude <<= -shift;
            }
            else
            {
                bool round = !((magnitude >> (shift - 1)) & 1).IsZero;
                bool below = sticky || !(magnitude & ((BigInteger.One << (shift - 1)) - 1)).IsZero;
                magnitude >>= shift;
                if (round && (below || !magnitude.IsEven))
                {
                    magnitude += 1;
                    if (magnitude.GetBitLength() > f.FractionBits + 1)
                    {
                        magnitude >>= 1;
                        e++;
                    }
                }
            }
            if (e > f.MaxExponent)
                return f.Infinity(negative);
            var sign = negative ? f.SignBit : BigInteger.Zero;
            if ((magnitude >> f.FractionBits).IsZero)
                return sign | magnitude;         // subnormal: the exponent field is zero
            return sign | (BigInteger)(e + f.Bias) << f.FractionBits | (magnitude & f.FractionMask);
        }

        private static BigInteger Repack(Unpacked x, FloatFormat f) => x.Class switch
        {
            Class.NaN => f.NaN,
            Class.Infinity => f.Infinity(x.Negative),
            Class.Zero => f.Zero(x.Negative),
            _ => Pack(x.Negative, x.Significand, x.Exponent, false, f),
        };

        public static bool IsNaN(BigInteger bits, FloatFormat f) => Unpack(bits, f).Class == Class.NaN;
        public static bool IsInfinity(BigInteger bits, FloatFormat f) => Unpack(bits, f).Class == Class.Infinity;
        public static bool IsNegative(BigInteger bits, FloatFormat f) => !(bits & f.SignBit).IsZero;

        // ── Arithmetic ─────────────────────────────────────────

        public static BigInteger Add(BigInteger a, BigInteger b, FloatFormat f) => Add(Unpack(a, f), Unpack(b, f), f);
        public static BigInteger Sub(BigInteger a, BigInteger b, FloatFormat f) => Add(Unpack(a, f), Unpack(b, f).Negated(), f);

        private static BigInteger Add(Unpacked x, Unpacked y, FloatFormat f)
        {
            if (x.Class == Class.NaN || y.Class == Class.NaN)
                return f.NaN;
            if (x.Class == Class.Infinity)
                return y.Class == Class.Infinity && x.Negative != y.Negative ? f.NaN : f.Infinity(x.Negative);
            if (y.Class == Class.Infinity)
                return f.Infinity(y.Negative);
            if (x.Class == Class.Zero)
                return y.Class == Class.Zero ? f.Zero(x.Negative && y.Negative) : Repack(y, f);
            if (y.Class == Class.Zero)
                return Repack(x, f);
            if (x.Exponent < y.Exponent)
                (x, y) = (y, x);
            // Guard bits below both, so the smaller one's dropped bits only reach the result as a sticky bit.
            const int Guard = 64;
            var sx = x.Significand << Guard;
            var sy = y.Significand << Guard;
            int d = x.Exponent - y.Exponent;
            bool sticky = false;
            if (d > 0)
            {
                if (d >= (int)sy.GetBitLength())
                {
                    sticky = !sy.IsZero;
                    sy = BigInteger.Zero;
                }
                else
                {
                    sticky = !(sy & ((BigInteger.One << d) - 1)).IsZero;
                    sy >>= d;
                }
            }
            int exponent = x.Exponent - Guard;
            if (x.Negative == y.Negative)
                return Pack(x.Negative, sx + sy, exponent, sticky, f);
            if (sx >= sy)
            {
                // The dropped bits of the smaller one make the difference a little less than sx - sy.
                var magnitude = sticky ? sx - sy - 1 : sx - sy;
                return magnitude.IsZero && !sticky ? f.Zero(false) : Pack(x.Negative, magnitude, exponent, sticky, f);
            }
            return Pack(y.Negative, sy - sx, exponent, sticky, f);
        }

        public static BigInteger Mul(BigInteger a, BigInteger b, FloatFormat f)
        {
            var x = Unpack(a, f);
            var y = Unpack(b, f);
            if (x.Class == Class.NaN || y.Class == Class.NaN)
                return f.NaN;
            bool negative = x.Negative != y.Negative;
            if (x.Class == Class.Infinity || y.Class == Class.Infinity)
                return x.Class == Class.Zero || y.Class == Class.Zero ? f.NaN : f.Infinity(negative);
            if (x.Class == Class.Zero || y.Class == Class.Zero)
                return f.Zero(negative);
            return Pack(negative, x.Significand * y.Significand, x.Exponent + y.Exponent, false, f);
        }

        public static BigInteger Div(BigInteger a, BigInteger b, FloatFormat f)
        {
            var x = Unpack(a, f);
            var y = Unpack(b, f);
            if (x.Class == Class.NaN || y.Class == Class.NaN)
                return f.NaN;
            bool negative = x.Negative != y.Negative;
            if (x.Class == Class.Infinity)
                return y.Class == Class.Infinity ? f.NaN : f.Infinity(negative);
            if (y.Class == Class.Infinity)
                return f.Zero(negative);
            if (y.Class == Class.Zero)
                return x.Class == Class.Zero ? f.NaN : f.Infinity(negative);
            if (x.Class == Class.Zero)
                return f.Zero(negative);
            // Enough quotient bits whatever the significands' widths (a subnormal's may be tiny): the rest is sticky.
            int extra = 2 * f.FractionBits + 4;
            var quotient = BigInteger.DivRem(x.Significand << extra, y.Significand, out var remainder);
            return Pack(negative, quotient, x.Exponent - y.Exponent - extra, !remainder.IsZero, f);
        }

        /// <summary>The remainder of a truncating division (<c>fmod</c>): exact, with the sign of the dividend.</summary>
        public static BigInteger Rem(BigInteger a, BigInteger b, FloatFormat f)
        {
            var x = Unpack(a, f);
            var y = Unpack(b, f);
            if (x.Class == Class.NaN || y.Class == Class.NaN || x.Class == Class.Infinity || y.Class == Class.Zero)
                return f.NaN;
            if (y.Class == Class.Infinity || x.Class == Class.Zero)
                return Repack(x, f);
            int e = Math.Min(x.Exponent, y.Exponent);
            var r = BigInteger.Remainder(x.Significand << (x.Exponent - e), y.Significand << (y.Exponent - e));
            return r.IsZero ? f.Zero(x.Negative) : Pack(x.Negative, r, e, false, f);
        }

        public static BigInteger Sqrt(BigInteger a, FloatFormat f)
        {
            var x = Unpack(a, f);
            if (x.Class == Class.NaN || x.Negative && x.Class != Class.Zero)
                return f.NaN;
            if (x.Class != Class.Finite)
                return Repack(x, f);
            // An even exponent and at least 2·(fraction + 2) significand bits: the root then has the bits to round.
            int extra = 2 * f.FractionBits + 4;
            if ((x.Exponent - extra) % 2 != 0)
                extra++;
            var scaled = x.Significand << extra;
            var root = IntegerSqrt(scaled);
            return Pack(false, root, (x.Exponent - extra) / 2, root * root != scaled, f);
        }

        private static BigInteger IntegerSqrt(BigInteger n)
        {
            if (n.IsZero)
                return BigInteger.Zero;
            var x = BigInteger.One << (((int)n.GetBitLength() + 1) / 2);
            while (true)
            {
                var y = (x + n / x) >> 1;
                if (y >= x)
                    return x;
                x = y;
            }
        }

        public static BigInteger Neg(BigInteger a, FloatFormat f) => (a & f.Mask) ^ f.SignBit;
        public static BigInteger Abs(BigInteger a, FloatFormat f) => a & f.Mask & ~f.SignBit;

        public enum Rounding : byte { Floor, Ceiling, Truncate, Nearest }

        /// <summary>The integer nearest in the direction (Nearest: ties to even), as a float of the same format.</summary>
        public static BigInteger RoundToInteger(BigInteger a, Rounding mode, FloatFormat f)
        {
            var x = Unpack(a, f);
            if (x.Class != Class.Finite || x.Exponent >= 0)
                return Repack(x, f);
            int drop = -x.Exponent;
            var whole = x.Significand >> drop;
            var rest = x.Significand & ((BigInteger.One << drop) - 1);
            bool up = mode switch
            {
                Rounding.Floor => x.Negative && !rest.IsZero,
                Rounding.Ceiling => !x.Negative && !rest.IsZero,
                Rounding.Truncate => false,
                _ => rest > BigInteger.One << (drop - 1) || rest == BigInteger.One << (drop - 1) && !whole.IsEven,
            };
            if (up)
                whole += 1;
            return whole.IsZero ? f.Zero(x.Negative) : Pack(x.Negative, whole, 0, false, f);
        }

        public static BigInteger Min(BigInteger a, BigInteger b, FloatFormat f) =>
            Compare(a, b, f) is int order ? order <= 0 ? a & f.Mask : b & f.Mask : f.NaN;

        public static BigInteger Max(BigInteger a, BigInteger b, FloatFormat f) =>
            Compare(a, b, f) is int order ? order >= 0 ? a & f.Mask : b & f.Mask : f.NaN;

        // ── Comparison ─────────────────────────────────────────

        /// <summary>-1, 0 or 1; null when either is NaN (unordered). +0 and -0 are equal.</summary>
        public static int? Compare(BigInteger a, BigInteger b, FloatFormat f)
        {
            var x = Unpack(a, f);
            var y = Unpack(b, f);
            if (x.Class == Class.NaN || y.Class == Class.NaN)
                return null;
            if (x.Class == Class.Zero && y.Class == Class.Zero)
                return 0;
            if (x.Class == Class.Zero)
                return y.Negative ? 1 : -1;
            if (y.Class == Class.Zero)
                return x.Negative ? -1 : 1;
            if (x.Negative != y.Negative)
                return x.Negative ? -1 : 1;
            int byMagnitude = CompareMagnitudes(x, y);
            return x.Negative ? -byMagnitude : byMagnitude;
        }

        private static int CompareMagnitudes(Unpacked x, Unpacked y)
        {
            if (x.Class == Class.Infinity || y.Class == Class.Infinity)
                return x.Class == y.Class ? 0 : x.Class == Class.Infinity ? 1 : -1;
            int ex = x.Exponent + (int)x.Significand.GetBitLength(), ey = y.Exponent + (int)y.Significand.GetBitLength();
            if (ex != ey)
                return ex < ey ? -1 : 1;
            // The same leading exponent: line the significands up.
            var sx = x.Significand;
            var sy = y.Significand;
            if (x.Exponent > y.Exponent)
                sx <<= x.Exponent - y.Exponent;
            else
                sy <<= y.Exponent - x.Exponent;
            return sx.CompareTo(sy);
        }

        // ── Conversions ────────────────────────────────────────

        public static BigInteger Convert(BigInteger bits, FloatFormat from, FloatFormat to) => Repack(Unpack(bits, from), to);

        public static BigInteger FromInteger(BigInteger value, FloatFormat f) =>
            value.IsZero ? f.Zero(false) : Pack(value.Sign < 0, BigInteger.Abs(value), 0, false, f);

        /// <summary>The integer part (toward zero); 0 for NaN and the infinities. The caller wraps it to its width.</summary>
        public static BigInteger ToInteger(BigInteger bits, FloatFormat f)
        {
            var x = Unpack(bits, f);
            if (x.Class != Class.Finite)
                return BigInteger.Zero;
            var whole = x.Exponent >= 0 ? x.Significand << x.Exponent : x.Significand >> -x.Exponent;
            return x.Negative ? -whole : whole;
        }

        public static BigInteger FromDouble(double value, FloatFormat f) => Convert(BitConverter.DoubleToUInt64Bits(value), FloatFormat.Double, f);

        public static double ToDouble(BigInteger bits, FloatFormat f) => BitConverter.UInt64BitsToDouble((ulong)Convert(bits, f, FloatFormat.Double));

        /// <summary><c>digits × 10^exponent</c>, correctly rounded.</summary>
        public static BigInteger FromDecimal(bool negative, BigInteger digits, int exponent, FloatFormat f)
        {
            if (digits.IsZero)
                return f.Zero(negative);
            if (exponent >= 0)
                return Pack(negative, digits * BigInteger.Pow(10, exponent), 0, false, f);
            var divisor = BigInteger.Pow(10, -exponent);
            int extra = f.FractionBits + 4 + (int)divisor.GetBitLength();
            var quotient = BigInteger.DivRem(digits << extra, divisor, out var remainder);
            return Pack(negative, quotient, -extra, !remainder.IsZero, f);
        }

        /// <summary>A decimal literal (digits, a point, an exponent; underscores ignored), correctly rounded.</summary>
        public static BigInteger FromText(string text, FloatFormat f)
        {
            text = text.Replace("_", "");
            bool negative = text.StartsWith('-');
            if (negative || text.StartsWith('+'))
                text = text[1..];
            int e = text.IndexOfAny(['e', 'E']);
            int exponent = 0;
            if (e >= 0)
            {
                exponent = int.Parse(text[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                text = text[..e];
            }
            int point = text.IndexOf('.');
            if (point >= 0)
            {
                exponent -= text.Length - point - 1;
                text = text.Remove(point, 1);
            }
            return FromDecimal(negative, BigInteger.Parse(text, System.Globalization.CultureInfo.InvariantCulture), exponent, f);
        }
    }
}
