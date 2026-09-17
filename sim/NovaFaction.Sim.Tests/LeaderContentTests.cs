using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

/// <summary>The "passive" and "ability" keys of a leader in units.json, and the modifier format.</summary>
public class LeaderContentTests
{
    private static string LeaderBody(string extra) =>
        UnitRosterTests.DefaultUnitBody.Replace("\"slot\": \"bruiser\"", "\"slot\": \"leader\"")
            .Replace("\"isLeader\": false", "\"isLeader\": true") + ",\n" + extra;

    private const string GoodPassive =
        "\"passive\": [{ \"stat\": \"hp\", \"kind\": \"add\", \"value\": -5, \"appliesTo\": \"all\" }]";

    private const string GoodRally =
        "\"ability\": { \"displayName\": \"Cry\", \"type\": \"rally\", \"radius\": 4, \"range\": 6, \"cooldownSeconds\": 20,"
        + " \"durationSeconds\": 5, \"modifiers\": [{ \"stat\": \"attackInterval\", \"kind\": \"multiply\", \"value\": 0.5,"
        + " \"appliesTo\": [\"tank\", \"leader\"] }] }";

    private static UnitDefinition Load(string extra) =>
        UnitRoster.FromJson(UnitRosterTests.OneUnitJson(LeaderBody(extra))).Units[0];

    [Fact]
    public void ShippedWarlord_HasItsPassiveAndWarCry()
    {
        UnitDefinition warlord = TestSim.LoadFantasy().Get("warlord");
        Modifier passive = Assert.Single(warlord.Passive);
        Assert.Equal(ModifierStat.Damage, passive.Stat);
        Assert.Equal(ModifierKind.Multiply, passive.Kind);
        Assert.Equal(Fix.Parse("1.1"), passive.Value);
        Assert.False(passive.AppliesToAll);
        Assert.Equal(new[] { UnitSlot.Bruiser, UnitSlot.Swarm }, passive.AppliesToSlots);

        LeaderAbilityDefinition ability = warlord.Ability!;
        Assert.Equal("War Cry", ability.DisplayName);
        Assert.Equal(AbilityType.Rally, ability.Type);
        Assert.Equal(Fix.FromInt(4), ability.Radius);
        Assert.Equal(Fix.FromInt(6), ability.Range);
        Assert.Equal(Fix.FromInt(20), ability.CooldownSeconds);
        Assert.Equal(Fix.FromInt(5), ability.DurationSeconds);
        Assert.Equal(2, ability.Modifiers.Count);
        Assert.Equal((ModifierStat.Damage, Fix.Parse("1.3")), (ability.Modifiers[0].Stat, ability.Modifiers[0].Value));
        Assert.Equal((ModifierStat.MoveSpeed, Fix.Parse("1.2")), (ability.Modifiers[1].Stat, ability.Modifiers[1].Value));
        Assert.All(ability.Modifiers, m => Assert.True(m.AppliesToAll && m.Kind == ModifierKind.Multiply));

        Assert.All(TestSim.LoadFantasy().Units.Where(u => !u.IsLeader), u =>
        {
            Assert.Empty(u.Passive);
            Assert.Null(u.Ability);
        });
    }

