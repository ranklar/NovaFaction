using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Observers;
using Xunit.Abstractions;

namespace NovaFaction.Sim.Tests;

/// <summary>The match statistics hook (docs/design.md "Match observer").</summary>
public class MatchObserverTests
{
    private readonly ITestOutputHelper _output;

    public MatchObserverTests(ITestOutputHelper output) => _output = output;

    /// <summary>Records every event, in order.</summary>
    private sealed class Recorder : IMatchObserver
    {
        public readonly List<object> Events = new();
        public readonly List<CardDeployedEvent> Deploys = new();
        public readonly List<SpellCastEvent> Spells = new();
        public readonly List<AbilityCastEvent> Abilities = new();
        public readonly List<UnitDiedEvent> Deaths = new();
        public readonly List<StructureDamagedEvent> Damage = new();
        public readonly List<StructureDestroyedEvent> Destroyed = new();
        public readonly List<MineCapturedEvent> Captures = new();
        public readonly List<ChestCollectedEvent> Chests = new();
        public readonly List<GoldAccruedEvent> Gold = new();
        public readonly List<MatchEndedEvent> Ends = new();

        public void OnCardDeployed(in CardDeployedEvent e) { Deploys.Add(e); Events.Add(e); }
        public void OnSpellCast(in SpellCastEvent e) { Spells.Add(e); Events.Add(e); }
        public void OnAbilityCast(in AbilityCastEvent e) { Abilities.Add(e); Events.Add(e); }
        public void OnUnitDied(in UnitDiedEvent e) { Deaths.Add(e); Events.Add(e); }
        public void OnStructureDamaged(in StructureDamagedEvent e) { Damage.Add(e); Events.Add(e); }
        public void OnStructureDestroyed(in StructureDestroyedEvent e) { Destroyed.Add(e); Events.Add(e); }
        public void OnMineCaptured(in MineCapturedEvent e) { Captures.Add(e); Events.Add(e); }
        public void OnChestCollected(in ChestCollectedEvent e) { Chests.Add(e); Events.Add(e); }
        public void OnGoldAccrued(in GoldAccruedEvent e) { Gold.Add(e); Events.Add(e); }
        public void OnMatchEnded(in MatchEndedEvent e) { Ends.Add(e); Events.Add(e); }
    }

    private static List<ulong> RunWithHashes(Simulation sim, CommandLog? script = null)
    {
        var hashes = new List<ulong> { sim.ComputeHash() };
        while (!sim.IsEnded)
        {
            int tick = sim.State.Tick;
            sim.Tick(script != null && tick < script.TickCount ? script.GetCommands(tick) : Array.Empty<Command>());
            hashes.Add(sim.ComputeHash());
        }
        return hashes;
    }

    private static MatchSetup BotSetup(string bot0, string bot1) =>
        BotTests.ShippedSetup().WithBot(0, BotTests.LoadBot(bot0)).WithBot(1, BotTests.LoadBot(bot1));

    // ------------------------------------------------------------ no effect on the match

    [Theory]
    [InlineData(1UL, "aggressive", "swarm")]
    [InlineData(2UL, "balanced", "turtle")]
    [InlineData(3UL, "swarm", "aggressive")]
    public void BotMatch_IsIdentical_WithAndWithoutObserver(ulong seed, string bot0, string bot1)
    {
        List<ulong> plain = RunWithHashes(new Simulation(BotSetup(bot0, bot1), seed));
        var recorder = new Recorder();
        List<ulong> observed = RunWithHashes(new Simulation(BotSetup(bot0, bot1), seed) { Observer = recorder });

        Assert.Equal(plain, observed);
        Assert.NotEmpty(recorder.Deploys);
        Assert.NotEmpty(recorder.Deaths);
    }

    [Fact]
    public void PinnedScriptedMatch_KeepsItsPinnedHash_WithAnObserver_AndReportsEveryKindOfEvent()
    {
        MatchRules rules = DeterminismTests.PinRules();
        MapDefinition map = MapTestData.Small();
        var recorder = new Recorder();
        var sim = new Simulation(TestSim.Setup(rules, map, DeterminismTests.PinStructures()), DeterminismTests.PinSeed)
        {
            Observer = recorder,
        };
        RunWithHashes(sim, DeterminismTests.ScriptedLog(rules, DeterminismTests.PinSeed, map));

        Assert.Equal(DeterminismTests.PinnedFinalHash, sim.ComputeHash());
        _output.WriteLine("deploys " + recorder.Deploys.Count + " spells " + recorder.Spells.Count + " abilities "
            + recorder.Abilities.Count + " deaths " + recorder.Deaths.Count + " hits " + recorder.Damage.Count + " destroyed "
            + recorder.Destroyed.Count + " captures " + recorder.Captures.Count + " chests " + recorder.Chests.Count);
        Assert.NotEmpty(recorder.Deploys);
        Assert.NotEmpty(recorder.Spells);
        Assert.NotEmpty(recorder.Abilities);
        Assert.NotEmpty(recorder.Deaths);
        Assert.NotEmpty(recorder.Damage);
        Assert.NotEmpty(recorder.Chests);
        Assert.Contains(recorder.Damage, e => e.Source.Kind == DamageSourceKind.Spell && e.Source.CardId == "fireball");
        Assert.Contains(recorder.Deaths, e => e.Killer.Kind == DamageSourceKind.Structure);
        Assert.Contains(recorder.Deaths, e => e.Killer.Kind == DamageSourceKind.Unit);
        CheckConsistency(sim, recorder);
    }

