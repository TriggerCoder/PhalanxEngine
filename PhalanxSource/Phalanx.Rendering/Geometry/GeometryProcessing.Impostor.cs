using System.Buffers;
using System.Numerics;

namespace Phalanx;

// foliage impostors, port of the impostor part of source/geometry/GeometryProcessing.cpp (Spartan 4c07baa)
// the frame math must match Shaders/impostor.glsl (Spartan: common_vertex_processing.hlsl) on the gpu
public static partial class GeometryProcessing
{
    // std::numeric_limits<float>::epsilon(), what Spartan's approximate_equals defaults to
    private const float FltEpsilon = 1.1920929E-07f;

    // Spartan Vector3::Normalized(): leaves zero length and already unit length vectors untouched (System.Numerics
    // would turn a zero vector into NaN). Spartan uses rsqrt + one newton step, this uses the exact sqrt
    internal static Vector3 SpartanNormalized(Vector3 v)
    {
        float length_squared = v.LengthSquared();
        if (!(length_squared + FltEpsilon >= 1.0f && length_squared - FltEpsilon <= 1.0f) && length_squared > 0.0f)
            return v / MathF.Sqrt(length_squared);
        return v;
    }

    // direction of hemi-octahedral impostor frame (x, y) out of frames per axis. the frame grid is the square rotated
    // 45 degrees onto the upper octahedron, every centre lands above the horizon and the corners of the grid meet it
    public static Vector3 ImpostorFrameDirection(uint x, uint y, uint frames)
    {
        float ox = ((float)x + 0.5f) / (float)frames * 2.0f - 1.0f;
        float oy = ((float)y + 0.5f) / (float)frames * 2.0f - 1.0f;
        float px = (ox + oy) * 0.5f;
        float pz = (ox - oy) * 0.5f;
        return SpartanNormalized(new Vector3(px, 1.0f - MathF.Abs(px) - MathF.Abs(pz), pz));
    }

    // the image plane basis a frame was baked with (Spartan's Vector3::Cross is left-handed-agnostic, same formula)
    public static void ImpostorFrameBasis(Vector3 direction, out Vector3 right, out Vector3 up)
    {
        Vector3 reference = direction.Y > 0.999f ? new Vector3(0.0f, 0.0f, 1.0f) : new Vector3(0.0f, 1.0f, 0.0f);
        right = SpartanNormalized(Vector3.Cross(reference, direction));
        up    = Vector3.Cross(direction, right);
    }

    // cpu twin of impostor_nearest_frame (common_vertex_processing.hlsl): nearest baked frame for a mesh local view
    // direction, views from below reuse the horizon ring. Spartan only has it on the gpu, it's here for tests/tools
    public static (uint x, uint y) ImpostorNearestFrame(Vector3 view_local, uint frames)
    {
        view_local.Y = MathF.Max(view_local.Y, 0.0f);
        float s  = MathF.Max(MathF.Abs(view_local.X) + view_local.Y + MathF.Abs(view_local.Z), 1e-6f);
        float px = view_local.X / s;
        float pz = view_local.Z / s;
        float fx = ((px + pz) * 0.5f + 0.5f) * frames - 0.5f;
        float fy = ((px - pz) * 0.5f + 0.5f) * frames - 0.5f;
        // hlsl round() is round-half-away-from-zero
        return ((uint)System.Math.Clamp(MathF.Round(fx, MidpointRounding.AwayFromZero), 0.0f, frames - 1.0f),
                (uint)System.Math.Clamp(MathF.Round(fy, MidpointRounding.AwayFromZero), 0.0f, frames - 1.0f));
    }

    // word offset of (frame, texel) inside one impostor atlas, same addressing as impostor_resolve() on the gpu
    public static uint ImpostorTexelWordOffset(uint frame_x, uint frame_y, uint texel_x, uint texel_y, uint frames, uint resolution, uint layers)
    {
        uint frame_words = resolution * resolution * layers * 2u;
        return (frame_y * frames + frame_x) * frame_words + (texel_y * resolution + texel_x) * layers * 2u;
    }

