using NovaFaction.Sim;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Observers;

namespace NovaFaction.Harness;

/// <summary>What to play: the two sides (a bot personality id, or "idle" for a player who never acts), map and decks.</summary>
internal sealed record MatchPlan(string P0, string P1, string Map, IReadOnlyList<string> Deck0, IReadOnlyList<string> Deck1)
{
    public const string Idle = "idle";

    /// <summary>The fantasy default deck: all seven units plus Fireball.</summary>
    public static readonly string[] DefaultDeck =
        ["stone_golem", "knight", "goblin_pack", "elf_archer", "griffin", "catapult", "warlord", "fireball"];

    public string Side(int player) => player == 0 ? P0 : P1;

    /// <summary>Builds the match setup, with clear errors for unknown ids.</summary>
    public MatchSetup CreateSetup(ContentLibrary content)
    {
        try
        {
            MatchSetup setup = new(content.Rules, content.GetMap(Map), content.Structures,
                BuildDeck(content, Deck0), BuildDeck(content, Deck1));
            for (int p = 0; p < 2; p++)
            {
                string side = Side(p);
                setup = setup.WithBot(p, side == Idle ? null : content.GetBot(side));
            }
            return setup;
        }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException)
        {
            throw new HarnessException(e.Message);
        }
    }

    private static Deck BuildDeck(ContentLibrary content, IReadOnlyList<string> ids)
    {
        // The deck's faction is the one that has its first card.
        CardCatalog? faction = content.Factions.FirstOrDefault(f => f.TryGet(ids[0], out _));
        if (faction == null)
        {
            throw new HarnessException("No faction has the card \"" + ids[0] + "\".");
        }
        return Deck.Create(faction, ids, content.Rules.DeckSize);
    }
}

/// <summary>Per card, per side: what it did in one match (or summed over many).</summary>
internal sealed class CardStats
{
    public int Deploys { get; set; }
    public int GoldSpent { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public double StructureDamage { get; set; }

    public void Add(CardStats other)
    {
        Deploys += other.Deploys;
        GoldSpent += other.GoldSpent;
        Kills += other.Kills;
        Deaths += other.Deaths;
        StructureDamage += other.StructureDamage;
    }
}

internal sealed class PlayerStats
{
    public double GoldBase { get; set; }
    public double GoldMine { get; set; }
    public double GoldChest { get; set; }
    public double GoldTotal => GoldBase + GoldMine + GoldChest;
    public double GoldSpent { get; set; }
    public int MineCaptures { get; set; }
    public int ChestsCollected { get; set; }
    public int UnitsLost { get; set; }
    public int StructuresDestroyed { get; set; }
    public int TowersDestroyed { get; set; }
    public int Deploys { get; set; }
    public int Spells { get; set; }
    public int Abilities { get; set; }
}

internal sealed record TimelineEntry(int Tick, string Text);

/// <summary>Everything the harness keeps about one finished match.</summary>
internal sealed class MatchRecord
{
    public required ulong Seed { get; init; }
    public required string P0 { get; init; }
    public required string P1 { get; init; }
    public required string Map { get; init; }

    /// <summary>Ticks of regulation time; a match that ran longer went to sudden death.</summary>
    public required int RegulationTicks { get; init; }

    public int Winner { get; set; }
    public string EndReason { get; set; } = "";
    public string TieBreakRule { get; set; } = "";
    public int Ticks { get; set; }
    public double Seconds { get; set; }
    public double Score0 { get; set; }
    public double Score1 { get; set; }
    public double Margin => Score0 - Score1;
    public bool KeepKill => EndReason == nameof(Sim.Combat.EndReason.KeepDestroyed);

    /// <summary>The match went past regulation into sudden death.</summary>
    public bool SuddenDeath => Ticks > RegulationTicks;

    /// <summary>Forward towers destroyed in this match, by either side.</summary>
    public int TowerKills => Players[0].TowersDestroyed + Players[1].TowersDestroyed;
    public string FinalHash { get; set; } = "";
    public PlayerStats[] Players { get; } = [new PlayerStats(), new PlayerStats()];

    /// <summary>Key: (side, card id). Leader abilities are listed as "&lt;leader&gt; (ability)", structures as "[keep]"/"[tower]".</summary>
    public Dictionary<(int Player, string Card), CardStats> Cards { get; } = new();

    public List<TimelineEntry> Timeline { get; } = new();

