using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace Phalanx;

// port of source/geometry/GeneratedCache.h/.cpp (Spartan 4c07baa): a disposable on-disk cache for derived geometry
// (simplified lods, impostor atlases) keyed by a hash of the generator's inputs
//
// file: <Root>/generated_cache/<category>/<key>.bin (key in decimal, like std::to_string)
// header (40 bytes, little endian):
//   u64 key | u64 checksum (XXH3-64 of the raw payload) | u64 magic "PHCACHE1" | u64 raw_size | u32 version (2) | u32 codec
// payload: for every stored vector, u64 element count followed by the raw element bytes
// codec 0 is stored, codec 1 is lz4 block in Spartan (see Deviations in Read/Write)
// version 1 / headerless legacy files carry an FNV-1a checksum instead and are still accepted
//
// a cache failure is always a rebuild, never an error: every Load/Save swallows io problems
public static class GeneratedCache
{
    public const ulong Magic   = 0x3145484341434850ul; // "PHCACHE1"
    public const uint  Version = 2;
    // Phalanx addition, hashed first into every geometry key: 1 = the old unpacked 44-byte vertex stub,
    // 2 = Spartan's packed 24-byte RHI_Vertex_PosTexNorTan. bump when the vertex bytes change so stale
    // files simply miss (and age out through Maintain)
    public const uint  VertexFormatVersion = 2;
    public const int   HeaderSize = 40;
    public const ulong MaximumPayloadSize = 512ul * 1024 * 1024 - 16;

    // Spartan roots the cache in the world's resource directory and disables it when there is none; here the root is
    // configurable and defaults to a per-user writable folder. null or empty disables the cache
    public static string? Root { get; set; } = DefaultRoot();

