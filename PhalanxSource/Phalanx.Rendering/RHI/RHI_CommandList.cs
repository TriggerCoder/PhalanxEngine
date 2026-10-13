// Port of SpartanEngine source/rhi/RHI_CommandList.h + vulkan/Vulkan_CommandList.cpp @ 4c07baa (first slice:
// begin/end, image barriers with synchronization2, clear via dynamic rendering or vkCmdClearColorImage, copy to buffer).
// TODO(port): pipelines, descriptors, draw/dispatch, timestamps, markers, render passes with attachments.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

// a timeline semaphore and the value a submission signals (spartan: RHI_Work)
internal readonly record struct RHI_Work(RHI_SyncPrimitive? Semaphore, ulong Value)
{
    public bool IsValid => Semaphore != null && Value != 0;
    public bool IsComplete() => !IsValid || Semaphore!.IsSignaled(Value);
    public void Wait(ulong timeout_nanoseconds = ulong.MaxValue) { if (IsValid) Semaphore!.Wait(timeout_nanoseconds, Value); }
}

// everything that was submitted or reserved (recording) at capture time (spartan: RHI_PendingWork)
internal sealed class RHI_PendingWork
{
    public IReadOnlyList<RHI_Work> Work { get; }
    public RHI_PendingWork(IReadOnlyList<RHI_Work> work) => Work = work;
    public bool IsComplete()
    {
        foreach (RHI_Work w in Work)
            if (!w.IsComplete())
                return false;
        return true;
    }
    public void Wait() { foreach (RHI_Work w in Work) w.Wait(); }
}

internal sealed unsafe class RHI_CommandList : IDisposable
{
    private static readonly List<RHI_CommandList> s_lists = new();

    private CommandBuffer m_rhi_resource;
    private readonly RHI_SyncPrimitive m_rendering_complete_semaphore_timeline;
    private ulong m_last_timeline_signal_value;
    private RHI_CommandListState m_state = RHI_CommandListState.Idle;

    public RHI_Queue Queue { get; }
    public string Name { get; }
    public RHI_CommandListState State => m_state;
    public CommandBuffer GetRhiResource() => m_rhi_resource;
    public RHI_SyncPrimitive GetTimelineSemaphore() => m_rendering_complete_semaphore_timeline;
    public ulong GetLastTimelineSignalValue() => m_last_timeline_signal_value;
    public RHI_Work GetWork() => new(m_rendering_complete_semaphore_timeline, m_last_timeline_signal_value);
    public bool IsExecuting() => m_state == RHI_CommandListState.Submitted && !GetWork().IsComplete();

    // spartan: CapturePendingWork. submitted lists that have not completed, plus recording lists with the value
    // their next submission will signal (a resource released mid-recording may still be referenced by them).
    public static RHI_PendingWork CapturePendingWork()
    {
        var work = new List<RHI_Work>();
        lock (s_lists)
        {
            foreach (RHI_CommandList list in s_lists)
            {
                if (list.m_state == RHI_CommandListState.Submitted && !list.GetWork().IsComplete())
                    work.Add(list.GetWork());
                else if (list.m_state == RHI_CommandListState.Recording)
                    work.Add(new RHI_Work(list.m_rendering_complete_semaphore_timeline, list.m_last_timeline_signal_value + 1));
            }
        }
        return new RHI_PendingWork(work);
    }

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
        m_rendering_complete_semaphore_timeline = new RHI_SyncPrimitive(RHI_SyncPrimitive_Type.SemaphoreTimeline, name + "_timeline");
        lock (s_lists) s_lists.Add(this);
    }

    // waits for the previous submission of this list to finish executing (spartan: WaitForExecution)
    public void WaitForExecution()
    {
        if (m_state != RHI_CommandListState.Submitted)
            return;
        m_rendering_complete_semaphore_timeline.Wait(ulong.MaxValue, m_last_timeline_signal_value);
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

    // called by RHI_Queue.Submit, returns the value this submission signals
    internal ulong NextTimelineSignalValue() => m_last_timeline_signal_value + 1;
    internal void OnSubmitted(ulong value)
    {
        m_last_timeline_signal_value = value;
        m_rendering_complete_semaphore_timeline.SetValue(value);
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
        lock (s_lists) s_lists.Remove(this);
        m_rendering_complete_semaphore_timeline.Dispose();
        // the command buffer is freed with its pool (RHI_Queue)
    }
}
