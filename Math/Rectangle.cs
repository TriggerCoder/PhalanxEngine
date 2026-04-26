using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Rectangle : IEquatable<Rectangle>
{
    public float X;
    public float Y;
    public float Width;
    public float Height;

    public Rectangle()
    {
        X = 0;
        Y = 0;
        Width = 0;
        Height = 0;
    }
    public Rectangle(float X, float Y, float Width, float Height)
    {
        this.X = X;
        this.Y = Y;
        this.Width = Width;
        this.Height = Height;
    }
    public Rectangle(Rectangle rectangle)
    {
        X = rectangle.X;
        Y = rectangle.Y;
        Width = rectangle.Width;
        Height = rectangle.Height;
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
    public readonly bool Equals(Rectangle other) { return (X == other.X) && (Y == other.Y) && (Width == other.Width) && (Height == other.Height);}

    public override readonly int GetHashCode() { return HashCode.Combine(X, Y, Width, Height); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsDefined() { return Width > 0.0f && Height > 0.0f; }
    public void Merge(Vector2 point)
    {
        float min_x = X;
        float min_y = Y;
        float max_x = X + Width;
        float max_y = Y + Height;

        min_x = Math.Min(min_x, point.X);
        min_y = Math.Min(min_y, point.Y);
        max_x = Math.Max(max_x, point.X);
        max_y = Math.Max(max_y, point.Y);

        X = min_x;
        Y = min_y;
        Width = max_x - min_x;
        Height = max_y - min_y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Intersects(Rectangle other)
    {
        return !((X + Width < other.X) ||
                 (other.X + other.Width < X) ||
                 (Y + Height < other.Y) ||
                 (other.Y + other.Height < Y));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Contains(Rectangle other)
    {
        return (X <= other.X) &&
               (Y <= other.Y) &&
               (X + Width >= other.X + other.Width) &&
               (Y + Height >= other.Y + other.Height);
    }

    private static readonly Rectangle zero = new Rectangle(0f, 0f, 0f, 0f);
    public static Rectangle Zero => zero;

}