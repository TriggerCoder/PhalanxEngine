using System.Runtime.InteropServices;
using Phalanx.Rendering.RHI;
using Silk.NET.Vulkan;
using Xunit;
using static Phalanx.Rendering.RHI.RHI_Definitions;

namespace Phalanx.Tests;

// no gpu: sampler hashing/create info, layout hashing and filtering, merging, bindless table, pool sizes (spartan @ 4c07baa)
public class RhiDescriptorLogicTests
{
    private static RHI_Descriptor D(string name, RHI_Descriptor_Type type, uint slot, uint space = 0, bool array = false, uint length = 0, uint size = 0,
                                    RHI_Shader_Type stage = RHI_Shader_Type.Compute) =>
        new(name, type, RHI_Image_Layout.General, slot, rhi_shader_type_to_mask(stage), size, array, length, space);

    [Fact]
    public void Sampler_hash_is_stable_and_covers_every_state()
    {
        ulong Hash(RHI_Filter min = RHI_Filter.Linear, RHI_Filter mag = RHI_Filter.Linear, RHI_Filter mip = RHI_Filter.Nearest,
                   RHI_Sampler_Address_Mode address = RHI_Sampler_Address_Mode.Wrap, RHI_Comparison_Function cmp = RHI_Comparison_Function.Never,
                   float aniso = 0, bool cmp_on = false, float bias = 0) =>
            RHI_Sampler.ComputeHash(min, mag, mip, address, cmp, aniso, cmp_on, bias);

        ulong reference = Hash();
        Assert.Equal(reference, Hash());
        var variants = new[]
        {
            Hash(min: RHI_Filter.Nearest), Hash(mag: RHI_Filter.Nearest), Hash(mip: RHI_Filter.Linear),
            Hash(address: RHI_Sampler_Address_Mode.Clamp), Hash(address: RHI_Sampler_Address_Mode.ClampToZero),
            Hash(cmp: RHI_Comparison_Function.Greater), Hash(aniso: 16), Hash(cmp_on: true), Hash(bias: -0.5f), Hash(bias: -0.0f)
        };
        Assert.All(variants, v => Assert.NotEqual(reference, v));
        Assert.Equal(variants.Length, variants.Distinct().Count());
    }

    [Fact]
    public void Sampler_create_info_follows_vulkan_sampler_cpp()
    {
        // spartan's compare_depth sampler (reverse-z)
        SamplerCreateInfo info = RHI_Sampler.BuildCreateInfo(RHI_Filter.Linear, RHI_Filter.Linear, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.ClampToZero,
                                                             RHI_Comparison_Function.Greater, 0, true, 0, 16);
        Assert.Equal(Filter.Linear, info.MinFilter);
        Assert.Equal(Filter.Linear, info.MagFilter);
        Assert.Equal(SamplerMipmapMode.Nearest, info.MipmapMode);
        Assert.Equal(SamplerAddressMode.ClampToBorder, info.AddressModeU);
        Assert.Equal(SamplerAddressMode.ClampToBorder, info.AddressModeW);
        Assert.True(info.CompareEnable);
        Assert.Equal(CompareOp.Greater, info.CompareOp);
        Assert.False(info.AnisotropyEnable);
        Assert.Equal(BorderColor.IntTransparentBlack, info.BorderColor);
        Assert.Equal(float.MaxValue, info.MaxLod);

        SamplerCreateInfo aniso = RHI_Sampler.BuildCreateInfo(RHI_Filter.Linear, RHI_Filter.Linear, RHI_Filter.Linear, RHI_Sampler_Address_Mode.MirrorOnce,
                                                              RHI_Comparison_Function.Always, 16, false, -0.25f, 8);
        Assert.True(aniso.AnisotropyEnable);
        Assert.Equal(8, aniso.MaxAnisotropy); // deviation: clamped to the device limit
        Assert.Equal(SamplerAddressMode.MirrorClampToEdge, aniso.AddressModeV);
        Assert.Equal(-0.25f, aniso.MipLodBias);
    }

