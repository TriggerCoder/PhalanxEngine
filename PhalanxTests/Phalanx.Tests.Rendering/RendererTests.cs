using System.Numerics;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using RHI_Texture = Phalanx.Rendering.RHI.RHI_Texture;
using Xunit;

namespace Phalanx.Tests;

// lavapipe, validation on: the renderer's frame loop end to end
[Collection("gpu")]
public class RendererGpuTests
{
    private const uint Width = 128, Height = 96;

    private static void AssertClean() =>
        Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal) || m.Contains("WarningBit", StringComparison.Ordinal)),
                    string.Join("\n", RHI_Device.ValidationMessages));

    private static void RunHeadless(Action body, uint width = Width, uint height = Height)
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        Renderer.Initialize(width, height, validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            AssertClean();
        }
        finally
        {
            Renderer.TestPatternTint = Vector4.One;
            Renderer.TestPatternMaterialIndex = 0;
            Renderer.Shutdown();
        }
    }

    // frame_output is rgba16f
    private static Vector4[] ReadOutput()
    {
        RHI_Texture output = Renderer.GetRenderTargetTexture(Renderer_RenderTarget.frame_output)!;
        byte[] bytes = RHI_Device.ReadbackTexture(output, 0, 0);
        var pixels = new Vector4[output.GetWidth() * output.GetHeight()];
        for (int i = 0; i < pixels.Length; i++)
        {
            float H(int c) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, i * 8 + c * 2));
            pixels[i] = new Vector4(H(0), H(1), H(2), H(3));
        }
        return pixels;
    }

    private static void AssertNear(Vector4 expected, Vector4 actual, string where) =>
        Assert.True(Vector4.Distance(expected, actual) < 2e-3f, $"{where}: expected {expected}, got {actual}");

    private static Vector4 Unorm(byte[] rgba) => new(rgba[0] / 255f, rgba[1] / 255f, rgba[2] / 255f, rgba[3] / 255f);

    [SkippableFact]
    public void Headless_frames_clear_and_draw_the_checkerboard_through_the_bindless_tables()
    {
        RunHeadless(() =>
        {
            for (int i = 0; i < 3; i++)
                Assert.True(Renderer.Tick());
            Assert.Equal(3ul, Renderer.GetFrameNumber());
            RHI_Device.QueueWaitAll();

            Vector4[] p = ReadOutput();
            Vector4 Px(uint x, uint y) => p[y * Width + x];

            // the quad covers the middle half, the rest is the clear colour
            var clear = new Vector4(Renderer.TestPatternClearColor.R, Renderer.TestPatternClearColor.G, Renderer.TestPatternClearColor.B, Renderer.TestPatternClearColor.A);
            AssertNear(clear, Px(0, 0), "corner");
            AssertNear(clear, Px(Width - 1, Height - 1), "corner");
            AssertNear(clear, Px(Width / 4 - 1, Height / 2), "left of the quad");

            // inside: one checkerboard texel per pixel (unset material slots are the checkerboard)
            for (uint y = Height / 4; y < Height * 3 / 4; y += 5)
                for (uint x = Width / 4; x < Width * 3 / 4; x += 5)
                {
                    byte[] expected = ((x / StandardTextures.CheckerboardCell + y / StandardTextures.CheckerboardCell) % 2 == 0)
                        ? StandardTextures.CheckerboardDark : StandardTextures.CheckerboardLight;
                    AssertNear(Unorm(expected), Px(x, y), $"({x}, {y})");
                }
        });
    }

    [SkippableFact]
    public void Pass_constants_and_bindless_material_slots_reach_the_pass()
    {
        RunHeadless(() =>
        {
            // the tint comes through the push constants
            Renderer.TestPatternTint = new Vector4(1, 0, 0, 1);
            Assert.True(Renderer.Tick());
            RHI_Device.QueueWaitAll();
            Vector4 c = ReadOutput()[(Height / 2) * Width + Width / 2];
            Assert.True(c.X > 0.2f && c.Y == 0 && c.Z == 0, c.ToString());

            // a material slot set to the white standard texture
            var textures = new RHI_Texture?[RHI_Definitions.rhi_max_array_size];
            textures[5] = Renderer.GetStandardTexture(Renderer_StandardTexture.White);
            RHI_Device.UpdateBindlessMaterials(textures, null);
            Renderer.TestPatternTint = Vector4.One;
            Renderer.TestPatternMaterialIndex = 5;
            Assert.True(Renderer.Tick());
            RHI_Device.QueueWaitAll();
            AssertNear(Vector4.One, ReadOutput()[(Height / 2) * Width + Width / 2], "white material");
        });
    }

    [SkippableFact]
    public void Frame_constant_buffer_carries_the_scalars_and_rotates_its_ring()
    {
        RunHeadless(() =>
        {
            RHI_Buffer cb = Renderer.GetBuffer(Renderer_Buffer.ConstantFrame)!;
            Assert.Equal((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<FrameBufferData>(), cb.GetStrideUnaligned());
            Assert.Equal(Renderer_Definitions.renderer_draw_data_buffer_count, cb.GetElementCount());

            var offsets = new List<uint>();
            for (int i = 0; i < 5; i++)
            {
                Assert.True(Renderer.Tick());
                offsets.Add(cb.GetOffset() / cb.GetStride());
            }
            // spartan's ring: the slot advances before the upload, the fourth frame rewinds
            Assert.Equal(new uint[] { 0, 1, 2, 0, 1 }, offsets);

            ref readonly FrameBufferData frame = ref Renderer.GetFrameConstantBuffer();
            Assert.Equal(5u, frame.frame);
            Assert.Equal(new Vector2(Width, Height), frame.resolution_render);
            Assert.Equal(new Vector2(Width, Height), frame.resolution_output);
            Assert.Equal(2.2f, frame.gamma);
            Assert.True(frame.time > 0 && frame.delta_time >= 0);
            Assert.Equal(Matrix4x4.Identity, frame.view_projection);
        });
    }

    [SkippableFact]
    public void Resources_match_spartans_tables_and_survive_a_reinitialize()
    {
        RunHeadless(() =>
        {
            for (int i = 0; i < (int)Renderer_Sampler.Max; i++)
                Assert.NotNull(Renderer.GetSampler((Renderer_Sampler)i));
            foreach (var t in new[] { Renderer_StandardTexture.Noise_perlin, Renderer_StandardTexture.Noise_blue, Renderer_StandardTexture.Checkerboard,
                                      Renderer_StandardTexture.Black, Renderer_StandardTexture.White })
                Assert.NotNull(Renderer.GetStandardTexture(t));
            Assert.Equal(1u, Renderer.GetStandardTexture(Renderer_StandardTexture.White)!.GetWidth());
            Assert.Null(Renderer.GetStandardTexture(Renderer_StandardTexture.Gizmo_camera));
            foreach (var b in new[] { Renderer_Buffer.ConstantFrame, Renderer_Buffer.MaterialParameters, Renderer_Buffer.LightParameters, Renderer_Buffer.GeometryInfo })
                Assert.NotNull(Renderer.GetBuffer(b));
            Assert.Equal((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialParameters>(), Renderer.GetBuffer(Renderer_Buffer.MaterialParameters)!.GetStrideUnaligned());

            // the render targets publish their sizes to the world side
            Assert.Equal(Width, RenderView.GetRenderTarget(Renderer_RenderTarget.frame_output).GetWidth());
            Assert.Equal(Height, RenderView.GetRenderTarget(Renderer_RenderTarget.frame_render).GetHeight());

            // a smaller render resolution recreates frame_render, the next frame blits it up into frame_output
            Renderer.SetResolutionRender(64, 64);
            Assert.Equal(64u, Renderer.GetRenderTargetTexture(Renderer_RenderTarget.frame_render)!.GetWidth());
            Assert.Equal(64u, RenderView.GetRenderTarget(Renderer_RenderTarget.frame_render).GetWidth());
            Assert.True(Renderer.Tick());

            // below 64 pixels nothing renders
            Renderer.SetResolutionRender(32, 32);
            Assert.False(Renderer.Tick());
        });

        // spartan creates the non anisotropic samplers once per process, after Shutdown they would stay null
        RunHeadless(() =>
        {
            Assert.NotNull(Renderer.GetSampler(Renderer_Sampler.Point_wrap));
            Assert.True(Renderer.Tick());
        });
    }

    [SkippableFact]
    public void Windowed_frames_present_and_follow_the_window_size()
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        Skip.If(OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")), "no display (run under xvfb-run to include it)");

        var options = Silk.NET.Windowing.WindowOptions.DefaultVulkan with { Size = new Silk.NET.Maths.Vector2D<int>(320, 240), Title = "phalanx renderer test" };
        using Silk.NET.Windowing.IWindow window = Silk.NET.Windowing.Window.Create(options);
        Renderer.Initialize(window, validation: true);
        try
        {
            Assert.Equal((320u, 240u), Renderer.GetResolutionOutput());
            Assert.Equal((1920u, 1080u), Renderer.GetResolutionRender()); // spartan's render resolution
            for (int i = 0; i < 3; i++)
            {
                window.DoEvents();
                Assert.True(Renderer.Tick());
            }

            window.Size = new Silk.NET.Maths.Vector2D<int>(200, 150);
            for (int i = 0; i < 20 && window.FramebufferSize.X != 200; i++) { window.DoEvents(); Thread.Sleep(10); }
            window.DoEvents();
            Assert.True(Renderer.Tick());
            Assert.Equal((200u, 150u), Renderer.GetResolutionOutput());
            Assert.Equal(200u, Renderer.GetRenderTargetTexture(Renderer_RenderTarget.frame_output)!.GetWidth());
            Assert.Equal(200f, Renderer.GetViewport().Width);
            RHI_Device.QueueWaitAll();
            Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal)), string.Join("\n", RHI_Device.ValidationMessages));
        }
        finally
        {
            Renderer.Shutdown();
        }
    }
}
