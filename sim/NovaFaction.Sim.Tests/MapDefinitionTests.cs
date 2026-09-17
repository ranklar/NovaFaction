using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using static NovaFaction.Sim.Tests.MapTestData;

namespace NovaFaction.Sim.Tests;

public class MapDefinitionTests
{
    private static SimJsonException LoadFails(string json)
    {
        return Assert.Throws<SimJsonException>(() => MapDefinition.FromJson(json, "bad.json"));
    }

    private static void AssertFails(string json, string expectedFragment)
    {
        SimJsonException e = LoadFails(json);
        Assert.Contains(expectedFragment, e.Message);
        Assert.Equal("bad.json", e.SourceName);
    }

    // ------------------------------------------------------------ happy path

    [Fact]
    public void SmallMap_LoadsEveryField()
    {
        MapDefinition map = Small();

        Assert.Equal("small", map.Id);
        Assert.Equal(7, map.Width);
        Assert.Equal(9, map.Height);
        Assert.Equal(Fix.One, map.CellSize);

        // First file row is the top of the map.
        Assert.Equal('T', map.GetCell(0, 8));
        Assert.Equal('1', map.GetCell(3, 7));
        Assert.Equal('0', map.GetCell(3, 1));
        Assert.True(map.IsBlockedTerrain(0, 4));
        Assert.False(map.IsBlockedTerrain(2, 4));
        Assert.False(map.IsBlockedTerrain(2, 0)); // a footprint is not terrain
        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetCell(7, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetCell(0, -1));

        Assert.Equal(6, map.Structures.Count);
        StructureDefinition tower = map.Structures[Tower0E];
        Assert.Equal(Tower0E, tower.Index);
        Assert.Equal("tower_0_e", tower.Id);
        Assert.Equal(StructureKind.ForwardTower, tower.Kind);
        Assert.Equal(0, tower.Owner);
        Assert.Equal(new CellRect(6, 0, 1, 1), tower.Footprint);
        Assert.Equal((CellRect?)new CellRect(4, 1, 3, 3), tower.UnlocksDeployZone);
        Assert.Null(map.Structures[Keep1].UnlocksDeployZone);
        Assert.Equal(new CellRect(2, 8, 3, 1), map.GetKeep(1).Footprint);
        Assert.Equal(2, map.GetStructures(1, StructureKind.ForwardTower).Count);

        Assert.Equal(new[] { new CellRect(0, 0, 7, 4) }, map.GetDeployZones(0));
        Assert.Equal(new[] { new CellRect(0, 5, 7, 4) }, map.GetDeployZones(1));
        Assert.Equal(new[] { new CellCoord(3, 1) }, map.GetSpawnPoints(0));
        Assert.Equal(new[] { new CellCoord(3, 7) }, map.GetSpawnPoints(1));
        // Bottom row first, then left to right.
        Assert.Equal(new[] { new CellCoord(0, 3), new CellCoord(6, 5) }, map.Mines);
        Assert.Equal(new[] { new CellCoord(6, 3), new CellCoord(0, 5) }, map.ChestSpawns);
        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetDeployZones(2));
    }

    [Fact]
    public void TwoLane_LoadsWithExpectedLayout()
    {
        MapDefinition map = LoadTwoLane();

        Assert.Equal("twolane", map.Id);
        Assert.Equal(18, map.Width);
        Assert.Equal(32, map.Height);
        Assert.Equal(Fix.One, map.CellSize);
        Assert.Equal(6, map.Structures.Count);
        for (int player = 0; player < 2; player++)
        {
            Assert.Single(map.GetStructures(player, StructureKind.Keep));
            Assert.Equal(2, map.GetStructures(player, StructureKind.ForwardTower).Count);
            Assert.NotEmpty(map.GetSpawnPoints(player));
        }
        // Player 0 owns the bottom half, player 1 the top.
        foreach (StructureDefinition s in map.Structures)
        {
            bool bottom = s.Footprint.YEnd <= map.Height / 2;
            Assert.Equal(bottom ? 0 : 1, s.Owner);
        }
        Assert.Equal(2, map.Mines.Count);
        Assert.Equal(4, map.ChestSpawns.Count);
        // Two chest spawns per lane (west lane x < 9, east lane x >= 9).
        Assert.Equal(2, map.ChestSpawns.Count(c => c.X < 9));
        // Both mines sit in the middle rows.
        Assert.All(map.Mines, m => Assert.InRange(m.Y, 14, 17));
    }

    [Fact]
    public void TwoLane_CenterStripIsBlockedExceptTwoLaneGaps()
    {
        MapDefinition map = LoadTwoLane();
        for (int y = 14; y <= 17; y++)
        {
            var open = Enumerable.Range(0, map.Width).Where(x => !map.IsBlockedTerrain(x, y)).ToArray();
            Assert.Equal(new[] { 2, 3, 4, 5, 12, 13, 14, 15 }, open);
        }
    }

    [Fact]
    public void TwoLane_IsSymmetricUnder180DegreeRotation()
    {
        MapDefinition map = LoadTwoLane();
        int w = map.Width, h = map.Height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                char c = map.GetCell(x, y);
                char expected = c == '0' ? '1' : c == '1' ? '0' : c;
                Assert.Equal(expected, map.GetCell(w - 1 - x, h - 1 - y));
            }
        }

        foreach (StructureDefinition s in map.Structures)
        {
            CellRect rotated = Rotate(s.Footprint, w, h);
            StructureDefinition twin = map.Structures.Single(o => o.Footprint == rotated);
            Assert.Equal(s.Kind, twin.Kind);
            Assert.Equal(1 - s.Owner, twin.Owner);
            if (s.UnlocksDeployZone.HasValue)
            {
                Assert.Equal((CellRect?)Rotate(s.UnlocksDeployZone.Value, w, h), twin.UnlocksDeployZone);
            }
        }
        Assert.Equal(map.GetDeployZones(0).Select(z => Rotate(z, w, h)), map.GetDeployZones(1));
    }

    internal static CellRect Rotate(CellRect r, int w, int h) =>
        new CellRect(w - r.X - r.Width, h - r.Y - r.Height, r.Width, r.Height);

    [Fact]
    public void TwoLane_TowerUnlockZonesAreOnTheOwnersSideAndClearOfStructures()
    {
        MapDefinition map = LoadTwoLane();
        foreach (StructureDefinition tower in map.Structures.Where(s => s.Kind == StructureKind.ForwardTower))
        {
            CellRect zone = tower.UnlocksDeployZone!.Value;
            bool ownerIsBottom = tower.Owner == 0;
            Assert.True(ownerIsBottom ? zone.YEnd <= 16 : zone.Y >= 16);
            Assert.All(map.Structures, s => Assert.False(zone.Overlaps(s.Footprint)));
        }
    }

    [Fact]
    public void ContentHash_IgnoresFormattingButSeesEveryContentChange()
    {
        string json = SmallJson();
        ulong baseline = MapDefinition.FromJson(json).ContentHash;

        Assert.Equal(baseline, MapDefinition.FromJson(json.Replace("\n", "\r\n")).ContentHash);
        Assert.Equal(baseline, MapDefinition.FromJson(json.Replace("  ", "\t")).ContentHash);
        Assert.Equal(baseline, MapDefinition.FromJson(json, "other-name.json").ContentHash);
        Assert.Equal(LoadTwoLane().ContentHash, MapDefinition.FromJson(TwoLaneJson().Replace("\n", "\r\n")).ContentHash);

        var variants = new[]
        {
            SmallJson(id: "\"small2\""),
            SmallJson(cellSize: "1.5"),
            SmallJson(rows: SmallRowsWith(6, "#......")),                      // one cell changed
            SmallJson(rows: SmallRowsWith(3, "C.....M")),                      // markers swapped
            SmallJson(zones: SmallZones.Replace("\"width\": 7, \"height\": 4 },", "\"width\": 6, \"height\": 4 },")),
            SmallJson(structures: SmallStructures.Replace("\"x\": 4, \"y\": 1, \"width\": 3", "\"x\": 4, \"y\": 2, \"width\": 3")),
            SmallJson(structures: SmallStructures.Replace("\"tower_0_w\"", "\"tower_0_west\"")),
        };
        var seen = new HashSet<ulong> { baseline };
        foreach (string variant in variants)
        {
            Assert.True(seen.Add(MapDefinition.FromJson(variant).ContentHash), "content change not detected:\n" + variant);
        }
    }

    // ------------------------------------------------------------ root and header

    [Fact]
    public void Rejects_NonObjectRoot() => AssertFails("[]", "must be a JSON object");

    [Fact]
    public void Rejects_InvalidJson() => AssertFails(SmallJson() + ",", "after the end of the document");

    [Fact]
    public void Rejects_UnknownRootKey() => AssertFails(SmallJson(extra: ",\n  \"legend\": {}"), "unknown key \"legend\"");

    [Theory]
    [InlineData("formatVersion")]
    [InlineData("id")]
    [InlineData("cellSize")]
    [InlineData("grid")]
    [InlineData("structures")]
    [InlineData("deployZones")]
    public void Rejects_MissingRootKey(string key)
    {
        string json = SmallJson().Replace("\"" + key + "\":", "\"removed_" + key + "\":");
        // The renamed key is unknown, so check the missing-key error with the renamed key dropped.
        SimJsonException e = LoadFails(json);
        Assert.Contains("removed_" + key, e.Message);

        JsonValue root = SimJson.Parse(SmallJson());
        var parts = root.Members.Where(m => m.Key != key).Select(m => "\"" + m.Key + "\": " + Reserialize(m.Value));
        AssertFails("{" + string.Join(",", parts) + "}", "missing required key \"" + key + "\"");
    }

    private static string Reserialize(JsonValue v)
    {
        switch (v.Kind)
        {
            case JsonKind.Number: return v.NumberText;
            case JsonKind.String: return "\"" + v.AsString() + "\"";
            case JsonKind.Array: return "[" + string.Join(",", v.AsArray().Select(Reserialize)) + "]";
            case JsonKind.Object: return "{" + string.Join(",", v.Members.Select(m => "\"" + m.Key + "\":" + Reserialize(m.Value))) + "}";
            case JsonKind.Bool: return v.AsBool() ? "true" : "false";
            default: return "null";
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void Rejects_WrongFormatVersion(string version) =>
        AssertFails(SmallJson(formatVersion: version), "formatVersion must be 1");

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"Two Lane\"")]
    [InlineData("\"UPPER\"")]
    [InlineData("5")]
    public void Rejects_BadId(string id)
    {
        SimJsonException e = LoadFails(SmallJson(id: id));
        Assert.True(e.Message.Contains("id must be") || e.Message.Contains("expected a string"), e.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.05")]
    [InlineData("65")]
    public void Rejects_CellSizeOutOfRange(string cellSize) =>
        AssertFails(SmallJson(cellSize: cellSize), "cellSize must be between");

    [Fact]
    public void Rejects_CellSizeWithExponent() => AssertFails(SmallJson(cellSize: "1e0"), "not a valid fixed-point value");

    // ------------------------------------------------------------ grid

    [Fact]
    public void Rejects_EmptyGrid() => AssertFails(SmallJson(rows: Array.Empty<string>()), "at least one row");

    [Fact]
    public void Rejects_EmptyRows() => AssertFails(SmallJson(rows: new[] { "", "" }), "rows must be 1-256 characters");

    [Fact]
    public void Rejects_NonRectangularGrid()
    {
        SimJsonException e = LoadFails(SmallJson(rows: SmallRowsWith(4, "##...#")));
        Assert.Contains("not rectangular: row 5 has 6 characters but row 1 has 7", e.Message);
        Assert.True(e.Line > 0, "error should point at the row");
    }

    [Fact]
    public void Rejects_UnknownGridCharacter()
    {
        SimJsonException e = LoadFails(SmallJson(rows: SmallRowsWith(4, "##.x.##")));
        Assert.Contains("unknown character 'x' at column 4 (cell 3, 4)", e.Message);
        Assert.Equal(10, e.Line); // grid rows start on line 6; y4 is the fifth row
    }

    [Fact]
    public void Rejects_NonStringRow() =>
        AssertFails(SmallJson().Replace("\"##...##\"", "7"), "expected a string");

    [Fact]
    public void Rejects_OversizedGrid()
    {
        string[] rows = Enumerable.Repeat(new string('.', 7), 257).ToArray();
        AssertFails(SmallJson(rows: rows), "maximum is 256");
    }

    // ------------------------------------------------------------ structures

    private static string Structures(string find, string replace)
    {
        Assert.Contains(find, SmallStructures);
        return SmallStructures.Replace(find, replace);
    }

    [Fact]
    public void Rejects_UnknownStructureKind() =>
        AssertFails(SmallJson(structures: Structures("\"kind\": \"keep\", \"owner\": 1", "\"kind\": \"castle\", \"owner\": 1")),
            "kind must be \"keep\" or \"tower\"");

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    public void Rejects_BadOwner(string owner) =>
        AssertFails(SmallJson(structures: Structures("\"owner\": 1, \"footprint\": { \"x\": 2", "\"owner\": " + owner + ", \"footprint\": { \"x\": 2")),
            "owner must be 0 or 1");

    [Fact]
    public void Rejects_TooManyTowersForOnePlayer()
    {
        // tower_1_w changes owner: player 0 owns 3 towers, player 1 owns 1.
        string structures = Structures("\"tower_1_w\", \"kind\": \"tower\", \"owner\": 1", "\"tower_1_w\", \"kind\": \"tower\", \"owner\": 0");
        AssertFails(SmallJson(structures: structures), "player 0 must own exactly 1 keep and 2 towers but owns 1 keep(s) and 3 tower(s)");
    }

    [Fact]
    public void Rejects_TwoKeepsForOnePlayer()
    {
        string structures = Structures("\"keep_1\", \"kind\": \"keep\", \"owner\": 1", "\"keep_1\", \"kind\": \"keep\", \"owner\": 0");
        AssertFails(SmallJson(structures: structures), "player 0 must own exactly 1 keep and 2 towers but owns 2 keep(s)");
    }

    [Fact]
    public void Rejects_MissingKeep()
    {
        // Remove keep_1 from both the list and the grid.
        string structures = SmallStructures.Replace(
            "    { \"id\": \"keep_1\", \"kind\": \"keep\", \"owner\": 1, \"footprint\": { \"x\": 2, \"y\": 8, \"width\": 3, \"height\": 1 } },\n", "");
        Assert.NotEqual(SmallStructures, structures);
        AssertFails(SmallJson(rows: SmallRowsWith(8, "T.....T"), structures: structures),
            "player 1 must own exactly 1 keep and 2 towers but owns 0 keep(s)");
    }

    [Theory]
    [InlineData("\"x\": 6, \"y\": 8, \"width\": 1, \"height\": 1", "\"x\": 6, \"y\": 8, \"width\": 2, \"height\": 1")] // past right edge
    [InlineData("\"x\": 6, \"y\": 8, \"width\": 1, \"height\": 1", "\"x\": 6, \"y\": 8, \"width\": 1, \"height\": 2")] // past top edge
    [InlineData("\"x\": 6, \"y\": 8, \"width\": 1, \"height\": 1", "\"x\": -1, \"y\": 8, \"width\": 1, \"height\": 1")]
    [InlineData("\"x\": 6, \"y\": 8, \"width\": 1, \"height\": 1", "\"x\": 6, \"y\": 8, \"width\": 0, \"height\": 1")]
    [InlineData("\"x\": 4, \"y\": 5, \"width\": 3, \"height\": 3", "\"x\": 4, \"y\": 5, \"width\": 3, \"height\": 5")] // unlock zone
    public void Rejects_RectangleOutOfBounds(string find, string replace) =>
        AssertFails(SmallJson(structures: Structures(find, replace)), "must have a positive size and lie inside the 7x9 grid");

    [Fact]
    public void Rejects_RectangleWithUnknownOrMissingKey()
    {
        AssertFails(SmallJson(structures: Structures("\"x\": 2, \"y\": 8, \"width\": 3, \"height\": 1", "\"x\": 2, \"y\": 8, \"w\": 3, \"height\": 1")),
            "unknown key \"w\"");
        AssertFails(SmallJson(structures: Structures("\"x\": 2, \"y\": 8, \"width\": 3, \"height\": 1", "\"x\": 2, \"y\": 8, \"width\": 3")),
            "missing required key \"height\"");
    }

    [Fact]
    public void Rejects_OverlappingFootprints()
    {
        // tower_1_w grows right over keep_1's cells (checked before the grid letters).
        AssertFails(SmallJson(structures: Structures("\"x\": 0, \"y\": 8, \"width\": 1", "\"x\": 0, \"y\": 8, \"width\": 3")),
            "footprint of \"tower_1_w\" overlaps \"keep_1\"");
    }

    [Fact]
    public void Rejects_FootprintOnBlockedCell() =>
        AssertFails(SmallJson(rows: SmallRowsWith(8, "T.KK#.T")), "footprint of \"keep_1\" covers blocked cell (4, 8)");

    [Fact]
    public void Rejects_FootprintOnGroundCell() =>
        AssertFails(SmallJson(rows: SmallRowsWith(8, "T.KK..T")), "covers cell (4, 8) but the grid shows '.' there; draw the footprint with 'K'");

    [Fact]
    public void Rejects_FootprintDrawnWithWrongLetter() =>
        AssertFails(SmallJson(rows: SmallRowsWith(8, "K.KKK.T")), "covers cell (0, 8) but the grid shows 'K' there; draw the footprint with 'T'");

    [Fact]
    public void Rejects_SpawnPointInsideFootprint()
    {
        AssertFails(SmallJson(rows: SmallRowsWith(8, "T.KMK.T")), "spawn point 'M' at cell (3, 8) is inside the footprint of \"keep_1\"");
        AssertFails(SmallJson(rows: SmallRowsWith(0, "T.K0K.T")), "spawn point '0' at cell (3, 0) is inside the footprint of \"keep_0\"");
    }

    [Fact]
    public void Rejects_StructureLetterWithoutFootprint() =>
        AssertFails(SmallJson(rows: SmallRowsWith(6, "#..T..#")), "grid cell (3, 6) is 'T' but no tower footprint");

    [Fact]
    public void Rejects_DuplicateStructureId() =>
        AssertFails(SmallJson(structures: Structures("\"id\": \"tower_1_e\"", "\"id\": \"tower_1_w\"")), "structure id \"tower_1_w\" is used twice");

    [Fact]
    public void Rejects_TowerWithoutUnlockZone()
    {
        string structures = Structures(
            "\"x\": 6, \"y\": 8, \"width\": 1, \"height\": 1 },\n      \"unlocksDeployZone\": { \"x\": 4, \"y\": 5, \"width\": 3, \"height\": 3 } }",
            "\"x\": 6, \"y\": 8, \"width\": 1, \"height\": 1 } }");
        AssertFails(SmallJson(structures: structures), "tower \"tower_1_e\" needs an \"unlocksDeployZone\"");
    }

    [Fact]
    public void Rejects_KeepWithUnlockZone()
    {
        string structures = Structures(
            "\"x\": 2, \"y\": 8, \"width\": 3, \"height\": 1 } }",
            "\"x\": 2, \"y\": 8, \"width\": 3, \"height\": 1 }, \"unlocksDeployZone\": { \"x\": 0, \"y\": 0, \"width\": 1, \"height\": 1 } }");
        AssertFails(SmallJson(structures: structures), "only towers have \"unlocksDeployZone\"; \"keep_1\" is a keep");
    }

    [Fact]
    public void Rejects_StructureNotAnObject() => AssertFails(SmallJson(structures: "[ 1 ]"), "each structure must be an object");

    // ------------------------------------------------------------ deploy zones and markers

    [Fact]
    public void Rejects_PlayerWithoutDeployZone() =>
        AssertFails(SmallJson(zones: "[ { \"player\": 0, \"x\": 0, \"y\": 0, \"width\": 7, \"height\": 4 } ]"), "player 1 has no deploy zone");

    [Fact]
    public void Rejects_DeployZoneOutOfBounds() =>
        AssertFails(SmallJson(zones: SmallZones.Replace("\"y\": 5, \"width\": 7, \"height\": 4", "\"y\": 5, \"width\": 7, \"height\": 5")),
            "deploy zone of player 1 {x 0, y 5, 7x5} must have a positive size and lie inside the 7x9 grid");

    [Fact]
    public void Rejects_DeployZoneForUnknownPlayer() =>
        AssertFails(SmallJson(zones: SmallZones.Replace("\"player\": 1", "\"player\": 2")), "deploy zone player must be 0 or 1");

    [Fact]
    public void Rejects_DeployZoneWithUnknownKey() =>
        AssertFails(SmallJson(zones: SmallZones.Replace("\"player\": 1,", "\"player\": 1, \"team\": 1,")), "unknown key \"team\" in deploy zone");

    [Fact]
    public void Rejects_MissingSpawnMarker() =>
        AssertFails(SmallJson(rows: SmallRowsWith(7, ".......")), "at least one '1' spawn marker for player 1");

    [Fact]
    public void Rejects_SpawnMarkerOutsideOwnDeployZone() =>
        AssertFails(SmallJson(rows: SmallRowsWith(7, ".......").Select((r, i) => i == 5 ? "M.1...C" : r).ToArray()),
            "spawn marker '1' at (2, 3) is outside player 1's deploy zones");

    [Fact]
    public void Rejects_StructureUnreachableFromASpawn()
    {
        // Seal off the bottom half: player 1 cannot reach player 0's structures.
        AssertFails(SmallJson(rows: SmallRowsWith(4, "#######")),
            "structure \"keep_0\" cannot be reached from player 1's spawn marker at (3, 7)");
    }

    [Fact]
    public void Accepts_OneCellGap()
    {
        // A one-cell gap is enough; the path goes straight through it.
        MapDefinition map = MapDefinition.FromJson(SmallJson(rows: SmallRowsWith(4, "###.###")));
        Assert.True(map.IsBlockedTerrain(2, 4));
    }

    [Fact]
    public void Rejects_PathThatOnlyExistsByCuttingACorner()
    {
        // Row 4 and 5 leave only diagonally touching openings: (3,4) and (4,5) with (4,4) and (3,5) blocked.
        string[] rows = SmallRowsWith(4, "###.###");
        rows = rows.Select((r, i) => i == 3 ? "C###.#M" : r).ToArray(); // y5
        AssertFails(SmallJson(rows: rows), "cannot be reached");
    }
}
