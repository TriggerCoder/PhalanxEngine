// Preview of SpartanEngine vulkan/Vulkan_Pipeline.cpp @ 4c07baa: the pipeline layout (set 0 = the merged
// descriptor set layout, sets 1..10 = the bindless layouts, push constant ranges from the reflected push constant
// blocks, :85-180) and the compute pipeline (:520-540). It exists so it can dispatch a compute shader and prove the
// descriptors end to end.
// Deviations: the set count follows RHI_Device.GetPipelineSetCount (maxBoundDescriptorSets, see RHI_Device.Descriptors.cs).
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_Pipeline : IDisposable
{
    private Pipeline m_rhi_resource;
    private PipelineLayout m_rhi_resource_layout;
    private ShaderStageFlags m_push_constant_stages;
    private uint m_push_constant_size;

    public string Name { get; }
    public RHI_DescriptorSetLayout DescriptorSetLayout { get; }
    public uint SetCount { get; }
    public bool IsCompute => true;

    public RHI_Pipeline(RHI_Shader compute_shader, string name)
    {
        if (compute_shader.GetShaderStage() != RHI_Shader_Type.Compute)
            throw new ArgumentException("Pipelines are compute only", nameof(compute_shader));
        Name = name;
        RHI_Shader[] shaders = { compute_shader };
        DescriptorSetLayout = RHI_Device.GetOrCreateDescriptorSetLayout(shaders, name);
        SetCount = RHI_Device.GetPipelineSetCount(shaders);

        // layouts: must match the order of appearance in common_resources.glsl
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[(int)SetCount];
        layouts[0] = DescriptorSetLayout.GetRhiResource();
        for (int i = 1; i < SetCount; i++)
            layouts[i] = RHI_Device.GetDescriptorSetLayout((RHI_Device_Bindless_Resource)(i - 1));

        // push constant buffers
        PushConstantRange* ranges = stackalloc PushConstantRange[4];
        uint range_count = 0;
        foreach (RHI_Descriptor descriptor in DescriptorSetLayout.GetDescriptors())
        {
            if (descriptor.type != RHI_Descriptor_Type.PushConstantBuffer)
                continue;
            if (descriptor.struct_size > RHI_Device.PropertyMaxPushConstantSize)
                throw new NotSupportedException($"push constant block \"{descriptor.name}\" is {descriptor.struct_size} bytes, the device allows {RHI_Device.PropertyMaxPushConstantSize}");
            ranges[range_count] = new PushConstantRange { Offset = 0, Size = descriptor.struct_size, StageFlags = ToVulkanStageFlags(descriptor.stage) };
            m_push_constant_stages |= ranges[range_count].StageFlags;
            m_push_constant_size    = System.Math.Max(m_push_constant_size, descriptor.struct_size);
            range_count++;
        }

        PipelineLayoutCreateInfo layout_info = new()
        {
            SType                  = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount         = SetCount,
            PSetLayouts            = layouts,
            PushConstantRangeCount = range_count,
            PPushConstantRanges    = ranges
        };
        Check(RHI_Context.Vk.CreatePipelineLayout(RHI_Context.Device, in layout_info, null, out m_rhi_resource_layout), "vkCreatePipelineLayout");
        RHI_Device.SetResourceName(m_rhi_resource_layout.Handle, RHI_Resource_Type.PipelineLayout, name);

        nint entry = SilkMarshal.StringToPtr(compute_shader.GetEntryPoint());
        try
        {
            ComputePipelineCreateInfo info = new()
            {
                SType  = StructureType.ComputePipelineCreateInfo,
                Stage  = new PipelineShaderStageCreateInfo
                {
                    SType  = StructureType.PipelineShaderStageCreateInfo,
                    Stage  = ShaderStageFlags.ComputeBit,
                    Module = compute_shader.GetRhiResource(),
                    PName  = (byte*)entry
                },
                Layout = m_rhi_resource_layout
            };
            Check(RHI_Context.Vk.CreateComputePipelines(RHI_Context.Device, default, 1, in info, null, out m_rhi_resource), "vkCreateComputePipelines");
        }
        finally { SilkMarshal.Free(entry); }
        RHI_Device.SetResourceName(m_rhi_resource.Handle, RHI_Resource_Type.Pipeline, name);
    }

    public Pipeline GetRhiResource() => m_rhi_resource;
    public PipelineLayout GetRhiResourceLayout() => m_rhi_resource_layout;
    public ShaderStageFlags GetPushConstantStages() => m_push_constant_stages;
    public uint GetPushConstantSize() => m_push_constant_size;

    public void Dispose()
    {
        if (m_rhi_resource.Handle != 0)
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Pipeline, m_rhi_resource.Handle);
        if (m_rhi_resource_layout.Handle != 0)
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.PipelineLayout, m_rhi_resource_layout.Handle);
        m_rhi_resource = default;
        m_rhi_resource_layout = default;
    }
}