    [Fact]
    public void Layout_hash_includes_array_length_and_block_size()
    {
        RHI_Descriptor[] a = { D("cb", RHI_Descriptor_Type.ConstantBuffer, 200, size: 64), D("t", RHI_Descriptor_Type.Image, 300, array: true, length: 4) };
        RHI_Descriptor[] b = { D("cb", RHI_Descriptor_Type.ConstantBuffer, 200, size: 80), D("t", RHI_Descriptor_Type.Image, 300, array: true, length: 4) };
        RHI_Descriptor[] c = { D("cb", RHI_Descriptor_Type.ConstantBuffer, 200, size: 64), D("t", RHI_Descriptor_Type.Image, 300, array: true, length: 8) };
        RHI_Descriptor[] d = { D("cb", RHI_Descriptor_Type.ConstantBuffer, 200, size: 64, stage: RHI_Shader_Type.Pixel), D("t", RHI_Descriptor_Type.Image, 300, array: true, length: 4) };
        ulong h = RHI_DescriptorSetLayout.ComputeLayoutHash(a);
        Assert.Equal(h, RHI_DescriptorSetLayout.ComputeLayoutHash(a.ToArray()));
        Assert.NotEqual(h, RHI_DescriptorSetLayout.ComputeLayoutHash(b));
        Assert.NotEqual(h, RHI_DescriptorSetLayout.ComputeLayoutHash(c));
        Assert.NotEqual(h, RHI_DescriptorSetLayout.ComputeLayoutHash(d));
    }

    [Fact]
    public void Layout_filter_drops_push_constants_and_bindless_and_rejects_misuse()
    {
        RHI_Descriptor[] all =
        {
            D("pass", RHI_Descriptor_Type.PushConstantBuffer, 0, size: 96),
            D("material_textures", RHI_Descriptor_Type.Image, 315, space: 1, array: true, length: rhi_max_array_size),
            D("samplers", RHI_Descriptor_Type.Sampler, 401, space: 7, array: true, length: 4),
            D("unbounded", RHI_Descriptor_Type.Image, 320, array: true, length: rhi_max_array_size), // bindless by length
            D("tex", RHI_Descriptor_Type.Image, 307),
            D("uav", RHI_Descriptor_Type.TextureStorage, 100),
        };
        var filtered = RHI_DescriptorSetLayout.FilterForLayout(all);
        Assert.Equal(new[] { "tex", "uav" }, filtered.Select(x => x.name));

        Assert.Throws<NotSupportedException>(() => RHI_DescriptorSetLayout.FilterForLayout(new[] { D("s", RHI_Descriptor_Type.CombinedImageSampler, 300) }));
        Assert.Throws<NotSupportedException>(() => RHI_DescriptorSetLayout.FilterForLayout(new[] { D("s", RHI_Descriptor_Type.Sampler, 400) }));
        Assert.Throws<InvalidOperationException>(() => RHI_DescriptorSetLayout.FilterForLayout(new[] { D("a", RHI_Descriptor_Type.Image, 300), D("b", RHI_Descriptor_Type.TextureStorage, 300) }));
    }

    [Fact]
    public void Merge_ors_stages_and_used_and_appends_new_slots()
    {
        var vs = D("cb", RHI_Descriptor_Type.ConstantBuffer, 200, size: 64, stage: RHI_Shader_Type.Vertex);
        vs.used = false;
        var list = new List<RHI_Descriptor> { vs };
        RHI_Device.MergeDescriptors(list, new[] { D("cb", RHI_Descriptor_Type.ConstantBuffer, 200, size: 64, stage: RHI_Shader_Type.Pixel),
                                                  D("tex", RHI_Descriptor_Type.Image, 307, stage: RHI_Shader_Type.Pixel),
                                                  D("tex_other_space", RHI_Descriptor_Type.Image, 200, space: 1, stage: RHI_Shader_Type.Pixel) });
        Assert.Equal(3, list.Count);
        Assert.Equal(rhi_shader_type_to_mask(RHI_Shader_Type.Vertex) | rhi_shader_type_to_mask(RHI_Shader_Type.Pixel), list[0].stage);
        Assert.True(list[0].used);
    }

