// Port of SpartanEngine source/rhi/RHI_DepthStencilState.h/.cpp @ 4c07baa
// Deviations: the stencil read/write masks are hashed too (spartan leaves them out, they are constant 1 there).
namespace Phalanx.Rendering.RHI;

internal sealed class RHI_DepthStencilState
{
    private readonly bool m_depth_test_enabled;
    private readonly bool m_depth_write_enabled;
    private readonly RHI_Comparison_Function m_depth_comparison_function;
    private readonly bool m_stencil_test_enabled;
    private readonly bool m_stencil_write_enabled;
    private readonly RHI_Comparison_Function m_stencil_comparison_function;
    private readonly RHI_Stencil_Operation m_stencil_fail_op;
    private readonly RHI_Stencil_Operation m_stencil_depth_fail_op;
    private readonly RHI_Stencil_Operation m_stencil_pass_op;
    private readonly byte m_stencil_read_mask  = 1;
    private readonly byte m_stencil_write_mask = 1;
    private readonly ulong m_hash;

    public RHI_DepthStencilState(
        bool depth_test                                     = true,
        bool depth_write                                    = true,
        RHI_Comparison_Function depth_comparison_function   = RHI_Comparison_Function.LessEqual,
        bool stencil_test                                   = false,
        bool stencil_write                                  = false,
        RHI_Comparison_Function stencil_comparison_function = RHI_Comparison_Function.Never,
        RHI_Stencil_Operation stencil_fail_op               = RHI_Stencil_Operation.Zero,
        RHI_Stencil_Operation stencil_depth_fail_op         = RHI_Stencil_Operation.Zero,
        RHI_Stencil_Operation stencil_pass_op               = RHI_Stencil_Operation.Zero)
    {
        m_depth_test_enabled          = depth_test;
        m_depth_write_enabled         = depth_write;
        m_depth_comparison_function   = depth_comparison_function;
        m_stencil_test_enabled        = stencil_test;
        m_stencil_write_enabled       = stencil_write;
        m_stencil_comparison_function = stencil_comparison_function;
        m_stencil_fail_op             = stencil_fail_op;
        m_stencil_depth_fail_op       = stencil_depth_fail_op;
        m_stencil_pass_op             = stencil_pass_op;

        ulong hash = 0;
        hash = RHI_Definitions.rhi_hash_combine(hash, m_depth_test_enabled ? 1UL : 0UL);
        hash = RHI_Definitions.rhi_hash_combine(hash, m_depth_write_enabled ? 1UL : 0UL);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_depth_comparison_function);
        hash = RHI_Definitions.rhi_hash_combine(hash, m_stencil_test_enabled ? 1UL : 0UL);
        hash = RHI_Definitions.rhi_hash_combine(hash, m_stencil_write_enabled ? 1UL : 0UL);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_stencil_comparison_function);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_stencil_fail_op);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_stencil_depth_fail_op);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)m_stencil_pass_op);
        hash = RHI_Definitions.rhi_hash_combine(hash, m_stencil_read_mask);
        hash = RHI_Definitions.rhi_hash_combine(hash, m_stencil_write_mask);
        m_hash = hash;
    }

    public bool GetDepthTestEnabled() => m_depth_test_enabled;
    public bool GetDepthWriteEnabled() => m_depth_write_enabled;
    public bool GetStencilTestEnabled() => m_stencil_test_enabled;
    public bool GetStencilWriteEnabled() => m_stencil_write_enabled;
    public RHI_Comparison_Function GetDepthComparisonFunction() => m_depth_comparison_function;
    public RHI_Comparison_Function GetStencilComparisonFunction() => m_stencil_comparison_function;
    public RHI_Stencil_Operation GetStencilFailOperation() => m_stencil_fail_op;
    public RHI_Stencil_Operation GetStencilDepthFailOperation() => m_stencil_depth_fail_op;
    public RHI_Stencil_Operation GetStencilPassOperation() => m_stencil_pass_op;
    public byte GetStencilReadMask() => m_stencil_read_mask;
    public byte GetStencilWriteMask() => m_stencil_write_mask;
    public ulong GetHash() => m_hash;
}
