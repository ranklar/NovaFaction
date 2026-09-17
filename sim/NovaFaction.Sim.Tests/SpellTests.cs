using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Spells;
using NovaFaction.Sim.Units;
using static NovaFaction.Sim.Tests.CombatTests;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Spell cards: casting, delays, area damage, zones, scoring and determinism (docs/design.md "Spells"). twolane,
/// structures that do nothing, no income, 10 starting gold.
/// Fireball: cost 4, radius 2.5, 325 damage, lands 1 s (20 ticks) after the cast, structures take 0.35 of it.
/// Blizzard: cost 3, radius 3, 30 damage every 0.5 s (10 ticks) for 4 s (80 ticks), lands 0.5 s after the cast.
/// </summary>
public class SpellTests
{
    /// <summary>Both spells plus six units and the leader (the knight sits out).</summary>
    internal static readonly string[] BothSpellsDeckIds =
        TestSim.DefaultDeckIds.Select(id => id == "knight" ? "blizzard" : id).ToArray();

    private static void Step(Simulation sim) => sim.Tick(Array.Empty<Command>());

    private static Simulation SpellSim(StructureCatalog? structures = null, MatchRules? rules = null) =>
        TestSim.New(rules ?? TestSim.Rules(income: "0", start: "10"), MapTestData.LoadTwoLane(), 1,
            structures ?? Quiet(), BothSpellsDeckIds);

    private static SpellDefinition Spell(Simulation sim, string id) => sim.State.GetPlayer(0).Deck.Catalog.Spells.Get(id);

    // ------------------------------------------------------------ Fireball

