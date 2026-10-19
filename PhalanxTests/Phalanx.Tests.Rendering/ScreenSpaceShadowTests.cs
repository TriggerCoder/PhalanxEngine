using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using RHI_Texture = Phalanx.Rendering.RHI.RHI_Texture;
using Xunit;

namespace Phalanx.Tests;

// screen space shadows on lavapipe (Pass_ScreenSpaceShadows, bend_sss.comp and the contact term of light.comp): the
// light table bits and slices, the contact shadow at the foot of a cube resting on the ground, no acne on open ground,
// the global switch, taau and a resize
public partial class RendererSceneTests
{
    // the shadow scene, the unit cube resting on the ground (its bottom face is the ground's top, y = -0.5), lit from
    // behind so its shadow falls towards the camera over the visible -z face
    private static (Scene scene, Light sun) CreateContactScene(Vector3 sun_forward)
    {
        Scene scene = CreateShadowScene();
        Light sun = AddLight(LightType.Directional, new Vector3(0, 10, 0), sun_forward);
        return (scene, sun);
    }

    private static float[] ReadSss(uint slice)
    {
        RHI_Device.QueueWaitAll();
        RHI_Texture sss = Renderer.GetRenderTargetTexture(Renderer_RenderTarget.sss)!;
        Assert.Equal(RHI_Format.R16_Float, sss.GetFormat());
        byte[] b = RHI_Device.ReadbackTexture(sss, slice, 0);
        var values = new float[b.Length / 2];
        for (int i = 0; i < values.Length; i++)
            values[i] = Half(b, i * 2);
        return values;
    }

    private static LightParameters ReadLight(uint index)
    {
        int size = Marshal.SizeOf<LightParameters>();
        byte[] table = RHI_Device.ReadbackBuffer(Renderer.GetBuffer(Renderer_Buffer.LightParameters)!, (ulong)(index * size), (ulong)size);
        return MemoryMarshal.Read<LightParameters>(table);
    }

    // ground pixels away from the cube in x: nothing towards the light can occlude them
    private static List<(uint x, uint y)> OpenGroundPixels(Scene scene)
    {
        var pixels = new List<(uint, uint)>();
        foreach (float x in new[] { -2.5f, -2.0f, -1.5f, 1.5f, 2.0f, 2.5f })
            foreach (float z in new[] { -2.0f, -1.0f, 0.0f, 1.0f })
                pixels.Add(Pixel(scene, new Vector3(x, -0.5f, z)));
        return pixels;
    }

    [SkippableFact]
    public void Only_directional_lights_get_a_contact_shadow_slice()
    {
        Run(() =>
        {
            (Scene scene, Light sun) = CreateContactScene(new Vector3(0, -1, -1.5f));
            Light spot  = AddLight(LightType.Spot, new Vector3(-2, 2, 0), new Vector3(3, -2.5f, 0));
            Light point = AddLight(LightType.Point, new Vector3(0, 1.5f, -1), Vector3.UnitZ);
            Assert.False(spot.GetFlag(LightFlags.ShadowsScreenSpace));  // Light.cpp:438-442
            Assert.False(point.GetFlag(LightFlags.ShadowsScreenSpace));
            Frame(scene);
            Frame(scene);

            Assert.Equal(3u, Renderer.GetLightCount());
            Assert.Equal(0u, sun.GetIndex()); // the slice no longer overwrites the table index
            Assert.Equal(new[] { sun }, Renderer.GetScreenSpaceShadowLights());

            LightParameters p_sun = ReadLight(sun.GetIndex());
            Assert.Equal(1u << 4, p_sun.flags & (1u << 4));
            Assert.Equal(0u, p_sun.screen_space_shadow_slice_index);
            foreach (Light local in new[] { spot, point })
            {
                LightParameters p = ReadLight(local.GetIndex());
                Assert.Equal(1u << 3, p.flags & (1u << 3)); // it has shadow maps
                Assert.Equal(0u, p.flags & (1u << 4));      // but no contact shadows
            }

            // the sun's own flag takes it out too
            sun.SetFlag(LightFlags.ShadowsScreenSpace, false);
            Frame(scene);
            Assert.Empty(Renderer.GetScreenSpaceShadowLights());
            Assert.Equal(0u, ReadLight(0).flags & (1u << 4));
        });
    }

