// The triangle sample (RHI_TriangleSample): a PosCol vertex buffer straight to clip space.
#version 460

layout(location = 0) in vec3 in_position; // POSITION
layout(location = 1) in vec4 in_color;    // COLOR

layout(location = 0) out vec4 out_color;

void main()
{
    gl_Position = vec4(in_position, 1.0);

    // spartan compiles with -fvk-invert-y, clip space y points up like d3d
    gl_Position.y = -gl_Position.y;
    out_color     = in_color;

    // only read for point lists
    gl_PointSize = 1.0;
}
