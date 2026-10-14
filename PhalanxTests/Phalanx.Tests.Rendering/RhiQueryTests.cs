using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Rendering.RHI;
using Xunit;

namespace Phalanx.Tests;

// no gpu: tick conversion and the timestamp sample (spartan @ 4c07baa)
public class RhiQueryLogicTests
{
    [Fact]
    public void Ticks_convert_to_milliseconds_and_wrap_with_the_valid_bits()
    {
        Assert.Equal(1.0, RHI_TimestampSample.TicksToMs(0, 1_000_000, ulong.MaxValue, 1.0), 9);
        Assert.Equal(2.5, RHI_TimestampSample.TicksToMs(1000, 1000 + 1_000_000, ulong.MaxValue, 2.5), 9);

        // a 36 bit counter that wrapped between the two timestamps
        ulong mask = (1UL << 36) - 1;
        Assert.Equal(0.000032, RHI_TimestampSample.TicksToMs(mask - 15, 16, mask, 1.0), 9);

        // large tick counts keep their precision (spartan's float math loses it, TimeBlock.cpp:157)
        ulong begin = 1UL << 50;
        Assert.Equal(0.000001, RHI_TimestampSample.TicksToMs(begin, begin + 1, ulong.MaxValue, 1.0), 12);
    }

    [Fact]
    public void A_sample_that_isnt_ready_or_unavailable_has_no_duration()
    {
        var sample = new RHI_TimestampSample();
        Assert.Equal(0.0, sample.GetDurationMs(0, 1));
        sample.count = 2;
        sample.ready = true;
        sample.ticks[0] = 10;
        sample.ticks[1] = 20;
        sample.available[0] = true;
        Assert.Equal(0.0, sample.GetDurationMs(0, 1)); // the end isn't available
        Assert.Equal(0.0, sample.GetDurationMs(0, 5)); // out of range
    }

    [Fact]
    public void Query_limits_match_spartan()
    {
        Assert.Equal(256u, RHI_CommandList.MaxTimestamps);
        Assert.Equal(4096u, RHI_CommandList.MaxOcclusionQueries);
        Assert.Equal(uint.MaxValue, RHI_CommandList.InvalidQuery);
    }
}

[Collection("gpu")]
public class RhiQueryGpuTests
{
    private const uint Size = 64;

