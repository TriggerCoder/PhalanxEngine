// Port of SpartanEngine RHI_Device::StagingBufferAcquire/Release + Vulkan_Texture.cpp staging_budget @ 4c07baa
// Spartan creates and destroys a host-visible buffer per acquire (its "pool" is a no-op on top of VMA's pools); ours
// does the same on top of RHI_Allocator, whose Upload blocks are the pool (sub-allocated, one spare block kept).
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Phalanx.Rendering.RHI;

internal readonly record struct RHI_StagingBuffer(Buffer Buffer, RHI_Allocation Allocation)
{
    public nint MappedData => Allocation.MappedData;
    public ulong Size => Allocation.Size;
}

internal static unsafe partial class RHI_Device
{
    // spartan: staging_budget, host-visible memory held by in-flight uploads is capped
    public const ulong StagingBudgetBytes = 128UL * 1024 * 1024;
    private static readonly object s_staging_budget_mutex = new();
    private static ulong s_staging_in_flight_bytes;
    private static long s_staging_acquire_count;

    public static ulong StagingInFlightBytes { get { lock (s_staging_budget_mutex) return s_staging_in_flight_bytes; } }
    public static long StagingAcquireCount => Interlocked.Read(ref s_staging_acquire_count);

    public static RHI_StagingBuffer StagingBufferAcquire(ulong size)
    {
        Interlocked.Increment(ref s_staging_acquire_count);
        RHI_Allocation allocation = MemoryBufferCreate(out Buffer buffer, size, BufferUsageFlags.TransferSrcBit, RHI_Memory_Usage.Upload, default, "staging");
        return new RHI_StagingBuffer(buffer, allocation);
    }

    public static void StagingBufferRelease(RHI_StagingBuffer staging)
    {
        if (staging.Buffer.Handle == 0) return;
        MemoryBufferDestroy(staging.Buffer, staging.Allocation);
    }

    // an upload larger than the budget waits for exclusive use instead of never fitting
    internal static void StagingBudgetAcquire(ulong bytes)
    {
        lock (s_staging_budget_mutex)
        {
            while (!(s_staging_in_flight_bytes == 0 || s_staging_in_flight_bytes + bytes <= StagingBudgetBytes))
                Monitor.Wait(s_staging_budget_mutex);
            s_staging_in_flight_bytes += bytes;
        }
    }

    internal static void StagingBudgetRelease(ulong bytes)
    {
        lock (s_staging_budget_mutex)
        {
            s_staging_in_flight_bytes -= bytes;
            Monitor.PulseAll(s_staging_budget_mutex);
        }
    }

    // stages bytes and copies them into a gpu-only buffer on the copy queue, waits (spartan RHI_Buffer staging path)
    internal static void StageBuffer(Buffer destination, ulong destination_offset, ReadOnlySpan<byte> data)
    {
        ulong size = (ulong)data.Length;
        StagingBudgetAcquire(size);
        RHI_StagingBuffer staging = StagingBufferAcquire(size);
        try
        {
            data.CopyTo(new Span<byte>((void*)staging.MappedData, data.Length));
            RHI_CommandList cmd_list = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Copy);
            cmd_list.CopyBuffer(staging.Buffer, 0, destination, destination_offset, size);
            RHI_CommandList.ImmediateExecutionEnd(cmd_list);
        }
        finally
        {
            StagingBufferRelease(staging);
            StagingBudgetRelease(size);
        }
    }
}
