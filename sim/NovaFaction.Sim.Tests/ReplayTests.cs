using System.Text.Json;
using NovaFaction.Sim.Bots;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Replays;
using Xunit.Abstractions;

namespace NovaFaction.Sim.Tests;

/// <summary>Replay files (docs/design.md "Replays"): binary round trip, JSON export, verification and rejection.</summary>
public class ReplayTests
{
    private readonly ITestOutputHelper _output;

    public ReplayTests(ITestOutputHelper output) => _output = output;

    /// <summary>The shipped content as a library, optionally with other structures or bots.</summary>
    internal static ContentLibrary ShippedContent(StructureCatalog? structures = null, IEnumerable<BotPersonality>? bots = null,
        MatchRules? rules = null) =>
        new ContentLibrary(rules ?? MatchRulesTests.LoadShippedRules(), structures ?? TestSim.LoadStructures(),
            new[] { MapTestData.LoadTwoLane() }, new[] { TestSim.LoadFantasyCards() },
            bots ?? BotTests.ShippedBots.Select(BotTests.LoadBot));

    private static MatchSetup BotSetup(ContentLibrary content, string? bot0, string? bot1, int[]? levels0 = null,
        int structureLevel1 = 1)
    {
        CardCatalog cards = content.GetFaction("fantasy");
        Deck deck0 = Deck.Create(cards, TestSim.DefaultDeckIds, content.Rules.DeckSize, levels0);
        Deck deck1 = Deck.Create(cards, TestSim.BlizzardDeckIds, content.Rules.DeckSize);
        var setup = new MatchSetup(content.Rules, content.GetMap("twolane"), content.Structures, deck0, deck1, 1, structureLevel1);
        return setup.WithBot(0, bot0 == null ? null : content.GetBot(bot0))
            .WithBot(1, bot1 == null ? null : content.GetBot(bot1));
    }

    private static MatchResult BotMatch(ContentLibrary content, ulong seed, string bot0 = "aggressive", string bot1 = "swarm") =>
        HeadlessMatch.Run(BotSetup(content, bot0, bot1), seed);

    private static List<Command> AllCommands(CommandLog log)
    {
        var all = new List<Command>();
        for (int t = 0; t < log.TickCount; t++)
        {
            all.AddRange(log.GetCommands(t));
        }
        return all;
    }

    private static CommandLog CopyLog(CommandLog log, Func<Command, Command>? change = null)
    {
        var copy = new CommandLog();
        for (int t = 0; t < log.TickCount; t++)
        {
            copy.Record(t, log.GetCommands(t).Select(c => change == null ? c : change(c)).ToArray());
        }
        return copy;
    }

    private static Replay WithLog(Replay r, CommandLog log, ulong? finalHash = null, int? simVersion = null) =>
        new Replay(simVersion ?? r.SimVersion, r.ContentVersion, r.RulesVersion, r.MapId, r.MapContentHash, r.Seed, r.Players,
            log, r.FinalTick, finalHash ?? r.FinalHash);

    // ------------------------------------------------------------ round trip