    private static void Run(Action body)
    {
        Skip.IfNot(Phalanx.Rendering.RenderDevice.IsVulkanAvailable(out string reason), reason);
        RHI_Device.Initialize(Array.Empty<string>(), validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            RHI_Device.DeletionQueueFlush();
            Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal) || m.Contains("WarningBit", StringComparison.Ordinal)),
                        string.Join("\n", RHI_Device.ValidationMessages));
        }
        finally
        {
            RHI_Device.GpuTiming  = true;
            RHI_Device.GpuMarking = true;
            RHI_Device.Destroy();
        }
    }

    private static RHI_Texture CreateTarget(string name) =>
        new(RHI_Texture_Type.Type2D, Size, Size, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit, name);

    [SkippableFact]
    public void The_device_reports_timestamp_support()
    {
        Run(() =>
        {
            Assert.True(RHI_Device.IsGpuTimingEnabled);
            Assert.True(RHI_Device.PropertyTimestampPeriod > 0.0f);
            uint bits = RHI_Device.GetTimestampValidBits(RHI_Queue_Type.Graphics);
            Assert.InRange(bits, 36u, 64u); // the spec's minimum for a family that has timestamps
            Assert.Equal(bits >= 64 ? ulong.MaxValue : (1UL << (int)bits) - 1, RHI_Device.GetTimestampMask(RHI_Queue_Type.Graphics));

            // graphics lists get both pools
            RHI_CommandList cmd = RHI_Device.GetQueue(RHI_Queue_Type.Graphics).NextCommandList();
            Assert.True(cmd.HasTimestamps());
            Assert.True(cmd.HasOcclusionQueries());
        });
    }

    [SkippableFact]
    public void Calibration_lines_up_gpu_ticks_with_the_cpu_clock()
    {
        Run(() =>
        {
            RHI_TimestampCalibration calibration = RHI_Device.GetTimestampCalibration(RHI_Queue_Type.Graphics);
            Assert.Equal(RHI_Device.PropertyTimestampPeriod, calibration.period_ns, 6);
            Assert.Equal(RHI_Device.GetTimestampValidBits(RHI_Queue_Type.Graphics), calibration.valid_bits);
            if (!RHI_Device.IsCalibratedTimestampsSupported)
            {
                Assert.False(calibration.calibrated);
                return;
            }

            Assert.True(calibration.calibrated);
            Assert.True(calibration.gpu_tick > 0);
            Assert.True(calibration.deviation_ms >= 0.0);
            Assert.InRange(RHI_Device.GetCpuTimestampMs() - calibration.cpu_ms, 0.0, 10_000.0);

            // a second calibration moves forward on both clocks
            RHI_TimestampCalibration later = RHI_Device.GetTimestampCalibration(RHI_Queue_Type.Graphics);
            Assert.True(later.cpu_ms >= calibration.cpu_ms);
            Assert.True(later.gpu_tick >= calibration.gpu_tick);
        });
    }

    [SkippableFact]
    public void Timestamps_around_a_draw_are_monotonic_and_have_a_duration()
    {
        Run(() =>
        {
            using var sample = new RHI_TriangleSample();
            using RHI_Texture color = CreateTarget("rt_timestamps");

            RHI_CommandList cmd = RHI_Device.BeginFrame();
            RHI_TimestampSample timestamps = cmd.GetTimestampSample();
            uint frame_begin = cmd.BeginTimestamp();
            sample.SetPipelineState(cmd, null, color);
            uint draw_begin = cmd.BeginTimestamp();
            sample.DrawTriangle(cmd);                   // the pass starts here
            uint draw_end = cmd.EndTimestamp();         // inside the render pass
            cmd.InsertBarrier(color, RHI_Image_Layout.General);
            uint frame_end = cmd.EndTimestamp();
            Assert.Equal(new uint[] { 0, 1, 2, 3 }, new[] { frame_begin, draw_begin, draw_end, frame_end });
            Assert.Equal(4u, cmd.GetTimestampCount());
            Assert.False(timestamps.ready);
            RHI_Device.EndFrame().Wait();

            cmd.ReadbackTimestampsForProfiler();
            Assert.True(timestamps.ready);
            Assert.Equal(4u, timestamps.count);
            for (uint i = 0; i < 4; i++)
            {
                Assert.True(timestamps.available[i], $"timestamp {i}");
                Assert.True(timestamps.ticks[i] > 0, $"timestamp {i}");
                Assert.Equal(timestamps.ticks[i], cmd.GetTimestampResult(i));
                if (i > 0)
                    Assert.True(timestamps.ticks[i] >= timestamps.ticks[i - 1], $"timestamp {i} went back");
            }

            ulong mask = RHI_Device.GetTimestampMask(RHI_Queue_Type.Graphics);
            double draw_ms  = timestamps.GetDurationMs(draw_begin, draw_end, mask);
            double frame_ms = timestamps.GetDurationMs(frame_begin, frame_end, mask);
            Assert.InRange(draw_ms, 0.0, 10_000.0);
            Assert.InRange(frame_ms, draw_ms, 10_000.0);

            // the next recording resets the pool and starts from zero, the old sample keeps its values
            RHI_CommandList next = RHI_Device.GetQueue(RHI_Queue_Type.Graphics).NextCommandList();
            Assert.NotSame(cmd, next);
            ulong first = timestamps.ticks[0];
            cmd.Begin();
            Assert.Equal(0u, cmd.GetTimestampCount());
            Assert.Equal(0u, cmd.BeginTimestamp());
            cmd.End();
            cmd.Queue.Submit(cmd, null, null);
            cmd.WaitForExecution();
            Assert.Equal(first, timestamps.ticks[0]);
            Assert.True(cmd.GetTimestampResult(0) >= timestamps.ticks[3]);
        });
    }

    [SkippableFact]
    public void Timestamps_work_on_the_compute_and_copy_queues()
    {
        Run(() =>
        {
            using var buffer = new RHI_Buffer(RHI_Buffer_Type.Storage, sizeof(uint), 16, ReadOnlySpan<byte>.Empty, mappable: false, "timestamp_buffer");
            foreach (RHI_Queue_Type type in new[] { RHI_Queue_Type.Compute, RHI_Queue_Type.Copy })
            {
                if (RHI_Device.GetTimestampValidBits(type) == 0)
                    continue;
                RHI_CommandList cmd = RHI_CommandList.ImmediateExecutionBegin(type);
                Assert.True(cmd.HasTimestamps());
                Assert.False(cmd.HasOcclusionQueries());
                uint begin = cmd.BeginTimestamp();
                cmd.WriteGpuBreadcrumb(buffer, 3, 7);
                uint end = cmd.EndTimestamp();
                RHI_CommandList.ImmediateExecutionEnd(cmd);

                ulong tick_begin = cmd.GetTimestampResult(begin), tick_end = cmd.GetTimestampResult(end);
                Assert.True(tick_begin > 0, type.ToString());
                Assert.True(tick_end >= tick_begin, type.ToString());
            }
        });
    }

    [SkippableFact]
    public void Timestamps_are_skipped_when_gpu_timing_is_off_or_the_pool_is_full()
    {
        Run(() =>
        {
            RHI_CommandList cmd = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
            RHI_Device.GpuTiming = false;
            Assert.Equal(RHI_CommandList.InvalidQuery, cmd.BeginTimestamp());
            RHI_Device.GpuTiming = true;

            long dropped = RHI_CommandList.GetTimestampsDropped();
            for (uint i = 0; i < RHI_CommandList.MaxTimestamps; i++)
                Assert.Equal(i, cmd.BeginTimestamp());
            Assert.Equal(RHI_CommandList.InvalidQuery, cmd.EndTimestamp());
            Assert.Equal(dropped + 1, RHI_CommandList.GetTimestampsDropped());
            RHI_CommandList.ImmediateExecutionEnd(cmd);

            Assert.True(cmd.GetTimestampResult(RHI_CommandList.MaxTimestamps - 1) >= cmd.GetTimestampResult(0));
            Assert.Equal(0ul, cmd.GetTimestampResult(RHI_CommandList.InvalidQuery));
        });
    }

    [SkippableFact]
    public void Occlusion_queries_count_samples_of_a_visible_triangle_and_none_of_a_culled_one()
    {
        Run(() =>
        {
            using var sample = new RHI_TriangleSample();
            using RHI_Texture color = CreateTarget("rt_occlusion");

            RHI_CommandList cmd = RHI_Device.BeginFrame();
            sample.SetPipelineState(cmd, null, color);
            cmd.BeginOcclusionQuery(1); // begins the render pass
            Assert.True(cmd.IsRenderPassActive());
            Assert.True(cmd.IsOcclusionQueryActive());
            sample.DrawTriangle(cmd);
            cmd.EndOcclusionQuery();

            // the triangle is clockwise (front facing), culling front faces leaves nothing
            cmd.SetCullMode(RHI_CullMode.Front);
            cmd.BeginOcclusionQuery(2);
            sample.DrawTriangle(cmd);
            cmd.EndOcclusionQuery();

            // an entity queried twice in one recording gets no second query
            cmd.BeginOcclusionQuery(1);
            Assert.False(cmd.IsOcclusionQueryActive());
            cmd.EndOcclusionQuery();

            // a query has to end inside the pass it began in
            cmd.SetCullMode(RHI_CullMode.Back);
            cmd.BeginOcclusionQuery(3);
            Assert.Throws<InvalidOperationException>(() => cmd.InsertBarrierMemory());
            sample.DrawTriangle(cmd);
            cmd.EndOcclusionQuery();
            RHI_Device.EndFrame().Wait();

            cmd.UpdateOcclusionQueries(wait_for_results: true);
            ulong visible = cmd.GetOcclusionQuerySamples(1) ?? 0;
            // the triangle covers a quarter of the target, give or take the edges
            Assert.InRange(visible, (ulong)(Size * Size / 8), (ulong)(Size * Size / 2));
            Assert.Equal(0ul, cmd.GetOcclusionQuerySamples(2));
            Assert.Equal(visible, cmd.GetOcclusionQuerySamples(3));
            Assert.Null(cmd.GetOcclusionQuerySamples(4));

            // spartan's meaning: true when occluded, false when visible or never queried
            Assert.False(cmd.GetOcclusionQueryResult(1));
            Assert.True(cmd.GetOcclusionQueryResult(2));
            Assert.False(cmd.GetOcclusionQueryResult(4));
        });
    }

    [SkippableFact]
    public void Occlusion_queries_need_a_graphics_pipeline()
    {
        Run(() =>
        {
            RHI_CommandList cmd = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
            Assert.Throws<InvalidOperationException>(() => cmd.BeginOcclusionQuery(1));
            RHI_CommandList.ImmediateExecutionEnd(cmd);
        });
    }

    [SkippableFact]
    public void Markers_nest_and_are_validation_clean()
    {
        Run(() =>
        {
            Assert.True(RHI_Device.IsGpuMarkingEnabled);
            using var sample = new RHI_TriangleSample();
            using RHI_Texture color = CreateTarget("rt_markers");

            RHI_CommandList cmd = RHI_Device.BeginFrame();
            cmd.BeginMarker("frame");
            cmd.BeginMarker("triangle", new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
            sample.SetPipelineState(cmd, null, color);
            cmd.InsertMarker("draw");
            sample.DrawTriangle(cmd);
            Assert.Equal(2, cmd.GetMarkerDepth());
            cmd.EndMarker();
            cmd.InsertMarker("between", new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
            cmd.EndMarker();
            Assert.Equal(0, cmd.GetMarkerDepth());
            Assert.Throws<InvalidOperationException>(() => cmd.EndMarker());

            // an open marker at End throws, the list can still be closed afterwards
            cmd.BeginMarker("left open");
            RHI_Device.GpuMarking = false; // turning markers off still ends the recorded one
            Assert.Throws<InvalidOperationException>(() => cmd.End());
            cmd.EndMarker();
            RHI_Device.GpuMarking = true;
            RHI_Device.EndFrame().Wait();
        });
    }

    [SkippableFact]
    public void Markers_are_no_ops_without_debug_utils()
    {
        Run(() =>
        {
            Silk.NET.Vulkan.Extensions.EXT.ExtDebugUtils? debug_utils = RHI_Context.DebugUtils;
            bool gpu_markers = RHI_Context.Gpu_Markers;
            try
            {
                RHI_Context.DebugUtils  = null;
                RHI_Context.Gpu_Markers = false;
                Assert.False(RHI_Device.IsGpuMarkingEnabled);

                RHI_CommandList cmd = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
                cmd.BeginMarker("no debug utils");
                cmd.InsertMarker("still nothing");
                Assert.Equal(1, cmd.GetMarkerDepth());
                cmd.EndMarker();
                RHI_CommandList.ImmediateExecutionEnd(cmd);
            }
            finally
            {
                RHI_Context.DebugUtils  = debug_utils;
                RHI_Context.Gpu_Markers = gpu_markers;
            }

            // and with the cvar off
            RHI_Device.GpuMarking = false;
            Assert.False(RHI_Device.IsGpuMarkingEnabled);
            RHI_CommandList cmd2 = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
            cmd2.BeginMarker("off");
            cmd2.EndMarker();
            RHI_CommandList.ImmediateExecutionEnd(cmd2);
        });
    }

    [SkippableFact]
    public void Breadcrumbs_land_in_their_slot()
    {
        Run(() =>
        {
            using var buffer = new RHI_Buffer(RHI_Buffer_Type.Storage, sizeof(uint), 8, new byte[8 * sizeof(uint)], mappable: false, "breadcrumbs");
            using var sample = new RHI_TriangleSample();
            using RHI_Texture color = CreateTarget("rt_breadcrumbs");

            RHI_CommandList cmd = RHI_Device.BeginFrame();
            cmd.WriteGpuBreadcrumb(buffer, 0, 1);
            sample.SetPipelineState(cmd, null, color);
            sample.DrawTriangle(cmd);
            cmd.WriteGpuBreadcrumb(buffer, 0, 2);    // suspends the pass
            cmd.WriteGpuBreadcrumb(buffer, 5, 0xDEADBEEF);
            sample.DrawTriangle(cmd);                // and resumes it
            Assert.Throws<ArgumentOutOfRangeException>(() => cmd.WriteGpuBreadcrumb(buffer, 8, 0));
            RHI_Device.EndFrame().Wait();

            byte[] bytes = RHI_Device.ReadbackBuffer(buffer);
            uint[] values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
            Assert.Equal(new uint[] { 2, 0, 0, 0, 0, 0xDEADBEEF, 0, 0 }, values);
        });
    }
}
