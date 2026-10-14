// Port of the query half of SpartanEngine RHI_CommandList.h + vulkan/Vulkan_CommandList.cpp @ 4c07baa: the query pools
// (queries namespace, :1156-1311, created in the constructor :1357 and released in the destructor :1360-1366), the
// per-recording reset of Begin (:1402-1417), begin_timestamp/end_timestamp (:3024-3091), ReadbackTimestampsForProfiler
// (:3093-3107), begin_occlusion_query/end_occlusion_query (:3109-3159), GetOcclusionQueryResult (:3161-3172) and
// update_occlusion_queries (:3174-3177).
// Differences:
// - The occlusion state (slot allocation, entity to slot map, results) belongs to the command list like its pool.
//   Spartan keeps it in namespace globals shared by every command list (:1217-1224) while each list owns a pool, so
//   results are read from one list's pool through the map another list built.
// - Timestamp results are read into this list's arrays directly (spartan reads them into a shared global array first,
//   :1158-1162, and copies it, which races between threads recording on different queues).
// - Pools only exist where they can be used: timestamps on queues whose family has timestamp valid bits (spartan
//   creates them for every list but never resets the copy lists' pool, :1403, while begin_timestamp doesn't check the
//   queue), occlusion on the graphics queue.
// - UpdateOcclusionQueries reads the slots this list handed out, and can wait (spartan reads all 4096 queries with
//   VK_QUERY_RESULT_PARTIAL_BIT, including ones that were never begun).
// - The occlusion pool reset suspends the render pass instead of ending it, so pending clears and the memory barrier
//   after the pass are left alone; ending or suspending a pass while an occlusion query is open throws.
// - An entity queried twice in one recording gets no second query (spartan returns the slot it already has, :1250-1256,
//   and begins the same query again without a reset).
// - Instance methods only (spartan also has static wrappers going through RHI_Device::Cmd()). Multiview doesn't exist
//   yet, so a timestamp is always one query.
// - GetOcclusionQuerySamples returns the raw sample count, GetOcclusionQueryResult keeps spartan's meaning: true when
//   the entity was occluded (no samples passed).
// - The pools are destroyed directly on dispose, the queue disposes its command lists once the device is idle (spartan
//   goes through the deletion queue).
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

// the timestamps of one recording, filled once its work completes (spartan: RHI_TimestampSample)
internal sealed class RHI_TimestampSample
{
    public readonly ulong[] ticks    = new ulong[RHI_CommandList.MaxTimestamps];
    public readonly bool[] available = new bool[RHI_CommandList.MaxTimestamps];
    public uint count;
    public bool ready;

    // milliseconds between two timestamps of this sample, 0 when either is unavailable. the difference wraps with the
    // valid bits of the queue the timestamps were written on
    public double GetDurationMs(uint index_begin, uint index_end, ulong mask = ulong.MaxValue)
    {
        if (!ready || index_begin >= count || index_end >= count || !available[index_begin] || !available[index_end])
            return 0.0;
        return TicksToMs(ticks[index_begin], ticks[index_end], mask, RHI_Device.PropertyTimestampPeriod);
    }

    // the elapsed ticks wrap with the mask, double keeps the precision of large tick counts
    public static double TicksToMs(ulong tick_begin, ulong tick_end, ulong mask, double period_ns) =>
        ((tick_end - tick_begin) & mask) * period_ns * 1e-6;
}

internal sealed unsafe partial class RHI_CommandList
{
    public const uint MaxTimestamps       = 256;  // spartan: m_max_timestamps and queries::timestamp::query_count
    public const uint MaxOcclusionQueries = 4096; // spartan: queries::occlusion::query_count, slot 0 is reserved
    public const uint InvalidQuery        = uint.MaxValue;

    private QueryPool m_rhi_query_pool_timestamps;
    private QueryPool m_rhi_query_pool_occlusion;

    // timestamps
    private uint m_timestamp_index;
    private readonly ulong[] m_timestamp_data      = new ulong[MaxTimestamps];
    private readonly bool[] m_timestamp_available  = new bool[MaxTimestamps];
    private RHI_TimestampSample? m_timestamp_sample;
    private bool m_timestamps_reset;

    // occlusion
    private bool m_occlusion_query_pool_reset;
    private bool m_occlusion_query_active;
    private uint m_occlusion_index;
    private uint m_occlusion_index_active;
    private readonly ulong[] m_occlusion_visible_samples = new ulong[MaxOcclusionQueries];
    private readonly Dictionary<ulong, uint> m_occlusion_id_to_index = new();

    public bool HasTimestamps() => m_rhi_query_pool_timestamps.Handle != 0;
    public bool HasOcclusionQueries() => m_rhi_query_pool_occlusion.Handle != 0;
    public uint GetTimestampCount() => m_timestamp_index;
    public bool IsOcclusionQueryActive() => m_occlusion_query_active;

