// Subset of SpartanEngine data/shaders/common_resources_*.hlsl, translated to GLSL (Vulkan).
// Grows shader by shader as the GLSL port proceeds (see the GLSL section of the port plan).
//
// Binding scheme: identical to what DXC produced for Spartan, so RHI descriptor layouts carry over:
//   HLSL register(xN, spaceS)  ->  layout(set = S, binding = N + shift)
//   shifts (RHI_Definitions.h): u = 100, b = 200, t = 300, s = 400
#ifndef PHALANX_COMMON_RESOURCES_GLSL
#define PHALANX_COMMON_RESOURCES_GLSL

#extension GL_EXT_nonuniform_qualifier : require        // bindless arrays (descriptor indexing)
#extension GL_EXT_samplerless_texture_functions : require // texelFetch/textureSize on texture2D without a sampler
#extension GL_EXT_shader_image_load_formatted : require  // storage images without a format qualifier ([[vk::image_format("unknown")]])

#include "shared_buffers.glsl"

// cbuffer BufferFrame : register(b0)  /  cbuffer BufferPass : register(b1) (push constants in Spartan's Vulkan path)
layout(std140, set = 0, binding = 200) uniform BufferFrame { FrameBufferData buffer_frame; };
layout(push_constant, std430) uniform BufferPass { PassBufferData buffer_pass; };

// Texture2D tex : register(t7)
layout(set = 0, binding = 307) uniform texture2D tex;

// RWTexture2D<float4> tex_uav : register(u0), [[vk::image_format("unknown")]] -> no format qualifier
// (needs shaderStorageImageReadWithoutFormat / WriteWithoutFormat)
layout(set = 0, binding = 100) uniform image2D tex_uav;

// SamplerState samplers[] : register(s1, space7)  -> separate sampler objects, combined at the call site
layout(set = 7, binding = 401) uniform sampler samplers[];
const uint sampler_bilinear_clamp = 3;
#define GET_SAMPLER(index_sampler) samplers[index_sampler]

// vertex pulling decode (common_resources_bindless.hlsl), PulledVertex is Spartan's 24-byte main vertex
vec3 unpack_vertex_position(PulledVertex v)
{
    return vec3(v.position_x, v.position_y, v.position_z);
}

vec2 unpack_vertex_uv(uint packed_uv)
{
    // f16tof32(uint2(packed & 0xFFFFu, packed >> 16)), x in the low half
    return unpackHalf2x16(packed_uv);
}

vec3 unpack_vertex_oct(uint packed_dir)
{
    // sign-extend snorm16 lanes into [-1, 1]
    int sx  = int(packed_dir << 16) >> 16;
    int sy  = int(packed_dir) >> 16;
    vec2 f  = max(vec2(sx, sy) * (1.0 / 32767.0), -1.0);

    // branchless octahedral wrap (rune stubbe form)
    vec3 n  = vec3(f, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-n.z, 0.0);
    n.x    += n.x >= 0.0 ? -t : t;
    n.y    += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

#endif
