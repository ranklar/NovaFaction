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
        /// Standing next to a gold mine its player does not own, until the player owns it. Only units with
        /// canCapture do this, and only while no enemy they may attack is within their scan radius.
        /// </summary>
        Capturing = 4,
    }

    /// <summary>
    /// A unit on the battlefield. The client may read everything; only the sim changes it.
    /// </summary>
    public sealed class Unit : IStateHashable
    {
        /// <summary>Objective value meaning "no objective".</summary>
        public const int NoObjective = -1;

        internal Unit(int id, int owner, UnitDefinition definition, FixVector2 position)
        {
            Id = id;
            Owner = owner;
            Definition = definition;
            Position = position;
            Hp = definition.Hp;
            State = UnitState.Spawning;
            Objective = NoObjective;
            Target = TargetRef.None;
        }

        /// <summary>Entity id: unique, assigned in creation order, never reused.</summary>
        public int Id { get; }

        /// <summary>0 or 1.</summary>
        public int Owner { get; }

        public UnitDefinition Definition { get; }

        public string DefinitionId => Definition.Id;

        /// <summary>World position of the unit's center.</summary>
        public FixVector2 Position { get; internal set; }

        /// <summary>Current hit points. Starts at the definition's max; the unit is removed at 0.</summary>
        public Fix Hp { get; internal set; }

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

        public bool IsAlive => Hp > Fix.Zero;

        public bool IsFlying => Definition.IsFlying;

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
        }
    }

    /// <summary>A deploy that has been paid for and will create its units on <see cref="SpawnTick"/>.</summary>
    public sealed class PendingSpawn : IStateHashable
    {
        internal PendingSpawn(int spawnTick, int owner, UnitDefinition definition, FixVector2 target)
        {
            SpawnTick = spawnTick;
            Owner = owner;
            Definition = definition;
            Target = target;
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

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(SpawnTick);
            hasher.Add(Owner);
            hasher.Add(Definition.Id);
            hasher.Add(Target);
        }
    }
}
