using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Phalanx;

// port of source/geometry/Mesh.h/.cpp (Spartan 4c07baa) - geometry/LOD part only
[Flags]
public enum MeshFlags : uint
{
    ImportRemoveRedundantData       = 1 << 0,
    ImportLights                    = 1 << 1,
    ImportCombineMeshes             = 1 << 2,
    ImportGenerateSmoothNormals     = 1 << 3,
    PostProcessNormalizeScale       = 1 << 4,
    PostProcessOptimize             = 1 << 5,
    PostProcessGenerateLods         = 1 << 6,
    PostProcessPreserveTerrainEdges = 1 << 7,
    PostProcessPreserveLod0         = 1 << 8, // keep authored detail, optimize layout without reducing geometry
    PostProcessSkipCache            = 1 << 9, // the owning generator caches the complete prepared mesh
}

public enum MeshType
{
    Cube,
    Quad,
    Sphere,
    Cylinder,
    Cone,
    Max
}

public struct MeshLod
{
    public uint vertex_offset; // starting offset in m_vertices
    public uint vertex_count;  // number of vertices for this LOD
    public uint index_offset;  // starting offset in m_indices
    public uint index_count;   // number of indices for this LOD
    public BoundingBox aabb;   // bounding box of this LOD
}

// class (not struct): it owns a list, and a struct copy out of List<SubMesh> would hide writes
public sealed class SubMesh
{
    public readonly List<MeshLod> lods = new List<MeshLod>(Mesh.MeshLodCount); // lod levels for this sub-mesh
}

[Serializable]
public class Mesh : IResource, IDisposable
{
    public const int MeshLodCount = 5;
    public const float MeshLodMinReduction = 0.9f; // a lod must be at least 10% cheaper than the one above it

    private static readonly uint s_vertex_size = (uint)Unsafe.SizeOf<RHI_Vertex_PosTexNorTan>();

    // screen fraction at which each lod becomes active, the simplification targets derive from these
    private static readonly float[] s_screen_thresholds =
    {
        0.05f,  // lod0: object covers >= 5% of screen height
        0.025f, // lod1: >= 2.5%
        0.012f, // lod2: >= 1.2%
        0.006f, // lod3: >= 0.6%
        0.003f  // lod4: >= 0.3%
    };

    // geometry
    private readonly List<RHI_Vertex_PosTexNorTan> m_vertices = new List<RHI_Vertex_PosTexNorTan>(); // all vertices of a model file
    private readonly List<uint> m_indices    = new List<uint>();    // all indices of a model file (local to each lod)
    private readonly List<SubMesh> m_sub_meshes = new List<SubMesh>(); // tracks sub-meshes and lods within the above lists

    // global geometry buffer offsets (base offsets into the shared vertex/index buffers)
    private uint m_global_vertex_offset = 0;
    private uint m_global_index_offset  = 0;

    // acceleration structures
    private readonly List<RHI_AccelerationStructure> m_blas = new List<RHI_AccelerationStructure>(); // one blas per sub-mesh

    // misc
    private readonly object m_mutex = new object();
    private Entity? m_root_entity = null;
    private MeshType m_type = MeshType.Max;
    private Skeleton? m_skeleton;
    private SkeletalMeshBinding? m_skeletal_mesh_binding;
    private readonly List<AnimationClip> m_animation_clips = new List<AnimationClip>();

    public Mesh()
    {
        m_resource_type = ResourceType.Mesh;
    }

    public void Dispose()
    {
        foreach (RHI_AccelerationStructure blas in m_blas)
            blas?.Dispose();
        m_blas.Clear();
        // TODO(port): release the gpu vertex/index buffer ranges once the rhi buffers exist
        GC.SuppressFinalize(this);
    }

    // geometry
    public void Clear()
    {
        lock (m_mutex)
        {
            m_indices.Clear();
            m_indices.TrimExcess();
            m_vertices.Clear();
            m_vertices.TrimExcess();
        }
    }

    public int GetSubMeshCount() { lock (m_mutex) return m_sub_meshes.Count; }
    public int GetLodCount(int sub_mesh_index)
    {
        lock (m_mutex)
            return (uint)sub_mesh_index < (uint)m_sub_meshes.Count ? m_sub_meshes[sub_mesh_index].lods.Count : 0;
    }
    public int GetVertexCount() { lock (m_mutex) return m_vertices.Count; }
    public int GetIndexCount()  { lock (m_mutex) return m_indices.Count; }

    public void GetGeometry(int sub_mesh_index, List<uint>? indices, List<RHI_Vertex_PosTexNorTan>? vertices)
    {
        Helpers.PLXAssertMsg(indices != null || vertices != null, "Indices and vertices lists can't both be null");

        if (!GetGeometryLod(sub_mesh_index, 0, indices, vertices))
            Log.LogError($"GetGeometry: sub-mesh {sub_mesh_index} has no LOD 0");
    }

    // copies one lod's geometry out of the mesh buffers (indices are local to the lod's vertex range)
    public bool GetGeometryLod(int sub_mesh_index, int lod_index, List<uint>? indices, List<RHI_Vertex_PosTexNorTan>? vertices)
    {
        // lock so concurrent AddGeometry/AddLod calls can't reallocate the buffers while we read them
        lock (m_mutex)
        {
            if ((uint)sub_mesh_index >= (uint)m_sub_meshes.Count)
                return false;

            List<MeshLod> lods = m_sub_meshes[sub_mesh_index].lods;
            if ((uint)lod_index >= (uint)lods.Count)
                return false;

            MeshLod lod = lods[lod_index];

            if (indices != null)
            {
                indices.Clear();
                indices.AddRange(CollectionsMarshal.AsSpan(m_indices).Slice((int)lod.index_offset, (int)lod.index_count));
            }

            if (vertices != null)
            {
                vertices.Clear();
                vertices.AddRange(CollectionsMarshal.AsSpan(m_vertices).Slice((int)lod.vertex_offset, (int)lod.vertex_count));
            }

            return true;
        }
    }

