using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Same rules + seed + commands must give the same state hash on every tick, on every machine.
/// </summary>
public class DeterminismTests
{
    private static MatchRules Rules => MatchRulesTests.LoadShippedRules();
    private static MapDefinition Map => MapTestData.LoadTwoLane();

    /// <summary>
    /// A full match of scripted random inputs for both players (deploys, abilities, no-ops),
    /// submitted in shuffled order to exercise canonical sorting.
    /// </summary>
    internal static CommandLog ScriptedLog(MatchRules rules, ulong inputSeed)
    {
        var rng = new SimRandom(inputSeed);
        var log = new CommandLog();
        var sequence = new int[2];
        for (int tick = 0; tick < rules.MatchLengthTicks; tick++)
        {
            var commands = new List<Command>();
            for (int player = 0; player < 2; player++)
            {
                int count = rng.Chance(Fix.Parse("0.1")) ? rng.NextInt(1, 4) : 0;
                for (int i = 0; i < count; i++)
                {
                    var target = new FixVector2(Fix.FromRaw(rng.NextInt(-1_000_000, 1_000_000)),
                        Fix.FromRaw(rng.NextInt(-1_000_000, 1_000_000)));
                    int kind = rng.NextInt(0, 3);
                    int seq = sequence[player]++;
                    commands.Add(kind == 0 ? Command.None(tick, player, seq)
                        : kind == 1 ? Command.DeployCard(tick, player, seq, rng.NextInt(0, rules.HandSize), target)
                        : Command.LeaderAbility(tick, player, seq, target));
                }
            }
            rng.Shuffle(commands);
            log.Record(tick, commands);
        }
        return log;
    }

    [Fact]
    public void SameSeedAndLog_GiveIdenticalHashesEveryTick_OverFullMatch()
    {
        MatchRules rules = Rules;
        CommandLog log = ScriptedLog(rules, 2024);
        Assert.Equal(3600, log.TickCount);
        Assert.True(log.CommandCount > 500, "script should produce plenty of commands");

        var a = new Simulation(rules, Map, 0xC0FFEE);
        var b = new Simulation(MatchRulesTests.LoadShippedRules(), MapTestData.LoadTwoLane(), 0xC0FFEE); // independently loaded rules
        Assert.Equal(a.ComputeHash(), b.ComputeHash());

        int ticks = 0;
        while (!a.IsEnded)
        {
            IReadOnlyList<Command> commands = log.GetCommands(a.State.Tick);
            a.Tick(commands);
            b.Tick(commands);
            Assert.Equal(a.ComputeHash(), b.ComputeHash());
            ticks++;
        }
        Assert.Equal(180 * 20, ticks);
        Assert.True(b.IsEnded);
    }

    [Fact]
    public void DifferentSeeds_Diverge()
    {
        MatchRules rules = Rules;
        CommandLog log = ScriptedLog(rules, 1);
        var hashesA = new List<ulong>();
        var hashesB = new List<ulong>();
        SimulationTests.RunFullMatch(rules, 1, log, hashesA);
        SimulationTests.RunFullMatch(rules, 2, log, hashesB);

        Assert.Equal(hashesA.Count, hashesB.Count);
        for (int i = 0; i < hashesA.Count; i++)
        {
            Assert.NotEqual(hashesA[i], hashesB[i]);
        }
    }

