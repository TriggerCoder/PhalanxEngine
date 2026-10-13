// Port of SpartanEngine source/rhi/RHI_Vertex.h @ 4c07baa (vertex_pack + RHI_Vertex_PosTexNorTan).
// TODO(port): RHI_Vertex_Pos, RHI_Vertex_PosTex, RHI_Vertex_PosCol, RHI_Vertex_Pos2dTexCol8 (not referenced yet)
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Phalanx;

// shared packing helpers used by the 24-byte main vertex
public static class VertexPack
{
    // ieee 754 half conversion, matches the helper in instance.h
    // NOTE: bit-exact port of Spartan's converter, not System.Half: it truncates the mantissa (System.Half rounds
    // to nearest even), flushes |x| < 2^-24 to signed zero and turns NaN into infinity, so packed bytes match Spartan
    public static ushort FloatToHalf(float value)
    {
        uint i          = BitConverter.SingleToUInt32Bits(value);
        uint sign       = (i >> 16) & 0x8000;
        int  exponent   = (int)((i >> 23) & 0xFF) - 127;
        uint mantissa   = i & 0x7FFFFF;
        if (exponent < -24)
        {
            return (ushort)sign;
        }
        if (exponent > 15)
        {
            return (ushort)(sign | 0x7C00);
        }
        // -14 is the smallest normal half exponent, below it the value is m / 1024 * 2^-14 with m = 1.mantissa * 2^(exponent + 24)
        if (exponent < -14)
        {
            mantissa |= 0x800000;
            mantissa >>= -exponent - 1;
            return (ushort)(sign | mantissa);
        }
        exponent += 15;
        mantissa >>= 13;
        return (ushort)(sign | ((uint)exponent << 10) | mantissa);
    }

    // NOTE: like Spartan, infinity/NaN halves decode to 0 (System.Half would give inf/NaN)
    public static float HalfToFloat(ushort value)
    {
        uint sign = (uint)(value & 0x8000) << 16;
        uint exp  = (uint)(value >> 10) & 0x1F;
        uint mant = (uint)value & 0x3FF;
        if (exp == 0x1F)
        {
            return 0.0f;
        }
        if (exp == 0 && mant == 0)
        {
            return BitConverter.UInt32BitsToSingle(sign);
        }
        if (exp == 0)
        {
            int shifts = 0;
            while ((mant & 0x400) == 0) { mant <<= 1; shifts++; }
            mant &= 0x3FF;
            exp = (uint)(1 - shifts);
        }
        exp += 112; // 127 - 15
        return BitConverter.UInt32BitsToSingle(sign | (exp << 23) | (mant << 13));
    }

    // pack two floats into a single uint as half2
    public static uint PackHalf2(float x, float y)
    {
        return FloatToHalf(x) | ((uint)FloatToHalf(y) << 16);
    }

    public static Vector2 UnpackHalf2(uint packed)
    {
        return new Vector2(HalfToFloat((ushort)(packed & 0xFFFF)), HalfToFloat((ushort)((packed >> 16) & 0xFFFF)));
    }

    // octahedral encoding into two snorm16 channels packed in a single uint
    // matches the shader-side decode (oct snorm 16:16, x in low 16 bits, y in high 16 bits)
    public static uint PackOctSnorm16(Vector3 dir)
    {
        float ax  = MathF.Abs(dir.X);
        float ay  = MathF.Abs(dir.Y);
        float az  = MathF.Abs(dir.Z);
        float inv = 1.0f / MathF.Max(ax + ay + az, 1e-20f);
        float ox  = dir.X * inv;
        float oy  = dir.Y * inv;
        if (dir.Z < 0.0f)
        {
            float tx = ox;
            ox = (1.0f - MathF.Abs(oy)) * (tx >= 0.0f ? 1.0f : -1.0f);
            oy = (1.0f - MathF.Abs(tx)) * (oy >= 0.0f ? 1.0f : -1.0f);
        }
        // inputs are bounded to snorm16: truncating after an exact double half-step gives lround's
        // ties-away-from-zero result
        float x = System.Math.Clamp(ox, -1.0f, 1.0f) * 32767.0f;
        float y = System.Math.Clamp(oy, -1.0f, 1.0f) * 32767.0f;
        int qx  = (int)((double)x + (x < 0.0f ? -0.5 : 0.5));
        int qy  = (int)((double)y + (y < 0.0f ? -0.5 : 0.5));
        return (uint)(ushort)qx | ((uint)(ushort)qy << 16);
    }

