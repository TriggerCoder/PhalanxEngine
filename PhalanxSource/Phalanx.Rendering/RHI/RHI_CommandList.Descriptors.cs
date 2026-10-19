// Port of the descriptor binding half of SpartanEngine vulkan/Vulkan_CommandList.cpp @ 4c07baa: set_dynamic
// (:1102-1132) and set_bindless (:1134-1155), SetConstantBuffer / SetBuffer / SetTexture routing to the current
// descriptor set layout, PushConstants (:2770-2800) and Dispatch.
// Deviations: vkCmdPushConstants instead of vkCmdPushConstants2 (1.4 entry point), the bindless sets are bound when the
// pipeline is set (spartan binds them in its pipeline-set path too), the state reset lives in BindPipeline.
// SetPipeline(RHI_Pipeline) binds a prebuilt compute pipeline without going through the pipeline cache.
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe partial class RHI_CommandList
{
    private RHI_Pipeline? m_pipeline;
    private RHI_DescriptorSetLayout? m_descriptor_layout_current;
    private DescriptorSet m_dynamic_descriptor_set;
    private PipelineLayout m_dynamic_pipeline_layout;
    private readonly uint[] m_dynamic_offsets = new uint[RHI_Definitions.rhi_max_dynamic_offsets];
    private int m_dynamic_offset_count;

    public RHI_DescriptorSetLayout? GetDescriptorLayoutCurrent() => m_descriptor_layout_current;

    public void SetPipeline(RHI_Pipeline pipeline)
    {
        RequireRecording();
        if (!pipeline.IsCompute)
            throw new ArgumentException("Graphics pipelines are set through SetPipelineState", nameof(pipeline));
        RenderPassEnd();
        m_pso = pipeline.GetState();
        m_render_pass_pending = false;
        BindPipeline(pipeline);
    }

    public void SetConstantBuffer(uint slot, RHI_Buffer constant_buffer) => RequireLayout().SetConstantBuffer(slot, constant_buffer);
    public void SetBuffer(uint slot, RHI_Buffer buffer) => RequireLayout().SetBuffer(slot, buffer);

    // storage textures are transitioned like spartan (everything parks in general)
    public void SetTexture(uint slot, RHI_Texture texture, uint mip_index = RHI_Definitions.rhi_all_mips, uint mip_range = 0, bool storage = false)
    {
        RHI_DescriptorSetLayout layout = RequireLayout();
        InsertBarrier(texture, RHI_Image_Layout.General, mip_index, mip_index == RHI_Definitions.rhi_all_mips ? 0 : System.Math.Max(mip_range, 1));
        layout.SetTexture(slot, texture, mip_index, mip_range, RHI_Definitions.rhi_all_mips, RHI_Image_Layout.General, storage);
    }

    public void PushConstants<T>(in T data) where T : unmanaged
    {
        RequireRecording();
        if (m_pipeline == null || m_pipeline.GetPushConstantStages() == 0)
            return;
        uint size = (uint)sizeof(T);
        if (size > RHI_Device.PropertyMaxPushConstantSize)
            throw new ArgumentException($"{size} bytes of push constants, the device allows {RHI_Device.PropertyMaxPushConstantSize}");
        fixed (T* p = &data)
            RHI_Context.Vk.CmdPushConstants(m_rhi_resource, m_pipeline.GetRhiResourceLayout(), m_pipeline.GetPushConstantStages(), 0, size, p);
    }

    public void Dispatch(uint x, uint y = 1, uint z = 1)
    {
        RequireRecording();
        if (m_pipeline == null || !m_pipeline.IsCompute)
            throw new InvalidOperationException($"{Name}: Dispatch without a compute pipeline");
        SetDynamic(m_pipeline);
        CheckpointWork("dispatch", x, y, z, instances: false);
        RHI_Context.Vk.CmdDispatch(m_rhi_resource, x, y, z);
    }

    private void SetDynamic(RHI_Pipeline pipeline)
    {
        DescriptorSet descriptor_set;
        Span<uint> offsets = stackalloc uint[(int)RHI_Definitions.rhi_max_dynamic_offsets];
        int count;
        lock (RHI_Device.GetDescriptorSetMutex())
        {
            RHI_DescriptorSetLayout layout = m_descriptor_layout_current!;
            descriptor_set = layout.GetOrCreateDescriptorSet();
            count = layout.GetDynamicOffsets(offsets);
        }

        PipelineLayout pipeline_layout = pipeline.GetRhiResourceLayout();
        if (m_dynamic_descriptor_set.Handle == descriptor_set.Handle && m_dynamic_pipeline_layout.Handle == pipeline_layout.Handle &&
            m_dynamic_offset_count == count && offsets[..count].SequenceEqual(m_dynamic_offsets.AsSpan(0, count)))
            return;

        fixed (uint* p_offsets = offsets)
            RHI_Context.Vk.CmdBindDescriptorSets(m_rhi_resource, pipeline.GetBindPoint(), pipeline_layout, 0, 1, &descriptor_set, (uint)count, p_offsets);

        m_dynamic_descriptor_set  = descriptor_set;
        m_dynamic_pipeline_layout = pipeline_layout;
        offsets[..count].CopyTo(m_dynamic_offsets);
        m_dynamic_offset_count = count;
    }

    private void SetBindless(RHI_Pipeline pipeline)
    {
        uint bindless_count = pipeline.SetCount - 1;
        if (bindless_count == 0)
            return;
        DescriptorSet* sets = stackalloc DescriptorSet[(int)bindless_count];
        lock (RHI_Device.GetDescriptorSetMutex())
            for (int i = 0; i < bindless_count; i++)
                sets[i] = RHI_Device.GetDescriptorSet((RHI_Device_Bindless_Resource)i);
        RHI_Context.Vk.CmdBindDescriptorSets(m_rhi_resource, pipeline.GetBindPoint(), pipeline.GetRhiResourceLayout(), 1, bindless_count, sets, 0, null);
    }

    private RHI_DescriptorSetLayout RequireLayout()
    {
        RequireRecording();
        return m_descriptor_layout_current ?? throw new InvalidOperationException($"{Name}: no pipeline set, bind one before resources");
    }

    private void RequireRecording()
    {
        if (m_state != RHI_CommandListState.Recording)
            throw new InvalidOperationException($"{Name}: recording required, the list is {m_state}");
    }
}
