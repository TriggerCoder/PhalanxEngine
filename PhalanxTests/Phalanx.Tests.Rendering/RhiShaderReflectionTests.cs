using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Xunit;
using static Phalanx.Rendering.RHI.RHI_Definitions;

namespace Phalanx.Tests;

// no gpu: shaderc -> spir-v -> RHI_SpirvReflection, checked against what spartan's spirv-cross path produces
public class RhiShaderReflectionTests
{
    private static readonly ShaderCompiler compiler = new() { KeepNames = true };

    private const string Probe = """
        #version 460
        #extension GL_EXT_nonuniform_qualifier : require
        #extension GL_EXT_samplerless_texture_functions : require
        layout(local_size_x = 64) in;

        struct Light { vec4 color; mat4 transform; };
        layout(std140, set = 0, binding = 200) uniform BufferFrame { mat4 view; vec3 position; float near; Light lights[2]; };
        layout(push_constant, std430) uniform BufferPass { mat4 transform; vec4 values[2]; } pass;
        layout(std430, set = 0, binding = 100) buffer Output { uint data[]; } output_buffer;
        layout(std430, set = 0, binding = 330) readonly buffer Input { uvec4 header; uint values[]; } input_buffer;
        layout(set = 0, binding = 307) uniform texture2D tex;
        layout(set = 0, binding = 308) uniform texture2D tex_unused;
        layout(set = 0, binding = 101) uniform writeonly image2D tex_uav;
        layout(set = 0, binding = 309) uniform texture2D tex_array[8];
        layout(set = 1, binding = 315) uniform texture2D material_textures[];
        layout(set = 7, binding = 401) uniform sampler samplers[];

        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            vec4 c = texelFetch(tex, ivec2(0), 0) + texelFetch(tex_array[3], ivec2(0), 0)
                   + texture(sampler2D(material_textures[nonuniformEXT(i)], samplers[0]), vec2(0));
            imageStore(tex_uav, ivec2(0), c * pass.values[1] * view[0] * lights[1].color + near + input_buffer.values[i] + input_buffer.header.x);
            output_buffer.data[i] = i;
        }
        """;

    private static List<RHI_Descriptor> ReflectProbe() =>
        RHI_SpirvReflection.Reflect(compiler.CompileSource(Probe, ShaderStage.Compute, "probe.comp"), RHI_Shader_Type.Compute, "main");

    private static RHI_Descriptor Get(List<RHI_Descriptor> list, string name) => Assert.Single(list, d => d.name == name);

    [Fact]
    public void Types_slots_and_sets_match_spirv_cross_classification()
    {
        var d = ReflectProbe();
        Assert.Equal(RHI_Descriptor_Type.ConstantBuffer,     Get(d, "BufferFrame").type);
        Assert.Equal(200u,                                   Get(d, "BufferFrame").slot);
        Assert.Equal(RHI_Descriptor_Type.PushConstantBuffer, Get(d, "pass").type);
        Assert.Equal(RHI_Descriptor_Type.StructuredBuffer,   Get(d, "output_buffer").type);
        Assert.Equal(100u,                                   Get(d, "output_buffer").slot);
        Assert.Equal(RHI_Descriptor_Type.StructuredBuffer,   Get(d, "input_buffer").type);
        Assert.Equal(RHI_Descriptor_Type.Image,              Get(d, "tex").type);
        Assert.Equal(RHI_Image_Layout.General,               Get(d, "tex").layout);
        Assert.Equal(RHI_Descriptor_Type.TextureStorage,     Get(d, "tex_uav").type);
        Assert.True(Get(d, "tex_uav").IsStorage());
        Assert.Equal(RHI_Descriptor_Type.Sampler,            Get(d, "samplers").type);
        Assert.Equal(7u,                                     Get(d, "samplers").space);
        Assert.Equal(1u,                                     Get(d, "material_textures").space);
        Assert.Equal(RHI_Image_Layout.Max,                   Get(d, "output_buffer").layout);
        Assert.All(d, x => Assert.Equal(rhi_shader_type_to_mask(RHI_Shader_Type.Compute), x.stage));
    }

