using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    internal enum MixerMode { None, Sculpt, Weights, Armature }

    /// <summary>
    /// Mixer's hand in the Scene view: while a mode is on, the pointer over an edited mesh shows the brush, a drag is
    /// a stroke, and the keys of Blender apply ([ and ] for the radius, Shift+wheel for the strength, Ctrl inverts,
    /// Shift smooths, Esc leaves). Sculpt strokes edit positions through <see cref="SculptStroke"/>, weight strokes
    /// the influences through <see cref="WeightStroke"/>; this class only turns events into steps and draws.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerSceneTool
    {
        private const string RadiusPref = "Orbiters.Mixer.Brush.Radius";
        private const string StrengthPref = "Orbiters.Mixer.Brush.Strength";
        private const string FalloffPref = "Orbiters.Mixer.Brush.Falloff";
        private const string SymmetryPref = "Orbiters.Mixer.Brush.Symmetry";
        private const string BrushPref = "Orbiters.Mixer.Brush.Name";
        private const string WeightKindPref = "Orbiters.Mixer.Weights.Kind";
        private const string WeightValuePref = "Orbiters.Mixer.Weights.Value";
        private const string NormalizePref = "Orbiters.Mixer.Weights.Normalize";
        private static readonly int ControlHint = "Orbiters.Mixer.SceneTool".GetHashCode();
        private static readonly Color Accent = new Color(0f, 0.855f, 0.427f, 1f);

        private static MixerMode mode;
        private static Brush brush;
        private static SculptStroke stroke;
        private static WeightStroke weightStroke;
        private static bool hovering;
        private static Vector3 hoverPoint, hoverNormal;
        private static int hoverTriangle;
        private static EditTarget hoverTarget;
        private static EditTarget focusTarget;
        private static double lastWorldRefresh;
        private static WeightChannel channel = WeightChannel.None;
        private static bool xrayWasEnabled, xrayPickingWasEnabled;
        private static XRayGizmoTargetMode xrayModeBefore;
        private static GameObject xrayPinnedBefore;

        public static event Action Changed;

        static MixerSceneTool()
        {
            SceneView.duringSceneGui += OnSceneGui;
            Selection.selectionChanged += OnSelectionChanged;
            brush = Brushes.Named(EditorPrefs.GetString(BrushPref, "Grab"));
        }

        public static MixerMode Mode
        {
            get => mode;
            set
            {
                if (mode == value) return;
                EndStroke(cancel: true);
                var previous = mode;
                mode = value;
                hovering = false;
                if (previous == MixerMode.Weights || previous == MixerMode.Armature) LeaveWeights();
                if (mode == MixerMode.Weights || mode == MixerMode.Armature) EnterWeights();
                if (previous == MixerMode.Armature) ArmatureEdit.Leave();
                Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        /// <summary>The target the window and the weight overlay follow: the last one stroked or picked.</summary>
        public static EditTarget FocusTarget
        {
            get => focusTarget != null && focusTarget.Alive && MixerSession.Targets.Contains(focusTarget) ? focusTarget : null;
            set
            {
                if (focusTarget == value) return;
                var previous = FocusTarget;
                focusTarget = value;
                if (mode == MixerMode.Weights)
                {
                    previous?.ShowChannel(WeightChannel.None);
                    FocusTarget?.ShowChannel(channel);
                    PinXRay();
                }
                Changed?.Invoke();
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

        public static WeightBrushKind WeightKind
        {
            get => (WeightBrushKind)EditorPrefs.GetInt(WeightKindPref, 0);
            set { EditorPrefs.SetInt(WeightKindPref, (int)value); Changed?.Invoke(); }
        }

        /// <summary>The weight the Draw and Replace brushes paint toward.</summary>
        public static float WeightValue
        {
            get => EditorPrefs.GetFloat(WeightValuePref, 1f);
            set { EditorPrefs.SetFloat(WeightValuePref, Mathf.Clamp01(value)); Changed?.Invoke(); }
        }

        public static bool AutoNormalize
        {
            get => EditorPrefs.GetBool(NormalizePref, true);
            set { EditorPrefs.SetBool(NormalizePref, value); Changed?.Invoke(); }
        }

        /// <summary>The bone or vertex group weight strokes paint.</summary>
        public static WeightChannel Channel => channel;

        public static void SetChannel(WeightChannel value)
        {
            channel = value;
            if (mode == MixerMode.Weights) FocusTarget?.ShowChannel(channel);
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        public static bool Stroking => stroke != null || weightStroke != null;
        public static bool Hovering => hovering;
        public static EditTarget HoverTarget => hoverTarget;
        /// <summary>The channel's weight under the cursor (nearest vertex of the hovered triangle), or -1 off the mesh.</summary>
        public static float HoverWeight { get; private set; } = -1f;

        private static void OnSceneGui(SceneView view)
        {
            if ((mode != MixerMode.Sculpt && mode != MixerMode.Weights) || !MixerSession.Active) { if (hovering) hovering = false; return; }
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
                    if (mode == MixerMode.Weights && channel.IsNone) { MixerSession.Say("Pick a bone or a vertex group to paint first.", true); break; }
                    GUIUtility.hotControl = control;
                    BeginStroke(e, view);
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != control || !Stroking) break;
                    Hover(e.mousePosition, false);
                    if (stroke != null) stroke.Step(HandleUtility.GUIPointToWorldRay(e.mousePosition), hovering ? hoverPoint : (Vector3?)null, hovering ? hoverNormal : (Vector3?)null, e.control, e.shift);
                    else weightStroke.Step(hovering ? hoverPoint : (Vector3?)null, hovering ? hoverNormal : (Vector3?)null, e.control);
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
                    if (e.keyCode == KeyCode.Escape && !Stroking) { Mode = MixerMode.None; e.Use(); }
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
            var ray = HandleUtility.GUIPointToWorldRay(guiPoint);
            hovering = false;
            float best = float.MaxValue;
            double now = EditorApplication.timeSinceStartup;
            bool refresh = refreshWorld && !Stroking && now - lastWorldRefresh > 0.25;
            foreach (var target in MixerSession.Targets)
            {
                if (!target.Alive) continue;
                if (refresh) target.RefreshWorld();
                var hit = target.Bvh.Raycast(ray.origin, ray.direction, 1000f, backfaces: false);
                if (!hit.Found || hit.Distance >= best) continue;
                best = hit.Distance;
                hovering = true;
                hoverPoint = hit.Position;
                hoverNormal = target.Bvh.FaceNormal(hit.Triangle);
                hoverTriangle = hit.Triangle;
                hoverTarget = target;
            }
            if (refresh) lastWorldRefresh = now;
            HoverWeight = -1f;
            if (hovering && mode == MixerMode.Weights && !channel.IsNone)
            {
                var doc = hoverTarget.Document;
                int t = hoverTriangle * 3, nearest = doc.Triangles[t];
                float d = float.MaxValue;
                for (int k = 0; k < 3; k++)
                {
                    int v = doc.Triangles[t + k];
                    float dv = (hoverTarget.World[v] - hoverPoint).sqrMagnitude;
                    if (dv < d) { d = dv; nearest = v; }
                }
                if (channel.IsGroup) { var g = doc.Group(channel.Group); HoverWeight = g != null && g.Weights.TryGetValue(nearest, out float w) ? w : 0f; }
                else HoverWeight = doc.BoneWeight(nearest, channel.Bone);
            }
        }

        private static void BeginStroke(Event e, SceneView view)
        {
            if (hoverTarget == null) return;
            hoverTarget.RefreshWorld();
            Hover(e.mousePosition, false);
            if (!hovering) return;
            if (MixerLinkService.IsBlenderOwned(hoverTarget)) { MixerSession.Say(hoverTarget.Name + " is being edited in Blender: leave its edit mode there first.", true); return; }
            FocusTarget = hoverTarget;
            if (mode == MixerMode.Weights)
            {
                if (!channel.IsGroup && !hoverTarget.Document.HasSkin) { MixerSession.Say(hoverTarget.Name + " has no bones to weight; paint a vertex group instead.", true); return; }
                weightStroke = new WeightStroke(hoverTarget, WeightKind, channel, Radius, Strength, WeightValue, Falloff, Symmetry, AutoNormalize);
                weightStroke.Begin(hoverPoint, hoverNormal, e.control);
                return;
            }
            var effective = e.shift && !(brush is SmoothBrush) ? Brushes.Named("Smooth") : brush;
            stroke = new SculptStroke(hoverTarget, effective, Radius, Strength, Falloff, Symmetry, view.camera != null ? view.camera.transform.forward : Vector3.forward);
            stroke.Begin(hoverPoint, hoverNormal, HandleUtility.GUIPointToWorldRay(e.mousePosition), e.control);
        }

        private static void EndStroke(bool cancel)
        {
            if (stroke != null)
            {
                var s = stroke;
                stroke = null;
                s.End(cancel);
            }
            if (weightStroke != null)
            {
                var w = weightStroke;
                weightStroke = null;
                w.End(cancel);
            }
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

        /// <summary>A weight stroke driven by code.</summary>
        internal static HistoryEntry WeightStrokeBetween(EditTarget target, WeightBrushKind kind, WeightChannel on, Vector3 from, Vector3 to, int steps, float radius, float strength, float value, bool normalize, bool invert = false)
        {
            var s = new WeightStroke(target, kind, on, radius, strength, value, Falloff, Symmetry, normalize);
            var hit = target.Bvh.ClosestPoint(from, radius * 4f);
            if (!hit.Found) return null;
            s.Begin(hit.Position, target.Bvh.FaceNormal(hit.Triangle), invert);
            for (int i = 1; i <= steps; i++)
            {
                var p = Vector3.Lerp(from, to, i / (float)steps);
                var h = target.Bvh.ClosestPoint(p, radius * 4f);
                s.Step(h.Found ? h.Position : (Vector3?)null, h.Found ? target.Bvh.FaceNormal(h.Triangle) : (Vector3?)null, invert);
            }
            return s.End(false);
        }

        // ---- Weights mode and the XRay gizmos ------------------------------------------------------------------------

        private static void EnterWeights()
        {
            if (xrayPinnedByMixer) return;
            xrayPinnedByMixer = true;
            xrayWasEnabled = XRayGizmoService.Enabled;
            xrayModeBefore = XRayGizmoService.TargetMode;
            xrayPinnedBefore = XRayGizmoService.PinnedObject;
            xrayPickingWasEnabled = XRayBonePickingService.Enabled;
            PinXRay();
            XRayBonePickingService.SetEnabled(true);
            FocusTarget?.ShowChannel(channel);
        }

        private static void PinXRay()
        {
            var target = FocusTarget ?? MixerSession.First;
            if (target == null || target.Skinned == null) return;
            XRayGizmoService.SetTargetMode(XRayGizmoTargetMode.PinnedObject);
            XRayGizmoService.SetPinnedObject(target.Renderer.transform.root.gameObject);
            XRayGizmoService.SetEnabled(true);
        }

        private static bool xrayPinnedByMixer;

        private static void LeaveWeights()
        {
            if (mode == MixerMode.Weights || mode == MixerMode.Armature) return; // one bone mode to the other: keep the gizmos
            xrayPinnedByMixer = false;
            foreach (var t in MixerSession.Targets) t.ShowChannel(WeightChannel.None);
            XRayWeightPaintService.Unpin();
            XRayGizmoService.SetTargetMode(xrayModeBefore);
            if (xrayPinnedBefore != null) XRayGizmoService.SetPinnedObject(xrayPinnedBefore);
            XRayGizmoService.SetEnabled(xrayWasEnabled);
            XRayBonePickingService.SetEnabled(xrayPickingWasEnabled);
        }

        // A bone of the focus target picked in the Scene view (XRay's bone picking) or the Hierarchy becomes the channel.
        private static void OnSelectionChanged()
        {
            if (mode != MixerMode.Weights && mode != MixerMode.Armature) return;
            var target = FocusTarget ?? MixerSession.First;
            var picked = Selection.activeTransform;
            if (target == null || picked == null || target.Skinned == null) return;
            var bones = target.Skinned.bones;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] == picked) { if (FocusTarget == null) FocusTarget = target; if (mode == MixerMode.Weights) SetChannel(WeightChannel.OfBone(i)); else ArmatureEdit.SelectedBone = i; return; }
        }

        private static void Draw(SceneView view)
        {
            if (!hovering && !Stroking) return;
            var center = stroke != null ? stroke.Center : weightStroke != null ? weightStroke.Center : hoverPoint;
            var normal = stroke != null ? stroke.Normal : weightStroke != null ? weightStroke.Normal : hoverNormal;
            float radius = stroke != null ? stroke.Radius : Radius;
            var color = mode == MixerMode.Weights && HoverWeight >= 0f ? WeightColor(HoverWeight) : Accent;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            Handles.color = new Color(color.r, color.g, color.b, Stroking ? 1f : 0.85f);
            Handles.DrawWireDisc(center, normal, radius, 2f);
            Handles.color = new Color(color.r, color.g, color.b, 0.45f);
            Handles.DrawWireDisc(center, normal, radius * Mathf.Lerp(0.15f, 0.9f, Strength), 1f);
            Handles.DrawLine(center, center + normal * radius * 0.5f, 1f);
            if (Symmetry && hoverTarget != null && hoverTarget.Alive)
            {
                var local = hoverTarget.Renderer.transform.worldToLocalMatrix.MultiplyPoint3x4(center);
                local.x = -local.x;
                var mirrored = hoverTarget.Renderer.transform.localToWorldMatrix.MultiplyPoint3x4(local);
                Handles.color = new Color(color.r, color.g, color.b, 0.35f);
                Handles.DrawWireDisc(mirrored, normal, radius, 1f);
            }
            if (mode == MixerMode.Weights && HoverWeight >= 0f)
            {
                var style = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = Color.white } };
                Handles.Label(center + view.camera.transform.right * radius * 1.1f, HoverWeight.ToString("0.00"), style);
            }
            if (mode == MixerMode.Sculpt) DrawMask(view, center, radius);
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
        }

        /// <summary>Blender's weight ramp: blue (0) through cyan, green and yellow to red (1).</summary>
        public static Color WeightColor(float weight)
        {
            weight = Mathf.Clamp01(weight);
            if (weight <= 0.25f) return Color.Lerp(Color.blue, Color.cyan, weight / 0.25f);
            if (weight <= 0.5f) return Color.Lerp(Color.cyan, Color.green, (weight - 0.25f) / 0.25f);
            if (weight <= 0.75f) return Color.Lerp(Color.green, Color.yellow, (weight - 0.5f) / 0.25f);
            return Color.Lerp(Color.yellow, Color.red, (weight - 0.75f) / 0.25f);
        }

        // Masked vertices around the brush as dark dots (the mask brush shows them everywhere within three radii).
        private static void DrawMask(SceneView view, Vector3 center, float radius)
        {
            var target = hoverTarget;
            if (target == null || !target.Alive || target.Document.Mask == null) return;
            var mask = target.Document.Mask;
            var world = target.World;
            var n = target.Document.Neighbors;
            float reach = radius * (brush is MaskBrush ? 3f : 1.5f);
            float reach2 = reach * reach;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            int drawn = 0;
            float size = HandleUtility.GetHandleSize(center) * 0.012f;
            for (int w = 0; w < n.WeldedCount && drawn < 4000; w++)
            {
                int v = n.Representative[w];
                float m = mask[v];
                if (m <= 0.02f) continue;
                var p = world[v];
                if ((p - center).sqrMagnitude > reach2) continue;
                Handles.color = new Color(0.08f, 0.09f, 0.11f, 0.35f + 0.6f * m);
                Handles.DotHandleCap(0, p, Quaternion.identity, size, EventType.Repaint);
                drawn++;
            }
        }
    }
}
