using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Quaternion : IEquatable<Quaternion>
{
    public float x;
    public float y;
    public float z;
    public float w;
    public Quaternion()
    {
        x = 0;
        y = 0;
        z = 0;
        w = 1;
    }
    public Quaternion(float[] vec)
    {
        int lenght = vec.Length;
        if ((lenght != 4))
            throw new ArgumentOutOfRangeException("array dimension is different from Quaternion");

        x = vec[0];
        y = vec[1];
        z = vec[2];
        w = vec[3];
    }
    public Quaternion(float X, float Y, float Z, float W)
    {
        x = X;
        y = Y;
        z = Z;
        w = W;
    }
    public static Quaternion operator +(Quaternion l, Quaternion r)
    {
        l.x += r.x;
        l.y += r.y;
        l.z += r.z;
        l.w += r.w;
        return l;
    }
    public static Quaternion operator -(Quaternion l, Quaternion r)
    {
        l.x -= r.x;
        l.y -= r.y;
        l.z -= r.z;
        l.w -= r.w;
        return l;
    }
    public static Quaternion operator -(Quaternion other)
    {
        other.x = 0f - other.x;
        other.y = 0f - other.y;
        other.z = 0f - other.z;
        other.w = 0f - other.w;
        return other;
    }

    public static Quaternion operator *(Quaternion Qa, Quaternion Qb)
    {
        float x = Qa.x;
        float y = Qa.y;
        float z = Qa.z;
        float w = Qa.w;
        float num4 = Qb.x;
        float num3 = Qb.y;
        float num2 = Qb.z;
        float num = Qb.w;
        float num12 = (y * num2) - (z * num3);
        float num11 = (z * num4) - (x * num2);
        float num10 = (x * num3) - (y * num4);
        float num9 = ((x * num4) + (y * num3)) + (z * num2);

        return new Quaternion(
            ((x * num) + (num4 * w)) + num12,
            ((y * num) + (num3 * w)) + num11,
            ((z * num) + (num2 * w)) + num10,
            (w * num) - num9
        );
    }
    public static Vector3 operator *(Quaternion Quat, Vector3 other)
    {
         Vector3 qVec = new Vector3(Quat.x, Quat.y, Quat.z);
         Vector3 cross1 = (qVec.Cross(other));
         Vector3 cross2 = (qVec.Cross(cross1));

        return other + 2.0f * (cross1 * Quat.w + cross2);
    }
    public static Quaternion operator *(Quaternion other, float scale)
    {
        other.x *= scale;
        other.y *= scale;
        other.z *= scale;
        other.w *= scale;
        return other;
    }

    public static bool operator ==(Quaternion l, Quaternion r) { return l.Equals(r); }
    public static bool operator !=(Quaternion l, Quaternion r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Quaternion other)
            return Equals(other);
        return false;
    }

    public override readonly int GetHashCode() { return HashCode.Combine(x, y, z, w); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Quaternion other) { return Math.ApproximateEquals(x, other.x) && Math.ApproximateEquals(y, other.y) && Math.ApproximateEquals(z, other.z) && Math.ApproximateEquals(w, other.w); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Quaternion Conjugate() { return new Quaternion(-x, -y, -z, w); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float LengthSquared() { return (x* x) + (y* y) + (z* z) + (w* w); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Dot(Quaternion other) { return w * other.w + x * other.x + y * other.y + z * other.z; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Quaternion a, Quaternion b) { return a.w* b.w + a.x* b.x + a.y* b.y + a.z* b.z; }


    // Normalizes the quaternion
    public void Normalize()
    {
        float lengthsquared = LengthSquared();
        if (!Math.ApproximateEquals(lengthsquared, 1.0f) && lengthsquared > 0.0f)
        {
            float length_inverted = 1.0f / Math.Sqrt(lengthsquared);
            x *= length_inverted;
            y *= length_inverted;
            z *= length_inverted;
            w *= length_inverted;
        }
    }

    public readonly Quaternion Normalized()
    {
        float lengthsquared = LengthSquared();
        if (!Math.ApproximateEquals(lengthsquared, 1.0f) && lengthsquared > 0.0f)
        {
            float length_inverted = 1.0f / Math.Sqrt(lengthsquared);
            return (this) * length_inverted;
        }
        return this;
    }

    public static Quaternion FastNormal(Quaternion quaternion)
    {
        float qmagsq = quaternion.LengthSquared();
        if (Math.Abs(1.0 - qmagsq) < 2.107342e-08)
            quaternion *= (2.0f / (1.0f + qmagsq));
        else
            quaternion = quaternion.Normalized();
        return quaternion;
    }
    public readonly Quaternion Inverse()
    {
        float lengthsquared = LengthSquared();
        if (lengthsquared == 1.0f)
            return Conjugate();
        else if (lengthsquared >= float.MinValue)
            return Conjugate() * (1.0f / lengthsquared);
        return Identity;
    }

    public void FromAxes(Vector3 xAxis, Vector3 yAxis, Vector3 zAxis)
    {
        // compute quaternion directly from rotation matrix axes (avoids unstable GetRotation decomposition)
        // based on: http://www.euclideanspace.com/maths/geometry/rotations/conversions/matrixToQuaternion/
         float m00 = xAxis.x, m01 = xAxis.y, m02 = xAxis.z;
         float m10 = yAxis.x, m11 = yAxis.y, m12 = yAxis.z;
         float m20 = zAxis.x, m21 = zAxis.y, m22 = zAxis.z;

         float trace = m00 + m11 + m22;

        if (trace > 0.0f)
        {
             float s = 0.5f / MathF.Sqrt(trace + 1.0f);
            w = 0.25f / s;
            x = (m12 - m21) * s;
            y = (m20 - m02) * s;
            z = (m01 - m10) * s;
        }
        else if (m00 > m11 && m00 > m22)
        {
             float s = 2.0f * MathF.Sqrt(1.0f + m00 - m11 - m22);
            w = (m12 - m21) / s;
            x = 0.25f * s;
            y = (m10 + m01) / s;
            z = (m20 + m02) / s;
        }
        else if (m11 > m22)
        {
             float s = 2.0f * MathF.Sqrt(1.0f + m11 - m00 - m22);
            w = (m20 - m02) / s;
            x = (m10 + m01) / s;
            y = 0.25f * s;
            z = (m21 + m12) / s;
        }
        else
        {
             float s = 2.0f * MathF.Sqrt(1.0f + m22 - m00 - m11);
            w = (m01 - m10) / s;
            x = (m20 + m02) / s;
            y = (m21 + m12) / s;
            z = 0.25f * s;
        }

        // ensure canonical form (w >= 0)
        if (w < 0.0f)
        {
            x = -x;
            y = -y;
            z = -z;
            w = -w;
        }
    }

    // Creates a new Quaternion from the specified axis and angle.
    // The angle in radians.
    // The axis of rotation.
    public static Quaternion FromAxisAngle(Vector3 axis, float angle)
    {
        float half = angle * 0.5f;
        float sin = MathF.Sin(half);
        float cos = MathF.Cos(half);

        return new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos);
    }

    public readonly void ToAngleAxis(ref float angle, ref Vector3 axis)
    {
        // Normalize the quaternion other prevent inaccuracies
        Quaternion q = Normalized();

        // Calculate the angle
        angle = 2.0f * MathF.Acos(q.w) * 180.0f / 3.14159265358979323846f;

        // Calculate the axis
        float s = Math.Sqrt(1.0f - q.w * q.w);
        if (s < 0.001f)
        {
            // If s is close other zero, the axis is not well-defined and
            // we can choose any arbitrary axis
            axis.x = q.x;
            axis.y = q.y;
            axis.z = q.z;
        }
        else
        {
            axis.x = q.x / s;
            axis.y = q.y / s;
            axis.z = q.z / s;
        }
    }

    // Creates a new Quaternion from the specified yaw, pitch and roll angles.
    // Yaw around the y axis in radians.
    // Pitch around the x axis in radians.
    // Roll around the z axis in radians.
    public static Quaternion FromYawPitchRoll(float yaw, float pitch, float roll)
    {
         float halfRoll = roll * 0.5f;
         float halfPitch = pitch * 0.5f;
         float halfYaw = yaw * 0.5f;

         float sinRoll = MathF.Sin(halfRoll);
         float cosRoll = MathF.Cos(halfRoll);
         float sinPitch = MathF.Sin(halfPitch);
         float cosPitch = MathF.Cos(halfPitch);
         float sinYaw = MathF.Sin(halfYaw);
         float cosYaw = MathF.Cos(halfYaw);

        return new Quaternion(
            cosYaw * sinPitch * cosRoll + sinYaw * cosPitch * sinRoll,
            sinYaw * cosPitch * cosRoll - cosYaw * sinPitch * sinRoll,
            cosYaw * cosPitch * sinRoll - sinYaw * sinPitch * cosRoll,
            cosYaw * cosPitch * cosRoll + sinYaw * sinPitch * sinRoll
        );
    }

    public static Quaternion FromRotation(Vector3 start, Vector3 end)
    {
        Vector3 normStart = start.Normalized();
        Vector3 normEnd = end.Normalized();
        float d = normStart.Dot(normEnd);

        if (d > -1.0f + Math.FloatEpsilon)
        {
            Vector3 c = normStart.Cross(normEnd);
            float s = Math.Sqrt((1.0f + d) * 2.0f);
            float invS = 1.0f / s;

            return new Quaternion(
                c.x * invS,
                c.y * invS,
                c.z * invS,
                0.5f * s);
        }
        else
        {
            Vector3 axis = Vector3.Right.Cross(normStart);
            if (axis.Length() < Math.FloatEpsilon)
                axis = Vector3.Up.Cross(normStart);
            return FromAxisAngle(axis, 180.0f * Math.DegToRad);
        }
    }

    public static Quaternion FromLookRotation(Vector3 direction)
    {
        Vector3 up_direction = Vector3.Up;
        Quaternion result = Identity;
        Vector3 forward = direction.Normalized();

        Vector3 v = forward.Cross(up_direction);
        if (v.LengthSquared() >= float.MinValue)
        {
            v.Normalize();
            Vector3 up = v.Cross(forward);
            Vector3 right = up.Cross(forward);
            result.FromAxes(right, up, forward);
        }
        else
            result = FromRotation(Vector3.Forward, forward);
        return result;
    }

    public static Quaternion FromLookRotation(Vector3 direction, Vector3 up_direction)
    {
        Quaternion result = Identity;
        Vector3 forward = direction.Normalized();

        Vector3 v = forward.Cross(up_direction);
        if (v.LengthSquared() >= float.MinValue)
        {
            v.Normalize();
            Vector3 up = v.Cross(forward);
            Vector3 right = up.Cross(forward);
            result.FromAxes(right, up, forward);
        }
        else
            result = FromRotation(Vector3.Forward, forward);
        return result;
    }
    public readonly Vector3 ToEulerAngles()
    {
        // Derivation from http://www.geometrictools.com/Documentation/EulerAngles.pdf
        // Order of rotations: Z first, then X, then Y
        float check = 2.0f * (-y * z + w * x);

        if (check < -0.995f)
        {
            return new Vector3(
                -90.0f,
                0.0f,
                -MathF.Atan2(2.0f * (x * z - w * y), 1.0f - 2.0f * (y * y + z * z)) * Math.RadToDeg
            );
        }

        if (check > 0.995f)
        {
            return new Vector3(
                90.0f,
                0.0f,
                MathF.Atan2(2.0f * (x * z - w * y), 1.0f - 2.0f * (y * y + z * z)) * Math.RadToDeg
            );
        }

        return new Vector3(
            MathF.Asin(check) * Math.RadToDeg,
            MathF.Atan2(2.0f * (x * z + w * y), 1.0f - 2.0f * (x * x + y * y)) * Math.RadToDeg,
            MathF.Atan2(2.0f * (x * y + w * z), 1.0f - 2.0f * (x * x + z * z)) * Math.RadToDeg
        );
    }

    public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
    {
        Quaternion quaternion;

        if (Dot(a, b) >= 0)
            quaternion = a * (1 - t) + b * t;
        else
            quaternion = a * (1 - t) - b * t;
        return quaternion.Normalized();
    }
    public readonly Quaternion Slerp(Quaternion other, float t)
    {
        float num = Dot(other);
        Quaternion quaternion = Identity;
        if (num < 0.0)
        {
            num = 0f - num;
            quaternion = -other;
        }
        else
            quaternion = other;

        float num4;
        float num5;
        if (!Math.ApproximateEquals(num, 1.0f) && num > 0.0f)
        {
            float num2 = MathF.Acos(num);
            float num3 = MathF.Sin(num2);
            num4 = MathF.Sin((1f - t) * num2) / num3;
            num5 = MathF.Sin(t * num2) / num3;
        }
        else
        {
            num4 = 1f - t;
            num5 = t;
        }

        return new Quaternion(num4 * x + num5 * quaternion.x, num4 * y + num5 * quaternion.y, num4 * z + num5 * quaternion.z, num4 * w + num5 * quaternion.w);
    }

    public readonly Quaternion Slerpni(Quaternion other, float t)
    {
        float s = Dot(other);
        if (Math.Abs(s) > 0.9999f)
            return this;

        float num = MathF.Acos(s);
        float num2 = 1f / MathF.Sin(num);
        float num3 = MathF.Sin(t * num) * num2;
        float num4 = MathF.Sin((1f - t) * num) * num2;
        return new Quaternion(num4 * x + num3 * other.x, num4 * y + num3 * other.y, num4 * z + num3 * other.z, num4 * w + num3 * other.w);
    }

    // euler angles other quaternion (input in degrees)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion FromEulerAngles(Vector3 rotation) { return FromYawPitchRoll(rotation.y* Math.DegToRad, rotation.x* Math.DegToRad, rotation.z* Math.DegToRad); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion FromEulerAngles(float rotationX, float rotationY, float rotationZ) { return FromYawPitchRoll(rotationY * Math.DegToRad, rotationX * Math.DegToRad, rotationZ * Math.DegToRad); }

    // Returns yaw in degrees
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Yaw() { return ToEulerAngles().y; }

    // Returns pitch in degrees
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Pitch() { return ToEulerAngles().x; }

    // Returns roll in degrees
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Roll() { return ToEulerAngles().z; }

    public override readonly string ToString() { return "X: " + x.ToString() + ", Y:" + y.ToString() + ", Z:" + z.ToString() + ", W:" + w.ToString(); }

    private static readonly Quaternion identity = new Quaternion(0f, 0f, 0f, 1f);

    public static Quaternion Identity => identity;
}
