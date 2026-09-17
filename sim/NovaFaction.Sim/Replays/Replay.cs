using System;
using System.Collections.Generic;
using NovaFaction.Sim.Bots;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;

namespace NovaFaction.Sim.Replays
{
    /// <summary>A replay file could not be read or does not fit the loaded content. The message says why.</summary>
    public sealed class ReplayException : Exception
    {
        public ReplayException(string message) : base(message)
        {
        }

        public ReplayException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Fingerprint of all content a match uses: rules, structure stats, the map, both players' factions (units and
    /// spells) and the bot personalities in play. Built from each file's content hash (parsed data, so line endings and
    /// formatting never matter). Content the match does not use (other maps, other bots) is left out, so adding a map
    /// does not invalidate old replays.
    /// </summary>
    public static class ContentVersion
    {
        /// <summary>Bump when the fingerprint's layout changes.</summary>
        public const int LayoutVersion = 1;

        public static ulong Compute(MatchSetup setup)
        {
            if (setup == null)
            {
                throw new ArgumentNullException(nameof(setup));
            }
            var h = new StateHasher();
            h.Add(LayoutVersion);
            h.Add(setup.Rules.ContentHash);
            h.Add(setup.Structures.ContentHash);
            h.Add(setup.Map.ContentHash);
            for (int player = 0; player < Command.PlayerCount; player++)
            {
                h.Add(setup.GetDeck(player).Catalog.ContentHash);
                BotPersonality? bot = setup.GetBot(player);
                h.Add(bot != null);
                if (bot != null)
                {
                    h.Add(bot.ContentHash);
                }
            }
            return h.Value;
        }
    }

    /// <summary>One player's side of a replay header.</summary>
    public sealed class ReplayPlayer
    {
        public ReplayPlayer(string faction, IReadOnlyList<string> cardIds, IReadOnlyList<int> cardLevels, int structureLevel,
            string? botId)
        {
            Faction = faction ?? throw new ArgumentNullException(nameof(faction));
            if (cardIds == null)
            {
                throw new ArgumentNullException(nameof(cardIds));
            }
            if (cardLevels == null || cardLevels.Count != cardIds.Count)
            {
                throw new ArgumentException("Every card needs exactly one level.", nameof(cardLevels));
            }
            CardIds = Copy(cardIds);
            CardLevels = Copy(cardLevels);
            StructureLevel = structureLevel;
            BotId = botId;
        }

        public string Faction { get; }

        /// <summary>The deck's cards in deck order (sorted by id).</summary>
        public IReadOnlyList<string> CardIds { get; }

        /// <summary>Each card's level, in the order of <see cref="CardIds"/>.</summary>
        public IReadOnlyList<int> CardLevels { get; }

        public int StructureLevel { get; }

        /// <summary>The bot personality that played this side, or null for a human.</summary>
        public string? BotId { get; }

        public bool IsBot => BotId != null;

        private static T[] Copy<T>(IReadOnlyList<T> items)
        {
            var copy = new T[items.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = items[i];
            }
            return copy;
        }
    }

    /// <summary>The result of <see cref="Replay.Verify"/>.</summary>
    public sealed class ReplayVerification
    {
        internal ReplayVerification(bool success, string message, Simulation? simulation, ulong expectedHash)
        {
            Success = success;
            Message = message;
            Simulation = simulation;
            ExpectedHash = expectedHash;
        }

        public bool Success { get; }

        /// <summary>What was checked, or why verification failed.</summary>
        public string Message { get; }

        /// <summary>The re-run match (as far as it got), or null if it could not be built.</summary>
        public Simulation? Simulation { get; }

        public ulong ExpectedHash { get; }

        public override string ToString() => (Success ? "OK: " : "FAILED: ") + Message;
    }

    /// <summary>
    /// A recorded match: everything needed to re-run it (setup by content id, seed, command log) plus the final tick and
    /// state hash to check the re-run against. Written and read by <see cref="ReplayWriter"/> and
    /// <see cref="ReplayReader"/>; see docs/design.md ("Replays") for the file layout. Immutable.
    /// </summary>
    public sealed class Replay
    {
        /// <summary>The binary layout version. Bump when the file layout changes.</summary>
        public const int FormatVersion = 1;

