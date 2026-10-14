// Port of the pipeline half of SpartanEngine vulkan/Vulkan_Device.cpp @ 4c07baa: the VkPipelineCache with its disk copy
// (create_pipeline_cache / save_pipeline_cache, :1039-1089, created in Initialize :2236 and saved in Destroy :2354-2360)
// and RHI_Device::GetOrCreatePipeline / GetPipelineCount / GetPipelineCache (:2734-2762).
// Deviations:
// - The cache file is PipelineCachePath, by default pipeline_cache.bin next to the executable (spartan: the working
//   directory), null keeps the cache in memory only.
// - A saved cache is only handed to the driver when its header (version, vendor, device, pipelineCacheUUID) matches the
//   device; the spec lets drivers reject foreign data themselves, this keeps a cache from another GPU out entirely.
//   The file is written through a temporary file, so an interrupted save doesn't leave a truncated cache behind.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    private static readonly object s_pipeline_mutex = new();
    private static readonly Dictionary<ulong, RHI_Pipeline> s_pipelines = new();
    private static PipelineCache s_pipeline_cache;

    public static string? PipelineCachePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "pipeline_cache.bin");

    private static void PipelinesInitialize()
    {
        PipelineCacheCreateInfo create_info = new() { SType = StructureType.PipelineCacheCreateInfo };

        // try to load a previously saved cache from disk
        byte[]? cache_data = null;
        string? path = PipelineCachePath;
        if (path != null && File.Exists(path))
        {
            try { cache_data = File.ReadAllBytes(path); }
            catch (IOException) { cache_data = null; }
            catch (UnauthorizedAccessException) { cache_data = null; }
        }
        if (cache_data != null && !IsPipelineCacheCompatible(cache_data))
            cache_data = null;

        fixed (byte* data = cache_data)
        {
            if (cache_data is { Length: > 0 })
            {
                create_info.InitialDataSize = (nuint)cache_data.Length;
                create_info.PInitialData    = data;
            }
            Check(RHI_Context.Vk.CreatePipelineCache(RHI_Context.Device, in create_info, null, out s_pipeline_cache), "vkCreatePipelineCache");
        }
    }

    // VkPipelineCacheHeaderVersionOne: header size, header version, vendor id, device id, pipeline cache uuid
    public static bool IsPipelineCacheCompatible(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
            return false;
        PhysicalDeviceProperties props;
        RHI_Context.Vk.GetPhysicalDeviceProperties(RHI_Context.DevicePhysical, &props);
        uint header_size    = BitConverter.ToUInt32(data[0..4]);
        uint header_version = BitConverter.ToUInt32(data[4..8]);
        uint vendor_id      = BitConverter.ToUInt32(data[8..12]);
        uint device_id      = BitConverter.ToUInt32(data[12..16]);
        if (header_size < 32 || header_version != (uint)PipelineCacheHeaderVersion.One || vendor_id != props.VendorID || device_id != props.DeviceID)
            return false;
        return data[16..32].SequenceEqual(new ReadOnlySpan<byte>(props.PipelineCacheUuid, 16));
    }

    private static void SavePipelineCache()
    {
        if (s_pipeline_cache.Handle == 0 || PipelineCachePath is not { } path)
            return;

        nuint data_size = 0;
        Check(RHI_Context.Vk.GetPipelineCacheData(RHI_Context.Device, s_pipeline_cache, &data_size, null), "vkGetPipelineCacheData");
        if (data_size == 0)
            return;

        byte[] data = new byte[(int)data_size];
        fixed (byte* p = data)
            Check(RHI_Context.Vk.GetPipelineCacheData(RHI_Context.Device, s_pipeline_cache, &data_size, p), "vkGetPipelineCacheData");

        try
        {
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, data.AsSpan(0, (int)data_size).ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException e) { Log.LogWarning($"Failed to save the pipeline cache to \"{path}\": {e.Message}"); }
        catch (UnauthorizedAccessException e) { Log.LogWarning($"Failed to save the pipeline cache to \"{path}\": {e.Message}"); }
    }

    // after the deletion queue was flushed, before the descriptor layouts go
    private static void PipelinesDestroy()
    {
        lock (s_pipeline_mutex)
        {
            foreach (RHI_Pipeline pipeline in s_pipelines.Values)
                pipeline.Dispose();
            s_pipelines.Clear();
        }
        DeletionQueueFlush(); // the pipelines just queued themselves

        // pipeline cache - save to disk before destroying
        SavePipelineCache();
        if (s_pipeline_cache.Handle != 0)
        {
            RHI_Context.Vk.DestroyPipelineCache(RHI_Context.Device, s_pipeline_cache, null);
            s_pipeline_cache = default;
        }
    }

    // the descriptor layout is part of the compiled pipeline, a cache hit doesn't hash shader reflection again
    public static RHI_Pipeline GetOrCreatePipeline(RHI_PipelineState pso)
    {
        if (pso.GetHash() == 0)
            throw new InvalidOperationException($"{pso.name}: Prepare() the pipeline state first");

        lock (s_pipeline_mutex)
        {
            ulong hash = pso.GetHash();
            if (!s_pipelines.TryGetValue(hash, out RHI_Pipeline? pipeline))
            {
                RHI_Shader[] shaders = pso.shaders.Where(s => s != null).ToArray()!;
                RHI_DescriptorSetLayout layout = GetOrCreateDescriptorSetLayout(shaders, pso.name ?? "pipeline");
                pipeline = new RHI_Pipeline(pso, layout);
                s_pipelines[hash] = pipeline;
            }
            return pipeline;
        }
    }

    public static int GetPipelineCount() { lock (s_pipeline_mutex) return s_pipelines.Count; }
    public static PipelineCache GetPipelineCache() => s_pipeline_cache;
}
