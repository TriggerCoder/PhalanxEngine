// Port of SpartanEngine source/rhi/RHI_PhysicalDevice.h/.cpp @ 4c07baa.
// Differences:
// - The version strings are decoded on demand (spartan decodes into fixed buffers in the constructor, through static
//   char arrays shared by every instance).
// - A driver of no known vendor is decoded with the standard vulkan version layout and its driverInfo appended (spartan:
//   "Unable to determine driver version"), so mesa drivers like lavapipe and radv read as their mesa version.
// - The api major version is masked to 7 bits like VK_API_VERSION_MAJOR (spartan shifts without the mask, so a nonzero
//   variant would show up in the major).
using Silk.NET.Vulkan;

namespace Phalanx.Rendering.RHI;

internal sealed class RHI_PhysicalDevice
{
    public uint ApiVersion { get; }
    public uint DriverVersion { get; }
    public uint VendorId { get; }
    public RHI_PhysicalDevice_Type Type { get; }
    public string Name { get; }
    public ulong Memory { get; }        // bytes of the first device local heap
    public PhysicalDevice NativeHandle { get; }
    public string DriverInfo { get; }   // VkPhysicalDeviceVulkan12Properties::driverInfo, empty when unknown

    public RHI_PhysicalDevice(uint api_version, uint driver_version, uint vendor_id, RHI_PhysicalDevice_Type type, string name, ulong memory, PhysicalDevice native_handle,
                              string driver_info = "")
    {
        ApiVersion = api_version; DriverVersion = driver_version; VendorId = vendor_id;
        Type = type; Name = name; Memory = memory; NativeHandle = native_handle; DriverInfo = driver_info;
    }

    // spartan: decode_api_version
    public static string DecodeApiVersion(uint version) => $"{(version >> 22) & 0x7f}.{(version >> 12) & 0x3ff}.{version & 0xfff}";
    public string GetApiVersion() => DecodeApiVersion(ApiVersion);

    // spartan: decode_driver_version
    public string GetDriverVersion()
    {
        uint version = DriverVersion;
        if (IsNvidia())
            return $"{(version >> 22) & 0x3ff}.{(version >> 14) & 0x0ff}.{(version >> 6) & 0x0ff}.{version & 0x003f}";

        if (IsAmd())
        {
            // for amd gpus, driver_info matches the adrenalin version, version is an internal version
            if (DriverInfo.Length == 0)
                return "Unknown AMD driver";
            int space = DriverInfo.IndexOf(' ');
            return space < 0 ? DriverInfo : DriverInfo[..space]; // truncate at first space
        }

        if (IsIntel())
            return $"{version >> 14}.{version & 0x3fff}";

        string standard = $"{version >> 22}.{(version >> 12) & 0x3ff}.{version & 0xfff}";
        return DriverInfo.Length == 0 ? standard : $"{standard} ({DriverInfo})";
    }

    public bool IsNvidia()   => VendorId == 0x10DE || Name.Contains("Nvidia", StringComparison.OrdinalIgnoreCase);
    public bool IsAmd()      => VendorId == 0x1002 || VendorId == 0x1022 || Name.Contains("Amd", StringComparison.OrdinalIgnoreCase);
    public bool IsIntel()    => VendorId == 0x8086 || VendorId == 0x163C || VendorId == 0x8087 || Name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    public bool IsArm()      => VendorId == 0x13B5 || Name.Contains("Arm,", StringComparison.OrdinalIgnoreCase);
    public bool IsQualcomm() => VendorId == 0x5143 || Name.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase);
    public ulong GetMemoryMb() => Memory / 1024 / 1024;

    public string GetVendorName()
    {
        if (IsNvidia())   return "Nvidia";
        if (IsAmd())      return "AMD";
        if (IsIntel())    return "Intel";
        if (IsArm())      return "ARM";
        if (IsQualcomm()) return "Qualcomm";
        return "Unknown";
    }

    // spartan: RHI_Device::PhysicalDeviceRegister. discrete devices come first, then (within a type) more memory first
    public static void Register(List<RHI_PhysicalDevice> devices, RHI_PhysicalDevice device)
    {
        devices.Add(device);
        // stable: keeps registration order for equal keys
        var sorted = devices
            .Select((d, i) => (d, i))
            .OrderBy(x => x.d.Type == RHI_PhysicalDevice_Type.Discrete ? 0 : 1)
            .ThenBy(x => (int)x.d.Type)
            .ThenByDescending(x => x.d.Memory)
            .ThenBy(x => x.i)
            .Select(x => x.d)
            .ToList();
        devices.Clear();
        devices.AddRange(sorted);
    }
}
