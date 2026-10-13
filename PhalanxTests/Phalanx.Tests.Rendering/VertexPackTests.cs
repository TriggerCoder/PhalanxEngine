using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Phalanx.Tests;

public class VertexPackTests
{
    // layout -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Vertex_Is24Bytes_WithSpartanOffsets()
    {
        Assert.Equal(24, Unsafe.SizeOf<RHI_Vertex_PosTexNorTan>());
        Assert.Equal(24, Marshal.SizeOf<RHI_Vertex_PosTexNorTan>());
        Assert.Equal(0,  (int)Marshal.OffsetOf<RHI_Vertex_PosTexNorTan>(nameof(RHI_Vertex_PosTexNorTan.pos)));
        Assert.Equal(12, (int)Marshal.OffsetOf<RHI_Vertex_PosTexNorTan>(nameof(RHI_Vertex_PosTexNorTan.uv)));
        Assert.Equal(16, (int)Marshal.OffsetOf<RHI_Vertex_PosTexNorTan>(nameof(RHI_Vertex_PosTexNorTan.nor)));
        Assert.Equal(20, (int)Marshal.OffsetOf<RHI_Vertex_PosTexNorTan>(nameof(RHI_Vertex_PosTexNorTan.tan)));
    }

    [Fact]
    public void Vertex_Bytes_AreTheBitLayout()
    {
        var v = new RHI_Vertex_PosTexNorTan(new Vector3(1, 2, 3), new Vector2(1.0f, 0.5f), Vector3.UnitX, -Vector3.UnitZ);
        byte[] b = MemoryMarshal.AsBytes(new ReadOnlySpan<RHI_Vertex_PosTexNorTan>(in v)).ToArray();
        Assert.Equal(2.0f, BitConverter.ToSingle(b, 4));
        Assert.Equal(0x38003C00u, BitConverter.ToUInt32(b, 12)); // half(1) low, half(0.5) high
        Assert.Equal(0x00007FFFu, BitConverter.ToUInt32(b, 16)); // oct (+1, 0)
        Assert.Equal(0x7FFF7FFFu, BitConverter.ToUInt32(b, 20)); // -z folds to the (+1, +1) corner
    }

    [Fact]
    public void Constructor_Defaults_AndAccessors()
    {
        var v = new RHI_Vertex_PosTexNorTan(new Vector3(4, 5, 6), new Vector2(0.25f, 0.75f));
        Assert.Equal(0u, v.nor); // zero normal packs to (0, 0) -> decodes to +z, like Spartan
        Assert.Equal(Vector3.UnitZ, v.GetNormal());
        Assert.Equal(new Vector2(0.25f, 0.75f), v.GetUv());
        v.SetUv(2, 3); Assert.Equal(new Vector2(2, 3), v.GetUv());
        v.SetNormal(Vector3.UnitY); Assert.Equal(Vector3.UnitY, v.GetNormal());
        v.SetTangent(-Vector3.UnitX); Assert.Equal(-Vector3.UnitX, v.GetTangent());
        v.SetPosition(Vector3.One); Assert.Equal(Vector3.One, v.GetPosition());
        Assert.Equal(default(RHI_Vertex_PosTexNorTan).GetUv(), Vector2.Zero);
    }

    // half -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.0f, 0x3C00)]
    [InlineData(-2.0f, 0xC000)]
    [InlineData(0.5f, 0x3800)]
    [InlineData(65504.0f, 0x7BFF)]
    [InlineData(65520.0f, 0x7BFF)]     // truncates (System.Half rounds this to infinity)
    [InlineData(70000.0f, 0x7C00)]     // exponent > 15 -> infinity
    [InlineData(1.0009765625f, 0x3C01)]
    [InlineData(1.00146484375f, 0x3C01)] // 1.5 ulp truncates down (System.Half: 0x3C02)
    [InlineData(6.103515625e-05f, 0x0400)] // smallest normal
    [InlineData(9.5367431640625e-07f, 0x0010)] // 2^-20 subnormal
    [InlineData(5.9604644775390625e-08f, 0x0001)] // 2^-24
    [InlineData(1e-8f, 0x0000)]          // below 2^-24 flushes
    [InlineData(-1e-8f, 0x8000)]
    [InlineData(float.PositiveInfinity, 0x7C00)]
    [InlineData(float.NaN, 0xFC00)]      // nan becomes infinity (exponent 128 > 15), .NET's NaN has the sign bit set
    public void FloatToHalf_IsSpartanBitExact(float value, int expected)
    {
        Assert.Equal((ushort)expected, VertexPack.FloatToHalf(value));
    }

    [Fact]
    public void HalfToFloat_IsSpartanBitExact()
    {
        Assert.Equal(0.0f, VertexPack.HalfToFloat(0x7C00)); // inf -> 0 (Spartan)
        Assert.Equal(0.0f, VertexPack.HalfToFloat(0x7E00)); // nan -> 0
        Assert.True(float.IsNegative(VertexPack.HalfToFloat(0x8000)));
        // every finite half decodes exactly like System.Half and re-encodes to itself
        for (int h = 0; h < 0x10000; h++)
        {
            ushort u = (ushort)h;
            if ((u & 0x7C00) == 0x7C00) continue;
            float f = VertexPack.HalfToFloat(u);
            Assert.Equal((float)BitConverter.UInt16BitsToHalf(u), f);
            Assert.Equal(u, VertexPack.FloatToHalf(f));
        }
    }

