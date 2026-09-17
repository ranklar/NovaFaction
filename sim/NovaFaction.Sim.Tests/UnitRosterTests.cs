using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class UnitRosterTests
{
    /// <summary>A one-unit file; tests swap parts of the unit object.</summary>
    internal static string OneUnitJson(string unitBody = DefaultUnitBody, string root = "\"formatVersion\": 1, \"faction\": \"testers\"") =>
        "{\n  " + root + ",\n  \"units\": [\n    {\n" + unitBody + "\n    }\n  ]\n}";

    internal const string DefaultUnitBody =
        "      \"id\": \"grunt\",\n"
        + "      \"displayName\": \"Grunt\",\n"
        + "      \"slot\": \"bruiser\",\n"
        + "      \"cost\": 3,\n"
        + "      \"hp\": 100,\n"
        + "      \"damage\": 10,\n"
        + "      \"attackIntervalSeconds\": 1,\n"
        + "      \"range\": 0.5,\n"
        + "      \"moveSpeed\": 1,\n"
        + "      \"targets\": \"ground\",\n"
        + "      \"isFlying\": false,\n"
        + "      \"spawnCount\": 1,\n"
        + "      \"isLeader\": false";

    [Fact]
    public void ShippedFantasyUnits_LoadAndCoverTheRequestedArchetypes()
    {
        UnitRoster roster = TestSim.LoadFantasy();

        Assert.Equal("fantasy", roster.Faction);
        Assert.Equal(8, roster.Units.Count);
        Assert.Equal(TestSim.DefaultDeckIds, roster.Units.Select(u => u.Id));
        Assert.Equal(new[]
        {
            UnitSlot.Tank, UnitSlot.Bruiser, UnitSlot.Swarm, UnitSlot.Ranged,
            UnitSlot.Flyer, UnitSlot.Siege, UnitSlot.Spell, UnitSlot.Leader,
        }, roster.Units.Select(u => u.Slot));
        Assert.Single(roster.Units, u => u.IsLeader);
        Assert.All(roster.Units, u =>
        {
            Assert.True(u.IsPlaceholder, u.Id + " numbers are placeholders and must be marked");
            Assert.InRange(u.Cost, 1, 7);
            Assert.True(u.Hp > Fix.Zero);
            Assert.True(u.Range > Fix.Zero);
            Assert.False(string.IsNullOrWhiteSpace(u.DisplayName));
        });

        UnitDefinition goblins = roster.Get("goblin_pack");
        Assert.True(goblins.SpawnCount > 1);
        Assert.All(roster.Units.Where(u => u.Slot != UnitSlot.Swarm), u => Assert.Equal(1, u.SpawnCount));

        UnitDefinition griffin = roster.Get("griffin");
        Assert.True(griffin.IsFlying);
        Assert.Equal(TargetLayer.Both, griffin.Targets);
        Assert.Equal(Fix.Parse("1.3"), griffin.AttackIntervalSeconds);
        Assert.Equal(Fix.Parse("1.5"), griffin.MoveSpeed);

        Assert.Equal(TargetLayer.Ground, roster.Get("catapult").Targets);
        Assert.Equal(5, roster.Get("catapult").Cost);
        Assert.Equal(Fix.Parse("6.5"), roster.Get("catapult").Range);
    }

    [Fact]
    public void Lookup_ByIdAndUnknownId()
    {
        UnitRoster roster = TestSim.LoadFantasy();
        Assert.True(roster.TryGet("knight", out UnitDefinition knight));
        Assert.Equal(1, knight.Index);
        Assert.False(roster.TryGet("dragon", out _));
        Assert.Throws<KeyNotFoundException>(() => roster.Get("dragon"));
    }

    [Fact]
    public void MinimalFile_Loads()
    {
        UnitRoster roster = UnitRoster.FromJson(OneUnitJson());
        UnitDefinition grunt = Assert.Single(roster.Units);
        Assert.Equal("grunt", grunt.Id);
        Assert.False(grunt.IsPlaceholder); // "placeholder" is optional
        Assert.False(grunt.IsLeader);
    }

    [Fact]
    public void ContentHash_IgnoresFormattingButNotValues()
    {
        string json = OneUnitJson();
        ulong hash = UnitRoster.FromJson(json).ContentHash;
        Assert.Equal(hash, UnitRoster.FromJson(json.Replace("\n", "\r\n")).ContentHash);
        Assert.NotEqual(hash, UnitRoster.FromJson(json.Replace("\"hp\": 100", "\"hp\": 100.5")).ContentHash);
        Assert.NotEqual(hash, UnitRoster.FromJson(json.Replace("\"Grunt\"", "\"Grunt2\"")).ContentHash);
    }

    [Fact]
    public void DuplicateIds_AreRejected()
    {
        string unit = "    {\n" + DefaultUnitBody + "\n    }";
        string json = "{ \"formatVersion\": 1, \"faction\": \"testers\", \"units\": [\n" + unit + ",\n" + unit + "\n] }";
        var ex = Assert.Throws<SimJsonException>(() => UnitRoster.FromJson(json, "dupes.json"));
        Assert.Contains("duplicate unit id \"grunt\"", ex.Message);
        Assert.StartsWith("dupes.json (line", ex.Message);
    }

    [Theory]
    [InlineData("\"slot\": \"bruiser\"", "\"slot\": \"wizard\"", "slot must be one of")]
    [InlineData("\"slot\": \"bruiser\"", "\"slot\": \"Bruiser\"", "slot must be one of")]
    [InlineData("\"cost\": 3", "\"cost\": 0", "cost must be between 1 and 7")]
    [InlineData("\"cost\": 3", "\"cost\": 8", "cost must be between 1 and 7")]
    [InlineData("\"cost\": 3", "\"cost\": 3.5", "whole number")]
    [InlineData("\"hp\": 100", "\"hp\": 0", "hp must be greater than 0")]
    [InlineData("\"hp\": 100", "\"hp\": 2000000", "at most")]
    [InlineData("\"damage\": 10", "\"damage\": -1", "damage must not be negative")]
    [InlineData("\"attackIntervalSeconds\": 1", "\"attackIntervalSeconds\": 0", "greater than 0")]
    [InlineData("\"range\": 0.5", "\"range\": 0", "range must be greater than 0")]
    [InlineData("\"moveSpeed\": 1", "\"moveSpeed\": -1", "moveSpeed must not be negative")]
    [InlineData("\"targets\": \"ground\"", "\"targets\": \"sea\"", "targets must be one of")]
    [InlineData("\"isFlying\": false", "\"isFlying\": 0", "expected")]
    [InlineData("\"spawnCount\": 1", "\"spawnCount\": 0", "spawnCount must be between 1 and")]
    [InlineData("\"spawnCount\": 1", "\"spawnCount\": 26", "spawnCount must be between 1 and")]
    [InlineData("\"isLeader\": false", "\"isLeader\": true", "isLeader must be true exactly when slot is")]
    [InlineData("\"slot\": \"bruiser\"", "\"slot\": \"leader\"", "isLeader must be true exactly when")]
    [InlineData("\"id\": \"grunt\"", "\"id\": \"Grunt\"", "id must be")]
    [InlineData("\"displayName\": \"Grunt\"", "\"displayName\": \"  \"", "displayName must not be empty")]
    [InlineData("\"hp\": 100", "\"hp\": 100, \"armor\": 1", "unknown key \"armor\"")]
    [InlineData("\"hp\": 100,", "", "missing required key \"hp\"")]
    [InlineData("\"isLeader\": false", "\"isLeader\": false, \"placeholder\": \"yes\"", "expected")]
    public void InvalidUnit_IsRejected(string original, string replacement, string fragment)
    {
        string json = OneUnitJson();
        Assert.Contains(original, json);
        var ex = Assert.Throws<SimJsonException>(() => UnitRoster.FromJson(json.Replace(original, replacement)));
        Assert.Contains(fragment, ex.Message);
        Assert.True(ex.Line > 0, "error should point into the file");
    }

    [Theory]
    [InlineData("\"formatVersion\": 2, \"faction\": \"testers\"", "formatVersion must be 1")]
    [InlineData("\"formatVersion\": 1, \"faction\": \"Test Faction\"", "faction must be")]
    [InlineData("\"formatVersion\": 1", "missing required key \"faction\"")]
    [InlineData("\"formatVersion\": 1, \"faction\": \"testers\", \"extra\": 1", "unknown key \"extra\"")]
    public void InvalidRoot_IsRejected(string root, string fragment)
    {
        var ex = Assert.Throws<SimJsonException>(() => UnitRoster.FromJson(OneUnitJson(root: root)));
        Assert.Contains(fragment, ex.Message);
    }

    [Theory]
    [InlineData("{ \"formatVersion\": 1, \"faction\": \"x\", \"units\": [] }", "must not be empty")]
    [InlineData("{ \"formatVersion\": 1, \"faction\": \"x\", \"units\": [ 5 ] }", "must be a JSON object")]
    [InlineData("[]", "must be a JSON object")]
    public void MalformedFiles_AreRejected(string json, string fragment)
    {
        var ex = Assert.Throws<SimJsonException>(() => UnitRoster.FromJson(json));
        Assert.Contains(fragment, ex.Message);
    }
}
