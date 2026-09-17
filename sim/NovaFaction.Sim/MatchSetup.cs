using System;
using System.Collections.Generic;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim
{
    /// <summary>
    /// Everything fixed before a match starts, apart from the seed: rules, map, structure stats, both decks (with
    /// card levels) and each player's structure level. Each deck carries its own faction roster, so the two players
    /// may use different factions.
    /// </summary>
    public sealed class MatchSetup
    {
        private readonly Deck[] _decks;
        private readonly int[] _structureLevels;

        public MatchSetup(MatchRules rules, MapDefinition map, StructureCatalog structures, Deck deck0, Deck deck1,
            int structureLevel0 = 1, int structureLevel1 = 1)
        {
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Structures = structures ?? throw new ArgumentNullException(nameof(structures));
            _decks = new[]
            {
                deck0 ?? throw new ArgumentNullException(nameof(deck0)),
                deck1 ?? throw new ArgumentNullException(nameof(deck1)),
            };
            _structureLevels = new[] { structureLevel0, structureLevel1 };
            foreach (int level in _structureLevels)
            {
                if (level < 1 || level > rules.MaxUnitLevel)
                {
                    throw new ArgumentException("Structure levels must be between 1 and maxUnitLevel ("
                        + rules.MaxUnitLevel + ") but one is " + level + ".");
                }
            }
            foreach (Deck deck in _decks)
            {
                if (deck.Cards.Count != rules.DeckSize)
                {
                    throw new ArgumentException("Every deck must have " + rules.DeckSize + " cards.");
                }
                for (int i = 0; i < deck.Cards.Count; i++)
                {
                    if (deck.Levels[i] > rules.MaxUnitLevel)
                    {
                        throw new ArgumentException("Card \"" + deck.Cards[i].Id + "\" has level " + deck.Levels[i]
                            + " but maxUnitLevel is " + rules.MaxUnitLevel + ".");
                    }
                }
                CheckSpeeds(deck);
                CheckSpellTimes(deck);
                CheckAbilityTimes(deck.Leader);
            }
            // A mine's own cell is blocked, so units can only stand next to it: the radius must reach that far.
            if (map.Mines.Count > 0 && rules.MineCaptureRadius < map.CellSize)
            {
                throw new ArgumentException("mineCaptureRadius (" + rules.MineCaptureRadius + ") must be at least the map's "
                    + "cellSize (" + map.CellSize + "), or units beside a mine could never capture it.");
            }
        }

        public MatchRules Rules { get; }

        public MapDefinition Map { get; }

        /// <summary>Keep and forward tower stats (content/structures.json).</summary>
        public StructureCatalog Structures { get; }

        /// <summary>Deck of player 0 and player 1.</summary>
        public IReadOnlyList<Deck> Decks => _decks;

        public Deck GetDeck(int player) => _decks[MapDefinition.CheckPlayer(player)];

        /// <summary>Each player's structure level (1 = base stats); scales their Keep and towers' Hp and Damage.</summary>
        public IReadOnlyList<int> StructureLevels => _structureLevels;

        public int GetStructureLevel(int player) => _structureLevels[MapDefinition.CheckPlayer(player)];

        /// <summary>
        /// Movement checks walkability only at the destination of each step, so a step must stay well
        /// under one cell. Reject content that would move a unit more than half a cell per tick, at the fastest speed
        /// the deck's leader can give it (passive alone, or passive plus a Rally).
        /// </summary>
        private void CheckSpeeds(Deck deck)
        {
            Fix limit = Map.CellSize * Fix.Half * Fix.FromInt(Rules.TicksPerSecond);
            // Moving neighbors push up to the base strength; stopped neighbors add up to (stopped factor) backward
            // plus a full-strength sideways slide. (2 + factor) times the base bounds the total.
            Fix maxPush = Rules.UnitSeparationPushPerSecond * (Fix.FromInt(2) + Rules.UnitStoppedPushFactor);
            foreach (CardDefinition entry in deck.Cards)
            {
                if (!(entry is UnitDefinition card))
                {
                    continue;
                }
                IReadOnlyList<Modifier> passive = deck.Leader.Passive;
                Fix speed = StatMath.Apply(ModifierStat.MoveSpeed, card.MoveSpeed, card.Slot, passive);
                LeaderAbilityDefinition? ability = deck.Leader.Ability;
                if (ability != null && ability.Type == AbilityType.Rally)
                {
                    speed = Fix.Max(speed, StatMath.Apply(ModifierStat.MoveSpeed, card.MoveSpeed, card.Slot, passive,
                        ability.Modifiers));
                }
                if (speed + maxPush > limit)
                {
                    throw new ArgumentException("Unit \"" + card.Id + "\" moves too fast for this map and tick rate: "
                        + "moveSpeed (with the leader's modifiers) + unitSeparationPushPerSecond * (2 + unitStoppedPushFactor) must be at most "
                        + limit + " world units per second.");
                }
            }
        }

        /// <summary>A leader ability's cooldown and duration must be whole ticks, like spell times.</summary>
        private void CheckAbilityTimes(UnitDefinition leader)
        {
            LeaderAbilityDefinition? ability = leader.Ability;
            if (ability != null && (!Rules.IsWholeTicks(ability.CooldownSeconds) || !Rules.IsWholeTicks(ability.DurationSeconds)))
            {
                throw new ArgumentException("Leader \"" + leader.Id + "\": the ability's cooldownSeconds and durationSeconds "
                    + "must each be a whole number of ticks (a multiple of 1/" + Rules.TicksPerSecond + " s).");
            }
        }

        /// <summary>Spell delays, durations and pulse intervals must be whole ticks, like the spawn delay.</summary>
        private void CheckSpellTimes(Deck deck)
        {
            foreach (CardDefinition entry in deck.Cards)
            {
                if (!(entry is SpellDefinition spell))
                {
                    continue;
                }
                if (!Rules.IsWholeTicks(spell.CastDelaySeconds) || !Rules.IsWholeTicks(spell.DurationSeconds)
                    || !Rules.IsWholeTicks(spell.ZoneTickSeconds))
                {
                    throw new ArgumentException("Spell \"" + spell.Id + "\": castDelaySeconds, durationSeconds and "
                        + "zoneTickSeconds must each be a whole number of ticks (a multiple of 1/" + Rules.TicksPerSecond + " s).");
                }
            }
        }
    }
}
