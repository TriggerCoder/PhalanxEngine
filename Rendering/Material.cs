using System;

namespace Phalanx;

public enum MaterialTextureType
{
    Color,
    Roughness, // packed g
    Metalness, // packed b
    Normal,
    Occlusion, // packed r
    Emission,
    Height,    // packed a
    AlphaMask, // packed into color a
    Packed,    // occlusion, roughness, metalness, height
    Max
}
public enum MaterialProperty
{
    // system / meta
    Gltf,                       // imported from gltf file

    // world / geometry context
    WorldHeight,                // height of the mesh
    WorldWidth,                 // width of the mesh
    WorldSpaceUv,               // use world-space uvs
    Tessellation,               // enable tessellation

    // core pbr
    ColorR,                     // base color red
    ColorG,                     // base color green
    ColorB,                     // base color blue
    ColorA,                     // base color alpha
    Roughness,                  // surface roughness
    Metalness,                  // metallic factor
    Normal,                     // normal map
    Height,                     // height map

    // extended pbr
    Clearcoat,                  // extra reflective layer
    Clearcoat_Roughness,        // roughness of clearcoat
    Anisotropic,                // anisotropy level
    AnisotropicRotation,        // anisotropy direction
    Sheen,                      // velvet-like reflection at grazing angles
    SubsurfaceScattering,       // light scattering through surface
    NormalFromAlbedo,           // derive normal from albedo
    EmissiveFromAlbedo,         // derive emissive from albedo

    // texture transforms
    TextureTilingX,             // tiling along x axis
    TextureTilingY,             // tiling along y axis
    TextureOffsetX,             // offset along x axis
    TextureOffsetY,             // offset along y axis
    TextureInvertX,             // invert texture along x axis (mirror horizontally)
    TextureInvertY,             // invert texture along y axis (mirror vertically)

    // special effects
    IsTerrain,                  // slope-based texture mapping
    IsGrassBlade,               // grass blade specific effects
    IsFlower,                   // flower specific effects
    WindAnimation,              // vertex wind animation
    ColorVariationFromInstance, // per-instance color variation
    IsWater,                    // water flow animation

    // render settings
    CullMode,                   // face culling mode

    // sentinel
    Max                         // total number of properties
}

public enum MaterialIor
{
    Air,
    Water,
    Eyes,
    Glass,
    Sapphire,
    Diamond,
    Max
}

[Serializable]
public class Material : IResource
{
    public const uint slots_per_texture = 4;

    private RHI_Texture[] m_textures = new RHI_Texture[(int)MaterialTextureType.Max * slots_per_texture];
    private float[] m_properties = new float[(int)MaterialProperty.Max];
    private uint m_index = 0;
    private bool m_needs_repack = true; // starts true so first PrepareForGpu() packs textures
    private readonly object m_mutex = new object();
    private bool IsPackableTextureType(MaterialTextureType type)
    {
        // these texture types contribute to the packed texture (occlusion, roughness, metalness, height)
        // or affect color packing (alpha mask into color alpha)
        switch (type)
        {
            case MaterialTextureType.Color:
            case MaterialTextureType.Roughness:
            case MaterialTextureType.Metalness:
            case MaterialTextureType.Occlusion:
            case MaterialTextureType.Height:
            case MaterialTextureType.AlphaMask:
                return true;
            default:
                return false;
        }
    }

    // textures
    private void SetProperty(MaterialProperty property_type, float value)
    {
        if (m_properties[(uint)(property_type)] == value)
            return;

        if (property_type == MaterialProperty.ColorA)
        {
            // if an object switches from opaque to transparent or vice versa, make the world update so that the renderer
            // goes through the entities and makes the ones that use this material, render in the correct mode.
            float current_alpha = m_properties[(uint)(property_type)];
            if ((current_alpha != 1.0f && value == 1.0f) || (current_alpha == 1.0f && value != 1.0f))
            {
                RHI_CullMode cull_mode = value < 1.0f ? RHI_CullMode::None : RHI_CullMode::Back;
                m_properties[(uint)(MaterialProperty.CullMode)] = (uint)(cull_mode);
            }

            // transparent objects are typically see-through (low roughness) so use the alpha as the roughness multiplier.
            m_properties[(uint)(MaterialProperty.Roughness)] = value * 0.5f;
        }

        m_properties[(uint)(property_type)] = value;

        // save on change
        SaveToFile(GetResourceFilePath());
    }
    public void SetTexture(MaterialTextureType texture_type, RHI_Texture texture, uint slot = 0, bool auto_adjust_multipler = true)
    {
        Helpers.PLXAssert(slot < slots_per_texture);

        // calculate the actual array index based on texture type and slot
        uint array_index = ((uint)(texture_type) * slots_per_texture) + slot;

        // check if the texture is actually changing
        RHI_Texture previous_texture = m_textures[array_index];
        bool texture_changed = (previous_texture != texture);

        m_textures[array_index] = texture;

        // mark for repacking if this texture type contributes to the packed texture and actually changed
        if (texture_changed && IsPackableTextureType(texture_type))
        {
            m_needs_repack = true;

            // if already prepared for gpu, schedule async repack
            if (m_resource_state == ResourceState.PreparedForGpu)
            {
                PrepareForGpu();
            }
        }

        if (auto_adjust_multipler)
        {
            switch (texture_type)
            {
                case MaterialTextureType.Metalness:
                    SetProperty(MaterialProperty.Metalness, 1.0f);
                break;
                case MaterialTextureType.Normal:
                    SetProperty(MaterialProperty.Normal, 1.0f);
                break;
                case MaterialTextureType.Roughness:
                    SetProperty(MaterialProperty.Roughness, 1.0f);
                break;
                case MaterialTextureType.Height:
                    SetProperty(MaterialProperty.Height, 1.0f);
                break;
                default:
                break;
            }
        }

        // save on change, but not during loading (auto_adjust_multipler is false when called from LoadFromFile)
        if (auto_adjust_multipler)
        {
            SaveToFile(GetResourceFilePath());
        }
    }
}