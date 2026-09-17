using System;
using System.Collections.Generic;
using NovaFaction.Sim.Content;

namespace NovaFaction.Sim.Cards
{
    /// <summary>
    /// A player's deck: deckSize (8) different cards from one faction, exactly one of them a leader.
    /// Card ids are stored sorted (ordinal), so the listing order a player chose never affects the
    /// match: the cycle order depends only on which cards are in the deck and the match seed.
    /// </summary>
    public sealed class Deck
    {
        private readonly UnitDefinition[] _cards;

        private Deck(UnitRoster roster, UnitDefinition[] cards)
        {
            Roster = roster;
            _cards = cards;
        }

        /// <summary>The faction the cards come from.</summary>
        public UnitRoster Roster { get; }

        /// <summary>The cards, sorted by id (ordinal).</summary>
        public IReadOnlyList<UnitDefinition> Cards => _cards;

        public UnitDefinition Leader
        {
            get
            {
                foreach (UnitDefinition card in _cards)
                {
                    if (card.IsLeader)
                    {
                        return card;
                    }
                }
                throw new InvalidOperationException("A validated deck always has a leader.");
            }
        }

        /// <summary>Builds a deck, throwing <see cref="ArgumentException"/> with the reason if it is invalid.</summary>
        public static Deck Create(UnitRoster roster, IReadOnlyList<string> cardIds, int deckSize)
        {
            string? problem = Validate(roster, cardIds, deckSize);
            if (problem != null)
            {
                throw new ArgumentException("Invalid deck: " + problem, nameof(cardIds));
            }
            string[] sorted = new string[cardIds.Count];
            for (int i = 0; i < sorted.Length; i++)
            {
                sorted[i] = cardIds[i];
            }
            Array.Sort(sorted, StringComparer.Ordinal);
            var cards = new UnitDefinition[sorted.Length];
            for (int i = 0; i < sorted.Length; i++)
            {
                cards[i] = roster.Get(sorted[i]);
            }
            return new Deck(roster, cards);
        }

        /// <summary>Returns null if the card list is a valid deck for the roster, otherwise the reason.</summary>
        public static string? Validate(UnitRoster roster, IReadOnlyList<string?>? cardIds, int deckSize)
        {
            if (roster == null)
            {
                throw new ArgumentNullException(nameof(roster));
            }
            if (cardIds == null)
            {
                return "no card list.";
            }
            if (cardIds.Count != deckSize)
            {
                return "a deck has " + deckSize + " cards but this one has " + cardIds.Count + ".";
            }
            int leaders = 0;
            for (int i = 0; i < cardIds.Count; i++)
            {
                string? id = cardIds[i];
                if (id == null || !roster.TryGet(id, out UnitDefinition card))
                {
                    return "faction \"" + roster.Faction + "\" has no card \"" + id + "\".";
                }
                for (int j = 0; j < i; j++)
                {
                    if (cardIds[j] == id)
                    {
                        return "card \"" + id + "\" appears twice.";
                    }
                }
                if (card.IsLeader)
                {
                    leaders++;
                }
            }
            if (leaders != 1)
            {
                return "a deck needs exactly one leader but this one has " + leaders + ".";
            }
            return null;
        }
    }
}
