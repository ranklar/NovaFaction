using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;
using static NovaFaction.Sim.Tests.CombatTests;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Card and structure levels, effective stats, the leader passive and the leader ability (docs/design.md "Stat
/// modifiers and levels" and "Leaders"). twolane with structures that do nothing unless a test says otherwise.
/// </summary>
public class LevelsAndLeadersTests
{
    private static readonly Fix Bonus = Fix.Parse("0.06");

    private static void Step(Simulation sim) => sim.Tick(Array.Empty<Command>());

    private static Fix Pct(string factor) => Fix.Parse(factor) - Fix.One;

    /// <summary>Levels in <see cref="TestSim.DefaultDeckIds"/> order, 1 unless named.</summary>
    internal static int[] Levels(params (string id, int level)[] named) =>
        TestSim.DefaultDeckIds.Select(id => named.Where(n => n.id == id).Select(n => n.level).DefaultIfEmpty(1).First())
            .ToArray();

    /// <summary>The shipped fantasy cards with the warlord's passive and ability replaced (null = removed).</summary>
    internal static CardCatalog Fantasy(string? passive, string? ability)
    {
        string json = TestSim.FantasyUnitsJson();
        int start = json.IndexOf("\"passive\"", StringComparison.Ordinal);
        int end = json.IndexOf("\"placeholder\"", start, StringComparison.Ordinal);
        string replacement = (passive == null ? "" : "\"passive\": " + passive + ",\n      ")
            + (ability == null ? "" : "\"ability\": " + ability + ",\n      ");
        json = json.Substring(0, start) + replacement + json.Substring(end);
        return CardCatalog.FromJson(json, TestSim.FantasySpellsJson());
    }

    internal const string WarlordPassive =
        "[{ \"stat\": \"damage\", \"kind\": \"multiply\", \"value\": 1.1, \"appliesTo\": [\"bruiser\", \"swarm\"] }]";

    private static string Ability(string type, string extra, string cooldown = "20", string radius = "4", string range = "6") =>
        "{ \"displayName\": \"Test\", \"type\": \"" + type + "\", \"radius\": " + radius + ", \"range\": " + range
        + ", \"cooldownSeconds\": " + cooldown + ", " + extra + " }";

    private static Simulation Sim(CardCatalog? cards0 = null, CardCatalog? cards1 = null, int[]? levels0 = null,
        int[]? levels1 = null, int structureLevel0 = 1, int structureLevel1 = 1, StructureCatalog? structures = null)
    {
        MatchRules rules = TestSim.Rules(income: "0", start: "10");
        Deck deck0 = Deck.Create(cards0 ?? TestSim.LoadFantasyCards(), TestSim.DefaultDeckIds, 8, levels0);
        Deck deck1 = Deck.Create(cards1 ?? TestSim.LoadFantasyCards(), TestSim.DefaultDeckIds, 8, levels1);
        return new Simulation(new MatchSetup(rules, MapTestData.LoadTwoLane(), structures ?? Quiet(), deck0, deck1,
            structureLevel0, structureLevel1), 1);
    }

    private static void Cast(Simulation sim, int player, FixVector2 target) =>
        sim.Tick(new[] { Command.LeaderAbility(sim.State.Tick, player, 0, target) });

    // ------------------------------------------------------------ levels

    [Fact]
    public void Level5Unit_HasScaledHpAndDamage_OtherStatsUnchanged()
    {
        Simulation sim = Sim(levels0: Levels(("stone_golem", 5), ("knight", 5)));
        Fix factor = sim.Rules.LevelFactor(5);
        Assert.Equal(Fix.One + Bonus * Fix.FromInt(4), factor); // 1 + 0.06 * 4, about 1.24

        TestSim.Deploy(sim, 0, "stone_golem", TestSim.V("9.5", "10.5"));
        Assert.Equal(5, sim.State.PendingSpawns[0].Level);
        TestSim.Run(sim, 20);
        Unit golem = Assert.Single(sim.State.Units);
        UnitDefinition def = golem.Definition;
        Assert.Equal(5, golem.Level);
        Assert.Equal(Fix.FromInt(1800) * factor, golem.MaxHp);
        Assert.Equal(golem.MaxHp, golem.Hp);
        Assert.Equal(Fix.FromInt(90) * factor, golem.Damage);
        Assert.True(Fix.Abs(golem.MaxHp - Fix.FromInt(2232)) < Fix.Parse("0.05"), golem.MaxHp.ToString());
        Assert.True(Fix.Abs(golem.Damage - Fix.Parse("111.6")) < Fix.Parse("0.05"), golem.Damage.ToString());
        Assert.Equal(def.MoveSpeed, golem.MoveSpeed);
        Assert.Equal(def.Range, golem.Range);
        Assert.Equal(def.AttackIntervalSeconds, golem.AttackIntervalSeconds);

        // A level 5 knight gets the level and the Warlord passive (+10% for bruisers): 140 * 1.24 * 1.1.
        Unit knight = sim.State.AddUnit(0, Card(sim, "knight"), TestSim.V("9", "8"), 5);
        Assert.Equal(Fix.FromInt(140) * factor * (Fix.One + Pct("1.1")), knight.Damage);
        Assert.Equal(Fix.FromInt(900) * factor, knight.MaxHp);
    }

    [Fact]
    public void LevelledSpell_DealsScaledDamage()
    {
        Simulation sim = Sim(levels0: Levels(("fireball", 3)));
        TestSim.Deploy(sim, 0, "fireball", TestSim.V("9", "20"));
        var spell = Assert.Single(sim.State.PendingSpells);
        Fix expected = Fix.FromInt(325) * sim.Rules.LevelFactor(3);
        Assert.Equal(expected, spell.Damage);
        Assert.Equal(expected * spell.Definition.StructureDamageMultiplier, spell.StructureDamage);

        Unit dummy = Place(sim, 1, "dummy", "9", "20");
        TestSim.Run(sim, 20);
        Assert.Equal(Fix.FromInt(1000) - expected, dummy.Hp);
    }

    [Fact]
    public void StructureLevel_ScalesTowersAndKeep_OfThatPlayerOnly()
    {
        StructureCatalog stats = TestSim.LoadStructures();
        Simulation sim = Sim(structureLevel0: 3, structures: stats);
        Fix factor = sim.Rules.LevelFactor(3);
        foreach (StructureState s in sim.State.Structures)
        {
            Fix expected = s.Owner == 0 ? factor : Fix.One;
            Assert.Equal(s.Stats.Hp * expected, s.MaxHp);
            Assert.Equal(s.MaxHp, s.Hp);
            Assert.Equal(s.Stats.Damage * expected, s.Damage);
        }
        Assert.True(Fix.Abs(sim.State.Structures[Keep0].MaxHp - Fix.FromInt(4480)) < Fix.Parse("0.05"));
        Assert.True(Fix.Abs(sim.State.Structures[Tower0West].MaxHp - Fix.FromInt(2800)) < Fix.Parse("0.05"));
        Assert.Equal(Fix.FromInt(4000), sim.State.Structures[Keep1].MaxHp);
        Assert.Equal(3, sim.State.GetPlayer(0).StructureLevel);

        // Its shots carry the scaled damage.
        Place(sim, 1, "dummy", "4", "8.5");
        Step(sim);
        Assert.Contains(sim.State.Projectiles, shot => shot.Damage == sim.State.Structures[Tower0West].Damage);
        Assert.All(sim.State.Projectiles, shot => Assert.Equal(0, shot.Owner));
    }

    [Fact]
    public void Deck_KeepsLevelsWithTheirCards_AndLevelsAreValidated()
    {
        CardCatalog cards = TestSim.LoadFantasyCards();
        int[] levels = { 1, 2, 3, 4, 5, 6, 7, 8 };
        Deck deck = Deck.Create(cards, TestSim.DefaultDeckIds, 8, levels);
        for (int i = 0; i < levels.Length; i++)
        {
            Assert.Equal(levels[i], deck.GetLevel(cards.Get(TestSim.DefaultDeckIds[i])));
        }
        Assert.Equal(new[] { "catapult", "elf_archer", "fireball", "goblin_pack", "griffin", "knight", "stone_golem", "warlord" },
            deck.Cards.Select(c => c.Id));
        Assert.Equal(new[] { 6, 4, 8, 3, 5, 2, 1, 7 }, deck.Levels);
        Assert.Equal(7, deck.LeaderLevel);
        Assert.All(Deck.Create(cards, TestSim.DefaultDeckIds, 8).Levels, l => Assert.Equal(1, l));

        Assert.Contains("levels must be between 1", Assert.Throws<ArgumentException>(
            () => Deck.Create(cards, TestSim.DefaultDeckIds, 8, Levels(("knight", 0)))).Message);
        Assert.Throws<ArgumentException>(() => Deck.Create(cards, TestSim.DefaultDeckIds, 8, Levels(("knight", 101))));
        Assert.Contains("8 cards but 7 levels", Assert.Throws<ArgumentException>(
            () => Deck.Create(cards, TestSim.DefaultDeckIds, 8, new[] { 1, 1, 1, 1, 1, 1, 1 })).Message);

        MatchRules rules = TestSim.Rules();
        Deck max = Deck.Create(cards, TestSim.DefaultDeckIds, 8, Levels(("knight", 15)));
        Deck over = Deck.Create(cards, TestSim.DefaultDeckIds, 8, Levels(("knight", 16)));
        _ = new MatchSetup(rules, MapTestData.LoadTwoLane(), Quiet(), max, max, 15, 15);
        Assert.Contains("maxUnitLevel is 15", Assert.Throws<ArgumentException>(
            () => new MatchSetup(rules, MapTestData.LoadTwoLane(), Quiet(), max, over)).Message);
        Assert.Throws<ArgumentException>(() => new MatchSetup(rules, MapTestData.LoadTwoLane(), Quiet(), max, max, 16));
        Assert.Throws<ArgumentException>(() => new MatchSetup(rules, MapTestData.LoadTwoLane(), Quiet(), max, max, 1, 0));
    }

    [Fact]
    public void Hash_CoversDeckAndStructureLevels()
    {
        ulong plain = Sim().ComputeHash();
        Assert.Equal(plain, Sim(levels0: Levels()).ComputeHash());
        Assert.NotEqual(plain, Sim(levels0: Levels(("catapult", 2))).ComputeHash());
        Assert.NotEqual(Sim(levels0: Levels(("catapult", 2))).ComputeHash(), Sim(levels1: Levels(("catapult", 2))).ComputeHash());
        Assert.NotEqual(plain, Sim(structureLevel1: 2).ComputeHash());
    }

    [Fact]
    public void MaxHpChange_ScalesCurrentHpProportionally_BothWays()
    {
        // A Rally that raises max hp by 50% for one second.
        string rally = Ability("rally", "\"durationSeconds\": 1, \"modifiers\": "
            + "[{ \"stat\": \"hp\", \"kind\": \"multiply\", \"value\": 1.5, \"appliesTo\": \"all\" }]");
        Simulation sim = Sim(cards0: Fantasy(WarlordPassive, rally));
        Place(sim, 0, "warlord", "9", "10");
        Unit knight = Place(sim, 0, "knight", "9", "12");
        knight.Hp = Fix.FromInt(450); // half of 900
        Cast(sim, 0, TestSim.V("9", "10"));
        Assert.Equal(Fix.FromInt(1350), knight.MaxHp);
        Assert.Equal(Fix.FromInt(675), knight.Hp); // still half

        knight.Hp = Fix.FromInt(540); // took 135 damage while buffed: 40%
        TestSim.Run(sim, 19);
        Assert.Null(knight.Buff);
        Assert.Equal(Fix.FromInt(900), knight.MaxHp);
        Assert.Equal(Fix.FromInt(360), knight.Hp); // still 40%
    }

    [Fact]
    public void ScaleHp_IsExactAndRoundsToNearest()
    {
        Assert.Equal(Fix.FromInt(999_999), Unit.ScaleHp(Fix.FromInt(1_000_000), Fix.FromInt(999_999), Fix.FromInt(1_000_000)));
        Assert.Equal(Fix.FromRaw(1), Unit.ScaleHp(Fix.FromRaw(2), Fix.FromInt(1), Fix.FromInt(3))); // 0.67 raw -> 1
        Assert.Equal(Fix.FromRaw(0), Unit.ScaleHp(Fix.FromRaw(1), Fix.FromInt(1), Fix.FromInt(3))); // 0.33 raw -> 0
        Assert.Equal(Fix.FromRaw(1), Unit.ScaleHp(Fix.FromRaw(1), Fix.FromInt(1), Fix.FromInt(2))); // 0.5 raw -> 1
    }

    [Fact]
    public void StatMath_SumsModifiers_OrderIndependent_AndClamps()
    {
        var all = (IReadOnlyList<UnitSlot>?)null;
        var mul11 = new Modifier(ModifierStat.Damage, ModifierKind.Multiply, Fix.Parse("1.1"), all);
        var mul13 = new Modifier(ModifierStat.Damage, ModifierKind.Multiply, Fix.Parse("1.3"), all);
        var add5 = new Modifier(ModifierStat.Damage, ModifierKind.Add, Fix.FromInt(5), all);
        var tankOnly = new Modifier(ModifierStat.Damage, ModifierKind.Add, Fix.FromInt(1000), new[] { UnitSlot.Tank });
        var hpMul = new Modifier(ModifierStat.Hp, ModifierKind.Multiply, Fix.FromInt(3), all);
        Fix a = StatMath.Apply(ModifierStat.Damage, Fix.FromInt(100), UnitSlot.Bruiser, new[] { mul11, add5, tankOnly, hpMul },
            new[] { mul13 });
        Fix b = StatMath.Apply(ModifierStat.Damage, Fix.FromInt(100), UnitSlot.Bruiser, new[] { mul13 },
            new[] { hpMul, tankOnly, add5, mul11 });
        Assert.Equal(a, b);
        Assert.Equal(Fix.FromInt(105) * (Fix.One + Pct("1.1") + Pct("1.3")), a); // (100 + 5) * 1.4

        var half = new Modifier(ModifierStat.Range, ModifierKind.Multiply, Fix.Zero, all);
        Assert.Equal(Fix.Epsilon, StatMath.Apply(ModifierStat.Range, Fix.One, UnitSlot.Tank, new[] { half }));
        var minus = new Modifier(ModifierStat.MoveSpeed, ModifierKind.Add, Fix.FromInt(-5), all);
        Assert.Equal(Fix.Zero, StatMath.Apply(ModifierStat.MoveSpeed, Fix.One, UnitSlot.Tank, new[] { minus }));
        Assert.Equal(Fix.One + Bonus * Fix.FromInt(14), StatMath.LevelFactor(Bonus, 15));
    }

    // ------------------------------------------------------------ passive

    [Fact]
    public void Passive_AppliesOnlyToMatchingSlots_FromTickZero_WithoutTheLeader()
    {
        // Player 1's warlord has no passive.
        Simulation sim = Sim(cards1: Fantasy(null, null));
        Assert.Equal(0, sim.State.Tick);
        Fix boost = Fix.One + Pct("1.1");
        foreach (string id in new[] { "stone_golem", "knight", "goblin_pack", "elf_archer", "griffin", "catapult" })
        {
            Unit mine = Place(sim, 0, id, "9", "8");
            Unit theirs = Place(sim, 1, id, "9", "24");
            UnitDefinition def = mine.Definition;
            bool matches = def.Slot == UnitSlot.Bruiser || def.Slot == UnitSlot.Swarm;
            Assert.Equal(matches ? def.Damage * boost : def.Damage, mine.Damage);
            Assert.Equal(def.Damage, theirs.Damage);
            Assert.Equal(def.Hp, mine.MaxHp);
            Assert.Equal(def.MoveSpeed, mine.MoveSpeed);
        }
        Assert.DoesNotContain(sim.State.Units, u => u.Definition.IsLeader);
        Assert.Equal(Fix.FromInt(120), Place(sim, 0, "warlord", "12", "8").Damage); // the leader's own slot is not listed

        // Deployed through a command, before any leader was ever on the field.
        Simulation deployed = Sim();
        TestSim.Deploy(deployed, 0, "goblin_pack", TestSim.V("9.5", "10.5"));
        TestSim.Run(deployed, 20);
        Assert.Equal(4, deployed.State.Units.Count);
        Assert.All(deployed.State.Units, u => Assert.Equal(Fix.FromInt(60) * boost, u.Damage));

        // It applies in combat: a goblin's hit on a dummy.
        Unit goblin = Place(sim, 0, "goblin_pack", "4", "12");
        Unit dummy = Place(sim, 1, "dummy", "4", "12.4");
        Step(sim);
        Assert.Equal(Fix.FromInt(1000) - Fix.FromInt(60) * boost, dummy.Hp);
        Assert.Equal(Fix.FromInt(60) * boost, goblin.Damage);
    }

    // ------------------------------------------------------------ ability: validity

    [Fact]
    public void Ability_IsIgnored_WhenTheLeaderIsNotOnTheField()
    {
        Simulation sim = Sim();
        PlayerState p0 = sim.State.GetPlayer(0);
        Cast(sim, 0, TestSim.V("9", "10"));
        Assert.Equal(1, p0.IgnoredAbilities);
        Assert.Equal(0, p0.AbilityReadyTick);

        // Deployed but still waiting to spawn: not on the field yet.
        TestSim.Deploy(sim, 0, "warlord", TestSim.V("9.5", "10.5"));
        Assert.Single(sim.State.PendingSpawns);
        Cast(sim, 0, TestSim.V("9.5", "10.5"));
        Assert.Equal(2, p0.IgnoredAbilities);
        TestSim.Run(sim, 18);
        Assert.Empty(sim.State.PendingSpawns);
        Unit leader = Assert.Single(sim.State.Units);
        Assert.Equal(UnitState.Spawning, leader.State);

        // Spawned (even before its first move): usable.
        Cast(sim, 0, leader.Position);
        Assert.Equal(2, p0.IgnoredAbilities);
        Assert.Equal(sim.State.Tick - 1 + 400, p0.AbilityReadyTick);
        Assert.Equal(0, p0.IgnoredDeploys);
    }

    [Fact]
    public void Ability_IsIgnored_WhenTheLeaderIsDead()
    {
        Simulation sim = Sim();
        Unit leader = Place(sim, 0, "warlord", "9", "10");
        leader.Hp = Fix.Zero; // killed: removed at the end of the tick
        Step(sim);
        Assert.Empty(sim.State.Units);
        Cast(sim, 0, TestSim.V("9", "10"));
        Assert.Equal(1, sim.State.GetPlayer(0).IgnoredAbilities);

        // Player 1's leader does not count for player 0.
        Place(sim, 1, "warlord", "9", "10");
        Cast(sim, 0, TestSim.V("9", "10"));
        Assert.Equal(2, sim.State.GetPlayer(0).IgnoredAbilities);
        Assert.Equal(0, sim.State.GetPlayer(1).IgnoredAbilities);
    }

    [Theory]
    [InlineData("3", true)]     // exactly the 6 range
    [InlineData("2.99", false)]
    [InlineData("15", true)]
    [InlineData("15.01", false)]
    public void Ability_RequiresTheTargetWithinRangeOfTheLeader(string x, bool used)
    {
        Simulation sim = Sim();
        Place(sim, 0, "warlord", "9", "10");
        Cast(sim, 0, TestSim.V(x, "10"));
        Assert.Equal(used ? 0 : 1, sim.State.GetPlayer(0).IgnoredAbilities);
        Assert.Equal(used ? 400 : 0, sim.State.GetPlayer(0).AbilityReadyTick);
    }

    [Fact]
    public void Ability_IsIgnoredOnCooldown_AndUsableExactlyWhenItEnds()
    {
        Simulation sim = Sim();
        Unit leader = Place(sim, 0, "warlord", "9", "10");
        PlayerState p0 = sim.State.GetPlayer(0);
        Cast(sim, 0, leader.Position); // tick 0: used
        Assert.Equal(400, p0.AbilityReadyTick);
        Cast(sim, 0, leader.Position); // tick 1
        Assert.Equal(1, p0.IgnoredAbilities);
        TestSim.Run(sim, 397);
        Assert.Equal(399, sim.State.Tick);
        Cast(sim, 0, leader.Position); // tick 399: one tick early
        Assert.Equal(2, p0.IgnoredAbilities);
        Assert.Equal(400, p0.AbilityReadyTick);
        Cast(sim, 0, leader.Position); // tick 400
        Assert.Equal(2, p0.IgnoredAbilities);
        Assert.Equal(800, p0.AbilityReadyTick);
    }

    [Fact]
    public void Ability_IsIgnored_WhenTheLeaderHasNone()
    {
        Simulation sim = Sim(cards0: Fantasy(WarlordPassive, null));
        Place(sim, 0, "warlord", "9", "10");
        Cast(sim, 0, TestSim.V("9", "10"));
        Assert.Equal(1, sim.State.GetPlayer(0).IgnoredAbilities);
    }

    // ------------------------------------------------------------ ability: effects

    [Fact]
    public void Rally_BuffsFriendlyUnitsInRadius_AndExpiresExactlyOnTime()
    {
        Simulation sim = Sim();
        Unit leader = Place(sim, 0, "warlord", "9", "10");
        Unit knight = Place(sim, 0, "knight", "9", "12.5");  // 2.5 from the target
        Unit archer = Place(sim, 0, "elf_archer", "13.1", "10"); // 4.1: outside the radius of 4
        Unit enemy = Place(sim, 1, "dummy", "9", "12.9");   // inside, but an enemy
        Fix knightBase = knight.Damage;
        Fix passive = Pct("1.1"), damage = Pct("1.3"), speed = Pct("1.2");

        int castTick = sim.State.Tick;
        Cast(sim, 0, TestSim.V("9", "10"));
        Fix buffed = Fix.FromInt(140) * (Fix.One + passive + damage);
        Assert.Equal(buffed, knight.Damage);
        Assert.True(Fix.Abs(buffed - Fix.FromInt(196)) < Fix.Parse("0.01"));
        Assert.Equal(Fix.One + speed, knight.MoveSpeed);
        Assert.Equal(Fix.FromInt(120) * (Fix.One + damage), leader.Damage); // the leader is in its own radius
        Assert.Equal(Fix.One + speed, leader.MoveSpeed);
        Assert.Null(archer.Buff);
        Assert.Equal(Fix.FromInt(90), archer.Damage);
        Assert.Null(enemy.Buff);
        Assert.Equal(castTick + 100, knight.Buff!.ExpireTick);
        Assert.Equal(knight.MaxHp, knight.Hp);
        // The buff already counted in the cast tick's combat: the knight's first hit.
        Assert.Equal(Fix.FromInt(1000) - buffed, enemy.Hp);

        TestSim.Run(sim, 98);
        Assert.Equal(castTick + 99, sim.State.Tick);
        Assert.NotNull(knight.Buff);
        Assert.Equal(buffed, knight.Damage);
        Step(sim); // the 100th buffed tick
        Assert.Equal(castTick + 100, sim.State.Tick);
        Assert.Null(knight.Buff);
        Assert.Null(leader.Buff);
        Assert.Equal(knightBase, knight.Damage);
        Assert.Equal(Fix.One, knight.MoveSpeed);
        Assert.Equal(Fix.FromInt(120), leader.Damage);
        Assert.Equal(knight.MaxHp, knight.Hp);
    }

    [Fact]
    public void Rally_RecastRefreshesTheBuff_WithoutStacking()
    {
        string rally = Ability("rally", "\"durationSeconds\": 5, \"modifiers\": "
            + "[{ \"stat\": \"hp\", \"kind\": \"multiply\", \"value\": 2, \"appliesTo\": \"all\" }]", cooldown: "1");
        Simulation sim = Sim(cards0: Fantasy(null, rally));
        Place(sim, 0, "warlord", "9", "10");
        Unit dummy = Place(sim, 0, "dummy", "10", "10"); // a friendly that stays put
        Cast(sim, 0, dummy.Position); // tick 0
        Assert.Equal(Fix.FromInt(2000), dummy.MaxHp);
        Assert.Equal(100, dummy.Buff!.ExpireTick);
        TestSim.Run(sim, 19);
        Cast(sim, 0, dummy.Position); // tick 20
        Assert.Equal(0, sim.State.GetPlayer(0).IgnoredAbilities);
        Assert.Equal(120, dummy.Buff!.ExpireTick);
        Assert.Equal(Fix.FromInt(2000), dummy.MaxHp); // not 4000
        Assert.Equal(Fix.FromInt(2000), dummy.Hp);
        TestSim.Run(sim, 79);
        Assert.Equal(100, sim.State.Tick);
        Assert.NotNull(dummy.Buff);
        TestSim.Run(sim, 20);
        Assert.Null(dummy.Buff);
        Assert.Equal(Fix.FromInt(1000), dummy.MaxHp);
        Assert.Equal(Fix.FromInt(1000), dummy.Hp);
    }

    [Fact]
    public void Heal_RestoresHp_CappedAtMaxHp_FriendliesInRadiusOnly_ScaledByLeaderLevel()
    {
        string heal = Ability("heal", "\"amount\": 200", radius: "3");
        Simulation sim = Sim(cards0: Fantasy(WarlordPassive, heal), levels0: Levels(("warlord", 3)));
        Place(sim, 0, "warlord", "9", "10");
        Unit nearlyFull = Place(sim, 0, "knight", "9", "11");
        Unit hurt = Place(sim, 0, "knight", "10", "10");
        Unit far = Place(sim, 0, "knight", "9", "13.5");
        Unit enemy = Place(sim, 1, "dummy", "8", "10");
        nearlyFull.Hp = nearlyFull.MaxHp - Fix.FromInt(50);
        hurt.Hp = Fix.FromInt(300);
        far.Hp = Fix.FromInt(300);
        enemy.Hp = Fix.FromInt(500);

        Cast(sim, 0, TestSim.V("9", "10"));
        Fix amount = Fix.FromInt(200) * sim.Rules.LevelFactor(3); // about 224
        Assert.Equal(nearlyFull.MaxHp, nearlyFull.Hp);
        Assert.Equal(Fix.FromInt(300) + amount, hurt.Hp);
        Assert.Equal(Fix.FromInt(300), far.Hp);
        Assert.Equal(Fix.FromInt(500), enemy.Hp); // enemies are never healed
        Assert.Null(hurt.Buff);
    }

    [Fact]
    public void AreaDamage_HitsEnemyUnitsAndStructuresInRadius_NotFriends()
    {
        string quake = Ability("areaDamage", "\"damage\": 100", radius: "2");
        Simulation sim = Sim(cards0: Fantasy(null, quake));
        Place(sim, 0, "warlord", "4", "19");
        Unit friend = Place(sim, 0, "knight", "3", "23");
        Unit near = Place(sim, 1, "dummy", "4.5", "22.5");
        Unit far = Place(sim, 1, "dummy", "7", "23.5");
        StructureState tower = sim.State.Structures[Tower1West];
        Fix towerHp = tower.Hp;

        Cast(sim, 0, TestSim.V("4", "23.5"));
        Assert.Equal(Fix.FromInt(900), near.Hp);
        Assert.Equal(Fix.FromInt(1000), far.Hp);
        Assert.Equal(friend.MaxHp, friend.Hp);
        Fix structureDamage = Fix.FromInt(100) * SpellBook.DefaultStructureDamageMultiplier;
        Assert.Equal(towerHp - structureDamage, tower.Hp);
        Assert.Equal(structureDamage, sim.State.GetPlayer(0).Score);
        Assert.Equal(400, sim.State.GetPlayer(0).AbilityReadyTick); // 20 s cooldown
        Assert.Empty(sim.State.AbilityStrikes);
    }

    // ------------------------------------------------------------ determinism

    /// <summary>
    /// Two independently built sims with different levels per player (cards and structures) play the scripted random
    /// match plus a leader deploy and regular ability casts by both players, hashing identically on every tick.
    /// </summary>
    [Theory]
    [InlineData(11UL)]
    [InlineData(12UL)]
    public void LevelsAndAbilities_HashIdenticallyEveryTick(ulong inputSeed)
    {
        MatchRules rules = TestSim.Rules(income: "1", start: "10");
        CommandLog script = DeterminismTests.ScriptedLog(rules, inputSeed, activeTicks: 90 * 20);
        int[] levels0 = Levels(("knight", 5), ("warlord", 4), ("fireball", 2), ("goblin_pack", 9));
        int[] levels1 = Levels(("elf_archer", 7), ("catapult", 3), ("stone_golem", 15));

        Simulation Build() => new Simulation(new MatchSetup(TestSim.Rules(income: "1", start: "10"),
            MapTestData.LoadTwoLane(), TestSim.LoadStructures(),
            Deck.Create(TestSim.LoadFantasyCards(), TestSim.DefaultDeckIds, 8, levels0),
            Deck.Create(TestSim.LoadFantasyCards(), TestSim.DefaultDeckIds, 8, levels1), 2, 4), 77);
        Simulation a = Build(), b = Build();
        Assert.Equal(a.ComputeHash(), b.ComputeHash());

        int buffedTicks = 0;
        while (!a.IsEnded)
        {
            int tick = a.State.Tick;
            var commands = new List<Command>(tick < script.TickCount ? script.GetCommands(tick) : Array.Empty<Command>());
            for (int p = 0; p < 2; p++)
            {
                // Casts every 2.5 s at a point 3 away from the player's first living leader (and sometimes 7 away:
                // out of range), or at the map center when there is none.
                if (tick % 50 != 25)
                {
                    continue;
                }
                Unit? leader = a.State.Units.FirstOrDefault(u => u.Owner == p && u.Definition.IsLeader);
                FixVector2 target = leader == null ? TestSim.V("9", "16")
                    : leader.Position + new FixVector2(Fix.Zero, Fix.FromInt(tick % 150 == 25 ? 7 : 3));
                commands.Add(Command.LeaderAbility(tick, p, 1_000_000 + tick, target));
            }
            a.Tick(commands);
            b.Tick(commands);
            Assert.True(a.ComputeHash() == b.ComputeHash(), "hashes diverged at tick " + a.State.Tick);
            if (a.State.Units.Any(u => u.Buff != null))
            {
                buffedTicks++;
            }
        }
        Assert.True(buffedTicks > 50, "rallies should have landed, buffed ticks " + buffedTicks);
        Assert.All(a.State.Players, p => Assert.True(p.IgnoredAbilities > 0, "some casts should be rejected"));
        Assert.Contains(a.State.Players, p => p.AbilityReadyTick > 0);
        Assert.Equal(a.State.Winner, b.State.Winner);

        Simulation replay = Simulation.Replay(Build().Setup, 77, a.Log);
        Assert.Equal(a.ComputeHash(), replay.ComputeHash());
    }
}
