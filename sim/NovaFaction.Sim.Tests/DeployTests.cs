using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim.Tests;

public class DeployTests
{
    // twolane: player 0 deploys in rows 0-12, player 1 in rows 19-31; the river is rows 14-17.
    private static readonly FixVector2 OpenP0 = TestSim.V("4.5", "10.5");
    private static readonly FixVector2 OpenP1 = TestSim.V("13.5", "21.5"); // OpenP0 rotated 180 degrees

    private static Simulation NewSim(string start = "10", string income = "0", string spawnDelay = "1", ulong seed = 1) =>
        TestSim.New(TestSim.Rules(income: income, start: start, spawnDelay: spawnDelay), MapTestData.LoadTwoLane(), seed);

    private static Command DeployCmd(Simulation sim, int player, int slot, FixVector2 target, int sequence = 0) =>
        Command.DeployCard(sim.State.Tick, player, sequence, slot, target);

    /// <summary>Asserts the deploy was ignored: counter up, nothing else changed except the tick itself.</summary>
    private static void AssertRejected(Simulation sim, int player, int slot, FixVector2 target)
    {
        PlayerState p = sim.State.GetPlayer(player);
        Fix goldBefore = p.Gold;
        string?[] handBefore = TestSim.HandIds(p);
        int ignoredBefore = p.IgnoredDeploys;

        sim.Tick(new[] { DeployCmd(sim, player, slot, target) });

        Assert.Equal(ignoredBefore + 1, p.IgnoredDeploys);
        Assert.Equal(goldBefore, p.Gold); // income is 0 in these tests
        Assert.Equal(handBefore, TestSim.HandIds(p));
        Assert.Empty(sim.State.PendingSpawns);
        Assert.Empty(sim.State.Units);
    }

    [Fact]
    public void Rejected_WhenGoldIsBelowCost()
    {
        Simulation sim = NewSim(start: "4.99");
        int slot = TestSim.EnsureInHand(sim.State.GetPlayer(0), "stone_golem"); // costs 5
        AssertRejected(sim, 0, slot, OpenP0);
    }