    [Fact]
    public void Fireball_LandsAfterItsDelay_AndHitsEveryLegalEnemyInRadius()
    {
        Simulation sim = SpellSim();
        SpellDefinition fireball = Spell(sim, "fireball");
        FixVector2 target = TestSim.V("4", "23"); // 1 below tower_1_west (x 3-5, y 24-26)
        Unit center = Place(sim, 1, "dummy", "4", "23.5");
        Unit near = Place(sim, 1, "dummy", "5.5", "22");      // 1.80 away
        Unit onEdge = Place(sim, 1, "dummy", "6.5", "23");    // exactly 2.5 away: inside
        Unit flyer = Place(sim, 1, "balloon", "4", "24.5");   // over the tower; Fireball hits air too
        Unit outside = Place(sim, 1, "dummy", "4", "26.01");  // just beyond 2.5 (above the tower)
        Unit friend = Place(sim, 0, "dummy", "4", "23");      // the caster's own unit, dead center
        Fix towerHp = sim.State.Structures[Tower1West].Hp;
        PlayerState p0 = sim.State.GetPlayer(0);
        string nextCard = p0.Cards.NextCard.Id;

        TestSim.Deploy(sim, 0, "fireball", target); // tick 0

        // Paid, cycled, pending: nothing hurt yet.
        Assert.Equal(Fix.FromInt(10 - fireball.Cost), p0.Gold);
        Assert.Equal(0, p0.IgnoredDeploys);
        Assert.DoesNotContain(p0.Cards.Hand, c => c?.Id == "fireball");
        Assert.Contains(p0.Cards.Hand, c => c?.Id == nextCard);
        Assert.Equal("fireball", p0.Cards.Queue[p0.Cards.Queue.Count - 1].Id);
        Assert.Empty(sim.State.PendingSpawns);
        SpellInstance cast = Assert.Single(sim.State.PendingSpells);
        Assert.Equal(1, cast.Id);
        Assert.Equal(0, cast.Owner);
        Assert.Equal("fireball", cast.DefinitionId);
        Assert.Equal(target, cast.Target);
        Assert.Equal(20, cast.LandTick);
        Assert.False(cast.IsZone);
        Assert.Equal(2, sim.State.NextSpellId);

        TestSim.Run(sim, 19); // ticks 1-19
        Assert.Single(sim.State.PendingSpells);
        Assert.All(sim.State.Units, u => Assert.Equal(u.Definition.Hp, u.Hp));
        Assert.Equal(towerHp, sim.State.Structures[Tower1West].Hp);

        Step(sim); // tick 20: it lands
        Assert.Empty(sim.State.PendingSpells);
        Assert.Empty(sim.State.SpellZones);
        foreach (Unit hit in new[] { center, near, onEdge, flyer })
        {
            Assert.Equal(hit.Definition.Hp - fireball.Damage, hit.Hp);
        }
        Assert.Equal(outside.Definition.Hp, outside.Hp);
        Assert.Equal(friend.Definition.Hp, friend.Hp);

        Fix structureHit = Fix.FromInt(325) * Fix.Parse("0.35");
        Assert.Equal(structureHit, fireball.StructureDamage);
        Assert.Equal(towerHp - structureHit, sim.State.Structures[Tower1West].Hp);
        Assert.Equal(structureHit, p0.Score);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1).Score);
        foreach (int other in new[] { Keep0, Tower0West, Tower0East, Keep1, Tower1East })
        {
            Assert.Equal(sim.State.Structures[other].Stats.Hp, sim.State.Structures[other].Hp);
        }

        TestSim.Run(sim, 40); // nothing more happens
        Assert.Equal(center.Definition.Hp - fireball.Damage, center.Hp);
    }

    [Fact]
    public void Fireball_KillsUnits_AndNeverHurtsTheCastersStructures()
    {
        Simulation sim = SpellSim();
        Unit fragile = Place(sim, 1, "fragile", "4", "9");
        // Cast by player 0 onto its own tower_0_west (x 3-5, y 6-8), where an enemy stands.
        TestSim.Deploy(sim, 0, "fireball", TestSim.V("4", "9"));
        TestSim.Run(sim, 20);
        Assert.Null(sim.State.FindUnit(fragile.Id));
        Assert.All(sim.State.Structures, s => Assert.Equal(s.Stats.Hp, s.Hp));
        Assert.All(sim.State.Players, p => Assert.Equal(Fix.Zero, p.Score));
    }

    [Fact]
    public void GroundOnlySpell_SparesFlyers_AndAirOnlySpell_SparesGroundAndStructures()
    {
        foreach (string layer in new[] { "ground", "air" })
        {
            string spells = TestSim.FantasySpellsJson().Replace("\r\n", "\n").Replace(
                "\"durationSeconds\": 0,\n      \"targets\": \"both\"", "\"durationSeconds\": 0,\n      \"targets\": \"" + layer + "\"");
            var cards = new CardCatalog(TestSim.LoadFantasy(), SpellBook.FromJson(spells));
            Assert.Equal(layer == "ground" ? TargetLayer.Ground : TargetLayer.Air, cards.Spells.Get("fireball").Targets);
            MatchRules rules = TestSim.Rules(income: "0", start: "10");
            Deck deck = Deck.Create(cards, TestSim.DefaultDeckIds, 8);
            var sim = new Simulation(new MatchSetup(rules, MapTestData.LoadTwoLane(), Quiet(), deck, deck), 1);
            Unit walker = Place(sim, 1, "dummy", "4", "23");
            Unit flyer = Place(sim, 1, "balloon", "4.5", "23");
            TestSim.Deploy(sim, 0, "fireball", TestSim.V("4", "23"));
            TestSim.Run(sim, 20);
            Assert.Equal(layer == "ground", walker.Hp < walker.Definition.Hp);
            Assert.Equal(layer == "air", flyer.Hp < flyer.Definition.Hp);
            Assert.Equal(layer == "ground", sim.State.GetPlayer(0).Score > Fix.Zero);
        }
    }

    // ------------------------------------------------------------ zones

    [Fact]
    public void Blizzard_PulsesForItsDuration_ThenDisappears()
    {
        Simulation sim = SpellSim();
        SpellDefinition blizzard = Spell(sim, "blizzard");
        Unit victim = Place(sim, 1, "dummy", "4", "22");
        Unit outside = Place(sim, 1, "dummy", "4", "18.9"); // 3.1 away
        Unit friend = Place(sim, 0, "dummy", "4.5", "22");
        Fix towerHp = sim.State.Structures[Tower1West].Hp;
        TestSim.Deploy(sim, 0, "blizzard", TestSim.V("4", "22")); // tick 0; tower_1_west is 2 away

        SpellInstance zone = Assert.Single(sim.State.PendingSpells);
        Assert.True(zone.IsZone);
        Assert.Equal(10, zone.LandTick);
        Assert.Equal(90, zone.EndTick);
        Assert.Equal(10, zone.PulseIntervalTicks);

        var pulseTicks = new List<int>();
        Fix last = victim.Hp;
        while (sim.State.Tick < 200)
        {
            int tick = sim.State.Tick;
            Step(sim);
            if (victim.Hp != last)
            {
                Assert.Equal(last - blizzard.Damage, victim.Hp);
                pulseTicks.Add(tick);
                last = victim.Hp;
            }
            // Pending until it lands, then an active zone after each of its active ticks but the last.
            Assert.Equal(tick < 10, sim.State.PendingSpells.Count == 1);
            Assert.Equal(tick >= 10 && tick < 89, sim.State.SpellZones.Count == 1);
            if (sim.State.SpellZones.Count == 1)
            {
                Assert.Same(zone, sim.State.SpellZones[0]);
            }
        }
        Assert.Equal(new[] { 10, 20, 30, 40, 50, 60, 70, 80 }, pulseTicks);
        Assert.Equal(victim.Definition.Hp - blizzard.Damage * Fix.FromInt(8), victim.Hp);
        Assert.Equal(outside.Definition.Hp, outside.Hp);
        Assert.Equal(friend.Definition.Hp, friend.Hp);

        Fix removed = blizzard.StructureDamage * Fix.FromInt(8);
        Assert.Equal(towerHp - removed, sim.State.Structures[Tower1West].Hp);
        Assert.Equal(removed, sim.State.GetPlayer(0).Score);
        Assert.Empty(sim.State.PendingSpells);
        Assert.Empty(sim.State.SpellZones);
    }

    [Fact]
    public void Zone_HurtsUnitsThatWalkIntoIt_AndOverlappingCastsStack()
    {
        Simulation sim = SpellSim();
        SpellDefinition blizzard = Spell(sim, "blizzard");
        // Player 0 casts twice on one spot, five ticks apart.
        TestSim.Deploy(sim, 0, "blizzard", TestSim.V("9", "20"));   // tick 0: lands tick 10, pulses 10..80
        TestSim.Run(sim, 4);
        TestSim.Deploy(sim, 0, "blizzard", TestSim.V("9", "20"));   // tick 5: lands tick 15, pulses 15..85
        Assert.Equal(new[] { 1, 2 }, sim.State.PendingSpells.Select(s => s.Id));
        TestSim.Run(sim, 11); // through tick 16
        Assert.Equal(new[] { 1, 2 }, sim.State.SpellZones.Select(s => s.Id));

        // A player 1 dummy dropped into the zone later takes both zones' later pulses.
        Unit late = Place(sim, 1, "dummy", "9", "20");
        TestSim.Run(sim, 100);
        // Zone 1 pulses at 20..80 (7), zone 2 at 25..85 (7).
        Assert.Equal(late.Definition.Hp - blizzard.Damage * Fix.FromInt(14), late.Hp);
        Assert.Empty(sim.State.SpellZones);
    }

    // ------------------------------------------------------------ where and when

    public static IEnumerable<object[]> CastTargets() => new[]
    {
        new object[] { "9", "29.5", true },     // on the enemy Keep
        new object[] { "4", "15.5", true },     // in the river
        new object[] { "5.5", "15.5", true },   // on a mine
        new object[] { "0", "0", true },        // the map's corner
        new object[] { "17.99", "31.99", true },
        new object[] { "-0.01", "10", false },  // off the map
        new object[] { "10", "32", false },
        new object[] { "18", "10", false },
    };

    [Theory]
    [MemberData(nameof(CastTargets))]
    public void Spells_CanBeCastAnywhereOnTheMap(string x, string y, bool accepted)
    {
        Simulation sim = SpellSim();
        FixVector2 target = TestSim.V(x, y);
        Assert.False(sim.State.Map.IsDeployable(0, target)); // outside every deploy zone (or off the map)
        TestSim.Deploy(sim, 0, "fireball", target);
        Assert.Equal(accepted ? 0 : 1, sim.State.GetPlayer(0).IgnoredDeploys);
        Assert.Equal(accepted ? 1 : 0, sim.State.PendingSpells.Count);
        Assert.Equal(Fix.FromInt(accepted ? 6 : 10), sim.State.GetPlayer(0).Gold);

        // A unit card on the same spot is still rejected.
        Simulation units = SpellSim();
        TestSim.Deploy(units, 0, "goblin_pack", target);
        Assert.Equal(1, units.State.GetPlayer(0).IgnoredDeploys);
    }

    [Fact]
    public void Spell_WithoutEnoughGold_IsRejected()
    {
        Simulation sim = SpellSim(rules: TestSim.Rules(income: "0", start: "3.9"));
        TestSim.Deploy(sim, 1, "fireball", TestSim.V("9", "5"));
        Assert.Equal(1, sim.State.GetPlayer(1).IgnoredDeploys);
        Assert.Empty(sim.State.PendingSpells);
        Assert.Equal(Fix.Parse("3.9"), sim.State.GetPlayer(1).Gold);
        TestSim.Deploy(sim, 1, "blizzard", TestSim.V("9", "5")); // 3 is affordable
        Assert.Single(sim.State.PendingSpells);
    }

    [Fact]
    public void ZeroDelaySpell_LandsOnItsCastTick()
    {
        string spells = TestSim.FantasySpellsJson().Replace("\"castDelaySeconds\": 1,", "\"castDelaySeconds\": 0,");
        var cards = new CardCatalog(TestSim.LoadFantasy(), SpellBook.FromJson(spells));
        MatchRules rules = TestSim.Rules(income: "0", start: "10");
        Deck deck = Deck.Create(cards, TestSim.DefaultDeckIds, 8);
        var sim = new Simulation(new MatchSetup(rules, MapTestData.LoadTwoLane(), Quiet(), deck, deck), 1);
        Unit victim = Place(sim, 1, "dummy", "9", "20");
        TestSim.Deploy(sim, 0, "fireball", TestSim.V("9", "20"));
        Assert.Empty(sim.State.PendingSpells);
        Assert.Equal(victim.Definition.Hp - Fix.FromInt(325), victim.Hp);
        Assert.Equal(2, sim.State.NextSpellId);
    }

    // ------------------------------------------------------------ scoring and match resolution

    [Fact]
    public void SpellDamage_CanDestroyAStructure_ForScoreAndBonus()
    {
        // Towers with 100 HP: a Fireball's 113.75 structure damage destroys one; only the HP removed scores.
        Simulation sim = SpellSim(TestSim.Structures(hp: "100", keepHp: "1000000", damage: "0", range: "0.1", bonus: "500"));
        TestSim.Deploy(sim, 1, "fireball", TestSim.V("14", "9")); // below tower_0_east (x 13-15, y 6-8)
        TestSim.Run(sim, 20);
        Assert.True(sim.State.Map.IsDestroyed(Tower0East));
        Assert.Equal(Fix.FromInt(100 + 500), sim.State.GetPlayer(1).Score);
        Assert.Single(sim.State.Map.GetUnlockedZones(1)); // player 1 may now deploy on that side
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SpellDamage_ToAStructure_WinsSuddenDeath(int caster)
    {
        MatchRules rules = TestSim.Rules(income: "0", start: "10", matchSeconds: "2", suddenDeathSeconds: "10");
        Simulation sim = SpellSim(rules: rules);
        TestSim.Run(sim, 40);
        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase);

        // A Fireball at the enemy's west tower; nothing else can do damage.
        FixVector2 belowTower1West = TestSim.V("4", "23");
        TestSim.Deploy(sim, caster, "fireball", caster == 0 ? belowTower1West : TestSim.V("18", "32") - belowTower1West);
        TestSim.Run(sim, 19);
        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase);
        Step(sim);
        Assert.Equal(MatchPhase.Ended, sim.State.Phase);
        Assert.Equal(EndReason.FirstDamage, sim.State.EndReason);
        Assert.Equal(caster, sim.State.Winner);
        Assert.Equal(Spell(sim, "fireball").StructureDamage, sim.State.GetPlayer(caster).Score);
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void Hash_CoversPendingSpellsAndZones()
    {
        // Same everything except where the spell was aimed.
        Simulation a = SpellSim(), b = SpellSim();
        TestSim.Deploy(a, 0, "blizzard", TestSim.V("9", "20"));
        TestSim.Deploy(b, 0, "blizzard", TestSim.V("9", "20.5"));
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash()); // pending
        TestSim.Run(a, 15);
        TestSim.Run(b, 15);
        Assert.Single(a.State.SpellZones);
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash()); // active zones

    }

    [Fact]
    public void ScriptedCasts_HashIdenticallyEveryTick_InTwoIndependentSims()
    {
        // Every second each player plays a card chosen from what the hand shows: a spell whenever one is in hand
        // (aimed in turn at an enemy tower, enemy ground, the river and the enemy Keep), otherwise the first card at
        // their own base. Structures shoot for real, so units die inside zones. Both sims are built independently;
        // decisions read sim A's hand, and both sims must agree on it.
        MatchRules Rules() => TestSim.Rules(income: "2", start: "10");
        StructureCatalog Structures() => TestSim.Structures(hp: "3000", keepHp: "5000", damage: "60", range: "6");
        FixVector2[] spellTargets = { TestSim.V("4", "23"), TestSim.V("9", "20"), TestSim.V("4.5", "16"), TestSim.V("9", "28.5") };
        FixVector2 baseDrop = TestSim.V("4.5", "10.5");
        FixVector2 corner = TestSim.V("18", "32");

        Simulation a = TestSim.New(Rules(), MapTestData.LoadTwoLane(), 99, Structures(), BothSpellsDeckIds);
        CardCatalog cards = CardCatalog.FromJson(TestSim.FantasyUnitsJson(), TestSim.FantasySpellsJson());
        Deck deck = Deck.Create(cards, BothSpellsDeckIds, 8);
        var b = new Simulation(new MatchSetup(Rules(), MapTestData.LoadTwoLane(), Structures(), deck, deck), 99);
        Assert.Equal(a.ComputeHash(), b.ComputeHash());

        int zonesSeen = 0, mostPending = 0;
        var casts = new int[2];
        var sequence = new int[2];
        for (int tick = 0; tick < 40 * 20; tick++)
        {
            var commands = new List<Command>();
            if (tick % 20 == 0)
            {
                for (int player = 0; player < 2; player++)
                {
                    Assert.Equal(TestSim.HandIds(a.State.GetPlayer(player)), TestSim.HandIds(b.State.GetPlayer(player)));
                    IReadOnlyList<CardDefinition?> hand = a.State.GetPlayer(player).Cards.Hand;
                    int slot = 0;
                    FixVector2 at = baseDrop;
                    for (int i = 0; i < hand.Count; i++)
                    {
                        if (hand[i] is SpellDefinition)
                        {
                            slot = i;
                            at = spellTargets[casts[player]++ % spellTargets.Length];
                            break;
                        }
                    }
                    commands.Add(Command.DeployCard(tick, player, sequence[player]++, slot, player == 0 ? at : corner - at));
                }
            }
            a.Tick(commands);
            b.Tick(commands);
            Assert.True(a.ComputeHash() == b.ComputeHash(), "hashes diverged at tick " + tick);
            zonesSeen = Math.Max(zonesSeen, a.State.SpellZones.Count);
            mostPending = Math.Max(mostPending, a.State.PendingSpells.Count);
            if (a.IsEnded)
            {
                break;
            }
        }
        Assert.True(a.State.NextSpellId - 1 >= 10, "the script should cast many spells, cast " + (a.State.NextSpellId - 1));
        Assert.True(zonesSeen >= 2, "zones should overlap in time, saw at most " + zonesSeen);
        Assert.True(mostPending >= 2, "casts from both players should be in flight at once");
        Assert.True(a.State.Players.All(p => p.Score > Fix.Zero), "spells should have hit structures on both sides");
        Assert.True(a.State.NextUnitId - 1 > a.State.Units.Count, "units should have died");

        // The recorded log replays to the same state.
        Simulation replay = Simulation.Replay(new MatchSetup(Rules(), MapTestData.LoadTwoLane(), Structures(), deck, deck), 99, a.Log);
        Assert.Equal(a.ComputeHash(), replay.ComputeHash());
    }
}
