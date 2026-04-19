using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Vector2 : IEquatable<Vector2>
{
    public float x;
    public float y;

    public Vector2()
    {
        x = 0;
        y = 0;
    }
    public Vector2(float X)
    {
        x = X;
        y = X;
    }

    public Vector2(float X, float Y)
    {
        x = X;
        y = Y;
    }
    public static Vector2 operator +(Vector2 l, Vector2 r)
    {
        l.x += r.x;
        l.y += r.y;
        return l;
    }
    public static Vector2 operator -(Vector2 l, Vector2 r)
    {
        l.x -= r.x;
        l.y -= r.y;
        return l;
    }
    public static Vector2 operator -(Vector2 other)
    {
        other.x = 0f - other.x;
        other.y = 0f - other.y;
        return other;
    }
    public static Vector2 operator *(Vector2 other, float scale)
    {
        other.x *= scale;
        other.y *= scale;
        return other;
    }
    public static Vector2 operator *(float scale, Vector2 other)
    {
        other.x *= scale;
        other.y *= scale;
        return other;
    }
    public static Vector2 operator *(Vector2 l, Vector2 r)
    {
        l.x *= r.x;
        l.y *= r.y;
        return l;
    }
    public static Vector2 operator /(Vector2 other, float divisor)
    {
        other.x /= divisor;
        other.y /= divisor;
        return other;
    }
    public static Vector2 operator /(Vector2 other, Vector2 divisorv)
    {
        other.x /= divisorv.x;
        other.y /= divisorv.y;
        return other;
    }
    public static Vector2 operator %(Vector2 other, float divisor)
    {
        other.x %= divisor;
        other.y %= divisor;
        return other;
    }
    public static Vector2 operator %(Vector2 other, Vector2 divisorv)
    {
        other.x %= divisorv.x;
        other.y %= divisorv.y;
        return other;
    }
    public static bool operator ==(Vector2 l, Vector2 r) { return l.Equals(r); }
    public static bool operator !=(Vector2 l, Vector2 r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Vector2 other)
            return Equals(other);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Vector2 other) { return Math.ApproximateEquals(x, other.x) && Math.ApproximateEquals(y, other.y); }
    public override readonly int GetHashCode() { return HashCode.Combine(x, y); }
    public readonly float Length() { return Math.Sqrt(x * x + y * y); }
    public readonly float LengthSquared() { return x* x + y* y; }

    public readonly Vector2 Max(float other) { return new Vector2(Math.Max(x, other), Math.Max(y, other)); }
    public readonly Vector2 Max(Vector2 other) { return new Vector2(Math.Max(x, other.x), Math.Max(y, other.y)); }
    public static Vector2 Max(Vector2 a, Vector2 b) { return new Vector2(Math.Max(a.x, b.x), Math.Max(a.y, b.y)); }
    public readonly Vector2 Min(float other) { return new Vector2(Math.Min(x, other), Math.Min(y, other)); }
    public readonly Vector2 Min(Vector2 other) { return new Vector2(Math.Min(x, other.x), Math.Min(y, other.y)); }
    public static Vector2 Min(Vector2 a, Vector2 b) { return new Vector2(Math.Min(a.x, b.x), Math.Min(a.y, b.y)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Distance(Vector2 other) { return Math.Sqrt((x - other.x) * (x - other.x) + (y - other.y) * (y - other.y)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(Vector2 a, Vector2 b) { return Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float DistanceSquared(Vector2 other) { return (x - other.x) * (x - other.x) + (y - other.y) * (y - other.y); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(Vector2 a, Vector2 b) { return (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y); }
    internal void Normalize()
    {
        float length = Length();
        if (length > 0.0001f) // Avoid division by zero
        {
            x /= length;
            y /= length;
        }
        else
        {
            x = 0.0f;
            y = 0.0f;
        }
    }
    public readonly Vector2 Normalized()
    {
        Vector2 result = this;
        result.Normalize();
        return result;
    }

    public override readonly string ToString() { return "X: " + x.ToString() + ", Y:" + y.ToString(); }

    private static readonly Vector2 zero = new Vector2(0f, 0f);
    private static readonly Vector2 one = new Vector2(1f, 1f);
    public static Vector2 Zero => zero;
    public static Vector2 One => one;
}
