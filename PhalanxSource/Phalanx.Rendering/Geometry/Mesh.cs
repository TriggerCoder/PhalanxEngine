using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Phalanx.Math;
using Phalanx.Geometry;
using Phalanx.World;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Rendering;

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

public struct MeshLod
{
    public uint vertex_offset; // starting offset in m_vertices
    public uint vertex_count;  // number of vertices for this LOD
    public uint index_offset;  // starting offset in m_indices
    public uint index_count;   // number of indices for this LOD
    public BoundingBox aabb;   // bounding box of this LOD
}

// class (not struct): it owns a list, and a struct copy out of List<SubMesh> would hide writes
public sealed class MeshImpostor
{
    public MeshLod lod;                            // the card, it lives in the regular geometry streams
    public uint[] texels = Array.Empty<uint>();    // two words per layer (see GeometryProcessing.BakeImpostor)
    public uint texel_offset = 0;                  // into the global impostor texel buffer (set at gpu upload)
    public uint resolution   = Mesh.MeshImpostorResolution;
    public Vector3 center    = Vector3.Zero;
    public float radius      = 0.0f;
    public Vector2 uv_min    = Vector2.Zero;
    public Vector2 uv_scale  = Vector2.One;

    // DrawData.impostor_layout, as packed by Renderer.cpp (frames | resolution << 8 | layers << 24)
    public uint GetLayout() { return Mesh.MeshImpostorFrames | (resolution << 8) | (Mesh.MeshImpostorLayers << 24); }

    // the impostor part of Spartan's indirect draw setup (Renderer.cpp, lod == mesh_lod_count): the card turns to face
    // the camera, so winding and meshlet cones mean nothing for it (flag bits 3, 4), and its silhouette is cut per texel
    // so it takes the alpha tested path; bit 14 is draw_flag_impostor in the shaders
    public void WriteDrawData(ref DrawData draw_data)
    {
        draw_data.flags                |= (1u << 14) | 8u | 16u;
        draw_data.impostor_texel_offset = texel_offset;
        draw_data.impostor_layout       = GetLayout();
        draw_data.impostor_center       = center;
        draw_data.impostor_radius       = radius;
        draw_data.impostor_uv_min       = uv_min;
        draw_data.impostor_uv_scale     = uv_scale;
    }
}

public sealed class SubMesh
{
    public readonly List<MeshLod> lods = new List<MeshLod>(Mesh.MeshLodCount); // lod levels for this sub-mesh
    // not one of the lods, serialization and the cpu lod pick never see it, the gpu lod pick addresses it as index lods.Count
    public MeshImpostor? impostor;
}

[Serializable]
public class Mesh : IResource, IDisposable
{
    public const int MeshLodCount = 5;
    public const float MeshLodMinReduction = 0.9f; // a lod must be at least 10% cheaper than the one above it

    // foliage impostors (Mesh.h): an atlas is frames^2 * resolution^2 * layers * 8 bytes, 4 MB at 64 px and 16 MB at 128 px
    public const uint  MeshImpostorFrames        = 8;
    public const uint  MeshImpostorResolution    = 64;
    public const uint  MeshImpostorLayers        = 2;
    // every crown swaps at the same screen size, but trees carry the skyline, so they get the sharper frames
    public const uint  MeshImpostorResolutionLarge = 128;
    public const float MeshImpostorLargeRadius     = 3.0f;

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
    public int AddGeometry(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices, bool generate_lods, bool preserve_lod0 = false, bool foliage_cards = false, bool impostor = false)
    {
        int sub_mesh_index;
        lock (m_mutex)
        {
            sub_mesh_index = m_sub_meshes.Count;
            m_sub_meshes.Add(new SubMesh());
        }

        AddGeometry(vertices, indices, generate_lods, sub_mesh_index, preserve_lod0, foliage_cards, impostor);
        return sub_mesh_index;
    }

