using System.Numerics;
using Larva.Hir.Lowering; // SoftFloat.cs is compiled into this assembly too (and into the language packs)

namespace Larva.Tests;

/// <summary>The software floating point used for constants and the wide formats.</summary>
public sealed class SoftFloatTests
{
    [Fact]
    public void Absurd_decimal_exponents_give_an_infinity_or_a_zero_at_once()
    {
        foreach (var f in new[] { FloatFormat.Half, FloatFormat.Single, FloatFormat.Double, FloatFormat.Quad, FloatFormat.Octuple })
        {
            Assert.Equal(f.Infinity(false), SoftFloat.FromText("1e99999999", f));
            Assert.Equal(f.Infinity(true), SoftFloat.FromText("-1e999999999999999999", f));
            Assert.Equal(f.Zero(false), SoftFloat.FromText("1e-99999999", f));
            Assert.Equal(f.Zero(true), SoftFloat.FromText("-123.456e-999999999999", f));
        }
        // Still exact near the edges of f64.
        Assert.Equal(BitConverter.DoubleToUInt64Bits(1.7976931348623157e308), (ulong)SoftFloat.FromText("1.7976931348623157e308", FloatFormat.Double));
        Assert.Equal(FloatFormat.Double.Infinity(false), SoftFloat.FromText("1.8e308", FloatFormat.Double));
        Assert.Equal(BitConverter.DoubleToUInt64Bits(5e-324), (ulong)SoftFloat.FromText("5e-324", FloatFormat.Double));
        Assert.Equal(FloatFormat.Double.Zero(false), SoftFloat.FromText("1e-400", FloatFormat.Double));
    }
}
