using System.Globalization;
using Phalanx.Platform;
using Phalanx.Rendering;

namespace Phalanx.Editor;

// The stock editor executable. A user's game exe replaces it (architecture §3, §8).
// TODO(port): roadmap steps C and I (GameHost, ImGui editor shell).
//
//   Phalanx.Editor --rhi-clear [frames]   opens the engine window and clears it every frame through the Vulkan RHI
//                                         (frames: stop after this many presented frames, 0 or omitted = until closed)
//   Phalanx.Editor --rhi-offscreen        no window: clears an offscreen image on the first Vulkan device and prints the pixel
//   add --validation to enable VK_LAYER_KHRONOS_validation (Debug builds enable it by default)
public static class Program
{
    public static int Main(string[] args)
    {
        Engine.Initialize();

#if DEBUG
        bool validation = !args.Contains("--no-validation");
#else
        bool validation = args.Contains("--validation");
#endif

        int clear = Array.IndexOf(args, "--rhi-clear");
        if (clear >= 0)
        {
            int frames = 0;
            if (clear + 1 < args.Length)
                int.TryParse(args[clear + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out frames);
            if (!RenderDevice.IsVulkanAvailable(out string reason))
            {
                Console.Error.WriteLine($"Vulkan is not available: {reason}");
                return 1;
            }
            Window.Initialize();
            // cornflower blue
            int presented = RenderDevice.RunClearLoop(Window.GetWindow()!, 0.392f, 0.584f, 0.929f, 1.0f, frames, validation);
            Console.WriteLine($"rhi-clear: presented {presented} frames");
            return 0;
        }

        if (args.Contains("--rhi-offscreen"))
        {
            if (!RenderDevice.IsVulkanAvailable(out string reason))
            {
                Console.Error.WriteLine($"Vulkan is not available: {reason}");
                return 1;
            }
            byte[] pixel = RenderDevice.ClearOffscreenAndReadPixel(1.0f, 0.5f, 0.25f, 1.0f, validation);
            Console.WriteLine($"rhi-offscreen: pixel = {pixel[0]}, {pixel[1]}, {pixel[2]}, {pixel[3]}");
            return 0;
        }

        Console.WriteLine("Phalanx editor: nothing to show yet. Try --rhi-clear or --rhi-offscreen.");
        return 0;
    }
}
