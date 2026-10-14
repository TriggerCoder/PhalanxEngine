using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering.RHI;
using Silk.NET.Vulkan;
using Xunit;
using static Phalanx.Rendering.RHI.RHI_Definitions;

namespace Phalanx.Tests;

// no gpu: state hashing, the pipeline state, input layouts, lookup tables, the sample geometry (spartan @ 4c07baa)
public class RhiPipelineLogicTests
{
    [Fact]
    public void Blend_states_hash_every_field_including_the_blend_factor()
    {
        Assert.Equal(new RHI_BlendState(true).GetHash(), new RHI_BlendState(true).GetHash());
        Assert.NotEqual(new RHI_BlendState(true).GetHash(), new RHI_BlendState(false).GetHash());
        Assert.NotEqual(new RHI_BlendState(true, RHI_Blend.One).GetHash(), new RHI_BlendState(true, RHI_Blend.Zero).GetHash());

        // spartan truncates the factor to an integer before hashing (RHI_BlendState.cpp:43)
        Assert.NotEqual(new RHI_BlendState(true, blend_factor: 0.25f).GetHash(), new RHI_BlendState(true, blend_factor: 0.75f).GetHash());
        var blend = new RHI_BlendState(true, blend_factor: 0.25f);
        ulong before = blend.GetHash();
        blend.SetBlendFactor(0.5f);
        Assert.NotEqual(before, blend.GetHash());
        Assert.Equal(new RHI_BlendState(true, blend_factor: 0.5f).GetHash(), blend.GetHash());
    }

    [Fact]
    public void Depth_stencil_and_rasterizer_states_hash_their_fields()
    {
        Assert.Equal(new RHI_DepthStencilState().GetHash(), new RHI_DepthStencilState().GetHash());
        Assert.NotEqual(new RHI_DepthStencilState(true, true, RHI_Comparison_Function.Greater).GetHash(),
                        new RHI_DepthStencilState(true, true, RHI_Comparison_Function.GreaterEqual).GetHash());
        Assert.NotEqual(new RHI_DepthStencilState(true, false).GetHash(), new RHI_DepthStencilState(true, true).GetHash());

        Assert.Equal(new RHI_RasterizerState(RHI_PolygonMode.Solid, true).GetHash(), new RHI_RasterizerState(RHI_PolygonMode.Solid, true).GetHash());
        Assert.NotEqual(new RHI_RasterizerState(RHI_PolygonMode.Solid, true).GetHash(), new RHI_RasterizerState(RHI_PolygonMode.Wireframe, true).GetHash());
        Assert.NotEqual(new RHI_RasterizerState(RHI_PolygonMode.Solid, true, line_width: 1.25f).GetHash(), new RHI_RasterizerState(RHI_PolygonMode.Solid, true, line_width: 1.75f).GetHash());
        Assert.NotEqual(new RHI_RasterizerState(RHI_PolygonMode.Solid, true, 0.001f).GetHash(), new RHI_RasterizerState(RHI_PolygonMode.Solid, true, 0.002f).GetHash());
    }

    [Fact]
    public void Pipeline_state_hash_follows_states_and_target_formats_not_target_objects()
    {
        var a = new RHI_PipelineState { name = "a", blend_state = new RHI_BlendState(), rasterizer_state = new RHI_RasterizerState(RHI_PolygonMode.Solid, true), depth_stencil_state = new RHI_DepthStencilState(false, false) };
        RHI_PipelineState b = a.Clone();
        Assert.Equal(GetHash(a), GetHash(b));

        b.primitive_topology = RHI_PrimitiveTopology.LineList;
        Assert.NotEqual(GetHash(a), GetHash(b));

        RHI_PipelineState c = a.Clone();
        c.blend_state = new RHI_BlendState(true);
        Assert.NotEqual(GetHash(a), GetHash(c));

        // clones don't share arrays
        RHI_PipelineState d = a.Clone();
        d.clear_color[0] = new Phalanx.Math.Color(1, 0, 0, 1);
        Assert.Equal(rhi_color_load, a.clear_color[0]);
    }

    // the hash without validation (no compiled shaders without a device)
    private static ulong GetHash(RHI_PipelineState pso)
    {
        try { pso.Prepare(); } catch (InvalidOperationException) { }
        return pso.GetHash();
    }

