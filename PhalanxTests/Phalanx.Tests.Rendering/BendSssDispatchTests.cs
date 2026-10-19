using Phalanx.Rendering;
using Xunit;

namespace Phalanx.Tests;

// BendSss.BuildDispatchList (bend_sss_cpu.h) on the cpu: the dispatches it builds, run through the thread layout of
// bend_sss_gpu.glsl (ComputeWavefrontExtents), must write every pixel of the viewport, wherever the light is
public class BendSssDispatchTests
{
    private const int WaveSize = 64;

    // ComputeWavefrontExtents, the write pixel of thread `thread` in group (gx, gy, gz), in float like the gpu
    private static (int x, int y) WritePixel(float[] light, int[] wave_offset, int gx, int gy, int gz, int thread)
    {
        int xy_x = gy * WaveSize + wave_offset[0];
        int xy_y = gz * WaveSize + wave_offset[1];
        float light_x = MathF.Floor(light[0]) + 0.5f, light_y = MathF.Floor(light[1]) + 0.5f;
        float frac_x = light[0] - light_x, frac_y = light[1] - light_y;
        bool reverse = light[3] > 0.0f;
        int sx = System.Math.Sign(xy_x), sy = System.Math.Sign(xy_y);
        bool horizontal = System.Math.Abs(xy_x + sy) < System.Math.Abs(xy_y - sx);
        int axis_x = horizontal ? sy : 0, axis_y = horizontal ? 0 : -sx;
        xy_x += axis_x * gx;
        xy_y += axis_y * gx;
        float fx = xy_x, fy = xy_y;
        bool x_major = MathF.Abs(fx) > MathF.Abs(fy);
        float major = x_major ? fx : fy;
        float major_start = MathF.Abs(major), major_end = MathF.Abs(major) - WaveSize;
        float ma_frac = x_major ? frac_x : frac_y;
        ma_frac = major > 0 ? -ma_frac : ma_frac;
        float start_x = fx + light_x, start_y = fy + light_y;
        float t = (major_end + ma_frac) / (major_start + ma_frac);
        float end_x = light[0] * (1 - t) + start_x * t, end_y = light[1] * (1 - t) + start_y * t; // mix()
        float step = thread ^ (reverse ? 0 : WaveSize - 1);
        float s = step / WaveSize;
        float px = start_x * (1 - s) + end_x * s, py = start_y * (1 - s) + end_y * s;
        return ((int)MathF.Floor(px), (int)MathF.Floor(py));
    }

    private static void AssertCoversViewport(float[] projection, int width, int height)
    {
        BendSss.DispatchList list = BendSss.BuildDispatchList(projection, new[] { width, height }, new[] { 0, 0 }, new[] { width, height });
        Assert.InRange(list.DispatchCount, 1, 8);
        var covered = new bool[width * height];
        for (int d = 0; d < list.DispatchCount; d++)
        {
            BendSss.DispatchData dispatch = list.Dispatch[d];
            Assert.Equal(WaveSize, dispatch.WaveCount[0]);
            Assert.True(dispatch.WaveCount[1] > 0 && dispatch.WaveCount[2] > 0);
            for (int gz = 0; gz < dispatch.WaveCount[2]; gz++)
                for (int gy = 0; gy < dispatch.WaveCount[1]; gy++)
                    for (int gx = 0; gx < dispatch.WaveCount[0]; gx++)
                        for (int thread = 0; thread < WaveSize; thread++)
                        {
                            (int x, int y) = WritePixel(list.LightCoordinate_Shader, dispatch.WaveOffset_Shader, gx, gy, gz, thread);
                            if (x >= 0 && y >= 0 && x < width && y < height)
                                covered[y * width + x] = true;
                        }
        }
        // bend never writes the pixel the light itself sits in (no wave starts at the light, every quadrant begins one
        // wave out), the rest of the viewport is written
        int light_x = (int)MathF.Floor(list.LightCoordinate_Shader[0]), light_y = (int)MathF.Floor(list.LightCoordinate_Shader[1]);
        if (light_x >= 0 && light_y >= 0 && light_x < width && light_y < height)
            covered[light_y * width + light_x] = true;
        int missing = covered.Count(c => !c);
        var miss = Enumerable.Range(0, covered.Length).Where(i => !covered[i]).Take(5).Select(i => $"({i % width},{i / width})");
        Assert.True(missing == 0, $"{missing} of {width * height} pixels are never written: {string.Join(" ", miss)}");
    }

    [Theory]
    [InlineData(0.1f, -0.2f, 0.3f, 1.0f)]    // on screen, in front (w > 0)
    [InlineData(0.9f, 0.85f, 0.3f, 1.0f)]    // on screen near a corner
    [InlineData(-0.3f, 0.4f, 0.3f, -1.0f)]   // behind the camera (w < 0), the rays run the other way
    [InlineData(4.0f, 0.5f, 0.3f, 1.0f)]     // off screen to the right
    [InlineData(-0.2f, -6.0f, 0.3f, 1.0f)]   // off screen below
    [InlineData(-3.0f, 2.5f, 0.3f, 0.5f)]    // off screen top left
    [InlineData(0.3f, -0.7f, 0.0f, 0.0f)]    // at infinity (w = 0, the fp limit kicks in)
    public void Every_pixel_is_written_wherever_the_light_is(float x, float y, float z, float w)
    {
        AssertCoversViewport(new[] { x, y, z, w }, 200, 136);
    }

    // bend_sss_cpu.h:84-88: pixel coordinates, y down, and the direction sign
    [Fact]
    public void The_light_coordinate_is_in_pixels_with_y_down()
    {
        BendSss.DispatchList list = BendSss.BuildDispatchList(new[] { 0.5f, 0.5f, 0.25f, 1.0f }, new[] { 200, 100 }, new[] { 0, 0 }, new[] { 200, 100 });
        Assert.Equal(150.0f, list.LightCoordinate_Shader[0], 3);
        Assert.Equal(25.0f, list.LightCoordinate_Shader[1], 3);
        Assert.Equal(0.25f, list.LightCoordinate_Shader[2], 5);
        Assert.Equal(1.0f, list.LightCoordinate_Shader[3]);

        BendSss.DispatchList behind = BendSss.BuildDispatchList(new[] { 0.5f, 0.5f, 0.25f, -1.0f }, new[] { 200, 100 }, new[] { 0, 0 }, new[] { 200, 100 });
        Assert.Equal(-1.0f, behind.LightCoordinate_Shader[3]);
        // an on screen light needs a dispatch per quadrant at least
        Assert.True(list.DispatchCount >= 4, $"{list.DispatchCount}");
    }
}
