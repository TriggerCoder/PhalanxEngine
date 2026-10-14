// Port of the rendering half of SpartanEngine vulkan/Vulkan_CommandList.cpp @ 4c07baa: set_pipeline_state (:1492-1603),
// RenderPassBegin (:1664-1815), render_pass_end (:1822-1850), draw (:1905), draw_indexed (:1921), set_viewport (:2615),
// set_scissor_rectangle (:2649), set_cull_mode (:2677), set_buffer_vertex (:2692), set_buffer_index (:2732) and
// PreDraw (:3370-3422), with the state reset of Begin (:1381-1399).
// Deviations:
// - No resource hazard tracking yet (spartan: SynchronizeResources/SynchronizeRenderTargets and the pending barrier
//   list). Barriers are recorded when they are inserted: a barrier while a render pass is active ends the pass and the
//   next draw resumes it with load ops, and ending a pass inserts a full memory barrier so whatever reads the targets
//   next sees the writes.
// - set_buffer_vertex binds the vertex buffer only (spartan also binds an instance buffer, falling back to a dummy, at
//   binding 1, which no pipeline declares since instance data is pulled from a storage buffer).
// - set_buffer_index uses vkCmdBindIndexBuffer (spartan: vkCmdBindIndexBuffer2, a 1.4 entry point). 16 bit indices
//   are detected from the stride like spartan.
// - Buffers are compared by reference instead of object id, there are no profiler counters and no vrs, multiview
//   render target views or InvokePipelineBound (use_standard_resources) yet.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe partial class RHI_CommandList
{
    private RHI_PipelineState? m_pso;
    private bool m_pipeline_state_dirty;
    private bool m_render_pass_active;
    private bool m_render_pass_pending;
    private bool m_load_depth_render_target;
    private readonly bool[] m_load_color_render_targets = new bool[RHI_Definitions.rhi_max_render_target_count];
    private readonly Dictionary<RHI_DescriptorSetLayout, RHI_DescriptorSetLayout> m_descriptor_layouts_local = new();
    private PipelineLayout m_bindless_pipeline_layout;
    private PipelineBindPoint m_bindless_bind_point;
    private RHI_CullMode m_cull_mode = RHI_CullMode.Max;
    private RHI_Viewport? m_viewport;
    private Rect2D? m_scissor;
    private RHI_Buffer? m_buffer_vertex;
    private RHI_Buffer? m_buffer_index;

    public bool IsRenderPassActive() => m_render_pass_active;
    public RHI_PipelineState? GetPipelineState() => m_pso;

    // Begin resets everything a new command buffer doesn't inherit
    private void ResetRenderState()
    {
        m_pso                      = null;
        m_pipeline_state_dirty     = false;
        m_render_pass_active       = false;
        m_render_pass_pending      = false;
        m_load_depth_render_target = false;
        Array.Clear(m_load_color_render_targets);
        m_bindless_pipeline_layout = default;
        m_cull_mode                = RHI_CullMode.Max;
        m_viewport                 = null;
        m_scissor                  = null;
        m_buffer_vertex            = null;
        m_buffer_index             = null;
    }

    public void SetPipelineState(RHI_PipelineState pso)
    {
        RequireRecording();

        // early exit if the pipeline state hasn't changed
        pso.Prepare();
        if (!m_pipeline_state_dirty && m_pso != null && m_pso.GetHash() == pso.GetHash() && m_pso.use_standard_resources == pso.use_standard_resources &&
            SameTargets(m_pso, pso))
            return;
        RenderPassEnd();

        // determine if the new render pass should clear the render targets or not
        RHI_Shader? vertex_old = m_pso?.GetShader(RHI_Shader_Type.Vertex);
        if (m_pso != null && vertex_old != null && vertex_old == pso.GetShader(RHI_Shader_Type.Vertex) && m_pso.render_target_array_index == pso.render_target_array_index)
        {
            m_load_depth_render_target = pso.render_target_depth_texture == m_pso.render_target_depth_texture;
            for (int i = 0; i < RHI_Definitions.rhi_max_render_target_count; i++)
                m_load_color_render_targets[i] = pso.render_target_color_textures[i] == m_pso.render_target_color_textures[i];
        }
        else
        {
            m_load_depth_render_target = false;
            Array.Clear(m_load_color_render_targets);
        }

        // get (or create) a pipeline which matches the requested pipeline state
        m_pso = pso.Clone();
        BindPipeline(RHI_Device.GetOrCreatePipeline(m_pso));
        m_render_pass_pending = m_pso.IsGraphics();

        // set some dynamic states
        if (m_pso.IsGraphics())
        {
            SetCullMode(m_pso.rasterizer_state!.GetPolygonMode() == RHI_PolygonMode.Wireframe ? RHI_CullMode.None : RHI_CullMode.Back);
            SetScissorRectangle(new Phalanx.Math.Rectangle(0, 0, m_pso.GetWidth(), m_pso.GetHeight()));
            SetViewport(new RHI_Viewport(0, 0, m_pso.GetWidth(), m_pso.GetHeight()));
        }
        m_pipeline_state_dirty = false;
    }

    // the hash only has target formats, a different target of the same format still needs a new render pass (spartan's
    // public SetPipelineState marks the state dirty, RHI_CommandList.cpp:1565-1570, so its early exit never sees this)
    private static bool SameTargets(RHI_PipelineState a, RHI_PipelineState b)
    {
        if (a.render_target_swapchain != b.render_target_swapchain || a.render_target_depth_texture != b.render_target_depth_texture)
            return false;
        for (int i = 0; i < RHI_Definitions.rhi_max_render_target_count; i++)
            if (a.render_target_color_textures[i] != b.render_target_color_textures[i])
                return false;
        return true;
    }

    // binds the pipeline and the bindless sets, the per-command-list copy of its descriptor layout becomes current
    private void BindPipeline(RHI_Pipeline pipeline)
    {
        m_pipeline = pipeline;
        RHI_DescriptorSetLayout shared = pipeline.DescriptorSetLayout;
        if (!m_descriptor_layouts_local.TryGetValue(shared, out RHI_DescriptorSetLayout? local))
        {
            local = new RHI_DescriptorSetLayout(shared);
            m_descriptor_layouts_local[shared] = local;
        }
        m_descriptor_layout_current = local;
        m_descriptor_layout_current.ClearBindings();

        RHI_Context.Vk.CmdBindPipeline(m_rhi_resource, pipeline.GetBindPoint(), pipeline.GetRhiResource());

        // set bindless descriptors
        PipelineLayout pipeline_layout = pipeline.GetRhiResourceLayout();
        if (m_bindless_pipeline_layout.Handle != pipeline_layout.Handle || m_bindless_bind_point != pipeline.GetBindPoint())
        {
            SetBindless(pipeline);
            m_bindless_pipeline_layout = pipeline_layout;
            m_bindless_bind_point      = pipeline.GetBindPoint();
        }

        // binding the upper sets with an incompatible layout disturbs set zero
        m_dynamic_descriptor_set  = default;
        m_dynamic_pipeline_layout = default;
        m_dynamic_offset_count    = 0;
    }

    public void RenderPassBegin()
    {
        RequireRecording();
        if (m_render_pass_active)
            throw new InvalidOperationException($"{Name}: a render pass is already active");
        m_render_pass_pending = false;

        if (m_pso == null || !m_pso.IsGraphics())
            return;

        RenderingAttachmentInfo* attachments_color = stackalloc RenderingAttachmentInfo[RHI_Definitions.rhi_max_render_target_count];
        uint attachment_index = 0;

        // swapchain buffer as a render target
        if (m_pso.render_target_swapchain is { } swapchain)
        {
            // transition to the appropriate layout
            InsertBarrier(swapchain, ImageLayout.General);
            attachments_color[attachment_index++] = new RenderingAttachmentInfo
            {
                SType       = StructureType.RenderingAttachmentInfo,
                ImageView   = swapchain.GetRhiRtv(),
                ImageLayout = ImageLayout.General,
                LoadOp      = m_load_color_render_targets[0] ? AttachmentLoadOp.Load : GetColorLoadOp(m_pso.clear_color[0]),
                StoreOp     = AttachmentStoreOp.Store,
                ClearValue  = ToClearValue(m_pso.clear_color[0])
            };
        }
        else // regular render target(s)
        {
            for (int i = 0; i < RHI_Definitions.rhi_max_render_target_count; i++)
            {
                RHI_Texture? rt = m_pso.render_target_color_textures[i];
                if (rt == null)
                    break;
                if (!rt.IsRtv())
                    throw new InvalidOperationException($"{rt.Name}: the texture wasn't created with the RHI_Texture_Rtv flag and/or isn't a color format");

                // transition to the appropriate layout
                InsertBarrier(rt, RHI_Image_Layout.General);
                attachments_color[attachment_index++] = new RenderingAttachmentInfo
                {
                    SType       = StructureType.RenderingAttachmentInfo,
                    ImageView   = rt.GetRhiRtv(m_pso.render_target_array_index),
                    ImageLayout = ImageLayout.General,
                    LoadOp      = m_load_color_render_targets[i] ? AttachmentLoadOp.Load : GetColorLoadOp(m_pso.clear_color[i]),
                    StoreOp     = AttachmentStoreOp.Store,
                    ClearValue  = ToClearValue(m_pso.clear_color[i])
                };
            }
        }

        RenderingInfo rendering_info = new()
        {
            SType                = StructureType.RenderingInfo,
            RenderArea           = new Rect2D(new Offset2D(0, 0), new Extent2D(m_pso.GetWidth(), m_pso.GetHeight())),
            LayerCount           = 1,
            ViewMask             = m_pso.is_multiview ? 0b11u : 0,
            ColorAttachmentCount = attachment_index,
            PColorAttachments    = attachments_color
        };

        // depth-stencil attachment
        RenderingAttachmentInfo attachment_depth_stencil = default;
        if (m_pso.render_target_depth_texture is { } depth)
        {
            if (depth.GetWidth() != rendering_info.RenderArea.Extent.Width)
                throw new InvalidOperationException($"{depth.Name}: the depth buffer doesn't match the output resolution");
            if (!depth.IsDsv())
                throw new InvalidOperationException($"{depth.Name}: the texture isn't a depth stencil render target");

            // transition to the appropriate layout
            InsertBarrier(depth, RHI_Image_Layout.General);
            attachment_depth_stencil = new RenderingAttachmentInfo
            {
                SType       = StructureType.RenderingAttachmentInfo,
                ImageView   = depth.GetRhiDsv(m_pso.render_target_array_index),
                ImageLayout = ImageLayout.General,
                LoadOp      = m_load_depth_render_target ? AttachmentLoadOp.Load : GetDepthLoadOp(m_pso.clear_depth),
                StoreOp     = AttachmentStoreOp.Store,
                ClearValue  = new ClearValue(depthStencil: new ClearDepthStencilValue(m_pso.clear_depth, m_pso.clear_stencil))
            };
            rendering_info.PDepthAttachment = &attachment_depth_stencil;

            // we are using the combined depth-stencil approach
            // this means we can assign the depth attachment as the stencil attachment
            if (depth.IsStencilFormat())
                rendering_info.PStencilAttachment = rendering_info.PDepthAttachment;
        }

        // begin dynamic render pass
        RHI_Context.Vk.CmdBeginRendering(m_rhi_resource, in rendering_info);

        m_load_depth_render_target = m_pso.render_target_depth_texture != null;
        for (int i = 0; i < RHI_Definitions.rhi_max_render_target_count; i++)
        {
            // a resumed pass must preserve the swapchain image just like an offscreen target
            m_load_color_render_targets[i] = m_pso.render_target_color_textures[i] != null || (i == 0 && m_pso.render_target_swapchain != null);
        }
        m_render_pass_active = true;
    }

    public void RenderPassEnd()
    {
        // a pass that only clears still has to run
        if (m_render_pass_pending && m_pso != null)
        {
            bool clear_pending = m_pso.render_target_depth_texture != null && !m_load_depth_render_target &&
                                 m_pso.clear_depth != RHI_Definitions.rhi_depth_load && m_pso.clear_depth != RHI_Definitions.rhi_depth_dont_care;
            for (int i = 0; i < RHI_Definitions.rhi_max_render_target_count && !clear_pending; i++)
            {
                bool has_color_target = m_pso.render_target_color_textures[i] != null || (i == 0 && m_pso.render_target_swapchain != null);
                clear_pending = has_color_target && !m_load_color_render_targets[i] &&
                                m_pso.clear_color[i] != RHI_Definitions.rhi_color_load && m_pso.clear_color[i] != RHI_Definitions.rhi_color_dont_care;
            }

            if (clear_pending)
                RenderPassBegin();
            else
                m_render_pass_pending = false;
        }

        if (!m_render_pass_active)
            return;

        RHI_Context.Vk.CmdEndRendering(m_rhi_resource);
        m_render_pass_active = false;

        // see the header: no hazard tracking yet, so the attachment writes are made visible to everything after
        InsertBarrierMemory();
    }

    // a barrier can't be recorded inside dynamic rendering, so the pass is suspended and resumed by the next draw
    private void SuspendRenderPass()
    {
        if (!m_render_pass_active)
            return;
        RHI_Context.Vk.CmdEndRendering(m_rhi_resource);
        m_render_pass_active  = false;
        m_render_pass_pending = true;
    }

    public void Draw(uint vertex_count, uint vertex_start_index = 0)
    {
        RequireRecording();
        PreDraw();
        RHI_Context.Vk.CmdDraw(m_rhi_resource, vertex_count, 1, vertex_start_index, 0);
    }

    public void DrawIndexed(uint index_count, uint index_offset = 0, int vertex_offset = 0, uint instance_index = 0, uint instance_count = 1)
    {
        RequireRecording();
        PreDraw();
        RHI_Context.Vk.CmdDrawIndexed(m_rhi_resource, index_count, instance_count, index_offset, vertex_offset, instance_index);
    }

    public void SetViewport(in RHI_Viewport viewport)
    {
        RequireRecording();
        if (viewport.width == 0 || viewport.height == 0)
            throw new ArgumentException("The viewport has no size", nameof(viewport));
        if (m_viewport == viewport)
            return;
        m_viewport = viewport;

        Viewport vk_viewport = new(viewport.x, viewport.y, viewport.width, viewport.height, viewport.depth_min, viewport.depth_max);
        RHI_Context.Vk.CmdSetViewport(m_rhi_resource, 0, 1, in vk_viewport);
    }

    public void SetScissorRectangle(in Phalanx.Math.Rectangle scissor_rectangle)
    {
        RequireRecording();
        Rect2D vk_scissor = new(new Offset2D((int)scissor_rectangle.X, (int)scissor_rectangle.Y),
                                new Extent2D((uint)scissor_rectangle.Width, (uint)scissor_rectangle.Height));
        if (m_scissor is { } current && current.Offset.X == vk_scissor.Offset.X && current.Offset.Y == vk_scissor.Offset.Y &&
            current.Extent.Width == vk_scissor.Extent.Width && current.Extent.Height == vk_scissor.Extent.Height)
            return;
        m_scissor = vk_scissor;
        RHI_Context.Vk.CmdSetScissor(m_rhi_resource, 0, 1, in vk_scissor);
    }

    public void SetCullMode(RHI_CullMode cull_mode)
    {
        RequireRecording();
        if (m_cull_mode == cull_mode)
            return;
        m_cull_mode = cull_mode;
        RHI_Context.Vk.CmdSetCullMode(m_rhi_resource, VulkanCullMode[(int)m_cull_mode]);
    }

    public void SetBufferVertex(RHI_Buffer vertex)
    {
        RequireRecording();
        if (vertex.GetRhiResource().Handle == 0)
            throw new ArgumentException($"{vertex.Name}: the buffer has no resource", nameof(vertex));
        if (m_buffer_vertex == vertex)
            return;

        Silk.NET.Vulkan.Buffer buffer = vertex.GetRhiResource();
        ulong offset = 0;
        RHI_Context.Vk.CmdBindVertexBuffers(m_rhi_resource, 0, 1, &buffer, &offset);
        m_buffer_vertex = vertex;
    }

    public void SetBufferIndex(RHI_Buffer buffer)
    {
        RequireRecording();
        if (buffer.GetRhiResource().Handle == 0)
            throw new ArgumentException($"{buffer.Name}: the buffer has no resource", nameof(buffer));
        if (m_buffer_index == buffer)
            return;

        bool is_16bit = buffer.GetStride() == sizeof(ushort);
        RHI_Context.Vk.CmdBindIndexBuffer(m_rhi_resource, buffer.GetRhiResource(), 0, is_16bit ? IndexType.Uint16 : IndexType.Uint32);
        m_buffer_index = buffer;
    }

    private void PreDraw()
    {
        if (m_pipeline == null || m_pso == null)
            throw new InvalidOperationException($"{Name}: draw without a pipeline state");
        if (!m_pso.IsGraphics())
            throw new InvalidOperationException($"{Name}: draw with a compute pipeline state");

        if (!m_render_pass_active)
            RenderPassBegin();

        SetDynamic(m_pipeline);
    }

    private static AttachmentLoadOp GetColorLoadOp(Phalanx.Math.Color color)
    {
        if (color == RHI_Definitions.rhi_color_dont_care)
            return AttachmentLoadOp.DontCare;
        if (color == RHI_Definitions.rhi_color_load)
            return AttachmentLoadOp.Load;
        return AttachmentLoadOp.Clear;
    }

    private static AttachmentLoadOp GetDepthLoadOp(float depth)
    {
        if (depth == RHI_Definitions.rhi_depth_dont_care)
            return AttachmentLoadOp.DontCare;
        if (depth == RHI_Definitions.rhi_depth_load)
            return AttachmentLoadOp.Load;
        return AttachmentLoadOp.Clear;
    }

    private static ClearValue ToClearValue(Phalanx.Math.Color color) => new(new ClearColorValue(color.R, color.G, color.B, color.A));
}
