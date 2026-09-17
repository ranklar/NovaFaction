using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Content
{
    /// <summary>
    /// Match-wide rules loaded from content/rules.json. Every key is required and unknown keys are
    /// errors, so a typo in the file fails loudly instead of silently using a default.
    /// <para>
    /// "tuningPlaceholders" is an optional list of key names whose values are guesses still to be
    /// tuned in the headless harness. JSON has no comments, so this is how the file marks them.
    /// </para>
    /// </summary>
    public sealed class MatchRules
    {
        public const string DefaultSourceName = "rules.json";

        // Key names, in file order. Every one is required.
        private const string KeyTicksPerSecond = "ticksPerSecond";
        private const string KeyMatchLengthSeconds = "matchLengthSeconds";
        private const string KeySuddenDeathSeconds = "suddenDeathSeconds";
        private const string KeySuddenDeathIncomeMultiplier = "suddenDeathIncomeMultiplier";
        private const string KeyGoldBaseIncomePerSecond = "goldBaseIncomePerSecond";
        private const string KeyGoldStartingAmount = "goldStartingAmount";
        private const string KeyGoldCap = "goldCap";
        private const string KeyDeploySpawnDelaySeconds = "deploySpawnDelaySeconds";
        private const string KeyHandSize = "handSize";
        private const string KeyDeckSize = "deckSize";
        private const string KeyUnitSeparationDistance = "unitSeparationDistance";
        private const string KeyUnitSeparationPushPerSecond = "unitSeparationPushPerSecond";
        private const string KeyUnitStoppedPushFactor = "unitStoppedPushFactor";
        private const string KeyUnitSpawnSpacing = "unitSpawnSpacing";
        private const string KeyAggroRadius = "aggroRadius";
        private const string KeyMeleeTargetCrowdPenalty = "meleeTargetCrowdPenalty";
        private const string KeyMineCaptureRadius = "mineCaptureRadius";
        private const string KeyMineCaptureSeconds = "mineCaptureSeconds";
        private const string KeyMineIncomePerSecond = "mineIncomePerSecond";
        private const string KeyMineIncomeCap = "mineIncomeCap";
        private const string KeyChestFirstSpawnSeconds = "chestFirstSpawnSeconds";
        private const string KeyChestSpawnIntervalSeconds = "chestSpawnIntervalSeconds";
        private const string KeyChestGold = "chestGold";
        private const string KeyChestCollectRadius = "chestCollectRadius";
        private const string KeyMineCaptureGiveUpSeconds = "mineCaptureGiveUpSeconds";
        private const string KeyMineCaptureRetrySeconds = "mineCaptureRetrySeconds";
        private const string KeyMaxUnitLevel = "maxUnitLevel";
        private const string KeyLevelStatBonusPerLevel = "levelStatBonusPerLevel";
        private const string KeyTuningPlaceholders = "tuningPlaceholders";

        private static readonly string[] RequiredKeys =
        {
            KeyTicksPerSecond, KeyMatchLengthSeconds, KeySuddenDeathSeconds, KeySuddenDeathIncomeMultiplier,
            KeyGoldBaseIncomePerSecond, KeyGoldStartingAmount, KeyGoldCap,
            KeyDeploySpawnDelaySeconds, KeyHandSize, KeyDeckSize,
            KeyUnitSeparationDistance, KeyUnitSeparationPushPerSecond, KeyUnitStoppedPushFactor, KeyUnitSpawnSpacing,
            KeyAggroRadius, KeyMeleeTargetCrowdPenalty,
            KeyMineCaptureRadius, KeyMineCaptureSeconds, KeyMineIncomePerSecond, KeyMineIncomeCap,
            KeyChestFirstSpawnSeconds, KeyChestSpawnIntervalSeconds, KeyChestGold, KeyChestCollectRadius,
            KeyMineCaptureGiveUpSeconds, KeyMineCaptureRetrySeconds, KeyMaxUnitLevel, KeyLevelStatBonusPerLevel,
        };

        private MatchRules()
        {
            TuningPlaceholders = Array.Empty<string>();
        }

        /// <summary>Simulation steps per second. The design fixes this at 20.</summary>
        public int TicksPerSecond { get; private set; }
        public int MatchLengthSeconds { get; private set; }
        public int SuddenDeathSeconds { get; private set; }
        /// <summary>Gold income is multiplied by this during sudden death (the design says doubled).</summary>
        public Fix SuddenDeathIncomeMultiplier { get; private set; }
        public Fix GoldBaseIncomePerSecond { get; private set; }
        public Fix GoldStartingAmount { get; private set; }
        public Fix GoldCap { get; private set; }
        public Fix DeploySpawnDelaySeconds { get; private set; }
        public int HandSize { get; private set; }
        public int DeckSize { get; private set; }
        /// <summary>Friendly units closer than this (world units, center to center) push each other apart.</summary>
        public Fix UnitSeparationDistance { get; private set; }
        /// <summary>Largest speed (world units per second) the separation push adds to a moving unit.</summary>
        public Fix UnitSeparationPushPerSecond { get; private set; }
        /// <summary>
        /// Share (0..1) of the separation push that a stopped (attacking or holding) unit exerts on a moving
        /// friend. Low values let arriving units squeeze past friends that are already fighting.
        /// </summary>
        public Fix UnitStoppedPushFactor { get; private set; }
        /// <summary>Distance between neighbors in the spawn pattern of a multi-unit card (world units).</summary>
        public Fix UnitSpawnSpacing { get; private set; }
        /// <summary>
        /// How far (world units, straight line) a unit looks for enemies to attack. A unit whose attack range
        /// is longer uses its range instead, so it never ignores an enemy it could already hit.
        /// </summary>
        public Fix AggroRadius { get; private set; }
        /// <summary>
        /// Melee units treat an enemy unit as this much further away (world units) for every friendly unit
        /// already targeting it, which spreads melee attackers over several enemies.
        /// </summary>
        public Fix MeleeTargetCrowdPenalty { get; private set; }
        /// <summary>
        /// A player's units within this distance (world units, unit center to mine cell center) count as present
        /// at a gold mine. Must be at least one cell on any map with mines, so units beside the mine count.
        /// </summary>
        public Fix MineCaptureRadius { get; private set; }
        /// <summary>How long one player must be alone at a mine to capture it (a whole number of ticks).</summary>
        public Fix MineCaptureSeconds { get; private set; }
        /// <summary>Extra income per second for each owned mine.</summary>
        public Fix MineIncomePerSecond { get; private set; }
        /// <summary>The most mine income per second one player can have, however many mines they own.</summary>
        public Fix MineIncomeCap { get; private set; }
        /// <summary>Match time of the first chest wave (a whole number of ticks; 0 = chests at the start).</summary>
        public Fix ChestFirstSpawnSeconds { get; private set; }
        /// <summary>Time between chest waves (a whole number of ticks, more than 0).</summary>
        public Fix ChestSpawnIntervalSeconds { get; private set; }
        /// <summary>Gold a chest gives the player who collects it (limited by the gold cap).</summary>
        public Fix ChestGold { get; private set; }
        /// <summary>A unit collects a chest when its center is within this distance of the chest cell's center.</summary>
        public Fix ChestCollectRadius { get; private set; }
        /// <summary>
        /// A capturing unit whose mine capture has not moved forward for this long gives up (a whole number of
        /// ticks, more than 0).
        /// </summary>
        public Fix MineCaptureGiveUpSeconds { get; private set; }
        /// <summary>After giving up, a unit ignores mines for this long (a whole number of ticks).</summary>
        public Fix MineCaptureRetrySeconds { get; private set; }
        /// <summary>Highest card and structure level (levels run 1..this).</summary>
        public int MaxUnitLevel { get; private set; }
        /// <summary>Hp and Damage grow by this share of the base value per level above 1.</summary>
        public Fix LevelStatBonusPerLevel { get; private set; }
        /// <summary>Keys whose values are placeholders awaiting tuning, in file order.</summary>
        public IReadOnlyList<string> TuningPlaceholders { get; private set; }

        public int MatchLengthTicks => MatchLengthSeconds * TicksPerSecond;
        public int SuddenDeathTicks => SuddenDeathSeconds * TicksPerSecond;
        /// <summary>Spawn delay in whole ticks (validated to convert exactly).</summary>
        public int DeploySpawnDelayTicks => ToTicks(DeploySpawnDelaySeconds);
        /// <summary>Mine capture time in whole ticks (validated to convert exactly; at least 1).</summary>
        public int MineCaptureTicks => ToTicks(MineCaptureSeconds);
        /// <summary>Tick of the first chest wave (validated to convert exactly).</summary>
        public int ChestFirstSpawnTick => ToTicks(ChestFirstSpawnSeconds);
        /// <summary>Ticks between chest waves (validated to convert exactly; at least 1).</summary>
        public int ChestSpawnIntervalTicks => ToTicks(ChestSpawnIntervalSeconds);

        /// <summary>Give-up time in whole ticks (validated to convert exactly; at least 1).</summary>
        public int MineCaptureGiveUpTicks => ToTicks(MineCaptureGiveUpSeconds);
        /// <summary>Mine-ignoring time after a give-up, in whole ticks (validated to convert exactly).</summary>
        public int MineCaptureRetryTicks => ToTicks(MineCaptureRetrySeconds);

        /// <summary>The Hp and Damage multiplier of a level (1 + bonus * (level - 1)).</summary>
        public Fix LevelFactor(int level) => StatMath.LevelFactor(LevelStatBonusPerLevel, level);

        private int ToTicks(Fix seconds) => Fix.FloorToInt(seconds * Fix.FromInt(TicksPerSecond));

        /// <summary>True when the time is a whole number of ticks at this tick rate (and not absurdly long).</summary>
        public bool IsWholeTicks(Fix seconds)
        {
            Fix ticks = seconds * Fix.FromInt(TicksPerSecond);
            return ticks == Fix.Floor(ticks) && ticks <= Fix.FromInt(1_000_000);
        }

        /// <summary>A time that <see cref="IsWholeTicks"/> accepts, in ticks.</summary>
        internal int SecondsToTicks(Fix seconds) => ToTicks(seconds);

        /// <summary>Parses and validates rules JSON. Throws <see cref="SimJsonException"/> on any problem.</summary>
        public static MatchRules FromJson(string json, string sourceName = DefaultSourceName)
        {
            JsonValue root = SimJson.Parse(json, sourceName);
            if (root.Kind != JsonKind.Object)
            {
                throw root.Error("the rules file must be a JSON object.");
            }

            foreach (KeyValuePair<string, JsonValue> member in root.Members)
            {
                if (member.Key != KeyTuningPlaceholders && Array.IndexOf(RequiredKeys, member.Key) < 0)
                {
                    throw member.Value.Error("unknown key \"" + member.Key + "\".");
                }
            }
            foreach (string key in RequiredKeys)
            {
                if (!root.TryGet(key, out _))
                {
                    throw new SimJsonException(sourceName, 0, 0, "missing required key \"" + key + "\".");
                }
            }

            var rules = new MatchRules
            {
                TicksPerSecond = RangeInt(root, KeyTicksPerSecond, 1, 1000),
                MatchLengthSeconds = RangeInt(root, KeyMatchLengthSeconds, 1, 3600),
                SuddenDeathSeconds = RangeInt(root, KeySuddenDeathSeconds, 0, 3600),
                SuddenDeathIncomeMultiplier = MinFix(root, KeySuddenDeathIncomeMultiplier, Fix.Zero),
                GoldBaseIncomePerSecond = MinFix(root, KeyGoldBaseIncomePerSecond, Fix.Zero),
                GoldStartingAmount = MinFix(root, KeyGoldStartingAmount, Fix.Zero),
                GoldCap = MinFix(root, KeyGoldCap, Fix.Epsilon),
                DeploySpawnDelaySeconds = MinFix(root, KeyDeploySpawnDelaySeconds, Fix.Zero),
                HandSize = RangeInt(root, KeyHandSize, 1, 64),
                DeckSize = RangeInt(root, KeyDeckSize, 1, 64),
                UnitSeparationDistance = MinFix(root, KeyUnitSeparationDistance, Fix.Epsilon),
                UnitSeparationPushPerSecond = MinFix(root, KeyUnitSeparationPushPerSecond, Fix.Zero),
                UnitStoppedPushFactor = MinFix(root, KeyUnitStoppedPushFactor, Fix.Zero),
                UnitSpawnSpacing = MinFix(root, KeyUnitSpawnSpacing, Fix.Zero),
                AggroRadius = MinFix(root, KeyAggroRadius, Fix.Zero),
                MeleeTargetCrowdPenalty = MinFix(root, KeyMeleeTargetCrowdPenalty, Fix.Zero),
                MineCaptureRadius = MinFix(root, KeyMineCaptureRadius, Fix.Epsilon),
                MineCaptureSeconds = MinFix(root, KeyMineCaptureSeconds, Fix.Epsilon),
                MineIncomePerSecond = MinFix(root, KeyMineIncomePerSecond, Fix.Zero),
                MineIncomeCap = MinFix(root, KeyMineIncomeCap, Fix.Zero),
                ChestFirstSpawnSeconds = MinFix(root, KeyChestFirstSpawnSeconds, Fix.Zero),
                ChestSpawnIntervalSeconds = MinFix(root, KeyChestSpawnIntervalSeconds, Fix.Epsilon),
                ChestGold = MinFix(root, KeyChestGold, Fix.Zero),
                ChestCollectRadius = MinFix(root, KeyChestCollectRadius, Fix.Epsilon),
                MineCaptureGiveUpSeconds = MinFix(root, KeyMineCaptureGiveUpSeconds, Fix.Epsilon),
                MineCaptureRetrySeconds = MinFix(root, KeyMineCaptureRetrySeconds, Fix.Zero),
                MaxUnitLevel = RangeInt(root, KeyMaxUnitLevel, 1, 100),
                LevelStatBonusPerLevel = MinFix(root, KeyLevelStatBonusPerLevel, Fix.Zero),
            };

            // Cross-field rules.
            if (rules.GoldCap > Fix.FromInt(1_000_000))
            {
                throw root.Get(KeyGoldCap).Error("goldCap must be at most 1000000.");
            }
            if (rules.GoldStartingAmount > rules.GoldCap)
            {
                throw root.Get(KeyGoldStartingAmount).Error("goldStartingAmount must not exceed goldCap.");
            }
            if (rules.GoldBaseIncomePerSecond > rules.GoldCap)
            {
                throw root.Get(KeyGoldBaseIncomePerSecond).Error("goldBaseIncomePerSecond must not exceed goldCap.");
            }
            if (rules.GoldBaseIncomePerSecond * rules.SuddenDeathIncomeMultiplier > rules.GoldCap)
            {
                throw root.Get(KeySuddenDeathIncomeMultiplier).Error(
                    "goldBaseIncomePerSecond * suddenDeathIncomeMultiplier must not exceed goldCap.");
            }
            if (rules.MineIncomePerSecond > rules.GoldCap)
            {
                throw root.Get(KeyMineIncomePerSecond).Error("mineIncomePerSecond must not exceed goldCap.");
            }
            if (rules.GoldBaseIncomePerSecond + rules.MineIncomeCap > rules.GoldCap)
            {
                throw root.Get(KeyMineIncomeCap).Error("goldBaseIncomePerSecond + mineIncomeCap must not exceed goldCap.");
            }
            // Sudden death multiplies all income, mine income included.
            if ((rules.GoldBaseIncomePerSecond + rules.MineIncomeCap) * rules.SuddenDeathIncomeMultiplier > rules.GoldCap)
            {
                throw root.Get(KeyMineIncomeCap).Error(
                    "(goldBaseIncomePerSecond + mineIncomeCap) * suddenDeathIncomeMultiplier must not exceed goldCap.");
            }
            if (rules.ChestGold > rules.GoldCap)
            {
                throw root.Get(KeyChestGold).Error("chestGold must not exceed goldCap.");
            }
            if (rules.UnitStoppedPushFactor > Fix.One)
            {
                throw root.Get(KeyUnitStoppedPushFactor).Error("unitStoppedPushFactor must be at most 1.");
            }
            if (rules.LevelStatBonusPerLevel > Fix.One)
            {
                throw root.Get(KeyLevelStatBonusPerLevel).Error("levelStatBonusPerLevel must be at most 1.");
            }
            if (rules.HandSize >= rules.DeckSize)
            {
                throw root.Get(KeyHandSize).Error("handSize must be smaller than deckSize (a next card must exist).");
            }
            foreach (string key in new[] { KeyDeploySpawnDelaySeconds, KeyMineCaptureSeconds, KeyChestFirstSpawnSeconds,
                KeyChestSpawnIntervalSeconds, KeyMineCaptureGiveUpSeconds, KeyMineCaptureRetrySeconds })
            {
                Fix ticks = root.Get(key).AsFix() * Fix.FromInt(rules.TicksPerSecond);
                if (ticks != Fix.Floor(ticks) || ticks > Fix.FromInt(1_000_000))
                {
                    throw root.Get(key).Error(key + " must be a whole number of ticks (a multiple of 1/ticksPerSecond).");
                }
            }

            foreach (string key in new[] { KeyUnitSeparationDistance, KeyUnitSeparationPushPerSecond, KeyUnitSpawnSpacing, KeyAggroRadius,
                KeyMeleeTargetCrowdPenalty, KeySuddenDeathIncomeMultiplier, KeyMineCaptureRadius, KeyChestCollectRadius })
            {
                if (root.Get(key).AsFix() > Fix.FromInt(64))
                {
                    throw root.Get(key).Error(key + " must be at most 64.");
                }
            }

            if (root.TryGet(KeyTuningPlaceholders, out JsonValue placeholders))
            {
                rules.TuningPlaceholders = ReadPlaceholders(placeholders);
            }
            return rules;
        }

        private static string[] ReadPlaceholders(JsonValue list)
        {
            IReadOnlyList<JsonValue> items = list.AsArray();
            var names = new string[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                string name = items[i].AsString();
                if (Array.IndexOf(RequiredKeys, name) < 0)
                {
                    throw items[i].Error("tuningPlaceholders names unknown key \"" + name + "\".");
                }
                if (Array.IndexOf(names, name, 0, i) >= 0)
                {
                    throw items[i].Error("tuningPlaceholders lists \"" + name + "\" twice.");
                }
                names[i] = name;
            }
            return names;
        }

        private static int RangeInt(JsonValue root, string key, int min, int max)
        {
            JsonValue value = root.Get(key);
            int result = value.AsInt();
            if (result < min || result > max)
            {
                throw value.Error(key + " must be between " + min + " and " + max + " but is " + result + ".");
            }
            return result;
        }

        private static Fix MinFix(JsonValue root, string key, Fix min)
        {
            JsonValue value = root.Get(key);
            Fix result = value.AsFix();
            if (result < min)
            {
                throw value.Error(key + " must be at least " + min + " but is " + result + ".");
            }
            return result;
        }
    }
}
