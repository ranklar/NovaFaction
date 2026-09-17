using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

/// <summary>Builds simulations with the shipped fantasy units and a default deck for both players.</summary>
internal static class TestSim
{
    /// <summary>The map gold keys of rules.json (fixed values), for tests that write rules inline.</summary>
    internal const string MapGoldRulesJson =
        ", \"mineCaptureRadius\": 1.5, \"mineCaptureSeconds\": 5, \"mineIncomePerSecond\": 0.05, \"mineIncomeCap\": 0.1"
        + ", \"chestFirstSpawnSeconds\": 30, \"chestSpawnIntervalSeconds\": 30, \"chestGold\": 0.75, \"chestCollectRadius\": 0.75"
        + LevelRulesJson;

    /// <summary>The capture give-up and level keys of rules.json (shipped values), for tests that write rules inline.</summary>
    internal const string LevelRulesJson =
        ", \"mineCaptureGiveUpSeconds\": 3, \"mineCaptureRetrySeconds\": 6, \"maxUnitLevel\": 15"
        + ", \"levelStatBonusPerLevel\": 0.06, \"rulesVersion\": 1";

    /// <summary>The movement, combat and map gold keys of rules.json, for tests that write rules inline.</summary>
    internal const string MovementRulesJson =
        ", \"unitSeparationDistance\": 0.6, \"unitSeparationPushPerSecond\": 1.5, \"unitSpawnSpacing\": 0.5"
        + ", \"unitStoppedPushFactor\": 0.3, \"aggroRadius\": 5.5, \"meleeTargetCrowdPenalty\": 1"
        + ", \"suddenDeathIncomeMultiplier\": 2" + MapGoldRulesJson;

    internal static string StructuresJson() => File.ReadAllText(MatchRulesTests.ContentPath("structures.json"));

    /// <summary>The shipped content/structures.json.</summary>
    internal static StructureCatalog LoadStructures() => StructureCatalog.FromJson(StructuresJson());

    /// <summary>A structures file with the given stats (numbers as JSON text); both kinds share the combat stats.</summary>
    internal static StructureCatalog Structures(string hp = "2500", string damage = "80", string interval = "1",
        string range = "7", string targets = "both", string projectileSpeed = "10", string bonus = "500",
        string keepHp = "4000", string keepBonus = "1000")
    {
        string Entry(string kind, string entryHp, string entryBonus) =>
            "{\"kind\": \"" + kind + "\", \"hp\": " + entryHp + ", \"damage\": " + damage + ", \"attackIntervalSeconds\": "
            + interval + ", \"range\": " + range + ", \"targets\": \"" + targets + "\", \"projectileSpeed\": "
            + projectileSpeed + ", \"destructionBonus\": " + entryBonus + "}";
        return StructureCatalog.FromJson("{\"formatVersion\": 1, \"structures\": [" + Entry("keep", keepHp, keepBonus)
            + ", " + Entry("tower", hp, bonus) + "]}");
    }

    /// <summary>Structures that never hurt anyone and practically cannot fall: for movement-only tests.</summary>
    internal static StructureCatalog HarmlessStructures() => Structures(hp: "1000000", damage: "0", keepHp: "1000000");

    internal static string FantasyUnitsJson() =>
        File.ReadAllText(MatchRulesTests.ContentPath(Path.Combine("factions", "fantasy", "units.json")));

    /// <summary>The shipped content/factions/fantasy/units.json.</summary>
    internal static UnitRoster LoadFantasy() => UnitRoster.FromJson(FantasyUnitsJson(), "units.json");

    internal static string FantasySpellsJson() =>
        File.ReadAllText(MatchRulesTests.ContentPath(Path.Combine("factions", "fantasy", "spells.json")));

    /// <summary>The shipped content/factions/fantasy/spells.json.</summary>
    internal static SpellBook LoadFantasySpells() => SpellBook.FromJson(FantasySpellsJson());

    /// <summary>The shipped fantasy units and spells.</summary>
    internal static CardCatalog LoadFantasyCards() => CardCatalog.FromJson(FantasyUnitsJson(), FantasySpellsJson());

    /// <summary>The shipped fantasy spells with the given units.</summary>
    internal static CardCatalog Cards(UnitRoster units) => new CardCatalog(units, LoadFantasySpells());

    /// <summary>All seven shipped fantasy units (one of them the leader) in file order, plus the Fireball spell.</summary>
    internal static readonly string[] DefaultDeckIds =
        { "stone_golem", "knight", "goblin_pack", "elf_archer", "griffin", "catapult", "warlord", "fireball" };

    /// <summary>The default deck with Blizzard in place of Fireball.</summary>
    internal static readonly string[] BlizzardDeckIds =
        DefaultDeckIds.Select(id => id == "fireball" ? "blizzard" : id).ToArray();

    internal static Deck DefaultDeck(MatchRules rules, CardCatalog? cards = null) =>
        Deck.Create(cards ?? LoadFantasyCards(), DefaultDeckIds, rules.DeckSize);

