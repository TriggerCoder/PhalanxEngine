using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;
using RHI_Texture = Phalanx.Rendering.RHI.RHI_Texture;

namespace Phalanx.Tests;

// the precision of the taau history: a flat floor lit by a bright point light, the scene of --renderer-cube
// --point-light without the cube. A half float history freezes each pixel within a few percent of the jittered mean
// and draws contour lines along the shading (Renderer_Resources.cs, the taau_history deviation). A grazing view of the
// same floor with the cube turning: r11g11b10 light buffers make taau leave stepped bands behind the moving shadows
// (Renderer_Resources.cs, the light buffer deviation).
public partial class RendererSceneTests
{
    private const uint FloorWidth = 480, FloorHeight = 270;

    private static Entity? CreateLitFloor(bool grazing_with_cube = false)
    {
        Entity camera_entity = World.World.CreateEntity();
        Camera camera = camera_entity.AddComponent<Camera>()!;
        camera.SetAperture(16.0f);
        camera.SetShutterSpeed(1.0f / 125.0f);
        camera.SetIso(100.0f);
        camera_entity.SetPosition(new Vector3(0, grazing_with_cube ? -0.3f : 0.8f, -3));
        camera_entity.SetRotation(Quaternion.CreateFromYawPitchRoll(0.0f, grazing_with_cube ? 0.12f : 0.3f, 0.0f));

        Entity sun_entity = World.World.CreateEntity();
        sun_entity.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
        Light sun = sun_entity.AddComponent<Light>()!;
        sun.SetLightType(LightType.Directional);
        sun.SetColor(new Phalanx.Math.Color(1.0f, 1.0f, 1.0f, 1.0f));

        Entity point_entity = World.World.CreateEntity();
        point_entity.SetPosition(new Vector3(0.8f, 0.6f, -1.5f));
        Light point = point_entity.AddComponent<Light>()!;
        point.SetLightType(LightType.Point);
        point.SetColor(new Phalanx.Math.Color(1.0f, 0.85f, 0.6f, 1.0f));
        point.SetIntensity(2000000.0f);

        var ground_material = new Material();
        ground_material.SetProperty(MaterialProperty.ColorR, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorG, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorB, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorA, 1.0f);
        ground_material.SetProperty(MaterialProperty.Roughness, 0.9f);
        Entity ground = World.World.CreateEntity();
        ground.SetPosition(new Vector3(0, -1.0f, 0));
        ground.SetScale(new Vector3(20.0f, 1.0f, 20.0f));
        Render ground_render = ground.AddComponent<Render>()!;
        ground_render.SetMesh(MeshType.Quad);
        ground_render.SetMaterial(ground_material);

        Entity? cube = null;
        if (grazing_with_cube)
        {
            var material = new Material();
            material.SetProperty(MaterialProperty.ColorR, 0.9f);
            material.SetProperty(MaterialProperty.ColorG, 0.55f);
            material.SetProperty(MaterialProperty.ColorB, 0.2f);
            material.SetProperty(MaterialProperty.ColorA, 1.0f);
            cube = World.World.CreateEntity();
            Render render = cube.AddComponent<Render>()!;
            render.SetMesh(MeshType.Cube);
            render.SetMaterial(material);
        }

        Renderer.SkyFallbackNits     = new Vector3(4000.0f, 6000.0f, 10000.0f);
        Renderer.AmbientFallbackNits = new Vector3(2000.0f, 3000.0f, 5000.0f);
        World.World.Tick();
        return cube;
    }

