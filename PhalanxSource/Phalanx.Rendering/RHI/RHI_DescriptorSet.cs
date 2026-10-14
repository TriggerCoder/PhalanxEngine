// Port of SpartanEngine source/rhi/RHI_DescriptorSet.h/.cpp + vulkan/Vulkan_DescriptorSet.cpp @ 4c07baa
// Deviations:
// - Unbound Image slots get a null descriptor (robustness2 nullDescriptor, reads return 0) instead of the renderer's
//   black standard texture (Vulkan_DescriptorSet.cpp:54-63); the renderer and its standard textures come later.
//   Same for unbound entries of a mip-view array (spartan writes its fallback there too, :224).
// - The write scratch is stackalloc'd per Update instead of a thread_local 256-entry context, with the same capacity
//   check spartan omits (more than 256 image infos would overflow its arrays).
// - No acceleration structures yet (ray tracing step).
using Silk.NET.Vulkan;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_DescriptorSet
{
    private const int max_descriptors = 256;

    private RHI_DescriptorWithBinding[] m_descriptors = Array.Empty<RHI_DescriptorWithBinding>();
    private readonly DescriptorSet m_resource;
    private ulong m_last_used_frame;

    public string Name { get; }

    public RHI_DescriptorSet(RHI_DescriptorWithBinding[] descriptors, RHI_DescriptorSetLayout layout, string name)
    {
        Name       = name;
        m_resource = RHI_Device.AllocateDescriptorSet(layout);
        RHI_Device.SetResourceName(m_resource.Handle, RHI_Resource_Type.DescriptorSet, name);
        Update(descriptors);
    }

    public bool IsReferingToResource(object resource)
    {
        foreach (RHI_DescriptorWithBinding desc in m_descriptors)
            if (ReferenceEquals(desc.binding.resource, resource))
                return true;
        return false;
    }

    public DescriptorSet GetResource() => m_resource;
    public ulong GetLastUsedFrame() => m_last_used_frame;
    public void MarkUsed(ulong frame) => m_last_used_frame = frame;

    private void Update(RHI_DescriptorWithBinding[] descriptors)
    {
        m_descriptors = descriptors;
        WriteDescriptorSet* writes   = stackalloc WriteDescriptorSet[max_descriptors];
        DescriptorImageInfo* images  = stackalloc DescriptorImageInfo[max_descriptors];
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[max_descriptors];
        int write_count = 0, image_count = 0, buffer_count = 0;

        void CheckCapacity(int images_needed)
        {
            if (write_count >= max_descriptors || image_count + images_needed > max_descriptors || buffer_count >= max_descriptors)
                throw new InvalidOperationException($"descriptor set \"{Name}\" needs more than {max_descriptors} writes");
        }

        foreach (RHI_DescriptorWithBinding desc in descriptors)
        {
            RHI_Descriptor layout = desc.descriptor;
            RHI_DescriptorBinding binding = desc.binding;

            // skip unbound descriptors (except images which use the fallback)
            if (!binding.IsBound() && layout.type != RHI_Descriptor_Type.Image)
                continue;
            // skip bindless and push constants, they have their own sets / ranges
            if (layout.IsBindless() || layout.type == RHI_Descriptor_Type.PushConstantBuffer)
                continue;

            DescriptorType vk_type = RHI_Device.GetDescriptorType(layout);
            switch (layout.type)
            {
                case RHI_Descriptor_Type.Image:
                case RHI_Descriptor_Type.TextureStorage:
                {
                    var texture = binding.resource as RHI_Texture;
                    bool mip_specified = binding.mip != RHI_Definitions.rhi_all_mips;
                    if (!layout.as_array)
                    {
                        ImageView view = default;
                        bool write = false;
                        if (texture != null)
                        {
                            bool layer_specified = binding.array_layer != RHI_Definitions.rhi_all_mips;
                            if (layer_specified && binding.array_layer < RHI_Definitions.rhi_max_render_target_count && texture.GetRhiSrvLayer(binding.array_layer).Handle != 0)
                                view = texture.GetRhiSrvLayer(binding.array_layer);
                            else
                                view = mip_specified ? texture.GetRhiSrvMip(binding.mip) : texture.GetRhiSrv();
                            write = true;
                        }
                        else if (layout.type == RHI_Descriptor_Type.Image)
                        {
                            write = true; // null descriptor fallback (see header)
                        }

                        if (write)
                        {
                            CheckCapacity(1);
                            images[image_count] = new DescriptorImageInfo { ImageView = view, ImageLayout = ImageLayout.General };
                            writes[write_count++] = new WriteDescriptorSet
                            {
                                SType = StructureType.WriteDescriptorSet, DstSet = m_resource, DstBinding = layout.slot,
                                DescriptorCount = 1, DescriptorType = vk_type, PImageInfo = &images[image_count]
                            };
                            image_count++;
                        }
                    }
                    else
                    {
                        // array of mip views
                        uint mip_start = mip_specified ? binding.mip : 0;
                        uint mip_count = binding.mip_range > 0 ? binding.mip_range : 1;
                        CheckCapacity((int)mip_count);
                        int first_image = image_count;
                        for (uint m = 0; m < mip_count; m++)
                        {
                            ImageView view = texture != null ? texture.GetRhiSrvMip(mip_start + m) : default;
                            images[image_count++] = new DescriptorImageInfo { ImageView = view, ImageLayout = ImageLayout.General };
                        }
                        writes[write_count++] = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet, DstSet = m_resource, DstBinding = layout.slot,
                            DescriptorCount = mip_count, DescriptorType = vk_type, PImageInfo = &images[first_image]
                        };
                    }
                    break;
                }

                case RHI_Descriptor_Type.ConstantBuffer:
                case RHI_Descriptor_Type.StructuredBuffer:
                {
                    if (binding.resource is RHI_Buffer buffer && buffer.GetRhiResource().Handle != 0)
                    {
                        CheckCapacity(0);
                        buffers[buffer_count] = new DescriptorBufferInfo { Buffer = buffer.GetRhiResource(), Offset = 0, Range = binding.range };
                        writes[write_count++] = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet, DstSet = m_resource, DstBinding = layout.slot,
                            DescriptorCount = 1, DescriptorType = vk_type, PBufferInfo = &buffers[buffer_count]
                        };
                        buffer_count++;
                    }
                    break;
                }

                default:
                    throw new NotSupportedException($"Unhandled descriptor type {layout.type}");
            }
        }

        if (write_count > 0)
            RHI_Context.Vk.UpdateDescriptorSets(RHI_Context.Device, (uint)write_count, writes, 0, null);
    }
}