    [Fact]
    public void Output_order_follows_spartans_reflect()
    {
        // images, storage images, storage buffers, uniform buffers, push constants, (phalanx) samplers
        var order = ReflectProbe().Select(x => x.type).ToList();
        var expected = new[] { RHI_Descriptor_Type.Image, RHI_Descriptor_Type.TextureStorage, RHI_Descriptor_Type.StructuredBuffer,
                               RHI_Descriptor_Type.ConstantBuffer, RHI_Descriptor_Type.PushConstantBuffer, RHI_Descriptor_Type.Sampler };
        Assert.Equal(expected, order.Distinct());
    }

    [Fact]
    public void Block_sizes_are_the_declared_struct_size()
    {
        var d = ReflectProbe();
        // std140: mat4 view (64) + vec3 position @64 + float near @76 + Light[2] @80 stride 80 -> 80 + 160
        Assert.Equal(240u, Get(d, "BufferFrame").struct_size);
        // std430: mat4 (64) + vec4[2] (32)
        Assert.Equal(96u, Get(d, "pass").struct_size);
        // spartan only sizes constant and push constant buffers
        Assert.Equal(0u, Get(d, "output_buffer").struct_size);
    }

    [Fact]
    public void Arrays_runtime_arrays_and_bindless()
    {
        var d = ReflectProbe();
        RHI_Descriptor tex = Get(d, "tex");
        Assert.False(tex.as_array);
        Assert.Equal(0u, tex.array_length);
        Assert.False(tex.IsBindless());

        RHI_Descriptor arr = Get(d, "tex_array");
        Assert.True(arr.as_array);
        Assert.Equal(8u, arr.array_length);
        Assert.False(arr.IsBindless());

        RHI_Descriptor mats = Get(d, "material_textures");
        Assert.True(mats.as_array);
        Assert.Equal(rhi_max_array_size, mats.array_length);
        Assert.True(mats.IsBindless());
        Assert.True(Get(d, "samplers").IsBindless());
    }

    [Fact]
    public void Preserved_but_unreferenced_bindings_are_reported_unused()
    {
        var d = ReflectProbe();
        RHI_Descriptor unused = Get(d, "tex_unused"); // kept by preserve bindings (spartan: -fspv-preserve-bindings)
        Assert.False(unused.used);
        Assert.True(Get(d, "tex").used);
        Assert.True(Get(d, "BufferFrame").used);
    }

    [Fact]
    public void Blit_comp_reflects_spartans_blit_bindings()
    {
        var d = RHI_SpirvReflection.Reflect(compiler.CompileFile("blit.comp"), RHI_Shader_Type.Compute);
        Assert.Equal(RHI_Descriptor_Type.Image,          Get(d, "tex").type);
        Assert.Equal(rhi_shader_register_shift_t + 7,    Get(d, "tex").slot);
        Assert.Equal(RHI_Descriptor_Type.TextureStorage, Get(d, "tex_uav").type);
        Assert.Equal(rhi_shader_register_shift_u + 0,    Get(d, "tex_uav").slot);
        Assert.Equal(rhi_shader_register_shift_b + 0,    Get(d, "BufferFrame").slot);
        Assert.Equal(7u, Get(d, "samplers").space);
        Assert.DoesNotContain(d, x => x.type == RHI_Descriptor_Type.PushConstantBuffer); // blit never touches buffer_pass
    }

