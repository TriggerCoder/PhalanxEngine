using System.Numerics;
using Phalanx;
using Xunit;
using Phalanx.Math;
using Phalanx.Geometry;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;
using Phalanx.Rendering;

namespace Phalanx.Tests;

public class ImpostorTests
{
    private const uint Frames = Mesh.MeshImpostorFrames;

    private static RHI_Vertex_PosTexNorTan V(Vector3 p, Vector2 uv, Vector3 n) => new(p, uv, n, Vector3.UnitX);

    // horizontal quad (normal +y) of half size h at height y, uv 0..1
    private static void AddQuad(List<RHI_Vertex_PosTexNorTan> v, List<uint> i, float h, float y, Vector2 uv0, Vector2 uv1)
    {
        uint b = (uint)v.Count;
        v.Add(V(new Vector3(-h, y, -h), new Vector2(uv0.X, uv0.Y), Vector3.UnitY));
        v.Add(V(new Vector3( h, y, -h), new Vector2(uv1.X, uv0.Y), Vector3.UnitY));
        v.Add(V(new Vector3(-h, y,  h), new Vector2(uv0.X, uv1.Y), Vector3.UnitY));
        v.Add(V(new Vector3( h, y,  h), new Vector2(uv1.X, uv1.Y), Vector3.UnitY));
        i.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
    }

    [Fact]
    public void FrameDirection_MatchesSpartanFormula_AndStaysAboveHorizon()
    {
        for (uint y = 0; y < Frames; y++)
            for (uint x = 0; x < Frames; x++)
            {
                float ox = (x + 0.5f) / Frames * 2.0f - 1.0f, oy = (y + 0.5f) / Frames * 2.0f - 1.0f;
                float px = (ox + oy) * 0.5f, pz = (ox - oy) * 0.5f;
                Vector3 expected = Vector3.Normalize(new Vector3(px, 1.0f - MathF.Abs(px) - MathF.Abs(pz), pz));

                Vector3 d = GeometryProcessing.ImpostorFrameDirection(x, y, Frames);
                Assert.True(Vector3.Distance(expected, d) < 1e-6f);
                Assert.True(d.Y > 0.0f);
                Assert.Equal(1.0f, d.Length(), 5);
            }
        // the grid is symmetric: frame (x, y) and (F-1-x, F-1-y) mirror x and z
        Vector3 a = GeometryProcessing.ImpostorFrameDirection(1, 2, Frames);
        Vector3 b = GeometryProcessing.ImpostorFrameDirection(Frames - 2, Frames - 3, Frames);
        Assert.True(Vector3.Distance(a, new Vector3(-b.X, b.Y, -b.Z)) < 1e-6f);
    }

