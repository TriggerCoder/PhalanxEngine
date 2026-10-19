using System.Collections.Generic;
using System.Numerics;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// the behaviour lifecycle of World (World.Behaviours.cs): order, enabling, activity, removal and deferred changes
[Collection("World")]
public class BehaviourTests
{
    private sealed class Recorder : Behaviour, IAwake, IOnEnable, IStart, IFixedUpdate, IUpdate, ILateUpdate, IOnDisable, IOnDestroy
    {
        public readonly List<string> Log;
        public readonly string Name;
        public Action<Recorder>? OnUpdate;

        public Recorder(List<string> log, string name = "")
        {
            Log  = log;
            Name = name;
        }

        private void Add(string callback) => Log.Add(Name.Length == 0 ? callback : $"{Name}.{callback}");
        public void Awake()       => Add("Awake");
        public void OnEnable()    => Add("OnEnable");
        public void Start()       => Add("Start");
        public void FixedUpdate() => Add("FixedUpdate");
        public void Update()      { Add("Update"); OnUpdate?.Invoke(this); }
        public void LateUpdate()  => Add("LateUpdate");
        public void OnDisable()   => Add("OnDisable");
        public void OnDestroy()   => Add("OnDestroy");
    }

    // only an update, nothing else is called on it
    private sealed class UpdateOnly : Behaviour, IUpdate
    {
        public int Updates;
        public void Update() => Updates++;
    }

    private static void Run(Action body)
    {
        World.World.SetMainThread();
        World.World.ClearScene();
        double step = World.World.FixedTimeStep;
        try
        {
            body();
        }
        finally
        {
            World.World.FixedTimeStep = step;
            World.World.ClearScene();
        }
    }

    [Fact]
    public void Callbacks_run_in_lifecycle_order()
    {
        Run(() =>
        {
            var log = new List<string>();
            World.World.FixedTimeStep = 0.5;
            Entity entity = World.World.CreateEntity();
            var recorder = entity.AddBehaviour(new Recorder(log));
            Assert.Same(entity, recorder.Entity);
            Assert.Same(recorder, entity.GetBehaviour<Recorder>());
            Assert.Same(recorder, entity.GetBehaviour<IUpdate>());
            Assert.NotNull(entity.GetComponent<Script>());
            Assert.Empty(log); // the entity isn't in the world until the next tick

            World.World.Tick(0.0); // the entity arrives: awake and enable, no updates yet
            Assert.Equal(new[] { "Awake", "OnEnable" }, log);
            Assert.True(recorder.IsActiveAndEnabled);

            log.Clear();
            World.World.Tick(1.0); // start, two fixed steps of 0.5 s, update, late update
            Assert.Equal(new[] { "Start", "FixedUpdate", "FixedUpdate", "Update", "LateUpdate" }, log);
            Assert.Equal(1.0, World.World.DeltaTime);

            log.Clear();
            World.World.Tick(0.25); // a quarter of a step: no fixed update
            World.World.Tick(0.25); // half a step accumulated... and another quarter makes one
            Assert.Equal(new[] { "Update", "LateUpdate", "FixedUpdate", "Update", "LateUpdate" }, log);

            log.Clear();
            recorder.Enabled = false;
            World.World.Tick(0.0);
            recorder.Enabled = true; // enabled again: no second Awake or Start
            World.World.Tick(0.0);
            Assert.Equal(new[] { "OnDisable", "OnEnable", "Update", "LateUpdate" }, log);

            log.Clear();
            World.World.RemoveEntity(entity);
            Assert.False(recorder.IsDestroyed); // removal waits for the next tick
            Assert.Empty(log);
            World.World.Tick(0.0);
            Assert.Equal(new[] { "OnDisable", "OnDestroy" }, log);
            Assert.True(recorder.IsDestroyed);
        });
    }

    [Fact]
    public void A_disabled_behaviour_awakes_but_neither_enables_nor_starts_until_enabled()
    {
        Run(() =>
        {
            var log = new List<string>();
            Entity entity = World.World.CreateEntity();
            var recorder = new Recorder(log) { Enabled = false };
            entity.AddBehaviour(recorder);
            World.World.Tick(0.0);
            World.World.Tick(0.0);
            Assert.Equal(new[] { "Awake" }, log);

            recorder.Enabled = true;
            World.World.Tick(0.0);
            Assert.Equal(new[] { "Awake", "OnEnable", "Start", "Update", "LateUpdate" }, log);
        });
    }

    [Fact]
    public void Activity_is_inherited_and_awake_waits_for_an_active_entity()
    {
        Run(() =>
        {
            var log = new List<string>();
            Entity parent = World.World.CreateEntity();
            Entity child  = World.World.CreateEntity();
            child.SetParent(parent);
            parent.SetActive(false);
            child.AddBehaviour(new Recorder(log));
            World.World.Tick(0.0);
            Assert.Empty(log); // in the world but inactive through its parent

            parent.SetActive(true);
            Assert.Equal(new[] { "Awake", "OnEnable" }, log);
            World.World.Tick(0.0);
            log.Clear();

            parent.SetActive(false);
            World.World.Tick(0.0);
            Assert.Equal(new[] { "OnDisable" }, log);

            log.Clear();
            child.SetParent(null); // its own activity again
            World.World.Tick(0.0);
            Assert.Equal(new[] { "OnEnable", "Update", "LateUpdate" }, log);
        });
    }

