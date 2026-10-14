// Port of the texture_processing namespace of SpartanEngine source/rendering/Material.cpp @ 4c07baa: pack_textures
// (:416), pack_occlusion_roughness_metalness_height (:198), merge_alpha_mask_into_color_alpha (:224), resize_texture
// (:357) and get_texture_data_or_default (:408), on the cpu rgba8 data of Phalanx.World.RHI_Texture.
// Deviations:
// - normal_from_albedo (:238) is not ported yet, the NormalFromAlbedo property is ignored.
// - no compression (spartan marks colour BC7/BC3, normal BC5, packed BC3 and compresses on the gpu) and no reuse of a
//   packed texture saved next to the material (keep_packed / IsPackedFromDisk): nothing is saved yet.
// - the packed texture is not put in a ResourceCache, the material owns it; it gets a full mip chain (spartan
//   creates it with one mip and its gpu preparation generates the rest).
// - textures without cpu data (gpu created render textures) can't be packed, like spartan's compressed ones.
namespace Phalanx.World;

internal static class MaterialTexturePacking
{
    private static bool IsValidForPacking(RHI_Texture? tex) => tex != null && tex.HasCpuData() && tex.GetWidth() > 0 && tex.GetHeight() > 0;
    private static byte[] Mip0(RHI_Texture tex) => tex.GetCpuMips()[0];

    // :357, bilinear, sampling at x * src / dst like spartan
    internal static byte[] ResizeTexture(byte[] src, uint src_width, uint src_height, uint dst_width, uint dst_height)
    {
        var dst = new byte[dst_width * dst_height * 4];
        float Get(uint x, uint y, uint c)
        {
            x = System.Math.Min(x, src_width - 1);
            y = System.Math.Min(y, src_height - 1);
            return src[(y * src_width + x) * 4 + c] / 255.0f;
        }
        for (uint y = 0; y < dst_height; ++y)
        {
            for (uint x = 0; x < dst_width; ++x)
            {
                float src_x = (float)x * src_width / dst_width;
                float src_y = (float)y * src_height / dst_height;
                uint x0 = (uint)src_x, y0 = (uint)src_y;
                float fx = src_x - x0, fy = src_y - y0;
                for (uint c = 0; c < 4; c++)
                {
                    float top    = Get(x0, y0, c) + (Get(x0 + 1, y0, c) - Get(x0, y0, c)) * fx;
                    float bottom = Get(x0, y0 + 1, c) + (Get(x0 + 1, y0 + 1, c) - Get(x0, y0 + 1, c)) * fx;
                    dst[(y * dst_width + x) * 4 + c] = (byte)((top + (bottom - top) * fy) * 255.0f);
                }
            }
        }
        return dst;
    }

    // :224
    internal static void MergeAlphaMaskIntoColorAlpha(byte[] albedo, byte[] mask)
    {
        Helpers.PLXAssertMsg(albedo.Length == mask.Length, "The dimensions must be equal");
        for (int i = 0; i < albedo.Length; i += 4)
        {
            float alpha_albedo   = albedo[i + 3] / 255.0f; // channel a
            float alpha_mask     = mask[i] / 255.0f;       // channel r
            float alpha_combined = System.Math.Min(alpha_albedo, alpha_mask);
            albedo[i + 3] = (byte)(alpha_combined * 255.0f);
        }
    }

    // :198, gltf: occlusion, roughness and metalness as r, g, b
    internal static void PackOcclusionRoughnessMetalnessHeight(byte[] occlusion, byte[] roughness, byte[] metalness, byte[] height, bool is_gltf, byte[] output)
    {
        Helpers.PLXAssertMsg(occlusion.Length == roughness.Length && roughness.Length == metalness.Length && metalness.Length == height.Length,
                             "The dimensions must be equal");
        for (int i = 0; i < occlusion.Length; i += 4)
        {
            output[i + 0] = occlusion[i];
            output[i + 1] = roughness[i + (is_gltf ? 1 : 0)];
            output[i + 2] = metalness[i + (is_gltf ? 2 : 0)];
            output[i + 3] = height[i];
        }
    }

    // :408, the source's mip 0 (resized to the packed size when it differs) or a constant
    private static byte[] DataOrDefault(RHI_Texture? tex, uint width, uint height, byte value)
    {
        if (!IsValidForPacking(tex))
        {
            var constant = new byte[width * height * 4];
            Array.Fill(constant, value);
            return constant;
        }
        if (tex!.GetWidth() != width || tex.GetHeight() != height)
            return ResizeTexture(Mip0(tex), tex.GetWidth(), tex.GetHeight(), width, height);
        return Mip0(tex);
    }

