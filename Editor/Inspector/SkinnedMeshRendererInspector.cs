using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orbiters.Toolkit.Editor;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Unity's Skinned Mesh Renderer inspector with Blender's shape key and vertex group panels under it: the
    /// blendshapes as a list with their values, add, duplicate, rename, mirror and delete (through a Mixer session,
    /// written to the mesh asset on Commit), and the bones and vertex groups with Paint, which enters Weight paint on
    /// that channel. Unity's own inspector is drawn as it is, its BlendShapes foldout folded.
    /// </summary>
    [CustomEditor(typeof(SkinnedMeshRenderer)), CanEditMultipleObjects]
    internal sealed class SkinnedMeshRendererInspector : UnityEditor.Editor
    {
        private static readonly Type BuiltinType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.SkinnedMeshRendererEditor");
        // Unity's inspector draws the bounds handle of its Edit Bounds button in its own OnSceneGUI.
        private static readonly MethodInfo BuiltinSceneGui = BuiltinType?.GetMethod("OnSceneGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        // Loaded on first use: Unity refuses asset loads while a ScriptableObject (this editor) is being constructed.
        private static StyleSheet style;
        private static StyleSheet Style => style != null ? style : style = AssetDatabase.LoadAssetAtPath<StyleSheet>(MixerInfo.BasePath + "/Editor/UI/mixer.uss");
        // How many vertices each bone weighs, per mesh and per state of its Mixer session (painting changes them).
        private static readonly Dictionary<int, (long state, int[] counts)> BoneVertexCounts = new Dictionary<int, (long, int[])>();

        private UnityEditor.Editor builtin;
        private VisualElement root, shapesList, groupsList;
        private Label shapesCount, groupsCount, shapesHint;
        private Button commitPill, revertPill;
        private TextField groupSearch, shapeSearch;
        private string activeShape;
        private string shownShapes, shownGroups;

        private SkinnedMeshRenderer Renderer => target as SkinnedMeshRenderer;

        private void OnEnable()
        {
            if (BuiltinType != null && targets.All(t => t != null))
            {
                builtin = CreateEditor(targets, BuiltinType);
                var weights = builtin != null ? builtin.serializedObject.FindProperty("m_BlendShapeWeights") : null;
                if (weights != null && targets.Length == 1) weights.isExpanded = false;
            }
        }

        private void OnDisable()
        {
            MixerSession.Changed -= Refresh;
            MixerSceneTool.Changed -= Refresh;
            if (builtin != null) DestroyImmediate(builtin);
            builtin = null;
        }

        private void OnSceneGUI()
        {
            if (builtin == null || BuiltinSceneGui == null) return;
            try { BuiltinSceneGui.Invoke(builtin, null); }
            catch (TargetInvocationException e) { Debug.LogException(e.InnerException ?? e); }
        }

        public override VisualElement CreateInspectorGUI()
        {
            root = new VisualElement();
            root.Add(new IMGUIContainer(() =>
            {
                if (builtin != null) builtin.OnInspectorGUI();
                else DrawDefaultInspector();
            }));
            if (targets.Length != 1 || Renderer == null) return root;
            if (Style != null) root.styleSheets.Add(Style);
            if (!EditorGUIUtility.isProSkin) root.AddToClassList("mx-skin-light");
            root.Add(BuildShapeKeys());
            root.Add(BuildVertexGroups());
            MixerSession.Changed += Refresh;
            MixerSceneTool.Changed += Refresh;
            root.schedule.Execute(Refresh).Every(400);
            Refresh();
            return root;
        }

        // ---- Shape keys -------------------------------------------------------------------------------------------------

        private VisualElement BuildShapeKeys()
        {
            var card = Card("SHAPE KEYS", "Blendshapes, as Blender's shape keys: values, new, duplicate, mirror, rename (double-click) and delete. Changes other than values go through Mixer and reach the mesh asset on Commit.", out shapesCount, out var body);
            shapeSearch = new TextField();
            shapeSearch.AddToClassList("mx-field");
            shapeSearch.AddToClassList("mx-inspector__search");
            shapeSearch.tooltip = "Search the shapes";
            shapeSearch.RegisterValueChangedCallback(_ => { shownShapes = null; RefreshShapes(); });
            body.Add(shapeSearch);
            var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.AddToClassList("mx-inspector__scroll");
            shapesList = MixerUi.Box("mx-inspector__list");
            scroll.Add(shapesList);
            body.Add(scroll);
            var tools = MixerUi.Row("mx-inspector__tools");
            tools.Add(MixerUi.Pill("New", NewShape, "accent", IconGlyph.Plus, "A new, empty blendshape: Sculpt mode gives it its shape"));
            tools.Add(MixerUi.Pill("Sculpt", () => SculptShape(activeShape), "ghost", IconGlyph.Sculpt, "Sculpt the selected shape's deltas in the Scene view (the base mesh when none is selected)"));
            tools.Add(MixerUi.Spacer());
            revertPill = MixerUi.Pill("Revert", () => { var t = MixerSession.TargetOf(Renderer); if (t != null && MixerConfirm.Revert(t) > 0) MixerModes.Notify("Reverted " + Renderer.name); }, "ghost", null, "The renderer's own mesh back; Mixer's edits are dropped (asks first when there are any)", destructive: true);
            commitPill = MixerUi.Pill("Commit", () => { var t = MixerSession.TargetOf(Renderer); if (t != null) { var mesh = MixerSession.Commit(t); MixerModes.Notify(mesh != null ? "Committed " + Renderer.name : "Nothing to commit"); } }, "primary", IconGlyph.Check, "Save Mixer's edits of this mesh as its mesh asset, in one Undo step");
            tools.Add(revertPill);
            tools.Add(commitPill);
            body.Add(tools);
            shapesHint = MixerUi.Text(string.Empty, "mx-inspector__hint");
            body.Add(shapesHint);
            return card;
        }

        private IReadOnlyList<string> ShapeNames()
        {
            var session = MixerSession.TargetOf(Renderer);
            if (session != null && session.Alive) return session.Document.ShapeOrder;
            var mesh = Renderer.sharedMesh;
            if (mesh == null) return Array.Empty<string>();
            var names = new string[mesh.blendShapeCount];
            for (int i = 0; i < names.Length; i++) names[i] = mesh.GetBlendShapeName(i);
            return names;
        }

        private void RefreshShapes()
        {
            var renderer = Renderer;
            var session = MixerSession.TargetOf(renderer);
            var names = ShapeNames();
            var mesh = renderer.sharedMesh;
            string layer = session != null && session.Alive ? session.Document.Layer : null;
            string filter = (shapeSearch.value ?? string.Empty).Trim();
            var key = new System.Text.StringBuilder();
            // The picked shape is not part of the key: a click only moves the highlight, the list stays.
            key.Append(mesh != null ? mesh.GetInstanceID() : 0).Append('|').Append(layer).Append('|').Append(filter).Append('|');
            foreach (string name in names) key.Append(name).Append(';');
            if (key.ToString() != shownShapes)
            {
                shownShapes = key.ToString();
                shapesList.Clear();
                if (names.Count == 0) shapesList.Add(MixerUi.Text(mesh == null ? "No mesh." : "No blendshapes yet: New makes one.", "mx-caption"));
                int listed = 0;
                foreach (string name in names)
                {
                    if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    shapesList.Add(ShapeRow(name, mesh != null ? mesh.GetBlendShapeIndex(name) : -1, name == layer));
                    listed++;
                }
                if (names.Count > 0 && listed == 0) shapesList.Add(MixerUi.Text("Nothing matches.", "mx-caption"));
            }
            // Values follow the renderer (an animation, another inspector); the shape Mixer edits shows its own weight,
            // held while it is shown at 100 %.
            var held = session != null && session.Alive ? session.HeldWeights : null;
            foreach (var row in shapesList.Children())
            {
                row.EnableInClassList("mx-shape--on", (string)row.name == activeShape);
                if (!(row.userData is int index) || index < 0 || mesh == null || index >= mesh.blendShapeCount) continue;
                var slider = row.Q<Slider>(className: "mx-shape__slider");
                var value = row.Q<Label>(className: "mx-shape__value");
                bool editing = (string)row.name == layer;
                float weight = editing && held != null && index < held.Length && !float.IsNaN(held[index]) ? held[index] : renderer.GetBlendShapeWeight(index);
                if (slider != null)
                {
                    FitRange(slider, weight);
                    if (Math.Abs(slider.value - weight) > 0.001f) slider.SetValueWithoutNotify(weight);
                    slider.SetEnabled(!editing);
                }
                if (value != null) value.text = weight.ToString("0");
            }
            shapesCount.text = names.Count == 0 ? string.Empty : names.Count + (names.Count == 1 ? " shape" : " shapes");
            bool edits = session != null && session.Alive && session.Document.Changed;
            commitPill.style.display = session != null ? DisplayStyle.Flex : DisplayStyle.None;
            revertPill.style.display = session != null ? DisplayStyle.Flex : DisplayStyle.None;
            commitPill.SetEnabled(edits);
            shapesHint.text = session == null ? "Mixer opens this mesh when you add or sculpt a shape; nothing changes the mesh asset until Commit."
                : layer != null ? "Sculpt mode shapes '" + layer + "' (shown at 100 % while it is edited)." + (edits ? " Commit writes the edits to the mesh asset." : string.Empty)
                : edits ? "Edits of the base mesh wait for Commit." : "Open in Mixer: pick a shape and Sculpt, or sculpt the base mesh.";
        }

        private VisualElement ShapeRow(string name, int index, bool editing)
        {
            var row = MixerUi.Box("mx-shape");
            row.name = name;
            row.userData = index;
            row.EnableInClassList("mx-shape--editing", editing);
            row.Add(MixerUi.Box("mx-shape__dot", PickingMode.Ignore));
            var label = MixerUi.Text(name, "mx-shape__name");
            label.pickingMode = PickingMode.Ignore;
            row.Add(label);
            var rename = new TextField { value = name };
            rename.AddToClassList("mx-shape__rename");
            MixerUi.Show(rename, false);
            row.Add(rename);
            var slider = new Slider(0f, 100f);
            if (index >= 0) { FitRange(slider, Renderer.GetBlendShapeWeight(index)); slider.SetValueWithoutNotify(Renderer.GetBlendShapeWeight(index)); }
            slider.AddToClassList("mx-shape__slider");
            slider.SetEnabled(index >= 0 && !editing);
            slider.tooltip = index < 0 ? "A new shape: it exists on the mesh asset after Commit"
                : editing ? "Shown at 100 % while Mixer edits it; its own weight comes back when you leave it"
                : "The shape's weight on the renderer";
            slider.RegisterValueChangedCallback(evt =>
            {
                if (index < 0 || name == MixerSession.TargetOf(Renderer)?.Document.Layer) return;
                Undo.RecordObject(Renderer, "Shape key " + name);
                Renderer.SetBlendShapeWeight(index, evt.newValue);
                row.Q<Label>(className: "mx-shape__value").text = evt.newValue.ToString("0");
            });
            row.Add(slider);
            row.Add(MixerUi.Text(index >= 0 ? Renderer.GetBlendShapeWeight(index).ToString("0") : "new", "mx-shape__value"));
            row.Add(MixerUi.Icon(IconGlyph.Sculpt, "Sculpt this shape in the Scene view", () => SculptShape(name), "mx-shape__sculpt"));
            row.Add(MixerUi.Icon(IconGlyph.Layers, "Duplicate", () => DuplicateShape(name)));
            row.Add(MixerUi.Icon(IconGlyph.Mirror, "Mirror the +X side onto -X", () => WithSession(t => MixerSession.MirrorShape(t, name, true), "mirror " + name)));
            row.Add(MixerUi.Icon(IconGlyph.Trash, "Delete", () => WithSession(t => { MixerSession.DeleteShape(t, name); if (activeShape == name) activeShape = null; }, "delete " + name)));
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || evt.target is Button || evt.target is Slider || rename.resolvedStyle.display == DisplayStyle.Flex) return;
                activeShape = name;
                foreach (var other in shapesList.Children()) other.EnableInClassList("mx-shape--on", other.name == activeShape);
            });
            bool renaming = false;
            row.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount != 2 || evt.target is Button || evt.target is Slider) return;
                renaming = true;
                MixerUi.Show(label, false);
                MixerUi.Show(rename, true);
                rename.value = name;
                rename.Focus();
                rename.SelectAll();
            });
            // Escape hides the field, whose focus-out then comes too: only the first of the two counts.
            void Commit(bool apply)
            {
                if (!renaming) return;
                renaming = false;
                MixerUi.Show(rename, false);
                MixerUi.Show(label, true);
                string to = rename.value.Trim();
                if (apply && to.Length > 0 && to != name) WithSession(t => { MixerSession.RenameShape(t, name, to); if (activeShape == name) activeShape = to; }, "rename " + name);
            }
            rename.RegisterCallback<FocusOutEvent>(_ => Commit(true));
            rename.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { Commit(true); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { Commit(false); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            return row;
        }

        private void NewShape()
        {
            WithSession(t =>
            {
                var names = new HashSet<string>(t.Document.ShapeOrder);
                string name = "Key " + (names.Count + 1);
                for (int n = names.Count + 1; names.Contains(name); n++) name = "Key " + n;
                MixerSession.SetLayer(t, name, createIfMissing: true);
                activeShape = name;
                MixerModes.Notify("New shape key " + name + ": Sculpt mode shapes it.");
            }, "add a shape key");
        }

        private void DuplicateShape(string from)
        {
            WithSession(t =>
            {
                var names = new HashSet<string>(t.Document.ShapeOrder);
                string to = from + " copy";
                for (int n = 2; names.Contains(to); n++) to = from + " copy " + n;
                MixerSession.DuplicateShape(t, from, to);
                activeShape = to;
            }, "duplicate " + from);
        }

        private void SculptShape(string name)
        {
            WithSession(t =>
            {
                if (name != null && t.Document.HasShape(name)) MixerSession.SetLayer(t, name, false);
                else if (name == null) MixerSession.SetLayer(t, null, false);
                MixerModes.Enter(MixerMode.Sculpt, t);
            }, "sculpt");
        }

        private void WithSession(Action<EditTarget> action, string what)
        {
            var renderer = Renderer;
            var session = MixerSession.TargetOf(renderer);
            if (session == null)
            {
                // A prefab asset in the Project: Mixer's working copy would never be saved there.
                if (EditorUtility.IsPersistent(renderer)) { MixerModes.Notify("Mixer edits meshes in a scene: put the prefab in one and edit that instance."); return; }
                if (!MixerSession.CanEdit(renderer, out string reason)) { MixerModes.Notify(reason); return; }
                session = MixerSession.Edit(renderer);
                MixerSceneTool.FocusTarget = session;
            }
            try { action(session); }
            catch (Exception e) { Debug.LogException(e); MixerModes.Notify("Could not " + what + ": " + e.Message); }
            Refresh();
        }

        // ---- Vertex groups -------------------------------------------------------------------------------------------

        private VisualElement BuildVertexGroups()
        {
            var card = Card("VERTEX GROUPS", "The bones this mesh is skinned to and Mixer's own vertex groups, as Blender's vertex groups: Paint enters Weight paint on one; the window's Weights page manages the groups.", out groupsCount, out var body);
            groupSearch = new TextField();
            groupSearch.AddToClassList("mx-field");
            groupSearch.AddToClassList("mx-inspector__search");
            groupSearch.tooltip = "Search bones and groups";
            groupSearch.RegisterValueChangedCallback(_ => { shownGroups = null; RefreshGroups(); });
            body.Add(groupSearch);
            groupsList = MixerUi.Box("mx-inspector__list");
            body.Add(groupsList);
            var tools = MixerUi.Row("mx-inspector__tools");
            tools.Add(MixerUi.Pill("New group", () => WithSession(t =>
            {
                string name = "Group " + (t.Document.Groups.Count + 1);
                for (int n = t.Document.Groups.Count + 1; t.Document.Group(name) != null; n++) name = "Group " + n;
                MixerSession.AddGroup(t, name);
                PaintChannel(t, WeightChannel.OfGroup(name));
            }, "add a vertex group"), "accent", IconGlyph.Plus, "A new vertex group (kept beside the committed mesh); Weight paint fills it"));
            tools.Add(MixerUi.Pill("Weight paint", () => WithSession(t => MixerModes.Enter(MixerMode.Weights, t), "weight paint"), "ghost", IconGlyph.Weights, "Weight paint mode on this mesh"));
            body.Add(tools);
            return card;
        }

        private void RefreshGroups()
        {
            var renderer = Renderer;
            var mesh = renderer.sharedMesh;
            var session = MixerSession.TargetOf(renderer);
            var bones = renderer.bones ?? Array.Empty<Transform>();
            var groups = session != null && session.Alive ? session.Document.Groups.Select(g => g.Name).ToList() : new List<string>();
            string filter = (groupSearch.value ?? string.Empty).Trim();
            var channel = session != null && MixerSceneTool.FocusTarget == session && MixerSceneTool.Mode == MixerMode.Weights ? MixerSceneTool.ChannelOn(session) : WeightChannel.None;
            // Painting gives a bone its first vertices (or takes its last): which bones weigh anything is part of the key.
            long state = session != null && session.Alive ? ((long)session.Document.Revision << 32) ^ ((long)MixerHistory.Cursor << 16) ^ MixerHistory.Count : 0L;
            var counts = VertexCounts(mesh, bones.Length, state);
            int weightedHash = 17;
            if (counts != null) for (int i = 0; i < counts.Length; i++) if (counts[i] > 0) weightedHash = unchecked(weightedHash * 31 + i);
            var key = new System.Text.StringBuilder();
            key.Append(mesh != null ? mesh.GetInstanceID() : 0).Append('|').Append(filter).Append('|').Append(channel.Bone).Append('|').Append(channel.Group).Append('|').Append(bones.Length).Append('|').Append(weightedHash).Append('|');
            foreach (string g in groups) key.Append(g).Append(';');
            if (key.ToString() == shownGroups) return;
            shownGroups = key.ToString();
            groupsList.Clear();
            int shown = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                string name = bones[i] != null ? bones[i].name : "(missing bone " + i + ")";
                if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (counts != null && counts[i] == 0 && filter.Length == 0) continue;
                int bone = i;
                groupsList.Add(GroupRow(name, counts != null ? counts[i] : -1, "bone", !channel.IsNone && channel.Bone == bone && channel.Group == null, () => WithSession(t => PaintChannel(t, WeightChannel.OfBone(bone)), "paint " + name)));
                shown++;
            }
            foreach (string group in groups)
            {
                if (filter.Length > 0 && group.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string g = group;
                int count = session.Document.Group(g)?.Weights.Count ?? 0;
                groupsList.Add(GroupRow(g, count, "group", channel.Group == g, () => WithSession(t => PaintChannel(t, WeightChannel.OfGroup(g)), "paint " + g)));
                shown++;
            }
            if (shown == 0) groupsList.Add(MixerUi.Text(bones.Length == 0 ? "Not skinned: no bones. New group makes a vertex group." : "Nothing matches.", "mx-caption"));
            int weighted = counts != null ? counts.Count(c => c > 0) : bones.Length;
            groupsCount.text = (bones.Length > 0 ? weighted + (weighted == 1 ? " bone" : " bones") : string.Empty) + (groups.Count > 0 ? (bones.Length > 0 ? " · " : string.Empty) + groups.Count + (groups.Count == 1 ? " group" : " groups") : string.Empty);
        }

        private VisualElement GroupRow(string name, int count, string kind, bool active, Action paint)
        {
            var row = MixerUi.Box("mx-group");
            row.EnableInClassList("mx-group--on", active);
            row.Add(MixerUi.Text(name, "mx-group__name"));
            if (count >= 0) row.Add(MixerUi.Text(count == 0 ? "no vertices" : MixerUi.Count(count), "mx-group__count"));
            row.Add(MixerUi.Text(kind, "mx-group__kind"));
            row.Add(MixerUi.Icon(IconGlyph.Weights, "Paint this " + kind + "'s weights", paint, "mx-group__paint"));
            MixerUi.Press(row, paint);
            return row;
        }

        private static void PaintChannel(EditTarget target, WeightChannel channel)
        {
            MixerSceneTool.SetChannel(target, channel);
            MixerModes.Enter(MixerMode.Weights, target);
        }

        // How many vertices each bone weighs: once per mesh, again whenever its Mixer session changed (painting moves them).
        private static int[] VertexCounts(Mesh mesh, int bones, long state)
        {
            if (mesh == null || bones == 0) return null;
            int key = mesh.GetInstanceID();
            if (BoneVertexCounts.TryGetValue(key, out var cached) && cached.state == state && cached.counts.Length == bones) return cached.counts;
            try
            {
                var counts = new int[bones];
                var all = mesh.GetAllBoneWeights();
                for (int i = 0; i < all.Length; i++) { var w = all[i]; if (w.boneIndex >= 0 && w.boneIndex < bones && w.weight > 0f) counts[w.boneIndex]++; }
                if (BoneVertexCounts.Count > 64) BoneVertexCounts.Clear();
                BoneVertexCounts[key] = (state, counts);
                return counts;
            }
            catch (Exception) { return null; }
        }

        // Blendshape weights may go past 0..100 (Unity does not clamp them): the slider widens instead of clamping.
        private static void FitRange(Slider slider, float weight)
        {
            float low = Mathf.Min(0f, weight), high = Mathf.Max(100f, weight);
            if (!Mathf.Approximately(slider.lowValue, low)) slider.lowValue = low;
            if (!Mathf.Approximately(slider.highValue, high)) slider.highValue = high;
        }

        // ---- Common -----------------------------------------------------------------------------------------------------

        private void Refresh()
        {
            if (root == null || Renderer == null || root.panel == null) return;
            RefreshShapes();
            RefreshGroups();
        }

        private static VisualElement Card(string title, string tooltip, out Label count, out VisualElement body)
        {
            var card = MixerUi.Box("mx-inspector");
            var head = MixerUi.Row("mx-inspector__head");
            var label = MixerUi.Text(title, "mx-inspector__title");
            label.tooltip = tooltip;
            head.Add(label);
            count = MixerUi.Text(string.Empty, "mx-inspector__count");
            head.Add(count);
            var logo = new MixerLogo.Element { Color = new Color(0.235f, 0.949f, 0.604f), Cut = new Color(0.11f, 0.118f, 0.137f) };
            logo.style.width = 14; logo.style.height = 14; logo.tooltip = "Mixer";
            head.Add(logo);
            card.Add(head);
            body = card;
            return card;
        }
    }
}
