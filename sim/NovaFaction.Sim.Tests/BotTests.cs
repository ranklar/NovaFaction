using System.Diagnostics;
using NovaFaction.Sim.Bots;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Controllers;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Spells;
using NovaFaction.Sim.Units;
using Xunit.Abstractions;
using static NovaFaction.Sim.Tests.CombatTests;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// The bot (docs/design.md "Controllers and bots"): personality files, decisions, and whole headless matches on the
/// shipped content (rules, twolane, structures, fantasy cards, default deck for both players).
/// </summary>
public class BotTests
{
    private readonly ITestOutputHelper _output;

    public BotTests(ITestOutputHelper output) => _output = output;

    internal static readonly string[] ShippedBots = { "balanced", "aggressive", "turtle", "swarm" };

    internal static string BotJson(string id) =>
        File.ReadAllText(MatchRulesTests.ContentPath(Path.Combine("bots", id + ".json")));

    internal static BotPersonality LoadBot(string id) => BotPersonality.FromJson(BotJson(id), id + ".json");

    /// <summary>A bot that always takes its best-scoring action, for tests of specific decisions.</summary>
    private static BotPersonality Sharp(string aggression = "0", string spellUsage = "0.5", string mineFocus = "0",
        string reserve = "0", string delay = "0.75") =>
        BotPersonality.FromJson("{\"formatVersion\": 1, \"id\": \"sharp\", \"reactionDelaySeconds\": " + delay
            + ", \"decisionQuality\": 1, \"aggression\": " + aggression + ", \"defensiveness\": 0.5, \"mineFocus\": "
            + mineFocus + ", \"spellUsage\": " + spellUsage + ", \"goldReserve\": " + reserve + "}");

    internal static MatchSetup ShippedSetup() =>
        TestSim.Setup(MatchRulesTests.LoadShippedRules(), MapTestData.LoadTwoLane());

    /// <summary>Shipped cards and map, structures that do nothing, no income: the board only changes when a test says so.</summary>
    private static Simulation QuietSim(BotPersonality bot, string startGold = "0") =>
        new Simulation(TestSim.Setup(TestSim.Rules(income: "0", start: startGold), MapTestData.LoadTwoLane(), Quiet())
            .WithBot(0, bot), 1);