    [Fact]
    public void Half_Truncation_ErrorBound_VsSystemHalf()
    {
        var rng = new Random(1);
        for (int k = 0; k < 100000; k++)
        {
            float x = (rng.NextSingle() * 2 - 1) * MathF.Pow(2, rng.Next(-14, 15));
            ushort spartan = VertexPack.FloatToHalf(x);
            float back     = VertexPack.HalfToFloat(spartan);
            // truncation: same sign, toward zero, less than one half ulp (2^-10 relative) away
            Assert.True(MathF.Abs(back) <= MathF.Abs(x));
            Assert.True(MathF.Abs(x - back) <= MathF.Max(MathF.Abs(x) * (1.0f / 1024.0f), 5.9604645e-08f)); // subnormals: one 2^-24 step
            // and at most one code below System.Half's round to nearest
            ushort net = BitConverter.HalfToUInt16Bits((Half)x);
            Assert.InRange(net - spartan, 0, 1);
        }
    }

    // octahedral -----------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Axes() => new[]
    {
        new object[] {  Vector3.UnitX, 0x00007FFFu }, new object[] { -Vector3.UnitX, 0x00008001u },
        new object[] {  Vector3.UnitY, 0x7FFF0000u }, new object[] { -Vector3.UnitY, 0x80010000u },
        new object[] {  Vector3.UnitZ, 0x00000000u }, new object[] { -Vector3.UnitZ, 0x7FFF7FFFu },
    };

    [Theory]
    [MemberData(nameof(Axes))]
    public void Oct_Axes_AndPoles_AreExact(Vector3 axis, uint packed)
    {
        Assert.Equal(packed, VertexPack.PackOctSnorm16(axis));
        Assert.Equal(axis, VertexPack.UnpackOctSnorm16(packed));
    }

    [Fact]
    public void Oct_NegativeZ_FoldCorners_AllDecodeToMinusZ()
    {
        foreach (uint p in new[] { 0x7FFF7FFFu, 0x80017FFFu, 0x7FFF8001u, 0x80018001u, 0x80008000u })
            Assert.Equal(-Vector3.UnitZ, VertexPack.UnpackOctSnorm16(p)); // -32768 clamps to -1
        // the equator survives both hemispheres
        var e = Vector3.Normalize(new Vector3(1, 1, 0));
        Assert.True(Vector3.Dot(e, VertexPack.UnpackOctSnorm16(VertexPack.PackOctSnorm16(e))) > 0.9999999f);
        var below = Vector3.Normalize(new Vector3(0.3f, -0.4f, -0.8f));
        Assert.True(Vector3.Dot(below, VertexPack.UnpackOctSnorm16(VertexPack.PackOctSnorm16(below))) > 0.99999f);
    }

    [Fact]
    public void Oct_Degenerate_Inputs()
    {
        Assert.Equal(0u, VertexPack.PackOctSnorm16(Vector3.Zero));
        Assert.Equal(VertexPack.PackOctSnorm16(Vector3.UnitX), VertexPack.PackOctSnorm16(new Vector3(5, 0, 0))); // scale free
        Assert.Equal(VertexPack.PackOctSnorm16(-Vector3.UnitZ), VertexPack.PackOctSnorm16(new Vector3(0, 0, -1e-30f)));
    }

