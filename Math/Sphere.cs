using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Sphere : IEquatable<Sphere>
{
    public Vector3 center;
    public float radius;
    public Sphere()
    {
        center = Vector3.Zero;
        radius = 1;
    }
    public Sphere(Vector3 center, float radius)
    {
        this.center = center;
        this.radius = radius;
    }

    public static bool operator ==(Sphere l, Sphere r) { return l.Equals(r); }
    public static bool operator !=(Sphere l, Sphere r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Sphere other)
            return Equals(other);
        return false;
    }
    public override readonly int GetHashCode() { return HashCode.Combine(center, radius); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Sphere other) { return Math.ApproximateEquals(radius, other.radius) && (center == other.center); }
}