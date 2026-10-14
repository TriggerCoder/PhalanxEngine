// Port of SpartanEngine source/rhi/RHI_DescriptorSetLayout.h/.cpp + vulkan/Vulkan_DescriptorSetLayout.cpp @ 4c07baa
// Deviations:
// - No SetAccelerationStructure yet (ray tracing step).
// - Per-pass Sampler / CombinedImageSampler descriptors are rejected with a clear message (GLSL only, spartan's dxc
//   path never produces them; use texture2D + the bindless samplers, see common_resources.glsl).
// - More than 255 bindings throws (spartan writes past its fixed array, Vulkan_DescriptorSetLayout.cpp:76-80).
// - Stage flags only carry the stages the device can run (RHI_Device.SupportedStageFlags), so the validation layer
//   doesn't flag extension bits that were never enabled.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Definitions;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_DescriptorSetLayout : IDisposable
{
    private const int max_bindings = 255;

    private DescriptorSetLayout m_rhi_resource;
    private readonly bool m_owns_resource = true;

    // layout info (immutable after construction)
    private readonly RHI_Descriptor[] m_descriptors;
    private readonly Dictionary<uint, int> m_slot_to_index = new();
    private readonly ulong m_layout_hash;

    // binding state (mutable)
    private readonly RHI_DescriptorBinding[] m_bindings;
    private ulong m_binding_hash;
    private bool m_dirty = true;

    public string Name { get; }

    public RHI_DescriptorSetLayout(ReadOnlySpan<RHI_Descriptor> descriptors, string name)
    {
        Name          = name;
        m_descriptors = descriptors.ToArray();
        m_bindings    = new RHI_DescriptorBinding[m_descriptors.Length];
        for (int i = 0; i < m_descriptors.Length; i++)
        {
            m_bindings[i] = new RHI_DescriptorBinding();
            m_slot_to_index[m_descriptors[i].slot] = i;
        }
        m_layout_hash = ComputeLayoutHash(m_descriptors);
        CreateRhiResource();
    }

    // spartan's copy constructor: shares the vulkan layout, fresh bindings
    public RHI_DescriptorSetLayout(RHI_DescriptorSetLayout source)
    {
        Name            = source.Name;
        m_rhi_resource  = source.m_rhi_resource;
        m_owns_resource = false;
        m_descriptors   = source.m_descriptors;
        m_slot_to_index = source.m_slot_to_index;
        m_layout_hash   = source.m_layout_hash;
        m_bindings      = new RHI_DescriptorBinding[m_descriptors.Length];
        for (int i = 0; i < m_bindings.Length; i++) m_bindings[i] = new RHI_DescriptorBinding();
    }

    // immutable, based on slots, stages, types and sizes (RHI_DescriptorSetLayout.cpp:39-47)
    public static ulong ComputeLayoutHash(ReadOnlySpan<RHI_Descriptor> descriptors)
    {
        ulong hash = 0;
        foreach (RHI_Descriptor d in descriptors)
        {
            hash = rhi_hash_combine(hash, d.slot);
            hash = rhi_hash_combine(hash, d.stage);
            hash = rhi_hash_combine(hash, (ulong)d.type);
            hash = rhi_hash_combine(hash, d.as_array ? 1UL : 0UL);
            hash = rhi_hash_combine(hash, d.array_length);
            hash = rhi_hash_combine(hash, d.struct_size);
        }
        return hash;
    }

    private int FindBinding(uint slot) => m_slot_to_index.TryGetValue(slot, out int i) ? i : -1;

    // binding api - O(1) slot lookup, the slot is the unshifted register like spartan
    public void SetConstantBuffer(uint slot, RHI_Buffer constant_buffer)
    {
        uint actual_slot = slot + rhi_shader_register_shift_b;
        int index = FindBinding(actual_slot);
        if (index < 0) return;

        ref RHI_DescriptorBinding binding = ref m_bindings[index];
        ref readonly RHI_Descriptor descriptor = ref m_descriptors[index];
        ulong range          = constant_buffer.GetStride();
        uint  dynamic_offset = constant_buffer.GetOffset();
        if (ReferenceEquals(binding.resource, constant_buffer) && binding.range == range && binding.dynamic_offset == dynamic_offset)
            return;

        binding.resource       = constant_buffer;
        binding.range          = range;
        binding.dynamic_offset = dynamic_offset;

        if (constant_buffer.GetStrideUnaligned() != descriptor.struct_size)
            throw new InvalidOperationException($"Size mismatch between CPU ({constant_buffer.GetStrideUnaligned()}) and GPU ({descriptor.struct_size}) side constant buffer \"{descriptor.name}\"");
        if (binding.dynamic_offset % binding.range != 0)
            throw new InvalidOperationException("Incorrect dynamic offset");

        m_dirty = true;
    }

    public void SetBuffer(uint slot, RHI_Buffer? buffer)
    {
        if (buffer == null)
            return;

        int index = FindBinding(slot + rhi_shader_register_shift_u);
        if (index < 0)
            index = FindBinding(slot + rhi_shader_register_shift_t);
        if (index < 0)
            return;

        ref RHI_DescriptorBinding binding = ref m_bindings[index];
        ulong range          = buffer.GetObjectSize();
        uint  dynamic_offset = buffer.GetOffset();
        if (ReferenceEquals(binding.resource, buffer) && binding.range == range && binding.dynamic_offset == dynamic_offset)
            return;

        binding.resource       = buffer;
        binding.range          = range;
        binding.dynamic_offset = dynamic_offset;
        m_dirty = true;
    }

    public bool SetTexture(uint slot, RHI_Texture? texture, uint mip_index, uint mip_range, uint array_layer, RHI_Image_Layout layout, bool storage)
    {
        if (layout != RHI_Image_Layout.General)
            throw new ArgumentException("textures are bound in the general layout", nameof(layout));

        uint shift = storage ? rhi_shader_register_shift_u : rhi_shader_register_shift_t;
        int index = FindBinding(slot + shift);
        if (index < 0)
            return false;

        ref RHI_DescriptorBinding binding = ref m_bindings[index];
        if (ReferenceEquals(binding.resource, texture) && binding.layout == layout && binding.mip == mip_index && binding.mip_range == mip_range && binding.array_layer == array_layer)
            return true;

        binding.resource    = texture;
        binding.layout      = layout;
        binding.mip         = mip_index;
        binding.mip_range   = mip_range;
        binding.array_layer = array_layer;
        m_dirty = true;
        return true;
    }

    public bool ResolveTextureBindingOverlap(RHI_Texture? texture, uint mip_index, uint mip_range, uint array_layer, bool storage)
    {
        if (texture == null)
            return false;

        bool overlap = false;
        uint clear_mip_start = mip_index == rhi_all_mips ? 0 : mip_index;
        uint clear_mip_count = mip_index == rhi_all_mips ? texture.GetMipCount() : (mip_range == 0 ? 1 : mip_range); // spartan: GetResidentMipCount (streaming, later)
        uint clear_mip_end   = clear_mip_start + clear_mip_count;

        for (int i = 0; i < m_bindings.Length; i++)
        {
            ref RHI_DescriptorBinding binding = ref m_bindings[i];
            ref readonly RHI_Descriptor descriptor = ref m_descriptors[i];
            bool type_matches = storage ? descriptor.type == RHI_Descriptor_Type.TextureStorage : descriptor.type == RHI_Descriptor_Type.Image;
            if (!type_matches || !ReferenceEquals(binding.resource, texture))
                continue;

            uint binding_mip_start = binding.mip == rhi_all_mips ? 0 : binding.mip;
            uint binding_mip_count = binding.mip == rhi_all_mips ? texture.GetMipCount() : (binding.mip_range == 0 ? 1 : binding.mip_range);
            uint binding_mip_end   = binding_mip_start + binding_mip_count;
            bool mip_overlap       = clear_mip_start < binding_mip_end && binding_mip_start < clear_mip_end;
            bool layer_overlap     = array_layer == rhi_all_mips || binding.array_layer == rhi_all_mips || array_layer == binding.array_layer;
            if (mip_overlap && layer_overlap && binding.layout != RHI_Image_Layout.General)
            {
                binding.layout = RHI_Image_Layout.General;
                m_dirty = true;
                overlap = true;
            }
        }
        return overlap;
    }

    // dynamic offsets for bound buffers, in descriptor order (the order vulkan expects: binding number order, the
    // descriptors are sorted by slot when merged)
    public int GetDynamicOffsets(Span<uint> offsets)
    {
        int count = 0;
        for (int i = 0; i < m_descriptors.Length; i++)
        {
            ref readonly RHI_Descriptor descriptor = ref m_descriptors[i];
            if (descriptor.IsBindless())
                continue; // phalanx: bindless buffers aren't in this set (spartan filters them out before, at merge time)
            if (descriptor.type is RHI_Descriptor_Type.StructuredBuffer or RHI_Descriptor_Type.ConstantBuffer)
            {
                if (count >= rhi_max_dynamic_offsets)
                    throw new InvalidOperationException("Pipeline has more dynamic buffers than rhi_max_dynamic_offsets");
                offsets[count++] = m_bindings[i].dynamic_offset;
            }
        }
        return count;
    }

    // state management
    public void ClearBindings()
    {
        for (int i = 0; i < m_bindings.Length; i++)
            m_bindings[i].Reset();
        m_dirty = true;
    }

    public ulong ComputeBindingHash()
    {
        ulong hash = m_layout_hash;
        foreach (RHI_DescriptorBinding binding in m_bindings)
            hash = rhi_hash_combine(hash, binding.GetHash());
        return hash;
    }

    // the caller holds RHI_Device.GetDescriptorSetMutex() (spartan: set_dynamic locks it)
    public DescriptorSet GetOrCreateDescriptorSet()
    {
        if (m_dirty)
        {
            m_binding_hash = ComputeBindingHash();
            m_dirty = false;
        }

        Dictionary<ulong, RHI_DescriptorSet> descriptor_sets = RHI_Device.GetDescriptorSets();
        if (!descriptor_sets.TryGetValue(m_binding_hash, out RHI_DescriptorSet? set))
        {
            var combined = new RHI_DescriptorWithBinding[m_descriptors.Length];
            for (int i = 0; i < m_descriptors.Length; i++)
                combined[i] = new RHI_DescriptorWithBinding { descriptor = m_descriptors[i], binding = m_bindings[i] };
            set = new RHI_DescriptorSet(combined, this, Name);
            descriptor_sets[m_binding_hash] = set;
        }

        set.MarkUsed(RHI_Device.GetDescriptorSetFrame());
        return set.GetResource();
    }

    public bool IsDirty() => m_dirty;
    public ReadOnlySpan<RHI_Descriptor> GetDescriptors() => m_descriptors;
    public ReadOnlySpan<RHI_DescriptorBinding> GetBindings() => m_bindings;
    public ulong GetLayoutHash() => m_layout_hash;
    public DescriptorSetLayout GetRhiResource() => m_rhi_resource;

    // the bindings vkCreateDescriptorSetLayout gets: no push constants, no bindless (pure, tested without a gpu)
    public static List<RHI_Descriptor> FilterForLayout(ReadOnlySpan<RHI_Descriptor> descriptors)
    {
        var filtered = new List<RHI_Descriptor>(descriptors.Length);
        foreach (RHI_Descriptor desc in descriptors)
        {
            if (desc.type == RHI_Descriptor_Type.PushConstantBuffer || desc.IsBindless())
                continue;
            if (desc.type is RHI_Descriptor_Type.Sampler or RHI_Descriptor_Type.CombinedImageSampler)
                throw new NotSupportedException($"\"{desc.name}\" (set {desc.space}, binding {desc.slot}): per-pass samplers are not supported, " +
                                                "declare a texture2D and sample it with the bindless samplers (GET_SAMPLER in common_resources.glsl)");
            filtered.Add(desc);
        }

        // validate unique bindings within a set, space maps to the vulkan set index so slot can repeat across spaces
        var unique_bindings = new HashSet<ulong>();
        foreach (RHI_Descriptor desc in filtered)
            if (!unique_bindings.Add(((ulong)desc.space << 32) | desc.slot))
                throw new InvalidOperationException($"Duplicate binding slot detected: {desc.slot} (\"{desc.name}\")");
        if (filtered.Count > max_bindings)
            throw new InvalidOperationException($"{filtered.Count} bindings, at most {max_bindings} are supported");
        return filtered;
    }

    private void CreateRhiResource()
    {
        List<RHI_Descriptor> filtered = FilterForLayout(m_descriptors);
        int count = filtered.Count;
        DescriptorSetLayoutBinding* layout_bindings = stackalloc DescriptorSetLayoutBinding[System.Math.Max(count, 1)];
        DescriptorBindingFlags* binding_flags = stackalloc DescriptorBindingFlags[System.Math.Max(count, 1)];
        for (int i = 0; i < count; i++)
        {
            RHI_Descriptor desc = filtered[i];
            layout_bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding            = desc.slot,
                DescriptorType     = RHI_Device.GetDescriptorType(desc),
                DescriptorCount    = desc.as_array ? desc.array_length : 1,
                StageFlags         = ToVulkanStageFlags(desc.stage) & RHI_Device.SupportedStageFlags,
                PImmutableSamplers = null
            };
            binding_flags[i] = DescriptorBindingFlags.PartiallyBoundBit;
        }

        DescriptorSetLayoutBindingFlagsCreateInfo flags_info = new()
        {
            SType         = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount  = (uint)count,
            PBindingFlags = binding_flags
        };
        DescriptorSetLayoutCreateInfo create_info = new()
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            PNext        = count > 0 ? &flags_info : null, // spartan: the empty layout has no flags info either
            BindingCount = (uint)count,
            PBindings    = count > 0 ? layout_bindings : null
        };
        Check(RHI_Context.Vk.CreateDescriptorSetLayout(RHI_Context.Device, in create_info, null, out m_rhi_resource), "vkCreateDescriptorSetLayout");
        RHI_Device.SetResourceName(m_rhi_resource.Handle, RHI_Resource_Type.DescriptorSetLayout, Name);
    }

    public void Dispose()
    {
        if (m_rhi_resource.Handle != 0 && m_owns_resource)
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.DescriptorSetLayout, m_rhi_resource.Handle);
        m_rhi_resource = default;
    }
}