    [Fact]
    public void BotMatch_RoundTripsThroughBinary_WithEveryHeaderField()
    {
        ContentLibrary content = ShippedContent();
        int[] levels = { 3, 1, 15, 2, 1, 1, 7, 4 }; // in DefaultDeckIds order
        var sim = HeadlessMatch.Run(BotSetup(content, "balanced", null, levels, structureLevel1: 5), 77,
            new[]
            {
                // Player 1 is scripted: one rejected deploy at a negative position, one real one, one ability.
                Command.DeployCard(0, 1, 0, 2, new FixVector2(Fix.Parse("-3.25"), Fix.Parse("-0.5"))),
                Command.DeployCard(40, 1, 0, 0, new FixVector2(Fix.Parse("9.5"), Fix.Parse("28.5"))),
                Command.LeaderAbility(41, 1, 1, new FixVector2(Fix.Parse("9"), Fix.Parse("25"))),
            }).Simulation;
        Replay original = Replay.FromMatch(sim);

        byte[] bytes = ReplayWriter.Write(original);
        Replay read = ReplayReader.Read(bytes, content);

        Assert.Equal(Replay.FormatVersion, (int)BitConverter.ToUInt16(bytes, 4));
        Assert.Equal(SimVersion.Current, read.SimVersion);
        Assert.Equal(ContentVersion.Compute(sim.Setup), read.ContentVersion);
        Assert.Equal(content.Rules.RulesVersion, read.RulesVersion);
        Assert.Equal("twolane", read.MapId);
        Assert.Equal(content.GetMap("twolane").ContentHash, read.MapContentHash);
        Assert.Equal(77UL, read.Seed);
        Assert.Equal("balanced", read.Players[0].BotId);
        Assert.Null(read.Players[1].BotId);
        Assert.False(read.Players[1].IsBot);
        Assert.Equal(1, read.Players[0].StructureLevel);
        Assert.Equal(5, read.Players[1].StructureLevel);
        for (int p = 0; p < 2; p++)
        {
            Deck deck = sim.Setup.GetDeck(p);
            Assert.Equal("fantasy", read.Players[p].Faction);
            Assert.Equal(deck.Cards.Select(c => c.Id), read.Players[p].CardIds);
            Assert.Equal(deck.Levels, read.Players[p].CardLevels);
        }
        Assert.Equal(15, read.Players[0].CardLevels[read.Players[0].CardIds.ToList().IndexOf("goblin_pack")]);
        Assert.Equal(sim.State.Tick, read.FinalTick);
        Assert.Equal(sim.ComputeHash(), read.FinalHash);
        Assert.Equal(sim.Log.TickCount, read.Log.TickCount);
        Assert.Equal(AllCommands(sim.Log), AllCommands(read.Log));
        Assert.Contains(AllCommands(read.Log), c => c.Target.X < Fix.Zero);
        Assert.Equal(bytes, ReplayWriter.Write(read)); // writing what was read gives the same bytes

        // The rebuilt setup plays the same match.
        Assert.True(read.Verify(content).Success);
        _output.WriteLine(bytes.Length + " bytes for " + read.Log.CommandCount + " commands over " + read.FinalTick + " ticks");
    }

    [Fact]
    public void BinaryFormat_IsCompact_AndStableForTheSameMatch()
    {
        ContentLibrary content = ShippedContent();
        Replay a = Replay.FromMatch(BotMatch(content, 5).Simulation);
        Replay b = Replay.FromMatch(BotMatch(content, 5).Simulation);
        byte[] bytes = ReplayWriter.Write(a);
        Assert.Equal(bytes, ReplayWriter.Write(b));
        Assert.True(a.Log.CommandCount > 20, "bots should have played");
        // Header + footer are a few hundred bytes; a command is well under 16 bytes.
        Assert.True(bytes.Length < 400 + 16 * a.Log.CommandCount, bytes.Length + " bytes for " + a.Log.CommandCount + " commands");
        _output.WriteLine(bytes.Length + " bytes, " + a.Log.CommandCount + " commands");
    }

