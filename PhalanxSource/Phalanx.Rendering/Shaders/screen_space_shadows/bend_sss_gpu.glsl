// Copyright 2023 Sony Interactive Entertainment.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

// If you have feedback, or found this code useful, we'd love to hear from you.
// https://www.bendstudio.com
// https://www.twitter.com/bendstudio

// GLSL port of Bend Studio's screen space shadow projection (gpu) as SpartanEngine carries it,
// data/shaders/screen_space_shadows/bend_sss_gpu.hlsl @ 4c07baa (identical at upstream 67a6e8b). Spartan's changes to
// the original are kept: the depth and output textures are float4 (the first component is read and written), the
// output is a texture array indexed by ArraySliceIndex, SurfaceThickness defaults to 0.01, and the depth thickness
// scale has a floor of half a texel (bend_sss_gpu.hlsl:304-307).
// Deviations:
// - GLSL structs can't hold textures or samplers, so DispatchParameters carries the values only. The depth is
//   SSS_DEPTH_TEXTURE sampled with SSS_POINT_BORDER_SAMPLER, the output is SSS_OUTPUT_TEXTURE (image2DArray); the
//   including shader defines the three.
// - The final store is bounds checked against the output size. The wavefront projection reads and writes up to
//   2 * WAVE_SIZE pixels outside the render bounds (bend_sss_cpu.h:65-66); D3D12 drops out of bounds uav writes, Vulkan
//   only promises that with robustImageAccess, which the rhi doesn't enable.
// - The early out path uses subgroup votes (WaveActiveAnyTrue -> subgroupAny, WaveGetLaneCount -> gl_SubgroupSize).
//   Spartan never enables it (UseEarlyOut stays false), it is kept for fidelity.
//
// The main function is WriteScreenSpaceShadow(), it reads a depth texture and writes the shadow texture.
// The following macros must be defined before including this file:
//   WAVE_SIZE            wavefront size of the compute shader running this code, local_size_x = WAVE_SIZE (only
//                        tested with 64)
//   SAMPLE_COUNT         shadow samples per pixel, the length of the shadow in pixels. The texture reads per thread
//                        are (SAMPLE_COUNT / WAVE_SIZE + 2) * 2. Recommended starting value 60.
//   HARD_SHADOW_SAMPLES  initial samples that produce a hard shadow and skip the averaging, which trades aliasing for
//                        grounding pixels very close to the caster. Recommended starting value 4.
//   FADE_OUT_SAMPLES     samples that fade out at the end of the shadow (for a small cost). Recommended starting value 8.
#ifndef PHALANX_BEND_SSS_GPU
#define PHALANX_BEND_SSS_GPU

#extension GL_KHR_shader_subgroup_basic : require
#extension GL_KHR_shader_subgroup_vote : require

#define USE_HALF_PIXEL_OFFSET 1 // apply a 0.5 texel offset when sampling, toggle it if the shadow shows odd regular grid-like artefacts
#define USE_UV_PIXEL_BIAS     1 // sample(uv + bias) instead of an offset operand

// the runtime properties passed to the shader. Wherever possible these should be compile time constants
struct DispatchParameters
{
    // visual configuration, needs manual tuning. All shadow computation is in non-linear depth space (not world space),
    // so the tuned values depend on the scene depth distribution (the projection matrix setup)

    float SurfaceThickness;           // assumed thickness of each pixel for shadow casting, a percentage of the difference in non-linear depth between the sample and FarDepthValue. Recommended starting value: 0.005 (0.5%)
    float BilinearThreshold;          // percentage threshold for when the difference between two depth values is an edge and should not interpolate. Tune it with DebugOutputEdgeMask. Recommended starting value: 0.02 (2%)
    float ShadowContrast;             // contrast boost applied to the transition in/out of shadow. Recommended starting value: 2 or 4. Values >= 1 are valid
    bool  IgnoreEdgePixels;           // edge pixels don't contribute to the shadow. Flat surfaces lit at grazing angles can detect many false edges that alias the shadow; this avoids that but can thin valid shadows, especially on foliage edges. Recommended: false, unless scenes have many large flat surfaces
    bool  UsePrecisionOffset;         // a small offset to account for an imprecise depth buffer (recommend off)
    bool  BilinearSamplingOffsetMode; // true: sampling points are offset to the wavefront shared ray, shadow and starting depths are the same, can project more jagged shadow lines. false: sampling starts at pixel centres, shadow depths are biased by the depth gradient across the bilinear sample, more issues in back-face / grazing areas. Recommended starting value: false

