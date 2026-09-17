using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class MatchRulesTests
{
    // Normalized to LF so line numbers and edits do not depend on the checkout's line endings.
    private static readonly string ValidJson = @"{
  ""ticksPerSecond"": 20,
  ""matchLengthSeconds"": 180,
  ""suddenDeathSeconds"": 60,
  ""goldBaseIncomePerSecond"": 0.35,
  ""goldStartingAmount"": 5,
  ""goldCap"": 10,
  ""deploySpawnDelaySeconds"": 1,
  ""handSize"": 4,
  ""deckSize"": 8,
  ""unitSeparationDistance"": 0.6,
  ""unitSeparationPushPerSecond"": 1.5,
  ""unitSpawnSpacing"": 0.5
}".Replace("\r\n", "\n");

    internal static string ContentPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "content", fileName);

    /// <summary>The shipped content/rules.json.</summary>
    internal static MatchRules LoadShippedRules() =>
        MatchRules.FromJson(File.ReadAllText(ContentPath("rules.json")));

    [Fact]
    public void ShippedRulesFile_LoadsWithDesignValues()
    {
        MatchRules rules = LoadShippedRules();

        Assert.Equal(20, rules.TicksPerSecond);
        Assert.Equal(180, rules.MatchLengthSeconds);
        Assert.Equal(60, rules.SuddenDeathSeconds);
        Assert.Equal(Fix.FromInt(10), rules.GoldCap);
        Assert.Equal(Fix.One, rules.DeploySpawnDelaySeconds);
        Assert.Equal(4, rules.HandSize);
        Assert.Equal(8, rules.DeckSize);
        Assert.True(rules.GoldBaseIncomePerSecond > Fix.Zero);
        Assert.True(rules.GoldStartingAmount <= rules.GoldCap);

        Assert.Equal(3600, rules.MatchLengthTicks);
        Assert.Equal(1200, rules.SuddenDeathTicks);
        Assert.Equal(20, rules.DeploySpawnDelayTicks);

        // Values the design doc does not set are flagged in the file.
        Assert.Equal(new[]
        {
            "goldBaseIncomePerSecond", "goldStartingAmount",
            "unitSeparationDistance", "unitSeparationPushPerSecond", "unitSpawnSpacing",
        }, rules.TuningPlaceholders);
        Assert.True(rules.UnitSeparationDistance > Fix.Zero);
    }

    [Fact]
    public void ValidJson_Loads()
    {
        MatchRules rules = MatchRules.FromJson(ValidJson);
        Assert.Equal(Fix.Parse("0.35"), rules.GoldBaseIncomePerSecond);
        Assert.Equal(Fix.FromInt(5), rules.GoldStartingAmount);
        Assert.Empty(rules.TuningPlaceholders);
    }

    [Fact]
    public void UnknownKey_IsRejectedWithItsPosition()
    {
        string json = ValidJson.Replace("\"handSize\": 4,", "\"handSize\": 4,\n  \"handsize\": 4,");
        var ex = Assert.Throws<SimJsonException>(() => MatchRules.FromJson(json));
        Assert.Contains("unknown key \"handsize\"", ex.Message);
        Assert.Equal(10, ex.Line);
        Assert.StartsWith("rules.json (line 10,", ex.Message);
    }

    [Theory]
    [InlineData("ticksPerSecond")]
    [InlineData("matchLengthSeconds")]
    [InlineData("suddenDeathSeconds")]
    [InlineData("goldBaseIncomePerSecond")]
    [InlineData("goldStartingAmount")]
    [InlineData("goldCap")]
    [InlineData("deploySpawnDelaySeconds")]
    [InlineData("handSize")]
    [InlineData("deckSize")]
    [InlineData("unitSeparationDistance")]
    [InlineData("unitSeparationPushPerSecond")]
    [InlineData("unitSpawnSpacing")]
    public void MissingKey_IsRejected(string key)
    {
        string json = string.Join("\n", ValidJson.Split('\n').Where(line => !line.Contains("\"" + key + "\"")));
        json = json.Replace(",\n}", "\n}"); // keep the JSON valid when the last key goes
        var ex = Assert.Throws<SimJsonException>(() => MatchRules.FromJson(json));
        Assert.Contains("missing required key \"" + key + "\"", ex.Message);
    }

    [Theory]
    [InlineData("\"ticksPerSecond\": 20", "\"ticksPerSecond\": 0", "between 1 and")]
    [InlineData("\"ticksPerSecond\": 20", "\"ticksPerSecond\": 20.5", "whole number")]
    [InlineData("\"ticksPerSecond\": 20", "\"ticksPerSecond\": \"20\"", "expected a number but found a string")]
    [InlineData("\"matchLengthSeconds\": 180", "\"matchLengthSeconds\": -1", "between 1 and")]
    [InlineData("\"suddenDeathSeconds\": 60", "\"suddenDeathSeconds\": -1", "between 0 and")]
    [InlineData("\"goldCap\": 10", "\"goldCap\": 0", "at least")]
    [InlineData("\"goldCap\": 10", "\"goldCap\": 4", "goldStartingAmount must not exceed goldCap")]
    [InlineData("\"goldCap\": 10", "\"goldCap\": 1e1", "fixed-point")]
    [InlineData("\"goldCap\": 10", "\"goldCap\": null", "expected a number but found null")]
    [InlineData("\"goldStartingAmount\": 5", "\"goldStartingAmount\": -1", "at least")]
    [InlineData("\"goldBaseIncomePerSecond\": 0.35", "\"goldBaseIncomePerSecond\": -0.1", "at least")]
    [InlineData("\"goldBaseIncomePerSecond\": 0.35", "\"goldBaseIncomePerSecond\": 11", "must not exceed goldCap")]
    [InlineData("\"deploySpawnDelaySeconds\": 1", "\"deploySpawnDelaySeconds\": 0.01", "whole number of ticks")]
    [InlineData("\"handSize\": 4", "\"handSize\": 8", "smaller than deckSize")]
    [InlineData("\"deckSize\": 8", "\"deckSize\": 0", "between 1 and")]
    [InlineData("\"unitSeparationDistance\": 0.6", "\"unitSeparationDistance\": 0", "at least")]
    [InlineData("\"unitSeparationPushPerSecond\": 1.5", "\"unitSeparationPushPerSecond\": -1", "at least")]
    [InlineData("\"unitSpawnSpacing\": 0.5", "\"unitSpawnSpacing\": 65", "at most 64")]
    public void InvalidValues_AreRejected(string original, string replacement, string fragment)
    {
        Assert.Contains(original, ValidJson);
        var ex = Assert.Throws<SimJsonException>(() => MatchRules.FromJson(ValidJson.Replace(original, replacement)));
        Assert.Contains(fragment, ex.Message);
        Assert.True(ex.Line > 0, "error should point at the offending value");
    }

    [Fact]
    public void SpawnDelay_AcceptsWholeTickFractions()
    {
        string json = ValidJson.Replace("\"deploySpawnDelaySeconds\": 1", "\"deploySpawnDelaySeconds\": 0.25");
        Assert.Equal(5, MatchRules.FromJson(json).DeploySpawnDelayTicks);
    }

    [Theory]
    [InlineData("[\"goldCap\", \"nope\"]", "unknown key \"nope\"")]
    [InlineData("[\"goldCap\", \"goldCap\"]", "twice")]
    [InlineData("[\"tuningPlaceholders\"]", "unknown key")]
    [InlineData("[1]", "expected a string")]
    [InlineData("\"goldCap\"", "expected an array")]
    public void InvalidPlaceholderList_IsRejected(string list, string fragment)
    {
        string json = ValidJson.Replace("\"deckSize\": 8", "\"deckSize\": 8,\n  \"tuningPlaceholders\": " + list);
        var ex = Assert.Throws<SimJsonException>(() => MatchRules.FromJson(json));
        Assert.Contains(fragment, ex.Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"ticksPerSecond\": 20,}")]
    public void NonObjectOrMalformedDocument_IsRejected(string json)
    {
        Assert.Throws<SimJsonException>(() => MatchRules.FromJson(json));
    }
}