    // ------------------------------------------------------------ events agree with the state

    [Theory]
    [InlineData(4UL, "aggressive", "turtle")]
    [InlineData(5UL, "swarm", "balanced")]
    public void Events_AddUpToTheFinalState(ulong seed, string bot0, string bot1)
    {
        var recorder = new Recorder();
        MatchResult result = HeadlessMatch.Run(BotSetup(bot0, bot1), seed, observer: recorder);
        CheckConsistency(result.Simulation, recorder);
    }

    private static void CheckConsistency(Simulation sim, Recorder recorder)
    {
        MatchState s = sim.State;
        var log = new List<Command>();
        for (int t = 0; t < sim.Log.TickCount; t++)
        {
            log.AddRange(sim.Log.GetCommands(t));
        }

        for (int p = 0; p < 2; p++)
        {
            PlayerState player = s.GetPlayer(p);
            // Every accepted deploy is one deploy or spell event; every accepted ability one ability event.
            int deploys = log.Count(c => c.Player == p && c.Type == CommandType.DeployCard);
            Assert.Equal(deploys - player.IgnoredDeploys,
                recorder.Deploys.Count(e => e.Player == p) + recorder.Spells.Count(e => e.Player == p));
            int abilities = log.Count(c => c.Player == p && c.Type == CommandType.LeaderAbility);
            Assert.Equal(abilities - player.IgnoredAbilities, recorder.Abilities.Count(e => e.Player == p));

            // Gold: start + everything banked - everything spent = what is left.
            Fix banked = Sum(recorder.Gold.Where(e => e.Player == p).Select(e => e.Amount));
            Fix spent = Fix.FromInt(recorder.Deploys.Where(e => e.Player == p).Sum(e => e.Cost)
                + recorder.Spells.Where(e => e.Player == p).Sum(e => e.Cost));
            Assert.Equal(player.Gold, sim.Rules.GoldStartingAmount + banked - spent);
            Assert.Equal(player.GoldFromMap,
                Sum(recorder.Gold.Where(e => e.Player == p && e.Source != GoldSource.Base).Select(e => e.Amount)));
            Assert.Equal(Sum(recorder.Chests.Where(e => e.Player == p).Select(e => e.GoldReceived)),
                Sum(recorder.Gold.Where(e => e.Player == p && e.Source == GoldSource.Chest).Select(e => e.Amount)));

            // Score = structure HP removed + destruction bonuses.
            Fix score = Sum(recorder.Damage.Where(e => e.Source.Player == p).Select(e => e.Amount))
                + Sum(recorder.Destroyed.Where(e => e.Source.Player == p).Select(e => e.DestructionBonus));
            Assert.Equal(player.Score, score);
            Assert.All(recorder.Damage, e => Assert.NotEqual(e.Owner, e.Source.Player));
        }

        // Every unit that ever existed and is gone died once, with a killer from the other side.
        Assert.Equal(s.NextUnitId - 1 - s.Units.Count, recorder.Deaths.Count);
        Assert.Equal(recorder.Deaths.Count, recorder.Deaths.Select(e => e.UnitId).Distinct().Count());
        Assert.All(recorder.Deaths, e =>
        {
            Assert.NotEqual(DamageSourceKind.None, e.Killer.Kind);
            Assert.NotEqual(e.Owner, e.Killer.Player);
            Assert.False(string.IsNullOrEmpty(e.Killer.CardId));
        });

        // Structures: destroyed ones match the state; the last damage event on each shows its HP.
        Assert.Equal(s.Structures.Where(x => x.IsDestroyed).Select(x => x.Index).OrderBy(i => i),
            recorder.Destroyed.Select(e => e.StructureIndex).OrderBy(i => i));
        foreach (StructureState structure in s.Structures)
        {
            StructureDamagedEvent[] hits = recorder.Damage.Where(e => e.StructureIndex == structure.Index).ToArray();
            Assert.Equal(structure.MaxHp - structure.Hp, Sum(hits.Select(e => e.Amount)));
            if (hits.Length > 0)
            {
                Assert.Equal(structure.Hp, hits[^1].HpLeft);
            }
        }

        // Mines end with the owner of their last capture.
        foreach (Economy.MineState mine in s.Mines)
        {
            MineCapturedEvent[] captures = recorder.Captures.Where(e => e.MineIndex == mine.Index).ToArray();
            Assert.Equal(captures.Length == 0 ? Economy.MineState.Nobody : captures[^1].Player, mine.Owner);
        }

        // Ticks never go backwards, and the end is reported once, last.
        int previous = 0;
        foreach (object e in recorder.Events)
        {
            int tick = (int)e.GetType().GetProperty("Tick")!.GetValue(e)!;
            Assert.True(tick >= previous, "event ticks must not decrease");
            previous = tick;
        }
        MatchEndedEvent end = Assert.Single(recorder.Ends);
        Assert.IsType<MatchEndedEvent>(recorder.Events[^1]);
        Assert.Equal(s.Tick, end.Tick);
        Assert.Equal(s.Winner, end.Winner);
        Assert.Equal(s.EndReason, end.Reason);
        Assert.Equal(s.TieBreakRule, end.TieBreakRule);
        Assert.Equal(s.GetPlayer(0).Score, end.Score0);
        Assert.Equal(s.GetPlayer(1).Score, end.Score1);
    }

