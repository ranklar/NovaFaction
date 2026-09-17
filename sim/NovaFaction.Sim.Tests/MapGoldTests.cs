using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Economy;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;
using static NovaFaction.Sim.Tests.CombatTests;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Gold mines and chests (docs/design.md "Map gold"). Stationary dummy units are placed directly on twolane next
/// to the map's mines and chest spawns, with structures that do nothing, so presence is fully controlled.
/// </summary>
public class MapGoldTests
{
    // twolane markers (cell = world units; centers at +0.5).
    // Mine 0 at cell (5, 15), player 0's side of the river; mine 1 at (12, 16), player 1's side.
    // Chests (bottom row first): 0 (3, 11), 1 (14, 11), 2 (3, 20), 3 (14, 20).
    private const int MineWest = 0, MineEast = 1;

    private static void Step(Simulation sim) => sim.Tick(Array.Empty<Command>());

    /// <summary>twolane, quiet structures, no base income (so gold changes come from the map only).</summary>
    private static Simulation GoldSim(MatchRules? rules = null, ulong seed = 1) =>
        TestSim.New(rules ?? GoldRules(), MapTestData.LoadTwoLane(), seed, Quiet());

    private static MatchRules GoldRules(string income = "0", string start = "0", string mineSeconds = "5",
        string mineIncome = "0.05", string mineCap = "0.1", string chestFirst = "30", string chestInterval = "30",
        string chestGold = "0.75", string match = "180", string suddenDeath = "60") =>
        TestSim.Rules(income: income, start: start, mineSeconds: mineSeconds, mineIncome: mineIncome, mineCap: mineCap,
            chestFirst: chestFirst, chestInterval: chestInterval, chestGold: chestGold, matchSeconds: match,
            suddenDeathSeconds: suddenDeath);

    /// <summary>A stationary dummy of the player's, one cell left of the west mine (distance 1).</summary>
    private static Unit AtWestMine(Simulation sim, int owner, string dx = "-1", string dy = "0")
    {
        FixVector2 p = sim.State.Mines[MineWest].Position + TestSim.V(dx, dy);
        return sim.State.AddUnit(owner, Card(sim, "dummy"), p);
    }

    private static void Kill(Simulation sim, Unit unit) => unit.Hp = Fix.Zero; // removed on the next combat step

    // ------------------------------------------------------------ setup

    [Fact]
    public void Twolane_HasTwoNeutralMinesAndFourEmptyChestSpawns()
    {
        Simulation sim = GoldSim();
        Assert.Equal(new[] { new CellCoord(5, 15), new CellCoord(12, 16) }, sim.State.Mines.Select(m => m.Cell));
        Assert.Equal(new[] { new CellCoord(3, 11), new CellCoord(14, 11), new CellCoord(3, 20), new CellCoord(14, 20) },
            sim.State.Chests.Select(c => c.Cell));
        Assert.All(sim.State.Mines, m =>
        {
            Assert.Equal(MineState.Nobody, m.Owner);
            Assert.Equal(MineState.Nobody, m.CapturingPlayer);
            Assert.Equal(0, m.CaptureProgressTicks);
            Assert.Equal(100, m.CaptureTicksRequired);
            Assert.Equal(sim.State.Map.Grid.CellToWorld(m.Cell), m.Position);
            // A 1x1 blocked footprint that is not a structure: nothing can target or score on it.
            Assert.False(sim.State.Map.Grid.IsWalkable(m.Cell));
            Assert.Equal(-1, sim.State.Map.Grid.GetStructureAt(m.Cell.X, m.Cell.Y));
            Assert.False(sim.State.Map.IsDeployable(m.Cell.Y < 16 ? 0 : 1, m.Position));
        });
        Assert.All(sim.State.Chests, c => Assert.False(c.IsPresent));
        Assert.All(sim.State.Players, p => Assert.Equal(Fix.Zero, p.GoldFromMap));
        Assert.Equal(6, sim.State.Structures.Count); // mines are not structures
    }

