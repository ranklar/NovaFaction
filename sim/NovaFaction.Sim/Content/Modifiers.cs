using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Content
{
    /// <summary>A unit stat that modifiers can change.</summary>
    public enum ModifierStat
    {
        Hp = 0,
        Damage = 1,
        MoveSpeed = 2,
        Range = 3,
        AttackInterval = 4,
    }

    public enum ModifierKind
    {
        /// <summary>Multiplies the stat by the value (1.1 = +10%). Several multipliers add their bonuses.</summary>
        Multiply = 0,
        /// <summary>Adds the value (may be negative) before multipliers apply.</summary>
        Add = 1,
    }

    /// <summary>
    /// One stat change, e.g. "+10% damage for bruisers and swarms". Used by leader passives and Rally abilities.
    /// Immutable. See docs/design.md ("Stat modifiers and levels").
    /// </summary>
    public sealed class Modifier
    {
        private readonly UnitSlot[] _slots;

        public Modifier(ModifierStat stat, ModifierKind kind, Fix value, IReadOnlyList<UnitSlot>? appliesToSlots)
        {
            Stat = stat;
            Kind = kind;
            Value = value;
            _slots = appliesToSlots == null ? Array.Empty<UnitSlot>() : new List<UnitSlot>(appliesToSlots).ToArray();
            AppliesToAll = appliesToSlots == null;
        }

        public ModifierStat Stat { get; }

        public ModifierKind Kind { get; }

        public Fix Value { get; }

        /// <summary>True when the modifier applies to every unit; otherwise only to <see cref="AppliesToSlots"/>.</summary>
        public bool AppliesToAll { get; }

        /// <summary>The slots the modifier applies to (empty when <see cref="AppliesToAll"/>), in file order.</summary>
        public IReadOnlyList<UnitSlot> AppliesToSlots => _slots;

        public bool AppliesTo(UnitSlot slot) => AppliesToAll || Array.IndexOf(_slots, slot) >= 0;

        internal void AppendHash(ref StateHasher h)
        {
            h.Add((int)Stat);
            h.Add((int)Kind);
            h.Add(Value);
            h.Add(AppliesToAll);
            h.Add(_slots.Length);
            foreach (UnitSlot slot in _slots)
            {
                h.Add((int)slot);
            }
        }

        internal static void AppendHash(ref StateHasher h, IReadOnlyList<Modifier> modifiers)
        {
            h.Add(modifiers.Count);
            foreach (Modifier m in modifiers)
            {
                m.AppendHash(ref h);
            }
        }

        // ------------------------------------------------------------ JSON

        private static readonly string[] Keys = { "stat", "kind", "value", "appliesTo" };
        private static readonly string[] StatNames = { "hp", "damage", "moveSpeed", "range", "attackInterval" };
        private static readonly string[] KindNames = { "multiply", "add" };
        private static readonly string[] SlotNames =
            { "tank", "bruiser", "swarm", "ranged", "flyer", "siege", "support", "spell", "building", "leader" };

        /// <summary>The largest multiplier a modifier may use.</summary>
        public const int MaxMultiplier = 100;

        /// <summary>
        /// Reads a list of modifiers: each { "stat", "kind", "value", "appliesTo" }, where appliesTo is "all" or a
        /// non-empty list of slot names. Multiply values are factors (0..100); Add values are -1000000..1000000.
        /// </summary>
        internal static Modifier[] ReadList(JsonValue list, string what)
        {
            IReadOnlyList<JsonValue> items = list.AsArray();
            var result = new Modifier[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                result[i] = Read(items[i], what);
            }
            return result;
        }

        private static Modifier Read(JsonValue item, string what)
        {
            if (item.Kind != JsonKind.Object)
            {
                throw item.Error(what + "each modifier must be a JSON object.");
            }
            UnitRoster.CheckKeys(item, Keys, Array.Empty<string>(), "modifier");
            var stat = (ModifierStat)UnitRoster.ReadName(item.Get("stat"), StatNames, what + "modifier stat");
            var kind = (ModifierKind)UnitRoster.ReadName(item.Get("kind"), KindNames, what + "modifier kind");
            JsonValue valueJson = item.Get("value");
            Fix value = valueJson.AsFix();
            if (kind == ModifierKind.Multiply && (value < Fix.Zero || value > Fix.FromInt(MaxMultiplier)))
            {
                throw valueJson.Error(what + "a multiply modifier's value must be between 0 and " + MaxMultiplier
                    + " (1.1 means +10%).");
            }
            if (kind == ModifierKind.Add && Fix.Abs(value) > Fix.FromInt(UnitRoster.MaxStatValue))
            {
                throw valueJson.Error(what + "an add modifier's value must be between -" + UnitRoster.MaxStatValue
                    + " and " + UnitRoster.MaxStatValue + ".");
            }

            JsonValue appliesTo = item.Get("appliesTo");
            if (appliesTo.Kind == JsonKind.String)
            {
                if (appliesTo.AsString() != "all")
                {
                    throw appliesTo.Error(what + "appliesTo must be \"all\" or a list of slots.");
                }
                return new Modifier(stat, kind, value, null);
            }
            IReadOnlyList<JsonValue> slotValues = appliesTo.AsArray();
            if (slotValues.Count == 0)
            {
                throw appliesTo.Error(what + "appliesTo must not be an empty list.");
            }
            var slots = new List<UnitSlot>();
            foreach (JsonValue slotValue in slotValues)
            {
                var slot = (UnitSlot)UnitRoster.ReadName(slotValue, SlotNames, what + "appliesTo slot");
                if (slot == UnitSlot.Spell)
                {
                    throw slotValue.Error(what + "appliesTo cannot name \"spell\": modifiers change units only.");
                }
                if (slots.Contains(slot))
                {
                    throw slotValue.Error(what + "appliesTo lists \"" + SlotNames[(int)slot] + "\" twice.");
                }
                slots.Add(slot);
            }
            return new Modifier(stat, kind, value, slots);
        }
    }

    /// <summary>
    /// Effective stat math. A stat is: (base * level factor + sum of Add values) * (1 + sum of (Multiply value - 1)),
    /// over every modifier that applies to the unit's slot. Sums of Fix values are exact, so the order of modifiers
    /// never matters. Results are clamped so a unit stays valid (hp, range and attack interval above 0; damage and
    /// speed at least 0).
    /// </summary>
    public static class StatMath
    {
        /// <summary>1 + bonusPerLevel * (level - 1): the Hp and Damage multiplier of a card or structure level.</summary>
        public static Fix LevelFactor(Fix bonusPerLevel, int level) =>
            Fix.One + bonusPerLevel * Fix.FromInt(level - 1);

        public static Fix Apply(ModifierStat stat, Fix levelled, UnitSlot slot, IReadOnlyList<Modifier> first,
            IReadOnlyList<Modifier>? second = null)
        {
            Fix add = Fix.Zero;
            Fix bonus = Fix.Zero;
            Sum(stat, slot, first, ref add, ref bonus);
            if (second != null)
            {
                Sum(stat, slot, second, ref add, ref bonus);
            }
            Fix factor = Fix.Max(Fix.Zero, Fix.One + bonus);
            Fix value = (levelled + add) * factor;
            Fix min = stat == ModifierStat.Damage || stat == ModifierStat.MoveSpeed ? Fix.Zero : Fix.Epsilon;
            return Fix.Max(min, value);
        }

        private static void Sum(ModifierStat stat, UnitSlot slot, IReadOnlyList<Modifier> modifiers, ref Fix add,
            ref Fix bonus)
        {
            foreach (Modifier m in modifiers)
            {
                if (m.Stat != stat || !m.AppliesTo(slot))
                {
                    continue;
                }
                if (m.Kind == ModifierKind.Add)
                {
                    add += m.Value;
                }
                else
                {
                    bonus += m.Value - Fix.One;
                }
            }
        }
    }
}
