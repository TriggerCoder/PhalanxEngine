using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// lavapipe, validation on: the bindless material textures through the g-buffer (Renderer.cpp:2973 UpdateMaterials,
// g_buffer.hlsl:634-750), read back from the g-buffer targets
[Collection("gpu")]
public class RendererMaterialTextureTests
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
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau;
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

    [SkippableFact]
    public void An_albedo_texture_multiplies_the_colour_and_decodes_as_srgb()
    {
        Run(() =>
        {
            Material material = CreateScene();
            material.SetProperty(MaterialProperty.ColorG, 0.5f);
            material.SetTexture(MaterialTextureType.Color, Renderer.CreateTexture("albedo", 4, 4, Solid(4, 188, 188, 64)));
            Frame();
            byte[] albedo = Centre(Renderer_RenderTarget.gbuffer_color, 4);
            byte l188 = SrgbToLinearUnorm(188), l64 = SrgbToLinearUnorm(64);
            Assert.InRange(albedo[0], l188 - 1, l188 + 1);
            Assert.InRange(albedo[1], l188 / 2 - 1, l188 / 2 + 1); // times the 0.5 colour
            Assert.InRange(albedo[2], l64 - 1, l64 + 1);
            Assert.Equal(255, albedo[3]); // the opaque pass forces alpha to 1

            // greyscale textures aren't srgb decoded (should_decode_as_srgb)
            material.SetTexture(MaterialTextureType.Color, Renderer.CreateTexture("grey", 4, 4, Solid(4, 188, 188, 188), greyscale: true));
            Frame();
            albedo = Centre(Renderer_RenderTarget.gbuffer_color, 4);
            Assert.InRange(albedo[0], 187, 189);

            // the material table carries the albedo slot index and the flags (albedo, srgb off for greyscale)
            byte[] table = RHI_Device.ReadbackBuffer(Renderer.GetBuffer(Renderer_Buffer.MaterialParameters)!, 0, (ulong)Marshal.SizeOf<MaterialParameters>());
            MaterialParameters p = MemoryMarshal.Read<MaterialParameters>(table);
            Assert.True(p.HasTextureAlbedo());
            Assert.Equal(0u, p.flags & (1u << 17));
            Assert.Equal(0u, p.texture_indices[0]);
        });
    }

    [SkippableFact]
    public void Without_textures_the_colour_is_constant_and_the_standard_material_shows_the_checkerboard()
    {
        Run(() =>
        {
            Material material = CreateScene();
            material.SetProperty(MaterialProperty.ColorR, 0.0f);
            Frame();
            Assert.Equal(new byte[] { 0, 255, 255, 255 }, Centre(Renderer_RenderTarget.gbuffer_color, 4));

            // the standard material's checkerboard: two srgb decoded greys in 32 texel cells across the face's uvs
            World.World.GetEntitiesRenderables()[0].GetComponent<Render>()!.SetDefaultMaterial();
            Frame();
            RHI_Device.QueueWaitAll();
            byte[] all = RHI_Device.ReadbackTexture(Renderer.GetRenderTargetTexture(Renderer_RenderTarget.gbuffer_color)!, 0, 0);
            var greys = new HashSet<byte>();
            for (uint x = Width / 2 - 4; x < Width / 2 + 4; x++)
            {
                int i = (int)((Height / 2) * Width + x) * 4;
                Assert.Equal(all[i], all[i + 1]);
                greys.Add(all[i]);
            }
            // the centre texels are the two greys, filtered in between at cell edges
            Assert.True(greys.Contains(SrgbToLinearUnorm(64)) || greys.Contains(SrgbToLinearUnorm(101)), string.Join(",", greys));
            Assert.All(greys, g => Assert.InRange(g, SrgbToLinearUnorm(64), SrgbToLinearUnorm(101)));
        });
    }

    [SkippableFact]
    public void A_normal_map_bends_the_normal_along_the_tangent_frame()
    {
        Run(() =>
        {
            Material material = CreateScene();
            material.SetProperty(MaterialProperty.Normal, 1.0f);

            // flat map: the front face normal (-z) is unchanged
            material.SetTexture(MaterialTextureType.Normal, Renderer.CreateTexture("flat", 4, 4, Solid(4, 128, 128, 255)));
            Frame();
            byte[] n = Centre(Renderer_RenderTarget.gbuffer_normal, 8);
            Assert.True(Vector3.Distance(new Vector3(0, 0, -1), new Vector3(Half(n, 0), Half(n, 2), Half(n, 4))) < 1e-2f);

            // a map tilted 45 degrees towards +tangent: the normal leans half way to the face's tangent
            material.SetTexture(MaterialTextureType.Normal, Renderer.CreateTexture("tilted", 4, 4, Solid(4, 218, 128, 218)));
            Frame();
            n = Centre(Renderer_RenderTarget.gbuffer_normal, 8);
            var world = new Vector3(Half(n, 0), Half(n, 2), Half(n, 4));
            Assert.Equal(1.0f, world.Length(), 2);
            Assert.InRange(world.Z, -0.75f, -0.65f); // cos 45
            Assert.InRange(MathF.Abs(world.X) + MathF.Abs(world.Y), 0.65f, 0.75f); // the tangent lies in the face

            // the intensity scales the bend (saturate(max(0.01, normal)))
            material.SetProperty(MaterialProperty.Normal, 0.0f);
            Frame();
            n = Centre(Renderer_RenderTarget.gbuffer_normal, 8);
            Assert.True(Half(n, 4) < -0.99f);
        });
    }

    [SkippableFact]
    public void Material_packing_feeds_occlusion_roughness_and_metalness_through_the_packed_texture()
    {
        Run(() =>
        {
            Material material = CreateScene();
            material.SetProperty(MaterialProperty.Roughness, 1.0f);
            material.SetProperty(MaterialProperty.Metalness, 1.0f);

            // a packed texture set by hand isn't sampled, the flags come from the source textures (Renderer.cpp:3069-3073)
            material.SetTexture(MaterialTextureType.Packed, Renderer.CreateTexture("packed", 4, 4, Solid(4, 51, 128, 0), greyscale: true), 0, false);
            Frame();
            Assert.Equal(new byte[] { 255, 255, 0, 255 }, Centre(Renderer_RenderTarget.gbuffer_material, 4));

            // the sources: the material is prepared (Render.SetMaterial), so each SetTexture repacks (Material.cpp:1310)
            material.SetTexture(MaterialTextureType.Occlusion, Renderer.CreateTexture("ao", 4, 4, Solid(4, 51, 51, 51), greyscale: true), 0, false);
            material.SetTexture(MaterialTextureType.Roughness, Renderer.CreateTexture("rough", 8, 8, Solid(8, 128, 128, 128), greyscale: true), 0, false);
            material.SetTexture(MaterialTextureType.Metalness, Renderer.CreateTexture("metal", 4, 4, Solid(4, 255, 255, 255), greyscale: true), 0, false);
            Assert.Equal(8u, material.GetTexture(MaterialTextureType.Packed)!.GetWidth()); // the largest source
            Frame();
            byte[] m = Centre(Renderer_RenderTarget.gbuffer_material, 4);
            Assert.InRange(m[0], 127, 129); // roughness 1 * packed.g
            Assert.Equal(255, m[1]);        // metalness 1 * packed.b
            Assert.InRange(m[3], 50, 52);   // occlusion packed.r

            // a new source repacks and the renderer uploads the new packed texture
            material.SetTexture(MaterialTextureType.Roughness, Renderer.CreateTexture("rough2", 8, 8, Solid(8, 64, 64, 64), greyscale: true), 0, false);
            Frame();
            Assert.InRange(Centre(Renderer_RenderTarget.gbuffer_material, 4)[0], 63, 65);
        });
    }

    [SkippableFact]
    public void An_emission_texture_reaches_the_emissive_target()
    {
        Run(() =>
        {
            Material material = CreateScene();
            material.SetTexture(MaterialTextureType.Emission, Renderer.CreateTexture("emission", 4, 4, Solid(4, 255, 0, 0)));
            Frame();
            uint packed = BitConverter.ToUInt32(Centre(Renderer_RenderTarget.gbuffer_emissive, 4), 0);
            // r11g11b10 float: red is the low 11 bits, 5 exponent and 6 mantissa bits
            float Unpack11(uint bits) => (bits >> 6) == 0 ? 0 : (1 + (bits & 63) / 64.0f) * MathF.Pow(2, (int)(bits >> 6) - 15);
            Assert.Equal(1.0f, Unpack11(packed & 0x7FF), 2);
            Assert.Equal(0.0f, Unpack11((packed >> 11) & 0x7FF), 2);
        });
    }
}
