using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.World;

namespace Phalanx.Editor;

// --entities N: the stress mode of --renderer-cube. N small cubes are store entities (EntityStore, no Entity objects)
// on a field in front of the camera, each turning about its own axis and circling its own spot while bobbing up and
// down. Batched behaviours move them (Spin and Orbit in Update, Bob in LateUpdate, in parallel over chunks) and the
// renderer draws them instanced, one draw per material per view (four materials). Once a second the console gets the
// frame rate and the per system timings (World.Stats, Renderer.InstancingStats).
// With --layers every other cube goes on render layer 2 with the demo's cube (hide-cube: they disappear, their
// shadows stay; only-cube: only they and the cube show). With --predraw about 200 of the cubes get the demo's OnPreDraw
// behaviour (quietly, the demo cube logs): drawn blue, blinking or with the destroyed material, each through its own
// override draw.
internal static class EntitiesDemo
{
    private struct Spin : IComponentData
    {
        public Vector3 Axis;
        public float Speed; // rad/s
    }

    private struct Orbit : IComponentData
    {
        public Vector3 Center;
        public float Radius, Speed, Phase;
    }

    private struct Bob : IComponentData
    {
        public float Height, Speed, Phase;
    }

    private sealed class SpinBehaviour : BatchedBehaviour<Spin>, IBatchedUpdate
    {
        public void Update(in BatchContext context, in ChunkView chunk)
        {
            ReadOnlySpan<Spin> spins = chunk.Get<Spin>();
            Span<Quaternion> rotations = chunk.Rotations;
            float dt = context.DeltaTime;
            for (int i = 0; i < chunk.Count; i++)
            {
                if (!chunk.AllEnabled && !chunk.IsEnabled(i))
                    continue;
                rotations[i] = Quaternion.Normalize(rotations[i] * Quaternion.CreateFromAxisAngle(spins[i].Axis, spins[i].Speed * dt));
            }
        }
    }

    private sealed class OrbitBehaviour : BatchedBehaviour<Orbit>, IBatchedUpdate
    {
        public void Update(in BatchContext context, in ChunkView chunk)
        {
            ReadOnlySpan<Orbit> orbits = chunk.Get<Orbit>();
            Span<Vector3> positions = chunk.Positions;
            float time = (float)context.Time;
            for (int i = 0; i < chunk.Count; i++)
            {
                Orbit o = orbits[i];
                float angle = o.Phase + o.Speed * time;
                positions[i] = new Vector3(o.Center.X + o.Radius * MathF.Cos(angle), positions[i].Y, o.Center.Z + o.Radius * MathF.Sin(angle));
            }
        }
    }

    private sealed class BobBehaviour : BatchedBehaviour<Bob>, IBatchedLateUpdate
    {
        public void LateUpdate(in BatchContext context, in ChunkView chunk)
        {
            ReadOnlySpan<Bob> bobs = chunk.Get<Bob>();
            Span<Vector3> positions = chunk.Positions;
            float time = (float)context.Time;
            for (int i = 0; i < chunk.Count; i++)
                positions[i].Y = Ground + 0.25f + bobs[i].Height * (0.5f + 0.5f * MathF.Sin(bobs[i].Phase + bobs[i].Speed * time));
        }
    }

    private const float Ground = -1.0f;
    private const float CubeSize = 0.4f;

    /// <summary>The side of the square field the cubes start on, in metres.</summary>
    public static float FieldSize(int count) => MathF.Max(40.0f, MathF.Sqrt(count) * 1.1f);

