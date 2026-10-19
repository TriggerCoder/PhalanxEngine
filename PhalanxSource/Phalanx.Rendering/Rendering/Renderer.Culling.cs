// What every view of the frame draws, and the per view callbacks of the behaviours. Spartan @ 4c07baa culls each
// renderable once, against the one camera, in Render::UpdateFrustumAndDistanceCulling (Render.cpp:417-475: distance,
// then frustum, into is_visible), and the draw call build keeps visible renderables and shadow casters
// (Renderer.cpp:3686). Phalanx culls once per view.
// Deviations:
// - Per view: a renderable is drawn by a view when its world aabb (the mesh aabb under the entity's current matrix)
//   intersects the view camera's frustum and its Render.RenderLayers share a bit with the view's cull mask (its
//   SceneView.CullMask and its camera's Camera.CullMask together; Godot's render layers and cull mask, not in
//   spartan). No distance culling (max_distance_render), occlusion or lod selection yet.
// - The shadow maps are shared by the views and draw every caster, whatever its layers or the views' frustums.
// - IPreDraw.OnPreDraw (the Unity idea of a per camera callback, not in spartan) runs on the main thread after a
//   view culled, before any view draws: every view of the frame is culled and called back first, then the uploads
//   (so a per view material is in the material table) and the draws. OnBecameVisible / OnBecameInvisible follow the
//   union of the views. World changes made in these callbacks are deferred to the next world tick.
using System.Numerics;
using Phalanx.Math;
using Phalanx.World;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    // a renderable that a view may draw this frame: active, with geometry, with its aabb as of now
    private struct CullCandidate
    {
        public Entity entity;
        public Render render;
        public BoundingBox box;
    }
    private static readonly List<CullCandidate> s_cull_candidates = new();
    // the views culled for this frame, in draw order
    private static readonly List<SceneView> s_prepared_views = new();
    private static readonly List<SceneView> s_rendered_views = new();

    // the views of this frame are culled and their callbacks run (Tick, before the uploads)
    private static void PrepareViews()
    {
        s_rendered_views.Clear();
        s_rendered_views.AddRange(RenderedViews());
        PrepareViews(s_rendered_views);
    }

    // culls each view, then runs its OnPreDraw callbacks, then the visibility callbacks of the frame; tests call it
    // with views that aren't rendered yet
    internal static void PrepareViews(IReadOnlyList<SceneView> views)
    {
        GatherCullCandidates();
        s_prepared_views.Clear();
        CullStoreForViews(views); // the store's instanced entities, every view at once (Renderer.Instancing.cs)
        Phalanx.World.World.BeginRenderCallbacks();
        try
        {
            for (int i = 0; i < views.Count; i++)
            {
                SceneView view = views[i];
                CullView(view);
                RunPreDraw(view, i);
                RunStorePreDraw(view, i);
                s_prepared_views.Add(view);
            }

            // the union of the views (a renderable hidden by every view that culled it in isn't visible)
            foreach (SceneView view in s_prepared_views)
            {
                ViewState s = view.State;
                foreach (KeyValuePair<Render, int> entry in s.Visible)
                {
                    if (entry.Value >= 0 && s.Overrides[entry.Value].Hidden)
                        continue;
                    Phalanx.World.World.MarkVisible(entry.Key.GetEntity()!);
                }
            }
            Phalanx.World.World.CommitVisibility();
        }
        finally
        {
            Phalanx.World.World.EndRenderCallbacks();
        }
    }

    private static void GatherCullCandidates()
    {
        s_cull_candidates.Clear();
        foreach (Entity entity in Phalanx.World.World.GetEntitiesRenderables())
        {
            if (!entity.GetActive() || entity.GetComponent<Render>() is not Render render || render.GetMesh() == null || render.GetIndexCount() == 0)
                continue;
            // the box as of now: Render.Tick refreshes its own one in the world tick, before late transform changes
            s_cull_candidates.Add(new CullCandidate { entity = entity, render = render, box = render.GetBoundingBoxMesh() * entity.GetMatrix() });
        }
    }

    // the effective cull mask of a view: its own and its camera's
    internal static uint GetCullMask(SceneView view) => view.CullMask & (view.GetCamera()?.CullMask ?? uint.MaxValue);

    // the renderables the view draws: in the frustum of its camera and on one of its layers
    private static void CullView(SceneView view)
    {
        ViewState s = view.State;
        s.Visible.Clear();
        s.OverrideCount = 0;
        Camera? camera = view.GetCamera();
        uint mask = GetCullMask(view);
        // from the matrices the view draws with (the frame constants take the same ones, without the jitter). Like
        // spartan's Frustum (Frustum.cpp:30-31) the planes come from c3 + c2 and c3 - c2, which with the reverse-z
        // projection are a plane through the camera and the near plane: there is no far plane culling.
        Frustum frustum = camera != null ? new Frustum(camera.GetViewMatrix(), camera.GetProjectionMatrix()) : default;
        foreach (CullCandidate candidate in s_cull_candidates)
        {
            if ((candidate.render.RenderLayers & mask) == 0)
                continue;
            // without a camera there is no frustum (the frame constants keep identity), nothing is frustum culled
            if (camera != null && !frustum.IsVisible(candidate.box.GetCenter(), candidate.box.GetExtents()))
                continue;
            s.Visible.Add(candidate.render, -1);
        }
    }

    // OnPreDraw of the view's visible entities that have the callback, each with its own overrides
    private static readonly List<Render> s_pre_draw_renders = new();
    private static void RunPreDraw(SceneView view, int view_index)
    {
        ViewState s = view.State;
        Camera? camera = view.GetCamera();
        s_pre_draw_renders.Clear();
        foreach (Render render in s.Visible.Keys)
            if (render.GetEntity()!.m_pre_draw.Count > 0)
                s_pre_draw_renders.Add(render);
        foreach (Render render in s_pre_draw_renders)
        {
            if (s.OverrideCount == s.Overrides.Count)
                s.Overrides.Add(new ViewDrawOverrides());
            int index = s.OverrideCount++;
            ViewDrawOverrides overrides = s.Overrides[index];
            overrides.Reset();
            s.Visible[render] = index;
            Entity entity = render.GetEntity()!;
            var context = new ViewContext(view, view_index, camera, entity, render, overrides);
            Phalanx.World.World.InvokePreDraw(entity, in context);
        }
    }

    // the materials the views' overrides draw with, after the world's (UpdateMaterials)
    private static void AddOverrideMaterials()
    {
        foreach (SceneView view in s_prepared_views)
        {
            ViewState s = view.State;
            for (int i = 0; i < s.OverrideCount; i++)
                if (s.Overrides[i].Material is Material material)
                    AddMaterial(material); // one that can't go in draws as the illegal material
        }
    }

    // the frame's draws, every renderable whatever the views culled (the shadow maps draw these; tests)
    internal static IReadOnlyList<DrawCall> FrameDrawCalls => s_draw_calls;

    // a view's draw lists: the frame's draws it culled in, in the frame's order, its overrides applied (a draw with a
    // per view material or uv transform gets its own draw data entry)
    internal static void BuildViewDrawLists(ViewState s)
    {
        s.DrawCalls.Clear();
        s.DrawCallsAlpha.Clear();
        AddViewDraws(s, s_draw_calls);
        AddViewDraws(s, s_draw_calls_alpha);
        AddPackedOverrideDraws(s);
    }

    private static void AddViewDraws(ViewState s, List<DrawCall> draws)
    {
        foreach (DrawCall draw in draws)
        {
            if (draw.render is not Render render || !s.Visible.TryGetValue(render, out int override_index))
                continue;
            if (override_index < 0)
            {
                (draws == s_draw_calls ? s.DrawCalls : s.DrawCallsAlpha).Add(draw);
                continue;
            }
            ViewDrawOverrides overrides = s.Overrides[override_index];
            if (overrides.Hidden)
                continue;
            DrawCall view_draw = draw;
            bool alpha_tested  = draws == s_draw_calls_alpha;
            if (overrides.ChangesDraw)
            {
                Entity entity       = render.GetEntity()!;
                uint material_index = GetMaterialIndex(overrides.Material ?? GetRenderMaterial(render));
                uint draw_index     = WriteDrawData(entity.GetMatrix(), entity.GetMatrixPrevious(), material_index, 0, render, overrides);
                if (draw_index != uint.MaxValue)
                {
                    view_draw.draw_index     = draw_index;
                    view_draw.material_index = material_index;
                    alpha_tested             = IsMaterialIndexAlphaTested(material_index);
                }
            }
            (alpha_tested ? s.DrawCallsAlpha : s.DrawCalls).Add(view_draw);
        }
    }
}
