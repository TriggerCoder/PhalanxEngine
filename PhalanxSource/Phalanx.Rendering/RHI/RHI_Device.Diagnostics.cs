// What the device reports when it is lost: the frame and pass the gpu was in, so a crash on someone else's machine
// names its pass. Spartan's counterpart is its breadcrumb system (profiling/Breadcrumbs.cpp @ 4c07baa, written by
// RHI_CommandList::begin_marker/end_marker, Vulkan_CommandList.cpp:2922-2972, reported from Vulkan_Queue.cpp:340-351
// and Vulkan_SyncPrimitive.cpp:80-86 when cvar debug.breadcrumbs is set).
// Deviations:
// - Three sources instead of one, each used when the driver has it: VK_NV_device_diagnostic_checkpoints (a checkpoint
//   at every pass marker and at the frame's begin and end, always on, the driver keeps the last ones per pipeline
//   stage), VK_EXT_device_fault (the faulting address and the vendor's description, always on) and the gpu
//   breadcrumbs (RenderValidation.GpuBreadcrumbs, startup only like spartan's cvar): every pass marker of the frame's
//   graphics list fills a slot of a host visible buffer with begun/completed. The breadcrumbs are kept per frame slot
//   for the frames in flight, so the report also shows the frame before the one that faulted.
// - No cpu marker stacks or error window: the report goes to the log (and log.txt) and the exception, the caller
//   decides how to exit (Renderer.Tick throws RendererDeviceLostException).
// - After the loss every wait returns at once and every timeline counts as complete, so the shutdown destroys the
//   objects without waiting on a device that will never signal (spartan asserts in its waits).
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    public const string CheckpointsExtensionName = "VK_NV_device_diagnostic_checkpoints";
    public const string DeviceFaultExtensionName = "VK_EXT_device_fault";

    private static volatile bool s_device_lost;
    private static delegate* unmanaged<CommandBuffer, void*, void> s_cmd_set_checkpoint;
    private static delegate* unmanaged<Queue, uint*, CheckpointDataNV*, void> s_get_queue_checkpoint_data;
    private static ExtDeviceFault? s_device_fault;
    private static readonly object s_marker_names_mutex = new();
    private static readonly List<string> s_marker_names = new();
    private static readonly Dictionary<string, uint> s_marker_ids = new();

    /// <summary>The device reported VK_ERROR_DEVICE_LOST; waits return at once from now on.</summary>
    public static bool IsDeviceLost => s_device_lost;
    public static bool HasCheckpoints => s_cmd_set_checkpoint != null;
    public static bool HasDeviceFault => s_device_fault != null;
    internal static void MarkDeviceLost() => s_device_lost = true;
    // test seam: the next queue submission fails with VK_ERROR_DEVICE_LOST (RHI_Queue.Submit)
    internal static volatile bool SimulateDeviceLostOnSubmit;

    // extensions and features CreateDevice asks for when the driver has them
    private static void DiagnosticsSelectExtensions(HashSet<string> available, List<string> extensions, PhysicalDevice physical, PhysicalDeviceFaultFeaturesEXT* fault_features)
    {
        if (available.Contains(CheckpointsExtensionName))
            extensions.Add(CheckpointsExtensionName);
        if (available.Contains(DeviceFaultExtensionName))
        {
            PhysicalDeviceFaultFeaturesEXT support = new() { SType = StructureType.PhysicalDeviceFaultFeaturesExt };
            PhysicalDeviceFeatures2 query = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &support };
            RHI_Context.Vk.GetPhysicalDeviceFeatures2(physical, &query);
            if (support.DeviceFault)
            {
                extensions.Add(DeviceFaultExtensionName);
                fault_features->DeviceFault = true;
            }
        }
    }

    // after vkCreateDevice: the entry points of what was enabled
    private static void DiagnosticsInitialize()
    {
        s_device_lost = false;
        s_cmd_set_checkpoint = null;
        s_get_queue_checkpoint_data = null;
        s_device_fault = null;
        Vk vk = RHI_Context.Vk;
        if (RHI_Context.ExtensionsDevice.Contains(CheckpointsExtensionName))
        {
            s_cmd_set_checkpoint        = (delegate* unmanaged<CommandBuffer, void*, void>)vk.GetDeviceProcAddr(RHI_Context.Device, "vkCmdSetCheckpointNV").Handle;
            s_get_queue_checkpoint_data = (delegate* unmanaged<Queue, uint*, CheckpointDataNV*, void>)vk.GetDeviceProcAddr(RHI_Context.Device, "vkGetQueueCheckpointDataNV").Handle;
            if (s_cmd_set_checkpoint == null || s_get_queue_checkpoint_data == null)
                s_cmd_set_checkpoint = null;
        }
        if (RHI_Context.ExtensionsDevice.Contains(DeviceFaultExtensionName) && vk.TryGetDeviceExtension(RHI_Context.Instance, RHI_Context.Device, out ExtDeviceFault fault))
            s_device_fault = fault;
        Log.LogInfo($"device lost diagnostics: checkpoints {(HasCheckpoints ? "on" : "unavailable")}, device fault {(HasDeviceFault ? "on" : "unavailable")}, gpu breadcrumbs {((ValidationFeatures & RenderValidation.GpuBreadcrumbs) != 0 ? "on" : "off")}");
    }

    private static void DiagnosticsDestroy()
    {
        RHI_Breadcrumbs.Destroy();
        s_cmd_set_checkpoint = null;
        s_get_queue_checkpoint_data = null;
        s_device_fault = null;
        s_device_lost = false; // the lost device is gone with it
    }

    internal static uint GetMarkerId(string name)
    {
        lock (s_marker_names_mutex)
        {
            if (!s_marker_ids.TryGetValue(name, out uint id))
            {
                id = (uint)s_marker_names.Count;
                s_marker_names.Add(name);
                s_marker_ids.Add(name, id);
            }
            return id;
        }
    }

    internal static string GetMarkerName(uint id)
    {
        lock (s_marker_names_mutex)
            return id < s_marker_names.Count ? s_marker_names[(int)id] : $"marker {id}";
    }

    // a checkpoint carries (frame, marker, begin or end) in its pointer value, nothing is dereferenced
    internal static ulong EncodeCheckpoint(ulong frame, uint marker_id, bool end) => ((frame & 0xFFFFFFFFUL) << 24) | ((ulong)(marker_id & 0x7FFFFF) << 1) | (end ? 1UL : 0UL) | (1UL << 56);
    internal static (ulong frame, uint marker_id, bool end) DecodeCheckpoint(ulong value) => ((value >> 24) & 0xFFFFFFFFUL, (uint)((value >> 1) & 0x7FFFFF), (value & 1) != 0);

    internal static void SetCheckpoint(RHI_CommandList cmd_list, string name, bool end)
    {
        if (s_cmd_set_checkpoint == null)
            return;
        ulong value = EncodeCheckpoint(s_frame_index, GetMarkerId(name), end);
        s_cmd_set_checkpoint(cmd_list.GetRhiResource(), (void*)value);
    }

    /// <summary>
    /// Everything the device and the driver still know after a loss, as text (also logged as errors and written to
    /// log.txt): the frame, the per queue timelines, the last checkpoints per pipeline stage, the device fault and the
    /// gpu breadcrumbs of the frames in flight.
    /// </summary>
    public static string ReportDeviceLost(string what)
    {
        s_device_lost = true;
        var report = new StringBuilder();
        report.AppendLine($"GPU device lost ({what})");
        report.AppendLine($"  frame {s_frame_index} (frames in flight {FramesInFlight}), device {(s_physical_devices.Count > 0 ? GetPrimaryPhysicalDevice().Name : "?")}");
        try { ReportTimelines(report); }   catch (Exception e) { report.AppendLine($"  timelines: {e.Message}"); }
        try { ReportCheckpoints(report); } catch (Exception e) { report.AppendLine($"  checkpoints: {e.Message}"); }
        try { ReportDeviceFault(report); } catch (Exception e) { report.AppendLine($"  device fault: {e.Message}"); }
        try { RHI_Breadcrumbs.Report(report); } catch (Exception e) { report.AppendLine($"  breadcrumbs: {e.Message}"); }
        try
        {
            if (MemoryGetBudget(out ulong[] budget, out ulong[] usage, out bool[] local))
                for (int i = 0; i < budget.Length; i++)
                    report.AppendLine($"  memory heap {i} ({(local[i] ? "device local" : "system")}): {usage[i] / (1024 * 1024)} of {budget[i] / (1024 * 1024)} MB budget");
        }
        catch (Exception e) { report.AppendLine($"  memory: {e.Message}"); }

        string text = report.ToString();
        Log.SetLogToFile(true);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Log.LogError(line.TrimEnd('\r'));
        return text;
    }

    private static void ReportTimelines(StringBuilder report)
    {
        lock (RHI_CommandList.ListsLock)
        {
            foreach (RHI_CommandList list in RHI_CommandList.AllLists)
            {
                if (list.GetLastTimelineSignalValue() == 0)
                    continue;
                ulong completed = 0;
                RHI_Context.Vk.GetSemaphoreCounterValue(RHI_Context.Device, list.GetTimelineSemaphore().GetSemaphore(), &completed);
                if (completed < list.GetLastTimelineSignalValue())
                    report.AppendLine($"  {list.Name}: submitted {list.GetLastTimelineSignalValue()}, completed {completed} (not finished)");
            }
        }
    }

    private static void ReportCheckpoints(StringBuilder report)
    {
        if (s_get_queue_checkpoint_data == null)
        {
            report.AppendLine($"  checkpoints: {CheckpointsExtensionName} is not available on this driver");
            return;
        }
        foreach (RHI_Queue queue in s_queues.Where(q => q != null).Distinct()!)
        {
            uint count = 0;
            s_get_queue_checkpoint_data(queue.NativeQueue, &count, null);
            if (count == 0)
            {
                report.AppendLine($"  checkpoints {queue.Name}: none");
                continue;
            }
            var data = new CheckpointDataNV[count];
            for (int i = 0; i < count; i++)
                data[i].SType = StructureType.CheckpointDataNV;
            fixed (CheckpointDataNV* p = data)
                s_get_queue_checkpoint_data(queue.NativeQueue, &count, p);
            for (int i = 0; i < count; i++)
            {
                (ulong frame, uint marker, bool end) = DecodeCheckpoint((ulong)data[i].PCheckpointMarker);
                report.AppendLine($"  checkpoint {queue.Name} [{data[i].Stage}]: frame {frame} {GetMarkerName(marker)} {(end ? "end" : "begin")}");
            }
        }
    }

    private static void ReportDeviceFault(StringBuilder report)
    {
        if (s_device_fault == null)
        {
            report.AppendLine($"  device fault: {DeviceFaultExtensionName} is not available on this driver");
            return;
        }
        DeviceFaultCountsEXT counts = new() { SType = StructureType.DeviceFaultCountsExt };
        Result result = s_device_fault.GetDeviceFaultInfo(RHI_Context.Device, &counts, null);
        if (result != Result.Success && result != Result.Incomplete)
        {
            report.AppendLine($"  device fault: vkGetDeviceFaultInfoEXT returned {result}");
            return;
        }
        var addresses = new DeviceFaultAddressInfoEXT[System.Math.Max(1u, counts.AddressInfoCount)];
        var vendors   = new DeviceFaultVendorInfoEXT[System.Math.Max(1u, counts.VendorInfoCount)];
        counts.VendorBinarySize = 0; // the binary dump is for vendor tools, not asked for
        fixed (DeviceFaultAddressInfoEXT* p_addresses = addresses)
        fixed (DeviceFaultVendorInfoEXT* p_vendors = vendors)
        {
            DeviceFaultInfoEXT info = new()
            {
                SType = StructureType.DeviceFaultInfoExt,
                PAddressInfos = counts.AddressInfoCount > 0 ? p_addresses : null,
                PVendorInfos  = counts.VendorInfoCount > 0 ? p_vendors : null,
            };
            result = s_device_fault.GetDeviceFaultInfo(RHI_Context.Device, &counts, &info);
            report.AppendLine($"  device fault: {SilkMarshal.PtrToString((nint)info.Description) ?? ""} ({result}, {counts.AddressInfoCount} address, {counts.VendorInfoCount} vendor records)");
        }
        for (int i = 0; i < counts.AddressInfoCount; i++)
        {
            DeviceFaultAddressInfoEXT a = addresses[i];
            ulong mask = a.AddressPrecision > 0 ? ~(a.AddressPrecision - 1) : ulong.MaxValue;
            report.AppendLine($"    address {a.AddressType}: 0x{a.ReportedAddress:x} (range 0x{a.ReportedAddress & mask:x} + 0x{a.AddressPrecision:x})");
        }
        for (int i = 0; i < counts.VendorInfoCount; i++)
        {
            DeviceFaultVendorInfoEXT v = vendors[i];
            report.AppendLine($"    vendor: {SilkMarshal.PtrToString((nint)v.Description) ?? ""} (code 0x{v.VendorFaultCode:x}, data 0x{v.VendorFaultData:x})");
        }
    }
}
