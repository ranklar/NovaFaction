using System.Collections.Generic;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim.Economy
{
    /// <summary>
    /// A gold mine at one of the map's 'M' cells. Neutral at the start; captured by standing next to it.
    /// It cannot be attacked, is not a scoring target, and its cell blocks movement all match.
    /// The client may read everything; only the sim changes it.
    /// </summary>
    public sealed class MineState : IStateHashable
    {
        /// <summary><see cref="Owner"/> / <see cref="CapturingPlayer"/> value meaning "nobody".</summary>
        public const int Nobody = -1;

        internal MineState(int index, CellCoord cell, FixVector2 position, int captureTicks)
        {
            Index = index;
            Cell = cell;
            Position = position;
            CaptureTicksRequired = captureTicks;
            Owner = Nobody;
            CapturingPlayer = Nobody;
        }

        /// <summary>Position in the map's mine list (bottom row first, then left to right).</summary>
        public int Index { get; }

        public CellCoord Cell { get; }

        /// <summary>World position of the mine cell's center; capture distance is measured from here.</summary>
        public FixVector2 Position { get; }

        /// <summary>The owning player (0 or 1), or <see cref="Nobody"/> while neutral.</summary>
        public int Owner { get; internal set; }

        /// <summary>
        /// The player whose capture progress is stored, or <see cref="Nobody"/> when progress is 0. Never the owner.
        /// </summary>
        public int CapturingPlayer { get; internal set; }

        /// <summary>Capture progress in ticks, 0 .. <see cref="CaptureTicksRequired"/> - 1.</summary>
        public int CaptureProgressTicks { get; internal set; }

        /// <summary>Ticks a lone player needs to capture the mine (from rules, not part of the state hash).</summary>
        public int CaptureTicksRequired { get; }

        /// <summary>Capture progress as a fraction 0..1, for display.</summary>
        public Fix CaptureProgress => Fix.FromInt(CaptureProgressTicks) / Fix.FromInt(CaptureTicksRequired);

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Index);
            hasher.Add(Owner);
            hasher.Add(CapturingPlayer);
            hasher.Add(CaptureProgressTicks);
        }
    }

    /// <summary>
    /// One of the map's 'C' chest spawn points and whether a chest is waiting there. At most one chest per point.
    /// </summary>
    public sealed class ChestState : IStateHashable
    {
        internal ChestState(int index, CellCoord cell, FixVector2 position)
        {
            Index = index;
            Cell = cell;
            Position = position;
        }

        /// <summary>Position in the map's chest spawn list (bottom row first, then left to right).</summary>
        public int Index { get; }

        public CellCoord Cell { get; }

        /// <summary>World position of the spawn cell's center; collection distance is measured from here.</summary>
        public FixVector2 Position { get; }

        /// <summary>True while a chest waits here to be collected.</summary>
        public bool IsPresent { get; internal set; }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Index);
            hasher.Add(IsPresent);
        }
    }

    /// <summary>
    /// Gold mines and chests: capture, chest collection and chest waves. See docs/design.md ("Map gold").
    /// Runs after combat, so it sees this tick's final unit positions (and only living units).
    /// </summary>
    internal static class MapGoldSystem
    {
        /// <summary>
        /// Updates mine capture, then collects chests. Chest gold is added at once, limited by the gold cap;
        /// what was actually received also counts toward <see cref="PlayerState.GoldFromMap"/>.
        /// </summary>
        internal static void Tick(MatchState state, MatchRules rules)
        {
            IReadOnlyList<Unit> units = state.Units;
            foreach (MineState mine in state.Mines) // index order
            {
                UpdateMine(mine, units, rules);
            }
            foreach (ChestState chest in state.Chests) // index order
            {
                if (chest.IsPresent)
                {
                    TryCollect(state, chest, units, rules);
                }
            }
        }

        /// <summary>
        /// Presence rules: one player alone moves progress toward their own capture (first undoing the other
        /// player's progress, if any); the owner alone and nobody at all both wind progress back toward 0; both
        /// players present freezes it. Every change is one tick per tick.
        /// </summary>
        private static void UpdateMine(MineState mine, IReadOnlyList<Unit> units, MatchRules rules)
        {
            bool present0 = false, present1 = false;
            foreach (Unit u in units)
            {
                if (FixVector2.Distance(u.Position, mine.Position) <= rules.MineCaptureRadius)
                {
                    if (u.Owner == 0) present0 = true;
                    else present1 = true;
                }
            }
            if (present0 && present1)
            {
                return; // contested: paused
            }
            int alone = present0 ? 0 : present1 ? 1 : MineState.Nobody;
            if (alone == MineState.Nobody || alone == mine.Owner)
            {
                Unwind(mine);
                return;
            }
            if (mine.CapturingPlayer != MineState.Nobody && mine.CapturingPlayer != alone)
            {
                Unwind(mine); // the other player's progress must be undone first
                return;
            }
            mine.CapturingPlayer = alone;
            mine.CaptureProgressTicks++;
            if (mine.CaptureProgressTicks >= mine.CaptureTicksRequired)
            {
                mine.Owner = alone;
                mine.CapturingPlayer = MineState.Nobody;
                mine.CaptureProgressTicks = 0;
            }
        }

        private static void Unwind(MineState mine)
        {
            if (mine.CaptureProgressTicks > 0)
            {
                mine.CaptureProgressTicks--;
            }
            if (mine.CaptureProgressTicks == 0)
            {
                mine.CapturingPlayer = MineState.Nobody;
            }
        }

        /// <summary>
        /// The closest unit within the collect radius takes the chest (ties: lower unit id, so a same-tick
        /// arrival never depends on processing order). The chest is used up even if the player is at the gold cap.
        /// </summary>
        private static void TryCollect(MatchState state, ChestState chest, IReadOnlyList<Unit> units, MatchRules rules)
        {
            Unit? best = null;
            Fix bestDistance = Fix.MaxValue;
            foreach (Unit u in units) // id order, so a strict comparison keeps the lower id on ties
            {
                Fix distance = FixVector2.Distance(u.Position, chest.Position);
                if (distance <= rules.ChestCollectRadius && distance < bestDistance)
                {
                    best = u;
                    bestDistance = distance;
                }
            }
            if (best == null)
            {
                return;
            }
            chest.IsPresent = false;
            PlayerState player = state.GetPlayer(best.Owner);
            Fix gold = Fix.Min(player.Gold + rules.ChestGold, rules.GoldCap);
            Fix received = gold - player.Gold;
            if (received > Fix.Zero)
            {
                player.Gold = gold;
                player.GoldFromMap += received;
            }
        }

        /// <summary>
        /// Chest waves: at <c>chestFirstSpawnSeconds</c> and every <c>chestSpawnIntervalSeconds</c> after it (match
        /// time, sudden death included), a chest appears at every spawn point that does not already hold one.
        /// Called whenever <c>State.Tick</c> reaches a new value (and at match start).
        /// </summary>
        internal static void SpawnDueChests(MatchState state, MatchRules rules)
        {
            int first = rules.ChestFirstSpawnTick;
            int tick = state.Tick;
            if (tick < first || (tick - first) % rules.ChestSpawnIntervalTicks != 0)
            {
                return;
            }
            foreach (ChestState chest in state.Chests)
            {
                chest.IsPresent = true; // a waiting chest stays as it is: never two at one point
            }
        }

        /// <summary>
        /// Mine income per second for a player: mineIncomePerSecond per owned mine, at most mineIncomeCap.
        /// </summary>
        internal static Fix MineIncome(MatchState state, MatchRules rules, int player)
        {
            int owned = 0;
            foreach (MineState mine in state.Mines)
            {
                if (mine.Owner == player)
                {
                    owned++;
                }
            }
            return Fix.Min(rules.MineIncomePerSecond * Fix.FromInt(owned), rules.MineIncomeCap);
        }

        internal static MineState[] CreateMines(MapDefinition map, Grid grid, MatchRules rules)
        {
            var mines = new MineState[map.Mines.Count];
            for (int i = 0; i < mines.Length; i++)
            {
                mines[i] = new MineState(i, map.Mines[i], grid.CellToWorld(map.Mines[i]), rules.MineCaptureTicks);
            }
            return mines;
        }

        internal static ChestState[] CreateChests(MapDefinition map, Grid grid)
        {
            var chests = new ChestState[map.ChestSpawns.Count];
            for (int i = 0; i < chests.Length; i++)
            {
                chests[i] = new ChestState(i, map.ChestSpawns[i], grid.CellToWorld(map.ChestSpawns[i]));
            }
            return chests;
        }
    }
}