    private void QueriesInitialize()
    {
        Vk vk = RHI_Context.Vk;

        // timestamps
        if (RHI_Device.IsGpuTimingEnabled && RHI_Device.GetTimestampValidBitsForFamily(Queue.FamilyIndex) != 0)
        {
            QueryPoolCreateInfo info = new() { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = MaxTimestamps };
            Check(vk.CreateQueryPool(RHI_Context.Device, in info, null, out m_rhi_query_pool_timestamps), "vkCreateQueryPool");
            RHI_Device.SetResourceName(m_rhi_query_pool_timestamps.Handle, RHI_Resource_Type.QueryPool, "query_pool_timestamp");
        }

        // occlusion
        if (Queue.Type == RHI_Queue_Type.Graphics)
        {
            QueryPoolCreateInfo info = new() { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Occlusion, QueryCount = MaxOcclusionQueries };
            Check(vk.CreateQueryPool(RHI_Context.Device, in info, null, out m_rhi_query_pool_occlusion), "vkCreateQueryPool");
            RHI_Device.SetResourceName(m_rhi_query_pool_occlusion.Handle, RHI_Resource_Type.QueryPool, "query_pool_occlusion");
        }
    }

    private void QueriesDestroy()
    {
        if (RHI_Context.Device.Handle == 0)
            return;
        if (m_rhi_query_pool_timestamps.Handle != 0)
            RHI_Context.Vk.DestroyQueryPool(RHI_Context.Device, m_rhi_query_pool_timestamps, null);
        if (m_rhi_query_pool_occlusion.Handle != 0)
            RHI_Context.Vk.DestroyQueryPool(RHI_Context.Device, m_rhi_query_pool_occlusion, null);
        m_rhi_query_pool_timestamps = default;
        m_rhi_query_pool_occlusion  = default;
    }

    // before vkBeginCommandBuffer: hand the previous recording's timestamps to its sample
    private void QueriesBeforeBegin()
    {
        ReadbackTimestampsForProfiler();
        m_timestamp_sample = null;
    }