    [Fact]
    public void FrameBasis_IsOrthonormal()
    {
        for (uint y = 0; y < Frames; y++)
            for (uint x = 0; x < Frames; x++)
            {
                Vector3 d = GeometryProcessing.ImpostorFrameDirection(x, y, Frames);
                GeometryProcessing.ImpostorFrameBasis(d, out Vector3 r, out Vector3 u);
                Assert.Equal(1.0f, r.Length(), 5);
                Assert.Equal(1.0f, u.Length(), 5);
                Assert.Equal(0.0f, Vector3.Dot(r, d), 5);
                Assert.Equal(0.0f, Vector3.Dot(u, d), 5);
                Assert.Equal(0.0f, Vector3.Dot(r, u), 5);
                Assert.True(Vector3.Distance(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, d)), r) < 1e-5f);
            }
        // straight up switches the reference to +z
        GeometryProcessing.ImpostorFrameBasis(Vector3.UnitY, out Vector3 r2, out Vector3 u2);
        Assert.Equal(Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, Vector3.UnitY)), r2);
        Assert.Equal(Vector3.Cross(Vector3.UnitY, r2), u2);
    }

    [Fact]
    public void NearestFrame_InvertsFrameDirection_AndClampsBelowHorizon()
    {
        for (uint y = 0; y < Frames; y++)
            for (uint x = 0; x < Frames; x++)
                Assert.Equal((x, y), GeometryProcessing.ImpostorNearestFrame(GeometryProcessing.ImpostorFrameDirection(x, y, Frames), Frames));

        // a view from below reuses the frame of its horizon projection
        Vector3 below = new Vector3(0.6f, -0.8f, 0.0f);
        Assert.Equal(GeometryProcessing.ImpostorNearestFrame(new Vector3(0.6f, 0.0f, 0.0f), Frames),
                     GeometryProcessing.ImpostorNearestFrame(below, Frames));
    }

    [Fact]
    public void TexelWordOffset_MatchesDocumentedLayout()
    {
        uint res = 64, layers = 2;
        for (uint fy = 0; fy < Frames; fy += 3)
            for (uint fx = 0; fx < Frames; fx += 3)
                foreach (var (px, py) in new[] { (0u, 0u), (5u, 7u), (63u, 63u) })
                    Assert.Equal((((fy * Frames + fx) * res + py) * res + px) * layers * 2,
                                 GeometryProcessing.ImpostorTexelWordOffset(fx, fy, px, py, Frames, res, layers));
    }

    [Fact]
    public void Bake_Size_Empty_AndTopFrameCoverage()
    {
        Assert.All(GeometryProcessing.BakeImpostor(default, default, Frames, 16, 2, Vector3.Zero, 1.0f, Vector2.Zero, Vector2.One), w => Assert.Equal(0u, w));

        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        AddQuad(v, i, 0.5f, 0.0f, Vector2.Zero, Vector2.One);
        float radius = MathF.Sqrt(0.5f);
        uint res = 16, layers = 2;

        uint[] atlas = GeometryProcessing.BakeImpostor(v.ToArray(), i.ToArray(), Frames, res, layers, Vector3.Zero, radius, Vector2.Zero, Vector2.One);
        Assert.Equal((int)(Frames * Frames * res * res * layers * 2), atlas.Length);

        // the four frames around the grid centre look down almost straight, the quad covers the atlas centre there
        uint centre = GeometryProcessing.ImpostorTexelWordOffset(Frames / 2, Frames / 2, res / 2, res / 2, Frames, res, layers);
        uint geometry = atlas[centre + 1];
        Assert.True((geometry & (1u << 31)) != 0);
        Assert.True((atlas[centre + 3] & (1u << 31)) == 0); // single surface: second layer empty

        // uv decodes to roughly the middle of the quad
        float u = (atlas[centre] & 0xFFFF) / 65535.0f, vv = (atlas[centre] >> 16) / 65535.0f;
        Assert.InRange(u, 0.3f, 0.7f);
        Assert.InRange(vv, 0.3f, 0.7f);

        // the +y normal faces the viewer: oct-encoded frame normal is mostly +z
        float ox = (geometry & 0xFF) * (2.0f / 255.0f) - 1.0f, oy = ((geometry >> 8) & 0xFF) * (2.0f / 255.0f) - 1.0f;
        Assert.True(1.0f - MathF.Abs(ox) - MathF.Abs(oy) > 0.8f);

        // depth at the centre is ~0 (quad through the origin) -> unorm15 near half
        uint dz = (geometry >> 16) & 0x7FFF;
        Assert.InRange(dz, 16000u, 16800u);

        // corner texels of the frame stay empty, the quad doesn't reach the bounding circle's corners
        Assert.True((atlas[GeometryProcessing.ImpostorTexelWordOffset(Frames / 2, Frames / 2, 0, 0, Frames, res, layers) + 1] & (1u << 31)) == 0);
    }

    [Fact]
    public void Bake_SortsLayers_NearestFirst()
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        AddQuad(v, i, 0.5f, -0.25f, new Vector2(0.0f, 0.0f), new Vector2(0.5f, 0.5f)); // far (lower) quad drawn first
        AddQuad(v, i, 0.5f,  0.25f, new Vector2(0.5f, 0.5f), new Vector2(1.0f, 1.0f)); // near (upper) quad
        uint res = 16, layers = 2;

        uint[] atlas = GeometryProcessing.BakeImpostor(v.ToArray(), i.ToArray(), Frames, res, layers, Vector3.Zero, 1.0f, Vector2.Zero, Vector2.One);

        uint b = GeometryProcessing.ImpostorTexelWordOffset(Frames / 2, Frames / 2, res / 2, res / 2, Frames, res, layers);
        Assert.True((atlas[b + 1] & (1u << 31)) != 0 && (atlas[b + 3] & (1u << 31)) != 0);
        uint near_depth = (atlas[b + 1] >> 16) & 0x7FFF, far_depth = (atlas[b + 3] >> 16) & 0x7FFF;
        Assert.True(near_depth > far_depth);
        Assert.True((atlas[b] & 0xFFFF) / 65535.0f > 0.5f);     // layer 0 samples the near quad's uv range
        Assert.True((atlas[b + 2] & 0xFFFF) / 65535.0f < 0.5f); // layer 1 the far one
    }

    [Fact]
    public void Bake_ThinNeedle_StillLeavesAMark()
    {
        // a sliver far thinner than a texel misses every texel centre, the fallback writes its centroid
        var v = new List<RHI_Vertex_PosTexNorTan>
        {
            V(new Vector3(-0.5f, 0, 0), Vector2.Zero, Vector3.UnitY),
            V(new Vector3( 0.5f, 0, 0), Vector2.One, Vector3.UnitY),
            V(new Vector3( 0.5f, 0, 0.0001f), Vector2.One, Vector3.UnitY),
        };
        uint[] atlas = GeometryProcessing.BakeImpostor(v.ToArray(), new uint[] { 0, 1, 2 }, Frames, 8, 1, Vector3.Zero, 0.5f, Vector2.Zero, Vector2.One);
        int covered_frames = 0;
        for (uint f = 0; f < Frames * Frames; f++)
        {
            bool any = false;
            for (uint t = 0; t < 64; t++) any |= (atlas[(f * 64 + t) * 2 + 1] & (1u << 31)) != 0;
            if (any) covered_frames++;
        }
        Assert.Equal((int)(Frames * Frames), covered_frames);
    }

    [Fact]
    public void Bake_IsDeterministic()
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        AddQuad(v, i, 0.5f, 0.1f, Vector2.Zero, Vector2.One);
        AddQuad(v, i, 0.3f, -0.2f, new Vector2(0.2f, 0.2f), new Vector2(0.4f, 0.9f));
        uint[] a = GeometryProcessing.BakeImpostor(v.ToArray(), i.ToArray(), Frames, 32, 2, new Vector3(0, 0, 0), 0.8f, Vector2.Zero, Vector2.One);
        uint[] b = GeometryProcessing.BakeImpostor(v.ToArray(), i.ToArray(), Frames, 32, 2, new Vector3(0, 0, 0), 0.8f, Vector2.Zero, Vector2.One);
        Assert.Equal(a, b);
    }

    private static (List<RHI_Vertex_PosTexNorTan>, List<uint>) Crown(int cards, float spread)
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        var rng = new Random(7);
        for (int c = 0; c < cards; c++)
        {
            var o = new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f) * spread;
            uint b = (uint)v.Count;
            v.Add(V(o + new Vector3(-0.1f, -0.1f, 0), new Vector2(0.25f, 0.5f), Vector3.UnitZ));
            v.Add(V(o + new Vector3( 0.1f, -0.1f, 0), new Vector2(0.75f, 0.5f), Vector3.UnitZ));
            v.Add(V(o + new Vector3(-0.1f,  0.1f, 0), new Vector2(0.25f, 1.0f), Vector3.UnitZ));
            v.Add(V(o + new Vector3( 0.1f,  0.1f, 0), new Vector2(0.75f, 1.0f), Vector3.UnitZ));
            i.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
        return (v, i);
    }

    [Fact]
    public void Mesh_FoliagePath_AddsImpostorAfterLods()
    {
        using var mesh = new Mesh();
        mesh.SetFlag(MeshFlags.PostProcessGenerateLods);
        var (v, i) = Crown(200, 2.0f);
        var aabb = new BoundingBox(v);

        int sub = mesh.AddGeometry(v, i, generate_lods: true, foliage_cards: true);

        MeshImpostor? imp = mesh.GetImpostor(sub);
        Assert.NotNull(imp);
        Assert.Equal(aabb.GetCenter(), imp!.center);
        Assert.Equal(v.Max(x => (x.pos - imp.center).Length()), imp.radius);
        Assert.Equal(new Vector2(0.25f, 0.5f), imp.uv_min);
        Assert.Equal(new Vector2(0.5f, 0.5f), imp.uv_scale);
        Assert.Equal(Mesh.MeshImpostorResolution, imp.resolution); // radius < 3
        Assert.Equal((int)(Frames * Frames * imp.resolution * imp.resolution * Mesh.MeshImpostorLayers * 2), imp.texels.Length);

        // the card is appended after the whole lod chain and isn't one of the lods
        Assert.True(mesh.GetLodCount(sub) > 1);
        Assert.Equal((uint)(mesh.GetVertexCount() - 4), imp.lod.vertex_offset);
        Assert.Equal(4u, imp.lod.vertex_count);
        Assert.Equal(6u, imp.lod.index_count);
        float r = imp.radius;
        Assert.Equal(imp.center - new Vector3(r), imp.lod.aabb.GetMin());
        Assert.Equal(imp.center + new Vector3(r), imp.lod.aabb.GetMax());

        // atlas matches a direct bake of lod 0
        var li = new List<uint>();
        var lv = new List<RHI_Vertex_PosTexNorTan>();
        mesh.GetGeometryLod(sub, 0, li, lv);
        Assert.Equal(GeometryProcessing.BakeImpostor(lv.ToArray(), li.ToArray(), Frames, imp.resolution, Mesh.MeshImpostorLayers, imp.center, imp.radius, imp.uv_min, imp.uv_scale), imp.texels);
    }

    [Fact]
    public void Mesh_LargeCrown_GetsSharperAtlas_AndNonFoliageGetsNone()
    {
        using var big = new Mesh();
        big.SetFlag(MeshFlags.PostProcessGenerateLods);
        var (v, i) = Crown(100, 8.0f);
        int sub = big.AddGeometry(v, i, generate_lods: true, impostor: true);
        Assert.Equal(Mesh.MeshImpostorResolutionLarge, big.GetImpostor(sub)!.resolution);

        using var plain = new Mesh();
        plain.SetFlag(MeshFlags.PostProcessGenerateLods);
        var (v2, i2) = Crown(100, 2.0f);
        Assert.Null(plain.GetImpostor(plain.AddGeometry(v2, i2, generate_lods: true)));

        // Spartan only builds it inside the lod generation branch
        using var nolods = new Mesh();
        var (v3, i3) = Crown(100, 2.0f);
        Assert.Null(nolods.GetImpostor(nolods.AddGeometry(v3, i3, generate_lods: false, foliage_cards: true)));
    }

    [Fact]
    public void WriteDrawData_PacksLayoutAndFlags()
    {
        var imp = new MeshImpostor { resolution = 128, texel_offset = 1234, center = new Vector3(1, 2, 3), radius = 4, uv_min = new Vector2(0.1f, 0.2f), uv_scale = new Vector2(0.5f, 0.6f) };
        var d = new DrawData();
        d.flags = 1u << 15;
        imp.WriteDrawData(ref d);
        Assert.Equal(8u | (128u << 8) | (2u << 24), d.impostor_layout);
        Assert.Equal((1u << 15) | (1u << 14) | 8u | 16u, d.flags);
        Assert.Equal(1234u, d.impostor_texel_offset);
        Assert.Equal(new Vector3(1, 2, 3), d.impostor_center);
        Assert.Equal(4f, d.impostor_radius);
        Assert.Equal(new Vector2(0.5f, 0.6f), d.impostor_uv_scale);
    }

    [Fact]
    public void ImpostorGlsl_Compiles()
    {
        string cache = Path.Combine(Path.GetTempPath(), "phalanx-impostor-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var compiler = new ShaderCompiler(cacheDirectory: cache);
            const string src = @"#version 460
#extension GL_GOOGLE_include_directive : require
#include ""impostor.glsl""
layout(local_size_x = 8, local_size_y = 8) in;
layout(std430, binding = 0) buffer OutF { uvec4 frames_out[]; };
layout(std430, binding = 2) buffer OutD { vec4 dirs[]; };
layout(std430, binding = 1) readonly buffer Texels { uint impostor_texels[]; };
void main()
{
    uvec2 f = gl_GlobalInvocationID.xy;
    vec3 d = impostor_frame_direction(f, 8u);
    vec3 r, u;
    impostor_frame_basis(d, r, u);
    uint layout_ = 8u | (64u << 8) | (2u << 24);
    uint base = impostor_texel_base(f, uvec2(0u), impostor_layout_frames(layout_), impostor_layout_resolution(layout_), impostor_layout_layers(layout_));
    uint g = impostor_texels[base + 1u];
    vec3 n = impostor_covered(g) ? impostor_unpack_normal(g) : vec3(0.0, 0.0, 1.0);
    vec2 uv = impostor_unpack_uv(impostor_texels[base], vec2(0.0), vec2(1.0));
    frames_out[f.y * 8u + f.x] = uvec4(impostor_nearest_frame(d, 8u), base, 0u);
    dirs[f.y * 8u + f.x] = vec4(d + r + u + n, uv.x + impostor_unpack_depth(g, 1.0));
}
";
            uint[] spv = compiler.CompileSource(src, ShaderStage.Compute, "impostor_probe.comp");
            Assert.Equal(0x07230203u, spv[0]);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }
}
