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
//   Phalanx.Editor --renderer-cube-offscreen  no window: renders that frame and prints frame_output's center pixel;
//     with --png <file> [--size w h] [--time t] it writes the whole frame (default 1920x1080) as a png instead
//   add --texture <image file> to either to texture the cube; --point-light adds a bright point light; --time-of-day
//     <0..1> moves the sun (0.25 sunrise, 0.5 noon, 0.75 sunset)
//   add --validation to enable VK_LAYER_KHRONOS_validation (Debug builds enable it by default)
//   add --aa <taau|off|fxaa|xess|dlss> to pick Renderer.AntiAliasingUpsampling (default taau; fxaa isn't ported and
//     renders like off, xess and dlss aren't integrated and fall back to off)
//   add --sss <on|off> to switch Renderer.ScreenSpaceShadows, the sun's contact shadows (default on)
//   add --layers <hide-cube|only-cube> to put the cube on render layer 2 and leave layer 2 out of the camera's cull mask
//     (hide-cube) or keep only layer 2 (only-cube); --predraw <material|blink|invalid> adds a cube behaviour that draws it blue
//     through OnPreDraw (material), hides it every other second (blink) or overrides it with a destroyed material
//     (invalid, the illegal material), logging its lifecycle callbacks; --material <none|destroyed|invalid|
//     missing-texture|full> shows the default and illegal materials (RendererCubeDemo.cs)
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

        int aa_arg = Array.IndexOf(args, "--aa");
        if (aa_arg >= 0 && aa_arg + 1 < args.Length)
        {
            Renderer_AntiAliasing_Upsampling? aa = args[aa_arg + 1].ToLowerInvariant() switch
            {
                "off"  => Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear,
                "fxaa" => Renderer_AntiAliasing_Upsampling.AA_Fxaa_Upscale_Linear,
                "taau" => Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau,
                "xess" => Renderer_AntiAliasing_Upsampling.AA_Xess_Upscale_Xess,
                "dlss" => Renderer_AntiAliasing_Upsampling.AA_Dlss_Upscale_Dlss,
                _      => null
            };
            if (aa is { } mode)
                Renderer.AntiAliasingUpsampling = mode;
            else
                Console.Error.WriteLine($"--aa: unknown mode '{args[aa_arg + 1]}', expected taau, off, fxaa, xess or dlss");
        }

        int sss_arg = Array.IndexOf(args, "--sss");
        if (sss_arg >= 0 && sss_arg + 1 < args.Length)
        {
            bool? sss = args[sss_arg + 1].ToLowerInvariant() switch
            {
                "on"  => true,
                "off" => false,
                _     => null
            };
            if (sss is { } enabled)
                Renderer.ScreenSpaceShadows = enabled;
            else
                Console.Error.WriteLine($"--sss: unknown value '{args[sss_arg + 1]}', expected on or off");
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
            int tod_arg = Array.IndexOf(args, "--time-of-day");
            float? time_of_day = null;
            if (tod_arg >= 0)
            {
                if (tod_arg + 1 >= args.Length || !float.TryParse(args[tod_arg + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float tod) || tod < 0.0f || tod > 1.0f)
                {
                    Console.Error.WriteLine("--time-of-day: expected a value from 0 to 1 (0.25 sunrise, 0.5 noon, 0.75 sunset)");
                    return 1;
                }
                time_of_day = tod;
            }
            var layers = RendererCubeDemo.Layers.Default;
            int layers_arg = Array.IndexOf(args, "--layers");
            if (layers_arg >= 0)
            {
                string? value = layers_arg + 1 < args.Length ? args[layers_arg + 1].ToLowerInvariant() : null;
                if (value == "hide-cube") layers = RendererCubeDemo.Layers.HideCube;
                else if (value == "only-cube") layers = RendererCubeDemo.Layers.OnlyCube;
                else
                {
                    Console.Error.WriteLine("--layers: expected hide-cube or only-cube");
                    return 1;
                }
            }
            var pre_draw = RendererCubeDemo.PreDraw.None;
            int pre_draw_arg = Array.IndexOf(args, "--predraw");
            if (pre_draw_arg >= 0)
            {
                string? value = pre_draw_arg + 1 < args.Length ? args[pre_draw_arg + 1].ToLowerInvariant() : null;
                if (value == "material") pre_draw = RendererCubeDemo.PreDraw.Material;
                else if (value == "blink") pre_draw = RendererCubeDemo.PreDraw.Blink;
                else if (value == "invalid") pre_draw = RendererCubeDemo.PreDraw.Invalid;
                else
                {
                    Console.Error.WriteLine("--predraw: expected material, blink or invalid");
                    return 1;
                }
            }
            var material_case = RendererCubeDemo.MaterialCase.Own;
            int material_arg = Array.IndexOf(args, "--material");
            if (material_arg >= 0)
            {
                string? value = material_arg + 1 < args.Length ? args[material_arg + 1].ToLowerInvariant() : null;
                if (value == "none") material_case = RendererCubeDemo.MaterialCase.None;
                else if (value == "destroyed") material_case = RendererCubeDemo.MaterialCase.Destroyed;
                else if (value == "invalid") material_case = RendererCubeDemo.MaterialCase.Invalid;
                else if (value == "missing-texture") material_case = RendererCubeDemo.MaterialCase.MissingTexture;
                else if (value == "full") material_case = RendererCubeDemo.MaterialCase.Full;
                else
                {
                    Console.Error.WriteLine("--material: expected none, destroyed, invalid, missing-texture or full");
                    return 1;
                }
            }
            var options = new RendererCubeDemo.ViewOptions(layers, pre_draw, material_case);
            int png_arg = Array.IndexOf(args, "--png");
            if (renderer_cube_offscreen && png_arg >= 0 && png_arg + 1 < args.Length)
            {
                // --renderer-cube-offscreen --png <file> [--size w h] [--time t]: the whole frame_output as a png
                uint w = 1920, h = 1080; float t = 0.0f;
                int size_arg = Array.IndexOf(args, "--size");
                if (size_arg >= 0 && size_arg + 2 < args.Length) { w = uint.Parse(args[size_arg + 1], CultureInfo.InvariantCulture); h = uint.Parse(args[size_arg + 2], CultureInfo.InvariantCulture); }
                int time_arg = Array.IndexOf(args, "--time");
                if (time_arg >= 0 && time_arg + 1 < args.Length) t = float.Parse(args[time_arg + 1], CultureInfo.InvariantCulture);
                RendererCubeDemo.RunOffscreenPng(args[png_arg + 1], w, h, t, validation, texture_path, Array.IndexOf(args, "--point-light") >= 0, time_of_day, options);
                Console.WriteLine($"renderer-cube-offscreen: wrote {args[png_arg + 1]} ({w}x{h})");
                return 0;
            }
            if (renderer_cube_offscreen)
            {
                System.Numerics.Vector4 c = RendererCubeDemo.RunOffscreen(128, validation, texture_path, Array.IndexOf(args, "--point-light") >= 0, time_of_day, options);
                Console.WriteLine(FormattableString.Invariant($"renderer-cube-offscreen: center pixel = {c.X:0.###}, {c.Y:0.###}, {c.Z:0.###}, {c.W:0.###}"));
                return 0;
            }
            int frames = 0;
            if (renderer_cube + 1 < args.Length)
                int.TryParse(args[renderer_cube + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out frames);
            Window.Initialize();
            int presented = RendererCubeDemo.Run(Window.GetWindow()!, frames, validation, texture_path, Array.IndexOf(args, "--point-light") >= 0, time_of_day, options);
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
