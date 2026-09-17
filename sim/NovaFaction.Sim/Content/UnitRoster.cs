using System;
using System.Collections.Generic;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Content
{
    /// <summary>The ten archetype jobs every faction fills (see docs/design.md, "Factions").</summary>
    public enum UnitSlot
    {
        Tank = 0,
        Bruiser = 1,
        Swarm = 2,
        Ranged = 3,
        Flyer = 4,
        Siege = 5,
        Support = 6,
        Spell = 7,
        Building = 8,
        Leader = 9,
    }

    /// <summary>What a unit can attack.</summary>
    public enum TargetLayer
    {
        Ground = 0,
        Air = 1,
        Both = 2,
    }

    /// <summary>What a unit looks for when it scans for something to attack.</summary>
    public enum TargetPriority
    {
        /// <summary>The nearest enemy unit or structure it can hit.</summary>
        Any = 0,
        /// <summary>Only structures (e.g. a siege engine or a building-hunting tank).</summary>
        StructuresOnly = 1,
    }

    /// <summary>One unit card from a faction's units.json. Immutable.</summary>
    public sealed class UnitDefinition : CardDefinition
    {
        private readonly Modifier[] _passive;

        internal UnitDefinition(int index, string id, string displayName, UnitSlot slot, int cost, Fix hp, Fix damage,
            Fix attackIntervalSeconds, Fix range, Fix moveSpeed, TargetLayer targets, TargetPriority targetPriority,
            bool isFlying, int spawnCount, Fix projectileSpeed, Fix splashRadius, bool isLeader, bool canCapture,
            bool isPlaceholder, Modifier[]? passive = null, LeaderAbilityDefinition? ability = null)
            : base(index, id, displayName, cost, isPlaceholder)
        {
            _passive = passive ?? Array.Empty<Modifier>();
            Ability = ability;
            Slot = slot;
            Hp = hp;
            Damage = damage;
            AttackIntervalSeconds = attackIntervalSeconds;
            Range = range;
            MoveSpeed = moveSpeed;
            Targets = targets;
            TargetPriority = targetPriority;
            IsFlying = isFlying;
            SpawnCount = spawnCount;
            ProjectileSpeed = projectileSpeed;
            SplashRadius = splashRadius;
            IsLeader = isLeader;
            CanCapture = canCapture;
        }

        public override CardKind Kind => CardKind.Unit;

        public override UnitSlot Slot { get; }

        /// <summary>Maximum hit points of each spawned unit.</summary>
        public Fix Hp { get; }

        /// <summary>Damage per attack.</summary>
        public Fix Damage { get; }

        public Fix AttackIntervalSeconds { get; }

        /// <summary>
        /// Reach in world units, measured from the unit's center to the nearest point of the target's
        /// footprint. Always greater than zero; melee units use a small value.
        /// </summary>
        public Fix Range { get; }

        /// <summary>World units per second.</summary>
        public Fix MoveSpeed { get; }

        public TargetLayer Targets { get; }

        /// <summary>Whether the unit also targets enemy units, or only structures.</summary>
        public TargetPriority TargetPriority { get; }

        /// <summary>Flying units ignore terrain and move in straight lines.</summary>
        public bool IsFlying { get; }

        /// <summary>Units created per deploy (1 for most, more for swarms).</summary>
        public int SpawnCount { get; }

        /// <summary>
        /// World units per second of the unit's projectile. Zero (the default when the file omits it) means
        /// melee: damage lands the moment the unit attacks.
        /// </summary>
        public Fix ProjectileSpeed { get; }

        /// <summary>Radius of area damage around the impact point; zero (the default) hits only the target.</summary>
        public Fix SplashRadius { get; }

        public bool IsRanged => ProjectileSpeed > Fix.Zero;

        public override bool IsLeader { get; }

        /// <summary>
        /// Whether the unit stops to capture a gold mine it passes (docs/design.md "Map gold"). Defaults to true for
        /// ground units that target anything; flyers and structures-only units can never capture.
        /// </summary>
        public bool CanCapture { get; }

        /// <summary>
        /// Leaders only: the faction passive, applied to all of the owner's units for the whole match whether or not
        /// the leader is on the field. Empty for other units.
        /// </summary>
        public IReadOnlyList<Modifier> Passive => _passive;

        /// <summary>Leaders only: the active ability used with the LeaderAbility command, or null.</summary>
        public LeaderAbilityDefinition? Ability { get; }

        internal override void AppendHash(ref StateHasher h)
        {
            h.Add(Id);
            h.Add(DisplayName);
            h.Add((int)Slot);
            h.Add(Cost);
            h.Add(Hp);
            h.Add(Damage);
            h.Add(AttackIntervalSeconds);
            h.Add(Range);
            h.Add(MoveSpeed);
            h.Add((int)Targets);
            h.Add((int)TargetPriority);
            h.Add(IsFlying);
            h.Add(SpawnCount);
            h.Add(ProjectileSpeed);
            h.Add(SplashRadius);
            h.Add(IsLeader);
            h.Add(CanCapture);
            Modifier.AppendHash(ref h, _passive);
            h.Add(Ability != null);
            Ability?.AppendHash(ref h);
        }
    }

    /// <summary>
    /// A faction's unit list, loaded from content/factions/&lt;faction&gt;/units.json. Immutable.
    /// See docs/design.md ("Units, decks and movement") for the file format.
    /// </summary>
    public sealed class UnitRoster
    {
        public const int FormatVersion = 1;
        public const int MinCost = 1;
        public const int MaxCost = 7;
        public const int MaxSpawnCount = 25;
        /// <summary>Upper bound for hp, damage, range, speed and interval, to keep fixed-point math far from overflow.</summary>
        public const int MaxStatValue = 1_000_000;

        private const string KeyFormatVersion = "formatVersion";
        private const string KeyFaction = "faction";
        private const string KeyUnits = "units";
        private static readonly string[] RootKeys = { KeyFormatVersion, KeyFaction, KeyUnits };

        private const string KeyPlaceholder = "placeholder";
        private const string KeyProjectileSpeed = "projectileSpeed";
        private const string KeySplashRadius = "splashRadius";
        private const string KeyCanCapture = "canCapture";
        private const string KeyPassive = "passive";
        private const string KeyAbility = "ability";
        private static readonly string[] UnitKeys =
        {
            "id", "displayName", "slot", "cost", "hp", "damage", "attackIntervalSeconds", "range", "moveSpeed",
            "targets", "targetPriority", "isFlying", "spawnCount", "isLeader",
        };
        private static readonly string[] OptionalUnitKeys =
        {
            KeyPlaceholder, KeyProjectileSpeed, KeySplashRadius, KeyCanCapture, KeyPassive, KeyAbility,
        };

        private static readonly string[] SlotNames =
            { "tank", "bruiser", "swarm", "ranged", "flyer", "siege", "support", "spell", "building", "leader" };

        internal static readonly string[] TargetNames = { "ground", "air", "both" };

        private static readonly string[] PriorityNames = { "any", "structuresOnly" };

        private UnitDefinition[] _units = Array.Empty<UnitDefinition>();

        private UnitRoster()
        {
            Faction = "";
        }

        /// <summary>Faction id, e.g. "fantasy".</summary>
        public string Faction { get; private set; }

        /// <summary>All units in file order.</summary>
        public IReadOnlyList<UnitDefinition> Units => _units;

        /// <summary>Fingerprint of the parsed data (not the file bytes). Folded into the match state hash.</summary>
        public ulong ContentHash { get; private set; }

        public bool TryGet(string id, out UnitDefinition definition)
        {
            foreach (UnitDefinition u in _units)
            {
                if (u.Id == id)
                {
                    definition = u;
                    return true;
                }
            }
            definition = null!;
            return false;
        }

        public UnitDefinition Get(string id)
        {
            if (!TryGet(id, out UnitDefinition definition))
            {
                throw new KeyNotFoundException("Faction \"" + Faction + "\" has no unit \"" + id + "\".");
            }
            return definition;
        }

        /// <summary>Parses and validates a units file. Throws <see cref="SimJsonException"/> on any problem.</summary>
        public static UnitRoster FromJson(string json, string sourceName = "units.json")
        {
            JsonValue root = SimJson.Parse(json, sourceName);
            if (root.Kind != JsonKind.Object)
            {
                throw root.Error("the units file must be a JSON object.");
            }
            CheckKeys(root, RootKeys, Array.Empty<string>(), "units file");

            JsonValue version = root.Get(KeyFormatVersion);
            if (version.AsInt() != FormatVersion)
            {
                throw version.Error("formatVersion must be " + FormatVersion + " but is " + version.NumberText + ".");
            }

            var roster = new UnitRoster();
            JsonValue faction = root.Get(KeyFaction);
            roster.Faction = faction.AsString();
            if (!MapDefinition.IsValidId(roster.Faction))
            {
                throw faction.Error("faction must be 1-64 characters of a-z, 0-9, '_' or '-'.");
            }

            IReadOnlyList<JsonValue> items = root.Get(KeyUnits).AsArray();
            if (items.Count == 0)
            {
                throw root.Get(KeyUnits).Error("units must not be empty.");
            }
            var units = new UnitDefinition[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                units[i] = ReadUnit(items[i], i);
                for (int j = 0; j < i; j++)
                {
                    if (units[j].Id == units[i].Id)
                    {
                        throw items[i].Get("id").Error("duplicate unit id \"" + units[i].Id + "\".");
                    }
                }
            }
            roster._units = units;
            roster.ContentHash = roster.ComputeContentHash();
            return roster;
        }

        private static UnitDefinition ReadUnit(JsonValue item, int index)
        {
            if (item.Kind != JsonKind.Object)
            {
                throw item.Error("each unit must be a JSON object.");
            }
            CheckKeys(item, UnitKeys, OptionalUnitKeys, "unit");

            string id = ReadId(item);
            string what = "unit \"" + id + "\": ";
            string displayName = ReadDisplayName(item, what);

            JsonValue slotValue = item.Get("slot");
            var slot = (UnitSlot)ReadName(slotValue, SlotNames, what + "slot");
            if (slot == UnitSlot.Spell)
            {
                throw slotValue.Error(what + "slot \"spell\" is for spell cards, which belong in spells.json.");
            }
            var targets = (TargetLayer)ReadName(item.Get("targets"), TargetNames, what + "targets");
            var priority = (TargetPriority)ReadName(item.Get("targetPriority"), PriorityNames, what + "targetPriority");

            int cost = ReadCost(item, what);

            Fix hp = ReadStat(item, "hp", what, allowZero: false);
            Fix damage = ReadStat(item, "damage", what, allowZero: true);
            Fix interval = ReadStat(item, "attackIntervalSeconds", what, allowZero: false);
            Fix range = ReadStat(item, "range", what, allowZero: false);
            Fix moveSpeed = ReadStat(item, "moveSpeed", what, allowZero: true);
            Fix projectileSpeed = item.TryGet(KeyProjectileSpeed, out _)
                ? ReadStat(item, KeyProjectileSpeed, what, allowZero: false)
                : Fix.Zero;
            Fix splashRadius = item.TryGet(KeySplashRadius, out _)
                ? ReadStat(item, KeySplashRadius, what, allowZero: false)
                : Fix.Zero;

            JsonValue countValue = item.Get("spawnCount");
            int spawnCount = countValue.AsInt();
            if (spawnCount < 1 || spawnCount > MaxSpawnCount)
            {
                throw countValue.Error(what + "spawnCount must be between 1 and " + MaxSpawnCount + " but is " + spawnCount + ".");
            }

            bool isFlying = item.Get("isFlying").AsBool();
            JsonValue leaderValue = item.Get("isLeader");
            bool isLeader = leaderValue.AsBool();
            if (isLeader != (slot == UnitSlot.Leader))
            {
                throw leaderValue.Error(what + "isLeader must be true exactly when slot is \"leader\".");
            }

            // Only ground units that fight anything stop at mines; a flyer or a structure hunter never does.
            bool mayCapture = !isFlying && priority == TargetPriority.Any;
            bool canCapture = mayCapture;
            if (item.TryGet(KeyCanCapture, out JsonValue captureValue))
            {
                canCapture = captureValue.AsBool();
                if (canCapture && !mayCapture)
                {
                    throw captureValue.Error(what + "canCapture may only be true for ground units with targetPriority \"any\".");
                }
            }

            // The faction passive and the active ability belong to leaders only; both are optional.
            Modifier[]? passive = null;
            if (item.TryGet(KeyPassive, out JsonValue passiveValue))
            {
                if (!isLeader)
                {
                    throw passiveValue.Error(what + "only a leader may have a passive.");
                }
                passive = Modifier.ReadList(passiveValue, what + "passive: ");
            }
            LeaderAbilityDefinition? ability = null;
            if (item.TryGet(KeyAbility, out JsonValue abilityValue))
            {
                if (!isLeader)
                {
                    throw abilityValue.Error(what + "only a leader may have an ability.");
                }
                ability = LeaderAbilityDefinition.Read(abilityValue, what);
            }

            bool placeholder = item.TryGet(KeyPlaceholder, out JsonValue p) && p.AsBool();
            return new UnitDefinition(index, id, displayName, slot, cost, hp, damage, interval, range, moveSpeed,
                targets, priority, isFlying, spawnCount, projectileSpeed, splashRadius, isLeader, canCapture, placeholder,
                passive, ability);
        }

        internal static int ReadCost(JsonValue item, string what)
        {
            JsonValue costValue = item.Get("cost");
            int cost = costValue.AsInt();
            if (cost < MinCost || cost > MaxCost)
            {
                throw costValue.Error(what + "cost must be between " + MinCost + " and " + MaxCost + " but is " + cost + ".");
            }
            return cost;
        }

        internal static string ReadId(JsonValue item)
        {
            JsonValue idValue = item.Get("id");
            string id = idValue.AsString();
            if (!MapDefinition.IsValidId(id))
            {
                throw idValue.Error("id must be 1-64 characters of a-z, 0-9, '_' or '-'.");
            }
            return id;
        }

        internal static string ReadDisplayName(JsonValue item, string what)
        {
            JsonValue nameValue = item.Get("displayName");
            string displayName = nameValue.AsString();
            if (displayName.Trim().Length == 0)
            {
                throw nameValue.Error(what + "displayName must not be empty.");
            }
            return displayName;
        }

        internal static int ReadName(JsonValue value, string[] names, string what)
        {
            string text = value.AsString();
            int i = Array.IndexOf(names, text);
            if (i < 0)
            {
                throw value.Error(what + " must be one of " + string.Join(", ", names) + " but is \"" + text + "\".");
            }
            return i;
        }

        internal static Fix ReadStat(JsonValue item, string key, string what, bool allowZero)
        {
            JsonValue value = item.Get(key);
            Fix result = value.AsFix();
            if (result < Fix.Zero || (!allowZero && result == Fix.Zero))
            {
                throw value.Error(what + key + (allowZero ? " must not be negative." : " must be greater than 0."));
            }
            if (result > Fix.FromInt(MaxStatValue))
            {
                throw value.Error(what + key + " must be at most " + MaxStatValue + ".");
            }
            return result;
        }

        internal static void CheckKeys(JsonValue obj, string[] required, string[] optional, string what)
        {
            foreach (KeyValuePair<string, JsonValue> member in obj.Members)
            {
                if (Array.IndexOf(required, member.Key) < 0 && Array.IndexOf(optional, member.Key) < 0)
                {
                    throw member.Value.Error("unknown key \"" + member.Key + "\" in " + what + ".");
                }
            }
            foreach (string key in required)
            {
                if (!obj.TryGet(key, out _))
                {
                    throw obj.Error(what + " is missing required key \"" + key + "\".");
                }
            }
        }

        private ulong ComputeContentHash()
        {
            var h = new StateHasher();
            h.Add(FormatVersion);
            h.Add(Faction);
            h.Add(_units.Length);
            foreach (UnitDefinition u in _units)
            {
                u.AppendHash(ref h);
            }
            return h.Value;
        }
    }
}
