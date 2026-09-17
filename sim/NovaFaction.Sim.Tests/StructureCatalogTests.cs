using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class StructureCatalogTests
{
    // Normalized to LF so edits do not depend on the checkout's line endings.
    private static readonly string ValidJson = @"{
  ""formatVersion"": 1,
  ""structures"": [
    { ""kind"": ""keep"", ""hp"": 4000, ""damage"": 90, ""attackIntervalSeconds"": 1, ""range"": 6,
      ""targets"": ""both"", ""projectileSpeed"": 10, ""destructionBonus"": 1000 },
    { ""kind"": ""tower"", ""hp"": 2500, ""damage"": 80, ""attackIntervalSeconds"": 0.8, ""range"": 7,
      ""targets"": ""ground"", ""projectileSpeed"": 12, ""destructionBonus"": 500, ""placeholder"": true }
  ]
}".Replace("\r\n", "\n");

    [Fact]
    public void ShippedFile_LoadsBothKinds_MarkedAsPlaceholders()
    {
        StructureCatalog catalog = TestSim.LoadStructures();
        foreach (StructureStats stats in new[] { catalog.Keep, catalog.ForwardTower })
        {
            Assert.True(stats.IsPlaceholder, stats.Kind + " numbers are placeholders and must be marked");
            Assert.True(stats.Hp > Fix.Zero);
            Assert.True(stats.Damage > Fix.Zero);
            Assert.True(stats.Range > Fix.Zero);
            Assert.True(stats.ProjectileSpeed > Fix.Zero);
            Assert.True(stats.DestructionBonus > Fix.Zero);
            Assert.Equal(TargetLayer.Both, stats.Targets);
        }
        Assert.Equal(StructureKind.Keep, catalog.Keep.Kind);
        Assert.Equal(StructureKind.ForwardTower, catalog.ForwardTower.Kind);
        Assert.Same(catalog.Keep, catalog.Get(StructureKind.Keep));
        // The Keep is the bigger prize.
        Assert.True(catalog.Keep.Hp > catalog.ForwardTower.Hp);
        Assert.True(catalog.Keep.DestructionBonus > catalog.ForwardTower.DestructionBonus);
    }

    [Fact]
    public void ValidJson_Loads()
    {
        StructureCatalog catalog = StructureCatalog.FromJson(ValidJson);
        Assert.Equal(Fix.FromInt(4000), catalog.Keep.Hp);
        Assert.False(catalog.Keep.IsPlaceholder);
        Assert.Equal(Fix.Parse("0.8"), catalog.ForwardTower.AttackIntervalSeconds);
        Assert.Equal(TargetLayer.Ground, catalog.ForwardTower.Targets);
        Assert.Equal(Fix.FromInt(12), catalog.ForwardTower.ProjectileSpeed);
        Assert.Equal(Fix.FromInt(500), catalog.ForwardTower.DestructionBonus);
        Assert.True(catalog.ForwardTower.IsPlaceholder);
    }

    [Fact]
    public void ContentHash_IgnoresFormattingButNotValues()
    {
        ulong hash = StructureCatalog.FromJson(ValidJson).ContentHash;
        Assert.Equal(hash, StructureCatalog.FromJson(ValidJson.Replace("\n", "\r\n")).ContentHash);
        Assert.Equal(hash, StructureCatalog.FromJson(ValidJson.Replace(", \"placeholder\": true", "")).ContentHash);
        Assert.NotEqual(hash, StructureCatalog.FromJson(ValidJson.Replace("\"range\": 7", "\"range\": 7.5")).ContentHash);
        Assert.NotEqual(hash, StructureCatalog.FromJson(ValidJson.Replace("\"destructionBonus\": 1000", "\"destructionBonus\": 999")).ContentHash);
    }

    [Theory]
    [InlineData("\"kind\": \"tower\"", "\"kind\": \"keep\"", "duplicate entry for kind \"keep\"")]
    [InlineData("\"kind\": \"tower\"", "\"kind\": \"wall\"", "kind must be one of")]
    [InlineData("\"hp\": 4000", "\"hp\": 0", "hp must be greater than 0")]
    [InlineData("\"damage\": 90", "\"damage\": -1", "damage must not be negative")]
    [InlineData("\"attackIntervalSeconds\": 1", "\"attackIntervalSeconds\": 0", "greater than 0")]
    [InlineData("\"range\": 6", "\"range\": 0", "range must be greater than 0")]
    [InlineData("\"targets\": \"both\"", "\"targets\": \"all\"", "targets must be one of")]
    [InlineData("\"projectileSpeed\": 10", "\"projectileSpeed\": 0", "projectileSpeed must be greater than 0")]
    [InlineData("\"destructionBonus\": 1000", "\"destructionBonus\": -5", "destructionBonus must not be negative")]
    [InlineData("\"hp\": 4000,", "", "missing required key \"hp\"")]
    [InlineData("\"hp\": 4000", "\"hp\": 4000, \"armor\": 3", "unknown key \"armor\"")]
    [InlineData("\"formatVersion\": 1", "\"formatVersion\": 2", "formatVersion must be 1")]
    [InlineData("\"formatVersion\": 1", "\"formatVersion\": 1, \"towers\": []", "unknown key \"towers\"")]
    public void InvalidFile_IsRejected(string original, string replacement, string fragment)
    {
        Assert.Contains(original, ValidJson);
        var ex = Assert.Throws<SimJsonException>(() => StructureCatalog.FromJson(ValidJson.Replace(original, replacement)));
        Assert.Contains(fragment, ex.Message);
        Assert.StartsWith("structures.json", ex.Message);
    }

    [Theory]
    [InlineData("{ \"formatVersion\": 1, \"structures\": [] }", "missing entry for kind \"keep\"")]
    [InlineData("{ \"formatVersion\": 1, \"structures\": [ 1 ] }", "must be a JSON object")]
    [InlineData("[]", "must be a JSON object")]
    public void MalformedFiles_AreRejected(string json, string fragment)
    {
        var ex = Assert.Throws<SimJsonException>(() => StructureCatalog.FromJson(json));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void MissingTowerEntry_IsRejected()
    {
        int start = ValidJson.IndexOf(",\n    { \"kind\": \"tower\"", StringComparison.Ordinal);
        int end = ValidJson.IndexOf("}\n  ]", start, StringComparison.Ordinal) + 1;
        string json = ValidJson.Remove(start, end - start);
        var ex = Assert.Throws<SimJsonException>(() => StructureCatalog.FromJson(json));
        Assert.Contains("missing entry for kind \"tower\"", ex.Message);
    }
}
