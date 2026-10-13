// Port of SpartanEngine source/rhi/RHI_SyncPrimitive.h + vulkan/Vulkan_SyncPrimitive.cpp @ 4c07baa.
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_SyncPrimitive : IDisposable
{
    private readonly RHI_SyncPrimitive_Type m_type;
    private ulong m_value;
    private Fence m_fence;
    private Semaphore m_semaphore;

    public string Name { get; }
    public RHI_SyncPrimitive_Type Type => m_type;

    public RHI_SyncPrimitive(RHI_SyncPrimitive_Type type, string name)
    {
        m_type = type;
        Name = name;
        Vk vk = RHI_Context.Vk;

        if (type == RHI_SyncPrimitive_Type.Fence)
        {
            FenceCreateInfo info = new() { SType = StructureType.FenceCreateInfo };
            Check(vk.CreateFence(RHI_Context.Device, in info, null, out m_fence), "vkCreateFence");
            RHI_Device.SetResourceName(m_fence.Handle, RHI_Resource_Type.Fence, name);
        }
        else
        {
            SemaphoreTypeCreateInfo type_info = new()
            {
                SType         = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = type == RHI_SyncPrimitive_Type.SemaphoreTimeline ? SemaphoreType.Timeline : SemaphoreType.Binary,
                InitialValue  = 0
            };
            SemaphoreCreateInfo info = new() { SType = StructureType.SemaphoreCreateInfo, PNext = &type_info };
            Check(vk.CreateSemaphore(RHI_Context.Device, in info, null, out m_semaphore), "vkCreateSemaphore");
            RHI_Device.SetResourceName(m_semaphore.Handle, RHI_Resource_Type.Semaphore, name);
        }
    }

    public Fence GetFence() => m_fence;
    public Semaphore GetSemaphore() => m_semaphore;

    // timeline only
    public ulong GetNextSignalValue() => Interlocked.Increment(ref m_value);
    public ulong GetValue() => Interlocked.Read(ref m_value);

    public void Wait(ulong timeout_nanoseconds)
    {
        if (m_type == RHI_SyncPrimitive_Type.Fence)
        {
            Fence fence = m_fence;
            Check(RHI_Context.Vk.WaitForFences(RHI_Context.Device, 1, in fence, true, timeout_nanoseconds), "vkWaitForFences");
        }
        else
        {
            Wait(timeout_nanoseconds, GetValue());
        }
    }

    public void Wait(ulong timeout_nanoseconds, ulong value)
    {
        Semaphore semaphore = m_semaphore;
        SemaphoreWaitInfo info = new() { SType = StructureType.SemaphoreWaitInfo, SemaphoreCount = 1, PSemaphores = &semaphore, PValues = &value };
        Check(RHI_Context.Vk.WaitSemaphores(RHI_Context.Device, in info, timeout_nanoseconds), "vkWaitSemaphores");
    }

    public void Signal(ulong value)
    {
        SemaphoreSignalInfo info = new() { SType = StructureType.SemaphoreSignalInfo, Semaphore = m_semaphore, Value = value };
        Check(RHI_Context.Vk.SignalSemaphore(RHI_Context.Device, in info), "vkSignalSemaphore");
    }

    public bool IsSignaled()
    {
        if (m_type == RHI_SyncPrimitive_Type.Fence)
            return RHI_Context.Vk.GetFenceStatus(RHI_Context.Device, m_fence) == Result.Success;
        return IsSignaled(GetValue());
    }

    public bool IsSignaled(ulong value)
    {
        ulong current = 0;
        Check(RHI_Context.Vk.GetSemaphoreCounterValue(RHI_Context.Device, m_semaphore, &current), "vkGetSemaphoreCounterValue");
        return current >= value;
    }

    public void Reset()
    {
        if (m_type != RHI_SyncPrimitive_Type.Fence)
            return;
        Fence fence = m_fence;
        Check(RHI_Context.Vk.ResetFences(RHI_Context.Device, 1, in fence), "vkResetFences");
    }

    public void Dispose()
    {
        if (RHI_Context.Api == null) return;
        if (m_fence.Handle != 0)     { RHI_Context.Vk.DestroyFence(RHI_Context.Device, m_fence, null); m_fence = default; }
        if (m_semaphore.Handle != 0) { RHI_Context.Vk.DestroySemaphore(RHI_Context.Device, m_semaphore, null); m_semaphore = default; }
    }
}
