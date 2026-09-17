using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Movement rules. These run with harmless structures (no damage, practically endless HP) so units
/// walk and stop without dying; combat has its own tests.
/// </summary>
public class MovementTests
{
    // twolane structure indices.
    private const int Tower0East = 2, Keep1 = 3, Tower1West = 4, Tower1East = 5;

    private static readonly FixVector2 WestLaneP0 = TestSim.V("4.5", "10.5");
    private static readonly FixVector2 MapCorner = TestSim.V("18", "32"); // twolane size; rotation = corner - position

    private static Simulation NewSim(ulong seed = 1) =>
        TestSim.New(TestSim.Rules(income: "0", start: "10"), MapTestData.LoadTwoLane(), seed, TestSim.HarmlessStructures());

    /// <summary>Runs until every unit has stopped (Attacking or Holding) or the limit passes, checking walkability each tick. Returns ticks run.</summary>
    internal static int RunUntilAllStopped(Simulation sim, int maxTicks)
    {
        Grid grid = sim.State.Map.Grid;
        for (int t = 1; t <= maxTicks; t++)
        {
            sim.Tick(Array.Empty<Command>());
            foreach (Unit u in sim.State.Units)
            {
                if (!u.IsFlying)
                {
                    Assert.True(grid.IsWalkableAt(u.Position), "unit " + u.DefinitionId + " is on a blocked cell at " + u.Position);
                }
            }
            if (sim.State.Units.Count > 0 && sim.State.Units.All(u => u.State == UnitState.Attacking || u.State == UnitState.Holding))
            {
                return t;
            }
        }
        return -1;
    }

    public static IEnumerable<object[]> GroundCards() =>
        new[] { "stone_golem", "knight", "goblin_pack", "elf_archer", "catapult", "fire_spirit", "warlord" }
            .SelectMany(id => new[] { new object[] { id, 0 }, new object[] { id, 1 } });

    [Theory]
    [MemberData(nameof(GroundCards))]
    public void GroundUnits_ReachTheEnemyForwardTowerAndStop(string cardId, int player)
    {
        Simulation sim = NewSim();
        FixVector2 target = player == 0 ? WestLaneP0 : MapCorner - WestLaneP0;
        int expectedTower = player == 0 ? Tower1West : Tower0East;
        TestSim.Deploy(sim, player, cardId, target);
        UnitDefinition def = sim.State.PendingSpawns[0].Definition;

        // About 16 world units of path from the drop point; allow 20 / speed seconds plus the spawn delay.
        Fix limitSeconds = Fix.FromInt(20) / def.MoveSpeed + Fix.FromInt(1);
        int limitTicks = Fix.CeilToInt(limitSeconds * Fix.FromInt(20));
        int ticks = RunUntilAllStopped(sim, limitTicks);
        Assert.True(ticks > 0, cardId + " did not reach its objective within " + limitTicks + " ticks");

        Assert.Equal(def.SpawnCount, sim.State.Units.Count);
        foreach (Unit u in sim.State.Units)
        {
            Assert.Equal(expectedTower, u.Objective);
            Assert.Equal(TargetRef.Structure(expectedTower), u.Target);
            Assert.Equal(UnitState.Attacking, u.State);
            Fix distance = UnitMovement.DistanceToFootprint(sim.State.Map.Grid, u.Position, expectedTower);
            Assert.True(distance <= def.Range, cardId + " holds out of range: " + distance);
            // Not unreasonably short of range either (it walked, it did not stop early).
            Assert.True(distance > def.Range - Fix.FromInt(2), cardId + " stopped far inside range: " + distance);
        }

        // Stopped means stopped.
        FixVector2[] held = sim.State.Units.Select(u => u.Position).ToArray();
        TestSim.Run(sim, 100);
        Assert.Equal(held, sim.State.Units.Select(u => u.Position).ToArray());
        Assert.All(sim.State.Units, u => Assert.Equal(UnitState.Attacking, u.State));
        // ...and fighting: the harmless tower has lost HP.
        Assert.True(sim.State.Structures[expectedTower].Hp < sim.State.StructureCatalog.ForwardTower.Hp);
    }

