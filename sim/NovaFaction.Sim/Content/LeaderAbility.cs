using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Content
{
    public enum AbilityType
    {
        /// <summary>Damages enemy units (and structures, reduced) in the radius.</summary>
        AreaDamage = 0,
        /// <summary>Gives friendly units in the radius timed stat modifiers.</summary>
        Rally = 1,
        /// <summary>Heals friendly units in the radius, never above their max hp.</summary>
        Heal = 2,
    }

    /// <summary>
    /// A leader's active ability (the "ability" object of a leader in units.json). Immutable.
    /// See docs/design.md ("Leaders").
    /// </summary>
    public sealed class LeaderAbilityDefinition
    {
        private readonly Modifier[] _modifiers;

        internal LeaderAbilityDefinition(string displayName, AbilityType type, Fix radius, Fix range, Fix cooldownSeconds,
            Fix durationSeconds, Modifier[] modifiers, Fix amount, Fix structureDamageMultiplier)
        {
            DisplayName = displayName;
            Type = type;
            Radius = radius;
            Range = range;
            CooldownSeconds = cooldownSeconds;
            DurationSeconds = durationSeconds;
            _modifiers = modifiers;
            Amount = amount;
            StructureDamageMultiplier = structureDamageMultiplier;
        }

        public string DisplayName { get; }

        public AbilityType Type { get; }

        /// <summary>Units whose center is within this distance of the target point are affected.</summary>
        public Fix Radius { get; }

        /// <summary>The target point must be within this distance of the leader's center.</summary>
        public Fix Range { get; }

        /// <summary>Time after a use before the next one is allowed (a whole number of ticks, more than 0).</summary>
        public Fix CooldownSeconds { get; }

        /// <summary>Rally only: how long the modifiers last (a whole number of ticks, more than 0); otherwise 0.</summary>
        public Fix DurationSeconds { get; }

        /// <summary>Rally only: the timed modifiers; otherwise empty.</summary>
        public IReadOnlyList<Modifier> Modifiers => _modifiers;

        /// <summary>AreaDamage: damage per enemy unit. Heal: hp restored per friendly unit. Rally: 0. Scaled by level.</summary>
        public Fix Amount { get; }

        /// <summary>AreaDamage only: structures take Amount times this (default 0.35, like spells).</summary>
        public Fix StructureDamageMultiplier { get; }

        internal void AppendHash(ref StateHasher h)
        {
            h.Add(DisplayName);
            h.Add((int)Type);
            h.Add(Radius);
            h.Add(Range);
            h.Add(CooldownSeconds);
            h.Add(DurationSeconds);
            Modifier.AppendHash(ref h, _modifiers);
            h.Add(Amount);
            h.Add(StructureDamageMultiplier);
        }

        // ------------------------------------------------------------ JSON

        private static readonly string[] TypeNames = { "areaDamage", "rally", "heal" };
        private static readonly string[] CommonKeys = { "displayName", "type", "radius", "range", "cooldownSeconds" };
        private static readonly string[] RallyKeys = { "durationSeconds", "modifiers" };
        private static readonly string[] AreaDamageKeys = { "damage" };
        private static readonly string[] HealKeys = { "amount" };
        private const string KeyMultiplier = "structureDamageMultiplier";

        internal static LeaderAbilityDefinition Read(JsonValue item, string what)
        {
            if (item.Kind != JsonKind.Object)
            {
                throw item.Error(what + "ability must be a JSON object.");
            }
            var type = (AbilityType)UnitRoster.ReadName(item.Get("type"), TypeNames, what + "ability type");
            string[] extra;
            string[] optional = Array.Empty<string>();
            switch (type)
            {
                case AbilityType.Rally:
                    extra = RallyKeys;
                    break;
                case AbilityType.AreaDamage:
                    extra = AreaDamageKeys;
                    optional = new[] { KeyMultiplier };
                    break;
                default:
                    extra = HealKeys;
                    break;
            }
            var required = new List<string>(CommonKeys);
            required.AddRange(extra);
            UnitRoster.CheckKeys(item, required.ToArray(), optional, "\"" + TypeNames[(int)type] + "\" ability");

            what += "ability ";
            string displayName = UnitRoster.ReadDisplayName(item, what);
            Fix radius = UnitRoster.ReadStat(item, "radius", what, allowZero: false);
            Fix range = UnitRoster.ReadStat(item, "range", what, allowZero: true);
            Fix cooldown = ReadSeconds(item, "cooldownSeconds", what);
            Fix duration = Fix.Zero;
            Modifier[] modifiers = Array.Empty<Modifier>();
            Fix amount = Fix.Zero;
            Fix multiplier = Fix.Zero;
            switch (type)
            {
                case AbilityType.Rally:
                    duration = ReadSeconds(item, "durationSeconds", what);
                    modifiers = Modifier.ReadList(item.Get("modifiers"), what);
                    if (modifiers.Length == 0)
                    {
                        throw item.Get("modifiers").Error(what + "a rally needs at least one modifier.");
                    }
                    break;
                case AbilityType.AreaDamage:
                    amount = UnitRoster.ReadStat(item, "damage", what, allowZero: true);
                    multiplier = item.TryGet(KeyMultiplier, out _)
                        ? UnitRoster.ReadStat(item, KeyMultiplier, what, allowZero: true)
                        : SpellBook.DefaultStructureDamageMultiplier;
                    break;
                default:
                    amount = UnitRoster.ReadStat(item, "amount", what, allowZero: false);
                    break;
            }
            return new LeaderAbilityDefinition(displayName, type, radius, range, cooldown, duration, modifiers, amount,
                multiplier);
        }

        private static Fix ReadSeconds(JsonValue item, string key, string what)
        {
            Fix value = UnitRoster.ReadStat(item, key, what, allowZero: false);
            if (value > Fix.FromInt(SpellBook.MaxSeconds))
            {
                throw item.Get(key).Error(what + key + " must be at most " + SpellBook.MaxSeconds + ".");
            }
            return value;
        }
    }
}
