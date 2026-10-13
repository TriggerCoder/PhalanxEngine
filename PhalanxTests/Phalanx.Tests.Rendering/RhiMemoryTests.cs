using Phalanx.Rendering.RHI;
using Silk.NET.Vulkan;
using Xunit;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Phalanx.Tests;

// no gpu: the sub-allocator and the allocation policy (phalanx-rhi-plan.md §7)
public class RhiAllocatorLogicTests
{
    [Theory]
    [InlineData(0UL, 256UL, 0UL)]
    [InlineData(1UL, 256UL, 256UL)]
    [InlineData(256UL, 256UL, 256UL)]
    [InlineData(257UL, 4UL, 260UL)]
    [InlineData(5UL, 1UL, 5UL)]
    [InlineData(5UL, 0UL, 5UL)]
    public void AlignUp(ulong value, ulong alignment, ulong expected) =>
        Assert.Equal(expected, RHI_Allocator_FreeList.AlignUp(value, alignment));

    [Fact]
    public void AlignUp_rejects_non_power_of_two() =>
        Assert.Throws<ArgumentException>(() => RHI_Allocator_FreeList.AlignUp(10, 3));

    [Fact]
    public void Allocations_are_aligned_and_padding_stays_free()
    {
        var list = new RHI_Allocator_FreeList(4096);
        Assert.Equal(0UL, list.Allocate(10, 1));
        ulong b = list.Allocate(100, 256);
        Assert.Equal(256UL, b);
        Assert.Equal(110UL, list.UsedBytes);
        Assert.Contains((10UL, 246UL), list.FreeRanges);        // the padding is reusable
        Assert.Equal(10UL, list.Allocate(200, 2));             // first fit lands in it
    }

    [Fact]
    public void Free_coalesces_with_both_neighbours_in_any_order()
    {
        var order = new[] { new[] { 0, 1, 2, 3 }, new[] { 3, 2, 1, 0 }, new[] { 1, 3, 0, 2 }, new[] { 2, 0, 3, 1 } };
        foreach (int[] o in order)
        {
            var list = new RHI_Allocator_FreeList(1000);
            ulong[] offsets = Enumerable.Range(0, 4).Select(_ => list.Allocate(250, 1)).ToArray();
            Assert.Equal(new ulong[] { 0, 250, 500, 750 }, offsets);
            Assert.Equal(0, list.FreeRangeCount);
            foreach (int i in o)
                list.Free(offsets[i]);
            Assert.True(list.IsEmpty);
            Assert.Equal(1, list.FreeRangeCount);
            Assert.Equal((0UL, 1000UL), list.FreeRanges[0]);
        }
    }

    [Fact]
    public void Hole_is_reused_and_full_block_returns_invalid()
    {
        var list = new RHI_Allocator_FreeList(300);
        ulong a = list.Allocate(100, 1), b = list.Allocate(100, 1), c = list.Allocate(100, 1);
        Assert.Equal(RHI_Allocator_FreeList.Invalid, list.Allocate(1, 1));
        list.Free(b);
        Assert.Equal(RHI_Allocator_FreeList.Invalid, list.Allocate(101, 1));
        Assert.Equal(b, list.Allocate(100, 1));
        Assert.Equal(new[] { 0UL, 100UL, 200UL }, new[] { a, b, c });
        Assert.Throws<InvalidOperationException>(() => list.Free(50));
    }

