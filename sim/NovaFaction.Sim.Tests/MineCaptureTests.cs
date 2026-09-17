using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Economy;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;
using static NovaFaction.Sim.Tests.CombatTests;

namespace NovaFaction.Sim.Tests;

/// <summary>
/// Units stopping to capture gold mines they pass (docs/design.md "Map gold"). twolane, structures that do
/// nothing, no income. The west mine sits at cell (5, 15), center (5.5, 15.5), at the inner edge of the west river
/// gap (open cells x 2-4 on that row); the east mine at (12, 16) in the east gap.
/// </summary>
public class MineCaptureTests
{
    private const int MineWest = 0, MineEast = 1;

    private static void Step(Simulation sim) => sim.Tick(Array.Empty<Command>());

    /// <summary>The shipped 4 s capture (80 ticks).</summary>
    private static Simulation CaptureSim() =>
        TestSim.New(TestSim.Rules(income: "0", start: "0", mineSeconds: "4"), MapTestData.LoadTwoLane(), 1, Quiet());

    /// <summary>Steps until the condition holds; returns the ticks taken or fails.</summary>
    private static int StepUntil(Simulation sim, Func<bool> condition, int limit, string what)
    {
        for (int t = 1; t <= limit; t++)
        {
            Step(sim);
            if (condition())
            {
                return t;
            }
        }
        Assert.Fail(what + " did not happen within " + limit + " ticks");
        return -1;
    }

    [Fact]
    public void GroundUnit_PausesAtANeutralMine_CapturesIt_ThenReachesItsObjective()
    {
        Simulation sim = CaptureSim();
        MineState mine = sim.State.Mines[MineWest];
        Assert.Equal(80, mine.CaptureTicksRequired);
        Unit knight = Place(sim, 0, "knight", "4.5", "11.5"); // below the west gap, walking north
        Assert.True(knight.Definition.CanCapture);

        StepUntil(sim, () => knight.State == UnitState.Capturing, 200, "stopping at the mine");
        FixVector2 stopped = knight.Position;
        Assert.True(FixVector2.Distance(stopped, mine.Position) <= sim.Rules.MineCaptureRadius);
        Assert.Equal(TargetRef.None, knight.Target);
        Assert.Equal(Tower1West, knight.Objective); // it still knows where it is going

        // It stays put until the mine is its player's.
        int captureTicks = 0;
        while (mine.Owner != 0)
        {
            Assert.True(captureTicks++ < 200, "the capture should finish");
            Assert.Equal(UnitState.Capturing, knight.State);
            Assert.Equal(stopped, knight.Position);
            Step(sim);
        }
        Assert.InRange(captureTicks, 79, 81); // the stop tick may already count toward presence
        Assert.Equal(stopped, knight.Position);

        // Then it resumes its objective and gets there.
        Step(sim);
        Assert.Equal(UnitState.Moving, knight.State);
        StepUntil(sim, () => knight.State == UnitState.Attacking, 400, "reaching tower_1_west");
        Assert.Equal(TargetRef.Structure(Tower1West), knight.Target);
        Assert.Equal(0, mine.Owner);
        Assert.Equal(MineState.Nobody, sim.State.Mines[MineEast].Owner);
    }

    [Fact]
    public void GroundUnit_StopsAtAnEnemyMineToo_ButWalksPastItsOwn()
    {
        Simulation sim = CaptureSim();
        MineState mine = sim.State.Mines[MineWest];
        mine.Owner = 1;
        Unit knight = Place(sim, 0, "knight", "4.5", "15.5"); // right beside the mine
        Step(sim);
        Assert.Equal(UnitState.Capturing, knight.State);
        TestSim.Run(sim, 80);
        Assert.Equal(0, mine.Owner);

        Simulation own = CaptureSim();
        own.State.Mines[MineWest].Owner = 0;
        Unit walker = Place(own, 0, "knight", "4.5", "15.5");
        for (int t = 0; t < 40; t++)
        {
            Step(own);
            Assert.Equal(UnitState.Moving, walker.State);
        }
        Assert.True(walker.Position.Y > Fix.FromInt(17), "it should have walked on, at " + walker.Position);
    }