    [Fact]
    public void Pipeline_state_validation_and_type()
    {
        var pso = new RHI_PipelineState { name = "empty" };
        Assert.False(pso.IsGraphics());
        Assert.False(pso.IsCompute());
        var e = Assert.Throws<InvalidOperationException>(pso.Prepare);
        Assert.Contains("no shader", e.Message);

        // a shader that is set but not compiled is reported the same way
        using var shader = new RHI_Shader();
        pso.SetShader(RHI_Shader_Type.Vertex, shader);
        Assert.True(pso.IsGraphics());
        Assert.Throws<InvalidOperationException>(pso.Prepare);

        // graphics defaults are applied, depth state follows the depth target (read/write, greater equal)
        Assert.NotNull(pso.rasterizer_state);
        Assert.NotNull(pso.blend_state);
        Assert.False(pso.depth_stencil_state!.GetDepthTestEnabled());
        Assert.Equal(3.0f, pso.rasterizer_state!.GetLineWidth());
    }

    [Fact]
    public void Clear_values_and_load_sentinels()
    {
        var pso = new RHI_PipelineState();
        Assert.False(pso.HasClearValues());
        pso.clear_stencil = 0; // spartan's rhi_stencil_load is 0 (RHI_Definitions.h:650), a stencil clear to 0 looked like "load"
        Assert.True(pso.HasClearValues());
        pso.clear_stencil = rhi_stencil_dont_care;
        Assert.False(pso.HasClearValues());
        pso.clear_depth = 0.0f;
        Assert.True(pso.HasClearValues());
        pso.clear_depth = rhi_depth_dont_care;
        pso.clear_color[3] = new Phalanx.Math.Color(0, 0, 0, 0);
        Assert.True(pso.HasClearValues());
        Assert.NotEqual(rhi_stencil_load, rhi_stencil_dont_care);
    }

    [Theory]
    [InlineData((int)RHI_Vertex_Type.Pos, 12u, 1)]
    [InlineData((int)RHI_Vertex_Type.PosCol, 28u, 2)]
    [InlineData((int)RHI_Vertex_Type.PosUv, 20u, 2)]
    [InlineData((int)RHI_Vertex_Type.Pos2dUvCol8, 20u, 3)]
    [InlineData((int)RHI_Vertex_Type.PosUvNorTan, 24u, 4)]
    [InlineData((int)RHI_Vertex_Type.Max, 0u, 0)]
    public void Input_layouts_match_the_vertex_structs(int vertex_type, uint size, int attributes)
    {
        var type = (RHI_Vertex_Type)vertex_type;
        var layout = new RHI_InputLayout();
        layout.Create(type);
        Assert.Equal(type, layout.GetVertexType()); // spartan never stores it (RHI_InputLayout.h:45-107)
        Assert.Equal(size, layout.GetVertexSize());
        Assert.Equal(attributes, layout.GetAttributeDescriptions().Count);
        uint location = 0;
        foreach (VertexAttribute a in layout.GetAttributeDescriptions())
        {
            Assert.Equal(location++, a.location);
            Assert.Equal(0u, a.binding);
            Assert.True(a.offset + rhi_format_to_bytes(a.format) <= size);
        }
    }

    [Fact]
    public void Input_layout_offsets()
    {
        var layout = new RHI_InputLayout();
        layout.Create(RHI_Vertex_Type.PosUvNorTan);
        Assert.Equal(new uint[] { 0, 12, 16, 20 }, layout.GetAttributeDescriptions().Select(a => a.offset));
        Assert.Equal(new[] { RHI_Format.R32G32B32_Float, RHI_Format.R32_Uint, RHI_Format.R32_Uint, RHI_Format.R32_Uint }, layout.GetAttributeDescriptions().Select(a => a.format));
        layout.Create(RHI_Vertex_Type.Pos2dUvCol8);
        Assert.Equal(new uint[] { 0, 8, 16 }, layout.GetAttributeDescriptions().Select(a => a.offset));
        Assert.Equal(RHI_Vertex_PosTexNorTan.Size, Marshal.SizeOf<RHI_Vertex_PosTexNorTan>());
    }

