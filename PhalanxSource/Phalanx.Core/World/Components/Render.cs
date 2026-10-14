// Port of SpartanEngine source/world/components/Render.cpp/.h @ 4c07baa, the part the renderer needs: the mesh and
// sub-mesh (SetMesh :551, SetMesh(MeshType) :581, ClearMesh), the material (SetMaterial, SetDefaultMaterial :649 with
// the deferred default of Tick :391), the lod geometry getters (:660-700), GetGeometry and the world bounding box
// (UpdateAabb :978, non-instanced).
// Deviations:
// - The mesh is an IMesh (Phalanx.Geometry): Mesh lives in Phalanx.Rendering (see IMesh.cs). The standard meshes and
//   the standard material come from RenderView, where the renderer publishes them (spartan: Renderer::GetStandardMesh
//   / GetStandardMaterial).
// - No instancing, decals, material overrides, uv overrides, acceleration structures, lod selection, frustum or
//   distance culling (every renderable is visible, lod 0), serialization or owned meshes yet.
using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Math;

namespace Phalanx.World;

// Render.h:29-31, the flags ported so far
[Flags]
public enum RenderFlags : uint
{
    CastsShadows = 1U << 0,
}

public class Render : Component
{
    private RenderFlags m_flags = RenderFlags.CastsShadows; // Render.h:66
    public bool HasFlag(RenderFlags flag) => (m_flags & flag) != 0;
    public void SetFlag(RenderFlags flag, bool enable = true) { m_flags = enable ? m_flags | flag : m_flags & ~flag; }

    private IMesh? m_mesh;
    private int m_sub_mesh_index;
    private Material? m_material;
    private bool m_material_default;
    private bool m_needs_default_material;
    private BoundingBox m_bounding_box      = BoundingBox.Unit;
    private BoundingBox m_bounding_box_mesh = BoundingBox.Unit;
    private bool m_bounding_box_dirty       = true;
    private Matrix4x4 m_transform_previous  = Matrix4x4.Identity;

    public Render(Entity entity) : base(entity) { }

    public override void Tick()
    {
        // deferred default material assignment (renderer may not be ready during load)
        if (m_needs_default_material && RenderView.GetStandardMaterial() != null)
        {
            SetDefaultMaterial();
            m_needs_default_material = false;
        }
        UpdateAabb();
    }

    // mesh
    public void SetMesh(IMesh? mesh, int sub_mesh_index = 0)
    {
        if (mesh == null)
        {
            Log.LogWarning("Render::SetMesh called with null mesh");
            return;
        }
        m_mesh           = mesh;
        m_sub_mesh_index = sub_mesh_index;
        if (GetLodCount() > 0)
        {
            m_bounding_box_mesh  = GetLodAabb(0);
            m_bounding_box_dirty = true;
        }
        Tick(); // update bounding boxes
    }
    public void SetMesh(MeshType type) { SetMesh(RenderView.GetStandardMesh(type)); }
    public void ClearMesh()
    {
        m_mesh               = null;
        m_sub_mesh_index     = 0;
        m_bounding_box_mesh  = BoundingBox.Unit;
        m_bounding_box_dirty = true;
    }
    public IMesh? GetMesh() { return m_mesh; }
    public int GetSubMeshIndex() { return m_sub_mesh_index; }
    public void GetGeometry(List<uint> indices, List<RHI_Vertex_PosTexNorTan> vertices)
    {
        // a null mesh is a valid transient state, procedural meshes are generated after load
        m_mesh?.GetGeometry(m_sub_mesh_index, indices, vertices);
    }

    public int GetLodCount() { return m_mesh != null && m_sub_mesh_index < m_mesh.GetSubMeshCount() ? m_mesh.GetLodCount(m_sub_mesh_index) : 0; }
    private MeshLod? GetMeshLod(int lod) { return lod < GetLodCount() ? m_mesh!.GetLod(m_sub_mesh_index, lod) : null; }
    public uint GetIndexOffset(int lod = 0)  { return GetMeshLod(lod) is MeshLod l ? m_mesh!.GetGlobalIndexOffset() + l.index_offset : 0; }
    public int GetIndexCount(int lod = 0)    { return GetMeshLod(lod) is MeshLod l ? (int)l.index_count : 0; }
    public uint GetVertexOffset(int lod = 0) { return GetMeshLod(lod) is MeshLod l ? m_mesh!.GetGlobalVertexOffset() + l.vertex_offset : 0; }
    public int GetVertexCount(int lod = 0)   { return GetMeshLod(lod) is MeshLod l ? (int)l.vertex_count : 0; }
    public BoundingBox GetLodAabb(int lod = 0) { return GetMeshLod(lod) is MeshLod l ? l.aabb : BoundingBox.Unit; }

    // bounding box
    public BoundingBox GetBoundingBox() { return m_bounding_box; }
    public BoundingBox GetBoundingBoxMesh() { return m_bounding_box_mesh; }

    private void UpdateAabb()
    {
        Entity? entity = GetEntity();
        Matrix4x4 transform = entity != null && entity.GetActive() ? entity.GetMatrix() : Matrix4x4.Identity;
        if (m_bounding_box_dirty || m_transform_previous != transform)
        {
            m_bounding_box       = m_bounding_box_mesh * transform;
            m_transform_previous = transform;
            m_bounding_box_dirty = false;
        }
    }

    // material
    public void SetMaterial(Material? material)
    {
        Helpers.PLXAssert(material != null);
        m_material_default = false;
        m_material         = material;

        // pack textures, generate mips, compress, upload to GPU
        if (material!.GetResourceState() == ResourceState.Max)
            material.PrepareForGpu();

        // use the cached mesh bounds
        if (m_mesh != null && GetLodCount() > 0)
        {
            Vector3 size = GetLodAabb(0).GetSize();
            material.SetProperty(MaterialProperty.WorldWidth, size.X);
            material.SetProperty(MaterialProperty.WorldHeight, size.Y);
        }
    }
    public void SetDefaultMaterial()
    {
        Material? standard = RenderView.GetStandardMaterial();
        if (standard == null)
        {
            m_needs_default_material = true; // Tick assigns it once the renderer published it
            return;
        }
        SetMaterial(standard);
        m_material_default = true;
    }
    public Material? GetMaterial() { return m_material; }
    public bool IsUsingDefaultMaterial() { return m_material_default; }

    // the per-draw uv state (Render.h:170-240): spartan resolves a per-render MaterialOverride first and falls back to
    // the material; Phalanx has no MaterialOverride yet, so these are the material's values (defaults with none)
    private float ResolveUv(MaterialProperty property, float fallback) => m_material != null ? m_material.GetProperty(property) : fallback;
    public float ResolveUvTilingX()    => ResolveUv(MaterialProperty.TextureTilingX, 1.0f);
    public float ResolveUvTilingY()    => ResolveUv(MaterialProperty.TextureTilingY, 1.0f);
    public float ResolveUvOffsetX()    => ResolveUv(MaterialProperty.TextureOffsetX, 0.0f);
    public float ResolveUvOffsetY()    => ResolveUv(MaterialProperty.TextureOffsetY, 0.0f);
    public float ResolveUvRotation()   => ResolveUv(MaterialProperty.TextureRotation, 0.0f);
    public float ResolveUvInvertX()    => ResolveUv(MaterialProperty.TextureInvertX, 0.0f);
    public float ResolveUvInvertY()    => ResolveUv(MaterialProperty.TextureInvertY, 0.0f);
    public float ResolveUvWorldSpace() => ResolveUv(MaterialProperty.WorldSpaceUv, 0.0f);
}
