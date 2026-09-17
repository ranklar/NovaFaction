using System;
using System.Globalization;
using System.Text;

namespace NovaFaction.Sim.Numerics
{
    /// <summary>
    /// Deterministic Q48.16 fixed-point number: a 64-bit raw value with 16 fractional bits.
    /// One unit = 65536 raw. Range is about +/-140,737,488,355,328 with a step of 1/65536.
    /// <para>
    /// Overflow policy: + - * / and unary minus saturate to <see cref="MaxValue"/> /
    /// <see cref="MinValue"/> instead of wrapping. Rounding policy: * and / (and Parse, Sqrt,
    /// FromFraction) round to the nearest raw step, ties away from zero, so results are symmetric
    /// under negation. Multiplication and division use 128-bit intermediates, so they are exact
    /// before rounding for every input. Division by zero throws <see cref="DivideByZeroException"/>.
    /// </para>
    /// </summary>
    public readonly struct Fix : IEquatable<Fix>, IComparable<Fix>
    {
        public const int FractionalBits = 16;
        private const long OneRaw = 1L << FractionalBits;
        private const long FractionMask = OneRaw - 1;
        private const long HalfRaw = OneRaw >> 1;

        public static readonly Fix Zero = new Fix(0);
        public static readonly Fix One = new Fix(OneRaw);
        public static readonly Fix Half = new Fix(HalfRaw);
        public static readonly Fix MaxValue = new Fix(long.MaxValue);
        public static readonly Fix MinValue = new Fix(long.MinValue);
        /// <summary>Smallest positive value: 1/65536.</summary>
        public static readonly Fix Epsilon = new Fix(1);

        /// <summary>The underlying Q48.16 integer. Use for hashing and serialization.</summary>
        public readonly long Raw;

        private Fix(long raw)
        {
            Raw = raw;
        }

        // ---------------------------------------------------------------- factories

        public static Fix FromRaw(long raw) => new Fix(raw);

        public static Fix FromInt(int value) => new Fix((long)value << FractionalBits);

        /// <summary>numerator / denominator, rounded to nearest (ties away from zero).</summary>
        public static Fix FromFraction(long numerator, long denominator)
        {
            return Divide(numerator, denominator);
        }

        /// <summary>
        /// Parses a plain decimal string using integer math only (invariant, culture-free).
        /// Accepted grammar: optional '-', one or more digits, optionally '.' and one or more digits
        /// (e.g. "3", "1.5", "-0.25"). No '+', exponent, whitespace or thousands separators.
        /// The value is rounded to the nearest 1/65536, ties away from zero.
        /// </summary>
        public static Fix Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            if (!TryParse(text, out Fix result))
            {
                throw new FormatException("Not a valid Fix decimal: \"" + text + "\"");
            }
            return result;
        }

