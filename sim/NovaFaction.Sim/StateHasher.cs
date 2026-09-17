using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim
{
    /// <summary>
    /// Anything that contributes to the per-tick state hash. Implementations append every field
    /// that affects future simulation, in a fixed order. Variable-length collections append their
    /// count first, then each element in a deterministic order (e.g. by entity id), never in
    /// Dictionary/HashSet order.
    /// </summary>
    public interface IStateHashable
    {
        void AppendHash(ref StateHasher hasher);
    }

    /// <summary>
    /// 64-bit FNV-1a, implemented locally. Every value is fed as its little-endian bytes via
    /// explicit shifts, so the result is identical on every CPU and runtime.
    /// </summary>
    public struct StateHasher
    {
        public const ulong OffsetBasis = 14695981039346656037UL;
        public const ulong Prime = 1099511628211UL;

        // Stored XOR the offset basis, so default(StateHasher) is a valid, freshly started hasher.
        private ulong _stored;

        /// <summary>Current hash. A new (default) hasher reports the FNV offset basis.</summary>
        public ulong Value => _stored ^ OffsetBasis;

        public void Add(byte value)
        {
            ulong hash = unchecked(((_stored ^ OffsetBasis) ^ value) * Prime);
            _stored = hash ^ OffsetBasis;
        }

        public void Add(bool value) => Add(value ? (byte)1 : (byte)0);

        public void Add(int value) => Add(unchecked((uint)value));

        public void Add(uint value)
        {
            Add((byte)value);
            Add((byte)(value >> 8));
            Add((byte)(value >> 16));
            Add((byte)(value >> 24));
        }

        public void Add(long value) => Add(unchecked((ulong)value));

        public void Add(ulong value)
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                Add((byte)(value >> shift));
            }
        }

        /// <summary>Hashes the raw Q48.16 value.</summary>
        public void Add(Fix value) => Add(value.Raw);

        public void Add(FixVector2 value)
        {
            Add(value.X);
            Add(value.Y);
        }

        /// <summary>Length, then each UTF-16 code unit as two little-endian bytes.</summary>
        public void Add(string value)
        {
            Add(value.Length);
            foreach (char c in value)
            {
                Add((byte)c);
                Add((byte)(c >> 8));
            }
        }

        public void AddHashable<T>(T value) where T : IStateHashable => value.AppendHash(ref this);
    }
}