    [Fact]
    public void Pipeline_set_count_keeps_spartans_11_sets_or_trims_to_the_device()
    {
        var uses_samplers = new[] { D("tex", RHI_Descriptor_Type.Image, 307), D("samplers", RHI_Descriptor_Type.Sampler, 401, space: 7, array: true, length: 8) };
        Assert.Equal(11u, RHI_Device.GetPipelineSetCount(uses_samplers, 32));
        Assert.Equal(8u, RHI_Device.GetPipelineSetCount(uses_samplers, 8));   // llvmpipe
        Assert.Equal(1u, RHI_Device.GetPipelineSetCount(new[] { D("tex", RHI_Descriptor_Type.Image, 307) }, 8));
        var uses_instances = new[] { D("geometry_instances", RHI_Descriptor_Type.StructuredBuffer, 323, space: 10) };
        Assert.Throws<NotSupportedException>(() => RHI_Device.GetPipelineSetCount(uses_instances, 8));

        // declared but never accessed (common_resources.glsl declares every bindless set): not counted
        var unused_instances = D("geometry_instances", RHI_Descriptor_Type.StructuredBuffer, 323, space: 10);
        unused_instances.used = false;
        Assert.Equal(8u, RHI_Device.GetPipelineSetCount(new[] { uses_samplers[0], uses_samplers[1], unused_instances }, 8));
    }

    [Fact]
    public void Bindless_table_matches_common_resources_bindless_hlsl()
    {
        // register(tN/sN, spaceS): set = enum + 1, binding = shift + N
        (RHI_Device_Bindless_Resource type, uint binding, uint count, DescriptorType vk)[] expected =
        {
            (RHI_Device_Bindless_Resource.MaterialTextures,   rhi_shader_register_shift_t + 15, rhi_max_array_size, DescriptorType.SampledImage),
            (RHI_Device_Bindless_Resource.MaterialParameters, rhi_shader_register_shift_t + 16, 1, DescriptorType.StorageBuffer),
            (RHI_Device_Bindless_Resource.LightParameters,    rhi_shader_register_shift_t + 17, 1, DescriptorType.StorageBuffer),
            (RHI_Device_Bindless_Resource.Aabbs,              rhi_shader_register_shift_t + 18, 1, DescriptorType.StorageBuffer),
            (RHI_Device_Bindless_Resource.DrawData,           rhi_shader_register_shift_t + 19, 1, DescriptorType.StorageBuffer),
            (RHI_Device_Bindless_Resource.SamplersComparison, rhi_shader_register_shift_s + 0,  1, DescriptorType.Sampler),
            (RHI_Device_Bindless_Resource.SamplersRegular,    rhi_shader_register_shift_s + 1,  8, DescriptorType.Sampler),
            (RHI_Device_Bindless_Resource.GeometryVertices,   rhi_shader_register_shift_t + 20, 1, DescriptorType.StorageBuffer),
            (RHI_Device_Bindless_Resource.GeometryIndices,    rhi_shader_register_shift_t + 22, 1, DescriptorType.StorageBuffer),
            (RHI_Device_Bindless_Resource.Instances,          rhi_shader_register_shift_t + 23, 1, DescriptorType.StorageBuffer),
        };
        Assert.Equal((int)RHI_Device_Bindless_Resource.Max, RHI_Device.BindlessConfigs.Length);
        foreach (var e in expected)
        {
            var cfg = RHI_Device.BindlessConfigs[(int)e.type];
            Assert.Equal(e.binding, cfg.Binding);
            Assert.Equal(e.count, cfg.Count);
            Assert.Equal(e.vk, cfg.Type);
        }
        Assert.Equal(9u, rhi_max_sampler_count); // 1 comparison + 8 regular
    }

    [Fact]
    public void Common_resources_samplers_land_in_the_regular_sampler_set()
    {
        using var compiler = new Phalanx.Rendering.ShaderCompiler { KeepNames = true };
        var d = RHI_SpirvReflection.Reflect(compiler.CompileFile("blit.comp"), RHI_Shader_Type.Compute);
        RHI_Descriptor samplers = Assert.Single(d, x => x.name == "samplers");
        Assert.Equal((uint)RHI_Device_Bindless_Resource.SamplersRegular + 1, samplers.space);
        Assert.Equal(RHI_Device.BindlessConfigs[(int)RHI_Device_Bindless_Resource.SamplersRegular].Binding, samplers.slot);
    }

