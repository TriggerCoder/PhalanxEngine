// Port of SpartanEngine source/resource/import/ImageImporter.cpp @ 4c07baa, ImageImporter::Load (:542-721), and the
// texture half of ResourceCache::Load (RHI_Texture::LoadFromFile, RHI_Texture.cpp:760): an image file becomes a cpu
// rgba8 texture with flags, cached through the generated cache like spartan's "image_pixels" category.
// Deviations:
// - Decoding is StbImageSharp (public domain or MIT, png/jpg/bmp/tga/psd/gif/hdr) instead of FreeImage and
//   tinyddsloader: no dds/bc, no exr/tiff, 16 bit and float images come in as 8 bit rgba (spartan keeps their depth),
//   stb already decodes top-down so there is no FreeImage_FlipVertical or red/blue swap.
// - Greyscale is a one channel source (FreeImage's FIC_MINISBLACK). Srgb comes only from an icc profile in spartan
//   (get_is_srgb, :34) and stb doesn't expose icc profiles, so the Srgb flag is never set here; the renderer's
//   should_decode_as_srgb doesn't rely on it.
// - A requested size rescales when the width or the height differs (spartan :693 tests && so an image differing in
//   one dimension only is not rescaled, reported as a bug) and the filter is the bilinear one material packing uses
//   (spartan: FreeImage lanczos3).
// - Mips are generated here (2x2 box down to 1x1); spartan generates them when the texture is prepared for the gpu.
// - Only the base pixels go to the cache, like spartan; the mips are rebuilt on a hit.
using Phalanx;
using Phalanx.Caching;
using Phalanx.World;
using StbImageSharp;

namespace Phalanx.Assets.Import;

public static class ImageImporter
{
    public const string CacheCategory = "image_pixels";
    private const RHI_Texture_Cpu_Flags ImageFlags = RHI_Texture_Cpu_Flags.Greyscale | RHI_Texture_Cpu_Flags.Srgb | RHI_Texture_Cpu_Flags.Transparent;
    private const long MaxCachedSourceBytes = 64L * 1024 * 1024;

    /// <summary>Decodes an image file into an rgba8 cpu texture with a full mip chain, or null (logged) when it can't.
    /// width/height of 0 keep the image's own size, otherwise the image is rescaled to them.</summary>
    public static RHI_Texture? Load(string file_path, uint width = 0, uint height = 0, RHI_Texture_Cpu_Flags flags = RHI_Texture_Cpu_Flags.None)
    {
        if (!File.Exists(file_path))
        {
            Log.LogError($"Path \"{file_path}\" is invalid.");
            return null;
        }

        byte[] encoded;
        try { encoded = File.ReadAllBytes(file_path); }
        catch (Exception e)
        {
            Log.LogError($"Failed to read \"{file_path}\": {e.Message}");
            return null;
        }
        string name = Path.GetFileNameWithoutExtension(file_path);

        // reuse exactly the decoded pixels, the key hashes the source bytes rather than timestamps (:553-591)
        string? cache_path = null;
        ulong key = 0;
        if (encoded.Length > 0 && encoded.Length <= MaxCachedSourceBytes)
        {
            var hash = new GeneratedCache.Hash();
            hash.Add(1u); // corrections/resize algorithm
            hash.Add(width); hash.Add(height);
            hash.Add((uint)(flags & ImageFlags));
            hash.Add(Path.GetExtension(file_path));
            hash.Add(GeneratedCache.HashBytes(encoded));
            key        = hash.value;
            cache_path = GeneratedCache.Path(CacheCategory, key);
            var metadata = new List<uint>();
            var pixels   = new List<byte>();
            if (GeneratedCache.Load(cache_path, key, metadata, pixels) && metadata.Count == 3 &&
                metadata[0] > 0 && metadata[0] <= 65536 && metadata[1] > 0 && metadata[1] <= 65536 &&
                (metadata[2] & ~(uint)ImageFlags) == 0 && (ulong)metadata[0] * metadata[1] * 4 == (ulong)pixels.Count)
            {
                return Create(name, file_path, metadata[0], metadata[1], pixels.ToArray(), (flags & ~ImageFlags) | (RHI_Texture_Cpu_Flags)metadata[2]);
            }
        }

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e)
        {
            Log.LogError($"Failed to load \"{file_path}\": {e.Message}");
            return null;
        }

        uint image_width = (uint)image.Width, image_height = (uint)image.Height;
        byte[] rgba = image.Data;

        // greyscale is deduced before the corrections (:679)
        if (image.SourceComp == ColorComponents.Grey)
            flags |= RHI_Texture_Cpu_Flags.Greyscale;

        // scale if needed (:691-695, || where spartan has &&)
        if (width != 0 && height != 0 && (image_width != width || image_height != height))
        {
            rgba = RHI_Texture.ResizeRgba8(rgba, image_width, image_height, width, height);
            image_width = width; image_height = height;
        }

        flags &= ~RHI_Texture_Cpu_Flags.Transparent;
        if (RHI_Texture.HasTransparentPixels(rgba))
            flags |= RHI_Texture_Cpu_Flags.Transparent;

        if (cache_path != null)
            GeneratedCache.Save<uint, byte>(cache_path, key, new[] { image_width, image_height, (uint)(flags & ImageFlags) }, rgba);

        return Create(name, file_path, image_width, image_height, rgba, flags);
    }

    private static RHI_Texture Create(string name, string file_path, uint width, uint height, byte[] rgba, RHI_Texture_Cpu_Flags flags)
    {
        var texture = new RHI_Texture(name, width, height, RHI_Texture.GenerateMips(rgba, width, height), flags);
        texture.SetResourceFilePath(file_path);
        return texture;
    }
}
