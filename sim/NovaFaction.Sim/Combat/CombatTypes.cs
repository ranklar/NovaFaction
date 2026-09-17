using System;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Combat
{
    public enum TargetKind
    {
        None = 0,
        Unit = 1,
        Structure = 2,
    }

    /// <summary>What an attacker or projectile is aimed at: a unit (by entity id) or a structure (by index).</summary>
    public readonly struct TargetRef : IEquatable<TargetRef>
    {
        public static readonly TargetRef None = default;

        private TargetRef(TargetKind kind, int id)
        {
            Kind = kind;
            Id = id;
        }

        public TargetKind Kind { get; }

        /// <summary>Unit id for <see cref="TargetKind.Unit"/>, structure index for <see cref="TargetKind.Structure"/>, else 0.</summary>
        public int Id { get; }

        public bool IsNone => Kind == TargetKind.None;

        public static TargetRef Unit(int unitId) => new TargetRef(TargetKind.Unit, unitId);

        public static TargetRef Structure(int structureIndex) => new TargetRef(TargetKind.Structure, structureIndex);

        public static bool operator ==(TargetRef a, TargetRef b) => a.Kind == b.Kind && a.Id == b.Id;

        public static bool operator !=(TargetRef a, TargetRef b) => !(a == b);

        public bool Equals(TargetRef other) => this == other;

        public override bool Equals(object? obj) => obj is TargetRef other && this == other;

        public override int GetHashCode() => unchecked(((int)Kind * 397) ^ Id);

        public override string ToString() => Kind == TargetKind.None ? "none" : Kind + " " + Id;

        internal void AppendHash(ref StateHasher h)
        {
            h.Add((int)Kind);
            h.Add(Id);
        }
    }

    /// <summary>Who can hit what: Ground attackers hit ground units and structures, Air attackers only flyers.</summary>
    public static class TargetRules
    {
        public static bool CanHitUnit(TargetLayer attacker, bool targetIsFlying) =>
            attacker == TargetLayer.Both || (attacker == TargetLayer.Air) == targetIsFlying;

        /// <summary>Structures stand on the ground.</summary>
        public static bool CanHitStructures(TargetLayer attacker) => attacker != TargetLayer.Air;

        /// <summary>An attack interval in whole ticks: rounded to the nearest tick, at least one.</summary>
        public static int IntervalTicks(Fix seconds, int ticksPerSecond) =>
            Math.Max(1, Fix.RoundToInt(seconds * Fix.FromInt(ticksPerSecond)));
    }

    /// <summary>The live combat state of one Keep or forward tower. Destroyed/standing lives in the map.</summary>
    public sealed class StructureState : IStateHashable
    {
        /// <summary><see cref="TargetUnitId"/> value meaning "no target" (unit ids start at 1).</summary>
        public const int NoTarget = 0;

        private readonly Grid _grid;

        internal StructureState(StructureDefinition definition, StructureStats stats, Grid grid)
        {
            Definition = definition;
            Stats = stats;
            _grid = grid;
            Hp = stats.Hp;
        }

        public int Index => Definition.Index;

        public int Owner => Definition.Owner;

        public StructureDefinition Definition { get; }

        public StructureStats Stats { get; }

        /// <summary>Current hit points; 0 once destroyed by damage.</summary>
        public Fix Hp { get; internal set; }

        public bool IsDestroyed => _grid.IsStructureDestroyed(Definition.Index);

        /// <summary>Ticks until the structure may attack again (0 = ready).</summary>
        public int AttackCooldownTicks { get; internal set; }

        /// <summary>Id of the enemy unit the structure is shooting at, or <see cref="NoTarget"/>.</summary>
        public int TargetUnitId { get; internal set; }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Definition.Index);
            hasher.Add(Hp);
            hasher.Add(AttackCooldownTicks);
            hasher.Add(TargetUnitId);
        }
    }

    /// <summary>
    /// A shot in flight. It homes on its target and always hits when it arrives (Clash Royale style). If the
    /// target is gone it flies on to the target's last known position and fizzles there without damage.
    /// Damage, splash and what it may hit are copied from the attacker when fired, so the shot does not
    /// depend on the attacker surviving.
    /// </summary>
    public sealed class Projectile : IStateHashable
    {
        internal Projectile(int id, int owner, FixVector2 position, TargetRef target, FixVector2 aimPoint,
            Fix speedPerTick, Fix damage, Fix splashRadius, TargetLayer canHit)
        {
            Id = id;
            Owner = owner;
            Position = position;
            Target = target;
            AimPoint = aimPoint;
            SpeedPerTick = speedPerTick;
            Damage = damage;
            SplashRadius = splashRadius;
            CanHit = canHit;
        }

        /// <summary>Entity id: unique among projectiles, assigned in firing order, never reused.</summary>
        public int Id { get; }

        /// <summary>The player who fired it.</summary>
        public int Owner { get; }

        public FixVector2 Position { get; internal set; }

        public TargetRef Target { get; }

        /// <summary>Where it is flying: the target's position (or its footprint's nearest point) last time it was seen.</summary>
        public FixVector2 AimPoint { get; internal set; }

        public Fix SpeedPerTick { get; }

        public Fix Damage { get; }

        public Fix SplashRadius { get; }

        /// <summary>The attacker's target layer; splash only damages what the attacker could hit.</summary>
        public TargetLayer CanHit { get; }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Id);
            hasher.Add(Owner);
            hasher.Add(Position);
            Target.AppendHash(ref hasher);
            hasher.Add(AimPoint);
            hasher.Add(SpeedPerTick);
            hasher.Add(Damage);
            hasher.Add(SplashRadius);
            hasher.Add((int)CanHit);
        }
    }

    /// <summary>Why the match ended.</summary>
    public enum EndReason
    {
        None = 0,
        /// <summary>A Keep was destroyed; its attacker wins at once.</summary>
        KeepDestroyed = 1,
        /// <summary>The regulation clock ran out and one player had the higher score.</summary>
        Score = 2,
        /// <summary>A player dealt the first structure damage in sudden death.</summary>
        FirstDamage = 3,
        /// <summary>Decided by the tie-break list; see <see cref="TieBreakRule"/>.</summary>
        TieBreak = 4,
    }

    /// <summary>The tie-break rule that decided a match (design doc order).</summary>
    public enum TieBreakRule
    {
        None = 0,
        /// <summary>More enemy structures destroyed.</summary>
        StructuresDestroyed = 1,
        /// <summary>Higher HP on the player's own weakest structure (destroyed counts as 0).</summary>
        WeakestStructureHp = 2,
        /// <summary>More gold collected from mines and chests.</summary>
        GoldCollected = 3,
        /// <summary>A coin flip from the match RNG.</summary>
        CoinFlip = 4,
    }
}