    [Fact]
    public void Random_alloc_free_keeps_invariants()
    {
        var random = new Random(1234);
        var list = new RHI_Allocator_FreeList(1 << 20);
        var live = new List<(ulong offset, ulong size)>();
        for (int step = 0; step < 5000; step++)
        {
            if (live.Count > 0 && random.Next(3) == 0)
            {
                int i = random.Next(live.Count);
                list.Free(live[i].offset);
                live.RemoveAt(i);
            }
            else
            {
                ulong size = (ulong)random.Next(1, 8192), alignment = 1UL << random.Next(0, 9);
                ulong offset = list.Allocate(size, alignment);
                if (offset == RHI_Allocator_FreeList.Invalid) continue;
                Assert.Equal(0UL, offset % alignment);
                live.Add((offset, size));
            }
            // free ranges sorted, non adjacent, non overlapping; total accounts for everything
            var free = list.FreeRanges;
            for (int i = 1; i < free.Count; i++)
                Assert.True(free[i - 1].offset + free[i - 1].size < free[i].offset);
        }
        var sorted = live.OrderBy(l => l.offset).ToList();
        for (int i = 1; i < sorted.Count; i++)
            Assert.True(sorted[i - 1].offset + sorted[i - 1].size <= sorted[i].offset, "overlap");
        Assert.Equal((ulong)live.Sum(l => (long)l.size), list.UsedBytes);
        foreach (var l in live) list.Free(l.offset);
        Assert.Equal(1, list.FreeRangeCount);
    }

    [Fact]
    public void Linear_and_optimal_resources_never_share_a_block()
    {
        // bufferImageGranularity only matters between linear and optimal neighbours in one VkDeviceMemory
        Assert.NotEqual(RHI_Allocator_Policy.BlockKey(3, RHI_Memory_Kind.Linear), RHI_Allocator_Policy.BlockKey(3, RHI_Memory_Kind.Optimal));
        var keys = new HashSet<int>();
        for (uint t = 0; t < 32; t++)
            foreach (RHI_Memory_Kind k in Enum.GetValues<RHI_Memory_Kind>())
                Assert.True(keys.Add(RHI_Allocator_Policy.BlockKey(t, k)));
    }

    [Fact]
    public void Block_size_and_dedicated_threshold()
    {
        Assert.Equal(64UL << 20, RHI_Allocator_Policy.BlockSize(8UL << 30));
        Assert.Equal(32UL << 20, RHI_Allocator_Policy.BlockSize(256UL << 20));
        Assert.Equal(4UL << 20, RHI_Allocator_Policy.BlockSize(1UL << 20));
        Assert.False(RHI_Allocator_Policy.UseDedicated(32UL << 20, 64UL << 20, false));
        Assert.True(RHI_Allocator_Policy.UseDedicated((32UL << 20) + 1, 64UL << 20, false));
        Assert.True(RHI_Allocator_Policy.UseDedicated(16, 64UL << 20, true));
    }

    [Fact]
    public void Readback_prefers_cached_and_upload_avoids_it()
    {
        var types = new[]
        {
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.DeviceLocalBit,
        };
        Assert.Equal(0, RHI_Allocator_Policy.FindMemoryType(0b111, RHI_Memory_Usage.Readback, types));
        Assert.Equal(1, RHI_Allocator_Policy.FindMemoryType(0b111, RHI_Memory_Usage.Upload, types));
        Assert.Equal(2, RHI_Allocator_Policy.FindMemoryType(0b111, RHI_Memory_Usage.GpuOnly, types));
        // gpu-only falls back to non device-local memory (software drivers)
        Assert.Equal(1, RHI_Allocator_Policy.FindMemoryType(0b010, RHI_Memory_Usage.GpuOnly, types));
    }
}