    [Fact]
    public void Oct_RoundTrip_ErrorBound()
    {
        // snorm16 octahedral: one oct step is 1/32767, a few times 1e-5 rad on the sphere
        var rng = new Random(2);
        float worst = 0;
        for (int k = 0; k < 200000; k++)
        {
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1));
            if (float.IsNaN(d.X)) continue;
            Vector3 r = VertexPack.UnpackOctSnorm16(VertexPack.PackOctSnorm16(d));
            Assert.InRange(r.Length(), 0.99999f, 1.00001f);
            // atan2(|cross|, dot): float acos is too coarse near 1 to measure this
            worst = MathF.Max(worst, MathF.Atan2(Vector3.Cross(d, r).Length(), Vector3.Dot(d, r)));
            // decode-encode is stable (quantised values are fixed points)
            Assert.Equal(VertexPack.PackOctSnorm16(r), VertexPack.PackOctSnorm16(VertexPack.UnpackOctSnorm16(VertexPack.PackOctSnorm16(r))));
        }
        Assert.True(worst < 1e-4f, $"worst {worst} rad"); // ~0.006 degrees
    }

    [Fact]
    public void Oct_Rounding_IsTiesAwayFromZero()
    {
        // ox = 0.5/32767 * k lands exactly on .5 steps: (k + 0.5) / 32767 * 32767 rounds away from zero
        float half_step = 0.5f / 32767.0f;
        uint p = VertexPack.PackOctSnorm16(new Vector3(half_step, 0, 1 - half_step));
        Assert.Equal(1u, p & 0xFFFF);
        uint n = VertexPack.PackOctSnorm16(new Vector3(-half_step, 0, 1 - half_step));
        Assert.Equal(0xFFFFu, n & 0xFFFF);
    }

    [Fact]
    public void Tangent_DirectionAndSign_Survive()
    {
        // Spartan 4c07baa has no bitangent sign bit: a mirrored tangent is simply the opposite direction
        var t = Vector3.Normalize(new Vector3(0.2f, -0.9f, 0.3f));
        var a = new RHI_Vertex_PosTexNorTan(Vector3.Zero, Vector2.Zero, Vector3.UnitZ, t);
        var b = new RHI_Vertex_PosTexNorTan(Vector3.Zero, Vector2.Zero, Vector3.UnitZ, -t);
        Assert.NotEqual(a.tan, b.tan);
        Assert.True(Vector3.Dot(a.GetTangent(), t) > 0.99999f);
        Assert.True(Vector3.Dot(b.GetTangent(), -t) > 0.99999f);
        Assert.True(Vector3.Dot(a.GetTangent(), b.GetTangent()) < -0.99999f);
    }

    // welding --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Weld_Merges_VerticesThatQuantiseIdentically()
    {
        // two triangles sharing an edge whose duplicated corners differ only below half/snorm16 precision
        var n1 = Vector3.Normalize(new Vector3(0.1f, 0.2f, 1));
        var n2 = Vector3.Normalize(n1 + new Vector3(1e-7f, 0, 0));
        var v = new List<RHI_Vertex_PosTexNorTan>
        {
            new(new Vector3(0, 0, 0), new Vector2(0, 0),           n1, Vector3.UnitX),
            new(new Vector3(1, 0, 0), new Vector2(1, 0),           n1, Vector3.UnitX),
            new(new Vector3(0, 0, 1), new Vector2(0, 1),           n1, Vector3.UnitX),
            new(new Vector3(1, 0, 0), new Vector2(1.0001f, 0),     n2, Vector3.UnitX), // uv truncates to 1
            new(new Vector3(0, 0, 1), new Vector2(0, 1.0002f),     n2, Vector3.UnitX),
            new(new Vector3(1, 0, 1), new Vector2(1, 1),           n1, Vector3.UnitX),
        };
        Assert.Equal(v[1], v[3]);
        Assert.Equal(v[2], v[4]);
        var i = new List<uint> { 0, 2, 1, 3, 4, 5 };
        GeometryProcessing.WeldAndOptimize(v, i);
        Assert.Equal(4, v.Count);
        Assert.Equal(6, i.Count);

        // a real uv seam (above half precision) is still kept apart
        var seam = new List<RHI_Vertex_PosTexNorTan>
        {
            new(new Vector3(0, 0, 0), new Vector2(0, 0), n1), new(new Vector3(1, 0, 0), new Vector2(1, 0), n1), new(new Vector3(0, 0, 1), new Vector2(0, 1), n1),
            new(new Vector3(1, 0, 0), new Vector2(1.01f, 0), n1), new(new Vector3(0, 0, 1), new Vector2(0, 1), n1), new(new Vector3(1, 0, 1), new Vector2(1, 1), n1),
        };
        var si = new List<uint> { 0, 2, 1, 3, 4, 5 };
        GeometryProcessing.WeldAndOptimize(seam, si);
        Assert.Equal(5, seam.Count);
    }

    // impostor bake reads the quantised attributes ------------------------------------------------------------------

    [Fact]
    public void ImpostorBake_UsesQuantisedUvs()
    {
        // uv bounds come from the decoded half uvs, so a uv like 0.3 is the half 0.2998... everywhere
        var v = new RHI_Vertex_PosTexNorTan(Vector3.Zero, new Vector2(0.3f, 0.3f));
        Assert.Equal(VertexPack.HalfToFloat(VertexPack.FloatToHalf(0.3f)), v.GetUv().X);
        Assert.NotEqual(0.3f, v.GetUv().X);
    }

    // glsl decode ----------------------------------------------------------------------------------------------------

    [Fact]
    public void VertexDecodeGlsl_Compiles()
    {
        string cache = Path.Combine(Path.GetTempPath(), "phalanx-vertex-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var compiler = new ShaderCompiler(cacheDirectory: cache);
            const string src = @"#version 460
#extension GL_GOOGLE_include_directive : require
#include ""common_resources.glsl""
layout(local_size_x = 64) in;
layout(std430, set = 1, binding = 0) readonly buffer Vertices { PulledVertex geometry_vertices[]; };
layout(std430, set = 1, binding = 1) buffer Out { vec4 outs[]; };
void main()
{
    PulledVertex v = geometry_vertices[gl_GlobalInvocationID.x];
    vec2 uv = unpack_vertex_uv(v.uv);
    outs[gl_GlobalInvocationID.x] = vec4(unpack_vertex_position(v) + unpack_vertex_oct(v.normal) + unpack_vertex_oct(v.tangent), uv.x + uv.y);
}
";
            uint[] spv = compiler.CompileSource(src, ShaderStage.Compute, "vertex_probe.comp");
            Assert.Equal(0x07230203u, spv[0]);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }
}
