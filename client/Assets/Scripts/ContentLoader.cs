using System;
using System.Collections.Generic;
using NovaFaction.Sim.Bots;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using UnityEngine;

namespace NovaFaction.Client
{
    /// <summary>
    /// Reads the game content out of Resources/content and builds the sim's <see cref="ContentLibrary"/>.
    /// The files are the same JSON the headless harness reads, put there by tools/sync-to-unity.ps1, and they
    /// go through the same sim loaders, so the client and the harness build identical content.
    ///
    /// No game rules live here: this class only finds files and hands their text to the sim.
    /// </summary>
    public static class ContentLoader
    {
        /// <summary>Folder under Resources/ that sync-to-unity.ps1 mirrors content/ into.</summary>
        public const string Root = "content";

        private const string UnitsFile = "units";
        private const string SpellsFile = "spells";

        /// <summary>
        /// Loads every content file. Throws <see cref="InvalidOperationException"/> when a file is missing and
        /// <see cref="SimJsonException"/> when one is malformed; both name the file.
        /// </summary>
        public static ContentLibrary Load()
        {
            MatchRules rules = MatchRules.FromJson(ReadRequired("rules"), "rules.json");
            StructureCatalog structures = StructureCatalog.FromJson(ReadRequired("structures"), "structures.json");

            var maps = new List<MapDefinition>();
            foreach (TextAsset asset in LoadFolder("maps"))
            {
                maps.Add(MapDefinition.FromJson(asset.text, "maps/" + asset.name + ".json"));
            }

            var bots = new List<BotPersonality>();
            foreach (TextAsset asset in LoadFolder("bots"))
            {
                bots.Add(BotPersonality.FromJson(asset.text, "bots/" + asset.name + ".json"));
            }

            return new ContentLibrary(rules, structures, maps, LoadFactions(), bots);
        }

        /// <summary>
        /// Every faction's cards. Resources gives a flat list, so the units.json and spells.json of one faction are
        /// paired by the "faction" id inside them rather than by their folder; the sim rejects a mismatch anyway.
        /// </summary>
        private static List<CardCatalog> LoadFactions()
        {
            var units = new SortedDictionary<string, TextAsset>(StringComparer.Ordinal);
            var spells = new SortedDictionary<string, TextAsset>(StringComparer.Ordinal);
            foreach (TextAsset asset in LoadFolder("factions"))
            {
                SortedDictionary<string, TextAsset> into =
                    asset.name == UnitsFile ? units : asset.name == SpellsFile ? spells : null;
                if (into == null)
                {
                    continue; // Some other JSON in the faction folder; the harness ignores it too.
                }
                string faction = FactionIdOf(asset);
                if (into.ContainsKey(faction))
                {
                    throw new InvalidOperationException("Two " + asset.name + ".json files claim the faction \""
                        + faction + "\" under Resources/" + Root + "/factions.");
                }
                into.Add(faction, asset);
            }

            var catalogs = new List<CardCatalog>();
            foreach (KeyValuePair<string, TextAsset> entry in units) // SortedDictionary: ordinal by faction id
            {
                string faction = entry.Key;
                string unitsJson = entry.Value.text;
                string unitsName = faction + "/units.json";
                catalogs.Add(spells.TryGetValue(faction, out TextAsset spellAsset)
                    ? CardCatalog.FromJson(unitsJson, spellAsset.text, unitsName, faction + "/spells.json")
                    : CardCatalog.UnitsOnly(UnitRoster.FromJson(unitsJson, unitsName)));
            }
            return catalogs;
        }

        /// <summary>The "faction" member of a content file, read without interpreting the rest of it.</summary>
        private static string FactionIdOf(TextAsset asset)
        {
            JsonValue root = SimJson.Parse(asset.text, asset.name + ".json");
            if (root.Kind != JsonKind.Object || !root.TryGet("faction", out JsonValue faction)
                || faction.Kind != JsonKind.String)
            {
                throw new InvalidOperationException("Resources/" + Root + "/factions/.../" + asset.name
                    + ".json has no \"faction\" string.");
            }
            return faction.AsString();
        }

        /// <summary>The TextAssets in one folder under Resources/content, sorted by name (ordinal).</summary>
        private static List<TextAsset> LoadFolder(string folder)
        {
            TextAsset[] assets = Resources.LoadAll<TextAsset>(Root + "/" + folder);
            var list = new List<TextAsset>(assets);
            // Resources.LoadAll does not promise an order, and the sim must be built the same way every run.
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return list;
        }

        private static string ReadRequired(string name)
        {
            TextAsset asset = Resources.Load<TextAsset>(Root + "/" + name);
            if (asset == null)
            {
                throw new InvalidOperationException("Missing Assets/Resources/" + Root + "/" + name
                    + ".json. Run tools\\sync-to-unity.ps1 and let Unity reimport.");
            }
            return asset.text;
        }
    }
}
