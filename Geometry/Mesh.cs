using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Phalanx;
public enum MeshFlags : uint
{
    ImportRemoveRedundantData       = 1 << 0,
    ImportLights                    = 1 << 1,
    ImportCombineMeshes             = 1 << 2,
    PostProcessNormalizeScale       = 1 << 3,
    PostProcessOptimize             = 1 << 4,
    PostProcessGenerateLods         = 1 << 5,
    PostProcessPreserveTerrainEdges = 1 << 6,
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
    public int vertex_offset; // starting offset in m_vertices
    public int vertex_count;  // number of vertices for this LOD
    public int index_offset;  // starting offset in m_indices
    public int index_count;   // number of indices for this LOD
    public BoundingBox aabb; // bounding box of this LOD
}

public struct SubMesh
{
    public List<MeshLod> lods; // list of LOD levels for this sub-mesh
}

[Serializable]
public class Mesh : IResource , IDisposable
{
    public void Dispose()
    {
        foreach (var blas in m_blas)
            blas?.Dispose();

        m_blas.Clear();
    }
    const uint mesh_lod_count = 5;
    static readonly int vertexSize = Marshal.SizeOf<RHI_Vertex_PosTexNorTan>();

    // geometry
    List<RHI_Vertex_PosTexNorTan> m_vertices = new List<RHI_Vertex_PosTexNorTan>(); // all vertices of a model file
    List<int> m_indices = new List<int>();                 // all indices of a model file
    List<SubMesh> m_sub_meshes = new List<SubMesh>();               // tracks sub-meshes and lods within the above vectors

    // global geometry buffer offsets (base offsets into the shared vertex/index buffers)
    uint m_global_vertex_offset = 0;
    uint m_global_index_offset = 0;

    // acceleration structures
    List<RHI_AccelerationStructure> m_blas = new List<RHI_AccelerationStructure>(); // one blas per sub-mesh

    // misc
    private readonly object m_mutex = new object();
    Entity? m_root_entity = null;
    MeshType m_type = MeshType.Max;
    Skeleton? m_skeleton;
    SkeletalMeshBinding? m_skeletal_mesh_binding;
    List<AnimationClip> m_animation_clips = new List<AnimationClip>();

    // geometry
    public void Clear()
    {
        m_indices.Clear();
        m_indices.TrimExcess();
        m_vertices.Clear();
        m_vertices.TrimExcess();
    }
    public void GetGeometry(int sub_mesh_index, List<uint> indices, List<RHI_Vertex_PosTexNorTan> vertices)
    {
        Helpers.PLXAssertMsg(indices != null || vertices != null, "Indices and vertices vectors can't both be null");

        // validate sub-mesh index
        if (sub_mesh_index >= m_sub_meshes.Count)
        {
            Log.LogError($"GetGeometry: sub_mesh_index {sub_mesh_index} out of bounds (mesh has  {m_sub_meshes.Count} sub-meshes)");
            return;
        }

        SubMesh sub_mesh = m_sub_meshes[sub_mesh_index];
        if (sub_mesh.lods.Count == 0)
        {
            Log.LogError($"GetGeometry: sub-mesh {sub_mesh_index} has no LODs");
            return;
        }

        MeshLod lod = sub_mesh.lods[0];

        if (indices != null)
        {
            Helpers.PLXAssertMsg(lod.index_count != 0, "Index count can't be 0");

            indices.Clear();
            for (int i = 0; i < lod.index_count; i++)
            {
                indices.Add(indices[(lod.index_offset + i)]);
            }
        }

        if (vertices != null)
        {
            Helpers.PLXAssertMsg(lod.vertex_count != 0, "Vertex count can't be 0");

            vertices.Clear();
            for (int i = 0; i < lod.vertex_count; i++)
            {
                vertices.Add(vertices[(lod.vertex_offset + i)]);
            }
        }
    }