        public static bool TryParse(string? text, out Fix result)
        {
            result = Zero;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int pos = 0;
            bool negative = text![0] == '-';
            if (negative)
            {
                pos++;
            }

            // Integer part: at most 2^47 (the magnitude of MinValue).
            const ulong maxIntegerPart = 1UL << 47;
            ulong integerPart = 0;
            int intStart = pos;
            while (pos < text.Length && IsDigit(text[pos]))
            {
                integerPart = integerPart * 10 + (ulong)(text[pos] - '0');
                if (integerPart > maxIntegerPart)
                {
                    return false;
                }
                pos++;
            }
            if (pos == intStart)
            {
                return false;
            }

            // Fraction: keep the first 18 digits (enough for exact nearest rounding, since every
            // rounding boundary k/131072 has at most 17 decimal digits); later digits only need to
            // be validated.
            const int maxFractionDigits = 18;
            ulong fractionDigits = 0;
            int fractionCount = 0;
            if (pos < text.Length)
            {
                if (text[pos] != '.')
                {
                    return false;
                }
                pos++;
                int fracStart = pos;
                while (pos < text.Length && IsDigit(text[pos]))
                {
                    if (fractionCount < maxFractionDigits)
                    {
                        fractionDigits = fractionDigits * 10 + (ulong)(text[pos] - '0');
                        fractionCount++;
                    }
                    pos++;
                }
                if (pos == fracStart || pos != text.Length)
                {
                    return false;
                }
            }

            ulong fractionRaw = DecimalFractionToRaw(fractionDigits, fractionCount);
            ulong magnitude = (integerPart << FractionalBits) + fractionRaw;
            if (!TryFromMagnitude(magnitude, negative, out result))
            {
                result = Zero;
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- operators

        public static Fix operator +(Fix a, Fix b)
        {
            long x = a.Raw, y = b.Raw;
            long r = unchecked(x + y);
            if (((x ^ r) & (y ^ r)) < 0)
            {
                return x < 0 ? MinValue : MaxValue;
            }
            return new Fix(r);
        }

        public static Fix operator -(Fix a, Fix b)
        {
            long x = a.Raw, y = b.Raw;
            long r = unchecked(x - y);
            if (((x ^ y) & (x ^ r)) < 0)
            {
                return x < 0 ? MinValue : MaxValue;
            }
            return new Fix(r);
        }

        /// <summary>Negation. -MinValue saturates to MaxValue.</summary>
        public static Fix operator -(Fix a)
        {
            return a.Raw == long.MinValue ? MaxValue : new Fix(-a.Raw);
        }

        /// <summary>
        /// Exact 128-bit product, rounded to nearest (ties away from zero), saturating on overflow.
        /// </summary>
        public static Fix operator *(Fix a, Fix b)
        {
            long x = a.Raw, y = b.Raw;
            bool negative = (x < 0) ^ (y < 0);
            UInt128Math.Multiply(Magnitude(x), Magnitude(y), out ulong hi, out ulong lo);

            // Add half a raw step, then shift right by 16.
            ulong roundedLo = lo + (ulong)HalfRaw;
            if (roundedLo < lo)
            {
                hi++;
            }
            if ((hi >> (64 - FractionalBits)) != 0)
            {
                return negative ? MinValue : MaxValue;
            }
            ulong magnitude = (hi << (64 - FractionalBits)) | (roundedLo >> FractionalBits);
            return Saturate(magnitude, negative);
        }

        /// <summary>
        /// Exact 128-bit quotient, rounded to nearest (ties away from zero), saturating on overflow.
        /// Throws <see cref="DivideByZeroException"/> when <paramref name="b"/> is zero.
        /// </summary>
        public static Fix operator /(Fix a, Fix b)
        {
            return Divide(a.Raw, b.Raw);
        }

        public static bool operator ==(Fix a, Fix b) => a.Raw == b.Raw;
        public static bool operator !=(Fix a, Fix b) => a.Raw != b.Raw;
        public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
        public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
        public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
        public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;

        public bool Equals(Fix other) => Raw == other.Raw;

        public override bool Equals(object? obj) => obj is Fix other && Raw == other.Raw;

        /// <summary>Deterministic on every runtime (does not defer to long.GetHashCode).</summary>
        public override int GetHashCode() => unchecked((int)Raw ^ (int)(Raw >> 32));

        public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);

        // ---------------------------------------------------------------- functions

        /// <summary>Absolute value. Abs(MinValue) saturates to MaxValue.</summary>
        public static Fix Abs(Fix value) => value.Raw < 0 ? -value : value;

        /// <summary>-1, 0 or 1.</summary>
        public static int Sign(Fix value) => value.Raw > 0 ? 1 : value.Raw < 0 ? -1 : 0;

        public static Fix Min(Fix a, Fix b) => a.Raw <= b.Raw ? a : b;

        public static Fix Max(Fix a, Fix b) => a.Raw >= b.Raw ? a : b;

        /// <summary>Clamps into [min, max]. Throws if min &gt; max.</summary>
        public static Fix Clamp(Fix value, Fix min, Fix max)
        {
            if (min.Raw > max.Raw)
            {
                throw new ArgumentException("Clamp: min must not exceed max.");
            }
            if (value.Raw < min.Raw)
            {
                return min;
            }
            return value.Raw > max.Raw ? max : value;
        }

        /// <summary>Largest integer &lt;= value (toward negative infinity).</summary>
        public static Fix Floor(Fix value) => new Fix(value.Raw & ~FractionMask);

        /// <summary>Smallest integer &gt;= value. Saturates to MaxValue above the largest integer.</summary>
        public static Fix Ceil(Fix value)
        {
            long floor = value.Raw & ~FractionMask;
            if (floor == value.Raw)
            {
                return value;
            }
            if (floor > long.MaxValue - OneRaw)
            {
                return MaxValue;
            }
            return new Fix(floor + OneRaw);
        }

        /// <summary>
        /// Nearest integer, ties away from zero (2.5 -> 3, -2.5 -> -3). Note this differs from
        /// System.Math.Round's default banker's rounding.
        /// </summary>
        public static Fix Round(Fix value)
        {
            if ((value.Raw & FractionMask) == 0)
            {
                return value;
            }
            if (value.Raw >= 0)
            {
                if (value.Raw > long.MaxValue - HalfRaw)
                {
                    return MaxValue;
                }
                return new Fix((value.Raw + HalfRaw) & ~FractionMask);
            }
            return -Round(-value);
        }

        // Integer conversions throw OverflowException when the result is outside the int range.

        public static int FloorToInt(Fix value) => checked((int)(value.Raw >> FractionalBits));

        public static int CeilToInt(Fix value) => checked((int)(Ceil(value).Raw >> FractionalBits));

        public static int RoundToInt(Fix value) => checked((int)(Round(value).Raw >> FractionalBits));

        /// <summary>
        /// Square root rounded to the nearest 1/65536, by integer Newton iteration on a 128-bit
        /// intermediate. Throws <see cref="ArgumentOutOfRangeException"/> for negative input.
        /// </summary>
        public static Fix Sqrt(Fix value)
        {
            if (value.Raw < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Sqrt of a negative Fix.");
            }
            // sqrt(raw / 2^16) * 2^16 = sqrt(raw * 2^16)
            ulong raw = (ulong)value.Raw;
            ulong hi = raw >> (64 - FractionalBits);
            ulong lo = raw << FractionalBits;
            return new Fix((long)UInt128Math.SqrtRound(hi, lo));
        }

        /// <summary>a + (b - a) * t. <paramref name="t"/> is not clamped.</summary>
        public static Fix Lerp(Fix a, Fix b, Fix t) => a + (b - a) * t;

        // ---------------------------------------------------------------- conversion

        /// <summary>
        /// VIEW-ONLY conversion for the Unity client (positions, bars, labels). Floats are not
        /// deterministic across platforms: nothing inside the sim may call this, and a float value
        /// must never flow back into the sim. Enforced by the float guard test.
        /// </summary>
        public float ToFloat() => Raw / (float)OneRaw;

        /// <summary>
        /// Shortest decimal string that parses back to exactly this value (invariant culture),
        /// e.g. "1.5", "-0.25", "0.1".
        /// </summary>
        public override string ToString()
        {
            bool negative = Raw < 0;
            ulong magnitude = Magnitude(Raw);
            ulong integerPart = magnitude >> FractionalBits;
            ulong fraction = magnitude & (ulong)FractionMask;

            var sb = new StringBuilder(24);
            if (negative)
            {
                sb.Append('-');
            }
            sb.Append(integerPart.ToString(CultureInfo.InvariantCulture));
            if (fraction == 0)
            {
                return sb.ToString();
            }

            // Five decimal digits always suffice (10^5 > 2^16), so this loop always returns.
            ulong scale = 1;
            for (int digits = 1; digits <= 5; digits++)
            {
                scale *= 10;
                ulong decimalValue = RoundDiv(fraction * scale, (ulong)OneRaw);
                if (decimalValue < scale && DecimalFractionToRaw(decimalValue, digits) == fraction)
                {
                    sb.Append('.');
                    string text = decimalValue.ToString(CultureInfo.InvariantCulture);
                    sb.Append('0', digits - text.Length);
                    sb.Append(text.TrimEnd('0'));
                    break;
                }
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- helpers

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        /// <summary>|x| as ulong; |long.MinValue| = 2^63 is representable.</summary>
        private static ulong Magnitude(long x) => x < 0 ? unchecked((ulong)(-x)) : (ulong)x;

        private static Fix Saturate(ulong magnitude, bool negative)
        {
            if (TryFromMagnitude(magnitude, negative, out Fix result))
            {
                return result;
            }
            return negative ? MinValue : MaxValue;
        }

        private static bool TryFromMagnitude(ulong magnitude, bool negative, out Fix result)
        {
            const ulong minMagnitude = 1UL << 63;
            if (negative)
            {
                if (magnitude > minMagnitude)
                {
                    result = MinValue;
                    return false;
                }
                result = new Fix(unchecked(-(long)magnitude));
                return true;
            }
            if (magnitude > long.MaxValue)
            {
                result = MaxValue;
                return false;
            }
            result = new Fix((long)magnitude);
            return true;
        }

        /// <summary>round(n / d), ties away from zero, for small unsigned values.</summary>
        private static ulong RoundDiv(ulong n, ulong d)
        {
            ulong q = n / d;
            ulong r = n % d;
            return r >= d - r ? q + 1 : q;
        }

        /// <summary>Raw fraction for digits / 10^count, rounded to nearest (ties away from zero).</summary>
        private static ulong DecimalFractionToRaw(ulong digits, int count)
        {
            if (count == 0)
            {
                return 0;
            }
            ulong denominator = 1;
            for (int i = 0; i < count; i++)
            {
                denominator *= 10;
            }
            // digits * 2^16 can exceed 64 bits when count is large; use a 128-bit numerator.
            ulong hi = digits >> (64 - FractionalBits);
            ulong lo = digits << FractionalBits;
            ulong q = UInt128Math.DivRem(hi, lo, denominator, out ulong r);
            return r >= denominator - r ? q + 1 : q;
        }

        /// <summary>round(n * 2^16 / d) with saturation; the shared core of / and FromFraction.</summary>
        private static Fix Divide(long n, long d)
        {
            if (d == 0)
            {
                throw new DivideByZeroException("Fix division by zero.");
            }
            bool negative = (n < 0) ^ (d < 0);
            ulong un = Magnitude(n);
            ulong ud = Magnitude(d);

            ulong hi = un >> (64 - FractionalBits);
            ulong lo = un << FractionalBits;
            if (hi >= ud)
            {
                return negative ? MinValue : MaxValue; // quotient needs more than 64 bits
            }

            ulong q = UInt128Math.DivRem(hi, lo, ud, out ulong r);
            if (q > 1UL << 63)
            {
                return negative ? MinValue : MaxValue;
            }
            if (r >= ud - r)
            {
                q++;
            }
            return Saturate(q, negative);
        }
    }
}
