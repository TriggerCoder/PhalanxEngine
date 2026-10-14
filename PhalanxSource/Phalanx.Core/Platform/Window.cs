using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Phalanx.Geometry;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Caching;

namespace Phalanx.Platform;
public static class Window
{
    private static IWindow? window;
    private delegate nint SetThreadDpiAwarenessContextDelegate(nint context);
    private const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;

    private static int width = 1280;
    private static int height = 720;
    private static float dpi_scale = 1.0f;
    private static bool wants_to_close = false;
    public static IWindow? GetWindow() { return window; }
    public static void Initialize()
    {
        // per-monitor dpi awareness is a Windows-only api
        if (OperatingSystem.IsWindows() && NativeLibrary.TryLoad("user32.dll", out var user32))
        {
            if (NativeLibrary.TryGetExport(user32, "SetThreadDpiAwarenessContext", out var export))
            {
                var setThreadDpiAwarenessContext = Marshal.GetDelegateForFunctionPointer<SetThreadDpiAwarenessContextDelegate>(export);
                setThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
            }
            NativeLibrary.Free(user32);
        }

        var options = WindowOptions.Default with
        {
            Title = "Phalanx",   // TODO
            Size = new Vector2D<int>(width, height),
            WindowBorder = WindowBorder.Hidden,
            WindowState = WindowState.Maximized,
            API =  new GraphicsAPI(ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.Default, new APIVersion(1, 4))
//                         : GraphicsAPI.Default
        };
        window = Silk.NET.Windowing.Window.Create(options);

        // spartan: the platform layer fires EventType::WindowResized, the main swapchain subscribes to it
        window.FramebufferResize += size => Event.Fire(Event.EventType.WindowResized, size);
    }

    // framebuffer size in pixels (spartan: Window::GetWidthInPixels/GetHeightInPixels)
    public static uint GetWidthInPixels() => window == null ? 0u : (uint)System.Math.Max(0, window.FramebufferSize.X);
    public static uint GetHeightInPixels() => window == null ? 0u : (uint)System.Math.Max(0, window.FramebufferSize.Y);

    // spartan: Window::IsMinimized. a zero-sized framebuffer counts as minimized (wayland/x11 report it that way)
    public static bool IsMinimized() => window != null && IsMinimized(window);
    public static bool IsMinimized(IWindow w) => w.WindowState == WindowState.Minimized || w.FramebufferSize.X <= 0 || w.FramebufferSize.Y <= 0;

    public static void PumpEvents()
    {
        // Initialize() must run first; a missing window is a startup-order bug, report it instead of a NullReferenceException
        if (window == null)
        {
            Log.LogError("Window.PumpEvents called before Window.Initialize");
            return;
        }
        window.DoEvents();
    }
}