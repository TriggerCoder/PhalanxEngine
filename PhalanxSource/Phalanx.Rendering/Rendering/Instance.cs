// Port of SpartanEngine source/rendering/Instance.cpp @ 4c07baa: the 32-byte packed instance (PackedInstance in the
// shared buffers), position, snorm16 quaternion and scale.
using System.Numerics;

namespace Phalanx.Rendering;

public partial struct PackedInstance
{
    private static uint pack_rotation(float a, float b)
    {
        static ushort pack(float value) => unchecked((ushort)(short)MathF.Round(System.Math.Clamp(value, -1.0f, 1.0f) * 32767.0f));
        return pack(a) | ((uint)pack(b) << 16);
    }

    private static float unpack_rotation(uint value) => unchecked((short)(ushort)value) / 32767.0f;

    public readonly Matrix4x4 GetMatrix()
    {
        var rotation = Quaternion.Normalize(new Quaternion(unpack_rotation(rotation_xy), unpack_rotation(rotation_xy >> 16),
                                                           unpack_rotation(rotation_zw), unpack_rotation(rotation_zw >> 16)));
        return Matrix4x4.CreateScale(scale_x, scale_y, scale_z) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(pos_x, pos_y, pos_z);
    }

    public void SetMatrix(Matrix4x4 matrix)
    {
        Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 position);
        rotation = Quaternion.Normalize(rotation);
        pos_x = position.X; pos_y = position.Y; pos_z = position.Z;
        rotation_xy = pack_rotation(rotation.X, rotation.Y);
        rotation_zw = pack_rotation(rotation.Z, rotation.W);
        scale_x = scale.X; scale_y = scale.Y; scale_z = scale.Z;
    }

    public static PackedInstance GetIdentity()
    {
        var instance = new PackedInstance();
        instance.SetMatrix(Matrix4x4.Identity);
        return instance;
    }
}
