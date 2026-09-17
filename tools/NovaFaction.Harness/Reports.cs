using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaFaction.Harness;

internal sealed record Spread(double Mean, double StdDev, double Min, double Max)
{
    public static Spread Of(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return new Spread(0, 0, 0, 0);
        }
        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return new Spread(mean, Math.Sqrt(variance), values.Min(), values.Max());
    }

    public override string ToString() =>
        Fmt.N(Mean, 1) + " +/- " + Fmt.N(StdDev, 1) + "  (min " + Fmt.N(Min, 1) + ", max " + Fmt.N(Max, 1) + ")";
}

internal sealed record SideSummary(
    int Side, string Personality, int Wins, double WinRate,
    double GoldBase, double GoldMine, double GoldChest, double GoldTotal, double GoldSpent,
    double IncomeVsBaseOnly, double MineCaptures, double ChestsCollected,
    double Deploys, double Spells, double Abilities, double UnitsLost, double StructuresDestroyed,
    double TowersDestroyed, double Score);

internal sealed record CardRow(
    int Side, string Personality, string Card, int Deploys, double DeploysPerMatch, int GoldSpent, int Kills, int Deaths,
    double StructureDamage, double StructureDamagePerGold, double KillsPerDeploy, double KillsPerGold);

/// <summary>
/// The per-card efficiency check: structure damage per gold and kills per gold for every card, both sides pooled,
/// against the median card. The balance target is that no card is more than <see cref="Limit"/> times the median.
/// </summary>
internal sealed record CardEfficiency(string Card, int GoldSpent, double StructureDamagePerGold, double KillsPerGold,
    double DamageRatio, double KillRatio)
{
    public const double Limit = 2.0;

    public bool IsOutlier => DamageRatio > Limit || KillRatio > Limit;
}

