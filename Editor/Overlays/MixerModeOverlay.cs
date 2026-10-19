using System;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Blender's header strip in the Scene view: the mode dropdown (Object Mode, Sculpt Mode, Weight Paint, Edit
    /// Armature), the brush settings of the mode (radius, strength, falloff and symmetry, or weight and normalize),
    /// Commit and Revert while a session is open, and the Mixer window. Every feature of Mixer starts from here.
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Mixer", true)]
    internal sealed class MixerModeOverlay : ToolbarOverlay
    {
        public const string Id = "orbiters.mixer.mode";

        public MixerModeOverlay() : base(MixerModeDropdown.Id, MixerBrushSettingsElement.Id, MixerCommitButton.Id, MixerRevertButton.Id, MixerWindowButton.Id) { }

        public override void OnCreated() => MixerOverlays.FirstPlacement(this, new Vector2(50f, 34f));
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerModeDropdown : EditorToolbarDropdown
    {
        public const string Id = "orbiters.mixer.mode.dropdown";
        private readonly VectorIcon glyph;

        public MixerModeDropdown()
        {
            tooltip = "Mixer mode: Object, Sculpt, Weight Paint or Edit Armature, on the selected mesh (Blender's modes)";
            MixerOverlays.Dress(this, "mx-mode");
            glyph = new VectorIcon(MixerModes.IconOf(MixerMode.None)) { pickingMode = PickingMode.Ignore };
            glyph.AddToClassList("mx-mode__icon");
            Insert(0, glyph);
            clicked += ShowMenu;
            Refresh();
            RegisterCallback<AttachToPanelEvent>(_ => { MixerSceneTool.Changed += Refresh; MixerSession.Changed += Refresh; });
            RegisterCallback<DetachFromPanelEvent>(_ => { MixerSceneTool.Changed -= Refresh; MixerSession.Changed -= Refresh; });
        }

        private void Refresh()
        {
            var mode = MixerSceneTool.Mode;
            text = MixerModes.NameOf(mode);
            glyph.Glyph = MixerModes.IconOf(mode);
            EnableInClassList("mx-mode--on", mode != MixerMode.None);
        }

        private void ShowMenu()
        {
            var menu = new GenericMenu();
            foreach (var entry in MixerModes.All)
            {
                var mode = entry.Mode;
                menu.AddItem(new GUIContent(entry.Name), MixerSceneTool.Mode == mode, () => MixerModes.Enter(mode));
            }
            menu.AddSeparator(string.Empty);
            var renderer = MixerModes.RendererOfSelection();
            if (renderer != null && MixerSession.TargetOf(renderer) == null)
                menu.AddItem(new GUIContent("Edit " + renderer.name + " in Mixer"), false, () => { if (MixerSession.CanEdit(renderer, out string why)) { MixerSceneTool.FocusTarget = MixerSession.Edit(renderer); MixerModes.Notify("Editing " + renderer.name); } else MixerModes.Notify(why); });
            foreach (var target in MixerSession.Targets.Where(t => t.Alive).ToList())
            {
                var t = target;
                menu.AddItem(new GUIContent("Commit " + t.Name), false, () => MixerSession.Commit(t));
                menu.AddItem(new GUIContent("Revert " + t.Name), false, () => MixerSession.Revert(t));
            }
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Open scene in Blender"), false, () => { string error = BlenderProjectLauncher.Open(); if (error != null) MixerModes.Notify(error); });
            menu.AddItem(new GUIContent("Mixer window"), false, MixerWindow.Open);
            menu.AddItem(new GUIContent("Mixer Hierarchy"), false, MixerHierarchyWindow.Open);
            menu.DropDown(worldBound);
        }
    }

    /// <summary>The brush settings of the mode in one strip: radius, strength, falloff, symmetry; or weight and normalize.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerBrushSettingsElement : VisualElement
    {
        public const string Id = "orbiters.mixer.mode.brush";
        private readonly Slider radius, strength, weight;
        private readonly Label radiusValue, strengthValue, weightValue;
        private readonly Button falloff, symmetry, normalize;

        public MixerBrushSettingsElement()
        {
            MixerOverlays.Dress(this, "mx-header");
            radius = Field("Radius", "Brush radius in the world ([ and ] in the Scene view)", MixerBrushTiles.RadiusToSlider(MixerSceneTool.Radius), v => MixerSceneTool.Radius = MixerBrushTiles.SliderToRadius(v), out radiusValue);
            strength = Field("Strength", "How much each step moves (Shift+wheel)", MixerSceneTool.Strength, v => MixerSceneTool.Strength = v, out strengthValue);
            weight = Field("Weight", "What Draw and Replace paint toward", MixerSceneTool.WeightValue, v => MixerSceneTool.WeightValue = v, out weightValue);
            falloff = new Button(() =>
            {
                var menu = new GenericMenu();
                foreach (Falloff f in Enum.GetValues(typeof(Falloff))) { var value = f; menu.AddItem(new GUIContent(f.ToString()), MixerSceneTool.Falloff == f, () => MixerSceneTool.Falloff = value); }
                menu.DropDown(falloff.worldBound);
            }) { tooltip = "Falloff: how the effect fades toward the edge of the brush" };
            falloff.AddToClassList("mx-header__drop");
            Add(falloff);
            symmetry = MixerOverlays.ToolButton(IconGlyph.Mirror, "Symmetry: mirror every stroke across the mesh's X axis (F)", () => MixerSceneTool.Symmetry = !MixerSceneTool.Symmetry);
            Add(symmetry);
            normalize = new Button(() => MixerSceneTool.AutoNormalize = !MixerSceneTool.AutoNormalize) { text = "Normalize", tooltip = "Keep each vertex's bone weights summing to 1" };
            normalize.AddToClassList("mx-header__drop");
            Add(normalize);
            Refresh();
            RegisterCallback<AttachToPanelEvent>(_ => MixerSceneTool.Changed += Refresh);
            RegisterCallback<DetachFromPanelEvent>(_ => MixerSceneTool.Changed -= Refresh);
        }

        private Slider Field(string name, string tooltip, float value, Action<float> changed, out Label valueLabel)
        {
            var box = new VisualElement();
            box.AddToClassList("mx-header__field");
            box.tooltip = tooltip;
            var label = new Label(name);
            label.AddToClassList("mx-header__label");
            box.Add(label);
            var slider = new Slider(0f, 1f) { value = value };
            slider.AddToClassList("mx-header__slider");
            slider.RegisterValueChangedCallback(evt => { changed(evt.newValue); Refresh(); });
            box.Add(slider);
            valueLabel = new Label();
            valueLabel.AddToClassList("mx-header__value");
            box.Add(valueLabel);
            Add(box);
            return slider;
        }

        private void Refresh()
        {
            var mode = MixerSceneTool.Mode;
            bool sculpt = mode == MixerMode.Sculpt, weights = mode == MixerMode.Weights;
            style.display = sculpt || weights ? DisplayStyle.Flex : DisplayStyle.None;
            if (!(sculpt || weights)) return;
            if (Math.Abs(radius.value - MixerBrushTiles.RadiusToSlider(MixerSceneTool.Radius)) > 1e-4f) radius.SetValueWithoutNotify(MixerBrushTiles.RadiusToSlider(MixerSceneTool.Radius));
            radiusValue.text = MixerUi.Metres(MixerSceneTool.Radius);
            if (Math.Abs(strength.value - MixerSceneTool.Strength) > 1e-4f) strength.SetValueWithoutNotify(MixerSceneTool.Strength);
            strengthValue.text = MixerUi.Percent(MixerSceneTool.Strength);
            if (Math.Abs(weight.value - MixerSceneTool.WeightValue) > 1e-4f) weight.SetValueWithoutNotify(MixerSceneTool.WeightValue);
            weightValue.text = MixerSceneTool.WeightValue.ToString("0.00");
            weight.parent.style.display = weights ? DisplayStyle.Flex : DisplayStyle.None;
            falloff.style.display = sculpt ? DisplayStyle.Flex : DisplayStyle.None;
            falloff.text = MixerSceneTool.Falloff + " ▾";
            symmetry.style.display = sculpt ? DisplayStyle.Flex : DisplayStyle.None;
            symmetry.EnableInClassList("mx-tool--on", MixerSceneTool.Symmetry);
            normalize.style.display = weights ? DisplayStyle.Flex : DisplayStyle.None;
            normalize.EnableInClassList("mx-tool--on", MixerSceneTool.AutoNormalize);
        }
    }

    /// <summary>Commit (save the edits of the focused mesh as its mesh asset); shown while a session holds edits.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerCommitButton : EditorToolbarButton
    {
        public const string Id = "orbiters.mixer.mode.commit";

        public MixerCommitButton()
        {
            text = "Commit";
            tooltip = "Save the edits of the edited mesh as its mesh asset, in one Undo step (Mixer)";
            MixerOverlays.Dress(this, "mx-header__commit");
            clicked += () =>
            {
                var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
                if (target == null) return;
                var mesh = MixerSession.Commit(target);
                MixerModes.Notify(mesh != null ? "Committed " + target.Name : "Nothing to commit");
            };
            Refresh();
            RegisterCallback<AttachToPanelEvent>(_ => { MixerSession.Changed += Refresh; MixerSceneTool.Changed += Refresh; schedule.Execute(Refresh).Every(500); });
            RegisterCallback<DetachFromPanelEvent>(_ => { MixerSession.Changed -= Refresh; MixerSceneTool.Changed -= Refresh; });
        }

        private void Refresh()
        {
            var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
            bool show = target != null && target.Alive && target.Document.Changed;
            style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    /// <summary>Revert (the original mesh back); shown while a session is open.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerRevertButton : EditorToolbarButton
    {
        public const string Id = "orbiters.mixer.mode.revert";

        public MixerRevertButton()
        {
            text = "Revert";
            tooltip = "Put the edited mesh's own mesh back and close its Mixer session";
            clicked += () =>
            {
                var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
                if (target == null) return;
                string name = target.Name;
                MixerSession.Revert(target);
                MixerModes.Notify("Reverted " + name);
            };
            Refresh();
            RegisterCallback<AttachToPanelEvent>(_ => { MixerSession.Changed += Refresh; MixerSceneTool.Changed += Refresh; });
            RegisterCallback<DetachFromPanelEvent>(_ => { MixerSession.Changed -= Refresh; MixerSceneTool.Changed -= Refresh; });
        }

        private void Refresh()
        {
            var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
            style.display = target != null && target.Alive ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    /// <summary>The Mixer window, for the layers, groups, join and render pages and the Blender link.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerWindowButton : EditorToolbarButton
    {
        public const string Id = "orbiters.mixer.mode.window";

        public MixerWindowButton()
        {
            tooltip = "Open the Mixer window (layers, vertex groups, join, render, the Blender link)";
            var icon = new MixerLogo.Element { Color = new Color(0.235f, 0.949f, 0.604f), Cut = new Color(0.11f, 0.118f, 0.137f) };
            icon.style.width = 14; icon.style.height = 14; icon.pickingMode = PickingMode.Ignore;
            Add(icon);
            clicked += MixerWindow.Open;
        }
    }
}
