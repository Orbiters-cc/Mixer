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
    /// Blender's header in the Scene view, made of Unity's own toolbar controls: the mode (Object Mode, Sculpt Mode,
    /// Weight Paint, Edit Armature), the brush and its settings in the sculpt and weight modes (radius, strength, falloff
    /// and symmetry, or weight and normalize), Commit and Revert while a session is open, and the Mixer window. Every
    /// feature of Mixer starts from here.
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Mixer", true)]
    internal sealed class MixerModeOverlay : ToolbarOverlay
    {
        public const string Id = "orbiters.mixer.header";

        public MixerModeOverlay() : base(MixerModeDropdown.Id, MixerBrushDropdown.Id, MixerWeightField.Id, MixerRadiusField.Id, MixerStrengthField.Id,
            MixerFalloffDropdown.Id, MixerSymmetryToggle.Id, MixerNormalizeToggle.Id, MixerCommitButton.Id, MixerRevertButton.Id, MixerWindowButton.Id) { }

        // Right of Unity's Tools strip and level with it, as Blender's header sits beside its toolbar.
        public override void OnCreated() => MixerOverlays.FirstPlacement(this, new Vector2(42f, 26f));
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerModeDropdown : EditorToolbarDropdown
    {
        public const string Id = "orbiters.mixer.header.mode";
        private readonly VectorIcon glyph;

        public MixerModeDropdown()
        {
            tooltip = "Mixer mode: Object, Sculpt, Weight Paint or Edit Armature, on the selected mesh (Blender's modes)";
            MixerOverlays.Dress(this);
            glyph = MixerOverlays.Glyph(this, new VectorIcon(MixerModes.IconOf(MixerMode.None)));
            MixerUi.Press(this, ShowMenu);
            MixerOverlays.Follow(this, Refresh, session: true);
        }

        private void Refresh()
        {
            var mode = MixerSceneTool.Mode;
            text = MixerModes.NameOf(mode);
            glyph.Glyph = MixerModes.IconOf(mode);
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

    /// <summary>The active brush with its picture, as Blender's header shows the active tool; the menu lists the mode's brushes.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerBrushDropdown : EditorToolbarDropdown
    {
        public const string Id = "orbiters.mixer.header.brush";
        private readonly BrushIcon glyph;

        public MixerBrushDropdown()
        {
            MixerOverlays.Dress(this, "mx-bar--group");
            glyph = MixerOverlays.Glyph(this, new BrushIcon(BrushGlyph.Grab));
            MixerUi.Press(this, ShowMenu);
            MixerOverlays.Follow(this, Refresh);
        }

        private void Refresh()
        {
            var mode = MixerSceneTool.Mode;
            style.display = MixerOverlays.Brushing ? DisplayStyle.Flex : DisplayStyle.None;
            if (mode == MixerMode.Sculpt)
            {
                var brush = MixerSceneTool.ActiveBrush;
                text = brush.Name;
                glyph.Glyph = brush.Glyph;
                tooltip = brush.Name + ": " + brush.Description;
            }
            else if (mode == MixerMode.Weights)
            {
                var kind = MixerSceneTool.WeightKind;
                text = kind.ToString();
                glyph.Glyph = MixerBrushTiles.GlyphOf(kind);
                tooltip = kind + ": " + MixerBrushTiles.WeightBrushes.First(b => b.Kind == kind).Tip;
            }
        }

        private void ShowMenu()
        {
            var menu = new GenericMenu();
            if (MixerSceneTool.Mode == MixerMode.Weights)
            {
                foreach (var brush in MixerBrushTiles.WeightBrushes)
                {
                    var kind = brush.Kind;
                    menu.AddItem(new GUIContent(kind.ToString()), MixerSceneTool.WeightKind == kind, () => MixerSceneTool.WeightKind = kind);
                }
            }
            else
            {
                foreach (var brush in Brushes.All)
                {
                    var b = brush;
                    menu.AddItem(new GUIContent(b.Name), MixerSceneTool.ActiveBrush == b, () => MixerSceneTool.ActiveBrush = b);
                }
            }
            menu.DropDown(worldBound);
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerWeightField : MixerSliderField
    {
        public const string Id = "orbiters.mixer.header.weight";

        public MixerWeightField() : base("Weight", "What Draw and Replace paint toward",
            () => MixerSceneTool.WeightValue, v => MixerSceneTool.WeightValue = v,
            () => MixerSceneTool.WeightValue.ToString("0.00"), () => MixerSceneTool.Mode == MixerMode.Weights) { }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerRadiusField : MixerSliderField
    {
        public const string Id = "orbiters.mixer.header.radius";

        public MixerRadiusField() : base("Radius", "How far the brush reaches, in the world ([ and ] in the Scene view)",
            () => MixerBrushTiles.RadiusToSlider(MixerSceneTool.Radius), v => MixerSceneTool.Radius = MixerBrushTiles.SliderToRadius(v),
            () => MixerUi.Metres(MixerSceneTool.Radius), () => MixerOverlays.Brushing) { }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerStrengthField : MixerSliderField
    {
        public const string Id = "orbiters.mixer.header.strength";

        public MixerStrengthField() : base("Strength", "How much each step moves (Shift+wheel in the Scene view)",
            () => MixerSceneTool.Strength, v => MixerSceneTool.Strength = v,
            () => MixerUi.Percent(MixerSceneTool.Strength), () => MixerOverlays.Brushing) { }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerFalloffDropdown : EditorToolbarDropdown
    {
        public const string Id = "orbiters.mixer.header.falloff";
        private readonly BrushIcon.FalloffCurve curve;

        public MixerFalloffDropdown()
        {
            tooltip = "Falloff: how the effect fades toward the edge of the brush";
            MixerOverlays.Dress(this);
            curve = MixerOverlays.Glyph(this, new BrushIcon.FalloffCurve());
            MixerUi.Press(this, () =>
            {
                var menu = new GenericMenu();
                foreach (Falloff falloff in Enum.GetValues(typeof(Falloff)))
                {
                    var value = falloff;
                    menu.AddItem(new GUIContent(MixerBrushTiles.NameOf(falloff)), MixerSceneTool.Falloff == falloff, () => MixerSceneTool.Falloff = value);
                }
                menu.DropDown(worldBound);
            });
            MixerOverlays.Follow(this, () =>
            {
                style.display = MixerSceneTool.Mode == MixerMode.Sculpt ? DisplayStyle.Flex : DisplayStyle.None;
                text = MixerBrushTiles.NameOf(MixerSceneTool.Falloff);
                curve.Falloff = MixerSceneTool.Falloff;
            });
        }
    }

    /// <summary>Blender's symmetry switch: every stroke mirrored across the mesh's X axis.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerSymmetryToggle : EditorToolbarToggle
    {
        public const string Id = "orbiters.mixer.header.symmetry";

        public MixerSymmetryToggle() : base("X")
        {
            tooltip = "Symmetry: mirror every stroke across the mesh's X axis (F)";
            MixerOverlays.Dress(this);
            MixerOverlays.Glyph(this, new VectorIcon(IconGlyph.Mirror));
            MixerUi.Press(this, () => { MixerSceneTool.Symmetry = !MixerSceneTool.Symmetry; SetValueWithoutNotify(MixerSceneTool.Symmetry); });
            MixerOverlays.Follow(this, () =>
            {
                style.display = MixerSceneTool.Mode == MixerMode.Sculpt ? DisplayStyle.Flex : DisplayStyle.None;
                SetValueWithoutNotify(MixerSceneTool.Symmetry);
            });
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerNormalizeToggle : EditorToolbarToggle
    {
        public const string Id = "orbiters.mixer.header.normalize";

        public MixerNormalizeToggle() : base("Normalize")
        {
            tooltip = "Keep each vertex's bone weights summing to 1";
            MixerOverlays.Dress(this);
            MixerUi.Press(this, () => { MixerSceneTool.AutoNormalize = !MixerSceneTool.AutoNormalize; SetValueWithoutNotify(MixerSceneTool.AutoNormalize); });
            MixerOverlays.Follow(this, () =>
            {
                style.display = MixerSceneTool.Mode == MixerMode.Weights ? DisplayStyle.Flex : DisplayStyle.None;
                SetValueWithoutNotify(MixerSceneTool.AutoNormalize);
            });
        }
    }

    /// <summary>Commit (save the edits of the focused mesh as its mesh asset); shown while a session holds edits.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerCommitButton : EditorToolbarButton
    {
        public const string Id = "orbiters.mixer.header.commit";

        public MixerCommitButton()
        {
            text = "Commit";
            tooltip = "Save the edits of the edited mesh as its mesh asset, in one Undo step (Mixer)";
            MixerOverlays.Dress(this, "mx-bar--group");
            MixerUi.Press(this, () =>
            {
                var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
                if (target == null) return;
                var mesh = MixerSession.Commit(target);
                MixerModes.Notify(mesh != null ? "Committed " + target.Name : "Nothing to commit");
            });
            MixerOverlays.Follow(this, Refresh, session: true);
            // Edits change the document without a session event: look again twice a second.
            RegisterCallback<AttachToPanelEvent>(_ => schedule.Execute(Refresh).Every(500));
        }

        private void Refresh()
        {
            var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
            style.display = target != null && target.Alive && target.Document.Changed ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    /// <summary>Revert (the original mesh back); shown while a session is open.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerRevertButton : EditorToolbarButton
    {
        public const string Id = "orbiters.mixer.header.revert";

        public MixerRevertButton()
        {
            text = "Revert";
            tooltip = "Put the edited mesh's own mesh back and close its Mixer session";
            MixerOverlays.Dress(this);
            MixerUi.Press(this, () =>
            {
                var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
                if (target == null) return;
                string name = target.Name;
                MixerSession.Revert(target);
                MixerModes.Notify("Reverted " + name);
            });
            MixerOverlays.Follow(this, () =>
            {
                var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
                style.display = target != null && target.Alive ? DisplayStyle.Flex : DisplayStyle.None;
            }, session: true);
        }
    }

    /// <summary>The Mixer window, for the layers, groups, join and render pages and the Blender link.</summary>
    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class MixerWindowButton : EditorToolbarButton
    {
        public const string Id = "orbiters.mixer.header.window";

        public MixerWindowButton()
        {
            tooltip = "Open the Mixer window (layers, vertex groups, join, render, the Blender link)";
            icon = MixerLogo.TabIcon();
            MixerOverlays.Dress(this, "mx-bar--group");
            MixerUi.Press(this, MixerWindow.Open);
        }
    }

    /// <summary>
    /// Blender's slider field in the look of Unity's toolbar buttons: the name at the left, the value at the right and a
    /// fill for how much. A press sets the value under the pointer at once and a drag follows it; the wheel nudges it.
    /// </summary>
    internal abstract class MixerSliderField : VisualElement
    {
        private readonly Func<float> get;
        private readonly Action<float> set;
        private readonly Func<string> valueText;
        private readonly Func<bool> shown;
        private readonly VisualElement fill;
        private readonly Label value;
        private bool dragging;

        protected MixerSliderField(string name, string tip, Func<float> get, Action<float> set, Func<string> valueText, Func<bool> shown)
        {
            this.get = get;
            this.set = set;
            this.valueText = valueText;
            this.shown = shown;
            tooltip = tip;
            AddToClassList("unity-editor-toolbar-element");
            MixerOverlays.Dress(this, "mx-field");
            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("mx-field__fill");
            Add(fill);
            var label = new Label(name) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("mx-field__name");
            Add(label);
            value = new Label { pickingMode = PickingMode.Ignore };
            value.AddToClassList("mx-field__value");
            Add(value);
            RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                dragging = true;
                this.CapturePointer(evt.pointerId);
                AddToClassList(MixerUi.PressedClass);
                SetAt(evt.localPosition.x);
                evt.StopPropagation();
            });
            RegisterCallback<PointerMoveEvent>(evt => { if (dragging && this.HasPointerCapture(evt.pointerId)) SetAt(evt.localPosition.x); });
            RegisterCallback<PointerUpEvent>(evt =>
            {
                if (!dragging) return;
                dragging = false;
                this.ReleasePointer(evt.pointerId);
                RemoveFromClassList(MixerUi.PressedClass);
            });
            RegisterCallback<PointerCaptureOutEvent>(_ => { dragging = false; RemoveFromClassList(MixerUi.PressedClass); });
            RegisterCallback<WheelEvent>(evt => { Set(get() - Mathf.Sign(evt.delta.y) * 0.02f); evt.StopPropagation(); });
            MixerOverlays.Follow(this, Refresh);
        }

        private void SetAt(float x)
        {
            if (layout.width > 0f) Set(x / layout.width);
        }

        private void Set(float normalized)
        {
            set(Mathf.Clamp01(normalized));
            Refresh();
        }

        private void Refresh()
        {
            bool visible = shown();
            style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;
            fill.style.width = Length.Percent(Mathf.Clamp01(get()) * 100f);
            value.text = valueText();
        }
    }
}