    [Fact]
    public void Pass_buffer_push_constant_is_the_generated_96_bytes()
    {
        const string src = """
            #version 460
            #extension GL_GOOGLE_include_directive : require
            #include "common_resources.glsl"
            layout(local_size_x = 1) in;
            void main() { imageStore(tex_uav, ivec2(0), buffer_pass.values[4]); }
            """;
        var d = RHI_SpirvReflection.Reflect(compiler.CompileSource(src, ShaderStage.Compute, "pass.comp"), RHI_Shader_Type.Compute);
        Assert.Equal(96u, Get(d, "BufferPass").struct_size);
        Assert.Equal((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<PassBufferData>(), Get(d, "BufferPass").struct_size);
    }

    [Fact]
    public void Combined_image_samplers_are_reflected_for_the_misuse_check()
    {
        const string src = """
            #version 460
            layout(set = 0, binding = 300) uniform sampler2D combined;
            layout(location = 0) out vec4 o;
            void main() { o = texture(combined, vec2(0)); }
            """;
        var d = RHI_SpirvReflection.Reflect(compiler.CompileSource(src, ShaderStage.Fragment, "c.frag"), RHI_Shader_Type.Pixel);
        RHI_Descriptor c = Get(d, "combined");
        Assert.Equal(RHI_Descriptor_Type.CombinedImageSampler, c.type);
        Assert.Equal(rhi_shader_type_to_mask(RHI_Shader_Type.Pixel), c.stage);
    }

    [Fact]
    public void Keep_names_strips_source_debug_info_but_not_names()
    {
        using var plain = new ShaderCompiler();
        using var debug = new ShaderCompiler { DebugInfo = true };
        uint[] stripped = compiler.CompileFile("blit.comp");
        uint[] full     = debug.CompileFile("blit.comp");
        Assert.True(stripped.Length < full.Length);
        Assert.StartsWith("_", Assert.Single(RHI_SpirvReflection.Reflect(plain.CompileFile("blit.comp"), RHI_Shader_Type.Compute), x => x.slot == 307).name);
        Assert.Equal("tex", Assert.Single(RHI_SpirvReflection.Reflect(stripped, RHI_Shader_Type.Compute), x => x.slot == 307).name);
    }

    [Fact]
    public void Rejects_non_spirv()
    {
        Assert.Throws<ArgumentException>(() => RHI_SpirvReflection.Reflect(new uint[] { 1, 2, 3, 4, 5 }, RHI_Shader_Type.Compute));
        Assert.Throws<ArgumentException>(() => RHI_SpirvReflection.Parse(new uint[] { RHI_SpirvReflection.Magic }));
    }

    [Fact]
    public void Shader_hash_depends_on_source_and_defines_not_their_order()
    {
        var a = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" };
        var b = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" };
        Assert.Equal(RHI_Shader.ComputeHash("src", a), RHI_Shader.ComputeHash("src", b));
        Assert.NotEqual(RHI_Shader.ComputeHash("src", a), RHI_Shader.ComputeHash("src2", a));
        Assert.NotEqual(RHI_Shader.ComputeHash("src", a), RHI_Shader.ComputeHash("src", new Dictionary<string, string> { ["A"] = "1" }));
        // key/value boundaries are delimited: {AB=C} != {A=BC}
        Assert.NotEqual(RHI_Shader.ComputeHash("s", new Dictionary<string, string> { ["AB"] = "C" }),
                        RHI_Shader.ComputeHash("s", new Dictionary<string, string> { ["A"] = "BC" }));
    }

    // golden values recorded from the hash as it shipped, so changes to how the bytes are fed in can't move them
    // (a changed hash invalidates every cached pipeline and shader)
    [Theory]
    [InlineData(0, 0xFC3AA8DACB128F8DUL)]
    [InlineData(1, 0xBDC6E6F84A44A59EUL)]
    [InlineData(2, 0x6E34DF25CC33357FUL)]
    [InlineData(3, 0xE49B755109E604CBUL)]
    [InlineData(4, 0xB94735118188BE8FUL)]
    public void Shader_hash_values_are_stable(int input, ulong expected)
    {
        ulong hash = input switch
        {
            0 => RHI_Shader.ComputeHash("", new Dictionary<string, string>()),
            1 => RHI_Shader.ComputeHash("void main() {}", new Dictionary<string, string>()),
            2 => RHI_Shader.ComputeHash("void main() {}", new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" }),
            3 => RHI_Shader.ComputeHash("void main() {}", new Dictionary<string, string> { ["DEBUG"] = "", ["ÜNICODE"] = "値" }, RHI_Shader_Type.Vertex, RHI_Vertex_Type.PosCol),
            _ => RHI_Shader.ComputeHash("#version 450\nlayout(location = 0) out vec4 c;\nvoid main() { c = vec4(1); }", new Dictionary<string, string> { ["X"] = "1" }, RHI_Shader_Type.Pixel)
        };
        Assert.Equal($"0x{expected:X16}", $"0x{hash:X16}");
    }
}