    // orthographic views of a mesh from every frame direction, per texel and per layer, nearest first:
    // word 0 is the source uv as unorm16 inside [uv_min, uv_min + uv_scale], word 1 is the frame space normal as
    // oct unorm8, the depth toward the viewer as unorm15 over [-radius, radius] and a coverage bit (bit 31)
    // texel (px, py) of layer l in frame (fx, fy) starts at ((((fy * frames + fx) * res + py) * res + px) * layers + l) * 2
    // returns the atlas (frames^2 * resolution^2 * layers * 2 words), cpu rasterizer exactly like Spartan's
    public static uint[] BakeImpostor(
        ReadOnlySpan<RHI_Vertex_PosTexNorTan> vertices,
        ReadOnlySpan<uint> indices,
        uint frames,
        uint resolution,
        uint layers,
        Vector3 center,
        float radius,
        Vector2 uv_min,
        Vector2 uv_scale)
    {
        int frame_texels = (int)(resolution * resolution);
        int frame_words  = frame_texels * (int)layers * 2;
        uint[] texels_out = new uint[(long)frame_words * frames * frames];
        if (vertices.IsEmpty || indices.Length < 3 || radius <= 0.0f)
            return texels_out;

        int vertex_count = vertices.Length;
        Vector3[] positions = ArrayPool<Vector3>.Shared.Rent(vertex_count);
        Vector3[] normals   = ArrayPool<Vector3>.Shared.Rent(vertex_count);
        Vector2[] uvs       = ArrayPool<Vector2>.Shared.Rent(vertex_count);
        Vector3[] projected = ArrayPool<Vector3>.Shared.Rent(vertex_count); // x, y in texels, z toward the viewer
        float[]   depth     = ArrayPool<float>.Shared.Rent(frame_texels * (int)layers);
        try
        {
            for (int i = 0; i < vertex_count; i++)
            {
                positions[i] = vertices[i].pos - center;
                normals[i]   = vertices[i].GetNormal();
                uvs[i]       = vertices[i].GetUv();
            }

            float inv_radius   = 1.0f / radius;
            float resolution_f = resolution;
            Vector2 inv_uv_scale = new Vector2(1.0f / MathF.Max(uv_scale.X, 1e-6f), 1.0f / MathF.Max(uv_scale.Y, 1e-6f));

            for (uint fy = 0; fy < frames; fy++)
            {
                for (uint fx = 0; fx < frames; fx++)
                {
                    Vector3 direction = ImpostorFrameDirection(fx, fy, frames);
                    ImpostorFrameBasis(direction, out Vector3 right, out Vector3 up);

                    // +z points at the viewer, so the nearest surface is the largest z
                    for (int i = 0; i < vertex_count; i++)
                    {
                        Vector3 p = positions[i];
                        projected[i] = new Vector3(
                            (Vector3.Dot(p, right) * inv_radius * 0.5f + 0.5f) * resolution_f,
                            (Vector3.Dot(p, up) * inv_radius * 0.5f + 0.5f) * resolution_f,
                            Vector3.Dot(p, direction));
                    }
                    Array.Fill(depth, -float.MaxValue, 0, frame_texels * (int)layers);
                    int frame = (int)((fy * frames + fx) * (uint)frame_words);

                    void Write(uint px, uint py, float z, Vector2 uv, Vector3 normal)
                    {
                        int texel       = (int)(py * resolution + px);
                        int depth_base  = texel * (int)layers;
                        uint slot = layers;
                        for (uint l = 0; l < layers; l++)
                        {
                            if (z > depth[depth_base + l])
                            {
                                slot = l;
                                break;
                            }
                        }
                        if (slot == layers)
                            return;

                        int words = frame + texel * (int)layers * 2;
                        for (uint l = layers - 1; l > slot; l--)
                        {
                            depth[depth_base + l]                 = depth[depth_base + l - 1];
                            texels_out[words + l * 2]             = texels_out[words + (l - 1) * 2];
                            texels_out[words + l * 2 + 1]         = texels_out[words + (l - 1) * 2 + 1];
                        }

                        // a two sided card seen from behind lights with its front normal flipped toward the viewer
                        float nx = Vector3.Dot(normal, right);
                        float ny = Vector3.Dot(normal, up);
                        float nz = Vector3.Dot(normal, direction);
                        if (nz < 0.0f)
                        {
                            nx = -nx;
                            ny = -ny;
                            nz = -nz;
                        }
                        float n_sum = MathF.Max(MathF.Abs(nx) + MathF.Abs(ny) + nz, 1e-6f);
                        uint ox = (uint)(System.Math.Clamp((nx / n_sum) * 0.5f + 0.5f, 0.0f, 1.0f) * 255.0f + 0.5f);
                        uint oy = (uint)(System.Math.Clamp((ny / n_sum) * 0.5f + 0.5f, 0.0f, 1.0f) * 255.0f + 0.5f);
                        uint dz = (uint)(System.Math.Clamp(z * inv_radius * 0.5f + 0.5f, 0.0f, 1.0f) * 32767.0f + 0.5f);
                        uint u  = (uint)(System.Math.Clamp((uv.X - uv_min.X) * inv_uv_scale.X, 0.0f, 1.0f) * 65535.0f + 0.5f);
                        uint v  = (uint)(System.Math.Clamp((uv.Y - uv_min.Y) * inv_uv_scale.Y, 0.0f, 1.0f) * 65535.0f + 0.5f);

                        depth[depth_base + slot]      = z;
                        texels_out[words + slot * 2]     = u | (v << 16);
                        texels_out[words + slot * 2 + 1] = ox | (oy << 8) | (dz << 16) | (1u << 31);
                    }

                    for (int t = 0; t + 2 < indices.Length; t += 3)
                    {
                        uint i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                        Vector3 a = projected[i0], b = projected[i1], c = projected[i2];

                        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                        int x0 = System.Math.Max((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0);
                        int y0 = System.Math.Max((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0);
                        int x1 = System.Math.Min((int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X))), (int)resolution - 1);
                        int y1 = System.Math.Min((int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), (int)resolution - 1);
                        if (x0 > x1 || y0 > y1)
                            continue;

                        bool covered = false;
                        if (MathF.Abs(area) > 1e-12f)
                        {
                            float inv_area = 1.0f / area;
                            for (int py = y0; py <= y1; py++)
                            {
                                for (int px = x0; px <= x1; px++)
                                {
                                    float sx = px + 0.5f;
                                    float sy = py + 0.5f;
                                    float w0 = ((b.X - sx) * (c.Y - sy) - (b.Y - sy) * (c.X - sx)) * inv_area;
                                    float w1 = ((c.X - sx) * (a.Y - sy) - (c.Y - sy) * (a.X - sx)) * inv_area;
                                    float w2 = 1.0f - w0 - w1;
                                    if (w0 < 0.0f || w1 < 0.0f || w2 < 0.0f)
                                        continue;

                                    covered = true;
                                    Write((uint)px, (uint)py,
                                        a.Z * w0 + b.Z * w1 + c.Z * w2,
                                        uvs[i0] * w0 + uvs[i1] * w1 + uvs[i2] * w2,
                                        SpartanNormalized(normals[i0] * w0 + normals[i1] * w1 + normals[i2] * w2));
                                }
                            }
                        }

                        // a needle thinner than a texel misses every texel centre, it still has to leave its mark
                        // or the distant crown bakes out as bare branches
                        if (!covered)
                        {
                            const float third = 1.0f / 3.0f;
                            uint px = (uint)System.Math.Clamp((a.X + b.X + c.X) * third, 0.0f, resolution_f - 1.0f);
                            uint py = (uint)System.Math.Clamp((a.Y + b.Y + c.Y) * third, 0.0f, resolution_f - 1.0f);
                            Write(px, py,
                                (a.Z + b.Z + c.Z) * third,
                                (uvs[i0] + uvs[i1] + uvs[i2]) * third,
                                SpartanNormalized(normals[i0] + normals[i1] + normals[i2]));
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(positions);
            ArrayPool<Vector3>.Shared.Return(normals);
            ArrayPool<Vector2>.Shared.Return(uvs);
            ArrayPool<Vector3>.Shared.Return(projected);
            ArrayPool<float>.Shared.Return(depth);
        }

        return texels_out;
    }
}
