// Port of SpartanEngine source/rhi/RHI_InputLayout.h @ 4c07baa
// Deviations: Create stores the vertex type (spartan never assigns m_vertex_type, RHI_InputLayout.h:45-107, so
// GetVertexType() and operator== read an uninitialized value), and an unknown type leaves an empty layout of size 0
// instead of an uninitialized m_vertex_size. The offsets come from Marshal.OffsetOf on the Phalanx.Core structs.
using System.Runtime.InteropServices;
using Phalanx.Geometry;

namespace Phalanx.Rendering.RHI;

internal readonly record struct VertexAttribute(string name, uint location, uint binding, RHI_Format format, uint offset);

internal sealed class RHI_InputLayout
{
    private RHI_Vertex_Type m_vertex_type = RHI_Vertex_Type.Max;
    private uint m_vertex_size;
    private VertexAttribute[] m_vertex_attributes = Array.Empty<VertexAttribute>();

    private static uint Offset<T>(string field) => (uint)Marshal.OffsetOf<T>(field);

    public void Create(RHI_Vertex_Type vertex_type)
    {
        const uint binding = 0;
        m_vertex_type = vertex_type;

        if (vertex_type == RHI_Vertex_Type.Max)
        {
            // the full-screen triangle vertex shaders generate their own vertices,
            // so they don't need a vertex type for an input layout
            m_vertex_attributes = Array.Empty<VertexAttribute>();
            m_vertex_size       = 0;
        }
        else if (vertex_type == RHI_Vertex_Type.Pos)
        {
            m_vertex_attributes = new VertexAttribute[]
            {
                new("POSITION", 0, binding, RHI_Format.R32G32B32_Float, Offset<RHI_Vertex_Pos>(nameof(RHI_Vertex_Pos.pos)))
            };
            m_vertex_size = (uint)Marshal.SizeOf<RHI_Vertex_Pos>();
        }
        else if (vertex_type == RHI_Vertex_Type.PosUv)
        {
            m_vertex_attributes = new VertexAttribute[]
            {
                new("POSITION", 0, binding, RHI_Format.R32G32B32_Float, Offset<RHI_Vertex_PosTex>(nameof(RHI_Vertex_PosTex.pos))),
                new("TEXCOORD", 1, binding, RHI_Format.R32G32_Float,    Offset<RHI_Vertex_PosTex>(nameof(RHI_Vertex_PosTex.tex)))
            };
            m_vertex_size = (uint)Marshal.SizeOf<RHI_Vertex_PosTex>();
        }
        else if (vertex_type == RHI_Vertex_Type.PosCol)
        {
            m_vertex_attributes = new VertexAttribute[]
            {
                new("POSITION", 0, binding, RHI_Format.R32G32B32_Float,    Offset<RHI_Vertex_PosCol>(nameof(RHI_Vertex_PosCol.pos))),
                new("COLOR",    1, binding, RHI_Format.R32G32B32A32_Float, Offset<RHI_Vertex_PosCol>(nameof(RHI_Vertex_PosCol.col)))
            };
            m_vertex_size = (uint)Marshal.SizeOf<RHI_Vertex_PosCol>();
        }
        else if (vertex_type == RHI_Vertex_Type.Pos2dUvCol8)
        {
            m_vertex_attributes = new VertexAttribute[]
            {
                new("POSITION", 0, binding, RHI_Format.R32G32_Float,   Offset<RHI_Vertex_Pos2dTexCol8>(nameof(RHI_Vertex_Pos2dTexCol8.pos))),
                new("TEXCOORD", 1, binding, RHI_Format.R32G32_Float,   Offset<RHI_Vertex_Pos2dTexCol8>(nameof(RHI_Vertex_Pos2dTexCol8.tex))),
                new("COLOR",    2, binding, RHI_Format.R8G8B8A8_Unorm, Offset<RHI_Vertex_Pos2dTexCol8>(nameof(RHI_Vertex_Pos2dTexCol8.col)))
            };
            m_vertex_size = (uint)Marshal.SizeOf<RHI_Vertex_Pos2dTexCol8>();
        }
        else if (vertex_type == RHI_Vertex_Type.PosUvNorTan)
        {
            // packed 24-byte vertex, uv/nor/tan are R32_Uint and decoded in-shader
            m_vertex_attributes = new VertexAttribute[]
            {
                new("POSITION", 0, binding, RHI_Format.R32G32B32_Float, Offset<RHI_Vertex_PosTexNorTan>(nameof(RHI_Vertex_PosTexNorTan.pos))),
                new("TEXCOORD", 1, binding, RHI_Format.R32_Uint,        Offset<RHI_Vertex_PosTexNorTan>(nameof(RHI_Vertex_PosTexNorTan.uv))),
                new("NORMAL",   2, binding, RHI_Format.R32_Uint,        Offset<RHI_Vertex_PosTexNorTan>(nameof(RHI_Vertex_PosTexNorTan.nor))),
                new("TANGENT",  3, binding, RHI_Format.R32_Uint,        Offset<RHI_Vertex_PosTexNorTan>(nameof(RHI_Vertex_PosTexNorTan.tan)))
            };
            m_vertex_size = (uint)Marshal.SizeOf<RHI_Vertex_PosTexNorTan>();
        }
    }

    public RHI_Vertex_Type GetVertexType() => m_vertex_type;
    public uint GetVertexSize() => m_vertex_size;
    public IReadOnlyList<VertexAttribute> GetAttributeDescriptions() => m_vertex_attributes;
    public uint GetAttributeCount() => (uint)m_vertex_attributes.Length;
}