    [Fact]
    public void JsonExport_IsValidJson_WithTheSameData()
    {
        ContentLibrary content = ShippedContent();
        Replay replay = Replay.FromMatch(BotMatch(content, 9).Simulation);
        string json = ReplayWriter.ToJson(ReplayReader.Read(ReplayWriter.Write(replay)));

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(Replay.FormatVersion, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(SimVersion.Current, root.GetProperty("simVersion").GetInt32());
        Assert.Equal("0x" + replay.ContentVersion.ToString("X16"), root.GetProperty("contentVersion").GetString());
        Assert.Equal("twolane", root.GetProperty("mapId").GetString());
        Assert.Equal(9UL, root.GetProperty("seed").GetUInt64());
        JsonElement players = root.GetProperty("players");
        Assert.Equal("aggressive", players[0].GetProperty("bot").GetString());
        Assert.Equal("swarm", players[1].GetProperty("bot").GetString());
        Assert.Equal(8, players[1].GetProperty("cards").GetArrayLength());
        Assert.Equal(replay.Log.CommandCount, root.GetProperty("commands").GetArrayLength());
        Command first = AllCommands(replay.Log)[0];
        JsonElement firstJson = root.GetProperty("commands")[0];
        Assert.Equal(first.Tick, firstJson.GetProperty("tick").GetInt32());
        Assert.Equal(first.Type.ToString(), firstJson.GetProperty("type").GetString());
        Assert.Equal(first.Target.X, Fix.Parse(firstJson.GetProperty("x").GetRawText()));
        Assert.Equal(replay.FinalTick, root.GetProperty("finalTick").GetInt32());
        Assert.Equal("0x" + replay.FinalHash.ToString("X16"), root.GetProperty("finalHash").GetString());
    }

    // ------------------------------------------------------------ verification

    [Theory]
    [InlineData(1UL, "aggressive", "swarm")]
    [InlineData(2UL, "turtle", "balanced")]
    public void Verify_Succeeds_ForARecordedBotMatch(ulong seed, string bot0, string bot1)
    {
        ContentLibrary content = ShippedContent();
        MatchResult result = HeadlessMatch.Run(BotSetup(content, bot0, bot1), seed);
        Replay replay = ReplayReader.Read(ReplayWriter.Write(Replay.FromMatch(result.Simulation)), content);

        ReplayVerification check = replay.Verify(content);

        Assert.True(check.Success, check.Message);
        Assert.True(check.Simulation!.IsEnded);
        Assert.Equal(result.Winner, check.Simulation.State.Winner);
        Assert.Equal(result.EndReason, check.Simulation.State.EndReason);
        Assert.Equal(result.FinalHash, check.Simulation.ComputeHash());
    }

    [Fact]
    public void Verify_Succeeds_ForAnUnfinishedMatch()
    {
        ContentLibrary content = ShippedContent();
        var sim = new Simulation(BotSetup(content, "swarm", "turtle"), 3);
        TestSim.Run(sim, 500);
        Replay replay = Replay.FromMatch(sim);
        Assert.Equal(500, replay.FinalTick);
        ReplayVerification check = replay.Verify(content);
        Assert.True(check.Success, check.Message);
        Assert.Contains("had not ended", check.Message);
    }

    [Fact]
    public void Verify_Fails_WhenACommandIsTampered()
    {
        ContentLibrary content = ShippedContent();
        Replay replay = Replay.FromMatch(BotMatch(content, 11).Simulation);
        Command victim = AllCommands(replay.Log).First(c => c.Type == CommandType.DeployCard);
        // Same shape, different card: the bot played slot N, the tampered file claims slot N+1.
        CommandLog tampered = CopyLog(replay.Log, c => c == victim
            ? Command.DeployCard(c.Tick, c.Player, c.Sequence, (c.HandSlot + 1) % content.Rules.HandSize, c.Target)
            : c);
        Replay read = ReplayReader.Read(ReplayWriter.Write(WithLog(replay, tampered)), content); // still a valid file

        ReplayVerification check = read.Verify(content);

        // Playing a different card changes the match: either the final hash no longer matches, or the match now ends
        // on a different tick. Both are the tampering being caught.
        Assert.False(check.Success);
        Assert.True(check.Message.Contains("hash") || check.Message.Contains("tick"), check.Message);
        Assert.StartsWith("FAILED", check.ToString());
    }

    [Fact]
    public void Verify_Fails_WhenACommandIsRemoved_OrTheHashIsTampered()
    {
        ContentLibrary content = ShippedContent();
        Replay replay = Replay.FromMatch(BotMatch(content, 12).Simulation);
        Command victim = AllCommands(replay.Log).Last(c => c.Type == CommandType.DeployCard);
        var dropped = new CommandLog();
        for (int t = 0; t < replay.Log.TickCount; t++)
        {
            dropped.Record(t, replay.Log.GetCommands(t).Where(c => c != victim).ToArray());
        }
        Assert.False(WithLog(replay, dropped).Verify(content).Success);

        byte[] bytes = ReplayWriter.Write(replay);
        bytes[bytes.Length - 1] ^= 0x01; // last byte of the final hash
        ReplayVerification check = ReplayReader.Read(bytes, content).Verify(content);
        Assert.False(check.Success);
        Assert.Contains("hash", check.Message);
    }

    [Fact]
    public void Verify_Fails_WhenTheMatchEndsBeforeTheReplayDoes()
    {
        ContentLibrary content = ShippedContent();
        Replay replay = Replay.FromMatch(BotMatch(content, 13).Simulation);
        // The same commands, but a replay claiming more (empty) ticks after the end.
        CommandLog longer = CopyLog(replay.Log);
        longer.Record(longer.TickCount, Array.Empty<Command>());
        var tooLong = new Replay(replay.SimVersion, replay.ContentVersion, replay.RulesVersion, replay.MapId,
            replay.MapContentHash, replay.Seed, replay.Players, longer, replay.FinalTick + 1, replay.FinalHash);

        ReplayVerification check = tooLong.Verify(content);

        Assert.False(check.Success);
        Assert.Contains("ended on tick " + replay.FinalTick, check.Message);
    }

    [Fact]
    public void Verify_Fails_WithAClearMessage_WhenACommandIsMalformed()
    {
        ContentLibrary content = ShippedContent();
        Replay replay = Replay.FromMatch(BotMatch(content, 14).Simulation);
        Command victim = AllCommands(replay.Log).First(c => c.Type == CommandType.DeployCard);
        CommandLog bad = CopyLog(replay.Log, c => c == victim
            ? Command.DeployCard(c.Tick, c.Player, c.Sequence, 9, c.Target) // no hand slot 9
            : c);

        ReplayVerification check = WithLog(replay, bad).Verify(content);

        Assert.False(check.Success);
        Assert.Contains("Tick " + victim.Tick + " could not run", check.Message);
    }

    // ------------------------------------------------------------ rejection

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(0x0100)]
    public void Reader_RejectsOtherFormatVersions(int version)
    {
        byte[] bytes = ReplayWriter.Write(Replay.FromMatch(BotMatch(ShippedContent(), 1).Simulation));
        bytes[4] = (byte)version;
        bytes[5] = (byte)(version >> 8);

        var ex = Assert.Throws<ReplayException>(() => ReplayReader.Read(bytes));

        Assert.Equal("Replay format version " + version + " is not supported; this build reads version 1.", ex.Message);
    }

