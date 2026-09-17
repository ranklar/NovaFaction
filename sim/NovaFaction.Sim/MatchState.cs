using System;
using System.Collections.Generic;
using NovaFaction.Sim.Cards;
using NovaFaction.Sim.Content;
using NovaFaction.Sim.Map;
using NovaFaction.Sim.Numerics;
using NovaFaction.Sim.Units;

namespace NovaFaction.Sim
{
    public enum MatchPhase
    {
        /// <summary>The normal 3:00 clock.</summary>
        Regulation = 0,
        /// <summary>Tie-break period after an exact score tie; first damage wins. Not reached yet.</summary>
        SuddenDeath = 1,
        /// <summary>The match is over; Tick() may no longer be called.</summary>
        Ended = 2,
    }

    /// <summary>Per-player match state.</summary>
    public sealed class PlayerState : IStateHashable
    {
        internal PlayerState(int index, Fix startingGold, Deck deck, int handSize, SimRandom random)
        {
            Index = index;
            Gold = startingGold;
            Deck = deck;
            Cards = new CardCycle(deck, handSize, random);
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

        /// <summary>Damage score. Placeholder: nothing deals damage yet, so it stays zero.</summary>
        public Fix Score { get; internal set; }

        /// <summary>Number of valid commands this player has submitted.</summary>
        public int CommandsReceived { get; internal set; }

        /// <summary>
        /// Deploy commands rejected by game rules (empty slot, not enough gold, position not deployable).
        /// Rejections change nothing else, so this counter is what makes them visible in the state hash.
        /// </summary>
        public int IgnoredDeploys { get; internal set; }

        /// <summary>The deck this player brought (sorted card list and faction).</summary>
        public Deck Deck { get; }

        /// <summary>Hand, next card and draw queue.</summary>
        public CardCycle Cards { get; }

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Index);
            hasher.Add(Gold);
            hasher.Add(IncomeRemainder);
            hasher.Add(Score);
            hasher.Add(CommandsReceived);
            hasher.Add(IgnoredDeploys);
            hasher.Add(Deck.Roster.ContentHash);
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
        public const int HashFormatVersion = 3;

        private readonly PlayerState[] _players;
        private readonly List<Unit> _units = new List<Unit>();
        private readonly List<PendingSpawn> _pendingSpawns = new List<PendingSpawn>();

        internal MatchState(ulong seed, MatchSetup setup)
        {
            MatchRules rules = setup.Rules;
            Map = new MapState(setup.Map);
            Random = new SimRandom(seed);
            ClockRemainingTicks = rules.MatchLengthTicks;
            Phase = MatchPhase.Regulation;
            // Player 0's cycle is shuffled first, then player 1's, both from the match RNG.
            _players = new[]
            {
                new PlayerState(0, rules.GoldStartingAmount, setup.GetDeck(0), rules.HandSize, Random),
                new PlayerState(1, rules.GoldStartingAmount, setup.GetDeck(1), rules.HandSize, Random),
            };
            NextUnitId = 1;
        }

        /// <summary>Number of ticks completed. The next Tick() call executes tick number <see cref="Tick"/>.</summary>
        public int Tick { get; internal set; }

        public SimRandom Random { get; }

        /// <summary>The battlefield: terrain, structures, deploy zones, flow fields.</summary>
        public MapState Map { get; }

        /// <summary>Ticks left on the current phase's clock.</summary>
        public int ClockRemainingTicks { get; internal set; }

        public MatchPhase Phase { get; internal set; }

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
            // Units are sorted by id, so binary search.
            int lo = 0, hi = _units.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int midId = _units[mid].Id;
                if (midId == id)
                {
                    return _units[mid];
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
            return null;
        }

        internal Unit AddUnit(int owner, UnitDefinition definition, FixVector2 position)
        {
            var unit = new Unit(NextUnitId++, owner, definition, position);
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
            hasher.Add(_players.Length);
            foreach (PlayerState player in _players) // array order = player index
            {
                hasher.AddHashable(player);
            }
            hasher.AddHashable(Map);
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
            // Future state (structure HP, projectiles, mines, chests) is appended here:
            // count first, then each item in id order.
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
