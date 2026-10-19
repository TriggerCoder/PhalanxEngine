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

// C# port of the cpu half of Bend Studio's screen space shadows as SpartanEngine carries it
// (source/rendering/bend_sss_cpu.h @ 4c07baa, identical at upstream 67a6e8b). BuildDispatchList turns the light's
// homogeneous screen position into up to 8 compute dispatches of bend_sss.comp, one or two per quadrant around the
// light. The gpu half is Shaders/screen_space_shadows/bend_sss_gpu.glsl.
// Deviation: the fixed size arrays of the C++ structs are C# arrays, DispatchList.Dispatch always holds 8 entries and
// DispatchCount says how many are used, like the original.
namespace Phalanx.Rendering;

internal static class BendSss
{
    // Compute Shader Dispatch(X,Y,Z) wave counts and the per dispatch shader value
    internal struct DispatchData
    {
        public int[] WaveCount;          // X/Y/Z
        public int[] WaveOffset_Shader;  // passed to the shader, different for each dispatch

        public DispatchData Copy() => new() { WaveCount = (int[])WaveCount.Clone(), WaveOffset_Shader = (int[])WaveOffset_Shader.Clone() };
    }

    internal sealed class DispatchList
    {
        public readonly float[] LightCoordinate_Shader = new float[4]; // the same for every dispatch of this light
        public readonly DispatchData[] Dispatch        = new DispatchData[8]; // max count is 8
        public int DispatchCount;
    }

