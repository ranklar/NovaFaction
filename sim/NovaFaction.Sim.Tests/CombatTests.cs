using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Targeting, attacks, projectiles, splash, structure combat and destruction. Units are placed directly on
/// the twolane map (outside the command path) so each test controls exactly who fights whom.
/// </summary>
public class CombatTests
{
    // twolane structure indices and footprints (cells = world units).
    internal const int Keep0 = 0, Tower0West = 1, Tower0East = 2, Keep1 = 3, Tower1West = 4, Tower1East = 5;

    /// <summary>A test-only roster: stationary "dummy" targets (hp as named), plus a flying one.</summary>
    internal static readonly UnitRoster Dummies = UnitRoster.FromJson(@"{
  ""formatVersion"": 1, ""faction"": ""dummies"",
  ""units"": [
    { ""id"": ""dummy"", ""displayName"": ""Dummy"", ""slot"": ""tank"", ""cost"": 1, ""hp"": 1000, ""damage"": 0,
      ""attackIntervalSeconds"": 1, ""range"": 0.5, ""moveSpeed"": 0, ""targets"": ""ground"", ""targetPriority"": ""any"",
      ""isFlying"": false, ""spawnCount"": 1, ""isLeader"": false },
    { ""id"": ""fragile"", ""displayName"": ""Fragile"", ""slot"": ""tank"", ""cost"": 1, ""hp"": 50, ""damage"": 0,
      ""attackIntervalSeconds"": 1, ""range"": 0.5, ""moveSpeed"": 0, ""targets"": ""ground"", ""targetPriority"": ""any"",
      ""isFlying"": false, ""spawnCount"": 1, ""isLeader"": false },
    { ""id"": ""balloon"", ""displayName"": ""Balloon"", ""slot"": ""flyer"", ""cost"": 1, ""hp"": 1000, ""damage"": 0,
      ""attackIntervalSeconds"": 1, ""range"": 0.5, ""moveSpeed"": 0, ""targets"": ""air"", ""targetPriority"": ""any"",
      ""isFlying"": true, ""spawnCount"": 1, ""isLeader"": false }
  ]
}");

    /// <summary>Structures that do nothing: no damage, a tiny range, practically endless HP.</summary>
    internal static StructureCatalog Quiet() =>
        TestSim.Structures(hp: "1000000", keepHp: "1000000", damage: "0", range: "0.1");

    internal static Simulation NewSim(StructureCatalog? structures = null, MatchRules? rules = null) =>
        TestSim.New(rules ?? TestSim.Rules(income: "0", start: "10"), MapTestData.LoadTwoLane(), 1, structures ?? Quiet());

    internal static UnitDefinition Card(Simulation sim, string id) =>
        Dummies.TryGet(id, out UnitDefinition dummy) ? dummy : sim.State.GetPlayer(0).Deck.Roster.Get(id);

    internal static Unit Place(Simulation sim, int owner, string id, string x, string y) =>
        sim.State.AddUnit(owner, Card(sim, id), TestSim.V(x, y));

    private static void Step(Simulation sim) => sim.Tick(Array.Empty<Command>());

    // ------------------------------------------------------------ melee

