using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// shadow maps on lavapipe: the atlas packing (UpdateShadowAtlas), the directional cascades, and a cube shadowing a
// ground quad under a sun, a spot and a point light (Pass_ShadowMaps + compute_shadow in Pass_Light)
public partial class RendererSceneTests
{
    // left-handed look rotation: the entity's forward (+z) along `forward`
    private static Quaternion LookRotation(Vector3 forward)
    {
        forward = Vector3.Normalize(forward);
        Vector3 up_ref = System.MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up_ref, forward));
        Vector3 up = Vector3.Cross(forward, right);
        var m = new Matrix4x4(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    // the cube scene plus a 20 m ground quad under it (top at y = -0.5), the camera above and in front
    private static Scene CreateShadowScene()
    {
        Scene scene = CreateScene(new Vector4(0.8f, 0.8f, 0.8f, 1.0f));
        Entity ground = World.World.CreateEntity();
        ground.SetPosition(new Vector3(0, -0.5f, 0));
        ground.SetScale(new Vector3(20, 1, 20));
        Render render = ground.AddComponent<Render>()!;
        render.SetMesh(MeshType.Quad);
        render.SetMaterial(scene.material);

        Entity camera = scene.camera.GetEntity()!;
        camera.SetPosition(new Vector3(0, 4, -5));
        camera.SetRotation(LookRotation(new Vector3(0, -0.5f, 0.5f) - new Vector3(0, 4, -5)));
        Renderer.ImageBasedLighting = false; // direct light only, Run turns it back on
        World.World.Tick();
        return scene;
    }

    private static (uint x, uint y) Pixel(Scene scene, Vector3 world) => Pixel(scene, world, Width, Height);

    private static (uint x, uint y) Pixel(Scene scene, Vector3 world, uint width, uint height)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1), scene.camera.GetViewMatrix() * scene.camera.GetProjectionMatrix());
        Vector2 ndc = new Vector2(clip.X, clip.Y) / clip.W;
        uint x = (uint)((ndc.X * 0.5f + 0.5f) * width), y = (uint)((-ndc.Y * 0.5f + 0.5f) * height);
        Assert.InRange(x, 0u, width - 1);
        Assert.InRange(y, 0u, height - 1);
        return (x, y);
    }

    private static Light AddLight(LightType type, Vector3 position, Vector3 forward)
    {
        Entity e = World.World.CreateEntity();
        e.SetPosition(position);
        e.SetRotation(LookRotation(forward));
        Light light = e.AddComponent<Light>()!;
        light.SetLightType(type);
        if (type != LightType.Directional)
            light.SetRange(30.0f);
        return light;
    }

    // a ground point behind the cube from the light against an unoccluded one at a similar distance
    private static void AssertShadowed(Scene scene, Light light, Vector3 shadowed, Vector3 lit)
    {
        Frame(scene);
        Frame(scene);
        Assert.Equal(1u, Renderer.GetLightCount());

        // the light parameters say the light has (effective) shadows and an atlas slot
        byte[] table = RHI_Device.ReadbackBuffer(Renderer.GetBuffer(Renderer_Buffer.LightParameters)!, 0, (ulong)Marshal.SizeOf<LightParameters>());
        LightParameters uploaded = MemoryMarshal.Read<LightParameters>(table);
        Assert.Equal(1u << 3, uploaded.flags & (1u << 3));
        // contact shadows (bit 4) only for the sun (Renderer.cpp:3285-3287)
        Assert.Equal(light.GetLightType() == LightType.Directional ? 1u << 4 : 0u, uploaded.flags & (1u << 4));

        // the gbuffer sees ground at both points (the cube does not hide them)
        (uint sx, uint sy) = Pixel(scene, shadowed);
        (uint lx, uint ly) = Pixel(scene, lit);
        byte[] normal = Read(Renderer_RenderTarget.gbuffer_normal);
        Assert.Equal(1.0f, Half(normal, Index(sx, sy) * 8 + 2), 2);
        Assert.Equal(1.0f, Half(normal, Index(lx, ly) * 8 + 2), 2);

        Vector3 d_shadowed = LightTarget(Renderer_RenderTarget.light_diffuse, sx, sy);
        Vector3 d_lit      = LightTarget(Renderer_RenderTarget.light_diffuse, lx, ly);
        Assert.True(d_lit.X > 0.01f, $"lit {d_lit}");
        Assert.True(d_shadowed.X < 0.35f * d_lit.X, $"shadowed {d_shadowed} lit {d_lit}");

        // and with the shadows off the shadowed point is lit again
        light.SetFlag(LightFlags.Shadows, false);
        Frame(scene);
        Vector3 d_off = LightTarget(Renderer_RenderTarget.light_diffuse, sx, sy);
        Assert.True(d_off.X > 0.5f * d_lit.X, $"unshadowed {d_off} lit {d_lit}");
    }

    [SkippableFact]
    public void The_sun_casts_the_cubes_shadow_on_the_ground()
    {
        Run(() =>
        {
            Scene scene = CreateShadowScene();
            // a 2 m cube on the ground: the 1024 atlas' near cascade texels are 0.25 m and spartan's normal offset bias
            // (0.6 + 2.2 slope texels) lifts the receiver ~0.6 m, which would let a unit cube's shadow escape
            scene.cube.SetScale(new Vector3(2, 2, 2));
            scene.cube.SetPosition(new Vector3(0, 0.5f, 0));
            // the sun comes down along (2, -1, 0): the cube's shadow falls towards +x, over x = 1 .. 5
            Light sun = AddLight(LightType.Directional, new Vector3(0, 10, 0), new Vector3(2, -1, 0));
            AssertShadowed(scene, sun, shadowed: new Vector3(3.0f, -0.5f, 0), lit: new Vector3(-2.5f, -0.5f, 0));
        });
    }

    [SkippableFact]
    public void A_spot_light_casts_the_cubes_shadow_on_the_ground()
    {
        Run(() =>
        {
            Scene scene = CreateShadowScene();
            Light spot = AddLight(LightType.Spot, new Vector3(-2, 2, 0), new Vector3(3, -2.5f, 0));
            // the ray from the light through (0.3, 0.3, 0) inside the cube meets the ground at x = 1.38
            AssertShadowed(scene, spot, shadowed: new Vector3(1.38f, -0.5f, 0), lit: new Vector3(1.38f, -0.5f, 1.5f));
        });
    }

    [SkippableFact]
    public void A_point_light_casts_the_cubes_shadow_on_the_ground()
    {
        Run(() =>
        {
            Scene scene = CreateShadowScene();
            Light point = AddLight(LightType.Point, new Vector3(-2, 2, 0), Vector3.UnitZ);
            Assert.Equal(6, point.GetSliceCount());
            // 0.5 m above the cube: its shadow rims it out to 1.5 m (the -Y face), the patch in front clears its edge
            point.GetEntity().SetPosition(new Vector3(0, 1, 0));
            AssertShadowed(scene, point, shadowed: new Vector3(1.0f, -0.5f, 0), lit: new Vector3(0, -0.5f, -1.6f));
        });
    }

    [SkippableFact]
    public void The_near_cascade_covers_the_camera_surroundings_and_the_far_one_the_distance()
    {
        Run(() =>
        {
            Scene scene = CreateShadowScene();
            Light sun = AddLight(LightType.Directional, new Vector3(0, 10, 0), new Vector3(1, -2, 0));
            Frame(scene);
            Assert.Equal(2, sun.GetSliceCount());

            byte[] table = RHI_Device.ReadbackBuffer(Renderer.GetBuffer(Renderer_Buffer.LightParameters)!, 0, (ulong)Marshal.SizeOf<LightParameters>());
            LightParameters uploaded = MemoryMarshal.Read<LightParameters>(table);
            Matrix4x4 near = uploaded.transform[0], far = uploaded.transform[1];
            Assert.Equal(sun.GetViewProjectionMatrix(0), near);
            Assert.Equal(sun.GetViewProjectionMatrix(1), far);

            // the shader picks the first cascade whose ndc holds the point (shadow_mapping.hlsl): the camera's
            // surroundings land in cascade 0, a point 200 m out only in cascade 1
            static bool Inside(Matrix4x4 m, Vector3 p)
            {
                Vector4 c = Vector4.Transform(new Vector4(p, 1), m);
                Vector3 ndc = new Vector3(c.X, c.Y, c.Z) / c.W;
                return System.MathF.Abs(ndc.X) <= 1 && System.MathF.Abs(ndc.Y) <= 1 && ndc.Z >= 0 && ndc.Z <= 1;
            }
            Vector3 camera = scene.camera.GetEntity()!.GetPosition();
            Assert.True(Inside(near, Vector3.Zero));
            Assert.True(Inside(near, camera + new Vector3(10, 0, 10)));
            Vector3 distant = camera + new Vector3(200, 0, 200);
            Assert.False(Inside(near, distant));
            Assert.True(Inside(far, distant));
            Assert.True(Inside(far, Vector3.Zero));

            // the near cascade is 64 m wide, the far 2000 m: the near texels are far finer
            Assert.True(System.MathF.Abs(near.M11) > 10 * System.MathF.Abs(far.M11), $"{near.M11} vs {far.M11}");
        });
    }

    [SkippableFact]
    public void The_shadow_atlas_packs_uniform_slices_with_borders()
    {
        Run(() =>
        {
            Scene scene = CreateShadowScene();
            Assert.Equal(1024u, Renderer.GetRenderTargetTexture(Renderer_RenderTarget.shadow_atlas)!.GetWidth());

            // one light, one slice (spot): the whole atlas
            Light spot = AddLight(LightType.Spot, new Vector3(-2, 2, 0), new Vector3(3, -2.5f, 0));
            Frame(scene);
            var slices = Renderer.GetShadowSlices();
            Assert.Single(slices);
            Assert.Equal(1024u, slices[0].resolution);
            Assert.Equal(new Vector2(0, 0), new Vector2(spot.GetAtlasRectangle(0).X, spot.GetAtlasRectangle(0).Y));

            // a sun (2 cascades) and the spot: 3 slices, the largest uniform size that row packs is 508 (2 x 508 + 8)
            Light sun = AddLight(LightType.Directional, new Vector3(0, 10, 0), new Vector3(1, -2, 0));
            Frame(scene);
            AssertPacking(Renderer.GetShadowSlices(), 3, 508);

            // plus a point light (6 faces): 9 slices, 3 per row, (1024 - 2 x 8) / 3 = 336
            AddLight(LightType.Point, new Vector3(2, 2, 0), Vector3.UnitZ);
            Frame(scene);
            AssertPacking(Renderer.GetShadowSlices(), 9, 336);

            // a light past the shadow distance (64 m) gets no slice
            AddLight(LightType.Spot, new Vector3(0, 2, 100), Vector3.UnitZ);
            Frame(scene);
            AssertPacking(Renderer.GetShadowSlices(), 9, 336);
            Assert.Equal(4u, Renderer.GetLightCount());
        });

        static void AssertPacking(IReadOnlyList<Renderer.ShadowSlice> slices, int count, uint resolution)
        {
            Assert.Equal(count, slices.Count);
            foreach (var s in slices)
            {
                Assert.Equal(resolution, s.resolution);
                Assert.Equal(resolution, s.rect.Width);
                Assert.Equal(resolution, s.rect.Height);
                Assert.True(s.rect.X + s.rect.Width <= 1024 && s.rect.Y + s.rect.Height <= 1024, s.rect.ToString());
                Assert.Equal(s.rect, s.light.GetAtlasRectangle(s.slice_index));
            }
            // no two slices overlap, and neighbours keep the 8 texel border
            for (int i = 0; i < slices.Count; i++)
                for (int j = i + 1; j < slices.Count; j++)
                {
                    var a = slices[i].rect; var b = slices[j].rect;
                    bool apart = a.X + a.Width + 8 <= b.X || b.X + b.Width + 8 <= a.X || a.Y + a.Height + 8 <= b.Y || b.Y + b.Height + 8 <= a.Y;
                    Assert.True(apart, $"{a} {b}");
                }
            // one size up no longer packs
            Assert.False(Renderer.PackRowForTest(resolution + 1, (uint)count, 1024));
        }
    }

    [SkippableFact]
    public void The_shadow_atlas_receives_depth_from_the_cube()
    {
        Run(() =>
        {
            Scene scene = CreateShadowScene();
            Light point = AddLight(LightType.Point, new Vector3(0, 3, 0), Vector3.UnitZ);
            Frame(scene);
            Frame(scene);
            Assert.True(point.IsInViewFrustum(scene.cube.GetComponent<Render>()!, 3));
            byte[] atlas = RHI_Device.ReadbackTexture(Renderer.GetRenderTargetTexture(Renderer_RenderTarget.shadow_atlas)!, 0, 0);
            int nonzero = 0; float max = 0;
            for (int i = 0; i < atlas.Length; i += 4)
            {
                float d = BitConverter.ToSingle(atlas, i);
                if (d > 0) { nonzero++; max = System.MathF.Max(max, d); }
            }
            Assert.True(nonzero > 100, $"nonzero={nonzero} max={max} slices={Renderer.GetShadowSlices().Count}");
            foreach (var slice in Renderer.GetShadowSlices())
                Assert.True(slice.rect.IsDefined());
        });
    }

    // light_diffuse's red channel for the whole frame (rgba16f, one readback)
    private static float[] DiffuseRed()
    {
        byte[] b = Read(Renderer_RenderTarget.light_diffuse);
        var r = new float[Width * Height];
        for (int i = 0; i < r.Length; i++)
            r[i] = Half(b, i * 8);
        return r;
    }

    // the ground's shadow factor per pixel: light_diffuse with shadows over light_diffuse without (ground pixels only)
    private static float[] ShadowFactors(Scene scene, Light light)
    {
        Frame(scene); Frame(scene);
        byte[] normal = Read(Renderer_RenderTarget.gbuffer_normal);
        float[] shadowed = DiffuseRed();
        light.SetFlag(LightFlags.Shadows, false);
        Frame(scene); Frame(scene);
        float[] lit = DiffuseRed();
        light.SetFlag(LightFlags.Shadows, true);
        var factors = new float[Width * Height];
        for (int i = 0; i < factors.Length; i++)
            factors[i] = Half(normal, i * 8 + 2) > 0.99f && lit[i] > 1e-3f ? shadowed[i] / lit[i] : float.NaN;
        return factors;
    }

    [SkippableFact]
    public void The_sun_shadow_edge_is_filtered_not_stair_stepped()
    {
        Run(() =>
        {
            Scene scene = CreateShadowScene();
            scene.cube.SetScale(new Vector3(2, 2, 2));
            scene.cube.SetPosition(new Vector3(0, 0.5f, 0));
            scene.cube.SetRotation(Quaternion.CreateFromYawPitchRoll(0.4f, 0, 0)); // edges oblique to the texel grid
            Light sun = AddLight(LightType.Directional, new Vector3(0, 10, 0), new Vector3(2, -1, 0.3f));
            float[] f = ShadowFactors(scene, sun);

            int full = 0, partial = 0;
            foreach (float v in f)
            {
                if (float.IsNaN(v)) continue;
                if (v < 0.05f) full++;
                else if (v < 0.95f) partial++;
            }
            // the umbra is there, and its rim is a band of partial pixels: pinned 4c07baa's centre tap early out
            // left only the one texel bilinear rim (the stair steps), upstream's full footprint widens it
            Assert.True(full > 50, $"umbra {full}");
            Assert.True(partial > 0.5f * System.MathF.Sqrt(full) * 4, $"partial {partial} umbra {full}");

            // and along each row crossing the shadow, the transition from shadow to lit spans more than 2 pixels
            int rows = 0, wide = 0;
            for (uint y = 0; y < Height; y++)
            {
                int first_partial = -1, last_partial = -1; bool saw_full = false;
                for (uint x = 0; x < Width; x++)
                {
                    float v = f[Index(x, y)];
                    if (float.IsNaN(v)) continue;
                    if (v < 0.05f) saw_full = true;
                    else if (v < 0.95f && saw_full) { if (first_partial < 0) first_partial = (int)x; last_partial = (int)x; }
                    else if (v >= 0.95f && saw_full && first_partial >= 0) break;
                }
                if (!saw_full || first_partial < 0) continue;
                rows++;
                if (last_partial - first_partial + 1 > 2) wide++;
            }
            Assert.True(rows > 5, $"rows {rows}");
            Assert.True(wide > 0.75f * rows, $"wide transitions {wide} of {rows} rows");
        }, shadow_atlas: 4096);
    }
}