    [Fact]
    public void Lookup_tables_cover_their_enums()
    {
        Assert.Equal((int)RHI_PrimitiveTopology.Max, RHI_Implementation.VulkanPrimitiveTopology.Length);
        Assert.Equal((int)RHI_CullMode.Max, RHI_Implementation.VulkanCullMode.Length);
        Assert.Equal((int)RHI_PolygonMode.Max + 1, RHI_Implementation.VulkanPolygonMode.Length);
        Assert.Equal(Enum.GetValues<RHI_Stencil_Operation>().Length, RHI_Implementation.VulkanStencilOperation.Length);
        Assert.Equal(Enum.GetValues<RHI_Blend>().Length, RHI_Implementation.VulkanBlendFactor.Length);
        Assert.Equal((int)RHI_Blend_Operation.Undefined, RHI_Implementation.VulkanBlendOperation.Length);
        Assert.Equal(BlendFactor.OneMinusSrc1Alpha, RHI_Implementation.VulkanBlendFactor[(int)RHI_Blend.Inv_Src1_Alpha]);
        Assert.Equal(StencilOp.IncrementAndWrap, RHI_Implementation.VulkanStencilOperation[(int)RHI_Stencil_Operation.Incr]);
    }

    [Fact]
    public void Shader_hash_includes_stage_and_vertex_type()
    {
        var none = new Dictionary<string, string>();
        Assert.NotEqual(RHI_Shader.ComputeHash("src", none, RHI_Shader_Type.Vertex, RHI_Vertex_Type.PosUv),
                        RHI_Shader.ComputeHash("src", none, RHI_Shader_Type.Vertex, RHI_Vertex_Type.PosCol));
        Assert.NotEqual(RHI_Shader.ComputeHash("src", none, RHI_Shader_Type.Vertex),
                        RHI_Shader.ComputeHash("src", none, RHI_Shader_Type.Pixel));
    }

    [Fact]
    public void Cube_faces_are_wound_the_same_way_and_face_outwards()
    {
        RHI_TexturedCubeSample.CreateCube(out RHI_Vertex_PosTex[] vertices, out ushort[] indices);
        Assert.Equal(24, vertices.Length);
        Assert.Equal(36, indices.Length);
        for (int t = 0; t < indices.Length; t += 3)
        {
            Vector3 a = vertices[indices[t]].pos, b = vertices[indices[t + 1]].pos, c = vertices[indices[t + 2]].pos;
            Vector3 center = (a + b + c) / 3.0f;
            // clockwise seen from outside in a left handed system: the numeric cross product points outwards
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), center) > 0.0f, $"triangle {t / 3}");
        }
    }

    [Fact]
    public void Reverse_z_projection_maps_near_to_one_and_far_to_zero()
    {
        Matrix4x4 p = RHI_TexturedCubeSample.CreatePerspectiveReverseZ(MathF.PI / 3, 1.0f, 0.1f, 100.0f);
        Vector4 near = Vector4.Transform(new Vector4(0, 0, 0.1f, 1), p);
        Vector4 far  = Vector4.Transform(new Vector4(0, 0, 100.0f, 1), p);
        Assert.Equal(1.0f, near.Z / near.W, 4);
        Assert.Equal(0.0f, far.Z / far.W, 4);
    }
}

