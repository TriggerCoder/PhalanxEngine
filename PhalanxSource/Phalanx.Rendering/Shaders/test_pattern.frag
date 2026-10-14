// The renderer's first pass (Renderer::Pass_TestPattern): samples a bindless material texture with screen space uvs,
// tinted by the pass constants. Reads all of the renderer's standard bindings: the frame constant buffer, the push
// constants, the bindless material textures and the bindless samplers.
#version 460
#extension GL_GOOGLE_include_directive : require

#include "common_resources.glsl"

layout(location = 0) out vec4 out_color;

void main()
{
    // one texel per pixel: the uv spans the render resolution over the texture size, point wrap repeats it
    vec2 size = vec2(textureSize(material_textures[nonuniformEXT(buffer_pass.material_index)], 0));
    vec2 uv   = gl_FragCoord.xy / buffer_frame.resolution_render * (buffer_frame.resolution_render / size);

    vec4 albedo = texture(sampler2D(material_textures[nonuniformEXT(buffer_pass.material_index)], GET_SAMPLER(sampler_point_wrap)), uv);
    out_color   = albedo * buffer_pass.values[0];
}
