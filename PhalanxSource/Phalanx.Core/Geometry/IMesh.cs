// The renderer facing view of a mesh that Phalanx.Core's Render component holds.
// Spartan's Render (world/components/Render.h) holds a Mesh*, Mesh lives in the same runtime library. In Phalanx the
// Mesh (geometry/Mesh.cpp) lives in Phalanx.Rendering: it carries meshoptimizer (native) interop and the gpu geometry
// buffer upload, which Phalanx.Core (no gpu, no native packages) must not depend on. Core gets this interface and
// MeshLod instead, Phalanx.Rendering's Mesh implements it. The members are the ones Render.cpp calls on its mesh.
using Phalanx.Math;

namespace Phalanx.Geometry;

// geometry/Mesh.h MeshLod
public struct MeshLod
{
    public uint vertex_offset; // starting offset in m_vertices
    public uint vertex_count;  // number of vertices for this LOD
    public uint index_offset;  // starting offset in m_indices
    public uint index_count;   // number of indices for this LOD
    public BoundingBox aabb;   // bounding box of this LOD
}

public interface IMesh
{
    string GetObjectName();
    MeshType GetMeshType();
    int GetSubMeshCount();
    int GetLodCount(int sub_mesh_index);
    MeshLod GetLod(int sub_mesh_index, int lod_index);
    uint GetGlobalVertexOffset();
    uint GetGlobalIndexOffset();
    void GetGeometry(int sub_mesh_index, List<uint>? indices, List<RHI_Vertex_PosTexNorTan>? vertices);
}
