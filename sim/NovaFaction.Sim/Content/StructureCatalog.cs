using System;
using System.Collections.Generic;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Content
{
    /// <summary>Combat stats shared by every structure of one kind (Keep or forward tower). Immutable.</summary>
    public sealed class StructureStats
    {
        internal StructureStats(StructureKind kind, Fix hp, Fix damage, Fix attackIntervalSeconds, Fix range,
            TargetLayer targets, Fix projectileSpeed, Fix destructionBonus, bool isPlaceholder)
        {
            Kind = kind;
            Hp = hp;
            Damage = damage;
            AttackIntervalSeconds = attackIntervalSeconds;
            Range = range;
            Targets = targets;
            ProjectileSpeed = projectileSpeed;
            DestructionBonus = destructionBonus;
            IsPlaceholder = isPlaceholder;
        }

        public StructureKind Kind { get; }

        /// <summary>Starting (and maximum) hit points.</summary>
        public Fix Hp { get; }

        public Fix Damage { get; }

        public Fix AttackIntervalSeconds { get; }

        /// <summary>Reach in world units, from the nearest point of the footprint to the target unit's center.</summary>
        public Fix Range { get; }

        public TargetLayer Targets { get; }

        /// <summary>World units per second. Structures always shoot projectiles.</summary>
        public Fix ProjectileSpeed { get; }

        /// <summary>Score the attacker earns when this structure is destroyed (on top of the HP removed).</summary>
        public Fix DestructionBonus { get; }

        public bool IsPlaceholder { get; }

        internal void AppendHash(ref StateHasher h)
        {
            h.Add((int)Kind);
            h.Add(Hp);
            h.Add(Damage);
            h.Add(AttackIntervalSeconds);
            h.Add(Range);
            h.Add((int)Targets);
            h.Add(ProjectileSpeed);
            h.Add(DestructionBonus);
        }
    }

    /// <summary>
    /// Structure stats from content/structures.json: one entry for "keep" and one for "tower" (the same
    /// kind names the map files use). Every key is required apart from "placeholder"; unknown keys are errors.
    /// </summary>
    public sealed class StructureCatalog
    {
        public const int FormatVersion = 1;
        public const string DefaultSourceName = "structures.json";

        private const string KeyFormatVersion = "formatVersion";
        private const string KeyStructures = "structures";
        private static readonly string[] RootKeys = { KeyFormatVersion, KeyStructures };

        private static readonly string[] EntryKeys =
        {
            "kind", "hp", "damage", "attackIntervalSeconds", "range", "targets", "projectileSpeed", "destructionBonus",
        };
        private static readonly string[] OptionalEntryKeys = { "placeholder" };

        private static readonly string[] KindNames = { "keep", "tower" };

        private readonly StructureStats[] _byKind = new StructureStats[2];

        private StructureCatalog()
        {
        }

        public StructureStats Keep => _byKind[(int)StructureKind.Keep];

        public StructureStats ForwardTower => _byKind[(int)StructureKind.ForwardTower];

        /// <summary>Fingerprint of the parsed data. Folded into the match state hash.</summary>
        public ulong ContentHash { get; private set; }

        public StructureStats Get(StructureKind kind) => _byKind[(int)kind];

        /// <summary>Parses and validates a structures file. Throws <see cref="SimJsonException"/> on any problem.</summary>
        public static StructureCatalog FromJson(string json, string sourceName = DefaultSourceName)
        {
            JsonValue root = SimJson.Parse(json, sourceName);
            if (root.Kind != JsonKind.Object)
            {
                throw root.Error("the structures file must be a JSON object.");
            }
            UnitRoster.CheckKeys(root, RootKeys, Array.Empty<string>(), "structures file");
            JsonValue version = root.Get(KeyFormatVersion);
            if (version.AsInt() != FormatVersion)
            {
                throw version.Error("formatVersion must be " + FormatVersion + " but is " + version.NumberText + ".");
            }

            var catalog = new StructureCatalog();
            JsonValue list = root.Get(KeyStructures);
            foreach (JsonValue item in list.AsArray())
            {
                if (item.Kind != JsonKind.Object)
                {
                    throw item.Error("each structure entry must be a JSON object.");
                }
                UnitRoster.CheckKeys(item, EntryKeys, OptionalEntryKeys, "structure entry");
                var kind = (StructureKind)UnitRoster.ReadName(item.Get("kind"), KindNames, "kind");
                if (catalog._byKind[(int)kind] != null)
                {
                    throw item.Get("kind").Error("duplicate entry for kind \"" + KindNames[(int)kind] + "\".");
                }
                string what = "structure \"" + KindNames[(int)kind] + "\": ";
                catalog._byKind[(int)kind] = new StructureStats(
                    kind,
                    UnitRoster.ReadStat(item, "hp", what, allowZero: false),
                    UnitRoster.ReadStat(item, "damage", what, allowZero: true),
                    UnitRoster.ReadStat(item, "attackIntervalSeconds", what, allowZero: false),
                    UnitRoster.ReadStat(item, "range", what, allowZero: false),
                    (TargetLayer)UnitRoster.ReadName(item.Get("targets"), UnitRoster.TargetNames, what + "targets"),
                    UnitRoster.ReadStat(item, "projectileSpeed", what, allowZero: false),
                    UnitRoster.ReadStat(item, "destructionBonus", what, allowZero: true),
                    item.TryGet("placeholder", out JsonValue p) && p.AsBool());
            }
            for (int k = 0; k < KindNames.Length; k++)
            {
                if (catalog._byKind[k] == null)
                {
                    throw list.Error("missing entry for kind \"" + KindNames[k] + "\".");
                }
            }

            var h = new StateHasher();
            h.Add(FormatVersion);
            foreach (StructureStats stats in catalog._byKind)
            {
                stats.AppendHash(ref h);
            }
            catalog.ContentHash = h.Value;
            return catalog;
        }
    }
}