    // debug views
    bool DebugOutputEdgeMask;    // visualize edges, for tuning BilinearThreshold
    bool DebugOutputThreadIndex; // visualize the layout of compute threads
    bool DebugOutputWaveIndex;   // visualize the layout of compute wavefronts, sanity checks the light coordinate

    // culling / early out
    vec2 DepthBounds;  // depth bounds (min, max) of the on-screen volume of the light, typically (0,1) for directional lights. Only used with UseEarlyOut
    bool UseEarlyOut;  // early out when depth values are outside DepthBounds. Can dramatically reduce cost when few pixels need a shadow (sky), ~15% overhead in the worst case

    // runtime data returned from BuildDispatchList()
    vec4  LightCoordinate; // DispatchList.LightCoordinate_Shader
    ivec2 WaveOffset;      // DispatchData.WaveOffset_Shader

    // renderer specific values
    float FarDepthValue;  // the depth buffer value of the far clip plane (typically 0)
    float NearDepthValue; // the depth buffer value of the near clip plane (typically 1)

    // sampling data
    vec2 InvDepthTextureSize; // inverse of the depth texture dimensions (pixel coordinates to uvs)
    int  ArraySliceIndex;
};

// sensible starting tuning values
void DispatchParameters_SetDefaults(inout DispatchParameters p)
{
    p.SurfaceThickness           = 0.01; // default: 0.005
    p.BilinearThreshold          = 0.02; // default: 0.02
    p.ShadowContrast             = 4.0;
    p.IgnoreEdgePixels           = false;
    p.UsePrecisionOffset         = false;
    p.BilinearSamplingOffsetMode = false;
    p.DebugOutputEdgeMask        = false;
    p.DebugOutputThreadIndex     = false;
    p.DebugOutputWaveIndex       = false;
    p.DepthBounds                = vec2(0.0, 1.0);
    p.UseEarlyOut                = false;
}

#if !defined(WAVE_SIZE) || !defined(SAMPLE_COUNT) || !defined(HARD_SHADOW_SAMPLES) || !defined(FADE_OUT_SAMPLES)
    #error Before including bend_sss_gpu.glsl, four macros must be defined: WAVE_SIZE, SAMPLE_COUNT, HARD_SHADOW_SAMPLES and FADE_OUT_SAMPLES.
#endif
#if !defined(SSS_DEPTH_TEXTURE) || !defined(SSS_POINT_BORDER_SAMPLER) || !defined(SSS_OUTPUT_TEXTURE)
    #error Before including bend_sss_gpu.glsl, define SSS_DEPTH_TEXTURE, SSS_POINT_BORDER_SAMPLER and SSS_OUTPUT_TEXTURE.
#endif

// customize this to return true when the pixel should early out for custom reasons (a shadow map already found it in
// shadow, back facing, ...). Keep it simple. The compiled code is better when depth isn't referenced
bool EarlyOutPixel(DispatchParameters inParameters, ivec2 pixel_xy, float depth)
{
    return depth >= inParameters.DepthBounds.y || depth <= inParameters.DepthBounds.x;
}

