using System.Numerics;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class FixTests
{
    private static Fix F(string s) => Fix.Parse(s);

    private const long One = 65536;

    // ------------------------------------------------------------ constants and factories

    [Fact]
    public void Constants_HaveExpectedRawValues()
    {
        Assert.Equal(0, Fix.Zero.Raw);
        Assert.Equal(One, Fix.One.Raw);
        Assert.Equal(One / 2, Fix.Half.Raw);
        Assert.Equal(1, Fix.Epsilon.Raw);
        Assert.Equal(long.MaxValue, Fix.MaxValue.Raw);
        Assert.Equal(long.MinValue, Fix.MinValue.Raw);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void FromInt_ScalesBy65536(int value)
    {
        Assert.Equal((long)value * One, Fix.FromInt(value).Raw);
    }

    [Theory]
    [InlineData(1, 2, 32768)]
    [InlineData(-1, 4, -16384)]
    [InlineData(1, -4, -16384)]
    [InlineData(-3, -4, 49152)]
    [InlineData(1, 3, 21845)]      // 21845.33 -> 21845
    [InlineData(2, 3, 43691)]      // 43690.67 -> 43691
    [InlineData(-2, 3, -43691)]    // symmetric under negation
    [InlineData(1, 131072, 1)]     // exactly half a raw step -> away from zero
    [InlineData(-1, 131072, -1)]
    [InlineData(1, 131073, 0)]     // just under half a step
    public void FromFraction_RoundsToNearestTiesAwayFromZero(long n, long d, long expectedRaw)
    {
        Assert.Equal(expectedRaw, Fix.FromFraction(n, d).Raw);
    }

    [Fact]
    public void FromFraction_ZeroDenominator_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => Fix.FromFraction(1, 0));
    }

    // ------------------------------------------------------------ arithmetic

    [Fact]
    public void AddSubtractNegate_Basic()
    {
        Assert.Equal(F("4"), F("1.5") + F("2.5"));
        Assert.Equal(F("-1"), F("1.5") - F("2.5"));
        Assert.Equal(F("-0.75"), F("-0.5") + F("-0.25"));
        Assert.Equal(F("-1.5"), -F("1.5"));
        Assert.Equal(F("1.5"), -F("-1.5"));
        Assert.Equal(Fix.Zero, -Fix.Zero);
    }

    [Fact]
    public void AddSubtract_SaturateInsteadOfWrapping()
    {
        Assert.Equal(Fix.MaxValue, Fix.MaxValue + Fix.Epsilon);
        Assert.Equal(Fix.MinValue, Fix.MinValue - Fix.Epsilon);
        Assert.Equal(Fix.MinValue, Fix.MinValue + Fix.FromInt(-5));
        Assert.Equal(Fix.MaxValue, Fix.MaxValue - Fix.FromInt(-5));
        Assert.Equal(Fix.FromRaw(-1), Fix.MaxValue + Fix.MinValue);
        Assert.Equal(Fix.MaxValue, -Fix.MinValue);
    }

    [Theory]
    [InlineData("1.5", "2", "3")]
    [InlineData("-1.5", "2", "-3")]
    [InlineData("-1.5", "-2", "3")]
    [InlineData("0.5", "0.5", "0.25")]
    [InlineData("-0.5", "0.5", "-0.25")]
    [InlineData("1000000", "1000", "1000000000")]
    [InlineData("0", "-7.25", "0")]
    public void Multiply_Exact(string a, string b, string expected)
    {
        Assert.Equal(F(expected), F(a) * F(b));
    }

    [Fact]
    public void Multiply_RoundsToNearestTiesAwayFromZero()
    {
        // Epsilon * Half = half a raw step -> rounds away from zero.
        Assert.Equal(1, (Fix.Epsilon * Fix.Half).Raw);
        Assert.Equal(-1, (-Fix.Epsilon * Fix.Half).Raw);
        Assert.Equal(-1, (Fix.Epsilon * -Fix.Half).Raw);
        // Epsilon * 0.25 = quarter step -> 0.
        Assert.Equal(0, (Fix.Epsilon * F("0.25")).Raw);
        // Epsilon * 0.75 -> 1, and symmetric for negatives.
        Assert.Equal(1, (Fix.Epsilon * F("0.75")).Raw);
        Assert.Equal(-1, (Fix.Epsilon * F("-0.75")).Raw);
    }

    [Fact]
    public void Multiply_LargeOperands_UseFullPrecisionIntermediate()
    {
        // Raw product is ~2^90: would overflow a naive long multiply.
        Fix big = Fix.FromInt(10_000_000);
        Assert.Equal(Fix.Parse("100000000000000"), big * big);
        Assert.Equal(Fix.Parse("-100000000000000"), big * -big);
        Assert.Equal(F("0.0625") * big, Fix.FromInt(625_000));
        Assert.Equal(Fix.MaxValue, Fix.MaxValue * Fix.One);
        Assert.Equal(Fix.MinValue, Fix.MinValue * Fix.One);
    }

    [Fact]
    public void Multiply_SaturatesOnOverflow()
    {
        Fix big = Fix.FromInt(100_000_000);
        Assert.Equal(Fix.MaxValue, big * big);
        Assert.Equal(Fix.MaxValue, -big * -big);
        Assert.Equal(Fix.MinValue, big * -big);
        Assert.Equal(Fix.MaxValue, Fix.MinValue * Fix.MinValue);
        Assert.Equal(Fix.MaxValue, Fix.MinValue * -Fix.One);
    }

    [Theory]
    [InlineData("3", "2", "1.5")]
    [InlineData("-3", "2", "-1.5")]
    [InlineData("3", "-2", "-1.5")]
    [InlineData("-3", "-2", "1.5")]
    [InlineData("1", "0.25", "4")]
    [InlineData("0", "-5", "0")]
    [InlineData("100000000000", "0.5", "200000000000")]
    public void Divide_Exact(string a, string b, string expected)
    {
        Assert.Equal(F(expected), F(a) / F(b));
    }

    [Fact]
    public void Divide_RoundsToNearestTiesAwayFromZero()
    {
        Fix three = Fix.FromInt(3);
        Assert.Equal(21845, (Fix.One / three).Raw);   // 21845.33
        Assert.Equal(-21845, (-Fix.One / three).Raw);
        Assert.Equal(43691, (Fix.FromInt(2) / three).Raw); // 43690.67
        Assert.Equal(-43691, (Fix.FromInt(2) / -three).Raw);
        Assert.Equal(1, (Fix.Epsilon / Fix.FromInt(2)).Raw);  // half step -> away from zero
        Assert.Equal(-1, (-Fix.Epsilon / Fix.FromInt(2)).Raw);
        Assert.Equal(0, (Fix.Epsilon / Fix.FromInt(3)).Raw);
    }

    [Fact]
    public void Divide_ByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => Fix.One / Fix.Zero);
        Assert.Throws<DivideByZeroException>(() => Fix.Zero / Fix.Zero);
    }

    [Fact]
    public void Divide_SaturatesOnOverflow()
    {
        Assert.Equal(Fix.MaxValue, Fix.MaxValue / Fix.Half);
        Assert.Equal(Fix.MinValue, Fix.MaxValue / -Fix.Half);
        Assert.Equal(Fix.MaxValue, Fix.MaxValue / Fix.Epsilon);
        Assert.Equal(Fix.MaxValue, Fix.FromRaw(1L << 47) / Fix.Epsilon); // quotient is exactly 2^63
        Assert.Equal(Fix.MinValue, Fix.FromRaw(-(1L << 48)) / Fix.Epsilon);
        Assert.Equal(Fix.MaxValue, Fix.MinValue / -Fix.One);
        Assert.Equal(Fix.MinValue, Fix.MinValue / Fix.One);
        Assert.Equal(Fix.One, Fix.MinValue / Fix.MinValue);
    }

    [Fact]
    public void Divide_LargeNumeratorBeyond47Bits_IsExact()
    {
        // Raw numerator shifted by 16 exceeds 64 bits; exercises the 128-bit path.
        Fix a = Fix.FromRaw(long.MaxValue - 12344);
        Assert.Equal(a, a / Fix.One);
        Assert.Equal(Fix.FromRaw((long.MaxValue - 12344) / 2 + 1), a / Fix.FromInt(2)); // odd raw: .5 rounds up
    }

    // ------------------------------------------------------------ comparison and equality

    [Fact]
    public void Comparisons_WorkIncludingNegatives()
    {
        Assert.True(F("-1") < F("-0.5"));
        Assert.True(F("-0.5") <= F("-0.5"));
        Assert.True(F("0.25") > F("-3"));
        Assert.True(F("2") >= F("2"));
        Assert.True(F("2") != F("2.00002"));
        Assert.True(F("1.5") == F("1.50"));
        Assert.True(F("1.5").Equals(F("1.5")));
        Assert.True(F("1.5").Equals((object)F("1.5")));
        Assert.False(F("1.5").Equals((object)1.5m));
        Assert.Equal(-1, F("-1").CompareTo(F("1")));
        Assert.Equal(0, F("1").CompareTo(F("1")));
        Assert.True(Fix.MinValue < Fix.MaxValue);
    }

    [Fact]
    public void GetHashCode_IsStableAndConsistentWithEquality()
    {
        Assert.Equal(F("1.5").GetHashCode(), F("1.5").GetHashCode());
        // Fixed expected values: must not change between runs, runtimes or platforms.
        Assert.Equal(98304, F("1.5").GetHashCode());
        Assert.Equal(0, Fix.Zero.GetHashCode());
        Assert.Equal(int.MinValue, Fix.MaxValue.GetHashCode());
    }

    // ------------------------------------------------------------ functions

    [Fact]
    public void AbsSignMinMax()
    {
        Assert.Equal(F("2.5"), Fix.Abs(F("-2.5")));
        Assert.Equal(F("2.5"), Fix.Abs(F("2.5")));
        Assert.Equal(Fix.MaxValue, Fix.Abs(Fix.MinValue));
        Assert.Equal(-1, Fix.Sign(F("-0.00002")));
        Assert.Equal(0, Fix.Sign(Fix.Zero));
        Assert.Equal(1, Fix.Sign(Fix.Epsilon));
        Assert.Equal(F("-3"), Fix.Min(F("-3"), F("2")));
        Assert.Equal(F("2"), Fix.Max(F("-3"), F("2")));
    }

    [Fact]
    public void Clamp_Works()
    {
        Assert.Equal(F("0"), Fix.Clamp(F("-1"), F("0"), F("1")));
        Assert.Equal(F("1"), Fix.Clamp(F("5"), F("0"), F("1")));
        Assert.Equal(F("0.5"), Fix.Clamp(F("0.5"), F("0"), F("1")));
        Assert.Throws<ArgumentException>(() => Fix.Clamp(F("0.5"), F("1"), F("0")));
    }

    [Theory]
    //          value    floor   ceil    round
    [InlineData("2",     "2",    "2",    "2")]
    [InlineData("2.25",  "2",    "3",    "2")]
    [InlineData("2.5",   "2",    "3",    "3")]
    [InlineData("2.75",  "2",    "3",    "3")]
    [InlineData("-2",    "-2",   "-2",   "-2")]
    [InlineData("-2.25", "-3",   "-2",   "-2")]
    [InlineData("-2.5",  "-3",   "-2",   "-3")]
    [InlineData("-2.75", "-3",   "-2",   "-3")]
    [InlineData("0.5",   "0",    "1",    "1")]
    [InlineData("-0.5",  "-1",   "0",    "-1")]
    [InlineData("0.00002", "0",  "1",    "0")]
    [InlineData("-0.00002", "-1", "0",   "0")]
    public void FloorCeilRound(string value, string floor, string ceil, string round)
    {
        Fix v = F(value);
        Assert.Equal(F(floor), Fix.Floor(v));
        Assert.Equal(F(ceil), Fix.Ceil(v));
        Assert.Equal(F(round), Fix.Round(v));
        Assert.Equal(Fix.FloorToInt(F(floor)), Fix.FloorToInt(v));
        Assert.Equal(Fix.FloorToInt(F(ceil)), Fix.CeilToInt(v));
        Assert.Equal(Fix.FloorToInt(F(round)), Fix.RoundToInt(v));
    }

    [Fact]
    public void FloorCeilRound_AtExtremes()
    {
        Assert.Equal(Fix.MinValue, Fix.Floor(Fix.MinValue));
        Assert.Equal(Fix.MinValue, Fix.Ceil(Fix.MinValue));
        Assert.Equal(Fix.MinValue, Fix.Round(Fix.MinValue));
        Assert.Equal(Fix.MaxValue, Fix.Ceil(Fix.MaxValue));
        Assert.Equal(Fix.MaxValue, Fix.Round(Fix.MaxValue));
        Assert.Equal(long.MaxValue & ~0xFFFFL, Fix.Floor(Fix.MaxValue).Raw);
        Assert.Throws<OverflowException>(() => Fix.FloorToInt(Fix.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(255)]
    [InlineData(1024)]
    [InlineData(46340)]
    [InlineData(3037000)]
    [InlineData(11863283)] // largest n with n*n <= 2^47
    public void Sqrt_OfPerfectSquare_IsExact(int n)
    {
        Fix square = Fix.FromRaw((long)n * n * One);
        Assert.Equal(Fix.FromInt(n), Fix.Sqrt(square));
    }

    [Theory]
    [InlineData("0.25", "0.5")]
    [InlineData("2.25", "1.5")]
    [InlineData("0.0625", "0.25")]
    [InlineData("6.25", "2.5")]
    public void Sqrt_OfFractionalSquare_IsExact(string square, string root)
    {
        Assert.Equal(F(root), Fix.Sqrt(F(square)));
    }

    [Fact]
    public void Sqrt_OfNonSquares_IsWithinOneRawStep()
    {
        // sqrt(2) = 1.41421356... -> raw 92681.9 -> 92682
        Assert.Equal(92682, Fix.Sqrt(Fix.FromInt(2)).Raw);
        // sqrt(3) = 1.7320508... -> raw 113511.68 -> 113512
        Assert.Equal(113512, Fix.Sqrt(Fix.FromInt(3)).Raw);
        // Smallest value: sqrt(1/65536) = 1/256 -> raw 256
        Assert.Equal(256, Fix.Sqrt(Fix.Epsilon).Raw);
        // sqrt(MaxValue): raw = sqrt((2^63 - 1) * 2^16) = 777472127993.87... -> 777472127994
        Assert.Equal(777472127994L, Fix.Sqrt(Fix.MaxValue).Raw);
    }

    [Fact]
    public void Sqrt_IsNearestRawStep_AcrossFullRange()
    {
        // Exact check with BigInteger: R = Sqrt(x).Raw and N = x.Raw * 2^16 must satisfy
        // (R - 1/2)^2 <= N < (R + 1/2)^2, i.e. 4R^2 - 4R + 1 <= 4N < 4R^2 + 4R + 1.
        var rng = new SimRandom(7);
        for (int i = 0; i < 5000; i++)
        {
            ulong bits = ((ulong)rng.NextUInt() << 32) | rng.NextUInt();
            long raw = (long)(bits >> rng.NextInt(1, 64)); // spread over all magnitudes
            BigInteger r = Fix.Sqrt(Fix.FromRaw(raw)).Raw;
            BigInteger n4 = 4 * (BigInteger)raw * 65536;
            Assert.True(4 * r * r - 4 * r + 1 <= n4 || r == 0, $"raw {raw}");
            Assert.True(n4 < 4 * r * r + 4 * r + 1, $"raw {raw}");
        }
    }

    [Fact]
    public void Sqrt_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fix.Sqrt(-Fix.Epsilon));
    }

    [Fact]
    public void Lerp_Works()
    {
        Assert.Equal(F("2"), Fix.Lerp(F("2"), F("6"), Fix.Zero));
        Assert.Equal(F("6"), Fix.Lerp(F("2"), F("6"), Fix.One));
        Assert.Equal(F("4"), Fix.Lerp(F("2"), F("6"), Fix.Half));
        Assert.Equal(F("-1"), Fix.Lerp(F("1"), F("-3"), Fix.Half));
        Assert.Equal(F("10"), Fix.Lerp(F("2"), F("6"), F("2"))); // t is not clamped
    }

    // ------------------------------------------------------------ parsing and formatting

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-0", 0)]
    [InlineData("1", One)]
    [InlineData("1.5", One + One / 2)]
    [InlineData("-0.25", -One / 4)]
    [InlineData("007.50", 7 * One + One / 2)]
    [InlineData("0.1", 6554)]                   // 6553.6 -> 6554
    [InlineData("-0.1", -6554)]
    [InlineData("0.00001", 1)]                  // 0.65536 -> 1
    [InlineData("0.000007", 0)]                 // 0.458752 -> 0
    [InlineData("0.00000762939453125", 1)]      // exactly half a step -> away from zero
    [InlineData("-0.00000762939453125", -1)]
    [InlineData("0.00000762939453124999999999", 0)] // just below half: needs >17 digits to decide
    [InlineData("0.99999999", One)]             // rounds up into the integer part
    [InlineData("140737488355327.99998474121", long.MaxValue)]
    [InlineData("-140737488355328", long.MinValue)]
    public void Parse_ProducesExpectedRaw(string text, long expectedRaw)
    {
        Assert.Equal(expectedRaw, Fix.Parse(text).Raw);
        Assert.True(Fix.TryParse(text, out Fix parsed));
        Assert.Equal(expectedRaw, parsed.Raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("+1")]
    [InlineData("1.2.3")]
    [InlineData("1e3")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1,5")]
    [InlineData("--1")]
    [InlineData("abc")]
    [InlineData("140737488355328")]        // MaxValue + epsilon territory
    [InlineData("140737488355327.999995")] // rounds past MaxValue
    [InlineData("-140737488355328.00001")] // below MinValue
    [InlineData("99999999999999999999999")]
    public void TryParse_RejectsInvalidInput(string? text)
    {
        Assert.False(Fix.TryParse(text, out Fix result));
        Assert.Equal(Fix.Zero, result);
    }

    [Fact]
    public void Parse_InvalidInput_Throws()
    {
        Assert.Throws<FormatException>(() => Fix.Parse("1.5x"));
        Assert.Throws<ArgumentNullException>(() => Fix.Parse(null!));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("-0.25")]
    [InlineData("0.1")]
    [InlineData("-3.14159")]
    [InlineData("0.00002")]
    [InlineData("12345.678")]
    [InlineData("140737488355327")]
    [InlineData("-140737488355328")]
    public void ToString_ProducesShortestDecimal(string text)
    {
        Assert.Equal(text, Fix.Parse(text).ToString());
    }

    [Fact]
    public void ToString_Examples()
    {
        Assert.Equal("0.5", Fix.Half.ToString());
        Assert.Equal("-0.5", (-Fix.Half).ToString());
        Assert.Equal("0.00002", Fix.Epsilon.ToString());
        Assert.Equal("-0.00002", (-Fix.Epsilon).ToString());
        Assert.Equal("0.33333", (Fix.One / Fix.FromInt(3)).ToString());
        Assert.Equal("140737488355327.99998", Fix.MaxValue.ToString());
    }

    [Fact]
    public void ToString_Parse_RoundTripsEveryFractionAndManyRandomValues()
    {
        // Every possible fractional part, positive and negative.
        for (long frac = 0; frac < One; frac++)
        {
            foreach (long raw in new[] { frac, -frac, 5 * One + frac, -(5 * One + frac) })
            {
                Fix value = Fix.FromRaw(raw);
                Assert.Equal(value, Fix.Parse(value.ToString()));
            }
        }

        var rng = new SimRandom(12345);
        for (int i = 0; i < 5000; i++)
        {
            Fix value = Fix.FromRaw(unchecked((long)(((ulong)rng.NextUInt() << 32) | rng.NextUInt())));
            Assert.Equal(value, Fix.Parse(value.ToString()));
        }
        Assert.Equal(Fix.MaxValue, Fix.Parse(Fix.MaxValue.ToString()));
        Assert.Equal(Fix.MinValue, Fix.Parse(Fix.MinValue.ToString()));
    }

    [Fact]
    public void ToFloat_IsViewOnlyApproximation()
    {
        Assert.Equal(1.5f, F("1.5").ToFloat());
        Assert.Equal(-0.25f, F("-0.25").ToFloat());
    }
}
