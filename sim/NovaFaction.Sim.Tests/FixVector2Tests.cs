using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class FixVector2Tests
{
    private static Fix F(string s) => Fix.Parse(s);

    private static FixVector2 V(string x, string y) => new FixVector2(F(x), F(y));

    [Fact]
    public void Constants()
    {
        Assert.Equal(V("0", "0"), FixVector2.Zero);
        Assert.Equal(V("1", "0"), FixVector2.UnitX);
        Assert.Equal(V("0", "1"), FixVector2.UnitY);
    }

    [Fact]
    public void Arithmetic()
    {
        Assert.Equal(V("4", "-1"), V("1.5", "2") + V("2.5", "-3"));
        Assert.Equal(V("-1", "5"), V("1.5", "2") - V("2.5", "-3"));
        Assert.Equal(V("-1.5", "2"), -V("1.5", "-2"));
        Assert.Equal(V("3", "-1"), V("1.5", "-0.5") * F("2"));
        Assert.Equal(V("3", "-1"), F("2") * V("1.5", "-0.5"));
        Assert.True(V("1", "2") != V("2", "1"));
        Assert.True(V("1", "2").Equals((object)V("1", "2")));
        Assert.Equal(V("1", "2").GetHashCode(), V("1", "2").GetHashCode());
        Assert.Equal("(1.5, -0.25)", V("1.5", "-0.25").ToString());
    }

    [Fact]
    public void DotAndLengths()
    {
        Assert.Equal(F("-2"), FixVector2.Dot(V("1", "2"), V("2", "-2")));
        Assert.Equal(Fix.Zero, FixVector2.Dot(FixVector2.UnitX, FixVector2.UnitY));
        Assert.Equal(F("25"), V("3", "-4").LengthSquared);
        Assert.Equal(F("5"), V("3", "-4").Length);
        Assert.Equal(F("5"), V("-3", "4").Length);
        Assert.Equal(F("2.5"), V("1.5", "2").Length);
        Assert.Equal(Fix.Zero, FixVector2.Zero.Length);
        Assert.Equal(F("13"), FixVector2.Distance(V("1", "1"), V("-4", "13")));
        Assert.Equal(F("169"), FixVector2.DistanceSquared(V("1", "1"), V("-4", "13")));
    }

    [Fact]
    public void Length_IsAccurateForTinyAndHugeVectors()
    {
        // (3,4) raw steps -> length exactly 5 raw steps (LengthSquared would underflow to zero).
        var tiny = new FixVector2(Fix.FromRaw(3), Fix.FromRaw(4));
        Assert.Equal(Fix.FromRaw(5), tiny.Length);
        Assert.Equal(Fix.Zero, tiny.LengthSquared);

        var huge = new FixVector2(Fix.FromInt(300_000_000), Fix.FromInt(-400_000_000));
        Assert.Equal(Fix.FromInt(500_000_000), huge.Length);

        var extreme = new FixVector2(Fix.MinValue, Fix.MinValue);
        Assert.Equal(Fix.MaxValue, extreme.Length);
    }

    [Fact]
    public void Normalized_ZeroVector_ReturnsZero()
    {
        Assert.Equal(FixVector2.Zero, FixVector2.Zero.Normalized);
    }

    [Fact]
    public void Normalized_AxisAndPythagoreanVectors_AreExact()
    {
        Assert.Equal(FixVector2.UnitX, V("5", "0").Normalized);
        Assert.Equal(-FixVector2.UnitY, V("0", "-0.001").Normalized);
        Assert.Equal(V("0.6", "-0.8").X, V("3", "-4").Normalized.X);
        Assert.Equal(V("0.6", "-0.8").Y, V("3", "-4").Normalized.Y);
        Assert.Equal(FixVector2.UnitX, new FixVector2(Fix.Epsilon, Fix.Zero).Normalized);
    }

    [Fact]
    public void Normalized_HasUnitLength_ForManyVectors()
    {
        var rng = new SimRandom(4242);
        Fix tolerance = Fix.FromRaw(2);
        for (int i = 0; i < 5000; i++)
        {
            // Raw components from 1 raw step up to ~2^45, both signs.
            int shift = rng.NextInt(0, 30);
            long x = (long)rng.NextInt(-65536, 65537) << shift;
            long y = (long)rng.NextInt(-65536, 65537) << shift;
            var v = new FixVector2(Fix.FromRaw(x), Fix.FromRaw(y));
            FixVector2 n = v.Normalized;

            if (x == 0 && y == 0)
            {
                Assert.Equal(FixVector2.Zero, n);
                continue;
            }
            Assert.True(Fix.Abs(n.Length - Fix.One) <= tolerance, $"{v} -> {n} len {n.Length}");
            // Same direction: signs preserved.
            Assert.Equal(Math.Sign(x), Fix.Sign(n.X));
            Assert.Equal(Math.Sign(y), Fix.Sign(n.Y));
        }
    }

    [Fact]
    public void Normalized_TinyDiagonal_IsAccurate()
    {
        // (1,1) raw steps: naive length rounds to 1 raw and gives (1,1). Must be ~(0.7071, 0.7071).
        FixVector2 n = new FixVector2(Fix.Epsilon, Fix.Epsilon).Normalized;
        Assert.Equal(46341, n.X.Raw); // 0.70710678 * 65536 = 46340.95
        Assert.Equal(46341, n.Y.Raw);
    }
}
