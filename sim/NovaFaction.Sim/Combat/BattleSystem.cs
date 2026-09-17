using System.Collections.Generic;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Economy;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Spells;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim.Combat
{
    /// <summary>What combat changed this tick, for match resolution.</summary>
    internal sealed class BattleOutcome
    {
        /// <summary>Structure HP each player removed this tick.</summary>
        public readonly Fix[] StructureDamage = { Fix.Zero, Fix.Zero };

        /// <summary>Structures destroyed this tick, in the order they fell.</summary>
        public readonly List<int> DestroyedStructures = new List<int>();
    }

    /// <summary>
    /// One tick of targeting, attacks, projectiles, spells, damage and movement. See docs/design.md ("Combat and
    /// match resolution", "Spells" and "Map gold"). Every decision reads the state as it was at the start of the tick (positions,
    /// targets, who is stopped), and all damage lands together, so the order units are processed in never
    /// changes the result. Order of work:
    /// <list type="number">
    /// <item>attack cooldowns count down;</item>
    /// <item>units choose a target and decide to attack, move, hold or capture a mine; structures choose a target;</item>
    /// <item>projectiles already in flight move, and those that arrive hit (or fizzle);</item>
    /// <item>active spell zones pulse, then pending spells that are due land;</item>
    /// <item>units then structures that are ready attack: melee hits land now, ranged attacks fire projectiles
    /// (which first move next tick);</item>
    /// <item>all hits are applied: HP, score, destroyed structures;</item>
    /// <item>moving units that are still alive step (stopping if they reach their target or a mine to capture);</item>
    /// <item>dead units are removed, targets that point at them are cleared, and if a structure fell every
    /// unit re-selects its objective.</item>
    /// </list>
    /// </summary>
    internal static class BattleSystem
    {
        private struct Hit
        {
            public int Owner;
            public TargetRef Target;
            public FixVector2 Impact;
            public Fix Damage;
            /// <summary>Damage to structures (Damage for attacks; reduced for spells).</summary>
            public Fix StructureDamage;
            public Fix SplashRadius;
            public TargetLayer CanHit;
        }

        internal static BattleOutcome Tick(MatchState state, MatchRules rules)
        {
            var outcome = new BattleOutcome();
            var ctx = new Context(state, rules);
            ctx.CountDownCooldowns();
            ctx.DecideUnits();
            ctx.DecideStructures();
            ctx.AdvanceProjectiles();
            ctx.ResolveSpells();
            ctx.ResolveAbilityStrikes();
            ctx.Attack();
            ctx.ApplyHits(outcome);
            ctx.MoveUnits();
            ctx.CleanUp(outcome);
            return outcome;
        }

        private sealed class Context
        {
            private readonly MatchState _state;
            private readonly MatchRules _rules;
            private readonly MapState _map;
            private readonly Grid _grid;
            private readonly List<Unit> _units;
            private readonly int _n;
            private readonly Fix _ticksPerSecond;

            // Start-of-tick snapshot.
            private readonly FixVector2[] _start;
            private readonly TargetRef[] _startTarget;
            private readonly bool[] _stopped;
            // [owner][unit index] = units of that owner targeting the unit. Starts from the start-of-tick targets and
            // is updated as each unit (in id order) drops or picks an enemy unit, so friends deciding later in the
            // same tick see the claim. Each player only sees their own claims, so neither player gains from the order.
            private readonly int[][] _crowd;

            // Decisions.
            private readonly FixVector2[] _velocity;
            private readonly FlowField?[] _flow;
            private readonly bool[] _moving;
            // The unit found no enemy to fight this tick, so passing a mine may stop it (it can capture).
            private readonly bool[] _mayCapture;
            private readonly bool[] _attackNow;
            private readonly bool[] _structureFires;

            private readonly List<Hit> _hits = new List<Hit>();

            public Context(MatchState state, MatchRules rules)
            {
                _state = state;
                _rules = rules;
                _map = state.Map;
                _grid = state.Map.Grid;
                _units = state.UnitList;
                _n = _units.Count;
                _ticksPerSecond = Fix.FromInt(rules.TicksPerSecond);

                _start = new FixVector2[_n];
                _startTarget = new TargetRef[_n];
                _stopped = new bool[_n];
                _crowd = new[] { new int[_n], new int[_n] };
                for (int i = 0; i < _n; i++)
                {
                    Unit u = _units[i];
                    _start[i] = u.Position;
                    _startTarget[i] = u.Target;
                    _stopped[i] = u.State == UnitState.Attacking || u.State == UnitState.Holding
                        || u.State == UnitState.Capturing;
                }
                for (int i = 0; i < _n; i++)
                {
                    if (_startTarget[i].Kind == TargetKind.Unit)
                    {
                        int k = state.IndexOfUnit(_startTarget[i].Id);
                        if (k >= 0)
                        {
                            _crowd[_units[i].Owner][k]++;
                        }
                    }
                }

                _velocity = new FixVector2[_n];
                _flow = new FlowField?[_n];
                _moving = new bool[_n];
                _mayCapture = new bool[_n];
                _attackNow = new bool[_n];
                _structureFires = new bool[state.Structures.Count];
            }

            // ------------------------------------------------------------ cooldowns

            public void CountDownCooldowns()
            {
                foreach (Unit u in _units)
                {
                    if (u.AttackCooldownTicks > 0)
                    {
                        u.AttackCooldownTicks--;
                    }
                }
                foreach (StructureState s in _state.Structures)
                {
                    if (!s.IsDestroyed && s.AttackCooldownTicks > 0)
                    {
                        s.AttackCooldownTicks--;
                    }
                }
            }

            // ------------------------------------------------------------ unit decisions

            public void DecideUnits()
            {
                for (int i = 0; i < _n; i++)
                {
                    DecideUnit(i);
                }
            }

            private void DecideUnit(int i)
            {
                Unit u = _units[i];
                UnitDefinition def = u.Definition;
                FixVector2 pos = _start[i];
                if (u.State == UnitState.Spawning)
                {
                    u.State = UnitState.Moving; // acts from its first tick on the field
                }

                TargetRef target = KeepsTarget(i, _startTarget[i]) ? _startTarget[i] : TargetRef.None;
                if (target.IsNone)
                {
                    Claim(u.Owner, _startTarget[i], -1);
                    u.Objective = UnitMovement.SelectObjective(_map, u, pos);
                    target = Scan(i);
                    if (target.IsNone && def.CanCapture)
                    {
                        // No enemy to fight: a capturer next to a mine its player does not own stops (or stays) there.
                        if (!u.IgnoresMines(_state.Tick) && IsAtUncapturedMine(u.Owner, pos))
                        {
                            u.Target = TargetRef.None;
                            u.State = UnitState.Capturing;
                            return;
                        }
                        _mayCapture[i] = !u.IgnoresMines(_state.Tick);
                    }
                    if (target.IsNone && u.Objective != Unit.NoObjective && TargetRules.CanHitStructures(def.Targets))
                    {
                        target = TargetRef.Structure(u.Objective);
                    }
                    Claim(u.Owner, target, +1);
                }
                u.Target = target;

                if (!target.IsNone && IsInRange(i, pos, target))
                {
                    u.State = UnitState.Attacking;
                    _attackNow[i] = u.AttackCooldownTicks == 0;
                    return;
                }

                // Nothing to attack in range: walk to the target, or to the objective if there is no target
                // (a unit that cannot hit structures still heads for one and waits there).
                TargetRef destination = !target.IsNone ? target
                    : u.Objective != Unit.NoObjective ? TargetRef.Structure(u.Objective)
                    : TargetRef.None;
                if (destination.IsNone || (target.IsNone && IsInRange(i, pos, destination)))
                {
                    u.State = UnitState.Holding;
                    return;
                }

                u.State = UnitState.Moving;
                FixVector2 heading = Direction(i, destination);
                _velocity[i] = heading * u.MoveSpeed
                    + UnitMovement.SeparationPush(_units, _start, _stopped, i, _rules, heading);
                _moving[i] = true;
            }

            /// <summary>Within mineCaptureRadius of a mine the player does not own (neutral or the enemy's).</summary>
            private bool IsAtUncapturedMine(int player, FixVector2 position)
            {
                foreach (MineState mine in _state.Mines)
                {
                    if (mine.Owner != player && FixVector2.Distance(position, mine.Position) <= _rules.MineCaptureRadius)
                    {
                        return true;
                    }
                }
                return false;
            }

            private void Claim(int owner, TargetRef target, int change)
            {
                if (target.Kind == TargetKind.Unit)
                {
                    int k = _state.IndexOfUnit(target.Id);
                    if (k >= 0)
                    {
                        _crowd[owner][k] += change;
                    }
                }
            }

            /// <summary>
            /// A unit keeps an enemy unit as its target while that unit lives, can still be hit and stays within
            /// the scan radius (and, for ground attackers out of range, stands where it can be walked to). It keeps
            /// a structure only while the structure stands and is in range; otherwise it looks again each tick.
            /// </summary>
            private bool KeepsTarget(int i, TargetRef target)
            {
                Unit u = _units[i];
                switch (target.Kind)
                {
                    case TargetKind.Structure:
                        return !_map.IsDestroyed(target.Id)
                            && TargetRules.CanHitStructures(u.Definition.Targets)
                            && IsInRange(i, _start[i], target);
                    case TargetKind.Unit:
                        int k = _state.IndexOfUnit(target.Id);
                        if (k < 0 || !_units[k].IsAlive || u.Definition.TargetPriority != TargetPriority.Any
                            || !TargetRules.CanHitUnit(u.Definition.Targets, _units[k].IsFlying))
                        {
                            return false;
                        }
                        Fix distance = FixVector2.Distance(_start[i], _start[k]);
                        return distance <= ScanRadius(u) && CanPursue(i, k, distance);
                    default:
                        return false;
                }
            }

            private Fix ScanRadius(Unit u) => Fix.Max(_rules.AggroRadius, u.Range);

            /// <summary>
            /// A ground attacker can go after an enemy unit that is already in range, or that stands on a walkable
            /// cell it can reach. Flyers can go anywhere.
            /// </summary>
            private bool CanPursue(int i, int k, Fix distance)
            {
                Unit u = _units[i];
                if (u.IsFlying || distance <= u.Range)
                {
                    return true;
                }
                CellCoord cell = _grid.WorldToCell(_start[k]);
                return _grid.IsWalkable(cell) && _map.FlowFields.GetForCell(cell).GetDistance(_start[i]) != Fix.MaxValue;
            }

            /// <summary>
            /// The nearest enemy the unit may attack within its scan radius: enemy units it can hit (unless it only
            /// targets structures), then standing enemy structures. Distance is a straight line, to the unit's
            /// center or the footprint's nearest point. Melee units add the crowd penalty for every friend already
            /// on an enemy unit (including friends earlier in id order that picked it this tick). Ties: units before structures, then lower id / index.
            /// </summary>
            private TargetRef Scan(int i)
            {
                Unit u = _units[i];
                UnitDefinition def = u.Definition;
                FixVector2 pos = _start[i];
                Fix radius = ScanRadius(u);
                TargetRef best = TargetRef.None;
                Fix bestScore = Fix.MaxValue;

                if (def.TargetPriority == TargetPriority.Any)
                {
                    for (int k = 0; k < _n; k++)
                    {
                        Unit other = _units[k];
                        if (other.Owner == u.Owner || !other.IsAlive || !TargetRules.CanHitUnit(def.Targets, other.IsFlying))
                        {
                            continue;
                        }
                        FixVector2 delta = _start[k] - pos;
                        if (Fix.Abs(delta.X) > radius || Fix.Abs(delta.Y) > radius)
                        {
                            continue;
                        }
                        Fix distance = delta.Length;
                        if (distance > radius)
                        {
                            continue;
                        }
                        Fix score = distance;
                        if (!def.IsRanged)
                        {
                            score += _rules.MeleeTargetCrowdPenalty * Fix.FromInt(_crowd[u.Owner][k]);
                        }
                        if (score < bestScore && CanPursue(i, k, distance))
                        {
                            bestScore = score;
                            best = TargetRef.Unit(other.Id);
                        }
                    }
                }

                if (TargetRules.CanHitStructures(def.Targets))
                {
                    foreach (StructureState s in _state.Structures)
                    {
                        if (s.Owner == u.Owner || s.IsDestroyed)
                        {
                            continue;
                        }
                        Fix distance = UnitMovement.DistanceToFootprint(_grid, pos, s.Index);
                        if (distance <= radius && distance < bestScore)
                        {
                            bestScore = distance;
                            best = TargetRef.Structure(s.Index);
                        }
                    }
                }
                return best;
            }

            private bool IsInRange(int i, FixVector2 position, TargetRef target)
            {
                Fix range = _units[i].Range;
                if (target.Kind == TargetKind.Structure)
                {
                    return UnitMovement.DistanceToFootprint(_grid, position, target.Id) <= range;
                }
                int k = _state.IndexOfUnit(target.Id);
                return k >= 0 && FixVector2.Distance(position, _start[k]) <= range;
            }

            /// <summary>
            /// Flyers head straight for the destination. Ground units follow the structure's flow field, or for an
            /// enemy unit the flow field to its cell, going straight once they are in the same or a neighboring cell.
            /// </summary>
            private FixVector2 Direction(int i, TargetRef destination)
            {
                Unit u = _units[i];
                FixVector2 pos = _start[i];
                if (destination.Kind == TargetKind.Structure)
                {
                    if (u.IsFlying)
                    {
                        return (UnitMovement.FootprintCenter(_grid, destination.Id) - pos).Normalized;
                    }
                    FlowField field = _map.FlowFields.Get(destination.Id);
                    _flow[i] = field;
                    return UnitMovement.BlendedFlowDirection(_grid, field, pos);
                }

                FixVector2 targetPos = _start[_state.IndexOfUnit(destination.Id)];
                if (u.IsFlying)
                {
                    return (targetPos - pos).Normalized;
                }
                CellCoord own = _grid.WorldToCell(pos);
                CellCoord cell = _grid.WorldToCell(targetPos);
                FlowField cellField = _map.FlowFields.GetForCell(cell);
                _flow[i] = cellField;
                if (System.Math.Abs(own.X - cell.X) <= 1 && System.Math.Abs(own.Y - cell.Y) <= 1)
                {
                    return (targetPos - pos).Normalized;
                }
                return UnitMovement.BlendedFlowDirection(_grid, cellField, pos);
            }

            // ------------------------------------------------------------ structure decisions

            /// <summary>
            /// A structure keeps shooting the same enemy unit while it stays in range and can be hit; otherwise it
            /// picks the nearest enemy unit in range (distance from the footprint), ties to the lower id.
            /// </summary>
            public void DecideStructures()
            {
                foreach (StructureState s in _state.Structures)
                {
                    if (s.IsDestroyed)
                    {
                        s.TargetUnitId = StructureState.NoTarget;
                        continue;
                    }
                    int current = s.TargetUnitId == StructureState.NoTarget ? -1 : _state.IndexOfUnit(s.TargetUnitId);
                    int chosen = current >= 0 && StructureCanShoot(s, current) >= Fix.Zero ? current : -1;
                    if (chosen < 0)
                    {
                        Fix best = Fix.MaxValue;
                        for (int k = 0; k < _n; k++)
                        {
                            Fix distance = StructureCanShoot(s, k);
                            if (distance >= Fix.Zero && distance < best)
                            {
                                best = distance;
                                chosen = k;
                            }
                        }
                    }
                    s.TargetUnitId = chosen < 0 ? StructureState.NoTarget : _units[chosen].Id;
                    _structureFires[s.Index] = chosen >= 0 && s.AttackCooldownTicks == 0;
                }
            }

            /// <summary>Distance from the footprint if the structure may shoot unit k now, else -1.</summary>
            private Fix StructureCanShoot(StructureState s, int k)
            {
                Unit target = _units[k];
                if (target.Owner == s.Owner || !target.IsAlive || !TargetRules.CanHitUnit(s.Stats.Targets, target.IsFlying))
                {
                    return -Fix.One;
                }
                Fix distance = UnitMovement.DistanceToFootprint(_grid, _start[k], s.Index);
                return distance <= s.Stats.Range ? distance : -Fix.One;
            }

            // ------------------------------------------------------------ projectiles and attacks

            /// <summary>Moves every projectile already in flight; arrivals become hits (live target) or fizzle.</summary>
            public void AdvanceProjectiles()
            {
                List<Projectile> projectiles = _state.ProjectileList;
                int kept = 0;
                for (int p = 0; p < projectiles.Count; p++)
                {
                    Projectile shot = projectiles[p];
                    bool targetAlive;
                    if (shot.Target.Kind == TargetKind.Unit)
                    {
                        int k = _state.IndexOfUnit(shot.Target.Id);
                        targetAlive = k >= 0 && _units[k].IsAlive;
                        if (targetAlive)
                        {
                            shot.AimPoint = _start[k];
                        }
                    }
                    else
                    {
                        targetAlive = !_map.IsDestroyed(shot.Target.Id);
                    }

                    FixVector2 toAim = shot.AimPoint - shot.Position;
                    if (toAim.Length > shot.SpeedPerTick)
                    {
                        shot.Position += toAim.Normalized * shot.SpeedPerTick;
                        projectiles[kept++] = shot;
                        continue;
                    }
                    shot.Position = shot.AimPoint;
                    if (targetAlive)
                    {
                        _hits.Add(new Hit
                        {
                            Owner = shot.Owner,
                            Target = shot.Target,
                            Impact = shot.AimPoint,
                            Damage = shot.Damage,
                            StructureDamage = shot.Damage,
                            SplashRadius = shot.SplashRadius,
                            CanHit = shot.CanHit,
                        });
                    }
                    // Arrived: removed either way (a dead target means the shot fizzles).
                }
                projectiles.RemoveRange(kept, projectiles.Count - kept);
            }

            /// <summary>
            /// Active zones due a pulse hit first (id order), then pending spells whose land tick has come (id order).
            /// An instant spell is then gone; a landed zone joins the zone list (its landing hit is its first pulse).
            /// A zone is removed after its last active tick. Spell hits damage every enemy the spell may hit around
            /// its target, structures at the reduced structure damage.
            /// </summary>
            public void ResolveSpells()
            {
                int tick = _state.Tick;
                List<SpellInstance> zones = _state.SpellZoneList;
                foreach (SpellInstance zone in zones)
                {
                    if (zone.PulsesOn(tick))
                    {
                        AddSpellHit(zone);
                    }
                }

                List<SpellInstance> pending = _state.PendingSpellList;
                int kept = 0;
                for (int p = 0; p < pending.Count; p++)
                {
                    SpellInstance spell = pending[p];
                    if (spell.LandTick > tick)
                    {
                        pending[kept++] = spell;
                        continue;
                    }
                    AddSpellHit(spell);
                    if (spell.IsZone)
                    {
                        int at = zones.Count;
                        while (at > 0 && zones[at - 1].Id > spell.Id)
                        {
                            at--;
                        }
                        zones.Insert(at, spell); // keeps id order
                    }
                }
                pending.RemoveRange(kept, pending.Count - kept);
                zones.RemoveAll(z => z.EndTick <= tick + 1); // this was its last active tick
            }

            private void AddSpellHit(SpellInstance spell)
            {
                SpellDefinition def = spell.Definition;
                _hits.Add(new Hit
                {
                    Owner = spell.Owner,
                    Target = TargetRef.None,
                    Impact = spell.Target,
                    Damage = spell.Damage,
                    StructureDamage = spell.StructureDamage,
                    SplashRadius = def.Radius,
                    CanHit = def.Targets,
                });
            }

            /// <summary>Leader AreaDamage casts from this tick's commands hit like an instant spell of both layers.</summary>
            public void ResolveAbilityStrikes()
            {
                foreach (AbilityStrike strike in _state.AbilityStrikes)
                {
                    _hits.Add(new Hit
                    {
                        Owner = strike.Owner,
                        Target = TargetRef.None,
                        Impact = strike.Center,
                        Damage = strike.Damage,
                        StructureDamage = strike.StructureDamage,
                        SplashRadius = strike.Radius,
                        CanHit = TargetLayer.Both,
                    });
                }
                _state.AbilityStrikes.Clear();
            }

            public void Attack()
            {
                int tps = _rules.TicksPerSecond;
                for (int i = 0; i < _n; i++)
                {
                    if (!_attackNow[i])
                    {
                        continue;
                    }
                    Unit u = _units[i];
                    UnitDefinition def = u.Definition;
                    u.AttackCooldownTicks = TargetRules.IntervalTicks(u.AttackIntervalSeconds, tps);
                    FixVector2 aim = AimPoint(_start[i], u.Target);
                    if (def.IsRanged)
                    {
                        Fire(u.Owner, _start[i], u.Target, aim, def.ProjectileSpeed, u.Damage, def.SplashRadius, def.Targets);
                    }
                    else
                    {
                        _hits.Add(new Hit
                        {
                            Owner = u.Owner,
                            Target = u.Target,
                            Impact = aim,
                            Damage = u.Damage,
                            StructureDamage = u.Damage,
                            SplashRadius = def.SplashRadius,
                            CanHit = def.Targets,
                        });
                    }
                }

                foreach (StructureState s in _state.Structures)
                {
                    if (!_structureFires[s.Index])
                    {
                        continue;
                    }
                    StructureStats stats = s.Stats;
                    s.AttackCooldownTicks = TargetRules.IntervalTicks(stats.AttackIntervalSeconds, tps);
                    TargetRef target = TargetRef.Unit(s.TargetUnitId);
                    FixVector2 from = UnitMovement.FootprintCenter(_grid, s.Index);
                    Fire(s.Owner, from, target, AimPoint(from, target), stats.ProjectileSpeed, s.Damage, Fix.Zero,
                        stats.Targets);
                }
            }

            /// <summary>An enemy unit's position, or the point of a structure's footprint nearest the attacker.</summary>
            private FixVector2 AimPoint(FixVector2 from, TargetRef target) =>
                target.Kind == TargetKind.Structure
                    ? UnitMovement.ClosestPointOnFootprint(_grid, from, target.Id)
                    : _start[_state.IndexOfUnit(target.Id)];

            private void Fire(int owner, FixVector2 from, TargetRef target, FixVector2 aim, Fix speedPerSecond,
                Fix damage, Fix splash, TargetLayer canHit)
            {
                var shot = new Projectile(_state.NextProjectileId++, owner, from, target, aim,
                    speedPerSecond / _ticksPerSecond, damage, splash, canHit);
                _state.ProjectileList.Add(shot); // ids only grow, so the list stays sorted
            }

            // ------------------------------------------------------------ damage

            /// <summary>
            /// Applies every hit in the order it was produced (projectile arrivals, spells, unit attacks, structure
            /// shots). Splash damages every enemy the attacker could hit whose center (or footprint) is
            /// within the radius of the impact point, measured at start-of-tick positions.
            /// </summary>
            public void ApplyHits(BattleOutcome outcome)
            {
                foreach (Hit hit in _hits)
                {
                    if (hit.SplashRadius == Fix.Zero)
                    {
                        if (hit.Target.Kind == TargetKind.Unit)
                        {
                            int k = _state.IndexOfUnit(hit.Target.Id);
                            if (k >= 0)
                            {
                                DamageUnit(_units[k], hit.Damage);
                            }
                        }
                        else
                        {
                            DamageStructure(hit.Target.Id, hit.Owner, hit.StructureDamage, outcome);
                        }
                        continue;
                    }

                    Fix radius = hit.SplashRadius;
                    for (int k = 0; k < _n; k++)
                    {
                        Unit other = _units[k];
                        if (other.Owner != hit.Owner && TargetRules.CanHitUnit(hit.CanHit, other.IsFlying)
                            && FixVector2.Distance(_start[k], hit.Impact) <= radius)
                        {
                            DamageUnit(other, hit.Damage);
                        }
                    }
                    if (TargetRules.CanHitStructures(hit.CanHit))
                    {
                        foreach (StructureState s in _state.Structures)
                        {
                            if (s.Owner != hit.Owner && UnitMovement.DistanceToFootprint(_grid, hit.Impact, s.Index) <= radius)
                            {
                                DamageStructure(s.Index, hit.Owner, hit.StructureDamage, outcome);
                            }
                        }
                    }
                }
            }

            private static void DamageUnit(Unit unit, Fix damage)
            {
                unit.Hp = Fix.Max(Fix.Zero, unit.Hp - damage);
            }

            /// <summary>
            /// Removes up to the structure's remaining HP, credits it to the attacker's score, and destroys the
            /// structure at 0 HP (adding the destruction bonus, opening the footprint and unlocking deploy zones).
            /// </summary>
            private void DamageStructure(int index, int attacker, Fix damage, BattleOutcome outcome)
            {
                StructureState s = _state.Structures[index];
                if (s.IsDestroyed || s.Owner == attacker)
                {
                    return;
                }
                Fix removed = Fix.Min(damage, s.Hp);
                if (removed <= Fix.Zero)
                {
                    return;
                }
                PlayerState player = _state.GetPlayer(attacker);
                s.Hp -= removed;
                player.Score += removed;
                outcome.StructureDamage[attacker] += removed;
                if (s.Hp == Fix.Zero)
                {
                    player.Score += s.Stats.DestructionBonus;
                    _map.DestroyStructure(index);
                    s.TargetUnitId = StructureState.NoTarget;
                    s.AttackCooldownTicks = 0;
                    outcome.DestroyedStructures.Add(index);
                }
            }

            // ------------------------------------------------------------ movement and clean-up

            public void MoveUnits()
            {
                for (int i = 0; i < _n; i++)
                {
                    Unit u = _units[i];
                    if (!_moving[i] || !u.IsAlive)
                    {
                        continue;
                    }
                    FixVector2 step = UnitMovement.PerTick(_velocity[i], _ticksPerSecond);
                    if (u.IsFlying)
                    {
                        u.Position = UnitMovement.ClampToMap(_grid, _start[i] + step);
                    }
                    else
                    {
                        FixVector2 flowOnly = _flow[i]!.GetDirection(_start[i]) * u.MoveSpeed;
                        u.Position = UnitMovement.GroundStep(_grid, _start[i], step,
                            UnitMovement.PerTick(flowOnly, _ticksPerSecond));
                    }

                    // Arrived this tick: stop now, attack from next tick.
                    TargetRef target = u.Target;
                    if (!target.IsNone && !IsDeadOrDestroyed(target) && IsInRange(i, u.Position, target))
                    {
                        u.State = UnitState.Attacking;
                    }
                    else if (target.IsNone && u.Objective != Unit.NoObjective
                        && IsInRange(i, u.Position, TargetRef.Structure(u.Objective)))
                    {
                        u.State = UnitState.Holding;
                    }
                    else if (_mayCapture[i] && IsAtUncapturedMine(u.Owner, u.Position))
                    {
                        // Passed within reach of a mine: stop now, capture from next tick.
                        u.Target = TargetRef.None;
                        u.State = UnitState.Capturing;
                    }
                }
            }

            private bool IsDeadOrDestroyed(TargetRef target)
            {
                if (target.Kind == TargetKind.Structure)
                {
                    return _map.IsDestroyed(target.Id);
                }
                Unit? unit = _state.FindUnit(target.Id);
                return unit == null || !unit.IsAlive;
            }

            public void CleanUp(BattleOutcome outcome)
            {
                bool anyDead = false;
                foreach (Unit u in _units)
                {
                    anyDead |= !u.IsAlive;
                }
                if (anyDead)
                {
                    _units.RemoveAll(u => !u.IsAlive); // keeps id order
                }

                bool structureFell = outcome.DestroyedStructures.Count > 0;
                if (!anyDead && !structureFell)
                {
                    return;
                }
                foreach (Unit u in _units)
                {
                    if (u.Target.Kind == TargetKind.Unit ? _state.FindUnit(u.Target.Id) == null
                        : u.Target.Kind == TargetKind.Structure && _map.IsDestroyed(u.Target.Id))
                    {
                        u.Target = TargetRef.None;
                    }
                    if (structureFell)
                    {
                        u.Objective = UnitMovement.SelectObjective(_map, u, u.Position);
                    }
                }
                foreach (StructureState s in _state.Structures)
                {
                    if (s.TargetUnitId != StructureState.NoTarget && _state.FindUnit(s.TargetUnitId) == null)
                    {
                        s.TargetUnitId = StructureState.NoTarget;
                    }
                }
            }
        }
    }
}
