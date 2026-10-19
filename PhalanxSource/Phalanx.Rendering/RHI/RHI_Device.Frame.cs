// Port of SpartanEngine RHI_Device::BeginFrame/EndFrame/Cmd/Submit/SetFrameWait, the swapchain ownership and
// DeletionQueueAdd/Parse/Flush @ 4c07baa (RHI_Device.cpp:233-420).
// Frames in flight are bounded by the timeline value of the graphics submission FramesInFlight frames ago; the
// deletion queue captures all pending work at add time and destroys the resource once that work completed.
// Deviations: the compute lists wait for nothing implicitly (spartan neither); SetFrameWait takes RHI_Work (a
// timeline value) where spartan takes a command list; EndFrame still accepts explicit semaphores for callers that
// drive their own swapchain (tests, secondary windows).
using System.Diagnostics;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Phalanx.Rendering.RHI;

internal enum RHI_Frame_List { Graphics, ComputeA, ComputeB, Max }

internal static unsafe partial class RHI_Device
{
    public const int FramesInFlight = 2;

    private readonly record struct DeletionEntry(RHI_Resource_Type Type, ulong Resource, RHI_Allocation? Allocation, RHI_PendingWork Completion);

    private static readonly RHI_Work[] s_frame_work = new RHI_Work[FramesInFlight];
    private static ulong s_frame_index;
    private static readonly RHI_CommandList?[] s_frame_lists = new RHI_CommandList?[(int)RHI_Frame_List.Max];
    private static RHI_Frame_List s_bound_list = RHI_Frame_List.Graphics;
    private static RHI_Work s_frame_wait;
    private static RHI_SwapChain? s_swapchain;
    private static readonly object s_mutex_deletion_queue = new();
    private static readonly List<DeletionEntry> s_deletion_queue = new();

    // frames begun so far (spartan: Renderer::GetFrameNumber equivalent for the rhi)
    public static ulong FrameIndex => s_frame_index;
    // cpu time BeginFrame spent waiting for the gpu (frames-in-flight throttle)
    public static double FrameCpuWaitMs { get; private set; }

    private static void FrameReset()
    {
        Array.Clear(s_frame_work);
        Array.Clear(s_frame_lists);
        s_frame_index = 0;
        s_bound_list  = RHI_Frame_List.Graphics;
        s_frame_wait  = default;
    }

    // ------------------------------------------------------------------------------------------------------------
    // swapchain (spartan: CreateSwapChain/DestroySwapChain/GetSwapChain/AcquireSwapChainImage)

