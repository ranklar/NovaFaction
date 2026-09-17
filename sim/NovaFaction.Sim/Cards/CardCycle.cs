using System;
using System.Collections.Generic;
using NovaFaction.Sim.Content;

namespace NovaFaction.Sim.Cards
{
    /// <summary>
    /// A player's hand and draw queue. At match start the deck is shuffled with the match RNG, the
    /// first handSize cards form the hand (slot order) and the rest form the queue; the front of the
    /// queue is the visible next card. Playing a slot puts the next card into that slot and the played
    /// card at the back of the queue, so the deck loops forever.
    /// </summary>
    public sealed class CardCycle : IStateHashable
    {
        private readonly UnitDefinition?[] _hand;
        private readonly List<UnitDefinition> _queue;

        internal CardCycle(Deck deck, int handSize, SimRandom random)
        {
            if (handSize < 1 || handSize >= deck.Cards.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(handSize), "handSize must be 1..deck size - 1.");
            }
            var order = new List<UnitDefinition>(deck.Cards);
            random.Shuffle(order);
            _hand = new UnitDefinition?[handSize];
            for (int i = 0; i < handSize; i++)
            {
                _hand[i] = order[i];
            }
            _queue = order.GetRange(handSize, order.Count - handSize);
        }

        /// <summary>The hand, by slot. A slot is null only if it is empty (never under the current rules).</summary>
        public IReadOnlyList<UnitDefinition?> Hand => _hand;

        public int HandSize => _hand.Length;

        /// <summary>The card that fills the next played slot.</summary>
        public UnitDefinition NextCard => _queue[0];

        /// <summary>Cards waiting to be drawn, next card first.</summary>
        public IReadOnlyList<UnitDefinition> Queue => _queue;

        public UnitDefinition? GetSlot(int slot)
        {
            if (slot < 0 || slot >= _hand.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }
            return _hand[slot];
        }

        /// <summary>Plays a filled slot: the next card takes its place and the played card goes to the back.</summary>
        internal UnitDefinition Play(int slot)
        {
            UnitDefinition played = GetSlot(slot) ?? throw new InvalidOperationException("Hand slot " + slot + " is empty.");
            _hand[slot] = _queue[0];
            _queue.RemoveAt(0);
            _queue.Add(played);
            return played;
        }

        /// <summary>
        /// Empties a slot, moving its card to the back of the queue. No rule does this yet; it exists so
        /// the "empty slot" deploy rejection can be tested and for future rules.
        /// </summary>
        internal void ClearSlot(int slot)
        {
            UnitDefinition? card = GetSlot(slot);
            if (card != null)
            {
                _hand[slot] = null;
                _queue.Add(card);
            }
        }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(_hand.Length);
            foreach (UnitDefinition? card in _hand)
            {
                hasher.Add(card != null);
                if (card != null)
                {
                    hasher.Add(card.Id);
                }
            }
            hasher.Add(_queue.Count);
            foreach (UnitDefinition card in _queue)
            {
                hasher.Add(card.Id);
            }
        }
    }
}
