using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Runtime.InteropServices;

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
    public BoundingBox(Vector3[] points, int point_count) : this(new ReadOnlySpan<Vector3>(points, 0, point_count)) { }
    public BoundingBox(ReadOnlySpan<Vector3> points)
    {
        m_min = Math.Vector3.Infinity;
        m_max = Math.Vector3.InfinityNeg;

        foreach (Vector3 p in points)
        {
            m_min = Vector3.Min(m_min, p);
            m_max = Vector3.Max(m_max, p);
        }
    }
    public BoundingBox(List<RHI_Vertex_PosTexNorTan> vertices) : this(CollectionsMarshal.AsSpan(vertices)) { }
    public BoundingBox(ReadOnlySpan<RHI_Vertex_PosTexNorTan> vertices)
    {
        m_min = Math.Vector3.Infinity;
        m_max = Math.Vector3.InfinityNeg;

        foreach (ref readonly RHI_Vertex_PosTexNorTan v in vertices)
        {
            m_min = Vector3.Min(m_min, v.pos);
            m_max = Vector3.Max(m_max, v.pos);
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
        m_min = Vector3.Min(m_min, box.m_min);
        m_max = Vector3.Max(m_max, box.m_max);
    }

    public readonly Vector3 GetMin() { return m_min; }
    public readonly Vector3 GetMax() { return m_max; }
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
        return Vector3.Clamp(point, m_min, m_max);
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
