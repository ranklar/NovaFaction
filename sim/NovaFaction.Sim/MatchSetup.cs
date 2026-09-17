using System;
using System.Collections.Generic;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim
{
    /// <summary>
    /// Everything fixed before a match starts, apart from the seed: rules, map, structure stats and both decks.
    /// Each deck carries its own faction roster, so the two players may use different factions.
    /// </summary>
    public sealed class MatchSetup
    {
        private readonly Deck[] _decks;

        public MatchSetup(MatchRules rules, MapDefinition map, StructureCatalog structures, Deck deck0, Deck deck1)
        {
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Structures = structures ?? throw new ArgumentNullException(nameof(structures));
            _decks = new[]
            {
                deck0 ?? throw new ArgumentNullException(nameof(deck0)),
                deck1 ?? throw new ArgumentNullException(nameof(deck1)),
            };
            foreach (Deck deck in _decks)
            {
                if (deck.Cards.Count != rules.DeckSize)
                {
                    throw new ArgumentException("Every deck must have " + rules.DeckSize + " cards.");
                }
                CheckSpeeds(deck);
            }
        }

        public MatchRules Rules { get; }

        public MapDefinition Map { get; }

        /// <summary>Keep and forward tower stats (content/structures.json).</summary>
        public StructureCatalog Structures { get; }

        /// <summary>Deck of player 0 and player 1.</summary>
        public IReadOnlyList<Deck> Decks => _decks;

        public Deck GetDeck(int player) => _decks[MapDefinition.CheckPlayer(player)];

        /// <summary>
        /// Movement checks walkability only at the destination of each step, so a step must stay well
        /// under one cell. Reject content that would move a unit more than half a cell per tick.
        /// </summary>
        private void CheckSpeeds(Deck deck)
        {
            Fix limit = Map.CellSize * Fix.Half * Fix.FromInt(Rules.TicksPerSecond);
            // Moving neighbors push up to the base strength; stopped neighbors add up to (stopped factor) backward
            // plus a full-strength sideways slide. (2 + factor) times the base bounds the total.
            Fix maxPush = Rules.UnitSeparationPushPerSecond * (Fix.FromInt(2) + Rules.UnitStoppedPushFactor);
            foreach (UnitDefinition card in deck.Cards)
            {
                if (card.MoveSpeed + maxPush > limit)
                {
                    throw new ArgumentException("Unit \"" + card.Id + "\" moves too fast for this map and tick rate: "
                        + "moveSpeed + unitSeparationPushPerSecond * (2 + unitStoppedPushFactor) must be at most "
                        + limit + " world units per second.");
                }
            }
        }
    }
}
