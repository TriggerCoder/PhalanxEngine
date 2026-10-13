// Port of SpartanEngine RHI_Device::BeginFrame/EndFrame/Cmd and DeletionQueueAdd/Parse/Flush @ 4c07baa (step 2).
// Frames in flight are bounded by the timeline value of the graphics submission FramesInFlight frames ago; the
// deletion queue captures all pending work at add time and destroys the resource once that work completed.
// TODO(port): RHI_Frame_List ComputeA/ComputeB (async compute) and SetFrameWait arrive with the renderer.
using System.Diagnostics;
using Silk.NET.Vulkan;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    public const int FramesInFlight = 2;

    private readonly record struct DeletionEntry(RHI_Resource_Type Type, ulong Resource, RHI_Allocation? Allocation, RHI_PendingWork Completion);

    private static readonly RHI_Work[] s_frame_work = new RHI_Work[FramesInFlight];
    private static ulong s_frame_index;
    private static RHI_CommandList? s_frame_cmd;
    private static readonly object s_mutex_deletion_queue = new();
    private static readonly List<DeletionEntry> s_deletion_queue = new();

    // frames begun so far (spartan: Renderer::GetFrameNumber equivalent for the rhi)
    public static ulong FrameIndex => s_frame_index;
    // cpu time BeginFrame spent waiting for the gpu (frames-in-flight throttle)
    public static double FrameCpuWaitMs { get; private set; }

    private static void FrameReset()
    {
        Array.Clear(s_frame_work);
        s_frame_index = 0;
        s_frame_cmd   = null;
    }

    // waits until the frame that used this slot finished, retires deletions, and begins the graphics list
    public static RHI_CommandList BeginFrame()
    {
        if (s_frame_cmd is { State: RHI_CommandListState.Recording })
            throw new InvalidOperationException("BeginFrame called twice without EndFrame");

        long start = Stopwatch.GetTimestamp();
        s_frame_work[s_frame_index % FramesInFlight].Wait();
        FrameCpuWaitMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        DeletionQueueParse();
        s_frame_cmd = GetQueue(RHI_Queue_Type.Graphics).NextCommandList();
        s_frame_cmd.Begin();
        return s_frame_cmd;
    }

    public static RHI_CommandList Cmd() => s_frame_cmd ?? throw new InvalidOperationException("no frame begun");

    // ends and submits the frame's list; the semaphores are the swapchain's acquire/rendering-complete pair (or null)
    public static RHI_Work EndFrame(RHI_SyncPrimitive? semaphore_wait = null, RHI_SyncPrimitive? semaphore_signal = null,
                                   RHI_SyncPrimitive? timeline_wait = null, ulong timeline_wait_value = 0)
    {
        RHI_CommandList cmd = Cmd();
        cmd.End();
        RHI_Work work = GetQueue(RHI_Queue_Type.Graphics).Submit(cmd, semaphore_wait, semaphore_signal, null, 0, timeline_wait, timeline_wait_value);
        s_frame_work[s_frame_index % FramesInFlight] = work;
        s_frame_index++;
        return work;
    }

    // the work of the frame that ends in that slot, for tests and the profiler
    public static RHI_Work GetFrameWork(ulong frame_index) => s_frame_work[frame_index % FramesInFlight];

    // ------------------------------------------------------------------------------------------------------------
    // deletion queue

    // allocation (optional) is freed after the resource is destroyed
    public static void DeletionQueueAdd(RHI_Resource_Type resource_type, ulong resource, RHI_Allocation? allocation = null)
    {
        if (resource == 0 && allocation == null) return;
        RHI_PendingWork completion = RHI_CommandList.CapturePendingWork();
        lock (s_mutex_deletion_queue)
            s_deletion_queue.Add(new DeletionEntry(resource_type, resource, allocation, completion));
    }

    public static int DeletionQueueCount() { lock (s_mutex_deletion_queue) return s_deletion_queue.Count; }

    public static bool DeletionQueueNeedsToParse() => DeletionQueueCount() > 0;

    // destroys every entry whose captured work has completed, returns how many
    public static int DeletionQueueParse()
    {
        List<DeletionEntry> retired = new();
        lock (s_mutex_deletion_queue)
        {
            var completed = new Dictionary<RHI_PendingWork, bool>(ReferenceEqualityComparer.Instance);
            s_deletion_queue.RemoveAll(entry =>
            {
                if (!completed.TryGetValue(entry.Completion, out bool done))
                    completed[entry.Completion] = done = entry.Completion.IsComplete();
                if (done) retired.Add(entry);
                return done;
            });
        }
        foreach (DeletionEntry entry in retired)
            DestroyResource(entry);
        return retired.Count;
    }

    public static void DeletionQueueFlush()
    {
        List<DeletionEntry> all;
        lock (s_mutex_deletion_queue)
        {
            all = new List<DeletionEntry>(s_deletion_queue);
            s_deletion_queue.Clear();
        }
        if (all.Count == 0) return;
        QueueWaitAll(); // not the captured work: a reserved value of a list that never submits would never signal
        foreach (DeletionEntry entry in all)
            DestroyResource(entry);
    }

    private static void DestroyResource(DeletionEntry entry)
    {
        Vk vk = RHI_Context.Vk;
        Device device = RHI_Context.Device;
        switch (entry.Type)
        {
            case RHI_Resource_Type.Buffer:      if (entry.Resource != 0) vk.DestroyBuffer(device, new Silk.NET.Vulkan.Buffer(entry.Resource), null); break;
            case RHI_Resource_Type.Image:       if (entry.Resource != 0) vk.DestroyImage(device, new Image(entry.Resource), null); break;
            case RHI_Resource_Type.ImageView:   vk.DestroyImageView(device, new ImageView(entry.Resource), null); break;
            case RHI_Resource_Type.Sampler:     vk.DestroySampler(device, new Sampler(entry.Resource), null); break;
            case RHI_Resource_Type.Semaphore:   vk.DestroySemaphore(device, new Silk.NET.Vulkan.Semaphore(entry.Resource), null); break;
            case RHI_Resource_Type.Fence:       vk.DestroyFence(device, new Fence(entry.Resource), null); break;
            case RHI_Resource_Type.Pipeline:    vk.DestroyPipeline(device, new Pipeline(entry.Resource), null); break;
            case RHI_Resource_Type.PipelineLayout: vk.DestroyPipelineLayout(device, new PipelineLayout(entry.Resource), null); break;
            case RHI_Resource_Type.DescriptorSetLayout: vk.DestroyDescriptorSetLayout(device, new DescriptorSetLayout(entry.Resource), null); break;
            case RHI_Resource_Type.QueryPool:   vk.DestroyQueryPool(device, new QueryPool(entry.Resource), null); break;
            case RHI_Resource_Type.Shader:      vk.DestroyShaderModule(device, new ShaderModule(entry.Resource), null); break;
            case RHI_Resource_Type.DeviceMemory: break; // only through the allocation
            default: throw new NotSupportedException($"DeletionQueue: {entry.Type}");
        }
        if (entry.Allocation != null)
            s_allocator!.Free(entry.Allocation);
    }
}
