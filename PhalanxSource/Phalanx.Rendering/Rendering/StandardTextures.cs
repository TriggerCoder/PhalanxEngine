// The pixels of the standard textures spartan loads from data/textures (Renderer::CreateStandardTextures,
// Renderer_Resources.cpp:1422-1470). The renderer generates its
// own: a grey checkerboard for no_texture.png, tileable perlin noise for noise_perlin.png (256x256, the same size) and
// void-and-cluster blue noise for noise_blue_0.png (64x64 instead of 128x128, the generator is O(n^2)).
// Everything is deterministic and rgba8, row major.
namespace Phalanx.Rendering;

internal static class StandardTextures
{
    public const uint CheckerboardSize = 256;
    public const uint CheckerboardCell = 32;
    // the two greys of spartan's no_texture.png
    public static readonly byte[] CheckerboardDark  = { 64, 64, 64, 255 };
    public static readonly byte[] CheckerboardLight = { 101, 101, 101, 255 };

    public const uint PerlinSize   = 256;
    public const uint PerlinPeriod = 8; // lattice cells across the texture, so it tiles
    public const uint BlueNoiseSize = 64;

    public static byte[] Checkerboard()
    {
        uint n = CheckerboardSize;
        byte[] pixels = new byte[n * n * 4];
        for (uint y = 0; y < n; y++)
            for (uint x = 0; x < n; x++)
                (((x / CheckerboardCell) + (y / CheckerboardCell)) % 2 == 0 ? CheckerboardDark : CheckerboardLight).CopyTo(pixels, (int)((y * n + x) * 4));
        return pixels;
    }

    public static byte[] Solid(byte r, byte g, byte b, byte a) => new[] { r, g, b, a };

