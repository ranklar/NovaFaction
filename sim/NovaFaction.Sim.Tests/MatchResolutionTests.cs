using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Commands;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;
using static NovaFaction.Sim.Tests.CombatTests;

namespace NovaFaction.Sim.Tests;

/// <summary>Scoring, Keep kills, the clock, sudden death and the tie-break list (docs/design.md "Match rules").</summary>
public class MatchResolutionTests
{
    private static void Step(Simulation sim) => sim.Tick(Array.Empty<Command>());

    private static void AssertEnded(Simulation sim, int winner, EndReason reason, TieBreakRule rule = TieBreakRule.None)
    {
        Assert.Equal(MatchPhase.Ended, sim.State.Phase);
        Assert.True(sim.IsEnded);
        Assert.Equal(winner, sim.State.Winner);
        Assert.Equal(reason, sim.State.EndReason);
        Assert.Equal(rule, sim.State.TieBreakRule);
        Assert.Throws<InvalidOperationException>(() => Step(sim));
    }

    /// <summary>Short clocks so the tests reach the end quickly: regulation and sudden death in whole seconds.</summary>
    private static MatchRules ShortRules(string match = "2", string suddenDeath = "2", string income = "0.35", string start = "0") =>
        TestSim.Rules(income: income, start: start, matchSeconds: match, suddenDeathSeconds: suddenDeath);