/// <summary>Everything the batch command reports about N matches of one pairing.</summary>
internal sealed record BatchSummary(
    string P0, string P1, string Map, int Matches, ulong SeedStart,
    SideSummary[] Sides,
    Dictionary<string, int> EndReasons, Dictionary<string, int> TieBreakRules,
    double KeepKillRate, double TowerKillRate, double SuddenDeathRate,
    Spread LengthSeconds, Spread ScoreMargin, Spread AbsScoreMargin,
    double IncomeRatioP0OverP1, string? ActiveVsTurtle, double? ActiveVsTurtleRatio,
    CardRow[] Cards, double MedianStructureDamagePerGold, double MedianKillsPerGold, CardEfficiency[] CardEfficiencies)
{
    public static BatchSummary Build(IReadOnlyList<MatchRecord> records, string p0, string p1, string map, ulong seedStart)
    {
        int n = records.Count;
        var sides = new SideSummary[2];
        for (int p = 0; p < 2; p++)
        {
            int side = p;
            double Mean(Func<PlayerStats, double> f) => records.Average(r => f(r.Players[side]));
            int wins = records.Count(r => r.Winner == side);
            double goldBase = Mean(s => s.GoldBase);
            sides[p] = new SideSummary(p, p == 0 ? p0 : p1, wins, (double)wins / n,
                goldBase, Mean(s => s.GoldMine), Mean(s => s.GoldChest), Mean(s => s.GoldTotal), Mean(s => s.GoldSpent),
                goldBase > 0 ? Mean(s => s.GoldTotal) / goldBase : 0,
                Mean(s => s.MineCaptures), Mean(s => s.ChestsCollected), Mean(s => s.Deploys), Mean(s => s.Spells),
                Mean(s => s.Abilities), Mean(s => s.UnitsLost), Mean(s => s.StructuresDestroyed),
                Mean(s => s.TowersDestroyed), records.Average(r => side == 0 ? r.Score0 : r.Score1));
        }

        string? activeVsTurtle = null;
        double? activeRatio = null;
        if ((p0 == "turtle") != (p1 == "turtle"))
        {
            SideSummary turtle = p0 == "turtle" ? sides[0] : sides[1];
            SideSummary active = p0 == "turtle" ? sides[1] : sides[0];
            activeVsTurtle = active.Personality + " / turtle";
            activeRatio = turtle.GoldTotal > 0 ? active.GoldTotal / turtle.GoldTotal : null;
        }

        var cardTotals = new SortedDictionary<(int, string), CardStats>();
        foreach (MatchRecord r in records)
        {
            foreach (var (key, stats) in r.Cards)
            {
                if (!cardTotals.TryGetValue(key, out CardStats? total))
                {
                    cardTotals[key] = total = new CardStats();
                }
                total.Add(stats);
            }
        }
        CardRow[] cards = cardTotals.Select(kv => new CardRow(kv.Key.Item1, kv.Key.Item1 == 0 ? p0 : p1, kv.Key.Item2,
            kv.Value.Deploys, (double)kv.Value.Deploys / n, kv.Value.GoldSpent, kv.Value.Kills, kv.Value.Deaths,
            kv.Value.StructureDamage, kv.Value.GoldSpent > 0 ? kv.Value.StructureDamage / kv.Value.GoldSpent : 0,
            kv.Value.Deploys > 0 ? (double)kv.Value.Kills / kv.Value.Deploys : 0,
            kv.Value.GoldSpent > 0 ? (double)kv.Value.Kills / kv.Value.GoldSpent : 0)).ToArray();
        var (medianDamage, medianKills, efficiencies) = Efficiency(cardTotals);

        return new BatchSummary(p0, p1, map, n, seedStart, sides,
            Count(records.Select(r => r.EndReason)),
            Count(records.Where(r => r.EndReason == "TieBreak").Select(r => r.TieBreakRule)),
            (double)records.Count(r => r.KeepKill) / n,
            (double)records.Count(r => r.TowerKills > 0) / n,
            (double)records.Count(r => r.SuddenDeath) / n,
            Spread.Of(records.Select(r => r.Seconds).ToArray()),
            Spread.Of(records.Select(r => r.Margin).ToArray()),
            Spread.Of(records.Select(r => Math.Abs(r.Margin)).ToArray()),
            sides[1].GoldTotal > 0 ? sides[0].GoldTotal / sides[1].GoldTotal : 0,
            activeVsTurtle, activeRatio, cards, medianDamage, medianKills, efficiencies);
    }

    /// <summary>
    /// Pools every played card over both sides (structures and leader abilities are not cards, so they are left out)
    /// and compares each one's structure damage per gold and kills per gold with the median card's.
    /// </summary>
    private static (double MedianDamage, double MedianKills, CardEfficiency[] Cards)
        Efficiency(IEnumerable<KeyValuePair<(int, string), CardStats>> totals)
    {
        var pooled = new SortedDictionary<string, CardStats>(StringComparer.Ordinal);
        foreach (var (key, stats) in totals)
        {
            string card = key.Item2;
            if (card.StartsWith('[') || card.EndsWith("(ability)"))
            {
                continue; // structures and leader abilities are not cards the player pays gold for
            }
            if (!pooled.TryGetValue(card, out CardStats? total))
            {
                pooled[card] = total = new CardStats();
            }
            total.Add(stats);
        }
        var played = pooled.Where(kv => kv.Value.GoldSpent > 0).ToArray();
        if (played.Length == 0)
        {
            return (0, 0, []);
        }
        double[] damage = played.Select(kv => kv.Value.StructureDamage / kv.Value.GoldSpent).OrderBy(v => v).ToArray();
        double[] kills = played.Select(kv => (double)kv.Value.Kills / kv.Value.GoldSpent).OrderBy(v => v).ToArray();
        double medianDamage = Median(damage);
        double medianKills = Median(kills);
        CardEfficiency[] cards = played.Select(kv =>
        {
            double d = kv.Value.StructureDamage / kv.Value.GoldSpent;
            double k = (double)kv.Value.Kills / kv.Value.GoldSpent;
            return new CardEfficiency(kv.Key, kv.Value.GoldSpent, d, k,
                medianDamage > 0 ? d / medianDamage : 0, medianKills > 0 ? k / medianKills : 0);
        }).OrderByDescending(c => Math.Max(c.DamageRatio, c.KillRatio)).ToArray();
        return (medianDamage, medianKills, cards);
    }

    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

    private static Dictionary<string, int> Count(IEnumerable<string> items) =>
        items.GroupBy(i => i).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
}

internal static class Fmt
{
    public static string N(double value, int decimals = 2) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    public static string Pct(double share) => N(share * 100, 1) + "%";

    public static string Time(int tick, int ticksPerSecond)
    {
        int hundredths = tick * 100 / ticksPerSecond;
        return (hundredths / 6000) + ":" + (hundredths / 100 % 60).ToString("00") + "." + (hundredths % 100).ToString("00");
    }