    // fills a sub-mesh slot that already exists; vertices/indices are modified in place when optimizing (as in Spartan)
    public void AddGeometry(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices, bool generate_lods, int sub_mesh_index, bool preserve_lod0 = false, bool foliage_cards = false, bool impostor = false)
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
            // reuse the exact simplification for repeated imports: hash the actual input geometry, target and policy
            // so asset edits and terrain-border protection can't reuse an incompatible result
            GeneratedCache.Hash lod_hash = new GeneratedCache.Hash();
            lod_hash.Add(GeneratedCache.VertexFormatVersion); // phalanx: packed vertex bytes
            lod_hash.Add(foliage_cards ? 2u : 1u); // simplification policy/cache version
            lod_hash.Add(lod_vertices);
            lod_hash.Add(lod_indices);
            lod_hash.Add((ulong)target_index_count);
            lod_hash.Add(preserve_uvs);
            lod_hash.Add(preserve_edges);
            string? lod_path = GeneratedCache.Path("lods", lod_hash.value);
            bool cache_lods  = !HasFlag(MeshFlags.PostProcessSkipCache);
            if (!cache_lods || !GeneratedCache.Load(lod_path, lod_hash.value, lod_vertices, lod_indices))
            {
                // leaf cards (imported from "_foliage" materials) are thinned as whole cards instead of simplified
                if (foliage_cards)
                    GeometryProcessing.ThinFoliageCards(lod_indices, lod_vertices, target_index_count);
                else
                    GeometryProcessing.Simplify(lod_indices, lod_vertices, target_index_count, preserve_uvs, preserve_edges, !preserve_edges);
                if (cache_lods)
                    GeneratedCache.Save<RHI_Vertex_PosTexNorTan, uint>(lod_path, lod_hash.value, CollectionsMarshal.AsSpan(lod_vertices), CollectionsMarshal.AsSpan(lod_indices));
            }

            // stop unless this level is meaningfully cheaper than the one above it
            if (lod_indices.Count >= (int)(prev_indices.Count * MeshLodMinReduction))
                break;

            AddLod(lod_vertices, lod_indices, sub_mesh_index);

