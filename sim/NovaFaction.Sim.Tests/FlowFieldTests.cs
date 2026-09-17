using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using static NovaFaction.Sim.Tests.MapTestData;

namespace NovaFaction.Sim.Tests;

public class FlowFieldTests
{
    private static readonly Fix D = FlowField.DiagonalCost;
    private static readonly FixVector2 East = FixVector2.UnitX;
    private static readonly FixVector2 West = -FixVector2.UnitX;
    private static readonly FixVector2 North = FixVector2.UnitY;
    private static readonly FixVector2 South = -FixVector2.UnitY;

    private static Fix Cost(int straight, int diagonal) => Fix.FromInt(straight) + Fix.FromInt(diagonal) * D;

    private static CellRect One(int x, int y) => new CellRect(x, y, 1, 1);

    private static CellCoord C(int x, int y) => new CellCoord(x, y);

    private static FixVector2 Center(Grid grid, int x, int y) => grid.CellToWorld(x, y);

    [Fact]
    public void DiagonalCost_IsSqrtTwo()
    {
        Assert.Equal(Fix.Sqrt(Fix.FromInt(2)), D);
        Assert.Equal(92682L, D.Raw); // 1.41421356... * 65536 = 92681.9
    }

    [Fact]
    public void StraightCorridor_DistancesCountCells()
    {
        Grid grid = GridFromRows(new[] { "......" }, new[] { One(0, 0) });
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));
        for (int x = 0; x < 6; x++)
        {
            Assert.Equal(Fix.FromInt(x), field.GetCellDistance(C(x, 0)));
        }
        Assert.Equal(West, field.GetCellDirection(C(5, 0)));
        Assert.Equal(FixVector2.Zero, field.GetCellDirection(C(0, 0))); // inside the target
        Assert.Equal(Fix.Zero, field.GetDistance(Center(grid, 0, 0)));
    }

    [Fact]
    public void OpenField_UsesDiagonals()
    {
        Grid grid = GridFromRows(new[] { "....", "....", "....", "...." }, new[] { One(0, 0) });
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));

        Assert.Equal(Cost(0, 1), field.GetCellDistance(C(1, 1)));
        Assert.Equal(Cost(0, 3), field.GetCellDistance(C(3, 3)));
        Assert.Equal(Cost(2, 1), field.GetCellDistance(C(3, 1)));
        Assert.Equal(Cost(1, 2), field.GetCellDistance(C(2, 3)));
        Assert.Equal(Cost(3, 0), field.GetCellDistance(C(0, 3)));

        Assert.Equal(new FixVector2(-Fix.One, -Fix.One).Normalized, field.GetCellDirection(C(3, 3)));
        Assert.Equal(South, field.GetCellDirection(C(0, 3)));
        Assert.Equal(Fix.One, field.GetCellDirection(C(2, 2)).Length);
    }

    [Fact]
    public void MultiCellTarget_DistanceIsToNearestTargetCell()
    {
        Grid grid = GridFromRows(new[] { "......", "......", "......", "......" }, new[] { new CellRect(2, 1, 2, 2) });
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));
        foreach (CellCoord c in new[] { C(2, 1), C(3, 1), C(2, 2), C(3, 2) })
        {
            Assert.Equal(Fix.Zero, field.GetCellDistance(c));
            Assert.Equal(FixVector2.Zero, field.GetCellDirection(c));
        }
        Assert.Equal(Fix.One, field.GetCellDistance(C(1, 2)));
        Assert.Equal(Fix.One, field.GetCellDistance(C(3, 3)));
        Assert.Equal(Cost(0, 1), field.GetCellDistance(C(4, 0)));
        Assert.Equal(Fix.FromInt(2), field.GetCellDistance(C(5, 1)));
        Assert.Equal(West, field.GetCellDirection(C(5, 2)));
        Assert.Equal(North, field.GetCellDirection(C(2, 0)));
    }

    // Wall with one opening on the right; the target sits below the wall.
    //   y2  .....
    //   y1  ####.
    //   y0  ..T..
    private static readonly string[] WallRows = { ".....", "####.", "....." };

    [Fact]
    public void RoutesAroundAnObstacle_WithExactDistances()
    {
        Grid grid = GridFromRows(WallRows, new[] { One(2, 0) });
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));

        // Costs worked out by hand; diagonals past the wall's end are corner cuts and not allowed.
        Assert.Equal(Fix.FromInt(1), field.GetCellDistance(C(3, 0)));
        Assert.Equal(Fix.FromInt(2), field.GetCellDistance(C(4, 0)));
        Assert.Equal(Fix.FromInt(3), field.GetCellDistance(C(4, 1)));
        Assert.Equal(Fix.FromInt(4), field.GetCellDistance(C(4, 2)));
        Assert.Equal(Fix.FromInt(5), field.GetCellDistance(C(3, 2)));
        Assert.Equal(Fix.FromInt(8), field.GetCellDistance(C(0, 2)));
        Assert.Equal(Fix.FromInt(2), field.GetCellDistance(C(0, 0)));
        Assert.Equal(Fix.MaxValue, field.GetCellDistance(C(1, 1))); // wall

        List<CellCoord> path = FollowPath(grid, field, C(0, 2));
        Assert.Equal(new[] { C(0, 2), C(1, 2), C(2, 2), C(3, 2), C(4, 2), C(4, 1), C(4, 0), C(3, 0), C(2, 0) }, path);
    }

    [Fact]
    public void GetDistance_IsInWorldUnits()
    {
        Grid grid = GridFromRows(WallRows, new[] { One(2, 0) }, cellSize: "2.5");
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));
        Assert.Equal(Fix.FromInt(20), field.GetDistance(Center(grid, 0, 2)));
        Assert.Equal(Fix.FromInt(20), field.GetDistance(new FixVector2(Fix.Parse("0.01"), Fix.Parse("7.4"))));
        Assert.Equal(Fix.Parse("2.5"), field.GetDistance(Center(grid, 3, 0)));
        Assert.Equal(Fix.MaxValue, field.GetDistance(Center(grid, 1, 1)));
        Assert.Equal(Fix.MaxValue, field.GetDistance(new FixVector2(Fix.FromInt(-1), Fix.One)));

        // Diagonal distance scales too.
        Grid open = GridFromRows(new[] { "..", ".." }, new[] { One(0, 0) }, cellSize: "2");
        FlowField openField = FlowField.Compute(open, open.GetFootprint(0));
        Assert.Equal(D * Fix.FromInt(2), openField.GetDistance(Center(open, 1, 1)));
    }

    [Fact]
    public void GoesThroughALaneGap()
    {
        //   y4  ...T...
        //   y3  .......
        //   y2  #####.#
        //   y1  .......
        //   y0  S......
        Grid grid = GridFromRows(new[] { ".......", ".......", "#####.#", ".......", "......." }, new[] { One(3, 4) });
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));
        List<CellCoord> path = FollowPath(grid, field, C(0, 0));
        Assert.Contains(C(5, 2), path);
        // Worked out by hand: the detour through the gap costs 7 straight + 2 diagonal steps
        // (the open-field distance would be 1 straight + 3 diagonal).
        Assert.Equal(Cost(7, 2), field.GetCellDistance(C(0, 0)));
        Assert.Equal(2, CountDiagonals(path));
    }

    private static int CountDiagonals(List<CellCoord> path)
    {
        int n = 0;
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i].X != path[i - 1].X && path[i].Y != path[i - 1].Y) n++;
        }
        return n;
    }

    [Fact]
    public void NeverCutsCorners()
    {
        // Only diagonal contact between (1,1) and the target at (0,0): no path.
        //   y1  #.
        //   y0  T#
        Grid sealedGrid = GridFromRows(new[] { "#.", ".#" }, new[] { One(0, 0) });
        FlowField sealedField = FlowField.Compute(sealedGrid, sealedGrid.GetFootprint(0));
        Assert.False(sealedField.IsReachable(C(1, 1)));
        Assert.Equal(Fix.MaxValue, sealedField.GetCellDistance(C(1, 1)));
        Assert.Equal(FixVector2.Zero, sealedField.GetCellDirection(C(1, 1)));

        // One side open: the path goes around the corner (2 straight steps, not 1 diagonal).
        //   y1  ..
        //   y0  T#
        Grid oneSide = GridFromRows(new[] { "..", ".#" }, new[] { One(0, 0) });
        FlowField field = FlowField.Compute(oneSide, oneSide.GetFootprint(0));
        Assert.Equal(Fix.FromInt(2), field.GetCellDistance(C(1, 1)));
        Assert.Equal(West, field.GetCellDirection(C(1, 1)));

        // A standing structure is a corner too.
        Grid structureCorner = GridFromRows(new[] { "..", ".." }, new[] { One(0, 0), One(1, 0) });
        FlowField f2 = FlowField.Compute(structureCorner, structureCorner.GetFootprint(0));
        Assert.Equal(Fix.FromInt(1), f2.GetCellDistance(C(0, 1)));
        Assert.Equal(Fix.FromInt(2), f2.GetCellDistance(C(1, 1)));
    }

    [Fact]
    public void UnreachableAndOutOfBoundsQueries_ReturnZeroDirection()
    {
        Grid grid = GridFromRows(WallRows, new[] { One(2, 0) });
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));
        Assert.Equal(FixVector2.Zero, field.GetDirection(Center(grid, 1, 1)));            // wall
        Assert.Equal(FixVector2.Zero, field.GetDirection(new FixVector2(Fix.FromInt(-3), Fix.Zero)));
        Assert.Equal(FixVector2.Zero, field.GetDirection(new FixVector2(Fix.One, Fix.FromInt(99))));
        Assert.False(field.IsReachable(C(5, 0)));
        Assert.Equal(C(-1, 0), field.GetNextCell(C(-1, 0)));
        Assert.Equal(C(1, 1), field.GetNextCell(C(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlowField.Compute(grid, new CellRect(4, 0, 2, 1)));
        Assert.Throws<ArgumentNullException>(() => FlowField.Compute(null!, One(0, 0)));
    }

    [Fact]
    public void TieBreak_PrefersDirectionTowardTheTarget()
    {
        // From (0,0) to (2,4) both N and NE start a shortest path; NE points closer to the target.
        Grid grid = GridFromRows(new[] { ".....", ".....", ".....", ".....", "....." }, new[] { One(2, 4) });
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));
        Assert.Equal(new FixVector2(Fix.One, Fix.One).Normalized, field.GetCellDirection(C(0, 0)));
    }

    [Fact]
    public void TieBreak_OnAPerfectMirror_IsRotationSymmetric()
    {
        // A wall straight between the cell and the target: going east or west is equally short.
        // Target at the top: the unit turns to the left of "up" = west.
        //   y4  ..T..
        //   y3  .....
        //   y2  .###.
        //   y1  ..S..
        //   y0  .....
        Grid up = GridFromRows(new[] { ".....", ".....", ".###.", ".....", "....." }, new[] { One(2, 4) });
        FlowField upField = FlowField.Compute(up, up.GetFootprint(0));
        Assert.Equal(Cost(5, 1), upField.GetCellDistance(C(2, 1)));
        Assert.Equal(West, upField.GetCellDirection(C(2, 1)));

        // The same picture rotated 180 degrees: the unit turns to the left of "down" = east.
        Grid down = GridFromRows(new[] { ".....", ".....", ".###.", ".....", "....." }, new[] { One(2, 0) });
        FlowField downField = FlowField.Compute(down, down.GetFootprint(0));
        Assert.Equal(East, downField.GetCellDirection(C(2, 3)));
    }

    [Fact]
    public void RepeatedComputation_IsIdentical()
    {
        MapDefinition mapA = LoadTwoLane();
        MapDefinition mapB = LoadTwoLane(); // independently loaded
        var gridA = new Grid(mapA);
        var gridB = new Grid(mapB);
        for (int s = 0; s < mapA.Structures.Count; s++)
        {
            FlowField a = FlowField.Compute(gridA, gridA.GetFootprint(s));
            FlowField a2 = FlowField.Compute(gridA, gridA.GetFootprint(s));
            FlowField b = FlowField.Compute(gridB, gridB.GetFootprint(s));
            Assert.Equal(Fingerprint(gridA, a), Fingerprint(gridA, a2));
            Assert.Equal(Fingerprint(gridA, a), Fingerprint(gridB, b));
        }
    }

    /// <summary>Every cell's distance and direction, as a hash.</summary>
    internal static ulong Fingerprint(Grid grid, FlowField field)
    {
        var h = new StateHasher();
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                h.Add(field.GetCellDistance(C(x, y)));
                h.Add(field.GetCellDirection(C(x, y)));
            }
        }
        return h.Value;
    }

    [Fact]
    public void TwoLane_FieldsArePinned()
    {
        // Pins the exact routing on the shipped map so any change to pathfinding is noticed.
        // If you change the algorithm or twolane.json on purpose, update these values.
        MapDefinition map = LoadTwoLane();
        var grid = new Grid(map);
        var fingerprints = Enumerable.Range(0, map.Structures.Count)
            .Select(s => Fingerprint(grid, FlowField.Compute(grid, grid.GetFootprint(s))).ToString("X16"))
            .ToArray();
        Assert.Equal(PinnedTwoLaneFingerprints, fingerprints);
    }

    private static readonly string[] PinnedTwoLaneFingerprints =
    {
        // Updated Sept 2026: mine cells became blocked.
        "58CBF0BD74A51FBE", "9BA33E146BB9F222", "3B9FCE5CEDF5CF3D",
        "D4AEE46E49733E76", "B3D235625E5261D1", "14ED33D2F2FD1EE1",
    };

    [Fact]
    public void TwoLane_FieldsAreRotationSymmetricBetweenPlayers()
    {
        MapDefinition map = LoadTwoLane();
        var grid = new Grid(map);
        int w = map.Width, h = map.Height;
        foreach (StructureDefinition s in map.Structures)
        {
            CellRect rotated = MapDefinitionTests.Rotate(s.Footprint, w, h);
            int twin = map.Structures.Single(o => o.Footprint == rotated).Index;
            FlowField field = FlowField.Compute(grid, s.Footprint);
            FlowField twinField = FlowField.Compute(grid, grid.GetFootprint(twin));
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var mirror = C(w - 1 - x, h - 1 - y);
                    Assert.Equal(field.GetCellDistance(C(x, y)), twinField.GetCellDistance(mirror));
                    Assert.Equal(-field.GetCellDirection(C(x, y)), twinField.GetCellDirection(mirror));
                }
            }
        }
    }

    [Fact]
    public void TwoLane_BothKeepsReachableFromBothSpawnSides()
    {
        MapDefinition map = LoadTwoLane();
        var grid = new Grid(map);
        for (int keepOwner = 0; keepOwner < 2; keepOwner++)
        {
            FlowField field = FlowField.Compute(grid, map.GetKeep(keepOwner).Footprint);
            for (int player = 0; player < 2; player++)
            {
                foreach (CellCoord spawn in map.GetSpawnPoints(player))
                {
                    Assert.True(field.IsReachable(spawn));
                    List<CellCoord> path = FollowPath(grid, field, spawn);
                    if (player != keepOwner)
                    {
                        // Crossing the river means passing a lane gap.
                        Assert.Contains(path, c => c.Y >= 14 && c.Y <= 17);
                        Assert.All(path.Where(c => c.Y >= 14 && c.Y <= 17),
                            c => Assert.True((c.X >= 2 && c.X <= 5) || (c.X >= 12 && c.X <= 15), "left the lane at " + c));
                    }
                }
            }
        }
    }

    [Fact]
    public void TwoLane_EveryReachableCellLeadsToEveryStructure()
    {
        MapDefinition map = LoadTwoLane();
        var grid = new Grid(map);
        foreach (StructureDefinition s in map.Structures)
        {
            FlowField field = FlowField.Compute(grid, s.Footprint);
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    bool walkable = grid.IsWalkable(x, y);
                    Assert.Equal(walkable || s.Footprint.Contains(x, y), field.IsReachable(C(x, y)));
                    if (walkable)
                    {
                        FollowPath(grid, field, C(x, y));
                    }
                }
            }
        }
    }

    // A structure S blocks the only short way down:
    //   y2  ..U..     U = unit
    //   y1  ##S#.
    //   y0  T....
    private static Grid ShortcutGrid() =>
        GridFromRows(new[] { ".....", "##.#.", "....." }, new[] { One(0, 0), One(2, 1) });

    [Fact]
    public void DirectionField_ChangesAfterAFootprintIsUnblocked()
    {
        Grid grid = ShortcutGrid();
        var cache = new FlowFieldCache(grid);
        FlowField before = cache.Get(0);
        Assert.Equal(Fix.FromInt(8), before.GetCellDistance(C(2, 2)));
        Assert.Equal(East, before.GetCellDirection(C(2, 2)));
        Assert.False(before.IsReachable(C(2, 1)));
        Assert.Same(before, cache.Get(0));
        Assert.Equal(1, cache.BuildCount);

        Assert.True(grid.MarkStructureDestroyed(1));

        Assert.True(before.IsStale);
        FlowField after = cache.Get(0);
        Assert.NotSame(before, after);
        Assert.False(after.IsStale);
        Assert.Equal(2, cache.BuildCount);
        Assert.Equal(Fix.FromInt(3), after.GetCellDistance(C(2, 1)));
        Assert.Equal(Fix.FromInt(4), after.GetCellDistance(C(2, 2)));
        Assert.Equal(South, after.GetCellDirection(C(2, 2)));
        Assert.Equal(South, after.GetDirection(Center(grid, 2, 2)));
        FollowPath(grid, after, C(2, 2));

        // The rebuilt field equals a fresh computation.
        Assert.Equal(Fingerprint(grid, FlowField.Compute(grid, grid.GetFootprint(0))), Fingerprint(grid, after));
        Assert.Same(after, cache.Get(0));
    }

    [Fact]
    public void Cache_KeepsOneFieldPerStructure()
    {
        Grid grid = ShortcutGrid();
        var cache = new FlowFieldCache(grid);
        FlowField toTower = cache.Get(0);
        FlowField toBlocker = cache.Get(1);
        Assert.NotSame(toTower, toBlocker);
        Assert.Equal(grid.GetFootprint(1), toBlocker.Target);
        Assert.Equal(Fix.One, toBlocker.GetCellDistance(C(2, 2)));
        Assert.Same(toTower, cache.Get(0));
        Assert.Equal(2, cache.BuildCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Get(2));
        Assert.Throws<ArgumentNullException>(() => new FlowFieldCache(null!));
    }

    [Fact]
    public void DestroyedTarget_IsStillATarget()
    {
        Grid grid = ShortcutGrid();
        grid.MarkStructureDestroyed(0);
        FlowField field = FlowField.Compute(grid, grid.GetFootprint(0));
        Assert.Equal(Fix.Zero, field.GetCellDistance(C(0, 0)));
        Assert.Equal(Fix.One, field.GetCellDistance(C(1, 0)));
    }
}
