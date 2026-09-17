using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

/// <summary>Builds simulations with the shipped fantasy units and a default deck for both players.</summary>
internal static class TestSim
{
    /// <summary>The movement keys of rules.json, for tests that write rules inline.</summary>
    internal const string MovementRulesJson =
        ", \"unitSeparationDistance\": 0.6, \"unitSeparationPushPerSecond\": 1.5, \"unitSpawnSpacing\": 0.5";

    internal static string FantasyUnitsJson() =>
        File.ReadAllText(MatchRulesTests.ContentPath(Path.Combine("factions", "fantasy", "units.json")));

    /// <summary>The shipped content/factions/fantasy/units.json.</summary>
    internal static UnitRoster LoadFantasy() => UnitRoster.FromJson(FantasyUnitsJson(), "units.json");

    /// <summary>All eight shipped fantasy units (one of them the leader), in file order.</summary>
    internal static readonly string[] DefaultDeckIds =
        { "stone_golem", "knight", "goblin_pack", "elf_archer", "griffin", "catapult", "fire_spirit", "warlord" };

    internal static Deck DefaultDeck(MatchRules rules, UnitRoster? roster = null) =>
        Deck.Create(roster ?? LoadFantasy(), DefaultDeckIds, rules.DeckSize);

    internal static MatchSetup Setup(MatchRules rules, MapDefinition map)
    {
        UnitRoster roster = LoadFantasy();
        return new MatchSetup(rules, map, DefaultDeck(rules, roster), DefaultDeck(rules, roster));
    }

    internal static Simulation New(MatchRules rules, MapDefinition map, ulong seed) => new Simulation(Setup(rules, map), seed);

    internal static Simulation Replay(MatchRules rules, MapDefinition map, ulong seed, CommandLog log) =>
        Simulation.Replay(Setup(rules, map), seed, log);

    /// <summary>Rules with the shipped structure but chosen gold values and spawn delay.</summary>
    internal static MatchRules Rules(string income = "0.35", string start = "10", string cap = "10", string spawnDelay = "1",
        string separationDistance = "0.6", string push = "1.5", string spacing = "0.5") =>
        MatchRules.FromJson("{\"ticksPerSecond\": 20, \"matchLengthSeconds\": 180, \"suddenDeathSeconds\": 60, "
            + "\"goldBaseIncomePerSecond\": " + income + ", \"goldStartingAmount\": " + start + ", \"goldCap\": " + cap
            + ", \"deploySpawnDelaySeconds\": " + spawnDelay + ", \"handSize\": 4, \"deckSize\": 8"
            + ", \"unitSeparationDistance\": " + separationDistance + ", \"unitSeparationPushPerSecond\": " + push
            + ", \"unitSpawnSpacing\": " + spacing + "}");

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
