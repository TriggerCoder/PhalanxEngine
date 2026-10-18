using System.Numerics;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// the taau resolve (taau.comp, dlss_reactivity.comp, Renderer_Passes_Post.cs): jitter, history reset, the depth
// history, the resolution maths and the resolve on lavapipe with validation on
public partial class RendererSceneTests
{
    [Fact]
    public void Taau_jitter_is_spartans_16_phase_halton_within_half_a_render_pixel()
    {
        const uint w = 1920, h = 1080;
        var seen = new HashSet<Vector2>();
        Vector2 sum = Vector2.Zero;
        for (uint phase = 0; phase < Renderer.TaauJitterPhaseCount; phase++)
        {
            Vector2 j = Renderer.GetTaauJitter(phase, w, h);
            // ndc offsets: 2 / w is one render pixel, the halton points stay within half a pixel each way
            Assert.InRange(j.X * w / 2.0f, -0.5f, 0.5f);
            Assert.InRange(j.Y * h / 2.0f, -0.5f, 0.5f);
            Assert.True(seen.Add(j));
            sum += j;
        }
        Assert.Equal(16u, Renderer.TaauJitterPhaseCount);
        // halton(1, 2) = 0.5 and halton(1, 3) = 1/3: phase 0 (the 16th frame) sits at (0, +1/6 pixel up)
        Assert.Equal(new Vector2(0.0f, -2.0f * (1.0f / 3.0f - 0.5f) / h), Renderer.GetTaauJitter(0, w, h));
        // halton(2, 2) = 0.25, halton(2, 3) = 2/3: phase 1, the first frame after Initialize
        Assert.Equal(new Vector2(2.0f * -0.25f / w, -2.0f * (2.0f / 3.0f - 0.5f) / h), Renderer.GetTaauJitter(1, w, h));
        // the 16 points are well spread: their mean is within a tenth of a pixel of the pixel centre
        Assert.InRange(sum.X / 16 * w / 2.0f, -0.1f, 0.1f);
        Assert.InRange(sum.Y / 16 * h / 2.0f, -0.1f, 0.1f);
    }

    [Fact]
    public void Taau_dispatches_one_8x8_group_per_8x8_output_pixels()
    {
        Assert.Equal((240u, 135u), Renderer.GetTaauDispatch(1920, 1080));
        Assert.Equal((320u, 180u), Renderer.GetTaauDispatch(2560, 1440));
        Assert.Equal((161u, 91u), Renderer.GetTaauDispatch(1281, 721));
        Assert.Equal((1u, 1u), Renderer.GetTaauDispatch(1, 1));
    }

    // taau.comp's render_center and its shared tile: when the render resolution is at most the output's (the tile
    // path), one group's taps fit the (8 + 4) x (8 + 4) tile for every output size and jitter phase
    [Fact]
    public void Taau_tile_fits_the_shared_memory_whenever_the_render_resolution_is_at_most_the_output()
    {
        static int RenderCenter(float px_out, float res_out, float res_render, float jitter_ndc)
        {
            float jitter_px = jitter_ndc * 0.5f * res_render;
            return (int)MathF.Floor(System.Math.Clamp((px_out + 0.5f) / res_out * res_render + jitter_px, 0.5f, res_render - 0.5f));
        }

        foreach ((uint render, uint output) in new[] { (1920u, 1920u), (1920u, 2560u), (1080u, 1440u), (1080u, 2160u), (64u, 128u), (1000u, 1001u) })
        {
            for (uint phase = 0; phase < Renderer.TaauJitterPhaseCount; phase++)
            {
                float jitter = Renderer.GetTaauJitter(phase, render, render).X;
                for (uint group = 0; group < Renderer.GetTaauDispatch(output, output).x; group++)
                {
                    uint start = group * Renderer.TAAU_GROUP_X;
                    int origin = RenderCenter(start, output, render, jitter) - 2;
                    int end    = RenderCenter(start + Renderer.TAAU_GROUP_X - 1, output, render, jitter) + 2;
                    Assert.InRange(end - origin + 1, 1, (int)Renderer.TAAU_GROUP_X + 4);
                }
            }
        }
    }

