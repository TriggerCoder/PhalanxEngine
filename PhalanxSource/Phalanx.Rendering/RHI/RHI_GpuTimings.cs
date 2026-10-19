// Per pass gpu times of the frame's graphics list, from timestamp queries: the frame and every top level marker
// (depth_prepass, g_buffer, shadow_maps, light, screen_space_shadows, aa_upscale...) get a timestamp at their begin
// and end; once the list's work completed its next Begin reads them and adds the durations here, and the console
// line of the stress mode prints the averages (Renderer.GetGpuTimings).
// Spartan's counterpart is the gpu half of its timeblocks: begin_timeblock / end_timeblock time every pass with
// begin_timestamp / end_timestamp when cvar_debug_gpu_timing is set (Vulkan_CommandList.cpp:3179-3190, TimeBlock.cpp:67,
// the queries at :3024-3107) and the profiler averages them per block name.
// Deviations:
// - Only the frame and the top level markers are timed (the profiler's nested blocks aren't ported), each a pair of
//   vkCmdWriteTimestamp2 at all commands (RHI_CommandList.WriteTimestamp), the same stage spartan uses.
// - The results accumulate per name until TakeAverages, no history or graphs.
// - Opt in (Enabled, RenderDevice.GpuPassTiming), on top of RHI_Device.GpuTiming (spartan: cvar_debug_gpu_timing):
//   the stress mode turns it on (--gpu-timing off leaves it off), the tests and the editor don't pay for it.
namespace Phalanx.Rendering.RHI;

internal static class RHI_GpuTimings
{
    public static volatile bool Enabled;

    private static readonly object s_mutex = new();
    private static readonly List<string> s_order = new();
    private static readonly Dictionary<string, double> s_sums = new();
    private static double s_frame_sum;
    private static int s_frames;

    public static void Add(string name, double ms)
    {
        lock (s_mutex)
        {
            if (!s_sums.TryGetValue(name, out double sum))
                s_order.Add(name);
            s_sums[name] = sum + ms;
        }
    }

    public static void AddFrame(double ms)
    {
        lock (s_mutex)
        {
            s_frame_sum += ms;
            s_frames++;
        }
    }

    /// <summary>The frames read back since the last call, their average gpu frame time and every pass's average per
    /// frame, in the order the passes first appeared; resets the sums.</summary>
    public static (int frames, double frame_ms, List<(string name, double ms)> passes) TakeAverages()
    {
        lock (s_mutex)
        {
            var passes = new List<(string, double)>(s_order.Count);
            int frames = s_frames;
            if (frames > 0)
                foreach (string name in s_order)
                    if (s_sums[name] > 0.0)
                        passes.Add((name, s_sums[name] / frames));
            double frame_ms = frames > 0 ? s_frame_sum / frames : 0.0;
            foreach (string name in s_order)
                s_sums[name] = 0.0;
            s_frame_sum = 0.0;
            s_frames = 0;
            return (frames, frame_ms, passes);
        }
    }

    public static void Clear()
    {
        lock (s_mutex)
        {
            s_order.Clear();
            s_sums.Clear();
            s_frame_sum = 0.0;
            s_frames = 0;
        }
    }
}