    [Fact]
    public void Leader_WithoutPassiveOrAbility_Loads_AndAllAbilityTypesParse()
    {
        UnitDefinition bare = Load("\"placeholder\": true");
        Assert.Empty(bare.Passive);
        Assert.Null(bare.Ability);

        UnitDefinition rally = Load(GoodPassive + ",\n" + GoodRally);
        Assert.Equal(Fix.FromInt(-5), rally.Passive[0].Value);
        Assert.True(rally.Ability!.Modifiers[0].AppliesTo(UnitSlot.Leader));
        Assert.False(rally.Ability.Modifiers[0].AppliesTo(UnitSlot.Bruiser));

        LeaderAbilityDefinition quake = Load("\"ability\": { \"displayName\": \"Quake\", \"type\": \"areaDamage\", \"radius\": 2,"
            + " \"range\": 0, \"cooldownSeconds\": 0.05, \"damage\": 150 }").Ability!;
        Assert.Equal(AbilityType.AreaDamage, quake.Type);
        Assert.Equal(Fix.FromInt(150), quake.Amount);
        Assert.Equal(SpellBook.DefaultStructureDamageMultiplier, quake.StructureDamageMultiplier);
        Assert.Equal(Fix.Zero, quake.Range);

        LeaderAbilityDefinition quake2 = Load("\"ability\": { \"displayName\": \"Quake\", \"type\": \"areaDamage\", \"radius\": 2,"
            + " \"range\": 1, \"cooldownSeconds\": 1, \"damage\": 150, \"structureDamageMultiplier\": 1 }").Ability!;
        Assert.Equal(Fix.One, quake2.StructureDamageMultiplier);

        LeaderAbilityDefinition heal = Load("\"ability\": { \"displayName\": \"Mend\", \"type\": \"heal\", \"radius\": 2,"
            + " \"range\": 1, \"cooldownSeconds\": 1, \"amount\": 80 }").Ability!;
        Assert.Equal(AbilityType.Heal, heal.Type);
        Assert.Equal(Fix.FromInt(80), heal.Amount);
        Assert.Empty(heal.Modifiers);
    }

