using NovaFaction.Sim.Bots;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;

namespace NovaFaction.Harness;

/// <summary>Finds the repo's content/ folder and loads every file in it into a <see cref="ContentLibrary"/>.</summary>
internal static class ContentLoader
{
    /// <summary>The given folder, or the first content/ folder (holding rules.json) above the working or program folder.</summary>
    public static string FindContentDir(string? explicitDir)
    {
        if (explicitDir != null)
        {
            string full = Path.GetFullPath(explicitDir);
            if (!File.Exists(Path.Combine(full, "rules.json")))
            {
                throw new HarnessException("No rules.json in " + full + ".");
            }
            return full;
        }
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "content");
                if (File.Exists(Path.Combine(candidate, "rules.json")))
                {
                    return candidate;
                }
            }
        }
        throw new HarnessException("Could not find the content folder; run from inside the repo or pass --content <folder>.");
    }

    public static ContentLibrary Load(string contentDir)
    {
        MatchRules rules = MatchRules.FromJson(Read(contentDir, "rules.json"), "rules.json");
        StructureCatalog structures = StructureCatalog.FromJson(Read(contentDir, "structures.json"));

        var maps = Files(Path.Combine(contentDir, "maps"), "*.json")
            .Select(f => MapDefinition.FromJson(File.ReadAllText(f), "maps/" + Path.GetFileName(f)));

        var factions = new List<CardCatalog>();
        foreach (string dir in Directories(Path.Combine(contentDir, "factions")))
        {
            string units = Path.Combine(dir, "units.json");
            string spells = Path.Combine(dir, "spells.json");
            string name = Path.GetFileName(dir);
            if (!File.Exists(units))
            {
                continue;
            }
            factions.Add(File.Exists(spells)
                ? CardCatalog.FromJson(File.ReadAllText(units), File.ReadAllText(spells), name + "/units.json", name + "/spells.json")
                : CardCatalog.UnitsOnly(UnitRoster.FromJson(File.ReadAllText(units), name + "/units.json")));
        }

        var bots = Files(Path.Combine(contentDir, "bots"), "*.json")
            .Select(f => BotPersonality.FromJson(File.ReadAllText(f), "bots/" + Path.GetFileName(f)));

        return new ContentLibrary(rules, structures, maps.ToList(), factions, bots.ToList());
    }

    private static string Read(string dir, string file) => File.ReadAllText(Path.Combine(dir, file));

    private static IEnumerable<string> Files(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal) : [];

    private static IEnumerable<string> Directories(string dir) =>
        Directory.Exists(dir) ? Directory.GetDirectories(dir).OrderBy(f => f, StringComparer.Ordinal) : [];
}

/// <summary>A problem with the command line or files; printed without a stack trace.</summary>
internal sealed class HarnessException(string message) : Exception(message);
