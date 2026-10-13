using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Silk.NET.Windowing;

namespace Phalanx;
public static class Display
{
    private static List<DisplayMode> display_modes = new List<DisplayMode>();
    public struct DisplayMode : IEquatable<DisplayMode>
    {
        public uint width = 0;
        public uint height = 0;
        public float hz = 0;
        public uint display_id = 0;

        public DisplayMode(uint width, uint height, float hz, uint display_id)
        {
            this.width = width;
            this.height = height;
            this.hz = hz;
            this.display_id = display_id;
        }

        public static bool operator ==(DisplayMode l, DisplayMode r) { return l.Equals(r); }
        public static bool operator !=(DisplayMode l, DisplayMode r) { return !l.Equals(r); }
        public override readonly bool Equals([NotNullWhen(true)] object? obj)
        {
            if (obj is DisplayMode other)
                return Equals(other);
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(DisplayMode other) { return (width == other.width) && (height == other.height) && (hz == other.hz) && (display_id == other.display_id); }
        public override readonly int GetHashCode() { return HashCode.Combine(width, height, hz, display_id); }
    }

    public static void RegisterDisplayMode(uint width, uint height, float hz, uint display_index)
    {
        Helpers.PLXAssertMsg(width != 0, "width can't be zero");
        Helpers.PLXAssertMsg(height != 0, "height can't be zero");
        Helpers.PLXAssertMsg(hz != 0.0f, "hz can't be zero");

        // early exit if the display mode is already registered
        foreach (DisplayMode display_mode in display_modes)
        {
            if (display_mode.width == width &&
                display_mode.height == height &&
                display_mode.hz == hz &&
                display_mode.display_id == display_index)
                return;
        }

        // add the new display mode
        display_modes.Add(new DisplayMode(width, height, hz, display_index));

        // sort display modes based on width, descending order
        display_modes.Sort((a, b) => b.width.CompareTo(a.width));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IWindow? GetWindow() { return Window.GetWindow(); }
    public static void Initialize()
    {
        display_modes.Clear();

        IWindow? window = GetWindow();
        IMonitor monitor = window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window);

        int displayId = monitor.Index;

        var videoModes = monitor.GetAllVideoModes();

        foreach (VideoMode mode in videoModes)
        {
            if ((mode.Resolution != null) && (mode.RefreshRate != null))
            {
                RegisterDisplayMode(
                    (uint)mode.Resolution.Value.X,
                    (uint)mode.Resolution.Value.Y,
                    mode.RefreshRate.Value,
                    (uint)displayId
                );
            }
        }

/*
        SP_LOG_INFO(
            "name: {0}, hz: {1:F1}, gamma: {2:F1}, hdr: {3}, max luminance: {4:F0} nits",
            monitor.Name ?? "Unknown",
            GetRefreshRate(),
            GetGamma(),
            GetHdr() ? "true" : "false",
            GetLuminanceMax()
        );
*/    }

    public static IReadOnlyList<DisplayMode> GetDisplayModes() { return display_modes; }

    public static uint GetWidth()
    {
        IWindow? window = GetWindow();
        var vm = window.Monitor?.VideoMode ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window).VideoMode;
        return (uint)(vm.Resolution != null ? vm.Resolution.Value.X : 0);
    }

    public static uint GetHeight()
    {
        IWindow? window = GetWindow();
        var vm = window.Monitor?.VideoMode ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window).VideoMode;
        return (uint)(vm.Resolution != null ? vm.Resolution.Value.Y : 0);
    }

    public static float GetRefreshRate()
    {
        IWindow? window = GetWindow();
        var vm = window.Monitor?.VideoMode ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window).VideoMode;
        return (float)(vm.RefreshRate != null ? vm.RefreshRate : 0);
    }
}