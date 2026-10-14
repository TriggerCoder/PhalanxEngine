// The scene side of SpartanEngine source/rendering/Renderer.cpp @ 4c07baa the geometry pass needs: the view camera
// (GetViewCamera), the bindless geometry tables (:1413-1438), the material and light uploads of
// TickUploadBindlessDependencies (UpdateMaterials :2995-3060, UpdateLights :3270-3310) and WriteDrawData (:2894).
// Deviations:
// - The renderer walks World.GetEntitiesRenderables() / GetEntitiesLights() and World.GetCamera() like spartan. The
//   Render component holds an IMesh (Mesh lives in Phalanx.Rendering, see Phalanx.Core/Geometry/IMesh.cs); a mesh
//   that isn't in the geometry buffer yet is uploaded on first sight and released at Shutdown. No camera override.
// - Materials upload colour, roughness, metalness, normal, height, the emissive scale, the texture flags and the
//   Color/Normal/Emission/Packed texture indices (Renderer.cpp:2973-3122) into the deduped bindless texture table.
//   No packing (the occlusion/roughness/metalness/height bits need a Packed texture), no alpha test bit, no terrain,
//   vegetation, paint or clearcoat properties, no Material::SetIndex. Lights upload colour, intensity (the watt
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
    }
    private static readonly List<DrawCall> s_draw_calls = new();

    // UpdateDrawCalls, the part that exists: every active render entity with gpu geometry, lod 0, one draw data
    // entry each (no culling, sorting, instancing or lod selection)
    private static void UpdateDrawCalls()
    {
        s_draw_calls.Clear();
        foreach (Entity entity in Phalanx.World.World.GetEntitiesRenderables())
        {
            if (entity.GetComponent<Render>() is not Render render || render.GetMesh() is not Mesh mesh || !mesh.HasGpuBuffers())
                continue;
            if (render.GetIndexCount() == 0)
                continue;
            Matrix4x4 transform = entity.GetMatrix();
            uint material_index = GetMaterialIndex(GetRenderMaterial(render));
            uint draw_index     = WriteDrawData(transform, GetPreviousTransform(entity, transform), material_index, 0);
            if (draw_index == uint.MaxValue)
                break;
            s_draw_calls.Add(new DrawCall
            {
                draw_index     = draw_index,
                material_index = material_index,
                index_count    = (uint)render.GetIndexCount(),
                index_offset   = render.GetIndexOffset(),
                vertex_offset  = render.GetVertexOffset(),
            });
        }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entity, StrongBox<Matrix4x4>> s_transform_drawn = new();

    // the transform the entity was drawn with last frame, for velocity. Spartan reads Entity::GetMatrixPrevious
    // (Entity.cpp:1015, an epoch committed per rendered frame); Phalanx's Entity has no transform history epoch
    // yet, so the renderer remembers the last drawn transform per entity.
    private static Matrix4x4 GetPreviousTransform(Entity entity, Matrix4x4 transform)
    {
        if (!s_transform_drawn.TryGetValue(entity, out StrongBox<Matrix4x4>? box))
        {
            s_transform_drawn.Add(entity, new StrongBox<Matrix4x4>(transform));
            return transform;
        }
        Matrix4x4 previous = box.Value;
        box.Value = transform;
        return previous;
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

    // Renderer.cpp:3066-3093, the bits g_buffer.frag reads. Phalanx has no texture packing yet (Material::PrepareForGpu
    // packs occlusion/roughness/metalness/height into the Packed texture): those four bits are set only when the
    // material carries a Packed texture, so the shader never samples a packed slot that isn't there.
    private static uint MaterialFlags(Material m)
    {
        bool packed = m.HasTextureOfType(MaterialTextureType.Packed);
        uint flags = 0;
        flags |= packed && m.HasTextureOfType(MaterialTextureType.Height)    ? 1u << 0 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Normal)              ? 1u << 1 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Color)               ? 1u << 2 : 0u;
        flags |= packed && m.HasTextureOfType(MaterialTextureType.Roughness) ? 1u << 3 : 0u;
        flags |= packed && m.HasTextureOfType(MaterialTextureType.Metalness) ? 1u << 4 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.AlphaMask)           ? 1u << 5 : 0u;
        flags |= m.HasTextureOfType(MaterialTextureType.Emission)            ? 1u << 6 : 0u;
        flags |= packed && m.HasTextureOfType(MaterialTextureType.Occlusion) ? 1u << 7 : 0u;
        flags |= m.GetProperty(MaterialProperty.EmissiveFromAlbedo) != 0.0f  ? 1u << 15 : 0u;
        flags |= ShouldDecodeAsSrgb(m.GetTexture(MaterialTextureType.Color) as RHI_Texture)    ? 1u << 17 : 0u;
        flags |= ShouldDecodeAsSrgb(m.GetTexture(MaterialTextureType.Emission) as RHI_Texture) ? 1u << 18 : 0u;
        return flags;
    }

    // Renderer.cpp:3098-3122, textures dedupe into the bindless table, unset slots keep index 0 (the flags gate them)
    private static void WriteMaterialTextureIndices(Material m, ref MaterialParameters p)
    {
        for (int type = 0; type < s_gpu_texture_types.Length; type++)
        {
            for (uint slot = 0; slot < Material.slots_per_texture; slot++)
            {
                if (m.GetTexture(s_gpu_texture_types[type], slot) is not RHI_Texture texture)
                    continue; // null, or a cpu-only texture the renderer can't bind
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

    private static void UpdateLights(RHI_CommandList cmd)
    {
        s_light_count = 0;
        foreach (Entity light_entity in Phalanx.World.World.GetEntitiesLights())
        {
            if (light_entity.GetComponent<Light>() is not Light light)
                continue;
            if (s_light_count >= RHI_Definitions.rhi_max_array_size)
                break;
            uint index = s_light_count++;
            light.SetIndex(index);
            Entity entity = light.GetEntity()!;
            LightType type = light.GetLightType();
            Phalanx.Math.Color color = light.GetColor();
            s_light_parameters[index] = new LightParameters
            {
                intensity       = light.GetIntensityWatt(),
                range           = light.GetRange(),
                angle           = light.GetAngle(),
                color           = new Vector4(color.R, color.G, color.B, color.A),
                position        = entity.GetPosition(),
                direction       = entity.GetForward(),
                direction_right = entity.GetRight(),
                area_width      = light.GetAreaWidth(),
                area_height     = light.GetAreaHeight(),
                flags           = (type == LightType.Directional ? 1u << 0 : 0u) |
                                  (type == LightType.Point       ? 1u << 1 : 0u) |
                                  (type == LightType.Spot        ? 1u << 2 : 0u) |
                                  (type == LightType.Area        ? 1u << 6 : 0u),
            };
        }
        UploadIfChanged(cmd, Renderer_Buffer.LightParameters, MemoryMarshal.AsBytes(s_light_parameters.AsSpan(0, (int)System.Math.Max(s_light_count, 1u))), ref s_light_bytes_uploaded);
    }

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
    internal static uint WriteDrawData(Matrix4x4 transform, Matrix4x4 transform_previous, uint material_index, uint is_transparent)
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
        s_transform_drawn.Clear();
        s_draw_calls.Clear();
        s_materials.Clear();
        s_material_bytes_uploaded = s_light_bytes_uploaded = null;
        s_bound_vertex_id = s_bound_index_id = s_bound_instance_id = 0;
        s_draw_data_count = 0;
        s_light_count     = 0;
    }
}
