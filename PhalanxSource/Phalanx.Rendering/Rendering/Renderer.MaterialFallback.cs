// The renderer's two reserved materials and what a renderable draws with when its material can't be used. Spartan has
// one engine material, the standard one (Renderer_Resources.cpp:1598), which a renderable without a material gets
// (Render::SetDefaultMaterial, Render.cpp:649) and which UpdateMaterials points a material that overflows the table at
// (index 0, Renderer.cpp:3011-3019; a texture that overflows the descriptor table keeps index 0, :3108-3115, both
// logged once for the process). Deviations:
// - Index 0 is the default material, spartan's standard one (white, its no_texture checkerboard, roughness 1,
//   metalness 0, no normal map). Index 1 is the illegal material: the sad face of data/textures/illegal_material.png,
//   point sampled and repeating (material flag bit 31, common_material.glsl), a solid magenta texture when the file is
//   missing or doesn't decode. Both are written first every frame so they always fit, and are engine owned
//   (Material.IsEngineOwned: their edits are refused, Destroy returns false).
// - A material draws as the illegal one (its renderables, or a view's IPreDraw override) when it is destroyed
//   (Material.Destroy), failed to prepare (Material.CreationFailed), has a property that isn't finite, threw while its
//   entry was built, doesn't fit the material table, or its textures don't fit the bindless texture table. Spartan
//   keeps drawing an overflowing material with the standard material and the textures with whatever index 0 holds.
// - A texture that is set but can't be bound (no pixels, or its upload threw) falls back by itself: its flag bits are
//   cleared, so the shader takes the material's constants, which is what an unset texture does (white times the
//   colour, the vertex normal, the material's roughness and metalness, no occlusion). The material keeps drawing.
// - Each failure is logged once per material (or texture) and cause, again only after a renderer restart.
using System.Runtime.CompilerServices;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using RHI_Texture = Phalanx.Rendering.RHI.RHI_Texture;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    internal const uint default_material_index = 0;
    internal const uint illegal_material_index = 1;
    internal const int material_flag_point_sampled = 31; // Phalanx's, above spartan's bits (0-25)
    internal const string illegal_material_texture_file = "illegal_material.png";

    internal enum MaterialFailure { Destroyed, CreationFailed, InvalidProperty, BuildFailed, TableFull, TexturesFull, TextureMissing, TextureUploadFailed }

    // the table capacities, rhi_max_array_size like spartan; tests lower them (reset at Shutdown)
    internal static int s_material_capacity = (int)RHI_Definitions.rhi_max_array_size;
    internal static int s_texture_capacity  = (int)RHI_Definitions.rhi_max_array_size;

    private static Material? s_illegal_material;
    /// <summary>The engine's illegal material, what a renderable draws with when its material can't be used.</summary>
    public static Material? GetIllegalMaterial() => s_illegal_material;
    internal static bool IllegalTextureLoadedFromFile { get; private set; }

    private static readonly Dictionary<Material, uint> s_material_indices = new(ReferenceEqualityComparer.Instance);
    // this frame's materials that draw as the illegal one, why (tests)
    private static readonly Dictionary<Material, MaterialFailure> s_material_failures = new(ReferenceEqualityComparer.Instance);
    internal static bool TryGetMaterialFailure(Material material, out MaterialFailure failure) => s_material_failures.TryGetValue(material, out failure);
    private static readonly HashSet<(object, MaterialFailure)> s_logged_material_failures = new(new ReferencePairComparer());
    // cpu textures whose upload threw, at which revision: not tried again until their pixels change
    private static readonly Dictionary<Phalanx.World.RHI_Texture, ulong> s_failed_texture_uploads = new(ReferenceEqualityComparer.Instance);

    private sealed class ReferencePairComparer : IEqualityComparer<(object, MaterialFailure)>
    {
        public bool Equals((object, MaterialFailure) a, (object, MaterialFailure) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((object, MaterialFailure) k) => HashCode.Combine(RuntimeHelpers.GetHashCode(k.Item1), k.Item2);
    }

    // the illegal material, made with the default one at Initialize (CreateStandardMaterials)
    private static void CreateIllegalMaterial()
    {
        var material = new Material();
        material.SetResourceName("illegal" + FileSystem.EXTENSION_MATERIAL);
        material.SetProperty(MaterialProperty.TextureTilingX, 1.0f);
        material.SetProperty(MaterialProperty.TextureTilingY, 1.0f);
        material.SetProperty(MaterialProperty.ColorR,         1.0f);
        material.SetProperty(MaterialProperty.ColorG,         1.0f);
        material.SetProperty(MaterialProperty.ColorB,         1.0f);
        material.SetProperty(MaterialProperty.ColorA,         1.0f);
        material.SetTexture(MaterialTextureType.Color,        LoadIllegalTexture(out bool from_file));
        IllegalTextureLoadedFromFile = from_file;
        material.SetEngineOwned();
        s_illegal_material = material;
    }

    // data/textures/illegal_material.png next to the binaries, rgba8 with its mips; solid magenta when it can't be read
    internal static Phalanx.World.RHI_Texture LoadIllegalTexture(out bool from_file, string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "data", "textures", illegal_material_texture_file);
        try
        {
            StbImageSharp.ImageResult image;
            using (FileStream stream = File.OpenRead(path))
                image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            from_file = true;
            return CreateTexture("illegal_material", (uint)image.Width, (uint)image.Height, image.Data);
        }
        catch (Exception e)
        {
            Log.LogWarning($"the illegal material texture '{path}' couldn't be read ({e.Message}), using solid magenta");
            from_file = false;
            return CreateTexture("illegal_material", 1, 1, StandardTextures.Solid(255, 0, 255, 255));
        }
    }

    private static void AddReservedMaterials()
    {
        if (s_standard_material != null)
            AddMaterial(s_standard_material);
        if (s_illegal_material != null)
            AddMaterial(s_illegal_material);
    }

    private static void LogMaterialFailure(object target, MaterialFailure failure, string what)
    {
        if (s_logged_material_failures.Add((target, failure)))
            Log.LogWarning(what);
    }

    private static string Describe(Material m) => $"material '{m.GetObjectName()}'";

    // puts a material in this frame's table, or records why it draws as the illegal material
    private static void AddMaterial(Material m)
    {
        if (s_material_indices.ContainsKey(m) || s_material_failures.ContainsKey(m))
            return;
        MaterialFailure? failure = null;
        string reason = string.Empty;
        if (m.IsDestroyed)
            (failure, reason) = (MaterialFailure.Destroyed, "is destroyed");
        else if (m.CreationFailed)
            (failure, reason) = (MaterialFailure.CreationFailed, "failed to prepare its textures");
        else if (FirstNonFiniteProperty(m) is MaterialProperty bad)
            (failure, reason) = (MaterialFailure.InvalidProperty, $"has a {bad} that isn't a finite number");
        else if (s_materials.Count >= s_material_capacity)
            (failure, reason) = (MaterialFailure.TableFull, $"doesn't fit the material table ({s_material_capacity} materials)");

        if (failure == null)
        {
            try
            {
                RHI_Texture?[] textures = GatherBoundTextures(m);
                if (CountNewTextures(textures) > s_texture_capacity - s_texture_indices.Count)
                {
                    (failure, reason) = (MaterialFailure.TexturesFull, $"doesn't fit its textures in the bindless texture table ({s_texture_capacity} textures)");
                }
                else
                {
                    WriteMaterialEntry(m, textures);
                    s_material_indices.Add(m, (uint)s_materials.Count);
                    s_materials.Add(m);
                    return;
                }
            }
            catch (Exception e)
            {
                (failure, reason) = (MaterialFailure.BuildFailed, $"failed to build its gpu entry ({e.Message})");
            }
        }

        s_material_failures.Add(m, failure.Value);
        if (ReferenceEquals(m, s_illegal_material) || ReferenceEquals(m, s_standard_material))
            LogMaterialFailure(m, failure.Value, $"engine {Describe(m)} {reason}"); // the reserved ones go first, it can't fit only at capacity < 2
        else
            LogMaterialFailure(m, failure.Value, $"{Describe(m)} {reason}, it draws with the illegal material");
    }

    private static MaterialProperty? FirstNonFiniteProperty(Material m)
    {
        for (int i = 0; i < (int)MaterialProperty.Max; i++)
            if (!float.IsFinite(m.GetProperty((MaterialProperty)i)))
                return (MaterialProperty)i;
        return null;
    }

    // the gpu textures of the 16 sampled slots (s_gpu_texture_types x slots_per_texture), null where unset or unusable
    private static RHI_Texture?[] GatherBoundTextures(Material m)
    {
        var bound = new RHI_Texture?[s_gpu_texture_types.Length * Material.slots_per_texture];
        for (int type = 0; type < s_gpu_texture_types.Length; type++)
            for (uint slot = 0; slot < Material.slots_per_texture; slot++)
                bound[type * (int)Material.slots_per_texture + (int)slot] = BindTexture(m, m.GetTexture(s_gpu_texture_types[type], slot), s_gpu_texture_types[type]);
        return bound;
    }

    private static int CountNewTextures(RHI_Texture?[] bound)
    {
        int count = 0;
        for (int i = 0; i < bound.Length; i++)
        {
            if (bound[i] is not RHI_Texture texture || s_texture_indices.ContainsKey(texture))
                continue;
            if (Array.IndexOf(bound, texture) == i) // first occurrence within the material
                count++;
        }
        return count;
    }

    // a set texture as the gpu texture to bind, null (logged once) when it has no pixels or its upload threw
    private static RHI_Texture? BindTexture(Material m, Phalanx.World.RHI_Texture? texture, MaterialTextureType type)
    {
        if (texture == null)
            return null;
        if (texture is not RHI_Texture && !texture.HasCpuData())
        {
            LogMaterialFailure(texture, MaterialFailure.TextureMissing, $"{Describe(m)}: its {type} texture '{texture.GetTextureName()}' has no pixels, the {type} falls back to the material's constants");
            return null;
        }
        if (s_failed_texture_uploads.TryGetValue(texture, out ulong failed_revision) && failed_revision == texture.GetCpuRevision())
            return null;
        try
        {
            return GetGpuTexture(texture);
        }
        catch (Exception e)
        {
            s_failed_texture_uploads[texture] = texture.GetCpuRevision();
            LogMaterialFailure(texture, MaterialFailure.TextureUploadFailed, $"{Describe(m)}: its {type} texture '{texture.GetTextureName()}' failed to upload ({e.Message}), the {type} falls back to the material's constants");
            return null;
        }
    }

    // the slot of a type's first texture in the gathered array
    private static int BoundSlot(MaterialTextureType type) => Array.IndexOf(s_gpu_texture_types, type) * (int)Material.slots_per_texture;

    // the flag bits of the texture types the material has but whose texture didn't bind; the packed texture carries
    // height (0), roughness (3), metalness (4), the alpha mask (5) and occlusion (7)
    private static uint UnboundTextureFlags(Material m, ReadOnlySpan<RHI_Texture?> bound)
    {
        uint cleared = 0;
        if (m.HasTextureOfType(MaterialTextureType.Color) && bound[BoundSlot(MaterialTextureType.Color)] == null)
            cleared |= 1u << 2 | 1u << 17;
        if (m.HasTextureOfType(MaterialTextureType.Normal) && bound[BoundSlot(MaterialTextureType.Normal)] == null)
            cleared |= 1u << 1;
        if (m.HasTextureOfType(MaterialTextureType.Emission) && bound[BoundSlot(MaterialTextureType.Emission)] == null)
            cleared |= 1u << 6 | 1u << 18;
        if (bound[BoundSlot(MaterialTextureType.Packed)] == null)
            cleared |= 1u << 0 | 1u << 3 | 1u << 4 | 1u << 5 | 1u << 7;
        return cleared;
    }

    private static void ResetMaterialFallbackState()
    {
        s_material_capacity = (int)RHI_Definitions.rhi_max_array_size;
        s_texture_capacity  = (int)RHI_Definitions.rhi_max_array_size;
        s_illegal_material  = null;
        s_material_indices.Clear();
        s_material_failures.Clear();
        s_logged_material_failures.Clear();
        s_failed_texture_uploads.Clear();
    }
}