    [Fact]
    public void BothPlayers_TakeMirroredRoutesAtTheSameSpeed()
    {
        Simulation sim = NewSim();
        // Start away from the lane center so the route bends.
        FixVector2 start = TestSim.V("7.25", "9.5");
        DeployTests.DeployBoth(sim, "knight", start, MapCorner - start);
        TestSim.Run(sim, 19);
        Unit a = sim.State.Units[0], b = sim.State.Units[1];
        Assert.Equal(MapCorner - a.Position, b.Position);
        int holdA = -1, holdB = -1;
        for (int t = 0; t < 600 && (holdA < 0 || holdB < 0); t++)
        {
            sim.Tick(Array.Empty<Command>());
            FixVector2 mirrored = MapCorner - a.Position;
            // Rounding is symmetric under rotation, so the routes should match to within rounding.
            Assert.True(FixVector2.Distance(mirrored, b.Position) <= Fix.Parse("0.01"),
                "tick " + sim.State.Tick + ": " + a.Position + " vs " + b.Position);
            if (holdA < 0 && a.State == UnitState.Attacking) holdA = t;
            if (holdB < 0 && b.State == UnitState.Attacking) holdB = t;
        }
        Assert.InRange(holdB - holdA, -1, 1);
        Assert.True(holdA > 0);
    }

    [Fact]
    public void GroundPaths_AreSmoothNotJustEightWay()
    {
        Simulation sim = NewSim();
        TestSim.Deploy(sim, 0, "knight", TestSim.V("7.25", "9.5"));
        TestSim.Run(sim, 20);
        Unit u = sim.State.Units[0];
        int offAxis = 0;
        for (int t = 0; t < 300 && u.State != UnitState.Attacking; t++)
        {
            FixVector2 before = u.Position;
            sim.Tick(Array.Empty<Command>());
            FixVector2 step = u.Position - before;
            Fix ax = Fix.Abs(step.X), ay = Fix.Abs(step.Y);
            bool eightWay = ax == Fix.Zero || ay == Fix.Zero || Fix.Abs(ax - ay) <= Fix.FromRaw(2);
            if (!eightWay)
            {
                offAxis++;
            }
            // Never faster than moveSpeed plus the separation push allows (alone, there is no push).
            Assert.True(step.Length <= u.Definition.MoveSpeed / Fix.FromInt(20) + Fix.FromRaw(4));
        }
        Assert.True(offAxis > 10, "only " + offAxis + " blended steps");
    }

    [Fact]
    public void LoneUnit_InOpenField_MovesAtItsSpeed()
    {
        Simulation sim = NewSim();
        TestSim.Deploy(sim, 0, "knight", TestSim.V("9", "6.5"));
        TestSim.Run(sim, 19);
        Unit u = sim.State.Units[0];
        FixVector2 before = u.Position;
        sim.Tick(Array.Empty<Command>());
        Fix stepLength = (u.Position - before).Length;
        Fix expected = u.Definition.MoveSpeed / Fix.FromInt(20);
        Assert.True(Fix.Abs(stepLength - expected) <= Fix.FromRaw(4), stepLength + " vs " + expected);
    }

