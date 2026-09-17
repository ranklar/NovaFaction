using System;
using System.Collections.Generic;
using NovaFaction.Sim.Bots;
using NovaFaction.Sim.Map;

namespace NovaFaction.Sim.Content
{
    /// <summary>
    /// All loaded game content, looked up by id: the rules, the structure stats, every map, every faction's cards and
    /// every bot personality. Replays name their content by id and rebuild the match from a library. The sim still
    /// takes JSON text, not file paths, so whoever builds the library (client, server, harness) reads the files.
    /// Immutable. The listed collections are sorted by id (ordinal) so iterating them is deterministic.
    /// </summary>
    public sealed class ContentLibrary
    {
        private readonly Dictionary<string, MapDefinition> _maps = new Dictionary<string, MapDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, CardCatalog> _factions = new Dictionary<string, CardCatalog>(StringComparer.Ordinal);
        private readonly Dictionary<string, BotPersonality> _bots = new Dictionary<string, BotPersonality>(StringComparer.Ordinal);

        /// <summary>Throws <see cref="ArgumentException"/> when two maps, factions or bots share an id.</summary>
        public ContentLibrary(MatchRules rules, StructureCatalog structures, IEnumerable<MapDefinition> maps,
            IEnumerable<CardCatalog> factions, IEnumerable<BotPersonality> bots)
        {
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            Structures = structures ?? throw new ArgumentNullException(nameof(structures));
            Maps = Index(maps ?? throw new ArgumentNullException(nameof(maps)), _maps, m => m.Id, "map");
            Factions = Index(factions ?? throw new ArgumentNullException(nameof(factions)), _factions, f => f.Faction, "faction");
            Bots = Index(bots ?? throw new ArgumentNullException(nameof(bots)), _bots, b => b.Id, "bot");
        }

        public MatchRules Rules { get; }

        public StructureCatalog Structures { get; }

        /// <summary>Every map, sorted by id.</summary>
        public IReadOnlyList<MapDefinition> Maps { get; }

        /// <summary>Every faction's cards, sorted by faction id.</summary>
        public IReadOnlyList<CardCatalog> Factions { get; }

        /// <summary>Every bot personality, sorted by id.</summary>
        public IReadOnlyList<BotPersonality> Bots { get; }

        public bool TryGetMap(string id, out MapDefinition map) => _maps.TryGetValue(id, out map!);

        public bool TryGetFaction(string id, out CardCatalog faction) => _factions.TryGetValue(id, out faction!);

        public bool TryGetBot(string id, out BotPersonality bot) => _bots.TryGetValue(id, out bot!);

        /// <summary>The map with this id; throws <see cref="KeyNotFoundException"/> listing the known ids.</summary>
        public MapDefinition GetMap(string id) => Get(_maps, Maps, id, "map", m => m.Id);

        public CardCatalog GetFaction(string id) => Get(_factions, Factions, id, "faction", f => f.Faction);

        public BotPersonality GetBot(string id) => Get(_bots, Bots, id, "bot", b => b.Id);

        private static T Get<T>(Dictionary<string, T> byId, IReadOnlyList<T> all, string id, string what, Func<T, string> idOf)
        {
            if (id != null && byId.TryGetValue(id, out T value))
            {
                return value;
            }
            var known = new List<string>();
            foreach (T item in all)
            {
                known.Add(idOf(item));
            }
            throw new KeyNotFoundException("No " + what + " \"" + id + "\". Known: " + string.Join(", ", known) + ".");
        }

        private static IReadOnlyList<T> Index<T>(IEnumerable<T> items, Dictionary<string, T> byId, Func<T, string> idOf,
            string what)
        {
            var list = new List<T>();
            foreach (T item in items)
            {
                if (item == null)
                {
                    throw new ArgumentException("A " + what + " entry is null.");
                }
                string id = idOf(item);
                if (byId.ContainsKey(id))
                {
                    throw new ArgumentException("Two " + what + "s have the id \"" + id + "\".");
                }
                byId.Add(id, item);
                list.Add(item);
            }
            list.Sort((a, b) => string.CompareOrdinal(idOf(a), idOf(b))); // ids are unique, so the order is total
            return list.ToArray();
        }
    }
}