        private readonly ReplayPlayer[] _players;

        public Replay(int simVersion, ulong contentVersion, int rulesVersion, string mapId, ulong mapContentHash, ulong seed,
            IReadOnlyList<ReplayPlayer> players, CommandLog log, int finalTick, ulong finalHash)
        {
            if (players == null || players.Count != Command.PlayerCount || players[0] == null || players[1] == null)
            {
                throw new ArgumentException("A replay has exactly two players.", nameof(players));
            }
            if (log == null)
            {
                throw new ArgumentNullException(nameof(log));
            }
            if (log.TickCount != finalTick)
            {
                throw new ArgumentException("The log covers " + log.TickCount + " ticks but the final tick is " + finalTick + ".",
                    nameof(finalTick));
            }
            SimVersion = simVersion;
            ContentVersion = contentVersion;
            RulesVersion = rulesVersion;
            MapId = mapId ?? throw new ArgumentNullException(nameof(mapId));
            MapContentHash = mapContentHash;
            Seed = seed;
            _players = new[] { players[0], players[1] };
            Log = log;
            FinalTick = finalTick;
            FinalHash = finalHash;
        }

        /// <summary><see cref="Sim.SimVersion.Current"/> of the build that recorded the match.</summary>
        public int SimVersion { get; }

        /// <summary><see cref="Replays.ContentVersion"/> of the recorded match's content.</summary>
        public ulong ContentVersion { get; }

        public int RulesVersion { get; }

        public string MapId { get; }

        public ulong MapContentHash { get; }

        public ulong Seed { get; }

        public IReadOnlyList<ReplayPlayer> Players => _players;

        /// <summary>Every tick's commands, ticks 0 .. FinalTick - 1. Do not change it.</summary>
        public CommandLog Log { get; }

        /// <summary>State.Tick when the recording stopped (the match length for a finished match).</summary>
        public int FinalTick { get; }

        /// <summary>State hash at <see cref="FinalTick"/>.</summary>
        public ulong FinalHash { get; }

        /// <summary>Records a match (normally a finished one) as it stands now. The log is copied.</summary>
        public static Replay FromMatch(Simulation simulation)
        {
            if (simulation == null)
            {
                throw new ArgumentNullException(nameof(simulation));
            }
            MatchSetup setup = simulation.Setup;
            var players = new ReplayPlayer[Command.PlayerCount];
            for (int p = 0; p < players.Length; p++)
            {
                Deck deck = setup.GetDeck(p);
                var ids = new string[deck.Cards.Count];
                for (int i = 0; i < ids.Length; i++)
                {
                    ids[i] = deck.Cards[i].Id;
                }
                players[p] = new ReplayPlayer(deck.Catalog.Faction, ids, deck.Levels, setup.GetStructureLevel(p),
                    setup.GetBot(p)?.Id);
            }
            CommandLog source = simulation.Log;
            var log = new CommandLog();
            for (int tick = 0; tick < source.TickCount; tick++)
            {
                log.Record(tick, source.GetCommands(tick));
            }
            return new Replay(Sim.SimVersion.Current, Replays.ContentVersion.Compute(setup), setup.Rules.RulesVersion,
                setup.Map.Id, setup.Map.ContentHash, simulation.Seed, players, log, simulation.State.Tick,
                simulation.ComputeHash());
        }

