using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

/// <summary>spells.json loading, the combined card catalog, and decks that mix unit and spell cards.</summary>
public class SpellBookTests
{
    /// <summary>A one-spell file; tests swap parts of the spell object.</summary>
    internal static string OneSpellJson(string spellBody = DefaultSpellBody, string faction = "testers") =>
        "{\n  \"formatVersion\": 1, \"faction\": \"" + faction + "\",\n  \"spells\": [\n    {\n" + spellBody + "\n    }\n  ]\n}";

    internal const string DefaultSpellBody =
        "      \"id\": \"zap\",\n"
        + "      \"displayName\": \"Zap\",\n"
        + "      \"cost\": 2,\n"
        + "      \"radius\": 1.5,\n"
        + "      \"damage\": 100,\n"
        + "      \"castDelaySeconds\": 0.5,\n"
        + "      \"durationSeconds\": 0,\n"
        + "      \"targets\": \"both\"";

    private const string ZoneBody =
        "      \"id\": \"storm\",\n"
        + "      \"displayName\": \"Storm\",\n"
        + "      \"cost\": 3,\n"
        + "      \"radius\": 2,\n"
        + "      \"damage\": 10,\n"
        + "      \"castDelaySeconds\": 0,\n"
        + "      \"durationSeconds\": 3,\n"
        + "      \"zoneTickSeconds\": 0.5,\n"
        + "      \"targets\": \"ground\"";

    [Fact]
    public void ShippedFantasySpells_LoadAsFireballAndAZone()
    {
        SpellBook book = TestSim.LoadFantasySpells();
        Assert.Equal("fantasy", book.Faction);
        Assert.Equal(new[] { "fireball", "blizzard" }, book.Spells.Select(s => s.Id));
        Assert.All(book.Spells, s =>
        {
            Assert.True(s.IsPlaceholder, s.Id + " numbers are placeholders and must be marked");
            Assert.Equal(CardKind.Spell, s.Kind);
            Assert.Equal(UnitSlot.Spell, s.Slot);
            Assert.False(s.IsLeader);
            Assert.InRange(s.Cost, 1, 7);
            Assert.Equal(Fix.Parse("0.35"), s.StructureDamageMultiplier);
            Assert.Equal(TargetLayer.Both, s.Targets);
        });

        SpellDefinition fireball = book.Get("fireball");
        Assert.False(fireball.IsZone);
        Assert.Equal(Fix.Zero, fireball.DurationSeconds);
        Assert.Equal(Fix.Zero, fireball.ZoneTickSeconds);
        Assert.True(fireball.CastDelaySeconds > Fix.Zero);

        SpellDefinition blizzard = book.Get("blizzard");
        Assert.True(blizzard.IsZone);
        Assert.True(blizzard.ZoneTickSeconds > Fix.Zero && blizzard.ZoneTickSeconds <= blizzard.DurationSeconds);

        // The shipped deck keeps 8 cards with one leader: seven units plus Fireball.
        CardCatalog cards = TestSim.LoadFantasyCards();
        Deck deck = TestSim.DefaultDeck(MatchRulesTests.LoadShippedRules(), cards);
        Assert.Equal(8, deck.Cards.Count);
        Assert.Equal("warlord", deck.Leader.Id);
        Assert.Equal(new[] { "fireball" }, deck.Cards.Where(c => c.Kind == CardKind.Spell).Select(c => c.Id));
    }

