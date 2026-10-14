// The scene side of SpartanEngine source/rendering/Renderer.cpp @ 4c07baa the geometry pass needs: the view camera
// (GetViewCamera), the bindless geometry tables (:1413-1438), the material and light uploads of
// TickUploadBindlessDependencies (UpdateMaterials :2995-3060, UpdateLights :3270-3310) and WriteDrawData (:2894).
// Deviations:
// - Spartan walks the World's Render components. Phalanx.Core's Render component can't reference the Mesh (it lives
//   in Phalanx.Rendering), so the renderer keeps its own list: AddRenderable(mesh, entity, material) and AddLight.
// - SetViewCamera overrides World.GetCamera() (spartan: the secondary render root / editor view camera).
// - Materials upload colour, roughness, metalness, normal, height and the emissive scale; no textures yet (their slots
//   keep the checkerboard). Lights upload colour, intensity (the watt approximation, Phalanx has no radiometric
//   conversion yet), position, direction, range, angle and the type flags; no shadows, slices or ies.
// - Material and light tables are rewritten only when their contents change, at offset 0 (spartan: the same).
// - Every renderable draws lod 0 of each sub-mesh, there is no culling or lod selection yet.
using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using static Phalanx.Rendering.Renderer_Definitions;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    internal sealed class Renderable
    {
        public required Mesh mesh;
        public required Entity entity;
        public required Material material;
        public Matrix4x4 transform_previous;
        public bool has_previous;
    }

    private static readonly List<Renderable> s_renderables = new();
    private static readonly List<Light> s_lights = new();
    private static readonly List<Material> s_materials = new();
    private static readonly MaterialParameters[] s_material_parameters = new MaterialParameters[RHI_Definitions.rhi_max_array_size];
    private static readonly LightParameters[] s_light_parameters = new LightParameters[RHI_Definitions.rhi_max_array_size];
    private static byte[]? s_material_bytes_uploaded, s_light_bytes_uploaded;
    private static uint s_light_count;
    private static uint s_draw_data_count;
    private static ulong s_bound_vertex_id, s_bound_index_id, s_bound_instance_id;
    private static Camera? s_view_camera;

    /// <summary>The camera the frame renders from: the one set with <see cref="SetViewCamera"/>, else the world's.</summary>
    public static Camera? GetViewCamera() => s_view_camera ?? Phalanx.World.World.GetCamera();
    public static void SetViewCamera(Camera? camera) => s_view_camera = camera;

    /// <summary>Draws <paramref name="mesh"/> with <paramref name="entity"/>'s transform; uploads it to the geometry buffer.</summary>
    public static void AddRenderable(Mesh mesh, Entity entity, Material material)
    {
        mesh.CreateGpuBuffers();
        s_renderables.Add(new Renderable { mesh = mesh, entity = entity, material = material });
    }

    public static void AddLight(Light light) => s_lights.Add(light);

    public static void ClearScene()
    {
        s_renderables.Clear();
        s_lights.Clear();
        s_view_camera = null;
    }

    internal static IReadOnlyList<Renderable> GetRenderables() => s_renderables;
    internal static uint GetMaterialIndex(Material material) => (uint)System.Math.Max(s_materials.IndexOf(material), 0);
    internal static uint GetLightCount() => s_light_count;

    // TickUploadBindlessDependencies, the part that exists: geometry, materials, lights
    private static void TickUploadBindlessDependencies(RHI_CommandList cmd)
    {
        GeometryBuffer.BuildIfDirty();
        UpdateBindlessGeometry();
        UpdateMaterials(cmd);
        UpdateLights(cmd);
        s_draw_data_count = 0;
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

    private static void UpdateMaterials(RHI_CommandList cmd)
    {
        s_materials.Clear();
        foreach (Renderable r in s_renderables)
        {
            if (s_materials.Contains(r.material))
                continue;
            if (s_materials.Count >= RHI_Definitions.rhi_max_array_size)
            {
                Log.LogError("material bindless capacity exceeded, overflowing materials will use index 0");
                break;
            }
            Material m = r.material;
            ref MaterialParameters p = ref s_material_parameters[s_materials.Count];
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
            };
            s_materials.Add(m);
        }
        UploadIfChanged(cmd, Renderer_Buffer.MaterialParameters, MemoryMarshal.AsBytes(s_material_parameters.AsSpan(0, System.Math.Max(s_materials.Count, 1))), ref s_material_bytes_uploaded);
    }

    private static void UpdateLights(RHI_CommandList cmd)
    {
        s_light_count = 0;
        foreach (Light light in s_lights)
        {
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
        foreach (Renderable r in s_renderables)
            r.mesh.ReleaseGpuBuffers();
        ClearScene();
        s_material_bytes_uploaded = s_light_bytes_uploaded = null;
        s_bound_vertex_id = s_bound_index_id = s_bound_instance_id = 0;
        s_draw_data_count = 0;
        s_light_count     = 0;
    }
}