    [Fact]
    public void Xess_and_dlss_fall_back_to_off_and_the_default_is_taau()
    {
        Renderer_AntiAliasing_Upsampling before = Renderer.AntiAliasingUpsampling;
        try
        {
            Assert.Equal(Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau, Renderer.AntiAliasingUpsamplingDefault); // ConsoleVariables.cpp:189
            Assert.Equal(Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear, (Renderer_AntiAliasing_Upsampling)0);
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Xess_Upscale_Xess;
            Assert.Equal(Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear, Renderer.AntiAliasingUpsampling);
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Dlss_Upscale_Dlss;
            Assert.Equal(Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear, Renderer.AntiAliasingUpsampling);
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Fxaa_Upscale_Linear;
            Assert.Equal(Renderer_AntiAliasing_Upsampling.AA_Fxaa_Upscale_Linear, Renderer.AntiAliasingUpsampling);
        }
        finally
        {
            Renderer.AntiAliasingUpsampling = before;
        }
    }

    // the history is rgba32f, the weight is its alpha
    private static float HistoryWeight(uint x, uint y) => BitConverter.ToSingle(Read(Renderer_RenderTarget.taau_history), Index(x, y) * 16 + 12);

    [SkippableFact]
    public void Taau_history_resets_on_start_resize_mode_change_and_camera_cut()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            uint cx = Width / 2, cy = Height / 2;

            // a fresh renderer has no history: the first resolve writes weight 0, the next ones accumulate
            Assert.True(Renderer.IsTaauHistoryResetPending());
            Frame(scene);
            Assert.False(Renderer.IsTaauHistoryResetPending());
            Assert.Equal(0.0f, HistoryWeight(cx, cy));
            Frame(scene);
            Frame(scene);
            Assert.True(HistoryWeight(cx, cy) > 0.0f);

            // a camera cut: the previous matrices are this frame's, the history weight starts over
            scene.camera.GetEntity()!.SetPosition(new Vector3(0, 0, -3.5f));
            Renderer.NotifyCameraCut();
            Frame(scene);
            ref readonly FrameBufferData frame = ref Renderer.GetFrameConstantBuffer();
            Assert.Equal(frame.view_projection, frame.view_projection_previous);
            Assert.Equal(frame.view_projection_unjittered, frame.view_projection_previous_unjittered);
            Assert.Equal(frame.camera_position, frame.camera_position_previous);
            Assert.Equal(0.0f, HistoryWeight(cx, cy));
            Frame(scene);
            Assert.True(HistoryWeight(cx, cy) > 0.0f);

