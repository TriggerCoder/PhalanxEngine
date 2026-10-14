// Port of the descriptor half of SpartanEngine vulkan/Vulkan_Device.cpp @ 4c07baa: descriptor pools
// (create/grow/find room/release, :1092-1186), the descriptor set and layout caches with eviction (:2286-2340),
// merge_descriptors / get_or_create_descriptor_set_layout (:1188-1362), the bindless sets (:1364-1550) and
// RHI_Device::AllocateDescriptorSet / GetDescriptorSet(Layout) / GetDescriptorType / UpdateBindless* (:2556-2720).
// Deviations:
// - STORAGE_IMAGE pool size is 32 * rhi_max_descriptor_set_count like the other per-pass types; spartan sizes it
//   rhi_max_array_size * rhi_max_descriptor_set_count (8.4M descriptors, Vulkan_Device.cpp:1098), nothing bindless is a
//   storage image, so it reads as a typo for the "+" of the sampled-image line above it.
// - No acceleration-structure pool size (no ray tracing yet). The pipeline cache lives in RHI_Device.Pipelines.cs.
// - The layout cache key also hashes as_array / array_length / struct_size (spartan hashes slot, stage and type only,
//   :1344-1349, so two pipelines whose bindings differ only in array length or block size share one layout, while
//   RHI_DescriptorSetLayout's own hash includes them).
// - Bindless stage flags are the stages the device can run (SupportedStageFlags) instead of every stage incl. mesh,
//   task and ray tracing bits whose extensions may not be enabled.
// - Unset bindless material textures get a null descriptor while the renderer has no checkerboard (RHI used alone).
// - maxBoundDescriptorSets: spartan always binds set 0 + the 10 bindless sets (11). The spec minimum is 4, llvmpipe
//   reports 8. GetPipelineSetCount trims the trailing bindless sets a pipeline doesn't access when the device has < 11.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Definitions;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    private sealed class DescriptorPool { public Silk.NET.Vulkan.DescriptorPool Handle; public uint Allocated; }

    private static readonly object s_descriptor_mutex = new();
    private static readonly List<DescriptorPool> s_descriptor_pools = new();
    private static readonly Dictionary<ulong, DescriptorPool> s_set_to_pool = new();
    private static readonly Dictionary<ulong, RHI_DescriptorSet> s_descriptor_sets = new();
    private static readonly Dictionary<ulong, RHI_DescriptorSetLayout> s_descriptor_set_layouts = new();
    private static ulong s_descriptor_frame;
    private static uint s_allocated_descriptor_sets;
    public const ulong DescriptorSetMaxUnusedFrames = 300;

    // bindless: (type, register shift, slot, count, name), indexed by RHI_Device_Bindless_Resource
    internal readonly record struct BindlessConfig(DescriptorType Type, uint RegisterShift, uint Slot, uint Count, string Name)
    {
        public uint Binding => RegisterShift + Slot;
    }

    internal static readonly BindlessConfig[] BindlessConfigs =
    {
        new(DescriptorType.SampledImage,  rhi_shader_register_shift_t, 15, rhi_max_array_size, "material_textures"),   // MaterialTextures
        new(DescriptorType.StorageBuffer, rhi_shader_register_shift_t, 16, 1,                  "material_parameters"), // MaterialParameters
        new(DescriptorType.StorageBuffer, rhi_shader_register_shift_t, 17, 1,                  "light_parameters"),    // LightParameters
        new(DescriptorType.StorageBuffer, rhi_shader_register_shift_t, 18, 1,                  "aabbs"),               // Aabbs
        new(DescriptorType.StorageBuffer, rhi_shader_register_shift_t, 19, 1,                  "draw_data"),           // DrawData
        new(DescriptorType.Sampler,       rhi_shader_register_shift_s, 0,  1,                  "samplers_comparison"), // SamplersComparison
        new(DescriptorType.Sampler,       rhi_shader_register_shift_s, 1,  8,                  "samplers_regular"),    // SamplersRegular
        new(DescriptorType.StorageBuffer, rhi_shader_register_shift_t, 20, 1,                  "geometry_vertices"),   // GeometryVertices
        new(DescriptorType.StorageBuffer, rhi_shader_register_shift_t, 22, 1,                  "geometry_indices"),    // GeometryIndices
        new(DescriptorType.StorageBuffer, rhi_shader_register_shift_t, 23, 1,                  "instances"),           // Instances
    };

    private static readonly DescriptorSetLayout[] s_bindless_layouts = new DescriptorSetLayout[(int)RHI_Device_Bindless_Resource.Max];
    private static readonly DescriptorSet[] s_bindless_sets = new DescriptorSet[(int)RHI_Device_Bindless_Resource.Max];

    // stages the device can execute (set in CreateDevice), used to mask layout stage flags
    public static ShaderStageFlags SupportedStageFlags { get; private set; } = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;

    // pool sizes per pool (pure, so the gpu-free tests can check them)
    public static DescriptorPoolSize[] GetDescriptorPoolSizes() => new[]
    {
        new DescriptorPoolSize(DescriptorType.Sampler,              32 * rhi_max_descriptor_set_count),
        new DescriptorPoolSize(DescriptorType.SampledImage,         rhi_max_array_size + 32 * rhi_max_descriptor_set_count),
        new DescriptorPoolSize(DescriptorType.StorageImage,         32 * rhi_max_descriptor_set_count), // deviation, see header
        new DescriptorPoolSize(DescriptorType.StorageBuffer,        32 * rhi_max_descriptor_set_count),
        new DescriptorPoolSize(DescriptorType.StorageBufferDynamic, 32 * rhi_max_descriptor_set_count),
        new DescriptorPoolSize(DescriptorType.UniformBufferDynamic, 32 * rhi_max_descriptor_set_count),
    };

    private static Silk.NET.Vulkan.DescriptorPool CreateDescriptorPool()
    {
        DescriptorPoolSize[] sizes = GetDescriptorPoolSizes();
        fixed (DescriptorPoolSize* p_sizes = sizes)
        {
            DescriptorPoolCreateInfo info = new()
            {
                SType         = StructureType.DescriptorPoolCreateInfo,
                Flags         = DescriptorPoolCreateFlags.UpdateAfterBindBit | DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                PoolSizeCount = (uint)sizes.Length,
                PPoolSizes    = p_sizes,
                MaxSets       = rhi_max_descriptor_set_count
            };
            Check(RHI_Context.Vk.CreateDescriptorPool(RHI_Context.Device, in info, null, out Silk.NET.Vulkan.DescriptorPool pool), "vkCreateDescriptorPool");
            return pool;
        }
    }

    private static DescriptorPool GrowPool()
    {
        var pool = new DescriptorPool { Handle = CreateDescriptorPool() };
        s_descriptor_pools.Add(pool);
        return pool;
    }

    private static DescriptorPool? FindPoolWithRoom()
    {
        for (int i = s_descriptor_pools.Count - 1; i >= 0; i--)
            if (s_descriptor_pools[i].Allocated < rhi_max_descriptor_set_count)
                return s_descriptor_pools[i];
        return null;
    }

    private static void DescriptorsInitialize()
    {
        GrowPool();
        s_descriptor_frame = 0;
        s_allocated_descriptor_sets = 0;
        for (int i = 0; i < (int)RHI_Device_Bindless_Resource.Max; i++)
            BindlessCreateLayoutAndSet((RHI_Device_Bindless_Resource)i);
        // bindless sets live in the first pool for the lifetime of the device
        s_descriptor_pools[0].Allocated += (uint)RHI_Device_Bindless_Resource.Max;
    }

    // after the deletion queue was flushed (sets queued there go back to their pool first)
    private static void DescriptorsDestroy()
    {
        Vk vk = RHI_Context.Vk;
        lock (s_descriptor_mutex)
        {
            foreach (RHI_DescriptorSetLayout layout in s_descriptor_set_layouts.Values)
                layout.Dispose();
            s_descriptor_set_layouts.Clear();
            s_descriptor_sets.Clear();
            s_set_to_pool.Clear();
        }
        DeletionQueueFlush(); // the cached layouts just queued themselves
        for (int i = 0; i < s_bindless_layouts.Length; i++)
        {
            if (s_bindless_layouts[i].Handle != 0)
                vk.DestroyDescriptorSetLayout(RHI_Context.Device, s_bindless_layouts[i], null);
            s_bindless_layouts[i] = default;
            s_bindless_sets[i]    = default;
        }
        // destroying a pool frees every set in it
        foreach (DescriptorPool pool in s_descriptor_pools)
            vk.DestroyDescriptorPool(RHI_Context.Device, pool.Handle, null);
        s_descriptor_pools.Clear();
        s_allocated_descriptor_sets = 0;
    }

    public static object GetDescriptorSetMutex() => s_descriptor_mutex;
    public static Dictionary<ulong, RHI_DescriptorSet> GetDescriptorSets() => s_descriptor_sets;
    public static ulong GetDescriptorSetFrame() => Volatile.Read(ref s_descriptor_frame);
    public static int GetDescriptorPoolCount() { lock (s_descriptor_mutex) return s_descriptor_pools.Count; }
    public static uint GetAllocatedDescriptorSetCount() => s_allocated_descriptor_sets;

    public static DescriptorSet AllocateDescriptorSet(RHI_DescriptorSetLayout layout)
    {
        if (s_descriptor_pools.Count == 0)
            throw new InvalidOperationException("no descriptor pool, RHI_Device.Initialize has not been called");

        DescriptorSetLayout vk_layout = layout.GetRhiResource();
        DescriptorSetAllocateInfo info = new()
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorSetCount = 1,
            PSetLayouts        = &vk_layout
        };

        // grow before the current pools are exhausted so validation never sees a failed allocate
        DescriptorPool pool = FindPoolWithRoom() ?? GrowPool();
        info.DescriptorPool = pool.Handle;
        DescriptorSet set;
        Result result = RHI_Context.Vk.AllocateDescriptorSets(RHI_Context.Device, in info, &set);
        if (result is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)
        {
            pool = GrowPool();
            info.DescriptorPool = pool.Handle;
            result = RHI_Context.Vk.AllocateDescriptorSets(RHI_Context.Device, in info, &set);
        }
        Check(result, "vkAllocateDescriptorSets");

        pool.Allocated++;
        s_set_to_pool[set.Handle] = pool;
        s_allocated_descriptor_sets++;
        return set;
    }

    // deletion queue target (spartan: release_descriptor_set)
    private static void ReleaseDescriptorSet(ulong resource)
    {
        lock (s_descriptor_mutex)
        {
            if (s_descriptor_pools.Count == 0 || !s_set_to_pool.Remove(resource, out DescriptorPool? pool))
                return;
            DescriptorSet set = new(resource);
            RHI_Context.Vk.FreeDescriptorSets(RHI_Context.Device, pool.Handle, 1, &set);
            if (pool.Allocated > 0) pool.Allocated--;
            if (s_allocated_descriptor_sets > 0) s_allocated_descriptor_sets--;
        }
    }

    // spartan RHI_Device::Tick, the descriptor part: evict sets unused for 300+ frames, checked every 60 frames
    public static int DescriptorSetTick(ulong frame_count)
    {
        Volatile.Write(ref s_descriptor_frame, frame_count);
        if (frame_count % 60 != 0)
            return 0;
        int evicted = 0;
        lock (s_descriptor_mutex)
        {
            foreach (var (hash, set) in s_descriptor_sets.ToArray())
            {
                if (frame_count - set.GetLastUsedFrame() > DescriptorSetMaxUnusedFrames)
                {
                    DeletionQueueAdd(RHI_Resource_Type.DescriptorSet, set.GetResource().Handle);
                    s_descriptor_sets.Remove(hash);
                    evicted++;
                }
            }
        }
        return evicted;
    }

    // cached sets are keyed on the resource, a later hash collision would return a set holding destroyed gpu handles
    public static void DescriptorSetInvalidateReferencingResource(object? resource)
    {
        if (resource == null || !IsInitialized)
            return;
        lock (s_descriptor_mutex)
        {
            foreach (var (hash, set) in s_descriptor_sets.ToArray())
            {
                if (set.IsReferingToResource(resource))
                {
                    DeletionQueueAdd(RHI_Resource_Type.DescriptorSet, set.GetResource().Handle);
                    s_descriptor_sets.Remove(hash);
                }
            }
        }
    }

    public static DescriptorType GetDescriptorType(in RHI_Descriptor descriptor) => descriptor.type switch
    {
        RHI_Descriptor_Type.Image                 => DescriptorType.SampledImage,
        RHI_Descriptor_Type.TextureStorage        => DescriptorType.StorageImage,
        RHI_Descriptor_Type.StructuredBuffer      => DescriptorType.StorageBufferDynamic,
        RHI_Descriptor_Type.ConstantBuffer        => DescriptorType.UniformBufferDynamic,
        RHI_Descriptor_Type.AccelerationStructure => DescriptorType.AccelerationStructureKhr,
        _ => throw new NotSupportedException($"Unhandled descriptor type {descriptor.type}")
    };

    // merge_descriptors: same slot + space -> or the stages and the used flag, new ones are appended
    public static void MergeDescriptors(List<RHI_Descriptor> base_descriptors, IEnumerable<RHI_Descriptor> additional_descriptors)
    {
        foreach (RHI_Descriptor additional in additional_descriptors)
        {
            int index = base_descriptors.FindIndex(d => d.slot == additional.slot && d.space == additional.space);
            if (index >= 0)
            {
                RHI_Descriptor merged = base_descriptors[index];
                merged.stage |= additional.stage;
                merged.used  |= additional.used;
                base_descriptors[index] = merged;
            }
            else
            {
                base_descriptors.Add(additional);
            }
        }
    }

    // get_descriptors_from_pipeline_state: merge the stages (bindless ones dropped), sorted by slot
    public static List<RHI_Descriptor> GetDescriptorsFromShaders(IEnumerable<RHI_Shader> shaders)
    {
        var merged = new List<RHI_Descriptor>();
        foreach (RHI_Shader shader in shaders)
        {
            if (!shader.IsCompiled())
                throw new InvalidOperationException($"shader \"{shader.ObjectName}\" is not compiled ({shader.GetCompilationState()})");
            foreach (RHI_Descriptor d in shader.GetDescriptors())
            {
                int index = merged.FindIndex(x => x.slot == d.slot && x.space == d.space);
                if (index >= 0)
                {
                    RHI_Descriptor m = merged[index];
                    m.stage |= d.stage;
                    m.used  |= d.used;
                    merged[index] = m;
                }
                else if (!d.IsBindless())
                {
                    merged.Add(d);
                }
            }
        }
        merged.Sort((a, b) => a.slot.CompareTo(b.slot));
        return merged;
    }

    // get_or_create_descriptor_set_layout, the key includes array length and block size (see header)
    public static RHI_DescriptorSetLayout GetOrCreateDescriptorSetLayout(IReadOnlyList<RHI_Shader> shaders, string name)
    {
        List<RHI_Descriptor> descriptors = GetDescriptorsFromShaders(shaders);
        ulong hash = RHI_DescriptorSetLayout.ComputeLayoutHash(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(descriptors));
        lock (s_descriptor_mutex)
        {
            if (!s_descriptor_set_layouts.TryGetValue(hash, out RHI_DescriptorSetLayout? layout))
            {
                layout = new RHI_DescriptorSetLayout(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(descriptors), name);
                s_descriptor_set_layouts[hash] = layout;
            }
            return layout;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // bindless

    // how many sets a pipeline layout gets: set 0 + every bindless set (spartan), or up to the highest bindless set the
    // shaders access when the device can't bind 11 sets. Bindings are preserved, so common_resources.glsl declares every
    // bindless set in every module; only the accessed ones (used) count, the others are never statically used
    public static uint GetPipelineSetCount(IEnumerable<RHI_Shader> shaders) =>
        GetPipelineSetCount(shaders.SelectMany(s => s.GetDescriptors().ToArray()), PropertyMaxBoundDescriptorSets);

    public static uint GetPipelineSetCount(IEnumerable<RHI_Descriptor> descriptors, uint max_bound_descriptor_sets)
    {
        uint all = 1 + (uint)RHI_Device_Bindless_Resource.Max;
        if (max_bound_descriptor_sets >= all)
            return all;
        uint highest = 0;
        foreach (RHI_Descriptor d in descriptors)
            if (d.used)
                highest = System.Math.Max(highest, d.space);
        if (highest >= max_bound_descriptor_sets)
            throw new NotSupportedException($"the shaders use descriptor set {highest}, the device binds at most {max_bound_descriptor_sets} sets");
        return highest + 1;
    }

    public static DescriptorSet GetDescriptorSet(RHI_Device_Bindless_Resource resource_type) => s_bindless_sets[(int)resource_type];
    public static DescriptorSetLayout GetDescriptorSetLayout(RHI_Device_Bindless_Resource resource_type) => s_bindless_layouts[(int)resource_type];

    private static void BindlessCreateLayoutAndSet(RHI_Device_Bindless_Resource type)
    {
        int index = (int)type;
        BindlessConfig cfg = BindlessConfigs[index];

        DescriptorSetLayoutBinding layout_binding = new()
        {
            Binding         = cfg.Binding,
            DescriptorType  = cfg.Type,
            DescriptorCount = cfg.Count,
            StageFlags      = SupportedStageFlags
        };
        DescriptorBindingFlags binding_flags = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.VariableDescriptorCountBit | DescriptorBindingFlags.UpdateAfterBindBit;
        DescriptorSetLayoutBindingFlagsCreateInfo binding_flags_info = new()
        {
            SType         = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount  = 1,
            PBindingFlags = &binding_flags
        };
        DescriptorSetLayoutCreateInfo layout_info = new()
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            PNext        = &binding_flags_info,
            Flags        = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 1,
            PBindings    = &layout_binding
        };
        Check(RHI_Context.Vk.CreateDescriptorSetLayout(RHI_Context.Device, in layout_info, null, out s_bindless_layouts[index]), "vkCreateDescriptorSetLayout (bindless)");
        SetResourceName(s_bindless_layouts[index].Handle, RHI_Resource_Type.DescriptorSetLayout, cfg.Name);

        uint count = cfg.Count;
        DescriptorSetVariableDescriptorCountAllocateInfo count_info = new()
        {
            SType              = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
            DescriptorSetCount = 1,
            PDescriptorCounts  = &count
        };
        DescriptorSetLayout vk_layout = s_bindless_layouts[index];
        DescriptorSetAllocateInfo alloc_info = new()
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            PNext              = &count_info,
            DescriptorPool     = s_descriptor_pools[0].Handle,
            DescriptorSetCount = 1,
            PSetLayouts        = &vk_layout
        };
        fixed (DescriptorSet* p_set = &s_bindless_sets[index])
            Check(RHI_Context.Vk.AllocateDescriptorSets(RHI_Context.Device, in alloc_info, p_set), "vkAllocateDescriptorSets (bindless)");
        SetResourceName(s_bindless_sets[index].Handle, RHI_Resource_Type.DescriptorSet, cfg.Name);
    }

    private static void BindlessUpdateTextures(ReadOnlySpan<RHI_Texture?> textures)
    {
        const int index = (int)RHI_Device_Bindless_Resource.MaterialTextures;
        BindlessConfig cfg = BindlessConfigs[index];
        var image_infos = new DescriptorImageInfo[cfg.Count]; // 16384 * 24 bytes, too large for the stack
        // unset slots get the renderer's checkerboard (spartan :1482), a null descriptor before the renderer made it
        ImageView fallback = Renderer.GetStandardTexture(Renderer_StandardTexture.Checkerboard)?.GetRhiSrv() ?? default;
        for (int i = 0; i < cfg.Count; i++)
        {
            RHI_Texture? tex = i < textures.Length ? textures[i] : null;
            ImageView srv = tex != null ? tex.GetRhiSrv() : fallback;
            image_infos[i] = new DescriptorImageInfo { ImageView = srv, ImageLayout = ImageLayout.General };
        }
        fixed (DescriptorImageInfo* p_infos = image_infos)
        {
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet, DstSet = s_bindless_sets[index], DstBinding = cfg.Binding,
                DstArrayElement = 0, DescriptorType = DescriptorType.SampledImage, DescriptorCount = cfg.Count, PImageInfo = p_infos
            };
            RHI_Context.Vk.UpdateDescriptorSets(RHI_Context.Device, 1, &write, 0, null);
        }
    }

    private static void BindlessUpdateSamplers(RHI_Device_Bindless_Resource type, ReadOnlySpan<RHI_Sampler> samplers)
    {
        // max sampler count is small enough for the stack
        const int max_samplers = 16;
        if (samplers.Length > max_samplers)
            throw new ArgumentOutOfRangeException(nameof(samplers));
        DescriptorImageInfo* image_infos = stackalloc DescriptorImageInfo[max_samplers];
        for (int i = 0; i < samplers.Length; i++)
            image_infos[i] = new DescriptorImageInfo { Sampler = samplers[i].GetRhiResource() };
        BindlessConfig cfg = BindlessConfigs[(int)type];
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet, DstSet = s_bindless_sets[(int)type], DstBinding = cfg.Binding,
            DstArrayElement = 0, DescriptorType = DescriptorType.Sampler, DescriptorCount = (uint)samplers.Length, PImageInfo = image_infos
        };
        RHI_Context.Vk.UpdateDescriptorSets(RHI_Context.Device, 1, &write, 0, null);
    }

    private static void BindlessUpdateBuffer(RHI_Device_Bindless_Resource type, RHI_Buffer buffer)
    {
        DescriptorBufferInfo buffer_info = new() { Buffer = buffer.GetRhiResource(), Offset = 0, Range = buffer.GetObjectSize() };
        BindlessConfig cfg = BindlessConfigs[(int)type];
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet, DstSet = s_bindless_sets[(int)type], DstBinding = cfg.Binding,
            DstArrayElement = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, PBufferInfo = &buffer_info
        };
        RHI_Context.Vk.UpdateDescriptorSets(RHI_Context.Device, 1, &write, 0, null);
    }

    public static void UpdateBindlessMaterials(ReadOnlySpan<RHI_Texture?> textures, RHI_Buffer? parameters)
    {
        lock (s_descriptor_mutex)
        {
            if (!textures.IsEmpty)
                BindlessUpdateTextures(textures);
            if (parameters != null)
                BindlessUpdateBuffer(RHI_Device_Bindless_Resource.MaterialParameters, parameters);
        }
    }

    public static void UpdateBindlessLights(RHI_Buffer? parameters)            { if (parameters != null) lock (s_descriptor_mutex) BindlessUpdateBuffer(RHI_Device_Bindless_Resource.LightParameters, parameters); }
    public static void UpdateBindlessAABBs(RHI_Buffer? buffer)                 { if (buffer != null) lock (s_descriptor_mutex) BindlessUpdateBuffer(RHI_Device_Bindless_Resource.Aabbs, buffer); }
    public static void UpdateBindlessDrawData(RHI_Buffer? buffer)              { if (buffer != null) lock (s_descriptor_mutex) BindlessUpdateBuffer(RHI_Device_Bindless_Resource.DrawData, buffer); }
    public static void UpdateBindlessGeometryVertices(RHI_Buffer? buffer)      { if (buffer != null) lock (s_descriptor_mutex) BindlessUpdateBuffer(RHI_Device_Bindless_Resource.GeometryVertices, buffer); }
    public static void UpdateBindlessGeometryIndices(RHI_Buffer? buffer)       { if (buffer != null) lock (s_descriptor_mutex) BindlessUpdateBuffer(RHI_Device_Bindless_Resource.GeometryIndices, buffer); }
    public static void UpdateBindlessInstances(RHI_Buffer? buffer)             { if (buffer != null) lock (s_descriptor_mutex) BindlessUpdateBuffer(RHI_Device_Bindless_Resource.Instances, buffer); }

    // samplers[0] is the comparison sampler, samplers[1..8] the regular ones (spartan: needs >= 9, else ignored)
    public static void UpdateBindlessSamplers(ReadOnlySpan<RHI_Sampler> samplers)
    {
        if (samplers.Length < (int)rhi_max_sampler_count)
            return;
        lock (s_descriptor_mutex)
        {
            BindlessUpdateSamplers(RHI_Device_Bindless_Resource.SamplersComparison, samplers[..1]);
            BindlessUpdateSamplers(RHI_Device_Bindless_Resource.SamplersRegular, samplers.Slice(1, 8));
        }
    }
}