    [Fact]
    public void MeleeUnit_KillsAUnit_WithInstantHitsOnItsInterval()
    {
        Simulation sim = NewSim();
        Unit knight = Place(sim, 0, "knight", "9", "11");
        Unit goblin = Place(sim, 1, "goblin_pack", "9", "11.4");
        Fix knightHit = knight.Definition.Damage, goblinHit = goblin.Definition.Damage;
        int knightInterval = TargetRules.IntervalTicks(knight.Definition.AttackIntervalSeconds, 20); // 1.2 s = 24
        Assert.Equal(24, knightInterval);

        Step(sim); // tick 0: both in range, both hit at once; melee damage needs no projectile
        Assert.Equal(TargetRef.Unit(goblin.Id), knight.Target);
        Assert.Equal(TargetRef.Unit(knight.Id), goblin.Target);
        Assert.Equal(UnitState.Attacking, knight.State);
        Assert.Equal(goblin.Definition.Hp - knightHit, goblin.Hp);
        Assert.Equal(knight.Definition.Hp - goblinHit, knight.Hp);
        Assert.Empty(sim.State.Projectiles);
        Assert.Equal(knightInterval, knight.AttackCooldownTicks);

        // The goblin (160 HP) survives the first 140-damage hit and dies to the second, on tick 24.
        for (int t = 1; t < knightInterval; t++)
        {
            Step(sim);
            Assert.NotNull(sim.State.FindUnit(goblin.Id));
            Assert.Equal(TestSim.V("9", "11"), knight.Position); // attackers stand still
            Assert.Equal(TestSim.V("9", "11.4"), goblin.Position);
        }
        Assert.Equal(knight.Definition.Hp - goblinHit * Fix.FromInt(2), knight.Hp); // goblin hit again on tick 20
        Step(sim);
        Assert.Null(sim.State.FindUnit(goblin.Id));
        Assert.Equal(Fix.Zero, goblin.Hp);
        Assert.Single(sim.State.Units);
        Assert.Equal(TargetRef.None, knight.Target); // cleared when the target died

        // Next tick it goes back to its objective.
        Step(sim);
        Assert.Equal(UnitState.Moving, knight.State);
        Assert.Equal(TargetRef.Structure(knight.Objective), knight.Target);
    }

    [Fact]
    public void Units_OutOfRangeButWithinAggro_ChaseTheirTarget()
    {
        Simulation sim = NewSim();
        Unit knight = Place(sim, 0, "knight", "9", "10");
        Place(sim, 1, "balloon", "9", "13"); // flying: never a target for a ground-only knight
        Place(sim, 1, "dummy", "9", "14.5"); // standing on the river: a ground melee unit cannot get there
        Step(sim);
        Assert.Equal(TargetKind.Structure, knight.Target.Kind);
        Unit dummy = Place(sim, 1, "dummy", "12", "12"); // 3.6 away: inside the 5.5 aggro radius
        Step(sim);
        Assert.Equal(TargetRef.Unit(dummy.Id), knight.Target);
        Assert.Equal(UnitState.Moving, knight.State);
        for (int t = 0; t < 100 && knight.State != UnitState.Attacking; t++)
        {
            Step(sim);
        }
        Assert.Equal(UnitState.Attacking, knight.State);
        Assert.True(FixVector2.Distance(knight.Position, dummy.Position) <= knight.Definition.Range,
            knight.Position + " " + knight.Target + " dummy " + dummy.Id + " tick " + sim.State.Tick);
        Assert.Equal(TargetRef.Unit(dummy.Id), knight.Target);
        Step(sim);
        Assert.True(dummy.Hp < dummy.Definition.Hp);
    }

    [Fact]
    public void Units_BeyondAggro_IgnoreEnemiesAndWalkToTheirObjective()
    {
        Simulation sim = NewSim();
        Unit knight = Place(sim, 0, "knight", "3", "10");
        Place(sim, 1, "dummy", "15", "10"); // 12 away
        Step(sim);
        Assert.Equal(TargetRef.Structure(Tower1West), knight.Target);
        Assert.Equal(Tower1West, knight.Objective);
    }

    [Fact]
    public void StructuresOnlyUnits_IgnoreEnemyUnits()
    {
        Simulation sim = NewSim();
        Unit golem = Place(sim, 0, "stone_golem", "9", "11");
        Place(sim, 1, "dummy", "9", "11.3");
        TestSim.Run(sim, 40);
        Assert.Equal(TargetKind.Structure, golem.Target.Kind);
        Assert.Equal(UnitState.Moving, golem.State);
        Assert.All(sim.State.Units, u => Assert.Equal(u.Definition.Hp, u.Hp));
    }

