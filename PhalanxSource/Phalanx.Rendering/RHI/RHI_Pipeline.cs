// Port of SpartanEngine source/rhi/RHI_Pipeline.h + vulkan/Vulkan_Pipeline.cpp @ 4c07baa: the pipeline layout (set 0 =
// the merged descriptor set layout, sets 1..10 = the bindless layouts, push constant ranges from the reflected push
// constant blocks, :85-181), compute pipelines (:183-192) and graphics pipelines with dynamic rendering (:193-487).
// Deviations:
// - The set count follows RHI_Device.GetPipelineSetCount (maxBoundDescriptorSets, see RHI_Device.Descriptors.cs).
// - No ray tracing or mesh shader pipelines, no tessellation state contents (no hull/domain stages), no variable rate
//   shading (no vrs input texture).
// - The blend attachments follow the color formats: the swapchain alone, or the render targets up to the first empty
//   slot. Spartan adds the swapchain and every non-null render target to the blend attachments (:357-369) while the
//   formats stop at the first null and skip the render targets when there is a swapchain (:415-431), so the two
//   counts disagree for a gap in the targets or a swapchain plus a render target.
// - The line width is clamped to the device's lineWidthRange.
// - RHI_Pipeline(RHI_Shader, string) is a shortcut for a compute pipeline state.
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_Pipeline : IDisposable
{
    private Pipeline m_rhi_resource;
    private PipelineLayout m_rhi_resource_layout;
    private ShaderStageFlags m_push_constant_stages;
    private uint m_push_constant_size;
    private readonly RHI_PipelineState m_state;

    public string Name { get; }
    public RHI_DescriptorSetLayout DescriptorSetLayout { get; }
    public uint SetCount { get; }
    public bool IsCompute => m_state.IsCompute();
    public RHI_PipelineState GetState() => m_state;
    public PipelineBindPoint GetBindPoint() => m_state.IsGraphics() ? PipelineBindPoint.Graphics : PipelineBindPoint.Compute;

    public RHI_Pipeline(RHI_Shader compute_shader, string name) : this(ComputeState(compute_shader, name), null) { }

    private static RHI_PipelineState ComputeState(RHI_Shader compute_shader, string name)
    {
        if (compute_shader.GetShaderStage() != RHI_Shader_Type.Compute)
            throw new ArgumentException("Expected a compute shader", nameof(compute_shader));
        var pso = new RHI_PipelineState { name = name };
        pso.SetShader(RHI_Shader_Type.Compute, compute_shader);
        pso.Prepare();
        return pso;
    }

    public RHI_Pipeline(RHI_PipelineState pipeline_state, RHI_DescriptorSetLayout? descriptor_set_layout)
    {
        m_state = pipeline_state.Clone();
        Name    = m_state.name ?? "pipeline";

        RHI_Shader[] shaders = m_state.shaders.Where(s => s != null).ToArray()!;
        DescriptorSetLayout  = descriptor_set_layout ?? RHI_Device.GetOrCreateDescriptorSetLayout(shaders, Name);
        SetCount             = RHI_Device.GetPipelineSetCount(shaders);

        CreateLayout();

        var entry_points = new List<nint>();
        try
        {
            // shader stages
            PipelineShaderStageCreateInfo* shader_stages = stackalloc PipelineShaderStageCreateInfo[(int)RHI_Shader_Type.Max];
            int stage_count = 0;
            foreach (RHI_Shader shader in shaders)
            {
                nint entry = SilkMarshal.StringToPtr(shader.GetEntryPoint());
                entry_points.Add(entry);
                shader_stages[stage_count++] = CreateShaderStage(shader, (byte*)entry);
            }

            if (m_state.IsCompute())
            {
                ComputePipelineCreateInfo pipeline_info = new()
                {
                    SType  = StructureType.ComputePipelineCreateInfo,
                    Layout = m_rhi_resource_layout,
                    Stage  = shader_stages[0]
                };
                Check(RHI_Context.Vk.CreateComputePipelines(RHI_Context.Device, RHI_Device.GetPipelineCache(), 1, in pipeline_info, null, out m_rhi_resource), "vkCreateComputePipelines");
            }
            else if (m_state.IsGraphics())
            {
                CreateGraphics(shader_stages, (uint)stage_count);
            }
            else
            {
                throw new InvalidOperationException($"{Name}: the pipeline state is neither graphics nor compute");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            foreach (nint entry in entry_points)
                SilkMarshal.Free(entry);
        }
        RHI_Device.SetResourceName(m_rhi_resource.Handle, RHI_Resource_Type.Pipeline, Name);
    }

    private static PipelineShaderStageCreateInfo CreateShaderStage(RHI_Shader shader, byte* entry_point)
    {
        if (!shader.IsCompiled() || shader.GetRhiResource().Handle == 0)
            throw new InvalidOperationException($"{shader.ObjectName}: the shader is not compiled");
        return new PipelineShaderStageCreateInfo
        {
            SType  = StructureType.PipelineShaderStageCreateInfo,
            Stage  = ToVulkanStageFlags(RHI_Definitions.rhi_shader_type_to_mask(shader.GetShaderStage())),
            Module = shader.GetRhiResource(),
            PName  = entry_point
        };
    }

    private void CreateLayout()
    {
        // build descriptor set layouts array - must match the order of appearance in common_resources.glsl
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[(int)SetCount];
        layouts[0] = DescriptorSetLayout.GetRhiResource();
        for (int i = 1; i < SetCount; i++)
            layouts[i] = RHI_Device.GetDescriptorSetLayout((RHI_Device_Bindless_Resource)(i - 1));

        // push constant buffers
        PushConstantRange* ranges = stackalloc PushConstantRange[4];
        uint range_count = 0;
        foreach (RHI_Descriptor descriptor in DescriptorSetLayout.GetDescriptors())
        {
            if (descriptor.type != RHI_Descriptor_Type.PushConstantBuffer)
                continue;
            if (descriptor.struct_size > RHI_Device.PropertyMaxPushConstantSize)
                throw new NotSupportedException($"push constant block \"{descriptor.name}\" is {descriptor.struct_size} bytes, the device allows {RHI_Device.PropertyMaxPushConstantSize}");
            if (range_count == 4)
                throw new NotSupportedException($"{Name}: more than 4 push constant blocks");
            ranges[range_count] = new PushConstantRange { Offset = 0, Size = descriptor.struct_size, StageFlags = ToVulkanStageFlags(descriptor.stage) };

            // store the stages for use in PushConstants calls
            m_push_constant_stages |= ranges[range_count].StageFlags;
            m_push_constant_size    = System.Math.Max(m_push_constant_size, descriptor.struct_size);
            range_count++;
        }

        PipelineLayoutCreateInfo layout_info = new()
        {
            SType                  = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount         = SetCount,
            PSetLayouts            = layouts,
            PushConstantRangeCount = range_count,
            PPushConstantRanges    = ranges
        };
        Check(RHI_Context.Vk.CreatePipelineLayout(RHI_Context.Device, in layout_info, null, out m_rhi_resource_layout), "vkCreatePipelineLayout");
        RHI_Device.SetResourceName(m_rhi_resource_layout.Handle, RHI_Resource_Type.PipelineLayout, Name);
    }

    private void CreateGraphics(PipelineShaderStageCreateInfo* shader_stages, uint stage_count)
    {
        // dynamic states
        DynamicState* dynamic_states = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor, DynamicState.CullMode };
        PipelineDynamicStateCreateInfo dynamic_state = new()
        {
            SType             = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 3,
            PDynamicStates    = dynamic_states
        };

        // viewport & scissor (dynamic, the values are placeholders like spartan's)
        Viewport viewport = new(0, 0, m_state.GetWidth(), m_state.GetHeight(), 0.0f, 1.0f);
        Rect2D scissor    = new(new Offset2D(0, 0), new Extent2D(m_state.GetWidth(), m_state.GetHeight()));
        PipelineViewportStateCreateInfo viewport_state = new()
        {
            SType         = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            PViewports    = &viewport,
            ScissorCount  = 1,
            PScissors     = &scissor
        };

        // binding and vertex attribute descriptions
        VertexInputBindingDescription vertex_input_binding_desc = default;
        uint binding_count = 0;
        VertexInputAttributeDescription* vertex_attribute_descs = stackalloc VertexInputAttributeDescription[8];
        uint attribute_count = 0;
        RHI_Shader? shader_vertex = m_state.GetShader(RHI_Shader_Type.Vertex);
        if (shader_vertex?.GetInputLayout() is { } input_layout)
        {
            // vertex buffer (binding 0) - for per-vertex attributes like position, uv, color, normal, tangent
            // instance data is pulled from a storage buffer, firstInstance is a draw index so instance-rate attributes would oob
            static bool is_vertex_attribute(string name) => name is "POSITION" or "TEXCOORD" or "COLOR" or "NORMAL" or "TANGENT";
            bool has_vertex_attributes = input_layout.GetAttributeDescriptions().Any(d => is_vertex_attribute(d.name));

            // add vertex buffer binding if there are per-vertex attributes
            if (has_vertex_attributes)
            {
                vertex_input_binding_desc = new VertexInputBindingDescription(0, shader_vertex.GetVertexSize(), VertexInputRate.Vertex);
                binding_count = 1;

                // add attribute descriptions for per-vertex attributes
                foreach (VertexAttribute desc in input_layout.GetAttributeDescriptions())
                    if (is_vertex_attribute(desc.name))
                        vertex_attribute_descs[attribute_count++] = new VertexInputAttributeDescription(desc.location, 0, ToVulkan(desc.format), desc.offset);
            }
        }
        PipelineVertexInputStateCreateInfo vertex_input_state = new()
        {
            SType                           = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount   = binding_count,
            PVertexBindingDescriptions      = &vertex_input_binding_desc,
            VertexAttributeDescriptionCount = attribute_count,
            PVertexAttributeDescriptions    = vertex_attribute_descs
        };

        // input assembly state
        PipelineInputAssemblyStateCreateInfo input_assembly_state = new()
        {
            SType                  = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology               = VulkanPrimitiveTopology[(int)m_state.primitive_topology],
            PrimitiveRestartEnable = false
        };

        // rasterizer state
        RHI_RasterizerState rasterizer = m_state.rasterizer_state!;
        float line_width = System.Math.Clamp(rasterizer.GetLineWidth(), RHI_Device.PropertyLineWidthMin, RHI_Device.PropertyLineWidthMax);
        PipelineRasterizationStateCreateInfo rasterizer_state = new()
        {
            SType                   = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable        = !rasterizer.GetDepthClipEnabled(),
            RasterizerDiscardEnable = false,
            PolygonMode             = VulkanPolygonMode[(int)rasterizer.GetPolygonMode()],
            LineWidth               = line_width,
            CullMode                = VulkanCullMode[(int)RHI_CullMode.Back],
            FrontFace               = FrontFace.Clockwise,
            DepthBiasEnable         = rasterizer.GetDepthBias() != 0.0f,
            DepthBiasConstantFactor = MathF.Floor(rasterizer.GetDepthBias() * (float)(1 << 24)),
            DepthBiasClamp          = rasterizer.GetDepthBiasClamp(),
            DepthBiasSlopeFactor    = rasterizer.GetDepthBiasSlopeScaled()
        };

        // multisampling
        PipelineMultisampleStateCreateInfo multisample_state = new()
        {
            SType                = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable  = false,
            RasterizationSamples = SampleCountFlags.Count1Bit
        };

        // color formats: the swapchain buffer or the render target(s)
        Format* attachment_formats_color = stackalloc Format[RHI_Definitions.rhi_max_render_target_count];
        uint color_count = 0;
        if (m_state.render_target_swapchain != null)
        {
            attachment_formats_color[color_count++] = ToVulkan(m_state.render_target_swapchain.GetFormat());
        }
        else
        {
            for (int i = 0; i < RHI_Definitions.rhi_max_render_target_count; i++)
            {
                RHI_Texture? texture = m_state.render_target_color_textures[i];
                if (texture == null)
                    break;
                attachment_formats_color[color_count++] = ToVulkan(texture.GetFormat());
            }
        }

        // blend state, same for all attachments
        RHI_BlendState blend = m_state.blend_state!;
        PipelineColorBlendAttachmentState blend_state_attachment = new()
        {
            ColorWriteMask      = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable         = blend.GetBlendEnabled(),
            SrcColorBlendFactor = VulkanBlendFactor[(int)blend.GetSourceBlend()],
            DstColorBlendFactor = VulkanBlendFactor[(int)blend.GetDestBlend()],
            ColorBlendOp        = VulkanBlendOperation[(int)blend.GetBlendOp()],
            SrcAlphaBlendFactor = VulkanBlendFactor[(int)blend.GetSourceBlendAlpha()],
            DstAlphaBlendFactor = VulkanBlendFactor[(int)blend.GetDestBlendAlpha()],
            AlphaBlendOp        = VulkanBlendOperation[(int)blend.GetBlendOpAlpha()]
        };
        PipelineColorBlendAttachmentState* blend_state_attachments = stackalloc PipelineColorBlendAttachmentState[RHI_Definitions.rhi_max_render_target_count];
        for (int i = 0; i < color_count; i++)
            blend_state_attachments[i] = blend_state_attachment;
        PipelineColorBlendStateCreateInfo color_blend_state = new()
        {
            SType           = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable   = false,
            LogicOp         = LogicOp.Copy,
            AttachmentCount = color_count,
            PAttachments    = blend_state_attachments
        };
        for (int i = 0; i < 4; i++)
            color_blend_state.BlendConstants[i] = blend.GetBlendFactor();

        // depth-stencil state
        RHI_DepthStencilState depth = m_state.depth_stencil_state!;
        StencilOpState stencil = new()
        {
            CompareOp   = VulkanCompareOperator[(int)depth.GetStencilComparisonFunction()],
            FailOp      = VulkanStencilOperation[(int)depth.GetStencilFailOperation()],
            DepthFailOp = VulkanStencilOperation[(int)depth.GetStencilDepthFailOperation()],
            PassOp      = VulkanStencilOperation[(int)depth.GetStencilPassOperation()],
            CompareMask = depth.GetStencilReadMask(),
            WriteMask   = depth.GetStencilWriteMask(),
            Reference   = 1
        };
        PipelineDepthStencilStateCreateInfo depth_stencil_state = new()
        {
            SType             = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable   = depth.GetDepthTestEnabled(),
            DepthWriteEnable  = depth.GetDepthWriteEnabled(),
            DepthCompareOp    = VulkanCompareOperator[(int)depth.GetDepthComparisonFunction()],
            StencilTestEnable = depth.GetStencilTestEnabled(),
            Front             = stencil,
            Back              = stencil,
            MinDepthBounds    = 1.0f, // functionality of DirectX with reverse-z
            MaxDepthBounds    = 0.0f  // functionality of DirectX with reverse-z
        };

        // enable dynamic rendering, no render passes and no frame buffer objects
        Format attachment_format_depth   = Format.Undefined;
        Format attachment_format_stencil = Format.Undefined;
        if (m_state.render_target_depth_texture is { } tex_depth)
        {
            attachment_format_depth   = ToVulkan(tex_depth.GetFormat());
            attachment_format_stencil = tex_depth.IsStencilFormat() ? attachment_format_depth : Format.Undefined;
        }
        PipelineRenderingCreateInfo pipeline_rendering_create_info = new()
        {
            SType                   = StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount    = color_count,
            PColorAttachmentFormats = attachment_formats_color,
            DepthAttachmentFormat   = attachment_format_depth,
            StencilAttachmentFormat = attachment_format_stencil,
            ViewMask                = m_state.is_multiview ? 0b11u : 0
        };

        GraphicsPipelineCreateInfo pipeline_info = new()
        {
            SType               = StructureType.GraphicsPipelineCreateInfo,
            PNext               = &pipeline_rendering_create_info,
            StageCount          = stage_count,
            PStages             = shader_stages,
            PVertexInputState   = &vertex_input_state,
            PInputAssemblyState = &input_assembly_state,
            PDynamicState       = &dynamic_state,
            PViewportState      = &viewport_state,
            PRasterizationState = &rasterizer_state,
            PMultisampleState   = &multisample_state,
            PColorBlendState    = &color_blend_state,
            PDepthStencilState  = &depth_stencil_state,
            Layout              = m_rhi_resource_layout
        };
        Check(RHI_Context.Vk.CreateGraphicsPipelines(RHI_Context.Device, RHI_Device.GetPipelineCache(), 1, in pipeline_info, null, out m_rhi_resource), "vkCreateGraphicsPipelines");
    }

    public Pipeline GetRhiResource() => m_rhi_resource;
    public PipelineLayout GetRhiResourceLayout() => m_rhi_resource_layout;
    public ShaderStageFlags GetPushConstantStages() => m_push_constant_stages;
    public uint GetPushConstantSize() => m_push_constant_size;

    public void Dispose()
    {
        if (m_rhi_resource.Handle != 0)
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Pipeline, m_rhi_resource.Handle);
        if (m_rhi_resource_layout.Handle != 0)
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.PipelineLayout, m_rhi_resource_layout.Handle);
        m_rhi_resource = default;
        m_rhi_resource_layout = default;
    }
}
