using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using UnityEditor;
using UnityEditor.SceneManagement;
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
        // The painted bone or vertex group, and the mesh whose bone indices it uses: bone indices differ from one mesh to
        // another, so the channel is carried to another mesh by the bone itself (its Transform), never by its index.
        private static WeightChannel channel = WeightChannel.None;
        private static EditTarget channelOwner;
        private static bool xrayWasEnabled, xrayPickingWasEnabled;
        private static XRayGizmoTargetMode xrayModeBefore;
        private static GameObject xrayPinnedBefore;

        // The brush settings, read once and written to EditorPrefs a moment after the last change (a slider drag
        // changes them on every move, and the Scene view reads them on every repaint).
        private static float radius, strength, weightValue;
        private static Falloff falloff;
        private static bool symmetry, autoNormalize;
        private static WeightBrushKind weightKind;
        private static bool prefsDirty;
        private static double prefsDue;

        public static event Action Changed;

        static MixerSceneTool()
        {
            SceneView.duringSceneGui += OnSceneGui;
            Selection.selectionChanged += OnSelectionChanged;
            brush = Brushes.Named(EditorPrefs.GetString(BrushPref, "Grab"));
            radius = Mathf.Clamp(EditorPrefs.GetFloat(RadiusPref, 0.05f), 0.001f, 5f);
            strength = Mathf.Clamp01(EditorPrefs.GetFloat(StrengthPref, 0.5f));
            falloff = (Falloff)EditorPrefs.GetInt(FalloffPref, 0);
            symmetry = EditorPrefs.GetBool(SymmetryPref, true);
            weightKind = (WeightBrushKind)EditorPrefs.GetInt(WeightKindPref, 0);
            weightValue = Mathf.Clamp01(EditorPrefs.GetFloat(WeightValuePref, 1f));
            autoNormalize = EditorPrefs.GetBool(NormalizePref, true);
            AssemblyReloadEvents.beforeAssemblyReload += FlushPrefs;
            EditorApplication.quitting += FlushPrefs;
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
                ClearHover();
                if (mode != MixerMode.Weights) BoneSubSelection.Clear();
                if (previous == MixerMode.Weights || previous == MixerMode.Armature) LeaveWeights();
                if (mode == MixerMode.Weights || mode == MixerMode.Armature) EnterWeights();
                if (previous == MixerMode.Armature) ArmatureEdit.Leave();
                // Edit Armature is Blender's: bones move where the skin rests, whatever started the mode (the mode strip,
                // the window's page, the MCP tool). The window's switch can still turn rest-pose editing off.
                if (mode == MixerMode.Armature) ArmatureEdit.Active = true;
                Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        /// <summary>The target the window and the weight overlay follow: the last one stroked, picked or selected.</summary>
        public static EditTarget FocusTarget
        {
            get => focusTarget != null && focusTarget.Alive && MixerSession.Targets.Contains(focusTarget) ? focusTarget : null;
            set
            {
                if (focusTarget == value) return;
                var previous = FocusTarget;
                // The painted bone goes along when the new focus is skinned to it too (rig mates share bones).
                var carried = ChannelOn(value);
                focusTarget = value;
                if (value != null) { channel = carried; channelOwner = value; }
                BoneSubSelection.Clear();
                if (mode == MixerMode.Weights)
                {
                    previous?.ShowChannel(WeightChannel.None);
                    EnsureChannel(FocusTarget);
                    FocusTarget?.ShowChannel(ChannelOn(FocusTarget));
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
            get => radius;
            set { radius = Mathf.Clamp(value, 0.001f, 5f); SavePrefsSoon(); Changed?.Invoke(); SceneView.RepaintAll(); }
        }

        public static float Strength
        {
            get => strength;
            set { strength = Mathf.Clamp01(value); SavePrefsSoon(); Changed?.Invoke(); }
        }

        public static Falloff Falloff
        {
            get => falloff;
            set { falloff = value; SavePrefsSoon(); Changed?.Invoke(); }
        }

        public static bool Symmetry
        {
            get => symmetry;
            set { symmetry = value; SavePrefsSoon(); Changed?.Invoke(); }
        }

        public static WeightBrushKind WeightKind
        {
            get => weightKind;
            set { weightKind = value; SavePrefsSoon(); Changed?.Invoke(); }
        }

        /// <summary>The weight the Draw and Replace brushes paint toward.</summary>
        public static float WeightValue
        {
            get => weightValue;
            set { weightValue = Mathf.Clamp01(value); SavePrefsSoon(); Changed?.Invoke(); }
        }

        public static bool AutoNormalize
        {
            get => autoNormalize;
            set { autoNormalize = value; SavePrefsSoon(); Changed?.Invoke(); }
        }

        private static void SavePrefsSoon()
        {
            prefsDue = EditorApplication.timeSinceStartup + 0.4;
            if (prefsDirty) return;
            prefsDirty = true;
            EditorApplication.update += FlushPrefsWhenDue;
        }

        private static void FlushPrefsWhenDue()
        {
            if (EditorApplication.timeSinceStartup >= prefsDue) FlushPrefs();
        }

        private static void FlushPrefs()
        {
            if (!prefsDirty) return;
            prefsDirty = false;
            EditorApplication.update -= FlushPrefsWhenDue;
            EditorPrefs.SetFloat(RadiusPref, radius);
            EditorPrefs.SetFloat(StrengthPref, strength);
            EditorPrefs.SetInt(FalloffPref, (int)falloff);
            EditorPrefs.SetBool(SymmetryPref, symmetry);
            EditorPrefs.SetInt(WeightKindPref, (int)weightKind);
            EditorPrefs.SetFloat(WeightValuePref, weightValue);
            EditorPrefs.SetBool(NormalizePref, autoNormalize);
        }

        // ---- The painted channel ----------------------------------------------------------------------------------

        /// <summary>The bone or vertex group weight strokes paint, as the focused mesh (or the first one) reads it.</summary>
        public static WeightChannel Channel => ChannelOn(FocusTarget ?? MixerSession.First);

        /// <summary>
        /// The painted channel as <paramref name="target"/> reads it: the same bone (found by its Transform, whatever its
        /// index on that mesh) or the vertex group of the same name; None when that mesh has neither.
        /// </summary>
        public static WeightChannel ChannelOn(EditTarget target)
        {
            if (target == null || channel.IsNone) return WeightChannel.None;
            if (target == channelOwner) return channel;
            if (channel.IsGroup) return target.Document.Group(channel.Group) != null ? channel : WeightChannel.None;
            var bone = BoneOf(channelOwner, channel.Bone);
            var skinned = target.Skinned;
            if (bone == null || skinned == null) return WeightChannel.None;
            int index = Array.IndexOf(skinned.bones, bone);
            return index >= 0 ? WeightChannel.OfBone(index) : WeightChannel.None;
        }

        private static Transform BoneOf(EditTarget target, int bone)
        {
            if (target == null || bone < 0) return null;
            var skinned = target.Skinned;
            if (skinned == null) return null;
            var bones = skinned.bones;
            return bone < bones.Length ? bones[bone] : null;
        }

        private static string ChannelName(EditTarget owner, WeightChannel on)
        {
            if (on.IsGroup) return "vertex group " + on.Group;
            var bone = BoneOf(owner, on.Bone);
            return bone != null ? bone.name : "bone " + on.Bone;
        }

        /// <summary>Paints <paramref name="value"/> on the focused mesh (or the first one), whose bone indices it uses.</summary>
        public static void SetChannel(WeightChannel value) => SetChannel(FocusTarget ?? MixerSession.First, value);

        /// <summary>
        /// Paints <paramref name="value"/>, a bone index of <paramref name="target"/> or a vertex group; an edited
        /// <paramref name="target"/> becomes the focus.
        /// </summary>
        public static void SetChannel(EditTarget target, WeightChannel value)
        {
            if (target != null && target != FocusTarget && target.Alive && MixerSession.Targets.Contains(target))
            {
                channel = value;
                channelOwner = target;
                FocusTarget = target;
            }
            channel = value;
            channelOwner = target;
            var picked = BoneSubSelection.Bone;
            if (picked != null && (value.IsGroup || BoneOf(target, value.Bone) != picked)) BoneSubSelection.Clear();
            if (mode == MixerMode.Weights)
            {
                var shown = FocusTarget ?? MixerSession.First;
                shown?.ShowChannel(ChannelOn(shown));
            }
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Starts what a weight stroke on <paramref name="target"/> paints: the stroked mesh becomes the focus and the
        /// painted bone (or group) goes along when that mesh has it. When it does not, the mesh's own heaviest bone is
        /// shown and <paramref name="refusal"/> says why nothing is painted, so a bone picked on one mesh never paints
        /// another bone of the next one.
        /// </summary>
        internal static WeightChannel WeightStrokeChannel(EditTarget target, out string refusal)
        {
            refusal = null;
            if (target == null) { refusal = "Nothing to paint here."; return WeightChannel.None; }
            var before = channel;
            var beforeOwner = channelOwner;
            var carried = ChannelOn(target);
            bool another = beforeOwner != null && beforeOwner != target;
            FocusTarget = target;
            if (channelOwner != target) { channel = carried; channelOwner = target; }
            if (another && !before.IsNone && carried.IsNone)
            {
                refusal = target.Name + " has no " + ChannelName(beforeOwner, before) + " to paint" + (channel.IsNone ? "." : ": it shows its " + ChannelName(target, channel) + " now; stroke again to paint that, or pick another bone.");
                return WeightChannel.None;
            }
            if (channel.IsNone) { refusal = "Pick a bone or a vertex group to paint first."; return WeightChannel.None; }
            if (!channel.IsGroup && !target.Document.HasSkin) { refusal = target.Name + " has no bones to weight; paint a vertex group instead."; return WeightChannel.None; }
            return channel;
        }

        public static bool Stroking => stroke != null || weightStroke != null;
        public static bool Hovering => hovering;
        /// <summary>The edited mesh under the pointer in the Scene view while the pointer is over one, else null.</summary>
        public static EditTarget HoverTarget => hovering && hoverTarget != null && hoverTarget.Alive ? hoverTarget : null;
        /// <summary>The channel's weight under the cursor (nearest vertex of the hovered triangle), or -1 off the mesh.</summary>
        public static float HoverWeight { get; private set; } = -1f;

        private static void ClearHover()
        {
            hovering = false;
            hoverTarget = null;
            HoverWeight = -1f;
        }

        private static void OnSceneGui(SceneView view)
        {
            if ((mode != MixerMode.Sculpt && mode != MixerMode.Weights) || !MixerSession.Active) { if (hovering) ClearHover(); return; }
            // Prefab Mode shows another stage: the edited meshes of the scene are hidden, so nothing may be stroked.
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) { if (hovering) ClearHover(); return; }
            var e = Event.current;
            if (mode == MixerMode.Weights) BoneSubSelection.OnSceneGui(view);
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
                case EventType.MouseLeaveWindow:
                    if (!Stroking && hovering) { ClearHover(); Changed?.Invoke(); view.Repaint(); }
                    break;
                case EventType.MouseDown:
                    if (e.button != 0 || e.alt) break;
                    Hover(e.mousePosition, true);
                    if (!hovering)
                    {
                        // As in Blender, a click on nothing drops the bone picked in Weight Paint; the mesh stays selected.
                        if (mode == MixerMode.Weights && BoneSubSelection.Bone != null) { BoneSubSelection.Clear(); e.Use(); }
                        break;
                    }
                    GUIUtility.hotControl = control;
                    if (!BeginStroke(e, view)) GUIUtility.hotControl = 0;
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
            if (!hovering) hoverTarget = null;
            HoverWeight = -1f;
            // What a stroke here would paint: the painted bone as the hovered mesh reads it.
            var on = hovering && mode == MixerMode.Weights ? ChannelOn(hoverTarget) : WeightChannel.None;
            if (hovering && mode == MixerMode.Weights && !on.IsNone)
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
                if (on.IsGroup) { var g = doc.Group(on.Group); HoverWeight = g != null && g.Weights.TryGetValue(nearest, out float w) ? w : 0f; }
                else HoverWeight = doc.BoneWeight(nearest, on.Bone);
            }
        }

        private static bool BeginStroke(Event e, SceneView view)
        {
            if (hoverTarget == null) return false;
            hoverTarget.RefreshWorld();
            Hover(e.mousePosition, false);
            if (!hovering) return false;
            if (MixerLinkService.IsBlenderOwned(hoverTarget)) { MixerSession.Say(hoverTarget.Name + " is being edited in Blender: leave its edit mode there first.", true); return false; }
            if (mode == MixerMode.Weights)
            {
                var on = WeightStrokeChannel(hoverTarget, out string refusal);
                if (refusal != null) { MixerModes.Notify(refusal); return false; }
                weightStroke = new WeightStroke(hoverTarget, WeightKind, on, Radius, Strength, WeightValue, Falloff, Symmetry, AutoNormalize);
                weightStroke.Begin(hoverPoint, hoverNormal, e.control);
                return true;
            }
            FocusTarget = hoverTarget;
            var effective = e.shift && !(brush is SmoothBrush) ? Brushes.Named("Smooth") : brush;
            stroke = new SculptStroke(hoverTarget, effective, Radius, Strength, Falloff, Symmetry, view.camera != null ? view.camera.transform.forward : Vector3.forward);
            stroke.Begin(hoverPoint, hoverNormal, HandleUtility.GUIPointToWorldRay(e.mousePosition), e.control);
            return true;
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

        // ---- Weights mode and the avatar gizmos ----------------------------------------------------------------------

        private static void EnterWeights()
        {
            if (!xrayPinnedByMixer)
            {
                xrayPinnedByMixer = true;
                xrayWasEnabled = XRayGizmoService.Enabled;
                xrayModeBefore = XRayGizmoService.TargetMode;
                xrayPinnedBefore = XRayGizmoService.PinnedObject;
                xrayPickingWasEnabled = XRayBonePickingService.Enabled;
                PinXRay();
                XRayBonePickingService.SetEnabled(true);
            }
            // From Armature mode too (the gizmos stay pinned): the weights show as soon as the mode is on.
            var shown = FocusTarget ?? MixerSession.First;
            EnsureChannel(shown);
            shown?.ShowChannel(ChannelOn(shown));
        }

        // Weights mode always shows a weight, as Blender's weight paint always shows a group: with none picked (or a bone
        // the mesh does not have), the bone that weighs most on the mesh.
        private static void EnsureChannel(EditTarget target)
        {
            if (mode != MixerMode.Weights || target == null || target.Skinned == null) return;
            if (channelOwner != target)
            {
                channel = ChannelOn(target);
                channelOwner = target;
            }
            if (channel.IsGroup || (channel.Bone >= 0 && channel.Bone < target.Skinned.bones.Length)) return;
            int bone = XRayWeightPaintService.MostWeightedBone(target.Skinned);
            if (bone >= 0) channel = WeightChannel.OfBone(bone);
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

        private static void OnSelectionChanged()
        {
            FollowSelection();
            if (mode != MixerMode.Weights && mode != MixerMode.Armature) return;
            // A bone of the focus target selected in Unity (its own Hierarchy, a Shift or Ctrl click) becomes the channel;
            // the overlay's and the Mixer Hierarchy's plain clicks sub-select it instead (BoneSubSelection).
            var target = FocusTarget ?? MixerSession.First;
            var picked = Selection.activeTransform;
            if (target == null || picked == null || target.Skinned == null) return;
            var bones = target.Skinned.bones;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] == picked) { if (FocusTarget == null) FocusTarget = target; if (mode == MixerMode.Weights) SetChannel(target, WeightChannel.OfBone(i)); else ArmatureEdit.SelectedBone = i; return; }
        }

        /// <summary>
        /// Selecting an edited mesh, or an object holding one, makes it the focus: the window's pages, the Scene view's
        /// Commit and Revert and the weight overlay follow what the user selects. Selecting the avatar the focused mesh is
        /// part of, or a bone, keeps the focus.
        /// </summary>
        internal static void FollowSelection()
        {
            if (!MixerSession.Active || Stroking) return;
            var go = Selection.activeGameObject;
            if (go == null) return;
            var focus = FocusTarget;
            var own = go.GetComponent<Renderer>();
            var target = own != null ? MixerSession.TargetOf(own) : null;
            if (target == null)
            {
                if (focus != null && focus.Renderer.transform.IsChildOf(go.transform)) return;
                if (MixerModes.SelectionKeepsFocus()) return;
                foreach (var t in MixerSession.Targets)
                    if (t.Alive && t.Renderer.transform.IsChildOf(go.transform)) { target = t; break; }
            }
            if (target != null && target.Alive && target != focus) FocusTarget = target;
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