            // switching to taau, a new render resolution or a new output resolution
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau;
            Assert.True(Renderer.IsTaauHistoryResetPending());
            Frame(scene);
            Assert.Equal(0.0f, HistoryWeight(cx, cy));
            Renderer.SetResolutionOutput(Width, Height + 8);
            Renderer.SetResolutionOutput(Width, Height);
            Assert.True(Renderer.IsTaauHistoryResetPending());
            Frame(scene);
            Assert.False(Renderer.IsTaauHistoryResetPending());
            Renderer.ResetTaauHistory();
            Assert.True(Renderer.IsTaauHistoryResetPending());
            Frame(scene);
            Assert.Equal(0.0f, HistoryWeight(cx, cy));
        }, Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau);
    }

    [SkippableFact]
    public void The_previous_depth_is_last_frames_depth_and_velocity_w_the_previous_linear_depth()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Frame(scene);
            byte[] depth_first = Read(Renderer_RenderTarget.gbuffer_depth);
            scene.camera.GetEntity()!.SetPosition(new Vector3(0, 0, -4)); // the front face 3.5 m away now
            Frame(scene);
            Assert.Equal(depth_first, Read(Renderer_RenderTarget.gbuffer_depth_previous));
            Assert.NotEqual(depth_first, Read(Renderer_RenderTarget.gbuffer_depth));

            // g_buffer.hlsl:1001: velocity.w is where the surface was last frame, as a view distance (2.5 m)
            byte[] velocity = Read(Renderer_RenderTarget.gbuffer_velocity);
            Assert.Equal(2.5f, Half(velocity, Index(Width / 2, Height / 2) * 8 + 6), 2);
        });
    }

    // the resolved centre of the face, after the history has had time to settle, against the unjittered frame
    private static Vector4 SettledCentre(Renderer_AntiAliasing_Upsampling aa, int frames, Action? before_frames = null)
    {
        Vector4 centre = default;
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(1.0f, 0.5f, 0.25f, 1.0f));
            before_frames?.Invoke();
            for (int i = 0; i < frames; i++)
                Frame(scene);
            centre = Renderer.ReadOutputPixel(Width / 2, Height / 2);

            // nothing in the output is nan or infinite
            byte[] output = Read(Renderer_RenderTarget.frame_output);
            for (int i = 0; i < output.Length; i += 2)
                Assert.True(float.IsFinite(Half(output, i)), $"output half {i / 2}");
        }, aa);
        return centre;
    }

    [SkippableFact]
    public void Taau_converges_to_the_unjittered_frame_on_a_flat_surface()
    {
        Vector4 off  = SettledCentre(Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear, 2);
        Vector4 taau = SettledCentre(Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau, 24);
        Assert.Equal(off.X, taau.X, 2);
        Assert.Equal(off.Y, taau.Y, 2);
        Assert.Equal(off.Z, taau.Z, 2);
        Assert.Equal(1.0f, taau.W);
    }

    [SkippableFact]
    public void Taau_upsamples_a_lower_render_resolution_to_the_output()
    {
        // 64x64 into 128x96 (the renderer renders nothing below 64): the tile path of taau.comp, upsampling 2x by 1.5x
        Vector4 off  = SettledCentre(Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear, 2, () => Renderer.SetResolutionRender(64, 64));
        Vector4 taau = SettledCentre(Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau, 24, () => Renderer.SetResolutionRender(64, 64));
        Assert.Equal(off.X, taau.X, 2);
        Assert.Equal(off.Y, taau.Y, 2);
        Assert.Equal(off.Z, taau.Z, 2);
    }
    // the middle row of the output (display encoded green) after the camera has slid sideways for a while
    private static float[] MiddleRowWhileMoving(Renderer_AntiAliasing_Upsampling aa)
    {
        var row = new float[Width];
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(1.0f, 0.5f, 0.25f, 1.0f));
            // the standard material's world space checkerboard gives the face detail the history has to follow
            scene.cube.GetComponent<Render>()!.SetMaterial(Renderer.GetStandardMaterial()!);
            for (int i = 0; i < 20; i++)
            {
                scene.camera.GetEntity()!.SetPosition(new Vector3(-0.5f + 0.05f * i, 0, -3));
                Frame(scene);
            }
            byte[] output = Read(Renderer_RenderTarget.frame_output);
            for (uint x = 0; x < Width; x++)
                row[x] = Half(output, Index(x, Height / 2) * 8 + 2);
        }, aa);
        return row;
    }

    [SkippableFact]
    public void Taau_follows_a_moving_camera_without_trailing_the_cube()
    {
        // the cube's silhouette moves about a pixel and a half per frame; a history that fails to reproject or reject
        // would leave the old silhouette behind (ghosting), only the anti-aliased edge pixels may differ
        float[] off  = MiddleRowWhileMoving(Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear);
        float[] taau = MiddleRowWhileMoving(Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau);
        int differing = 0;
        for (int x = 0; x < Width; x++)
            if (MathF.Abs(off[x] - taau[x]) > 0.05f)
                differing++;
        Assert.True(differing <= 4, $"{differing} pixels differ: off {string.Join(' ', off.Select(v => v.ToString("0.00")))} taau {string.Join(' ', taau.Select(v => v.ToString("0.00")))}");
    }
}
