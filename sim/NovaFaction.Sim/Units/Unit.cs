using System;
using System.Collections.Generic;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Units
{
    public enum UnitState
    {
        /// <summary>Created at the end of the last tick; starts moving on the next one.</summary>
        Spawning = 0,
        /// <summary>Walking (or flying) toward its target or objective.</summary>
        Moving = 1,
        /// <summary>Standing still with nothing it can attack (for example no enemy structure left).</summary>
        Holding = 2,
        /// <summary>Its target is in range: standing still and attacking every attack interval.</summary>
        Attacking = 3,
        /// <summary>
        /// Standing next to a gold mine its player does not own, until the player owns it (or the capture stalls long
        /// enough that it gives up). Only units with canCapture do this, and only while no enemy they may attack is
        /// within their scan radius.
        /// </summary>
        Capturing = 4,
    }

    /// <summary>
    /// Timed stat modifiers on a unit (from a Rally). Active while <c>MatchState.Tick</c> is below
    /// <see cref="ExpireTick"/>; removed, and the unit's stats recomputed, as soon as the tick reaches it.
    /// </summary>
    public sealed class TimedBuff : IStateHashable
    {
        internal TimedBuff(IReadOnlyList<Modifier> modifiers, int expireTick)
        {
            Modifiers = modifiers;
            ExpireTick = expireTick;
        }

        public IReadOnlyList<Modifier> Modifiers { get; }

        /// <summary>The first tick the buff no longer applies on.</summary>
        public int ExpireTick { get; }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(ExpireTick);
            Modifier.AppendHash(ref hasher, Modifiers);
        }
    }

    /// <summary>
    /// A unit on the battlefield. The client may read everything; only the sim changes it.
    /// Its live stats (<see cref="MaxHp"/>, <see cref="Damage"/>, ...) are the definition's stats with the card level
    /// and modifiers applied; they are computed at spawn and whenever a modifier is added or removed.
    /// </summary>
    public sealed class Unit : IStateHashable
    {
        /// <summary>Objective value meaning "no objective".</summary>
        public const int NoObjective = -1;

        private static readonly Modifier[] NoModifiers = Array.Empty<Modifier>();

        private readonly Fix _levelFactor;
        private readonly IReadOnlyList<Modifier> _passive;

        internal Unit(int id, int owner, UnitDefinition definition, FixVector2 position, int level, Fix levelFactor,
            IReadOnlyList<Modifier> passive)
        {
            Id = id;
            Owner = owner;
            Definition = definition;
            Position = position;
            Level = level;
            _levelFactor = levelFactor;
            _passive = passive;
            State = UnitState.Spawning;
            Objective = NoObjective;
            Target = TargetRef.None;
            RecomputeStats();
            Hp = MaxHp;
        }

        /// <summary>Entity id: unique, assigned in creation order, never reused.</summary>
        public int Id { get; }

        /// <summary>0 or 1.</summary>
        public int Owner { get; }

        public UnitDefinition Definition { get; }

        public string DefinitionId => Definition.Id;

        /// <summary>Card level (1..maxUnitLevel) the unit was deployed at.</summary>
        public int Level { get; }

        /// <summary>World position of the unit's center.</summary>
        public FixVector2 Position { get; internal set; }

        /// <summary>Current hit points. Starts at <see cref="MaxHp"/>; the unit is removed at 0.</summary>
        public Fix Hp { get; internal set; }

        /// <summary>Effective maximum hit points.</summary>
        public Fix MaxHp { get; private set; }

        /// <summary>Effective damage per attack.</summary>
        public Fix Damage { get; private set; }

        /// <summary>Effective speed in world units per second.</summary>
        public Fix MoveSpeed { get; private set; }

        /// <summary>Effective reach (measured like <see cref="UnitDefinition.Range"/>).</summary>
        public Fix Range { get; private set; }

        /// <summary>Effective seconds between attacks.</summary>
        public Fix AttackIntervalSeconds { get; private set; }

        /// <summary>The Rally buff on the unit, or null.</summary>
        public TimedBuff? Buff { get; private set; }

        public UnitState State { get; internal set; }

        /// <summary>
        /// Index of the nearest standing enemy structure (by path for ground units), or <see cref="NoObjective"/>.
        /// This is where the unit goes when it has nothing nearer to fight. Re-selected every tick the unit is
        /// not locked onto a target, and for every unit whenever a structure is destroyed.
        /// </summary>
        public int Objective { get; internal set; }

        /// <summary>
        /// What the unit is attacking or chasing (an enemy unit or structure), or none. Enemy units stay the
        /// target until they die or leave the unit's aggro radius; structures stay the target while in range.
        /// </summary>
        public TargetRef Target { get; internal set; }

        /// <summary>Ticks until the unit may attack again (0 = ready). Counts down in every state.</summary>
        public int AttackCooldownTicks { get; internal set; }

        /// <summary>
        /// Consecutive ticks this unit has been Capturing while its capture did not move forward; at
        /// mineCaptureGiveUpSeconds it gives up. 0 whenever it is not Capturing.
        /// </summary>
        public int CaptureStallTicks { get; internal set; }

        /// <summary>The unit ignores mines while <c>MatchState.Tick</c> is below this (set when it gives up a capture).</summary>
        public int IgnoreMinesUntilTick { get; internal set; }

        public bool IsAlive => Hp > Fix.Zero;

        public bool IsFlying => Definition.IsFlying;

        internal bool IgnoresMines(int tick) => tick < IgnoreMinesUntilTick;

        /// <summary>Sets (or replaces) the timed buff and recomputes stats.</summary>
        internal void SetBuff(TimedBuff buff)
        {
            Buff = buff;
            RecomputeStats();
        }

        /// <summary>Removes the buff once the tick has reached its expiry, restoring the stats.</summary>
        internal void ExpireBuff(int tick)
        {
            if (Buff != null && tick >= Buff.ExpireTick)
            {
                Buff = null;
                RecomputeStats();
            }
        }

        /// <summary>Heals, never above max hp.</summary>
        internal void Heal(Fix amount)
        {
            Hp = Fix.Min(MaxHp, Hp + amount);
        }

        /// <summary>
        /// Recomputes the effective stats from the definition, level, the owner's leader passive and the buff. When max
        /// hp changes, current hp keeps its share of it (hp * newMax / oldMax, rounded to the nearest raw unit, and
        /// never below the smallest positive value for a living unit).
        /// </summary>
        private void RecomputeStats()
        {
            UnitDefinition d = Definition;
            IReadOnlyList<Modifier> timed = Buff?.Modifiers ?? NoModifiers;
            Fix oldMax = MaxHp;
            MaxHp = StatMath.Apply(ModifierStat.Hp, d.Hp * _levelFactor, d.Slot, _passive, timed);
            Damage = StatMath.Apply(ModifierStat.Damage, d.Damage * _levelFactor, d.Slot, _passive, timed);
            MoveSpeed = StatMath.Apply(ModifierStat.MoveSpeed, d.MoveSpeed, d.Slot, _passive, timed);
            Range = StatMath.Apply(ModifierStat.Range, d.Range, d.Slot, _passive, timed);
            AttackIntervalSeconds = StatMath.Apply(ModifierStat.AttackInterval, d.AttackIntervalSeconds, d.Slot,
                _passive, timed);
            if (oldMax > Fix.Zero && oldMax != MaxHp && Hp > Fix.Zero)
            {
                Hp = Fix.Clamp(ScaleHp(Hp, MaxHp, oldMax), Fix.Epsilon, MaxHp);
            }
        }

        /// <summary>hp * newMax / oldMax in one exact step, rounded to the nearest raw unit.</summary>
        internal static Fix ScaleHp(Fix hp, Fix newMax, Fix oldMax) =>
            Fix.FromRaw(UInt128Math.MulDivRound(hp.Raw, newMax.Raw, oldMax.Raw));

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Id);
            hasher.Add(Owner);
            hasher.Add(Definition.Id);
            hasher.Add(Position);
            hasher.Add(Hp);
            hasher.Add((int)State);
            hasher.Add(Objective);
            Target.AppendHash(ref hasher);
            hasher.Add(AttackCooldownTicks);
            hasher.Add(Level);
            hasher.Add(MaxHp);
            hasher.Add(Damage);
            hasher.Add(MoveSpeed);
            hasher.Add(Range);
            hasher.Add(AttackIntervalSeconds);
            hasher.Add(Buff != null);
            Buff?.AppendHash(ref hasher);
            hasher.Add(CaptureStallTicks);
            hasher.Add(IgnoreMinesUntilTick);
        }
    }

    /// <summary>A deploy that has been paid for and will create its units on <see cref="SpawnTick"/>.</summary>
    public sealed class PendingSpawn : IStateHashable
    {
        internal PendingSpawn(int spawnTick, int owner, UnitDefinition definition, FixVector2 target, int level = 1)
        {
            SpawnTick = spawnTick;
            Owner = owner;
            Definition = definition;
            Target = target;
            Level = level;
        }

        /// <summary>
        /// Deploy tick + spawn delay in ticks. The units appear at the end of the tick that brings
        /// <c>MatchState.Tick</c> to this value (a zero delay spawns during the deploy tick itself).
        /// </summary>
        public int SpawnTick { get; }

        public int Owner { get; }

        public UnitDefinition Definition { get; }

        /// <summary>Where the player dropped the card.</summary>
        public FixVector2 Target { get; }

        /// <summary>The card's level in the owner's deck.</summary>
        public int Level { get; }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(SpawnTick);
            hasher.Add(Owner);
            hasher.Add(Definition.Id);
            hasher.Add(Target);
            hasher.Add(Level);
        }
    }
}
