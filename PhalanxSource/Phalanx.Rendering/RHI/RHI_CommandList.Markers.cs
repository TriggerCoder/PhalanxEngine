// Port of the markers of SpartanEngine RHI_CommandList.h + vulkan/Vulkan_CommandList.cpp @ 4c07baa: begin_marker and
// end_marker (:2922-2972, the debug utils half) and write_gpu_breadcrumb (:2974-3022).
// Differences:
// - InsertMarker has no spartan counterpart, and BeginMarker takes an optional color (spartan passes Vector4::Zero).
// - Markers are balanced per command list: EndMarker without a BeginMarker and End with open markers throw. Each marker
//   remembers whether it was recorded, so turning GpuMarking off while markers are open still ends the ones that were.
// - The breadcrumb system (profiling/Breadcrumbs.h/.cpp: cpu marker stacks, per-queue gpu buffers, crash reports, the
//   cvar_debug_breadcrumbs half of begin/end_marker) isn't ported, only the gpu write it is built on.
// - write_gpu_breadcrumb suspends the render pass instead of ending it (vkCmdFillBuffer can't run inside one).
// - Timeblocks (begin_timeblock/end_timeblock) need the profiler, which isn't ported yet.
// - Every marker also sets a device diagnostic checkpoint and, with RenderValidation.GpuBreadcrumbs, writes the gpu
//   breadcrumbs (RHI_Device.Diagnostics.cs, RHI_Breadcrumbs.cs), whether or not the debug utils labels are recorded.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe partial class RHI_CommandList
{
    private readonly Stack<(bool recorded, string name)> m_markers = new();

    public int GetMarkerDepth() => m_markers.Count;

    public void BeginMarker(string name, System.Numerics.Vector4 color = default)
    {
        RequireRecording();
        bool record = RHI_Device.IsGpuMarkingEnabled;
        if (record)
            RHI_Device.MarkerBegin(this, name, color);
        m_markers.Push((record, name));
        RHI_Device.SetCheckpoint(this, name, end: false); // device lost diagnostics, RHI_Device.Diagnostics.cs
        RHI_Breadcrumbs.Begin(this, name);
    }

    public void EndMarker()
    {
        RequireRecording();
        if (m_markers.Count == 0)
            throw new InvalidOperationException($"{Name}: EndMarker without a BeginMarker");
        (bool recorded, string name) = m_markers.Pop();
        RHI_Breadcrumbs.End(this);
        RHI_Device.SetCheckpoint(this, name, end: true);
        if (recorded)
            RHI_Device.MarkerEnd(this);
    }

    public void InsertMarker(string name, System.Numerics.Vector4 color = default)
    {
        RequireRecording();
        if (RHI_Device.IsGpuMarkingEnabled)
            RHI_Device.MarkerInsert(this, name, color);
    }

    // writes a uint32 value into a slot of the breadcrumb buffer on the gpu timeline
    public void WriteGpuBreadcrumb(RHI_Buffer buffer, uint slot, uint value)
    {
        RequireRecording();
        if (buffer.GetRhiResource().Handle == 0)
            throw new ArgumentException($"{buffer.Name}: the buffer has no gpu resource", nameof(buffer));
        if ((slot + 1UL) * sizeof(uint) > buffer.GetObjectSize())
            throw new ArgumentOutOfRangeException(nameof(slot), $"{buffer.Name}: slot {slot} is past the end of the buffer");

        // vkCmdFillBuffer is a transfer op and cannot be issued inside a render pass
        SuspendRenderPass();

        // scope the barrier to just this 4-byte slot
        BufferMemoryBarrier2 barrier = new()
        {
            SType               = StructureType.BufferMemoryBarrier2,
            SrcStageMask        = PipelineStageFlags2.ClearBit,
            SrcAccessMask       = AccessFlags2.TransferWriteBit,
            DstStageMask        = PipelineStageFlags2.ClearBit,
            DstAccessMask       = AccessFlags2.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer              = buffer.GetRhiResource(),
            Offset              = slot * sizeof(uint),
            Size                = sizeof(uint)
        };
        DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
        RHI_Context.Vk.CmdPipelineBarrier2(m_rhi_resource, in dependency);

        RHI_Context.Vk.CmdFillBuffer(m_rhi_resource, buffer.GetRhiResource(), slot * sizeof(uint), sizeof(uint), value);

        // host and the next fill both need the write available
        barrier.DstStageMask  = PipelineStageFlags2.HostBit | PipelineStageFlags2.ClearBit;
        barrier.DstAccessMask = AccessFlags2.HostReadBit | AccessFlags2.TransferWriteBit;
        RHI_Context.Vk.CmdPipelineBarrier2(m_rhi_resource, in dependency);
    }

    private void RequireMarkersClosed()
    {
        if (m_markers.Count != 0)
            throw new InvalidOperationException($"{Name}: {m_markers.Count} marker(s) still open at End");
    }
}
