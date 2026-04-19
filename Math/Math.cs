using System;
using System.Runtime.CompilerServices;

namespace Phalanx;
public static class Math
{
    public enum Intersection
    {
        Outside,
        Inside,
        Intersects
    };

    public const float Pi = 3.14159265359f;
    public const float Pi2 = 6.28318530718f;
    public const float Pi4 = 12.5663706144f;
    public const float PiDiv2 = 1.57079632679f;
    public const float PiDiv4 = 0.78539816339f;
    public const float PiInv = 0.31830988618f;
    public const float DegToRad = Pi / 180.0f;
    public const float RadToDeg = 180.0f / Pi;
    public const float Epsilon = 1E-05f;

    private static readonly float epsilon = MathF.Pow(2.0f, -23);
    public static float FloatEpsilon => epsilon;

    public static float Saturate(float x) { return System.Math.Clamp(x, 0, 1); }
    public static double Saturate(double x) { return System.Math.Clamp(x, 0, 1); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float lhs, float rhs, float t) { return lhs * (1 - t) + rhs * t; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Lerp(double lhs, double rhs, double t) { return lhs * (1 - t) + rhs * t; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ApproximateEquals(float lhs, float rhs, float error = 1E-06f) { return lhs + error >= rhs && lhs - error <= rhs; }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ApproximateEquals(double lhs, double rhs, double error = 1E-14) { return lhs + error >= rhs && lhs - error <= rhs; }
    public static int Sign (float x) { return System.Math.Sign(x); }
    public static int Sign(double x) { return System.Math.Sign(x); }

    public static int PowerOfTwoPrevious(int x)
    {
        x = x | (x >> 1);
        x = x | (x >> 2);
        x = x | (x >> 4);
        x = x | (x >> 8);
        x = x | (x >> 16);
        return x - (x >> 1);
    }

    public static int PowerOfTwoNext(int x)
    {
        if (x < 2)
            return 2;

        if (x % 2 == 0)
            return x << 1;

        x--;
        x |= x >> 1;
        x |= x >> 2;
        x |= x >> 4;
        x |= x >> 8;
        x |= x >> 16;
        x++;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(float s)
    {
        return float.IsFinite(s);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(double s)
    {
        return double.IsFinite(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInf(float s)
    {
        return float.IsInfinity(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInf(double s)
    {
        return double.IsInfinity(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNaN(float s)
    {
        return float.IsNaN(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNaN(double s)
    {
        return double.IsNaN(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sqrt(float s)
    {
        return MathF.Sqrt(s);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Sqrt(double s)
    {
        return System.Math.Sqrt(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Abs(float s)
    {
        return Math.Abs(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Abs(double s)
    {
        return Math.Abs(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Min(int a, int b)
    {
        return System.Math.Min(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(float a, float b)
    {
        return System.Math.Min(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Min(double a, double b)
    {
        return System.Math.Min(a, b);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Max(int a, int b)
    {
        return System.Math.Max(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(float a, float b)
    {
        return System.Math.Max(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Max(double a, double b)
    {
        return System.Math.Max(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Floor(float s)
    {
        return MathF.Floor(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Floor(double s)
    {
        return System.Math.Floor(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Round(float s)
    {
        return MathF.Round(s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Round(double s)
    {
        return System.Math.Round(s);
    }
}
