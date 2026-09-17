using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

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
        internal PlayerState(int index, Fix startingGold)
        {
            Index = index;
            Gold = startingGold;
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

        public void AppendHash(ref StateHasher hasher)
        {
            hasher.Add(Index);
            hasher.Add(Gold);
            hasher.Add(IncomeRemainder);
            hasher.Add(Score);
            hasher.Add(CommandsReceived);
        }
    }

    /// <summary>
    /// Everything that defines a match at a given tick. Two matches with equal MatchState evolve
    /// identically given the same commands; <see cref="StateHash"/> fingerprints it.
    /// </summary>
    public sealed class MatchState : IStateHashable
    {
        /// <summary>Bump when the hashed layout changes, so old and new hashes never collide by accident.</summary>
        public const int HashFormatVersion = 1;

        private readonly PlayerState[] _players;

        internal MatchState(ulong seed, int clockTicks, Fix startingGold)
        {
            Random = new SimRandom(seed);
            ClockRemainingTicks = clockTicks;
            Phase = MatchPhase.Regulation;
            _players = new[] { new PlayerState(0, startingGold), new PlayerState(1, startingGold) };
        }

        /// <summary>Number of ticks completed. The next Tick() call executes tick number <see cref="Tick"/>.</summary>
        public int Tick { get; internal set; }

        public SimRandom Random { get; }

        /// <summary>Ticks left on the current phase's clock.</summary>
        public int ClockRemainingTicks { get; internal set; }

        public MatchPhase Phase { get; internal set; }

        /// <summary>Always two players, index 0 and 1.</summary>
        public IReadOnlyList<PlayerState> Players => _players;

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
            // Future state (units, structures, projectiles, mines, chests, decks) is appended here:
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