    public uint GetMemoryUsage()
    {
        uint size = 0;

        size += (uint)(m_indices.Count * 4);                    // uint
        size += (uint)(m_vertices.Count * vertexSize);         // RHI_Vertex_PosTexNorTan

        return size;
    }
    public void AddLod(List<RHI_Vertex_PosTexNorTan> vertices, List<int> indices, int sub_mesh_index)
    {
        // build lod
        MeshLod lod = new MeshLod
        {
            vertex_offset = m_vertices.Count,
            vertex_count = vertices.Count,
            index_offset = m_indices.Count,
            index_count = indices.Count,
            aabb = new BoundingBox(vertices)
        };

        // append geometry
        lock (m_mutex)
        {
            // append geometry to mesh buffers
            m_vertices.AddRange(vertices);
            m_indices.AddRange(indices);

            // Add the new LOD to the target sub-mesh
            m_sub_meshes[sub_mesh_index].lods.Add(lod);
        }
    }

    public void AddGeometry(List<RHI_Vertex_PosTexNorTan> vertices, List<int> indices, bool generate_lods, out int sub_mesh_index)
    {
        // Create and register a new sub-mesh
        SubMesh sub_mesh = new SubMesh();
        int current_sub_mesh_index = m_sub_meshes.Count;
        m_sub_meshes.Add(sub_mesh);   // Add it early so AddLod() can write into it

        // lod 0: original geometry
        {
            // optimize original geometry if flagged
            if ((m_flags & (uint)(MeshFlags.PostProcessOptimize)) != 0)
            {
                geometry_processing.optimize(vertices, indices);
            }

            // add the original geometry as lod 0
            AddLod(vertices, indices, current_sub_mesh_index);
        }

        // --- Generate additional LODs if requested ---
        if (generateLods && (Flags & MeshFlags.PostProcessGenerateLods) != 0)
        {
            // Screen coverage thresholds (from your renderable::update_lod_indices logic)
            // These define at what screen fraction each LOD becomes active.
            // LOD generation targets are derived from them so simplification matches runtime selection.
            ReadOnlySpan<float> screenThresholds = stackalloc float[]
            {
            0.05f,   // LOD 0: object covers >= 5% of screen height
            0.025f,  // LOD 1: >= 2.5%
            0.012f,  // LOD 2: >= 1.2%
            0.006f,  // LOD 3: >= 0.6%
            0.003f   // LOD 4: >= 0.3%
        };

            int originalIndexCount = indices.Count;

            // Start progressive simplification from LOD 0
            List<RHI_Vertex_PosTexNorTan> prevVertices = new List<RHI_Vertex_PosTexNorTan>(vertices);
            List<uint> prevIndices = new List<uint>(indices);

            for (int lodLevel = 1; lodLevel < MeshLodCount; lodLevel++)
            {
                // Work on a copy of the previous LOD
                List<RHI_Vertex_PosTexNorTan> lodVertices = new List<RHI_Vertex_PosTexNorTan>(prevVertices);
                List<uint> lodIndices = new List<uint>(prevIndices);

                // Too simple to simplify further
                if (lodIndices.Count <= 64)
                    break;

                // Target triangle count based on screen coverage ratio (~2x reduction per level)
                float coverage = screenThresholds[lodLevel];
                float baseCoverage = screenThresholds[0];
                float targetRatio = coverage / baseCoverage;

                int targetIndexCount = Math.Max(64, (int)(originalIndexCount * targetRatio));

                // Simplify
                bool preserveUvs = true;
                bool preserveEdges = (Flags & MeshFlags.PostProcessPreserveTerrainEdges) != 0;

                GeometryProcessing.Simplify(lodIndices, lodVertices, targetIndexCount, preserveUvs, preserveEdges); // <-- port this

                // Stop if simplification didn't actually reduce complexity
                if (lodIndices.Count >= prevIndices.Count)
                    break;

                // Add this LOD
                AddLod(lodVertices, lodIndices, currentSubMeshIndex);

                // Prepare for next level
                prevVertices = lodVertices;
                prevIndices = lodIndices;
            }
        }

        // Return the sub-mesh index if the caller asked for it
        subMeshIndex = (uint)currentSubMeshIndex;
    }
}
