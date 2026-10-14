using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

public class MaterialPackingTests
{
    private static RHI_Texture Solid(string name, uint w, uint h, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a; }
        return new RHI_Texture(name, w, h, RHI_Texture.GenerateMips(pixels, w, h));
    }

    [Fact]
    public void Prepare_for_gpu_packs_occlusion_roughness_metalness_and_height()
    {
        var material = new Material();
        material.SetResourceName("pack_test");
        material.SetProperty(MaterialProperty.Roughness, 0.25f);
        material.SetProperty(MaterialProperty.Metalness, 0.75f);
        material.SetTexture(MaterialTextureType.Occlusion, Solid("ao", 4, 4, 51, 0, 0), 0, false);
        material.SetTexture(MaterialTextureType.Roughness, Solid("rough", 2, 2, 128, 0, 0), 0, false); // resized to 4x4
        material.SetTexture(MaterialTextureType.Metalness, Solid("metal", 4, 4, 255, 0, 0), 0, false);
        // height falls back to 127

        material.PrepareForGpu();
        RHI_Texture? packed = material.GetTexture(MaterialTextureType.Packed);
        Assert.NotNull(packed);
        Assert.Equal(4u, packed!.GetWidth());
        Assert.Equal(4u, packed.GetHeight());
        Assert.True(packed.GetCpuMips().Count > 1); // the full mip chain
        byte[] mip0 = packed.GetCpuMips()[0];
        Assert.Equal(51, mip0[0]);  // occlusion r
        Assert.Equal(128, mip0[1]); // roughness (resized) r
        Assert.Equal(255, mip0[2]); // metalness r
        Assert.Equal(127, mip0[3]); // height default
    }

    [Fact]
    public void Gltf_packing_reads_roughness_g_and_metalness_b()
    {
        var material = new Material();
        material.SetResourceName("gltf");
        material.SetProperty(MaterialProperty.Gltf, 1.0f);
        material.SetTexture(MaterialTextureType.Occlusion, Solid("ao", 1, 1, 10, 0, 0), 0, false);
        material.SetTexture(MaterialTextureType.Roughness, Solid("orm", 1, 1, 0, 20, 0), 0, false);
        material.SetTexture(MaterialTextureType.Metalness, Solid("orm", 1, 1, 0, 0, 30), 0, false);
        material.PrepareForGpu();
        byte[] mip0 = material.GetTexture(MaterialTextureType.Packed)!.GetCpuMips()[0];
        Assert.Equal(new byte[] { 10, 20, 30, 127 }, mip0);
    }

    [Fact]
    public void An_alpha_mask_is_merged_into_the_colour_alpha_and_marks_it_transparent()
    {
        var material = new Material();
        material.SetResourceName("cutout");
        RHI_Texture color = Solid("albedo", 2, 2, 255, 0, 0, 255);
        RHI_Texture mask  = Solid("mask", 2, 2, 64, 0, 0, 255); // r = 64
        material.SetTexture(MaterialTextureType.Color, color, 0, false);
        Assert.False(material.IsAlphaTested()); // neither an alpha mask nor a transparent colour yet
        material.SetTexture(MaterialTextureType.AlphaMask, mask, 0, false);
        Assert.True(material.IsAlphaTested()); // Material.cpp:1685, HasTextureOfType(AlphaMask)

        material.PrepareForGpu();
        Assert.Equal(64, color.GetCpuMips()[0][3]);
        Assert.True(color.IsSemiTransparent());
        Assert.True(material.IsAlphaTested());
        Assert.Null(material.GetTexture(MaterialTextureType.Packed)); // no orm sources
    }

    [Fact]
    public void Colour_only_materials_skip_the_packed_texture()
    {
        var material = new Material();
        material.SetResourceName("colour");
        material.SetTexture(MaterialTextureType.Color, Solid("c", 1, 1, 1, 2, 3), 0, false);
        material.PrepareForGpu();
        Assert.Null(material.GetTexture(MaterialTextureType.Packed));
        Assert.False(material.IsAlphaTested());
    }

    [Fact]
    public void Defaults_match_spartan_and_generate_mips_halves_to_one()
    {
        var material = new Material();
        Assert.Equal(1.0f, material.GetProperty(MaterialProperty.ColorR));
        Assert.Equal(1.0f, material.GetProperty(MaterialProperty.Roughness));
        Assert.Equal(1.0f, material.GetProperty(MaterialProperty.TextureTilingX));
        Assert.Equal(1.52f, material.GetProperty(MaterialProperty.Ior));
        Assert.Equal((float)RHI_CullMode.Back, material.GetProperty(MaterialProperty.CullMode));

        List<byte[]> mips = RHI_Texture.GenerateMips(new byte[16 * 16 * 4], 16, 16);
        Assert.Equal(5, mips.Count); // 16, 8, 4, 2, 1
        Assert.Equal(4, mips[^1].Length);
    }
}
