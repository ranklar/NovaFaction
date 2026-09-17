using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using static NovaFaction.Sim.Tests.MapTestData;

namespace NovaFaction.Sim.Tests;

public class GridTests
{
    private static FixVector2 V(string x, string y) => new FixVector2(Fix.Parse(x), Fix.Parse(y));

    [Theory]
    [InlineData("1")]
    [InlineData("0.75")]
    [InlineData("1.3")]     // odd raw value: the half-cell offset is rounded down
    [InlineData("0.0625")]
    [InlineData("64")]
    public void CellCenter_RoundTripsForEveryCell(string cellSize)
    {
        MapDefinition map = Small(cellSize);
        var grid = new Grid(map);
        Assert.Equal(Fix.Parse(cellSize), grid.CellSize);
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                var cell = new CellCoord(x, y);
                FixVector2 center = grid.CellToWorld(cell);
                Assert.Equal(cell, grid.WorldToCell(center));
                Assert.Equal(cell, grid.WorldToCell(grid.CellToWorldCorner(cell)));
                // The last point inside the cell still maps to it.
                var lastInside = new FixVector2(Fix.FromRaw(grid.CellToWorldCorner(cell).X.Raw + grid.CellSize.Raw - 1),
                    Fix.FromRaw(grid.CellToWorldCorner(cell).Y.Raw + grid.CellSize.Raw - 1));
                Assert.Equal(cell, grid.WorldToCell(lastInside));
            }
        }
    }

    [Fact]
    public void WorldToCell_UsesExactFloorOnEdgesAndNegatives()
    {
        var grid = new Grid(Small("0.75"));
        Assert.Equal(new CellCoord(0, 0), grid.WorldToCell(V("0", "0")));
        Assert.Equal(new CellCoord(1, 2), grid.WorldToCell(V("0.75", "1.5")));   // exactly on an edge -> upper cell
        Assert.Equal(new CellCoord(0, 1), grid.WorldToCell(new FixVector2(
            Fix.FromRaw(Fix.Parse("0.75").Raw - 1), Fix.FromRaw(Fix.Parse("1.5").Raw - 1))));
        Assert.Equal(new CellCoord(-1, -1), grid.WorldToCell(V("-0.1", "-0.75")));
        Assert.Equal(new CellCoord(-2, 0), grid.WorldToCell(V("-0.76", "0.1")));
        Assert.Equal(new CellCoord(9, 12), grid.WorldToCell(V("7", "9")));       // out of bounds is still reported
        Assert.Equal(V("1.125", "0.375"), grid.CellToWorld(1, 0));

        // Huge values clamp instead of overflowing.
        CellCoord far = grid.WorldToCell(new FixVector2(Fix.MaxValue, Fix.MinValue));
        Assert.Equal(int.MaxValue, far.X);
        Assert.Equal(int.MinValue, far.Y);
        Assert.False(grid.IsWalkable(far));
    }

    [Fact]
    public void CellToWorld_MatchesCellSizeTimesCoordinatePlusHalf()
    {
        var grid = new Grid(LoadTwoLane());
        Assert.Equal(V("0.5", "0.5"), grid.CellToWorld(0, 0));
        Assert.Equal(V("17.5", "31.5"), grid.CellToWorld(17, 31));
        Assert.Equal(V("3", "6"), grid.CellToWorldCorner(new CellCoord(3, 6)));
    }

    [Fact]
    public void Walkability_TreatsTerrainFootprintsAndBoundsAsBlocked()
    {
        var grid = new Grid(Small());
        Assert.True(grid.IsWalkable(1, 0));      // ground
        Assert.True(grid.IsWalkable(3, 1));      // spawn marker
        Assert.True(grid.IsWalkable(0, 3));      // mine
        Assert.True(grid.IsWalkable(6, 3));      // chest spawn
        Assert.False(grid.IsWalkable(0, 4));     // '#'
        Assert.False(grid.IsWalkable(3, 0));     // keep footprint
        Assert.False(grid.IsWalkable(0, 0));     // tower footprint
        Assert.False(grid.IsWalkable(-1, 1));
        Assert.False(grid.IsWalkable(7, 1));
        Assert.False(grid.IsWalkable(1, 9));
        Assert.True(grid.IsTerrainBlocked(0, 4));
        Assert.False(grid.IsTerrainBlocked(3, 0)); // footprints are not terrain
        Assert.True(grid.IsTerrainBlocked(-1, 0));
        Assert.True(grid.IsWalkableAt(V("1.5", "0.5")));
        Assert.False(grid.IsWalkableAt(V("3.5", "0.5")));
        Assert.False(grid.IsWalkableAt(V("-0.01", "0.5")));

        Assert.Equal(Keep0, grid.GetStructureAt(4, 0));
        Assert.Equal(Tower1E, grid.GetStructureAt(6, 8));
        Assert.Equal(-1, grid.GetStructureAt(1, 0));
        Assert.Equal(-1, grid.GetStructureAt(-5, 0));
        Assert.Equal(6, grid.StructureCount);
        Assert.Equal(new CellRect(2, 8, 3, 1), grid.GetFootprint(Keep1));
    }

    [Fact]
    public void DestroyingAStructure_MakesItsFootprintWalkable()
    {
        var grid = new Grid(Small());
        int version = grid.WalkabilityVersion;
        Assert.False(grid.IsStructureDestroyed(Keep0));

        Assert.True(grid.MarkStructureDestroyed(Keep0));

        Assert.True(grid.IsStructureDestroyed(Keep0));
        Assert.Equal(version + 1, grid.WalkabilityVersion);
        for (int x = 2; x <= 4; x++)
        {
            Assert.True(grid.IsWalkable(x, 0));
            Assert.Equal(Keep0, grid.GetStructureAt(x, 0)); // still remembers whose footprint it was
        }
        Assert.False(grid.IsWalkable(0, 0)); // other structures unaffected
        Assert.False(grid.IsStructureDestroyed(Tower0W));

        // A second destroy is a no-op.
        Assert.False(grid.MarkStructureDestroyed(Keep0));
        Assert.Equal(version + 1, grid.WalkabilityVersion);

        Assert.Throws<ArgumentOutOfRangeException>(() => grid.MarkStructureDestroyed(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.IsStructureDestroyed(-1));
    }

    [Fact]
    public void RawConstructor_ValidatesInput()
    {
        var one = new bool[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => new Grid(0, 4, Fix.One, Array.Empty<bool>(), Array.Empty<CellRect>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Grid(2, 2, Fix.Epsilon, one, Array.Empty<CellRect>()));
        Assert.Throws<ArgumentException>(() => new Grid(2, 3, Fix.One, one, Array.Empty<CellRect>()));
        Assert.Throws<ArgumentException>(() => new Grid(2, 2, Fix.One, one, new[] { new CellRect(1, 1, 2, 1) }));
        Assert.Throws<ArgumentException>(() => new Grid(2, 2, Fix.One, one, new[] { new CellRect(0, 0, 2, 1), new CellRect(1, 0, 1, 1) }));
    }

    [Fact]
    public void CellRect_Geometry()
    {
        var r = new CellRect(2, 3, 4, 2);
        Assert.True(r.Contains(2, 3));
        Assert.True(r.Contains(5, 4));
        Assert.False(r.Contains(6, 4));
        Assert.False(r.Contains(5, 5));
        Assert.False(r.Contains(1, 3));
        Assert.True(r.IsInside(6, 5));
        Assert.False(r.IsInside(5, 5));
        Assert.False(new CellRect(0, 0, 0, 1).IsInside(5, 5));
        Assert.False(new CellRect(int.MaxValue, 0, 1, 1).IsInside(5, 5));
        Assert.True(r.Overlaps(new CellRect(5, 4, 1, 1)));
        Assert.False(r.Overlaps(new CellRect(6, 3, 1, 1)));
        Assert.False(r.Overlaps(new CellRect(2, 5, 4, 1)));
        Assert.NotEqual(r, new CellRect(2, 3, 4, 3));
    }
}
