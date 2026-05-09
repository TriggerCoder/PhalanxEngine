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
    public const float NegativeInfinity = (float)-1.0 / (float)0.0;
    public const float PositiveInfinity = (float)1.0 / (float)0.0;
    public const float NaN = (float)0.0 / (float)0.0;

    private static readonly float epsilon = MathF.Pow(2.0f, -23);
    public static float FloatEpsilon => epsilon;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saturate(float x) { return System.Math.Clamp(x, 0, 1); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] 
    public static double Saturate(double x) { return System.Math.Clamp(x, 0, 1); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float lhs, float rhs, float t) { return lhs * (1 - t) + rhs * t; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Lerp(double lhs, double rhs, double t) { return lhs * (1 - t) + rhs * t; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ApproximateEquals(float lhs, float rhs, float error = 1E-06f) { return lhs + error >= rhs && lhs - error <= rhs; }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ApproximateEquals(double lhs, double rhs, double error = 1E-14) { return lhs + error >= rhs && lhs - error <= rhs; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] 
    public static int Sign (float x) { return System.Math.Sign(x); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] 
    public static int Sign(double x) { return System.Math.Sign(x); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Rand(float from = 0f, float to = 1f) { return Random.Shared.NextSingle() * (to - from) + from; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Rand(double from = 0.0, double to = 1.0) { return Random.Shared.NextDouble() * (to - from) + from; }

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
    public static bool IsFinite(float s) { return float.IsFinite(s); }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(double s) { return double.IsFinite(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInf(float s) { return float.IsInfinity(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInf(double s) { return double.IsInfinity(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNaN(float s) { return float.IsNaN(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNaN(double s) { return double.IsNaN(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sqrt(float s) { return MathF.Sqrt(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Sqrt(double s) { return System.Math.Sqrt(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Abs(float s) { return System.Math.Abs(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Abs(double s) { return System.Math.Abs(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(float value, float min, float max) { return System.Math.Clamp(value, min, max); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clamp(double value, double min, double max) { return System.Math.Clamp(value, min, max); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Log(float s) { return MathF.Log(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Log(double s) { return System.Math.Log(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Log2(float s) { return MathF.Log2(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Log2(double s) { return System.Math.Log2(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Min(int a, int b) { return System.Math.Min(a, b); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(float a, float b) { return System.Math.Min(a, b); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Min(double a, double b) { return System.Math.Min(a, b); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Max(int a, int b) { return System.Math.Max(a, b); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(float a, float b) { return System.Math.Max(a, b); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Max(double a, double b) { return System.Math.Max(a, b); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Floor(float s) { return MathF.Floor(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Floor(double s) { return System.Math.Floor(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Pow(float x, float y) { return MathF.Pow(x, y); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Pow(double x, double y) { return System.Math.Pow(x, y); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Round(float s) { return MathF.Round(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Round(double s) { return System.Math.Round(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sin(float s) { return MathF.Sin(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Sin(double s) { return System.Math.Sin(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Asin(float s) { return MathF.Asin(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Asin(double s) { return System.Math.Asin(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cos(float s) { return MathF.Cos(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Cos(double s) { return System.Math.Cos(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Acos(float s) { return MathF.Acos(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Acos(double s) { return System.Math.Acos(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tan(float s) { return MathF.Tan(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Tan(double s) { return System.Math.Tan(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Atan(float s) { return MathF.Atan(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Atan(double s) { return System.Math.Atan(s); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Atan2(float y, float x) { return MathF.Atan2(y, x); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Atan2(double y, double x) { return System.Math.Atan2(y, x); }

    public static class Vector2
    {
        public static System.Numerics.Vector2 Zero => System.Numerics.Vector2.Zero;
        public static System.Numerics.Vector2 One => System.Numerics.Vector2.One;

    }
    public static class Vector3
    {
        private static System.Numerics.Vector3 left = new System.Numerics.Vector3(-1.0f, 0.0f, 0.0f);
        private static System.Numerics.Vector3 right = new System.Numerics.Vector3(1.0f, 0.0f, 0.0f);
        private static System.Numerics.Vector3 up = new System.Numerics.Vector3(0.0f, 1.0f, 0.0f);
        private static System.Numerics.Vector3 down = new System.Numerics.Vector3(0.0f, -1.0f, 0.0f);
        private static System.Numerics.Vector3 forward = new System.Numerics.Vector3(0.0f, 0.0f, 1.0f);
        private static System.Numerics.Vector3 backward = new System.Numerics.Vector3(0.0f, 0.0f, -1.0f);
        private static System.Numerics.Vector3 infinity = new System.Numerics.Vector3(PositiveInfinity, PositiveInfinity, PositiveInfinity);
        private static System.Numerics.Vector3 infinityneg = new System.Numerics.Vector3(PositiveInfinity, PositiveInfinity, NegativeInfinity);

        public static System.Numerics.Vector3 Zero => System.Numerics.Vector3.Zero;
        public static System.Numerics.Vector3 One => System.Numerics.Vector3.One;
        public static System.Numerics.Vector3 Left => left;
        public static System.Numerics.Vector3 Right => right;
        public static System.Numerics.Vector3 Up => up;
        public static System.Numerics.Vector3 Down => down;
        public static System.Numerics.Vector3 Forward => forward;
        public static System.Numerics.Vector3 Backward => backward;
        public static System.Numerics.Vector3 Infinity => infinity;
        public static System.Numerics.Vector3 InfinityNeg => infinityneg;
    }
    public static class Vector4
    {
        private static readonly System.Numerics.Vector4 infinity = new System.Numerics.Vector4(PositiveInfinity, PositiveInfinity, PositiveInfinity, PositiveInfinity);
        private static readonly System.Numerics.Vector4 infinityneg = new System.Numerics.Vector4(NegativeInfinity, NegativeInfinity, NegativeInfinity, NegativeInfinity);
        public static System.Numerics.Vector4 Zero => System.Numerics.Vector4.Zero;
        public static System.Numerics.Vector4 One => System.Numerics.Vector4.One;
        public static System.Numerics.Vector4 Infinity => infinity;
        public static System.Numerics.Vector4 InfinityNeg => infinityneg;
    }
}