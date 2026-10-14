// Port of SpartanEngine source/rhi/RHI_BlendState.h/.cpp @ 4c07baa
// Deviations: the blend factor is hashed by its bit pattern (spartan casts the float to uint64_t, RHI_BlendState.cpp:43,
// so 0.25 and 0.75 hash like 0), and SetBlendFactor rehashes (spartan's RHI_BlendState.h:39 keeps the old hash, so a
// pipeline cached with the old factor, which Vulkan_Pipeline.cpp:377-380 bakes in, keeps being used).
namespace Phalanx.Rendering.RHI;

internal sealed class RHI_BlendState
{
    private readonly bool m_blend_enabled;
    private readonly RHI_Blend m_source_blend;
    private readonly RHI_Blend m_dest_blend;
    private readonly RHI_Blend_Operation m_blend_op;
    private readonly RHI_Blend m_source_blend_alpha;
    private readonly RHI_Blend m_dest_blend_alpha;
    private readonly RHI_Blend_Operation m_blend_op_alpha;
    private float m_blend_factor;
    private ulong m_hash;

    public RHI_BlendState(
        bool blend_enabled                 = false,
        RHI_Blend source_blend             = RHI_Blend.Src_Alpha,
        RHI_Blend dest_blend               = RHI_Blend.Inv_Src_Alpha,
        RHI_Blend_Operation blend_op       = RHI_Blend_Operation.Add,
        RHI_Blend source_blend_alpha       = RHI_Blend.One,
        RHI_Blend dest_blend_alpha         = RHI_Blend.One,
        RHI_Blend_Operation blend_op_alpha = RHI_Blend_Operation.Add,
        float blend_factor                 = 0.0f)
    {
        m_blend_enabled      = blend_enabled;
        m_source_blend       = source_blend;
        m_dest_blend         = dest_blend;
        m_blend_op           = blend_op;
        m_source_blend_alpha = source_blend_alpha;
        m_dest_blend_alpha   = dest_blend_alpha;
        m_blend_op_alpha     = blend_op_alpha;
        m_blend_factor       = blend_factor;
        ComputeHash();
    }

    public bool GetBlendEnabled() => m_blend_enabled;
    public RHI_Blend GetSourceBlend() => m_source_blend;
    public RHI_Blend GetDestBlend() => m_dest_blend;
    public RHI_Blend_Operation GetBlendOp() => m_blend_op;
    public RHI_Blend GetSourceBlendAlpha() => m_source_blend_alpha;
    public RHI_Blend GetDestBlendAlpha() => m_dest_blend_alpha;
    public RHI_Blend_Operation GetBlendOpAlpha() => m_blend_op_alpha;
    public float GetBlendFactor() => m_blend_factor;
    public void SetBlendFactor(float blend_factor) { m_blend_factor = blend_factor; ComputeHash(); }
    public ulong GetHash() => m_hash;

    private void ComputeHash()
    {
        ulong hash = 0;
        hash = RHI_Definitions.rhi_hash_combine(hash, m_blend_enabled ? 1UL : 0UL);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_source_blend);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_dest_blend);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_blend_op);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_source_blend_alpha);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_dest_blend_alpha);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_blend_op_alpha);
        hash = RHI_Definitions.rhi_hash_combine(hash, BitConverter.SingleToUInt32Bits(m_blend_factor));
        m_hash = hash;
    }
}