    private static void Step(Simulation sim, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            sim.Tick();
        }
    }

    private static List<Command> CommandsOf(CommandLog log, int player, CommandType type)
    {
        var result = new List<Command>();
        for (int t = 0; t < log.TickCount; t++)
        {
            result.AddRange(log.GetCommands(t).Where(c => c.Player == player && c.Type == type));
        }
        return result;
    }

    private static void AssertNoRejectedCommands(MatchState state)
    {
        foreach (PlayerState p in state.Players)
        {
            Assert.Equal(0, p.IgnoredDeploys);
            Assert.Equal(0, p.IgnoredAbilities);
        }
    }

    // ------------------------------------------------------------ personality files

    [Fact]
    public void ShippedPersonalities_LoadWithTheirIds_AndAreDistinct()
    {
        MatchRules rules = MatchRulesTests.LoadShippedRules();
        foreach (string id in ShippedBots)
        {
            BotPersonality bot = LoadBot(id);
            Assert.Equal(id, bot.Id);
            Assert.True(bot.IsPlaceholder);
            Assert.True(rules.IsWholeTicks(bot.ReactionDelaySeconds));
        }
        BotPersonality aggressive = LoadBot("aggressive"), turtle = LoadBot("turtle");
        Assert.True(aggressive.Aggression > turtle.Aggression);
        Assert.True(aggressive.GoldReserve < turtle.GoldReserve);
        Assert.True(turtle.Defensiveness > aggressive.Defensiveness);
    }

    [Fact]
    public void Personality_ReadsEveryValue()
    {
        BotPersonality bot = BotPersonality.FromJson("{\"formatVersion\": 1, \"id\": \"x\", \"reactionDelaySeconds\": 0.25, "
            + "\"decisionQuality\": 0.1, \"aggression\": 0.2, \"defensiveness\": 0.3, \"mineFocus\": 0.4, "
            + "\"spellUsage\": 0.5, \"goldReserve\": 3.5}");
        Assert.Equal("x", bot.Id);
        Assert.Equal(Fix.Parse("0.25"), bot.ReactionDelaySeconds);
        Assert.Equal(Fix.Parse("0.1"), bot.DecisionQuality);
        Assert.Equal(Fix.Parse("0.2"), bot.Aggression);
        Assert.Equal(Fix.Parse("0.3"), bot.Defensiveness);
        Assert.Equal(Fix.Parse("0.4"), bot.MineFocus);
        Assert.Equal(Fix.Parse("0.5"), bot.SpellUsage);
        Assert.Equal(Fix.Parse("3.5"), bot.GoldReserve);
        Assert.False(bot.IsPlaceholder);
    }

    [Theory]
    [InlineData("\"aggression\": 0.5", "\"aggression\": 1.5", "aggression must be at least 0 and at most 1")]
    [InlineData("\"spellUsage\": 0.5", "\"spellUsage\": -0.1", "spellUsage must be")]
    [InlineData("\"reactionDelaySeconds\": 0.75", "\"reactionDelaySeconds\": 0", "reactionDelaySeconds must be above 0")]
    [InlineData("\"reactionDelaySeconds\": 0.75", "\"reactionDelaySeconds\": 11", "at most 10")]
    [InlineData("\"goldReserve\": 2", "\"goldReserve\": -1", "goldReserve must be")]
    [InlineData("\"goldReserve\": 2,", "\"goldReserve\": 2, \"cunning\": 1,", "unknown key \"cunning\"")]
    [InlineData("\"mineFocus\": 0.4,", "", "missing required key \"mineFocus\"")]
    [InlineData("\"formatVersion\": 1", "\"formatVersion\": 2", "formatVersion must be 1")]
    [InlineData("\"id\": \"balanced\"", "\"id\": \"Balanced\"", "id must be")]
    public void Personality_RejectsBadFiles(string find, string replace, string message)
    {
        string json = BotJson("balanced");
        Assert.Contains(find, json);
        var error = Assert.Throws<SimJsonException>(() => BotPersonality.FromJson(json.Replace(find, replace), "bad.json"));
        Assert.Contains(message, error.Message);
        Assert.Contains("bad.json", error.Message);
    }

    [Fact]
    public void Setup_RejectsAReactionDelayThatIsNotWholeTicks()
    {
        BotPersonality odd = Sharp(delay: "0.33");
        var error = Assert.Throws<ArgumentException>(() => ShippedSetup().WithBot(1, odd));
        Assert.Contains("whole number of ticks", error.Message);
    }

    [Fact]
    public void Setup_AssignsControllersPerPlayer_WithoutChangingTheOriginal()
    {
        MatchSetup plain = ShippedSetup();
        MatchSetup withBot = plain.WithBot(1, LoadBot("turtle"));
        Assert.Null(plain.GetBot(1));
        Assert.Equal("turtle", withBot.GetBot(1)!.Id);
        Assert.Null(withBot.WithoutBots().GetBot(1));

        var sim = new Simulation(withBot, 5);
        Assert.IsType<HumanController>(sim.GetController(0));
        var bot = Assert.IsType<BotController>(sim.GetController(1));
        Assert.Equal(1, bot.Player);
        Assert.Equal(20, bot.ReactionDelayTicks);
        Assert.Null(sim.GetHuman(1));
        Assert.Equal(new Simulation(plain, 5).ComputeHash(), sim.ComputeHash()); // controllers are not match state
    }

    [Fact]
    public void BotSeeds_DependOnMatchSeedAndPlayer()
    {
        var seeds = new HashSet<ulong> { BotController.DeriveSeed(1, 0), BotController.DeriveSeed(1, 1), BotController.DeriveSeed(2, 0) };
        Assert.Equal(3, seeds.Count);
        Assert.Equal(BotController.DeriveSeed(1, 0), BotController.DeriveSeed(1, 0));
        Assert.DoesNotContain(1UL, seeds);
    }

    // ------------------------------------------------------------ controllers

    [Fact]
    public void HumanController_DeliversQueuedCommandsOnTheirTick_AndTheyAreLogged()
    {
        var viaQueue = new Simulation(ShippedSetup(), 3);
        var viaTick = new Simulation(ShippedSetup(), 3);
        FixVector2 target = TestSim.V("9.5", "10.5");
        PlayerState p0 = viaQueue.State.GetPlayer(0);
        int slot = Enumerable.Range(0, 4).First(i => p0.Cards.Hand[i]!.Cost <= 5); // affordable with the starting gold
        viaQueue.GetHuman(0)!.Submit(Command.DeployCard(2, 0, 0, slot, target));
        viaQueue.GetHuman(1)!.Submit(Command.None(1, 1, 0));
        Assert.Equal(2, viaQueue.GetHuman(0)!.PendingCount + viaQueue.GetHuman(1)!.PendingCount);
        for (int tick = 0; tick < 3; tick++)
        {
            viaQueue.Tick();
            viaTick.Tick(tick == 1 ? new[] { Command.None(1, 1, 0) }
                : tick == 2 ? new[] { Command.DeployCard(2, 0, 0, slot, target) } : Array.Empty<Command>());
            Assert.Equal(viaTick.ComputeHash(), viaQueue.ComputeHash());
        }
        Assert.Equal(0, viaQueue.GetHuman(0)!.PendingCount);
        Assert.Single(viaQueue.Log.GetCommands(2));
        Assert.Equal(1, viaQueue.State.PendingSpawns.Count + viaQueue.State.PendingSpells.Count);
        Assert.Equal(0, p0.IgnoredDeploys);
    }

    [Fact]
    public void HumanController_RejectsOtherPlayersAndStaleCommands_WithoutChangingTheMatch()
    {
        var sim = new Simulation(ShippedSetup(), 3);
        HumanController human = sim.GetHuman(0)!;
        Assert.Throws<ArgumentException>(() => human.Submit(Command.None(0, 1, 0)));

        sim.Tick();
        human.Submit(Command.None(0, 0, 0)); // tick 0 has already run
        ulong before = sim.ComputeHash();
        Assert.Throws<InvalidOperationException>(() => sim.Tick());
        Assert.Equal(before, sim.ComputeHash());
        Assert.Equal(1, sim.Log.TickCount);
    }

    [Fact]
    public void OutsideCommands_ForABotPlayer_AreRejected()
    {
        var sim = new Simulation(ShippedSetup().WithBot(0, LoadBot("balanced")), 3);
        ulong before = sim.ComputeHash();
        Assert.Throws<ArgumentException>(() => sim.Tick(new[] { Command.None(0, 0, 0) }));
        Assert.Equal(before, sim.ComputeHash());
        sim.Tick(new[] { Command.None(0, 1, 0) }); // the human player may still send commands
        Assert.Equal(1, sim.State.GetPlayer(1).CommandsReceived);
    }

    [Fact]
    public void Headless_RejectsScriptedCommandsForABotPlayer()
    {
        MatchSetup setup = ShippedSetup().WithBot(0, LoadBot("balanced"));
        Assert.Throws<ArgumentException>(() => HeadlessMatch.Run(setup, 1, new[] { Command.None(0, 0, 0) }));
    }

    // ------------------------------------------------------------ leader redeploy rule

    [Fact]
    public void LeaderRedeploy_IsIgnoredWhilePendingOrAlive_AndAllowedAfterDeath()
    {
        var sim = new Simulation(TestSim.Setup(TestSim.Rules(income: "0", start: "10", cap: "100"),
            MapTestData.LoadTwoLane(), Quiet()), 1);
        PlayerState p0 = sim.State.GetPlayer(0);
        p0.Gold = Fix.FromInt(100);
        FixVector2 spot = TestSim.V("9.5", "10.5");

        TestSim.Deploy(sim, 0, "warlord", spot);
        Assert.Equal(Fix.FromInt(94), p0.Gold);
        Assert.True(sim.State.HasLeaderOnField(0));
        Assert.False(sim.State.HasLeaderOnField(1));

        // A second copy is refused and nothing else changes.
        void AssertRefused()
        {
            int slot = TestSim.EnsureInHand(p0, "warlord");
            string?[] hand = TestSim.HandIds(p0);
            Fix gold = p0.Gold;
            int ignored = p0.IgnoredDeploys;
            int leaders = sim.State.Units.Count(u => u.Definition.IsLeader && u.Owner == 0)
                + sim.State.PendingSpawns.Count(s => s.Definition.IsLeader && s.Owner == 0);
            sim.Tick(new[] { Command.DeployCard(sim.State.Tick, 0, 0, slot, spot) });
            Assert.Equal(ignored + 1, p0.IgnoredDeploys);
            Assert.Equal(gold, p0.Gold);
            Assert.Equal(hand, TestSim.HandIds(p0));
            Assert.Equal(leaders, sim.State.Units.Count(u => u.Definition.IsLeader && u.Owner == 0)
                + sim.State.PendingSpawns.Count(s => s.Definition.IsLeader && s.Owner == 0));
        }
        AssertRefused(); // still waiting out the spawn delay
        Assert.Single(sim.State.PendingSpawns);

        TestSim.Run(sim, 20);
        Unit leader = Assert.Single(sim.State.Units);
        AssertRefused(); // alive on the field
        Assert.Single(sim.State.Units);
        Assert.Empty(sim.State.PendingSpawns);

        // The other player's leader is not affected.
        TestSim.Deploy(sim, 1, "warlord", TestSim.V("9.5", "21.5"));
        Assert.Equal(0, sim.State.GetPlayer(1).IgnoredDeploys);

        leader.Hp = Fix.Zero; // killed: removed during the next tick
        TestSim.Run(sim, 1);
        Assert.False(sim.State.HasLeaderOnField(0));
        int before = p0.IgnoredDeploys;
        TestSim.Deploy(sim, 0, "warlord", spot);
        Assert.Equal(before, p0.IgnoredDeploys);
        Assert.True(sim.State.HasLeaderOnField(0));
    }

    // ------------------------------------------------------------ decisions

    [Fact]
    public void Bot_DefendsAThreatenedTower_WithinOneReactionDelay()
    {
        Simulation sim = QuietSim(Sharp());
        var bot = (BotController)sim.GetController(0);
        Step(sim, 37); // no gold: nothing to do
        Assert.Equal(0, sim.Log.CommandCount);

        // A push appears in front of player 0's west tower, and the bot now has gold.
        int pushTick = sim.State.Tick;
        FixVector2 push = TestSim.V("4", "11");
        Place(sim, 1, "knight", "4", "11");
        Place(sim, 1, "goblin_pack", "5", "11.5");
        Place(sim, 1, "goblin_pack", "3.5", "11.5");
        sim.State.GetPlayer(0).Gold = Fix.FromInt(10);
        Step(sim, bot.ReactionDelayTicks);

        Command deploy = Assert.Single(CommandsOf(sim.Log, 0, CommandType.DeployCard));
        Assert.InRange(deploy.Tick, pushTick, pushTick + bot.ReactionDelayTicks - 1);
        Assert.StartsWith("defend tower_0_west", bot.LastAction);
        PendingSpawn spawn = Assert.Single(sim.State.PendingSpawns);
        Assert.Equal(TargetPriority.Any, spawn.Definition.TargetPriority);
        // Near the tower and between it and the attackers.
        Fix toTower = UnitMovement.DistanceToFootprint(sim.State.Map.Grid, deploy.Target, Tower0West);
        Fix pushToTower = UnitMovement.DistanceToFootprint(sim.State.Map.Grid, push, Tower0West);
        Assert.True(toTower < pushToTower, "deployed at " + deploy.Target);
        Assert.True(FixVector2.Distance(deploy.Target, push) < pushToTower, "deployed at " + deploy.Target);
        AssertNoRejectedCommands(sim.State);
    }

    [Fact]
    public void Bot_WithAHighReserve_DoesNothingWhenNothingThreatens()
    {
        Simulation sim = QuietSim(Sharp(aggression: "0", reserve: "20"), startGold: "8");
        Step(sim, 200);
        Assert.Empty(CommandsOf(sim.Log, 0, CommandType.DeployCard));
    }

    [Fact]
    public void Bot_CastsFireballOnACluster()
    {
        Simulation sim = QuietSim(Sharp());
        TestSim.EnsureInHand(sim.State.GetPlayer(0), "fireball");
        Place(sim, 1, "goblin_pack", "9", "9");
        Place(sim, 1, "goblin_pack", "9.5", "9.5");
        Place(sim, 1, "elf_archer", "8.5", "9.5");
        Place(sim, 1, "elf_archer", "9.5", "8.5");
        sim.State.GetPlayer(0).Gold = Fix.FromInt(4);
        Step(sim);

        var bot = (BotController)sim.GetController(0);
        Assert.StartsWith("cast fireball", bot.LastAction);
        SpellInstance spell = Assert.Single(sim.State.PendingSpells);
        Assert.Equal(0, spell.Owner);
        Assert.True(FixVector2.Distance(spell.Target, TestSim.V("9.1", "9.1")) < Fix.One, "aimed at " + spell.Target);
        AssertNoRejectedCommands(sim.State);
    }

    [Fact]
    public void Bot_DoesNotFireballALoneCheapUnit()
    {
        Simulation sim = QuietSim(Sharp());
        TestSim.EnsureInHand(sim.State.GetPlayer(0), "fireball");
        Place(sim, 1, "elf_archer", "9", "9");
        sim.State.GetPlayer(0).Gold = Fix.FromInt(4);
        for (int i = 0; i < 100; i++)
        {
            sim.State.GetPlayer(0).Gold = Fix.FromInt(4); // keeps the Fireball affordable after any unit drop
            sim.Tick();
        }
        Assert.Equal(1, sim.State.NextSpellId); // no spell was ever cast
        Assert.NotEmpty(CommandsOf(sim.Log, 0, CommandType.DeployCard)); // it answered with units instead
    }

    [Fact]
    public void Bot_UsesWarCryDuringAPush()
    {
        Simulation sim = QuietSim(Sharp());
        Unit leader = Place(sim, 0, "warlord", "4", "21.5");
        Place(sim, 0, "knight", "3", "22.5");
        Place(sim, 0, "knight", "4.5", "22.5");
        Place(sim, 0, "goblin_pack", "5", "22");
        Step(sim, 40);

        Command cry = Assert.Single(CommandsOf(sim.Log, 0, CommandType.LeaderAbility));
        Assert.Contains("War Cry at tower_1_west", ((BotController)sim.GetController(0)).LastAction);
        Assert.Equal(cry.Tick + 20 * 20, sim.State.GetPlayer(0).AbilityReadyTick);
        Assert.True(sim.State.Units.Count(u => u.Owner == 0 && u.Buff != null) >= 3);
        Assert.True(FixVector2.Distance(leader.Position, cry.Target) <= Fix.FromInt(6));
        AssertNoRejectedCommands(sim.State);
    }

    [Fact]
    public void Bot_DoesNotRallyWithoutAPush()
    {
        Simulation sim = QuietSim(Sharp());
        Place(sim, 0, "warlord", "9", "5");
        Step(sim, 100);
        Assert.Empty(CommandsOf(sim.Log, 0, CommandType.LeaderAbility));
    }

    [Fact]
    public void Bot_NeverDeploysItsLeaderWhileItIsOnTheField()
    {
        Simulation sim = QuietSim(Sharp(aggression: "1"));
        Place(sim, 0, "warlord", "9", "5");
        for (int i = 0; i < 400; i++)
        {
            sim.State.GetPlayer(0).Gold = Fix.FromInt(10);
            sim.Tick();
        }
        Assert.True(CommandsOf(sim.Log, 0, CommandType.DeployCard).Count > 5);
        Assert.Equal(1, sim.State.Units.Count(u => u.Definition.IsLeader));
        Assert.DoesNotContain(sim.State.PendingSpawns, p => p.Definition.IsLeader);
        AssertNoRejectedCommands(sim.State);
    }

    // ------------------------------------------------------------ saving for the card the plan wants

    /// <summary>The default deck without the Stone Golem, so the 6-cost leader is the only card the bot rates a tank.</summary>
    private static readonly string[] NoGolemDeck =
        { "catapult", "knight", "goblin_pack", "elf_archer", "griffin", "warlord", "fireball", "blizzard" };

    /// <summary>A quiet sim (no income, nothing shoots) whose only tank card is the leader.</summary>
    private static Simulation SavingSim(BotPersonality bot, string startGold) =>
        new Simulation(TestSim.Setup(TestSim.Rules(income: "0", start: startGold), MapTestData.LoadTwoLane(), Quiet(),
            NoGolemDeck).WithBot(0, bot), 1);

    /// <summary>A saving sim in which chests are on the field from the first tick.</summary>
    private static Simulation ChestSim(BotPersonality bot, string startGold) =>
        new Simulation(TestSim.Setup(TestSim.Rules(income: "0", start: startGold, chestFirst: "0", chestInterval: "30"),
            MapTestData.LoadTwoLane(), Quiet(), NoGolemDeck).WithBot(0, bot), 1);

    /// <summary>Parks a capturer next to each mine so the bot has no mine left to send anyone to.</summary>
    private static void CoverBothMines(Simulation sim)
    {
        // Far enough from the chests at (3, 11) and (14, 11) that they do not count as covered too.
        Place(sim, 0, "knight", "5.5", "17.5"); // mine 0 is the cell (5, 15)
        Place(sim, 0, "knight", "13.5", "17.5"); // mine 1 is the cell (12, 16)
    }

    [Fact]
    public void Bot_SavesForTheLeader_InsteadOfSpendingOnCheaperAttacks()
    {
        // Nothing threatens and no map gold is wanted, so the only thing to do is push. The 6-cost leader leads a
        // push and the cheaper cards in hand only trickle, so with 5 gold the bot must sit on its gold.
        Simulation sim = SavingSim(Sharp(aggression: "1", mineFocus: "0"), startGold: "5");
        PlayerState p0 = sim.State.GetPlayer(0);
        TestSim.EnsureInHand(p0, "warlord");
        Assert.Contains(TestSim.HandIds(p0), id => id != null && id != "warlord"); // something cheaper is in hand too

        Step(sim, 100);
        Assert.Empty(CommandsOf(sim.Log, 0, CommandType.DeployCard));
        Assert.Equal(Fix.FromInt(5), p0.Gold);

        // One more gold and the leader goes down; nothing cheaper was played in the meantime.
        p0.Gold = Fix.FromInt(6);
        Step(sim, 20);
        Assert.Single(CommandsOf(sim.Log, 0, CommandType.DeployCard));
        Assert.Contains("warlord", ((BotController)sim.GetController(0)).LastAction);
        PendingSpawn spawn = Assert.Single(sim.State.PendingSpawns);
        Assert.Equal("warlord", spawn.Definition.Id);
        AssertNoRejectedCommands(sim.State);
    }

    [Fact]
    public void Bot_WithNothingBetterToWaitFor_SpendsAsBefore()
    {
        // The same position without the leader in hand: there is nothing worth saving for, so the bot plays at once.
        Simulation sim = SavingSim(Sharp(aggression: "1", mineFocus: "0"), startGold: "5");
        PlayerState p0 = sim.State.GetPlayer(0);
        while (TestSim.SlotOf(p0, "warlord") >= 0)
        {
            p0.Cards.Play(TestSim.SlotOf(p0, "warlord"));
        }
        Step(sim, 20);
        Assert.NotEmpty(CommandsOf(sim.Log, 0, CommandType.DeployCard));
    }

    [Fact]
    public void Bot_StillDefendsWhileSavingForTheLeader()
    {
        // Saving never holds back a defence: an unanswered push costs more than a missed leader.
        Simulation sim = SavingSim(Sharp(aggression: "1", mineFocus: "0"), startGold: "5");
        PlayerState p0 = sim.State.GetPlayer(0);
        TestSim.EnsureInHand(p0, "warlord");
        Step(sim, 40);
        Assert.Empty(CommandsOf(sim.Log, 0, CommandType.DeployCard)); // saving

        Place(sim, 1, "knight", "4", "11");
        Place(sim, 1, "goblin_pack", "5", "11.5");
        Place(sim, 1, "goblin_pack", "3.5", "11.5");
        Step(sim, 20);
        Assert.NotEmpty(CommandsOf(sim.Log, 0, CommandType.DeployCard));
        Assert.StartsWith("defend tower_0_west", ((BotController)sim.GetController(0)).LastAction);
        Assert.True(p0.Gold < Fix.FromInt(5), "the defender was paid for out of the savings");
        AssertNoRejectedCommands(sim.State);
    }

    [Fact]
    public void Bot_DoesNotSpendItsSavingsOnALowerValueMineDrop()
    {
        // A bot that cares little about mines would rather wait for the card its push wants than trickle the gold
        // into a capturer: saving must not simply push the gold into whatever cheap action is left.
        Simulation sim = SavingSim(Sharp(aggression: "1", mineFocus: "0.1"), startGold: "5");
        TestSim.EnsureInHand(sim.State.GetPlayer(0), "warlord");
        Step(sim, 100);
        Assert.Empty(CommandsOf(sim.Log, 0, CommandType.DeployCard));
    }

    [Fact]
    public void Bot_SpendsItsSavingsOnAMineWhenMinesMatterMoreThanThePush()
    {
        // The same position with a mine-hungry, unaggressive personality: the mine drop is worth more than the push
        // the bot is declining, so breaking into the savings for it is the better play.
        Simulation sim = SavingSim(Sharp(aggression: "0", mineFocus: "1"), startGold: "5");
        TestSim.EnsureInHand(sim.State.GetPlayer(0), "warlord");
        Step(sim, 40);
        Assert.NotEmpty(CommandsOf(sim.Log, 0, CommandType.DeployCard));
        Assert.StartsWith("mine ", ((BotController)sim.GetController(0)).LastAction);
    }

    // ------------------------------------------------------------ chests

    [Fact]
    public void Bot_SendsAUnitForAChestOnItsOwnSideOfTheLaneItIsPushing()
    {
        Simulation sim = ChestSim(Sharp(aggression: "0", mineFocus: "1"), startGold: "10");
        Assert.All(sim.State.Chests, c => Assert.True(c.IsPresent));
        CoverBothMines(sim);
        Step(sim, 1); // the first decision is made on tick 0

        Command deploy = Assert.Single(CommandsOf(sim.Log, 0, CommandType.DeployCard));
        Assert.StartsWith("chest 0 ", ((BotController)sim.GetController(0)).LastAction);
        // Chest 0 sits on the cell (3, 11), in the west lane the bot pushes first (both towers are at full hp).
        Assert.Equal(sim.State.Chests[0].Position, deploy.Target);
        AssertNoRejectedCommands(sim.State);
    }

    [Fact]
    public void Bot_CollectsTheChestItWasSentFor()
    {
        Simulation sim = ChestSim(Sharp(aggression: "0", mineFocus: "1"), startGold: "10");
        CoverBothMines(sim);
        Step(sim, 60); // decide, spawn delay, collect
        Assert.False(sim.State.Chests[0].IsPresent);
        Assert.True(sim.State.GetPlayer(0).GoldFromMap > Fix.Zero);
    }

    [Fact]
    public void Bot_LeavesTheEnemySideChestsAlone()
    {
        // Chests 2 and 3 are nearer player 1's Keep, so player 0 never drops a unit for them. (Its units may still
        // walk over one on their way to the enemy base; that is the normal pass-by pickup, not a decision.)
        Simulation sim = ChestSim(Sharp(aggression: "0", mineFocus: "1"), startGold: "10");
        var bot = (BotController)sim.GetController(0);
        CoverBothMines(sim);
        var fetched = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 400; i++)
        {
            sim.State.GetPlayer(0).Gold = Fix.FromInt(10);
            sim.Tick();
            if (bot.LastAction.StartsWith("chest ", StringComparison.Ordinal))
            {
                fetched.Add(bot.LastAction.Substring(0, "chest 0".Length));
            }
        }
        Assert.NotEmpty(fetched);
        Assert.All(fetched, name => Assert.Contains(name, new[] { "chest 0", "chest 1" }));
    }

    [Fact]
    public void Bot_DoesNotSendASecondUnitForAChestItIsAlreadyFetching()
    {
        Simulation sim = ChestSim(Sharp(aggression: "0", mineFocus: "1"), startGold: "10");
        CoverBothMines(sim);
        Step(sim, 1);
        Assert.StartsWith("chest 0 ", ((BotController)sim.GetController(0)).LastAction);
        sim.State.GetPlayer(0).Gold = Fix.FromInt(10);
        Step(sim, 15);
        Assert.DoesNotContain("chest 0 ", ((BotController)sim.GetController(0)).LastAction);
    }

    [Fact]
    public void AggressiveBot_DeploysMoreInTheFirstMinuteThanTheTurtle()
    {
        int Early(string id, ulong seed)
        {
            var sim = new Simulation(ShippedSetup().WithBot(0, LoadBot(id)), seed);
            Step(sim, 60 * 20);
            return CommandsOf(sim.Log, 0, CommandType.DeployCard).Count;
        }
        for (ulong seed = 1; seed <= 3; seed++)
        {
            int aggressive = Early("aggressive", seed), turtle = Early("turtle", seed);
            _output.WriteLine("seed " + seed + ": aggressive " + aggressive + ", turtle " + turtle);
            Assert.True(aggressive > turtle, "seed " + seed + ": aggressive " + aggressive + ", turtle " + turtle);
        }
    }

    // ------------------------------------------------------------ whole matches

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(4UL)]
    public void BalancedBot_DestroysTheKeepOfADoNothingOpponent(ulong seed)
    {
        MatchResult result = HeadlessMatch.Run(ShippedSetup().WithBot(0, LoadBot("balanced")), seed);
        _output.WriteLine("ended on tick " + result.Ticks + ", " + result.Log.CommandCount + " commands");
        Assert.Equal(0, result.Winner);
        Assert.Equal(EndReason.KeepDestroyed, result.EndReason);
        Assert.True(result.Ticks <= 180 * 20);
        Assert.True(result.Simulation.State.Structures[Keep1].IsDestroyed);
        AssertNoRejectedCommands(result.Simulation.State);
    }

    [Fact]
    public void Bot_PlaysAgainstScriptedCommands()
    {
        // Player 1 sends whatever is in hand slot 0 down alternating lanes every 15 s.
        var script = new List<Command>();
        for (int i = 0; i < 10; i++)
        {
            script.Add(Command.DeployCard(100 + i * 300, 1, 0, 0, TestSim.V(i % 2 == 0 ? "3.5" : "14.5", "20.5")));
        }
        MatchResult result = HeadlessMatch.Run(ShippedSetup().WithBot(0, LoadBot("balanced")), 9, script);
        Assert.InRange(result.Winner, 0, 1);
        Assert.NotEqual(EndReason.None, result.EndReason);
        Assert.Equal(script.Count(c => c.Tick < result.Ticks), CommandsOf(result.Log, 1, CommandType.DeployCard).Count);
        Assert.NotEmpty(CommandsOf(result.Log, 0, CommandType.DeployCard));
        Assert.Equal(0, result.Simulation.State.GetPlayer(0).IgnoredDeploys);
        Assert.Equal(result.FinalHash, Simulation.Replay(result.Simulation.Setup, 9, result.Log).ComputeHash());
    }

    public static IEnumerable<object[]> BotPairs()
    {
        ulong seed = 1;
        foreach (string a in ShippedBots)
        {
            foreach (string b in ShippedBots)
            {
                yield return new object[] { a, b, seed++ };
            }
        }
    }

    [Theory]
    [MemberData(nameof(BotPairs))]
    public void BotVsBot_EndsWithAWinnerAndAValidReason(string bot0, string bot1, ulong seed)
    {
        MatchResult result = HeadlessMatch.Run(ShippedSetup().WithBot(0, LoadBot(bot0)).WithBot(1, LoadBot(bot1)), seed);
        MatchState s = result.Simulation.State;
        _output.WriteLine(bot0 + " vs " + bot1 + ": player " + result.Winner + " by " + result.EndReason + " "
            + result.TieBreakRule + " on tick " + result.Ticks + ", score " + s.Players[0].Score + " / " + s.Players[1].Score);
        Assert.Equal(MatchPhase.Ended, s.Phase);
        Assert.InRange(result.Winner, 0, 1);
        Assert.True(Enum.IsDefined(result.EndReason) && result.EndReason != EndReason.None);
        Assert.Equal(result.EndReason == EndReason.TieBreak, result.TieBreakRule != TieBreakRule.None);
        Assert.InRange(result.Ticks, 1, (180 + 60) * 20);
        Assert.Equal(result.FinalHash, result.Simulation.ComputeHash());
        Assert.True(CommandsOf(result.Log, 0, CommandType.DeployCard).Count > 5);
        Assert.True(CommandsOf(result.Log, 1, CommandType.DeployCard).Count > 5);
        AssertNoRejectedCommands(s);
    }

    [Fact]
    public void BotVsBot_SameSeed_HashesIdenticallyEveryTick_AndOtherSeedsDiffer()
    {
        MatchSetup setup = ShippedSetup().WithBot(0, LoadBot("balanced")).WithBot(1, LoadBot("swarm"));
        var a = new Simulation(setup, 77);
        var b = new Simulation(setup, 77);
        var c = new Simulation(setup, 78);
        bool diverged = false;
        while (!a.IsEnded)
        {
            a.Tick();
            b.Tick();
            Assert.True(a.ComputeHash() == b.ComputeHash(), "hashes diverged at tick " + a.State.Tick);
            if (!c.IsEnded)
            {
                c.Tick();
                diverged |= c.ComputeHash() != a.ComputeHash();
            }
        }
        Assert.True(b.IsEnded);
        Assert.True(diverged);
        Assert.Equal(a.Log.CommandCount, b.Log.CommandCount);
        Assert.True(a.Log.CommandCount > 20);
    }

    [Fact]
    public void BotVsBot_ReplayedWithHumanControllers_ReproducesTheMatch()
    {
        MatchSetup setup = ShippedSetup().WithBot(0, LoadBot("aggressive")).WithBot(1, LoadBot("turtle"));
        MatchResult result = HeadlessMatch.Run(setup, 31);

        // Simulation.Replay feeds the log through HumanControllers.
        Simulation replay = Simulation.Replay(setup, 31, result.Log);
        Assert.True(replay.IsEnded);
        Assert.Equal(result.FinalHash, replay.ComputeHash());
        Assert.IsType<HumanController>(replay.GetController(0));
        Assert.IsType<HumanController>(replay.GetController(1));

        // The same by hand, checking every tick against a fresh bot run.
        var humans = new Simulation(setup.WithoutBots(), 31);
        humans.GetHuman(0)!.SubmitAll(result.Log);
        humans.GetHuman(1)!.SubmitAll(result.Log);
        var bots = new Simulation(setup, 31);
        while (!bots.IsEnded)
        {
            bots.Tick();
            humans.Tick();
            Assert.Equal(bots.ComputeHash(), humans.ComputeHash());
        }
        Assert.True(humans.IsEnded);
        Assert.Equal(result.FinalHash, humans.ComputeHash());
        Assert.Equal(0, humans.GetHuman(0)!.PendingCount + humans.GetHuman(1)!.PendingCount);
    }

    [Fact]
    public void BotVsBot_FullMatch_RunsHeadlessInUnderTwoSeconds()
    {
        MatchSetup setup = ShippedSetup().WithBot(0, LoadBot("balanced")).WithBot(1, LoadBot("balanced"));
        HeadlessMatch.Run(setup, 2); // warm up (JIT)
        var watch = Stopwatch.StartNew();
        MatchResult result = HeadlessMatch.Run(setup, 1);
        watch.Stop();
        _output.WriteLine("bot vs bot, " + result.Ticks + " ticks, " + (result.Simulation.State.NextUnitId - 1)
            + " units: " + watch.ElapsedMilliseconds + " ms");
        Assert.True(result.Ticks >= 180 * 20, "the timed match should run the full clock, ended on " + result.Ticks);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "took " + watch.ElapsedMilliseconds + " ms");
    }
}