// lavapipe or any vulkan 1.3 driver: allocator, timelines, frames in flight, deletion queue
[Collection("gpu")]
public class RhiMemoryGpuTests
{
    private static void Run(Action body)
    {
        Skip.IfNot(Phalanx.Rendering.RenderDevice.IsVulkanAvailable(out string reason), reason);
        RHI_Device.Initialize(Array.Empty<string>(), validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal)), string.Join("\n", RHI_Device.ValidationMessages));
        }
        finally
        {
            RHI_Device.Destroy();
        }
    }

    [SkippableFact]
    public void Many_allocations_and_frees_suballocate_and_release()
    {
        Run(() =>
        {
            var random = new Random(7);
            var live = new List<(Buffer buffer, RHI_Allocation allocation)>();
            var live_images = new List<(Image image, RHI_Allocation allocation)>();
            for (int i = 0; i < 400; i++)
            {
                RHI_Memory_Usage usage = (RHI_Memory_Usage)(i % 3);
                ulong size = (ulong)random.Next(16, 256 * 1024);
                live.Add((default, RHI_Device.MemoryBufferCreate(out Buffer b, size, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, usage, default, $"b{i}")));
                live[^1] = (b, live[^1].allocation);
                if (i % 20 == 0)
                {
                    RHI_Allocation a = RHI_Device.MemoryTextureCreate2D(out Image img, 64, 64, RHI_Format.R8G8B8A8_Unorm, ImageUsageFlags.SampledBit, $"t{i}");
                    live_images.Add((img, a));
                }
                if (i % 4 == 3) // free a random one
                {
                    int k = random.Next(live.Count);
                    RHI_Device.MemoryBufferDestroy(live[k].buffer, live[k].allocation);
                    live.RemoveAt(k);
                }
            }

            RHI_Allocator_Stats stats = RHI_Device.MemoryGetStats();
            Assert.Equal(live.Count + live_images.Count, stats.AllocationCount);
            Assert.True(stats.BlockCount <= 12, $"blocks {stats.BlockCount}");      // sub-allocated, not one per resource
            Assert.True(stats.BytesUsed <= stats.BytesReserved);

            // memory never overlaps within one VkDeviceMemory, mapped pointers only for host pools
            var all = live.Select(l => l.allocation).Concat(live_images.Select(l => l.allocation)).ToList();
            foreach (var group in all.GroupBy(a => a.Memory.Handle))
            {
                var sorted = group.OrderBy(a => a.Offset).ToList();
                for (int i = 1; i < sorted.Count; i++)
                    Assert.True(sorted[i - 1].Offset + sorted[i - 1].Size <= sorted[i].Offset);
                Assert.Single(group.Select(a => a.MemoryTypeIndex).Distinct());
            }
            Assert.All(all.Where(a => a.Usage != RHI_Memory_Usage.GpuOnly), a => Assert.NotEqual(0, a.MappedData));

            // write/read through the persistent mapping
            var upload = live.First(l => l.allocation.Usage == RHI_Memory_Usage.Upload).allocation;
            System.Runtime.InteropServices.Marshal.Copy(Enumerable.Repeat((byte)0xAB, 16).ToArray(), 0, upload.MappedData, 16);
            Assert.Equal(0xAB, System.Runtime.InteropServices.Marshal.ReadByte(upload.MappedData, 15));

            foreach (var (b, a) in live) RHI_Device.MemoryBufferDestroy(b, a);
            foreach (var (img, a) in live_images) RHI_Device.MemoryTextureDestroy(img, a);
            stats = RHI_Device.MemoryGetStats();
            Assert.Equal(0, stats.AllocationCount);
            Assert.Equal(0UL, stats.BytesUsed);
            Assert.True(stats.BlockCount <= 4, $"empty blocks kept {stats.BlockCount}"); // at most one spare per pool
        });
    }

    [SkippableFact]
    public void Large_resources_get_dedicated_memory()
    {
        Run(() =>
        {
            RHI_Allocation a = RHI_Device.MemoryBufferCreate(out Buffer b, 48UL << 20, BufferUsageFlags.StorageBufferBit, RHI_Memory_Usage.GpuOnly, default, "large");
            Assert.True(a.IsDedicated);
            Assert.Equal(1, RHI_Device.MemoryGetStats().DedicatedCount);
            RHI_Device.MemoryBufferDestroy(b, a);
            Assert.Equal(0, RHI_Device.MemoryGetStats().DedicatedCount);
            Assert.Throws<InvalidOperationException>(() => RHI_Device.Allocator.Free(a));
        });
    }

    [SkippableFact]
    public void Timeline_values_increase_and_waits_are_exact()
    {
        Run(() =>
        {
            RHI_Queue queue = RHI_Device.GetQueue(RHI_Queue_Type.Graphics);
            using var host = new RHI_SyncPrimitive(RHI_SyncPrimitive_Type.SemaphoreTimeline, "host_gate");

            // gated on a host signal: must not complete until we signal
            RHI_CommandList cmd = queue.NextCommandList();
            ulong before = cmd.GetLastTimelineSignalValue();
            cmd.Begin();
            cmd.End();
            RHI_Work work = queue.Submit(cmd, null, null, null, 0, host, 1);
            Assert.Equal(before + 1, work.Value);
            Thread.Sleep(50);
            Assert.False(work.IsComplete());
            Assert.True(cmd.IsExecuting());
            host.Signal(1);
            work.Wait();
            Assert.True(work.IsComplete());
            Assert.False(cmd.IsExecuting());
            Assert.True(cmd.GetTimelineSemaphore().GetCompletedValue() >= work.Value);

            // the ring wraps: every list's value keeps increasing
            var values = new Dictionary<RHI_CommandList, ulong>();
            for (int i = 0; i < RHI_Queue.CommandListCount(RHI_Queue_Type.Graphics) * 3; i++)
            {
                RHI_CommandList c = queue.NextCommandList();
                c.Begin(); c.End();
                RHI_Work w = queue.Submit(c, null, null);
                if (values.TryGetValue(c, out ulong last)) Assert.Equal(last + 1, w.Value);
                values[c] = w.Value;
            }
            queue.Wait();
            Assert.All(values, kv => Assert.True(kv.Key.GetWork().IsComplete()));
        });
    }

    [SkippableFact]
    public void Frames_in_flight_are_bounded()
    {
        Run(() =>
        {
            for (int frame = 0; frame < 20; frame++)
            {
                RHI_Device.BeginFrame();
                // the frame that used this slot is complete before recording starts
                if (frame >= RHI_Device.FramesInFlight)
                    Assert.True(RHI_Device.GetFrameWork((ulong)(frame - RHI_Device.FramesInFlight)).IsComplete());
                RHI_Device.EndFrame();
            }
            Assert.Equal(20UL, RHI_Device.FrameIndex);
        });
    }

    [SkippableFact]
    public void Deletion_queue_waits_for_pending_work()
    {
        Run(() =>
        {
            using var host = new RHI_SyncPrimitive(RHI_SyncPrimitive_Type.SemaphoreTimeline, "host_gate");
            RHI_Allocation a = RHI_Device.MemoryBufferCreate(out Buffer buffer, 1024, BufferUsageFlags.TransferDstBit, RHI_Memory_Usage.GpuOnly, default, "deferred");

            // gpu work that uses the buffer and cannot finish until the host signals
            RHI_CommandList cmd = RHI_Device.BeginFrame();
            RHI_Context.Vk.CmdFillBuffer(cmd.GetRhiResource(), buffer, 0, 1024, 0xdeadbeef);
            RHI_Device.EndFrame(timeline_wait: host, timeline_wait_value: 1);

            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Buffer, buffer.Handle, a);
            Assert.Equal(0, RHI_Device.DeletionQueueParse());
            Assert.Equal(1, RHI_Device.DeletionQueueCount());
            Assert.Equal(1, RHI_Device.MemoryGetStats().AllocationCount);

            host.Signal(1);
            RHI_Device.QueueWaitAll();
            Assert.Equal(1, RHI_Device.DeletionQueueParse());
            Assert.Equal(0, RHI_Device.DeletionQueueCount());
            Assert.Equal(0, RHI_Device.MemoryGetStats().AllocationCount);

            // with nothing pending the next parse retires immediately; flush handles the rest at shutdown
            RHI_Allocation b = RHI_Device.MemoryBufferCreate(out Buffer buffer_b, 64, BufferUsageFlags.TransferDstBit, RHI_Memory_Usage.Upload, default, "deferred_b");
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Buffer, buffer_b.Handle, b);
            RHI_Device.DeletionQueueFlush();
            Assert.Equal(0, RHI_Device.MemoryGetStats().AllocationCount);
        });
    }
}