    // :416
    internal static void PackTextures(Material material, uint slot)
    {
        RHI_Texture? texture_color      = material.GetTexture(MaterialTextureType.Color, slot);
        RHI_Texture? texture_alpha_mask = material.GetTexture(MaterialTextureType.AlphaMask, slot);
        RHI_Texture? texture_occlusion  = material.GetTexture(MaterialTextureType.Occlusion, slot);
        RHI_Texture? texture_roughness  = material.GetTexture(MaterialTextureType.Roughness, slot);
        RHI_Texture? texture_metalness  = material.GetTexture(MaterialTextureType.Metalness, slot);
        RHI_Texture? texture_height     = material.GetTexture(MaterialTextureType.Height, slot);

        // find max resolution among relevant textures
        uint max_width = 1, max_height = 1;
        foreach (RHI_Texture? tex in new[] { texture_color, texture_occlusion, texture_roughness, texture_metalness, texture_height })
        {
            if (IsValidForPacking(tex))
            {
                max_width  = System.Math.Max(max_width, tex!.GetWidth());
                max_height = System.Math.Max(max_height, tex.GetHeight());
            }
        }

        // step 1: pack alpha mask into color alpha (resize if needed)
        if (texture_alpha_mask != null)
        {
            if (texture_color == null)
            {
                material.SetTexture(MaterialTextureType.Color, texture_alpha_mask, slot, false);
            }
            else if (IsValidForPacking(texture_color) && IsValidForPacking(texture_alpha_mask))
            {
                byte[] mask = texture_color.GetWidth() != texture_alpha_mask.GetWidth() || texture_color.GetHeight() != texture_alpha_mask.GetHeight()
                    ? ResizeTexture(Mip0(texture_alpha_mask), texture_alpha_mask.GetWidth(), texture_alpha_mask.GetHeight(), texture_color.GetWidth(), texture_color.GetHeight())
                    : Mip0(texture_alpha_mask);
                MergeAlphaMaskIntoColorAlpha(Mip0(texture_color), mask);

                // spartan regenerates the mips when it prepares the texture, the cpu chain is rebuilt here
                IReadOnlyList<byte[]> mips = texture_color.GetCpuMips();
                if (mips.Count > 1)
                {
                    List<byte[]> rebuilt = RHI_Texture.GenerateMips(mips[0], texture_color.GetWidth(), texture_color.GetHeight());
                    for (int i = 1; i < mips.Count && i < rebuilt.Count; i++)
                        rebuilt[i].CopyTo(mips[i], 0);
                }
                texture_color.MarkCpuDataChanged();

                // the merged alpha now carries the mask, so flag the color texture as transparent
                texture_color.SetCpuFlag(RHI_Texture_Cpu_Flags.Transparent, true);
            }
            else if (!texture_color.IsSemiTransparent())
            {
                Log.LogWarning($"material '{material.GetObjectName()}': colour map has no cpu data to merge the alpha mask into, alpha testing is lost");
            }
        }

        // step 2: pack occlusion, roughness, metalness, and height into a single texture
        // shaders only read the packed channels behind the has texture flags, a material without any of the four
        // sources never samples it
        if (texture_occlusion == null && texture_roughness == null && texture_metalness == null && texture_height == null)
        {
            material.SetTexture(MaterialTextureType.Packed, null, slot, false);
            return;
        }

        byte roughness_default = (byte)(material.GetProperty(MaterialProperty.Roughness) * 255.0f);
        byte metalness_default = (byte)(material.GetProperty(MaterialProperty.Metalness) * 255.0f);
        byte[] occlusion_data = DataOrDefault(texture_occlusion, max_width, max_height, 255);
        byte[] roughness_data = DataOrDefault(texture_roughness, max_width, max_height, roughness_default);
        byte[] metalness_data = DataOrDefault(texture_metalness, max_width, max_height, metalness_default);
        byte[] height_data    = DataOrDefault(texture_height,    max_width, max_height, 127);

        var packed = new byte[max_width * max_height * 4];
        PackOcclusionRoughnessMetalnessHeight(occlusion_data, roughness_data, metalness_data, height_data,
                                              material.GetProperty(MaterialProperty.Gltf) == 1.0f, packed);

        string tex_name = material.GetObjectName() + "_packed_slot" + slot;
        var texture_packed = new RHI_Texture(tex_name + ".tex_packed", max_width, max_height,
                                             RHI_Texture.GenerateMips(packed, max_width, max_height));
        material.SetTexture(MaterialTextureType.Packed, texture_packed, slot, false);
    }
}
