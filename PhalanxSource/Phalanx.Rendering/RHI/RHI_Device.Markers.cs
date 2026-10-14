// Port of the markers of SpartanEngine RHI_Device.h + vulkan/Vulkan_Device.cpp @ 4c07baa: MarkerBegin/MarkerEnd
// (:3193-3210) and the function pointers they use (:197-198, loaded at :234-238).
// Differences:
// - GpuMarking stands in for cvar_debug_gpu_marking. Markers are no-ops when VK_EXT_debug_utils isn't enabled on the
//   instance (spartan calls the pointers without checking that they loaded, :3204 and :3209).
// - MarkerInsert (vkCmdInsertDebugUtilsLabelEXT) has no spartan counterpart.
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    // gpu markers on or off (spartan: cvar_debug_gpu_marking)
    public static bool GpuMarking { get; set; } = true;
    public static bool IsGpuMarkingEnabled => GpuMarking && RHI_Context.Gpu_Markers && RHI_Context.DebugUtils != null;

    public static void MarkerBegin(RHI_CommandList cmd_list, string name, System.Numerics.Vector4 color)
    {
        nint name_ptr = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsLabelEXT label = CreateLabel((byte*)name_ptr, color);
            RHI_Context.DebugUtils!.CmdBeginDebugUtilsLabel(cmd_list.GetRhiResource(), &label);
        }
        finally { SilkMarshal.Free(name_ptr); }
    }

    public static void MarkerEnd(RHI_CommandList cmd_list) => RHI_Context.DebugUtils!.CmdEndDebugUtilsLabel(cmd_list.GetRhiResource());

    public static void MarkerInsert(RHI_CommandList cmd_list, string name, System.Numerics.Vector4 color)
    {
        nint name_ptr = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsLabelEXT label = CreateLabel((byte*)name_ptr, color);
            RHI_Context.DebugUtils!.CmdInsertDebugUtilsLabel(cmd_list.GetRhiResource(), &label);
        }
        finally { SilkMarshal.Free(name_ptr); }
    }

    private static DebugUtilsLabelEXT CreateLabel(byte* name, System.Numerics.Vector4 color)
    {
        DebugUtilsLabelEXT label = new() { SType = StructureType.DebugUtilsLabelExt, PLabelName = name };
        label.Color[0] = color.X;
        label.Color[1] = color.Y;
        label.Color[2] = color.Z;
        label.Color[3] = color.W;
        return label;
    }
}