    [Fact]
    public void FlyingUnits_CrossBlockedTerrainInAStraightLine()
    {
        Simulation sim = NewSim();
        FixVector2 start = TestSim.V("9.5", "10.5"); // below the middle of the river
        TestSim.Deploy(sim, 0, "griffin", start);
        TestSim.Run(sim, 19);
        Unit griffin = Assert.Single(sim.State.Units);
        Assert.True(griffin.IsFlying);
        Grid grid = sim.State.Map.Grid;

        bool crossedBlocked = false;
        int ticks = 0;
        FixVector2? firstStep = null;
        while (griffin.State != UnitState.Attacking)
        {
            Assert.True(++ticks < 400, "griffin never arrived");
            FixVector2 before = griffin.Position;
            sim.Tick(Array.Empty<Command>());
            FixVector2 step = griffin.Position - before;
            firstStep ??= step;
            if (step != FixVector2.Zero)
            {
                // Straight line: every step points the same way (within rounding).
                Fix cross = firstStep.Value.X * step.Y - firstStep.Value.Y * step.X;
                Assert.True(Fix.Abs(cross) <= Fix.FromRaw(64), "griffin turned at " + griffin.Position);
            }
            crossedBlocked |= !grid.IsWalkableAt(griffin.Position);
        }
        Assert.True(crossedBlocked, "griffin should have flown over the river");
        Assert.Equal(Tower1East, griffin.Objective); // nearest by straight line from the middle
        Assert.True(UnitMovement.DistanceToFootprint(grid, griffin.Position, Tower1East) <= griffin.Definition.Range);
        // A ground unit from the same spot would need a lane: 16 cells of straight flight take far less time.
        Assert.True(ticks < Fix.CeilToInt(Fix.FromInt(16) / griffin.Definition.MoveSpeed * Fix.FromInt(20)));
    }

    [Fact]
    public void Separation_KeepsTwoUnitsFromSharingAPosition()
    {
        Simulation sim = NewSim();
        PlayerState p0 = sim.State.GetPlayer(0);
        int slot = TestSim.EnsureInHand(p0, "knight");
        FixVector2 spot = TestSim.V("9", "6.5");
        sim.Tick(new[] { Command.DeployCard(0, 0, 0, slot, spot) });
        // Put the knight back into the same slot so it can be deployed again on the same spot.
        while (p0.Cards.Hand[slot]!.Id != "knight")
        {
            p0.Cards.Play(slot);
        }
        sim.Tick(new[] { Command.DeployCard(1, 0, 0, slot, spot) });
        TestSim.Run(sim, 19); // both spawned now: the first one a tick earlier

        Assert.Equal(2, sim.State.Units.Count);
        Unit a = sim.State.Units[0], b = sim.State.Units[1];
        Fix range = sim.Rules.UnitSeparationDistance;

        for (int t = 0; t < 600; t++)
        {
            sim.Tick(Array.Empty<Command>());
            Assert.NotEqual(a.Position, b.Position);
            if (t >= 40 && a.State == UnitState.Moving && b.State == UnitState.Moving)
            {
                // After two seconds they walk side by side, at least half the separation distance apart.
                Assert.True(FixVector2.Distance(a.Position, b.Position) >= range / Fix.FromInt(2),
                    "too close at tick " + sim.State.Tick + ": " + FixVector2.Distance(a.Position, b.Position));
            }
        }
        Assert.Equal(UnitState.Attacking, a.State);
        Assert.Equal(UnitState.Attacking, b.State);
        // The second knight may squeeze in closer to the one already fighting, but never onto the same spot.
        Assert.True(FixVector2.Distance(a.Position, b.Position) >= range / Fix.FromInt(4), "final " + a.Position + " " + b.Position);
    }

    [Fact]
    public void Separation_ExactOverlapIsSplitByEntityId()
    {
        Simulation sim = NewSim();
        UnitDefinition knight = sim.State.GetPlayer(0).Deck.Roster.Get("knight");
        FixVector2 spot = TestSim.V("9", "6.5");
        Unit first = sim.State.AddUnit(0, knight, spot);
        Unit second = sim.State.AddUnit(0, knight, spot);
        sim.Tick(Array.Empty<Command>());
        Assert.True(first.Position.X < second.Position.X);
        Assert.Equal(first.Position.Y, second.Position.Y);
    }