    // the contact tests render at 720p: at the 128x96 of the other scene tests the cube's foot is a handful of pixels
    // and spartan's 0.005 surface thickness is thinner than one ray step through the -z face, so whether a ray hits it
    // flips from row to row (lit and shadowed stripes, they are gone from about 720p on)
    private const uint ContactWidth = 1280, ContactHeight = 720;

    private static int ContactIndex(uint x, uint y) => (int)(y * ContactWidth + x);

    // the ground straight in front of the -z face, from its foot to 0.75 m out (the shadow is 1.5 m long, the 64
    // fade out samples start past that)
    private static List<(uint x, uint y)> ContactShadowPixels(Scene scene)
    {
        var pixels = new List<(uint, uint)>();
        for (float z = -0.55f; z >= -1.25f; z -= 0.05f)
            foreach (float x in new[] { -0.3f, 0.0f, 0.3f })
                pixels.Add(Pixel(scene, new Vector3(x, -0.5f, z), ContactWidth, ContactHeight));
        return pixels;
    }

    private static List<(uint x, uint y)> ContactOpenGroundPixels(Scene scene)
    {
        var pixels = new List<(uint, uint)>();
        foreach (float x in new[] { -2.5f, -2.0f, -1.5f, 1.5f, 2.0f, 2.5f })
            foreach (float z in new[] { -2.0f, -1.0f, 0.0f, 1.0f })
                pixels.Add(Pixel(scene, new Vector3(x, -0.5f, z), ContactWidth, ContactHeight));
        return pixels;
    }

    private static void AssertContactShadow(Scene scene, float[] sss, string label)
    {
        // every ground pixel in front of the face is shadowed, no row slips through
        foreach ((uint x, uint y) in ContactShadowPixels(scene))
            Assert.True(sss[ContactIndex(x, y)] < 0.25f, $"{label}contact ({x},{y}) {sss[ContactIndex(x, y)]}");

        // open ground is lit, no self shadowing
        foreach ((uint x, uint y) in ContactOpenGroundPixels(scene))
            Assert.True(sss[ContactIndex(x, y)] > 0.95f, $"{label}open ground ({x},{y}) {sss[ContactIndex(x, y)]}");
    }

    [SkippableFact]
    public void A_cube_resting_on_the_ground_casts_a_contact_shadow_at_its_foot()
    {
        Run(() =>
        {
            (Scene scene, Light sun) = CreateContactScene(new Vector3(0, -1, -1.5f));
            Frame(scene);
            Frame(scene);
            AssertContactShadow(scene, ReadSss(0), "");
        }, width: ContactWidth, height: ContactHeight);
    }

    [SkippableFact]
    public void A_grazing_sun_leaves_open_ground_without_acne()
    {
        Run(() =>
        {
            // about 8 degrees above the horizon, the hardest case for a depth buffer trace over a flat floor
            (Scene scene, Light sun) = CreateContactScene(new Vector3(0, -0.14f, -1));
            Frame(scene);
            Frame(scene);
            float[] sss = ReadSss(0);
            foreach ((uint x, uint y) in OpenGroundPixels(scene))
                Assert.True(sss[Index(x, y)] > 0.95f, $"open ground ({x},{y}) {sss[Index(x, y)]}");

            // and from the side, across the camera
            sun.GetEntity()!.SetRotation(LookRotation(new Vector3(1, -0.14f, 0.2f)));
            Frame(scene);
            sss = ReadSss(0);
            foreach (float z in new[] { -2.0f, -1.5f, 1.0f })
                foreach (float x in new[] { -2.5f, -1.5f, 2.0f })
                {
                    (uint px, uint py) = Pixel(scene, new Vector3(x, -0.5f, z));
                    Assert.True(sss[Index(px, py)] > 0.95f, $"open ground ({x}, {z}) {sss[Index(px, py)]}");
                }
        });
    }

