using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class SimRandomTests
{
    [Fact]
    public void MatchesPcg32ReferenceOutput()
    {
        // pcg32-demo from the PCG reference implementation, seed 42, stream 54.
        // Pins the algorithm: changing it would break every saved replay.
        var rng = new SimRandom(42);
        uint[] expected = { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };
        foreach (uint value in expected)
        {
            Assert.Equal(value, rng.NextUInt());
        }
    }

    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = new SimRandom(123456789);
        var b = new SimRandom(123456789);
        for (int i = 0; i < 10_000; i++)
        {
            Assert.Equal(a.NextUInt(), b.NextUInt());
            Assert.Equal(a.NextInt(-50, 50), b.NextInt(-50, 50));
            Assert.Equal(a.NextFix01(), b.NextFix01());
            Assert.Equal(a.Chance(Fix.Half), b.Chance(Fix.Half));
        }
        Assert.Equal(a.GetState(), b.GetState());
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        var a = new SimRandom(1);
        var b = new SimRandom(2);
        int same = 0;
        for (int i = 0; i < 100; i++)
        {
            if (a.NextUInt() == b.NextUInt())
            {
                same++;
            }
        }
        Assert.True(same < 3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(-5, 5)]
    [InlineData(1, 7)]
    [InlineData(-3, -1)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(int.MaxValue - 1, int.MaxValue)]
    public void NextInt_StaysInBounds(int min, int max)
    {
        var rng = new SimRandom(99);
        for (int i = 0; i < 20_000; i++)
        {
            int v = rng.NextInt(min, max);
            Assert.InRange(v, min, max - 1);
        }
    }

    [Fact]
    public void NextInt_HitsEveryValueRoughlyEvenly()
    {
        var rng = new SimRandom(2026);
        int[] counts = new int[7];
        const int draws = 70_000;
        for (int i = 0; i < draws; i++)
        {
            counts[rng.NextInt(0, 7)]++;
        }
        foreach (int c in counts)
        {
            Assert.InRange(c, 9_000, 11_000); // expected 10,000 each
        }
    }

    [Fact]
    public void NextInt_InvalidRange_Throws()
    {
        var rng = new SimRandom(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 4));
    }

    [Fact]
    public void NextFix01_IsInHalfOpenUnitInterval()
    {
        var rng = new SimRandom(5);
        Fix sum = Fix.Zero;
        const int draws = 20_000;
        for (int i = 0; i < draws; i++)
        {
            Fix v = rng.NextFix01();
            Assert.True(v >= Fix.Zero && v < Fix.One, v.ToString());
            sum += v;
        }
        Fix mean = sum / Fix.FromInt(draws);
        Assert.True(Fix.Abs(mean - Fix.Half) < Fix.Parse("0.02"), mean.ToString());
    }

    [Fact]
    public void Chance_RespectsExtremesAndAlwaysConsumesOneDraw()
    {
        var rng = new SimRandom(8);
        var reference = new SimRandom(8);
        for (int i = 0; i < 1000; i++)
        {
            Assert.False(rng.Chance(Fix.Zero));
            Assert.False(rng.Chance(-Fix.One));
            Assert.True(rng.Chance(Fix.One));
            Assert.True(rng.Chance(Fix.FromInt(2)));
            for (int k = 0; k < 4; k++)
            {
                reference.NextUInt();
            }
        }
        Assert.Equal(reference.GetState(), rng.GetState());
    }

    [Fact]
    public void Chance_MatchesProbabilityApproximately()
    {
        var rng = new SimRandom(77);
        int hits = 0;
        for (int i = 0; i < 40_000; i++)
        {
            if (rng.Chance(Fix.Parse("0.25")))
            {
                hits++;
            }
        }
        Assert.InRange(hits, 9_400, 10_600); // expected 10,000
    }

    [Fact]
    public void Shuffle_IsReproducibleAndAPermutation()
    {
        List<int> Shuffled(ulong seed)
        {
            var list = Enumerable.Range(0, 52).ToList();
            new SimRandom(seed).Shuffle(list);
            return list;
        }

        List<int> first = Shuffled(31337);
        Assert.Equal(first, Shuffled(31337));
        Assert.NotEqual(Enumerable.Range(0, 52).ToList(), first);
        Assert.NotEqual(first, Shuffled(31338));
        Assert.Equal(Enumerable.Range(0, 52), first.OrderBy(x => x));
    }

    [Fact]
    public void Shuffle_HandlesArraysAndTinyLists()
    {
        var rng = new SimRandom(3);
        var empty = new List<string>();
        rng.Shuffle(empty);
        Assert.Empty(empty);

        var single = new[] { "a" };
        rng.Shuffle(single);
        Assert.Equal(new[] { "a" }, single);

        Assert.Throws<ArgumentNullException>(() => rng.Shuffle<int>(null!));
    }

    [Fact]
    public void GetStateSetState_RestoresSequence()
    {
        var rng = new SimRandom(2024);
        for (int i = 0; i < 17; i++)
        {
            rng.NextUInt();
        }

        ulong saved = rng.GetState();
        var firstRun = Enumerable.Range(0, 100).Select(_ => rng.NextUInt()).ToList();

        rng.SetState(saved);
        var secondRun = Enumerable.Range(0, 100).Select(_ => rng.NextUInt()).ToList();
        Assert.Equal(firstRun, secondRun);

        // A fresh instance restored to the same state continues identically.
        var other = new SimRandom(0);
        other.SetState(saved);
        Assert.Equal(firstRun[0], other.NextUInt());
    }
}