    [Fact]
    public void Reader_RejectsFilesThatAreNotReplays()
    {
        Assert.Contains("not a NovaFaction replay", Assert.Throws<ReplayException>(() => ReplayReader.Read(Array.Empty<byte>())).Message);
        Assert.Contains("not a NovaFaction replay",
            Assert.Throws<ReplayException>(() => ReplayReader.Read("{\"json\": true}"u8.ToArray())).Message);
    }

    [Fact]
    public void Reader_RejectsMismatchedContentVersion()
    {
        ContentLibrary content = ShippedContent();
        byte[] bytes = ReplayWriter.Write(Replay.FromMatch(BotMatch(content, 1).Simulation));

        // Different structures.json: same rules, map and cards, but stats that content tuning will never produce.
        ContentLibrary otherStructures = ShippedContent(structures: TestSim.Structures(hp: "12345", keepHp: "23456"));
        var ex = Assert.Throws<ReplayException>(() => ReplayReader.Read(bytes, otherStructures));
        Assert.StartsWith("Content version mismatch: the replay was recorded with content 0x", ex.Message);

        // A tweaked bot personality counts too.
        BotPersonality tweaked = BotPersonality.FromJson(BotTests.BotJson("swarm").Replace("\"goldReserve\": 1", "\"goldReserve\": 2"));
        ContentLibrary otherBots = ShippedContent(bots: BotTests.ShippedBots.Where(b => b != "swarm").Select(BotTests.LoadBot)
            .Append(tweaked));
        Assert.StartsWith("Content version mismatch", Assert.Throws<ReplayException>(() => ReplayReader.Read(bytes, otherBots)).Message);

        // The unchecked read still works, for inspecting the file.
        Assert.Equal(1UL, ReplayReader.Read(bytes).Seed);
        // And the content it was recorded with still accepts it, even loaded afresh.
        Assert.Equal(1UL, ReplayReader.Read(bytes, ShippedContent()).Seed);
    }

