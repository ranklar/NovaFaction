using System;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Bots
{
    /// <summary>
    /// How a bot plays (content/bots/&lt;id&gt;.json). Every key is required apart from "placeholder"; unknown keys are
    /// errors. The five weights and decisionQuality are 0..1; reactionDelaySeconds is above 0 and at most 10 (and must
    /// be whole ticks, which <see cref="MatchSetup"/> checks); goldReserve is 0..1000. Immutable.
    /// </summary>
    public sealed class BotPersonality
    {
        public const int FormatVersion = 1;
        public const int MaxReactionDelaySeconds = 10;
        public const int MaxGoldReserve = 1000;

        private static readonly string[] Keys =
        {
            "formatVersion", "id", "reactionDelaySeconds", "decisionQuality", "aggression", "defensiveness", "mineFocus",
            "spellUsage", "goldReserve",
        };
        private static readonly string[] OptionalKeys = { "placeholder" };

        private BotPersonality()
        {
            Id = "";
        }

        public string Id { get; private set; }

        /// <summary>Seconds between decisions. The bot looks at the match and acts once per delay.</summary>
        public Fix ReactionDelaySeconds { get; private set; }

        /// <summary>Probability of taking the best-scoring action; otherwise one of the top three at random.</summary>
        public Fix DecisionQuality { get; private set; }

        /// <summary>Willingness to attack with less than a full wallet (0 = saves up, 1 = spends at once).</summary>
        public Fix Aggression { get; private set; }

        /// <summary>How small a threat the bot answers (1 = anything on its half).</summary>
        public Fix Defensiveness { get; private set; }

        /// <summary>Weight of sending capturers toward mines it does not own.</summary>
        public Fix MineFocus { get; private set; }

        /// <summary>Willingness to cast spells (1 = casts on a target worth half the spell's cost).</summary>
        public Fix SpellUsage { get; private set; }

        /// <summary>Gold the bot keeps in hand for defense; attacks and mine drops never spend it.</summary>
        public Fix GoldReserve { get; private set; }

        public bool IsPlaceholder { get; private set; }

        /// <summary>Fingerprint of the parsed values (not the placeholder flag). Part of a replay's content version.</summary>
        public ulong ContentHash { get; private set; }

        /// <summary>Parses and validates a bot file. Throws <see cref="SimJsonException"/> on any problem.</summary>
        public static BotPersonality FromJson(string json, string sourceName = "bot.json")
        {
            JsonValue root = SimJson.Parse(json, sourceName);
            if (root.Kind != JsonKind.Object)
            {
                throw root.Error("the bot file must be a JSON object.");
            }
            UnitRoster.CheckKeys(root, Keys, OptionalKeys, "bot file");
            JsonValue version = root.Get("formatVersion");
            if (version.AsInt() != FormatVersion)
            {
                throw version.Error("formatVersion must be " + FormatVersion + " but is " + version.NumberText + ".");
            }
            var bot = new BotPersonality
            {
                Id = UnitRoster.ReadId(root),
                ReactionDelaySeconds = Read(root, "reactionDelaySeconds", Fix.Epsilon, Fix.FromInt(MaxReactionDelaySeconds)),
                DecisionQuality = Read01(root, "decisionQuality"),
                Aggression = Read01(root, "aggression"),
                Defensiveness = Read01(root, "defensiveness"),
                MineFocus = Read01(root, "mineFocus"),
                SpellUsage = Read01(root, "spellUsage"),
                GoldReserve = Read(root, "goldReserve", Fix.Zero, Fix.FromInt(MaxGoldReserve)),
                IsPlaceholder = root.TryGet("placeholder", out JsonValue p) && p.AsBool(),
            };
            bot.ContentHash = bot.ComputeContentHash();
            return bot;
        }

        private ulong ComputeContentHash()
        {
            var h = new StateHasher();
            h.Add(FormatVersion);
            h.Add(Id);
            h.Add(ReactionDelaySeconds);
            h.Add(DecisionQuality);
            h.Add(Aggression);
            h.Add(Defensiveness);
            h.Add(MineFocus);
            h.Add(SpellUsage);
            h.Add(GoldReserve);
            return h.Value;
        }

        private static Fix Read01(JsonValue root, string key) => Read(root, key, Fix.Zero, Fix.One);

        private static Fix Read(JsonValue root, string key, Fix min, Fix max)
        {
            JsonValue value = root.Get(key);
            Fix result = value.AsFix();
            if (result < min || result > max)
            {
                string low = min == Fix.Epsilon ? "above 0" : "at least " + min;
                throw value.Error(key + " must be " + low + " and at most " + max + " but is " + value.NumberText + ".");
            }
            return result;
        }
    }
}
