namespace NovaFaction.Sim.Numerics
{
    /// <summary>
    /// Unsigned 128-bit integer helpers built from 64-bit halves (netstandard2.1 has no UInt128).
    /// Used by Fix multiplication, division and square root so intermediates never overflow.
    /// </summary>
    internal static class UInt128Math
    {
        /// <summary>Full 64x64 -> 128-bit unsigned multiply.</summary>
        public static void Multiply(ulong a, ulong b, out ulong hi, out ulong lo)
        {
            ulong aL = (uint)a, aH = a >> 32;
            ulong bL = (uint)b, bH = b >> 32;

            ulong ll = aL * bL;
            ulong lh = aL * bH;
            ulong hl = aH * bL;
            ulong hh = aH * bH;

            ulong mid = (ll >> 32) + (uint)lh + (uint)hl;
            lo = (mid << 32) | (uint)ll;
            hi = hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
        }

        /// <summary>
        /// Divides the 128-bit value (hi:lo) by <paramref name="divisor"/>.
        /// Precondition: hi &lt; divisor, so the quotient fits in 64 bits.
        /// </summary>
        public static ulong DivRem(ulong hi, ulong lo, ulong divisor, out ulong remainder)
        {
            if (hi == 0)
            {
                remainder = lo % divisor;
                return lo / divisor;
            }

            // Restoring binary long division, one quotient bit per iteration.
            ulong rem = hi;
            ulong quotient = 0;
            for (int i = 63; i >= 0; i--)
            {
                ulong carry = rem >> 63;
                rem = (rem << 1) | ((lo >> i) & 1UL);
                quotient <<= 1;
                if (carry != 0 || rem >= divisor)
                {
                    rem = unchecked(rem - divisor);
                    quotient |= 1UL;
                }
            }

            remainder = rem;
            return quotient;
        }

        /// <summary>
        /// a * b / divisor for non-negative values, rounded to nearest (ties up), computed exactly in 128 bits.
        /// Precondition: the result fits in a long (e.g. a &lt;= divisor).
        /// </summary>
        public static long MulDivRound(long a, long b, long divisor)
        {
            Multiply((ulong)a, (ulong)b, out ulong hi, out ulong lo);
            ulong half = (ulong)divisor >> 1;
            ulong roundedLo = lo + half;
            if (roundedLo < lo)
            {
                hi++;
            }
            return (long)DivRem(hi, roundedLo, (ulong)divisor, out _);
        }

        /// <summary>Number of significant bits in <paramref name="value"/> (0 for 0).</summary>
        public static int BitLength(ulong value)
        {
            int n = 0;
            if ((value >> 32) != 0) { value >>= 32; n += 32; }
            if ((value >> 16) != 0) { value >>= 16; n += 16; }
            if ((value >> 8) != 0) { value >>= 8; n += 8; }
            if ((value >> 4) != 0) { value >>= 4; n += 4; }
            if ((value >> 2) != 0) { value >>= 2; n += 2; }
            if ((value >> 1) != 0) { value >>= 1; n += 1; }
            return n + (int)value;
        }

        /// <summary>
        /// Square root of the 128-bit value (hi:lo), rounded to the nearest integer, by integer
        /// Newton iteration. Precondition: value &lt;= 2^127.
        /// </summary>
        public static ulong SqrtRound(ulong hi, ulong lo)
        {
            if (hi == 0 && lo == 0)
            {
                return 0;
            }

            int bits = hi != 0 ? 64 + BitLength(hi) : BitLength(lo);
            int shift = (bits + 1) / 2;

            // Start at or above the true root; Newton then decreases monotonically to floor(sqrt).
            ulong x = shift >= 64 ? ulong.MaxValue : 1UL << shift;
            while (true)
            {
                ulong q = DivRem(hi, lo, x, out _);
                ulong y = (x >> 1) + (q >> 1) + (x & q & 1UL); // floor((x + q) / 2) without overflow
                if (y >= x)
                {
                    break;
                }
                x = y;
            }

            // x = floor(sqrt(N)). Round up when N - x^2 > x, i.e. N >= (x + 0.5)^2.
            Multiply(x, x, out ulong sqHi, out ulong sqLo);
            ulong diffLo = unchecked(lo - sqLo);
            ulong diffHi = unchecked(hi - sqHi - (lo < sqLo ? 1UL : 0UL));
            if (diffHi != 0 || diffLo > x)
            {
                x++;
            }
            return x;
        }
    }
}