    [Fact]
    public void MeleeUnits_SpreadOverSeveralEnemies()
    {
        // Three knights next to three dummies that are almost equally close: with the crowd penalty each knight
        // takes a different dummy; without it they all pile onto the nearest.
        foreach ((string penalty, int expectedTargets) in new[] { ("1", 3), ("0", 1) })
        {
            Simulation sim = NewSim(rules: TestSim.Rules(income: "0", crowdPenalty: penalty));
            Unit[] knights =
            {
                Place(sim, 0, "knight", "8.5", "10"),
                Place(sim, 0, "knight", "9", "10"),
                Place(sim, 0, "knight", "9.5", "10"),
            };
            Place(sim, 1, "dummy", "9", "11");
            Place(sim, 1, "dummy", "8.2", "11.1");
            Place(sim, 1, "dummy", "9.8", "11.1");
            Step(sim);
            Assert.All(knights, k => Assert.Equal(TargetKind.Unit, k.Target.Kind));
            Assert.Equal(expectedTargets, knights.Select(k => k.Target).Distinct().Count());
        }
    }

    // ------------------------------------------------------------ projectiles

    [Fact]
    public void RangedAttack_FiresAProjectileThatHitsAfterItsTravelTime()
    {
        Simulation sim = NewSim();
        Unit archer = Place(sim, 0, "elf_archer", "13", "9");
        Unit dummy = Place(sim, 1, "dummy", "13", "13"); // 4 away, archer range 5
        Step(sim); // tick 0: fire
        Projectile shot = Assert.Single(sim.State.Projectiles);
        Assert.Equal(1, shot.Id);
        Assert.Equal(0, shot.Owner);
        Assert.Equal(archer.Position, shot.Position); // fired this tick, moves from the next
        Assert.Equal(TargetRef.Unit(dummy.Id), shot.Target);
        Assert.Equal(archer.Definition.Damage, shot.Damage);
        Assert.Equal(dummy.Definition.Hp, dummy.Hp);
        Assert.Equal(2, sim.State.NextProjectileId);

        // 4 units at 10 u/s = 0.5 per tick: arrives on the 8th tick after firing.
        Fix perTick = archer.Definition.ProjectileSpeed / Fix.FromInt(20);
        int travelTicks = Fix.CeilToInt(Fix.FromInt(4) / perTick);
        Assert.Equal(8, travelTicks);
        for (int t = 1; t < travelTicks; t++)
        {
            Step(sim);
            Assert.Equal(TestSim.V("13", "9") + TestSim.V("0", "0.5") * Fix.FromInt(t), shot.Position);
            Assert.Equal(dummy.Definition.Hp, dummy.Hp);
        }
        Step(sim);
        Assert.Empty(sim.State.Projectiles);
        Assert.Equal(dummy.Definition.Hp - archer.Definition.Damage, dummy.Hp);
    }

    [Fact]
    public void Projectiles_HomeOnAMovingTarget()
    {
        Simulation sim = NewSim();
        Place(sim, 0, "dummy", "13", "9");
        Unit archer = Place(sim, 0, "elf_archer", "13", "8.5");
        Unit knight = Place(sim, 1, "knight", "13", "12.5"); // walks toward the dummy/archer
        Step(sim);
        Projectile shot = Assert.Single(sim.State.Projectiles);
        TestSim.Run(sim, 12);
        Assert.DoesNotContain(shot, sim.State.Projectiles);
        Assert.Equal(knight.Definition.Hp - archer.Definition.Damage, knight.Hp);
        Assert.NotEqual(TestSim.V("13", "12.5"), knight.Position);
    }