            (prev_vertices, lod_vertices) = (lod_vertices, prev_vertices);
            (prev_indices, lod_indices)   = (lod_indices, prev_indices);
        }

        // the impostor is baked from lod 0 (the geometry as it was added), after the lod chain, like Spartan
        if ((foliage_cards || impostor) && m_skeleton == null)
            AddImpostor(vertices, indices, sub_mesh_index);
    }

    // makes sure sub-mesh slots [0, count) exist, for callers that fill slots by index (the model importer)
    public void ReserveSubMeshes(int count)
    {
        lock (m_mutex)
        {
            while (m_sub_meshes.Count < count)
                m_sub_meshes.Add(new SubMesh());
        }
    }

    public Skeleton? GetSkeleton() { return m_skeleton; }

    public MeshImpostor? GetImpostor(int sub_mesh_index)
    {
        lock (m_mutex)
            return (uint)sub_mesh_index < (uint)m_sub_meshes.Count ? m_sub_meshes[sub_mesh_index].impostor : null;
    }

    public void AddImpostor(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices, int sub_mesh_index)
    {
        if (vertices.Count == 0 || indices.Count < 3)
            return;

        MeshImpostor impostor = new MeshImpostor();

        // the card spans the bounding sphere so every frame fits whichever way the crown is turned
        BoundingBox aabb = new BoundingBox(vertices);
        impostor.center  = aabb.GetCenter();
        Vector2 uv_lo = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 uv_hi = new Vector2(-float.MaxValue, -float.MaxValue);
        foreach (ref readonly RHI_Vertex_PosTexNorTan vertex in CollectionsMarshal.AsSpan(vertices))
        {
            impostor.radius = System.Math.Max(impostor.radius, (vertex.pos - impostor.center).Length());
            uv_lo = Vector2.Min(uv_lo, vertex.GetUv());
            uv_hi = Vector2.Max(uv_hi, vertex.GetUv());
        }
        if (impostor.radius <= 0.0f)
            return;

        impostor.uv_min     = uv_lo;
        impostor.uv_scale   = new Vector2(System.Math.Max(uv_hi.X - uv_lo.X, 1e-6f), System.Math.Max(uv_hi.Y - uv_lo.Y, 1e-6f));
        impostor.resolution = impostor.radius >= MeshImpostorLargeRadius ? MeshImpostorResolutionLarge : MeshImpostorResolution;

        GeneratedCache.Hash hash = new GeneratedCache.Hash();
        hash.Add(GeneratedCache.VertexFormatVersion); // phalanx: packed vertex bytes
        hash.Add(1u); // impostor bake version
        hash.Add(MeshImpostorFrames);
        hash.Add(impostor.resolution);
        hash.Add(MeshImpostorLayers);
        hash.Add(vertices);
        hash.Add(indices);
        string? path = GeneratedCache.Path("impostors", hash.value);
        bool cache   = !HasFlag(MeshFlags.PostProcessSkipCache);
        long expected_words = (long)MeshImpostorFrames * MeshImpostorFrames * impostor.resolution * impostor.resolution * MeshImpostorLayers * 2;
        List<uint> cached = new List<uint>();
        if (cache && GeneratedCache.Load(path, hash.value, cached) && cached.Count == expected_words)
        {
            impostor.texels = cached.ToArray();
        }
        else
        {
            impostor.texels = GeometryProcessing.BakeImpostor(
                CollectionsMarshal.AsSpan(vertices),
                CollectionsMarshal.AsSpan(indices),
                MeshImpostorFrames,
                impostor.resolution,
                MeshImpostorLayers,
                impostor.center,
                impostor.radius,
                impostor.uv_min,
                impostor.uv_scale);
            if (cache)
                GeneratedCache.Save<uint>(path, hash.value, impostor.texels);
        }

        // the corners ride in the uv, the vertex stage turns the card toward the camera, the positions only have to
        // give the lod aabb (and, once ported, the meshlet sphere) a volume that holds the card in any orientation
        float r   = impostor.radius;
        Vector3 c = impostor.center;
        Vector3 forward = MathEx.Vector3.Forward;
        Vector3 right   = MathEx.Vector3.Right;
        List<RHI_Vertex_PosTexNorTan> card = new List<RHI_Vertex_PosTexNorTan>(4)
        {
            new RHI_Vertex_PosTexNorTan(c + new Vector3(-r, -r, -r), new Vector2(0.0f, 0.0f), forward, right),
            new RHI_Vertex_PosTexNorTan(c + new Vector3( r, -r,  r), new Vector2(1.0f, 0.0f), forward, right),
            new RHI_Vertex_PosTexNorTan(c + new Vector3( r,  r, -r), new Vector2(1.0f, 1.0f), forward, right),
            new RHI_Vertex_PosTexNorTan(c + new Vector3(-r,  r,  r), new Vector2(0.0f, 1.0f), forward, right),
        };
        uint[] card_indices = { 0, 2, 1, 0, 3, 2 };

        // TODO(port): geometry_processing::build_meshlets(card, card_indices, ...) + offset_meshlet_unique_ranges, the
        // card's meshlet goes into m_meshlets/m_meshlet_vertices/m_meshlet_micro_indices and lod.meshlet_offset/count;
        // its lod aabb is the aabb of the 4 card vertices, which is what build_meshlets returns
        BoundingBox card_aabb = new BoundingBox(card);

        lock (m_mutex)
        {
            impostor.lod = new MeshLod
            {
                vertex_offset = (uint)m_vertices.Count,
                vertex_count  = (uint)card.Count,
                index_offset  = (uint)m_indices.Count,
                index_count   = (uint)card_indices.Length,
                aabb          = card_aabb
            };
            m_vertices.AddRange(card);
            m_indices.AddRange(card_indices);
            m_sub_meshes[sub_mesh_index].impostor = impostor;
        }
    }

    // TODO(port): Mesh::PrepareForGpu uploads every sub-mesh impostor atlas with
    //   impostor.texel_offset = GeometryBuffer::AppendImpostorTexels(impostor.texels.data(), (uint32_t)impostor.texels.size());
    // GeometryBuffer keeps one growable StructuredBuffer<uint> ("geometry_buffer_impostor_texels", bound at t76 /
    // Renderer_BindingsSrv::impostor_texels = 76) and returns the base word offset of the appended atlas; the renderer then
    // calls MeshImpostor.WriteDrawData for the extra gpu lod (index lods.Count) of instanced foliage, gated by
    // cvar_foliage_impostors and mesh_lod_count < 7 (the lod count has to fit three DrawData.flags bits, flag bit 15)

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