    [Fact]
    public void Mine_IsNeverATarget_AndUnitsWalkAroundIt()
    {
        // A knight right below the west mine walks up the lane to the enemy: it never targets the mine (only enemy
        // structures by index), never stands on its cell, and scores nothing on the way.
        Simulation sim = GoldSim();
        MineState mine = sim.State.Mines[MineWest];
        Unit knight = sim.State.AddUnit(0, Card(sim, "knight"), mine.Position + TestSim.V("0", "-1"));
        int startY = sim.State.Map.Grid.WorldToCell(knight.Position).Y;
        for (int t = 0; t < 100; t++)
        {
            Step(sim);
            Assert.NotEqual(mine.Cell, sim.State.Map.Grid.WorldToCell(knight.Position));
            Assert.True(knight.Target.IsNone || knight.Target.Kind == TargetKind.Structure);
            Assert.Contains(knight.Objective, new[] { Keep1, Tower1West, Tower1East });
        }
        Assert.True(sim.State.Map.Grid.WorldToCell(knight.Position).Y > startY + 2, "the knight should have walked past");
        Assert.All(sim.State.Players, p => Assert.Equal(Fix.Zero, p.Score));
    }

    [Fact]
    public void MatchSetup_RejectsACaptureRadiusShorterThanACell()
    {
        MatchRules rules = TestSim.Rules(mineRadius: "0.9");
        var ex = Assert.Throws<ArgumentException>(() => TestSim.Setup(rules, MapTestData.LoadTwoLane()));
        Assert.Contains("mineCaptureRadius", ex.Message);
    }

    // ------------------------------------------------------------ capture

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NeutralMine_IsCapturedByOneUnit_AfterTheConfiguredTime(int player)
    {
        Simulation sim = GoldSim();
        MineState mine = sim.State.Mines[MineWest];
        AtWestMine(sim, player);
        TestSim.Run(sim, 99);
        Assert.Equal(MineState.Nobody, mine.Owner);
        Assert.Equal(player, mine.CapturingPlayer);
        Assert.Equal(99, mine.CaptureProgressTicks);
        Assert.Equal(Fix.FromInt(99) / Fix.FromInt(100), mine.CaptureProgress);
        Step(sim); // the 100th tick (5 s) completes the capture
        Assert.Equal(player, mine.Owner);
        Assert.Equal(MineState.Nobody, mine.CapturingPlayer);
        Assert.Equal(0, mine.CaptureProgressTicks);
        Assert.Equal(MineState.Nobody, sim.State.Mines[MineEast].Owner);
    }

    [Fact]
    public void Presence_UsesTheCaptureRadius_AndCountsFlyers()
    {
        Simulation sim = GoldSim();
        MineState mine = sim.State.Mines[MineWest];
        // 1.5 is exactly on the radius (counts); a unit just outside does nothing.
        Unit onEdge = AtWestMine(sim, 0, "-1.5", "0");
        Step(sim);
        Assert.Equal(1, mine.CaptureProgressTicks);
        Kill(sim, onEdge);
        sim.State.AddUnit(1, Card(sim, "dummy"), mine.Position + new FixVector2(-Fix.Parse("1.5") - Fix.Epsilon, Fix.Zero));
        Step(sim); // the dead unit is removed in combat before presence is checked, so nobody counts: unwinds
        Assert.Equal(0, mine.CaptureProgressTicks);

        Simulation air = GoldSim();
        air.State.AddUnit(1, Card(air, "balloon"), air.State.Mines[MineWest].Position); // right over the mine
        TestSim.Run(air, 100);
        Assert.Equal(1, air.State.Mines[MineWest].Owner);
    }

