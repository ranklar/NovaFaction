using System.Diagnostics;
using NovaFaction.Harness;
using NovaFaction.Sim;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Replays;

// NovaFaction headless harness: runs bot matches on the real sim and content for tuning and determinism checks.
// Usage: dotnet run -c Release --project tools\NovaFaction.Harness -- <command> [options]

const string Usage = """
NovaFaction harness. Commands and options:

  run         --seed S --p0 BOT --p1 BOT --map MAP [--replay FILE] [--no-timeline]
              One match: winner, reason, scores, timeline, final hash. --replay writes (and verifies) a replay file.
  batch       --matches N --p0 BOT --p1 BOT --map MAP [--seed-start S]
              N matches in parallel (seeds S..S+N-1): win rates, end reasons, lengths, margins, Keep kills, card stats,
              gold by source, mines, chests. Writes CSV and JSON to the output folder.
  roundrobin  --matches-per-pair N --map MAP [--seed-start S] [--bots a,b,c]
              Every ordered pairing of bot personalities (both sides, mirrors included), one summary table.
  determinism --matches N [--map MAP] [--seed-start S]
              Plays each match twice (serially with an observer, then in parallel without) and compares the state
              hash after every tick; then saves each match as a replay, reads it back and verifies it.
  verify      --replay FILE        Re-runs a replay file and checks its final hash.
  export      --replay FILE [--json FILE]   Writes a replay file as JSON (default FILE.json).

Common options:
  BOT is a personality id from content/bots (balanced, aggressive, turtle, swarm) or "idle" (never acts).
  --deck0 a,b,...  --deck1 a,b,...   Card ids (default: the seven fantasy units plus fireball).
  --content DIR    Content folder (default: found by searching upward for content/rules.json).
  --out DIR        Output folder (default: tools/out in the repo).
  --threads N      Parallel matches (default: all cores).
Defaults: --seed 1, --seed-start 1, --p0 balanced, --p1 balanced, --map twolane.
""";

try
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
    {
        Console.WriteLine(Usage);
        return args.Length == 0 ? 2 : 0;
    }
    var options = Options.Parse(args.Skip(1));
    string contentDir = ContentLoader.FindContentDir(options.Get("content"));
    ContentLibrary content = ContentLoader.Load(contentDir);
    string outDir = Path.GetFullPath(options.Get("out") ?? Path.Combine(Path.GetDirectoryName(contentDir)!, "tools", "out"));
    var commands = new Commands(content, options, outDir);
    int code = args[0] switch
    {
        "run" => commands.Run(),
        "batch" => commands.Batch(),
        "roundrobin" => commands.RoundRobin(),
        "determinism" => commands.Determinism(),
        "verify" => commands.Verify(),
        "export" => commands.Export(),
        _ => throw new HarnessException("Unknown command \"" + args[0] + "\". Run with --help for the list."),
    };
    options.CheckAllUsed();
    return code;
}
catch (HarnessException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 2;
}
catch (Exception e) when (e is ReplayException or NovaFaction.Sim.Content.SimJsonException)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

