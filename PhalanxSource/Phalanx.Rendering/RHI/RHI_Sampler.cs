// Port of SpartanEngine source/rhi/RHI_Sampler.h/.cpp + vulkan/Vulkan_Sampler.cpp @ 4c07baa
// Deviations: GetHash()/ComputeHash() (spartan has no sampler hash, phalanx uses it to dedupe the renderer's samplers
// and as a pipeline-state input later), maxAnisotropy is clamped to the device limit (spartan passes the value through,
// above maxSamplerAnisotropy it is a validation error), Dispose() instead of the destructor.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_Sampler : IDisposable
{
    private readonly RHI_Filter m_filter_min;
    private readonly RHI_Filter m_filter_mag;
    private readonly RHI_Filter m_filter_mipmap;
    private readonly RHI_Sampler_Address_Mode m_sampler_address_mode;
    private readonly RHI_Comparison_Function m_comparison_function;
    private readonly float m_anisotropy;
    private readonly bool m_comparison_enabled;
    private readonly float m_mip_lod_bias;
    private Sampler m_rhi_resource;

    public RHI_Sampler(
        RHI_Filter filter_min                         = RHI_Filter.Nearest,
        RHI_Filter filter_mag                         = RHI_Filter.Nearest,
        RHI_Filter filter_mipmap                      = RHI_Filter.Nearest,
        RHI_Sampler_Address_Mode sampler_address_mode = RHI_Sampler_Address_Mode.Wrap,
        RHI_Comparison_Function comparison_function   = RHI_Comparison_Function.Never,
        float anisotropy                              = 0.0f,
        bool comparison_enabled                       = false,
        float mip_bias                                = 0.0f,
        string name                                   = "sampler")
    {
        m_filter_min           = filter_min;
        m_filter_mag           = filter_mag;
        m_filter_mipmap        = filter_mipmap;
        m_sampler_address_mode = sampler_address_mode;
        m_comparison_function  = comparison_function;
        m_anisotropy           = anisotropy;
        m_comparison_enabled   = comparison_enabled;
        m_mip_lod_bias         = mip_bias;
        Name                   = name;

        CreateResource();
    }

    public string Name { get; }
    public RHI_Filter GetFilterMin() => m_filter_min;
    public RHI_Filter GetFilterMag() => m_filter_mag;
    public RHI_Filter GetFilterMipmap() => m_filter_mipmap;
    public RHI_Sampler_Address_Mode GetAddressMode() => m_sampler_address_mode;
    public RHI_Comparison_Function GetComparisonFunction() => m_comparison_function;
    public bool GetAnisotropyEnabled() => m_anisotropy != 0;
    public bool GetComparisonEnabled() => m_comparison_enabled;
    public float GetMipLodBias() => m_mip_lod_bias;
    public Sampler GetRhiResource() => m_rhi_resource;
    public ulong GetHash() => ComputeHash(m_filter_min, m_filter_mag, m_filter_mipmap, m_sampler_address_mode, m_comparison_function, m_anisotropy, m_comparison_enabled, m_mip_lod_bias);

    // every state that reaches VkSamplerCreateInfo, floats by bit pattern (so -0.0 and 0.0 differ, nan is stable)
    public static ulong ComputeHash(RHI_Filter filter_min, RHI_Filter filter_mag, RHI_Filter filter_mipmap, RHI_Sampler_Address_Mode address_mode,
                                    RHI_Comparison_Function comparison_function, float anisotropy, bool comparison_enabled, float mip_bias)
    {
        ulong hash = 0;
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)filter_min);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)filter_mag);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)filter_mipmap);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)address_mode);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)comparison_function);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)BitConverter.SingleToUInt32Bits(anisotropy));
        hash = RHI_Definitions.rhi_hash_combine(hash, comparison_enabled ? 1UL : 0UL);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)BitConverter.SingleToUInt32Bits(mip_bias));
        return hash;
    }

    // vulkan/Vulkan_Sampler.cpp, pure so the gpu-free tests can check it
    public SamplerCreateInfo GetCreateInfo(float max_device_anisotropy) => BuildCreateInfo(
        m_filter_min, m_filter_mag, m_filter_mipmap, m_sampler_address_mode, m_comparison_function, m_anisotropy, m_comparison_enabled, m_mip_lod_bias, max_device_anisotropy);

    public static SamplerCreateInfo BuildCreateInfo(RHI_Filter filter_min, RHI_Filter filter_mag, RHI_Filter filter_mipmap, RHI_Sampler_Address_Mode address_mode,
                                                    RHI_Comparison_Function comparison_function, float anisotropy, bool comparison_enabled, float mip_bias,
                                                    float max_device_anisotropy)
    {
        SamplerAddressMode address = VulkanSamplerAddressMode[(int)address_mode];
        return new SamplerCreateInfo
        {
            SType            = StructureType.SamplerCreateInfo,
            MagFilter        = VulkanFilter[(int)filter_mag],
            MinFilter        = VulkanFilter[(int)filter_min],
            MipmapMode       = VulkanMipmapMode[(int)filter_mipmap],
            AddressModeU     = address,
            AddressModeV     = address,
            AddressModeW     = address,
            AnisotropyEnable = anisotropy != 0,
            MaxAnisotropy    = max_device_anisotropy > 0 ? System.Math.Min(anisotropy, max_device_anisotropy) : anisotropy, // deviation: clamp
            CompareEnable    = comparison_enabled,
            CompareOp        = VulkanCompareOperator[(int)comparison_function],
            BorderColor      = BorderColor.IntTransparentBlack,
            MipLodBias       = mip_bias,
            MinLod           = 0.0f,
            MaxLod           = float.MaxValue
        };
    }

    private void CreateResource()
    {
        SamplerCreateInfo info = GetCreateInfo(RHI_Device.PropertyMaxSamplerAnisotropy);
        Check(RHI_Context.Vk.CreateSampler(RHI_Context.Device, in info, null, out m_rhi_resource), "vkCreateSampler");
        RHI_Device.SetResourceName(m_rhi_resource.Handle, RHI_Resource_Type.Sampler, Name);
    }

    public void Dispose()
    {
        if (m_rhi_resource.Handle == 0) return;
        RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Sampler, m_rhi_resource.Handle);
        m_rhi_resource = default;
    }
}
