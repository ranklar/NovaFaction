using System;
using System.Collections.Generic;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Content
{
    /// <summary>One spell card from a faction's spells.json. Immutable.</summary>
    public sealed class SpellDefinition : CardDefinition
    {
        internal SpellDefinition(int index, string id, string displayName, int cost, Fix radius, Fix damage,
            Fix castDelaySeconds, Fix durationSeconds, Fix zoneTickSeconds, TargetLayer targets,
            Fix structureDamageMultiplier, bool isPlaceholder)
            : base(index, id, displayName, cost, isPlaceholder)
        {
            Radius = radius;
            Damage = damage;
            CastDelaySeconds = castDelaySeconds;
            DurationSeconds = durationSeconds;
            ZoneTickSeconds = zoneTickSeconds;
            Targets = targets;
            StructureDamageMultiplier = structureDamageMultiplier;
        }

        public override CardKind Kind => CardKind.Spell;

        public override UnitSlot Slot => UnitSlot.Spell;

        /// <summary>Everything whose center (units) or footprint (structures) is this close to the target is hit.</summary>
        public Fix Radius { get; }

        /// <summary>Damage to each enemy unit per hit (per pulse for a zone).</summary>
        public Fix Damage { get; }

        /// <summary>Time from the cast until the spell lands (a whole number of ticks).</summary>
        public Fix CastDelaySeconds { get; }

        /// <summary>0 = instant: one hit when it lands. Otherwise the zone lasts this long (a whole number of ticks).</summary>
        public Fix DurationSeconds { get; }

        /// <summary>Time between a zone's pulses (a whole number of ticks); 0 for instant spells.</summary>
        public Fix ZoneTickSeconds { get; }

        public bool IsZone => DurationSeconds > Fix.Zero;

        /// <summary>Which units it hurts. Structures are hurt unless it is Air only.</summary>
        public TargetLayer Targets { get; }

        /// <summary>Structures take Damage times this.</summary>
        public Fix StructureDamageMultiplier { get; }

        public Fix StructureDamage => Damage * StructureDamageMultiplier;

        internal override void AppendHash(ref StateHasher h)
        {
            h.Add(Id);
            h.Add(DisplayName);
            h.Add(Cost);
            h.Add(Radius);
            h.Add(Damage);
            h.Add(CastDelaySeconds);
            h.Add(DurationSeconds);
            h.Add(ZoneTickSeconds);
            h.Add((int)Targets);
            h.Add(StructureDamageMultiplier);
        }
    }

    /// <summary>
    /// A faction's spell list, loaded from content/factions/&lt;faction&gt;/spells.json. Immutable.
    /// See docs/design.md ("Spells") for the file format.
    /// </summary>
    public sealed class SpellBook
    {
        public const int FormatVersion = 1;
        public const string DefaultSourceName = "spells.json";

        /// <summary>structureDamageMultiplier when the file leaves it out (a placeholder; see the design doc).</summary>
        public static readonly Fix DefaultStructureDamageMultiplier = Fix.Parse("0.35");

        /// <summary>Upper bound for spell times in seconds.</summary>
        public const int MaxSeconds = 600;

        private const string KeyFormatVersion = "formatVersion";
        private const string KeyFaction = "faction";
        private const string KeySpells = "spells";
        private static readonly string[] RootKeys = { KeyFormatVersion, KeyFaction, KeySpells };

        private const string KeyZoneTick = "zoneTickSeconds";
        private const string KeyMultiplier = "structureDamageMultiplier";
        private static readonly string[] SpellKeys =
        {
            "id", "displayName", "cost", "radius", "damage", "castDelaySeconds", "durationSeconds", "targets",
        };
        private static readonly string[] OptionalSpellKeys = { "placeholder", KeyZoneTick, KeyMultiplier };

        private SpellDefinition[] _spells = Array.Empty<SpellDefinition>();

        private SpellBook(string faction)
        {
            Faction = faction;
        }

        /// <summary>Faction id; must match the faction's units.json.</summary>
        public string Faction { get; }

        /// <summary>All spells in file order.</summary>
        public IReadOnlyList<SpellDefinition> Spells => _spells;

        /// <summary>Fingerprint of the parsed data (not the file bytes).</summary>
        public ulong ContentHash { get; private set; }

        /// <summary>A faction with no spell cards.</summary>
        public static SpellBook Empty(string faction)
        {
            var book = new SpellBook(faction);
            book.ContentHash = book.ComputeContentHash();
            return book;
        }

        public bool TryGet(string id, out SpellDefinition definition)
        {
            foreach (SpellDefinition s in _spells)
            {
                if (s.Id == id)
                {
                    definition = s;
                    return true;
                }
            }
            definition = null!;
            return false;
        }

        public SpellDefinition Get(string id)
        {
            if (!TryGet(id, out SpellDefinition definition))
            {
                throw new KeyNotFoundException("Faction \"" + Faction + "\" has no spell \"" + id + "\".");
            }
            return definition;
        }

        /// <summary>Parses and validates a spells file. Throws <see cref="SimJsonException"/> on any problem.</summary>
        public static SpellBook FromJson(string json, string sourceName = DefaultSourceName) =>
            Parse(json, sourceName, null);

        /// <summary>
        /// Parses a spells file; with <paramref name="units"/>, also rejects a spell whose id is already a unit id
        /// or whose faction differs, pointing at the offending spot in the spells file.
        /// </summary>
        internal static SpellBook Parse(string json, string sourceName, UnitRoster? units)
        {
            JsonValue root = SimJson.Parse(json, sourceName);
            if (root.Kind != JsonKind.Object)
            {
                throw root.Error("the spells file must be a JSON object.");
            }
            UnitRoster.CheckKeys(root, RootKeys, Array.Empty<string>(), "spells file");

            JsonValue version = root.Get(KeyFormatVersion);
            if (version.AsInt() != FormatVersion)
            {
                throw version.Error("formatVersion must be " + FormatVersion + " but is " + version.NumberText + ".");
            }

            JsonValue factionValue = root.Get(KeyFaction);
            string faction = factionValue.AsString();
            if (!MapDefinition.IsValidId(faction))
            {
                throw factionValue.Error("faction must be 1-64 characters of a-z, 0-9, '_' or '-'.");
            }
            if (units != null && units.Faction != faction)
            {
                throw factionValue.Error("faction \"" + faction + "\" does not match the units file's faction \""
                    + units.Faction + "\".");
            }

            IReadOnlyList<JsonValue> items = root.Get(KeySpells).AsArray();
            var spells = new SpellDefinition[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                spells[i] = ReadSpell(items[i], i);
                for (int j = 0; j < i; j++)
                {
                    if (spells[j].Id == spells[i].Id)
                    {
                        throw items[i].Get("id").Error("duplicate spell id \"" + spells[i].Id + "\".");
                    }
                }
                if (units != null && units.TryGet(spells[i].Id, out _))
                {
                    throw items[i].Get("id").Error("card id \"" + spells[i].Id
                        + "\" is already a unit; card ids must be unique across units and spells.");
                }
            }
            var book = new SpellBook(faction) { _spells = spells };
            book.ContentHash = book.ComputeContentHash();
            return book;
        }

        private static SpellDefinition ReadSpell(JsonValue item, int index)
        {
            if (item.Kind != JsonKind.Object)
            {
                throw item.Error("each spell must be a JSON object.");
            }
            UnitRoster.CheckKeys(item, SpellKeys, OptionalSpellKeys, "spell");

            string id = UnitRoster.ReadId(item);
            string what = "spell \"" + id + "\": ";
            string displayName = UnitRoster.ReadDisplayName(item, what);
            int cost = UnitRoster.ReadCost(item, what);
            Fix radius = UnitRoster.ReadStat(item, "radius", what, allowZero: false);
            Fix damage = UnitRoster.ReadStat(item, "damage", what, allowZero: true);
            Fix castDelay = ReadSeconds(item, "castDelaySeconds", what, allowZero: true);
            Fix duration = ReadSeconds(item, "durationSeconds", what, allowZero: true);
            var targets = (TargetLayer)UnitRoster.ReadName(item.Get("targets"), UnitRoster.TargetNames, what + "targets");

            Fix zoneTick = Fix.Zero;
            bool hasZoneTick = item.TryGet(KeyZoneTick, out JsonValue zoneTickValue);
            if (duration > Fix.Zero)
            {
                if (!hasZoneTick)
                {
                    throw item.Error(what + "a zone spell (durationSeconds > 0) needs \"" + KeyZoneTick + "\".");
                }
                zoneTick = ReadSeconds(item, KeyZoneTick, what, allowZero: false);
                if (zoneTick > duration)
                {
                    throw zoneTickValue.Error(what + KeyZoneTick + " must not exceed durationSeconds.");
                }
            }
            else if (hasZoneTick)
            {
                throw zoneTickValue.Error(what + KeyZoneTick + " is only allowed when durationSeconds is greater than 0.");
            }

            Fix multiplier = item.TryGet(KeyMultiplier, out _)
                ? UnitRoster.ReadStat(item, KeyMultiplier, what, allowZero: true)
                : DefaultStructureDamageMultiplier;

            bool placeholder = item.TryGet("placeholder", out JsonValue p) && p.AsBool();
            return new SpellDefinition(index, id, displayName, cost, radius, damage, castDelay, duration, zoneTick,
                targets, multiplier, placeholder);
        }

        private static Fix ReadSeconds(JsonValue item, string key, string what, bool allowZero)
        {
            Fix value = UnitRoster.ReadStat(item, key, what, allowZero);
            if (value > Fix.FromInt(MaxSeconds))
            {
                throw item.Get(key).Error(what + key + " must be at most " + MaxSeconds + ".");
            }
            return value;
        }

        private ulong ComputeContentHash()
        {
            var h = new StateHasher();
            h.Add(FormatVersion);
            h.Add(Faction);
            h.Add(_spells.Length);
            foreach (SpellDefinition s in _spells)
            {
                s.AppendHash(ref h);
            }
            return h.Value;
        }
    }
}