    internal static MatchSetup Setup(MatchRules rules, MapDefinition map, StructureCatalog? structures = null,
        string[]? deckIds = null)
    {
        CardCatalog cards = LoadFantasyCards();
        Deck deck = Deck.Create(cards, deckIds ?? DefaultDeckIds, rules.DeckSize);
        return new MatchSetup(rules, map, structures ?? LoadStructures(), deck, deck);
    }

    internal static Simulation New(MatchRules rules, MapDefinition map, ulong seed, StructureCatalog? structures = null,
        string[]? deckIds = null) =>
        new Simulation(Setup(rules, map, structures, deckIds), seed);

    internal static Simulation Replay(MatchRules rules, MapDefinition map, ulong seed, CommandLog log) =>
        Simulation.Replay(Setup(rules, map), seed, log);

    /// <summary>Rules with the shipped structure but chosen gold values and spawn delay.</summary>
    internal static MatchRules Rules(string income = "0.35", string start = "10", string cap = "10", string spawnDelay = "1",
        string separationDistance = "0.6", string push = "1.5", string spacing = "0.5", string stoppedPush = "0.3",
        string aggro = "5.5", string crowdPenalty = "1", string matchSeconds = "180", string suddenDeathSeconds = "60",
        string mineRadius = "1.5", string mineSeconds = "5", string mineIncome = "0.05", string mineCap = "0.1",
        string chestFirst = "30", string chestInterval = "30", string chestGold = "0.75", string chestRadius = "0.75",
        string giveUp = "3", string retry = "6", string maxLevel = "15", string levelBonus = "0.06") =>
        MatchRules.FromJson("{\"ticksPerSecond\": 20, \"matchLengthSeconds\": " + matchSeconds
            + ", \"suddenDeathSeconds\": " + suddenDeathSeconds + ", \"suddenDeathIncomeMultiplier\": 2, "
            + "\"goldBaseIncomePerSecond\": " + income + ", \"goldStartingAmount\": " + start + ", \"goldCap\": " + cap
            + ", \"deploySpawnDelaySeconds\": " + spawnDelay + ", \"handSize\": 4, \"deckSize\": 8"
            + ", \"unitSeparationDistance\": " + separationDistance + ", \"unitSeparationPushPerSecond\": " + push
            + ", \"unitSpawnSpacing\": " + spacing + ", \"unitStoppedPushFactor\": " + stoppedPush
            + ", \"aggroRadius\": " + aggro + ", \"meleeTargetCrowdPenalty\": " + crowdPenalty
            + ", \"mineCaptureRadius\": " + mineRadius + ", \"mineCaptureSeconds\": " + mineSeconds
            + ", \"mineIncomePerSecond\": " + mineIncome + ", \"mineIncomeCap\": " + mineCap
            + ", \"chestFirstSpawnSeconds\": " + chestFirst + ", \"chestSpawnIntervalSeconds\": " + chestInterval
            + ", \"chestGold\": " + chestGold + ", \"chestCollectRadius\": " + chestRadius
            + ", \"mineCaptureGiveUpSeconds\": " + giveUp + ", \"mineCaptureRetrySeconds\": " + retry
            + ", \"maxUnitLevel\": " + maxLevel + ", \"levelStatBonusPerLevel\": " + levelBonus + ", \"rulesVersion\": 1}");

    internal static FixVector2 V(string x, string y) => new FixVector2(Fix.Parse(x), Fix.Parse(y));

    internal static string?[] HandIds(PlayerState player) => player.Cards.Hand.Select(c => c?.Id).ToArray();

    /// <summary>The hand slot holding the card, or -1.</summary>
    internal static int SlotOf(PlayerState player, string cardId)
    {
        for (int i = 0; i < player.Cards.HandSize; i++)
        {
            if (player.Cards.Hand[i]?.Id == cardId)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the slot holding the card, first cycling slot 0 (outside the command path, test-only)
    /// until the card reaches the hand.
    /// </summary>
    internal static int EnsureInHand(PlayerState player, string cardId)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            int slot = SlotOf(player, cardId);
            if (slot >= 0)
            {
                return slot;
            }
            player.Cards.Play(0); // cycle one card through slot 0 without a deploy
        }
        throw new InvalidOperationException("card " + cardId + " never reached the hand");
    }

    private static readonly Command[] NoCommands = Array.Empty<Command>();

    internal static void Run(Simulation sim, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            sim.Tick(NoCommands);
        }
    }

    /// <summary>Deploys a card on the current tick (moving it into the hand first if needed).</summary>
    internal static void Deploy(Simulation sim, int player, string cardId, FixVector2 target, int sequence = 0)
    {
        int slot = EnsureInHand(sim.State.GetPlayer(player), cardId);
        sim.Tick(new[] { Command.DeployCard(sim.State.Tick, player, sequence, slot, target) });
    }
}
