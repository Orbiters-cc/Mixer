using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    internal sealed partial class MixerWindow
    {
        private VisualElement brushTiles, layerChips, sculptSettings;
        private Slider radiusSlider, strengthSlider;
        private Label radiusValue, strengthValue;
        private SegmentedControl falloffControl;
        private ToggleSwitch symmetrySwitch;
        private TextField newShapeField;
        private Label layerCaption, maskCaption;
        private VisualElement maskActions;
        private readonly Dictionary<string, Button> tileByBrush = new Dictionary<string, Button>();

        private void BuildSculptPage(VisualElement page)
        {
            var brushes = MixerUi.Card("BRUSHES", out var brushBody);
            page.Add(brushes);
            brushTiles = MixerUi.Box("mx-tiles");
            brushBody.Add(brushTiles);
            foreach (var brush in Brushes.All)
            {
                var b = brush;
                var tile = new Button { text = string.Empty, tooltip = brush.Description };
                tile.AddToClassList("mx-tile");
                var icon = new BrushIcon(brush.Glyph);
                icon.AddToClassList("mx-tile__icon");
                tile.Add(icon);
                tile.Add(MixerUi.Text(brush.Name, "mx-tile__name"));
                MixerUi.Press(tile, () => { MixerSceneTool.ActiveBrush = b; MixerSceneTool.Mode = MixerMode.Sculpt; WakeGlow(); });
                brushTiles.Add(tile);
                tileByBrush[brush.Name] = tile;
            }
            brushBody.Add(MixerUi.Text("Drag on the mesh in the Scene view. [ and ] change the radius, Shift+wheel the strength, Ctrl inverts, Shift smooths, F toggles symmetry, Esc leaves.", "mx-caption"));

            var settings = MixerUi.Card("BRUSH", out sculptSettings);
            page.Add(settings);
            radiusSlider = new Slider(0f, 1f) { value = MixerBrushTiles.RadiusToSlider(MixerSceneTool.Radius) };
            radiusSlider.AddToClassList("mx-slider");
            radiusValue = MixerUi.Text(MixerUi.Metres(MixerSceneTool.Radius), "mx-chip mx-chip__text");
            radiusSlider.RegisterValueChangedCallback(evt => { MixerSceneTool.Radius = MixerBrushTiles.SliderToRadius(evt.newValue); radiusValue.text = MixerUi.Metres(MixerSceneTool.Radius); });
            sculptSettings.Add(MixerUi.Setting("Radius", "How far the brush reaches, in the world", Pair(radiusSlider, radiusValue), first: true));
            strengthSlider = new Slider(0f, 1f) { value = MixerSceneTool.Strength };
            strengthSlider.AddToClassList("mx-slider");
            strengthValue = MixerUi.Text(MixerUi.Percent(MixerSceneTool.Strength), "mx-chip mx-chip__text");
            strengthSlider.RegisterValueChangedCallback(evt => { MixerSceneTool.Strength = evt.newValue; strengthValue.text = MixerUi.Percent(evt.newValue); });
            sculptSettings.Add(MixerUi.Setting("Strength", "How much each step moves", Pair(strengthSlider, strengthValue)));
            falloffControl = new SegmentedControl(MixerBrushTiles.FalloffNames, index => MixerSceneTool.Falloff = (Falloff)index);
            falloffControl.AddToClassList("orb-segmented--compact");
            falloffControl.SetIndex((int)MixerSceneTool.Falloff);
            sculptSettings.Add(MixerUi.Setting("Falloff", "How the effect fades toward the edge", falloffControl));
            symmetrySwitch = new ToggleSwitch(MixerSceneTool.Symmetry, on => MixerSceneTool.Symmetry = on);
            sculptSettings.Add(MixerUi.Setting("Symmetry", "Mirror every stroke across the mesh's X axis", symmetrySwitch));
            maskActions = MixerUi.Row();
            maskActions.Add(MixerUi.Pill("Clear", () => MixerSession.ClearMask(CurrentTarget()), "ghost", null, "Free every vertex"));
            maskActions.Add(MixerUi.Pill("Invert", () => MixerSession.InvertMask(CurrentTarget()), "ghost", null, "Swap masked and free vertices"));
            maskCaption = MixerUi.Text("No mask", "mx-setting__detail");
            var maskTexts = MixerUi.Box("mx-setting__texts");
            maskTexts.Add(MixerUi.Text("Mask", "mx-setting__title"));
            maskTexts.Add(maskCaption);
            var maskRow = MixerUi.Box("mx-setting");
            maskRow.Add(maskTexts);
            maskActions.AddToClassList("mx-setting__control");
            maskRow.Add(maskActions);
            sculptSettings.Add(maskRow);

            var layers = MixerUi.Card("LAYER", out var layerBody);
            page.Add(layers);
            layerCaption = MixerUi.Text("", "mx-caption");
            layerBody.Add(layerCaption);
            layerChips = MixerUi.Box("mx-chips");
            layerBody.Add(layerChips);
            var newRow = MixerUi.Row("mx-newshape");
            newShapeField = new TextField { value = string.Empty };
            newShapeField.AddToClassList("mx-field");
            newShapeField.tooltip = "New blendshape name";
            newShapeField.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { CreateShape(); evt.StopPropagation(); } });
            newRow.Add(newShapeField);
            newRow.Add(MixerUi.Pill("Create", CreateShape, "accent", IconGlyph.Plus, "Make a new blendshape and sculpt it"));
            layerBody.Add(newRow);
        }

        private static VisualElement Pair(VisualElement a, VisualElement b)
        {
            var row = MixerUi.Row("mx-pair");
            row.Add(a);
            row.Add(b);
            return row;
        }


        private void CreateShape()
        {
            var target = CurrentTarget();
            if (target == null) { toasts.Show("Edit a mesh first.", true); return; }
            string name = newShapeField.value.Trim();
            if (name.Length == 0) { toasts.Show("Give the blendshape a name.", true); return; }
            if (target.Document.HasShape(name)) { MixerSession.SetLayer(target, name, false); toasts.Show("Editing " + name + "."); return; }
            MixerSession.SetLayer(target, name, createIfMissing: true);
            newShapeField.value = string.Empty;
            toasts.Show("New blendshape " + name + ": sculpt it, it is recorded as the shape's deltas.");
            WakeGlow();
        }

        private void RefreshSculptPage(EditTarget target)
        {
            foreach (var pair in tileByBrush) pair.Value.EnableInClassList("mx-tile--on", MixerSceneTool.ActiveBrush.Name == pair.Key && MixerSceneTool.Mode == MixerMode.Sculpt);
            if (Math.Abs(radiusSlider.value - MixerBrushTiles.RadiusToSlider(MixerSceneTool.Radius)) > 1e-4f) radiusSlider.SetValueWithoutNotify(MixerBrushTiles.RadiusToSlider(MixerSceneTool.Radius));
            radiusValue.text = MixerUi.Metres(MixerSceneTool.Radius);
            if (Math.Abs(strengthSlider.value - MixerSceneTool.Strength) > 1e-4f) strengthSlider.SetValueWithoutNotify(MixerSceneTool.Strength);
            strengthValue.text = MixerUi.Percent(MixerSceneTool.Strength);
            if (falloffControl.Index != (int)MixerSceneTool.Falloff) falloffControl.SetIndex((int)MixerSceneTool.Falloff);
            symmetrySwitch.SetValueWithoutNotify(MixerSceneTool.Symmetry);
            layerChips.Clear();
            int masked = target != null ? target.Document.MaskedCount : 0;
            maskCaption.text = target == null ? "Paint with the Mask brush to hold parts of the mesh still" : masked == 0 ? "No mask: paint one with the Mask brush, Ctrl unmasks" : MixerUi.Count(masked) + " vertices held still";
            maskActions.SetEnabled(target != null && target.Document.Mask != null);
            if (target == null)
            {
                layerCaption.text = "Strokes go to the base mesh or to a blendshape. Edit a mesh to pick.";
                return;
            }
            var doc = target.Document;
            layerCaption.text = doc.Layer == null ? "Strokes change the base mesh. Pick a shape to sculpt its deltas instead." : "Strokes are recorded as deltas of " + doc.Layer + "; the base mesh stays.";
            var baseChip = Chip("Base mesh", doc.Layer == null, () => MixerSession.SetLayer(target, null, false), null);
            layerChips.Add(baseChip);
            var names = doc.ShapeOrder.Where(doc.HasShape).ToList();
            var shown = names;
            if (names.Count > 24)
            {
                // Many shapes (a VRChat body has hundreds): the edited ones, then the first ones, and a picker for the rest.
                var edited = doc.ShapeEdits.Keys.Where(doc.HasShape).ToList();
                shown = edited.Concat(names.Where(n => !edited.Contains(n)).Take(Mathf.Max(0, 16 - edited.Count))).ToList();
            }
            foreach (string name in shown)
            {
                string n = name;
                bool edited = doc.ShapeEdits.TryGetValue(n, out var layer) && (layer.Edited || layer.IsNew);
                var chip = Chip(name + (edited ? " •" : ""), doc.Layer == name, () => MixerSession.SetLayer(target, n, false), () => MixerSession.DeleteShape(target, n));
                chip.tooltip = "Right-click: rename, duplicate, mirror or delete";
                chip.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    evt.menu.AppendAction("Rename…", _ => AskName("Rename " + n, n, value => MixerSession.RenameShape(target, n, value)));
                    evt.menu.AppendAction("Duplicate…", _ => AskName("Duplicate " + n, n + " copy", value => MixerSession.DuplicateShape(target, n, value)));
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction("Mirror +X side onto -X", _ => MixerSession.MirrorShape(target, n, true));
                    evt.menu.AppendAction("Mirror -X side onto +X", _ => MixerSession.MirrorShape(target, n, false));
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction("Delete", _ => MixerSession.DeleteShape(target, n));
                }));
                layerChips.Add(chip);
            }
            if (shown.Count < names.Count)
            {
                layerChips.Add(MixerUi.Pill("All " + names.Count + " shapes…", () =>
                {
                    var picker = new BlendshapePicker(names, picked => { if (picked != null) MixerSession.SetLayer(target, picked, false); });
                    UnityEditor.PopupWindow.Show(layerChips.worldBound, picker);
                }, "ghost", IconGlyph.Layers));
            }
        }

        private void AskName(string title, string initial, Action<string> done)
        {
            UnityEditor.PopupWindow.Show(layerChips.worldBound, new NamePopup(title, initial, done));
        }

        private static VisualElement Chip(string text, bool on, Action pick, Action remove)
        {
            var chip = MixerUi.Box("mx-chip mx-chip--pick");
            if (on) chip.AddToClassList("mx-chip--on");
            chip.Add(MixerUi.Text(text, "mx-chip__text"));
            MixerUi.Press(chip, pick);
            if (remove != null) chip.Add(MixerUi.Icon(IconGlyph.Close, "Delete this blendshape", remove, "mx-chip__remove"));
            return chip;
        }
    }

    /// <summary>A small popup asking for a name.</summary>
    internal sealed class NamePopup : PopupWindowContent
    {
        private readonly string title, initial;
        private readonly Action<string> done;

        public NamePopup(string title, string initial, Action<string> done) { this.title = title; this.initial = initial; this.done = done; }
        public override Vector2 GetWindowSize() => new Vector2(300f, 96f);
        public override void OnGUI(Rect rect) { }

        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            root.AddToClassList("mx-root");
            root.AddToClassList("mx-popup");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(MixerInfo.BasePath + "/Editor/UI/mixer.uss");
            if (sheet != null) root.styleSheets.Add(sheet);
            Orbiters.Mixer.Editor.MixerAccent.Apply(root);
            root.Add(MixerUi.Text(title, "mx-popup__title"));
            var field = new TextField { value = initial };
            field.AddToClassList("mx-field");
            root.Add(field);
            var row = MixerUi.Row();
            row.Add(MixerUi.Spacer());
            row.Add(MixerUi.Pill("Cancel", () => editorWindow.Close(), "ghost"));
            row.Add(MixerUi.Pill("OK", () => { done(field.value); editorWindow.Close(); }, "primary"));
            row.style.marginTop = 8;
            root.Add(row);
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { done(field.value); editorWindow.Close(); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { editorWindow.Close(); evt.StopPropagation(); }
            });
            root.schedule.Execute(() => { field.Q(TextField.textInputUssName)?.Focus(); field.SelectAll(); }).StartingIn(30);
        }
    }

    /// <summary>A searchable list of names in a popup (the blendshapes of a body).</summary>
    internal sealed class BlendshapePicker : PopupWindowContent
    {
        private readonly List<string> names;
        private readonly Action<string> picked;
        private ListView list;
        private List<string> shown;

        public BlendshapePicker(List<string> names, Action<string> picked)
        {
            this.names = names;
            this.picked = picked;
            shown = names;
        }

        public override Vector2 GetWindowSize() => new Vector2(320f, 420f);
        public override void OnGUI(Rect rect) { }

        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            root.AddToClassList("mx-root");
            root.AddToClassList("mx-popup");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(MixerInfo.BasePath + "/Editor/UI/mixer.uss");
            if (sheet != null) root.styleSheets.Add(sheet);
            Orbiters.Mixer.Editor.MixerAccent.Apply(root);
            var field = new TextField();
            field.AddToClassList("mx-field");
            field.tooltip = "Search shapes";
            field.RegisterValueChangedCallback(evt =>
            {
                string q = evt.newValue.Trim();
                shown = q.Length == 0 ? names : names.Where(n => n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                list.itemsSource = shown;
                list.RefreshItems();
            });
            root.Add(field);
            list = new ListView(shown, 24f, () => { var l = new Label(); l.AddToClassList("mx-list__item"); return l; }, (e, i) => ((Label)e).text = shown[i]);
            list.AddToClassList("mx-list");
            list.selectionType = SelectionType.Single;
            list.selectionChanged += items => { var pick = items.FirstOrDefault() as string; if (pick != null) { picked(pick); editorWindow.Close(); } };
            root.Add(list);
            root.schedule.Execute(() => field.Q(TextField.textInputUssName)?.Focus()).StartingIn(30);
        }
    }
}