    [Fact]
    public void Pool_sizes_and_descriptor_types()
    {
        var sizes = RHI_Device.GetDescriptorPoolSizes();
        Assert.Equal(rhi_max_array_size + 32 * rhi_max_descriptor_set_count, sizes.Single(s => s.Type == DescriptorType.SampledImage).DescriptorCount);
        Assert.Equal(32 * rhi_max_descriptor_set_count, sizes.Single(s => s.Type == DescriptorType.StorageImage).DescriptorCount); // deviation
        Assert.Equal(DescriptorType.StorageBufferDynamic, RHI_Device.GetDescriptorType(D("b", RHI_Descriptor_Type.StructuredBuffer, 100)));
        Assert.Equal(DescriptorType.UniformBufferDynamic, RHI_Device.GetDescriptorType(D("c", RHI_Descriptor_Type.ConstantBuffer, 200)));
        Assert.Equal(DescriptorType.SampledImage, RHI_Device.GetDescriptorType(D("t", RHI_Descriptor_Type.Image, 300)));
        Assert.Equal(DescriptorType.StorageImage, RHI_Device.GetDescriptorType(D("u", RHI_Descriptor_Type.TextureStorage, 100)));
        Assert.Throws<NotSupportedException>(() => RHI_Device.GetDescriptorType(D("p", RHI_Descriptor_Type.PushConstantBuffer, 0)));
    }

    [Fact]
    public void Binding_hash_uses_unique_object_ids()
    {
        var objects = Enumerable.Range(0, 1000).Select(_ => new object()).ToArray();
        Assert.Equal(1000, objects.Select(RHI_ObjectId.Get).Distinct().Count());
        Assert.Equal(RHI_ObjectId.Get(objects[3]), RHI_ObjectId.Get(objects[3]));

        var a = new RHI_DescriptorBinding { resource = objects[0] };
        var b = new RHI_DescriptorBinding { resource = objects[1] };
        Assert.NotEqual(a.GetHash(), b.GetHash());
        var c = a; c.mip = 2;
        Assert.NotEqual(a.GetHash(), c.GetHash());
        var unbound = new RHI_DescriptorBinding();
        Assert.False(unbound.IsBound());
        Assert.Equal(rhi_all_mips, unbound.array_layer);
    }

    [Fact]
    public void Missing_descriptor_features_are_named()
    {
        var all = new PhysicalDeviceVulkan12Features
        {
            DescriptorIndexing = true, DescriptorBindingVariableDescriptorCount = true, DescriptorBindingSampledImageUpdateAfterBind = true,
            DescriptorBindingStorageBufferUpdateAfterBind = true, DescriptorBindingPartiallyBound = true, RuntimeDescriptorArray = true,
            ShaderSampledImageArrayNonUniformIndexing = true, ShaderStorageBufferArrayNonUniformIndexing = true
        };
        var core = new PhysicalDeviceFeatures { SamplerAnisotropy = true, ShaderFloat64 = true, DepthClamp = true, FillModeNonSolid = true, WideLines = true };
        Assert.Null(RHI_Device.MissingRequiredFeature(all, core));
        var no_partial = all; no_partial.DescriptorBindingPartiallyBound = false;
        Assert.Equal("descriptorBindingPartiallyBound", RHI_Device.MissingRequiredFeature(no_partial, core));
        Assert.Equal("samplerAnisotropy", RHI_Device.MissingRequiredFeature(all, default));
    }
}

