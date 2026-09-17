using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Observers
{
    /// <summary>
    /// Receives match events for statistics, logs and effects (see <see cref="Simulation.Observer"/>). Observers are
    /// not part of the match: attaching one never changes the state or its hash. Events arrive synchronously, in the
    /// order they happen inside <see cref="Simulation.Tick(System.Collections.Generic.IReadOnlyList{Commands.Command})"/>,
    /// while the tick is still running, so an observer should use the event's data rather than read the live state, must
    /// not call Tick() (that throws) and must not throw (an exception leaves the Simulation mid-tick; discard it).
    /// <see cref="MatchObserver"/> is a base class with empty handlers.
    /// </summary>
    public interface IMatchObserver
    {
        /// <summary>A unit card was deployed (paid; its units appear after the spawn delay).</summary>
        void OnCardDeployed(in CardDeployedEvent e);

        /// <summary>A spell card was cast (paid; it lands after its cast delay).</summary>
        void OnSpellCast(in SpellCastEvent e);

        /// <summary>A leader ability was used.</summary>
        void OnAbilityCast(in AbilityCastEvent e);

        /// <summary>A unit died (reported in unit id order, after the tick's combat).</summary>
        void OnUnitDied(in UnitDiedEvent e);

        /// <summary>A structure lost HP (only HP actually removed; a hit is capped by the HP left).</summary>
        void OnStructureDamaged(in StructureDamagedEvent e);

        /// <summary>A structure fell. Follows the <see cref="OnStructureDamaged"/> event of the final hit.</summary>
        void OnStructureDestroyed(in StructureDestroyedEvent e);

        /// <summary>A player completed a mine capture.</summary>
        void OnMineCaptured(in MineCapturedEvent e);

        /// <summary>A unit took a chest (reported even when the player was at the gold cap and received nothing).</summary>
        void OnChestCollected(in ChestCollectedEvent e);

        /// <summary>Gold actually added to a player's bank (never zero; gold lost at the cap is not reported).</summary>
        void OnGoldAccrued(in GoldAccruedEvent e);

        /// <summary>The match ended. The last event of a match.</summary>
        void OnMatchEnded(in MatchEndedEvent e);
    }

    /// <summary>An <see cref="IMatchObserver"/> whose handlers do nothing; override the ones you need.</summary>
    public abstract class MatchObserver : IMatchObserver
    {
        public virtual void OnCardDeployed(in CardDeployedEvent e) { }
        public virtual void OnSpellCast(in SpellCastEvent e) { }
        public virtual void OnAbilityCast(in AbilityCastEvent e) { }
        public virtual void OnUnitDied(in UnitDiedEvent e) { }
        public virtual void OnStructureDamaged(in StructureDamagedEvent e) { }
        public virtual void OnStructureDestroyed(in StructureDestroyedEvent e) { }
        public virtual void OnMineCaptured(in MineCapturedEvent e) { }
        public virtual void OnChestCollected(in ChestCollectedEvent e) { }
        public virtual void OnGoldAccrued(in GoldAccruedEvent e) { }
        public virtual void OnMatchEnded(in MatchEndedEvent e) { }
    }

    public enum DamageSourceKind
    {
        None = 0,
        /// <summary>A unit's attack (melee hit or projectile).</summary>
        Unit = 1,
        /// <summary>A Keep or tower's shot.</summary>
        Structure = 2,
        /// <summary>A spell hit (instant or a zone pulse).</summary>
        Spell = 3,
        /// <summary>A leader's AreaDamage ability.</summary>
        LeaderAbility = 4,
    }

    /// <summary>What dealt a hit: the attacking player and the card (or structure) behind it.</summary>
    public readonly struct DamageSource
    {
        public static readonly DamageSource None = default;

        internal DamageSource(DamageSourceKind kind, int player, string cardId, int entityId)
        {
            Kind = kind;
            Player = player;
            CardId = cardId;
            EntityId = entityId;
        }

        public DamageSourceKind Kind { get; }

        /// <summary>The attacking player.</summary>
        public int Player { get; }

        /// <summary>The unit, spell or leader card id; for a structure its kind ("keep" or "tower").</summary>
        public string CardId { get; }

        /// <summary>Unit id, structure index or spell id; 0 for a leader ability.</summary>
        public int EntityId { get; }

        internal static DamageSource ForUnit(Units.Unit unit) =>
            new DamageSource(DamageSourceKind.Unit, unit.Owner, unit.Definition.Id, unit.Id);

        internal static DamageSource ForStructure(StructureState structure) =>
            new DamageSource(DamageSourceKind.Structure, structure.Owner, KindName(structure.Definition.Kind), structure.Index);

        internal static DamageSource ForSpell(Spells.SpellInstance spell) =>
            new DamageSource(DamageSourceKind.Spell, spell.Owner, spell.Definition.Id, spell.Id);

        internal static DamageSource ForAbility(int player, string leaderCardId) =>
            new DamageSource(DamageSourceKind.LeaderAbility, player, leaderCardId, 0);

        internal static string KindName(StructureKind kind) => kind == StructureKind.Keep ? "keep" : "tower";

        public override string ToString() => Kind == DamageSourceKind.None ? "none" : Kind + " " + CardId + " (player " + Player + ")";
    }

    /// <summary>Where banked gold came from.</summary>
    public enum GoldSource
    {
        /// <summary>The steady base income.</summary>
        Base = 0,
        /// <summary>Income from owned mines.</summary>
        Mine = 1,
        /// <summary>A collected chest.</summary>
        Chest = 2,
    }

    // Every event carries Tick: the number of the tick being run (MatchState.Tick before the Tick() call), except
    // MatchEndedEvent, whose Tick is the match length in ticks.

    public readonly struct CardDeployedEvent
    {
        internal CardDeployedEvent(int tick, int player, string cardId, int level, int cost, FixVector2 target)
        {
            Tick = tick; Player = player; CardId = cardId; Level = level; Cost = cost; Target = target;
        }

        public int Tick { get; }
        public int Player { get; }
        public string CardId { get; }
        public int Level { get; }
        /// <summary>Gold paid.</summary>
        public int Cost { get; }
        public FixVector2 Target { get; }
    }

    public readonly struct SpellCastEvent
    {
        internal SpellCastEvent(int tick, int player, string cardId, int spellId, int level, int cost, FixVector2 target, int landTick)
        {
            Tick = tick; Player = player; CardId = cardId; SpellId = spellId; Level = level; Cost = cost; Target = target;
            LandTick = landTick;
        }

        public int Tick { get; }
        public int Player { get; }
        public string CardId { get; }
        /// <summary>The cast's id in <see cref="MatchState.PendingSpells"/>.</summary>
        public int SpellId { get; }
        public int Level { get; }
        public int Cost { get; }
        public FixVector2 Target { get; }
        public int LandTick { get; }
    }

    public readonly struct AbilityCastEvent
    {
        internal AbilityCastEvent(int tick, int player, string leaderCardId, AbilityType type, FixVector2 target)
        {
            Tick = tick; Player = player; LeaderCardId = leaderCardId; Type = type; Target = target;
        }

        public int Tick { get; }
        public int Player { get; }
        public string LeaderCardId { get; }
        public AbilityType Type { get; }
        public FixVector2 Target { get; }
    }

    public readonly struct UnitDiedEvent
    {
        internal UnitDiedEvent(int tick, int unitId, int owner, string cardId, int level, FixVector2 position, DamageSource killer)
        {
            Tick = tick; UnitId = unitId; Owner = owner; CardId = cardId; Level = level; Position = position; Killer = killer;
        }

        public int Tick { get; }
        public int UnitId { get; }
        public int Owner { get; }
        public string CardId { get; }
        public int Level { get; }
        public FixVector2 Position { get; }
        /// <summary>The hit that brought the unit to 0 HP (hits of one tick are applied in the design doc's order).</summary>
        public DamageSource Killer { get; }
    }

    public readonly struct StructureDamagedEvent
    {
        internal StructureDamagedEvent(int tick, int structureIndex, int owner, string kind, Fix amount, Fix hpLeft, DamageSource source)
        {
            Tick = tick; StructureIndex = structureIndex; Owner = owner; Kind = kind; Amount = amount; HpLeft = hpLeft; Source = source;
        }

        public int Tick { get; }
        public int StructureIndex { get; }
        /// <summary>The player who owns the damaged structure.</summary>
        public int Owner { get; }
        /// <summary>"keep" or "tower".</summary>
        public string Kind { get; }
        /// <summary>HP removed (and scored by the attacker).</summary>
        public Fix Amount { get; }
        public Fix HpLeft { get; }
        public DamageSource Source { get; }
    }

    public readonly struct StructureDestroyedEvent
    {
        internal StructureDestroyedEvent(int tick, int structureIndex, int owner, string kind, Fix destructionBonus, DamageSource source)
        {
            Tick = tick; StructureIndex = structureIndex; Owner = owner; Kind = kind; DestructionBonus = destructionBonus;
            Source = source;
        }

        public int Tick { get; }
        public int StructureIndex { get; }
        public int Owner { get; }
        public string Kind { get; }
        /// <summary>The bonus the attacker scored.</summary>
        public Fix DestructionBonus { get; }
        /// <summary>The final hit.</summary>
        public DamageSource Source { get; }
    }

    public readonly struct MineCapturedEvent
    {
        internal MineCapturedEvent(int tick, int mineIndex, int player, int previousOwner)
        {
            Tick = tick; MineIndex = mineIndex; Player = player; PreviousOwner = previousOwner;
        }

        public int Tick { get; }
        public int MineIndex { get; }
        /// <summary>The new owner.</summary>
        public int Player { get; }
        /// <summary>The old owner, or -1 for a neutral mine.</summary>
        public int PreviousOwner { get; }
    }

    public readonly struct ChestCollectedEvent
    {
        internal ChestCollectedEvent(int tick, int chestIndex, int player, int unitId, Fix goldReceived)
        {
            Tick = tick; ChestIndex = chestIndex; Player = player; UnitId = unitId; GoldReceived = goldReceived;
        }

        public int Tick { get; }
        public int ChestIndex { get; }
        public int Player { get; }
        /// <summary>The unit that took it.</summary>
        public int UnitId { get; }
        /// <summary>Gold banked (less than chestGold, even 0, near the cap).</summary>
        public Fix GoldReceived { get; }
    }

    public readonly struct GoldAccruedEvent
    {
        internal GoldAccruedEvent(int tick, int player, GoldSource source, Fix amount)
        {
            Tick = tick; Player = player; Source = source; Amount = amount;
        }

        public int Tick { get; }
        public int Player { get; }
        public GoldSource Source { get; }
        public Fix Amount { get; }
    }

    public readonly struct MatchEndedEvent
    {
        internal MatchEndedEvent(int tick, int winner, EndReason reason, TieBreakRule tieBreakRule, Fix score0, Fix score1)
        {
            Tick = tick; Winner = winner; Reason = reason; TieBreakRule = tieBreakRule; Score0 = score0; Score1 = score1;
        }

        /// <summary>Ticks the match lasted.</summary>
        public int Tick { get; }
        public int Winner { get; }
        public EndReason Reason { get; }
        public TieBreakRule TieBreakRule { get; }
        public Fix Score0 { get; }
        public Fix Score1 { get; }
    }
}