    public static Vector3 UnpackOctSnorm16(uint packed)
    {
        short sx = (short)(packed & 0xFFFF);
        short sy = (short)((packed >> 16) & 0xFFFF);
        float x  = MathF.Max(-1.0f, sx / 32767.0f);
        float y  = MathF.Max(-1.0f, sy / 32767.0f);
        float z  = 1.0f - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0.0f)
        {
            float tx = x;
            x = (1.0f - MathF.Abs(y))  * (x  >= 0.0f ? 1.0f : -1.0f);
            y = (1.0f - MathF.Abs(tx)) * (y  >= 0.0f ? 1.0f : -1.0f);
        }
        return SpartanNormalize(new Vector3(x, y, z));
    }

    // math::Vector3::Normalize(): untouched when already unit (within float epsilon) or zero
    // NOTE: exact 1/sqrt here, Spartan uses rsqrt + one newton-raphson step (differs in the last ulp or so)
    private static Vector3 SpartanNormalize(Vector3 v)
    {
        float length_squared = Vector3.Dot(v, v);
        const float epsilon  = 1.1920929e-7f; // std::numeric_limits<float>::epsilon()
        bool unit            = length_squared + epsilon >= 1.0f && length_squared - epsilon <= 1.0f;
        if (!unit && length_squared > 0.0f)
            return v * (1.0f / MathF.Sqrt(length_squared));
        return v;
    }
}

// main mesh vertex, 24 bytes
// pos kept as float3 for meshopt and bbox/picking precision
// uv as half2, normal/tangent as octahedral snorm 16:16
// NOTE: like Spartan 4c07baa there is no bitangent sign, the tangent is a plain direction
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RHI_Vertex_PosTexNorTan : IEquatable<RHI_Vertex_PosTexNorTan>
{
    // raw position storage (float pos[3] in Spartan), exposed for meshopt and bbox math, must stay first:
    // meshoptimizer reads positions at offset 0 with the vertex stride
    public Vector3 pos; // 12
    public uint    uv;  // 4, half2
    public uint    nor; // 4, oct snorm 16:16
    public uint    tan; // 4, oct snorm 16:16

    public RHI_Vertex_PosTexNorTan(Vector3 position, Vector2 texcoord, Vector3 normal = default, Vector3 tangent = default)
    {
        pos = position;
        uv  = VertexPack.PackHalf2(texcoord.X, texcoord.Y);
        nor = VertexPack.PackOctSnorm16(normal);
        tan = VertexPack.PackOctSnorm16(tangent);
    }

    public void SetPosition(Vector3 v)        { pos = v; }
    public readonly Vector3 GetPosition()     { return pos; }

    public void SetUv(Vector2 v)              { uv = VertexPack.PackHalf2(v.X, v.Y); }
    public void SetUv(float x, float y)       { uv = VertexPack.PackHalf2(x, y); }
    public readonly Vector2 GetUv()           { return VertexPack.UnpackHalf2(uv); }

    public void SetNormal(Vector3 v)          { nor = VertexPack.PackOctSnorm16(v); }
    public readonly Vector3 GetNormal()       { return VertexPack.UnpackOctSnorm16(nor); }

    public void SetTangent(Vector3 v)         { tan = VertexPack.PackOctSnorm16(v); }
    public readonly Vector3 GetTangent()      { return VertexPack.UnpackOctSnorm16(tan); }

    // bitwise (what meshopt's remap and the caches see)
    public readonly bool Equals(RHI_Vertex_PosTexNorTan o) { return pos.Equals(o.pos) && uv == o.uv && nor == o.nor && tan == o.tan; }
    public override readonly bool Equals(object? obj)      { return obj is RHI_Vertex_PosTexNorTan o && Equals(o); }
    public override readonly int GetHashCode()             { return HashCode.Combine(pos, uv, nor, tan); }
    public static bool operator ==(RHI_Vertex_PosTexNorTan a, RHI_Vertex_PosTexNorTan b) { return a.Equals(b); }
    public static bool operator !=(RHI_Vertex_PosTexNorTan a, RHI_Vertex_PosTexNorTan b) { return !a.Equals(b); }

    public const int Size = 24;
}
