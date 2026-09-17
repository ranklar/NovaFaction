using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class SimulationTests
{
    private static readonly Command[] NoCommands = Array.Empty<Command>();

    private static MatchRules Rules => MatchRulesTests.LoadShippedRules();
    private static MapDefinition Map => MapTestData.LoadTwoLane();

    /// <summary>Fixed rules with the given gold values (independent of content tuning).</summary>
    private static MatchRules RulesWith(string income, string start, string cap = "10") =>
        MatchRules.FromJson("{\"ticksPerSecond\": 20, \"matchLengthSeconds\": 180, \"suddenDeathSeconds\": 60, "
            + "\"goldBaseIncomePerSecond\": " + income + ", \"goldStartingAmount\": " + start
            + ", \"goldCap\": " + cap + ", \"deploySpawnDelaySeconds\": 1, \"handSize\": 4, \"deckSize\": 8}");

    [Fact]
    public void NewMatch_StartsInRegulationWithStartingGold()
    {
        MatchRules rules = Rules;
        var sim = new Simulation(rules, Map, seed: 1);
        MatchState s = sim.State;

        Assert.Equal(0, s.Tick);
        Assert.Equal(MatchPhase.Regulation, s.Phase);
        Assert.Equal(3600, s.ClockRemainingTicks);
        Assert.Equal(2, s.Players.Count);
        foreach (PlayerState p in s.Players)
        {
            Assert.Equal(rules.GoldStartingAmount, p.Gold);
            Assert.Equal(Fix.Zero, p.Score);
            Assert.Equal(0, p.CommandsReceived);
            Assert.Equal(0L, p.IncomeRemainder);
        }
        Assert.Equal(new SimRandom(1).GetState(), s.Random.GetState());
        Assert.Same(s.Players[1], s.GetPlayer(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.GetPlayer(2));
        Assert.Throws<ArgumentNullException>(() => new Simulation(null!, Map, 1));
        Assert.Throws<ArgumentNullException>(() => new Simulation(rules, null!, 1));
    }

    [Fact]
    public void Tick_AdvancesTickAndClock()
    {
        var sim = new Simulation(Rules, Map, 1);
        sim.Tick(NoCommands);
        sim.Tick(NoCommands);
        Assert.Equal(2, sim.State.Tick);
        Assert.Equal(3598, sim.State.ClockRemainingTicks);
        Assert.Equal(2, sim.Log.TickCount);
    }

    [Fact]
    public void Income_IsExactOverWholeSeconds()
    {
        // 0.35/20 is not representable in Q48.16; the carried remainder must keep it exact.
        var sim = new Simulation(RulesWith("0.35", "0"), Map, 1);
        for (int second = 1; second <= 20; second++)
        {
            for (int t = 0; t < 20; t++)
            {
                sim.Tick(NoCommands);
            }
            Fix expected = Fix.Parse("0.35") * Fix.FromInt(second);
            Assert.Equal(expected, sim.State.GetPlayer(0).Gold);
            Assert.Equal(expected, sim.State.GetPlayer(1).Gold);
            Assert.Equal(0L, sim.State.GetPlayer(0).IncomeRemainder);
        }
    }

    [Fact]
    public void Income_GrowsEveryTickWithinASecond()
    {
        var sim = new Simulation(RulesWith("1", "0"), Map, 1);
        Fix previous = Fix.Zero;
        for (int t = 0; t < 20; t++)
        {
            sim.Tick(NoCommands);
            Fix gold = sim.State.GetPlayer(0).Gold;
            Assert.True(gold > previous);
            previous = gold;
        }
        Assert.Equal(Fix.One, previous);
    }

    [Fact]
    public void Gold_ReachesCapAndStaysThere()
    {
        MatchRules rules = Rules;
        var sim = new Simulation(rules, Map, 99);
        // From the starting amount, the cap is reached after (cap - start) / income seconds.
        Fix secondsToCap = (rules.GoldCap - rules.GoldStartingAmount) / rules.GoldBaseIncomePerSecond;
        int ticksToCap = Fix.CeilToInt(secondsToCap * Fix.FromInt(rules.TicksPerSecond));

        int reachedAt = -1;
        while (!sim.IsEnded)
        {
            sim.Tick(NoCommands);
            foreach (PlayerState p in sim.State.Players)
            {
                Assert.True(p.Gold <= rules.GoldCap, "gold exceeded the cap at tick " + sim.State.Tick);
                Assert.True(p.Gold >= Fix.Zero);
            }
            Fix gold = sim.State.GetPlayer(0).Gold;
            if (reachedAt < 0 && gold == rules.GoldCap)
            {
                reachedAt = sim.State.Tick;
            }
            if (reachedAt >= 0)
            {
                Assert.Equal(rules.GoldCap, gold);
                Assert.Equal(rules.GoldCap, sim.State.GetPlayer(1).Gold);
                Assert.Equal(0L, sim.State.GetPlayer(0).IncomeRemainder);
            }
        }
        Assert.InRange(reachedAt, 1, ticksToCap + 1);
        Assert.Equal(rules.GoldCap, sim.State.GetPlayer(0).Gold);
    }

    [Fact]
    public void Gold_StartingAtCap_StaysAtCap()
    {
        var sim = new Simulation(RulesWith("0.35", "10"), Map, 3);
        for (int t = 0; t < 100; t++)
        {
            sim.Tick(NoCommands);
            Assert.Equal(Fix.FromInt(10), sim.State.GetPlayer(0).Gold);
        }
    }

    [Fact]
    public void ZeroIncome_KeepsGoldConstant()
    {
        var sim = new Simulation(RulesWith("0", "3"), Map, 3);
        for (int t = 0; t < 100; t++)
        {
            sim.Tick(NoCommands);
        }
        Assert.Equal(Fix.FromInt(3), sim.State.GetPlayer(1).Gold);
    }

    [Fact]
    public void Clock_ReachesZeroAndMatchEnds()
    {
        MatchRules rules = Rules;
        var sim = new Simulation(rules, Map, 7);
        for (int t = 0; t < rules.MatchLengthTicks - 1; t++)
        {
            sim.Tick(NoCommands);
            Assert.Equal(MatchPhase.Regulation, sim.State.Phase);
        }
        Assert.Equal(1, sim.State.ClockRemainingTicks);

        sim.Tick(NoCommands);
        Assert.Equal(0, sim.State.ClockRemainingTicks);
        Assert.Equal(MatchPhase.Ended, sim.State.Phase);
        Assert.True(sim.IsEnded);
        Assert.Equal(180 * 20, sim.State.Tick);
        Assert.Equal(3600, sim.Log.TickCount);
    }

    [Fact]
    public void Tick_AfterEnd_Throws()
    {
        var sim = RunFullMatch(Rules, 7, new CommandLog());
        Assert.Throws<InvalidOperationException>(() => sim.Tick(NoCommands));
    }

    [Fact]
    public void Commands_AreRecordedCountedAndOtherwiseHaveNoEffectYet()
    {
        var withCommands = new Simulation(Rules, Map, 11);
        var without = new Simulation(Rules, Map, 11);
        var target = new FixVector2(Fix.FromInt(4), Fix.FromInt(9));

        withCommands.Tick(new[]
        {
            Command.LeaderAbility(0, 1, 0, target),
            Command.DeployCard(0, 0, 1, 3, target),
            Command.DeployCard(0, 0, 0, 0, target),
            Command.None(0, 1, 1),
        });
        without.Tick(NoCommands);

        Assert.Equal(new[]
        {
            Command.DeployCard(0, 0, 0, 0, target),
            Command.DeployCard(0, 0, 1, 3, target),
            Command.LeaderAbility(0, 1, 0, target),
            Command.None(0, 1, 1),
        }, withCommands.Log.GetCommands(0));
        Assert.Equal(2, withCommands.State.GetPlayer(0).CommandsReceived);
        Assert.Equal(2, withCommands.State.GetPlayer(1).CommandsReceived);

        // Gold, clock and RNG are untouched by the not-yet-implemented commands.
        for (int p = 0; p < 2; p++)
        {
            Assert.Equal(without.State.GetPlayer(p).Gold, withCommands.State.GetPlayer(p).Gold);
        }
        Assert.Equal(without.State.ClockRemainingTicks, withCommands.State.ClockRemainingTicks);
        Assert.Equal(without.State.Random.GetState(), withCommands.State.Random.GetState());
    }

    public static IEnumerable<object[]> BadTickInputs()
    {
        var target = new FixVector2(Fix.One, Fix.One);
        yield return new object[] { new[] { Command.DeployCard(0, 0, 0, 4, target) }, "hand slot" };
        yield return new object[] { new[] { Command.None(0, 2, 0) }, "player" };
        yield return new object[] { new[] { Command.None(1, 0, 0) }, "submitted on tick 0" };
        yield return new object[] { new[] { Command.None(0, 0, 0), Command.None(0, 0, 0) }, "Duplicate" };
    }

    [Theory]
    [MemberData(nameof(BadTickInputs))]
    public void InvalidCommands_ThrowAndLeaveStateUnchanged(Command[] commands, string fragment)
    {
        var sim = new Simulation(Rules, Map, 5);
        ulong before = sim.ComputeHash();

        var ex = Assert.Throws<ArgumentException>(() => sim.Tick(commands));
        Assert.Contains(fragment, ex.Message);
        Assert.Equal(before, sim.ComputeHash());
        Assert.Equal(0, sim.State.Tick);
        Assert.Equal(0, sim.Log.TickCount);

        sim.Tick(NoCommands); // still usable
        Assert.Equal(1, sim.State.Tick);
    }

    [Fact]
    public void Tick_NullCommands_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Simulation(Rules, Map, 1).Tick(null!));
    }

    /// <summary>Plays a whole match, feeding the log's commands where it has them and nothing after.</summary>
    internal static Simulation RunFullMatch(MatchRules rules, ulong seed, CommandLog log, List<ulong>? hashes = null,
        MapDefinition? map = null)
    {
        var sim = new Simulation(rules, map ?? Map, seed);
        hashes?.Add(sim.ComputeHash());
        while (!sim.IsEnded)
        {
            int tick = sim.State.Tick;
            sim.Tick(tick < log.TickCount ? log.GetCommands(tick) : NoCommands);
            hashes?.Add(sim.ComputeHash());
        }
        return sim;
    }
}
