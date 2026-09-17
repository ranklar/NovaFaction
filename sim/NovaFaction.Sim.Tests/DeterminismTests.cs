using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;
using Xunit.Abstractions;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Same rules + seed + commands must give the same state hash on every tick, on every machine.
/// </summary>
public class DeterminismTests
{
    private readonly ITestOutputHelper _output;

    public DeterminismTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static MatchRules Rules => MatchRulesTests.LoadShippedRules();
    private static MapDefinition Map => MapTestData.LoadTwoLane();

    /// <summary>
    /// A full match of scripted random inputs for both players (deploys, abilities, no-ops),
    /// submitted in shuffled order to exercise canonical sorting. Commands stop after
    /// <paramref name="activeTicks"/> ticks (default: the whole regulation clock).
    /// </summary>
    internal static CommandLog ScriptedLog(MatchRules rules, ulong inputSeed, MapDefinition? map = null, int activeTicks = -1)
    {
        map ??= MapTestData.LoadTwoLane();
        if (activeTicks < 0)
        {
            activeTicks = rules.MatchLengthTicks;
        }
        // Targets range one cell beyond every map edge, so many deploys are valid and some are not.
        long cell = map.CellSize.Raw;
        long maxX = (map.Width + 1) * cell, maxY = (map.Height + 1) * cell;
        var rng = new SimRandom(inputSeed);
        var log = new CommandLog();
        var sequence = new int[2];
        for (int tick = 0; tick < activeTicks; tick++)
        {
            var commands = new List<Command>();
            for (int player = 0; player < 2; player++)
            {
                int count = rng.Chance(Fix.Parse("0.1")) ? rng.NextInt(1, 4) : 0;
                for (int i = 0; i < count; i++)
                {
                    var target = new FixVector2(Fix.FromRaw(rng.NextInt((int)-cell, (int)maxX)),
                        Fix.FromRaw(rng.NextInt((int)-cell, (int)maxY)));
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

    private static IReadOnlyList<Command> CommandsAt(CommandLog log, int tick) =>
        tick < log.TickCount ? log.GetCommands(tick) : Array.Empty<Command>();

    /// <summary>
    /// A busy scripted battle (two minutes of random play, then a minute with no new deploys) through two
    /// independently built sims: identical hashes on every tick, units always on open ground, a real fight
    /// between units, and at the end no unit is stuck walking: every living unit is attacking or holding.
    /// Income is raised to 1 gold/s to keep the field crowded, and the structures shoot but deal no damage
    /// and cannot fall, so crowds pile up at them and nothing gives units a new place to walk to late in the
    /// match. (Structure damage and destruction have their own tests.)
    /// </summary>
    [Theory]
    [InlineData(2024UL)]
    [InlineData(5UL)]
    [InlineData(7UL)]
    public void BusyScriptedBattle_HashesIdenticallyEveryTick_AndEndsWithNoStuckUnits(ulong inputSeed)
    {
        MatchRules rules = TestSim.Rules(income: "1", start: "10");
        CommandLog log = ScriptedLog(rules, inputSeed, activeTicks: 120 * 20);
        Assert.True(log.CommandCount > 400, "script should produce plenty of commands");

        StructureCatalog Sturdy() => TestSim.Structures(hp: "1000000", keepHp: "1000000", damage: "0");
        var a = TestSim.New(rules, Map, 0xC0FFEE, Sturdy());
        var b = new Simulation(new MatchSetup(TestSim.Rules(income: "1", start: "10"), MapTestData.LoadTwoLane(),
            Sturdy(), TestSim.DefaultDeck(rules), TestSim.DefaultDeck(rules)), 0xC0FFEE); // independently built
        Assert.Equal(a.ComputeHash(), b.ComputeHash());

        int ticks = 0;
        int mostUnits = 0;
        bool towerDamaged = false;
        while (!a.IsEnded)
        {
            IReadOnlyList<Command> commands = CommandsAt(log, a.State.Tick);
            a.Tick(commands);
            b.Tick(commands);
            Assert.True(a.ComputeHash() == b.ComputeHash(), "hashes diverged at tick " + a.State.Tick);
            foreach (Unit u in a.State.Units)
            {
                Assert.True(u.IsFlying || a.State.Map.Grid.IsWalkableAt(u.Position), "unit " + u.Id + " on a blocked cell");
                Assert.True(u.Hp > Fix.Zero);
            }
            mostUnits = Math.Max(mostUnits, a.State.Units.Count);
            towerDamaged |= a.State.Structures.Any(s => s.Hp < s.Stats.Hp);
            ticks++;
        }
        Assert.True(b.IsEnded);
        Assert.True(ticks >= 180 * 20, "the match should run to the clock, ended after " + ticks + " by " + a.State.EndReason);
        Assert.NotEqual(EndReason.KeepDestroyed, a.State.EndReason);
        Assert.InRange(a.State.Winner, 0, 1);

        // It was a real battle.
        Assert.True(mostUnits >= 15, "the script should field a crowd, peak " + mostUnits);
        Assert.True(a.State.NextUnitId - 1 > a.State.Units.Count + 30, "units should have died");
        Assert.True(a.State.NextProjectileId > 300, "ranged units and structures should have fired");
        Assert.True(towerDamaged);
        Assert.True(a.State.Units.Count >= 5, "enough units should survive for the check to mean something");

        // No stuck units: everything alive is attacking or has nothing to do.
        var stuck = a.State.Units.Where(u => u.State != UnitState.Attacking && u.State != UnitState.Holding).ToList();
        Assert.True(stuck.Count == 0, "stuck units: " + string.Join("; ", stuck.Select(u =>
            u.DefinitionId + "#" + u.Id + " p" + u.Owner + " " + u.State + " at " + u.Position + " target " + u.Target)));
        Assert.Contains(a.State.Units, u => u.State == UnitState.Attacking);
    }

    /// <summary>
    /// A full scripted battle with the shipped content (real towers and Keeps) and a faster income, through two
    /// independently loaded sims, hashing identically on every tick until the match ends however it ends.
    /// The recorded log then replays to the same final hash.
    /// </summary>
    [Theory]
    [InlineData(2024UL, 0xC0FFEEUL)]
    [InlineData(31UL, 99UL)]
    public void FullScriptedBattle_WithShippedContent_HashesIdenticallyEveryTick(ulong inputSeed, ulong matchSeed)
    {
        MatchRules rules = TestSim.Rules(income: "1", start: "10");
        CommandLog log = ScriptedLog(rules, inputSeed);
        var a = TestSim.New(rules, Map, matchSeed);
        var b = new Simulation(new MatchSetup(TestSim.Rules(income: "1", start: "10"), MapTestData.LoadTwoLane(),
            StructureCatalog.FromJson(TestSim.StructuresJson()), TestSim.DefaultDeck(rules, TestSim.LoadFantasy()),
            TestSim.DefaultDeck(rules, TestSim.LoadFantasy())), matchSeed);
        Assert.Equal(a.ComputeHash(), b.ComputeHash());

        bool structureHit = false;
        while (!a.IsEnded)
        {
            IReadOnlyList<Command> commands = CommandsAt(log, a.State.Tick);
            a.Tick(commands);
            b.Tick(commands);
            Assert.True(a.ComputeHash() == b.ComputeHash(), "hashes diverged at tick " + a.State.Tick);
            structureHit |= a.State.Structures.Any(s => s.Hp < s.Stats.Hp);
        }
        _output.WriteLine("ended at tick " + a.State.Tick + " by " + a.State.EndReason + " (" + a.State.TieBreakRule
            + "), winner " + a.State.Winner + ", scores " + a.State.Players[0].Score + " / " + a.State.Players[1].Score
            + ", units created " + (a.State.NextUnitId - 1) + ", shots " + (a.State.NextProjectileId - 1));
        Assert.True(structureHit);
        Assert.True(a.State.NextUnitId - 1 > a.State.Units.Count, "units should have died");
        Assert.True(a.State.NextProjectileId > 100);
        Assert.InRange(a.State.Winner, 0, 1);
        Assert.Equal(a.State.Winner, b.State.Winner);
        Assert.Equal(a.State.EndReason, b.State.EndReason);

        Simulation replay = TestSim.Replay(rules, Map, matchSeed, a.Log);
        Assert.Equal(a.ComputeHash(), replay.ComputeHash());
        Assert.True(replay.IsEnded);
    }

    [Fact]
    public void Hash_CoversStructureData()
    {
        MatchRules rules = Rules;
        StructureCatalog original = TestSim.LoadStructures();
        StructureCatalog tweaked = StructureCatalog.FromJson(TestSim.StructuresJson().Replace("\"destructionBonus\": 500", "\"destructionBonus\": 501"));
        Assert.NotEqual(original.ContentHash, tweaked.ContentHash);
        var a = new Simulation(new MatchSetup(rules, Map, original, TestSim.DefaultDeck(rules), TestSim.DefaultDeck(rules)), 1);
        var b = new Simulation(new MatchSetup(rules, Map, tweaked, TestSim.DefaultDeck(rules), TestSim.DefaultDeck(rules)), 1);
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void ScriptedDeploySequence_HashesIdenticallyEveryTick()
    {
        // Both players deploy by hand slot on fixed ticks: ground, swarm, flyer, an invalid target,
        // and two drops on the same spot. Everything runs through two independently built sims.
        var script = new Dictionary<int, Command[]>
        {
            [0] = new[] { Command.DeployCard(0, 0, 0, 0, TestSim.V("4.5", "10.5")), Command.DeployCard(0, 1, 0, 1, TestSim.V("13.5", "21.5")) },
            [30] = new[] { Command.DeployCard(30, 0, 0, 2, TestSim.V("9.5", "10.5")), Command.DeployCard(30, 1, 0, 0, TestSim.V("4.5", "15.5")) },
            [31] = new[] { Command.DeployCard(31, 1, 0, 3, TestSim.V("8.5", "25.5")) },
            [120] = new[] { Command.DeployCard(120, 0, 0, 3, TestSim.V("13.25", "8.75")), Command.DeployCard(120, 0, 1, 1, TestSim.V("13.25", "8.75")) },
            [400] = new[] { Command.DeployCard(400, 1, 0, 2, TestSim.V("2.5", "30.5")), Command.LeaderAbility(400, 0, 0, TestSim.V("1", "1")) },
        };
        // Harmless structures keep everyone alive, so the whole sequence stays on the field.
        MatchRules rules = TestSim.Rules(income: "1", start: "10");
        Simulation a = TestSim.New(rules, Map, 777, TestSim.HarmlessStructures());
        Simulation b = new Simulation(new MatchSetup(TestSim.Rules(income: "1", start: "10"), MapTestData.LoadTwoLane(), TestSim.HarmlessStructures(),
            TestSim.DefaultDeck(rules), TestSim.DefaultDeck(rules)), 777);
        Assert.Equal(a.ComputeHash(), b.ComputeHash());

        var seenHashes = new HashSet<ulong>();
        for (int tick = 0; tick < 60 * 20; tick++)
        {
            Command[] commands = script.TryGetValue(tick, out Command[]? c) ? c : Array.Empty<Command>();
            a.Tick(commands);
            b.Tick(commands);
            ulong hash = a.ComputeHash();
            Assert.True(hash == b.ComputeHash(), "hashes diverged at tick " + tick);
            seenHashes.Add(hash);
        }
        Assert.Equal(60 * 20, seenHashes.Count); // the state changes every tick
        Assert.True(a.State.NextUnitId - 1 >= 6, "the script should field several units, got " + (a.State.NextUnitId - 1));
        Assert.Contains(a.State.Units, u => u.State == UnitState.Attacking);
        Assert.Contains(a.State.Units, u => u.Owner == 1);
        Assert.Equal(1, a.State.GetPlayer(1).IgnoredDeploys); // the river drop
        for (int i = 0; i < a.State.Units.Count; i++)
        {
            Assert.Equal(a.State.Units[i].Position, b.State.Units[i].Position);
        }
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
        Simulation replay = TestSim.Replay(rules, Map, 424242, live.Log);

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
        Assert.Equal(liveHashes[1000], TestSim.Replay(rules, Map, 5, partial).ComputeHash());
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
        var a = TestSim.New(rules, Map, 3);
        var b = TestSim.New(rules, Map, 3);
        a.Tick(ordered);
        b.Tick(new[] { ordered[2], ordered[1], ordered[0] });
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_CoversEveryStateField()
    {
        var sim = TestSim.New(Rules, Map, 1);
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
        s.Winner = 1; Changed("Winner");
        s.EndReason = EndReason.TieBreak; Changed("EndReason");
        s.TieBreakRule = TieBreakRule.GoldCollected; Changed("TieBreakRule");
        for (int p = 0; p < 2; p++)
        {
            PlayerState player = s.GetPlayer(p);
            player.Gold = Fix.FromRaw(player.Gold.Raw + 1); Changed("Gold raw of player " + p);
            player.IncomeRemainder = 3; Changed("IncomeRemainder of player " + p);
            player.Score = Fix.FromRaw(1); Changed("Score of player " + p);
            player.GoldCollected = Fix.FromRaw(1); Changed("GoldCollected of player " + p);
            player.CommandsReceived = 1; Changed("CommandsReceived of player " + p);
            player.IgnoredDeploys = 1; Changed("IgnoredDeploys of player " + p);
            player.Cards.Play(2); Changed("hand/cycle order of player " + p);
            player.Cards.ClearSlot(1); Changed("empty hand slot of player " + p);
        }

        UnitDefinition golem = s.GetPlayer(0).Deck.Roster.Get("stone_golem");
        s.PendingSpawnList.Add(new PendingSpawn(40, 0, golem, TestSim.V("4", "4"))); Changed("pending spawn");
        s.NextUnitId = 7; Changed("NextUnitId");
        Unit unit = s.AddUnit(1, golem, TestSim.V("4", "20")); Changed("unit added");
        unit.Position = TestSim.V("4", "21"); Changed("unit position");
        unit.Hp = Fix.FromInt(5); Changed("unit hp");
        unit.State = UnitState.Holding; Changed("unit state");
        unit.Objective = 2; Changed("unit objective");
        unit.Target = TargetRef.Structure(2); Changed("unit target structure");
        unit.Target = TargetRef.Unit(2); Changed("unit target unit (same number)");
        unit.AttackCooldownTicks = 3; Changed("unit cooldown");

        foreach (StructureState structure in s.Structures)
        {
            structure.Hp = Fix.FromRaw(structure.Hp.Raw - 1); Changed("hp of structure " + structure.Index);
            structure.AttackCooldownTicks = 4; Changed("cooldown of structure " + structure.Index);
            structure.TargetUnitId = unit.Id; Changed("target of structure " + structure.Index);
        }

        s.NextProjectileId = 5; Changed("NextProjectileId");
        var shot = new Projectile(5, 0, TestSim.V("1", "1"), TargetRef.Unit(unit.Id), unit.Position, Fix.Half,
            Fix.FromInt(10), Fix.Zero, TargetLayer.Both);
        s.ProjectileList.Add(shot); Changed("projectile added");
        shot.Position = TestSim.V("1", "2"); Changed("projectile position");
        shot.AimPoint = TestSim.V("2", "2"); Changed("projectile aim point");

        for (int i = 0; i < s.Map.Definition.Structures.Count; i++)
        {
            s.Map.DestroyStructure(i); Changed("destroyed structure " + i);
        }
    }

    [Fact]
    public void Hash_CoversUnitData()
    {
        // Same everything except one unit stat in the roster file: the hashes must differ.
        MatchRules rules = Rules;
        UnitRoster original = TestSim.LoadFantasy();
        UnitRoster tweaked = UnitRoster.FromJson(TestSim.FantasyUnitsJson().Replace("\"hp\": 1800", "\"hp\": 1801"));
        Assert.NotEqual(original.ContentHash, tweaked.ContentHash);
        var a = new Simulation(new MatchSetup(rules, Map, TestSim.LoadStructures(), TestSim.DefaultDeck(rules, original), TestSim.DefaultDeck(rules, original)), 1);
        var b = new Simulation(new MatchSetup(rules, Map, TestSim.LoadStructures(), TestSim.DefaultDeck(rules, original), TestSim.DefaultDeck(rules, tweaked)), 1);
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_DistinguishesWhichPlayerHasAValue()
    {
        var a = TestSim.New(Rules, Map, 1);
        var b = TestSim.New(Rules, Map, 1);
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
  ""deploySpawnDelaySeconds"": 1, ""handSize"": 4, ""deckSize"": 8,
  ""unitSeparationDistance"": 0.6, ""unitSeparationPushPerSecond"": 1.5, ""unitSpawnSpacing"": 0.5,
  ""suddenDeathIncomeMultiplier"": 2, ""unitStoppedPushFactor"": 0.3, ""aggroRadius"": 5.5, ""meleeTargetCrowdPenalty"": 1 }");
        // Likewise a fixed inline map, so editing content/maps does not move the pin.
        MapDefinition map = MapTestData.Small();
        // ...and fixed structure stats, so tuning content/structures.json does not move it either.
        StructureCatalog structures = TestSim.Structures(hp: "3000", keepHp: "6000", damage: "40", range: "2.5");
        ulong initialHash = TestSim.New(rules, map, 12345, structures).ComputeHash();
        Simulation sim = SimulationTests.RunFullMatch(rules, 12345, ScriptedLog(rules, 12345, map), map: map,
            structures: structures);
        // The pin must cover deploys, spawns, movement and combat, not just the clock.
        MatchState s = sim.State;
        _output.WriteLine("units " + s.Units.Count + " created " + (s.NextUnitId - 1) + " shots " + s.NextProjectileId
            + " scores " + s.Players[0].Score + "/" + s.Players[1].Score + " end " + s.Tick + " " + s.EndReason + " "
            + s.TieBreakRule + " winner " + s.Winner + " hash 0x" + sim.ComputeHash().ToString("X16")
            + " initial 0x" + initialHash.ToString("X16"));
        Assert.Equal(PinnedInitialHash, initialHash);
        Assert.True(s.Players.All(p => p.IgnoredDeploys > 0), "scripted match should include rejected deploys");
        Assert.True(s.NextUnitId - 1 > s.Units.Count, "units should have died");
        Assert.True(s.NextProjectileId > 10, "projectiles should have flown");
        Assert.True(s.Players.All(p => p.Score > Fix.Zero), "both players should have damaged structures");
        Assert.NotEqual(EndReason.None, s.EndReason);
        Assert.Equal(PinnedFinalHash, sim.ComputeHash());
    }

    private const ulong PinnedInitialHash = 0x22052D77FEFD544EUL;
    private const ulong PinnedFinalHash = 0xFDB827F2542E267DUL;
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
