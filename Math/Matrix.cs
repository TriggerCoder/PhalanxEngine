using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Matrix : IEquatable<Matrix>
{
    public float m00 = 0.0f, m10 = 0.0f, m20 = 0.0f, m30 = 0.0f;
    public float m01 = 0.0f, m11 = 0.0f, m21 = 0.0f, m31 = 0.0f;
    public float m02 = 0.0f, m12 = 0.0f, m22 = 0.0f, m32 = 0.0f;
    public float m03 = 0.0f, m13 = 0.0f, m23 = 0.0f, m33 = 0.0f;

    public Matrix()
    {
        SetIdentity();
    }

    public Matrix(
    float m00, float m01, float m02, float m03,
    float m10, float m11, float m12, float m13,
    float m20, float m21, float m22, float m23,
    float m30, float m31, float m32, float m33)
    {
        this.m00 = m00; this.m01 = m01; this.m02 = m02; this.m03 = m03;
        this.m10 = m10; this.m11 = m11; this.m12 = m12; this.m13 = m13;
        this.m20 = m20; this.m21 = m21; this.m22 = m22; this.m23 = m23;
        this.m30 = m30; this.m31 = m31; this.m32 = m32; this.m33 = m33;
    }

    public Matrix(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        Matrix mRotation = CreateRotation(rotation);

        m00 = scale.x * mRotation.m00; m01 = scale.x * mRotation.m01; m02 = scale.x * mRotation.m02; m03 = 0.0f;
        m10 = scale.y * mRotation.m10; m11 = scale.y * mRotation.m11; m12 = scale.y * mRotation.m12; m13 = 0.0f;
        m20 = scale.z * mRotation.m20; m21 = scale.z * mRotation.m21; m22 = scale.z * mRotation.m22; m23 = 0.0f;
        m30 = translation.x; m31 = translation.y; m32 = translation.z; m33 = 1.0f;
    }

    public Matrix(float[] m)
    {
        if (m.Length != 16)
            throw new ArgumentOutOfRangeException("array dimension is different from Matrix");

        // row-major to column-major
        m00 = m[0]; m01 = m[1]; m02 = m[2]; m03 = m[3];
        m10 = m[4]; m11 = m[5]; m12 = m[6]; m13 = m[7];
        m20 = m[8]; m21 = m[9]; m22 = m[10]; m23 = m[11];
        m30 = m[12]; m31 = m[13]; m32 = m[14]; m33 = m[15];
    }

    public float this[int index]
    {
        readonly get
        {
            return index switch
            {
                0 => m00,
                1 => m01,
                2 => m02,
                3 => m03,
                4 => m10,
                5 => m11,
                6 => m12,
                7 => m13,
                8 => m20,
                9 => m21,
                10 => m22,
                11 => m23,
                12 => m30,
                13 => m31,
                14 => m32,
                15 => m33,
                _ => throw new ArgumentOutOfRangeException("index"),
            };
        }
        set
        {
            switch (index)
            {
                case 0:
                    m00 = value;
                    break;
                case 1:
                    m01 = value;
                    break;
                case 2:
                    m02 = value;
                    break;
                case 3:
                    m03 = value;
                    break;
                case 4:
                    m10 = value;
                    break;
                case 5:
                    m11 = value;
                    break;
                case 6:
                    m12 = value;
                    break;
                case 7:
                    m13 = value;
                    break;
                case 8:
                    m20 = value;
                    break;
                case 9:
                    m21 = value;
                    break;
                case 10:
                    m22 = value;
                    break;
                case 11:
                    m23 = value;
                    break;
                case 12:
                    m30 = value;
                    break;
                case 13:
                    m31 = value;
                    break;
                case 14:
                    m32 = value;
                    break;
                case 15:
                    m33 = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("index");
            }
        }
    }

    public static Matrix operator *(Matrix l, Matrix r)
    {
        return new Matrix(
                l.m00 * r.m00 + l.m01 * r.m10 + l.m02 * r.m20 + l.m03 * r.m30,
                l.m00 * r.m01 + l.m01 * r.m11 + l.m02 * r.m21 + l.m03 * r.m31,
                l.m00 * r.m02 + l.m01 * r.m12 + l.m02 * r.m22 + l.m03 * r.m32,
                l.m00 * r.m03 + l.m01 * r.m13 + l.m02 * r.m23 + l.m03 * r.m33,
                l.m10 * r.m00 + l.m11 * r.m10 + l.m12 * r.m20 + l.m13 * r.m30,
                l.m10 * r.m01 + l.m11 * r.m11 + l.m12 * r.m21 + l.m13 * r.m31,
                l.m10 * r.m02 + l.m11 * r.m12 + l.m12 * r.m22 + l.m13 * r.m32,
                l.m10 * r.m03 + l.m11 * r.m13 + l.m12 * r.m23 + l.m13 * r.m33,
                l.m20 * r.m00 + l.m21 * r.m10 + l.m22 * r.m20 + l.m23 * r.m30,
                l.m20 * r.m01 + l.m21 * r.m11 + l.m22 * r.m21 + l.m23 * r.m31,
                l.m20 * r.m02 + l.m21 * r.m12 + l.m22 * r.m22 + l.m23 * r.m32,
                l.m20 * r.m03 + l.m21 * r.m13 + l.m22 * r.m23 + l.m23 * r.m33,
                l.m30 * r.m00 + l.m31 * r.m10 + l.m32 * r.m20 + l.m33 * r.m30,
                l.m30 * r.m01 + l.m31 * r.m11 + l.m32 * r.m21 + l.m33 * r.m31,
                l.m30 * r.m02 + l.m31 * r.m12 + l.m32 * r.m22 + l.m33 * r.m32,
                l.m30 * r.m03 + l.m31 * r.m13 + l.m32 * r.m23 + l.m33 * r.m33
            );
    }
    public static Vector3 operator *(Vector3 l,  Matrix r)
    {
        float x = (l.x * r.m00) + (l.y * r.m10) + (l.z * r.m20) + r.m30;
        float y = (l.x * r.m01) + (l.y * r.m11) + (l.z * r.m21) + r.m31;
        float z = (l.x * r.m02) + (l.y * r.m12) + (l.z * r.m22) + r.m32;
        float w = (l.x * r.m03) + (l.y * r.m13) + (l.z * r.m23) + r.m33;

        // to ensure the perspective divide, divide each component by w
        if (w != 1.0f)
        {
            x /= w;
            y /= w;
            z /= w;
        }

        return new Vector3(x, y, z);
    }
    public static Vector4 operator *(Vector4 l, Matrix r)
    {
        return new Vector4
        (
            (l.x * r.m00) + (l.y * r.m10) + (l.z * r.m20) + (l.w * r.m30),
            (l.x * r.m01) + (l.y * r.m11) + (l.z * r.m21) + (l.w * r.m31),
            (l.x * r.m02) + (l.y * r.m12) + (l.z * r.m22) + (l.w * r.m32),
            (l.x * r.m03) + (l.y * r.m13) + (l.z * r.m23) + (l.w * r.m33)
        );
    }
    public static bool operator ==(Matrix l, Matrix r) { return l.Equals(r); }
    public static bool operator !=(Matrix l, Matrix r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Matrix other)
            return Equals(other);
        return false;
    }
    public readonly bool Equals(Matrix other)
    {
        for (int i = 0; i < 16; ++i)
        {
            if (!Math.ApproximateEquals(this[i], other[i]))
                return false;
        }
        return true;
    }
    public override readonly int GetHashCode()
    {
        int row0 = HashCode.Combine(m00, m01, m02, m03);
        int row1 = HashCode.Combine(m10, m11, m12, m13);
        int row2 = HashCode.Combine(m20, m21, m22, m23);
        int row3 = HashCode.Combine(m20, m31, m32, m33);
        return HashCode.Combine(row0, row1, row2, row3);
    }

    public override readonly string ToString()
    {
        string row0 = "m00: " + m00.ToString() + ", m01:" + m01.ToString() + ", m02:" + m02.ToString() + ", m03:" + m03.ToString();
        string row1 = "m10: " + m10.ToString() + ", m11:" + m11.ToString() + ", m12:" + m12.ToString() + ", m13:" + m13.ToString();
        string row2 = "m20: " + m20.ToString() + ", m21:" + m21.ToString() + ", m22:" + m22.ToString() + ", m23:" + m23.ToString();
        string row3 = "m30: " + m30.ToString() + ", m31:" + m31.ToString() + ", m32:" + m32.ToString() + ", m33:" + m33.ToString();
        return row0 + "\n" + row1 + "\n" + row2 + "\n" + row3 + "\n";
    }

    public void SetIdentity()
    {
        m00 = 1; m01 = 0; m02 = 0; m03 = 0;
        m10 = 0; m11 = 1; m12 = 0; m13 = 0;
        m20 = 0; m21 = 0; m22 = 1; m23 = 0;
        m30 = 0; m31 = 0; m32 = 0; m33 = 1;
    }
    public static Matrix CreateTranslation(Vector3 translation)
    {
        return new Matrix(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            translation.x, translation.y, translation.z, 1.0f
        );
    }
    public static Matrix CreateRotation(Quaternion rotation)
    {
        float num9 = rotation.x * rotation.x;
        float num8 = rotation.y * rotation.y;
        float num7 = rotation.z * rotation.z;
        float num6 = rotation.x * rotation.y;
        float num5 = rotation.z * rotation.w;
        float num4 = rotation.z * rotation.x;
        float num3 = rotation.y * rotation.w;
        float num2 = rotation.y * rotation.z;
        float num = rotation.x * rotation.w;

        return new Matrix(
            1.0f - (2.0f * (num8 + num7)),
            2.0f * (num6 + num5),
            2.0f * (num4 - num3),
            0.0f,
            2.0f * (num6 - num5),
            1.0f - (2.0f * (num7 + num9)),
            2.0f * (num2 + num),
            0.0f,
            2.0f * (num4 + num3),
            2.0f * (num2 - num),
            1.0f - (2.0f * (num8 + num9)),
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            1.0f
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 GetTranslation() { return new Vector3(m30, m31, m32); }

    public readonly Quaternion GetRotation()
    {
        Vector3 scale = GetScale();
        if (scale.x == 0.0f || scale.y == 0.0f || scale.z == 0.0f)
            return Quaternion.Identity;

        Matrix normalized = new Matrix();

        normalized.m00 = m00 / scale.x; normalized.m01 = m01 / scale.x; normalized.m02 = m02 / scale.x; normalized.m03 = 0.0f;
        normalized.m10 = m10 / scale.y; normalized.m11 = m11 / scale.y; normalized.m12 = m12 / scale.y; normalized.m13 = 0.0f;
        normalized.m20 = m20 / scale.z; normalized.m21 = m21 / scale.z; normalized.m22 = m22 / scale.z; normalized.m23 = 0.0f;
        normalized.m30 = 0; normalized.m31 = 0; normalized.m32 = 0; normalized.m33 = 1.0f;

        return RotationMatrixToQuaternion(normalized);
    }
    public readonly Vector3 GetScale()
    {
        int xs = (Math.Sign(m00 * m01 * m02 * m03) < 0) ? -1 : 1;
        int ys = (Math.Sign(m10 * m11 * m12 * m13) < 0) ? -1 : 1;
        int zs = (Math.Sign(m20 * m21 * m22 * m23) < 0) ? -1 : 1;
        return new Vector3(
            xs * Math.Sqrt(m00 * m00 + m01 * m01 + m02 * m02),
            ys * Math.Sqrt(m10 * m10 + m11 * m11 + m12 * m12),
            zs * Math.Sqrt(m20 * m20 + m21 * m21 + m22 * m22)
        );
    }
    public static Quaternion RotationMatrixToQuaternion(Matrix mRot)
    {
        Quaternion quaternion;
        float sqrt_;
        float half;
        float scale = mRot.m00 + mRot.m11 + mRot.m22;

        if (scale > 0.0f)
        {
            sqrt_ = Math.Sqrt(scale + 1.0f);
            quaternion.w = sqrt_ * 0.5f;
            sqrt_ = 0.5f / sqrt_;

            quaternion.x = (mRot.m12 - mRot.m21) * sqrt_;
            quaternion.y = (mRot.m20 - mRot.m02) * sqrt_;
            quaternion.z = (mRot.m01 - mRot.m10) * sqrt_;
        }
        else if ((mRot.m00 >= mRot.m11) && (mRot.m00 >= mRot.m22))
        {
            sqrt_ = Math.Sqrt(1.0f + mRot.m00 - mRot.m11 - mRot.m22);
            half = 0.5f / sqrt_;

            quaternion.x = 0.5f * sqrt_;
            quaternion.y = (mRot.m01 + mRot.m10) * half;
            quaternion.z = (mRot.m02 + mRot.m20) * half;
            quaternion.w = (mRot.m12 - mRot.m21) * half;
        }
        else if (mRot.m11 > mRot.m22)
        {
            sqrt_ = Math.Sqrt(1.0f + mRot.m11 - mRot.m00 - mRot.m22);
            half = 0.5f / sqrt_;

            quaternion.x = (mRot.m10 + mRot.m01) * half;
            quaternion.y = 0.5f * sqrt_;
            quaternion.z = (mRot.m21 + mRot.m12) * half;
            quaternion.w = (mRot.m20 - mRot.m02) * half;
        }
        else
        {
            sqrt_ = Math.Sqrt(1.0f + mRot.m22 - mRot.m00 - mRot.m11);
            half = 0.5f / sqrt_;

            quaternion.x = (mRot.m20 + mRot.m02) * half;
            quaternion.y = (mRot.m21 + mRot.m12) * half;
            quaternion.z = 0.5f * sqrt_;
            quaternion.w = (mRot.m01 - mRot.m10) * half;
        }

        // ensure canonical form (w >= 0) to prevent sign flipping between equivalent quaternions
        if (quaternion.w < 0.0f)
        {
            quaternion.x = -quaternion.x;
            quaternion.y = -quaternion.y;
            quaternion.z = -quaternion.z;
            quaternion.w = -quaternion.w;
        }

        return quaternion;
    }
    public static Matrix CreateLookAtLH(Vector3 position, Vector3 target, Vector3 up)
    {
        Vector3 zAxis = Vector3.Normalize(target - position);
        Vector3 xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
        Vector3 yAxis = Vector3.Cross(zAxis, xAxis);

        return new Matrix(
            xAxis.x, yAxis.x, zAxis.x, 0,
            xAxis.y, yAxis.y, zAxis.y, 0,
            xAxis.z, yAxis.z, zAxis.z, 0,
            -Vector3.Dot(xAxis, position), -Vector3.Dot(yAxis, position), -Vector3.Dot(zAxis, position), 1.0f
        );
    }
    public static Matrix CreateOrthographicLH(float width, float height, float zNearPlane, float zFarPlane)
    {
        return new Matrix(
            2 / width, 0, 0, 0,
            0, 2 / height, 0, 0,
            0, 0, 1 / (zFarPlane - zNearPlane), 0,
            0, 0, zNearPlane / (zNearPlane - zFarPlane), 1
        );
    }
    public static Matrix CreateOrthoOffCenterLH(float left, float right, float bottom, float top, float zNearPlane, float zFarPlane)
    {
        return new Matrix(
            2 / (right - left), 0, 0, 0,
            0, 2 / (top - bottom), 0, 0,
            0, 0, 1 / (zFarPlane - zNearPlane), 0,
            (left + right) / (left - right), (top + bottom) / (bottom - top), zNearPlane / (zNearPlane - zFarPlane), 1
        );
    }
    public static Matrix CreatePerspectiveFieldOfViewLH(float fov_y_radians, float aspect_ratio, float near_plane, float far_plane)
    {
        float tan_half_fovy = MathF.Tan(fov_y_radians / 2);
        float f = 1.0f / tan_half_fovy;
        float range_inv = 1.0f / (far_plane - near_plane);

        return new Matrix(
            f / aspect_ratio, 0, 0, 0,
            0, f, 0, 0,
            0, 0, far_plane * range_inv, 1,
            0, 0, -near_plane * far_plane * range_inv, 0
        );
    }
    public static Matrix Transpose(Matrix matrix)
    {
        return new Matrix(
            matrix.m00, matrix.m10, matrix.m20, matrix.m30,
            matrix.m01, matrix.m11, matrix.m21, matrix.m31,
            matrix.m02, matrix.m12, matrix.m22, matrix.m32,
            matrix.m03, matrix.m13, matrix.m23, matrix.m33
        );
    }

    public void Transpose() { this = Transpose(this); }
    public Matrix Transposed() { return Transpose(this); }
    public Matrix Inverted() { return Invert(this); }
    public static Matrix Invert(Matrix matrix)
    {
        float v0 = matrix.m20 * matrix.m31 - matrix.m21 * matrix.m30;
        float v1 = matrix.m20 * matrix.m32 - matrix.m22 * matrix.m30;
        float v2 = matrix.m20 * matrix.m33 - matrix.m23 * matrix.m30;
        float v3 = matrix.m21 * matrix.m32 - matrix.m22 * matrix.m31;
        float v4 = matrix.m21 * matrix.m33 - matrix.m23 * matrix.m31;
        float v5 = matrix.m22 * matrix.m33 - matrix.m23 * matrix.m32;

        float i00 = (v5 * matrix.m11 - v4 * matrix.m12 + v3 * matrix.m13);
        float i10 = -(v5 * matrix.m10 - v2 * matrix.m12 + v1 * matrix.m13);
        float i20 = (v4 * matrix.m10 - v2 * matrix.m11 + v0 * matrix.m13);
        float i30 = -(v3 * matrix.m10 - v1 * matrix.m11 + v0 * matrix.m12);

        float det = i00 * matrix.m00 + i10 * matrix.m01 + i20 * matrix.m02 + i30 * matrix.m03;
        if (Math.IsNaN(det))
            return Identity;

        float inv_det = 1.0f / det;

        i00 *= inv_det;
        i10 *= inv_det;
        i20 *= inv_det;
        i30 *= inv_det;

        float i01 = -(v5 * matrix.m01 - v4 * matrix.m02 + v3 * matrix.m03) * inv_det;
        float i11 = (v5 * matrix.m00 - v2 * matrix.m02 + v1 * matrix.m03) * inv_det;
        float i21 = -(v4 * matrix.m00 - v2 * matrix.m01 + v0 * matrix.m03) * inv_det;
        float i31 = (v3 * matrix.m00 - v1 * matrix.m01 + v0 * matrix.m02) * inv_det;

        v0 = matrix.m10 * matrix.m31 - matrix.m11 * matrix.m30;
        v1 = matrix.m10 * matrix.m32 - matrix.m12 * matrix.m30;
        v2 = matrix.m10 * matrix.m33 - matrix.m13 * matrix.m30;
        v3 = matrix.m11 * matrix.m32 - matrix.m12 * matrix.m31;
        v4 = matrix.m11 * matrix.m33 - matrix.m13 * matrix.m31;
        v5 = matrix.m12 * matrix.m33 - matrix.m13 * matrix.m32;

        float i02 = (v5 * matrix.m01 - v4 * matrix.m02 + v3 * matrix.m03) * inv_det;
        float i12 = -(v5 * matrix.m00 - v2 * matrix.m02 + v1 * matrix.m03) * inv_det;
        float i22 = (v4 * matrix.m00 - v2 * matrix.m01 + v0 * matrix.m03) * inv_det;
        float i32 = -(v3 * matrix.m00 - v1 * matrix.m01 + v0 * matrix.m02) * inv_det;

        v0 = matrix.m21 * matrix.m10 - matrix.m20 * matrix.m11;
        v1 = matrix.m22 * matrix.m10 - matrix.m20 * matrix.m12;
        v2 = matrix.m23 * matrix.m10 - matrix.m20 * matrix.m13;
        v3 = matrix.m22 * matrix.m11 - matrix.m21 * matrix.m12;
        v4 = matrix.m23 * matrix.m11 - matrix.m21 * matrix.m13;
        v5 = matrix.m23 * matrix.m12 - matrix.m22 * matrix.m13;

        float i03 = -(v5 * matrix.m01 - v4 * matrix.m02 + v3 * matrix.m03) * inv_det;
        float i13 = (v5 * matrix.m00 - v2 * matrix.m02 + v1 * matrix.m03) * inv_det;
        float i23 = -(v4 * matrix.m00 - v2 * matrix.m01 + v0 * matrix.m03) * inv_det;
        float i33 = (v3 * matrix.m00 - v1 * matrix.m01 + v0 * matrix.m02) * inv_det;

        return new Matrix(
            i00, i01, i02, i03,
            i10, i11, i12, i13,
            i20, i21, i22, i23,
            i30, i31, i32, i33);
    }

    public readonly void Decompose(ref Vector3 scale, ref Quaternion rotation, ref Vector3 translation)
    {
        translation = GetTranslation();
        scale = GetScale();
        rotation = GetRotation();
    }

    private static readonly Matrix identity = new Matrix(1, 0, 0, 0,
                                                         0, 1, 0, 0,
                                                         0, 0, 1, 0,
                                                         0, 0, 0, 1);

    public static Matrix Identity => identity;
}