    [Fact]
    public void ContestedMine_MakesNoProgress()
    {
        Simulation sim = GoldSim();
        MineState mine = sim.State.Mines[MineWest];
        AtWestMine(sim, 0);
        TestSim.Run(sim, 30);
        Assert.Equal(30, mine.CaptureProgressTicks);

        Unit enemy = AtWestMine(sim, 1, "0", "1");
        TestSim.Run(sim, 200);
        Assert.Equal(30, mine.CaptureProgressTicks); // paused, not reset
        Assert.Equal(0, mine.CapturingPlayer);
        Assert.Equal(MineState.Nobody, mine.Owner);

        Kill(sim, enemy);
        TestSim.Run(sim, 70);
        Assert.Equal(0, mine.Owner);

        // From a fresh start, both present from the first tick: nothing ever happens.
        Simulation both = GoldSim();
        AtWestMine(both, 0);
        AtWestMine(both, 1, "0", "1");
        TestSim.Run(both, 300);
        Assert.Equal(MineState.Nobody, both.State.Mines[MineWest].Owner);
        Assert.Equal(0, both.State.Mines[MineWest].CaptureProgressTicks);
        Assert.Equal(MineState.Nobody, both.State.Mines[MineWest].CapturingPlayer);
    }

    [Fact]
    public void AbandonedMine_ProgressDecaysTowardZero()
    {
        Simulation sim = GoldSim();
        MineState mine = sim.State.Mines[MineWest];
        Unit unit = AtWestMine(sim, 1);
        TestSim.Run(sim, 60);
        Assert.Equal(60, mine.CaptureProgressTicks);

        Kill(sim, unit);
        Step(sim);
        Assert.Equal(59, mine.CaptureProgressTicks); // one tick back per tick
        TestSim.Run(sim, 58);
        Assert.Equal(1, mine.CaptureProgressTicks);
        Assert.Equal(1, mine.CapturingPlayer);
        Step(sim);
        Assert.Equal(0, mine.CaptureProgressTicks);
        Assert.Equal(MineState.Nobody, mine.CapturingPlayer);
        TestSim.Run(sim, 20);
        Assert.Equal(0, mine.CaptureProgressTicks); // never below zero
        Assert.Equal(MineState.Nobody, mine.Owner);
    }

    [Fact]
    public void OtherPlayersProgress_IsUnwoundBeforeANewCaptureStarts()
    {
        Simulation sim = GoldSim();
        MineState mine = sim.State.Mines[MineWest];
        Unit first = AtWestMine(sim, 0);
        TestSim.Run(sim, 40);
        Kill(sim, first);
        AtWestMine(sim, 1);
        TestSim.Run(sim, 40); // the dead unit is gone before presence is checked: player 1 is alone from here
        Assert.Equal(0, mine.CaptureProgressTicks);
        Assert.Equal(MineState.Nobody, mine.CapturingPlayer);
        Step(sim);
        Assert.Equal(1, mine.CapturingPlayer);
        Assert.Equal(1, mine.CaptureProgressTicks);
        TestSim.Run(sim, 99);
        Assert.Equal(1, mine.Owner);
    }

    [Fact]
    public void OwnedMine_IsRecapturedTheSameWay_AndTheOwnerDefendsIt()
    {
        Simulation sim = GoldSim();
        MineState mine = sim.State.Mines[MineWest];
        Unit owner = AtWestMine(sim, 0);
        TestSim.Run(sim, 100);
        Assert.Equal(0, mine.Owner);

        // The owner alone: nothing to capture, progress stays at zero.
        TestSim.Run(sim, 50);
        Assert.Equal(0, mine.CaptureProgressTicks);

        // The enemy arrives: contested, still the owner's.
        Unit raider = AtWestMine(sim, 1, "0", "1");
        TestSim.Run(sim, 50);
        Assert.Equal(0, mine.Owner);
        Assert.Equal(0, mine.CaptureProgressTicks);

        // The owner's unit dies: the enemy captures it in the full capture time.
        Kill(sim, owner);
        TestSim.Run(sim, 99);
        Assert.Equal(0, mine.Owner);
        Assert.Equal(1, mine.CapturingPlayer);
        Assert.Equal(99, mine.CaptureProgressTicks);
        Step(sim);
        Assert.Equal(1, mine.Owner);
        Assert.Equal(0, mine.CaptureProgressTicks);

        // Player 0 comes back after player 1 has left, gets halfway, then the owner (player 1) returns alone:
        // the owner's presence winds the attempt back.
        Kill(sim, raider);
        Unit retaker = AtWestMine(sim, 0);
        TestSim.Run(sim, 51); // dead units are removed in combat, before presence is checked
        Assert.Equal(51, mine.CaptureProgressTicks);
        Kill(sim, retaker);
        AtWestMine(sim, 1, "0", "1");
        TestSim.Run(sim, 10);
        Assert.Equal(41, mine.CaptureProgressTicks);
        Assert.Equal(1, mine.Owner);
    }

