using System;
using System.Collections.Generic;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim
{
    /// <summary>
    /// The only source of randomness in the sim. PCG32 (XSH-RR variant, O'Neill 2014): 64-bit
    /// state, 32-bit output, fixed stream increment. Identical output on every platform.
    /// The full generator state is one ulong (<see cref="GetState"/> / <see cref="SetState"/>),
    /// so it can be included in per-tick state hashes and restored for replays.
    /// </summary>
    public sealed class SimRandom
    {
        private const ulong Multiplier = 6364136223846793005UL;
        // Stream selector 54 from the PCG reference demo: increment = (54 << 1) | 1.
        private const ulong Increment = (54UL << 1) | 1UL;

        private ulong _state;

        /// <summary>Seeds using the PCG reference procedure (pcg32_srandom_r).</summary>
        public SimRandom(ulong seed)
        {
            _state = 0;
            NextUInt();
            _state = unchecked(_state + seed);
            NextUInt();
        }

        public ulong GetState() => _state;

        public void SetState(ulong state) => _state = state;

        /// <summary>Uniform 32-bit value.</summary>
        public uint NextUInt()
        {
            ulong old = _state;
            _state = unchecked(old * Multiplier + Increment);
            uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
            int rotation = (int)(old >> 59);
            return (xorShifted >> rotation) | (xorShifted << ((-rotation) & 31));
        }

        /// <summary>
        /// Uniform int in [minInclusive, maxExclusive), without modulo bias (rejection sampling,
        /// so the number of draws consumed can vary, but it is deterministic).
        /// Throws if maxExclusive &lt;= minInclusive.
        /// </summary>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be greater than minInclusive.");
            }
            uint range = (uint)((long)maxExclusive - minInclusive);
            uint threshold = unchecked((uint)-range) % range; // (2^32 - range) % range
            while (true)
            {
                uint r = NextUInt();
                if (r >= threshold)
                {
                    return unchecked((int)(minInclusive + (long)(r % range)));
                }
            }
        }

        /// <summary>Uniform Fix in [0, 1): one of the 65536 raw steps. Consumes one draw.</summary>
        public Fix NextFix01() => Fix.FromRaw(NextUInt() >> (32 - Fix.FractionalBits));

        /// <summary>
        /// True with the given probability. Always consumes exactly one draw, even when the
        /// probability is &lt;= 0 (always false) or &gt;= 1 (always true), so the random stream
        /// does not depend on balance values.
        /// </summary>
        public bool Chance(Fix probability) => NextFix01() < probability;

        /// <summary>In-place Fisher-Yates shuffle.</summary>
        public void Shuffle<T>(IList<T> list)
        {
            if (list == null)
            {
                throw new ArgumentNullException(nameof(list));
            }
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = NextInt(0, i + 1);
                T temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }
    }
}