    // after vkBeginCommandBuffer: timestamp queries are reset before first use
    private void QueriesBegin()
    {
        if (HasTimestamps())
        {
            if (m_timestamp_index != 0)
                UpdateTimestamps(false);
            m_timestamp_index = 0;
            RHI_Context.Vk.CmdResetQueryPool(m_rhi_resource, m_rhi_query_pool_timestamps, 0, MaxTimestamps);
            m_timestamps_reset = true;
        }
        m_occlusion_query_pool_reset = false;
        m_occlusion_query_active     = false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // timestamps

    // returns the query index, or InvalidQuery when gpu timing is off or the pool is full
    public uint BeginTimestamp() => WriteTimestamp();
    public uint EndTimestamp() => WriteTimestamp();

    private uint WriteTimestamp()
    {
        RequireRecording();
        if (!RHI_Device.GpuTiming || !HasTimestamps())
            return InvalidQuery;
        if (m_timestamp_index + 1 > MaxTimestamps)
        {
            s_timestamps_dropped++;
            return InvalidQuery;
        }

        // timestamp commands are legal inside dynamic rendering, ending the render pass here changes the workload being measured
        uint timestamp_index = m_timestamp_index;

        // top-of-pipe bunches timestamps at command buffer start, all-commands gives a point after recorded work completed
        RHI_Context.Vk.CmdWriteTimestamp2(m_rhi_resource, PipelineStageFlags2.AllCommandsBit, m_rhi_query_pool_timestamps, m_timestamp_index);

        m_timestamp_index++;
        return timestamp_index;
    }

    // timestamps that didn't fit in the pool (spartan: Profiler::m_rhi_timestamps_dropped)
    private static long s_timestamps_dropped;
    public static long GetTimestampsDropped() => Interlocked.Read(ref s_timestamps_dropped);

    // the sample the timestamps of the current recording land in once its work completes
    public RHI_TimestampSample GetTimestampSample() => m_timestamp_sample ??= new RHI_TimestampSample();

    public void ReadbackTimestampsForProfiler()
    {
        if (m_timestamp_sample == null || m_timestamp_sample.ready || m_state == RHI_CommandListState.Recording || !GetWork().IsComplete())
            return;
        if (m_timestamp_index > 0)
        {
            UpdateTimestamps(false);
            m_timestamp_sample.count = System.Math.Min(m_timestamp_index, MaxTimestamps);
            Array.Copy(m_timestamp_data, m_timestamp_sample.ticks, m_timestamp_sample.count);
            Array.Copy(m_timestamp_available, m_timestamp_sample.available, m_timestamp_sample.count);
        }
        m_timestamp_sample.ready = true;
    }

    // the raw tick of a timestamp of the last completed recording, 0 when it isn't available
    public ulong GetTimestampResult(uint index)
    {
        if (index >= MaxTimestamps)
            return 0;
        if (m_state != RHI_CommandListState.Recording && m_timestamp_index > index && GetWork().IsComplete())
            UpdateTimestamps(false);
        return m_timestamp_available[index] ? m_timestamp_data[index] : 0;
    }

    private void UpdateTimestamps(bool wait_for_results)
    {
        Array.Clear(m_timestamp_data);
        Array.Clear(m_timestamp_available);
        uint query_count = System.Math.Min(m_timestamp_index, MaxTimestamps);
        if (query_count == 0 || !m_timestamps_reset)
            return;

        // each query writes a (timestamp, availability) pair, hence the doubled size and stride
        ulong* interleaved = stackalloc ulong[(int)query_count * 2];
        QueryResultFlags flags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit | (wait_for_results ? QueryResultFlags.ResultWaitBit : 0);
        Result result = RHI_Context.Vk.GetQueryPoolResults(RHI_Context.Device, m_rhi_query_pool_timestamps, 0, query_count,
                                                            (nuint)(query_count * sizeof(ulong) * 2), interleaved, sizeof(ulong) * 2, flags);
        if (wait_for_results)
            Check(result, "vkGetQueryPoolResults");

        for (uint i = 0; i < query_count; i++)
        {
            m_timestamp_available[i] = interleaved[i * 2 + 1] != 0;
            m_timestamp_data[i]      = m_timestamp_available[i] ? interleaved[i * 2] : 0;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // occlusion queries

    public void BeginOcclusionQuery(ulong entity_id)
    {
        RequireRecording();
        if (m_pso == null || !m_pso.IsGraphics())
            throw new InvalidOperationException($"{Name}: occlusion queries are only supported in graphics pipelines");
        if (!HasOcclusionQueries())
            throw new InvalidOperationException($"{Name}: occlusion queries need a graphics queue command list");
        if (m_occlusion_query_active)
            throw new InvalidOperationException($"{Name}: an occlusion query is already active");

        if (!m_occlusion_query_pool_reset)
        {
            SuspendRenderPass();
            OcclusionReset();
            m_occlusion_query_pool_reset = true;
        }

        m_occlusion_index_active = OcclusionAllocateIndex(entity_id);
        if (m_occlusion_index_active == 0)
            return; // pool exhausted, skip this query

        if (!m_render_pass_active)
            RenderPassBegin();

        RHI_Context.Vk.CmdBeginQuery(m_rhi_resource, m_rhi_query_pool_occlusion, m_occlusion_index_active, 0);
        m_occlusion_query_active = true;
    }

    public void EndOcclusionQuery()
    {
        if (!m_occlusion_query_active)
            return;
        RHI_Context.Vk.CmdEndQuery(m_rhi_resource, m_rhi_query_pool_occlusion, m_occlusion_index_active);
        m_occlusion_query_active = false;
    }

    // true when the entity was occluded (no samples passed), false when it was visible or wasn't queried
    public bool GetOcclusionQueryResult(ulong entity_id) =>
        m_occlusion_id_to_index.TryGetValue(entity_id, out uint index) && m_occlusion_visible_samples[index] == 0;

    // the samples that passed for the entity, null when it wasn't queried
    public ulong? GetOcclusionQuerySamples(ulong entity_id) =>
        m_occlusion_id_to_index.TryGetValue(entity_id, out uint index) ? m_occlusion_visible_samples[index] : null;

    // reads the results of this list's last completed recording, call it before the next recording's first query
    public void UpdateOcclusionQueries(bool wait_for_results = false)
    {
        if (!HasOcclusionQueries() || m_occlusion_index == 0 || m_state == RHI_CommandListState.Recording)
            return;
        if (wait_for_results)
            WaitForExecution();

        uint query_count = m_occlusion_index; // slots 1..index
        QueryResultFlags flags = QueryResultFlags.Result64Bit | (wait_for_results ? QueryResultFlags.ResultWaitBit : QueryResultFlags.ResultPartialBit);
        fixed (ulong* p = &m_occlusion_visible_samples[1])
        {
            Result result = RHI_Context.Vk.GetQueryPoolResults(RHI_Context.Device, m_rhi_query_pool_occlusion, 1, query_count,
                                                                (nuint)(query_count * sizeof(ulong)), p, sizeof(ulong), flags);
            if (result != Result.Success && result != Result.NotReady)
                Check(result, "vkGetQueryPoolResults");
        }
    }

    private void OcclusionReset()
    {
        RHI_Context.Vk.CmdResetQueryPool(m_rhi_resource, m_rhi_query_pool_occlusion, 0, MaxOcclusionQueries);

        // the pool is reset, so all previous results are invalidated
        m_occlusion_index = 0;
        m_occlusion_id_to_index.Clear();
        Array.Clear(m_occlusion_visible_samples);
    }

    private uint OcclusionAllocateIndex(ulong entity_id)
    {
        // the map is cleared with the pool, so an entity that has an index was already queried in this recording and
        // its query can't begin again before the next reset (spartan hands the same index out again)
        if (m_occlusion_id_to_index.ContainsKey(entity_id))
            return 0;

        // pool is full, return the invalid index (0 is reserved)
        if (m_occlusion_index >= MaxOcclusionQueries - 1)
        {
            Log.LogWarning("Occlusion query pool exhausted, some objects may not be queried");
            return 0;
        }

        uint new_index = ++m_occlusion_index;
        m_occlusion_id_to_index[entity_id] = new_index;
        return new_index;
    }
}
