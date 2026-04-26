using System;
using System.Diagnostics;

namespace Phalanx;
public static class Time
{
    public enum FpsLimitType
    {
        Unlocked,
        Fixed,
        FixedToMonitor
    };

    // accumulation
    private const uint frames_to_accumulate = 15;
    private const double weight_delta = (1.0f / frames_to_accumulate);

    // frame time
    private static double time_ms = 0.0f;
    private static double delta_time_ms = 0.0f;
    private static double delta_time_smoothed_ms = 0.0f;

    // fps
    private static float fps_min = 30.0f;
    private static float fps_max = 10000.0f;
    private static float fps_limit = 30.0f;
    private static float fps_limit_previous = 30.0f;

    // misc
    private static Stopwatch stopwatch = new();
    private static long last_tick_time;
    private static long stopwatch_freq;
    public static void Initialize()
    {
        fps_limit = Display.GetRefreshRate();
        stopwatch.Start();
        stopwatch_freq = Stopwatch.Frequency;
        last_tick_time = Stopwatch.GetTimestamp();
    }
    public static void PostTick()
    {
        long current_ticks = stopwatch.ElapsedTicks;

        // if this is not the first tick, we calculate the delta time
        if (last_tick_time != 0)
        {
            long deltaTicks = current_ticks - last_tick_time;
            delta_time_ms = deltaTicks * (1000.0 / stopwatch_freq);
        }

        // fps limit
        double target_ms = 1000.0 / fps_limit;
        while (delta_time_ms < target_ms)
        {
            current_ticks = stopwatch.ElapsedTicks;
            long deltaTicks = current_ticks - last_tick_time;
            delta_time_ms = deltaTicks * (1000.0 / stopwatch_freq);
        }

        // compute delta time based timings
        delta_time_smoothed_ms = delta_time_smoothed_ms * (1.0 - weight_delta) + delta_time_ms * weight_delta;
        time_ms += delta_time_ms;

        // end
        last_tick_time = stopwatch.ElapsedTicks;
    }
    public static void SetFpsLimit(float fps_in)
    {
        if (fps_in < 0.0f) // negative -> match monitor's refresh rate
        {
            fps_in = static_cast<float>(Display::GetRefreshRate());
        }

        // clamp to a minimum of 10 FPS to avoid unresponsiveness
        fps_in = System.Math.Clamp(fps_in, fps_min, fps_max);

        if (fps_limit == fps_in)
            return;

        fps_limit = fps_in;
        Log.LogInfo("Set to "+ fps_limit +" FPS" );
    }
    public static float GetFpsLimit() { return fps_limit; }

    public static FpsLimitType GetFpsLimitType()
    {
        if (fps_limit == static_cast<float>(Display::GetRefreshRate()))
            return FpsLimitType.FixedToMonitor;

        if (fps_limit == fps_max)
            return FpsLimitType.Unlocked;

        return FpsLimitType.Fixed;
    }
    public static void OnVsyncToggled(bool enabled)
    {
        if (enabled)
        {
            fps_limit_previous = fps_limit;
            SetFpsLimit(static_cast<float>(Display::GetRefreshRate()));
        }
        else
        {
            SetFpsLimit(fps_limit_previous);
        }
    }

    public static double GetTimeMs() { return time_ms; }
    public static double GetTimeSec() { return time_ms / 1000.0; }
    public static double GetDeltaTimeMs() { return delta_time_ms; }
    public static double GetDeltaTimeSec() { return delta_time_ms / 1000.0; }
    public static double GetDeltaTimeSmoothedMs() { return delta_time_smoothed_ms; }
    public static double GetDeltaTimeSmoothedSec() { return delta_time_smoothed_ms / 1000.0; }
}