    [SkippableFact]
    public void Contact_shadows_only_ever_darken_the_direct_light()
    {
        Run(() =>
        {
            (Scene scene, Light sun) = CreateContactScene(new Vector3(0.3f, -1, -1.5f));
            Frame(scene);
            Frame(scene);
            byte[] on = Read(Renderer_RenderTarget.light_diffuse);
            float[] sss = ReadSss(0);
            Assert.Contains(sss, v => v < 0.5f);

            Renderer.ScreenSpaceShadows = false;
            try
            {
                Frame(scene);
                byte[] off = Read(Renderer_RenderTarget.light_diffuse);
                Assert.Empty(Renderer.GetScreenSpaceShadowLights());
                Assert.Equal(0u, ReadLight(0).flags & (1u << 4));
                Assert.All(ReadSss(0), v => Assert.Equal(1.0f, v)); // cleared to lit once nothing writes it

                int darker = 0;
                for (uint y = 0; y < Height; y++)
                    for (uint x = 0; x < Width; x++)
                    {
                        float d_on = Half(on, Index(x, y) * 8), d_off = Half(off, Index(x, y) * 8);
                        Assert.True(d_on <= d_off * 1.002f + 1e-4f, $"({x},{y}) on {d_on} off {d_off}");
                        if (d_on < d_off * 0.9f)
                            darker++;
                    }
                // the shadow map already covers most of the contact, but not all of it (its normal offset lifts the
                // receiver, the 1024 atlas is coarse at the foot of the cube)
                Assert.True(darker > 0, "contact shadows changed nothing");
            }
            finally
            {
                Renderer.ScreenSpaceShadows = true;
            }

            // back on, the light gets its slice again
            Frame(scene);
            Assert.Equal(1u << 4, ReadLight(0).flags & (1u << 4));
            Assert.Contains(ReadSss(0), v => v < 0.5f);
        });
    }

    [SkippableFact]
    public void Contact_shadows_hold_still_under_the_taau_jitter()
    {
        Run(() =>
        {
            // the trace uses the unjittered view projection for the light but the jittered depth, so the shadow may
            // move by the jitter (under a pixel) but never break up
            (Scene scene, Light sun) = CreateContactScene(new Vector3(0, -1, -1.5f));
            for (int frame = 0; frame < 6; frame++)
            {
                Frame(scene);
                AssertContactShadow(scene, ReadSss(0), $"frame {frame}: ");
            }
            Assert.False(Renderer.IsTaauHistoryResetPending());
        }, aa: Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau, width: ContactWidth, height: ContactHeight);
    }

    [SkippableFact]
    public void The_contact_shadow_target_follows_the_render_resolution()
    {
        Run(() =>
        {
            (Scene scene, Light sun) = CreateContactScene(new Vector3(0, -1, -1.5f));
            Frame(scene);
            RHI_Texture sss = Renderer.GetRenderTargetTexture(Renderer_RenderTarget.sss)!;
            Assert.Equal((Width, Height, Renderer.renderer_sss_slice_count), (sss.GetWidth(), sss.GetHeight(), sss.GetDepth()));
            Assert.Equal(RHI_Texture_Type.Type2DArray, sss.Type);

            Renderer.SetResolutionRender(96, 72); // spartan renders nothing below 64 pixels
            Assert.True(Renderer.IsTaauHistoryResetPending()); // new targets, no history
            Frame(scene);
            sss = Renderer.GetRenderTargetTexture(Renderer_RenderTarget.sss)!;
            Assert.Equal((96u, 72u), (sss.GetWidth(), sss.GetHeight()));

            // the unused slices were cleared to lit with the new target
            byte[] slice1 = RHI_Device.ReadbackTexture(sss, 1, 0);
            for (int i = 0; i < slice1.Length; i += 2)
                Assert.Equal(1.0f, Half(slice1, i));
            Renderer.SetResolutionRender(Width, Height);
            Frame(scene);
        });
    }
}
