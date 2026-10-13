// Port of SpartanEngine source/rhi/RHI_PhysicalDevice.h/.cpp @ 4c07baa.
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

    public RHI_PhysicalDevice(uint api_version, uint driver_version, uint vendor_id, RHI_PhysicalDevice_Type type, string name, ulong memory, PhysicalDevice native_handle)
    {
        ApiVersion = api_version; DriverVersion = driver_version; VendorId = vendor_id;
        Type = type; Name = name; Memory = memory; NativeHandle = native_handle;
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