    [Fact]
    public void Separation_IgnoresEnemiesAndOtherLayers()
    {
        MatchRules rules = TestSim.Rules();
        Simulation sim = NewSim();
        UnitDefinition knight = sim.State.GetPlayer(0).Deck.Roster.Get("knight");
        UnitDefinition griffin = sim.State.GetPlayer(0).Deck.Roster.Get("griffin");
        FixVector2 spot = TestSim.V("9", "6.5");
        var units = new List<Unit>
        {
            sim.State.AddUnit(0, knight, spot),
            sim.State.AddUnit(1, knight, spot),
            sim.State.AddUnit(0, griffin, spot),
        };
        var positions = units.Select(u => u.Position).ToArray();
        var stopped = new bool[units.Count + 1];
        Assert.Equal(FixVector2.Zero, UnitMovement.SeparationPush(units, positions, stopped, 0, rules));
        units.Add(sim.State.AddUnit(0, knight, spot + TestSim.V("0.3", "0")));
        positions = units.Select(u => u.Position).ToArray();
        FixVector2 push = UnitMovement.SeparationPush(units, positions, stopped, 0, rules);
        // Half overlap -> half strength, pointing away (-X).
        Assert.Equal(-rules.UnitSeparationPushPerSecond / Fix.FromInt(2), push.X);
        Assert.Equal(Fix.Zero, push.Y);
    }

    [Fact]
    public void Separation_StoppedNeighborsPushWeakly()
    {
        MatchRules rules = TestSim.Rules(stoppedPush: "0.3");
        Simulation sim = NewSim();
        UnitDefinition knight = sim.State.GetPlayer(0).Deck.Roster.Get("knight");
        FixVector2 spot = TestSim.V("9", "6.5");
        var units = new List<Unit>
        {
            sim.State.AddUnit(0, knight, spot),
            sim.State.AddUnit(0, knight, spot + TestSim.V("0.3", "0")),
            sim.State.AddUnit(0, knight, spot + TestSim.V("0.3", "0")),
            sim.State.AddUnit(0, knight, spot + TestSim.V("0.3", "0")),
        };
        FixVector2[] positions = units.Select(u => u.Position).ToArray();
        // Three stopped friends overlapping by half: their sum is capped at full strength, then weakened.
        FixVector2 push = UnitMovement.SeparationPush(units, positions, new[] { false, true, true, true }, 0, rules);
        Assert.Equal(-rules.UnitSeparationPushPerSecond * Fix.Parse("0.3"), push.X);
        // The weakened push is slower than every unit, so a moving unit always gains ground.
        Assert.All(TestSim.LoadFantasy().Units, u => Assert.True(u.MoveSpeed == Fix.Zero || -push.X < u.MoveSpeed));
        // Moving neighbors still push at full strength (capped).
        FixVector2 full = UnitMovement.SeparationPush(units, positions, new bool[4], 0, rules);
        Assert.Equal(-rules.UnitSeparationPushPerSecond, full.X);
    }

    [Fact]
    public void AttackingUnits_ReselectWhenTheirObjectiveIsDestroyed()
    {
        Simulation sim = NewSim();
        TestSim.Deploy(sim, 0, "knight", WestLaneP0);
        Assert.True(RunUntilAllStopped(sim, 1000) > 0);
        Unit knight = sim.State.Units[0];
        Assert.Equal(Tower1West, knight.Objective);
        FixVector2 held = knight.Position;

        sim.State.Map.DestroyStructure(Tower1West);
        sim.Tick(Array.Empty<Command>());

        Assert.Equal(UnitState.Moving, knight.State);
        Assert.Equal(Keep1, knight.Objective); // nearer than the far tower from the west lane
        Assert.NotEqual(held, knight.Position);

        Assert.True(RunUntilAllStopped(sim, 1000) > 0);
        Assert.Equal(Keep1, knight.Objective);
        Assert.True(UnitMovement.DistanceToFootprint(sim.State.Map.Grid, knight.Position, Keep1) <= knight.Definition.Range);
    }

    [Fact]
    public void MovingUnits_SwitchObjectiveWhenTheNearestStructureChanges()
    {
        Simulation sim = NewSim();
        TestSim.Deploy(sim, 0, "knight", WestLaneP0);
        TestSim.Run(sim, 40);
        Unit knight = sim.State.Units[0];
        Assert.Equal(UnitState.Moving, knight.State);
        Assert.Equal(Tower1West, knight.Objective);
        sim.State.Map.DestroyStructure(Tower1West);
        sim.Tick(Array.Empty<Command>());
        // Through the east gap the far tower is a shorter walk than the Keep from here.
        FlowFieldCache fields = sim.State.Map.FlowFields;
        Assert.True(fields.Get(Tower1East).GetDistance(knight.Position) < fields.Get(Keep1).GetDistance(knight.Position));
        Assert.Equal(Tower1East, knight.Objective);
    }

