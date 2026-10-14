// The texture part of SpartanEngine source/resource/ResourceCache.h @ 4c07baa (ResourceCache::Load<T>(path): the
// cached resource with the same file path is returned instead of loading it again, otherwise it is loaded and
// cached). Deviations: textures only; keyed by the full path and the requested size and flags (spartan keys by
// path); no serialization of the cache with the world, no unload by reference count.
using Phalanx.World;

namespace Phalanx.Assets.Import;

public static class ResourceCache
{
    private static readonly object s_lock = new();
    private static readonly Dictionary<(string, uint, uint, RHI_Texture_Cpu_Flags), RHI_Texture> s_textures = new();

    /// <summary>The texture loaded from file_path, decoded once (ImageImporter.Load) and shared after that; null when it can't be loaded.</summary>
    public static RHI_Texture? LoadTexture(string file_path, uint width = 0, uint height = 0, RHI_Texture_Cpu_Flags flags = RHI_Texture_Cpu_Flags.None)
    {
        var key = (Path.GetFullPath(file_path), width, height, flags);
        lock (s_lock)
        {
            if (s_textures.TryGetValue(key, out RHI_Texture? cached))
                return cached;
        }
        RHI_Texture? texture = ImageImporter.Load(key.Item1, width, height, flags);
        if (texture == null)
            return null;
        lock (s_lock)
        {
            // another thread may have loaded it meanwhile, keep the first one
            if (s_textures.TryGetValue(key, out RHI_Texture? cached))
                return cached;
            s_textures[key] = texture;
        }
        return texture;
    }

    public static bool IsCached(string file_path)
    {
        string full = Path.GetFullPath(file_path);
        lock (s_lock)
            return s_textures.Keys.Any(k => k.Item1 == full);
    }

    public static int GetTextureCount() { lock (s_lock) return s_textures.Count; }

    public static void Clear() { lock (s_lock) s_textures.Clear(); }
}
