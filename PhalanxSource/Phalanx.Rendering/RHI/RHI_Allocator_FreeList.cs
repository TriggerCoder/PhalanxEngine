// Pure, GPU-free sub-allocator used by RHI_Allocator_Custom for each memory block (phalanx-rhi-plan.md §7, option C).
// First fit over a free list sorted by offset; alignment padding in front of an allocation stays free; freeing
// merges with both neighbours, so a fully freed block is one range again.
namespace Phalanx.Rendering.RHI;

internal sealed class RHI_Allocator_FreeList
{
    public const ulong Invalid = ulong.MaxValue;

    private readonly List<(ulong offset, ulong size)> m_free = new();          // sorted by offset, never adjacent
    private readonly Dictionary<ulong, ulong> m_allocated = new();              // offset -> size

    public ulong Size { get; }
    public ulong UsedBytes { get; private set; }
    public int AllocationCount => m_allocated.Count;
    public int FreeRangeCount => m_free.Count;
    public bool IsEmpty => m_allocated.Count == 0;
    public IReadOnlyList<(ulong offset, ulong size)> FreeRanges => m_free;

    public ulong LargestFreeRange
    {
        get { ulong largest = 0; foreach (var r in m_free) largest = System.Math.Max(largest, r.size); return largest; }
    }

    public RHI_Allocator_FreeList(ulong size)
    {
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        m_free.Add((0, size));
    }

    public static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment <= 1) return value;
        if ((alignment & (alignment - 1)) != 0) throw new ArgumentException("alignment must be a power of two", nameof(alignment));
        return (value + alignment - 1) & ~(alignment - 1);
    }

    // returns the offset, or Invalid when no free range fits
    public ulong Allocate(ulong size, ulong alignment)
    {
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size));
        for (int i = 0; i < m_free.Count; i++)
        {
            (ulong offset, ulong range) = m_free[i];
            ulong aligned = AlignUp(offset, alignment);
            ulong end     = offset + range;
            if (aligned < offset || aligned + size > end || aligned + size < aligned)
                continue;

            m_free.RemoveAt(i);
            int insert = i;
            if (aligned > offset)               m_free.Insert(insert++, (offset, aligned - offset));
            if (aligned + size < end)           m_free.Insert(insert, (aligned + size, end - aligned - size));
            m_allocated.Add(aligned, size);
            UsedBytes += size;
            return aligned;
        }
        return Invalid;
    }

    public void Free(ulong offset)
    {
        if (!m_allocated.Remove(offset, out ulong size))
            throw new InvalidOperationException($"no allocation at offset {offset}");
        UsedBytes -= size;

        // binary search for the insert position
        int lo = 0, hi = m_free.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (m_free[mid].offset < offset) lo = mid + 1; else hi = mid; }

        ulong start = offset, end = offset + size;
        if (lo < m_free.Count && m_free[lo].offset == end)                       // merge with next
        {
            end = m_free[lo].offset + m_free[lo].size;
            m_free.RemoveAt(lo);
        }
        if (lo > 0 && m_free[lo - 1].offset + m_free[lo - 1].size == start)      // merge with previous
        {
            start = m_free[lo - 1].offset;
            m_free.RemoveAt(--lo);
        }
        m_free.Insert(lo, (start, end - start));
    }
}
