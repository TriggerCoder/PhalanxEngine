// The renderer's first pass (Renderer::Pass_TestPattern): a quad over the middle of the target, no vertex buffer.
// Not a spartan shader; it stands in for the scene passes until geometry is wired up.
#version 460

// two triangles, clip space, y up like d3d (spartan compiles with -fvk-invert-y)
const vec2 corners[6] = vec2[](vec2(-0.5, -0.5), vec2(-0.5, 0.5), vec2(0.5, 0.5),
                               vec2(-0.5, -0.5), vec2(0.5, 0.5), vec2(0.5, -0.5));

void main()
{
    gl_Position   = vec4(corners[gl_VertexIndex], 0.0, 1.0);
    gl_Position.y = -gl_Position.y;
}
