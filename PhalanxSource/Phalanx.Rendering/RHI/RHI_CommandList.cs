// Port of SpartanEngine source/rhi/RHI_CommandList.h + vulkan/Vulkan_CommandList.cpp @ 4c07baa (first slice:
// begin/end, image barriers with synchronization2, clear via dynamic rendering or vkCmdClearColorImage, copy to buffer).
// Queries: RHI_CommandList.Queries.cs, markers: RHI_CommandList.Markers.cs.
// TODO(port): resource hazard tracking.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

// one region of RHI_CommandList.CopyBuffer, in bytes
internal readonly record struct RHI_BufferCopy(ulong SourceOffset, ulong DestinationOffset, ulong Size);

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

internal sealed unsafe partial class RHI_CommandList : IDisposable
{
    private static readonly List<RHI_CommandList> s_lists = new();
    internal static object ListsLock => s_lists;
    internal static IReadOnlyList<RHI_CommandList> AllLists => s_lists;

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
        QueriesInitialize();
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
        QueriesBeforeBegin();
        m_markers.Clear();
        m_marker_work.Clear();

        Check(RHI_Context.Vk.ResetCommandBuffer(m_rhi_resource, 0), "vkResetCommandBuffer");
        m_pipeline = null; // bound state does not survive a reset
        m_descriptor_layout_current = null;
        m_dynamic_descriptor_set  = default;
        m_dynamic_pipeline_layout = default;
        m_dynamic_offset_count    = 0;
        ResetRenderState();
        CommandBufferBeginInfo info = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(RHI_Context.Vk.BeginCommandBuffer(m_rhi_resource, in info), "vkBeginCommandBuffer");
        m_state = RHI_CommandListState.Recording;
        QueriesBegin();
    }

    public void End()
    {
        if (m_state != RHI_CommandListState.Recording)
            throw new InvalidOperationException($"{Name}: End while {m_state}");
        RequireMarkersClosed();
        RenderPassEnd();
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
        SuspendRenderPass();
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
        SuspendRenderPass();
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
        SuspendRenderPass();
        ClearColorValue color = new(r, g, b, a);
        ImageSubresourceRange range = new(ImageAspectFlags.ColorBit, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers);
        RHI_Context.Vk.CmdClearColorImage(m_rhi_resource, image, layout, in color, 1, in range);
    }

    // spartan: RHI_CommandList::ClearTexture, every mip and array layer of a colour texture to one value. The texture
    // needs RHI_Texture_ClearBlit (transfer dst); it is parked in general first, the clear is made visible to what reads
    // it next
    public void ClearTexture(RHI_Texture texture, Phalanx.Math.Color color)
    {
        if (m_state != RHI_CommandListState.Recording)
            throw new InvalidOperationException($"{Name}: ClearTexture while {m_state}");
        if (texture.IsDepthFormat())
            throw new ArgumentException($"{texture.Name}: ClearTexture clears colour textures", nameof(texture));
        InsertBarrier(texture, RHI_Image_Layout.General);
        ClearColorImage(texture.GetRhiResource(), ImageLayout.General, color.R, color.G, color.B, color.A);
        InsertBarrierMemory();
    }

    public void CopyImageToBuffer(Image image, ImageLayout layout, Silk.NET.Vulkan.Buffer buffer, uint width, uint height)
    {
        SuspendRenderPass();
        BufferImageCopy region = new()
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent      = new Extent3D(width, height, 1)
        };
        RHI_Context.Vk.CmdCopyImageToBuffer(m_rhi_resource, image, layout, buffer, 1, in region);
    }

    // ------------------------------------------------------------------------------------------------------------
    // immediate execution (spartan: ImmediateExecutionBegin/End). one at a time per queue type; the list comes from
    // the device queue of that type (spartan keeps separate immediate queues).
    private static readonly object[] s_immediate_mutexes = { new(), new(), new(), new() };

    public static RHI_CommandList ImmediateExecutionBegin(RHI_Queue_Type queue_type)
    {
        Monitor.Enter(s_immediate_mutexes[(int)queue_type]);
        try
        {
            RHI_CommandList cmd_list = RHI_Device.GetQueue(queue_type).NextCommandList();
            cmd_list.Begin();
            cmd_list.m_immediate_queue_type = queue_type;
            return cmd_list;
        }
        catch
        {
            Monitor.Exit(s_immediate_mutexes[(int)queue_type]);
            throw;
        }
    }

    public static RHI_Work ImmediateExecutionEnd(RHI_CommandList cmd_list, bool wait = true)
    {
        RHI_Queue_Type queue_type = cmd_list.m_immediate_queue_type ?? throw new InvalidOperationException($"{cmd_list.Name} is not an immediate list");
        try
        {
            cmd_list.End();
            RHI_Work work = cmd_list.Queue.Submit(cmd_list, null, null);
            if (wait)
                cmd_list.WaitForExecution();
            return work;
        }
        finally
        {
            cmd_list.m_immediate_queue_type = null;
            Monitor.Exit(s_immediate_mutexes[(int)queue_type]);
        }
    }

    private RHI_Queue_Type? m_immediate_queue_type;

    // ------------------------------------------------------------------------------------------------------------
    // texture layout tracking (spartan: InsertBarrier(RHI_Barrier::image_layout(texture, layout, mip, range))).
    // engine textures always end up in GENERAL; the tracker is per mip (all array layers share it) like spartan.

    public void InsertBarrier(RHI_Texture texture, RHI_Image_Layout layout, uint mip_index = RHI_Definitions.rhi_all_mips, uint mip_range = 0)
    {
        if (m_state != RHI_CommandListState.Recording)
            throw new InvalidOperationException($"{Name}: InsertBarrier while {m_state}");
        _ = layout; // spartan: textures are parked in general regardless of the requested layout
        const RHI_Image_Layout target_layout = RHI_Image_Layout.General;

        bool mip_specified = mip_index != RHI_Definitions.rhi_all_mips;
        uint index = mip_specified ? mip_index : 0;
        uint range = mip_specified ? mip_range : texture.GetMipCount();
        if (index + range > RHI_Definitions.rhi_max_mip_count || index + range > texture.GetMipCount() || range == 0)
            throw new ArgumentOutOfRangeException(nameof(mip_index), $"{texture.Name}: mips {index}+{range}");

        RHI_Image_Layout first = texture.GetLayout(index);
        bool all_match_target = true, all_same_source = true;
        for (uint i = index; i < index + range; i++)
        {
            if (texture.GetLayout(i) != target_layout) all_match_target = false;
            if (texture.GetLayout(i) != first)         all_same_source  = false;
        }
        if (all_match_target)
            return;

        SuspendRenderPass();
        ImageAspectFlags aspect = texture.GetAspectMask();
        Span<ImageMemoryBarrier2> barriers = stackalloc ImageMemoryBarrier2[(int)RHI_Definitions.rhi_max_mip_count];
        int count = 0;
        if (all_same_source)
            barriers[count++] = CreateImageBarrier(first, target_layout, texture.GetRhiResource(), aspect, index, range);
        else
            for (uint i = index; i < index + range; i++)
                if (texture.GetLayout(i) != target_layout)
                    barriers[count++] = CreateImageBarrier(texture.GetLayout(i), target_layout, texture.GetRhiResource(), aspect, i, 1);

        fixed (ImageMemoryBarrier2* p = barriers)
        {
            DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = (uint)count, PImageMemoryBarriers = p };
            RHI_Context.Vk.CmdPipelineBarrier2(m_rhi_resource, in dependency);
        }
        texture.SetLayoutDirect(index, range, target_layout);
    }

    // conservative stages/access like spartan's Auto scope (no shader stages exist yet, so all commands)
    private static ImageMemoryBarrier2 CreateImageBarrier(RHI_Image_Layout layout_old, RHI_Image_Layout layout_new, Image image,
                                                          ImageAspectFlags aspect, uint mip_index, uint mip_range) => new()
    {
        SType               = StructureType.ImageMemoryBarrier2,
        SrcStageMask        = PipelineStageFlags2.AllCommandsBit,
        SrcAccessMask       = layout_old == RHI_Image_Layout.Max ? AccessFlags2.None : AccessFlags2.MemoryWriteBit,
        DstStageMask        = PipelineStageFlags2.AllCommandsBit,
        DstAccessMask       = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        OldLayout           = ToVulkan(layout_old),
        NewLayout           = ToVulkan(layout_new),
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Image               = image,
        SubresourceRange    = new ImageSubresourceRange(aspect, mip_index, mip_range, 0, Vk.RemainingArrayLayers)
    };

    // spartan: PrepareTextureForUpload -> every mip to general
    public void PrepareTextureForUpload(RHI_Texture texture) => InsertBarrier(texture, RHI_Image_Layout.General);

    // a full memory barrier, used around transfers until RHI_Barrier scopes are ported
    public void InsertBarrierMemory(bool host_read = false)
    {
        SuspendRenderPass();
        MemoryBarrier2 barrier = new()
        {
            SType         = StructureType.MemoryBarrier2,
            SrcStageMask  = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask  = PipelineStageFlags2.AllCommandsBit | (host_read ? PipelineStageFlags2.HostBit : 0),
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit | (host_read ? AccessFlags2.HostReadBit : 0)
        };
        DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        RHI_Context.Vk.CmdPipelineBarrier2(m_rhi_resource, in dependency);
    }

    // spartan: RHI_CommandList::CopyBufferContents, copies the first size bytes of source into destination on an
    // immediate list and waits (the geometry buffer moves its committed range into a grown buffer with it)
    public static void CopyBufferContents(RHI_Buffer source, RHI_Buffer destination, ulong size)
    {
        if (size == 0)
            return;
        if (size > source.GetObjectSize() || size > destination.GetObjectSize())
            throw new ArgumentOutOfRangeException(nameof(size));
        RHI_CommandList cmd_list = ImmediateExecutionBegin(RHI_Queue_Type.Copy);
        cmd_list.CopyBuffer(source.GetRhiResource(), 0, destination.GetRhiResource(), 0, size);
        ImmediateExecutionEnd(cmd_list);
    }

    // copies regions of source into destination on this list (the renderer's per frame uploads into device local buffers)
    public void CopyBuffer(RHI_Buffer source, RHI_Buffer destination, ReadOnlySpan<RHI_BufferCopy> regions)
    {
        RequireRecording();
        if (regions.IsEmpty)
            return;
        var copies = new BufferCopy[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            RHI_BufferCopy r = regions[i];
            if (r.SourceOffset + r.Size > source.GetObjectSize() || r.DestinationOffset + r.Size > destination.GetObjectSize())
                throw new ArgumentOutOfRangeException(nameof(regions), $"{source.Name} -> {destination.Name}: region {r} is out of bounds");
            copies[i] = new BufferCopy(r.SourceOffset, r.DestinationOffset, r.Size);
        }
        SuspendRenderPass();
        fixed (BufferCopy* p = copies)
            RHI_Context.Vk.CmdCopyBuffer(m_rhi_resource, source.GetRhiResource(), destination.GetRhiResource(), (uint)copies.Length, p);
    }

    // around the uploads of buffers only vertex shaders read: before, the copies wait for the earlier frames' vertex
    // shaders (write after read); after, this frame's vertex shaders see the copies
    public void InsertBarrierVertexReadUpload(bool before_copies)
    {
        SuspendRenderPass();
        MemoryBarrier2 barrier = before_copies
            ? new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.VertexShaderBit, SrcAccessMask = AccessFlags2.None,
                DstStageMask = PipelineStageFlags2.CopyBit,         DstAccessMask = AccessFlags2.TransferWriteBit
            }
            : new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.CopyBit,         SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.VertexShaderBit, DstAccessMask = AccessFlags2.ShaderStorageReadBit
            };
        DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        RHI_Context.Vk.CmdPipelineBarrier2(m_rhi_resource, in dependency);
    }

    public void CopyBuffer(Silk.NET.Vulkan.Buffer source, ulong source_offset, Silk.NET.Vulkan.Buffer destination, ulong destination_offset, ulong size)
    {
        SuspendRenderPass();
        BufferCopy region = new(source_offset, destination_offset, size);
        RHI_Context.Vk.CmdCopyBuffer(m_rhi_resource, source, destination, 1, in region);
    }

    public void Dispose()
    {
        lock (s_lists) s_lists.Remove(this);
        QueriesDestroy();
        m_rendering_complete_semaphore_timeline.Dispose();
        // the command buffer is freed with its pool (RHI_Queue)
    }
}
