// Port of SpartanEngine source/rhi/RHI_RasterizerState.h/.cpp @ 4c07baa
// Deviations: every float is hashed by its bit pattern. Spartan hashes the line width twice, once truncated to an
// integer (RHI_RasterizerState.cpp:38) and once through std::hash (:42); the result is the same set of states.
namespace Phalanx.Rendering.RHI;

internal sealed class RHI_RasterizerState
{
    private readonly RHI_PolygonMode m_polygon_mode = RHI_PolygonMode.Max;
    private readonly bool m_depth_clip_enabled;
    private readonly float m_depth_bias;
    private readonly float m_depth_bias_clamp;
    private readonly float m_depth_bias_slope_scaled;
    private readonly float m_line_width = 1.0f;
    private readonly ulong m_hash;

    public RHI_RasterizerState(
        RHI_PolygonMode polygon_mode,
        bool depth_clip_enabled,
        float depth_bias              = 0.0f,
        float depth_bias_clamp        = 0.0f,
        float depth_bias_slope_scaled = 0.0f,
        float line_width              = 1.0f)
    {
        m_polygon_mode            = polygon_mode;
        m_depth_clip_enabled      = depth_clip_enabled;
        m_depth_bias              = depth_bias;
        m_depth_bias_clamp        = depth_bias_clamp;
        m_depth_bias_slope_scaled = depth_bias_slope_scaled;
        m_line_width              = line_width;

        ulong hash = 0;
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_polygon_mode);
        hash = RHI_Definitions.rhi_hash_combine(hash, m_depth_clip_enabled ? 1UL : 0UL);
        hash = RHI_Definitions.rhi_hash_combine(hash, BitConverter.SingleToUInt32Bits(m_depth_bias));
        hash = RHI_Definitions.rhi_hash_combine(hash, BitConverter.SingleToUInt32Bits(m_depth_bias_clamp));
        hash = RHI_Definitions.rhi_hash_combine(hash, BitConverter.SingleToUInt32Bits(m_depth_bias_slope_scaled));
        hash = RHI_Definitions.rhi_hash_combine(hash, BitConverter.SingleToUInt32Bits(m_line_width));
        m_hash = hash;
    }

    public RHI_PolygonMode GetPolygonMode() => m_polygon_mode;
    public bool GetDepthClipEnabled() => m_depth_clip_enabled;
    public float GetLineWidth() => m_line_width;
    public float GetDepthBias() => m_depth_bias;
    public float GetDepthBiasClamp() => m_depth_bias_clamp;
    public float GetDepthBiasSlopeScaled() => m_depth_bias_slope_scaled;
    public ulong GetHash() => m_hash;
}
