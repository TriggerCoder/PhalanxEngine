// Port of SpartanEngine source/rhi/RHI_CommandList.h + vulkan/Vulkan_CommandList.cpp @ 4c07baa (first slice:
// begin/end, image barriers with synchronization2, clear via dynamic rendering or vkCmdClearColorImage, copy to buffer).
// TODO(port): pipelines, descriptors, draw/dispatch, timestamps, markers, render passes with attachments.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_CommandList : IDisposable
{
    private CommandBuffer m_rhi_resource;
    private readonly RHI_SyncPrimitive m_rendering_complete_fence;
    private RHI_CommandListState m_state = RHI_CommandListState.Idle;

    public RHI_Queue Queue { get; }
    public string Name { get; }
    public RHI_CommandListState State => m_state;
    public CommandBuffer GetRhiResource() => m_rhi_resource;
    public RHI_SyncPrimitive GetFence() => m_rendering_complete_fence;

    public RHI_CommandList(RHI_Queue queue, CommandPool pool, string name)
    {
        Queue = queue;
        Name  = name;
        CommandBufferAllocateInfo info = new()
        {
            SType              = StructureType.CommandBufferAllocateInfo,
            CommandPool        = pool,
            Level              = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        Check(RHI_Context.Vk.AllocateCommandBuffers(RHI_Context.Device, in info, out m_rhi_resource), "vkAllocateCommandBuffers");
        RHI_Device.SetResourceName((ulong)m_rhi_resource.Handle, RHI_Resource_Type.CommandList, name);
        m_rendering_complete_fence = new RHI_SyncPrimitive(RHI_SyncPrimitive_Type.Fence, name + "_fence");
    }

    // waits for the previous submission of this list to finish executing (spartan: WaitForExecution)
    public void WaitForExecution()
    {
        if (m_state != RHI_CommandListState.Submitted)
            return;
        m_rendering_complete_fence.Wait(ulong.MaxValue);
        m_state = RHI_CommandListState.Idle;
    }

    public void Begin()
    {
        WaitForExecution();
        if (m_state != RHI_CommandListState.Idle)
            throw new InvalidOperationException($"{Name}: Begin while {m_state}");

        Check(RHI_Context.Vk.ResetCommandBuffer(m_rhi_resource, 0), "vkResetCommandBuffer");
        CommandBufferBeginInfo info = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(RHI_Context.Vk.BeginCommandBuffer(m_rhi_resource, in info), "vkBeginCommandBuffer");
        m_state = RHI_CommandListState.Recording;
    }

    public void End()
    {
        if (m_state != RHI_CommandListState.Recording)
            throw new InvalidOperationException($"{Name}: End while {m_state}");
        Check(RHI_Context.Vk.EndCommandBuffer(m_rhi_resource), "vkEndCommandBuffer");
    }

    // called by RHI_Queue.Submit
    internal void OnSubmitted()
    {
        m_state = RHI_CommandListState.Submitted;
    }

    // spartan keeps engine images in GENERAL after the first transition, present source is the only other layout
    public void InsertBarrierImage(Image image, ImageAspectFlags aspect, ImageLayout layout_old, ImageLayout layout_new)
    {
        ImageMemoryBarrier2 barrier = new()
        {
            SType               = StructureType.ImageMemoryBarrier2,
            SrcStageMask        = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask       = layout_old == ImageLayout.Undefined ? AccessFlags2.None : AccessFlags2.MemoryWriteBit,
            DstStageMask        = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask       = layout_new == ImageLayout.PresentSrcKhr ? AccessFlags2.None : AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout           = layout_old,
            NewLayout           = layout_new,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image               = image,
            SubresourceRange    = new ImageSubresourceRange(aspect, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers)
        };
        DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        RHI_Context.Vk.CmdPipelineBarrier2(m_rhi_resource, in dependency);
    }

    // clear through dynamic rendering (load op clear), the way the renderer will clear render targets
    public void ClearRenderTarget(ImageView view, ImageLayout layout, uint width, uint height, float r, float g, float b, float a)
    {
        RenderingAttachmentInfo color = new()
        {
            SType       = StructureType.RenderingAttachmentInfo,
            ImageView   = view,
            ImageLayout = layout,
            LoadOp      = AttachmentLoadOp.Clear,
            StoreOp     = AttachmentStoreOp.Store,
            ClearValue  = new ClearValue(new ClearColorValue(r, g, b, a))
        };
        RenderingInfo info = new()
        {
            SType                = StructureType.RenderingInfo,
            RenderArea           = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)),
            LayerCount           = 1,
            ColorAttachmentCount = 1,
            PColorAttachments    = &color
        };
        RHI_Context.Vk.CmdBeginRendering(m_rhi_resource, in info);
        RHI_Context.Vk.CmdEndRendering(m_rhi_resource);
    }

    // clear without a view (image must be in GENERAL or TRANSFER_DST)
    public void ClearColorImage(Image image, ImageLayout layout, float r, float g, float b, float a)
    {
        ClearColorValue color = new(r, g, b, a);
        ImageSubresourceRange range = new(ImageAspectFlags.ColorBit, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers);
        RHI_Context.Vk.CmdClearColorImage(m_rhi_resource, image, layout, in color, 1, in range);
    }

    public void CopyImageToBuffer(Image image, ImageLayout layout, Silk.NET.Vulkan.Buffer buffer, uint width, uint height)
    {
        BufferImageCopy region = new()
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent      = new Extent3D(width, height, 1)
        };
        RHI_Context.Vk.CmdCopyImageToBuffer(m_rhi_resource, image, layout, buffer, 1, in region);
    }

    public void Dispose()
    {
        m_rendering_complete_fence.Dispose();
        // the command buffer is freed with its pool (RHI_Queue)
    }
}
