using System;
using System.Collections.Generic;

namespace NovaFaction.Sim.Content
{
    /// <summary>
    /// Every card one faction offers: its unit cards (units.json) and spell cards (spells.json). Card ids are
    /// unique across both files. Immutable.
    /// </summary>
    public sealed class CardCatalog
    {
        /// <summary>Joins already-loaded files, throwing <see cref="ArgumentException"/> if they do not fit together.</summary>
        public CardCatalog(UnitRoster units, SpellBook spells)
        {
            Units = units ?? throw new ArgumentNullException(nameof(units));
            Spells = spells ?? throw new ArgumentNullException(nameof(spells));
            if (units.Faction != spells.Faction)
            {
                throw new ArgumentException("The spells file's faction \"" + spells.Faction
                    + "\" does not match the units file's faction \"" + units.Faction + "\".");
            }
            foreach (SpellDefinition spell in spells.Spells)
            {
                if (units.TryGet(spell.Id, out _))
                {
                    throw new ArgumentException("Card id \"" + spell.Id
                        + "\" is both a unit and a spell; card ids must be unique across units and spells.");
                }
            }
            var h = new StateHasher();
            h.Add(units.ContentHash);
            h.Add(spells.ContentHash);
            ContentHash = h.Value;
        }

        /// <summary>Loads and cross-checks a faction's units.json and spells.json. Throws <see cref="SimJsonException"/>.</summary>
        public static CardCatalog FromJson(string unitsJson, string spellsJson, string unitsSourceName = "units.json",
            string spellsSourceName = SpellBook.DefaultSourceName)
        {
            UnitRoster units = UnitRoster.FromJson(unitsJson, unitsSourceName);
            return new CardCatalog(units, SpellBook.Parse(spellsJson, spellsSourceName, units));
        }

        /// <summary>A catalog of unit cards only.</summary>
        public static CardCatalog UnitsOnly(UnitRoster units) =>
            new CardCatalog(units, SpellBook.Empty(units?.Faction ?? throw new ArgumentNullException(nameof(units))));

        public string Faction => Units.Faction;

        public UnitRoster Units { get; }

        public SpellBook Spells { get; }

        /// <summary>Fingerprint of both files' parsed data. Folded into the match state hash.</summary>
        public ulong ContentHash { get; }

        public bool TryGet(string id, out CardDefinition card)
        {
            if (Units.TryGet(id, out UnitDefinition unit))
            {
                card = unit;
                return true;
            }
            if (Spells.TryGet(id, out SpellDefinition spell))
            {
                card = spell;
                return true;
            }
            card = null!;
            return false;
        }

        public CardDefinition Get(string id)
        {
            if (!TryGet(id, out CardDefinition card))
            {
                throw new KeyNotFoundException("Faction \"" + Faction + "\" has no card \"" + id + "\".");
            }
            return card;
        }
    }
}
