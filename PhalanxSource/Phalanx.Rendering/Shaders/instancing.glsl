// The instanced draws of the geometry passes (Renderer.Instancing.cs). pass_instancing (values slot 16, uint bits):
// 0 a plain draw of draw_data[draw_index], 1 the instances are draw data indices (instance_slots), 2 the instances are
// store entity slots of the packed instance buffers, drawn with draw_data[draw_index]'s material and uv transform.
// Deviation: spartan reads geometry_instances (bindless set 10, common_vertex_processing.hlsl:73-86); these buffers are
// bound in set 0 (bindings 340-342), llvmpipe binds only 8 descriptor sets.
#ifndef PHALANX_INSTANCING_GLSL
#define PHALANX_INSTANCING_GLSL

layout(std430, set = 0, binding = 340) readonly buffer InstanceSlotBuffer      { uint instance_slots[]; };
layout(std430, set = 0, binding = 341) readonly buffer InstanceCurrentBuffer   { PackedInstance instances_current[]; };
layout(std430, set = 0, binding = 342) readonly buffer InstancePreviousBuffer  { PackedInstance instances_previous[]; };

const uint instancing_mode_single = 0u;
const uint instancing_mode_exact  = 1u;
const uint instancing_mode_packed = 2u;

uint instancing_mode() { return floatBitsToUint(buffer_pass.values[4].x); }

// common_vertex_processing.hlsl:58-70 compose_packed_instance: scale * rotation * translation from the snorm16
// quaternion; the rows of spartan's row vector matrix are this matrix's columns
mat4 compose_packed_instance(PackedInstance instance)
{
    vec4 q = normalize(vec4(float(int(instance.rotation_xy << 16) >> 16), float(int(instance.rotation_xy) >> 16),
                            float(int(instance.rotation_zw << 16) >> 16), float(int(instance.rotation_zw) >> 16)) / 32767.0);
    float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
    float xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
    float xw = q.x * q.w, yw = q.y * q.w, zw = q.z * q.w;
    return mat4(
        vec4(vec3(1.0 - 2.0 * (yy + zz), 2.0 * (xy + zw), 2.0 * (xz - yw)) * instance.scale_x, 0.0),
        vec4(vec3(2.0 * (xy - zw), 1.0 - 2.0 * (xx + zz), 2.0 * (yz + xw)) * instance.scale_y, 0.0),
        vec4(vec3(2.0 * (xz + yw), 2.0 * (yz - xw), 1.0 - 2.0 * (xx + yy)) * instance.scale_z, 0.0),
        vec4(instance.pos_x, instance.pos_y, instance.pos_z, 1.0));
}

// the draw data entry, world matrix and previous world matrix of this vertex's instance. Every index is checked against
// its buffer: a list that doesn't match its buffers (a cpu bug) collapses the instance to a point instead of reading
// past the end, which faults a gpu without robust buffer access (an MMU fault, VK_ERROR_DEVICE_LOST); valid indices
// are unchanged, so the frames are bit identical
DrawData instance_draw_data(out mat4 transform, out mat4 transform_previous)
{
    uint mode          = instancing_mode();
    uint instance      = uint(gl_InstanceIndex);
    bool listed        = mode == instancing_mode_single || instance < uint(instance_slots.length());
    uint listed_value  = mode != instancing_mode_single && listed ? instance_slots[instance] : 0u;
    uint draw_index    = mode == instancing_mode_exact ? listed_value : buffer_pass.draw_index;
    bool drawable      = listed && draw_index < uint(draw_data.length());
    DrawData _draw     = draw_data[drawable ? draw_index : 0u];
    transform          = _draw.transform;
    transform_previous = _draw.transform_previous;
    if (mode == instancing_mode_packed)
    {
        uint slot = listed_value;
        drawable  = drawable && slot < uint(instances_current.length()) && slot < uint(instances_previous.length());
        if (drawable)
        {
            transform          = compose_packed_instance(instances_current[slot]);
            transform_previous = compose_packed_instance(instances_previous[slot]);
        }
    }
    if (!drawable)
    {
        transform          = mat4(0.0);
        transform_previous = mat4(0.0);
    }
    return _draw;
}

#endif