    // greyscale improved perlin noise (Perlin 2002) with a lattice that wraps at PerlinPeriod, two octaves
    public static byte[] Perlin(uint seed = 1)
    {
        int[] perm = Permutation(seed);
        uint n = PerlinSize;
        byte[] pixels = new byte[n * n * 4];
        for (uint y = 0; y < n; y++)
        {
            for (uint x = 0; x < n; x++)
            {
                double u = (double)x / n, v = (double)y / n;
                double value = 0.0, amplitude = 1.0, norm = 0.0;
                for (int octave = 0; octave < 2; octave++)
                {
                    int period = (int)PerlinPeriod << octave;
                    value     += amplitude * Noise(perm, u * period, v * period, period);
                    norm      += amplitude;
                    amplitude *= 0.5;
                }
                byte g = (byte)System.Math.Clamp((int)System.Math.Round((value / norm * 0.5 + 0.5) * 255.0), 0, 255);
                int i = (int)((y * n + x) * 4);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = g;
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Grad(int hash, double x, double y) => (hash & 3) switch
    {
        0 => x + y,
        1 => -x + y,
        2 => x - y,
        _ => -x - y
    };

    private static double Noise(int[] perm, double x, double y, int period)
    {
        int xi = (int)System.Math.Floor(x), yi = (int)System.Math.Floor(y);
        double xf = x - xi, yf = y - yi;
        int x0 = xi % period, y0 = yi % period, x1 = (x0 + 1) % period, y1 = (y0 + 1) % period;
        int H(int a, int b) => perm[(perm[a & 255] + b) & 255];
        double fu = Fade(xf), fv = Fade(yf);
        double a0 = Lerp(Grad(H(x0, y0), xf, yf),     Grad(H(x1, y0), xf - 1, yf),     fu);
        double a1 = Lerp(Grad(H(x0, y1), xf, yf - 1), Grad(H(x1, y1), xf - 1, yf - 1), fu);
        return Lerp(a0, a1, fv);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static int[] Permutation(uint seed)
    {
        int[] p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        uint state = seed == 0 ? 1u : seed;
        for (int i = 255; i > 0; i--)
        {
            int j = (int)(NextRandom(ref state) % (uint)(i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        return p;
    }

    // xorshift32, fixed so the textures never change between runtimes
    private static uint NextRandom(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>
    /// Void-and-cluster (Ulichney 1993) ranks of a <paramref name="size"/>² toroidal grid, 0..size²-1, each rank once.
    /// </summary>
    public static int[] BlueNoiseRanks(int size, uint seed = 1, double sigma = 1.5)
    {
        int count = size * size;
        // toroidal gaussian splat, indexed by wrapped offset
        double[] kernel = new double[count];
        for (int dy = 0; dy < size; dy++)
            for (int dx = 0; dx < size; dx++)
            {
                int wx = System.Math.Min(dx, size - dx), wy = System.Math.Min(dy, size - dy);
                kernel[dy * size + dx] = System.Math.Exp(-(wx * wx + wy * wy) / (2.0 * sigma * sigma));
            }

        bool[] pattern = new bool[count];
        double[] energy = new double[count];
        void Splat(bool[] p, double[] e, int index, double sign)
        {
            int px = index % size, py = index / size;
            for (int y = 0; y < size; y++)
            {
                int ky = ((y - py) % size + size) % size;
                for (int x = 0; x < size; x++)
                    e[y * size + x] += sign * kernel[ky * size + ((x - px) % size + size) % size];
            }
        }
        int Find(bool[] p, double[] e, bool value, bool highest)
        {
            int best = -1;
            for (int i = 0; i < count; i++)
                if (p[i] == value && (best < 0 || (highest ? e[i] > e[best] : e[i] < e[best])))
                    best = i;
            return best;
        }

        // initial binary pattern: ~10% random minority pixels, relaxed until the tightest cluster is the largest void
        uint state = seed == 0 ? 1u : seed;
        int ones = System.Math.Max(1, count / 10);
        for (int placed = 0; placed < ones;)
        {
            int i = (int)(NextRandom(ref state) % (uint)count);
            if (pattern[i]) continue;
            pattern[i] = true;
            Splat(pattern, energy, i, +1);
            placed++;
        }
        for (int guard = 0; guard < count * 4; guard++)
        {
            int cluster = Find(pattern, energy, true, true);
            pattern[cluster] = false; Splat(pattern, energy, cluster, -1);
            int void_ = Find(pattern, energy, false, false);
            pattern[void_] = true; Splat(pattern, energy, void_, +1);
            if (void_ == cluster) break;
        }

        int[] ranks = new int[count];
        // phase 1: remove the tightest clusters of a copy, ranks ones-1 .. 0
        bool[] p1 = (bool[])pattern.Clone();
        double[] e1 = (double[])energy.Clone();
        for (int rank = ones - 1; rank >= 0; rank--)
        {
            int cluster = Find(p1, e1, true, true);
            p1[cluster] = false; Splat(p1, e1, cluster, -1);
            ranks[cluster] = rank;
        }
        // phases 2 and 3: fill the largest voids, ranks ones .. count-1
        for (int rank = ones; rank < count; rank++)
        {
            int void_ = Find(pattern, energy, false, false);
            pattern[void_] = true; Splat(pattern, energy, void_, +1);
            ranks[void_] = rank;
        }
        return ranks;
    }

    // rgba8: r is the ranked blue noise, g and b the same pattern shifted by a half and a quarter of the tile
    // (toroidal shifts keep the spectrum), a is opaque like spartan's noise_blue_0.png
    public static byte[] BlueNoise(uint seed = 1)
    {
        int n = (int)BlueNoiseSize, count = n * n;
        int[] ranks = BlueNoiseRanks(n, seed);
        byte Value(int x, int y) => (byte)(ranks[(y % n) * n + (x % n)] * 256L / count);
        byte[] pixels = new byte[count * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                pixels[i]     = Value(x, y);
                pixels[i + 1] = Value(x + n / 2, y + n / 2);
                pixels[i + 2] = Value(x + n / 4, y + n * 3 / 4);
                pixels[i + 3] = 255;
            }
        return pixels;
    }
}
