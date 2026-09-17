using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;

namespace NovaFaction.Sim.Tests;

public class DeckTests
{
    private static CardCatalog Fantasy => TestSim.LoadFantasyCards();

    /// <summary>The fantasy units plus a second leader and a spare card, for deck-building edge cases.</summary>
    private static CardCatalog ExtendedRoster()
    {
        string json = TestSim.FantasyUnitsJson();
        int end = json.LastIndexOf(']');
        string extra = ",\n{\n" + UnitRosterTests.DefaultUnitBody + "\n},\n{\n"
            + UnitRosterTests.DefaultUnitBody.Replace("\"grunt\"", "\"queen\"").Replace("\"bruiser\"", "\"leader\"")
                .Replace("\"isLeader\": false", "\"isLeader\": true")
            + "\n}\n";
        return TestSim.Cards(UnitRoster.FromJson(json.Substring(0, end).TrimEnd() + extra + json.Substring(end)));
    }

    [Fact]
    public void ValidDeck_IsSortedAndHasItsLeader()
    {
        Deck deck = Deck.Create(Fantasy, TestSim.DefaultDeckIds, 8);
        Assert.Equal(TestSim.DefaultDeckIds.OrderBy(id => id, StringComparer.Ordinal), deck.Cards.Select(c => c.Id));
        Assert.Equal("warlord", deck.Leader.Id);
        Assert.Equal("fantasy", deck.Roster.Faction);
        Assert.Null(Deck.Validate(Fantasy, TestSim.DefaultDeckIds, 8));
    }

    [Fact]
    public void ListingOrder_DoesNotAffectTheMatch()
    {
        MatchRules rules = MatchRulesTests.LoadShippedRules();
        CardCatalog roster = Fantasy;
        Deck a = Deck.Create(roster, TestSim.DefaultDeckIds, 8);
        Deck b = Deck.Create(roster, TestSim.DefaultDeckIds.Reverse().ToArray(), 8);
        var simA = new Simulation(new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), a, a), 5);
        var simB = new Simulation(new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), b, b), 5);
        Assert.Equal(simA.ComputeHash(), simB.ComputeHash());
    }

    [Fact]
    public void SwappedCard_IsAccepted()
    {
        CardCatalog roster = ExtendedRoster();
        string[] ids = TestSim.DefaultDeckIds.Select(id => id == "knight" ? "grunt" : id).ToArray();
        Assert.Contains("grunt", Deck.Create(roster, ids, 8).Cards.Select(c => c.Id));
    }

    public static IEnumerable<object?[]> InvalidDecks()
    {
        string[] d = TestSim.DefaultDeckIds;
        yield return new object?[] { d.Take(7).ToArray(), "has 8 cards but this one has 7" };
        yield return new object?[] { d.Concat(new[] { "knight" }).ToArray(), "has 8 cards but this one has 9" };
        yield return new object?[] { d.Select(id => id == "knight" ? "dragon" : id).ToArray(), "no card \"dragon\"" };
        yield return new object?[] { d.Select(id => id == "knight" ? "griffin" : id).ToArray(), "\"griffin\" appears twice" };
        yield return new object?[] { d.Select(id => id == "warlord" ? "grunt" : id).ToArray(), "exactly one leader but this one has 0" };
        yield return new object?[] { d.Select(id => id == "knight" ? "queen" : id).ToArray(), "exactly one leader but this one has 2" };
        yield return new object?[] { d.Select(id => id == "knight" ? null : id).ToArray(), "no card" };
        yield return new object?[] { null, "no card list" };
    }

    [Theory]
    [MemberData(nameof(InvalidDecks))]
    public void InvalidDeck_IsRejectedWithReason(string?[]? ids, string fragment)
    {
        CardCatalog roster = ExtendedRoster();
        string? problem = Deck.Validate(roster, ids, 8);
        Assert.NotNull(problem);
        Assert.Contains(fragment, problem);
        if (ids != null)
        {
            var ex = Assert.Throws<ArgumentException>(() => Deck.Create(roster, ids!, 8));
            Assert.Contains(fragment, ex.Message);
        }
    }

    [Fact]
    public void Setup_RejectsDeckOfWrongSizeForRules()
    {
        MatchRules rules = MatchRulesTests.LoadShippedRules();
        CardCatalog roster = ExtendedRoster();
        Deck nine = Deck.Create(roster, TestSim.DefaultDeckIds.Concat(new[] { "grunt" }).ToArray(), 9);
        Deck eight = Deck.Create(roster, TestSim.DefaultDeckIds, 8);
        Assert.Throws<ArgumentException>(() => new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), eight, nine));
        Assert.Throws<ArgumentNullException>(() => new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), eight, null!));
    }

    [Fact]
    public void Setup_RejectsUnitsTooFastForTheTickRate()
    {
        MatchRules rules = MatchRulesTests.LoadShippedRules();
        // Half a cell (0.5) per tick at 20 ticks/s = 10 units/s, including the 1.5 separation push times (2 + 0.3): moving push, weak stopped push and sideways slide.
        // goblin_pack and griffin both move at 1.5; goblin_pack comes first in the deck's id order.
        UnitRoster fast = UnitRoster.FromJson(TestSim.FantasyUnitsJson().Replace("\"moveSpeed\": 1.5,", "\"moveSpeed\": 6.56,"));
        Deck deck = Deck.Create(TestSim.Cards(fast), TestSim.DefaultDeckIds, 8);
        var ex = Assert.Throws<ArgumentException>(() => new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), deck, deck));
        Assert.Contains("goblin_pack", ex.Message);

        UnitRoster ok = UnitRoster.FromJson(TestSim.FantasyUnitsJson().Replace("\"moveSpeed\": 1.5,", "\"moveSpeed\": 6.54,"));
        Deck okDeck = Deck.Create(TestSim.Cards(ok), TestSim.DefaultDeckIds, 8);
        _ = new MatchSetup(rules, MapTestData.LoadTwoLane(), TestSim.LoadStructures(), okDeck, okDeck);
    }
}

