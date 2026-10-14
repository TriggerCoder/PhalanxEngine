// Port of the timestamp half of SpartanEngine RHI_Device.h/.cpp + vulkan/Vulkan_Device.cpp @ 4c07baa:
// GetCpuTimestampMs (RHI_Device.cpp:31-44), the timestamp period (Vulkan_Device.cpp:2169), the timestamp support check
// (:2182-2186), VK_EXT_calibrated_timestamps (:300, :339) and GetTimestampCalibration (:2240-2284).
// Differences:
// - GpuTiming stands in for cvar_debug_gpu_timing. A device without timestampComputeAndGraphics disables gpu timing
//   (spartan asserts there, while the comment above the assert says the profiler gets disabled).
// - The valid bits per queue family and the calibrateable time domains are read once at device creation (spartan
//   enumerates both on every call).
// - GetCpuTimestampMs uses Stopwatch, which is QueryPerformanceCounter on windows and CLOCK_MONOTONIC on linux, the
//   same clocks spartan reads and the same time domains the calibration asks for.
using System.Diagnostics;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    public const string CalibratedTimestampsExtensionName = "VK_EXT_calibrated_timestamps";

    private static uint[] s_timestamp_valid_bits = System.Array.Empty<uint>();
    private static ExtCalibratedTimestamps? s_calibrated_timestamps;
    private static bool s_timestamps_supported;
    private static TimeDomainKHR[] s_calibrateable_domains = System.Array.Empty<TimeDomainKHR>();

    // gpu timestamps on or off (spartan: cvar_debug_gpu_timing), command lists created while off get no timestamp pool
    public static bool GpuTiming { get; set; } = true;
    public static bool IsGpuTimingEnabled => GpuTiming && s_timestamps_supported;
    public static bool IsCalibratedTimestampsSupported => s_calibrated_timestamps != null;

    public static double GetCpuTimestampMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    // 0 means the family can't write timestamps
    public static uint GetTimestampValidBits(RHI_Queue_Type queue_type) => GetTimestampValidBitsForFamily(GetQueue(queue_type).FamilyIndex);
    public static uint GetTimestampValidBitsForFamily(uint family) => family < s_timestamp_valid_bits.Length ? s_timestamp_valid_bits[family] : 0;

    // the mask a tick difference wraps with
    public static ulong GetTimestampMask(RHI_Queue_Type queue_type)
    {
        uint bits = GetTimestampValidBits(queue_type);
        return bits >= 64 ? ulong.MaxValue : (1UL << (int)bits) - 1;
    }

    public static RHI_TimestampCalibration GetTimestampCalibration(RHI_Queue_Type queue_type)
    {
        double period_ns = PropertyTimestampPeriod;
        uint valid_bits  = GetTimestampValidBits(queue_type);
        var result       = new RHI_TimestampCalibration(0, 0.0, period_ns, 0.0, valid_bits, false);
        if (valid_bits == 0 || s_calibrated_timestamps == null)
            return result;

        TimeDomainKHR cpu_domain = OperatingSystem.IsWindows() ? TimeDomainKHR.QueryPerformanceCounterKhr : TimeDomainKHR.ClockMonotonicKhr;
        double cpu_tick_ms       = OperatingSystem.IsWindows() ? 1000.0 / Stopwatch.Frequency : 1e-6;

        if (!s_calibrateable_domains.Contains(cpu_domain))
            return result;

        CalibratedTimestampInfoKHR* infos = stackalloc CalibratedTimestampInfoKHR[2];
        infos[0] = new CalibratedTimestampInfoKHR { SType = StructureType.CalibratedTimestampInfoExt, TimeDomain = TimeDomainKHR.DeviceKhr };
        infos[1] = new CalibratedTimestampInfoKHR { SType = StructureType.CalibratedTimestampInfoExt, TimeDomain = cpu_domain };
        ulong* ticks    = stackalloc ulong[2];
        ulong deviation = 0;
        if (s_calibrated_timestamps.GetCalibratedTimestamp(RHI_Context.Device, 2, infos, ticks, &deviation) != Result.Success)
            return result;
        return result with { gpu_tick = ticks[0], cpu_ms = ticks[1] * cpu_tick_ms, deviation_ms = deviation * 1e-6, calibrated = true };
    }

    // called once the logical device exists
    private static void TimestampsInitialize(PhysicalDevice physical, in PhysicalDeviceLimits limits, bool calibrated_timestamps)
    {
        Vk vk = RHI_Context.Vk;
        PropertyTimestampPeriod = limits.TimestampPeriod;
        s_timestamps_supported = limits.TimestampComputeAndGraphics;
        if (!s_timestamps_supported)
            Log.LogWarning("The device doesn't support timestamps on all graphics and compute queues, gpu timing is disabled");

        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, ref count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
            vk.GetPhysicalDeviceQueueFamilyProperties(physical, ref count, p);
        s_timestamp_valid_bits = families.Select(f => f.TimestampValidBits).ToArray();

        s_calibrated_timestamps = null;
        s_calibrateable_domains = System.Array.Empty<TimeDomainKHR>();
        if (calibrated_timestamps && vk.TryGetDeviceExtension(RHI_Context.Instance, RHI_Context.Device, out ExtCalibratedTimestamps ext))
        {
            s_calibrated_timestamps = ext;
            s_calibrateable_domains = GetCalibrateableTimeDomains(physical);
        }
    }

    // a physical device function, so it comes from the instance (the extension object loads through the device)
    private static TimeDomainKHR[] GetCalibrateableTimeDomains(PhysicalDevice physical)
    {
        var get_domains = (delegate* unmanaged<PhysicalDevice, uint*, TimeDomainKHR*, Result>)
            RHI_Context.Vk.GetInstanceProcAddr(RHI_Context.Instance, "vkGetPhysicalDeviceCalibrateableTimeDomainsEXT").Handle;
        if (get_domains == null)
            return System.Array.Empty<TimeDomainKHR>();
        uint count = 0;
        if (get_domains(physical, &count, null) != Result.Success || count == 0)
            return System.Array.Empty<TimeDomainKHR>();
        var domains = new TimeDomainKHR[count];
        fixed (TimeDomainKHR* p = domains)
            if (get_domains(physical, &count, p) != Result.Success)
                return System.Array.Empty<TimeDomainKHR>();
        return domains[..(int)count];
    }

    private static void TimestampsDestroy()
    {
        s_calibrated_timestamps = null;
        s_timestamps_supported  = false;
        s_calibrateable_domains = System.Array.Empty<TimeDomainKHR>();
        s_timestamp_valid_bits  = System.Array.Empty<uint>();
    }
}
