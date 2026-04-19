using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Rectangle : IEquatable<Rectangle>
{
    public float x;
    public float y;
    public float width;
    public float height;

    public Rectangle()
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;
    }
    public Rectangle(float x, float y, float width, float height)
    {
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }
    public Rectangle(Rectangle rectangle)
    {
        x = rectangle.x;
        y = rectangle.y;
        width = rectangle.width;
        height = rectangle.height;
    }

    public static bool operator ==(Rectangle l, Rectangle r) { return l.Equals(r); }
    public static bool operator !=(Rectangle l, Rectangle r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Rectangle other)
            return Equals(other);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Rectangle other) { return (x == other.x) && (y == other.y) && (width == other.width) && (height == other.height);}

    public override readonly int GetHashCode() { return HashCode.Combine(x, y, width, height); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsDefined() { return width > 0.0f && height > 0.0f; }
    public void Merge(Vector2 point)
    {
        float min_x = x;
        float min_y = y;
        float max_x = x + width;
        float max_y = y + height;

        min_x = Math.Min(min_x, point.x);
        min_y = Math.Min(min_y, point.y);
        max_x = Math.Max(max_x, point.x);
        max_y = Math.Max(max_y, point.y);

        x = min_x;
        y = min_y;
        width = max_x - min_x;
        height = max_y - min_y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Intersects(Rectangle other)
    {
        return !((x + width < other.x) ||
                 (other.x + other.width < x) ||
                 (y + height < other.y) ||
                 (other.y + other.height < y));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Contains(Rectangle other)
    {
        return (x <= other.x) &&
               (y <= other.y) &&
               (x + width >= other.x + other.width) &&
               (y + height >= other.y + other.height);
    }

    private static readonly Rectangle zero = new Rectangle(0f, 0f, 0f, 0f);
    public static Rectangle Zero => zero;

}