    private static Fix Sum(IEnumerable<Fix> values)
    {
        Fix total = Fix.Zero;
        foreach (Fix v in values)
        {
            total += v;
        }
        return total;
    }

    // ------------------------------------------------------------ specific events

    [Fact]
    public void Deploy_SpellAndAbility_ReportCardLevelCostAndTarget()
    {
        MatchRules rules = TestSim.Rules(start: "10", cap: "10");
        var cards = TestSim.LoadFantasyCards();
        var deck = Cards.Deck.Create(cards, TestSim.DefaultDeckIds, rules.DeckSize, new[] { 1, 1, 1, 1, 1, 1, 4, 2 });
        var sim = new Simulation(new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.HarmlessStructures(), deck, deck), 1);
        var recorder = new Recorder();
        sim.Observer = recorder;
        FixVector2 home = TestSim.V("9.5", "10.5");

        TestSim.Deploy(sim, 0, "warlord", home);
        TestSim.Run(sim, 20);
        TestSim.Deploy(sim, 0, "fireball", TestSim.V("9", "28"));
        sim.Tick(new[] { Command.LeaderAbility(sim.State.Tick, 0, 0, home) });

        CardDeployedEvent deploy = Assert.Single(recorder.Deploys);
        Assert.Equal((0, 0, "warlord", 4, cards.Get("warlord").Cost, home),
            (deploy.Tick, deploy.Player, deploy.CardId, deploy.Level, deploy.Cost, deploy.Target));
        SpellCastEvent spell = Assert.Single(recorder.Spells);
        Assert.Equal((21, "fireball", 2, 4, 1, 41), (spell.Tick, spell.CardId, spell.Level, spell.Cost, spell.SpellId, spell.LandTick));
        AbilityCastEvent ability = Assert.Single(recorder.Abilities);
        Assert.Equal((22, "warlord", AbilityType.Rally, home), (ability.Tick, ability.LeaderCardId, ability.Type, ability.Target));
    }

    [Fact]
    public void GoldEvents_ReportOnlyWhatWasBanked()
    {
        // 1 gold/s base income from 0, cap 10: 10 s of base income, then nothing more is banked.
        var sim = new Simulation(TestSim.Setup(TestSim.Rules(income: "1", start: "0", cap: "10"), MapTestData.LoadTwoLane()), 1);
        var recorder = new Recorder();
        sim.Observer = recorder;
        TestSim.Run(sim, 20 * 15);

        Assert.All(recorder.Gold, e => Assert.Equal(GoldSource.Base, e.Source));
        Assert.All(recorder.Gold, e => Assert.True(e.Amount > Fix.Zero));
        Assert.Equal(Fix.FromInt(10), Sum(recorder.Gold.Where(e => e.Player == 0).Select(e => e.Amount)));
        Assert.True(recorder.Gold.Max(e => e.Tick) < 200, "nothing is banked at the cap");
    }

    [Fact]
    public void Tick_FromInsideAnObserver_Throws()
    {
        var sim = new Simulation(BotSetup("aggressive", "aggressive"), 1);
        sim.Observer = new Reentrant(sim);
        var ex = Assert.Throws<InvalidOperationException>(() => TestSim.Run(sim, 400));
        Assert.Contains("while a tick is running", ex.Message);
    }

    private sealed class Reentrant : MatchObserver
    {
        private readonly Simulation _sim;

        public Reentrant(Simulation sim) => _sim = sim;

        public override void OnCardDeployed(in CardDeployedEvent e) => _sim.Tick();
    }

    [Fact]
    public void Observer_CanBeAttachedAndRemovedBetweenTicks()
    {
        List<ulong> plain = RunWithHashes(new Simulation(BotSetup("balanced", "swarm"), 8));
        var sim = new Simulation(BotSetup("balanced", "swarm"), 8);
        var recorder = new Recorder();
        var hashes = new List<ulong> { sim.ComputeHash() };
        while (!sim.IsEnded)
        {
            sim.Observer = sim.State.Tick % 7 < 3 ? recorder : null;
            sim.Tick();
            hashes.Add(sim.ComputeHash());
        }
        Assert.Equal(plain, hashes);
        Assert.NotEmpty(recorder.Gold);
    }
}