    [Fact]
    public void WithNoEnemyStructuresLeft_UnitsHoldWithoutObjective()
    {
        Simulation sim = NewSim();
        TestSim.Deploy(sim, 0, "knight", WestLaneP0);
        TestSim.Deploy(sim, 0, "griffin", WestLaneP0, sequence: 1);
        foreach (int s in new[] { Keep1, Tower1West, Tower1East })
        {
            sim.State.Map.DestroyStructure(s);
        }
        TestSim.Run(sim, 25);
        Assert.All(sim.State.Units, u =>
        {
            Assert.Equal(UnitState.Holding, u.State);
            Assert.Equal(Unit.NoObjective, u.Objective);
        });
    }

    [Fact]
    public void Objective_IsNearestByPathForGroundAndByLineForAir()
    {
        Simulation sim = NewSim();
        UnitDefinition knight = sim.State.GetPlayer(0).Deck.Roster.Get("knight");
        UnitDefinition griffin = sim.State.GetPlayer(0).Deck.Roster.Get("griffin");
        // Just below the river, right of center: the straight line favors the east tower, but a ground
        // unit at x 6.5 is next to the west gap (cells 2-5) and the east gap is further to walk.
        FixVector2 spot = TestSim.V("6.5", "12.5");
        Unit ground = sim.State.AddUnit(0, knight, spot);
        Unit air = sim.State.AddUnit(0, griffin, TestSim.V("9.5", "12.5"));
        Assert.Equal(Tower1West, UnitMovement.SelectObjective(sim.State.Map, ground, spot));
        Assert.Equal(Tower1East, UnitMovement.SelectObjective(sim.State.Map, air, air.Position));
        // Player 1 units go for player 0's structures.
        Unit enemy = sim.State.AddUnit(1, knight, MapCorner - spot);
        Assert.Equal(Tower0East, UnitMovement.SelectObjective(sim.State.Map, enemy, enemy.Position));
    }

    [Fact]
    public void DistanceToFootprint_MeasuresToTheNearestEdge()
    {
        var grid = new Grid(MapTestData.LoadTwoLane());
        // tower_1_west covers x 3-5, y 24-26 in world units.
        Assert.Equal(Fix.Zero, UnitMovement.DistanceToFootprint(grid, TestSim.V("4", "25"), Tower1West));
        Assert.Equal(Fix.FromInt(2), UnitMovement.DistanceToFootprint(grid, TestSim.V("4", "22"), Tower1West));
        Assert.Equal(Fix.FromInt(5), UnitMovement.DistanceToFootprint(grid, TestSim.V("9", "29"), Tower1West));
        Assert.Equal(Fix.One, UnitMovement.DistanceToFootprint(grid, TestSim.V("2", "25.5"), Tower1West));
    }

    [Fact]
    public void Units_ReadOnlyViewExposesState()
    {
        Simulation sim = NewSim();
        TestSim.Deploy(sim, 1, "goblin_pack", MapCorner - WestLaneP0);
        TestSim.Run(sim, 30);
        IReadOnlyList<Unit> units = sim.State.Units;
        Assert.Equal(4, units.Count);
        Assert.All(units, u =>
        {
            Assert.Equal(1, u.Owner);
            Assert.Equal("goblin_pack", u.DefinitionId);
            Assert.Equal(UnitState.Moving, u.State);
            Assert.Equal(Tower0East, u.Objective);
            Assert.Equal(u.Definition.Hp, u.Hp);
        });
        Assert.False(typeof(MatchState).GetProperty(nameof(MatchState.Units))!.CanWrite);
        Assert.False(typeof(Unit).GetProperty(nameof(Unit.Position))!.SetMethod!.IsPublic);
    }
}