    public static RHI_SwapChain CreateSwapChain(IWindow window, RHI_Present_Mode present_mode = RHI_Present_Mode.Fifo, bool hdr = false, string name = "swapchain")
    {
        DestroySwapChain();
        s_swapchain = new RHI_SwapChain(window, (uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y, present_mode, RHI_SwapChain.BufferCount, hdr, name);
        return s_swapchain;
    }

    public static void DestroySwapChain()
    {
        s_swapchain?.Dispose();
        s_swapchain = null;
    }

    public static RHI_SwapChain? GetSwapChain() => s_swapchain;

    public static bool AcquireSwapChainImage() => s_swapchain?.AcquireNextImage() ?? false;

    // spartan: BlitToBackBuffer(cmd_list, texture)
    public static void BlitToBackBuffer(RHI_CommandList cmd_list, RHI_Texture texture)
    {
        if (s_swapchain is { IsImageAcquired: true })
            cmd_list.Blit(texture, s_swapchain);
    }

    // ------------------------------------------------------------------------------------------------------------
    // frame

    // waits until the frame that used this slot finished, retires deletions, begins the graphics list and, when
    // acquire_compute is set, the two async compute lists (spartan: BeginFrame(bool acquire_compute), :248)
    public static RHI_CommandList BeginFrame(bool acquire_compute = false)
    {
        if (s_frame_lists[(int)RHI_Frame_List.Graphics] is { State: RHI_CommandListState.Recording })
            throw new InvalidOperationException("BeginFrame called twice without EndFrame");

        long start = Stopwatch.GetTimestamp();
        s_frame_work[s_frame_index % FramesInFlight].Wait();
        FrameCpuWaitMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        DescriptorSetTick(s_frame_index); // spartan RHI_Device::Tick: descriptor frame + eviction, then the deletion queue
        DeletionQueueParse();
        Array.Clear(s_frame_lists);
        s_frame_wait = default;
        RHI_CommandList graphics = GetQueue(RHI_Queue_Type.Graphics).NextCommandList();
        graphics.Begin();
        s_frame_lists[(int)RHI_Frame_List.Graphics] = graphics;
        RHI_Breadcrumbs.BeginFrame(graphics, s_frame_index);
        SetCheckpoint(graphics, "frame", end: false);
        if (acquire_compute)
        {
            RHI_Queue compute = GetQueue(RHI_Queue_Type.Compute);
            foreach (RHI_Frame_List list in new[] { RHI_Frame_List.ComputeA, RHI_Frame_List.ComputeB })
            {
                RHI_CommandList cmd = compute.NextCommandList();
                cmd.Begin();
                s_frame_lists[(int)list] = cmd;
            }
        }
        s_bound_list = RHI_Frame_List.Graphics;
        return graphics;
    }

    public static RHI_CommandList Cmd() => Cmd(s_bound_list);
    public static RHI_CommandList Cmd(RHI_Frame_List list) => s_frame_lists[(int)list] ?? throw new InvalidOperationException($"no {list} list begun this frame");
    public static void Bind(RHI_Frame_List list) => s_bound_list = list;
    public static bool IsRecording(RHI_Frame_List list) => s_frame_lists[(int)list] is { State: RHI_CommandListState.Recording };

    // ends and submits one frame list early (spartan: Submit(list, ...)); graphics waits on SetFrameWait if set
    public static RHI_Work Submit(RHI_Frame_List list, RHI_SyncPrimitive? timeline_wait = null, ulong timeline_wait_value = 0)
    {
        RHI_CommandList cmd = Cmd(list);
        cmd.End();
        RHI_Queue_Type type = list == RHI_Frame_List.Graphics ? RHI_Queue_Type.Graphics : RHI_Queue_Type.Compute;
        return GetQueue(type).Submit(cmd, null, null, null, 0, timeline_wait, timeline_wait_value);
    }

    // the graphics submission of this frame waits for this work on the gpu (spartan: SetFrameWait, :350)
    public static void SetFrameWait(RHI_Work work) => s_frame_wait = work;

    // ends the frame: submits open compute lists, then graphics; with the device swapchain's image acquired the back
    // buffer goes to present source, the submission waits/signals the swapchain semaphores and the image is presented
    public static RHI_Work EndFrame(RHI_SyncPrimitive? semaphore_wait = null, RHI_SyncPrimitive? semaphore_signal = null,
                                   RHI_SyncPrimitive? timeline_wait = null, ulong timeline_wait_value = 0)
    {
        foreach (RHI_Frame_List list in new[] { RHI_Frame_List.ComputeA, RHI_Frame_List.ComputeB })
            if (IsRecording(list))
                Submit(list);

        RHI_CommandList cmd = Cmd(RHI_Frame_List.Graphics);
        if (timeline_wait == null && s_frame_wait.Semaphore != null && !s_frame_wait.IsComplete())
        {
            timeline_wait       = s_frame_wait.Semaphore;
            timeline_wait_value = s_frame_wait.Value;
        }

        bool present = semaphore_wait == null && semaphore_signal == null && s_swapchain is { IsImageAcquired: true };
        if (present)
        {
            cmd.PrepareForPresent(s_swapchain!);
            semaphore_wait   = s_swapchain!.GetImageAcquiredSemaphore();
            semaphore_signal = s_swapchain.GetRenderingCompleteSemaphore();
        }
        SetCheckpoint(cmd, "frame", end: true);
        RHI_Breadcrumbs.EndFrame();
        cmd.End();
        RHI_Work work = GetQueue(RHI_Queue_Type.Graphics).Submit(cmd, semaphore_wait, semaphore_signal, null, 0, timeline_wait, timeline_wait_value);
        if (present)
        {
            s_swapchain!.SetAcquireConsumer(work);
            s_swapchain.Present();
        }
        s_frame_work[s_frame_index % FramesInFlight] = work;
        s_frame_index++;
        s_bound_list = RHI_Frame_List.Graphics;
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
            case RHI_Resource_Type.DescriptorSet: ReleaseDescriptorSet(entry.Resource); break;
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
