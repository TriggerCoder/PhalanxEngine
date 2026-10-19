// The gpu half of SpartanEngine profiling/Breadcrumbs.h/.cpp @ 4c07baa (Initialize :41-75, GpuMarkerBegin :97-121,
// ResetGpuMarkers :142-178, the begin/end writes of Vulkan_CommandList.cpp:2922-2972, WriteReport :180-).
// Deviations: only the frame's graphics list records (spartan: a buffer per queue type); the slots are kept per frame
// slot for the frames in flight instead of the current and the previous frame; no cpu marker stack; the report is
// text for RHI_Device.ReportDeviceLost. Startup only (RenderValidation.GpuBreadcrumbs, spartan: debug.breadcrumbs):
// each marker adds a fill and two barriers, which serialise the passes a little.
using System.Text;

namespace Phalanx.Rendering.RHI;

internal static unsafe class RHI_Breadcrumbs
{
    public const int MaxMarkers = 128; // per frame (spartan: max_gpu_markers)
    public const int FrameSlots = 4;
    private const uint begun = 1, completed = 2;

    private static RHI_Buffer? s_buffer;
    private static readonly string?[][] s_names = Enumerable.Range(0, FrameSlots).Select(_ => new string?[MaxMarkers]).ToArray();
    private static readonly int[] s_counts = new int[FrameSlots];
    private static readonly ulong[] s_frames = new ulong[FrameSlots];
    private static readonly bool[] s_used = new bool[FrameSlots];
    private static readonly Stack<int> s_open = new();
    private static RHI_CommandList? s_list;
    private static int s_slot;

    public static bool IsEnabled => s_buffer != null;

    public static void Initialize()
    {
        Destroy();
        if ((RHI_Device.ValidationFeatures & RenderValidation.GpuBreadcrumbs) == 0)
            return;
        s_buffer = new RHI_Buffer(RHI_Buffer_Type.Storage, sizeof(uint), MaxMarkers * FrameSlots, ReadOnlySpan<byte>.Empty, mappable: true, "breadcrumbs_gpu_graphics");
        new Span<uint>((void*)s_buffer.GetMappedData(), MaxMarkers * FrameSlots).Clear();
        Array.Clear(s_counts);
        Array.Clear(s_used);
    }

    public static void Destroy()
    {
        s_buffer?.Dispose();
        s_buffer = null;
        s_list = null;
        s_open.Clear();
    }

    // the frame slot's previous frame finished (BeginFrame waited for it), so its slots can be cleared from the cpu
    public static void BeginFrame(RHI_CommandList list, ulong frame)
    {
        if (s_buffer == null)
            return;
        s_slot = (int)(frame % FrameSlots);
        new Span<uint>((uint*)s_buffer.GetMappedData() + s_slot * MaxMarkers, MaxMarkers).Clear();
        Array.Clear(s_names[s_slot]);
        s_counts[s_slot] = 0;
        s_frames[s_slot] = frame;
        s_used[s_slot]   = true;
        s_list = list;
        s_open.Clear();
    }

    public static void EndFrame() => s_list = null;

    public static void Begin(RHI_CommandList list, string name)
    {
        if (s_buffer == null || !ReferenceEquals(list, s_list))
            return;
        int index = s_counts[s_slot] < MaxMarkers ? s_counts[s_slot]++ : -1;
        s_open.Push(index);
        if (index < 0)
            return;
        s_names[s_slot][index] = name;
        list.WriteGpuBreadcrumb(s_buffer, (uint)(s_slot * MaxMarkers + index), begun);
    }

    public static void End(RHI_CommandList list)
    {
        if (s_buffer == null || !ReferenceEquals(list, s_list) || s_open.Count == 0)
            return;
        int index = s_open.Pop();
        if (index >= 0)
            list.WriteGpuBreadcrumb(s_buffer, (uint)(s_slot * MaxMarkers + index), completed);
    }

    // per frame in flight, oldest first: what the gpu completed, what it started and didn't finish (the crash point)
    public static void Report(StringBuilder report)
    {
        if (s_buffer == null)
        {
            report.AppendLine("  breadcrumbs: off (launch with --gpu-breadcrumbs to record them)");
            return;
        }
        uint* values = (uint*)s_buffer.GetMappedData();
        foreach (int slot in Enumerable.Range(0, FrameSlots).Where(s => s_used[s]).OrderBy(s => s_frames[s]))
        {
            var done = new List<string>();
            var started = new List<string>();
            var pending = new List<string>();
            for (int i = 0; i < s_counts[slot]; i++)
            {
                string name = s_names[slot][i] ?? "?";
                uint value = values[slot * MaxMarkers + i];
                (value == completed ? done : value == begun ? started : pending).Add(name);
            }
            report.AppendLine($"  breadcrumbs frame {s_frames[slot]}: {done.Count} completed{(done.Count > 0 ? $" (last {done[^1]})" : "")}" +
                              $", started not finished: {(started.Count > 0 ? string.Join(", ", started) : "none")}" +
                              $", not started: {(pending.Count > 0 ? string.Join(", ", pending) : "none")}");
        }
    }
}