    [Fact]
    public void MinimalSpell_Loads_WithTheDefaultStructureMultiplier()
    {
        SpellDefinition zap = Assert.Single(SpellBook.FromJson(OneSpellJson()).Spells);
        Assert.Equal("zap", zap.Id);
        Assert.Equal(2, zap.Cost);
        Assert.Equal(Fix.Parse("1.5"), zap.Radius);
        Assert.Equal(Fix.FromInt(100), zap.Damage);
        Assert.Equal(Fix.Half, zap.CastDelaySeconds);
        Assert.False(zap.IsZone);
        Assert.False(zap.IsPlaceholder);
        Assert.Equal(SpellBook.DefaultStructureDamageMultiplier, zap.StructureDamageMultiplier);
        // 0.35 is not exact in Q48.16, so the product is a hair above 35; it is the same on every machine.
        Assert.Equal(Fix.FromInt(100) * Fix.Parse("0.35"), zap.StructureDamage);
        Assert.True(Fix.Abs(zap.StructureDamage - Fix.FromInt(35)) < Fix.Parse("0.001"));

        SpellDefinition storm = SpellBook.FromJson(OneSpellJson(ZoneBody)).Spells[0];
        Assert.True(storm.IsZone);
        Assert.Equal(Fix.Half, storm.ZoneTickSeconds);
        Assert.Equal(TargetLayer.Ground, storm.Targets);

        SpellDefinition custom = SpellBook.FromJson(OneSpellJson(DefaultSpellBody + ",\n      \"structureDamageMultiplier\": 1")).Spells[0];
        Assert.Equal(Fix.FromInt(100), custom.StructureDamage);

        // An empty spell list is fine: a faction may have no spells.
        Assert.Empty(SpellBook.FromJson("{ \"formatVersion\": 1, \"faction\": \"x\", \"spells\": [] }").Spells);
    }

    [Theory]
    [InlineData("\"cost\": 2", "\"cost\": 0", "cost must be between 1 and 7")]
    [InlineData("\"cost\": 2", "\"cost\": 8", "cost must be between 1 and 7")]
    [InlineData("\"radius\": 1.5", "\"radius\": 0", "radius must be greater than 0")]
    [InlineData("\"damage\": 100", "\"damage\": -5", "damage must not be negative")]
    [InlineData("\"castDelaySeconds\": 0.5", "\"castDelaySeconds\": -1", "castDelaySeconds must not be negative")]
    [InlineData("\"castDelaySeconds\": 0.5", "\"castDelaySeconds\": 601", "at most 600")]
    [InlineData("\"durationSeconds\": 0", "\"durationSeconds\": -1", "durationSeconds must not be negative")]
    [InlineData("\"durationSeconds\": 0", "\"durationSeconds\": 2", "needs \"zoneTickSeconds\"")]
    [InlineData("\"durationSeconds\": 0", "\"durationSeconds\": 0, \"zoneTickSeconds\": 1", "only allowed when durationSeconds")]
    [InlineData("\"durationSeconds\": 0", "\"durationSeconds\": 1, \"zoneTickSeconds\": 0", "zoneTickSeconds must be greater than 0")]
    [InlineData("\"durationSeconds\": 0", "\"durationSeconds\": 1, \"zoneTickSeconds\": 2", "must not exceed durationSeconds")]
    [InlineData("\"targets\": \"both\"", "\"targets\": \"sea\"", "targets must be one of")]
    [InlineData("\"targets\": \"both\"", "\"targets\": \"both\", \"structureDamageMultiplier\": -0.5", "must not be negative")]
    [InlineData("\"targets\": \"both\"", "\"targets\": \"both\", \"slot\": \"spell\"", "unknown key \"slot\"")]
    [InlineData("\"radius\": 1.5,", "", "missing required key \"radius\"")]
    [InlineData("\"id\": \"zap\"", "\"id\": \"Zap!\"", "id must be")]
    [InlineData("\"displayName\": \"Zap\"", "\"displayName\": \"\"", "displayName must not be empty")]
    public void InvalidSpell_IsRejected(string original, string replacement, string fragment)
    {
        string json = OneSpellJson();
        Assert.Contains(original, json);
        var ex = Assert.Throws<SimJsonException>(() => SpellBook.FromJson(json.Replace(original, replacement), "bad.json"));
        Assert.Contains(fragment, ex.Message);
        Assert.StartsWith("bad.json", ex.Message);
        Assert.True(ex.Line > 0, "error should point into the file");
    }