namespace NovaFaction.Harness
{
    /// <summary>"--name value" pairs and "--flag" switches.</summary>
    internal sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            string[] list = args.ToArray();
            for (int i = 0; i < list.Length; i++)
            {
                if (!list[i].StartsWith("--", StringComparison.Ordinal) || list[i].Length == 2)
                {
                    throw new HarnessException("Unexpected argument \"" + list[i] + "\"; options look like --name value.");
                }
                string name = list[i][2..];
                string? value = i + 1 < list.Length && !list[i + 1].StartsWith("--", StringComparison.Ordinal) ? list[++i] : null;
                if (!options._values.TryAdd(name, value))
                {
                    throw new HarnessException("Option --" + name + " is given twice.");
                }
            }
            return options;
        }

        public string? Get(string name)
        {
            _used.Add(name);
            if (_values.TryGetValue(name, out string? value) && value == null)
            {
                throw new HarnessException("Option --" + name + " needs a value.");
            }
            return value;
        }

        public string Get(string name, string fallback) => Get(name) ?? fallback;

        public bool Flag(string name)
        {
            _used.Add(name);
            if (_values.TryGetValue(name, out string? value) && value != null)
            {
                throw new HarnessException("Option --" + name + " takes no value.");
            }
            return _values.ContainsKey(name);
        }

        public long Number(string name, long fallback, long min = 0, long max = long.MaxValue)
        {
            string? text = Get(name);
            if (text == null)
            {
                return fallback;
            }
            if (!long.TryParse(text, out long value) || value < min || value > max)
            {
                throw new HarnessException("Option --" + name + " must be a whole number from " + min + " to " + max
                    + " but is \"" + text + "\".");
            }
            return value;
        }

        public long Required(string name, long min = 1)
        {
            if (!_values.ContainsKey(name))
            {
                throw new HarnessException("Option --" + name + " is required.");
            }
            return Number(name, 0, min);
        }

        public void CheckAllUsed()
        {
            string[] unused = _values.Keys.Where(k => !_used.Contains(k)).ToArray();
            if (unused.Length > 0)
            {
                Console.Error.WriteLine("warning: ignored option(s) " + string.Join(", ", unused.Select(u => "--" + u)));
            }
        }
    }

    internal sealed class Commands(ContentLibrary content, Options options, string outDir)
    {
        private int Tps => content.Rules.TicksPerSecond;

        private MatchPlan Plan(string? p0 = null, string? p1 = null)
        {
            static string[] Deck(string? text) =>
                text == null ? MatchPlan.DefaultDeck : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new MatchPlan(p0 ?? options.Get("p0", "balanced"), p1 ?? options.Get("p1", "balanced"),
                options.Get("map", "twolane"), Deck(options.Get("deck0")), Deck(options.Get("deck1")));
        }

        private ParallelOptions Parallelism() => new()
        {
            MaxDegreeOfParallelism = (int)options.Number("threads", Environment.ProcessorCount, 1, 1024),
        };

        private MatchRecord[] RunMany(MatchPlan plan, ulong seedStart, int count)
        {
            MatchSetup setup = plan.CreateSetup(content); // immutable: shared by every thread
            var records = new MatchRecord[count];
            Parallel.For(0, count, Parallelism(),
                i => records[i] = MatchRunner.Run(content, setup, plan, seedStart + (ulong)i).Record);
            return records;
        }

        // ------------------------------------------------------------ run

        public int Run()
        {
            MatchPlan plan = Plan();
            ulong seed = (ulong)options.Number("seed", 1);
            string? replayPath = options.Get("replay");
            bool timeline = !options.Flag("no-timeline");
            MatchSetup setup = plan.CreateSetup(content);

            var watch = Stopwatch.StartNew();
            var (record, sim) = MatchRunner.Run(content, setup, plan, seed, timeline);
            watch.Stop();

            Console.WriteLine("Match: P0 " + plan.P0 + " vs P1 " + plan.P1 + " on " + plan.Map + ", seed " + seed
                + " (" + Fmt.N(watch.Elapsed.TotalMilliseconds, 0) + " ms)");
            if (timeline)
            {
                Console.WriteLine();
                Console.WriteLine("Timeline:");
                foreach (TimelineEntry entry in record.Timeline)
                {
                    Console.WriteLine("  " + Fmt.Time(entry.Tick, Tps).PadLeft(8) + "  " + entry.Text);
                }
            }
            Console.WriteLine();
            Console.WriteLine("Winner:     P" + record.Winner + " (" + plan.Side(record.Winner) + ")");
            Console.WriteLine("Reason:     " + record.EndReason + (record.TieBreakRule != "None" ? " / " + record.TieBreakRule : ""));
            Console.WriteLine("Score:      " + Fmt.N(record.Score0) + " - " + Fmt.N(record.Score1));
            Console.WriteLine("Length:     " + Fmt.Time(record.Ticks, Tps) + " (" + record.Ticks + " ticks)");
            Console.WriteLine();
            Fmt.Table(["side", "bot", "gold base", "mine", "chest", "spent", "deploys", "spells", "abilities", "lost",
                    "structures", "mines", "chests"],
                record.Players.Select((p, i) => (IReadOnlyList<string>)
                [
                    "P" + i, plan.Side(i), Fmt.N(p.GoldBase), Fmt.N(p.GoldMine), Fmt.N(p.GoldChest), Fmt.N(p.GoldSpent),
                    Output.I(p.Deploys), Output.I(p.Spells), Output.I(p.Abilities), Output.I(p.UnitsLost),
                    Output.I(p.StructuresDestroyed), Output.I(p.MineCaptures), Output.I(p.ChestsCollected),
                ]));
            Console.WriteLine();
            PrintCards(BatchSummary.Build([record], plan.P0, plan.P1, plan.Map, seed).Cards);
            Console.WriteLine();
            Console.WriteLine("Final hash: " + record.FinalHash);

            if (replayPath != null)
            {
                Replay replay = Replay.FromMatch(sim);
                byte[] bytes = ReplayWriter.Write(replay);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(replayPath))!);
                File.WriteAllBytes(replayPath, bytes);
                ReplayVerification check = ReplayReader.Read(File.ReadAllBytes(replayPath), content).Verify(content);
                Console.WriteLine("Replay:     " + Path.GetFullPath(replayPath) + " (" + bytes.Length + " bytes, "
                    + replay.Log.CommandCount + " commands) - verify " + check);
                return check.Success ? 0 : 1;
            }
            return 0;
        }

        private static void PrintCards(IEnumerable<CardRow> cards) =>
            Fmt.Table(["side", "bot", "card", "deploys", "per match", "gold", "kills", "deaths", "struct dmg", "dmg/gold",
                    "kills/deploy", "kills/gold"],
                cards.Select(c => (IReadOnlyList<string>)
                [
                    "P" + c.Side, c.Personality, c.Card, Output.I(c.Deploys), Fmt.N(c.DeploysPerMatch), Output.I(c.GoldSpent),
                    Output.I(c.Kills), Output.I(c.Deaths), Fmt.N(c.StructureDamage, 0), Fmt.N(c.StructureDamagePerGold, 1),
                    Fmt.N(c.KillsPerDeploy), Fmt.N(c.KillsPerGold, 3),
                ]));

        /// <summary>The card outlier check: each card against the median card, both sides pooled.</summary>
        private static void PrintEfficiency(BatchSummary s)
        {
            Console.WriteLine("Card efficiency vs the median card (both sides pooled; target: no ratio above "
                + Fmt.N(CardEfficiency.Limit, 1) + "):");
            Console.WriteLine("  median dmg/gold " + Fmt.N(s.MedianStructureDamagePerGold, 1)
                + ", median kills/gold " + Fmt.N(s.MedianKillsPerGold, 3));
            Fmt.Table(["card", "gold", "dmg/gold", "x median", "kills/gold", "x median", "outlier"],
                s.CardEfficiencies.Select(c => (IReadOnlyList<string>)
                [
                    c.Card, Output.I(c.GoldSpent), Fmt.N(c.StructureDamagePerGold, 1), Fmt.N(c.DamageRatio),
                    Fmt.N(c.KillsPerGold, 3), Fmt.N(c.KillRatio), c.IsOutlier ? "OUTLIER" : "",
                ]));
        }

        // ------------------------------------------------------------ batch

        public int Batch()
        {
            int n = (int)options.Required("matches");
            ulong seedStart = (ulong)options.Number("seed-start", 1);
            MatchPlan plan = Plan();

            var watch = Stopwatch.StartNew();
            MatchRecord[] records = RunMany(plan, seedStart, n);
            watch.Stop();
            BatchSummary s = BatchSummary.Build(records, plan.P0, plan.P1, plan.Map, seedStart);

            Console.WriteLine("Batch: " + n + " matches, P0 " + plan.P0 + " vs P1 " + plan.P1 + " on " + plan.Map + ", seeds "
                + seedStart + ".." + (seedStart + (ulong)n - 1) + " (" + Fmt.N(watch.Elapsed.TotalSeconds, 1) + " s, "
                + Fmt.N(watch.Elapsed.TotalMilliseconds / n, 0) + " ms/match wall clock)");
            Console.WriteLine();
            Console.WriteLine("Win rate:          P0 " + Fmt.Pct(s.Sides[0].WinRate) + "   P1 " + Fmt.Pct(s.Sides[1].WinRate));
            Console.WriteLine("End reasons:       " + string.Join(", ", s.EndReasons.Select(kv => kv.Key + " " + kv.Value
                + " (" + Fmt.Pct((double)kv.Value / n) + ")")));
            if (s.TieBreakRules.Count > 0)
            {
                Console.WriteLine("Tie-break rules:   " + string.Join(", ", s.TieBreakRules.Select(kv => kv.Key + " " + kv.Value)));
            }
            Console.WriteLine("Keep-kill rate:    " + Fmt.Pct(s.KeepKillRate) + "   (target 20-35% in balanced mirrors)");
            Console.WriteLine("Tower-kill rate:   " + Fmt.Pct(s.TowerKillRate) + "   (matches with 1+ forward tower down; target 60%+)");
            Console.WriteLine("Sudden death:      " + Fmt.Pct(s.SuddenDeathRate) + "   (target under 10%)");
            Console.WriteLine("Match length (s):  " + s.LengthSeconds);
            Console.WriteLine("Score margin P0-P1:" + " " + s.ScoreMargin);
            Console.WriteLine("|Score margin|:    " + s.AbsScoreMargin);
            Console.WriteLine("Income P0/P1:      " + Fmt.N(s.IncomeRatioP0OverP1, 3));
            if (s.ActiveVsTurtle != null)
            {
                Console.WriteLine("Active vs turtle:  " + s.ActiveVsTurtle + " = " + Fmt.N(s.ActiveVsTurtleRatio ?? 0, 3)
                    + " (design target about 1.333)");
            }
            Console.WriteLine();
            Console.WriteLine("Per side (means per match):");
            PrintSides(s.Sides);
            Console.WriteLine();
            Console.WriteLine("Per card (totals over all matches):");
            PrintCards(s.Cards);
            Console.WriteLine();
            PrintEfficiency(s);
            Console.WriteLine();

            string stem = Path.Combine(outDir, "batch_" + Output.Safe(plan.P0) + "_vs_" + Output.Safe(plan.P1) + "_"
                + Output.Safe(plan.Map) + "_n" + n + "_s" + seedStart);
            Output.WriteText(stem + ".json", Output.Json(new { summary = s, matches = records.Select(Output.MatchJson) }));
            Output.WriteText(stem + "_matches.csv", Output.MatchesCsv(records));
            Output.WriteText(stem + "_cards.csv", Output.CardsCsv(s.Cards));
            return 0;
        }

        private static void PrintSides(IEnumerable<SideSummary> sides) =>
            Fmt.Table(["side", "bot", "wins", "score", "gold base", "mine", "chest", "total", "vs base", "spent", "mines",
                    "chests", "deploys", "spells", "abilities", "lost", "structures"],
                sides.Select(p => (IReadOnlyList<string>)
                [
                    "P" + p.Side, p.Personality, Fmt.Pct(p.WinRate), Fmt.N(p.Score, 0), Fmt.N(p.GoldBase), Fmt.N(p.GoldMine),
                    Fmt.N(p.GoldChest), Fmt.N(p.GoldTotal), Fmt.N(p.IncomeVsBaseOnly, 3), Fmt.N(p.GoldSpent),
                    Fmt.N(p.MineCaptures), Fmt.N(p.ChestsCollected), Fmt.N(p.Deploys, 1), Fmt.N(p.Spells, 1),
                    Fmt.N(p.Abilities, 1), Fmt.N(p.UnitsLost, 1), Fmt.N(p.StructuresDestroyed),
                ]));

        // ------------------------------------------------------------ round robin

        public int RoundRobin()
        {
            int n = (int)options.Required("matches-per-pair");
            ulong seedStart = (ulong)options.Number("seed-start", 1);
            string? botList = options.Get("bots");
            string[] bots = botList == null
                ? content.Bots.Select(b => b.Id).ToArray()
                : botList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var pairs = bots.SelectMany(a => bots.Select(b => (P0: a, P1: b))).ToArray();

            var watch = Stopwatch.StartNew();
            // Every pairing uses the same seeds, so pairings differ only by the bots.
            var summaries = pairs.Select(pair =>
            {
                MatchPlan plan = Plan(pair.P0, pair.P1);
                return BatchSummary.Build(RunMany(plan, seedStart, n), plan.P0, plan.P1, plan.Map, seedStart);
            }).ToArray();
            watch.Stop();

            string map = summaries[0].Map;
            Console.WriteLine("Round robin: " + bots.Length + " bots, " + pairs.Length + " pairings x " + n + " matches on "
                + map + ", seeds " + seedStart + ".." + (seedStart + (ulong)n - 1) + " (" + Fmt.N(watch.Elapsed.TotalSeconds, 1) + " s)");
            Console.WriteLine();
            string[] headers =
            [
                "p0", "p1", "p0 win", "p1 win", "keep kill", "tower kill", "sudden", "length s", "margin", "|margin|",
                "p0 score", "p1 score", "p0 gold", "p1 gold", "income p0/p1", "p0 mines", "p1 mines", "p0 chests",
                "p1 chests", "end reasons",
            ];
            IReadOnlyList<string> Row(BatchSummary s) =>
            [
                s.P0, s.P1, Fmt.Pct(s.Sides[0].WinRate), Fmt.Pct(s.Sides[1].WinRate), Fmt.Pct(s.KeepKillRate),
                Fmt.Pct(s.TowerKillRate), Fmt.Pct(s.SuddenDeathRate),
                Fmt.N(s.LengthSeconds.Mean, 1), Fmt.N(s.ScoreMargin.Mean, 0), Fmt.N(s.AbsScoreMargin.Mean, 0),
                Fmt.N(s.Sides[0].Score, 0), Fmt.N(s.Sides[1].Score, 0), Fmt.N(s.Sides[0].GoldTotal, 1),
                Fmt.N(s.Sides[1].GoldTotal, 1), Fmt.N(s.IncomeRatioP0OverP1, 3), Fmt.N(s.Sides[0].MineCaptures),
                Fmt.N(s.Sides[1].MineCaptures), Fmt.N(s.Sides[0].ChestsCollected), Fmt.N(s.Sides[1].ChestsCollected),
                string.Join(" ", s.EndReasons.Select(kv => kv.Key + ":" + kv.Value)),
            ];
            Fmt.Table(headers, summaries.Select(Row));

            Console.WriteLine();
            Console.WriteLine("Overall (both sides, all opponents including itself):");
            var overall = bots.Select(bot =>
            {
                int played = 0, won = 0;
                double gold = 0;
                foreach (BatchSummary s in summaries)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        if (s.Sides[side].Personality == bot)
                        {
                            played += s.Matches;
                            won += s.Sides[side].Wins;
                            gold += s.Sides[side].GoldTotal * s.Matches;
                        }
                    }
                }
                return new { Bot = bot, Played = played, WinRate = (double)won / played, MeanGold = gold / played };
            }).OrderByDescending(o => o.WinRate).ToArray();
            Fmt.Table(["bot", "played", "win rate", "gold/match"],
                overall.Select(o => (IReadOnlyList<string>)[o.Bot, Output.I(o.Played), Fmt.Pct(o.WinRate), Fmt.N(o.MeanGold, 1)]));
            Console.WriteLine();
            var incomes = summaries.Where(x => x.ActiveVsTurtleRatio != null)
                .Select(x => new { Pairing = x.P0 + " vs " + x.P1, Label = x.ActiveVsTurtle!, Ratio = x.ActiveVsTurtleRatio!.Value })
                .ToArray();
            if (incomes.Length > 0)
            {
                Console.WriteLine("Active vs turtle income (target 1.25-1.40):");
                Fmt.Table(["pairing", "ratio", "ratio"],
                    incomes.Select(i => (IReadOnlyList<string>)[i.Pairing, i.Label, Fmt.N(i.Ratio, 3)]));
                Console.WriteLine("  mean over the pairings above: " + Fmt.N(incomes.Average(i => i.Ratio), 3));
                Console.WriteLine();
            }
            Console.WriteLine("Overall rates: keep kill " + Fmt.Pct(summaries.Sum(x => x.KeepKillRate * x.Matches) / (pairs.Length * (double)n))
                + ", tower kill " + Fmt.Pct(summaries.Sum(x => x.TowerKillRate * x.Matches) / (pairs.Length * (double)n))
                + ", sudden death " + Fmt.Pct(summaries.Sum(x => x.SuddenDeathRate * x.Matches) / (pairs.Length * (double)n)));
            Console.WriteLine();

            string stem = Path.Combine(outDir, "roundrobin_" + Output.Safe(map) + "_n" + n + "_s" + seedStart);
            Output.WriteText(stem + ".json", Output.Json(new { map, matchesPerPair = n, seedStart, overall, pairings = summaries }));
            Output.WriteText(stem + ".csv", Output.Csv(headers, summaries.Select(Row)));
            return 0;
        }

        // ------------------------------------------------------------ determinism

        public int Determinism()
        {
            int n = (int)options.Required("matches");
            ulong seedStart = (ulong)options.Number("seed-start", 1);
            string[] bots = content.Bots.Select(b => b.Id).ToArray();
            var plans = Enumerable.Range(0, n).Select(i => Plan(bots[i % bots.Length], bots[i / bots.Length % bots.Length]))
                .ToArray();
            var setups = plans.Select(p => p.CreateSetup(content)).ToArray();
            var watch = Stopwatch.StartNew();

            // Pass A: one after another, with the statistics observer attached.
            var serial = new List<ulong>[n];
            var sims = new Simulation[n];
            for (int i = 0; i < n; i++)
            {
                serial[i] = new List<ulong>();
                sims[i] = MatchRunner.Run(content, setups[i], plans[i], seedStart + (ulong)i, hashes: serial[i]).Sim;
            }
            // Pass B: all at once on the thread pool, without an observer.
            var parallel = new List<ulong>[n];
            Parallel.For(0, n, Parallelism(), i =>
            {
                parallel[i] = new List<ulong>();
                MatchRunner.Run(content, setups[i], plans[i], seedStart + (ulong)i, observe: false, hashes: parallel[i]);
            });

            int failures = 0;
            long ticks = 0;
            for (int i = 0; i < n; i++)
            {
                string name = "match " + (i + 1) + " (seed " + (seedStart + (ulong)i) + ", " + plans[i].P0 + " vs " + plans[i].P1 + ")";
                ticks += serial[i].Count - 1;
                int mismatch = Enumerable.Range(0, Math.Min(serial[i].Count, parallel[i].Count))
                    .FirstOrDefault(t => serial[i][t] != parallel[i][t], -1);
                if (mismatch >= 0 || serial[i].Count != parallel[i].Count)
                {
                    failures++;
                    Console.WriteLine("MISMATCH " + name + ": runs differ "
                        + (mismatch >= 0 ? "after tick " + (mismatch - 1) : "in length (" + serial[i].Count + " vs " + parallel[i].Count + " hashes)"));
                    continue;
                }
                // Pass C: the replay file re-runs to the same end.
                byte[] bytes = ReplayWriter.Write(Replay.FromMatch(sims[i]));
                ReplayVerification check = ReplayReader.Read(bytes, content).Verify(content);
                if (!check.Success)
                {
                    failures++;
                    Console.WriteLine("REPLAY FAILED " + name + ": " + check.Message);
                }
            }
            watch.Stop();
            Console.WriteLine("Determinism: " + n + " matches, " + ticks + " ticks compared hash by hash (serial with observer vs "
                + "parallel without), " + n + " replays written, read and verified (" + Fmt.N(watch.Elapsed.TotalSeconds, 1) + " s).");
            Console.WriteLine(failures == 0 ? "PASS: every run was identical and every replay verified."
                : "FAIL: " + failures + " match(es) failed.");
            return failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ replay files

        private string ReplayFile()
        {
            string path = options.Get("replay") ?? throw new HarnessException("Option --replay is required.");
            if (!File.Exists(path))
            {
                throw new HarnessException("No replay file at " + Path.GetFullPath(path) + ".");
            }
            return path;
        }

        public int Verify()
        {
            string path = ReplayFile();
            Replay replay = ReplayReader.Read(File.ReadAllBytes(path), content);
            ReplayVerification check = replay.Verify(content);
            Console.WriteLine(Path.GetFullPath(path) + ": seed " + replay.Seed + ", map " + replay.MapId + ", "
                + string.Join(" vs ", replay.Players.Select(p => p.BotId ?? "human")) + ", " + replay.Log.CommandCount
                + " commands, " + replay.FinalTick + " ticks");
            Console.WriteLine(check.ToString());
            return check.Success ? 0 : 1;
        }

        public int Export()
        {
            string path = ReplayFile();
            Replay replay = ReplayReader.Read(File.ReadAllBytes(path)); // no content check: old files can be inspected too
            Output.WriteText(Path.GetFullPath(options.Get("json") ?? path + ".json"), ReplayWriter.ToJson(replay));
            return 0;
        }
    }
}