// vulkan on lavapipe: graphics pipelines, the pipeline cache and the two samples rendered offscreen
[Collection("gpu")]
public class RhiPipelineGpuTests
{
    private static void Run(Action body)
    {
        Skip.IfNot(Phalanx.Rendering.RenderDevice.IsVulkanAvailable(out string reason), reason);
        RHI_Device.Initialize(Array.Empty<string>(), validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            RHI_Device.DeletionQueueFlush();
            Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal) || m.Contains("WarningBit", StringComparison.Ordinal)),
                        string.Join("\n", RHI_Device.ValidationMessages));
        }
        finally
        {
            RHI_Device.Destroy();
        }
    }

    private const uint Size = 64;

    private static byte[] Pixel(byte[] pixels, uint x, uint y) => pixels.AsSpan((int)((y * Size + x) * 4), 4).ToArray();

    private static byte[] ToRgba8(Phalanx.Math.Color c) =>
        new[] { (byte)MathF.Round(c.R * 255), (byte)MathF.Round(c.G * 255), (byte)MathF.Round(c.B * 255), (byte)MathF.Round(c.A * 255) };

    [SkippableFact]
    public void Triangle_renders_offscreen_with_its_vertex_colors()
    {
        Run(() =>
        {
            byte[] pixels = RHI_SampleHost.RenderOffscreenOnDevice(() => new RHI_TriangleSample(), Size, Size, 0.0f);
            Assert.Equal((int)(Size * Size * 4), pixels.Length);
            byte[] clear = ToRgba8(RHI_TriangleSample.ClearColor);

            // corners clear, center covered
            Assert.Equal(clear, Pixel(pixels, 0, 0));
            Assert.Equal(clear, Pixel(pixels, Size - 1, 0));
            Assert.Equal(clear, Pixel(pixels, 0, Size - 1));
            Assert.Equal(clear, Pixel(pixels, Size - 1, Size - 1));
            Assert.NotEqual(clear, Pixel(pixels, Size / 2, Size / 2));

            // y up like d3d: red at the top, green bottom right, blue bottom left
            byte[] top = Pixel(pixels, Size / 2, Size / 4 + 2), right = Pixel(pixels, Size * 3 / 4 - 3, Size * 3 / 4 - 2), left = Pixel(pixels, Size / 4 + 3, Size * 3 / 4 - 2);
            Assert.True(top[0] > top[1] && top[0] > top[2], $"top {string.Join(",", top)}");
            Assert.True(right[1] > right[0] && right[1] > right[2], $"right {string.Join(",", right)}");
            Assert.True(left[2] > left[0] && left[2] > left[1], $"left {string.Join(",", left)}");

            // the triangle's pipeline is cached
            Assert.Equal(1, RHI_Device.GetPipelineCount());
        });
    }

    [SkippableFact]
    public void Textured_cube_renders_offscreen_with_depth_and_its_texture()
    {
        Run(() =>
        {
            byte[] pixels = RHI_SampleHost.RenderOffscreenOnDevice(() => new RHI_TexturedCubeSample(), Size, Size, 0.0f);
            byte[] clear = ToRgba8(RHI_TexturedCubeSample.ClearColor);
            Assert.Equal(clear, Pixel(pixels, 0, 0));
            Assert.Equal(clear, Pixel(pixels, Size - 1, Size - 1));

            // every covered pixel is one of the two checker colours (point sampling), and both show up
            int a = 0, b = 0, other = 0;
            for (uint y = 0; y < Size; y++)
                for (uint x = 0; x < Size; x++)
                {
                    byte[] p = Pixel(pixels, x, y);
                    if (p.SequenceEqual(clear)) continue;
                    if (p.SequenceEqual(RHI_TexturedCubeSample.ColorA)) a++;
                    else if (p.SequenceEqual(RHI_TexturedCubeSample.ColorB)) b++;
                    else other++;
                }
            Assert.Equal(0, other);
            Assert.True(a > 50 && b > 50, $"a {a}, b {b}");
            byte[] center = Pixel(pixels, Size / 2, Size / 2);
            Assert.True(center.SequenceEqual(RHI_TexturedCubeSample.ColorA) || center.SequenceEqual(RHI_TexturedCubeSample.ColorB));
        });
    }

    [SkippableFact]
    public void Cube_depth_test_keeps_the_nearest_faces()
    {
        Run(() =>
        {
            // with culling off and the depth test on, the picture must not change: the back faces lose the depth test
            byte[] culled = RHI_SampleHost.RenderOffscreenOnDevice(() => new RHI_TexturedCubeSample(), Size, Size, 0.3f);
            byte[] unculled = RHI_SampleHost.RenderOffscreenOnDevice(() => new RHI_TexturedCubeSample { CullMode = RHI_CullMode.None }, Size, Size, 0.3f);
            Assert.Equal(culled, unculled);

            // and with front faces culled the inside shows, which differs
            byte[] inside = RHI_SampleHost.RenderOffscreenOnDevice(() => new RHI_TexturedCubeSample { CullMode = RHI_CullMode.Front }, Size, Size, 0.3f);
            Assert.NotEqual(culled, inside);
        });
    }

    [SkippableFact]
    public void Pipelines_are_cached_by_state_and_the_cache_survives_on_disk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "phalanx-pipeline-cache-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "pipeline_cache.bin");
        File.Delete(path);
        string? previous = RHI_Device.PipelineCachePath;
        RHI_Device.PipelineCachePath = path;
        try
        {
            Run(() =>
            {
                using var sample = new RHI_TriangleSample();
                using var color = new RHI_Texture(RHI_Texture_Type.Type2D, Size, Size, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "rt");
                using var color2 = new RHI_Texture(RHI_Texture_Type.Type2D, Size, Size, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "rt2");
                using var color_float = new RHI_Texture(RHI_Texture_Type.Type2D, Size, Size, 1, 1, RHI_Format.R16G16B16A16_Float, RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "rt_float");
                using var depth = new RHI_Texture(RHI_Texture_Type.Type2D, Size, Size, 1, 1, RHI_Format.D32_Float, RHI_Texture_Flags.RHI_Texture_Rtv, "depth");

                RHI_CommandList cmd = RHI_Device.BeginFrame();
                sample.Render(cmd, null, color, depth, 0);
                sample.Render(cmd, null, color2, depth, 0);      // same format: same pipeline, new render pass
                Assert.Equal(1, RHI_Device.GetPipelineCount());
                sample.Render(cmd, null, color_float, depth, 0); // new format: new pipeline
                Assert.Equal(2, RHI_Device.GetPipelineCount());
                RHI_Device.EndFrame().Wait();

                // both targets of the same format got the triangle
                byte[] p1 = RHI_Device.ReadbackTexture(color, 0, 0), p2 = RHI_Device.ReadbackTexture(color2, 0, 0);
                Assert.Equal(p1, p2);
                Assert.NotEqual(ToRgba8(RHI_TriangleSample.ClearColor), Pixel(p1, Size / 2, Size / 2));
            });

            Assert.True(File.Exists(path));
            byte[] data = File.ReadAllBytes(path);
            Run(() =>
            {
                Assert.True(RHI_Device.IsPipelineCacheCompatible(data));
                byte[] foreign = (byte[])data.Clone();
                foreign[16] ^= 0xFF; // another pipelineCacheUUID
                Assert.False(RHI_Device.IsPipelineCacheCompatible(foreign));
                Assert.False(RHI_Device.IsPipelineCacheCompatible(new byte[8]));
            });

            // a garbage file is ignored
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            Run(() => Assert.Equal(0, RHI_Device.GetPipelineCount()));
        }
        finally
        {
            RHI_Device.PipelineCachePath = previous;
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void A_pass_with_only_a_clear_still_clears()
    {
        Run(() =>
        {
            using var sample = new RHI_TriangleSample();
            using var color = new RHI_Texture(RHI_Texture_Type.Type2D, 4, 4, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "rt_clear");
            var shader_v = new RHI_Shader();
            var shader_p = new RHI_Shader();
            shader_v.Compile(RHI_Shader_Type.Vertex, "triangle.vert", false, RHI_Vertex_Type.PosCol);
            shader_p.Compile(RHI_Shader_Type.Pixel, "triangle.frag", false);

            var pso = new RHI_PipelineState { name = "clear_only" };
            pso.SetShader(RHI_Shader_Type.Vertex, shader_v);
            pso.SetShader(RHI_Shader_Type.Pixel, shader_p);
            pso.render_target_color_textures[0] = color;
            pso.clear_color[0] = new Phalanx.Math.Color(0, 1, 0, 1);

            RHI_CommandList cmd = RHI_Device.BeginFrame();
            cmd.SetPipelineState(pso); // no draw: render_pass_end runs the pending clear
            RHI_Device.EndFrame().Wait();
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, RHI_Device.ReadbackTexture(color, 0, 0).AsSpan(0, 4).ToArray());
            shader_v.Dispose();
            shader_p.Dispose();
        });
    }

    [SkippableFact]
    public void Wireframe_lines_points_and_blending_pipelines_are_valid()
    {
        Run(() =>
        {
            using var sample = new RHI_TriangleSample();
            using var color = new RHI_Texture(RHI_Texture_Type.Type2D, Size, Size, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "rt_states");
            using var shader_v = new RHI_Shader();
            using var shader_p = new RHI_Shader();
            shader_v.Compile(RHI_Shader_Type.Vertex, "triangle.vert", false, RHI_Vertex_Type.PosCol);
            shader_p.Compile(RHI_Shader_Type.Pixel, "triangle.frag", false);
            RHI_Vertex_PosCol[] vertices =
            {
                new(new Vector3(-0.5f, 0.0f, 0.0f), new Phalanx.Math.Color(1, 1, 1, 0.5f)),
                new(new Vector3( 0.5f, 0.0f, 0.0f), new Phalanx.Math.Color(1, 1, 1, 0.5f)),
                new(new Vector3( 0.0f, 0.5f, 0.0f), new Phalanx.Math.Color(1, 1, 1, 0.5f))
            };
            using var buffer = new RHI_Buffer(RHI_Buffer_Type.Vertex, (ulong)Marshal.SizeOf<RHI_Vertex_PosCol>(), 3, MemoryMarshal.AsBytes(vertices.AsSpan()), false, "states_vertices");

            RHI_CommandList cmd = RHI_Device.BeginFrame();
            void draw(Action<RHI_PipelineState> configure)
            {
                var pso = new RHI_PipelineState { name = "states" };
                pso.SetShader(RHI_Shader_Type.Vertex, shader_v);
                pso.SetShader(RHI_Shader_Type.Pixel, shader_p);
                pso.render_target_color_textures[0] = color;
                configure(pso);
                cmd.SetPipelineState(pso);
                cmd.SetBufferVertex(buffer);
                cmd.Draw(3);
            }
            draw(p => p.clear_color[0] = new Phalanx.Math.Color(0, 0, 0, 1));
            draw(p => p.rasterizer_state = new RHI_RasterizerState(RHI_PolygonMode.Wireframe, true, line_width: 2.0f));
            draw(p => p.primitive_topology = RHI_PrimitiveTopology.LineList);
            draw(p => p.primitive_topology = RHI_PrimitiveTopology.PointList);
            draw(p => p.blend_state = new RHI_BlendState(true));
            draw(p => p.rasterizer_state = new RHI_RasterizerState(RHI_PolygonMode.Solid, false, 0.001f, 0.0f, 1.0f));
            RHI_Device.EndFrame().Wait();
            Assert.Equal(6, RHI_Device.GetPipelineCount());
        });
    }

    [SkippableFact]
    public void Compute_after_draw_ends_the_render_pass()
    {
        Run(() =>
        {
            using var sample = new RHI_TriangleSample();
            using var color = new RHI_Texture(RHI_Texture_Type.Type2D, Size, Size, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "rt_mixed");
            using var depth = new RHI_Texture(RHI_Texture_Type.Type2D, Size, Size, 1, 1, RHI_Format.D32_Float, RHI_Texture_Flags.RHI_Texture_Rtv, "depth_mixed");
            using var shader = new RHI_Shader();
            string path = Path.Combine(Path.GetTempPath(), $"phalanx-mixed-{Environment.ProcessId}.comp");
            File.WriteAllText(path, """
                #version 460
                layout(local_size_x = 1) in;
                layout(std430, set = 0, binding = 100) buffer Output { uint data[]; } output_buffer;
                void main() { output_buffer.data[0] = 42u; }
                """);
            shader.Compile(RHI_Shader_Type.Compute, path, false);
            using var pipeline = new RHI_Pipeline(shader, "mixed");
            using var output = new RHI_Buffer(RHI_Buffer_Type.Storage, 4, 1, default, false, "mixed_out");

            RHI_CommandList cmd = RHI_Device.BeginFrame();
            sample.Render(cmd, null, color, depth, 0);
            Assert.True(cmd.IsRenderPassActive());
            cmd.SetPipeline(pipeline);
            Assert.False(cmd.IsRenderPassActive());
            cmd.SetBuffer(0, output);
            cmd.Dispatch(1);
            RHI_Device.EndFrame().Wait();
            Assert.Equal(42u, BitConverter.ToUInt32(RHI_Device.ReadbackBuffer(output)));
            File.Delete(path);
        });
    }
}