    /// <summary>Spawns the cubes and registers their behaviours; <paramref name="layer_two"/> puts every other one on
    /// layer 2, <paramref name="pre_draw"/> (a factory) gives about 200 of them a class behaviour.</summary>
    public static void Populate(int count, bool layer_two, Func<Behaviour>? pre_draw)
    {
        var clock = Stopwatch.StartNew();
        EntityStore store = World.World.Store;
        var materials = new Material[4];
        var colors = new[] { new Vector3(0.9f, 0.55f, 0.2f), new Vector3(0.25f, 0.7f, 0.3f), new Vector3(0.85f, 0.85f, 0.8f), new Vector3(0.7f, 0.2f, 0.25f) };
        var batches = new RenderBatch[4];
        for (int m = 0; m < materials.Length; m++)
        {
            materials[m] = new Material();
            materials[m].SetResourceName($"entities_{m}");
            materials[m].SetProperty(MaterialProperty.ColorR, colors[m].X);
            materials[m].SetProperty(MaterialProperty.ColorG, colors[m].Y);
            materials[m].SetProperty(MaterialProperty.ColorB, colors[m].Z);
            materials[m].SetProperty(MaterialProperty.ColorA, 1.0f);
            materials[m].SetProperty(MaterialProperty.Roughness, 0.6f);
            batches[m] = RenderBatches.Get(MeshType.Cube, materials[m]);
        }

        EntityArchetype archetype = EntityArchetype.Empty.Renderable().With<Spin>().With<Orbit>().With<Bob>();
        float field = FieldSize(count);
        int side = (int)MathF.Ceiling(MathF.Sqrt(count));
        float spacing = field / side;
        var random = new Random(1234);
        int stride = pre_draw != null ? System.Math.Max(1, count / 200) : 0;
        var ids = new EntityId[1];
        for (int i = 0; i < count; i++)
        {
            float x = (i % side + 0.5f) * spacing - field * 0.5f + (float)(random.NextDouble() - 0.5) * spacing;
            float z = (i / side + 0.5f) * spacing - 10.0f + (float)(random.NextDouble() - 0.5) * spacing;
            var spawn = new EntitySpawn(new Vector3(x, Ground + 0.25f, z), batches[i & 3])
            {
                Rotation = Quaternion.CreateFromYawPitchRoll((float)random.NextDouble() * 6.28f, (float)random.NextDouble() * 6.28f, 0.0f),
                Scale    = new Vector3(CubeSize),
                Layers   = layer_two && (i & 1) == 1 ? 1u << 1 : 1u
            };
            EntityId id = store.Spawn(archetype, spawn);
            var axis = Vector3.Normalize(new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() + 0.2f, (float)random.NextDouble() - 0.5f));
            store.Set(id, new Spin { Axis = axis, Speed = 0.5f + 2.5f * (float)random.NextDouble() });
            store.Set(id, new Orbit { Center = new Vector3(x, 0, z), Radius = 0.2f + 0.8f * (float)random.NextDouble() * spacing, Speed = 0.3f + 1.2f * (float)random.NextDouble(), Phase = (float)random.NextDouble() * 6.28f });
            store.Set(id, new Bob { Height = 0.6f * (float)random.NextDouble(), Speed = 1.0f + 2.0f * (float)random.NextDouble(), Phase = (float)random.NextDouble() * 6.28f });
            if (stride > 0 && i % stride == 0)
            {
                Entity facade = World.World.GetEntity(id)!; // a class Entity for this one store entity, made on demand
                facade.SetScale(new Vector3(2.5f * CubeSize));
                facade.AddBehaviour(pre_draw!());
            }
        }
        World.World.RegisterBatched(new SpinBehaviour());
        World.World.RegisterBatched(new OrbitBehaviour());
        World.World.RegisterBatched(new BobBehaviour());
        Console.WriteLine(FormattableString.Invariant($"entities: spawned {count} cubes on a {field:0} m field in {clock.Elapsed.TotalMilliseconds:0} ms ({(stride > 0 ? (count + stride - 1) / stride : 0)} with an OnPreDraw behaviour)"));
    }

    /// <summary>Accumulates the per frame numbers and prints their averages once a second.</summary>
    public sealed class Report
    {
        private readonly Stopwatch m_second = Stopwatch.StartNew();
        private readonly int m_count;
        private int m_frames;
        private double m_frame_ms, m_frame_max, m_tick, m_fixed, m_update, m_late, m_class, m_playback, m_transforms;
        private double m_cull, m_shadow_cull, m_lists, m_upload;
        private long m_uploaded;

        public Report(int count) { m_count = count; }

        public void AddFrame(double frame_ms)
        {
            m_frames++;
            m_frame_ms += frame_ms;
            m_frame_max = System.Math.Max(m_frame_max, frame_ms);
            m_tick += World.World.Stats.TickMs;
            m_fixed += World.World.Stats.FixedUpdateMs;
            m_update += World.World.Stats.UpdateMs;
            m_late += World.World.Stats.LateUpdateMs;
            m_class += World.World.Stats.ClassBehavioursMs;
            m_playback += World.World.Stats.PlaybackMs;
            m_transforms += World.World.Stats.TransformsMs;
            m_cull += Renderer.InstancingStats.CullMs;
            m_shadow_cull += Renderer.InstancingStats.ShadowCullMs;
            m_lists += Renderer.InstancingStats.ListsMs;
            m_upload += Renderer.InstancingStats.UploadMs;
            m_uploaded += Renderer.InstancingStats.UploadedBytes;
            if (m_second.Elapsed.TotalSeconds < 1.0)
                return;

            double n = m_frames;
            double seconds = m_second.Elapsed.TotalSeconds;
            Console.WriteLine(
                FormattableString.Invariant($"entities {m_count}: {m_frames / seconds:0.0} fps, frame {m_frame_ms / n:0.00} ms (max {m_frame_max:0.00}) | ") +
                FormattableString.Invariant($"world tick {m_tick / n:0.00} ms (fixed {m_fixed / n:0.00}, update {m_update / n:0.00}, late {m_late / n:0.00}, class {m_class / n:0.00}, playback {m_playback / n:0.00}, transforms {m_transforms / n:0.00}) | ") +
                FormattableString.Invariant($"render cull {m_cull / n:0.00} ms, shadow cull {m_shadow_cull / n:0.00} ms, lists {m_lists / n:0.00} ms, upload {m_upload / n:0.00} ms ({m_uploaded / n / 1048576.0:0.0} MB) | ") +
                FormattableString.Invariant($"visible {Renderer.InstancingStats.VisibleInstances}, shadow {Renderer.InstancingStats.ShadowInstances}, batches {Renderer.InstancingStats.Batches}, draws {Renderer.InstancingStats.Draws} | ") +
                FormattableString.Invariant($"memory {GC.GetTotalMemory(false) / 1048576.0:0} MB managed, {Environment.WorkingSet / 1048576.0:0} MB working set"));
            m_frames = 0;
            m_frame_ms = m_frame_max = m_tick = m_fixed = m_update = m_late = m_class = m_playback = m_transforms = 0;
            m_cull = m_shadow_cull = m_lists = m_upload = 0;
            m_uploaded = 0;
            m_second.Restart();
        }
    }
}
