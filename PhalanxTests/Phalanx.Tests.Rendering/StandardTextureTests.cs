using Phalanx.Rendering;
using Xunit;

namespace Phalanx.Tests;

// no gpu: the generated standard textures
public class StandardTextureTests
{
    [Fact]
    public void Checkerboard_alternates_spartans_greys_in_32_pixel_cells()
    {
        byte[] p = StandardTextures.Checkerboard();
        uint n = StandardTextures.CheckerboardSize;
        Assert.Equal((int)(n * n * 4), p.Length);
        byte[] At(uint x, uint y) => p.AsSpan((int)((y * n + x) * 4), 4).ToArray();
        Assert.Equal(StandardTextures.CheckerboardDark, At(0, 0));
        Assert.Equal(StandardTextures.CheckerboardDark, At(31, 31));
        Assert.Equal(StandardTextures.CheckerboardLight, At(32, 0));
        Assert.Equal(StandardTextures.CheckerboardLight, At(0, 32));
        Assert.Equal(StandardTextures.CheckerboardDark, At(32, 32));
        Assert.Equal(StandardTextures.CheckerboardLight, At(n - 1, 0)); // 8 cells: tiles seamlessly
    }

    [Fact]
    public void Perlin_noise_is_deterministic_grey_and_tiles()
    {
        byte[] a = StandardTextures.Perlin(), b = StandardTextures.Perlin();
        Assert.Equal(a, b);
        Assert.NotEqual(a, StandardTextures.Perlin(seed: 7));
        int n = (int)StandardTextures.PerlinSize;
        int Grey(int x, int y) { int i = (y * n + x) * 4; Assert.Equal(a[i], a[i + 1]); Assert.Equal(a[i], a[i + 2]); Assert.Equal(255, a[i + 3]); return a[i]; }

        // neighbours across the wrap differ about as much as neighbours inside: the lattice wraps
        double inside = 0, wrap = 0;
        for (int y = 0; y < n; y++)
        {
            inside += System.Math.Abs(Grey(1, y) - Grey(0, y));
            wrap   += System.Math.Abs(Grey(0, y) - Grey(n - 1, y));
        }
        Assert.True(wrap < inside * 3 + n, $"wrap {wrap}, inside {inside}");

        // it is noise, not flat
        int min = 255, max = 0;
        for (int y = 0; y < n; y += 3) for (int x = 0; x < n; x += 3) { min = System.Math.Min(min, Grey(x, y)); max = System.Math.Max(max, Grey(x, y)); }
        Assert.True(max - min > 100, $"range {min}..{max}");
    }

    [Fact]
    public void Blue_noise_ranks_every_pixel_once_and_spreads_the_first_ones()
    {
        const int n = 32;
        int[] ranks = StandardTextures.BlueNoiseRanks(n);
        Assert.Equal(Enumerable.Range(0, n * n), ranks.OrderBy(r => r));
        Assert.Equal(ranks, StandardTextures.BlueNoiseRanks(n));

        // the darkest 10% are well separated (white noise would put some next to each other)
        var points = Enumerable.Range(0, n * n).Where(i => ranks[i] < n * n / 10).Select(i => (x: i % n, y: i / n)).ToArray();
        int min_d2 = int.MaxValue;
        foreach (var p in points)
            foreach (var q in points)
            {
                if (p == q) continue;
                int dx = System.Math.Abs(p.x - q.x), dy = System.Math.Abs(p.y - q.y);
                dx = System.Math.Min(dx, n - dx); dy = System.Math.Min(dy, n - dy);
                min_d2 = System.Math.Min(min_d2, dx * dx + dy * dy);
            }
        Assert.True(min_d2 >= 4, $"closest pair at squared distance {min_d2}");

        byte[] rgba = StandardTextures.BlueNoise();
        Assert.Equal((int)(StandardTextures.BlueNoiseSize * StandardTextures.BlueNoiseSize * 4), rgba.Length);
        Assert.All(Enumerable.Range(0, rgba.Length / 4), i => Assert.Equal(255, rgba[i * 4 + 3]));
    }
}
