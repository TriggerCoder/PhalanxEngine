// Port of SpartanEngine source/rhi/RHI_Queue.h + vulkan/Vulkan_Queue.cpp @ 4c07baa (first slice).
// Differences: each command list has its own fence (spartan tracks submissions with timeline semaphores);
// the timeline path is kept in Submit for later steps.
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_Queue : IDisposable
{
    // spartan locks per queue type; here per vulkan queue, because queue types that share a family share one VkQueue
    private readonly object m_mutex;
    private readonly RHI_CommandList[] m_cmd_lists;
    private CommandPool m_rhi_resource;
    private uint m_index;

    public RHI_Queue_Type Type { get; }
    public uint FamilyIndex { get; }
    public Queue NativeQueue { get; }
    public string Name { get; }

    // spartan: command_list_count()
    public static int CommandListCount(RHI_Queue_Type type) => type switch
    {
        RHI_Queue_Type.Graphics => 16,
        RHI_Queue_Type.Compute  => 12,
        _                       => 4,
    };

    public RHI_Queue(RHI_Queue_Type type, uint family_index, Queue queue, object queue_mutex, string name)
    {
        m_mutex     = queue_mutex;
        Type        = type;
        FamilyIndex = family_index;
        NativeQueue = queue;
        Name        = name;

        CommandPoolCreateInfo pool_info = new()
        {
            SType            = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = family_index,
            Flags            = CommandPoolCreateFlags.ResetCommandBufferBit
        };
        Check(RHI_Context.Vk.CreateCommandPool(RHI_Context.Device, in pool_info, null, out m_rhi_resource), "vkCreateCommandPool");
        RHI_Device.SetResourceName(m_rhi_resource.Handle, RHI_Resource_Type.CommandPool, name);

        m_cmd_lists = new RHI_CommandList[CommandListCount(type)];
        for (int i = 0; i < m_cmd_lists.Length; i++)
            m_cmd_lists[i] = new RHI_CommandList(this, m_rhi_resource, $"{name}_cmd_list_{i}");
        m_index = (uint)m_cmd_lists.Length - 1;
    }

    // round robin, waits if the next list is still executing
    public RHI_CommandList NextCommandList()
    {
        m_index = (m_index + 1) % (uint)m_cmd_lists.Length;
        RHI_CommandList cmd_list = m_cmd_lists[m_index];
        cmd_list.WaitForExecution();
        return cmd_list;
    }

    public void Wait()
    {
        lock (m_mutex)
            Check(RHI_Context.Vk.QueueWaitIdle(NativeQueue), "vkQueueWaitIdle");
        foreach (RHI_CommandList cmd_list in m_cmd_lists)
            cmd_list.WaitForExecution();
    }

    public void Submit(RHI_CommandList cmd_list, RHI_SyncPrimitive? semaphore_wait, RHI_SyncPrimitive? semaphore_signal,
                       RHI_SyncPrimitive? semaphore_timeline_signal = null, ulong timeline_signal_value = 0)
    {
        SemaphoreSubmitInfo* waits   = stackalloc SemaphoreSubmitInfo[1];
        SemaphoreSubmitInfo* signals = stackalloc SemaphoreSubmitInfo[2];
        uint wait_count = 0, signal_count = 0;

        if (semaphore_wait != null)
            waits[wait_count++] = new SemaphoreSubmitInfo { SType = StructureType.SemaphoreSubmitInfo, Semaphore = semaphore_wait.GetSemaphore(), StageMask = PipelineStageFlags2.AllCommandsBit };
        if (semaphore_signal != null)
            signals[signal_count++] = new SemaphoreSubmitInfo { SType = StructureType.SemaphoreSubmitInfo, Semaphore = semaphore_signal.GetSemaphore(), StageMask = PipelineStageFlags2.AllCommandsBit };
        if (semaphore_timeline_signal != null)
            signals[signal_count++] = new SemaphoreSubmitInfo { SType = StructureType.SemaphoreSubmitInfo, Semaphore = semaphore_timeline_signal.GetSemaphore(), Value = timeline_signal_value, StageMask = PipelineStageFlags2.AllCommandsBit };

        CommandBufferSubmitInfo cmd_info = new() { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = cmd_list.GetRhiResource() };
        SubmitInfo2 submit = new()
        {
            SType                    = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount   = wait_count,
            PWaitSemaphoreInfos      = waits,
            CommandBufferInfoCount   = 1,
            PCommandBufferInfos      = &cmd_info,
            SignalSemaphoreInfoCount = signal_count,
            PSignalSemaphoreInfos    = signals
        };

        RHI_SyncPrimitive fence = cmd_list.GetFence();
        fence.Reset();
        lock (m_mutex)
            Check(RHI_Context.Vk.QueueSubmit2(NativeQueue, 1, in submit, fence.GetFence()), "vkQueueSubmit2");
        cmd_list.OnSubmitted();
    }

    // false when the swapchain is out of date and has to be recreated
    public bool Present(SwapchainKHR swapchain, uint image_index, RHI_SyncPrimitive semaphore_wait)
    {
        Semaphore wait = semaphore_wait.GetSemaphore();
        PresentInfoKHR info = new()
        {
            SType              = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores    = &wait,
            SwapchainCount     = 1,
            PSwapchains        = &swapchain,
            PImageIndices      = &image_index
        };
        Result result;
        lock (m_mutex)
            result = RHI_Context.Swapchain!.QueuePresent(NativeQueue, in info);
        if (result == Result.ErrorOutOfDateKhr)
            return false;
        if (result == Result.SuboptimalKhr)
            return true;
        Check(result, "vkQueuePresentKHR");
        return true;
    }

    public void Dispose()
    {
        foreach (RHI_CommandList cmd_list in m_cmd_lists)
            cmd_list.Dispose();
        if (m_rhi_resource.Handle != 0)
        {
            RHI_Context.Vk.DestroyCommandPool(RHI_Context.Device, m_rhi_resource, null);
            m_rhi_resource = default;
        }
    }
}
