// The textured cube sample: an indexed PosUv cube with a reverse-z depth buffer, a per-pass texture sampled through the
// bindless samplers and the transform in push constants. Exercises depth targets, DrawIndexed, SetTexture on a
// graphics pipeline and the bindless sampler set.
using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;

namespace Phalanx.Rendering.RHI;

internal sealed class RHI_TexturedCubeSample : IRHI_Sample
{
    public static readonly Phalanx.Math.Color ClearColor = new(0.1f, 0.1f, 0.1f, 1.0f);

    // the checker texture's two colours (rgba8)
    public static readonly byte[] ColorA = { 255, 200, 0, 255 };
    public static readonly byte[] ColorB = { 20, 90, 255, 255 };
    public const uint TextureSize = 8;

    private readonly RHI_Shader m_shader_v = new();
    private readonly RHI_Shader m_shader_p = new();
    private readonly RHI_Buffer m_vertices;
    private readonly RHI_Buffer m_indices;
    private readonly RHI_Texture m_texture;
    private readonly RHI_Sampler[] m_samplers;

    // the cull mode after the pipeline state is set (spartan: SetCullMode), back by default
    public RHI_CullMode CullMode { get; init; } = RHI_CullMode.Back;

    public RHI_TexturedCubeSample()
    {
        m_shader_v.Compile(RHI_Shader_Type.Vertex, "textured_cube.vert", async: false, RHI_Vertex_Type.PosUv);
        m_shader_p.Compile(RHI_Shader_Type.Pixel, "textured_cube.frag", async: false);
        if (!m_shader_v.IsCompiled() || !m_shader_p.IsCompiled())
            throw new InvalidOperationException($"The cube shaders failed to compile: {m_shader_v.LastError ?? m_shader_p.LastError}");

        CreateCube(out RHI_Vertex_PosTex[] vertices, out ushort[] indices);
        m_vertices = new RHI_Buffer(RHI_Buffer_Type.Vertex, (ulong)Marshal.SizeOf<RHI_Vertex_PosTex>(), (uint)vertices.Length,
                                    MemoryMarshal.AsBytes(vertices.AsSpan()), mappable: false, "cube_vertices");
        m_indices  = new RHI_Buffer(RHI_Buffer_Type.Index, sizeof(ushort), (uint)indices.Length,
                                    MemoryMarshal.AsBytes(indices.AsSpan()), mappable: false, "cube_indices");

        // a 2x2 checker of 4x4 texel cells
        byte[] pixels = new byte[TextureSize * TextureSize * 4];
        for (uint y = 0; y < TextureSize; y++)
            for (uint x = 0; x < TextureSize; x++)
                ((((x / 4) + (y / 4)) % 2 == 0) ? ColorA : ColorB).CopyTo(pixels, (int)((y * TextureSize + x) * 4));
        var slice = new RHI_Texture_Slice();
        slice.mips.Add(new RHI_Texture_Mip { bytes = pixels });
        m_texture = new RHI_Texture(RHI_Texture_Type.Type2D, TextureSize, TextureSize, 1, 1, RHI_Format.R8G8B8A8_Unorm,
                                    RHI_Texture_Flags.RHI_Texture_Srv, "cube_checker", new List<RHI_Texture_Slice> { slice });

        m_samplers = RHI_SampleHost.CreateRendererSamplers();
        RHI_Device.UpdateBindlessSamplers(m_samplers);
    }

    // 24 vertices (4 per face, so every face gets the full texture) and 36 indices, every face clockwise seen from outside
    public static void CreateCube(out RHI_Vertex_PosTex[] vertices, out ushort[] indices)
    {
        Vector3[] normals = { -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY };
        var v = new List<RHI_Vertex_PosTex>();
        var i = new List<ushort>();
        foreach (Vector3 n in normals)
        {
            // left handed like spartan: right = up x forward, the viewer looks along -n
            Vector3 forward = -n;
            Vector3 up      = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 right   = Vector3.Cross(up, forward);
            Vector3 c       = n * 0.5f;
            var b = (ushort)v.Count;
            v.Add(new RHI_Vertex_PosTex(c - right * 0.5f + up * 0.5f, new Vector2(0, 0))); // top left
            v.Add(new RHI_Vertex_PosTex(c + right * 0.5f + up * 0.5f, new Vector2(1, 0))); // top right
            v.Add(new RHI_Vertex_PosTex(c + right * 0.5f - up * 0.5f, new Vector2(1, 1))); // bottom right
            v.Add(new RHI_Vertex_PosTex(c - right * 0.5f - up * 0.5f, new Vector2(0, 1))); // bottom left
            i.AddRange(new[] { b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3) });
        }
        vertices = v.ToArray();
        indices  = i.ToArray();
    }

    // left handed reverse-z perspective (near maps to 1, far to 0), row vectors like System.Numerics
    public static Matrix4x4 CreatePerspectiveReverseZ(float fov_y, float aspect, float near, float far)
    {
        float ys = 1.0f / MathF.Tan(fov_y * 0.5f);
        float xs = ys / aspect;
        return new Matrix4x4(
            xs, 0,  0,                          0,
            0,  ys, 0,                          0,
            0,  0,  -near / (far - near),       1,
            0,  0,  near * far / (far - near),  0);
    }

    public static Matrix4x4 GetTransform(float time, float aspect)
    {
        Matrix4x4 world = Matrix4x4.CreateRotationY(0.6f + time * 0.8f) * Matrix4x4.CreateRotationX(0.45f + time * 0.3f) * Matrix4x4.CreateTranslation(0, 0, 2.2f);
        return world * CreatePerspectiveReverseZ(MathF.PI / 3.0f, aspect, 0.1f, 100.0f);
    }

    public void Render(RHI_CommandList cmd, RHI_SwapChain? swapchain, RHI_Texture? color, RHI_Texture depth, float time)
    {
        var pso = new RHI_PipelineState { name = "textured_cube" };
        pso.SetShader(RHI_Shader_Type.Vertex, m_shader_v);
        pso.SetShader(RHI_Shader_Type.Pixel, m_shader_p);
        pso.render_target_swapchain = swapchain;
        pso.render_target_color_textures[0] = color;
        pso.render_target_depth_texture = depth; // the default depth state is read/write, greater equal (reverse-z)
        pso.clear_color[0] = ClearColor;
        pso.clear_depth = 0.0f;

        cmd.SetPipelineState(pso);
        cmd.SetCullMode(CullMode);
        cmd.SetTexture(7, m_texture); // t7
        cmd.PushConstants(GetTransform(time, (float)pso.GetWidth() / pso.GetHeight()));
        cmd.SetBufferVertex(m_vertices);
        cmd.SetBufferIndex(m_indices);
        cmd.DrawIndexed(36);
    }

    public void Dispose()
    {
        m_vertices.Dispose();
        m_indices.Dispose();
        m_texture.Dispose();
        foreach (RHI_Sampler sampler in m_samplers)
            sampler.Dispose();
        m_shader_v.Dispose();
        m_shader_p.Dispose();
    }
}
