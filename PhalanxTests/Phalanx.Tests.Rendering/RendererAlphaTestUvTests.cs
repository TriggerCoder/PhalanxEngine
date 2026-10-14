using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// lavapipe, validation on: the alpha tested halves of the depth prepass and the g-buffer
// (Renderer_Passes_Geometry.cpp:1553-1570, :1764), and the per-draw uv transform (Renderer.cpp:451,
// common_vertex_processing.hlsl:1006-1030, g_buffer.hlsl:505-525), read back from the g-buffer targets
[Collection("gpu")]
public class RendererAlphaTestUvTests
{
    private const uint Width = 64, Height = 64;

    private static void Run(Action body)
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
        World.World.SetMainThread();
        World.World.ClearScene();
        Renderer.ShadowAtlasResolution = 1024;
        Renderer.Initialize(Width, Height, validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal) || m.Contains("WarningBit", StringComparison.Ordinal)),
                        string.Join("\n", RHI_Device.ValidationMessages));
        }
        finally
        {
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
            Renderer.ShadowAtlasResolution = 8192;
            Renderer.Shutdown();
            World.World.ClearScene();
        }
    }

    // the standard cube 3 m in front of the camera, its front face fills the centre
    private static Material CreateScene()
    {
        Entity camera_entity = World.World.CreateEntity();
        camera_entity.AddComponent<Camera>();
        camera_entity.SetPosition(new Vector3(0, 0, -3));

        var material = new Material();
        material.SetProperty(MaterialProperty.ColorR, 1.0f);
        material.SetProperty(MaterialProperty.ColorG, 1.0f);
        material.SetProperty(MaterialProperty.ColorB, 1.0f);
        material.SetProperty(MaterialProperty.ColorA, 1.0f);
        Entity cube = World.World.CreateEntity();
        Render render = cube.AddComponent<Render>()!;
        render.SetMesh(MeshType.Cube);
        render.SetMaterial(material);
        World.World.Tick();
        return material;
    }

    private static void Frame()
    {
        World.World.Tick();
        Assert.True(Renderer.Tick());
    }

    private static byte[] Centre(Renderer_RenderTarget target, int bytes_per_pixel)
    {
        RHI_Device.QueueWaitAll();
        byte[] b = RHI_Device.ReadbackTexture(Renderer.GetRenderTargetTexture(target)!, 0, 0);
        int i = (int)((Height / 2) * Width + Width / 2) * bytes_per_pixel;
        return b[i..(i + bytes_per_pixel)];
    }

    private static float Half(byte[] b, int offset) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, offset));

    private static byte[] Solid(uint size, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a; }
        return pixels;
    }

    private static byte SrgbToLinearUnorm(byte value)
    {
        double c = value / 255.0;
        double l = c < 0.0404482362771082 ? c / 12.92 : System.Math.Pow((c + 0.055) / 1.055, 2.4);
        return (byte)System.Math.Round(l * 255.0);
    }

    // 4x4: the left two columns red, the right two green (alpha 255)
    private static byte[] RedGreen()
    {
        var p = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int i = (y * 4 + x) * 4;
                p[i] = (byte)(x < 2 ? 255 : 0); p[i + 1] = (byte)(x < 2 ? 0 : 255); p[i + 3] = 255;
            }
        return p;
    }

    private static void SetUv(Material m, float tile_x, float tile_y, float off_x, float off_y)
    {
        m.SetProperty(MaterialProperty.TextureTilingX, tile_x);
        m.SetProperty(MaterialProperty.TextureTilingY, tile_y);
        m.SetProperty(MaterialProperty.TextureOffsetX, off_x);
        m.SetProperty(MaterialProperty.TextureOffsetY, off_y);
    }

    private static void AssertRedOrGreen(bool red)
    {
        byte[] c = Centre(Renderer_RenderTarget.gbuffer_color, 4);
        Assert.True(red ? c[0] > 250 && c[1] < 5 : c[0] < 5 && c[1] > 250, $"{c[0]},{c[1]},{c[2]},{c[3]}");
    }

    [SkippableFact]
    public void Tiling_offset_invert_and_rotation_move_the_sampled_texel()
    {
        Run(() =>
        {
            Material material = CreateScene();
            material.SetTexture(MaterialTextureType.Color, Renderer.CreateTexture("redgreen", 4, 4, RedGreen()));

            // the centre of the face samples uv 0.5, a column boundary; a tiny tiling makes the whole face sample about the offset (tiling 0 has zero
            // derivatives, which lavapipe's anisotropic filter turns into black)
            SetUv(material, 0.001f, 0.001f, 0.125f, 0.5f);
            Frame();
            AssertRedOrGreen(red: true);

            SetUv(material, 0.001f, 0.001f, 0.875f, 0.5f);
            Frame();
            AssertRedOrGreen(red: false);

            // mirrored along x: 2 * floor(0.125) + 1 - 0.125 = 0.875
            SetUv(material, 0.001f, 0.001f, 0.125f, 0.5f);
            material.SetProperty(MaterialProperty.TextureInvertX, 1.0f);
            Frame();
            AssertRedOrGreen(red: false);

            // rotated 180 degrees around the uv centre on top of the mirror: back to 0.125
            material.SetProperty(MaterialProperty.TextureRotation, 2.0f);
            Frame();
            AssertRedOrGreen(red: true);

            // tiling on the mesh uv: uv 0.5 * 1 + 0.25 = 0.75 at the centre
            material.SetProperty(MaterialProperty.TextureInvertX, 0.0f);
            material.SetProperty(MaterialProperty.TextureRotation, 0.0f);
            SetUv(material, 1.0f, 1.0f, 0.25f, 0.0f);
            Frame();
            AssertRedOrGreen(red: false);
        });
    }

    [SkippableFact]
    public void World_space_uvs_project_the_texture_from_the_world_position()
    {
        Run(() =>
        {
            Material material = CreateScene();
            material.SetTexture(MaterialTextureType.Color, Renderer.CreateTexture("redgreen", 4, 4, RedGreen()));
            SetUv(material, 1.0f, 1.0f, 0.25f, 0.25f);
            Frame();
            AssertRedOrGreen(red: false); // mesh uv 0.75

            // the front face (normal -z) at world (0, 0, -0.5): u = dot(p, (-1, 0, 0)) + 0.25 = 0.25
            material.SetProperty(MaterialProperty.WorldSpaceUv, 1.0f);
            Frame();
            AssertRedOrGreen(red: true);
        });
    }

    [SkippableFact]
    public void Alpha_tested_materials_cut_holes_into_the_depth_and_the_g_buffer()
    {
        Run(() =>
        {
            Material material = CreateScene();
            // 8x8 white, stripes along u: alpha 0 for even pairs of columns, 255 for odd ones
            byte[] Stripes(bool cut)
            {
                var pixels = new byte[8 * 8 * 4];
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int i = (y * 8 + x) * 4;
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                        pixels[i + 3] = (byte)(cut && (x / 2) % 2 == 0 ? 0 : 255);
                    }
                return pixels;
            }
            (byte[] colour, byte[] velocity) Read()
            {
                RHI_Device.QueueWaitAll();
                return (RHI_Device.ReadbackTexture(Renderer.GetRenderTargetTexture(Renderer_RenderTarget.gbuffer_color)!, 0, 0),
                        RHI_Device.ReadbackTexture(Renderer.GetRenderTargetTexture(Renderer_RenderTarget.gbuffer_velocity)!, 0, 0));
            }
            int row = (int)(Height / 2 * Width);

            // opaque: not alpha tested, the face's pixels along the centre row, velocity.w positive
            material.SetTexture(MaterialTextureType.Color, Renderer.CreateTexture("solid", 8, 8, Stripes(false)));
            Assert.False(material.IsAlphaTested());
            Frame();
            Frame(); // a previous view projection for velocity.w
            var (colour, velocity) = Read();
            var face = new List<int>();
            for (int x = 0; x < Width; x++)
                if (colour[(row + x) * 4 + 3] == 255)
                {
                    face.Add(row + x);
                    Assert.True(Half(velocity, (row + x) * 8 + 6) > 0.0f);
                }
            Assert.True(face.Count >= 8, $"face {face.Count}");

            // cutout: holes inside the face keep the clear, the rest shades and negates velocity.w (g_buffer.hlsl:1004)
            material.SetTexture(MaterialTextureType.Color, Renderer.CreateTexture("cutout", 8, 8, Stripes(true)));
            Assert.True(material.IsAlphaTested());
            Frame();
            (colour, velocity) = Read();
            int holes = 0, covered = 0;
            foreach (int pixel in face)
            {
                if (colour[pixel * 4 + 3] == 0)
                {
                    holes++;
                    Assert.Equal(0, colour[pixel * 4]);
                }
                else
                {
                    covered++;
                    Assert.Equal(255, colour[pixel * 4 + 3]);
                    Assert.True(Half(velocity, pixel * 8 + 6) < 0.0f);
                }
            }
            Assert.True(holes >= 2 && covered >= 2, $"holes {holes} covered {covered}");
        });
    }
}
