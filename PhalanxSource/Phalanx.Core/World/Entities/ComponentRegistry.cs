// The component data types of the entity store: a process wide id per IComponentData struct (one bit of an
// archetype's mask) and the typed column operations a chunk needs without knowing the type.
using System.Runtime.InteropServices;

namespace Phalanx.World;

internal abstract class ComponentInfo
{
    public int Id;
    public int Size;
    public Type Type = null!;
    public abstract Array Create(int capacity);
    public abstract Array Resize(Array column, int count, int capacity);
    public abstract void Copy(Array source, int source_row, Array destination, int destination_row);
    public abstract void Clear(Array column, int row);
    public abstract void Write(Array column, int row, ReadOnlySpan<byte> bytes);
}

internal sealed class ComponentInfo<T> : ComponentInfo where T : unmanaged
{
    public override Array Create(int capacity) => new T[capacity];
    public override Array Resize(Array column, int count, int capacity)
    {
        var resized = new T[capacity];
        Array.Copy(column, resized, count);
        return resized;
    }
    public override void Copy(Array source, int source_row, Array destination, int destination_row) => ((T[])destination)[destination_row] = ((T[])source)[source_row];
    public override void Clear(Array column, int row) => ((T[])column)[row] = default;
    public override void Write(Array column, int row, ReadOnlySpan<byte> bytes) => ((T[])column)[row] = MemoryMarshal.Read<T>(bytes);
}

internal static class ComponentRegistry
{
    public const int MaxTypes = 64;
    private static readonly ComponentInfo?[] s_infos = new ComponentInfo?[MaxTypes];
    private static int s_count;
    private static readonly object s_lock = new();

    public static int Count => Volatile.Read(ref s_count);
    public static ComponentInfo Get(int id) => s_infos[id]!;

    public static int Register<T>() where T : unmanaged
    {
        lock (s_lock)
        {
            for (int i = 0; i < s_count; i++)
                if (s_infos[i]!.Type == typeof(T))
                    return i;
            if (s_count == MaxTypes)
                throw new InvalidOperationException($"More than {MaxTypes} IComponentData types, {typeof(T).Name} can't be registered");
            int id = s_count;
            s_infos[id] = new ComponentInfo<T> { Id = id, Size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), Type = typeof(T) };
            Volatile.Write(ref s_count, id + 1);
            return id;
        }
    }
}

// the id of T, assigned on first use
internal static class ComponentId<T> where T : unmanaged, IComponentData
{
    public static readonly int Value = ComponentRegistry.Register<T>();
    public static readonly ulong Bit = 1ul << Value;
}
