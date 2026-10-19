// What a view carries from one frame to the next: the taau history and the target it swaps with, the depth and its
// previous frame, the screen space shadows and the view output, plus the flags that say when they start over.
// Spartan keeps all of these in the renderer's global target array and m_pass_state (Renderer_Internal.h:340), one set
// for every view; here each view owns one, so a reset of one view leaves the others alone.
using Phalanx.World;
using Texture      = Phalanx.Rendering.RHI.RHI_Texture;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;

namespace Phalanx.Rendering;

internal sealed class ViewHistory
{
    private readonly Texture?[] m_targets = new Texture?[(int)RenderTarget.max];

    // the first taau frame has no history (Renderer_Internal.h:340)
    public bool TaauResetPending = true;
    // m_pass_state.depth_history_cleared: the depth history starts at the clear value
    public bool DepthHistoryCleared;
    // the screen space shadow target holds garbage until cleared, and is cleared to lit again when no light uses it
    public bool SssNeedsClear = true;
    public bool SssWritten;
    // NotifyCameraCut, and the camera the last frame rendered from (a change cuts)
    public bool CameraCutPending;
    public Camera? CameraPrevious;

    public static bool IsHistoryTarget(RenderTarget type) => type is
        RenderTarget.gbuffer_depth or RenderTarget.gbuffer_depth_previous or RenderTarget.sss or
        RenderTarget.frame_output or RenderTarget.frame_output_2 or RenderTarget.taau_history;

    public Texture? Get(RenderTarget type) => m_targets[(int)type];
    public void Set(RenderTarget type, Texture? texture) => m_targets[(int)type] = texture;

    // ping-pong: the depth written last frame becomes the previous depth, the new taau history is protected from
    // post-processing and the old one becomes scratch
    public void Swap(RenderTarget a, RenderTarget b)
    {
        if (m_targets[(int)a] != null && m_targets[(int)b] != null)
            (m_targets[(int)a], m_targets[(int)b]) = (m_targets[(int)b], m_targets[(int)a]);
    }
}