    // ------------------------------------------------------------ Keep destroyed

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void KeepDestroyed_EndsTheMatchAtOnce_ForTheAttacker(int attacker)
    {
        Simulation sim = NewSim(TestSim.Structures(hp: "1000000", keepHp: "300", keepBonus: "1000", damage: "0", range: "0.1"));
        // Three knights at the enemy Keep: 3 x 140 = 420 >= 300 on the first tick. Player 1's side is the
        // 180-degree rotation of player 0's.
        foreach (string x in new[] { "8", "9", "10" })
        {
            FixVector2 below = TestSim.V(x, "27.5"); // under keep_1 (y 28)
            sim.State.AddUnit(attacker, Card(sim, "knight"),
                attacker == 0 ? below : TestSim.V("18", "32") - below);
        }
        Step(sim);
        AssertEnded(sim, attacker, EndReason.KeepDestroyed);
        Assert.Equal(1, sim.State.Tick);
        Assert.Equal(Fix.FromInt(300 + 1000), sim.State.GetPlayer(attacker).Score);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1 - attacker).Score);
        Assert.True(sim.State.Map.IsDestroyed(attacker == 0 ? Keep1 : Keep0));
    }

    [Fact]
    public void KeepDestroyed_WinsEvenWithALowerScore()
    {
        Simulation sim = NewSim(TestSim.Structures(hp: "1000000", keepHp: "100", keepBonus: "0", damage: "0", range: "0.1"));
        sim.State.GetPlayer(0).Score = Fix.FromInt(5000);
        Place(sim, 1, "knight", "9", "4.5"); // above keep_0 (y 1-4)
        Step(sim);
        AssertEnded(sim, 1, EndReason.KeepDestroyed);
    }

    [Fact]
    public void BothKeepsFallOnTheSameTick_TheScoreDecides()
    {
        Simulation sim = NewSim(TestSim.Structures(hp: "1000000", keepHp: "100", keepBonus: "0", damage: "0", range: "0.1"));
        sim.State.GetPlayer(1).Score = Fix.FromInt(50); // player 1 was ahead before this tick
        Place(sim, 0, "knight", "9", "27.5");
        Place(sim, 1, "knight", "9", "4.5");
        Step(sim);
        // Both removed 100 HP; player 1's earlier lead decides.
        AssertEnded(sim, 1, EndReason.Score);
        Assert.True(sim.State.Map.IsDestroyed(Keep0) && sim.State.Map.IsDestroyed(Keep1));
    }

    // ------------------------------------------------------------ the clock

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ScriptedMatch_IsResolvedByScoreAtTheClock(int attacker)
    {
        // A full 3:00 match driven by deploy commands: the attacker drops a knight and a catapult into the west
        // lane, which walk to the enemy tower and hit it until the clock runs out. The towers deal no damage.
        MatchRules rules = TestSim.Rules(income: "0.35", start: "10");
        Simulation sim = TestSim.New(rules, MapTestData.LoadTwoLane(), 3,
            TestSim.Structures(hp: "1000000", keepHp: "1000000", damage: "0"));
        FixVector2 lane = TestSim.V("4.5", "10.5");
        FixVector2 drop = attacker == 0 ? lane : TestSim.V("18", "32") - lane;
        TestSim.Deploy(sim, attacker, "knight", drop);
        TestSim.Deploy(sim, attacker, "catapult", drop);
        Assert.Equal(0, sim.State.GetPlayer(attacker).IgnoredDeploys);

        while (!sim.IsEnded)
        {
            Assert.Equal(MatchPhase.Regulation, sim.State.Phase);
            Step(sim);
        }
        Assert.Equal(rules.MatchLengthTicks, sim.State.Tick);
        AssertEnded(sim, attacker, EndReason.Score);

        // Score = HP removed from enemy structures (no structure fell, so no bonus).
        Fix removed = Fix.Zero;
        foreach (StructureState s in sim.State.Structures)
        {
            if (s.Owner != attacker)
            {
                removed += s.Stats.Hp - s.Hp;
            }
            else
            {
                Assert.Equal(s.Stats.Hp, s.Hp);
            }
        }
        Assert.True(removed > Fix.FromInt(1000), "the attackers should have done real damage: " + removed);
        Assert.Equal(removed, sim.State.GetPlayer(attacker).Score);
        Assert.Equal(Fix.Zero, sim.State.GetPlayer(1 - attacker).Score);
    }

    [Fact]
    public void Clock_WithDifferentScores_HigherScoreWins()
    {
        Simulation sim = NewSim(rules: ShortRules());
        sim.State.GetPlayer(0).Score = Fix.FromInt(10);
        sim.State.GetPlayer(1).Score = Fix.FromRaw(Fix.FromInt(10).Raw + 1); // the smallest possible lead
        TestSim.Run(sim, 39);
        Assert.Equal(MatchPhase.Regulation, sim.State.Phase);
        Step(sim);
        AssertEnded(sim, 1, EndReason.Score);
    }

    // ------------------------------------------------------------ sudden death

    [Fact]
    public void EqualScores_EnterSuddenDeath_WithDoubledIncome_AndFirstDamageWins()
    {
        MatchRules rules = ShortRules(match: "10", suddenDeath: "60", income: "0.35", start: "0");
        Simulation sim = NewSim(rules: rules);
        sim.State.GetPlayer(0).Score = Fix.FromInt(7);
        sim.State.GetPlayer(1).Score = Fix.FromInt(7);
        TestSim.Run(sim, 200);

        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase);
        Assert.Equal(rules.SuddenDeathTicks, sim.State.ClockRemainingTicks);
        Assert.Equal(MatchState.NoWinner, sim.State.Winner);
        Fix income = rules.GoldBaseIncomePerSecond;
        Fix atRegulationEnd = income * Fix.FromInt(10);
        Assert.Equal(atRegulationEnd, sim.State.GetPlayer(0).Gold);

        // Income is doubled: exactly twice the base income more per second, for both players.
        Fix doubled = income * Fix.FromInt(2);
        Assert.Equal(doubled, income * rules.SuddenDeathIncomeMultiplier);
        TestSim.Run(sim, 20);
        Assert.Equal(atRegulationEnd + doubled, sim.State.GetPlayer(0).Gold);
        Assert.Equal(atRegulationEnd + doubled, sim.State.GetPlayer(1).Gold);
        TestSim.Run(sim, 40);
        Assert.Equal(atRegulationEnd + doubled * Fix.FromInt(3), sim.State.GetPlayer(1).Gold);

        // Unit fights do not end sudden death; only structure damage does.
        Place(sim, 0, "knight", "9", "11");
        Place(sim, 1, "knight", "9", "11.4");
        TestSim.Run(sim, 10);
        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase);

        // Player 1 hits a player 0 tower: the match ends on that tick.
        Place(sim, 1, "goblin_pack", "4", "8.5");
        int tick = sim.State.Tick;
        Step(sim);
        AssertEnded(sim, 1, EndReason.FirstDamage);
        Assert.Equal(tick + 1, sim.State.Tick);
        Assert.Equal(Fix.FromInt(7) + Fix.FromInt(60), sim.State.GetPlayer(1).Score);
    }

    [Theory]
    [InlineData("knight", "goblin_pack", 0)] // 140 vs 60 on the same tick: player 0 removed more
    [InlineData("goblin_pack", "knight", 1)]
    public void SuddenDeath_BothDamageOnTheSameTick_MoreDamageWins(string card0, string card1, int winner)
    {
        Simulation sim = NewSim(rules: ShortRules());
        TestSim.Run(sim, 40);
        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase);
        Place(sim, 0, card0, "4", "23.5"); // under tower_1_west
        Place(sim, 1, card1, "4", "8.5");  // above tower_0_west
        Step(sim);
        AssertEnded(sim, winner, EndReason.FirstDamage);
    }

    [Fact]
    public void SuddenDeath_EqualDamageOnTheSameTick_GoesToTheTieBreakList()
    {
        Simulation sim = NewSim(rules: ShortRules());
        TestSim.Run(sim, 40);
        Place(sim, 0, "knight", "4", "23.5");
        Place(sim, 1, "knight", "4", "8.5");
        Step(sim);
        // Same damage to towers of the same HP: rules 1-3 are level, so the coin decides.
        Assert.Equal(EndReason.TieBreak, sim.State.EndReason);
        Assert.Equal(TieBreakRule.CoinFlip, sim.State.TieBreakRule);
    }

    // ------------------------------------------------------------ tie-breaks

    /// <summary>Runs to sudden death, lets <paramref name="setup"/> change the state, then runs out the clock.</summary>
    private static Simulation ExpireSuddenDeath(Action<MatchState> setup, ulong seed = 1)
    {
        Simulation sim = TestSim.New(ShortRules(), MapTestData.LoadTwoLane(), seed, Quiet());
        TestSim.Run(sim, 40);
        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase);
        setup(sim.State);
        TestSim.Run(sim, 39);
        Assert.Equal(MatchPhase.SuddenDeath, sim.State.Phase);
        Step(sim);
        Assert.Equal((2 + 2) * 20, sim.State.Tick);
        return sim;
    }

    [Theory]
    [InlineData(Tower0West, 1)] // player 1 destroyed one of player 0's structures
    [InlineData(Tower1East, 0)]
    public void TieBreak1_MoreEnemyStructuresDestroyed(int destroyed, int winner)
    {
        Simulation sim = ExpireSuddenDeath(s =>
        {
            s.Map.DestroyStructure(destroyed);
            // Also give the loser the healthier weakest structure and more gold: rule 1 comes first.
            s.Structures[winner == 0 ? Tower0East : Tower1West].Hp = Fix.One;
            s.GetPlayer(1 - winner).GoldFromMap = Fix.FromInt(5);
        });
        AssertEnded(sim, winner, EndReason.TieBreak, TieBreakRule.StructuresDestroyed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TieBreak2_HigherHpOnOwnWeakestStructure(int winner)
    {
        Simulation sim = ExpireSuddenDeath(s =>
        {
            int loser = 1 - winner;
            // The loser's weakest structure is lower, even though the loser's total HP is higher.
            s.Structures[loser == 0 ? Tower0West : Tower1West].Hp = Fix.FromInt(100);
            s.Structures[winner == 0 ? Tower0West : Tower1West].Hp = Fix.FromInt(101);
            s.Structures[winner == 0 ? Keep0 : Keep1].Hp = Fix.FromInt(200);
            s.GetPlayer(loser).GoldFromMap = Fix.FromInt(5);
        });
        AssertEnded(sim, winner, EndReason.TieBreak, TieBreakRule.WeakestStructureHp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TieBreak2_ComparesWeakestStandingStructure_DestroyedOnesAreLeftOut(int winner)
    {
        // Each player lost one tower (rule 1 level; the destroyed towers are at 0 HP). Only standing structures
        // count for rule 2: the winner's weakest standing one (200) beats the loser's (100). Under the old
        // "destroyed counts as 0" wording both sides would have been level at 0.
        Simulation sim = ExpireSuddenDeath(s =>
        {
            int loser = 1 - winner;
            foreach (int destroyed in new[] { Tower0West, Tower1West })
            {
                s.Structures[destroyed].Hp = Fix.Zero;
                s.Map.DestroyStructure(destroyed);
            }
            s.Structures[winner == 0 ? Tower0East : Tower1East].Hp = Fix.FromInt(200);
            s.Structures[loser == 0 ? Tower0East : Tower1East].Hp = Fix.FromInt(100);
            s.GetPlayer(loser).GoldFromMap = Fix.FromInt(5); // rule 3 would favor the loser
        });
        AssertEnded(sim, winner, EndReason.TieBreak, TieBreakRule.WeakestStructureHp);
    }

    [Fact]
    public void TieBreak2_EqualStandingStructures_AfterEqualLosses_GoesToGold()
    {
        // Each lost one tower but player 0's lost tower kept its HP value: it must still be ignored, so the
        // standing structures (all full) are level and rule 3 decides.
        Simulation sim = ExpireSuddenDeath(s =>
        {
            s.Structures[Tower1West].Hp = Fix.Zero;
            s.Map.DestroyStructure(Tower0West);
            s.Map.DestroyStructure(Tower1West);
            s.GetPlayer(1).GoldFromMap = Fix.One;
        });
        AssertEnded(sim, 1, EndReason.TieBreak, TieBreakRule.GoldFromMap);
    }

    [Fact]
    public void TieBreak2_NoStandingStructureOnEitherSide_IsLevel()
    {
        Simulation sim = ExpireSuddenDeath(s =>
        {
            for (int i = 0; i < s.Structures.Count; i++)
            {
                s.Map.DestroyStructure(i);
            }
            s.GetPlayer(0).GoldFromMap = Fix.One;
        });
        AssertEnded(sim, 0, EndReason.TieBreak, TieBreakRule.GoldFromMap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TieBreak3_MoreGoldFromMap(int winner)
    {
        Simulation sim = ExpireSuddenDeath(s => s.GetPlayer(winner).GoldFromMap = Fix.Epsilon);
        AssertEnded(sim, winner, EndReason.TieBreak, TieBreakRule.GoldFromMap);
        // Base income does not count: both players banked the same, and the rule still used GoldFromMap only.
        Assert.Equal(sim.State.GetPlayer(0).Gold, sim.State.GetPlayer(1).Gold);
    }

    [Fact]
    public void TieBreak4_CoinFlipFromTheMatchRng()
    {
        var winners = new HashSet<int>();
        for (ulong seed = 1; seed <= 16; seed++)
        {
            Simulation sim = ExpireSuddenDeath(s => { }, seed);
            // Replay to read the RNG state just before the flip (nothing else draws from it after the shuffle).
            Simulation twin = TestSim.New(ShortRules(), MapTestData.LoadTwoLane(), seed, Quiet());
            var coin = new SimRandom(0);
            coin.SetState(twin.State.Random.GetState());
            int expected = coin.NextInt(0, 2);

            AssertEnded(sim, expected, EndReason.TieBreak, TieBreakRule.CoinFlip);
            Assert.Equal(coin.GetState(), sim.State.Random.GetState());
            winners.Add(sim.State.Winner);
        }
        Assert.Equal(new[] { 0, 1 }, winners.OrderBy(w => w));
    }

    [Fact]
    public void ZeroLengthSuddenDeath_GoesStraightToTheTieBreakList()
    {
        Simulation sim = TestSim.New(ShortRules(suddenDeath: "0"), MapTestData.LoadTwoLane(), 1, Quiet());
        sim.State.GetPlayer(1).GoldFromMap = Fix.One;
        TestSim.Run(sim, 39);
        Step(sim);
        AssertEnded(sim, 1, EndReason.TieBreak, TieBreakRule.GoldFromMap);
    }

    [Fact]
    public void MatchState_StartsWithNoResult()
    {
        Simulation sim = NewSim();
        Assert.Equal(MatchState.NoWinner, sim.State.Winner);
        Assert.Equal(EndReason.None, sim.State.EndReason);
        Assert.Equal(TieBreakRule.None, sim.State.TieBreakRule);
        Assert.All(sim.State.Players, p => Assert.Equal(Fix.Zero, p.GoldFromMap));
        Assert.All(sim.State.Structures, s =>
        {
            Assert.Equal(s.Stats.Hp, s.Hp);
            Assert.False(s.IsDestroyed);
            Assert.Equal(StructureState.NoTarget, s.TargetUnitId);
        });
        Assert.Equal(sim.Map.Structures.Count, sim.State.Structures.Count);
        Assert.Empty(sim.State.Projectiles);
        Assert.Equal(1, sim.State.NextProjectileId);
    }
}