    [Fact]
    public void DifferentCommands_Diverge()
    {
        MatchRules rules = Rules;
        Simulation a = SimulationTests.RunFullMatch(rules, 9, ScriptedLog(rules, 100));
        Simulation b = SimulationTests.RunFullMatch(rules, 9, ScriptedLog(rules, 101));
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void ReplayingRecordedLog_ReproducesFinalHash()
    {
        MatchRules rules = Rules;
        CommandLog script = ScriptedLog(rules, 77);
        var liveHashes = new List<ulong>();
        Simulation live = SimulationTests.RunFullMatch(rules, 424242, script, liveHashes);

        // Replay from the simulation's own recorded log, not the script.
        Simulation replay = Simulation.Replay(rules, Map, 424242, live.Log);

        Assert.Equal(live.ComputeHash(), replay.ComputeHash());
        Assert.Equal(liveHashes[liveHashes.Count - 1], replay.ComputeHash());
        Assert.True(replay.IsEnded);
        Assert.Equal(live.Log.CommandCount, replay.Log.CommandCount);
        for (int tick = 0; tick < live.Log.TickCount; tick++)
        {
            Assert.Equal(live.Log.GetCommands(tick), replay.Log.GetCommands(tick));
        }
    }

    [Fact]
    public void PartialReplay_MatchesLiveHashAtThatTick()
    {
        MatchRules rules = Rules;
        var liveHashes = new List<ulong>();
        Simulation live = SimulationTests.RunFullMatch(rules, 5, ScriptedLog(rules, 5), liveHashes);

        var partial = new CommandLog();
        for (int tick = 0; tick < 1000; tick++)
        {
            partial.Record(tick, live.Log.GetCommands(tick));
        }
        Assert.Equal(liveHashes[1000], Simulation.Replay(rules, Map, 5, partial).ComputeHash());
    }

    [Fact]
    public void SubmissionOrderWithinATick_DoesNotChangeTheHash()
    {
        MatchRules rules = Rules;
        var target = new FixVector2(Fix.One, Fix.Half);
        Command[] ordered =
        {
            Command.DeployCard(0, 0, 0, 1, target),
            Command.LeaderAbility(0, 0, 1, target),
            Command.None(0, 1, 0),
        };
        var a = new Simulation(rules, Map, 3);
        var b = new Simulation(rules, Map, 3);
        a.Tick(ordered);
        b.Tick(new[] { ordered[2], ordered[1], ordered[0] });
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_CoversEveryStateField()
    {
        var sim = new Simulation(Rules, Map, 1);
        MatchState s = sim.State;
        var seen = new HashSet<ulong> { sim.ComputeHash() };

        void Changed(string what)
        {
            Assert.True(seen.Add(sim.ComputeHash()), what + " is not reflected in the state hash");
        }

        s.Tick = 1; Changed("Tick");
        s.Random.NextUInt(); Changed("RNG state");
        s.ClockRemainingTicks = 5; Changed("ClockRemainingTicks");
        s.Phase = MatchPhase.SuddenDeath; Changed("Phase");
        for (int p = 0; p < 2; p++)
        {
            PlayerState player = s.GetPlayer(p);
            player.Gold = Fix.FromRaw(player.Gold.Raw + 1); Changed("Gold raw of player " + p);
            player.IncomeRemainder = 3; Changed("IncomeRemainder of player " + p);
            player.Score = Fix.FromRaw(1); Changed("Score of player " + p);
            player.CommandsReceived = 1; Changed("CommandsReceived of player " + p);
        }
        for (int i = 0; i < s.Map.Definition.Structures.Count; i++)
        {
            s.Map.DestroyStructure(i); Changed("destroyed structure " + i);
        }
    }

    [Fact]
    public void Hash_DistinguishesWhichPlayerHasAValue()
    {
        var a = new Simulation(Rules, Map, 1);
        var b = new Simulation(Rules, Map, 1);
        a.State.GetPlayer(0).Score = Fix.One;
        b.State.GetPlayer(1).Score = Fix.One;
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_IsPinned()
    {
        // Pins the hash layout and the whole tick pipeline. If this changes on purpose (new state,
        // new rule), update the constants and bump MatchState.HashFormatVersion when the layout
        // changed. If it changes unexpectedly, determinism broke.
        // Uses fixed rules (not content/rules.json) so tuning the content does not move the pin.
        MatchRules rules = MatchRules.FromJson(@"{
  ""ticksPerSecond"": 20, ""matchLengthSeconds"": 180, ""suddenDeathSeconds"": 60,
  ""goldBaseIncomePerSecond"": 0.35, ""goldStartingAmount"": 5, ""goldCap"": 10,
  ""deploySpawnDelaySeconds"": 1, ""handSize"": 4, ""deckSize"": 8 }");
        // Likewise a fixed inline map, so editing content/maps does not move the pin.
        MapDefinition map = MapTestData.Small();
        Assert.Equal(PinnedInitialHash, new Simulation(rules, map, 12345).ComputeHash());
        Simulation sim = SimulationTests.RunFullMatch(rules, 12345, ScriptedLog(rules, 12345), map: map);
        Assert.Equal(PinnedFinalHash, sim.ComputeHash());
    }

    private const ulong PinnedInitialHash = 0x31FB5C460926730AUL;
    private const ulong PinnedFinalHash = 0xE3023109AA724F26UL;
}

public class StateHasherTests
{
    [Fact]
    public void MatchesFnv1a64ReferenceVectors()
    {
        // Reference values from the FNV specification test suite.
        var empty = new StateHasher();
        Assert.Equal(0xcbf29ce484222325UL, empty.Value);

        var a = new StateHasher();
        a.Add((byte)'a');
        Assert.Equal(0xaf63dc4c8601ec8cUL, a.Value);

        var foobar = new StateHasher();
        foreach (char c in "foobar")
        {
            foobar.Add((byte)c);
        }
        Assert.Equal(0x85944171f73967e8UL, foobar.Value);
    }

    [Fact]
    public void MultiByteValues_AreLittleEndian()
    {
        var bytes = new StateHasher();
        foreach (byte b in new byte[] { 0x04, 0x03, 0x02, 0x01, 0x00, 0x00, 0x00, 0x80 })
        {
            bytes.Add(b);
        }
        var asLong = new StateHasher();
        asLong.Add(unchecked((long)0x8000000001020304UL));
        Assert.Equal(bytes.Value, asLong.Value);

        var asFix = new StateHasher();
        asFix.Add(Fix.FromRaw(unchecked((long)0x8000000001020304UL)));
        Assert.Equal(bytes.Value, asFix.Value);

        var intBytes = new StateHasher();
        foreach (byte b in new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })
        {
            intBytes.Add(b);
        }
        var asInt = new StateHasher();
        asInt.Add(-1);
        Assert.Equal(intBytes.Value, asInt.Value);
    }

    [Fact]
    public void OrderMatters()
    {
        var x = new StateHasher();
        x.Add(1);
        x.Add(2);
        var y = new StateHasher();
        y.Add(2);
        y.Add(1);
        Assert.NotEqual(x.Value, y.Value);
    }

    [Fact]
    public void HashableCommands_FoldIn()
    {
        var target = new FixVector2(Fix.One, Fix.Zero);
        var x = new StateHasher();
        x.AddHashable(Command.DeployCard(1, 0, 0, 1, target));
        var y = new StateHasher();
        y.AddHashable(Command.DeployCard(1, 0, 0, 2, target));
        Assert.NotEqual(x.Value, y.Value);
    }
}
