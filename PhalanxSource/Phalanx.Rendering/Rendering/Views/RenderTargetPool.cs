// Phalanx: spartan has no render target pool, every target is a fixed slot of Renderer's render target array
// (Renderer_Resources.cpp:631). With several views the per-view targets come from here instead.
// - A shareable target (the g-buffer, the light buffers, frame_render: written and read within one view's frame) is
//   handed to every view that asks for the same description, reference counted. Views render one after another, so
//   two views of the same size use one set and memory grows only by what each view keeps across frames.
// - A history target (taau, the depth history, screen space shadows, the view output) is never shared, but goes
//   through the pool too so every view target has one owner and one release path.
// - A released target with no users left is disposed (the rhi's deletion queue keeps it alive for the frames in
//   flight), nothing is cached for later: memory use stays what it was with one view.
using Phalanx.Rendering.RHI;
using Texture = Phalanx.Rendering.RHI.RHI_Texture;

namespace Phalanx.Rendering;

/// <summary>What a pooled render target is: two requests with equal descriptions may share one texture.</summary>
internal readonly record struct RenderTargetDesc(RHI_Texture_Type Type, uint Width, uint Height, uint Depth, uint Mips, RHI_Format Format, RHI_Texture_Flags Flags, string Name);

internal sealed class RenderTargetPool : IDisposable
{
    private sealed class Entry
    {
        public required Texture texture;
        public required RenderTargetDesc desc;
        public required bool shareable;
        public int references;
    }

    private readonly Dictionary<RenderTargetDesc, Entry> m_shared = new();
    private readonly Dictionary<Texture, Entry> m_entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>Live textures (shared ones count once).</summary>
    public int Count => m_entries.Count;

    /// <summary>How many users <paramref name="texture"/> has, 0 when it isn't from this pool.</summary>
    public int GetReferenceCount(Texture texture) => m_entries.TryGetValue(texture, out Entry? entry) ? entry.references : 0;

    /// <summary>A texture for <paramref name="desc"/>: the one other users already share when <paramref name="shareable"/>,
    /// otherwise a new one only the caller uses.</summary>
    public Texture Acquire(in RenderTargetDesc desc, bool shareable)
    {
        if (shareable && m_shared.TryGetValue(desc, out Entry? shared))
        {
            shared.references++;
            return shared.texture;
        }
        var texture = new Texture(desc.Type, desc.Width, desc.Height, desc.Depth, desc.Mips, desc.Format, desc.Flags, desc.Name);
        var entry   = new Entry { texture = texture, desc = desc, shareable = shareable, references = 1 };
        m_entries.Add(texture, entry);
        if (shareable)
            m_shared.Add(desc, entry);
        return texture;
    }

    /// <summary>Drops one use of <paramref name="texture"/>; the last one disposes it.</summary>
    public void Release(Texture? texture)
    {
        if (texture == null || !m_entries.TryGetValue(texture, out Entry? entry))
            return;
        if (--entry.references > 0)
            return;
        m_entries.Remove(texture);
        if (entry.shareable)
            m_shared.Remove(entry.desc);
        texture.Dispose();
    }

    public void Dispose()
    {
        foreach (Entry entry in m_entries.Values)
            entry.texture.Dispose();
        m_entries.Clear();
        m_shared.Clear();
    }
}
