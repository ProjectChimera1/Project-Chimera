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

        /// <summary>Squared magnitude (avoids sqrt).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Fixed SqrMagnitude() => Dot(this, this);

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

        /// <summary>Squared distance between two points (avoids sqrt).</summary>
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

        /// <summary>Convert to Godot Vector3 for rendering. Only use in presentation layer.</summary>
        public Godot.Vector3 ToGodotVector3() =>
            new Godot.Vector3(X.ToFloat(), Y.ToFloat(), Z.ToFloat());

        public static FixedVec3 FromGodotVector3(Godot.Vector3 v) =>
            new FixedVec3(Fixed.FromFloat(v.X), Fixed.FromFloat(v.Y), Fixed.FromFloat(v.Z));

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
