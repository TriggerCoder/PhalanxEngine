// foliage impostor math, port of the standalone impostor helpers in Spartan 4c07baa
// data/shaders/common_vertex_processing.hlsl. must match GeometryProcessing.ImpostorFrameDirection/Basis/NearestFrame
// and the atlas layout written by GeometryProcessing.BakeImpostor on the cpu
//
// TODO(port): impostor_resolve() (needs _draw, the impostor_texels buffer at Renderer_BindingsSrv 76, the material albedo
// texture and samplers) and the card rotation in transform_to_world_space() (draw_flag_impostor branch) belong to the
// gpu-driven gbuffer/depth pipelines and get ported with them; they only use the helpers below plus impostor_unpack_*
#ifndef PHALANX_IMPOSTOR_GLSL
#define PHALANX_IMPOSTOR_GLSL

const uint draw_flag_impostor = 1u << 14;

vec3 impostor_frame_direction(uvec2 frame, uint frames)
{
    vec2 o   = (vec2(frame) + 0.5) / float(frames) * 2.0 - 1.0;
    float px = (o.x + o.y) * 0.5;
    float pz = (o.x - o.y) * 0.5;
    return normalize(vec3(px, 1.0 - abs(px) - abs(pz), pz));
}

void impostor_frame_basis(vec3 direction, out vec3 right, out vec3 up)
{
    vec3 reference = direction.y > 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    right          = normalize(cross(reference, direction));
    up             = cross(direction, right);
}

// nearest baked frame for a mesh local view direction, views from below reuse the horizon ring
// (hlsl round() is half away from zero, glsl round() is implementation defined at .5, roundEven is not it either)
uvec2 impostor_nearest_frame(vec3 view_local, uint frames)
{
    view_local.y = max(view_local.y, 0.0);
    float s      = max(abs(view_local.x) + view_local.y + abs(view_local.z), 1e-6);
    vec2 p       = view_local.xz / s;
    vec2 o       = vec2(p.x + p.y, p.x - p.y);
    vec2 f       = (o * 0.5 + 0.5) * float(frames) - 0.5;
    vec2 r       = sign(f) * floor(abs(f) + 0.5);
    return uvec2(clamp(r, 0.0, float(frames - 1u)));
}

// DrawData.impostor_layout = frames | resolution << 8 | layers << 24
uint impostor_layout_frames(uint packed_layout)     { return packed_layout & 0xFFu; }
uint impostor_layout_resolution(uint packed_layout) { return (packed_layout >> 8) & 0xFFFFu; }
uint impostor_layout_layers(uint packed_layout)     { return packed_layout >> 24; }

// first word of (frame, texel) in an atlas, add impostor_texel_offset for the global buffer, layer l is at + l * 2
uint impostor_texel_base(uvec2 frame, uvec2 texel, uint frames, uint resolution, uint layers)
{
    uint frame_words = resolution * resolution * layers * 2u;
    return (frame.y * frames + frame.x) * frame_words + (texel.y * resolution + texel.x) * layers * 2u;
}

// word 0: source uv as unorm16 inside [uv_min, uv_min + uv_scale]
vec2 impostor_unpack_uv(uint packed_uv, vec2 uv_min, vec2 uv_scale)
{
    vec2 source = vec2(packed_uv & 0xFFFFu, packed_uv >> 16) * (1.0 / 65535.0);
    return uv_min + source * uv_scale;
}

// word 1: bit 31 coverage, oct unorm8 frame space normal in bits 0-15, depth unorm15 in bits 16-30
bool impostor_covered(uint geometry) { return (geometry & (1u << 31)) != 0u; }

vec3 impostor_unpack_normal(uint geometry)
{
    vec2 o = vec2(geometry & 0xFFu, (geometry >> 8) & 0xFFu) * (2.0 / 255.0) - 1.0;
    return normalize(vec3(o, max(1.0 - abs(o.x) - abs(o.y), 0.0)));
}

// depth toward the viewer in [-radius, radius]
float impostor_unpack_depth(uint geometry, float radius)
{
    return (float((geometry >> 16) & 0x7FFFu) / 32767.0 * 2.0 - 1.0) * radius;
}

#endif // PHALANX_IMPOSTOR_GLSL
