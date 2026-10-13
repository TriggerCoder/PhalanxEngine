// Port of SpartanEngine source/rhi/RHI_Vertex.h @ 4c07baa (vertex_pack + RHI_Vertex_PosTexNorTan).
// TODO(port): RHI_Vertex_Pos, RHI_Vertex_PosTex, RHI_Vertex_PosCol, RHI_Vertex_Pos2dTexCol8 (not referenced yet)
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Phalanx.Math;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Geometry;

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
