using System;
using System.Runtime.CompilerServices;

namespace ProjectChimera.Core
{
    /// <summary>
    /// 16.16 fixed-point number for deterministic simulation math.
    /// Raw value: upper 16 bits = integer, lower 16 bits = fraction.
    /// </summary>
    public readonly struct Fixed : IEquatable<Fixed>, IComparable<Fixed>
    {
        public const int FRACTIONAL_BITS = 16;
        public const int ONE = 1 << FRACTIONAL_BITS; // 65536
        public const int HALF = ONE >> 1;             // 32768

        public readonly int Raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Fixed(int raw) => Raw = raw;

        // --- Factory methods ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed FromInt(int value) => new Fixed(value << FRACTIONAL_BITS);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed FromFloat(float value) => new Fixed((int)(value * ONE));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed FromRaw(int raw) => new Fixed(raw);

        // --- Common constants ---

        public static readonly Fixed Zero = new Fixed(0);
        public static readonly Fixed One = new Fixed(ONE);
        public static readonly Fixed Half = new Fixed(HALF);
        public static readonly Fixed NegOne = new Fixed(-ONE);
        public static readonly Fixed MaxValue = new Fixed(int.MaxValue);
        public static readonly Fixed MinValue = new Fixed(int.MinValue);
        public static readonly Fixed Epsilon = new Fixed(1);

        // Pi ≈ 3.14159265 → 3.14159265 * 65536 ≈ 205887
        public static readonly Fixed Pi = new Fixed(205887);
        // 2*Pi
        public static readonly Fixed TwoPi = new Fixed(411775);
        // Pi/2
        public static readonly Fixed HalfPi = new Fixed(102944);

        // --- Conversion ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ToInt() => Raw >> FRACTIONAL_BITS;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ToFloat() => (float)Raw / ONE;

        // --- Arithmetic operators ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed operator +(Fixed a, Fixed b) => new Fixed(a.Raw + b.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed operator -(Fixed a, Fixed b) => new Fixed(a.Raw - b.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed operator -(Fixed a) => new Fixed(-a.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed operator *(Fixed a, Fixed b) =>
            new Fixed((int)(((long)a.Raw * b.Raw) >> FRACTIONAL_BITS));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed operator /(Fixed a, Fixed b) =>
            new Fixed((int)(((long)a.Raw << FRACTIONAL_BITS) / b.Raw));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed operator %(Fixed a, Fixed b) => new Fixed(a.Raw % b.Raw);

        // --- Comparison operators ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Fixed a, Fixed b) => a.Raw == b.Raw;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Fixed a, Fixed b) => a.Raw != b.Raw;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(Fixed a, Fixed b) => a.Raw < b.Raw;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(Fixed a, Fixed b) => a.Raw > b.Raw;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(Fixed a, Fixed b) => a.Raw <= b.Raw;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(Fixed a, Fixed b) => a.Raw >= b.Raw;

        // --- Implicit conversions from int ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Fixed(int value) => FromInt(value);

        // --- Math utilities ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed Abs(Fixed a) => new Fixed(Math.Abs(a.Raw));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed Min(Fixed a, Fixed b) => a.Raw < b.Raw ? a : b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed Max(Fixed a, Fixed b) => a.Raw > b.Raw ? a : b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed Clamp(Fixed value, Fixed min, Fixed max) =>
            Max(min, Min(max, value));

        /// <summary>
        /// DW-28 saturating add: widens to a 64-bit accumulator, sums, and clamps to <c>[int.MinValue, int.MaxValue]</c>
        /// BEFORE constructing the <see cref="Fixed"/> — so a SINGLE <c>Base + already-summed bonus</c> read saturates at
        /// <see cref="MaxValue"/>/<see cref="MinValue"/> instead of wrapping negative like the unchecked int
        /// <c>operator+</c>. Integer-only and deterministic. Used by the effective-stat recompute (which passes the base
        /// and the net bonus). NOTE: this does NOT saturate a modifier STACK — the per-stack bonus is accumulated in
        /// <c>ModifierSystem.AccumulateBonus</c> via the wrapping <c>+=</c>, so <see cref="AddSaturating"/> only ever sees
        /// the already-summed bonus; a pathological stack could still wrap the accumulator itself (tracked as deferred
        /// work). The wrapping <c>operator+</c> stays as-is for all other arithmetic (a widen-then-clamp cannot recover a
        /// value that has ALREADY wrapped in the int add, so the saturation must live in the sum itself).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed AddSaturating(Fixed a, Fixed b)
        {
            long sum = (long)a.Raw + b.Raw;
            if (sum > int.MaxValue) sum = int.MaxValue;
            else if (sum < int.MinValue) sum = int.MinValue;
            return new Fixed((int)sum);
        }

        /// <summary>
        /// Integer square root via Newton's method in fixed-point.
        /// </summary>
        public static Fixed Sqrt(Fixed a)
        {
            if (a.Raw <= 0) return Zero;

            // Initial guess: shift right by half the fractional bits
            long raw = (long)a.Raw << FRACTIONAL_BITS;
            long guess = (long)a.Raw;
            if (guess == 0) return Zero;

            // Newton iterations
            for (int i = 0; i < 8; i++)
            {
                guess = (guess + raw / guess) >> 1;
            }

            return new Fixed((int)guess);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed Lerp(Fixed a, Fixed b, Fixed t) =>
            a + (b - a) * t;

        // --- IEquatable / IComparable ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Fixed other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Fixed f && Raw == f.Raw;
        public override int GetHashCode() => Raw;
        public int CompareTo(Fixed other) => Raw.CompareTo(other.Raw);

        public override string ToString() => ToFloat().ToString("F4");
    }

    /// <summary>
    /// 3D vector using Fixed-point components for deterministic simulation.
    /// </summary>
    public readonly struct FixedVec3 : IEquatable<FixedVec3>
    {
        public readonly Fixed X;
        public readonly Fixed Y;
        public readonly Fixed Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FixedVec3(Fixed x, Fixed y, Fixed z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly FixedVec3 Zero = new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        public static readonly FixedVec3 One = new FixedVec3(Fixed.One, Fixed.One, Fixed.One);
        public static readonly FixedVec3 Up = new FixedVec3(Fixed.Zero, Fixed.One, Fixed.Zero);
        public static readonly FixedVec3 Forward = new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.NegOne);

        // --- Arithmetic ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 operator +(FixedVec3 a, FixedVec3 b) =>
            new FixedVec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 operator -(FixedVec3 a, FixedVec3 b) =>
            new FixedVec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 operator -(FixedVec3 a) =>
            new FixedVec3(-a.X, -a.Y, -a.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 operator *(FixedVec3 a, Fixed scalar) =>
            new FixedVec3(a.X * scalar, a.Y * scalar, a.Z * scalar);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 operator *(Fixed scalar, FixedVec3 a) =>
            new FixedVec3(a.X * scalar, a.Y * scalar, a.Z * scalar);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 operator /(FixedVec3 a, Fixed scalar) =>
            new FixedVec3(a.X / scalar, a.Y / scalar, a.Z / scalar);

        // --- Vector ops ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed Dot(FixedVec3 a, FixedVec3 b) =>
            a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 Cross(FixedVec3 a, FixedVec3 b) =>
            new FixedVec3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X
            );

        /// <summary>
        /// Squared magnitude (avoids sqrt), computed in a LONG accumulator and SATURATED at
        /// <see cref="Fixed.MaxValue"/> — never wrapping negative.
        ///
        /// <para><b>Why this is not <c>Dot(this, this)</c>.</b> A 16.16 <c>Fixed operator*</c> truncates to int32
        /// (<c>(int)(((long)a.Raw * b.Raw) &gt;&gt; 16)</c>) and <c>operator+</c> wraps, so the naive
        /// X²+Y²+Z² overflows once a single axis passes <b>~181 units</b>: a 250-unit leg needs
        /// 250²·65536 = 4,096,000,000, which does not fit in int32 and lands at <b>−198,967,296</b> — a NEGATIVE
        /// value that compares <c>&lt;=</c> every radius. Every "is it in range" / "have I arrived" test built on
        /// this helper therefore read TRUE across the whole map. <c>MapSize.Large</c> is a 128 HALF-extent and
        /// <c>ScenarioData.MapBounds</c> defaults to 120, so 240–256-unit spans are ordinary geometry, not an
        /// edge case. (DW-688 / DW-764 are the filed instances; <see cref="ProjectChimera.Combat.HeroXpSystem"/>
        /// hand-rolled this same widening locally and named the hazard, but nothing generalised it — which is
        /// why the defect kept being re-introduced at new call sites.)
        ///
        /// <para><b>Byte-compatibility.</b> Each squared term is shifted INDIVIDUALLY before summing — matching
        /// <c>Fixed operator*</c>'s <c>(raw·raw)&gt;&gt;16</c> exactly — rather than summing then shifting once.
        /// Each term is a square and therefore non-negative, so the arithmetic shift is exact floor division and
        /// the result is BIT-IDENTICAL to the old path for every separation that did not already overflow. Only
        /// the previously-wrapping cases change, and they change from a wrong answer to a correct one. Each
        /// pre-shift term is at most ~2^62 and each post-shift term at most ~2^46, so the three-way long sum
        /// cannot itself overflow.</para>
        ///
        /// <para>Saturating (the <see cref="Fixed.AddSaturating"/> / DW-28 posture) rather than throwing: a
        /// clamped MaxValue is out of range for every radius in the game, which is the correct answer, and it
        /// keeps the sim branch-free and deterministic. Integer-only → cross-platform safe.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Fixed SqrMagnitude()
        {
            long x = (long)X.Raw * X.Raw;
            long y = (long)Y.Raw * Y.Raw;
            long z = (long)Z.Raw * Z.Raw;
            long sum = (x >> Fixed.FRACTIONAL_BITS) + (y >> Fixed.FRACTIONAL_BITS) + (z >> Fixed.FRACTIONAL_BITS);
            return sum >= int.MaxValue ? Fixed.MaxValue : new Fixed((int)sum);
        }

        /// <summary>Magnitude via fixed-point sqrt.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Fixed Magnitude() => Fixed.Sqrt(SqrMagnitude());

        /// <summary>Returns normalized vector. Returns Zero if magnitude is zero.</summary>
        public FixedVec3 Normalized()
        {
            Fixed mag = Magnitude();
            if (mag == Fixed.Zero) return Zero;
            return this / mag;
        }

        /// <summary>
        /// Squared distance between two points (avoids sqrt). Overflow-safe: routes through
        /// <see cref="SqrMagnitude"/>, which accumulates in <c>long</c> and saturates instead of wrapping
        /// negative past ~181 units. Do NOT reimplement this as <c>Dot(d, d)</c> at a call site — that is the
        /// exact shape that made every long-range range/arrival test read TRUE (see <see cref="SqrMagnitude"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed SqrDistance(FixedVec3 a, FixedVec3 b) =>
            (b - a).SqrMagnitude();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fixed Distance(FixedVec3 a, FixedVec3 b) =>
            (b - a).Magnitude();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 Lerp(FixedVec3 a, FixedVec3 b, Fixed t) =>
            new FixedVec3(
                Fixed.Lerp(a.X, b.X, t),
                Fixed.Lerp(a.Y, b.Y, t),
                Fixed.Lerp(a.Z, b.Z, t)
            );

        // --- Conversions to Godot types (presentation layer only) ---
#if GODOT
        /// <summary>Convert to Godot Vector3 for rendering. Only use in presentation layer.</summary>
        public Godot.Vector3 ToGodotVector3() =>
            new Godot.Vector3(X.ToFloat(), Y.ToFloat(), Z.ToFloat());

        public static FixedVec3 FromGodotVector3(Godot.Vector3 v) =>
            new FixedVec3(Fixed.FromFloat(v.X), Fixed.FromFloat(v.Y), Fixed.FromFloat(v.Z));
#endif

        // --- Equality ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(FixedVec3 a, FixedVec3 b) =>
            a.X == b.X && a.Y == b.Y && a.Z == b.Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(FixedVec3 a, FixedVec3 b) => !(a == b);

        public bool Equals(FixedVec3 other) => this == other;
        public override bool Equals(object obj) => obj is FixedVec3 v && this == v;
        public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw, Z.Raw);

        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}
