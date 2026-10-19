// The scene side of SpartanEngine source/rendering/Renderer.cpp @ 4c07baa the geometry pass needs: the view camera
// (GetViewCamera), the bindless geometry tables (:1413-1438), the material and light uploads of
// TickUploadBindlessDependencies (UpdateMaterials :2995-3060, UpdateLights :3270-3310) and WriteDrawData (:2894).
// Deviations:
// - The renderer walks World.GetEntitiesRenderables() / GetEntitiesLights() and World.GetCamera() like spartan. The
//   Render component holds an IMesh (Mesh lives in Phalanx.Rendering, see Phalanx.Core/Geometry/IMesh.cs); a mesh
//   that isn't in the geometry buffer yet is uploaded on first sight and released at Shutdown. No camera override.
// - Materials upload colour, roughness, metalness, normal, height, the emissive scale, the texture flags and the
//   Color/Normal/Emission/Packed texture indices (Renderer.cpp:2973-3122) into the deduped bindless texture table.
//   Cpu textures are uploaded on first use. No terrain, vegetation, paint or clearcoat properties, no
//   Material::SetIndex. Lights upload colour, intensity (the watt
//   approximation, Phalanx has no radiometric conversion yet), position, direction, range, angle and the type flags;
//   no shadows, slices or ies.
// - Material and light tables are rewritten only when their contents change, at offset 0 (spartan: the same).
// - Every renderable draws lod 0 of each sub-mesh, there is no culling or lod selection yet.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using static Phalanx.Rendering.Renderer_Definitions;
using RHI_Texture = Phalanx.Rendering.RHI.RHI_Texture;
using Texture = Phalanx.Rendering.RHI.RHI_Texture;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    private static readonly List<Material> s_materials = new();
    private static readonly MaterialParameters[] s_material_parameters = new MaterialParameters[RHI_Definitions.rhi_max_array_size];
    private static readonly LightParameters[] s_light_parameters = new LightParameters[RHI_Definitions.rhi_max_array_size];
    private static byte[]? s_material_bytes_uploaded, s_light_bytes_uploaded;
    private static uint s_light_count;
    private static uint s_draw_data_count;
    private static ulong s_bound_vertex_id, s_bound_index_id, s_bound_instance_id;
    private static readonly HashSet<Mesh> s_uploaded_meshes = new();

    // a cpu draw: what the prepass and the g-buffer replay (spartan: Renderer_DrawCall)
    private struct DrawCall
    {
        public uint draw_index, material_index, index_count, index_offset, vertex_offset;
        public Render? render;
    }
    // spartan's two halves: opaque draws first, alpha tested draws (Material::IsAlphaTested) after
    // (Renderer_Passes_Geometry.cpp:1553 prepass, :1764 g-buffer)
    private static readonly List<DrawCall> s_draw_calls       = new();
    private static readonly List<DrawCall> s_draw_calls_alpha = new();

    // UpdateDrawCalls, the part that exists: every active render entity with gpu geometry, lod 0, one draw data
    // entry each (no culling, sorting, instancing or lod selection)
    private static void UpdateDrawCalls()
    {
        s_draw_calls.Clear();
        s_draw_calls_alpha.Clear();
        foreach (Entity entity in Phalanx.World.World.GetEntitiesRenderables())
        {
            if (entity.GetComponent<Render>() is not Render render || render.GetMesh() is not Mesh mesh || !mesh.HasGpuBuffers())
                continue;
            if (render.GetIndexCount() == 0)
                continue;
            Matrix4x4 transform = entity.GetMatrix();
            Material? material  = GetRenderMaterial(render);
            uint material_index = GetMaterialIndex(material);
            uint draw_index     = WriteDrawData(transform, entity.GetMatrixPrevious(), material_index, 0, render);
            if (draw_index == uint.MaxValue)
                break;
            (material != null && material.IsAlphaTested() ? s_draw_calls_alpha : s_draw_calls).Add(new DrawCall
            {
                draw_index     = draw_index,
                material_index = material_index,
                index_count    = (uint)render.GetIndexCount(),
                index_offset   = render.GetIndexOffset(),
                vertex_offset  = render.GetVertexOffset(),
                render         = render,
            });
        }
    }

    /// <summary>The camera the frame renders from (Renderer::GetCamera): the world's.</summary>
    public static Camera? GetViewCamera() => Phalanx.World.World.GetCamera();

    // the material a renderable draws with: its own, else the standard one (spartan: SetDefaultMaterial on load)
    internal static Material? GetRenderMaterial(Render render) => render.GetMaterial() ?? s_standard_material;

    internal static uint GetMaterialIndex(Material? material) => material == null ? 0u : (uint)System.Math.Max(s_materials.IndexOf(material), 0);
    internal static uint GetLightCount() => s_light_count;

    // the world's render entities whose mesh is in the geometry buffer, uploading meshes on first sight (spartan
    // uploads in Mesh::PostProcess / CreateGpuBuffers when the model loads)
    private static void UploadWorldMeshes()
    {
        foreach (Entity entity in Phalanx.World.World.GetEntitiesRenderables())
        {
            if (entity.GetComponent<Render>()?.GetMesh() is Mesh mesh && !mesh.HasGpuBuffers())
            {
                mesh.CreateGpuBuffers();
                s_uploaded_meshes.Add(mesh);
            }
        }
    }

    // TickUploadBindlessDependencies, the part that exists: geometry, materials, lights
    private static void TickUploadBindlessDependencies(RHI_CommandList cmd)
    {
        UploadWorldMeshes();
        GeometryBuffer.BuildIfDirty();
        UpdateBindlessGeometry();
        UpdateMaterials(cmd);
        UpdateLights(cmd);
        s_draw_data_count = 0;
        UpdateDrawCalls();
    }

    // Renderer.cpp:1413-1438, the descriptors follow the buffer identity, the instances fall back to the dummy buffer
    private static void UpdateBindlessGeometry()
    {
        RHI_Buffer? vertices  = GeometryBuffer.GetVertexBuffer();
        RHI_Buffer? indices   = GeometryBuffer.GetIndexBuffer();
        RHI_Buffer? instances = GeometryBuffer.GetInstanceBuffer() ?? GetBuffer(Renderer_Buffer.DummyInstance);
        ulong Id(RHI_Buffer? b) => b == null ? 0ul : (ulong)b.GetRhiResource().Handle;
        if (vertices != null && Id(vertices) != s_bound_vertex_id)
        {
            RHI_Device.UpdateBindlessGeometryVertices(vertices);
            s_bound_vertex_id = Id(vertices);
        }
        if (indices != null && Id(indices) != s_bound_index_id)
        {
            RHI_Device.UpdateBindlessGeometryIndices(indices);
            s_bound_index_id = Id(indices);
        }
        if (instances != null && Id(instances) != s_bound_instance_id)
        {
            RHI_Device.UpdateBindlessInstances(instances);
            s_bound_instance_id = Id(instances);
        }
    }

    // Renderer.cpp:2973, the texture types the gpu samples, slots_per_texture (4) slots each: 16 indices
    private static readonly MaterialTextureType[] s_gpu_texture_types =
        { MaterialTextureType.Color, MaterialTextureType.Normal, MaterialTextureType.Emission, MaterialTextureType.Packed };
    private static readonly RHI_Texture?[] s_bindless_textures = new RHI_Texture?[RHI_Definitions.rhi_max_array_size];
    private static readonly RHI_Texture?[] s_bindless_textures_bound = new RHI_Texture?[RHI_Definitions.rhi_max_array_size];
    private static readonly Dictionary<RHI_Texture, uint> s_texture_indices = new();
    private static bool s_bindless_textures_never_bound = true;

    // Renderer.cpp:2979 should_decode_as_srgb
    private static bool ShouldDecodeAsSrgb(RHI_Texture? texture)
    {
        if (texture == null)
            return false;
        if ((texture.GetFlags() & RHI_Texture_Flags.RHI_Texture_Srgb) != 0)
            return true;
        if (texture.IsGrayscale())
            return false;
        RHI_Format format = texture.GetFormat();
        return format == RHI_Format.R8G8B8A8_Unorm || format == RHI_Format.B8R8G8A8_Unorm ||
               format == RHI_Format.BC1_Unorm || format == RHI_Format.BC3_Unorm || format == RHI_Format.BC7_Unorm;
    }

    // Renderer.cpp:3066-3093, the bits g_buffer.frag reads (the terrain/vegetation/paint/road bits come with them)
    private static uint MaterialFlags(Material m)
    {
        uint flags = 0;
        flags |= m.HasTextureOfType(MaterialTextureType.Height)             ? 1u << 0 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Normal)             ? 1u << 1 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Color)              ? 1u << 2 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Roughness)          ? 1u << 3 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Metalness)          ? 1u << 4 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.AlphaMask)          ? 1u << 5 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Emission)           ? 1u << 6 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Occlusion)          ? 1u << 7 : 0u;
        flags |= m.GetProperty(MaterialProperty.EmissiveFromAlbedo) != 0.0f ? 1u << 15 : 0u;
        flags |= m.IsAlphaTested()                                          ? 1u << 16 : 0u;
        flags |= ShouldDecodeAsSrgb(GetGpuTexture(m.GetTexture(MaterialTextureType.Color)))    ? 1u << 17 : 0u;
        flags |= ShouldDecodeAsSrgb(GetGpuTexture(m.GetTexture(MaterialTextureType.Emission))) ? 1u << 18 : 0u;
        return flags;
    }

    // a material texture as a gpu texture: gpu ones as they are, cpu ones (importer, packing, CreateTexture) uploaded
    // the first time they are bound and again when their pixels change (spartan: RHI_Texture::PrepareForGpu)
    private static readonly Dictionary<Phalanx.World.RHI_Texture, (RHI_Texture gpu, ulong revision)> s_cpu_texture_uploads = new(ReferenceEqualityComparer.Instance);
    internal static RHI_Texture? GetGpuTexture(Phalanx.World.RHI_Texture? texture)
    {
        if (texture == null)
            return null;
        if (texture is RHI_Texture gpu_texture)
            return gpu_texture;
        if (!texture.HasCpuData())
            return null;
        if (s_cpu_texture_uploads.TryGetValue(texture, out var upload))
        {
            if (upload.revision == texture.GetCpuRevision())
                return upload.gpu;
            RHI_Device.QueueWaitAll(); // the old copy may be in use by a frame in flight
            upload.gpu.Dispose();
        }
        var slice = new RHI_Texture_Slice();
        foreach (byte[] mip in texture.GetCpuMips())
            slice.mips.Add(new RHI_Texture_Mip { bytes = mip });
        var flags = RHI_Texture_Flags.RHI_Texture_Srv | (RHI_Texture_Flags)(uint)texture.GetCpuFlags(); // same bit values
        string name = texture.GetTextureName().Length > 0 ? texture.GetTextureName() : "material_texture";
        var gpu = new RHI_Texture(RHI_Texture_Type.Type2D, texture.GetWidth(), texture.GetHeight(), 1, (uint)texture.GetCpuMips().Count,
                                  RHI_Format.R8G8B8A8_Unorm, flags, name, new List<RHI_Texture_Slice> { slice });
        s_cpu_texture_uploads[texture] = (gpu, texture.GetCpuRevision());
        return gpu;
    }

    private static void ReleaseCpuTextureUploads()
    {
        foreach (var upload in s_cpu_texture_uploads.Values)
            upload.gpu.Dispose();
        s_cpu_texture_uploads.Clear();
    }

    // Renderer.cpp:3098-3122, textures dedupe into the bindless table, unset slots keep index 0 (the flags gate them)
    private static void WriteMaterialTextureIndices(Material m, ref MaterialParameters p)
    {
        for (int type = 0; type < s_gpu_texture_types.Length; type++)
        {
            for (uint slot = 0; slot < Material.slots_per_texture; slot++)
            {
                if (GetGpuTexture(m.GetTexture(s_gpu_texture_types[type], slot)) is not RHI_Texture texture)
                    continue; // unset, or a size-only texture without pixels
                if (!s_texture_indices.TryGetValue(texture, out uint index))
                {
                    if (s_texture_indices.Count >= RHI_Definitions.rhi_max_array_size)
                    {
                        Log.LogError("material texture descriptor capacity exceeded");
                        continue;
                    }
                    index = (uint)s_texture_indices.Count;
                    s_texture_indices.Add(texture, index);
                    s_bindless_textures[index] = texture;
                }
                p.texture_indices[type * (int)Material.slots_per_texture + (int)slot] = index;
            }
        }
    }

    // the material textures descriptor array is rewritten only when the table changed (spartan: m_bindless_textures)
    private static void UpdateBindlessMaterialTextures()
    {
        if (!s_bindless_textures_never_bound && s_bindless_textures.AsSpan().SequenceEqual(s_bindless_textures_bound))
            return;
        RHI_Device.QueueWaitAll(); // a frame in flight may still read the descriptors being rewritten
        RHI_Device.UpdateBindlessMaterials(s_bindless_textures, null);
        s_bindless_textures.AsSpan().CopyTo(s_bindless_textures_bound);
        s_bindless_textures_never_bound = false;
    }

    private static void UpdateMaterials(RHI_CommandList cmd)
    {
        s_materials.Clear();
        s_texture_indices.Clear();
        Array.Clear(s_bindless_textures);
        foreach (Entity entity in Phalanx.World.World.GetEntitiesRenderables())
        {
            if (entity.GetComponent<Render>() is not Render render || GetRenderMaterial(render) is not Material material)
                continue;
            if (s_materials.Contains(material))
                continue;
            if (s_materials.Count >= RHI_Definitions.rhi_max_array_size)
            {
                Log.LogError("material bindless capacity exceeded, overflowing materials will use index 0");
                break;
            }
            Material m = material;
            ref MaterialParameters p = ref s_material_parameters[s_materials.Count];
            uint flags = MaterialFlags(m);
            p = new MaterialParameters
            {
                local_width       = m.GetProperty(MaterialProperty.WorldWidth),
                local_height      = m.GetProperty(MaterialProperty.WorldHeight),
                emissive_strength = m.GetProperty(MaterialProperty.EmissiveFromAlbedo),
                color             = new Vector4(m.GetProperty(MaterialProperty.ColorR), m.GetProperty(MaterialProperty.ColorG),
                                                m.GetProperty(MaterialProperty.ColorB), m.GetProperty(MaterialProperty.ColorA)),
                roughness         = m.GetProperty(MaterialProperty.Roughness),
                metalness         = m.GetProperty(MaterialProperty.Metalness),
                normal            = m.GetProperty(MaterialProperty.Normal),
                height            = m.GetProperty(MaterialProperty.Height),
                flags             = flags,
            };
            WriteMaterialTextureIndices(m, ref p);
            s_materials.Add(m);
        }
        UpdateBindlessMaterialTextures();
        UploadIfChanged(cmd, Renderer_Buffer.MaterialParameters, MemoryMarshal.AsBytes(s_material_parameters.AsSpan(0, System.Math.Max(s_materials.Count, 1))), ref s_material_bytes_uploaded);
    }

    private static readonly List<Light> s_lights = new();

    // UpdateLights (Renderer.cpp:3255-3340): indices first, then the shadow atlas (UpdateShadowAtlas, spartan runs it
    // when the lights changed, here every frame), then the matrices that depend on the atlas, then the table
    private static void UpdateLights(RHI_CommandList cmd)
    {
        s_light_count = 0;
        s_lights.Clear();
        // the first directional light always goes in slot 0, the sky and the sun helpers read it there
        // (Renderer.cpp:3334-3352), the rest follow in world order
        Light? first_directional = null;
        foreach (Entity light_entity in Phalanx.World.World.GetEntitiesLights())
        {
            if (light_entity.GetComponent<Light>() is Light light && light.GetLightType() == LightType.Directional)
            {
                first_directional = light;
                light.SetIndex(s_light_count++);
                s_lights.Add(light);
                break;
            }
        }
        foreach (Entity light_entity in Phalanx.World.World.GetEntitiesLights())
        {
            if (light_entity.GetComponent<Light>() is not Light light || light == first_directional)
                continue;
            if (s_light_count >= RHI_Definitions.rhi_max_array_size)
                break;
            light.SetIndex(s_light_count++);
            s_lights.Add(light);
        }

        UpdateShadowAtlas();

        foreach (Light light in s_lights)
        {
            light.UpdateMatrices(); // the directional cascades snap to the atlas texel they were just given
            uint index = light.GetIndex();
            Entity entity = light.GetEntity()!;
            LightType type = light.GetLightType();
            // phalanx: also needs a packed slice, spartan sets the bit for slices left unpacked past the minimum size
            bool shadows_effective = light.IsShadowEffective() && light.GetAtlasRectangle(0).IsDefined();
            Phalanx.Math.Color color = light.GetColorEffective(); // Renderer.cpp:3290-3293
            ref LightParameters entry = ref s_light_parameters[index];
            entry = new LightParameters
            {
                intensity       = light.GetIntensityRadiometric(),
                range           = light.GetRange(),
                angle           = light.GetAngle(),
                color           = new Vector4(color.R, color.G, color.B, color.A),
                position        = entity.GetPosition(),
                direction       = entity.GetForward(),
                direction_right = entity.GetRight(),
                area_width      = light.GetAreaWidth(),
                area_height     = light.GetAreaHeight(),
                // bit 4 (screen space shadows) and 5 (volumetric) stay clear, those passes aren't ported
                flags           = (type == LightType.Directional ? 1u << 0 : 0u) |
                                  (type == LightType.Point       ? 1u << 1 : 0u) |
                                  (type == LightType.Spot        ? 1u << 2 : 0u) |
                                  (shadows_effective             ? 1u << 3 : 0u) |
                                  (type == LightType.Area        ? 1u << 6 : 0u),
            };
            for (int i = 0; i < 6; i++)
            {
                if (i < light.GetSliceCount())
                {
                    entry.transform[i]         = light.GetViewProjectionMatrix((uint)i);
                    entry.atlas_offsets[i]     = light.GetAtlasOffset((uint)i);
                    entry.atlas_scales[i]      = light.GetAtlasScale((uint)i);
                    Phalanx.Math.Rectangle rect = light.GetAtlasRectangle((uint)i);
                    // spartan writes 1 / 0 = inf for unpacked slices, the shader never reads them (has_shadows is off)
                    entry.atlas_texel_sizes[i] = rect.IsDefined() ? new Vector2(1.0f / rect.Width, 1.0f / rect.Height) : Vector2.Zero;
                }
            }
        }

        // slot 0 is read directly by the sky, clear it when the last light disappears instead of leaving the previous
        // sun live; the atmosphere still uses the direction at zero intensity (Renderer.cpp:3497-3504)
        if (s_light_count == 0)
        {
            s_light_parameters[0] = new LightParameters
            {
                direction       = new Vector3(0.0f, -1.0f, 0.0f),
                direction_right = new Vector3(1.0f, 0.0f, 0.0f),
            };
        }
        UploadIfChanged(cmd, Renderer_Buffer.LightParameters, MemoryMarshal.AsBytes(s_light_parameters.AsSpan(0, (int)System.Math.Max(s_light_count, 1u))), ref s_light_bytes_uploaded);
    }

    // light slot 0 as uploaded this frame, the sky's sun (Pass_Skysphere change detection)
    internal static ref readonly LightParameters GetLightParametersSlot0() => ref s_light_parameters[0];

    // a shadow map slice: one directional cascade, one point light cube face or a spot light (Renderer.h ShadowSlice)
    internal sealed record ShadowSlice(Light light, uint slice_index)
    {
        public uint resolution;
        public Phalanx.Math.Rectangle rect;
    }
    private static readonly List<ShadowSlice> s_shadow_slices = new();
    internal static IReadOnlyList<ShadowSlice> GetShadowSlices() => s_shadow_slices;

    /// <summary>The shadow atlas size, square (spartan: renderer_resolution_shadow_atlas, 8192). Takes effect on the next
    /// Initialize; a smaller atlas keeps software rasterizers fast.</summary>
    public static uint ShadowAtlasResolution { get; set; } = Renderer_Definitions.renderer_resolution_shadow_atlas;

    // UpdateShadowAtlas (Renderer.cpp:4739-4851): every slice of every light with effective shadows gets the same
    // square size, the largest that row packs (8 texel borders) into the atlas, found by binary search
    internal static void UpdateShadowAtlas()
    {
        Texture? atlas = GetRenderTargetTexture(RenderTarget.shadow_atlas);
        s_shadow_slices.Clear();
        if (atlas == null)
            return;
        uint resolution_atlas = atlas.GetWidth();
        const uint min_slice_res = 256;

        foreach (Light light in s_lights)
        {
            light.ClearAtlasRectangles();
            if (!light.IsShadowEffective())
                continue;
            for (uint i = 0; i < light.GetSliceCount(); ++i)
                s_shadow_slices.Add(new ShadowSlice(light, i));
        }
        if (s_shadow_slices.Count == 0)
            return;

        bool PackRow(uint slice_res, uint num_slices, bool assign) => PackShadowRow(slice_res, num_slices, resolution_atlas, assign ? s_shadow_slices : null);

        // binary search for the largest uniform slice resolution
        uint max_slice_res = resolution_atlas;
        uint num_slices    = (uint)s_shadow_slices.Count;
        if (num_slices > 1)
        {
            uint low = min_slice_res, high = resolution_atlas;
            while (low < high)
            {
                uint mid = (low + high + 1) / 2;
                if (PackRow(mid, num_slices, false))
                    low = mid;
                else
                    high = mid - 1;
            }
            max_slice_res = low;
        }
        max_slice_res = System.Math.Max(max_slice_res, min_slice_res);

        // assign rectangles (spartan ignores the result: past the minimum the trailing slices keep a zero rect)
        PackRow(max_slice_res, num_slices, true);
        foreach (ShadowSlice slice in s_shadow_slices)
            slice.light.SetAtlasRectangle(slice.slice_index, slice.rect);
    }

    // row-based packing: uniform slices left to right with 8 texel borders, wrapping to the next row; assign only
    // when given the slices (the PackRow lambda of UpdateShadowAtlas)
    private static bool PackShadowRow(uint slice_res, uint num_slices, uint resolution_atlas, List<ShadowSlice>? assign)
    {
        const uint border = 8;
        if (slice_res > resolution_atlas)
            return false;
        uint x = 0, y = 0, row_h = 0;
        for (int i = 0; i < num_slices; ++i)
        {
            uint left_pad = x == 0 ? 0 : border;
            uint placed_x = x + left_pad;
            if (placed_x + slice_res > resolution_atlas)
            {
                y       += row_h + border;
                x        = 0;
                row_h    = 0;
                placed_x = 0;
            }
            if (placed_x + slice_res > resolution_atlas || y + slice_res > resolution_atlas)
                return false;
            if (assign != null)
            {
                assign[i].resolution = slice_res;
                assign[i].rect       = new Phalanx.Math.Rectangle(placed_x, y, slice_res, slice_res);
            }
            x     = placed_x + slice_res;
            row_h = System.Math.Max(row_h, slice_res);
        }
        return true;
    }
    internal static bool PackRowForTest(uint slice_res, uint num_slices, uint resolution_atlas) => PackShadowRow(slice_res, num_slices, resolution_atlas, null);

    private static void UploadIfChanged(RHI_CommandList cmd, Renderer_Buffer type, ReadOnlySpan<byte> bytes, ref byte[]? uploaded)
    {
        if (uploaded != null && bytes.SequenceEqual(uploaded))
            return;
        RHI_Buffer buffer = GetBuffer(type)!;
        buffer.ResetOffset();
        buffer.Update(cmd, bytes, (uint)bytes.Length);
        uploaded = bytes.ToArray();
    }

    // WriteDrawData: the draw data buffer is partitioned into per-frame regions, each frame writes its own
    internal static uint WriteDrawData(Matrix4x4 transform, Matrix4x4 transform_previous, uint material_index, uint is_transparent, Render? render = null)
    {
        if (s_draw_data_count >= renderer_max_draw_calls)
        {
            Log.LogWarning($"draw data budget exhausted ({renderer_max_draw_calls}), dropping further draws this frame");
            return uint.MaxValue;
        }
        uint index = s_draw_data_count++;
        var entry = new DrawData
        {
            transform          = transform,
            transform_previous = transform_previous,
            material_index     = material_index,
            is_transparent     = is_transparent,
        };
        // fill_uv_draw_fields_from_render (Renderer.cpp:451)
        if (render != null)
        {
            entry.uv_tiling      = new Vector2(render.ResolveUvTilingX(), render.ResolveUvTilingY());
            entry.uv_offset      = new Vector2(render.ResolveUvOffsetX(), render.ResolveUvOffsetY());
            entry.uv_invert      = new Vector2(render.ResolveUvInvertX(), render.ResolveUvInvertY());
            entry.uv_rotation    = render.ResolveUvRotation();
            entry.uv_world_space = render.ResolveUvWorldSpace();
        }
        uint global_index = s_frame_cb_ring_slot * renderer_max_draw_calls + index;
        RHI_Buffer buffer = GetBuffer(Renderer_Buffer.DrawData)!;
        buffer.UploadSubRegion(MemoryMarshal.AsBytes(new ReadOnlySpan<DrawData>(in entry)), (ulong)global_index * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<DrawData>());
        return global_index;
    }

    private static void ResetSceneGpuState()
    {
        foreach (Mesh mesh in s_uploaded_meshes)
            mesh.ReleaseGpuBuffers();
        s_uploaded_meshes.Clear();
        s_draw_calls.Clear();
        s_draw_calls_alpha.Clear();
        s_materials.Clear();
        s_material_bytes_uploaded = s_light_bytes_uploaded = null;
        s_bound_vertex_id = s_bound_index_id = s_bound_instance_id = 0;
        s_draw_data_count = 0;
        s_light_count     = 0;
    }
}