// the start pixel coordinates of the pixels in the wavefront, and the delta to the next pixel WAVE_SIZE pixels along the ray
void ComputeWavefrontExtents(DispatchParameters inParameters, ivec3 inGroupID, uint inGroupThreadID, out vec2 outDeltaXY, out vec2 outPixelXY, out float outPixelDistance, out bool outMajorAxisX)
{
    ivec2 xy = inGroupID.yz * WAVE_SIZE + inParameters.WaveOffset.xy;

    // integer light position / fractional component
    vec2 light_xy          = floor(inParameters.LightCoordinate.xy) + 0.5;
    vec2 light_xy_fraction = inParameters.LightCoordinate.xy - light_xy;
    bool reverse_direction = inParameters.LightCoordinate.w > 0.0;

    ivec2 sign_xy   = sign(xy);
    bool horizontal = abs(xy.x + sign_xy.y) < abs(xy.y - sign_xy.x);

    ivec2 axis;
    axis.x = horizontal ? (+sign_xy.y) : (0);
    axis.y = horizontal ? (0) : (-sign_xy.x);

    // apply the wave offset
    xy = axis * inGroupID.x + xy;
    vec2 xy_f = vec2(xy);

    // for interpolation to the light center only the larger of the two axes matters
    bool x_axis_major = abs(xy_f.x) > abs(xy_f.y);
    float major_axis  = x_axis_major ? xy_f.x : xy_f.y;

    float major_axis_start = abs(major_axis);
    float major_axis_end   = abs(major_axis) - float(WAVE_SIZE);

    float ma_light_frac = x_axis_major ? light_xy_fraction.x : light_xy_fraction.y;
    ma_light_frac       = major_axis > 0.0 ? -ma_light_frac : ma_light_frac;

    // back in to screen direction
    vec2 start_xy = xy_f + light_xy;

    // the inner most ring interpolates to a pixel centred uv, so the uv to pixel rounding doesn't skip output pixels
    vec2 end_xy = mix(inParameters.LightCoordinate.xy, start_xy, (major_axis_end + ma_light_frac) / (major_axis_start + ma_light_frac));

    // the major axis should be a round number
    vec2 xy_delta = (start_xy - end_xy);

    // inverse the read order when reverse direction is true
    float thread_step = float(inGroupThreadID ^ (reverse_direction ? 0u : uint(WAVE_SIZE - 1)));

    vec2 pixel_xy        = mix(start_xy, end_xy, thread_step / float(WAVE_SIZE));
    float pixel_distance = major_axis_start - thread_step + ma_light_frac;

    outPixelXY       = pixel_xy;
    outPixelDistance = pixel_distance;
    outDeltaXY       = xy_delta;
    outMajorAxisX    = x_axis_major;
}

// number of bilinear sample reads performed per thread
#define READ_COUNT (SAMPLE_COUNT / WAVE_SIZE + 2)

// common shared data
shared float DepthData[READ_COUNT * WAVE_SIZE];
shared bool  LdsEarlyOut;

// GroupMemoryBarrierWithGroupSync
void bend_group_sync()
{
    memoryBarrierShared();
    barrier();
}