[Collection("gpu")]
public class RhiDescriptorGpuTests
{
    private static string WriteShader(string name, string source)
    {
        string dir = Path.Combine(Path.GetTempPath(), "phalanx-rhi5-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static void Run(Action body)
    {
        Skip.IfNot(Phalanx.Rendering.RenderDevice.IsVulkanAvailable(out string reason), reason);
        RHI_Device.Initialize(Array.Empty<string>(), validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            RHI_Device.DeletionQueueFlush();
            Assert.Equal(0, RHI_Device.MemoryGetStats().AllocationCount);
            Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal) || m.Contains("WarningBit", StringComparison.Ordinal)),
                        string.Join("\n", RHI_Device.ValidationMessages));
        }
        finally
        {
            RHI_Device.Destroy();
        }
    }

    private const string WriteSource = """
        #version 460
        layout(local_size_x = 64) in;
        layout(std140, set = 0, binding = 200) uniform BufferFrame { uint multiplier; uint pad0; uint pad1; uint pad2; };
        layout(push_constant, std430) uniform BufferPass { uint add; uint count; } pass;
        layout(std430, set = 0, binding = 100) buffer Output { uint data[]; } output_buffer;
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i < pass.count)
                output_buffer.data[i] = i * multiplier + pass.add;
        }
        """;

    [StructLayout(LayoutKind.Sequential)] private struct Push { public uint add, count; }

    private static RHI_Shader CompileCompute(string name, string source)
    {
        var shader = new RHI_Shader();
        shader.Compile(RHI_Shader_Type.Compute, WriteShader(name, source), async: false);
        Assert.True(shader.IsCompiled(), shader.LastError);
        return shader;
    }

    private static void Dispatch(RHI_Pipeline pipeline, Action<RHI_CommandList> bind, uint groups)
    {
        RHI_CommandList cmd = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
        cmd.SetPipeline(pipeline);
        bind(cmd);
        cmd.Dispatch(groups);
        cmd.InsertBarrierMemory();
        RHI_CommandList.ImmediateExecutionEnd(cmd);
    }

    [SkippableFact]
    public void Compute_shader_writes_a_buffer_through_reflected_descriptors()
    {
        Run(() =>
        {
            using RHI_Shader shader = CompileCompute("write.comp", WriteSource);
            Assert.Equal(new[] { RHI_Descriptor_Type.StructuredBuffer, RHI_Descriptor_Type.ConstantBuffer, RHI_Descriptor_Type.PushConstantBuffer },
                         shader.GetDescriptors().Select(d => d.type));
            using var pipeline = new RHI_Pipeline(shader, "write");
            Assert.Equal(1u, pipeline.SetCount); // no bindless sets referenced
            Assert.Equal(8u, pipeline.GetPushConstantSize());

            const uint count = 200;
            using var output   = new RHI_Buffer(RHI_Buffer_Type.Storage, 4, 256, default, mappable: false, "output");
            using var constant = new RHI_Buffer(RHI_Buffer_Type.Constant, 16, 1, MemoryMarshal.AsBytes(new uint[] { 3, 0, 0, 0 }.AsSpan()), mappable: true, "frame");

            Dispatch(pipeline, cmd =>
            {
                cmd.SetBuffer(0, output);           // u0 -> binding 100
                cmd.SetConstantBuffer(0, constant); // b0 -> binding 200
                cmd.PushConstants(new Push { add = 7, count = count });
            }, 4);

            uint[] data = MemoryMarshal.Cast<byte, uint>(RHI_Device.ReadbackBuffer(output)).ToArray();
            for (uint i = 0; i < 256; i++)
                Assert.Equal(i < count ? i * 3 + 7 : 0u, data[i]);
        });
    }

    [SkippableFact]
    public void Descriptor_sets_are_cached_by_binding_and_invalidated_with_the_resource()
    {
        Run(() =>
        {
            using RHI_Shader shader = CompileCompute("write_cache.comp", WriteSource);
            using var pipeline = new RHI_Pipeline(shader, "write_cache");
            using var constant = new RHI_Buffer(RHI_Buffer_Type.Constant, 16, 1, MemoryMarshal.AsBytes(new uint[] { 1, 0, 0, 0 }.AsSpan()), mappable: true, "frame");
            var a = new RHI_Buffer(RHI_Buffer_Type.Storage, 4, 64, default, mappable: false, "a");
            using var b = new RHI_Buffer(RHI_Buffer_Type.Storage, 4, 64, default, mappable: false, "b");

            int before = RHI_Device.GetDescriptorSets().Count;
            void Run(RHI_Buffer target) => Dispatch(pipeline, cmd => { cmd.SetBuffer(0, target); cmd.SetConstantBuffer(0, constant); cmd.PushConstants(new Push { count = 64 }); }, 1);

            Run(a);
            Assert.Equal(before + 1, RHI_Device.GetDescriptorSets().Count);
            Run(a); // same bindings, same set
            Assert.Equal(before + 1, RHI_Device.GetDescriptorSets().Count);
            Run(b);
            Assert.Equal(before + 2, RHI_Device.GetDescriptorSets().Count);

            // the same layout is shared by pipelines with the same reflected bindings
            using var pipeline2 = new RHI_Pipeline(shader, "write_cache_2");
            Assert.Same(pipeline.DescriptorSetLayout, pipeline2.DescriptorSetLayout);

            a.Dispose(); // sets referring to it are dropped (spartan DescriptorSetInvalidateReferencingResource)
            Assert.Equal(before + 1, RHI_Device.GetDescriptorSets().Count);

            // eviction after 300 unused frames, checked every 60
            Assert.Equal(0, RHI_Device.DescriptorSetTick(59));
            Assert.Equal(before + 1, RHI_Device.DescriptorSetTick(360) + before);
            Assert.Empty(RHI_Device.GetDescriptorSets());
            RHI_Device.DescriptorSetTick(0);
        });
    }

    [SkippableFact]
    public void Bindless_textures_and_samplers_are_read_by_index()
    {
        Run(() =>
        {
            const string src = """
                #version 460
                #extension GL_GOOGLE_include_directive : require
                #extension GL_EXT_nonuniform_qualifier : require
                #include "common_resources.glsl"
                layout(local_size_x = 1) in;
                layout(set = 1, binding = 315) uniform texture2D material_textures[];
                layout(std430, set = 0, binding = 120) buffer Output { uint data[]; } output_buffer; // 101 is tex_uav2 in common_resources.glsl
                void main()
                {
                    // index 5: the uploaded texture, index 6: never set (null descriptor -> 0)
                    output_buffer.data[0] = packUnorm4x8(texelFetch(material_textures[nonuniformEXT(5)], ivec2(1, 0), 0));
                    output_buffer.data[1] = packUnorm4x8(texture(sampler2D(material_textures[nonuniformEXT(5)], GET_SAMPLER(0)), vec2(0.25, 0.75)));
                    output_buffer.data[2] = packUnorm4x8(texelFetch(material_textures[nonuniformEXT(6)], ivec2(0), 0));
                }
                """;
            using RHI_Shader shader = CompileCompute("bindless.comp", src);
            using var pipeline = new RHI_Pipeline(shader, "bindless");
            Assert.True(pipeline.SetCount >= 8); // set 0 + bindless sets 1..7 (samplers)

            // 2x2 rgba8: (0,0) red, (1,0) green, (0,1) blue, (1,1) white
            byte[] pixels = { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 };
            var slice = new RHI_Texture_Slice();
            slice.mips.Add(new RHI_Texture_Mip { bytes = pixels });
            using var texture = new RHI_Texture(RHI_Texture_Type.Type2D, 2, 2, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Srv, "bindless_tex",
                                                new List<RHI_Texture_Slice> { slice });

            var textures = new RHI_Texture?[rhi_max_array_size];
            textures[5] = texture;
            RHI_Device.UpdateBindlessMaterials(textures, null);

            // spartan's sampler order: [0] comparison, [1..8] regular; regular[0] = point clamp here
            var samplers = new RHI_Sampler[rhi_max_sampler_count];
            samplers[0] = new RHI_Sampler(RHI_Filter.Linear, RHI_Filter.Linear, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.ClampToZero, RHI_Comparison_Function.Greater, 0, true, 0, "compare_depth");
            for (int i = 1; i < samplers.Length; i++)
                samplers[i] = new RHI_Sampler(RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Clamp, name: $"regular_{i}");
            RHI_Device.UpdateBindlessSamplers(samplers);

            using var output = new RHI_Buffer(RHI_Buffer_Type.Storage, 4, 4, default, mappable: false, "bindless_out");
            Dispatch(pipeline, cmd => cmd.SetBuffer(20, output), 1);

            uint[] data = MemoryMarshal.Cast<byte, uint>(RHI_Device.ReadbackBuffer(output)).ToArray();
            Assert.Equal(0xFF00FF00u, data[0]); // green, packUnorm4x8 puts r in the low byte
            Assert.Equal(0xFFFF0000u, data[1]); // blue at uv (0.25, 0.75)
            Assert.Equal(0u, data[2]);          // null descriptor
            foreach (RHI_Sampler s in samplers) s.Dispose();
        });
    }

    [SkippableFact]
    public void Shader_compiles_async_reloads_and_compiles_from_spirv()
    {
        Run(() =>
        {
            string path = WriteShader("reload.comp", WriteSource);
            using var shader = new RHI_Shader();
            shader.AddDefine("UNUSED_DEFINE", "2");
            shader.Compile(RHI_Shader_Type.Compute, path, async: true);
            shader.WaitForCompilation();
            Assert.True(shader.IsCompiled(), shader.LastError);
            Assert.Equal("reload", shader.ObjectName);
            Assert.Equal("reload.comp", shader.GetNames()[0]);
            ulong hash = shader.GetHash();
            ulong module = shader.GetRhiResource().Handle;
            int descriptors = shader.GetDescriptors().Count;

            // hot reload: a broken edit keeps the last good module
            File.WriteAllText(path, WriteSource.Replace("pass.add;", "pass.add +;"));
            shader.Compile(RHI_Shader_Type.Compute, path, async: true);
            shader.WaitForCompilation();
            Assert.True(shader.IsReloading());
            shader.ApplyReload();
            Assert.True(shader.ReloadFailed());
            Assert.Equal(module, shader.GetRhiResource().Handle);
            Assert.Equal(hash, shader.GetHash());

            // a good edit swaps the module and the hash
            File.WriteAllText(path, WriteSource.Replace("pass.add;", "pass.add + 1u;"));
            shader.Compile(RHI_Shader_Type.Compute, path, async: false);
            shader.ApplyReload();
            Assert.False(shader.ReloadFailed());
            Assert.NotEqual(module, shader.GetRhiResource().Handle);
            Assert.NotEqual(hash, shader.GetHash());
            Assert.Equal(descriptors, shader.GetDescriptors().Count);

            // precompiled spir-v (shipping builds), twice: reflection must not accumulate (spartan bug, see RHI_Shader.cs)
            uint[] spirv = shader.GetSpirv().ToArray();
            using var from_spirv = new RHI_Shader();
            from_spirv.CompileFromSpirv(RHI_Shader_Type.Compute, spirv, "precompiled");
            from_spirv.CompileFromSpirv(RHI_Shader_Type.Compute, spirv, "precompiled");
            Assert.True(from_spirv.IsCompiled());
            Assert.Equal(descriptors, from_spirv.GetDescriptors().Count);

            // a failing first compile reports failed, not succeeded
            using var broken = new RHI_Shader();
            broken.Compile(RHI_Shader_Type.Compute, WriteShader("broken.comp", "#version 460\nvoid main() { nope; }"), async: false);
            Assert.Equal(RHI_ShaderCompilationState.Failed, broken.GetCompilationState());
            Assert.Contains("nope", broken.LastError);
        });
    }

    [SkippableFact]
    public void Per_pass_combined_samplers_are_rejected_with_a_message()
    {
        Run(() =>
        {
            using RHI_Shader shader = CompileCompute("combined.comp", """
                #version 460
                layout(local_size_x = 1) in;
                layout(set = 0, binding = 300) uniform sampler2D combined;
                layout(std430, set = 0, binding = 100) buffer Output { vec4 data[]; } o;
                void main() { o.data[0] = textureLod(combined, vec2(0), 0); }
                """);
            var e = Assert.Throws<NotSupportedException>(() => new RHI_Pipeline(shader, "combined"));
            Assert.Contains("GET_SAMPLER", e.Message);
        });
    }
}
