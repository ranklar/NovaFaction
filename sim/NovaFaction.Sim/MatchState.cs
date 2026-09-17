using System;
using System.Collections.Generic;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Combat;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Economy;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Spells;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim
{
    public enum MatchPhase
    {
        /// <summary>The normal 3:00 clock.</summary>
        Regulation = 0,
        /// <summary>Tie-break period after an exact score tie: doubled income, first structure damage wins.</summary>
        SuddenDeath = 1,
        /// <summary>The match is over; Tick() may no longer be called.</summary>
        Ended = 2,
    }

    /// <summary>Per-player match state.</summary>
    public sealed class PlayerState : IStateHashable
    {
        internal PlayerState(int index, Fix startingGold, Deck deck, int handSize, SimRandom random, int structureLevel)
        {
            Index = index;
            Gold = startingGold;
            Deck = deck;
            Cards = new CardCycle(deck, handSize, random);
            StructureLevel = structureLevel;
        }

        /// <summary>0 or 1.</summary>
        public int Index { get; }

        /// <summary>Current gold, 0..goldCap.</summary>
        public Fix Gold { get; internal set; }

        /// <summary>
        /// Income carried between ticks, in raw Fix units times ticksPerSecond. Per-tick income is
        /// income/ticksPerSecond, which is rarely exact in Q48.16; carrying the remainder keeps gold
        /// exact over whole seconds instead of drifting. Always in [0, ticksPerSecond).
        /// </summary>
        public long IncomeRemainder { get; internal set; }

        /// <summary>
        /// The same carry as <see cref="IncomeRemainder"/>, for mine income. Kept separately so the gold that mines
        /// actually delivered can be counted in <see cref="GoldFromMap"/>.
        /// </summary>
        public long MineIncomeRemainder { get; internal set; }

        /// <summary>Damage score: HP removed from enemy structures plus the destruction bonus of each one destroyed.</summary>
        public Fix Score { get; internal set; }

        /// <summary>
        /// Gold actually received from mines and chests (base income does not count, nor gold lost to the cap);
        /// tie-break rule 3.
        /// </summary>
        public Fix GoldFromMap { get; internal set; }

        /// <summary>Number of valid commands this player has submitted.</summary>
        public int CommandsReceived { get; internal set; }

        /// <summary>
        /// Deploy commands rejected by game rules (empty slot, not enough gold, position not deployable).
        /// Rejections change nothing else, so this counter is what makes them visible in the state hash.
        /// </summary>
        public int IgnoredDeploys { get; internal set; }

        /// <summary>
        /// LeaderAbility commands rejected by game rules (no living leader, target out of range, cooldown, no ability).
        /// Like <see cref="IgnoredDeploys"/>, this makes rejections visible in the state hash.
        /// </summary>
        public int IgnoredAbilities { get; internal set; }

        /// <summary>The first tick the leader ability may be used on again (0 = ready from the start).</summary>
        public int AbilityReadyTick { get; internal set; }

        /// <summary>The deck this player brought (sorted card list with levels, and faction).</summary>
        public Deck Deck { get; }

        /// <summary>The deck leader's passive: applies to all of this player's units all match.</summary>
        public IReadOnlyList<Modifier> Passive => Deck.Leader.Passive;

        /// <summary>This player's structure level (scales their Keep and towers).</summary>
        public int StructureLevel { get; }

        /// <summary>Hand, next card and draw queue.</summary>
        public CardCycle Cards { get; }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Index);
            hasher.Add(Gold);
            hasher.Add(IncomeRemainder);
            hasher.Add(MineIncomeRemainder);
            hasher.Add(Score);
            hasher.Add(GoldFromMap);
            hasher.Add(CommandsReceived);
            hasher.Add(IgnoredDeploys);
            hasher.Add(IgnoredAbilities);
            hasher.Add(AbilityReadyTick);
            hasher.Add(Deck.Catalog.ContentHash);
            hasher.Add(Deck.Levels.Count);
            foreach (int level in Deck.Levels) // deck (sorted id) order
            {
                hasher.Add(level);
            }
            hasher.Add(StructureLevel);
            hasher.AddHashable(Cards);
        }
    }

    /// <summary>
    /// Everything that defines a match at a given tick. Two matches with equal MatchState evolve
    /// identically given the same commands; <see cref="StateHash"/> fingerprints it.
    /// </summary>
    public sealed class MatchState : IStateHashable
    {
        /// <summary>Bump when the hashed layout changes, so old and new hashes never collide by accident.</summary>
        // Version 2 added the map (id, content hash, destroyed structures, unlocked zones).
        // Version 3 added decks/hands, ignored deploys, units and pending spawns.
        // Version 4 added combat: result fields, gold collected, structure stats and state, unit targets and
        // cooldowns, and projectiles.
        // Version 5 added map gold: mine income carry, gold from map, mines and chests.
        // Version 6 added spells (card catalog hash, pending spells, spell zones) and the Capturing unit state.
        // Version 7 added levels and leaders: ignored abilities, ability cooldown, deck levels and structure level per
        // player; unit level, effective stats, Rally buff, capture stall and mine-ignore tick; pending spawn level;
        // spell damage.
        public const int HashFormatVersion = 7;

        /// <summary><see cref="Winner"/> value while nobody has won.</summary>
        public const int NoWinner = -1;

        private readonly PlayerState[] _players;
        private readonly List<Unit> _units = new List<Unit>();
        private readonly List<PendingSpawn> _pendingSpawns = new List<PendingSpawn>();
        private readonly StructureState[] _structures;
        private readonly List<Projectile> _projectiles = new List<Projectile>();
        private readonly MineState[] _mines;
        private readonly ChestState[] _chests;
        private readonly List<SpellInstance> _pendingSpells = new List<SpellInstance>();
        private readonly List<SpellInstance> _spellZones = new List<SpellInstance>();

        internal MatchState(ulong seed, MatchSetup setup)
        {
            MatchRules rules = setup.Rules;
            Rules = rules;
            Map = new MapState(setup.Map);
            Random = new SimRandom(seed);
            ClockRemainingTicks = rules.MatchLengthTicks;
            Phase = MatchPhase.Regulation;
            // Player 0's cycle is shuffled first, then player 1's, both from the match RNG.
            _players = new[]
            {
                new PlayerState(0, rules.GoldStartingAmount, setup.GetDeck(0), rules.HandSize, Random,
                    setup.GetStructureLevel(0)),
                new PlayerState(1, rules.GoldStartingAmount, setup.GetDeck(1), rules.HandSize, Random,
                    setup.GetStructureLevel(1)),
            };
            NextUnitId = 1;
            NextProjectileId = 1;
            NextSpellId = 1;
            Winner = NoWinner;
            StructureCatalog = setup.Structures;
            _structures = new StructureState[setup.Map.Structures.Count];
            foreach (StructureDefinition s in setup.Map.Structures)
            {
                _structures[s.Index] = new StructureState(s, setup.Structures.Get(s.Kind), Map.Grid,
                    rules.LevelFactor(setup.GetStructureLevel(s.Owner)));
            }
            _mines = MapGoldSystem.CreateMines(setup.Map, Map.Grid, rules);
            _chests = MapGoldSystem.CreateChests(setup.Map, Map.Grid);
            MapGoldSystem.SpawnDueChests(this, rules); // a first wave at 0 s appears at the start
        }

        /// <summary>Number of ticks completed. The next Tick() call executes tick number <see cref="Tick"/>.</summary>
        public int Tick { get; internal set; }

        public SimRandom Random { get; }

        internal MatchRules Rules { get; }

        /// <summary>
        /// Leader AreaDamage casts applied this tick, consumed by this tick's combat. Always empty between ticks, so
        /// not part of the hash.
        /// </summary>
        internal List<AbilityStrike> AbilityStrikes { get; } = new List<AbilityStrike>();

        /// <summary>The battlefield: terrain, structures, deploy zones, flow fields.</summary>
        public MapState Map { get; }

        /// <summary>Ticks left on the current phase's clock.</summary>
        public int ClockRemainingTicks { get; internal set; }

        public MatchPhase Phase { get; internal set; }

        /// <summary>The winning player (0 or 1) once the match has ended, else <see cref="NoWinner"/>.</summary>
        public int Winner { get; internal set; }

        public EndReason EndReason { get; internal set; }

        /// <summary>Which tie-break rule decided the match when <see cref="EndReason"/> is TieBreak.</summary>
        public TieBreakRule TieBreakRule { get; internal set; }

        /// <summary>Keep and tower stats this match uses.</summary>
        public StructureCatalog StructureCatalog { get; }

        /// <summary>Combat state of every structure, indexed like the map's structure list.</summary>
        public IReadOnlyList<StructureState> Structures => _structures;

        /// <summary>Projectiles in flight, in id (firing) order.</summary>
        public IReadOnlyList<Projectile> Projectiles => _projectiles;

        internal List<Projectile> ProjectileList => _projectiles;

        /// <summary>The id the next fired projectile will get. Ids start at 1 and are never reused.</summary>
        public int NextProjectileId { get; internal set; }

        /// <summary>Gold mines, in the map's mine order.</summary>
        public IReadOnlyList<MineState> Mines => _mines;

        /// <summary>Chest spawn points and whether a chest waits at each, in the map's chest spawn order.</summary>
        public IReadOnlyList<ChestState> Chests => _chests;

        /// <summary>Cast spells that have not landed yet, in id (cast) order.</summary>
        public IReadOnlyList<SpellInstance> PendingSpells => _pendingSpells;

        internal List<SpellInstance> PendingSpellList => _pendingSpells;

        /// <summary>Landed zone spells that are still active, in id order.</summary>
        public IReadOnlyList<SpellInstance> SpellZones => _spellZones;

        internal List<SpellInstance> SpellZoneList => _spellZones;

        /// <summary>The id the next cast spell will get. Ids start at 1 and are never reused.</summary>
        public int NextSpellId { get; internal set; }

        /// <summary>Always two players, index 0 and 1.</summary>
        public IReadOnlyList<PlayerState> Players => _players;

        /// <summary>Every unit on the field, in id (creation) order.</summary>
        public IReadOnlyList<Unit> Units => _units;

        internal List<Unit> UnitList => _units;

        /// <summary>Paid deploys whose units have not appeared yet, in deploy order.</summary>
        public IReadOnlyList<PendingSpawn> PendingSpawns => _pendingSpawns;

        internal List<PendingSpawn> PendingSpawnList => _pendingSpawns;

        /// <summary>The id the next created unit will get. Ids start at 1 and are never reused.</summary>
        public int NextUnitId { get; internal set; }

        /// <summary>The unit with this id, or null.</summary>
        public Unit? FindUnit(int id)
        {
            int index = IndexOfUnit(id);
            return index < 0 ? null : _units[index];
        }

        /// <summary>Position of the unit with this id in <see cref="Units"/>, or -1.</summary>
        public int IndexOfUnit(int id)
        {
            // Units are sorted by id, so binary search.
            int lo = 0, hi = _units.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int midId = _units[mid].Id;
                if (midId == id)
                {
                    return mid;
                }
                if (midId < id)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return -1;
        }

        /// <summary>Creates a unit at the given card level with its owner's leader passive applied.</summary>
        internal Unit AddUnit(int owner, UnitDefinition definition, FixVector2 position, int level = 1)
        {
            var unit = new Unit(NextUnitId++, owner, definition, position, level, Rules.LevelFactor(level),
                GetPlayer(owner).Passive);
            _units.Add(unit); // ids only grow, so the list stays sorted
            return unit;
        }

        public PlayerState GetPlayer(int index)
        {
            if (index < 0 || index >= _players.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return _players[index];
        }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(HashFormatVersion);
            hasher.Add(Tick);
            hasher.Add(Random.GetState());
            hasher.Add(ClockRemainingTicks);
            hasher.Add((int)Phase);
            hasher.Add(Winner);
            hasher.Add((int)EndReason);
            hasher.Add((int)TieBreakRule);
            hasher.Add(_players.Length);
            foreach (PlayerState player in _players) // array order = player index
            {
                hasher.AddHashable(player);
            }
            hasher.AddHashable(Map);
            hasher.Add(StructureCatalog.ContentHash);
            hasher.Add(_structures.Length);
            foreach (StructureState structure in _structures) // index order
            {
                hasher.AddHashable(structure);
            }
            hasher.Add(NextUnitId);
            hasher.Add(_units.Count);
            foreach (Unit unit in _units) // id order
            {
                hasher.AddHashable(unit);
            }
            hasher.Add(_pendingSpawns.Count);
            foreach (PendingSpawn spawn in _pendingSpawns) // deploy order
            {
                hasher.AddHashable(spawn);
            }
            hasher.Add(NextProjectileId);
            hasher.Add(_projectiles.Count);
            foreach (Projectile projectile in _projectiles) // id order
            {
                hasher.AddHashable(projectile);
            }
            hasher.Add(_mines.Length);
            foreach (MineState mine in _mines) // index order
            {
                hasher.AddHashable(mine);
            }
            hasher.Add(_chests.Length);
            foreach (ChestState chest in _chests) // index order
            {
                hasher.AddHashable(chest);
            }
            hasher.Add(NextSpellId);
            hasher.Add(_pendingSpells.Count);
            foreach (SpellInstance spell in _pendingSpells) // id order
            {
                hasher.AddHashable(spell);
            }
            hasher.Add(_spellZones.Count);
            foreach (SpellInstance zone in _spellZones) // id order
            {
                hasher.AddHashable(zone);
            }
            // Future state is appended here: count first, then each item in id order.
        }
    }

    /// <summary>Deterministic 64-bit fingerprint of a match state (FNV-1a).</summary>
    public static class StateHash
    {
        public static ulong Compute(MatchState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            var hasher = new StateHasher();
            state.AppendHash(ref hasher);
            return hasher.Value;
        }
    }
}
