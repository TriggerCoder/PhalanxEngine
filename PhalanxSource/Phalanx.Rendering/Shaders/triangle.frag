// The triangle sample (RHI_TriangleSample): the interpolated vertex colour.
#version 460

layout(location = 0) in vec4 in_color;
layout(location = 0) out vec4 out_color;

void main()
{
    out_color = in_color;
}
