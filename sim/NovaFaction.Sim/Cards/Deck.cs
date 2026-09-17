using System;
using System.Collections.Generic;
using NovaFaction.Sim.Content;

namespace NovaFaction.Sim.Cards
{
    /// <summary>
    /// A player's deck: deckSize (8) different cards (units and spells) from one faction, exactly one of them a
    /// leader unit, each with a level (1 by default).
    /// Card ids are stored sorted (ordinal), so the listing order a player chose never affects the
    /// match: the cycle order depends only on which cards are in the deck and the match seed.
    /// </summary>
    public sealed class Deck
    {
        /// <summary>Sanity bound for levels; the match rules' maxUnitLevel is the real limit (checked by MatchSetup).</summary>
        public const int LevelLimit = 100;

        private readonly CardDefinition[] _cards;
        private readonly int[] _levels;

        private Deck(CardCatalog catalog, CardDefinition[] cards, int[] levels)
        {
            Catalog = catalog;
            _cards = cards;
            _levels = levels;
        }

        /// <summary>The faction's cards the deck was built from.</summary>
        public CardCatalog Catalog { get; }

        /// <summary>The faction's unit cards.</summary>
        public UnitRoster Roster => Catalog.Units;

        /// <summary>The cards, sorted by id (ordinal).</summary>
        public IReadOnlyList<CardDefinition> Cards => _cards;

        /// <summary>Each card's level, in the same order as <see cref="Cards"/>.</summary>
        public IReadOnlyList<int> Levels => _levels;

        /// <summary>The level of a card in this deck.</summary>
        public int GetLevel(CardDefinition card)
        {
            int i = Array.IndexOf(_cards, card);
            if (i < 0)
            {
                throw new ArgumentException("Card \"" + card.Id + "\" is not in this deck.", nameof(card));
            }
            return _levels[i];
        }

        public int LeaderLevel => GetLevel(Leader);

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

        /// <summary>
        /// Builds a deck, throwing <see cref="ArgumentException"/> with the reason if it is invalid.
        /// <paramref name="levels"/> gives each card's level in the same order as <paramref name="cardIds"/>
        /// (null = every card at level 1).
        /// </summary>
        public static Deck Create(CardCatalog catalog, IReadOnlyList<string> cardIds, int deckSize,
            IReadOnlyList<int>? levels = null)
        {
            string? problem = Validate(catalog, cardIds, deckSize);
            if (problem == null && levels != null)
            {
                problem = ValidateLevels(cardIds, levels);
            }
            if (problem != null)
            {
                throw new ArgumentException("Invalid deck: " + problem, nameof(cardIds));
            }
            int[] order = new int[cardIds.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }
            // Insertion sort by id (ordinal); ids are unique, so the order is fully determined.
            for (int i = 1; i < order.Length; i++)
            {
                int current = order[i];
                int j = i - 1;
                while (j >= 0 && string.CompareOrdinal(cardIds[order[j]], cardIds[current]) > 0)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = current;
            }
            var cards = new CardDefinition[order.Length];
            var sortedLevels = new int[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                cards[i] = catalog.Get(cardIds[order[i]]);
                sortedLevels[i] = levels == null ? 1 : levels[order[i]];
            }
            return new Deck(catalog, cards, sortedLevels);
        }

        private static string? ValidateLevels(IReadOnlyList<string> cardIds, IReadOnlyList<int> levels)
        {
            if (levels.Count != cardIds.Count)
            {
                return "there are " + cardIds.Count + " cards but " + levels.Count + " levels.";
            }
            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i] < 1 || levels[i] > LevelLimit)
                {
                    return "card \"" + cardIds[i] + "\" has level " + levels[i] + "; levels must be between 1 and " + LevelLimit + ".";
                }
            }
            return null;
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
