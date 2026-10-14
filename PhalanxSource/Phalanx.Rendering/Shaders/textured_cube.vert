// The textured cube sample (RHI_TexturedCubeSample): a PosUv vertex buffer transformed by a push constant matrix.
#version 460

layout(location = 0) in vec3 in_position; // POSITION
layout(location = 1) in vec2 in_uv;       // TEXCOORD

layout(push_constant, std430) uniform BufferPass { mat4 transform; } pass;

layout(location = 0) out vec2 out_uv;

void main()
{
    gl_Position = pass.transform * vec4(in_position, 1.0);

    // spartan compiles with -fvk-invert-y, clip space y points up like d3d
    gl_Position.y = -gl_Position.y;
    out_uv        = in_uv;
}
