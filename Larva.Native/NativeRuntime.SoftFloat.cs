using System.Runtime.InteropServices;

namespace Larva.Native
{
    /// <summary>
    /// The soft-float routines LLVM calls for f16 and f128 on x86-64 (compiler-rt's names and contracts), for JIT code:
    /// half ↔ float conversions and quad comparisons. On Win64 an f128 travels by reference and a half in the low 16 bits
    /// of an XMM register. Quad comparisons work on the IEEE bits: sign-magnitude, with NaN unordered.
    /// </summary>
    public static unsafe partial class NativeRuntime
    {
        /// <summary>Routines code generation inserts (not declared in the module): registered process-wide for the JIT.</summary>
        public static IReadOnlyDictionary<string, nint> SoftFloatSymbols { get; } = new Dictionary<string, nint>
        {
            ["__extendhfsf2"] = (nint)(delegate* unmanaged<float, float>)&ExtendHalfToSingle,
            ["__gnu_h2f_ieee"] = (nint)(delegate* unmanaged<float, float>)&ExtendHalfToSingle,
            ["__extendhfdf2"] = (nint)(delegate* unmanaged<float, double>)&ExtendHalfToDouble,
            ["__truncsfhf2"] = (nint)(delegate* unmanaged<float, float>)&TruncateSingleToHalf,
            ["__gnu_f2h_ieee"] = (nint)(delegate* unmanaged<float, float>)&TruncateSingleToHalf,
            ["__truncdfhf2"] = (nint)(delegate* unmanaged<double, float>)&TruncateDoubleToHalf,
            ["__eqtf2"] = (nint)(delegate* unmanaged<UInt128*, UInt128*, int>)&CompareQuadUnorderedHigh,
            ["__netf2"] = (nint)(delegate* unmanaged<UInt128*, UInt128*, int>)&CompareQuadUnorderedHigh,
            ["__lttf2"] = (nint)(delegate* unmanaged<UInt128*, UInt128*, int>)&CompareQuadUnorderedHigh,
            ["__letf2"] = (nint)(delegate* unmanaged<UInt128*, UInt128*, int>)&CompareQuadUnorderedHigh,
            ["__cmptf2"] = (nint)(delegate* unmanaged<UInt128*, UInt128*, int>)&CompareQuadUnorderedHigh,
            ["__gttf2"] = (nint)(delegate* unmanaged<UInt128*, UInt128*, int>)&CompareQuadUnorderedLow,
            ["__getf2"] = (nint)(delegate* unmanaged<UInt128*, UInt128*, int>)&CompareQuadUnorderedLow,
            ["__unordtf2"] = (nint)(delegate* unmanaged<UInt128*, UInt128*, int>)&UnorderedQuad,
        };

        /// <summary>A half held in the low 16 bits of a float register.</summary>
        private static Half HalfIn(float register) => BitConverter.UInt16BitsToHalf((ushort)BitConverter.SingleToUInt32Bits(register));

        private static float HalfOut(Half value) => BitConverter.UInt32BitsToSingle(BitConverter.HalfToUInt16Bits(value));

        [UnmanagedCallersOnly]
        private static float ExtendHalfToSingle(float register) => (float)HalfIn(register);

        [UnmanagedCallersOnly]
        private static double ExtendHalfToDouble(float register) => (double)HalfIn(register);

        [UnmanagedCallersOnly]
        private static float TruncateSingleToHalf(float value) => HalfOut((Half)value);

        [UnmanagedCallersOnly]
        private static float TruncateDoubleToHalf(double value) => HalfOut((Half)value);

        private static readonly UInt128 QuadSign = UInt128.One << 127;
        private static readonly UInt128 QuadInfinity = (UInt128)0x7FFF << 112;

        private static bool IsNaN(UInt128 bits) => (bits & ~QuadSign) > QuadInfinity;

        /// <summary>-1, 0 or 1 for ordered quads; <paramref name="unordered"/> when either is NaN.</summary>
        private static int CompareQuad(UInt128 a, UInt128 b, int unordered)
        {
            if (IsNaN(a) || IsNaN(b))
                return unordered;
            UInt128 magnitudeA = a & ~QuadSign, magnitudeB = b & ~QuadSign;
            if (magnitudeA == 0 && magnitudeB == 0)
                return 0; // +0 == -0
            bool negativeA = (a & QuadSign) != 0, negativeB = (b & QuadSign) != 0;
            if (negativeA != negativeB)
                return negativeA ? -1 : 1;
            int byMagnitude = magnitudeA.CompareTo(magnitudeB);
            return negativeA ? -byMagnitude : byMagnitude;
        }

        /// <summary>__eqtf2, __netf2, __lttf2, __letf2: unordered gives 1 (so `<`, `<=` and `==` are false).</summary>
        [UnmanagedCallersOnly]
        private static int CompareQuadUnorderedHigh(UInt128* a, UInt128* b) => CompareQuad(*a, *b, 1);

        /// <summary>__gttf2, __getf2: unordered gives -1 (so `>` and `>=` are false).</summary>
        [UnmanagedCallersOnly]
        private static int CompareQuadUnorderedLow(UInt128* a, UInt128* b) => CompareQuad(*a, *b, -1);

        [UnmanagedCallersOnly]
        private static int UnorderedQuad(UInt128* a, UInt128* b) => IsNaN(*a) || IsNaN(*b) ? 1 : 0;
    }
}