    [Fact]
    public void DuplicateSpellIds_AreRejected()
    {
        string spell = "    {\n" + DefaultSpellBody + "\n    }";
        string json = "{ \"formatVersion\": 1, \"faction\": \"testers\", \"spells\": [\n" + spell + ",\n" + spell + "\n] }";
        var ex = Assert.Throws<SimJsonException>(() => SpellBook.FromJson(json));
        Assert.Contains("duplicate spell id \"zap\"", ex.Message);
    }

    [Fact]
    public void ContentHash_IgnoresFormattingButNotValues()
    {
        string json = OneSpellJson();
        ulong hash = SpellBook.FromJson(json).ContentHash;
        Assert.Equal(hash, SpellBook.FromJson(json.Replace("\n", "\r\n")).ContentHash);
        Assert.NotEqual(hash, SpellBook.FromJson(json.Replace("\"damage\": 100", "\"damage\": 101")).ContentHash);
        Assert.NotEqual(hash, SpellBook.FromJson(json.Replace("\"castDelaySeconds\": 0.5", "\"castDelaySeconds\": 1")).ContentHash);
        Assert.NotEqual(hash, SpellBook.FromJson(json.Replace("\"targets\": \"both\"",
            "\"targets\": \"both\", \"structureDamageMultiplier\": 0.5")).ContentHash);
        // Writing the default multiplier explicitly changes nothing.
        Assert.Equal(hash, SpellBook.FromJson(json.Replace("\"targets\": \"both\"",
            "\"targets\": \"both\", \"structureDamageMultiplier\": 0.35")).ContentHash);
    }

    // ------------------------------------------------------------ catalog

    [Fact]
    public void Catalog_FindsUnitsAndSpells_AndHashesBothFiles()
    {
        CardCatalog cards = TestSim.LoadFantasyCards();
        Assert.Equal("fantasy", cards.Faction);
        Assert.IsType<UnitDefinition>(cards.Get("knight"));
        Assert.IsType<SpellDefinition>(cards.Get("blizzard"));
        Assert.False(cards.TryGet("fire_spirit", out _)); // replaced by the Fireball spell
        Assert.Throws<KeyNotFoundException>(() => cards.Get("dragon"));

        string units = TestSim.FantasyUnitsJson(), spells = TestSim.FantasySpellsJson();
        ulong hash = cards.ContentHash;
        Assert.Equal(hash, CardCatalog.FromJson(units, spells).ContentHash);
        Assert.NotEqual(hash, CardCatalog.FromJson(units, spells.Replace("\"damage\": 325", "\"damage\": 326")).ContentHash);
        Assert.NotEqual(hash, CardCatalog.FromJson(units.Replace("\"hp\": 1800", "\"hp\": 1801"), spells).ContentHash);
    }

    [Fact]
    public void Catalog_RejectsAnIdUsedByAUnitAndASpell()
    {
        string spells = TestSim.FantasySpellsJson().Replace("\"id\": \"blizzard\"", "\"id\": \"knight\"");
        var ex = Assert.Throws<SimJsonException>(() =>
            CardCatalog.FromJson(TestSim.FantasyUnitsJson(), spells, "units.json", "spells.json"));
        Assert.Contains("card id \"knight\" is already a unit", ex.Message);
        Assert.StartsWith("spells.json", ex.Message);
        Assert.True(ex.Line > 0);

        // The same check when joining files that were loaded separately.
        var direct = Assert.Throws<ArgumentException>(() => new CardCatalog(TestSim.LoadFantasy(), SpellBook.FromJson(spells)));
        Assert.Contains("\"knight\" is both a unit and a spell", direct.Message);
    }