    [Fact]
    public void Behaviours_added_or_removed_during_an_update_take_effect_safely()
    {
        Run(() =>
        {
            var log = new List<string>();
            Entity entity = World.World.CreateEntity();
            var a = entity.AddBehaviour(new Recorder(log, "a"));
            var b = entity.AddBehaviour(new Recorder(log, "b"));
            World.World.Tick(0.0);
            World.World.Tick(0.0);

            // a removes b and adds c during its update: b gets no update, c starts next tick
            Recorder? c = null;
            a.OnUpdate = self =>
            {
                self.OnUpdate = null;
                self.Entity.RemoveBehaviour(b);
                c = self.Entity.AddBehaviour(new Recorder(log, "c"));
            };
            log.Clear();
            World.World.Tick(0.0);
            Assert.Equal(new[] { "a.Update", "b.OnDisable", "b.OnDestroy", "c.Awake", "c.OnEnable", "a.LateUpdate" }, log);

            log.Clear();
            World.World.Tick(0.0);
            Assert.Equal(new[] { "c.Start", "a.Update", "c.Update", "a.LateUpdate", "c.LateUpdate" }, log);
            Assert.Equal(new Behaviour[] { a, c! }, entity.Behaviours);
        });
    }

    [Fact]
    public void Only_implemented_callbacks_are_called_and_a_throwing_behaviour_does_not_stop_the_others()
    {
        Run(() =>
        {
            var log = new List<string>();
            Entity entity = World.World.CreateEntity();
            var thrower = entity.AddBehaviour(new Recorder(log, "x") { OnUpdate = _ => throw new InvalidOperationException("boom") });
            var counter = entity.AddBehaviour<UpdateOnly>();
            World.World.Tick(0.0);
            World.World.Tick(0.0);
            World.World.Tick(0.0);
            Assert.Equal(2, counter.Updates);
            Assert.Equal(2, log.FindAll(s => s == "x.LateUpdate").Count);
        });
    }

    [Fact]
    public void Clearing_the_scene_disables_and_destroys_behaviours()
    {
        Run(() =>
        {
            var log = new List<string>();
            Entity entity = World.World.CreateEntity();
            entity.AddBehaviour(new Recorder(log));
            World.World.Tick(0.0);
            log.Clear();
            World.World.ClearScene();
            Assert.Equal(new[] { "OnDisable", "OnDestroy" }, log);
        });
    }

    [Fact]
    public void Changes_made_in_a_render_callback_are_deferred_to_the_next_tick()
    {
        Run(() =>
        {
            var log = new List<string>();
            Entity parent = World.World.CreateEntity();
            Entity entity = World.World.CreateEntity();
            var recorder = entity.AddBehaviour(new Recorder(log));
            World.World.Tick(0.0);
            World.World.Tick(0.0);
            log.Clear();

            var order = new List<int>();
            World.World.BeginRenderCallbacks(); // what the renderer does around OnPreDraw
            try
            {
                Assert.True(World.World.IsInRenderCallback);
                entity.SetPosition(new Vector3(1, 2, 3));
                entity.SetRotation(Quaternion.CreateFromYawPitchRoll(1.0f, 0.0f, 0.0f));
                entity.SetScale(new Vector3(2, 2, 2));
                entity.SetParent(parent);
                recorder.Enabled = false;
                var added = entity.AddBehaviour(new Recorder(log, "late"));
                World.World.Defer(() => order.Add(1));
                World.World.Defer(() => order.Add(2));

                Assert.Equal(Vector3.Zero, entity.GetPosition());
                Assert.Equal(Vector3.One, entity.GetScale());
                Assert.Null(entity.GetParent());
                Assert.True(recorder.Enabled);
                Assert.Single(entity.Behaviours);
                Assert.Same(entity, added.Entity);
            }
            finally
            {
                World.World.EndRenderCallbacks();
            }
            Assert.False(World.World.IsInRenderCallback);
            Assert.Empty(log);
            Assert.Empty(order);

            World.World.Tick(0.0); // the deferred changes run first, in order, then the tick as usual
            Assert.Equal(new[] { 1, 2 }, order);
            Assert.Equal(new Vector3(1, 2, 3), entity.GetPosition());
            Assert.Same(parent, entity.GetParent());
            Assert.False(recorder.Enabled);
            Assert.Equal(2, entity.Behaviours.Count);
            Assert.Equal(new[] { "OnDisable", "late.Awake", "late.OnEnable", "late.Start", "late.Update", "late.LateUpdate" }, log);
        });
    }
}