    [Fact]
    public void Projectile_FizzlesWhenItsTargetIsAlreadyDead()
    {
        Simulation sim = NewSim();
        Place(sim, 0, "elf_archer", "13", "9");
        Unit knight = Place(sim, 0, "knight", "13", "12.6");
        Unit fragile = Place(sim, 1, "fragile", "13", "13");
        // Right next to where the shot lands; flying, so only the archer could hurt it.
        Unit bystander = Place(sim, 1, "balloon", "13.2", "13.2");
        Step(sim); // the archer fires and the knight kills the target on the same tick
        Projectile shot = Assert.Single(sim.State.Projectiles);
        Assert.Null(sim.State.FindUnit(fragile.Id));

        // The shot keeps flying to the last known position (4 units: 8 ticks), then vanishes without damage.
        for (int t = 1; t < 8; t++)
        {
            Step(sim);
            Assert.Contains(shot, sim.State.Projectiles);
        }
        Assert.Equal(TestSim.V("13", "13"), shot.AimPoint);
        Step(sim);
        Assert.DoesNotContain(shot, sim.State.Projectiles);
        Assert.Equal(TestSim.V("13", "13"), shot.Position);
        Assert.Equal(bystander.Definition.Hp, bystander.Hp);
        Assert.NotEqual(TargetRef.Unit(bystander.Id), knight.Target);
    }

    // ------------------------------------------------------------ air and ground

    [Fact]
    public void Flyers_AreImmuneToGroundOnlyAttackers()
    {
        // Ground-only structures too: the tower next to the fight never shoots the griffin.
        Simulation sim = NewSim(TestSim.Structures(hp: "1000000", keepHp: "1000000", damage: "50", range: "7",
            targets: "ground"));
        Unit knight = Place(sim, 0, "knight", "9", "11");
        Unit griffin = Place(sim, 1, "griffin", "9", "11.3");
        for (int t = 0; t < 60; t++)
        {
            Step(sim);
            Assert.NotEqual(TargetRef.Unit(griffin.Id), knight.Target);
            Assert.All(sim.State.Structures, s => Assert.NotEqual(griffin.Id, s.TargetUnitId));
        }
        Assert.Equal(griffin.Definition.Hp, griffin.Hp);
        Assert.True(knight.Hp < knight.Definition.Hp, "the griffin (targets both) hits the knight");
        Assert.Equal(TargetRef.Unit(knight.Id), griffin.Target);
    }

    [Fact]
    public void AttackersThatTargetAir_HitFlyers_AndAirOnlyAttackersIgnoreGround()
    {
        Simulation sim = NewSim();
        Unit archer = Place(sim, 0, "elf_archer", "9", "10");
        Unit balloon = Place(sim, 1, "balloon", "9", "12"); // flying, air-only attacker
        Unit dummy = Place(sim, 0, "dummy", "9", "12.2");
        TestSim.Run(sim, 20);
        Assert.True(balloon.Hp < balloon.Definition.Hp);
        Assert.NotEqual(TargetRef.Unit(dummy.Id), balloon.Target);
        Assert.NotEqual(TargetRef.Unit(archer.Id), balloon.Target);
        Assert.True(TargetRules.CanHitUnit(TargetLayer.Both, true));
        Assert.False(TargetRules.CanHitUnit(TargetLayer.Ground, true));
        Assert.False(TargetRules.CanHitUnit(TargetLayer.Air, false));
        Assert.False(TargetRules.CanHitStructures(TargetLayer.Air));
    }

    // ------------------------------------------------------------ splash

    [Fact]
    public void RangedSplash_DamagesEveryEnemyNearTheImpact_ButNotFriends()
    {
        Simulation sim = NewSim();
        Unit catapult = Place(sim, 0, "catapult", "4", "18.5"); // 5.5 below tower_1_west (y 24)
        Unit near = Place(sim, 1, "dummy", "4", "23.5");
        Unit nearToo = Place(sim, 1, "dummy", "3.5", "23.4");
        Unit far = Place(sim, 1, "dummy", "4", "21.5");
        Unit friend = Place(sim, 0, "dummy", "4.5", "23.5");
        Fix damage = catapult.Definition.Damage;

        Step(sim);
        Projectile shot = Assert.Single(sim.State.Projectiles);
        Assert.Equal(TargetRef.Structure(Tower1West), shot.Target); // structures only
        Assert.Equal(TestSim.V("4", "24"), shot.AimPoint); // nearest point of the footprint
        Assert.Equal(catapult.Definition.SplashRadius, shot.SplashRadius);

        // 5.5 units at 6 u/s (0.3 per tick): 19 ticks.
        TestSim.Run(sim, 18);
        Assert.Single(sim.State.Projectiles);
        Step(sim);
        Assert.Empty(sim.State.Projectiles);
        Assert.Equal(near.Definition.Hp - damage, near.Hp);
        Assert.Equal(nearToo.Definition.Hp - damage, nearToo.Hp);
        Assert.Equal(far.Definition.Hp, far.Hp);
        Assert.Equal(friend.Definition.Hp, friend.Hp);
        Assert.Equal(Fix.FromInt(1000000) - damage, sim.State.Structures[Tower1West].Hp);
        Assert.Equal(damage, sim.State.GetPlayer(0).Score);
    }

