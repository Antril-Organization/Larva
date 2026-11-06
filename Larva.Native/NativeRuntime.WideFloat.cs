using System.Numerics;
using System.Runtime.InteropServices;

namespace Larva.Native
{
    /// <summary>
    /// The soft-float routines the direct x86-64 back end calls for f128 and f256 (and for any conversion that touches
    /// them, or a wide integer and a float), for JIT code; larva_rt.c has the same routines for executables. Values travel
    /// by address as 64-bit limbs, lowest first; a narrower float or integer in the low bytes of an 8-byte slot (integers
    /// in their canonical 64-bit form, extended by signedness). The arithmetic is <see cref="SoftFloat"/>'s.
    /// </summary>
    public static unsafe partial class NativeRuntime
    {
        /// <summary>Adds the routines to the JIT's symbol table (a method, not a field: <see cref="Symbols"/> is built first).</summary>
        private static void AddWideFloatSymbols(Dictionary<string, nint> symbols)
        {
            symbols["larva_float_add"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&FloatAdd;
            symbols["larva_float_sub"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&FloatSub;
            symbols["larva_float_mul"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&FloatMul;
            symbols["larva_float_div"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&FloatDiv;
            symbols["larva_float_rem"] = (nint)(delegate* unmanaged<ulong*, ulong*, ulong*, int, void>)&FloatRem;
            symbols["larva_float_cmp"] = (nint)(delegate* unmanaged<ulong*, ulong*, int, int, int>)&FloatCompare;
            symbols["larva_float_conv"] = (nint)(delegate* unmanaged<ulong*, ulong*, int, int, void>)&FloatConvert;
        }

        /// <summary>How the back end names a type to <c>larva_float_conv</c>: its width (12 bits), with 0x1000 for signed and 0x2000 for float.</summary>
        public const int SignedCode = 0x1000, FloatCode = 0x2000, WidthMask = 0xFFF;

        private static FloatFormat WideFormat(int limbs) => FloatFormat.Of(64 * limbs);

        [UnmanagedCallersOnly]
        private static void FloatAdd(ulong* a, ulong* b, ulong* result, int limbs) =>
            WideWrite(result, limbs, SoftFloat.Add(WideRead(a, limbs, false), WideRead(b, limbs, false), WideFormat(limbs)));

        [UnmanagedCallersOnly]
        private static void FloatSub(ulong* a, ulong* b, ulong* result, int limbs) =>
            WideWrite(result, limbs, SoftFloat.Sub(WideRead(a, limbs, false), WideRead(b, limbs, false), WideFormat(limbs)));

        [UnmanagedCallersOnly]
        private static void FloatMul(ulong* a, ulong* b, ulong* result, int limbs) =>
            WideWrite(result, limbs, SoftFloat.Mul(WideRead(a, limbs, false), WideRead(b, limbs, false), WideFormat(limbs)));

        [UnmanagedCallersOnly]
        private static void FloatDiv(ulong* a, ulong* b, ulong* result, int limbs) =>
            WideWrite(result, limbs, SoftFloat.Div(WideRead(a, limbs, false), WideRead(b, limbs, false), WideFormat(limbs)));

        [UnmanagedCallersOnly]
        private static void FloatRem(ulong* a, ulong* b, ulong* result, int limbs) =>
            WideWrite(result, limbs, SoftFloat.Rem(WideRead(a, limbs, false), WideRead(b, limbs, false), WideFormat(limbs)));

        /// <summary>1 when the condition (0 ==, 1 !=, 2 &lt;, 3 &lt;=, 4 &gt;, 5 &gt;=) holds; NaN makes every one false but !=.</summary>
        [UnmanagedCallersOnly]
        private static int FloatCompare(ulong* a, ulong* b, int limbs, int condition)
        {
            int? order = SoftFloat.Compare(WideRead(a, limbs, false), WideRead(b, limbs, false), WideFormat(limbs));
            bool holds = condition switch
            {
                0 => order == 0,
                1 => order != 0,
                2 => order < 0,
                3 => order <= 0,
                4 => order > 0,
                _ => order >= 0,
            };
            return holds ? 1 : 0;
        }

        /// <summary>Converts the value at <paramref name="source"/> (of type <paramref name="from"/>) into <paramref name="target"/> (type <paramref name="to"/>).</summary>
        [UnmanagedCallersOnly]
        private static void FloatConvert(ulong* source, ulong* target, int from, int to)
        {
            int fromBits = from & WidthMask, toBits = to & WidthMask;
            bool fromFloat = (from & FloatCode) != 0, toFloat = (to & FloatCode) != 0;
            int fromLimbs = Math.Max(1, fromBits / 64), toLimbs = Math.Max(1, toBits / 64);
            if (fromFloat)
            {
                var bits = WideRead(source, fromLimbs, false) & ((BigInteger.One << fromBits) - 1);
                var format = FloatFormat.Of(fromBits);
                if (toFloat)
                {
                    WideWrite(target, toLimbs, SoftFloat.Convert(bits, format, FloatFormat.Of(toBits)));
                    return;
                }
                WriteInteger(target, toLimbs, toBits, (to & SignedCode) != 0, SoftFloat.ToInteger(bits, format));
                return;
            }
            var value = WideRead(source, fromLimbs, (from & SignedCode) != 0);
            if (fromBits < 64)
                value = (from & SignedCode) != 0 ? Wrap(value, fromBits, true) : value & ((BigInteger.One << fromBits) - 1);
            WideWrite(target, toLimbs, SoftFloat.FromInteger(value, FloatFormat.Of(toBits)));
        }

        /// <summary>The integer wrapped to <paramref name="bits"/>, in canonical form (sign-extended to 64 bits when signed).</summary>
        private static void WriteInteger(ulong* target, int limbs, int bits, bool signed, BigInteger value)
        {
            var wrapped = Wrap(value, bits, signed);
            if (bits >= 64)
                WideWrite(target, limbs, wrapped);
            else
                target[0] = (ulong)(long)wrapped & ulong.MaxValue | (wrapped.Sign < 0 ? ulong.MaxValue << bits : 0);
        }

        private static BigInteger Wrap(BigInteger value, int bits, bool signed)
        {
            var modulus = BigInteger.One << bits;
            var wrapped = ((value % modulus) + modulus) % modulus;
            return signed && wrapped >= modulus >> 1 ? wrapped - modulus : wrapped;
        }
    }
}
