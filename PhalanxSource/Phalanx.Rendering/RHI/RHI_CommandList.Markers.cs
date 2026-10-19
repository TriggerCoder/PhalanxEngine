// Port of the markers of SpartanEngine RHI_CommandList.h + vulkan/Vulkan_CommandList.cpp @ 4c07baa: begin_marker and
// end_marker (:2922-2972, the debug utils half) and write_gpu_breadcrumb (:2974-3022).
// Differences:
// - InsertMarker has no spartan counterpart, and BeginMarker takes an optional color (spartan passes Vector4::Zero).
// - Markers are balanced per command list: EndMarker without a BeginMarker and End with open markers throw. Each marker
//   remembers whether it was recorded, so turning GpuMarking off while markers are open still ends the ones that were.
// - The breadcrumb system (profiling/Breadcrumbs.h/.cpp: cpu marker stacks, per-queue gpu buffers, crash reports, the
//   cvar_debug_breadcrumbs half of begin/end_marker) isn't ported, only the gpu write it is built on.
// - write_gpu_breadcrumb suspends the render pass instead of ending it (vkCmdFillBuffer can't run inside one).
// - Timeblocks (begin_timeblock/end_timeblock) need the profiler, which isn't ported yet; their gpu timing is: a top
//   level marker of a list with timestamps writes one at its begin and end (RHI_GpuTimings.cs).
// - Every marker also sets a device diagnostic checkpoint and, with RenderValidation.GpuBreadcrumbs, writes the gpu
//   breadcrumbs (RHI_Device.Diagnostics.cs, RHI_Breadcrumbs.cs), whether or not the debug utils labels are recorded.
// - With the driver's checkpoints on, every draw and dispatch sets one more before it, named after the innermost marker
//   and its index there (CheckpointWork), so a device lost report narrows the loss down to one draw call or dispatch.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe partial class RHI_CommandList
{
    private readonly Stack<(bool recorded, string name, uint timestamp)> m_markers = new();
    // the draws and dispatches recorded so far in each open marker, innermost last (CheckpointWork)
    private readonly Stack<uint> m_marker_work = new();
    // the timed markers of this recording and the frame's own pair (RHI_GpuTimings), read back at the next Begin
    private readonly List<(string name, uint begin, uint end)> m_timed_markers = new();
    private uint m_frame_timestamp_begin = InvalidQuery, m_frame_timestamp_end = InvalidQuery;

    public int GetMarkerDepth() => m_markers.Count;

    public void BeginMarker(string name, System.Numerics.Vector4 color = default)
    {
        RequireRecording();
        bool record = RHI_Device.IsGpuMarkingEnabled;
        if (record)
            RHI_Device.MarkerBegin(this, name, color);
        uint timestamp = m_markers.Count == 0 && RHI_GpuTimings.Enabled && HasTimestamps() ? WriteTimestamp() : InvalidQuery;
        m_markers.Push((record, name, timestamp));
        m_marker_work.Push(0);
        RHI_Device.SetCheckpoint(this, name, end: false); // device lost diagnostics, RHI_Device.Diagnostics.cs
        RHI_Breadcrumbs.Begin(this, name);
    }

    public void EndMarker()
    {
        RequireRecording();
        if (m_markers.Count == 0)
            throw new InvalidOperationException($"{Name}: EndMarker without a BeginMarker");
        (bool recorded, string name, uint timestamp) = m_markers.Pop();
        m_marker_work.TryPop(out _);
        if (timestamp != InvalidQuery && WriteTimestamp() is uint end && end != InvalidQuery)
            m_timed_markers.Add((name, timestamp, end));
        RHI_Breadcrumbs.End(this);
        RHI_Device.SetCheckpoint(this, name, end: true);
        if (recorded)
            RHI_Device.MarkerEnd(this);
    }

    // device lost diagnostics: a checkpoint before a draw or dispatch, "g_buffer draw 3 (instances 196608+65536)" or
    // "screen_space_shadows dispatch 2 (12x9x1)". The top of pipe checkpoint is then the last draw that started, the
    // bottom of pipe one the first that did not complete. Only when the driver has checkpoints (the name is built per
    // call); the names repeat from frame to frame, so the marker name table stays as large as one frame's draws.
    private void CheckpointWork(string kind, uint a, uint b, uint c, bool instances)
    {
        if (!RHI_Device.RecordsCheckpoints)
            return;
        uint index = 0;
        if (m_marker_work.Count > 0)
        {
            index = m_marker_work.Pop();
            m_marker_work.Push(index + 1);
        }
        string marker = m_markers.Count > 0 ? m_markers.Peek().name : "no marker";
        string name = instances ? $"{marker} {kind} {index} (instances {a}+{b})" : $"{marker} {kind} {index} ({a}x{b}x{c})";
        RHI_Device.SetCheckpoint(this, name, end: false);
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

    // the frame's gpu time: a timestamp at the start and the end of the frame's graphics list (RHI_Device.Frame.cs)
    public void BeginFrameTiming() => m_frame_timestamp_begin = RHI_GpuTimings.Enabled && HasTimestamps() ? WriteTimestamp() : InvalidQuery;
    public void EndFrameTiming() => m_frame_timestamp_end = m_frame_timestamp_begin != InvalidQuery ? WriteTimestamp() : InvalidQuery;

    // the timed markers of the completed recording into RHI_GpuTimings: at the frame's start for the list of the
    // frame that used its slot (RHI_Device.BeginFrame), else before the list records again
    internal void ReadbackTimedMarkers()
    {
        if ((m_timed_markers.Count == 0 && m_frame_timestamp_end == InvalidQuery) || m_state == RHI_CommandListState.Recording)
            return;
        if (GetWork().IsComplete() && !RHI_Device.IsDeviceLost)
        {
            UpdateTimestamps(false);
            ulong mask = RHI_Device.GetTimestampMask(Queue.Type);
            double period = RHI_Device.PropertyTimestampPeriod;
            double Ms(uint begin, uint end) =>
                m_timestamp_available[begin] && m_timestamp_available[end]
                    ? RHI_TimestampSample.TicksToMs(m_timestamp_data[begin], m_timestamp_data[end], mask, period) : 0.0;
            foreach ((string name, uint begin, uint end) in m_timed_markers)
                RHI_GpuTimings.Add(name, Ms(begin, end));
            if (m_frame_timestamp_end != InvalidQuery)
                RHI_GpuTimings.AddFrame(Ms(m_frame_timestamp_begin, m_frame_timestamp_end));
        }
        m_timed_markers.Clear();
        m_frame_timestamp_begin = m_frame_timestamp_end = InvalidQuery;
    }

    private void RequireMarkersClosed()
    {
        if (m_markers.Count != 0)
            throw new InvalidOperationException($"{Name}: {m_markers.Count} marker(s) still open at End");
    }
}