    public static IEnumerable<object[]> NonCapturers() => new[]
    {
        new object[] { "griffin", "5.5", "12.5" },     // a flyer passing right over the mine
        new object[] { "stone_golem", "4.5", "13.5" }, // structures only
    };

    [Theory]
    [MemberData(nameof(NonCapturers))]
    public void NonCapturers_WalkPastWithoutStopping(string cardId, string x, string y)
    {
        Simulation sim = CaptureSim();
        MineState mine = sim.State.Mines[MineWest];
        Unit unit = Place(sim, 0, cardId, x, y);
        Assert.False(unit.Definition.CanCapture);
        bool wasInRadius = false;
        int mostProgress = 0;
        for (int t = 0; t < 200; t++)
        {
            Step(sim);
            Assert.NotEqual(UnitState.Capturing, unit.State);
            wasInRadius |= FixVector2.Distance(unit.Position, mine.Position) <= sim.Rules.MineCaptureRadius;
            mostProgress = Math.Max(mostProgress, mine.CaptureProgressTicks);
        }
        Assert.True(wasInRadius, cardId + " should pass within the capture radius");
        Assert.True(mostProgress > 0, "passing by still counts as presence while it lasts");
        Assert.True(unit.Position.Y > Fix.FromInt(18), cardId + " should have moved on, at " + unit.Position);
        Assert.Equal(MineState.Nobody, mine.Owner);
    }

    [Fact]
    public void Units_NeverDetourTowardAMine()
    {
        // A knight walking up the far side of the east gap stays out of the east mine's radius: it never turns
        // toward the mine and never stops.
        Simulation sim = CaptureSim();
        MineState mine = sim.State.Mines[MineEast];
        Unit knight = Place(sim, 0, "knight", "15.5", "11.5");
        Fix closest = Fix.MaxValue;
        for (int t = 0; t < 300; t++)
        {
            Step(sim);
            Assert.NotEqual(UnitState.Capturing, knight.State);
            closest = Fix.Min(closest, FixVector2.Distance(knight.Position, mine.Position));
        }
        Assert.True(closest > sim.Rules.MineCaptureRadius, "closest approach " + closest);
        Assert.Equal(0, mine.CaptureProgressTicks);
        Assert.Equal(TargetRef.Structure(Tower1East), knight.Target);
    }

    [Fact]
    public void ArrivingEnemy_InterruptsTheCapture_AndAfterAFightAwayTheUnitResumesItsObjective()
    {
        Simulation sim = CaptureSim();
        MineState mine = sim.State.Mines[MineWest];
        Unit knight = Place(sim, 0, "knight", "4.5", "15.5");
        TestSim.Run(sim, 20);
        Assert.Equal(UnitState.Capturing, knight.State);
        Assert.Equal(20, mine.CaptureProgressTicks);

        // An enemy shows up inside the knight's scan radius (4 away, north of the river): combat wins.
        Unit enemy = Place(sim, 1, "dummy", "4.5", "19.5");
        Step(sim);
        Assert.Equal(TargetRef.Unit(enemy.Id), knight.Target);
        Assert.Equal(UnitState.Moving, knight.State);
        Assert.NotEqual(knight.Position, TestSim.V("4.5", "15.5")); // it left to fight

        // It fights the dummy to death away from the mine; the abandoned progress decays.
        StepUntil(sim, () => sim.State.FindUnit(enemy.Id) == null, 600, "killing the dummy");
        Assert.True(FixVector2.Distance(knight.Position, mine.Position) > sim.Rules.MineCaptureRadius);
        Assert.Equal(MineState.Nobody, mine.Owner);
        Assert.Equal(0, mine.CaptureProgressTicks);

        // The fight is over and it is no longer at the mine: it re-evaluates and heads for its objective.
        Fix y = knight.Position.Y;
        for (int t = 0; t < 40; t++)
        {
            Step(sim);
            Assert.Equal(UnitState.Moving, knight.State);
        }
        Assert.True(knight.Position.Y > y + Fix.One, "it should walk on north, not back to the mine");
        Assert.Equal(TargetRef.Structure(Tower1West), knight.Target);
    }