    public static string DefaultRoot() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "PhalanxEngine");

    // statistics -------------------------------------------------------------------------------------------------
    public sealed class Statistics { public ulong hits, missing, invalid; public double milliseconds; }
    private static readonly object s_statistics_mutex = new object();
    private static readonly Dictionary<string, Statistics> s_statistics = new Dictionary<string, Statistics>();
    private static DateTime s_session_started = DateTime.UtcNow;

    public static void ResetStatistics()
    {
        lock (s_statistics_mutex)
        {
            s_statistics.Clear();
            s_session_started = DateTime.UtcNow;
        }
    }

    public static Statistics GetStatistics(string category)
    {
        lock (s_statistics_mutex)
        {
            Statistics s = s_statistics.TryGetValue(category, out Statistics? found) ? found : new Statistics();
            return new Statistics { hits = s.hits, missing = s.missing, invalid = s.invalid, milliseconds = s.milliseconds };
        }
    }

    public static List<string> GetStatisticsLines()
    {
        lock (s_statistics_mutex)
            return s_statistics.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}: {kv.Value.hits} hits, {kv.Value.missing} missing/changed keys, {kv.Value.invalid} invalid files, {kv.Value.milliseconds} ms accumulated cache reads")
                .ToList();
    }

    private static void Record(string category, bool hit, bool missing, double ms)
    {
        lock (s_statistics_mutex)
        {
            if (!s_statistics.TryGetValue(category, out Statistics? s))
                s_statistics[category] = s = new Statistics();
            if (hit) s.hits++;
            else if (missing) s.missing++;
            else s.invalid++;
            s.milliseconds += ms;
        }
    }

    // key hashing ------------------------------------------------------------------------------------------------
    // incremental FNV-1a 64 over the raw bytes of the inputs; version the caller's key when changing a generator
    // or its binary layout
    public struct Hash
    {
        public ulong value;
        public Hash() { value = 14695981039346656037ul; }

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            ulong v = value;
            foreach (byte b in bytes)
                v = (v ^ b) * 1099511628211ul;
            value = v;
        }
        public void Add<T>(T v) where T : unmanaged { Bytes(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in v))); }
        public void Add(string v)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(v);
            Add((ulong)utf8.Length);
            Bytes(utf8);
        }
        public void Add<T>(ReadOnlySpan<T> v) where T : unmanaged
        {
            Add((ulong)v.Length);
            Bytes(MemoryMarshal.AsBytes(v));
        }
        public void Add<T>(List<T> v) where T : unmanaged { Add<T>((ReadOnlySpan<T>)CollectionsMarshal.AsSpan(v)); }
    }

    // xxh3-64, Spartan's HashBytes (System.IO.Hashing implements the same XXH3_64bits with seed 0)
    public static ulong HashBytes(ReadOnlySpan<byte> data) { return XxHash3.HashToUInt64(data); }

    public static string? Path(string category, ulong key)
    {
        string? root = Root;
        if (string.IsNullOrEmpty(root))
            return null;
        return System.IO.Path.Combine(root, "generated_cache", category, key.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");
    }

    // per-file locks, striped like Spartan's 64 mutexes
    private static readonly object[] s_file_mutexes = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private static object FileMutex(string path)
    {
        string name = System.IO.Path.GetFullPath(path).Replace('\\', '/');
        if (OperatingSystem.IsWindows())
            name = name.ToLowerInvariant();
        return s_file_mutexes[(uint)StringComparer.Ordinal.GetHashCode(name) % (uint)s_file_mutexes.Length];
    }
    private static long s_serial = 0;

    // payload io -------------------------------------------------------------------------------------------------
    public static bool ReadPayload(string path, ulong key, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            lock (FileMutex(path))
            {
                if (!File.Exists(path))
                    return false;
                byte[] file = File.ReadAllBytes(path);
                long length = file.Length;
                if (length < 16 || length > (long)(MaximumPayloadSize + HeaderSize))
                    return false;

                ulong stored_key = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(0));
                ulong checksum   = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8));
                if (stored_key != key)
                    return false;
                ulong marker = length >= 24 ? BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(16)) : 0;
                // in a valid legacy file this slot is a vector count, bounded far below magic
                bool legacy = marker != Magic;
                ulong raw_size = (ulong)length - 16;
                uint stored_version = 0, codec = 0;
                int payload_offset = 16;
                if (!legacy)
                {
                    if (length < HeaderSize)
                        return false;
                    raw_size       = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(24));
                    stored_version = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(32));
                    codec          = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(36));
                    if ((stored_version != 1 && stored_version != Version) || codec > 1)
                        return false;
                    payload_offset = HeaderSize;
                }
                if (raw_size > MaximumPayloadSize)
                    return false;
                long stored_size = length - payload_offset;
                if ((codec == 0 && (ulong)stored_size != raw_size) || (codec != 0 && (stored_size == 0 || (ulong)stored_size >= raw_size)))
                    return false;

                // DEVIATION: lz4 (codec 1) isn't available without a native/third-party dependency, such files read as
                // invalid and get rebuilt (and rewritten stored)
                if (codec != 0)
                    return false;

                byte[] decoded = file.AsSpan(payload_offset, (int)raw_size).ToArray();
                if (stored_version < 2)
                {
                    // DEVIATION: Spartan also keeps a learned (fnv -> xxh3) checksum index to skip re-hashing big legacy
                    // files; legacy files are just verified with fnv every time here
                    Hash fnv = new Hash();
                    fnv.Bytes(decoded);
                    if (fnv.value != checksum)
                        return false;
                }
                else if (HashBytes(decoded) != checksum)
                {
                    return false;
                }

                // touched on every hit so Maintain() evicts least recently used first
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                bytes = decoded;
                return true;
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is OutOfMemoryException)
        {
            return false; // a cache failure is a rebuild, never a failed load
        }
    }

    public static void WritePayload(string path, ulong key, ReadOnlySpan<byte> bytes)
    {
        if ((ulong)bytes.Length > MaximumPayloadSize)
            return;
        string? temporary = null;
        try
        {
            lock (FileMutex(path))
            {
                string? directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                // DEVIATION: always codec 0 (stored), Spartan lz4-compresses when it shrinks the payload
                byte[] header = new byte[HeaderSize];
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0), key);
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), HashBytes(bytes));
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), Magic);
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), (ulong)bytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), Version);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), 0u);

                // write to a temporary and rename over, never delete the previous file on a failed replacement
                temporary = $"{path}.tmp.{Stopwatch.GetTimestamp()}.{Interlocked.Increment(ref s_serial)}";
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(header);
                    stream.Write(bytes);
                }
                File.Move(temporary, path, overwrite: true);
                temporary = null;
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // cache storage is optional
        }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // vector codec -----------------------------------------------------------------------------------------------
    private static bool Append<T>(List<byte> bytes, ReadOnlySpan<T> values) where T : unmanaged
    {
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        ulong start = (ulong)bytes.Count;
        ulong count = (ulong)values.Length;
        if (MaximumPayloadSize - start < sizeof(ulong) || count > (MaximumPayloadSize - start - sizeof(ulong)) / (ulong)size)
            return false;
        Span<byte> count_bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(count_bytes, count);
        bytes.AddRange(count_bytes);
        bytes.AddRange(MemoryMarshal.AsBytes(values));
        return true;
    }

    private static bool Read<T>(ReadOnlySpan<byte> bytes, ref int cursor, out T[] values) where T : unmanaged
    {
        values = Array.Empty<T>();
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (bytes.Length - cursor < sizeof(ulong))
            return false;
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(cursor));
        cursor += sizeof(ulong);
        if (count > (ulong)(bytes.Length - cursor) / (ulong)size)
            return false;
        values = MemoryMarshal.Cast<byte, T>(bytes.Slice(cursor, (int)count * size)).ToArray();
        cursor += (int)count * size;
        return true;
    }

    public static byte[] Encode<T1>(ReadOnlySpan<T1> a) where T1 : unmanaged
    {
        var bytes = new List<byte>();
        return Append(bytes, a) ? bytes.ToArray() : Array.Empty<byte>();
    }
    public static byte[] Encode<T1, T2>(ReadOnlySpan<T1> a, ReadOnlySpan<T2> b) where T1 : unmanaged where T2 : unmanaged
    {
        var bytes = new List<byte>();
        return Append(bytes, a) && Append(bytes, b) ? bytes.ToArray() : Array.Empty<byte>();
    }

    // the outputs are only replaced when the whole payload decodes and nothing is left over
    public static bool Decode<T1>(ReadOnlySpan<byte> bytes, List<T1> a) where T1 : unmanaged
    {
        int cursor = 0;
        if (!Read(bytes, ref cursor, out T1[] va) || cursor != bytes.Length)
            return false;
        a.Clear(); a.AddRange(va);
        return true;
    }
    public static bool Decode<T1, T2>(ReadOnlySpan<byte> bytes, List<T1> a, List<T2> b) where T1 : unmanaged where T2 : unmanaged
    {
        int cursor = 0;
        if (!Read(bytes, ref cursor, out T1[] va) || !Read(bytes, ref cursor, out T2[] vb) || cursor != bytes.Length)
            return false;
        a.Clear(); a.AddRange(va);
        b.Clear(); b.AddRange(vb);
        return true;
    }

    // load / save --------------------------------------------------------------------------------------------------
    private static bool LoadCore(string? path, ulong key, Func<byte[], bool> decode)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        long start = Stopwatch.GetTimestamp();
        string category = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
        bool hit = false, missing = false;
        if (ReadPayload(path, key, out byte[] bytes))
            hit = decode(bytes);
        else
            missing = !File.Exists(path);
        Record(category, hit, missing, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return hit;
    }

    public static bool Load<T1>(string? path, ulong key, List<T1> a) where T1 : unmanaged
        => LoadCore(path, key, bytes => Decode(bytes, a));
    public static bool Load<T1, T2>(string? path, ulong key, List<T1> a, List<T2> b) where T1 : unmanaged where T2 : unmanaged
        => LoadCore(path, key, bytes => Decode(bytes, a, b));

    public static void Save<T1>(string? path, ulong key, ReadOnlySpan<T1> a) where T1 : unmanaged
    {
        if (string.IsNullOrEmpty(path)) return;
        byte[] bytes = Encode(a);
        if (bytes.Length != 0) WritePayload(path, key, bytes);
    }
    public static void Save<T1, T2>(string? path, ulong key, ReadOnlySpan<T1> a, ReadOnlySpan<T2> b) where T1 : unmanaged where T2 : unmanaged
    {
        if (string.IsNullOrEmpty(path)) return;
        byte[] bytes = Encode(a, b);
        if (bytes.Length != 0) WritePayload(path, key, bytes);
    }

    // maintenance --------------------------------------------------------------------------------------------------
    public struct MaintenanceResult { public ulong removed, bytes_removed; }
    private static readonly object s_maintenance_mutex = new object();

    // evict least recently used history over the soft budget, but keep everything this session touched even if it
    // exceeds the budget, otherwise large worlds rebuild on every load
    public static MaintenanceResult Maintain(ulong budget = 2ul * 1024 * 1024 * 1024)
    {
        MaintenanceResult result = default;
        string? root_dir = Root;
        if (string.IsNullOrEmpty(root_dir) || !Monitor.TryEnter(s_maintenance_mutex))
            return result;
        try
        {
            DateTime protect_since;
            lock (s_statistics_mutex)
                protect_since = s_session_started;

            DirectoryInfo root = new DirectoryInfo(System.IO.Path.Combine(root_dir, "generated_cache"));
            if (!root.Exists || root.LinkTarget != null)
                return result;

            var entries = new List<(FileInfo file, long size, DateTime time)>();
            ulong total = 0;
            // do not recurse through links or walk outside this disposable cache directory
            foreach (DirectoryInfo category in root.EnumerateDirectories())
            {
                if (category.LinkTarget != null)
                    continue;
                foreach (FileInfo entry in category.EnumerateFiles("*.bin"))
                {
                    string stem = System.IO.Path.GetFileNameWithoutExtension(entry.Name);
                    if (entry.LinkTarget != null || stem.Length == 0 || stem.Length > 20 || !stem.All(char.IsAsciiDigit))
                        continue;
                    entries.Add((entry, entry.Length, entry.LastWriteTimeUtc));
                    total += (ulong)entry.Length;
                }
            }

            foreach (var (file, size, time) in entries.OrderBy(e => e.time))
            {
                if (total <= budget)
                    break;
                if (time >= protect_since)
                    continue;
                lock (FileMutex(file.FullName))
                {
                    // a load/write since the scan makes this entry recent, so leave it alone
                    file.Refresh();
                    if (!file.Exists || file.LastWriteTimeUtc != time)
                        continue;
                    file.Delete();
                    total -= (ulong)size;
                    result.removed++;
                    result.bytes_removed += (ulong)size;
                }
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // incomplete enumeration is safe, try again next time
        }
        finally
        {
            Monitor.Exit(s_maintenance_mutex);
        }
        return result;
    }
}
