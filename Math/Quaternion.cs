using System.Numerics;
using System.Runtime.CompilerServices;

namespace Phalanx;
public static class QuaternionExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion Inverse(this Quaternion q) { return Quaternion.Inverse(q); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(this Quaternion q, Quaternion other) { return Quaternion.Dot(q,other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion Normalized(this Quaternion q) { return Quaternion.Normalize(q); }
    public static Quaternion FastNormal(Quaternion quaternion)
    {
        float qmagsq = quaternion.LengthSquared();
        if (Math.Abs(1.0 - qmagsq) < 2.107342e-08)
            quaternion *= (2.0f / (1.0f + qmagsq));
        else
            quaternion = Quaternion.Normalize(quaternion);
        return quaternion;
    }
    public static void FromAxes(this Quaternion q, Vector3 xAxis, Vector3 yAxis, Vector3 zAxis)
    {
        // compute quaternion directly from rotation matrix axes (avoids unstable GetRotation decomposition)
        // based on: http://www.euclideanspace.com/maths/geometry/rotations/conversions/matrixToQuaternion/
         float M11 = xAxis.X, M12 = xAxis.Y, M13 = xAxis.Z;
         float M21 = yAxis.X, M22 = yAxis.Y, M23 = yAxis.Z;
         float M31 = zAxis.X, M32 = zAxis.Y, M33 = zAxis.Z;

         float trace = M11 + M22 + M33;

        if (trace > 0.0f)
        {
            float s = 0.5f / Math.Sqrt(trace + 1.0f);
            q.W = 0.25f / s;
            q.X = (M23 - M32) * s;
            q.Y = (M31 - M13) * s;
            q.Z = (M12 - M21) * s;
        }
        else if (M11 > M22 && M11 > M33)
        {
            float s = 2.0f * Math.Sqrt(1.0f + M11 - M22 - M33);
            q.W = (M23 - M32) / s;
            q.X = 0.25f * s;
            q.Y = (M21 + M12) / s;
            q.Z = (M31 + M13) / s;
        }
        else if (M22 > M33)
        {
            float s = 2.0f * Math.Sqrt(1.0f + M22 - M11 - M33);
            q.W = (M31 - M13) / s;
            q.X = (M21 + M12) / s;
            q.Y = 0.25f * s;
            q.Z = (M32 + M23) / s;
        }
        else
        {
            float s = 2.0f * Math.Sqrt(1.0f + M33 - M11 - M22);
            q.W = (M12 - M21) / s;
            q.X = (M31 + M13) / s;
            q.Y = (M32 + M23) / s;
            q.Z = 0.25f * s;
        }

        // ensure canonical form (w >= 0)
        if (q.W < 0.0f)
        {
            q.X = -q.X;
            q.Y = -q.Y;
            q.Z = -q.Z;
            q.W = -q.W;
        }
    }

    // Creates a new Quaternion from the specified axis and angle.
    // The angle in radians.
    // The axis of rotation.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion FromAxisAngle(Vector3 axis, float angle) { return Quaternion.CreateFromAxisAngle(axis, angle); }

    public static void ToAngleAxis(this Quaternion q, ref float angle, ref Vector3 axis)
    {
        // Normalize the quaternion other prevent inaccuracies
        Quaternion nq = Quaternion.Normalize(q);

        // Calculate the angle
        angle = 2.0f * Math.Acos(nq.W) * 180.0f / 3.14159265358979323846f;

        // Calculate the axis
        float s = Math.Sqrt(1.0f - nq.W * nq.W);
        if (s < 0.001f)
        {
            // If s is close other zero, the axis is not well-defined and
            // we can choose any arbitrary axis
            axis.X = nq.X;
            axis.Y = nq.Y;
            axis.Z = nq.Z;
        }
        else
        {
            axis.X = nq.X / s;
            axis.Y = nq.Y / s;
            axis.Z = nq.Z / s;
        }
    }

    // Creates a new Quaternion from the specified yaw, pitch and roll angles.
    // Yaw around the y axis in radians.
    // Pitch around the x axis in radians.
    // Roll around the z axis in radians.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion FromYawPitchRoll(float yaw, float pitch, float roll) { return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll); }
    public static Quaternion FromRotation(Vector3 start, Vector3 end)
    {
        Vector3 normStart = Vector3.Normalize(start);
        Vector3 normEnd = Vector3.Normalize(end);
        float d = normStart.Dot(normEnd);

        if (d > -1.0f + Math.FloatEpsilon)
        {
            Vector3 c = normStart.Cross(normEnd);
            float s = Math.Sqrt((1.0f + d) * 2.0f);
            float invS = 1.0f / s;

            return new Quaternion(
                c.X * invS,
                c.Y * invS,
                c.Z * invS,
                0.5f * s);
        }
        else
        {
            Vector3 axis = Math.Vector3.Right.Cross(normStart);
            if (axis.Length() < Math.FloatEpsilon)
                axis = Math.Vector3.Up.Cross(normStart);
            return FromAxisAngle(axis, 180.0f * Math.DegToRad);
        }
    }

    public static Quaternion FromLookRotation(Vector3 direction)
    {
        Vector3 up_direction = Math.Vector3.Up;
        Quaternion result = Quaternion.Identity;
        Vector3 forward = Vector3.Normalize(direction);

        Vector3 v = forward.Cross(up_direction);
        if (v.LengthSquared() >= float.MinValue)
        {
            Vector3.Normalize(v);
            Vector3 up = v.Cross(forward);
            Vector3 right = up.Cross(forward);
            result.FromAxes(right, up, forward);
        }
        else
            result = FromRotation(Math.Vector3.Forward, forward);
        return result;
    }

    public static Quaternion FromLookRotation(Vector3 direction, Vector3 up_direction)
    {
        Quaternion result = Quaternion.Identity;
        Vector3 forward = Vector3.Normalize(direction);

        Vector3 v = forward.Cross(up_direction);
        if (v.LengthSquared() >= float.MinValue)
        {
            Vector3.Normalize(v);
            Vector3 up = v.Cross(forward);
            Vector3 right = up.Cross(forward);
            result.FromAxes(right, up, forward);
        }
        else
            result = FromRotation(Math.Vector3.Forward, forward);
        return result;
    }
    public static Vector3 ToEulerAngles(this Quaternion q)
    {
        // Derivation from http://www.geometrictools.com/Documentation/EulerAngles.pdf
        // Order of rotations: Z first, then X, then Y
        float check = 2.0f * (-q.Y * q.Z + q.W * q.X);

        if (check < -0.995f)
        {
            return new Vector3(
                -90.0f,
                0.0f,
                -Math.Atan2(2.0f * (q.X * q.Z - q.W * q.Y), 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z)) * Math.RadToDeg
            );
        }

        if (check > 0.995f)
        {
            return new Vector3(
                90.0f,
                0.0f,
                Math.Atan2(2.0f * (q.X * q.Z - q.W * q.Y), 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z)) * Math.RadToDeg
            );
        }