    [Fact]
    public void UnitResumesCapturing_AfterWinningAFightAtTheMine()
    {
        Simulation sim = CaptureSim();
        MineState mine = sim.State.Mines[MineWest];
        Unit knight = Place(sim, 0, "knight", "4.5", "15.5");
        TestSim.Run(sim, 30);
        Assert.Equal(UnitState.Capturing, knight.State);
        Assert.Equal(30, mine.CaptureProgressTicks);
        FixVector2 spot = knight.Position;

        // A weak enemy steps in within melee range: the knight stops capturing and fights it on the spot. One
        // 140 hit kills its 50 HP; it dies in combat, before presence is counted, so it never contests the mine.
        Unit enemy = Place(sim, 1, "fragile", "4.5", "15.9");
        Step(sim);
        Assert.Equal(UnitState.Attacking, knight.State);
        Assert.Equal(TargetRef.None, knight.Target); // cleared when the enemy died
        Assert.Null(sim.State.FindUnit(enemy.Id));
        Assert.Equal(31, mine.CaptureProgressTicks);

        // With the fight won it re-evaluates: still beside a mine it does not own, so it captures again.
        Step(sim);
        Assert.Equal(UnitState.Capturing, knight.State);
        Assert.Equal(TargetRef.None, knight.Target);
        Assert.Equal(spot, knight.Position);
        Assert.Equal(32, mine.CaptureProgressTicks);

        // A tougher enemy contests the mine while the fight lasts: progress pauses, then capturing resumes.
        Unit tough = Place(sim, 1, "dummy", "4.5", "15.9");
        Step(sim);
        Assert.Equal(TargetRef.Unit(tough.Id), knight.Target);
        Assert.Equal(32, mine.CaptureProgressTicks);
        StepUntil(sim, () => sim.State.FindUnit(tough.Id) == null, 400, "killing the tough dummy");
        Assert.Equal(33, mine.CaptureProgressTicks); // paused all fight; the kill tick counts (the dummy was gone)
        Assert.Equal(spot, knight.Position);
        Step(sim);
        Assert.Equal(UnitState.Capturing, knight.State);
        Assert.Equal(34, mine.CaptureProgressTicks);

        StepUntil(sim, () => mine.Owner == 0, 60, "finishing the capture");
        Assert.Equal(spot, knight.Position);
        Step(sim);
        Assert.Equal(UnitState.Moving, knight.State);
    }

    [Fact]
    public void Capturer_IgnoresEnemiesItCannotHit()
    {
        // A flying enemy over the mine cannot be hit by a ground-only knight: the knight keeps capturing, and the
        // mine stays contested (paused).
        Simulation sim = CaptureSim();
        MineState mine = sim.State.Mines[MineWest];
        Unit knight = Place(sim, 0, "knight", "4.5", "15.5");
        TestSim.Run(sim, 10);
        Place(sim, 1, "balloon", "5.5", "15.5");
        TestSim.Run(sim, 100);
        Assert.Equal(UnitState.Capturing, knight.State);
        Assert.Equal(10, mine.CaptureProgressTicks);
        Assert.Equal(MineState.Nobody, mine.Owner);
    }

    [Fact]
    public void DeployedNextToAMine_CapturesIt()
    {
        // The bot's way of taking a mine: drop a capturer beside it.
        Simulation sim = CaptureSim();
        sim.State.GetPlayer(0).Gold = Fix.FromInt(10);
        TestSim.Deploy(sim, 0, "goblin_pack", TestSim.V("4.5", "12.5"));
        StepUntil(sim, () => sim.State.Mines[MineWest].Owner == 0, 400, "the goblins taking the mine");
        Assert.Equal(4, sim.State.Units.Count);
        Assert.Contains(sim.State.Units, u => u.State == UnitState.Capturing);
    }
}
