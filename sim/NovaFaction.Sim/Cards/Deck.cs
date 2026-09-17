using System;
using System.Collections.Generic;
using NovaFaction.Sim.Content;

namespace NovaFaction.Sim.Cards
{
    /// <summary>
    /// A player's deck: deckSize (8) different cards (units and spells) from one faction, exactly one of them a
    /// leader unit.
    /// Card ids are stored sorted (ordinal), so the listing order a player chose never affects the
    /// match: the cycle order depends only on which cards are in the deck and the match seed.
    /// </summary>
    public sealed class Deck
    {
        private readonly CardDefinition[] _cards;

        private Deck(CardCatalog catalog, CardDefinition[] cards)
        {
            Catalog = catalog;
            _cards = cards;
        }

        /// <summary>The faction's cards the deck was built from.</summary>
        public CardCatalog Catalog { get; }

        /// <summary>The faction's unit cards.</summary>
        public UnitRoster Roster => Catalog.Units;

        /// <summary>The cards, sorted by id (ordinal).</summary>
        public IReadOnlyList<CardDefinition> Cards => _cards;

        public UnitDefinition Leader
        {
            get
            {
                foreach (CardDefinition card in _cards)
                {
                    if (card.IsLeader)
                    {
                        return (UnitDefinition)card;
                    }
                }
                throw new InvalidOperationException("A validated deck always has a leader.");
            }
        }

        /// <summary>Builds a deck, throwing <see cref="ArgumentException"/> with the reason if it is invalid.</summary>
        public static Deck Create(CardCatalog catalog, IReadOnlyList<string> cardIds, int deckSize)
        {
            string? problem = Validate(catalog, cardIds, deckSize);
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
            var cards = new CardDefinition[sorted.Length];
            for (int i = 0; i < sorted.Length; i++)
            {
                cards[i] = catalog.Get(sorted[i]);
            }
            return new Deck(catalog, cards);
        }

        /// <summary>
        /// Returns null if the card list is a valid deck for the faction, otherwise the reason. Unit and spell cards
        /// mix freely; card costs and id uniqueness are already guaranteed by the catalog.
        /// </summary>
        public static string? Validate(CardCatalog catalog, IReadOnlyList<string?>? cardIds, int deckSize)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
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
                if (id == null || !catalog.TryGet(id, out CardDefinition card))
                {
                    return "faction \"" + catalog.Faction + "\" has no card \"" + id + "\".";
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
