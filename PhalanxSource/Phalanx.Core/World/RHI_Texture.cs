// The gpu-agnostic face of SpartanEngine source/rhi/RHI_Texture.h @ 4c07baa: what Phalanx.Core reads (sizes, flags)
// and holds (Material textures), with the cpu side pixel data spartan keeps in its RHI_Texture_Mip list.
// Phalanx.Rendering's RHI_Texture derives from it (a gpu resource from the start); a texture made here (importer,
// material packing) is cpu only and the renderer uploads it the first time a material samples it (spartan:
// RHI_Texture::PrepareForGpu). Only 8 bit rgba, single slice, 2D data is held.
namespace Phalanx.World;

// the bits of spartan's RHI_Texture_Flags the cpu side carries (RHI_Texture.h:37-39), same values
[Flags]
public enum RHI_Texture_Cpu_Flags : uint
{
    None        = 0,
    Greyscale   = 1U << 6,
    Transparent = 1U << 7,
    Srgb        = 1U << 8,
}

public class RHI_Texture
{
    private readonly uint m_width, m_height;
    private readonly List<byte[]> m_mips = new();
    private RHI_Texture_Cpu_Flags m_cpu_flags;
    private readonly string m_name = string.Empty;
    private string m_resource_file_path = string.Empty;

    // size only (render target sizes for RenderView, the gpu texture's base)
    public RHI_Texture(uint width, uint height) { m_width = width; m_height = height; }

    // cpu texture: mips[0] is width x height x 4 bytes, every next mip halves (min 1) like spartan's mip chain
    public RHI_Texture(string name, uint width, uint height, IReadOnlyList<byte[]> mips_rgba8, RHI_Texture_Cpu_Flags flags = RHI_Texture_Cpu_Flags.None)
    {
        if (width == 0 || height == 0)
            throw new ArgumentException("width/height can't be zero");
        if (mips_rgba8.Count == 0)
            throw new ArgumentException("a cpu texture needs mip 0", nameof(mips_rgba8));
        for (int i = 0; i < mips_rgba8.Count; i++)
        {
            uint w = System.Math.Max(1u, width >> i), h = System.Math.Max(1u, height >> i);
            if (mips_rgba8[i].Length != (long)w * h * 4)
                throw new ArgumentException($"mip {i} of '{name}' should be {w}x{h}x4 bytes, it is {mips_rgba8[i].Length}");
        }
        m_name      = name;
        m_width     = width;
        m_height    = height;
        m_cpu_flags = flags;
        m_mips.AddRange(mips_rgba8);
    }

    public virtual uint GetWidth() { return m_width; }
    public virtual uint GetHeight() { return m_height; }
    public string GetTextureName() { return m_name; }

    // cpu data (spartan: GetMip(0, mip)->bytes), empty for size only and gpu created textures
    public IReadOnlyList<byte[]> GetCpuMips() { return m_mips; }
    public bool HasCpuData() { return m_mips.Count > 0; }

    // bumped when the cpu pixels change in place (Material packing merges the alpha mask into the colour), the renderer
    // re-uploads a texture whose revision moved
    private ulong m_cpu_revision;
    public ulong GetCpuRevision() { return m_cpu_revision; }
    public void MarkCpuDataChanged() { m_cpu_revision++; }

    public RHI_Texture_Cpu_Flags GetCpuFlags() { return m_cpu_flags; }
    public void SetCpuFlag(RHI_Texture_Cpu_Flags flag, bool enabled = true) { m_cpu_flags = enabled ? m_cpu_flags | flag : m_cpu_flags & ~flag; }
    public virtual bool IsSemiTransparent() { return (m_cpu_flags & RHI_Texture_Cpu_Flags.Transparent) != 0; }
    public virtual bool IsGreyscaleCpu() { return (m_cpu_flags & RHI_Texture_Cpu_Flags.Greyscale) != 0; }

    // ImageImporter.cpp has_transparent_pixels: any alpha below 255
    // the file it was loaded from (spartan: IResource::GetResourceFilePath), empty when made in code
    public string GetResourceFilePath() => m_resource_file_path;
    public void SetResourceFilePath(string path) => m_resource_file_path = path ?? string.Empty;

    /// <summary>Bilinear rgba8 resize, the one material packing uses (Material.cpp:357).</summary>
    public static byte[] ResizeRgba8(byte[] rgba8, uint width, uint height, uint new_width, uint new_height)
        => MaterialTexturePacking.ResizeTexture(rgba8, width, height, new_width, new_height);

    public static bool HasTransparentPixels(ReadOnlySpan<byte> rgba8)
    {
        for (int i = 3; i < rgba8.Length; i += 4)
            if (rgba8[i] != 255)
                return true;
        return false;
    }

    // the full mip chain of an rgba8 image, a 2x2 box filter per level down to 1x1 (spartan downsamples on the gpu
    // with RHI_Texture_PerMipViews / the amd spd pass when it prepares the texture; Phalanx does it on the cpu)
    public static List<byte[]> GenerateMips(byte[] mip0, uint width, uint height)
    {
        var mips = new List<byte[]> { mip0 };
        uint w = width, h = height;
        byte[] src = mip0;
        while (w > 1 || h > 1)
        {
            uint nw = System.Math.Max(1u, w / 2), nh = System.Math.Max(1u, h / 2);
            var dst = new byte[nw * nh * 4];
            for (uint y = 0; y < nh; y++)
            {
                uint y0 = System.Math.Min(y * 2, h - 1), y1 = System.Math.Min(y * 2 + 1, h - 1);
                for (uint x = 0; x < nw; x++)
                {
                    uint x0 = System.Math.Min(x * 2, w - 1), x1 = System.Math.Min(x * 2 + 1, w - 1);
                    for (uint c = 0; c < 4; c++)
                    {
                        uint sum = (uint)src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                        dst[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
                }
            }
            mips.Add(dst);
            src = dst; w = nw; h = nh;
        }
        return mips;
    }
}