    [Fact]
    public void Reader_RejectsOtherRulesVersionMissingContentAndOtherSimVersion()
    {
        ContentLibrary content = ShippedContent();
        Replay replay = Replay.FromMatch(BotMatch(content, 1).Simulation);
        byte[] bytes = ReplayWriter.Write(replay);

        int version = content.Rules.RulesVersion;
        MatchRules rules2 = MatchRules.FromJson(File.ReadAllText(MatchRulesTests.ContentPath("rules.json"))
            .Replace("\"rulesVersion\": " + version, "\"rulesVersion\": " + (version + 1)));
        Assert.Equal("The replay was recorded with rules version " + version + " but the loaded rules.json is version "
            + (version + 1) + ".",
            Assert.Throws<ReplayException>(() => ReplayReader.Read(bytes, ShippedContent(rules: rules2))).Message);

        var noSwarm = ShippedContent(bots: BotTests.ShippedBots.Where(b => b != "swarm").Select(BotTests.LoadBot));
        Assert.Equal("Player 1's bot \"swarm\" is not in the loaded content.",
            Assert.Throws<ReplayException>(() => ReplayReader.Read(bytes, noSwarm)).Message);

        var noMaps = new ContentLibrary(content.Rules, content.Structures, Array.Empty<Map.MapDefinition>(), content.Factions,
            content.Bots);
        Assert.Equal("The replay's map \"twolane\" is not in the loaded content.",
            Assert.Throws<ReplayException>(() => ReplayReader.Read(bytes, noMaps)).Message);

        byte[] future = ReplayWriter.Write(WithLog(replay, replay.Log, simVersion: SimVersion.Current + 1));
        Assert.Contains("recorded with sim version " + (SimVersion.Current + 1),
            Assert.Throws<ReplayException>(() => ReplayReader.Read(future, content)).Message);
        ReplayVerification check = WithLog(replay, replay.Log, simVersion: SimVersion.Current + 1).Verify(content);
        Assert.False(check.Success);
        Assert.Contains("sim version", check.Message);
    }

    [Fact]
    public void Reader_RejectsEveryTruncation_AndTrailingBytes_WithReplayException()
    {
        ContentLibrary content = ShippedContent();
        var sim = new Simulation(BotSetup(content, "aggressive", "swarm"), 4);
        TestSim.Run(sim, 400);
        byte[] bytes = ReplayWriter.Write(Replay.FromMatch(sim));
        Assert.True(sim.Log.CommandCount > 0);

        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] cut = bytes.Take(length).ToArray();
            Assert.Throws<ReplayException>(() => ReplayReader.Read(cut));
        }
        byte[] longer = bytes.Append((byte)0).ToArray();
        Assert.Contains("unexpected bytes", Assert.Throws<ReplayException>(() => ReplayReader.Read(longer)).Message);
    }

    [Fact]
    public void Reader_NeverThrowsAnythingButReplayException_OnCorruptBytes()
    {
        ContentLibrary content = ShippedContent();
        var sim = new Simulation(BotSetup(content, "aggressive", "swarm"), 4);
        TestSim.Run(sim, 200);
        byte[] bytes = ReplayWriter.Write(Replay.FromMatch(sim));
        var random = new SimRandom(99);
        for (int trial = 0; trial < 300; trial++)
        {
            byte[] corrupt = (byte[])bytes.Clone();
            int position = 6 + random.NextInt(0, corrupt.Length - 6); // keep magic and format version
            corrupt[position] = (byte)random.NextInt(0, 256);
            try
            {
                Replay replay = ReplayReader.Read(corrupt, content);
                replay.Verify(content); // must not throw either
            }
            catch (ReplayException)
            {
            }
        }
    }

    [Fact]
    public void ContentVersion_DependsOnTheContentInPlay_NotOnHowItWasLoaded()
    {
        ContentLibrary a = ShippedContent();
        ContentLibrary b = ShippedContent();
        Assert.Equal(ContentVersion.Compute(BotSetup(a, "turtle", "swarm")), ContentVersion.Compute(BotSetup(b, "turtle", "swarm")));
        Assert.NotEqual(ContentVersion.Compute(BotSetup(a, "turtle", "swarm")), ContentVersion.Compute(BotSetup(a, "turtle", "balanced")));
        Assert.NotEqual(ContentVersion.Compute(BotSetup(a, "turtle", "swarm")), ContentVersion.Compute(BotSetup(a, "turtle", null)));
        // Levels are header data, not content.
        Assert.Equal(ContentVersion.Compute(BotSetup(a, null, null)),
            ContentVersion.Compute(BotSetup(a, null, null, levels0: new[] { 2, 2, 2, 2, 2, 2, 2, 2 })));
    }

    [Fact]
    public void ContentLibrary_SortsById_AndRejectsDuplicates()
    {
        ContentLibrary content = ShippedContent();
        Assert.Equal(BotTests.ShippedBots.OrderBy(b => b, StringComparer.Ordinal), content.Bots.Select(b => b.Id));
        Assert.Contains("Known: aggressive, balanced, swarm, turtle.", Assert.Throws<KeyNotFoundException>(() => content.GetBot("nope")).Message);
        Assert.Throws<ArgumentException>(() => ShippedContent(bots: new[] { BotTests.LoadBot("swarm"), BotTests.LoadBot("swarm") }));
    }
}