// generate the shadow, call from a compute shader with local_size_x = WAVE_SIZE
//   inGroupID:       gl_WorkGroupID
//   inGroupThreadID: gl_LocalInvocationID.x
void WriteScreenSpaceShadow(DispatchParameters inParameters, ivec3 inGroupID, int inGroupThreadID)
{
    vec2 xy_delta;
    vec2 pixel_xy;
    float pixel_distance;
    bool x_axis_major; // major axis is x axis? abs(xy_delta.x) > abs(xy_delta.y)

    ComputeWavefrontExtents(inParameters, inGroupID, uint(inGroupThreadID), xy_delta, pixel_xy, pixel_distance, x_axis_major);

    // read in the depth values
    float sampling_depth[READ_COUNT];
    float shadowing_depth[READ_COUNT];
    float depth_thickness_scale[READ_COUNT];
    float sample_distance[READ_COUNT];

    const float direction = -inParameters.LightCoordinate.w;
    const float z_sign    = inParameters.NearDepthValue > inParameters.FarDepthValue ? -1.0 : +1.0;

    int i;
    bool is_edge    = false;
    bool skip_pixel = false;
    vec2 write_xy   = floor(pixel_xy);

    for (i = 0; i < READ_COUNT; i++)
    {
        // depth is sampled twice per pixel per sample and interpolated with an edge detect filter. Interpolation only
        // happens on the minor axis of the ray, major axis coordinates are at pixel centres
        vec2 read_xy     = floor(pixel_xy);
        float minor_axis = x_axis_major ? pixel_xy.y : pixel_xy.x;

        // a pixel detected as an edge optionally (IgnoreEdgePixels) doesn't take part in the shadow
        const float edge_skip = 1e20; // pushes edge samples out of range

        vec2 depths;
        float bilinear = fract(minor_axis) - 0.5;

    #if USE_HALF_PIXEL_OFFSET
        read_xy += 0.5;
    #endif

    #if USE_UV_PIXEL_BIAS
        float bias     = bilinear > 0.0 ? 1.0 : -1.0;
        vec2 offset_xy = vec2(x_axis_major ? 0.0 : bias, x_axis_major ? bias : 0.0);

        // the first component of the texture, spartan's depth is untyped float4
        depths.x = textureLod(sampler2D(SSS_DEPTH_TEXTURE, SSS_POINT_BORDER_SAMPLER), read_xy * inParameters.InvDepthTextureSize, 0.0).x;
        depths.y = textureLod(sampler2D(SSS_DEPTH_TEXTURE, SSS_POINT_BORDER_SAMPLER), (read_xy + offset_xy) * inParameters.InvDepthTextureSize, 0.0).x;
    #else
        int bias        = bilinear > 0.0 ? 1 : -1;
        ivec2 offset_xy = ivec2(x_axis_major ? 0 : bias, x_axis_major ? bias : 0);
        depths.x = textureLod(sampler2D(SSS_DEPTH_TEXTURE, SSS_POINT_BORDER_SAMPLER), read_xy * inParameters.InvDepthTextureSize, 0.0).x;
        depths.y = textureLod(sampler2D(SSS_DEPTH_TEXTURE, SSS_POINT_BORDER_SAMPLER), (read_xy + vec2(offset_xy)) * inParameters.InvDepthTextureSize, 0.0).x;
    #endif

        // depth thresholds (bilinear/shadow thickness) are a fractional ratio of the difference between the sampled
        // depth and the far clip depth
        float far_depth_delta           = abs(inParameters.FarDepthValue - depths.x);
        float min_depth_thickness_scale = max(inParameters.InvDepthTextureSize.x, inParameters.InvDepthTextureSize.y) * 0.5;

        // keep far-distance grazing surfaces from turning tiny depth deltas into hard edges
        depth_thickness_scale[i] = max(far_depth_delta, min_depth_thickness_scale);

        // past the threshold, use point filtering
        bool use_point_filter = abs(depths.x - depths.y) > depth_thickness_scale[i] * inParameters.BilinearThreshold;

        // for the debug output when DebugOutputEdgeMask is true
        if (i == 0) is_edge = use_point_filter;

        if (inParameters.BilinearSamplingOffsetMode)
        {
            bilinear = use_point_filter ? 0.0 : bilinear;
            // both shadow depth and starting depth are the same in this mode, unless shadow skipping edges
            sampling_depth[i]  = mix(depths.x, depths.y, abs(bilinear));
            shadowing_depth[i] = (inParameters.IgnoreEdgePixels && use_point_filter) ? edge_skip : sampling_depth[i];
        }
        else
        {
            // the pixel starts sampling at this depth
            sampling_depth[i] = depths.x;

            float edge_depth = inParameters.IgnoreEdgePixels ? edge_skip : depths.x;
            // any sample in this wavefront is possibly interpolated towards the bilinear sample, so the shadowing depth
            // is further away, by the difference between the two samples
            float shadow_depth = depths.x + abs(depths.x - depths.y) * z_sign;

            // shadows cast from this depth
            shadowing_depth[i] = use_point_filter ? edge_depth : shadow_depth;
        }

        // store for later
        sample_distance[i] = pixel_distance + float(WAVE_SIZE * i) * direction;

        // the next pixel along the ray, WAVE_SIZE pixels along it
        pixel_xy += xy_delta * direction;
    }

    // early out, with no debug mode enabled
    if (inParameters.UseEarlyOut && (inParameters.DebugOutputWaveIndex == false && inParameters.DebugOutputThreadIndex == false && inParameters.DebugOutputEdgeMask == false))
    {
        // read the depth of the pixel being shadowed and early out
        skip_pixel = EarlyOutPixel(inParameters, ivec2(write_xy), sampling_depth[0]);

        // are all threads in this wave out of bounds?
        bool early_out = subgroupAny(!skip_pixel) == false;

        // gl_SubgroupSize is the hardware wave size
        if (gl_SubgroupSize == uint(WAVE_SIZE))
        {
            // optimal case: each wavefront is a single wave, so it can trivially early out
            if (early_out == true)
                return;
        }
        else
        {
            // the wavefront is made of several small waves, they coordinate to early out together. That makes the worst
            // case (all pixels drawn) ~15% more expensive, the best case (all early out) is typically 2-3x better
            LdsEarlyOut = true;

            bend_group_sync();

            if (early_out == false)
                LdsEarlyOut = false;

            bend_group_sync();

            if (LdsEarlyOut)
                return;
        }
    }

    // write the shadow depths to lds
    for (i = 0; i < READ_COUNT; i++)
    {
        // perspective correct the shadowing depth, in this space all light rays are parallel
        float stored_depth = (shadowing_depth[i] - inParameters.LightCoordinate.z) / sample_distance[i];

        if (i != 0)
        {
            // within sampling distance of the light, the extended reads can overshoot the light coordinate, ignore those
            stored_depth = sample_distance[i] > 0.0 ? stored_depth : 1e10;
        }

        // store the depth values in groupshared
        int idx = (i * WAVE_SIZE) + inGroupThreadID;
        DepthData[idx] = stored_depth;
    }

    // sync the wavefronts now groupshared DepthData is written
    bend_group_sync();

    // if the starting depth isn't in depth bounds, no shadow is needed
    if (skip_pixel)
        return;

    float start_depth = sampling_depth[0];

    // lerp away from far depth by a tiny fraction?
    if (inParameters.UsePrecisionOffset)
        start_depth = mix(start_depth, inParameters.FarDepthValue, -1.0 / float(0xFFFF));

    // perspective correct the depth
    start_depth = (start_depth - inParameters.LightCoordinate.z) / sample_distance[0];

    // start by reading the next value
    int sample_index = inGroupThreadID + 1;

    vec4 shadow_value = vec4(1.0);
    float hard_shadow = 1.0;

    // the inverse of how large the shadowing window is for the projected sample data. All lds values are scaled by
    // 1 / sample_distance, so all light directions become parallel; the multiply by sample_distance[0] compensates
    // for the projection divide in the data. 1 / SurfaceThickness adjusts the selected thickness (0.5% scales depth
    // values from [0,1] to [0,200], the shadow window is always 1 wide). 1 / depth_thickness_scale[0] because
    // SurfaceThickness is a percentage of the remaining depth between the sample and the far clip, not of the full
    // range. The min() keeps the window a minimum width very close to the light; the +direction term biases the pixel
    // at the very centre of the light to fully lit or shadowed
    float depth_scale = min(sample_distance[0] + direction, 1.0 / inParameters.SurfaceThickness) * sample_distance[0] / depth_thickness_scale[0];

    start_depth = start_depth * depth_scale - z_sign;

    // the first hard shadow samples, a single pixel can produce a full shadow
    for (i = 0; i < HARD_SHADOW_SAMPLES; i++)
    {
        float depth_delta = abs(start_depth - DepthData[sample_index + i] * depth_scale);

        // the distance of the sample closest to the reference depth
        hard_shadow = min(hard_shadow, depth_delta);
    }

    // brute force: the main shadow samples, averaged in to a set of 4 shadow values
    for (i = HARD_SHADOW_SAMPLES; i < SAMPLE_COUNT - FADE_OUT_SAMPLES; i++)
    {
        float depth_delta = abs(start_depth - DepthData[sample_index + i] * depth_scale);

        // like the hard shadow, accumulated to 4 separate values whose average softens single pixel shadows
        shadow_value[i & 3] = min(shadow_value[i & 3], depth_delta);
    }

    // final fade out samples
    for (i = SAMPLE_COUNT - FADE_OUT_SAMPLES; i < SAMPLE_COUNT; i++)
    {
        float depth_delta = abs(start_depth - DepthData[sample_index + i] * depth_scale);

        // add the fade value to these samples
        const float fade_out = float(i + 1 - (SAMPLE_COUNT - FADE_OUT_SAMPLES)) / float(FADE_OUT_SAMPLES + 1) * 0.75;

        shadow_value[i & 3] = min(shadow_value[i & 3], depth_delta + fade_out);
    }

    // apply the contrast. 0 means a sample exactly matched the reference depth (fully shadowed); the boost means
    // samples don't have to match exactly to produce a full shadow
    shadow_value = clamp(shadow_value * (inParameters.ShadowContrast) + (1.0 - inParameters.ShadowContrast), 0.0, 1.0);
    hard_shadow  = clamp(hard_shadow * (inParameters.ShadowContrast) + (1.0 - inParameters.ShadowContrast), 0.0, 1.0);

    float result = 0.0;

    // the average of the 4 values, reduces aliasing noise in the source depth, especially with long shadows
    result = dot(shadow_value, vec4(0.25));

    // if the first samples always produce a hard shadow, take it separately
    result = min(hard_shadow, result);

    // write the result
    {
        if (inParameters.DebugOutputEdgeMask)
            result = is_edge ? 1.0 : 0.0;
        if (inParameters.DebugOutputThreadIndex)
            result = (float(inGroupThreadID) / float(WAVE_SIZE));
        if (inParameters.DebugOutputWaveIndex)
            result = fract(float(inGroupID.x) / float(WAVE_SIZE));

        ivec2 write_pixel = ivec2(write_xy);
        if (all(greaterThanEqual(write_pixel, ivec2(0))) && all(lessThan(write_pixel, imageSize(SSS_OUTPUT_TEXTURE).xy)))
            imageStore(SSS_OUTPUT_TEXTURE, ivec3(write_pixel, inParameters.ArraySliceIndex), vec4(result));
    }
}

#endif
