using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using static NovaFaction.Sim.Tests.MapTestData;

namespace NovaFaction.Sim.Tests;

/// <summary>The map inside a running match: destruction, unlocks, and the state hash.</summary>
public class MapStateTests
{
    private static MatchRules Rules => MatchRulesTests.LoadShippedRules();

    private static Simulation SmallSim(ulong seed = 1) => TestSim.New(Rules, Small(), seed);

    private static FixVector2 At(MapState map, int x, int y) => map.Grid.CellToWorld(x, y);

    [Fact]
    public void NewMatch_HasTheMapWithNothingDestroyed()
    {
        MapDefinition def = Small();
        var sim = TestSim.New(Rules, def, 1);
        MapState map = sim.State.Map;
        Assert.Same(def, map.Definition);
        Assert.Same(def, sim.Map);
        for (int i = 0; i < def.Structures.Count; i++)
        {
            Assert.False(map.IsDestroyed(i));
        }
        Assert.Empty(map.GetUnlockedZones(0));
        Assert.Empty(map.GetUnlockedZones(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetUnlockedZones(2));
    }

    [Fact]
    public void DestroyingATower_UnlocksItsZoneForTheAttacker()
    {
        MapState map = SmallSim().State.Map;
        CellRect zone = map.Definition.Structures[Tower0E].UnlocksDeployZone!.Value;
        Assert.False(map.IsDeployable(1, At(map, 5, 2)));

        map.DestroyStructure(Tower0E);

        Assert.True(map.IsDestroyed(Tower0E));
        Assert.Equal(new[] { zone }, map.GetUnlockedZones(1));
        Assert.Empty(map.GetUnlockedZones(0));
        Assert.True(map.IsDeployable(1, At(map, 5, 2)));
        Assert.False(map.IsDeployable(1, At(map, 1, 2)));

        // The destroyed tower's cell is now open ground in player 0's base zone.
        Assert.True(map.Grid.IsWalkable(6, 0));
        Assert.True(map.IsDeployable(0, At(map, 6, 0)));
        Assert.False(map.IsDeployable(1, At(map, 6, 0))); // outside the unlock rectangle
    }

    [Fact]
    public void DestroyingAKeep_UnlocksNothing()
    {
        MapState map = SmallSim().State.Map;
        map.DestroyStructure(Keep1);
        Assert.True(map.IsDestroyed(Keep1));
        Assert.Empty(map.GetUnlockedZones(0));
        Assert.Empty(map.GetUnlockedZones(1));
    }

    [Fact]
    public void UnlockedZones_AreOrderedByTowerIndex_NotDestructionOrder()
    {
        MapState a = SmallSim().State.Map;
        MapState b = SmallSim().State.Map;
        a.DestroyStructure(Tower1W);
        a.DestroyStructure(Tower1E);
        b.DestroyStructure(Tower1E);
        b.DestroyStructure(Tower1W);
        b.DestroyStructure(Tower1W); // repeat is a no-op
        Assert.Equal(a.GetUnlockedZones(0), b.GetUnlockedZones(0));
        Assert.Equal(new[] { new CellRect(0, 5, 3, 3), new CellRect(4, 5, 3, 3) }, a.GetUnlockedZones(0));
        Assert.Equal(Hash(a), Hash(b));
    }

    [Fact]
    public void Destruction_RebuildsCachedFlowFields()
    {
        // On the small map, keep_0's cells become part of the route to tower_0_w once keep_0 falls.
        MapState map = SmallSim().State.Map;
        FlowField before = map.FlowFields.Get(Tower0W);
        Assert.Same(before, map.FlowFields.Get(Tower0W));
        map.DestroyStructure(Keep0);
        FlowField after = map.FlowFields.Get(Tower0W);
        Assert.NotSame(before, after);
        Assert.True(after.IsReachable(new CellCoord(3, 0)));
        Assert.False(before.IsReachable(new CellCoord(3, 0)));
        Assert.Equal(Fix.FromInt(3), after.GetCellDistance(new CellCoord(3, 0)));
    }

    private static ulong Hash(MapState map)
    {
        var h = new StateHasher();
        map.AppendHash(ref h);
        return h.Value;
    }

    [Fact]
    public void StateHash_SeesDestructionAndUnlocks()
    {
        Simulation sim = SmallSim();
        var seen = new HashSet<ulong> { sim.ComputeHash() };
        for (int i = 0; i < sim.Map.Structures.Count; i++)
        {
            sim.State.Map.DestroyStructure(i);
            Assert.True(seen.Add(sim.ComputeHash()), "destroying structure " + i + " is not reflected in the hash");
        }

        // Same destruction on two matches -> same hash; different structure -> different hash.
        Simulation a = SmallSim(), b = SmallSim(), c = SmallSim();
        a.State.Map.DestroyStructure(Tower0W);
        b.State.Map.DestroyStructure(Tower0W);
        c.State.Map.DestroyStructure(Tower1W);
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), c.ComputeHash());
    }

    [Fact]
    public void StateHash_SeesUnlockedZonesOnTheirOwn()
    {
        // Two map states with the same destroyed flags but different unlock lists must differ.
        MapState a = SmallSim().State.Map;
        MapState b = SmallSim().State.Map;
        a.DestroyStructure(Tower0W);
        b.DestroyStructure(Tower0W);
        ((List<CellRect>)b.GetUnlockedZones(1)).Clear(); // corrupt b directly
        Assert.NotEqual(Hash(a), Hash(b));
    }

    [Fact]
    public void StateHash_DetectsDifferentMapData()
    {
        ulong baseline = SmallSim().ComputeHash();
        Assert.Equal(baseline, TestSim.New(Rules, MapDefinition.FromJson(SmallJson().Replace("\n", "\r\n")), 1).ComputeHash());

        // Same id, one cell different.
        var edited = TestSim.New(Rules, MapDefinition.FromJson(SmallJson(rows: SmallRowsWith(6, "#......"))), 1);
        Assert.NotEqual(baseline, edited.ComputeHash());

        // Same content, different id.
        var renamed = TestSim.New(Rules, MapDefinition.FromJson(SmallJson(id: "\"small-b\"")), 1);
        Assert.NotEqual(baseline, renamed.ComputeHash());

        // Another map entirely.
        Assert.NotEqual(baseline, TestSim.New(Rules, LoadTwoLane(), 1).ComputeHash());
    }

    [Fact]
    public void Determinism_ScriptedMatchWithDestruction_GivesIdenticalHashesEveryTick()
    {
        MatchRules rules = Rules;
        CommandLog log = DeterminismTests.ScriptedLog(rules, 31);
        var a = TestSim.New(rules, LoadTwoLane(), 77);
        var b = TestSim.New(MatchRulesTests.LoadShippedRules(), LoadTwoLane(), 77);
        // Structures fall at fixed ticks (standing in for combat, which does not exist yet).
        var destroyAt = new Dictionary<int, int> { { 100, 1 }, { 700, 5 }, { 1500, 0 } };
        while (!a.IsEnded)
        {
            int tick = a.State.Tick;
            if (destroyAt.TryGetValue(tick, out int structure))
            {
                a.State.Map.DestroyStructure(structure);
                b.State.Map.DestroyStructure(structure);
                // Derived flow fields agree too.
                Assert.Equal(FlowFieldTests.Fingerprint(a.State.Map.Grid, a.State.Map.FlowFields.Get(3)),
                    FlowFieldTests.Fingerprint(b.State.Map.Grid, b.State.Map.FlowFields.Get(3)));
            }
            a.Tick(log.GetCommands(tick));
            b.Tick(log.GetCommands(tick));
            Assert.Equal(a.ComputeHash(), b.ComputeHash());
        }
        Assert.Equal(new CellRect[] { a.Map.Structures[1].UnlocksDeployZone!.Value }, a.State.Map.GetUnlockedZones(1));
        Assert.Equal(new CellRect[] { a.Map.Structures[5].UnlocksDeployZone!.Value }, a.State.Map.GetUnlockedZones(0));
    }
}