    public ulong GetMemoryUsage()
    {
        lock (m_mutex)
            return (ulong)m_indices.Count * sizeof(uint) + (ulong)m_vertices.Count * s_vertex_size;
    }

    public void AddLod(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices, int sub_mesh_index)
    {
        BoundingBox aabb = new BoundingBox(vertices); // computed outside the lock

        lock (m_mutex)
        {
            MeshLod lod = new MeshLod
            {
                vertex_offset = (uint)m_vertices.Count,
                vertex_count  = (uint)vertices.Count,
                index_offset  = (uint)m_indices.Count,
                index_count   = (uint)indices.Count,
                aabb          = aabb
            };

            m_vertices.AddRange(vertices);
            m_indices.AddRange(indices);
            m_sub_meshes[sub_mesh_index].lods.Add(lod);
        }
    }

    // creates a new sub-mesh, returns its index
    public int AddGeometry(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices, bool generate_lods)
    {
        int sub_mesh_index;
        lock (m_mutex)
        {
            sub_mesh_index = m_sub_meshes.Count;
            m_sub_meshes.Add(new SubMesh());
        }

        AddGeometry(vertices, indices, generate_lods, sub_mesh_index);
        return sub_mesh_index;
    }

    // fills a sub-mesh slot that already exists; vertices/indices are modified in place when optimizing (as in Spartan)
    public void AddGeometry(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices, bool generate_lods, int sub_mesh_index, bool preserve_lod0 = false)
    {
        Helpers.PLXAssert(sub_mesh_index < m_sub_meshes.Count);

        // lod 0: original geometry
        {
            // never reorder skinned verts, bone weights are authored against import order
            bool is_skinned = m_skeleton != null;
            if (!is_skinned && HasFlag(MeshFlags.PostProcessOptimize))
            {
                if (preserve_lod0 || HasFlag(MeshFlags.PostProcessPreserveLod0))
                {
                    // authored game-ready props must not be reduced before distance lod selection
                    GeometryProcessing.WeldAndOptimize(vertices, indices);
                }
                else
                {
                    GeometryProcessing.Optimize(vertices, indices);
                }
            }

            AddLod(vertices, indices, sub_mesh_index);
        }

        if (!generate_lods || !HasFlag(MeshFlags.PostProcessGenerateLods))
            return;

        int original_index_count = indices.Count;
        bool preserve_uvs   = true;
        bool preserve_edges = HasFlag(MeshFlags.PostProcessPreserveTerrainEdges);

        // two working buffers reused across levels: lod = copy(prev), simplify lod, then swap
        // (TODO(port): generated_cache lod caching and thin_foliage_cards)
        List<RHI_Vertex_PosTexNorTan> prev_vertices = new List<RHI_Vertex_PosTexNorTan>(vertices);
        List<uint> prev_indices                     = new List<uint>(indices);
        List<RHI_Vertex_PosTexNorTan> lod_vertices  = new List<RHI_Vertex_PosTexNorTan>(vertices.Count);
        List<uint> lod_indices                      = new List<uint>(indices.Count);

        for (int lod_level = 1; lod_level < MeshLodCount; lod_level++)
        {
            // geometry too simple to benefit from further simplification
            if (prev_indices.Count <= 64)
                break;

            lod_vertices.Clear();
            lod_vertices.AddRange(CollectionsMarshal.AsSpan(prev_vertices));
            lod_indices.Clear();
            lod_indices.AddRange(CollectionsMarshal.AsSpan(prev_indices));

            // target relative to the original mesh, about 2x reduction per lod to match the 2x screen size steps
            float target_ratio     = s_screen_thresholds[lod_level] / s_screen_thresholds[0];
            int target_index_count = System.Math.Max(64, (int)(original_index_count * target_ratio));

            // disconnected branches/cards need component pruning, terrain borders stay untouched
            GeometryProcessing.Simplify(lod_indices, lod_vertices, target_index_count, preserve_uvs, preserve_edges, !preserve_edges);

            // stop unless this level is meaningfully cheaper than the one above it
            if (lod_indices.Count >= (int)(prev_indices.Count * MeshLodMinReduction))
                break;

            AddLod(lod_vertices, lod_indices, sub_mesh_index);

            (prev_vertices, lod_vertices) = (lod_vertices, prev_vertices);
            (prev_indices, lod_indices)   = (lod_indices, prev_indices);
        }
    }

    public bool HasFlag(MeshFlags flag) { return (m_flags & (uint)flag) != 0; }
    public void SetFlag(MeshFlags flag, bool enabled = true) { SetFlag((uint)flag, enabled); }

    public MeshType GetMeshType() { return m_type; }
    public void SetMeshType(MeshType type) { m_type = type; }
    public uint GetGlobalVertexOffset() { return m_global_vertex_offset; }
    public uint GetGlobalIndexOffset()  { return m_global_index_offset; }
    public void SetGlobalOffsets(uint vertex_offset, uint index_offset) { m_global_vertex_offset = vertex_offset; m_global_index_offset = index_offset; }
    public Entity? GetRootEntity() { return m_root_entity; }
    public void SetRootEntity(Entity? entity) { m_root_entity = entity; }
}
