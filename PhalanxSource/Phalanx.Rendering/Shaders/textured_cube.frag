// The textured cube sample (RHI_TexturedCubeSample): a per-pass texture sampled through the bindless samplers.
#version 460
#extension GL_EXT_nonuniform_qualifier : require

// Texture2D tex : register(t7), SamplerState samplers[] : register(s1, space7), as in common_resources.glsl
layout(set = 0, binding = 307) uniform texture2D tex;
layout(set = 7, binding = 401) uniform sampler samplers[];

layout(location = 0) in vec2 in_uv;
layout(location = 0) out vec4 out_color;

void main()
{
    // samplers[0] is point clamp, like spartan's sampler order
    out_color = texture(sampler2D(tex, samplers[0]), in_uv);
}