    public CardStats Card(int player, string card)
    {
        if (!Cards.TryGetValue((player, card), out CardStats? stats))
        {
            Cards[(player, card)] = stats = new CardStats();
        }
        return stats;
    }
}

/// <summary>Turns match events into a <see cref="MatchRecord"/>. Never touches the simulation.</summary>
internal sealed class StatsObserver(MatchRecord record, int ticksPerSecond, bool keepTimeline, IReadOnlyList<string> sides)
    : IMatchObserver
{
    private readonly HashSet<int> _damagedStructures = new();

    public static double D(Fix value) => value.Raw / 65536.0;

    private static string CardKey(DamageSource source) => source.Kind switch
    {
        DamageSourceKind.Structure => "[" + source.CardId + "]",
        DamageSourceKind.LeaderAbility => source.CardId + " (ability)",
        _ => source.CardId,
    };

    private void Note(int tick, string text)
    {
        if (keepTimeline)
        {
            record.Timeline.Add(new TimelineEntry(tick, text));
        }
    }

    private string Who(int player) => "P" + player + " " + sides[player];

    private static string At(FixVector2 p) => p.X + "," + p.Y;

    public void OnCardDeployed(in CardDeployedEvent e)
    {
        CardStats card = record.Card(e.Player, e.CardId);
        card.Deploys++;
        card.GoldSpent += e.Cost;
        record.Players[e.Player].Deploys++;
        record.Players[e.Player].GoldSpent += e.Cost;
        Note(e.Tick, Who(e.Player) + " deploys " + e.CardId + " (" + e.Cost + " gold) at " + At(e.Target));
    }

    public void OnSpellCast(in SpellCastEvent e)
    {
        CardStats card = record.Card(e.Player, e.CardId);
        card.Deploys++;
        card.GoldSpent += e.Cost;
        record.Players[e.Player].Spells++;
        record.Players[e.Player].GoldSpent += e.Cost;
        Note(e.Tick, Who(e.Player) + " casts " + e.CardId + " (" + e.Cost + " gold) at " + At(e.Target));
    }

    public void OnAbilityCast(in AbilityCastEvent e)
    {
        record.Card(e.Player, e.LeaderCardId + " (ability)").Deploys++;
        record.Players[e.Player].Abilities++;
        Note(e.Tick, Who(e.Player) + " uses " + e.LeaderCardId + "'s " + e.Type + " at " + At(e.Target));
    }

    public void OnUnitDied(in UnitDiedEvent e)
    {
        record.Card(e.Owner, e.CardId).Deaths++;
        record.Players[e.Owner].UnitsLost++;
        if (e.Killer.Kind != DamageSourceKind.None)
        {
            record.Card(e.Killer.Player, CardKey(e.Killer)).Kills++;
        }
    }

    public void OnStructureDamaged(in StructureDamagedEvent e)
    {
        record.Card(e.Source.Player, CardKey(e.Source)).StructureDamage += D(e.Amount);
        if (_damagedStructures.Add(e.StructureIndex))
        {
            Note(e.Tick, Who(e.Source.Player) + " first hits P" + e.Owner + "'s " + e.Kind + " #" + e.StructureIndex
                + " with " + CardKey(e.Source));
        }
    }

    public void OnStructureDestroyed(in StructureDestroyedEvent e)
    {
        record.Players[e.Source.Player].StructuresDestroyed++;
        if (e.Kind == "tower")
        {
            record.Players[e.Source.Player].TowersDestroyed++;
        }
        Note(e.Tick, Who(e.Source.Player) + " DESTROYS P" + e.Owner + "'s " + e.Kind + " #" + e.StructureIndex
            + " (" + CardKey(e.Source) + ", +" + e.DestructionBonus + " score)");
    }

    public void OnMineCaptured(in MineCapturedEvent e)
    {
        record.Players[e.Player].MineCaptures++;
        Note(e.Tick, Who(e.Player) + " captures mine #" + e.MineIndex
            + (e.PreviousOwner >= 0 ? " from P" + e.PreviousOwner : ""));
    }

    public void OnChestCollected(in ChestCollectedEvent e)
    {
        record.Players[e.Player].ChestsCollected++;
        Note(e.Tick, Who(e.Player) + " collects chest #" + e.ChestIndex + " (+" + e.GoldReceived + " gold)");
    }

    public void OnGoldAccrued(in GoldAccruedEvent e)
    {
        PlayerStats p = record.Players[e.Player];
        switch (e.Source)
        {
            case GoldSource.Base: p.GoldBase += D(e.Amount); break;
            case GoldSource.Mine: p.GoldMine += D(e.Amount); break;
            default: p.GoldChest += D(e.Amount); break;
        }
    }

    public void OnMatchEnded(in MatchEndedEvent e)
    {
        record.Winner = e.Winner;
        record.EndReason = e.Reason.ToString();
        record.TieBreakRule = e.TieBreakRule.ToString();
        record.Ticks = e.Tick;
        record.Seconds = (double)e.Tick / ticksPerSecond;
        record.Score0 = D(e.Score0);
        record.Score1 = D(e.Score1);
        Note(e.Tick, "Match over: " + Who(e.Winner) + " wins by " + e.Reason
            + (e.TieBreakRule != Sim.Combat.TieBreakRule.None ? " (" + e.TieBreakRule + ")" : "")
            + ", score " + e.Score0 + " - " + e.Score1);
    }
}

internal static class MatchRunner
{
    /// <summary>
    /// Plays one match to the end. With <paramref name="hashes"/>, records the state hash before the first tick and
    /// after every tick. With <paramref name="observe"/> false no observer is attached (the record then only has the
    /// result fields).
    /// </summary>
    public static (MatchRecord Record, Simulation Sim) Run(ContentLibrary content, MatchSetup setup, MatchPlan plan,
        ulong seed, bool timeline = false, bool observe = true, List<ulong>? hashes = null)
    {
        var record = new MatchRecord
        {
            Seed = seed, P0 = plan.P0, P1 = plan.P1, Map = plan.Map,
            RegulationTicks = content.Rules.MatchLengthTicks,
        };
        var sim = new Simulation(setup, seed);
        if (observe)
        {
            sim.Observer = new StatsObserver(record, content.Rules.TicksPerSecond, timeline, [plan.P0, plan.P1]);
        }
        hashes?.Add(sim.ComputeHash());
        while (!sim.IsEnded)
        {
            sim.Tick();
            hashes?.Add(sim.ComputeHash());
        }
        MatchState s = sim.State;
        record.Winner = s.Winner;
        record.EndReason = s.EndReason.ToString();
        record.TieBreakRule = s.TieBreakRule.ToString();
        record.Ticks = s.Tick;
        record.Seconds = (double)s.Tick / content.Rules.TicksPerSecond;
        record.Score0 = StatsObserver.D(s.GetPlayer(0).Score);
        record.Score1 = StatsObserver.D(s.GetPlayer(1).Score);
        record.FinalHash = "0x" + sim.ComputeHash().ToString("X16");
        return (record, sim);
    }
}
