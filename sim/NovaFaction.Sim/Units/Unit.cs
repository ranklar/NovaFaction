using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Units
{
    public enum UnitState
    {
        /// <summary>Created at the end of the last tick; starts moving on the next one.</summary>
        Spawning = 0,
        /// <summary>Walking (or flying) toward its objective.</summary>
        Moving = 1,
        /// <summary>Within range of its objective (or has none) and standing still. Attacks come later.</summary>
        Holding = 2,
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
        }

        /// <summary>Entity id: unique, assigned in creation order, never reused.</summary>
        public int Id { get; }

        /// <summary>0 or 1.</summary>
        public int Owner { get; }

        public UnitDefinition Definition { get; }

        public string DefinitionId => Definition.Id;

        /// <summary>World position of the unit's center.</summary>
        public FixVector2 Position { get; internal set; }

        /// <summary>Current hit points. Starts at the definition's max; nothing lowers it yet.</summary>
        public Fix Hp { get; internal set; }

        public UnitState State { get; internal set; }

        /// <summary>
        /// Index of the enemy structure the unit is heading for or holding at, or <see cref="NoObjective"/>.
        /// Moving units re-select every tick; holding units re-select when this structure is destroyed.
        /// </summary>
        public int Objective { get; internal set; }

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
