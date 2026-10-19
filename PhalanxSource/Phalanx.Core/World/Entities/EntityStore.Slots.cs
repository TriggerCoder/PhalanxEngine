// The instance slots of renderable store entities: a dense index per entity into Instances, the packed transforms the
// transform pass writes and the renderer copies into its gpu instance buffer (Renderer.Instancing.cs). A slot keeps
// its entity for life (rows move between chunks, slots don't); a freed slot waits a few rendered frames before reuse,
// so a frame still in flight never draws the next owner from the previous frame's copy.
namespace Phalanx.World;

public sealed partial class EntityStore
{
    public const int SlotBlockSize = 16384; // the instance buffers grow by these
    internal const int SlotGroupShift = 8;
    internal const int SlotGroupSize  = 1 << SlotGroupShift; // the granularity of the dirty stamps and the uploads (8 KB)
    private const int SlotQuarantineFrames = 4; // renderer_draw_data_buffer_count

    internal PackedTransform[] Instances = Array.Empty<PackedTransform>();
    internal uint SlotCount;          // the high water mark: slots below it may be live
    internal uint[] SlotGroupStamp = Array.Empty<uint>(); // per group of SlotGroupSize slots, the sync that last wrote one
    private readonly Stack<uint> m_free_slots = new();
    private readonly Queue<(uint slot, long frame)> m_quarantine = new();
    private readonly List<uint> m_fresh_slots = new(); // allocated since the renderer last took them
    private long m_render_frame;

    /// <summary>Set by the renderer while it draws store entities: freed slots then wait for its frames, and new slots
    /// are reported (TakeFreshSlots). Without one a freed slot is reused at once.</summary>
    internal bool HasRenderConsumer;

    private uint AllocateSlot()
    {
        uint slot;
        if (m_free_slots.Count > 0)
            slot = m_free_slots.Pop();
        else
        {
            slot = SlotCount++;
            if (slot >= Instances.Length)
            {
                int capacity = System.Math.Max(SlotBlockSize, Instances.Length * 2);
                Array.Resize(ref Instances, capacity);
                Array.Resize(ref SlotGroupStamp, (capacity + SlotGroupSize - 1) / SlotGroupSize);
            }
        }
        if (HasRenderConsumer)
            m_fresh_slots.Add(slot);
        return slot;
    }

    private void FreeSlot(uint slot)
    {
        if (slot == uint.MaxValue)
            return;
        if (HasRenderConsumer)
            m_quarantine.Enqueue((slot, m_render_frame));
        else
            m_free_slots.Push(slot);
    }

    /// <summary>The renderer finished a frame: slots freed long enough ago can be reused.</summary>
    internal void OnRenderFrame()
    {
        m_render_frame++;
        while (m_quarantine.Count > 0 && m_render_frame - m_quarantine.Peek().frame > SlotQuarantineFrames)
            m_free_slots.Push(m_quarantine.Dequeue().slot);
    }

    /// <summary>The slots allocated since the last call (their previous frame transform is their current one).</summary>
    internal void TakeFreshSlots(List<uint> into)
    {
        into.AddRange(m_fresh_slots);
        m_fresh_slots.Clear();
    }

    internal int SlotGroupCount => (int)((SlotCount + SlotGroupSize - 1) / SlotGroupSize);

    private void ClearSlots()
    {
        // the gpu may still draw from the old slots; with a renderer they all wait like freed ones
        m_free_slots.Clear();
        m_quarantine.Clear();
        m_fresh_slots.Clear();
        if (HasRenderConsumer)
        {
            for (uint slot = 0; slot < SlotCount; slot++)
                m_quarantine.Enqueue((slot, m_render_frame));
        }
        else
            SlotCount = 0;
    }

    /// <summary>The renderer stopped (Shutdown): no frames are in flight any more.</summary>
    internal void DetachRenderConsumer()
    {
        HasRenderConsumer = false;
        while (m_quarantine.Count > 0)
            m_free_slots.Push(m_quarantine.Dequeue().slot);
        m_fresh_slots.Clear();
    }
}