    // ------------------------------------------------------------ mine income

    [Fact]
    public void MineIncome_AccruesToTheOwner_Exactly()
    {
        MatchRules rules = GoldRules(mineSeconds: "1");
        Simulation sim = GoldSim(rules);
        AtWestMine(sim, 0);
        TestSim.Run(sim, 20); // captured on the 20th tick; that tick's accrual already includes the mine
        Assert.Equal(0, sim.State.Mines[MineWest].Owner);
        Fix afterCapture = sim.State.GetPlayer(0).Gold;
        Assert.True(afterCapture > Fix.Zero && afterCapture < rules.MineIncomePerSecond);

        TestSim.Run(sim, 20 * 10);
        PlayerState p0 = sim.State.GetPlayer(0);
        // Exactly ten seconds' worth more (the carry keeps whole seconds exact).
        Assert.Equal(afterCapture + rules.MineIncomePerSecond * Fix.FromInt(10), p0.Gold);
        Assert.Equal(p0.Gold, p0.GoldFromMap); // no base income here, so all of it came from the mine
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1).Gold);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1).GoldFromMap);
    }

    [Fact]
    public void MineIncome_AddsToBaseIncome_ButBaseIncomeIsNotGoldFromMap()
    {
        MatchRules rules = GoldRules(income: "0.35", mineSeconds: "1");
        Simulation sim = GoldSim(rules);
        AtWestMine(sim, 1);
        TestSim.Run(sim, 20);
        Fix map0 = sim.State.GetPlayer(1).GoldFromMap;
        Fix gold0 = sim.State.GetPlayer(1).Gold;
        TestSim.Run(sim, 100);
        PlayerState p1 = sim.State.GetPlayer(1);
        Assert.Equal(gold0 + (rules.GoldBaseIncomePerSecond + rules.MineIncomePerSecond) * Fix.FromInt(5), p1.Gold);
        Assert.Equal(map0 + rules.MineIncomePerSecond * Fix.FromInt(5), p1.GoldFromMap);
        Assert.Equal(rules.GoldBaseIncomePerSecond * Fix.FromInt(6), sim.State.GetPlayer(0).Gold);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(0).GoldFromMap);
    }

    [Fact]
    public void MineIncome_IsCappedByMineIncomeCap()
    {
        // Two mines at 0.05 each but a cap of 0.06: the owner of both earns 0.06 per second, not 0.1.
        MatchRules rules = GoldRules(mineSeconds: "1", mineCap: "0.06");
        Simulation sim = GoldSim(rules);
        AtWestMine(sim, 0);
        sim.State.AddUnit(0, Card(sim, "dummy"), sim.State.Mines[MineEast].Position + TestSim.V("1", "0"));
        TestSim.Run(sim, 20);
        Assert.All(sim.State.Mines, m => Assert.Equal(0, m.Owner));
        Fix start = sim.State.GetPlayer(0).Gold;
        TestSim.Run(sim, 20 * 20);
        Assert.Equal(start + Fix.Parse("0.06") * Fix.FromInt(20), sim.State.GetPlayer(0).Gold);
        Assert.Equal(Fix.Parse("0.06"), MapGoldSystem.MineIncome(sim.State, rules, 0));
        Assert.Equal(Fix.Zero, MapGoldSystem.MineIncome(sim.State, rules, 1));
    }

    [Fact]
    public void MineIncome_RespectsTheGoldCap_AndOnlyReceivedGoldCounts()
    {
        MatchRules rules = GoldRules(start: "9.9", income: "0.35", mineSeconds: "1");
        Simulation sim = GoldSim(rules);
        AtWestMine(sim, 0);
        TestSim.Run(sim, 20 * 5);
        PlayerState p0 = sim.State.GetPlayer(0);
        Assert.Equal(rules.GoldCap, p0.Gold);
        Assert.Equal(0L, p0.IncomeRemainder);
        Assert.Equal(0L, p0.MineIncomeRemainder);
        // The bank filled from base income within the first second, before the mine was even taken: none of the
        // mine's income arrived.
        Assert.Equal(Fix.Zero, p0.GoldFromMap);

        // Spend down to 50 raw units under what one tick of base income would fill: base income is added first,
        // so of this tick's mine income only those 50 raw units arrive (the carries are empty after the cap).
        long baseTick = rules.GoldBaseIncomePerSecond.Raw / rules.TicksPerSecond;
        long mineTick = rules.MineIncomePerSecond.Raw / rules.TicksPerSecond;
        Assert.True(mineTick > 50);
        p0.Gold = Fix.FromRaw(rules.GoldCap.Raw - baseTick - 50);
        Step(sim);
        Assert.Equal(rules.GoldCap, p0.Gold);
        Assert.Equal(Fix.FromRaw(50), p0.GoldFromMap);

        // With room for both, the whole tick of mine income counts.
        p0.Gold = Fix.FromInt(5);
        Step(sim);
        Assert.Equal(Fix.FromRaw(50 + mineTick), p0.GoldFromMap);
        Assert.Equal(Fix.FromRaw(Fix.FromInt(5).Raw + baseTick + mineTick), p0.Gold);
    }

    [Fact]
    public void SuddenDeath_DoublesMineIncomeToo()
    {
        MatchRules rules = GoldRules(mineSeconds: "1", match: "2", suddenDeath: "60");
        Simulation sim = GoldSim(rules);
        AtWestMine(sim, 0);
        TestSim.Run(sim, 40);
        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase); // scores are equal at 0
        Fix before = sim.State.GetPlayer(0).Gold;
        TestSim.Run(sim, 40);
        Assert.Equal(before + rules.MineIncomePerSecond * Fix.FromInt(2) * Fix.FromInt(2), sim.State.GetPlayer(0).Gold);
        Assert.Equal(sim.State.GetPlayer(0).Gold, sim.State.GetPlayer(0).GoldFromMap);
    }

    // ------------------------------------------------------------ chests

    [Fact]
    public void Chests_SpawnOnSchedule_AtEverySpawnPoint()
    {
        MatchRules rules = GoldRules(chestFirst: "3", chestInterval: "2");
        Simulation sim = GoldSim(rules);
        TestSim.Run(sim, 59);
        Assert.All(sim.State.Chests, c => Assert.False(c.IsPresent));
        Step(sim); // State.Tick reaches 60 = 3 s
        Assert.Equal(60, sim.State.Tick);
        Assert.All(sim.State.Chests, c => Assert.True(c.IsPresent));
        Assert.All(sim.State.Chests, c => Assert.Equal(sim.State.Map.Grid.CellToWorld(c.Cell), c.Position));
    }

    [Fact]
    public void Chests_AtZeroSeconds_ArePresentFromTheStart()
    {
        Simulation sim = GoldSim(GoldRules(chestFirst: "0", chestInterval: "2"));
        Assert.All(sim.State.Chests, c => Assert.True(c.IsPresent));
    }

    [Fact]
    public void Chests_NeverStack_AndRefillOnlyEmptyPoints_AtEachWave()
    {
        MatchRules rules = GoldRules(chestFirst: "1", chestInterval: "2", chestGold: "1");
        Simulation sim = GoldSim(rules);
        TestSim.Run(sim, 20);
        Assert.All(sim.State.Chests, c => Assert.True(c.IsPresent));

        // Player 0 takes chest 0 once; the other three are left alone through several waves.
        Unit collector = sim.State.AddUnit(0, Card(sim, "dummy"), sim.State.Chests[0].Position);
        Step(sim);
        Assert.False(sim.State.Chests[0].IsPresent);
        Assert.Equal(Fix.One, sim.State.GetPlayer(0).Gold);
        Kill(sim, collector);
        Step(sim);

        TestSim.Run(sim, 300 - sim.State.Tick); // through the waves at 3, 5, ... 15 s (tick 60 ... 300)
        Assert.Equal(300, sim.State.Tick);
        Assert.All(sim.State.Chests, c => Assert.True(c.IsPresent));
        // A unit standing on the spot now collects exactly one chest, not one per wave that passed.
        sim.State.AddUnit(1, Card(sim, "dummy"), sim.State.Chests[3].Position);
        Step(sim);
        Assert.False(sim.State.Chests[3].IsPresent);
        Assert.Equal(Fix.One, sim.State.GetPlayer(1).Gold);
        TestSim.Run(sim, 38); // tick 339: the next wave is due when the tick count reaches 340
        Assert.False(sim.State.Chests[3].IsPresent);
        Step(sim);
        Assert.True(sim.State.Chests[3].IsPresent); // appears at 17 s...
        Assert.Equal(Fix.One, sim.State.GetPlayer(1).Gold);
        Step(sim);
        Assert.False(sim.State.Chests[3].IsPresent); // ...and the unit standing there takes it on the next tick
        Assert.Equal(Fix.FromInt(2), sim.State.GetPlayer(1).Gold);
        Assert.Equal(Fix.FromInt(2), sim.State.GetPlayer(1).GoldFromMap);
        Assert.Equal(Fix.One, sim.State.GetPlayer(0).GoldFromMap);
    }

    [Fact]
    public void Chest_IsCollectedOnTheFirstTickAUnitArrives_ByAnyUnitIncludingFlyers()
    {
        MatchRules rules = GoldRules(chestFirst: "0", chestInterval: "100");
        Simulation sim = GoldSim(rules);
        ChestState chest = sim.State.Chests[1]; // (14, 11), player 0's east lane
        // A knight of player 1 walking down the east lane: it collects the chest on the first tick it is within
        // the radius, measured after it has moved that tick.
        Unit walker = sim.State.AddUnit(1, Card(sim, "knight"), chest.Position + TestSim.V("0", "4"));
        int ticks = 0;
        while (chest.IsPresent)
        {
            Fix before = FixVector2.Distance(walker.Position, chest.Position);
            Assert.True(before > rules.ChestCollectRadius, "should have been collected already");
            Step(sim);
            ticks++;
            Assert.True(ticks < 200);
        }
        Assert.True(FixVector2.Distance(walker.Position, chest.Position) <= rules.ChestCollectRadius);
        Assert.Equal(rules.ChestGold, sim.State.GetPlayer(1).Gold);
        Assert.Equal(rules.ChestGold, sim.State.GetPlayer(1).GoldFromMap);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(0).Gold);

        // A flyer over chest 2 takes it too.
        sim.State.AddUnit(0, Card(sim, "balloon"), sim.State.Chests[2].Position + TestSim.V("0.5", "0.5"));
        Step(sim);
        Assert.False(sim.State.Chests[2].IsPresent);
        Assert.Equal(rules.ChestGold, sim.State.GetPlayer(0).GoldFromMap);
        // A unit just outside the radius does not.
        sim.State.AddUnit(0, Card(sim, "dummy"),
            sim.State.Chests[0].Position + new FixVector2(rules.ChestCollectRadius + Fix.Epsilon, Fix.Zero));
        TestSim.Run(sim, 5);
        Assert.True(sim.State.Chests[0].IsPresent);
    }

    [Theory]
    [InlineData("0.5", "0.25", 1)] // player 1's unit is closer
    [InlineData("0.25", "0.5", 0)] // player 0's unit is closer
    public void SameTickArrival_TheCloserUnitTakesTheChest(string distance0, string distance1, int winner)
    {
        Simulation sim = GoldSim(GoldRules(chestFirst: "0", chestInterval: "100"));
        ChestState chest = sim.State.Chests[0];
        sim.State.AddUnit(0, Card(sim, "dummy"), chest.Position + new FixVector2(-Fix.Parse(distance0), Fix.Zero));
        sim.State.AddUnit(1, Card(sim, "dummy"), chest.Position + new FixVector2(Fix.Parse(distance1), Fix.Zero));
        Step(sim);
        Assert.False(chest.IsPresent);
        Assert.Equal(Fix.Parse("0.75"), sim.State.GetPlayer(winner).GoldFromMap);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1 - winner).GoldFromMap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SameTickArrival_AtEqualDistance_TheLowerUnitIdTakesTheChest(int firstPlaced)
    {
        Simulation sim = GoldSim(GoldRules(chestFirst: "0", chestInterval: "100"));
        ChestState chest = sim.State.Chests[0];
        // Mirror-image positions: exactly the same distance. The unit created first has the lower id.
        sim.State.AddUnit(firstPlaced, Card(sim, "dummy"), chest.Position + TestSim.V("-0.3", "0.4"));
        sim.State.AddUnit(1 - firstPlaced, Card(sim, "dummy"), chest.Position + TestSim.V("0.3", "-0.4"));
        Step(sim);
        Assert.False(chest.IsPresent);
        Assert.Equal(Fix.Parse("0.75"), sim.State.GetPlayer(firstPlaced).Gold);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1 - firstPlaced).Gold);
    }

    [Fact]
    public void ChestGold_RespectsTheGoldCap_OnlyReceivedGoldCounts_AndTheChestIsUsedUp()
    {
        Simulation sim = GoldSim(GoldRules(start: "9.5", chestFirst: "0", chestInterval: "100"));
        sim.State.AddUnit(0, Card(sim, "dummy"), sim.State.Chests[0].Position);
        Step(sim);
        PlayerState p0 = sim.State.GetPlayer(0);
        Assert.Equal(Fix.FromInt(10), p0.Gold);
        Assert.Equal(Fix.Half, p0.GoldFromMap);
        Assert.False(sim.State.Chests[0].IsPresent);

        // Already full: the chest is still taken, and nothing is counted.
        sim.State.AddUnit(0, Card(sim, "dummy"), sim.State.Chests[1].Position);
        Step(sim);
        Assert.False(sim.State.Chests[1].IsPresent);
        Assert.Equal(Fix.FromInt(10), p0.Gold);
        Assert.Equal(Fix.Half, p0.GoldFromMap);
    }

    // ------------------------------------------------------------ tie-break 3

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GoldFromMap_DecidesTieBreak3_WhenSuddenDeathExpires(int winner)
    {
        // Short regulation and sudden death with no damage at all. The winner's unit sits on a chest spawn and
        // collects one chest during sudden death; base income is equal for both and does not count.
        MatchRules rules = GoldRules(income: "0.35", match: "2", suddenDeath: "2", chestFirst: "3", chestInterval: "10");
        Simulation sim = GoldSim(rules, seed: 5);
        int chestIndex = winner == 0 ? 0 : 3;
        sim.State.AddUnit(winner, Card(sim, "dummy"), sim.State.Chests[chestIndex].Position);
        TestSim.Run(sim, 40);
        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase);
        Assert.All(sim.State.Players, p => Assert.Equal(Fix.Zero, p.GoldFromMap));

        TestSim.Run(sim, 40);
        Assert.True(sim.IsEnded);
        Assert.Equal(winner, sim.State.Winner);
        Assert.Equal(EndReason.TieBreak, sim.State.EndReason);
        Assert.Equal(TieBreakRule.GoldFromMap, sim.State.TieBreakRule);
        Assert.Equal(rules.ChestGold, sim.State.GetPlayer(winner).GoldFromMap);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1 - winner).GoldFromMap);
        Assert.Equal(sim.State.GetPlayer(1 - winner).Gold + rules.ChestGold, sim.State.GetPlayer(winner).Gold);
        Assert.All(sim.State.Players, p => Assert.Equal(Fix.Zero, p.Score));
    }

    [Fact]
    public void GoldFromMap_FromAMine_DecidesTieBreak3()
    {
        MatchRules rules = GoldRules(income: "0.35", match: "2", suddenDeath: "2", mineSeconds: "1");
        Simulation sim = GoldSim(rules);
        sim.State.AddUnit(1, Card(sim, "dummy"), sim.State.Mines[MineEast].Position + TestSim.V("1", "0"));
        TestSim.Run(sim, 80);
        Assert.True(sim.IsEnded);
        Assert.Equal(1, sim.State.Winner);
        Assert.Equal(TieBreakRule.GoldFromMap, sim.State.TieBreakRule);
        Assert.True(sim.State.GetPlayer(1).GoldFromMap > Fix.Zero);
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void ScriptedDeploysAroundTheMines_HashIdenticallyEveryTick()
    {
        // Both players keep dropping units at the front of their deploy zone in both lanes, so units walk past
        // (and fight over) both mines and chest spawns all match. Two independently built sims, harmless
        // structures so the fights last. A 3-unit capture radius and 2 s capture let walking units take mines.
        MatchRules rules = TestSim.Rules(income: "1", start: "10", mineRadius: "3", mineSeconds: "2", chestFirst: "10",
            chestInterval: "15");
        FixVector2 Mirror(FixVector2 v) => TestSim.V("18", "32") - v;
        var drops0 = new[] { TestSim.V("5.5", "12.5"), TestSim.V("12.5", "12.5"), TestSim.V("3.5", "11.5"), TestSim.V("14.5", "12.5") };
        var script = new Dictionary<int, Command[]>();
        int[] sequence = { 0, 0 };
        for (int i = 0; i < 40; i++)
        {
            int tick = 10 + i * 80;
            script[tick] = new[]
            {
                Command.DeployCard(tick, 0, sequence[0]++, i % 4, drops0[i % drops0.Length]),
                Command.DeployCard(tick, 1, sequence[1]++, (i + 1) % 4, Mirror(drops0[(i + 1) % drops0.Length])),
            };
        }

        MatchSetup Setup(MatchRules r) => new MatchSetup(r, MapTestData.LoadTwoLane(), TestSim.HarmlessStructures(),
            TestSim.DefaultDeck(r), TestSim.DefaultDeck(r));
        Simulation a = new Simulation(Setup(rules), 4242);
        Simulation b = new Simulation(Setup(TestSim.Rules(income: "1", start: "10", mineRadius: "3", mineSeconds: "2",
            chestFirst: "10", chestInterval: "15")), 4242);
        Assert.Equal(a.ComputeHash(), b.ComputeHash());

        var owners = new HashSet<(int mine, int owner)>();
        bool contested = false, unwound = false;
        int previousProgressSum = 0;
        while (!a.IsEnded)
        {
            Command[] commands = script.TryGetValue(a.State.Tick, out Command[]? c) ? c : Array.Empty<Command>();
            a.Tick(commands);
            b.Tick(commands);
            Assert.True(a.ComputeHash() == b.ComputeHash(), "hashes diverged at tick " + a.State.Tick);
            int progressSum = 0;
            foreach (MineState mine in a.State.Mines)
            {
                owners.Add((mine.Index, mine.Owner));
                progressSum += mine.CaptureProgressTicks;
                bool here0 = a.State.Units.Any(u => u.Owner == 0 && FixVector2.Distance(u.Position, mine.Position) <= rules.MineCaptureRadius);
                bool here1 = a.State.Units.Any(u => u.Owner == 1 && FixVector2.Distance(u.Position, mine.Position) <= rules.MineCaptureRadius);
                contested |= here0 && here1;
            }
            unwound |= progressSum < previousProgressSum && progressSum > 0;
            previousProgressSum = progressSum;
        }
        // The script really exercised the mines and chests.
        Assert.True(owners.Contains((MineWest, 0)) || owners.Contains((MineEast, 0)), "player 0 should have held a mine");
        Assert.True(owners.Contains((MineWest, 1)) || owners.Contains((MineEast, 1)), "player 1 should have held a mine");
        Assert.True(contested, "the mines should have been contested at some point");
        Assert.True(unwound, "capture progress should have gone backwards at some point");
        Assert.True(a.State.Players.All(p => p.GoldFromMap > Fix.Zero), "both players should have gained map gold");
        Assert.True(a.State.NextUnitId > 20, "the script should field plenty of units");

        Simulation replay = Simulation.Replay(Setup(rules), 4242, a.Log);
        Assert.Equal(a.ComputeHash(), replay.ComputeHash());
    }
}
