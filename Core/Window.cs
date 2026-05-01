using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Phalanx;
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
        if (NativeLibrary.TryLoad("user32.dll", out var user32))
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
    }

    public static void PumpEvents()
    {
        window.DoEvents();
    }
}