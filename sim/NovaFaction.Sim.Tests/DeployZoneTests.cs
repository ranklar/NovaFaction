using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using static NovaFaction.Sim.Tests.MapTestData;

namespace NovaFaction.Sim.Tests;

public class DeployZoneTests
{
    private static FixVector2 At(Grid grid, int x, int y) => grid.CellToWorld(x, y);

    private static (MapDefinition map, Grid grid, DeployZones zones) SmallZonesSetup()
    {
        MapDefinition map = Small();
        var grid = new Grid(map);
        return (map, grid, new DeployZones(map, grid));
    }

    [Fact]
    public void BaseZones_BelongToTheirPlayerOnly()
    {
        var (_, grid, zones) = SmallZonesSetup();
        Assert.True(zones.IsDeployable(0, At(grid, 1, 1), null));
        Assert.False(zones.IsDeployable(1, At(grid, 1, 1), null));
        Assert.True(zones.IsDeployable(1, At(grid, 5, 7), null));
        Assert.False(zones.IsDeployable(0, At(grid, 5, 7), null));
        // The neutral middle row belongs to nobody.
        Assert.False(zones.IsDeployable(0, At(grid, 3, 4), null));
        Assert.False(zones.IsDeployable(1, At(grid, 3, 4), null));
        Assert.Equal(new[] { new CellRect(0, 0, 7, 4) }, zones.GetBaseZones(0));
    }

    [Fact]
    public void ZoneEdges_FollowCellBoundaries()
    {
        var (_, grid, zones) = SmallZonesSetup();
        // Player 0's zone is rows 0-3: world y in [0, 4).
        Assert.True(zones.IsDeployable(0, new FixVector2(Fix.Parse("1.5"), Fix.FromRaw(Fix.FromInt(4).Raw - 1)), null));
        Assert.False(zones.IsDeployable(0, new FixVector2(Fix.Parse("1.5"), Fix.FromInt(4)), null));
        Assert.True(zones.IsDeployable(0, new FixVector2(Fix.Parse("1.5"), Fix.Zero), null));
        Assert.False(zones.IsDeployable(0, new FixVector2(Fix.Parse("1.5"), -Fix.Epsilon), null));
        Assert.False(zones.IsDeployable(0, new FixVector2(Fix.FromInt(7), Fix.One), null));
        Assert.False(zones.IsDeployable(1, new FixVector2(Fix.One, Fix.FromInt(9)), null));
    }

    [Fact]
    public void BlockedCells_AreNeverDeployable()
    {
        var (_, grid, zones) = SmallZonesSetup();
        Assert.False(zones.IsDeployable(0, At(grid, 0, 2), null)); // '#' inside player 0's zone
        Assert.False(zones.IsDeployable(0, At(grid, 3, 0), null)); // own keep footprint
        Assert.False(zones.IsDeployable(0, At(grid, 6, 0), null)); // own tower footprint
        Assert.True(zones.IsDeployable(0, At(grid, 0, 3), null));  // mine marker is open ground
        Assert.True(zones.IsDeployable(0, At(grid, 3, 1), null));  // spawn marker is open ground

        // Even an unlocked zone covering the blocked cell does not help.
        var everything = new[] { new CellRect(0, 0, 7, 9) };
        Assert.False(zones.IsDeployable(1, At(grid, 0, 2), everything));
        Assert.False(zones.IsDeployable(1, At(grid, 0, 4), everything));
        Assert.True(zones.IsDeployable(1, At(grid, 3, 4), everything));
    }

    [Fact]
    public void UnlockedZones_ExtendWhereAPlayerMayDeploy()
    {
        var (map, grid, zones) = SmallZonesSetup();
        CellRect west = map.Structures[Tower0W].UnlocksDeployZone!.Value; // (0,1) 3x3, on player 0's side
        CellRect east = map.Structures[Tower0E].UnlocksDeployZone!.Value;

        Assert.False(zones.IsDeployable(1, At(grid, 1, 2), null));
        Assert.False(zones.IsDeployable(1, At(grid, 1, 2), Array.Empty<CellRect>()));
        Assert.True(zones.IsDeployable(1, At(grid, 1, 2), new[] { west }));
        Assert.False(zones.IsDeployable(1, At(grid, 5, 2), new[] { west }));
        Assert.True(zones.IsDeployable(1, At(grid, 5, 2), new[] { west, east }));
        Assert.False(zones.IsDeployable(1, At(grid, 3, 2), new[] { west, east })); // between the two rectangles
        Assert.True(zones.IsDeployable(1, At(grid, 5, 7), new[] { west }));        // base zone still works
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        var (map, grid, zones) = SmallZonesSetup();
        Assert.Throws<ArgumentOutOfRangeException>(() => zones.IsDeployable(2, At(grid, 1, 1), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => zones.IsDeployable(-1, At(grid, 1, 1), null));
        Assert.Throws<ArgumentNullException>(() => new DeployZones(null!, grid));
        Assert.Throws<ArgumentNullException>(() => new DeployZones(map, null!));
        Assert.Throws<ArgumentException>(() => new DeployZones(map, new Grid(LoadTwoLane())));
    }

    [Fact]
    public void TwoLane_SpawnMarkersAreDeployableForTheirOwnerOnly()
    {
        MapDefinition map = LoadTwoLane();
        var grid = new Grid(map);
        var zones = new DeployZones(map, grid);
        for (int player = 0; player < 2; player++)
        {
            foreach (CellCoord spawn in map.GetSpawnPoints(player))
            {
                Assert.True(zones.IsDeployable(player, grid.CellToWorld(spawn), null));
                Assert.False(zones.IsDeployable(1 - player, grid.CellToWorld(spawn), null));
            }
        }
        // The river is nobody's.
        for (int y = 13; y <= 18; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                Assert.False(zones.IsDeployable(0, grid.CellToWorld(x, y), null));
                Assert.False(zones.IsDeployable(1, grid.CellToWorld(x, y), null));
            }
        }
    }
}