    /// <summary>Prints rows as an aligned text table; numeric-looking cells are right-aligned.</summary>
    public static void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var all = rows.ToList();
        int[] widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length))).ToArray();
        static bool Numeric(string s) => s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '-' || s[0] == '+') && !s.Contains(' ');
        string Line(IReadOnlyList<string> cells) => string.Join("  ", cells.Select((c, i) =>
            Numeric(c) ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();
        Console.WriteLine(Line(headers));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in all)
        {
            Console.WriteLine(Line(row));
        }
    }
}

internal static class Output
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);

    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        Console.WriteLine("wrote " + path);
    }

    public static string Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Cell)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(Cell)));
        }
        return sb.ToString();
    }

    private static string Cell(string value) =>
        value.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>One CSV row per match.</summary>
    public static string MatchesCsv(IEnumerable<MatchRecord> records)
    {
        string[] headers =
        [
            "seed", "p0", "p1", "map", "winner", "endReason", "tieBreakRule", "ticks", "seconds", "score0", "score1", "margin",
            "keepKill", "suddenDeath", "towerKills", "finalHash",
            "p0GoldBase", "p0GoldMine", "p0GoldChest", "p0GoldSpent", "p0MineCaptures", "p0Chests", "p0Deploys", "p0Spells",
            "p0Abilities", "p0UnitsLost", "p0StructuresDestroyed", "p0TowersDestroyed",
            "p1GoldBase", "p1GoldMine", "p1GoldChest", "p1GoldSpent", "p1MineCaptures", "p1Chests", "p1Deploys", "p1Spells",
            "p1Abilities", "p1UnitsLost", "p1StructuresDestroyed", "p1TowersDestroyed",
        ];
        return Csv(headers, records.Select(r => (IReadOnlyList<string>)new[]
        {
            r.Seed.ToString(CultureInfo.InvariantCulture), r.P0, r.P1, r.Map, r.Winner.ToString(CultureInfo.InvariantCulture),
            r.EndReason, r.TieBreakRule, r.Ticks.ToString(CultureInfo.InvariantCulture), Fmt.N(r.Seconds), Fmt.N(r.Score0),
            Fmt.N(r.Score1), Fmt.N(r.Margin), r.KeepKill ? "1" : "0", r.SuddenDeath ? "1" : "0",
            I(r.TowerKills), r.FinalHash,
        }.Concat(r.Players.SelectMany(PlayerCells)).ToArray()));
    }

    private static string[] PlayerCells(PlayerStats p) =>
    [
        Fmt.N(p.GoldBase), Fmt.N(p.GoldMine), Fmt.N(p.GoldChest), Fmt.N(p.GoldSpent), I(p.MineCaptures), I(p.ChestsCollected),
        I(p.Deploys), I(p.Spells), I(p.Abilities), I(p.UnitsLost), I(p.StructuresDestroyed), I(p.TowersDestroyed),
    ];

    public static string CardsCsv(IEnumerable<CardRow> cards) => Csv(
        ["side", "personality", "card", "deploys", "deploysPerMatch", "goldSpent", "kills", "deaths", "structureDamage",
            "structureDamagePerGold", "killsPerDeploy", "killsPerGold"],
        cards.Select(c => (IReadOnlyList<string>)
        [
            I(c.Side), c.Personality, c.Card, I(c.Deploys), Fmt.N(c.DeploysPerMatch), I(c.GoldSpent), I(c.Kills), I(c.Deaths),
            Fmt.N(c.StructureDamage), Fmt.N(c.StructureDamagePerGold), Fmt.N(c.KillsPerDeploy), Fmt.N(c.KillsPerGold, 3),
        ]));

    public static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A file name part made only of safe characters.</summary>
    public static string Safe(string text) => new(text.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

    /// <summary>JSON-friendly view of a match record (the card dictionary becomes a list).</summary>
    public static object MatchJson(MatchRecord r) => new
    {
        r.Seed, r.P0, r.P1, r.Map, r.Winner, r.EndReason, r.TieBreakRule, r.Ticks, r.Seconds, r.Score0, r.Score1, r.Margin,
        r.KeepKill, r.SuddenDeath, r.TowerKills, r.FinalHash, r.Players,
        Cards = r.Cards.OrderBy(kv => kv.Key.Player).ThenBy(kv => kv.Key.Card, StringComparer.Ordinal)
            .Select(kv => new { Side = kv.Key.Player, kv.Key.Card, kv.Value.Deploys, kv.Value.GoldSpent, kv.Value.Kills,
                kv.Value.Deaths, kv.Value.StructureDamage }),
    };
}