    // the mean of r, g and b per pixel: frame_render after an unjittered frame (aa off), or the taau history
    private static float[] LitFloorLuminance(Renderer_AntiAliasing_Upsampling aa, int frames, bool half_history)
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        var luminance = new float[FloorWidth * FloorHeight];
        Renderer.AntiAliasingUpsampling = aa;
        Renderer.TaauHistoryHalfPrecision = half_history;
        World.World.SetMainThread();
        World.World.ClearScene();
        Renderer.ShadowAtlasResolution = 2048;
        Renderer.Initialize(FloorWidth, FloorHeight, validation: true);
        try
        {
            Renderer.SetResolutionRender(FloorWidth, FloorHeight);
            CreateLitFloor();
            for (int i = 0; i < frames; i++)
            {
                World.World.Tick();
                Assert.True(Renderer.Tick());
            }
            bool taau = aa == Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau;
            RHI_Texture texture = Renderer.GetRenderTargetTexture(taau ? Renderer_RenderTarget.taau_history : Renderer_RenderTarget.frame_render)!;
            RHI_Device.QueueWaitAll();
            byte[] bytes = RHI_Device.ReadbackTexture(texture, 0, 0);
            bool single = texture.GetFormat() == RHI_Format.R32G32B32A32_Float;
            float Channel(int pixel, int c) => single ? BitConverter.ToSingle(bytes, pixel * 16 + c * 4) : Half(bytes, pixel * 8 + c * 2);
            for (int i = 0; i < luminance.Length; i++)
                luminance[i] = (Channel(i, 0) + Channel(i, 1) + Channel(i, 2)) / 3.0f;
            AssertClean();
        }
        finally
        {
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
            Renderer.TaauHistoryHalfPrecision = false;
            Renderer.SkyFallbackNits     = new Vector3(1500.0f, 2200.0f, 3500.0f);
            Renderer.AmbientFallbackNits = new Vector3(150.0f, 220.0f, 350.0f);
            Renderer.ShadowAtlasResolution = 8192;
            Renderer.Shutdown();
            World.World.ClearScene();
        }
        return luminance;
    }

    // the share of smooth floor pixels (the lower 45% of the frame, where the unjittered frame changes by under 2% per
    // pixel) on which the settled history is more than 1.5% away from the unjittered frame, and the mean deviation
    private static (float share, float mean) HistoryDeviation(float[] reference, float[] history)
    {
        int count = 0, off = 0;
        double sum = 0.0;
        for (uint y = (uint)(FloorHeight * 0.55f); y < FloorHeight - 2; y++)
        {
            for (uint x = 2; x < FloorWidth - 2; x++)
            {
                int i = (int)(y * FloorWidth + x);
                float r = reference[i];
                if (!(r > 0.0f))
                    continue;
                float gx = 0.5f * (reference[i + 1] - reference[i - 1]);
                float gy = 0.5f * (reference[i + (int)FloorWidth] - reference[i - (int)FloorWidth]);
                if (MathF.Sqrt(gx * gx + gy * gy) / r >= 0.02f)
                    continue;
                float d = MathF.Abs(history[i] - r) / r;
                sum += d;
                count++;
                if (d > 0.015f)
                    off++;
            }
        }
        Assert.True(count > 10000, $"only {count} floor pixels");
        return (off / (float)count, (float)(sum / count));
    }

    [SkippableFact]
    public void Taau_history_is_single_precision()
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        Run(() =>
        {
            Assert.Equal(RHI_Format.R32G32B32A32_Float, Renderer.GetRenderTargetTexture(Renderer_RenderTarget.taau_history)!.GetFormat());
            Assert.Equal(RHI_Format.R32G32B32A32_Float, Renderer.GetRenderTargetTexture(Renderer_RenderTarget.frame_output_2)!.GetFormat());
        }, Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau);
    }

    [SkippableFact]
    public void Taau_history_converges_on_a_smoothly_lit_floor_without_contour_lines()
    {
        float[] reference = LitFloorLuminance(Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear, 2, half_history: false);
        float[] history   = LitFloorLuminance(Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau, 64, half_history: false);
        float[] half      = LitFloorLuminance(Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau, 64, half_history: true);
        (float share, float mean)           = HistoryDeviation(reference, history);
        (float share_half, float mean_half) = HistoryDeviation(reference, half);
        // lavapipe: rgba32f 1.3% of the pixels, mean 0.10%; rgba16f (spartan's format) 13.8%, mean 0.81%
        Assert.True(share < 0.04f && mean < 0.003f, $"float history: {share:P2} of the floor more than 1.5% off, mean {mean:P3}");
        Assert.True(share_half > share, $"the half float history should show the contours: {share_half:P2} (mean {mean_half:P3}) vs {share:P2}");
    }

    private const uint GrazingRenderWidth = 960, GrazingRenderHeight = 540, GrazingOutputWidth = 800, GrazingOutputHeight = 430;

    // frame_output (display encoded) as 8 bit codes, the mean of r, g and b, after the cube of the grazing scene has
    // turned for 96 frames at 60 fps (the demo's motion), rendering above the output size like the editor
    private static float[] GrazingDisplay(Renderer_AntiAliasing_Upsampling aa, bool packed_light)
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        var codes = new float[GrazingOutputWidth * GrazingOutputHeight];
        Renderer.AntiAliasingUpsampling = aa;
        Renderer.LightBuffersPacked = packed_light;
        World.World.SetMainThread();
        World.World.ClearScene();
        Renderer.ShadowAtlasResolution = 2048;
        Renderer.Initialize(GrazingOutputWidth, GrazingOutputHeight, validation: true);
        try
        {
            Renderer.SetResolutionRender(GrazingRenderWidth, GrazingRenderHeight);
            Entity cube = CreateLitFloor(grazing_with_cube: true)!;
            for (int i = 0; i < 96; i++)
            {
                float time = i / 60.0f;
                cube.SetRotation(Quaternion.CreateFromYawPitchRoll(0.6f + time * 0.8f, 0.45f + time * 0.3f, 0.0f));
                World.World.Tick();
                Assert.True(Renderer.Tick());
            }
            RHI_Device.QueueWaitAll();
            byte[] bytes = RHI_Device.ReadbackTexture(Renderer.GetRenderTargetTexture(Renderer_RenderTarget.frame_output)!, 0, 0);
            for (int i = 0; i < codes.Length; i++)
            {
                float sum = 0.0f;
                for (int c = 0; c < 3; c++)
                    sum += MathF.Round(System.Math.Clamp(Half(bytes, i * 8 + c * 2), 0.0f, 1.0f) * 255.0f);
                codes[i] = sum / 3.0f;
            }
            AssertClean();
        }
        finally
        {
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
            Renderer.LightBuffersPacked = false;
            Renderer.SkyFallbackNits     = new Vector3(1500.0f, 2200.0f, 3500.0f);
            Renderer.AmbientFallbackNits = new Vector3(150.0f, 220.0f, 350.0f);
            Renderer.ShadowAtlasResolution = 8192;
            Renderer.Shutdown();
            World.World.ClearScene();
        }
        return codes;
    }

    // the 5x5 box mean of an image, edges clamped
    private static float[] Box5(float[] image, int width, int height)
    {
        var result = new float[image.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float sum = 0.0f;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        sum += image[System.Math.Clamp(y + dy, 0, height - 1) * width + System.Math.Clamp(x + dx, 0, width - 1)];
                result[y * width + x] = sum / 25.0f;
            }
        return result;
    }

    // the smooth floor of the reference: below the horizon band (the lower 55%), where the 5x5 mean changes by under 1%
    // per pixel, eroded by 4 pixels so no edge, silhouette or shadow border is counted
    private static bool[] SmoothFloor(float[] reference, int width, int height)
    {
        float[] mean = Box5(reference, width, height);
        var smooth = new bool[reference.Length];
        for (int y = (int)(height * 0.45f); y < height - 4; y++)
            for (int x = 4; x < width - 4; x++)
            {
                int i = y * width + x;
                float gx = 0.5f * (mean[i + 1] - mean[i - 1]), gy = 0.5f * (mean[i + width] - mean[i - width]);
                smooth[i] = mean[i] > 0.0f && MathF.Sqrt(gx * gx + gy * gy) / mean[i] < 0.01f;
            }
        var eroded = new bool[smooth.Length];
        for (int y = 4; y < height - 4; y++)
            for (int x = 4; x < width - 4; x++)
            {
                bool all = true;
                for (int dy = -4; dy <= 4 && all; dy++)
                    for (int dx = -4; dx <= 4 && all; dx++)
                        all = smooth[(y + dy) * width + x + dx];
                eroded[y * width + x] = all;
            }
        return eroded;
    }

    // the share of the masked pixels that stand out of their 5x5 mean by 0.6 of a code or more: contour lines and bands
    private static float LineShare(float[] codes, bool[] mask, int width, int height)
    {
        float[] mean = Box5(codes, width, height);
        int count = 0, lines = 0;
        for (int i = 0; i < codes.Length; i++)
        {
            if (!mask[i])
                continue;
            count++;
            if (MathF.Abs(codes[i] - mean[i]) >= 0.6f)
                lines++;
        }
        Assert.True(count > 50000, $"only {count} floor pixels");
        return lines / (float)count;
    }

    [SkippableFact]
    public void Light_buffers_are_half_float()
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        Run(() =>
        {
            Assert.Equal(RHI_Format.R16G16B16A16_Float, Renderer.GetRenderTargetTexture(Renderer_RenderTarget.light_diffuse)!.GetFormat());
            Assert.Equal(RHI_Format.R16G16B16A16_Float, Renderer.GetRenderTargetTexture(Renderer_RenderTarget.light_specular)!.GetFormat());
        });
    }

    [SkippableFact]
    public void Taau_leaves_no_bands_behind_moving_shadows_at_a_grazing_view()
    {
        int w = (int)GrazingOutputWidth, h = (int)GrazingOutputHeight;
        float[] off    = GrazingDisplay(Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear, packed_light: false);
        float[] taau   = GrazingDisplay(Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau, packed_light: false);
        float[] packed = GrazingDisplay(Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau, packed_light: true);
        bool[] floor = SmoothFloor(off, w, h);
        float share_off    = LineShare(off, floor, w, h);
        float share_taau   = LineShare(taau, floor, w, h);
        float share_packed = LineShare(packed, floor, w, h);
        // lavapipe: off 0.4%, taau 0.7%; with r11g11b10 light buffers (spartan's, and 978e62e) taau 2.6%
        Assert.True(share_taau < share_off + 0.01f, $"taau {share_taau:P2} of the floor in lines, aa off {share_off:P2}, r11g11b10 {share_packed:P2}");
        Assert.True(share_packed > share_taau, $"r11g11b10 light buffers should show the bands: {share_packed:P2} vs {share_taau:P2}");
    }
}
