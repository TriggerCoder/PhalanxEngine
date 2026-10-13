using Phalanx.Geometry;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Rendering;

// Hand-written helpers for the generated structs in SharedBuffers.g.cs (Phalanx.Generators, kept separate so generation
// does not overwrite them). Bit positions mirror the HLSL helpers in shared_buffers.h.
public partial struct MaterialParameters
{
    public readonly bool HasTextureHeight()    { return (flags & (1u << 0)) != 0; }
    public readonly bool HasTextureNormal()    { return (flags & (1u << 1)) != 0; }
    public readonly bool HasTextureAlbedo()    { return (flags & (1u << 2)) != 0; }
    public readonly bool HasTextureRoughness() { return (flags & (1u << 3)) != 0; }
    public readonly bool HasTextureMetalness() { return (flags & (1u << 4)) != 0; }
    public readonly bool HasTextureEmissive()  { return (flags & (1u << 6)) != 0; }
    public readonly bool HasTextureOcclusion() { return (flags & (1u << 7)) != 0; }
    public readonly bool IsTerrain()           { return (flags & (1u << 8)) != 0; }
    public readonly bool EmissiveFromAlbedo()  { return (flags & (1u << 15)) != 0; }
    public readonly bool IsAlphaTested()       { return (flags & (1u << 16)) != 0; }
    public void SetFlag(uint bit, bool value) { flags = value ? (flags | bit) : (flags & ~bit); }
}

public partial struct FrameBufferData
{
    public void SetBit(bool set, uint bit) { options = set ? (options | bit) : (options & ~bit); }
}

public partial struct PassBufferData
{
    // slot helpers mirroring the C++ set(slot, value) overloads, slot = 0..19 over values[5]
    public void Set(int slot, float value) { System.Runtime.InteropServices.MemoryMarshal.Cast<System.Numerics.Vector4, float>((Span<System.Numerics.Vector4>)values)[slot] = value; }
    public void Set(int slot, uint value)  { Set(slot, BitConverter.UInt32BitsToSingle(value)); }
    public void Set(int slot, bool value)  { Set(slot, value ? 1u : 0u); }
    public void Set(int slot, System.Numerics.Vector2 v) { Set(slot, v.X); Set(slot + 1, v.Y); }
    public void Set(int slot, System.Numerics.Vector3 v) { Set(slot, v.X); Set(slot + 1, v.Y); Set(slot + 2, v.Z); }
    public void Set(int slot, System.Numerics.Vector4 v) { Set(slot, v.X); Set(slot + 1, v.Y); Set(slot + 2, v.Z); Set(slot + 3, v.W); }
    public float Get(int slot) { return System.Runtime.InteropServices.MemoryMarshal.Cast<System.Numerics.Vector4, float>((Span<System.Numerics.Vector4>)values)[slot]; }
}