    [Fact]
    public void Catalog_RejectsFilesFromDifferentFactions()
    {
        string spells = TestSim.FantasySpellsJson().Replace("\"faction\": \"fantasy\"", "\"faction\": \"scifi\"");
        var ex = Assert.Throws<SimJsonException>(() => CardCatalog.FromJson(TestSim.FantasyUnitsJson(), spells));
        Assert.Contains("does not match the units file's faction \"fantasy\"", ex.Message);
        Assert.Throws<ArgumentException>(() => new CardCatalog(TestSim.LoadFantasy(), SpellBook.FromJson(spells)));

        CardCatalog unitsOnly = CardCatalog.UnitsOnly(TestSim.LoadFantasy());
        Assert.Empty(unitsOnly.Spells.Spells);
        Assert.Equal("fantasy", unitsOnly.Spells.Faction);
    }

    // ------------------------------------------------------------ mixed decks

    public static IEnumerable<object?[]> MixedDecks()
    {
        string[] d = TestSim.DefaultDeckIds;
        // Valid: both spells (knight swapped out), any order.
        yield return new object?[] { d.Select(id => id == "knight" ? "blizzard" : id).ToArray(), null };
        yield return new object?[] { d.Reverse().Select(id => id == "knight" ? "blizzard" : id).ToArray(), null };
        // Invalid.
        yield return new object?[] { d.Select(id => id == "warlord" ? "blizzard" : id).ToArray(), "exactly one leader but this one has 0" };
        yield return new object?[] { d.Select(id => id == "knight" ? "fireball" : id).ToArray(), "\"fireball\" appears twice" };
        yield return new object?[] { d.Select(id => id == "knight" ? "lightning" : id).ToArray(), "no card \"lightning\"" };
        yield return new object?[] { d.Concat(new[] { "blizzard" }).ToArray(), "has 8 cards but this one has 9" };
    }

    [Theory]
    [MemberData(nameof(MixedDecks))]
    public void MixedDeck_Validation(string[] ids, string? fragment)
    {
        CardCatalog cards = TestSim.LoadFantasyCards();
        string? problem = Deck.Validate(cards, ids, 8);
        if (fragment == null)
        {
            Assert.Null(problem);
            Deck deck = Deck.Create(cards, ids, 8);
            Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), deck.Cards.Select(c => c.Id));
            Assert.Equal(new[] { "blizzard", "fireball" }, deck.Cards.OfType<SpellDefinition>().Select(s => s.Id));
            Assert.Equal("warlord", deck.Leader.Id);
            return;
        }
        Assert.NotNull(problem);
        Assert.Contains(fragment, problem);
        var ex = Assert.Throws<ArgumentException>(() => Deck.Create(cards, ids, 8));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Setup_RejectsSpellTimesThatAreNotWholeTicks()
    {
        MatchRules rules = MatchRulesTests.LoadShippedRules();
        string spells = TestSim.FantasySpellsJson().Replace("\"castDelaySeconds\": 1,", "\"castDelaySeconds\": 1.01,");
        var cards = new CardCatalog(TestSim.LoadFantasy(), SpellBook.FromJson(spells));
        Deck deck = Deck.Create(cards, TestSim.DefaultDeckIds, 8);
        var ex = Assert.Throws<ArgumentException>(() =>
            new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), deck, deck));
        Assert.Contains("fireball", ex.Message);
        Assert.Contains("whole number of ticks", ex.Message);

        string zone = TestSim.FantasySpellsJson().Replace("\"zoneTickSeconds\": 0.5", "\"zoneTickSeconds\": 0.33");
        Deck zoneDeck = Deck.Create(new CardCatalog(TestSim.LoadFantasy(), SpellBook.FromJson(zone)), TestSim.BlizzardDeckIds, 8);
        Assert.Throws<ArgumentException>(() =>
            new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), zoneDeck, zoneDeck));
        // A spell the deck does not use is not checked.
        _ = new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(),
            Deck.Create(new CardCatalog(TestSim.LoadFantasy(), SpellBook.FromJson(zone)), TestSim.DefaultDeckIds, 8),
            TestSim.DefaultDeck(rules));
    }
}
