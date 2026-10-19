using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    internal enum MixerMode { None, Sculpt, Weights, Armature }

    /// <summary>
    /// Mixer's hand in the Scene view: while a mode is on, the pointer over an edited mesh shows the brush, a drag is
    /// a stroke, and the keys of Blender apply ([ and ] for the radius, Shift+wheel for the strength, Ctrl inverts,
    /// Shift smooths, Esc leaves). Strokes edit the document through <see cref="SculptStroke"/>; this class only
    /// turns events into steps and draws.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerSceneTool
    {
        private const string RadiusPref = "Orbiters.Mixer.Brush.Radius";
        private const string StrengthPref = "Orbiters.Mixer.Brush.Strength";
        private const string FalloffPref = "Orbiters.Mixer.Brush.Falloff";
        private const string SymmetryPref = "Orbiters.Mixer.Brush.Symmetry";
        private const string BrushPref = "Orbiters.Mixer.Brush.Name";
        private static readonly int ControlHint = "Orbiters.Mixer.SceneTool".GetHashCode();
        private static readonly Color Accent = new Color(0f, 0.855f, 0.427f, 1f);

        private static MixerMode mode;
        private static Brush brush;
        private static SculptStroke stroke;
        private static bool hovering;
        private static Vector3 hoverPoint, hoverNormal;
        private static EditTarget hoverTarget;
        private static double lastWorldRefresh;
        private static Vector2 lastMouse;

        public static event Action Changed;

        static MixerSceneTool()
        {
            SceneView.duringSceneGui += OnSceneGui;
            brush = Brushes.Named(EditorPrefs.GetString(BrushPref, "Grab"));
        }

        public static MixerMode Mode
        {
            get => mode;
            set
            {
                if (mode == value) return;
                EndStroke(cancel: true);
                mode = value;
                hovering = false;
                Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        public static Brush ActiveBrush
        {
            get => brush;
            set { brush = value ?? Brushes.All[0]; EditorPrefs.SetString(BrushPref, brush.Name); Changed?.Invoke(); }
        }

        /// <summary>Brush radius in world units (metres).</summary>
        public static float Radius
        {
            get => EditorPrefs.GetFloat(RadiusPref, 0.05f);
            set { EditorPrefs.SetFloat(RadiusPref, Mathf.Clamp(value, 0.001f, 5f)); Changed?.Invoke(); SceneView.RepaintAll(); }
        }

        public static float Strength
        {
            get => EditorPrefs.GetFloat(StrengthPref, 0.5f);
            set { EditorPrefs.SetFloat(StrengthPref, Mathf.Clamp01(value)); Changed?.Invoke(); }
        }

        public static Falloff Falloff
        {
            get => (Falloff)EditorPrefs.GetInt(FalloffPref, 0);
            set { EditorPrefs.SetInt(FalloffPref, (int)value); Changed?.Invoke(); }
        }

        public static bool Symmetry
        {
            get => EditorPrefs.GetBool(SymmetryPref, true);
            set { EditorPrefs.SetBool(SymmetryPref, value); Changed?.Invoke(); }
        }

        public static bool Stroking => stroke != null;
        public static bool Hovering => hovering;
        public static EditTarget HoverTarget => hoverTarget;

        private static void OnSceneGui(SceneView view)
        {
            if (mode != MixerMode.Sculpt || !MixerSession.Active) { if (hovering) { hovering = false; } return; }
            var e = Event.current;
            int control = GUIUtility.GetControlID(ControlHint, FocusType.Passive);
            switch (e.GetTypeForControl(control))
            {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(control);
                    break;
                case EventType.MouseMove:
                    Hover(e.mousePosition, true);
                    view.Repaint();
                    break;
                case EventType.MouseDown:
                    if (e.button != 0 || e.alt) break;
                    Hover(e.mousePosition, true);
                    if (!hovering) break;
                    GUIUtility.hotControl = control;
                    BeginStroke(e, view);
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != control || stroke == null) break;
                    Hover(e.mousePosition, false);
                    stroke.Step(HandleUtility.GUIPointToWorldRay(e.mousePosition), hovering ? hoverPoint : (Vector3?)null, hovering ? hoverNormal : (Vector3?)null, e.control, e.shift);
                    e.Use();
                    view.Repaint();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != control) break;
                    GUIUtility.hotControl = 0;
                    EndStroke(cancel: false);
                    e.Use();
                    break;
                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.Escape && stroke == null) { Mode = MixerMode.None; e.Use(); }
                    else if (e.keyCode == KeyCode.LeftBracket) { Radius = Radius / 1.25f; e.Use(); }
                    else if (e.keyCode == KeyCode.RightBracket) { Radius = Radius * 1.25f; e.Use(); }
                    else if (e.keyCode == KeyCode.F) { Symmetry = !Symmetry; e.Use(); }
                    break;
                case EventType.ScrollWheel:
                    if (e.shift) { Strength = Strength - Mathf.Sign(e.delta.y) * 0.05f; e.Use(); }
                    break;
                case EventType.Repaint:
                    Draw(view);
                    break;
            }
        }

        /// <summary>Finds what is under a Scene view point among the edited meshes.</summary>
        private static void Hover(Vector2 guiPoint, bool refreshWorld)
        {
            lastMouse = guiPoint;
            var ray = HandleUtility.GUIPointToWorldRay(guiPoint);
            hovering = false;
            float best = float.MaxValue;
            double now = EditorApplication.timeSinceStartup;
            foreach (var target in MixerSession.Targets)
            {
                if (!target.Alive) continue;
                if (refreshWorld && stroke == null && now - lastWorldRefresh > 0.25) target.RefreshWorld();
                var hit = target.Bvh.Raycast(ray.origin, ray.direction, 1000f, backfaces: false);
                if (!hit.Found || hit.Distance >= best) continue;
                best = hit.Distance;
                hovering = true;
                hoverPoint = hit.Position;
                hoverNormal = target.Bvh.FaceNormal(hit.Triangle);
                hoverTarget = target;
            }
            if (refreshWorld && stroke == null && now - lastWorldRefresh > 0.25) lastWorldRefresh = now;
        }

        private static void BeginStroke(Event e, SceneView view)
        {
            if (hoverTarget == null) return;
            hoverTarget.RefreshWorld();
            Hover(e.mousePosition, false);
            if (!hovering) return;
            var effective = e.shift && !(brush is SmoothBrush) ? Brushes.Named("Smooth") : brush;
            stroke = new SculptStroke(hoverTarget, effective, Radius, Strength, Falloff, Symmetry, view.camera != null ? view.camera.transform.forward : Vector3.forward);
            stroke.Begin(hoverPoint, hoverNormal, HandleUtility.GUIPointToWorldRay(e.mousePosition), e.control);
        }

        private static void EndStroke(bool cancel)
        {
            if (stroke == null) return;
            var s = stroke;
            stroke = null;
            s.End(cancel);
            Changed?.Invoke();
        }

        /// <summary>A stroke driven by code (tests, the MCP tool): from a world point along to another.</summary>
        internal static PositionEdit StrokeBetween(EditTarget target, Brush with, Vector3 from, Vector3 to, int steps, float radius, float strength, Vector3 viewDirection, bool invert = false)
        {
            var s = new SculptStroke(target, with, radius, strength, Falloff, Symmetry, viewDirection);
            var hit = target.Bvh.ClosestPoint(from, radius * 4f);
            if (!hit.Found) return null;
            s.Begin(hit.Position, target.Bvh.FaceNormal(hit.Triangle), new Ray(hit.Position - viewDirection, viewDirection), invert);
            for (int i = 1; i <= steps; i++)
            {
                var p = Vector3.Lerp(from, to, i / (float)steps);
                var h = target.Bvh.ClosestPoint(p, radius * 4f);
                s.Step(new Ray(p - viewDirection, viewDirection), h.Found ? h.Position : (Vector3?)null, h.Found ? target.Bvh.FaceNormal(h.Triangle) : (Vector3?)null, invert, false);
            }
            return s.End(false);
        }

        private static void Draw(SceneView view)
        {
            if (!hovering && stroke == null) return;
            var center = stroke != null ? stroke.Center : hoverPoint;
            var normal = stroke != null ? stroke.Normal : hoverNormal;
            float radius = stroke != null ? stroke.Radius : Radius;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            Handles.color = new Color(Accent.r, Accent.g, Accent.b, stroke != null ? 1f : 0.85f);
            Handles.DrawWireDisc(center, normal, radius, 2f);
            Handles.color = new Color(Accent.r, Accent.g, Accent.b, 0.45f);
            Handles.DrawWireDisc(center, normal, radius * Mathf.Lerp(0.15f, 0.9f, Strength), 1f);
            Handles.DrawLine(center, center + normal * radius * 0.5f, 1f);
            if (Symmetry && hoverTarget != null && hoverTarget.Alive)
            {
                var local = hoverTarget.Renderer.transform.worldToLocalMatrix.MultiplyPoint3x4(center);
                local.x = -local.x;
                var mirrored = hoverTarget.Renderer.transform.localToWorldMatrix.MultiplyPoint3x4(local);
                Handles.color = new Color(Accent.r, Accent.g, Accent.b, 0.35f);
                Handles.DrawWireDisc(mirrored, normal, radius, 1f);
            }
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
        }
    }
}