    // inLightProjection: the light times the view projection matrix, without the w divide. float4(direction towards
    //   the light, 0) for directional lights, float4(position, 1) for point and spot lights.
    // inViewportSize: width/height of the render target.
    // inMinRenderBounds / inMaxRenderBounds: the light's 2d screen bounds, inclusive. [0,0], [width,height] full screen.
    //   The shader still reads and writes outside of them by up to 2 * WAVE_SIZE pixels.
    // inExpandedZRange: true if clip z is [-1,+1] and becomes [0,1] in the depth buffer, typically false.
    // inWaveSize: the wavefront size of the compiled compute shader (only tested with 64).
    internal static DispatchList BuildDispatchList(float[] inLightProjection, int[] inViewportSize, int[] inMinRenderBounds, int[] inMaxRenderBounds,
                                                   bool inExpandedZRange = false, int inWaveSize = 64)
    {
        var result = new DispatchList();
        for (int i = 0; i < result.Dispatch.Length; i++)
            result.Dispatch[i] = new DispatchData { WaveCount = new int[3], WaveOffset_Shader = new int[2] };

        // floating point division in the shader has a practical limit for precision when the light is *very* far off
        // screen (~1m pixels+), so the xy light coordinate uses an adjusted w value for these extreme values
        float xy_light_w = inLightProjection[3];
        float FP_limit   = 0.000002f * inWaveSize;

        if (xy_light_w >= 0 && xy_light_w < FP_limit) xy_light_w = FP_limit;
        else if (xy_light_w < 0 && xy_light_w > -FP_limit) xy_light_w = -FP_limit;

        // precise xy pixel coordinates of the light
        result.LightCoordinate_Shader[0] = ((inLightProjection[0] / xy_light_w) * +0.5f + 0.5f) * inViewportSize[0];
        result.LightCoordinate_Shader[1] = ((inLightProjection[1] / xy_light_w) * -0.5f + 0.5f) * inViewportSize[1];
        result.LightCoordinate_Shader[2] = inLightProjection[3] == 0 ? 0 : (inLightProjection[2] / inLightProjection[3]);
        result.LightCoordinate_Shader[3] = inLightProjection[3] > 0 ? 1.0f : -1.0f;

        if (inExpandedZRange)
            result.LightCoordinate_Shader[2] = result.LightCoordinate_Shader[2] * 0.5f + 0.5f;

        int[] light_xy = { (int)(result.LightCoordinate_Shader[0] + 0.5f), (int)(result.LightCoordinate_Shader[1] + 0.5f) };

        // make the bounds inclusive, relative to the light
        int[] biased_bounds =
        {
            inMinRenderBounds[0] - light_xy[0],
            -(inMaxRenderBounds[1] - light_xy[1]),
            inMaxRenderBounds[0] - light_xy[0],
            -(inMinRenderBounds[1] - light_xy[1]),
        };

        // process 4 quadrants around the light center, they each form a rectangle with one corner on the light xy
        // coordinate; a rectangle that isn't square needs breaking in two on the larger axis
        // 0 = bottom left, 1 = bottom right, 2 = top left, 3 = top right
        for (int q = 0; q < 4; q++)
        {
            // quads 0 and 3 need to be +1 vertically, 1 and 2 need to be +1 horizontally
            bool vertical = q == 0 || q == 3;

            // bounds relative to the quadrant
            int[] bounds =
            {
                System.Math.Max(0, ((q & 1) != 0 ? biased_bounds[0] : -biased_bounds[2])) / inWaveSize,
                System.Math.Max(0, ((q & 2) != 0 ? biased_bounds[1] : -biased_bounds[3])) / inWaveSize,
                System.Math.Max(0, (((q & 1) != 0 ? biased_bounds[2] : -biased_bounds[0]) + inWaveSize * (vertical ? 1 : 2) - 1)) / inWaveSize,
                System.Math.Max(0, (((q & 2) != 0 ? biased_bounds[3] : -biased_bounds[1]) + inWaveSize * (vertical ? 2 : 1) - 1)) / inWaveSize,
            };

            if ((bounds[2] - bounds[0]) > 0 && (bounds[3] - bounds[1]) > 0)
            {
                int bias_x = (q == 2 || q == 3) ? 1 : 0;
                int bias_y = (q == 1 || q == 3) ? 1 : 0;

                int disp_index = result.DispatchCount++;
                ref DispatchData disp = ref result.Dispatch[disp_index];

                disp.WaveCount[0] = inWaveSize;
                disp.WaveCount[1] = bounds[2] - bounds[0];
                disp.WaveCount[2] = bounds[3] - bounds[1];
                disp.WaveOffset_Shader[0] = ((q & 1) != 0 ? bounds[0] : -bounds[2]) + bias_x;
                disp.WaveOffset_Shader[1] = ((q & 2) != 0 ? -bounds[3] : bounds[1]) + bias_y;

                // the far corner of this quadrant relative to the light, where the diagonal light ray meets the bounds
                int axis_delta = +biased_bounds[0] - biased_bounds[1];
                if (q == 1) axis_delta = +biased_bounds[2] + biased_bounds[1];
                if (q == 2) axis_delta = -biased_bounds[0] - biased_bounds[3];
                if (q == 3) axis_delta = -biased_bounds[2] + biased_bounds[3];

                axis_delta = (axis_delta + inWaveSize - 1) / inWaveSize;

                if (axis_delta > 0)
                {
                    int disp2_index = result.DispatchCount++;

                    // take a copy of the current volume
                    result.Dispatch[disp2_index] = disp.Copy();
                    ref DispatchData disp2 = ref result.Dispatch[disp2_index];

                    if (q == 0)
                    {
                        // split on y, the split becomes -1 larger on x
                        disp2.WaveCount[2] = System.Math.Min(disp.WaveCount[2], axis_delta);
                        disp.WaveCount[2] -= disp2.WaveCount[2];
                        disp2.WaveOffset_Shader[1] = disp.WaveOffset_Shader[1] + disp.WaveCount[2];
                        disp2.WaveOffset_Shader[0]--;
                        disp2.WaveCount[1]++;
                    }
                    if (q == 1)
                    {
                        // split on x, the split becomes +1 larger on y
                        disp2.WaveCount[1] = System.Math.Min(disp.WaveCount[1], axis_delta);
                        disp.WaveCount[1] -= disp2.WaveCount[1];
                        disp2.WaveOffset_Shader[0] = disp.WaveOffset_Shader[0] + disp.WaveCount[1];
                        disp2.WaveCount[2]++;
                    }
                    if (q == 2)
                    {
                        // split on x, the split becomes -1 larger on y
                        disp2.WaveCount[1] = System.Math.Min(disp.WaveCount[1], axis_delta);
                        disp.WaveCount[1] -= disp2.WaveCount[1];
                        disp.WaveOffset_Shader[0] += disp2.WaveCount[1];
                        disp2.WaveCount[2]++;
                        disp2.WaveOffset_Shader[1]--;
                    }
                    if (q == 3)
                    {
                        // split on y, the split becomes +1 larger on x
                        disp2.WaveCount[2] = System.Math.Min(disp.WaveCount[2], axis_delta);
                        disp.WaveCount[2] -= disp2.WaveCount[2];
                        disp.WaveOffset_Shader[1] += disp2.WaveCount[2];
                        disp2.WaveCount[1]++;
                    }

                    // remove if too small (the C++ copies the last entry over it, the removed slot is then reused)
                    if (disp2.WaveCount[1] <= 0 || disp2.WaveCount[2] <= 0)
                    {
                        result.Dispatch[disp2_index] = result.Dispatch[--result.DispatchCount].Copy();
                    }
                    if (result.Dispatch[disp_index].WaveCount[1] <= 0 || result.Dispatch[disp_index].WaveCount[2] <= 0)
                    {
                        result.Dispatch[disp_index] = result.Dispatch[--result.DispatchCount].Copy();
                    }
                }
            }
        }

        // scale the shader values by the wave count, the shader expects this
        for (int i = 0; i < result.DispatchCount; i++)
        {
            result.Dispatch[i].WaveOffset_Shader[0] *= inWaveSize;
            result.Dispatch[i].WaveOffset_Shader[1] *= inWaveSize;
        }

        return result;
    }
}
