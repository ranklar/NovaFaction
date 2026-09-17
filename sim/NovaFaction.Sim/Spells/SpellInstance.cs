using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Spells
{
    /// <summary>
    /// One cast spell. It waits in <see cref="MatchState.PendingSpells"/> until <see cref="LandTick"/>, then hits.
    /// An instant spell is gone after that; a zone spell moves to <see cref="MatchState.SpellZones"/> and pulses
    /// every zoneTickSeconds until <see cref="EndTick"/>. The client may read everything; only the sim changes it.
    /// </summary>
    public sealed class SpellInstance : IStateHashable
    {
        internal SpellInstance(int id, int owner, SpellDefinition definition, FixVector2 target, int landTick,
            int durationTicks, int pulseIntervalTicks)
        {
            Id = id;
            Owner = owner;
            Definition = definition;
            Target = target;
            LandTick = landTick;
            EndTick = landTick + durationTicks;
            PulseIntervalTicks = pulseIntervalTicks;
        }

        /// <summary>Entity id: unique among spells, assigned in cast order, never reused.</summary>
        public int Id { get; }

        /// <summary>The player who cast it. Spells never hurt their caster's units or structures.</summary>
        public int Owner { get; }

        public SpellDefinition Definition { get; }

        public string DefinitionId => Definition.Id;

        /// <summary>Center of the area, where the player dropped the card.</summary>
        public FixVector2 Target { get; }

        /// <summary>The tick whose combat step applies the first (or only) hit: cast tick + cast delay.</summary>
        public int LandTick { get; }

        /// <summary>
        /// A zone is active on ticks LandTick .. EndTick - 1 and removed after the last of them. Equal to
        /// <see cref="LandTick"/> for instant spells.
        /// </summary>
        public int EndTick { get; }

        /// <summary>Ticks between pulses (0 for instant spells). Derived from the definition and tick rate.</summary>
        public int PulseIntervalTicks { get; }

        public bool IsZone => EndTick > LandTick;

        /// <summary>Whether a pulse is due on this tick.</summary>
        internal bool PulsesOn(int tick) =>
            tick >= LandTick && (tick == LandTick || (tick < EndTick && (tick - LandTick) % PulseIntervalTicks == 0));

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Id);
            hasher.Add(Owner);
            hasher.Add(Definition.Id);
            hasher.Add(Target);
            hasher.Add(LandTick);
            hasher.Add(EndTick);
            hasher.Add(PulseIntervalTicks);
        }
    }
}