    [Fact]
    public void Accepted_WhenGoldEqualsCost()
    {
        Simulation sim = NewSim(start: "5");
        int slot = TestSim.EnsureInHand(sim.State.GetPlayer(0), "stone_golem");
        sim.Tick(new[] { DeployCmd(sim, 0, slot, OpenP0) });
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(0).Gold);
        Assert.Equal(0, sim.State.GetPlayer(0).IgnoredDeploys);
        Assert.Single(sim.State.PendingSpawns);
    }

    [Fact]
    public void Rejected_WhenSlotIsEmpty()
    {
        Simulation sim = NewSim();
        sim.State.GetPlayer(0).Cards.ClearSlot(2);
        Assert.Null(sim.State.GetPlayer(0).Cards.Hand[2]);
        AssertRejected(sim, 0, 2, OpenP0);
    }

    public static IEnumerable<object[]> UndeployableTargets()
    {
        yield return new object[] { 0, "9.5", "22.5" };   // enemy half
        yield return new object[] { 0, "8.5", "15.5" };   // river
        yield return new object[] { 0, "4.5", "13.5" };   // neutral row 13
        yield return new object[] { 0, "4.5", "13" };     // exactly on the zone's top edge (belongs to row 13)
        yield return new object[] { 0, "0.5", "0.5" };    // '#' terrain inside the base zone
        yield return new object[] { 0, "8.5", "2.5" };    // on own Keep
        yield return new object[] { 0, "3.5", "6.5" };    // on own tower
        yield return new object[] { 0, "-0.5", "5.5" };  // off the map
        yield return new object[] { 0, "18", "5.5" };     // just past the right edge
        yield return new object[] { 1, "4.5", "10.5" };   // player 1 in player 0's half
        yield return new object[] { 1, "4.5", "25.5" };   // on player 1's tower
    }

    [Theory]
    [MemberData(nameof(UndeployableTargets))]
    public void Rejected_WhenPositionIsNotDeployable(int player, string x, string y)
    {
        Simulation sim = NewSim();
        AssertRejected(sim, player, 0, TestSim.V(x, y));
    }

    [Fact]
    public void UnlockedZone_AcceptsDeploysOnlyAfterTheTowerFalls()
    {
        Simulation sim = NewSim();
        FixVector2 forward = TestSim.V("4.5", "20.5"); // inside tower_1_west's unlock rectangle
        AssertRejected(sim, 0, 0, forward);

        sim.State.Map.DestroyStructure(4); // tower_1_west
        sim.Tick(new[] { DeployCmd(sim, 0, 0, forward) });
        Assert.Single(sim.State.PendingSpawns);
        Assert.Equal(1, sim.State.GetPlayer(0).IgnoredDeploys);
    }

    [Fact]
    public void ValidDeploy_PaysGoldAndQueuesSpawn()
    {
        Simulation sim = NewSim(start: "10");
        PlayerState p0 = sim.State.GetPlayer(0);
        int slot = TestSim.EnsureInHand(p0, "knight"); // costs 4
        CardDefinition knight = p0.Cards.Hand[slot]!;
        string nextBefore = p0.Cards.NextCard.Id;

        sim.Tick(new[] { DeployCmd(sim, 0, slot, OpenP0) });

        Assert.Equal(Fix.FromInt(6), p0.Gold);
        Assert.Equal(nextBefore, p0.Cards.Hand[slot]!.Id);
        Assert.Same(knight, p0.Cards.Queue[p0.Cards.Queue.Count - 1]);
        PendingSpawn spawn = Assert.Single(sim.State.PendingSpawns);
        Assert.Equal(20, spawn.SpawnTick);
        Assert.Equal(0, spawn.Owner);
        Assert.Same(knight, spawn.Definition);
        Assert.Equal(OpenP0, spawn.Target);
        Assert.Equal(10, sim.State.GetPlayer(1).Gold.Raw >> 16); // the other player is untouched
    }

    [Fact]
    public void SecondDeployInTheSameTick_SeesTheGoldSpentByTheFirst()
    {
        // Find a seed whose opening hand holds both the knight (4) and the stone golem (5).
        ulong seed = 0;
        Simulation sim;
        while (true)
        {
            sim = NewSim(start: "7", seed: seed++);
            PlayerState p = sim.State.GetPlayer(0);
            if (TestSim.SlotOf(p, "knight") >= 0 && TestSim.SlotOf(p, "stone_golem") >= 0)
            {
                break;
            }
        }
        PlayerState p0 = sim.State.GetPlayer(0);
        int knight = TestSim.SlotOf(p0, "knight");
        int golem = TestSim.SlotOf(p0, "stone_golem");

        // Submitted out of order; sequence 0 (knight) is applied first, leaving 3 gold for the golem.
        sim.Tick(new[]
        {
            Command.DeployCard(0, 0, 1, golem, OpenP0),
            Command.DeployCard(0, 0, 0, knight, OpenP0),
        });

        Assert.Equal(Fix.FromInt(3), p0.Gold);
        Assert.Equal(1, p0.IgnoredDeploys);
        Assert.Equal("knight", Assert.Single(sim.State.PendingSpawns).Definition.Id);
        Assert.Equal("stone_golem", p0.Cards.Hand[golem]!.Id);
    }

    [Theory]
    [InlineData("1", 20)]
    [InlineData("0.5", 10)]
    [InlineData("0.25", 5)]
    [InlineData("0.75", 15)]
    public void Units_AppearExactlyAfterTheSpawnDelay(string delaySeconds, int delayTicks)
    {
        Simulation sim = NewSim(spawnDelay: delaySeconds);
        TestSim.Run(sim, 7); // deploy on a tick other than 0
        int slot = TestSim.EnsureInHand(sim.State.GetPlayer(0), "knight");
        int deployTick = sim.State.Tick;
        sim.Tick(new[] { DeployCmd(sim, 0, slot, OpenP0) });

        while (sim.State.Tick < deployTick + delayTicks)
        {
            Assert.Empty(sim.State.Units);
            Assert.Single(sim.State.PendingSpawns);
            sim.Tick(Array.Empty<Command>());
        }
        Assert.Equal(deployTick + delayTicks, sim.State.Tick);
        Unit unit = Assert.Single(sim.State.Units);
        Assert.Empty(sim.State.PendingSpawns);

        Assert.Equal(1, unit.Id);
        Assert.Equal(0, unit.Owner);
        Assert.Equal("knight", unit.DefinitionId);
        Assert.Equal(unit.Definition.Hp, unit.Hp);
        Assert.Equal(OpenP0, unit.Position);
        Assert.Equal(UnitState.Spawning, unit.State);
        Assert.Equal(Unit.NoObjective, unit.Objective);
        Assert.Same(unit, sim.State.FindUnit(1));
        Assert.Null(sim.State.FindUnit(2));

        sim.Tick(Array.Empty<Command>());
        Assert.Equal(UnitState.Moving, unit.State);
        Assert.NotEqual(OpenP0, unit.Position);
        Assert.Equal(unit.Definition.Hp, unit.Hp);
    }

    [Fact]
    public void ZeroDelay_SpawnsDuringTheDeployTickAndMovesAtOnce()
    {
        Simulation sim = NewSim(spawnDelay: "0");
        int slot = TestSim.EnsureInHand(sim.State.GetPlayer(0), "knight");
        sim.Tick(new[] { DeployCmd(sim, 0, slot, OpenP0) });
        Unit unit = Assert.Single(sim.State.Units);
        Assert.Empty(sim.State.PendingSpawns);
        Assert.Equal(UnitState.Moving, unit.State);
        Assert.NotEqual(OpenP0, unit.Position);
    }

    [Fact]
    public void Swarm_SpawnsItsCountInATightPattern()
    {
        Simulation sim = NewSim();
        TestSim.Deploy(sim, 0, "goblin_pack", OpenP0);
        TestSim.Run(sim, 19);

        Assert.Equal(4, sim.State.Units.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, sim.State.Units.Select(u => u.Id));
        Assert.Equal(5, sim.State.NextUnitId);
        Assert.All(sim.State.Units, u =>
        {
            Assert.Equal("goblin_pack", u.DefinitionId);
            Assert.True(sim.State.Map.Grid.IsWalkableAt(u.Position));
            Assert.True(FixVector2.Distance(u.Position, OpenP0) <= Fix.Parse("0.5"));
        });
        Assert.Equal(4, sim.State.Units.Select(u => u.Position).Distinct().Count());
        // Center, then front (toward the enemy), then left and right.
        Assert.Equal(new[] { OpenP0, OpenP0 + TestSim.V("0", "0.5"), OpenP0 + TestSim.V("-0.5", "0"), OpenP0 + TestSim.V("0.5", "0") },
            sim.State.Units.Select(u => u.Position));
    }

    [Fact]
    public void SpawnPattern_IsRotatedForPlayerOne()
    {
        Simulation sim = NewSim();
        DeployBoth(sim, "goblin_pack", OpenP0, OpenP1);
        TestSim.Run(sim, 19);
        var mapCorner = new FixVector2(Fix.FromInt(18), Fix.FromInt(32));
        Unit[] p0 = sim.State.Units.Where(u => u.Owner == 0).ToArray();
        Unit[] p1 = sim.State.Units.Where(u => u.Owner == 1).ToArray();
        Assert.Equal(4, p1.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(mapCorner - p0[i].Position, p1[i].Position);
        }
    }

    /// <summary>Both players deploy the same card on the same tick.</summary>
    internal static void DeployBoth(Simulation sim, string cardId, FixVector2 target0, FixVector2 target1)
    {
        int slot0 = TestSim.EnsureInHand(sim.State.GetPlayer(0), cardId);
        int slot1 = TestSim.EnsureInHand(sim.State.GetPlayer(1), cardId);
        int tick = sim.State.Tick;
        sim.Tick(new[] { Command.DeployCard(tick, 0, 0, slot0, target0), Command.DeployCard(tick, 1, 0, slot1, target1) });
    }

    [Fact]
    public void SpawnPositions_OnBlockedCellsAreNudgedToWalkableCells()
    {
        Simulation sim = NewSim();
        // Right next to the left edge of player 0's Keep (cells x 7-10, y 1-3): the right-hand goblin would land in it.
        FixVector2 target = TestSim.V("6.8", "2");
        TestSim.Deploy(sim, 0, "goblin_pack", target);
        TestSim.Run(sim, 19);
        Grid grid = sim.State.Map.Grid;
        Assert.Equal(4, sim.State.Units.Count);
        Assert.All(sim.State.Units, u => Assert.True(grid.IsWalkableAt(u.Position), "unit " + u.Id + " spawned on a blocked cell"));
        Unit right = sim.State.Units[3];
        Assert.False(grid.IsWalkableAt(target + TestSim.V("0.5", "0")));
        Assert.Equal(TestSim.V("6.5", "1.5"), right.Position); // nearest walkable cell center (ties: lower row first)
    }

    [Fact]
    public void SpawnOffsets_FollowTheSquareSpiral()
    {
        IReadOnlyList<CellCoord> offsets = UnitMovement.SpawnOffsets(10);
        Assert.Equal(new[]
        {
            new CellCoord(0, 0),
            new CellCoord(0, 1), new CellCoord(-1, 0), new CellCoord(1, 0), new CellCoord(0, -1),
            new CellCoord(-1, 1), new CellCoord(1, 1), new CellCoord(-1, -1), new CellCoord(1, -1),
            new CellCoord(0, 2),
        }, offsets);
        Assert.Equal(25, UnitMovement.SpawnOffsets(25).Distinct().Count());
    }

    [Fact]
    public void Nudge_FallsBackToTargetWhenNothingIsNearby()
    {
        Grid grid = MapTestData.GridFromRows(new[] { "#########", "#########", "#########", "#########", ".########" }, Array.Empty<CellRect>());
        FixVector2 fallback = TestSim.V("0.5", "0.5");
        // The only open cell, (0, 0), is 8 cells away: too far, so the fallback is used.
        Assert.Equal(fallback, UnitMovement.NudgeToWalkable(grid, TestSim.V("8.5", "4.5"), fallback));
        // Within 3 cells it is found.
        FixVector2 elsewhere = TestSim.V("9", "9");
        Assert.Equal(TestSim.V("0.5", "0.5"), UnitMovement.NudgeToWalkable(grid, TestSim.V("3.5", "3.5"), elsewhere));
        Assert.Equal(elsewhere, UnitMovement.NudgeToWalkable(grid, TestSim.V("4.5", "4.5"), elsewhere));
    }
}
