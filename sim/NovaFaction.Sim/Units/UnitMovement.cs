using System;
using System.Collections.Generic;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Units
{
    /// <summary>
    /// Movement helpers: objectives, footprint distances, flow directions, separation, stepping and spawn
    /// placement. The per-tick loop that uses them is <see cref="Combat.BattleSystem"/>. See docs/design.md
    /// ("Units, decks and movement" and "Combat and match resolution") for the rules.
    /// </summary>
    internal static class UnitMovement
    {
        /// <summary>Neighbor flow cells whose path cost differs from the unit's own cell by more than this are not blended.</summary>
        private static readonly Fix MaxBlendCostGap = Fix.FromInt(2);

        // ------------------------------------------------------------ objectives

        /// <summary>
        /// Nearest standing enemy structure: by flow-field path distance for ground units, by straight-line
        /// distance to the footprint for flyers. Ties go to the lower structure index.
        /// </summary>
        internal static int SelectObjective(MapState map, Unit unit, FixVector2 position)
        {
            int best = Unit.NoObjective;
            Fix bestDistance = Fix.MaxValue;
            IReadOnlyList<StructureDefinition> structures = map.Definition.Structures;
            for (int s = 0; s < structures.Count; s++)
            {
                if (structures[s].Owner == unit.Owner || map.IsDestroyed(s))
                {
                    continue;
                }
                Fix distance = unit.IsFlying
                    ? DistanceToFootprint(map.Grid, position, s)
                    : map.FlowFields.Get(s).GetDistance(position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = s;
                }
            }
            return best;
        }

        internal static bool IsInRange(Grid grid, Unit unit, FixVector2 position, int structure) =>
            DistanceToFootprint(grid, position, structure) <= unit.Definition.Range;

        /// <summary>Distance from a point to the nearest point of a structure's footprint (0 inside it).</summary>
        internal static Fix DistanceToFootprint(Grid grid, FixVector2 position, int structure)
        {
            CellRect rect = grid.GetFootprint(structure);
            Fix cs = grid.CellSize;
            Fix dx = AxisGap(position.X, Fix.FromInt(rect.X) * cs, Fix.FromInt(rect.XEnd) * cs);
            Fix dy = AxisGap(position.Y, Fix.FromInt(rect.Y) * cs, Fix.FromInt(rect.YEnd) * cs);
            return new FixVector2(dx, dy).Length;
        }

        private static Fix AxisGap(Fix value, Fix min, Fix max) =>
            value < min ? min - value : value > max ? value - max : Fix.Zero;

        /// <summary>The point of a structure's footprint nearest to the position (the position itself if inside).</summary>
        internal static FixVector2 ClosestPointOnFootprint(Grid grid, FixVector2 position, int structure)
        {
            CellRect rect = grid.GetFootprint(structure);
            Fix cs = grid.CellSize;
            return new FixVector2(
                Fix.Clamp(position.X, Fix.FromInt(rect.X) * cs, Fix.FromInt(rect.XEnd) * cs),
                Fix.Clamp(position.Y, Fix.FromInt(rect.Y) * cs, Fix.FromInt(rect.YEnd) * cs));
        }

        /// <summary>World position of the center of a structure's footprint.</summary>
        internal static FixVector2 FootprintCenter(Grid grid, int structure)
        {
            CellRect rect = grid.GetFootprint(structure);
            Fix cs = grid.CellSize;
            return new FixVector2(
                Fix.FromInt(rect.X + rect.XEnd) * cs * Fix.Half,
                Fix.FromInt(rect.Y + rect.YEnd) * cs * Fix.Half);
        }

        // ------------------------------------------------------------ directions


        /// <summary>
        /// Bilinear blend of the flow directions of the four cells whose centers surround the position,
        /// so paths bend smoothly instead of in 8-way steps. Blocked cells, cells with no direction, and
        /// cells whose path cost is far from the unit's own (the other side of a wall) are left out.
        /// Falls back to the unit's own cell direction if the blend cancels out.
        /// </summary>
        internal static FixVector2 BlendedFlowDirection(MapState map, int structure, FixVector2 position) =>
            BlendedFlowDirection(map.Grid, map.FlowFields.Get(structure), position);

        internal static FixVector2 BlendedFlowDirection(Grid grid, FlowField field, FixVector2 position)
        {
            CellCoord ownCell = grid.WorldToCell(position);
            FixVector2 own = field.GetCellDirection(ownCell);
            Fix ownCost = field.GetCellDistance(ownCell);
            if (own == FixVector2.Zero)
            {
                return own;
            }

            // Position in cell units relative to the center of cell (0, 0).
            Fix ux = position.X / grid.CellSize - Fix.Half;
            Fix uy = position.Y / grid.CellSize - Fix.Half;
            int x0 = Fix.FloorToInt(ux);
            int y0 = Fix.FloorToInt(uy);
            Fix fx = ux - Fix.FromInt(x0);
            Fix fy = uy - Fix.FromInt(y0);

            FixVector2 sum = FixVector2.Zero;
            for (int dy = 0; dy <= 1; dy++)
            {
                for (int dx = 0; dx <= 1; dx++)
                {
                    var cell = new CellCoord(x0 + dx, y0 + dy);
                    if (!grid.IsWalkable(cell))
                    {
                        continue;
                    }
                    FixVector2 d = field.GetCellDirection(cell);
                    if (d == FixVector2.Zero || Fix.Abs(field.GetCellDistance(cell) - ownCost) > MaxBlendCostGap)
                    {
                        continue;
                    }
                    Fix weight = (dx == 1 ? fx : Fix.One - fx) * (dy == 1 ? fy : Fix.One - fy);
                    sum += d * weight;
                }
            }
            FixVector2 blended = sum.Normalized;
            return blended == FixVector2.Zero ? own : blended;
        }

        /// <summary>
        /// Push away from nearby friendly units of the same layer (ground or air), in world units per second.
        /// Each neighbor closer than the separation distance contributes a unit vector away from it scaled by
        /// how deep the overlap is. Moving neighbors and stopped neighbors (<paramref name="stopped"/>) are
        /// summed separately, each sum capped at full strength. The stopped sum is weakened by
        /// unitStoppedPushFactor, so it is slower than any unit and cannot hold one back short of its target
        /// (the unit may overlap stopped friends a little). It also adds a sideways push of the stopped sum's
        /// full size, perpendicular to <paramref name="heading"/> and toward the side the unit is already
        /// offset to (its left when exactly head-on), so an arriving unit slides around friends that are
        /// already fighting; being sideways, that part never slows the unit down. Two units on exactly the same spot are split along X by entity id
        /// (lower id goes left), so the result never depends on luck.
        /// </summary>
        internal static FixVector2 SeparationPush(List<Unit> units, FixVector2[] positions, bool[] stopped, int index,
            MatchRules rules, FixVector2 heading = default)
        {
            Unit self = units[index];
            Fix range = rules.UnitSeparationDistance;
            FixVector2 push = FixVector2.Zero;
            FixVector2 stoppedPush = FixVector2.Zero;
            for (int j = 0; j < units.Count; j++)
            {
                Unit other = units[j];
                if (j == index || other.Owner != self.Owner || other.IsFlying != self.IsFlying)
                {
                    continue;
                }
                FixVector2 delta = positions[index] - positions[j];
                if (Fix.Abs(delta.X) >= range || Fix.Abs(delta.Y) >= range)
                {
                    continue;
                }
                Fix distance = delta.Length;
                if (distance >= range)
                {
                    continue;
                }
                FixVector2 away = distance == Fix.Zero
                    ? (self.Id < other.Id ? -FixVector2.UnitX : FixVector2.UnitX)
                    : delta.Normalized;
                FixVector2 contribution = away * ((range - distance) / range);
                if (stopped[j])
                {
                    stoppedPush += contribution;
                }
                else
                {
                    push += contribution;
                }
            }
            stoppedPush = CapAtOne(stoppedPush);
            FixVector2 slide = FixVector2.Zero;
            if (stoppedPush != FixVector2.Zero && heading != FixVector2.Zero)
            {
                var left = new FixVector2(-heading.Y, heading.X);
                slide = (FixVector2.Dot(stoppedPush, left) >= Fix.Zero ? left : -left) * stoppedPush.Length;
            }
            return (CapAtOne(push) + stoppedPush * rules.UnitStoppedPushFactor + slide) * rules.UnitSeparationPushPerSecond;
        }

        private static FixVector2 CapAtOne(FixVector2 v) =>
            v != FixVector2.Zero && v.Length > Fix.One ? v.Normalized : v;

        // ------------------------------------------------------------ stepping

        internal static FixVector2 PerTick(FixVector2 perSecond, Fix ticksPerSecond) =>
            new FixVector2(perSecond.X / ticksPerSecond, perSecond.Y / ticksPerSecond);

        /// <summary>
        /// Takes the full step if it lands on a walkable cell without cutting a blocked corner. Otherwise
        /// takes the pure flow step, which the flow field guarantees is open (it points at an open neighbor
        /// and never cuts corners, and a step is under half a cell). Otherwise stays put.
        /// </summary>
        internal static FixVector2 GroundStep(Grid grid, FixVector2 from, FixVector2 step, FixVector2 flowStep)
        {
            FixVector2 to = from + step;
            if (CanMove(grid, from, to))
            {
                return to;
            }
            to = from + flowStep;
            if (CanMove(grid, from, to))
            {
                return to;
            }
            return from;
        }

        private static bool CanMove(Grid grid, FixVector2 from, FixVector2 to)
        {
            CellCoord a = grid.WorldToCell(from);
            CellCoord b = grid.WorldToCell(to);
            if (!grid.IsWalkable(b))
            {
                return false;
            }
            if (a.X != b.X && a.Y != b.Y)
            {
                return grid.IsWalkable(b.X, a.Y) && grid.IsWalkable(a.X, b.Y);
            }
            return true;
        }

        internal static FixVector2 ClampToMap(Grid grid, FixVector2 position)
        {
            Fix maxX = Fix.FromInt(grid.Width) * grid.CellSize - Fix.Epsilon;
            Fix maxY = Fix.FromInt(grid.Height) * grid.CellSize - Fix.Epsilon;
            return new FixVector2(Fix.Clamp(position.X, Fix.Zero, maxX), Fix.Clamp(position.Y, Fix.Zero, maxY));
        }

        // ------------------------------------------------------------ spawning

        /// <summary>
        /// Where the units of one deploy appear: offsets from a fixed square-spiral pattern (center, then
        /// the ring around it, front before sides before back), spaced by unitSpawnSpacing and rotated
        /// 180 degrees for player 1 so both players get the same formation facing the enemy. A position on
        /// a blocked cell is moved to the center of the nearest walkable cell within 3 cells, or to the
        /// deploy target (always walkable) if there is none.
        /// </summary>
        internal static FixVector2[] SpawnPositions(Grid grid, int owner, FixVector2 target, int count, Fix spacing)
        {
            var result = new FixVector2[count];
            IReadOnlyList<CellCoord> offsets = SpawnOffsets(count);
            Fix sign = owner == 0 ? Fix.One : -Fix.One;
            for (int i = 0; i < count; i++)
            {
                var offset = new FixVector2(Fix.FromInt(offsets[i].X) * spacing * sign, Fix.FromInt(offsets[i].Y) * spacing * sign);
                result[i] = NudgeToWalkable(grid, target + offset, target);
            }
            return result;
        }

        /// <summary>The first <paramref name="count"/> offsets of the spawn spiral (in spacing units, player 0 facing +Y).</summary>
        internal static IReadOnlyList<CellCoord> SpawnOffsets(int count)
        {
            var offsets = new List<CellCoord>(count);
            for (int ring = 0; offsets.Count < count; ring++)
            {
                var ringCells = new List<CellCoord>();
                for (int dy = ring; dy >= -ring; dy--)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        {
                            ringCells.Add(new CellCoord(dx, dy));
                        }
                    }
                }
                // Stable insertion sort by Manhattan length; the scan above already orders front-to-back, left-to-right.
                for (int i = 1; i < ringCells.Count; i++)
                {
                    CellCoord current = ringCells[i];
                    int j = i - 1;
                    while (j >= 0 && Manhattan(ringCells[j]) > Manhattan(current))
                    {
                        ringCells[j + 1] = ringCells[j];
                        j--;
                    }
                    ringCells[j + 1] = current;
                }
                for (int i = 0; i < ringCells.Count && offsets.Count < count; i++)
                {
                    offsets.Add(ringCells[i]);
                }
            }
            return offsets;
        }

        private static int Manhattan(CellCoord c) => Math.Abs(c.X) + Math.Abs(c.Y);

        private const int NudgeSearchRadius = 3;

        internal static FixVector2 NudgeToWalkable(Grid grid, FixVector2 position, FixVector2 fallback)
        {
            if (grid.IsWalkableAt(position))
            {
                return position;
            }
            CellCoord center = grid.WorldToCell(position);
            for (int ring = 1; ring <= NudgeSearchRadius; ring++)
            {
                bool found = false;
                FixVector2 best = fallback;
                Fix bestDistance = Fix.MaxValue;
                for (int dy = -ring; dy <= ring; dy++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        {
                            continue;
                        }
                        int x = center.X + dx, y = center.Y + dy;
                        if (!grid.IsWalkable(x, y))
                        {
                            continue;
                        }
                        FixVector2 candidate = grid.CellToWorld(x, y);
                        Fix distance = FixVector2.DistanceSquared(candidate, position);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = candidate;
                            found = true;
                        }
                    }
                }
                if (found)
                {
                    return best;
                }
            }
            return fallback;
        }
    }
}
