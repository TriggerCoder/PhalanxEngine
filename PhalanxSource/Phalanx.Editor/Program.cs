using System.Globalization;
using Phalanx.Platform;
using Phalanx.Rendering;

namespace Phalanx.Editor;

// The stock editor executable. A user's game exe replaces it (architecture §3, §8).
// TODO(port): roadmap steps C and I (GameHost, ImGui editor shell).
//
//   Phalanx.Editor --rhi-clear [frames]   opens the engine window and clears it every frame through the Vulkan RHI
//                                         (frames: stop after this many presented frames, 0 or omitted = until closed)
//   Phalanx.Editor --rhi-clear-resize [frames]   same, resizing the window every 30 frames (swapchain resize demo)
//   Phalanx.Editor --rhi-offscreen        no window: clears an offscreen image on the first Vulkan device and prints the pixel
//   Phalanx.Editor --rhi-triangle [frames]   draws a triangle every frame (vertex buffer, graphics pipeline)
//   Phalanx.Editor --rhi-cube [frames]       draws a rotating textured cube every frame (depth, index buffer, texture)
//   Phalanx.Editor --rhi-triangle-offscreen / --rhi-cube-offscreen   no window: renders one frame and prints the center pixel
//   Phalanx.Editor --renderer-cube [frames]  spartan's standard cube through the renderer (g-buffer, resolve) with a camera
//   Phalanx.Editor --renderer-cube-offscreen  no window: renders that frame and prints frame_output's center pixel
//   add --texture <image file> to either to texture the cube; --point-light adds a bright point light
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
        int resize_every = 0;
        if (clear < 0 && (clear = Array.IndexOf(args, "--rhi-clear-resize")) >= 0)
            resize_every = 30;
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
            int presented = RenderDevice.RunClearLoop(Window.GetWindow()!, 0.392f, 0.584f, 0.929f, 1.0f, frames, validation, resize_every);
            Console.WriteLine($"rhi-clear: presented {presented} frames");
            return 0;
        }

        int renderer_cube = Array.IndexOf(args, "--renderer-cube");
        bool renderer_cube_offscreen = args.Contains("--renderer-cube-offscreen");
        if (renderer_cube >= 0 || renderer_cube_offscreen)
        {
            if (!RenderDevice.IsVulkanAvailable(out string reason))
            {
                Console.Error.WriteLine($"Vulkan is not available: {reason}");
                return 1;
            }
            int texture_arg = Array.IndexOf(args, "--texture");
            string? texture_path = texture_arg >= 0 && texture_arg + 1 < args.Length ? args[texture_arg + 1] : null;
            if (renderer_cube_offscreen)
            {
                System.Numerics.Vector4 c = RendererCubeDemo.RunOffscreen(128, validation, texture_path, Array.IndexOf(args, "--point-light") >= 0);
                Console.WriteLine(FormattableString.Invariant($"renderer-cube-offscreen: center pixel = {c.X:0.###}, {c.Y:0.###}, {c.Z:0.###}, {c.W:0.###}"));
                return 0;
            }
            int frames = 0;
            if (renderer_cube + 1 < args.Length)
                int.TryParse(args[renderer_cube + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out frames);
            Window.Initialize();
            int presented = RendererCubeDemo.Run(Window.GetWindow()!, frames, validation, texture_path, Array.IndexOf(args, "--point-light") >= 0);
            Console.WriteLine($"renderer-cube: presented {presented} frames");
            return 0;
        }

        int sample = Array.IndexOf(args, "--rhi-triangle");
        bool cube = false;
        if (sample < 0 && (sample = Array.IndexOf(args, "--rhi-cube")) >= 0)
            cube = true;
        if (sample >= 0)
        {
            int frames = 0;
            if (sample + 1 < args.Length)
                int.TryParse(args[sample + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out frames);
            if (!RenderDevice.IsVulkanAvailable(out string reason))
            {
                Console.Error.WriteLine($"Vulkan is not available: {reason}");
                return 1;
            }
            Window.Initialize();
            int presented = cube
                ? RenderDevice.RunTexturedCubeLoop(Window.GetWindow()!, frames, validation)
                : RenderDevice.RunTriangleLoop(Window.GetWindow()!, frames, validation);
            Console.WriteLine($"{(cube ? "rhi-cube" : "rhi-triangle")}: presented {presented} frames");
            return 0;
        }

        bool triangle_offscreen = args.Contains("--rhi-triangle-offscreen");
        if (triangle_offscreen || args.Contains("--rhi-cube-offscreen"))
        {
            if (!RenderDevice.IsVulkanAvailable(out string reason))
            {
                Console.Error.WriteLine($"Vulkan is not available: {reason}");
                return 1;
            }
            const uint size = 64;
            byte[] pixels = triangle_offscreen
                ? RenderDevice.RenderTriangleOffscreen(size, size, validation)
                : RenderDevice.RenderTexturedCubeOffscreen(size, size, 0.0f, validation);
            int center = (int)((size / 2 * size + size / 2) * 4);
            Console.WriteLine($"{(triangle_offscreen ? "rhi-triangle-offscreen" : "rhi-cube-offscreen")}: center pixel = {pixels[center]}, {pixels[center + 1]}, {pixels[center + 2]}, {pixels[center + 3]}");
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

        Console.WriteLine("Phalanx editor: nothing to show yet. Try --renderer-cube, --rhi-clear, --rhi-triangle, --rhi-cube or --rhi-offscreen.");
        return 0;
    }
}