    [Fact]
    public void MeleeSplash_HitsSeveralEnemiesAtOnce()
    {
        Simulation sim = NewSim();
        Unit spirit = Place(sim, 0, "fire_spirit", "9", "11");
        Unit a = Place(sim, 1, "dummy", "9", "11.4");
        Unit b = Place(sim, 1, "dummy", "10", "11.9"); // 1.12 from the impact at a: inside the 1.5 splash
        Unit c = Place(sim, 1, "dummy", "9", "13.5");
        Step(sim);
        Assert.Equal(TargetRef.Unit(a.Id), spirit.Target);
        Fix damage = spirit.Definition.Damage;
        Assert.Equal(a.Definition.Hp - damage, a.Hp);
        Assert.Equal(b.Definition.Hp - damage, b.Hp);
        Assert.Equal(c.Definition.Hp, c.Hp);
    }

    // ------------------------------------------------------------ structures

    [Fact]
    public void Towers_ShootTheNearestEnemyInRange_AndKillApproachingUnits()
    {
        Simulation sim = NewSim(TestSim.Structures(hp: "1000000", keepHp: "1000000", damage: "100", interval: "0.5",
            range: "7"));
        // Player 1 goblins walk down the west lane toward tower_0_west.
        Unit[] goblins =
        {
            Place(sim, 1, "goblin_pack", "4", "20"),
            Place(sim, 1, "goblin_pack", "4.5", "20.5"),
            Place(sim, 1, "goblin_pack", "3.5", "21"),
        };
        StructureState tower = sim.State.Structures[Tower0West];
        int firstShotTick = -1;
        for (int t = 0; t < 400 && sim.State.Units.Count > 0; t++)
        {
            Step(sim);
            if (firstShotTick < 0 && tower.TargetUnitId != StructureState.NoTarget)
            {
                firstShotTick = t;
                // The nearest goblin to the footprint is the first target.
                Unit target = sim.State.FindUnit(tower.TargetUnitId)!;
                Fix d = UnitMovement.DistanceToFootprint(sim.State.Map.Grid, target.Position, Tower0West);
                Assert.True(d <= Fix.FromInt(7));
                Assert.Contains(sim.State.Projectiles, p => p.Owner == 0 && p.Target == TargetRef.Unit(target.Id));
            }
        }
        Assert.True(firstShotTick >= 0);
        Assert.Empty(sim.State.Units);
        Assert.All(goblins, g => Assert.Equal(Fix.Zero, g.Hp));
        Assert.Equal(StructureState.NoTarget, tower.TargetUnitId);
        Assert.True(sim.State.Structures[Tower0West].Hp > Fix.Zero);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(0).Score); // killing units scores nothing
    }

    [Fact]
    public void Structure_KeepsItsTargetWhileInRange()
    {
        Simulation sim = NewSim(TestSim.Structures(hp: "1000000", keepHp: "1000000", damage: "1", range: "7"));
        Place(sim, 1, "dummy", "4.5", "14"); // 6 from tower_0_west
        Unit first = Place(sim, 1, "dummy", "4", "13"); // 5 away: the nearest
        Step(sim);
        StructureState tower = sim.State.Structures[Tower0West];
        Assert.Equal(first.Id, tower.TargetUnitId);
        Unit closer = Place(sim, 1, "dummy", "2.5", "10"); // 2 away (and out of the Keep's reach), arrives later
        TestSim.Run(sim, 30);
        Assert.Equal(first.Id, tower.TargetUnitId);
        Assert.Equal(closer.Definition.Hp, closer.Hp);
    }

    [Fact]
    public void UnitsDestroyAForwardTower_ScoreHpPlusBonus_OpenTheFootprint_UnlockTheZone_AndRetarget()
    {
        Simulation sim = NewSim(TestSim.Structures(hp: "500", bonus: "300", damage: "0", range: "0.1",
            keepHp: "1000000"));
        MapState map = sim.State.Map;
        CellRect footprint = map.Definition.Structures[Tower1West].Footprint;
        CellRect unlock = map.Definition.Structures[Tower1West].UnlocksDeployZone!.Value;
        FixVector2 insideUnlock = TestSim.V("1.5", "20.5");
        Assert.False(map.IsDeployable(0, insideUnlock));
        Assert.False(map.Grid.IsWalkable(footprint.X, footprint.Y));
        int version = map.Grid.WalkabilityVersion;
        FixVector2 onFootprint = TestSim.V("3.5", "24.5");
        Assert.Equal(Fix.MaxValue, map.FlowFields.Get(Keep1).GetDistance(onFootprint));

        // Two knights (140 per hit each, every 24 ticks) under the 500 HP tower.
        Unit a = Place(sim, 0, "knight", "3.6", "23.5");
        Unit b = Place(sim, 0, "knight", "4.4", "23.5");
        Step(sim);
        StructureState tower = sim.State.Structures[Tower1West];
        Assert.Equal(Fix.FromInt(500 - 280), tower.Hp);
        Assert.Equal(Fix.FromInt(280), sim.State.GetPlayer(0).Score);
        TestSim.Run(sim, 23);
        Assert.False(tower.IsDestroyed);

        Step(sim); // tick 24: 280 more damage, only 220 of it removable
        Assert.True(tower.IsDestroyed);
        Assert.Equal(Fix.Zero, tower.Hp);
        Assert.Equal(Fix.FromInt(500 + 300), sim.State.GetPlayer(0).Score); // HP removed + destruction bonus
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1).Score);
        Assert.True(map.Grid.IsWalkable(footprint.X, footprint.Y));
        Assert.True(map.Grid.WalkabilityVersion > version); // flow fields rebuild on next use
        Assert.Equal(new[] { unlock }, map.GetUnlockedZones(0));
        Assert.Empty(map.GetUnlockedZones(1));
        Assert.True(map.IsDeployable(0, insideUnlock));
        foreach (Unit knight in new[] { a, b })
        {
            Assert.Equal(TargetRef.None, knight.Target);
            Assert.Equal(Keep1, knight.Objective); // re-selected on the same tick
        }

        Step(sim);
        Assert.All(new[] { a, b }, k => Assert.Equal(UnitState.Moving, k.State));
        Assert.All(new[] { a, b }, k => Assert.Equal(TargetRef.Structure(Keep1), k.Target));
        // The flow field was rebuilt: the fallen tower's footprint is part of the paths now.
        Assert.NotEqual(Fix.MaxValue, map.FlowFields.Get(Keep1).GetDistance(onFootprint));
        Assert.NotEqual(MatchPhase.Ended, sim.State.Phase); // a tower does not end the match
    }

    [Fact]
    public void DeployZoneUnlock_LetsTheAttackerDeployThereByCommand()
    {
        Simulation sim = NewSim(TestSim.Structures(hp: "100", damage: "0", range: "0.1", keepHp: "1000000"));
        Place(sim, 0, "knight", "4", "23.5");
        Step(sim);
        Assert.True(sim.State.Map.IsDestroyed(Tower1West));
        TestSim.Deploy(sim, 0, "goblin_pack", TestSim.V("1.5", "20.5"));
        Assert.Equal(0, sim.State.GetPlayer(0).IgnoredDeploys);
        Assert.Single(sim.State.PendingSpawns);
    }
}
