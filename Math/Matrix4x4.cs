using System.Numerics;
using System.Runtime.CompilerServices;

namespace Phalanx;
public static class Matrix4x4Extensions
{
    public static Matrix4x4 CreateTRS(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        Matrix4x4 matrix = new Matrix4x4();
        Matrix4x4 mRotation = CreateRotation(rotation);

        matrix.M11 = scale.X * mRotation.M11; matrix.M12 = scale.X * mRotation.M12; matrix.M13 = scale.X * mRotation.M13; matrix.M14 = 0.0f;
        matrix.M21 = scale.Y * mRotation.M21; matrix.M22 = scale.Y * mRotation.M22; matrix.M23 = scale.Y * mRotation.M23; matrix.M24 = 0.0f;
        matrix.M31 = scale.Z * mRotation.M31; matrix.M32 = scale.Z * mRotation.M32; matrix.M33 = scale.Z * mRotation.M33; matrix.M34 = 0.0f;
        matrix.M41 = translation.X; matrix.M42 = translation.Y; matrix.M43 = translation.Z; matrix.M44 = 1.0f;

        return matrix;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 CreateRotation(Quaternion rotation) { return Matrix4x4.CreateFromQuaternion(rotation); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetTranslation(this Matrix4x4 m) { return m.Translation; }
    public static Quaternion GetRotation(this Matrix4x4 m)
    {
        Vector3 scale = GetScale(m);
        if (scale.X == 0.0f || scale.Y == 0.0f || scale.Z == 0.0f)
            return Quaternion.Identity;

        Matrix4x4 normalized = new Matrix4x4();

        normalized.M11 = m.M11 / scale.X; normalized.M12 = m.M12 / scale.X; normalized.M13 = m.M13 / scale.X; normalized.M14 = 0.0f;
        normalized.M21 = m.M21 / scale.Y; normalized.M22 = m.M22 / scale.Y; normalized.M23 = m.M23 / scale.Y; normalized.M24 = 0.0f;
        normalized.M31 = m.M31 / scale.Z; normalized.M32 = m.M32 / scale.Z; normalized.M33 = m.M33 / scale.Z; normalized.M34 = 0.0f;
        normalized.M41 = 0; normalized.M42 = 0; normalized.M43 = 0; normalized.M44 = 1.0f;

        return RotationMatrixToQuaternion(normalized);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetScale(this Matrix4x4 m)
    {
        int xs = (Math.Sign(m.M11 * m.M12 * m.M13 * m.M14) < 0) ? -1 : 1;
        int ys = (Math.Sign(m.M21 * m.M22 * m.M23 * m.M24) < 0) ? -1 : 1;
        int zs = (Math.Sign(m.M31 * m.M32 * m.M33 * m.M34) < 0) ? -1 : 1;
        return new Vector3(
            xs * Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13),
            ys * Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23),
            zs * Math.Sqrt(m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33)
        );
    }
    public static Quaternion RotationMatrixToQuaternion(Matrix4x4 mRot)
    {
        Quaternion quaternion;
        float sqrt_;
        float half;
        float scale = mRot.M11 + mRot.M22 + mRot.M33;

        if (scale > 0.0f)
        {
            sqrt_ = Math.Sqrt(scale + 1.0f);
            quaternion.W = sqrt_ * 0.5f;
            sqrt_ = 0.5f / sqrt_;

            quaternion.X = (mRot.M23 - mRot.M32) * sqrt_;
            quaternion.Y = (mRot.M31 - mRot.M13) * sqrt_;
            quaternion.Z = (mRot.M12 - mRot.M21) * sqrt_;
        }
        else if ((mRot.M11 >= mRot.M22) && (mRot.M11 >= mRot.M33))
        {
            sqrt_ = Math.Sqrt(1.0f + mRot.M11 - mRot.M22 - mRot.M33);
            half = 0.5f / sqrt_;

            quaternion.X = 0.5f * sqrt_;
            quaternion.Y = (mRot.M12 + mRot.M21) * half;
            quaternion.Z = (mRot.M13 + mRot.M31) * half;
            quaternion.W = (mRot.M23 - mRot.M32) * half;
        }
        else if (mRot.M22 > mRot.M33)
        {
            sqrt_ = Math.Sqrt(1.0f + mRot.M22 - mRot.M11 - mRot.M33);
            half = 0.5f / sqrt_;

            quaternion.X = (mRot.M21 + mRot.M12) * half;
            quaternion.Y = 0.5f * sqrt_;
            quaternion.Z = (mRot.M32 + mRot.M23) * half;
            quaternion.W = (mRot.M31 - mRot.M13) * half;
        }
        else
        {
            sqrt_ = Math.Sqrt(1.0f + mRot.M33 - mRot.M11 - mRot.M22);
            half = 0.5f / sqrt_;

            quaternion.X = (mRot.M31 + mRot.M13) * half;
            quaternion.Y = (mRot.M32 + mRot.M23) * half;
            quaternion.Z = 0.5f * sqrt_;
            quaternion.W = (mRot.M12 - mRot.M21) * half;
        }

        // ensure canonical form (w >= 0) to prevent sign flipping between equivalent quaternions
        if (quaternion.W < 0.0f)
        {
            quaternion.X = -quaternion.X;
            quaternion.Y = -quaternion.Y;
            quaternion.Z = -quaternion.Z;
            quaternion.W = -quaternion.W;
        }

        return quaternion;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 CreateLookAtLH(Vector3 position, Vector3 target, Vector3 up) { return Matrix4x4.CreateLookAtLeftHanded(position, target, up); }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 CreateOrthographicLH(float width, float height, float zNearPlane, float zFarPlane) { return Matrix4x4.CreateOrthographicLeftHanded(width, height, zNearPlane, zFarPlane); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 CreateOrthoOffCenterLH(float left, float right, float bottom, float top, float zNearPlane, float zFarPlane) { return Matrix4x4.CreateOrthographicOffCenterLeftHanded(left, right, bottom, top, zNearPlane, zFarPlane); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 CreatePerspectiveFieldOfViewLH(float fov_y_radians, float aspect_ratio, float near_plane, float far_plane) { return Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov_y_radians, aspect_ratio, near_plane, far_plane); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 Transpose(this Matrix4x4 m) { return m = Matrix4x4.Transpose(m); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 Transposed(this Matrix4x4 m) { return Matrix4x4.Transpose(m); }

    public static Matrix4x4 Inverted(this Matrix4x4 m)
    {
        Matrix4x4 inverted = new Matrix4x4();
        if (Matrix4x4.Invert(m, out inverted))
            return inverted;
        return Matrix4x4.Identity;
    }

    public static Matrix4x4 Invert(Matrix4x4 matrix)
    {
        Matrix4x4 inverted = new Matrix4x4();
        if (Matrix4x4.Invert(matrix, out inverted))
            return inverted;
        return Matrix4x4.Identity;
    }
    public static void Decompose(this Matrix4x4 m, ref Vector3 scale, ref Quaternion rotation, ref Vector3 translation)
    {
        translation = m.GetTranslation();
        scale = m.GetScale();
        rotation = m.GetRotation();
    }
}