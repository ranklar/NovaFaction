using System;

namespace NovaFaction.Sim.Numerics
{
    /// <summary>
    /// Deterministic 2D vector of <see cref="Fix"/>. Arithmetic follows Fix's saturating rules.
    /// Length and Normalized are computed from exact 128-bit sums of squares, so they stay accurate
    /// for very small and very large vectors. LengthSquared and Dot use ordinary Fix
    /// multiplication and saturate once components exceed roughly +/-8,000,000.
    /// </summary>
    public readonly struct FixVector2 : IEquatable<FixVector2>
    {
        public static readonly FixVector2 Zero = new FixVector2(Fix.Zero, Fix.Zero);
        public static readonly FixVector2 UnitX = new FixVector2(Fix.One, Fix.Zero);
        public static readonly FixVector2 UnitY = new FixVector2(Fix.Zero, Fix.One);

        public readonly Fix X;
        public readonly Fix Y;

        public FixVector2(Fix x, Fix y)
        {
            X = x;
            Y = y;
        }

        public static FixVector2 operator +(FixVector2 a, FixVector2 b) => new FixVector2(a.X + b.X, a.Y + b.Y);

        public static FixVector2 operator -(FixVector2 a, FixVector2 b) => new FixVector2(a.X - b.X, a.Y - b.Y);

        public static FixVector2 operator -(FixVector2 v) => new FixVector2(-v.X, -v.Y);

        public static FixVector2 operator *(FixVector2 v, Fix scale) => new FixVector2(v.X * scale, v.Y * scale);

        public static FixVector2 operator *(Fix scale, FixVector2 v) => new FixVector2(v.X * scale, v.Y * scale);

        public static bool operator ==(FixVector2 a, FixVector2 b) => a.X == b.X && a.Y == b.Y;

        public static bool operator !=(FixVector2 a, FixVector2 b) => !(a == b);

        public static Fix Dot(FixVector2 a, FixVector2 b) => a.X * b.X + a.Y * b.Y;

        public Fix LengthSquared => X * X + Y * Y;

        /// <summary>Exact sqrt(X^2 + Y^2) rounded to the nearest 1/65536; saturates to Fix.MaxValue.</summary>
        public Fix Length
        {
            get
            {
                ulong ax = Magnitude(X.Raw);
                ulong ay = Magnitude(Y.Raw);
                UInt128Math.Multiply(ax, ax, out ulong xHi, out ulong xLo);
                UInt128Math.Multiply(ay, ay, out ulong yHi, out ulong yLo);
                ulong lo = unchecked(xLo + yLo);
                ulong hi = xHi + yHi + (lo < xLo ? 1UL : 0UL);
                // Both squares are <= 2^126, so the sum is <= 2^127 as SqrtRound requires.
                ulong root = UInt128Math.SqrtRound(hi, lo);
                return root > long.MaxValue ? Fix.MaxValue : Fix.FromRaw((long)root);
            }
        }

        /// <summary>
        /// Unit vector in the same direction, or <see cref="Zero"/> for the zero vector.
        /// Each component is within one raw step of the exact result.
        /// </summary>
        public FixVector2 Normalized
        {
            get
            {
                long x = X.Raw, y = Y.Raw;
                if (x == 0 && y == 0)
                {
                    return Zero;
                }

                // Direction is scale-invariant: scale tiny vectors up first so the length has
                // enough significant bits for an accurate division.
                ulong largest = Math.Max(Magnitude(x), Magnitude(y));
                int bits = UInt128Math.BitLength(largest);
                if (bits < 31)
                {
                    int shift = 31 - bits;
                    x <<= shift;
                    y <<= shift;
                }

                var scaled = new FixVector2(Fix.FromRaw(x), Fix.FromRaw(y));
                Fix length = scaled.Length;
                return new FixVector2(scaled.X / length, scaled.Y / length);
            }
        }

        public static Fix Distance(FixVector2 a, FixVector2 b) => (a - b).Length;

        public static Fix DistanceSquared(FixVector2 a, FixVector2 b) => (a - b).LengthSquared;

        public bool Equals(FixVector2 other) => this == other;

        public override bool Equals(object? obj) => obj is FixVector2 other && this == other;

        public override int GetHashCode() => unchecked((X.GetHashCode() * 397) ^ Y.GetHashCode());

        public override string ToString() => "(" + X.ToString() + ", " + Y.ToString() + ")";

        private static ulong Magnitude(long v) => v < 0 ? unchecked((ulong)(-v)) : (ulong)v;
    }
}
