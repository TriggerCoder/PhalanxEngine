using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace Phalanx;

[Serializable]
public struct BoundingBox : IEquatable<BoundingBox> 
{
    private Vector3 m_min;
    private Vector3 m_max;
    public BoundingBox()
    {
        m_min = Math.Vector3.Infinity;
        m_max = Math.Vector3.InfinityNeg;
    }
    public BoundingBox(Vector3 min, Vector3 max)
    {
        m_min = min;
        m_max = max;
    }
    public BoundingBox(Vector3[] points, int point_count)
    {
        m_min = Math.Vector3.Infinity;
        m_max = Math.Vector3.InfinityNeg;

        for (int i = 0; i < point_count; i++)
        {
            m_max.X = Math.Max(m_max.X, points[i].X);
            m_max.Y = Math.Max(m_max.Y, points[i].Y);
            m_max.Z = Math.Max(m_max.Z, points[i].Z);

            m_min.X = Math.Min(m_min.X, points[i].X);
            m_min.Y = Math.Min(m_min.Y, points[i].Y);
            m_min.Z = Math.Min(m_min.Z, points[i].Z);
        }
    }
    public static bool operator ==(BoundingBox l, BoundingBox r) { return l.Equals(r); }
    public static bool operator !=(BoundingBox l, BoundingBox r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is BoundingBox other)
            return Equals(other);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(BoundingBox other) { return (m_min == other.m_min) && (m_max == other.m_max); }
    public override readonly int GetHashCode() { return HashCode.Combine(m_min, m_max); }

    public readonly Math.Intersection Intersects(Vector3 point)
    {
        if (point.X < m_min.X || point.X > m_max.X ||
            point.Y < m_min.Y || point.Y > m_max.Y ||
            point.Z < m_min.Z || point.Z > m_max.Z)
            return Math.Intersection.Outside;

        return Math.Intersection.Inside;
    }
    public readonly Math.Intersection Intersects(BoundingBox box)
    {
        if (box.m_max.X < m_min.X || box.m_min.X > m_max.X ||
            box.m_max.Y < m_min.Y || box.m_min.Y > m_max.Y ||
            box.m_max.Z < m_min.Z || box.m_min.Z > m_max.Z)
                return Math.Intersection.Outside;
        else if (box.m_min.X < m_min.X || box.m_max.X > m_max.X ||
                 box.m_min.Y < m_min.Y || box.m_max.Y > m_max.Y ||
                 box.m_min.Z < m_min.Z || box.m_max.Z > m_max.Z)
            return Math.Intersection.Intersects;
        return Math.Intersection.Inside;
    }
    public void Merge(BoundingBox box)
    {
        m_min.X = Math.Min(m_min.X, box.m_min.X);
        m_min.Y = Math.Min(m_min.Y, box.m_min.Y);
        m_min.Z = Math.Min(m_min.Z, box.m_min.Z);

        m_max.X = Math.Max(m_max.X, box.m_max.X);
        m_max.Y = Math.Max(m_max.Y, box.m_max.Y);
        m_max.Z = Math.Max(m_max.Z, box.m_max.Z);
    }

    public Vector3 GetMin() { return m_min; }
    public Vector3 GetMax() { return m_max; }
    public readonly Vector3 GetCenter() { return (m_max + m_min) * 0.5f; }
    public readonly Vector3 GetSize() { return m_max - m_min; }
    public readonly Vector3 GetExtents() { return (m_max - m_min) * 0.5f; }
    public readonly float GetVolume()
    {
        Vector3 size = GetSize();
        return size.X * size.Y * size.Z;
    }
    public readonly Vector3 GetClosestPoint(Vector3 point)
    {
        return new Vector3(Math.Max(m_min.X, Math.Min(point.X, m_max.X)),
                           Math.Max(m_min.Y, Math.Min(point.Y, m_max.Y)),
                           Math.Max(m_min.Z, Math.Min(point.Z, m_max.Z)));
    }
    public readonly bool Contains(Vector3 point)
    {
        return  (point.X >= m_min.X && point.X <= m_max.X) &&
                (point.Y >= m_min.Y && point.Y <= m_max.Y) &&
                (point.Z >= m_min.Z && point.Z <= m_max.Z);
    }

    private static readonly BoundingBox zero = new BoundingBox (Vector3.Zero, Vector3.Zero);
    private static readonly BoundingBox unit = new BoundingBox (Vector3.One * -0.5f, Vector3.One * 0.5f);
    private static readonly BoundingBox infinite = new BoundingBox(Math.Vector3.InfinityNeg, Math.Vector3.Infinity);

    public static BoundingBox Zero => zero;
    public static BoundingBox Unit => unit;
    public static BoundingBox Infinite => infinite;
}
