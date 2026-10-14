// Port of SpartanEngine source/rhi/RHI_Device.h/.cpp + vulkan/Vulkan_Device.cpp @ 4c07baa (first slice):
// instance (+ optional validation through VK_EXT_debug_utils), physical device registration/selection,
// queue family detection (dedicated compute/copy when available), logical device with Vulkan 1.3
// dynamic rendering + synchronization2 and 1.2 timeline semaphores, queues, object naming.
// Frames + deletion queue: RHI_Device.Frame.cs, memory: RHI_Device.Memory.cs (step 2).
// TODO(port): descriptors + bindless, pipeline cache, staging pool, compute frame lists,
// vendor technologies (FSR/XeSS/DLSS), ray tracing, mesh shaders, VRS, calibrated timestamps.
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    public const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";
    public static readonly uint ApiVersionRequired = Vk.Version13;

    private static readonly List<RHI_PhysicalDevice> s_physical_devices = new();
    private static int s_physical_device_index;
    private static readonly RHI_Queue?[] s_queues = new RHI_Queue?[(int)RHI_Queue_Type.Max];
    private static uint s_index_graphics, s_index_compute, s_index_copy;
    private static DebugUtilsMessengerCallbackFunctionEXT? s_debug_callback; // kept alive while the messenger exists
    private static readonly List<string> s_validation_messages = new();

    public static bool IsInitialized => RHI_Context.Device.Handle != 0;
    public static IReadOnlyList<string> ValidationMessages { get { lock (s_validation_messages) return s_validation_messages.ToArray(); } }

    // properties (spartan: Property*)
    public static float  PropertyTimestampPeriod { get; private set; }
    public static ulong  PropertyMinUniformBufferOffsetAlignment { get; private set; }
    public static ulong  PropertyMinStorageBufferOffsetAlignment { get; private set; }
    public static uint   PropertyMaxPushConstantSize { get; private set; }
    public static uint   PropertyMaxTexture2dDimension { get; private set; }
    public static ulong  PropertyOptimalBufferCopyOffsetAlignment { get; private set; }

    // instance_extensions: what the window system needs (IVkSurface.GetRequiredExtensions), empty for headless
    public static void Initialize(IReadOnlyList<string> instance_extensions, bool validation)
    {
        if (IsInitialized)
            throw new InvalidOperationException("RHI_Device is already initialized");

        Vk vk = RHI_Context.Api ??= Vk.GetApi();
        lock (s_validation_messages) s_validation_messages.Clear();

        // instance
        {
            uint driver_version = Vk.Version10;
            vk.EnumerateInstanceVersion(ref driver_version);
            if (driver_version < ApiVersionRequired)
                throw new NotSupportedException($"Vulkan {VersionToString(ApiVersionRequired)} is required, the driver supports {VersionToString(driver_version)}");
            RHI_Context.ApiVersion = ApiVersionRequired;

            var extensions = new List<string>(instance_extensions);
            var layers     = new List<string>();
            HashSet<string> available_extensions = GetInstanceExtensions(null);

            if (validation)
            {
                if (IsLayerAvailable(ValidationLayerName))
                {
                    layers.Add(ValidationLayerName);
                    available_extensions.UnionWith(GetInstanceExtensions(ValidationLayerName));
                }
                else
                {
                    Log.LogWarning($"Validation requested but {ValidationLayerName} is not installed, continuing without it");
                    validation = false;
                }
            }
            if (available_extensions.Contains(ExtDebugUtils.ExtensionName))
                extensions.Add(ExtDebugUtils.ExtensionName);
            if (available_extensions.Contains("VK_EXT_swapchain_colorspace") && instance_extensions.Count > 0)
                extensions.Add("VK_EXT_swapchain_colorspace");

            RHI_Context.Validation         = validation;
            RHI_Context.Gpu_Markers        = extensions.Contains(ExtDebugUtils.ExtensionName);
            RHI_Context.ExtensionsInstance = extensions.Distinct().ToArray();

            nint app_name = SilkMarshal.StringToPtr("Phalanx");
            nint ext_ptr  = SilkMarshal.StringArrayToPtr(RHI_Context.ExtensionsInstance);
            nint layer_ptr = SilkMarshal.StringArrayToPtr(layers.ToArray());
            try
            {
                ApplicationInfo app_info = new()
                {
                    SType              = StructureType.ApplicationInfo,
                    PApplicationName   = (byte*)app_name,
                    ApplicationVersion = new Version32(1, 0, 0),
                    PEngineName        = (byte*)app_name,
                    EngineVersion      = new Version32(0, 1, 0),
                    ApiVersion         = RHI_Context.ApiVersion
                };
                InstanceCreateInfo create_info = new()
                {
                    SType                   = StructureType.InstanceCreateInfo,
                    PApplicationInfo        = &app_info,
                    EnabledExtensionCount   = (uint)RHI_Context.ExtensionsInstance.Length,
                    PpEnabledExtensionNames = (byte**)ext_ptr,
                    EnabledLayerCount       = (uint)layers.Count,
                    PpEnabledLayerNames     = (byte**)layer_ptr
                };
                Check(vk.CreateInstance(in create_info, null, out RHI_Context.Instance), "vkCreateInstance");
            }
            finally
            {
                SilkMarshal.Free(app_name);
                SilkMarshal.Free(ext_ptr);
                SilkMarshal.Free(layer_ptr);
            }

            if (RHI_Context.Gpu_Markers && vk.TryGetInstanceExtension(RHI_Context.Instance, out ExtDebugUtils debug_utils))
            {
                RHI_Context.DebugUtils = debug_utils;
                if (validation)
                    CreateDebugMessenger(debug_utils);
            }
            // only when the window asked for it: the loader hands out the functions even when the extension is not enabled
            if (RHI_Context.ExtensionsInstance.Contains(KhrSurface.ExtensionName) && vk.TryGetInstanceExtension(RHI_Context.Instance, out KhrSurface khr_surface))
                RHI_Context.Surface = khr_surface;
        }

        // physical device
        DetectPhysicalDevices();
        SelectPrimaryPhysicalDevice();

        // logical device + queues
        CreateDevice();

        // memory + frame state
        s_allocator = new RHI_Allocator_Custom();
        FrameReset();
    }

    // true when a loader exists and some device reports vulkan 1.3; creates and destroys a bare instance
    public static bool Probe(out string reason)
    {
        try
        {
            Vk vk = RHI_Context.Api ??= Vk.GetApi();
            uint version = Vk.Version10;
            vk.EnumerateInstanceVersion(ref version);
            if (version < ApiVersionRequired) { reason = $"loader supports Vulkan {VersionToString(version)}"; return false; }
            if (IsInitialized) { reason = ""; return true; }

            ApplicationInfo app = new() { SType = StructureType.ApplicationInfo, ApiVersion = ApiVersionRequired };
            InstanceCreateInfo info = new() { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
            if (vk.CreateInstance(in info, null, out Instance instance) != Result.Success) { reason = "vkCreateInstance failed"; return false; }
            try
            {
                uint count = 0;
                vk.EnumeratePhysicalDevices(instance, ref count, null);
                if (count == 0) { reason = "no physical devices (no Vulkan driver/ICD installed)"; return false; }
                var devices = new PhysicalDevice[count];
                fixed (PhysicalDevice* p = devices) vk.EnumeratePhysicalDevices(instance, ref count, p);
                foreach (PhysicalDevice d in devices)
                {
                    vk.GetPhysicalDeviceProperties(d, out PhysicalDeviceProperties props);
                    if (props.ApiVersion >= ApiVersionRequired) { reason = ""; return true; }
                }
                reason = "no Vulkan 1.3 device";
                return false;
            }
            finally { vk.DestroyInstance(instance, null); }
        }
        catch (Exception e) { reason = e.Message; return false; }
    }

    public static void Destroy()
    {
        if (RHI_Context.Api == null)
            return;
        Vk vk = RHI_Context.Vk;

        if (RHI_Context.Device.Handle != 0)
        {
            vk.DeviceWaitIdle(RHI_Context.Device);
            DeletionQueueFlush();
            FrameReset();
            s_allocator?.Dispose();
            s_allocator = null;
            var destroyed = new HashSet<RHI_Queue>();
            for (int i = 0; i < s_queues.Length; i++)
            {
                if (s_queues[i] is RHI_Queue q && destroyed.Add(q))
                    q.Dispose();
                s_queues[i] = null;
            }
            vk.DestroyDevice(RHI_Context.Device, null);
            RHI_Context.Device = default;
        }
        if (RHI_Context.DebugMessenger.Handle != 0)
        {
            RHI_Context.DebugUtils!.DestroyDebugUtilsMessenger(RHI_Context.Instance, RHI_Context.DebugMessenger, null);
            RHI_Context.DebugMessenger = default;
        }
        if (RHI_Context.Instance.Handle != 0)
        {
            vk.DestroyInstance(RHI_Context.Instance, null);
            RHI_Context.Instance = default;
        }
        RHI_Context.DebugUtils = null;
        RHI_Context.Surface    = null;
        RHI_Context.Swapchain  = null;
        RHI_Context.DevicePhysical = default;
        s_physical_devices.Clear();
        s_debug_callback = null;
    }

    // queues
    public static RHI_Queue GetQueue(RHI_Queue_Type type) =>
        s_queues[(int)(type == RHI_Queue_Type.Present ? RHI_Queue_Type.Graphics : type)] ?? throw new InvalidOperationException("RHI_Device is not initialized");

    public static uint GetQueueIndex(RHI_Queue_Type type) => type switch
    {
        RHI_Queue_Type.Compute => s_index_compute,
        RHI_Queue_Type.Copy    => s_index_copy,
        _                      => s_index_graphics,
    };

    public static void QueueWaitAll()
    {
        foreach (RHI_Queue? q in s_queues.Distinct())
            q?.Wait();
    }

    // physical devices
    public static IReadOnlyList<RHI_PhysicalDevice> PhysicalDeviceGet() => s_physical_devices;
    public static RHI_PhysicalDevice GetPrimaryPhysicalDevice() => s_physical_devices[s_physical_device_index];
    public static void PhysicalDeviceRegister(RHI_PhysicalDevice device) => RHI_PhysicalDevice.Register(s_physical_devices, device);
    public static void PhysicalDeviceSetPrimary(int index)
    {
        s_physical_device_index = index;
        RHI_PhysicalDevice d = GetPrimaryPhysicalDevice();
        Log.LogInfo($"{d.Name} ({d.GetMemoryMb()} MB)");
    }

    // spartan: queues::detect_queue_family_indices. pure so it can be tested without a gpu.
    // graphics: any family with graphics. compute: dedicated (no graphics) first, then any other family, then graphics'.
    // copy: dedicated (no graphics/compute) first, then any family not used above, then any family with transfer.
    public static bool DetectQueueFamilyIndices(IReadOnlyList<QueueFlags> families, out uint graphics, out uint compute, out uint copy)
    {
        graphics = compute = copy = 0;

        bool GetFamilyIndex(QueueFlags flags, ISet<uint> exclude, out uint index)
        {
            // dedicated first
            for (uint i = 0; i < families.Count; i++)
            {
                if (exclude.Contains(i) || (families[(int)i] & flags) != flags)
                    continue;
                bool is_dedicated = true;
                if (flags == QueueFlags.ComputeBit)
                    is_dedicated = (families[(int)i] & QueueFlags.GraphicsBit) == 0;
                if (flags == QueueFlags.TransferBit)
                    is_dedicated = (families[(int)i] & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) == 0;
                if (is_dedicated) { index = i; return true; }
            }
            // fallback to any supporting
            for (uint i = 0; i < families.Count; i++)
            {
                if (exclude.Contains(i))
                    continue;
                if ((families[(int)i] & flags) != 0) { index = i; return true; }
            }
            index = 0;
            return false;
        }

        if (!GetFamilyIndex(QueueFlags.GraphicsBit, new HashSet<uint>(), out graphics))
            return false;

        if (!GetFamilyIndex(QueueFlags.ComputeBit, new HashSet<uint> { graphics }, out compute) &&
            !GetFamilyIndex(QueueFlags.ComputeBit, new HashSet<uint>(), out compute))
            return false;

        // graphics and compute families support transfer implicitly (spec), so check the flag or either of those
        var with_transfer = families.Select(f => (f & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) != 0 ? f | QueueFlags.TransferBit : f).ToArray();
        families = with_transfer;
        if (!GetFamilyIndex(QueueFlags.TransferBit, new HashSet<uint> { graphics, compute }, out copy) &&
            !GetFamilyIndex(QueueFlags.TransferBit, new HashSet<uint>(), out copy))
            return false;

        return true;
    }

    public static void SetResourceName(ulong handle, RHI_Resource_Type type, string name)
    {
        if (!RHI_Context.Gpu_Markers || RHI_Context.DebugUtils == null || RHI_Context.Device.Handle == 0 || handle == 0)
            return;
        nint name_ptr = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsObjectNameInfoEXT info = new()
            {
                SType        = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType   = VulkanObjectType[(int)type],
                ObjectHandle = handle,
                PObjectName  = (byte*)name_ptr
            };
            RHI_Context.DebugUtils.SetDebugUtilsObjectName(RHI_Context.Device, in info);
        }
        finally { SilkMarshal.Free(name_ptr); }
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static HashSet<string> GetInstanceExtensions(string? layer)
    {
        Vk vk = RHI_Context.Vk;
        uint count = 0;
        nint layer_ptr = layer == null ? 0 : SilkMarshal.StringToPtr(layer);
        try
        {
            vk.EnumerateInstanceExtensionProperties((byte*)layer_ptr, ref count, null);
            var props = new ExtensionProperties[count];
            fixed (ExtensionProperties* p = props)
                vk.EnumerateInstanceExtensionProperties((byte*)layer_ptr, ref count, p);
            var set = new HashSet<string>();
            foreach (ExtensionProperties e in props)
                set.Add(SilkMarshal.PtrToString((nint)e.ExtensionName)!);
            return set;
        }
        finally { if (layer_ptr != 0) SilkMarshal.Free(layer_ptr); }
    }

    private static HashSet<string> GetDeviceExtensions(PhysicalDevice device)
    {
        Vk vk = RHI_Context.Vk;
        uint count = 0;
        vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null);
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = props)
            vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, p);
        var set = new HashSet<string>();
        foreach (ExtensionProperties e in props)
            set.Add(SilkMarshal.PtrToString((nint)e.ExtensionName)!);
        return set;
    }

    private static bool IsLayerAvailable(string name)
    {
        Vk vk = RHI_Context.Vk;
        uint count = 0;
        vk.EnumerateInstanceLayerProperties(ref count, null);
        var props = new LayerProperties[count];
        fixed (LayerProperties* p = props)
            vk.EnumerateInstanceLayerProperties(ref count, p);
        foreach (LayerProperties l in props)
            if (SilkMarshal.PtrToString((nint)l.LayerName) == name)
                return true;
        return false;
    }

    private static void CreateDebugMessenger(ExtDebugUtils debug_utils)
    {
        s_debug_callback = (severity, types, data, user) =>
        {
            string message = SilkMarshal.PtrToString((nint)data->PMessage) ?? "";
            lock (s_validation_messages)
                s_validation_messages.Add($"[{severity}] {message}");
            if (severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt))
                Log.LogError(message);
            else
                Log.LogWarning(message);
            return Vk.False;
        };
        DebugUtilsMessengerCreateInfoEXT info = new()
        {
            SType           = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType     = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(s_debug_callback)
        };
        Check(debug_utils.CreateDebugUtilsMessenger(RHI_Context.Instance, in info, null, out RHI_Context.DebugMessenger), "vkCreateDebugUtilsMessengerEXT");
    }

    private static void DetectPhysicalDevices()
    {
        Vk vk = RHI_Context.Vk;
        uint count = 0;
        Check(vk.EnumeratePhysicalDevices(RHI_Context.Instance, ref count, null), "vkEnumeratePhysicalDevices");
        if (count == 0)
            throw new NotSupportedException("There are no available Vulkan physical devices");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            Check(vk.EnumeratePhysicalDevices(RHI_Context.Instance, ref count, p), "vkEnumeratePhysicalDevices");

        s_physical_devices.Clear();
        foreach (PhysicalDevice device in devices)
        {
            vk.GetPhysicalDeviceProperties(device, out PhysicalDeviceProperties props);
            vk.GetPhysicalDeviceMemoryProperties(device, out PhysicalDeviceMemoryProperties memory);

            // find the local device memory heap size
            ulong vram = 0;
            for (int i = 0; i < memory.MemoryHeapCount; i++)
            {
                if ((memory.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                {
                    vram = memory.MemoryHeaps[i].Size;
                    break;
                }
            }

            string name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "unknown";
            PhysicalDeviceRegister(new RHI_PhysicalDevice(props.ApiVersion, props.DriverVersion, props.VendorID, FromVulkan(props.DeviceType), name, vram, device));
        }
    }

    private static QueueFlags[] GetQueueFamilies(PhysicalDevice device)
    {
        Vk vk = RHI_Context.Vk;
        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        var props = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = props)
            vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, p);
        return props.Select(p => p.QueueFlags).ToArray();
    }

    private static void SelectPrimaryPhysicalDevice()
    {
        // the first device (sorted best to worst) that supports vulkan 1.3 and graphics, compute and transfer queues
        for (int i = 0; i < s_physical_devices.Count; i++)
        {
            RHI_PhysicalDevice device = s_physical_devices[i];
            if (device.ApiVersion < ApiVersionRequired)
                continue;
            if (DetectQueueFamilyIndices(GetQueueFamilies(device.NativeHandle), out s_index_graphics, out s_index_compute, out s_index_copy))
            {
                PhysicalDeviceSetPrimary(i);
                RHI_Context.DevicePhysical = device.NativeHandle;
                return;
            }
        }
        throw new NotSupportedException("No Vulkan 1.3 device with graphics, compute and transfer queues was found");
    }

    private static void CreateDevice()
    {
        Vk vk = RHI_Context.Vk;
        PhysicalDevice physical = RHI_Context.DevicePhysical;

        // features, the ones the first slice needs (spartan asserts these as well)
        PhysicalDeviceVulkan13Features support_1_3 = new() { SType = StructureType.PhysicalDeviceVulkan13Features };
        PhysicalDeviceVulkan12Features support_1_2 = new() { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &support_1_3 };
        PhysicalDeviceFeatures2 support = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &support_1_2 };
        vk.GetPhysicalDeviceFeatures2(physical, &support);
        if (!support_1_3.DynamicRendering || !support_1_3.Synchronization2 || !support_1_2.TimelineSemaphore)
            throw new NotSupportedException("The device lacks dynamic rendering, synchronization2 or timeline semaphores");

        PhysicalDeviceVulkan13Features features_1_3 = new() { SType = StructureType.PhysicalDeviceVulkan13Features, DynamicRendering = true, Synchronization2 = true };
        PhysicalDeviceVulkan12Features features_1_2 = new() { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &features_1_3, TimelineSemaphore = true };
        PhysicalDeviceFeatures2 features = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features_1_2 };

        // extensions
        HashSet<string> available = GetDeviceExtensions(physical);
        var extensions = new List<string>();
        if (RHI_Context.Surface != null && available.Contains(KhrSwapchain.ExtensionName))
            extensions.Add(KhrSwapchain.ExtensionName);
        RHI_Context.ExtensionsDevice = extensions.ToArray();

        // one queue per unique family
        uint[] families = new[] { s_index_graphics, s_index_compute, s_index_copy }.Distinct().ToArray();
        float priority = 1.0f;
        var queue_infos = new DeviceQueueCreateInfo[families.Length];
        for (int i = 0; i < families.Length; i++)
            queue_infos[i] = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = families[i], QueueCount = 1, PQueuePriorities = &priority };

        nint ext_ptr = SilkMarshal.StringArrayToPtr(RHI_Context.ExtensionsDevice);
        try
        {
            fixed (DeviceQueueCreateInfo* p_queues = queue_infos)
            {
                DeviceCreateInfo info = new()
                {
                    SType                   = StructureType.DeviceCreateInfo,
                    PNext                   = &features,
                    QueueCreateInfoCount    = (uint)queue_infos.Length,
                    PQueueCreateInfos       = p_queues,
                    EnabledExtensionCount   = (uint)RHI_Context.ExtensionsDevice.Length,
                    PpEnabledExtensionNames = (byte**)ext_ptr
                };
                Check(vk.CreateDevice(physical, in info, null, out RHI_Context.Device), "vkCreateDevice");
            }
        }
        finally { SilkMarshal.Free(ext_ptr); }

        if (RHI_Context.Surface != null && vk.TryGetDeviceExtension(RHI_Context.Instance, RHI_Context.Device, out KhrSwapchain khr_swapchain))
            RHI_Context.Swapchain = khr_swapchain;

        // properties
        vk.GetPhysicalDeviceProperties(physical, out PhysicalDeviceProperties props);
        PropertyTimestampPeriod                 = props.Limits.TimestampPeriod;
        PropertyMinUniformBufferOffsetAlignment = props.Limits.MinUniformBufferOffsetAlignment;
        PropertyMinStorageBufferOffsetAlignment = props.Limits.MinStorageBufferOffsetAlignment;
        PropertyMaxPushConstantSize             = props.Limits.MaxPushConstantsSize;
        PropertyMaxTexture2dDimension           = props.Limits.MaxImageDimension2D;
        PropertyOptimalBufferCopyOffsetAlignment = System.Math.Max(4UL, props.Limits.OptimalBufferCopyOffsetAlignment); // 4: copy offsets must be multiples of 4 for any format

        // queues (queue types that share a family share the vulkan queue and its lock)
        var by_family = new Dictionary<uint, (Queue queue, object mutex)>();
        foreach (uint family in families)
        {
            vk.GetDeviceQueue(RHI_Context.Device, family, 0, out Queue queue);
            by_family[family] = (queue, new object());
            SetResourceName((ulong)queue.Handle, RHI_Resource_Type.Queue, $"queue_family_{family}");
        }
        RHI_Queue Make(RHI_Queue_Type type, uint family, string name) => new(type, family, by_family[family].queue, by_family[family].mutex, name);
        s_queues[(int)RHI_Queue_Type.Graphics] = Make(RHI_Queue_Type.Graphics, s_index_graphics, "graphics");
        s_queues[(int)RHI_Queue_Type.Compute]  = Make(RHI_Queue_Type.Compute,  s_index_compute,  "compute");
        s_queues[(int)RHI_Queue_Type.Copy]     = Make(RHI_Queue_Type.Copy,     s_index_copy,     "copy");
    }
}