        return new Vector3(
            Math.Asin(check) * Math.RadToDeg,
            Math.Atan2(2.0f * (q.X * q.Z + q.W * q.Y), 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y)) * Math.RadToDeg,
            Math.Atan2(2.0f * (q.X * q.Y + q.W * q.Z), 1.0f - 2.0f * (q.X * q.X + q.Z * q.Z)) * Math.RadToDeg
        );
    }

    public static Quaternion Slerpni(this Quaternion q, Quaternion other, float t)
    {
        float s = q.Dot(other);
        if (Math.Abs(s) > 0.9999f)
            return q;

        float num = Math.Acos(s);
        float num2 = 1f / Math.Sin(num);
        float num3 = Math.Sin(t * num) * num2;
        float num4 = Math.Sin((1f - t) * num) * num2;
        return new Quaternion(num4 * q.X + num3 * other.X, num4 * q.Y + num3 * other.Y, num4 * q.Z + num3 * other.Z, num4 * q.W + num3 * other.W);
    }

    // euler angles other quaternion (input in degrees)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion FromEulerAngles(Vector3 rotation) { return FromYawPitchRoll(rotation.Y* Math.DegToRad, rotation.X* Math.DegToRad, rotation.Z* Math.DegToRad); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion FromEulerAngles(float rotationX, float rotationY, float rotationZ) { return FromYawPitchRoll(rotationY * Math.DegToRad, rotationX * Math.DegToRad, rotationZ * Math.DegToRad); }

    // Returns yaw in degrees
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Yaw(this Quaternion q) { return ToEulerAngles(q).Y; }

    // Returns pitch in degrees
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Pitch(this Quaternion q) { return ToEulerAngles(q).X; }

    // Returns roll in degrees
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Roll(this Quaternion q) { return ToEulerAngles(q).Z; }
}
