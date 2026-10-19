// The instancing batches of store entities: one per mesh, sub-mesh and material, interned. Every renderable store
// entity of a batch is drawn by one instanced draw per view (Renderer.Instancing.cs). Spartan @ 4c07baa instances
// inside one Render component (Render::SetInstances, Render.cpp:821, a static instance list per renderable); Phalanx
// batches separate entities that share a mesh and material instead, see the deviations in EntityStore.cs.
using Phalanx.Geometry;
using Phalanx.Math;
using System.Numerics;

namespace Phalanx.World;

/// <summary>A mesh, sub-mesh and material that store entities are drawn with, from <see cref="RenderBatches.Get"/>.
/// default is no batch (the entity isn't drawn).</summary>
public readonly struct RenderBatch : IEquatable<RenderBatch>
{
    internal readonly uint Id; // 0 is no batch

    internal RenderBatch(uint id) { Id = id; }

    public bool IsValid => Id != 0;
    public IMesh? Mesh => IsValid ? RenderBatches.GetInfo(Id).Mesh : null;
    public int SubMeshIndex => IsValid ? RenderBatches.GetInfo(Id).SubMesh : 0;
    public Material? Material => IsValid ? RenderBatches.GetInfo(Id).Material : null;

    public bool Equals(RenderBatch other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is RenderBatch other && Equals(other);
    public override int GetHashCode() => (int)Id;
    public static bool operator ==(RenderBatch a, RenderBatch b) => a.Id == b.Id;
    public static bool operator !=(RenderBatch a, RenderBatch b) => a.Id != b.Id;
}

public static class RenderBatches
{
    internal sealed class Info
    {
        public uint Id;
        public IMesh? Mesh;
        public int SubMesh;
        public Material? Material; // null draws with the standard material
        public Vector3 LocalCenter;
        public Vector3 LocalExtent;
    }

    private static readonly List<Info> s_infos = new() { new Info() }; // 0: no batch
    private static readonly Dictionary<(IMesh?, int, Material?), uint> s_ids = new();
    private static readonly object s_lock = new();

    /// <summary>The batch of <paramref name="mesh"/>'s sub-mesh drawn with <paramref name="material"/> (null: the standard
    /// material), the same one for the same three. Like <see cref="Render.SetMaterial"/> it prepares the material for the
    /// gpu and gives it the mesh's world size.</summary>
    public static RenderBatch Get(IMesh mesh, Material? material = null, int sub_mesh_index = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        BoundingBox local = BoundingBox.Unit;
        if (sub_mesh_index < mesh.GetSubMeshCount() && mesh.GetLodCount(sub_mesh_index) > 0)
            local = mesh.GetLod(sub_mesh_index, 0).aabb;
        if (material != null)
        {
            if (material.GetResourceState() == ResourceState.Max)
                material.PrepareForGpu();
            Vector3 size = local.GetSize();
            material.SetProperty(MaterialProperty.WorldWidth, size.X);
            material.SetProperty(MaterialProperty.WorldHeight, size.Y);
        }
        return Intern(mesh, sub_mesh_index, material, local);
    }

    /// <summary>The batch of the standard mesh <paramref name="type"/> (the renderer must be initialized, it publishes
    /// them).</summary>
    public static RenderBatch Get(MeshType type, Material? material = null)
    {
        IMesh mesh = RenderView.GetStandardMesh(type) ?? throw new InvalidOperationException($"The standard mesh {type} isn't available before the renderer is initialized");
        return Get(mesh, material);
    }

    // a batch with explicit local bounds and no mesh (tests: culling without a renderer)
    internal static RenderBatch GetForBounds(BoundingBox local, Material? material = null) => Intern(null, s_infos.Count, material, local);

    private static RenderBatch Intern(IMesh? mesh, int sub_mesh, Material? material, BoundingBox local)
    {
        lock (s_lock)
        {
            if (s_ids.TryGetValue((mesh, sub_mesh, material), out uint id))
                return new RenderBatch(id);
            id = (uint)s_infos.Count;
            s_infos.Add(new Info { Id = id, Mesh = mesh, SubMesh = sub_mesh, Material = material, LocalCenter = local.GetCenter(), LocalExtent = local.GetExtents() });
            s_ids.Add((mesh, sub_mesh, material), id);
            return new RenderBatch(id);
        }
    }

    internal static int Count { get { lock (s_lock) return s_infos.Count; } }
    internal static Info GetInfo(uint id) { lock (s_lock) return s_infos[(int)id]; }
}
