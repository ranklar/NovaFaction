using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

/// <summary>Shared map fixtures and helpers for the map-layer tests.</summary>
internal static class MapTestData
{
    internal static string TwoLaneJson() => File.ReadAllText(MatchRulesTests.ContentPath(Path.Combine("maps", "twolane.json")));

    /// <summary>The shipped content/maps/twolane.json.</summary>
    internal static MapDefinition LoadTwoLane() => MapDefinition.FromJson(TwoLaneJson(), "twolane.json");

    /// <summary>
    /// A small valid 7x9 map, top row first. Player 0 at the bottom, player 1 at the top,
    /// 180-degree symmetric.
    /// </summary>
    internal static readonly string[] SmallRows =
    {
        "T.KKK.T", // y8
        "...1...", // y7
        "#.....#", // y6
        "C.....M", // y5
        "##...##", // y4
        "M.....C", // y3
        "#.....#", // y2
        "...0...", // y1
        "T.KKK.T", // y0
    };

    // Normalized to LF so string edits in tests do not depend on the checkout's line endings.
    internal static readonly string SmallStructures = @"[
    { ""id"": ""keep_0"", ""kind"": ""keep"", ""owner"": 0, ""footprint"": { ""x"": 2, ""y"": 0, ""width"": 3, ""height"": 1 } },
    { ""id"": ""tower_0_w"", ""kind"": ""tower"", ""owner"": 0, ""footprint"": { ""x"": 0, ""y"": 0, ""width"": 1, ""height"": 1 },
      ""unlocksDeployZone"": { ""x"": 0, ""y"": 1, ""width"": 3, ""height"": 3 } },
    { ""id"": ""tower_0_e"", ""kind"": ""tower"", ""owner"": 0, ""footprint"": { ""x"": 6, ""y"": 0, ""width"": 1, ""height"": 1 },
      ""unlocksDeployZone"": { ""x"": 4, ""y"": 1, ""width"": 3, ""height"": 3 } },
    { ""id"": ""keep_1"", ""kind"": ""keep"", ""owner"": 1, ""footprint"": { ""x"": 2, ""y"": 8, ""width"": 3, ""height"": 1 } },
    { ""id"": ""tower_1_w"", ""kind"": ""tower"", ""owner"": 1, ""footprint"": { ""x"": 0, ""y"": 8, ""width"": 1, ""height"": 1 },
      ""unlocksDeployZone"": { ""x"": 0, ""y"": 5, ""width"": 3, ""height"": 3 } },
    { ""id"": ""tower_1_e"", ""kind"": ""tower"", ""owner"": 1, ""footprint"": { ""x"": 6, ""y"": 8, ""width"": 1, ""height"": 1 },
      ""unlocksDeployZone"": { ""x"": 4, ""y"": 5, ""width"": 3, ""height"": 3 } }
  ]".Replace("\r\n", "\n");

    internal static readonly string SmallZones = @"[
    { ""player"": 0, ""x"": 0, ""y"": 0, ""width"": 7, ""height"": 4 },
    { ""player"": 1, ""x"": 0, ""y"": 5, ""width"": 7, ""height"": 4 }
  ]".Replace("\r\n", "\n");

    // Structure indices in the small map.
    internal const int Keep0 = 0, Tower0W = 1, Tower0E = 2, Keep1 = 3, Tower1W = 4, Tower1E = 5;

    /// <summary>Builds the small map's JSON with any part swapped out.</summary>
    internal static string SmallJson(string[]? rows = null, string? structures = null, string? zones = null,
        string cellSize = "1", string id = "\"small\"", string formatVersion = "1", string extra = "")
    {
        rows ??= SmallRows;
        string grid = "[\n" + string.Join(",\n", rows.Select(r => "    \"" + r + "\"")) + "\n  ]";
        return "{\n  \"formatVersion\": " + formatVersion + ",\n  \"id\": " + id + ",\n  \"cellSize\": " + cellSize
            + ",\n  \"grid\": " + grid + ",\n  \"structures\": " + (structures ?? SmallStructures)
            + ",\n  \"deployZones\": " + (zones ?? SmallZones) + extra + "\n}";
    }

    internal static MapDefinition Small(string cellSize = "1") => MapDefinition.FromJson(SmallJson(cellSize: cellSize), "small.json");

    /// <summary>Small rows with one row (by y, 0 = bottom) replaced.</summary>
    internal static string[] SmallRowsWith(int y, string row)
    {
        string[] rows = (string[])SmallRows.Clone();
        rows[rows.Length - 1 - y] = row;
        return rows;
    }

    /// <summary>
    /// A bare grid from ASCII rows (top row first): '#' blocked, anything else open. Structure
    /// footprints are given separately, in index order.
    /// </summary>
    internal static Grid GridFromRows(string[] rows, CellRect[] footprints, string cellSize = "1")
    {
        int height = rows.Length;
        int width = rows[0].Length;
        var blocked = new bool[width * height];
        for (int r = 0; r < height; r++)
        {
            Assert.Equal(width, rows[r].Length);
            for (int x = 0; x < width; x++)
            {
                blocked[(height - 1 - r) * width + x] = rows[r][x] == '#';
            }
        }
        return new Grid(width, height, Fix.Parse(cellSize), blocked, footprints);
    }

    /// <summary>
    /// Walks the direction field from a cell to the target, checking every step is a legal move that
    /// lowers the remaining cost by exactly its length. Returns the visited cells (start included).
    /// </summary>
    internal static List<CellCoord> FollowPath(Grid grid, FlowField field, CellCoord start)
    {
        var path = new List<CellCoord> { start };
        CellCoord cell = start;
        Fix remaining = field.GetCellDistance(cell);
        Fix travelled = Fix.Zero;
        while (remaining != Fix.Zero)
        {
            Assert.True(path.Count <= grid.CellCount, "path does not terminate");
            CellCoord next = field.GetNextCell(cell);
            int dx = next.X - cell.X, dy = next.Y - cell.Y;
            Assert.True(Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1 && (dx != 0 || dy != 0), "bad step at " + cell);
            Assert.True(grid.IsInBounds(next));
            bool nextIsTarget = field.Target.Contains(next);
            Assert.True(nextIsTarget || grid.IsWalkable(next), "stepped onto blocked cell " + next);
            if (dx != 0 && dy != 0)
            {
                // No corner cutting: both side cells must be open.
                var sideA = new CellCoord(cell.X + dx, cell.Y);
                var sideB = new CellCoord(cell.X, cell.Y + dy);
                Assert.True(field.Target.Contains(sideA) || grid.IsWalkable(sideA), "cut corner at " + sideA);
                Assert.True(field.Target.Contains(sideB) || grid.IsWalkable(sideB), "cut corner at " + sideB);
            }
            Fix step = dx != 0 && dy != 0 ? FlowField.DiagonalCost : Fix.One;
            Fix nextRemaining = field.GetCellDistance(next);
            Assert.Equal(remaining - step, nextRemaining);
            Assert.Equal(new FixVector2(Fix.FromInt(dx), Fix.FromInt(dy)).Normalized, field.GetCellDirection(cell));
            travelled += step;
            remaining = nextRemaining;
            cell = next;
            path.Add(cell);
        }
        Assert.True(field.Target.Contains(cell));
        Assert.Equal(field.GetCellDistance(start), travelled);
        return path;
    }
}