public class CardCycleTests
{
    private static MatchRules Rules => MatchRulesTests.LoadShippedRules();

    private static string[] CycleOrder(PlayerState p) =>
        p.Cards.Hand.Select(c => c!.Id).Concat(p.Cards.Queue.Select(c => c.Id)).ToArray();

    [Fact]
    public void MatchStart_DealsFourAndShowsNext()
    {
        Simulation sim = TestSim.New(Rules, MapTestData.LoadTwoLane(), 42);
        foreach (PlayerState p in sim.State.Players)
        {
            Assert.Equal(4, p.Cards.HandSize);
            Assert.All(p.Cards.Hand, c => Assert.NotNull(c));
            Assert.Equal(4, p.Cards.Queue.Count);
            Assert.Same(p.Cards.Queue[0], p.Cards.NextCard);
            // Hand + queue is a permutation of the deck.
            Assert.Equal(TestSim.DefaultDeckIds.OrderBy(x => x, StringComparer.Ordinal),
                CycleOrder(p).OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void CycleOrder_IsReproducibleFromSeed()
    {
        for (ulong seed = 0; seed < 20; seed++)
        {
            Simulation a = TestSim.New(Rules, MapTestData.LoadTwoLane(), seed);
            Simulation b = TestSim.New(Rules, MapTestData.LoadTwoLane(), seed);
            for (int p = 0; p < 2; p++)
            {
                Assert.Equal(CycleOrder(a.State.GetPlayer(p)), CycleOrder(b.State.GetPlayer(p)));
            }
        }
    }

    [Fact]
    public void CycleOrder_DiffersAcrossSeedsAndPlayers()
    {
        var orders = new HashSet<string>();
        int samePlayers = 0;
        for (ulong seed = 0; seed < 50; seed++)
        {
            Simulation sim = TestSim.New(Rules, MapTestData.LoadTwoLane(), seed);
            string p0 = string.Join(",", CycleOrder(sim.State.GetPlayer(0)));
            string p1 = string.Join(",", CycleOrder(sim.State.GetPlayer(1)));
            orders.Add(p0);
            if (p0 == p1)
            {
                samePlayers++;
            }
        }
        // 8! = 40320 orders; 50 seeds should almost all differ.
        Assert.True(orders.Count >= 45, "only " + orders.Count + " distinct orders from 50 seeds");
        Assert.True(samePlayers <= 1, "players share an order too often");
    }

    [Fact]
    public void CycleOrder_IsPinnedForSeedOne()
    {
        // Pins the shuffle so an RNG or dealing change is noticed (it would break saved replays).
        Simulation sim = TestSim.New(Rules, MapTestData.LoadTwoLane(), 1);
        Assert.Equal(PinnedPlayer0, string.Join(",", CycleOrder(sim.State.GetPlayer(0))));
    }

    private const string PinnedPlayer0 = "goblin_pack,stone_golem,knight,fireball,griffin,warlord,catapult,elf_archer";

    [Fact]
    public void PlayingASlot_MovesNextIntoSlotAndPlayedToBack()
    {
        Simulation sim = TestSim.New(Rules, MapTestData.LoadTwoLane(), 3);
        CardCycle cards = sim.State.GetPlayer(0).Cards;
        string[] before = CycleOrder(sim.State.GetPlayer(0));
        // before = [h0 h1 h2 h3 | q0 q1 q2 q3]

        CardDefinition played = cards.Play(2);

        Assert.Equal(before[2], played.Id);
        string[] after = CycleOrder(sim.State.GetPlayer(0));
        Assert.Equal(new[] { before[0], before[1], before[4], before[3], before[5], before[6], before[7], before[2] }, after);
        Assert.Equal(before[5], cards.NextCard.Id);
    }

    [Fact]
    public void PlayedCard_ReturnsAfterTheRestOfTheQueue()
    {
        Simulation sim = TestSim.New(Rules, MapTestData.LoadTwoLane(), 9);
        CardCycle cards = sim.State.GetPlayer(1).Cards;
        string first = cards.Play(0).Id;
        // Three cards are ahead of it in the queue; it is the next card after three more plays.
        for (int i = 0; i < 2; i++)
        {
            cards.Play(1);
            Assert.NotEqual(first, cards.NextCard.Id);
        }
        cards.Play(1);
        Assert.Equal(first, cards.NextCard.Id);
        cards.Play(3);
        Assert.Equal(first, cards.Hand[3]!.Id);
    }

    [Fact]
    public void ValidDeploy_CyclesTheHandThroughTheCommand()
    {
        Simulation sim = TestSim.New(TestSim.Rules(), MapTestData.LoadTwoLane(), 4);
        PlayerState p0 = sim.State.GetPlayer(0);
        string[] before = CycleOrder(p0);
        sim.Tick(new[] { Command.DeployCard(0, 0, 0, 1, TestSim.V("4.5", "10.5")) });
        Assert.Equal(new[] { before[0], before[4], before[2], before[3], before[5], before[6], before[7], before[1] }, CycleOrder(p0));
    }

    [Fact]
    public void Slots_OutOfRangeThrow()
    {
        Simulation sim = TestSim.New(Rules, MapTestData.LoadTwoLane(), 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.State.GetPlayer(0).Cards.GetSlot(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.State.GetPlayer(0).Cards.GetSlot(-1));
    }
}