    [Theory]
    // Modifier format.
    [InlineData("\"stat\": \"hp\"", "\"stat\": \"armor\"", "modifier stat must be one of")]
    [InlineData("\"kind\": \"add\"", "\"kind\": \"subtract\"", "modifier kind must be one of")]
    [InlineData("\"kind\": \"add\", \"value\": -5", "\"kind\": \"multiply\", \"value\": -5", "between 0 and 100")]
    [InlineData("\"value\": -5", "\"value\": 2000000", "between -1000000")]
    [InlineData("\"appliesTo\": \"all\"", "\"appliesTo\": \"most\"", "\"all\" or a list of slots")]
    [InlineData("\"appliesTo\": \"all\"", "\"appliesTo\": []", "must not be an empty list")]
    [InlineData("\"appliesTo\": \"all\"", "\"appliesTo\": [\"spell\"]", "modifiers change units only")]
    [InlineData("\"appliesTo\": \"all\"", "\"appliesTo\": [\"tank\", \"tank\"]", "twice")]
    [InlineData("\"appliesTo\": \"all\"", "\"appliesTo\": [\"dragon\"]", "appliesTo slot must be one of")]
    [InlineData("\"appliesTo\": \"all\"", "\"appliesTo\": \"all\", \"note\": 1", "unknown key \"note\" in modifier")]
    [InlineData(", \"appliesTo\": \"all\"", "", "missing required key \"appliesTo\"")]
    [InlineData("\"passive\": [", "\"passive\": 3, \"x\": [", "unknown key")]
    // Ability format.
    [InlineData("\"type\": \"rally\"", "\"type\": \"teleport\"", "ability type must be one of")]
    [InlineData("\"radius\": 4", "\"radius\": 0", "radius must be greater than 0")]
    [InlineData("\"range\": 6", "\"range\": -1", "range must not be negative")]
    [InlineData("\"cooldownSeconds\": 20", "\"cooldownSeconds\": 0", "cooldownSeconds must be greater than 0")]
    [InlineData("\"cooldownSeconds\": 20", "\"cooldownSeconds\": 601", "at most 600")]
    [InlineData("\"durationSeconds\": 5", "\"durationSeconds\": 0", "durationSeconds must be greater than 0")]
    [InlineData("\"durationSeconds\": 5, ", "", "missing required key \"durationSeconds\"")]
    [InlineData("\"modifiers\": [{", "\"modifiers\": [], \"x\": [{", "unknown key \"x\"")]
    [InlineData("\"displayName\": \"Cry\"", "\"displayName\": \" \"", "displayName must not be empty")]
    [InlineData("\"durationSeconds\": 5", "\"durationSeconds\": 5, \"amount\": 3", "unknown key \"amount\"")]
    public void InvalidLeaderContent_IsRejected(string original, string replacement, string fragment)
    {
        string body = LeaderBody(GoodPassive + ",\n" + GoodRally);
        Assert.Contains(original, body);
        var ex = Assert.Throws<SimJsonException>(() =>
            UnitRoster.FromJson(UnitRosterTests.OneUnitJson(body.Replace(original, replacement))));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void RallyWithoutModifiers_AndHealWithoutAmount_AndNonLeaderAbility_AreRejected()
    {
        Assert.Contains("at least one modifier", Assert.Throws<SimJsonException>(() => Load(GoodRally.Replace(
            "\"modifiers\": [{ \"stat\": \"attackInterval\", \"kind\": \"multiply\", \"value\": 0.5, \"appliesTo\": [\"tank\", \"leader\"] }]",
            "\"modifiers\": []"))).Message);
        Assert.Contains("missing required key \"amount\"", Assert.Throws<SimJsonException>(() => Load(
            "\"ability\": { \"displayName\": \"Mend\", \"type\": \"heal\", \"radius\": 2, \"range\": 1, \"cooldownSeconds\": 1 }"))
            .Message);
        Assert.Contains("unknown key \"structureDamageMultiplier\"", Assert.Throws<SimJsonException>(() => Load(
            "\"ability\": { \"displayName\": \"Mend\", \"type\": \"heal\", \"radius\": 2, \"range\": 1, \"cooldownSeconds\": 1,"
            + " \"amount\": 3, \"structureDamageMultiplier\": 1 }")).Message);
        string nonLeader = UnitRosterTests.DefaultUnitBody + ",\n" + GoodRally;
        Assert.Contains("only a leader may have an ability", Assert.Throws<SimJsonException>(() =>
            UnitRoster.FromJson(UnitRosterTests.OneUnitJson(nonLeader))).Message);
        string passiveOnly = UnitRosterTests.DefaultUnitBody + ",\n" + GoodPassive;
        Assert.Contains("only a leader may have a passive", Assert.Throws<SimJsonException>(() =>
            UnitRoster.FromJson(UnitRosterTests.OneUnitJson(passiveOnly))).Message);
    }

    [Fact]
    public void ContentHash_CoversPassiveAndAbility()
    {
        string json = TestSim.FantasyUnitsJson();
        ulong original = UnitRoster.FromJson(json).ContentHash;
        Assert.NotEqual(original, UnitRoster.FromJson(json.Replace("\"value\": 1.1", "\"value\": 1.11")).ContentHash);
        Assert.NotEqual(original, UnitRoster.FromJson(json.Replace("[\"bruiser\", \"swarm\"]", "[\"swarm\", \"bruiser\"]")).ContentHash);
        Assert.NotEqual(original, UnitRoster.FromJson(json.Replace("\"cooldownSeconds\": 20", "\"cooldownSeconds\": 21")).ContentHash);
        Assert.NotEqual(original, UnitRoster.FromJson(json.Replace("\"War Cry\"", "\"Battle Cry\"")).ContentHash);
    }

    [Fact]
    public void Setup_RejectsAbilityTimesThatAreNotWholeTicks()
    {
        MatchRules rules = TestSim.Rules();
        foreach (string replacement in new[] { "\"cooldownSeconds\": 20.01", "\"durationSeconds\": 5.01" })
        {
            string key = replacement.Substring(0, replacement.IndexOf(':'));
            string json = TestSim.FantasyUnitsJson();
            int at = json.IndexOf(key, StringComparison.Ordinal);
            int end = json.IndexOf(',', at);
            json = json.Substring(0, at) + replacement + json.Substring(end);
            Deck deck = Deck.Create(TestSim.Cards(UnitRoster.FromJson(json)), TestSim.DefaultDeckIds, 8);
            var ex = Assert.Throws<ArgumentException>(() =>
                new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), deck, deck));
            Assert.Contains("whole number of ticks", ex.Message);
        }
    }
}
