using Phalanx.Rendering.RHI;
using Xunit;

namespace Phalanx.Tests;

// the device logs which gpu it picked
[Collection("gpu")]
public class RhiLogGpuTests
{
    [SkippableFact]
    public void Device_selection_is_logged_with_the_driver_and_api_versions()
    {
        Skip.IfNot(Phalanx.Rendering.RenderDevice.IsVulkanAvailable(out string reason), reason);
        var lines = new List<LogCmd>();
        void Handler(LogCmd cmd) { lock (lines) lines.Add(cmd); }
        Log.LogEntry += Handler;
        RHI_Device.Initialize(Array.Empty<string>(), validation: true);
        try
        {
            string expected = RHI_Device.DescribePhysicalDevice(RHI_Device.GetPrimaryPhysicalDevice());
            Assert.Matches(@"^.+ \(\d+ MB\), vendor \w+, driver .+, Vulkan \d+\.\d+\.\d+$", expected);
            lock (lines)
                Assert.Contains(lines, l => l.type == LogType.Info && l.text.EndsWith("RHI_Device.PhysicalDeviceSetPrimary: " + expected, StringComparison.Ordinal));
        }
        finally
        {
            Log.LogEntry -= Handler;
            RHI_Device.Destroy();
        }
    }
}
