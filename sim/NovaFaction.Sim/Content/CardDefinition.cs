namespace NovaFaction.Sim.Content
{
    /// <summary>What playing a card does.</summary>
    public enum CardKind
    {
        /// <summary>Spawns units (a <see cref="UnitDefinition"/> from units.json).</summary>
        Unit = 0,
        /// <summary>Casts a spell anywhere on the map (a <see cref="SpellDefinition"/> from spells.json).</summary>
        Spell = 1,
    }

    /// <summary>
    /// A card a deck can hold: either a unit card or a spell card. Card ids are unique across a faction's
    /// units.json and spells.json. Immutable.
    /// </summary>
    public abstract class CardDefinition
    {
        internal CardDefinition(int index, string id, string displayName, int cost, bool isPlaceholder)
        {
            Index = index;
            Id = id;
            DisplayName = displayName;
            Cost = cost;
            IsPlaceholder = isPlaceholder;
        }

        public abstract CardKind Kind { get; }

        /// <summary>Position in its own file's list ("units" or "spells").</summary>
        public int Index { get; }

        /// <summary>Card id, unique within the faction across units and spells (a-z, 0-9, '_', '-').</summary>
        public string Id { get; }

        public string DisplayName { get; }

        /// <summary>The archetype job the card fills. Always <see cref="UnitSlot.Spell"/> for spell cards.</summary>
        public abstract UnitSlot Slot { get; }

        /// <summary>Gold cost, 1..7.</summary>
        public int Cost { get; }

        /// <summary>Only unit cards can be leaders.</summary>
        public virtual bool IsLeader => false;

        /// <summary>True while the card's numbers are guesses awaiting tuning ("placeholder": true in the file).</summary>
        public bool IsPlaceholder { get; }

        internal abstract void AppendHash(ref StateHasher h);
    }
}
