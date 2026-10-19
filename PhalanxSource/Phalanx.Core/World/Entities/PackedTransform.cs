// The per instance transform of instanced store entities, bit for bit spartan's 32 byte PackedInstance
// (source/rendering/Instance.h/.cpp @ 4c07baa, shared_buffers.h): position, a snorm16 quaternion and the scale. The
// renderer copies these into its instance buffer unchanged (Phalanx.Rendering.PackedInstance has the same layout,
// SharedBufferLayoutTests checks it), the vertex shader rebuilds scale * rotation * translation
// (common_vertex_processing.hlsl:58-70).
using System.Numerics;
using System.Runtime.InteropServices;

namespace Phalanx.World;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PackedTransform
{
    public float pos_x, pos_y, pos_z;
    public uint rotation_xy, rotation_zw;
    public float scale_x, scale_y, scale_z;

    // Instance.cpp pack_rotation: clamp to [-1, 1], scale by 32767, round
    private static uint PackRotation(float a, float b)
    {
        static ushort Pack(float value) => unchecked((ushort)(short)MathF.Round(System.Math.Clamp(value, -1.0f, 1.0f) * 32767.0f));
        return Pack(a) | ((uint)Pack(b) << 16);
    }
    private static float UnpackRotation(uint value) => unchecked((short)(ushort)value) / 32767.0f;

    /// <summary>Packs a translation, a rotation (normalized here) and a scale.</summary>
    public static PackedTransform Pack(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        rotation = Quaternion.Normalize(rotation);
        return new PackedTransform
        {
            pos_x = position.X, pos_y = position.Y, pos_z = position.Z,
            rotation_xy = PackRotation(rotation.X, rotation.Y),
            rotation_zw = PackRotation(rotation.Z, rotation.W),
            scale_x = scale.X, scale_y = scale.Y, scale_z = scale.Z,
        };
    }

    /// <summary>Packs a world matrix (Instance::SetMatrix: decomposed; shear and negative scale don't survive).</summary>
    public static PackedTransform Pack(in Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 position))
        {
            position = matrix.Translation;
            scale    = new Vector3(new Vector3(matrix.M11, matrix.M12, matrix.M13).Length(), new Vector3(matrix.M21, matrix.M22, matrix.M23).Length(), new Vector3(matrix.M31, matrix.M32, matrix.M33).Length());
            rotation = Quaternion.Identity;
        }
        return Pack(position, rotation, scale);
    }

    /// <summary>The matrix the vertex shader rebuilds (scale * rotation * translation, row vectors).</summary>
    public readonly Matrix4x4 GetMatrix()
    {
        var rotation = Quaternion.Normalize(new Quaternion(UnpackRotation(rotation_xy), UnpackRotation(rotation_xy >> 16),
                                                           UnpackRotation(rotation_zw), UnpackRotation(rotation_zw >> 16)));
        return Matrix4x4.CreateScale(scale_x, scale_y, scale_z) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(pos_x, pos_y, pos_z);
    }
}
