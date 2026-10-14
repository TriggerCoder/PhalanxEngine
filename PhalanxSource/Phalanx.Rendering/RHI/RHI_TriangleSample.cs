// The triangle sample: one PosCol vertex buffer, a vertex + pixel shader pipeline state, a clear and a draw. Exercises
// the input layout, the graphics pipeline with dynamic rendering, the pipeline cache and Draw.
using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;

namespace Phalanx.Rendering.RHI;

internal sealed class RHI_TriangleSample : IRHI_Sample
{
    public static readonly Phalanx.Math.Color ClearColor = new(0.1f, 0.1f, 0.1f, 1.0f);

    private readonly RHI_Shader m_shader_v = new();
    private readonly RHI_Shader m_shader_p = new();
    private readonly RHI_Buffer m_vertices;

    public RHI_TriangleSample()
    {
        m_shader_v.Compile(RHI_Shader_Type.Vertex, "triangle.vert", async: false, RHI_Vertex_Type.PosCol);
        m_shader_p.Compile(RHI_Shader_Type.Pixel, "triangle.frag", async: false);
        if (!m_shader_v.IsCompiled() || !m_shader_p.IsCompiled())
            throw new InvalidOperationException($"The triangle shaders failed to compile: {m_shader_v.LastError ?? m_shader_p.LastError}");

        // clockwise (d3d's front face), y up
        RHI_Vertex_PosCol[] vertices =
        {
            new(new Vector3( 0.0f,  0.5f, 0.0f), new Phalanx.Math.Color(1.0f, 0.0f, 0.0f, 1.0f)),
            new(new Vector3( 0.5f, -0.5f, 0.0f), new Phalanx.Math.Color(0.0f, 1.0f, 0.0f, 1.0f)),
            new(new Vector3(-0.5f, -0.5f, 0.0f), new Phalanx.Math.Color(0.0f, 0.0f, 1.0f, 1.0f))
        };
        m_vertices = new RHI_Buffer(RHI_Buffer_Type.Vertex, (ulong)Marshal.SizeOf<RHI_Vertex_PosCol>(), (uint)vertices.Length,
                                    MemoryMarshal.AsBytes(vertices.AsSpan()), mappable: false, "triangle_vertices");
    }

    public void Render(RHI_CommandList cmd, RHI_SwapChain? swapchain, RHI_Texture? color, RHI_Texture depth, float time)
    {
        var pso = new RHI_PipelineState { name = "triangle" };
        pso.SetShader(RHI_Shader_Type.Vertex, m_shader_v);
        pso.SetShader(RHI_Shader_Type.Pixel, m_shader_p);
        pso.render_target_swapchain = swapchain;
        pso.render_target_color_textures[0] = color;
        pso.clear_color[0] = ClearColor;

        cmd.SetPipelineState(pso);
        cmd.SetBufferVertex(m_vertices);
        cmd.Draw(3);
    }

    public void Dispose()
    {
        m_vertices.Dispose();
        m_shader_v.Dispose();
        m_shader_p.Dispose();
    }
}