        /// <summary>
        /// Rebuilds the recorded match's setup (bots included) from the loaded content. Throws
        /// <see cref="ReplayException"/> when the content is missing or differs from what the match was recorded with.
        /// </summary>
        public MatchSetup CreateSetup(ContentLibrary content)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }
            MatchRules rules = content.Rules;
            if (rules.RulesVersion != RulesVersion)
            {
                throw new ReplayException("The replay was recorded with rules version " + RulesVersion
                    + " but the loaded rules.json is version " + rules.RulesVersion + ".");
            }
            if (!content.TryGetMap(MapId, out MapDefinition map))
            {
                throw new ReplayException("The replay's map \"" + MapId + "\" is not in the loaded content.");
            }
            if (map.ContentHash != MapContentHash)
            {
                throw new ReplayException("The map \"" + MapId + "\" has changed since the replay was recorded (map hash "
                    + Hex(map.ContentHash) + ", replay expects " + Hex(MapContentHash) + ").");
            }
            var decks = new Deck[Command.PlayerCount];
            for (int p = 0; p < decks.Length; p++)
            {
                ReplayPlayer player = _players[p];
                if (!content.TryGetFaction(player.Faction, out CardCatalog catalog))
                {
                    throw new ReplayException("Player " + p + "'s faction \"" + player.Faction + "\" is not in the loaded content.");
                }
                try
                {
                    decks[p] = Deck.Create(catalog, player.CardIds, rules.DeckSize, player.CardLevels);
                }
                catch (ArgumentException e)
                {
                    throw new ReplayException("Player " + p + "'s deck does not fit the loaded content: " + e.Message, e);
                }
            }
            MatchSetup setup;
            try
            {
                setup = new MatchSetup(rules, map, content.Structures, decks[0], decks[1],
                    _players[0].StructureLevel, _players[1].StructureLevel);
                for (int p = 0; p < decks.Length; p++)
                {
                    string? botId = _players[p].BotId;
                    if (botId == null)
                    {
                        continue;
                    }
                    if (!content.TryGetBot(botId, out BotPersonality bot))
                    {
                        throw new ReplayException("Player " + p + "'s bot \"" + botId + "\" is not in the loaded content.");
                    }
                    setup = setup.WithBot(p, bot);
                }
            }
            catch (ArgumentException e)
            {
                throw new ReplayException("The replay's match setup does not fit the loaded content: " + e.Message, e);
            }
            ulong loaded = Replays.ContentVersion.Compute(setup);
            if (loaded != ContentVersion)
            {
                throw new ReplayException("Content version mismatch: the replay was recorded with content " + Hex(ContentVersion)
                    + " but the loaded content is " + Hex(loaded) + ". The rules, structures, map, a faction's units or "
                    + "spells, or a bot personality differ from the recording.");
            }
            return setup;
        }

        /// <summary>
        /// Re-runs the match from the log (bot decisions come from the log, not the bots) and checks that it reaches
        /// <see cref="FinalTick"/> with <see cref="FinalHash"/>. Never throws for a bad replay; the result says why it failed.
        /// </summary>
        public ReplayVerification Verify(ContentLibrary content)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }
            if (SimVersion != Sim.SimVersion.Current)
            {
                return new ReplayVerification(false, SimVersionMessage(SimVersion), null, FinalHash);
            }
            MatchSetup setup;
            try
            {
                setup = CreateSetup(content);
            }
            catch (ReplayException e)
            {
                return new ReplayVerification(false, e.Message, null, FinalHash);
            }
            var sim = new Simulation(setup.WithoutBots(), Seed);
            try
            {
                for (int player = 0; player < Command.PlayerCount; player++)
                {
                    sim.GetHuman(player)!.SubmitAll(Log);
                }
                while (sim.State.Tick < FinalTick)
                {
                    if (sim.IsEnded)
                    {
                        return new ReplayVerification(false, "The match ended on tick " + sim.State.Tick
                            + " but the replay runs to tick " + FinalTick + ".", sim, FinalHash);
                    }
                    sim.Tick();
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                return new ReplayVerification(false, "Tick " + sim.State.Tick + " could not run: " + e.Message, sim, FinalHash);
            }
            ulong hash = sim.ComputeHash();
            if (hash != FinalHash)
            {
                return new ReplayVerification(false, "State hash on tick " + FinalTick + " is " + Hex(hash) + " but the replay "
                    + "expects " + Hex(FinalHash) + ".", sim, FinalHash);
            }
            return new ReplayVerification(true, "Reached tick " + FinalTick + " with hash " + Hex(hash)
                + (sim.IsEnded ? "; the match ended (winner " + sim.State.Winner + ", " + sim.State.EndReason + ")." : "; the match had not ended."),
                sim, FinalHash);
        }

        internal static string SimVersionMessage(int recorded) =>
            "The replay was recorded with sim version " + recorded + " but this build is sim version "
            + Sim.SimVersion.Current + "; it cannot be re-run here.";

        internal static string Hex(ulong value) => "0x" + value.ToString("X16");
    }
}
