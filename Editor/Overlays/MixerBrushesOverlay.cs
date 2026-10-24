using System;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Blender's toolbar in the Scene view: the brushes of the sculpt or weight mode as a column of large pictures under
    /// Unity's Tools strip, as wide as it (a row when docked at the top or bottom; pictures with names as a floating
    /// panel), shown while one of those modes is on. The brush settings live in the header (<see cref="MixerModeOverlay"/>).
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Mixer brushes", true, defaultDockZone = DockZone.LeftColumn, defaultDockPosition = DockPosition.Top,
        defaultDockIndex = 1, defaultLayout = Layout.VerticalToolbar)]
    internal sealed class MixerBrushesOverlay : Overlay, ICreateVerticalToolbar, ICreateHorizontalToolbar
    {
        // Unity's Tools strip is the first overlay of the left column: the brushes come right under it.
        public const string Id = "orbiters.mixer.brush-bar";

        public override void OnCreated()
        {
            MixerSceneTool.Changed += OnToolChanged;
            // The canvas applies the saved or default display after OnCreated: hide after that, on the next tick.
            EditorApplication.CallbackFunction once = null;
            once = () => { EditorApplication.update -= once; OnToolChanged(); };
            EditorApplication.update += once;
            OnToolChanged();
        }

        public override void OnWillBeDestroyed() => MixerSceneTool.Changed -= OnToolChanged;

        private void OnToolChanged()
        {
            if (displayed != MixerOverlays.Brushing) displayed = MixerOverlays.Brushing;
        }

        public override VisualElement CreatePanelContent() => Strip(new VisualElement(), "mx-brushes--panel", names: true);
        public OverlayToolbar CreateVerticalToolbarContent() => (OverlayToolbar)Strip(new OverlayToolbar(), "mx-brushes--vertical", names: false);
        public OverlayToolbar CreateHorizontalToolbarContent() => (OverlayToolbar)Strip(new OverlayToolbar(), "mx-brushes--horizontal", names: false);

        // The strip rebuilds itself when the mode changes between sculpting and weights.
        private static VisualElement Strip(VisualElement strip, string layout, bool names)
        {
            MixerOverlays.Dress(strip, "mx-brushes");
            strip.AddToClassList(layout);
            var built = (MixerMode)(-1);
            MixerOverlays.Follow(strip, () =>
            {
                var mode = MixerSceneTool.Mode == MixerMode.Weights ? MixerMode.Weights : MixerMode.Sculpt;
                if (mode == built) return;
                built = mode;
                Fill(strip, mode, names);
            });
            return strip;
        }

        private static void Fill(VisualElement strip, MixerMode mode, bool names)
        {
            strip.Clear();
            if (mode == MixerMode.Weights)
            {
                foreach (var (kind, glyph, tip) in MixerBrushTiles.WeightBrushes)
                {
                    var k = kind;
                    strip.Add(Tool(glyph, kind.ToString(), tip, names, () =>
                    {
                        MixerSceneTool.WeightKind = k;
                        if (MixerSceneTool.Mode != MixerMode.Weights) MixerModes.Enter(MixerMode.Weights);
                    }, () => MixerSceneTool.WeightKind == k));
                }
            }
            else
            {
                foreach (var brush in Brushes.All)
                {
                    var b = brush;
                    strip.Add(Tool(brush.Glyph, brush.Name, brush.Description, names, () =>
                    {
                        MixerSceneTool.ActiveBrush = b;
                        if (MixerSceneTool.Mode != MixerMode.Sculpt) MixerModes.Enter(MixerMode.Sculpt);
                    }, () => MixerSceneTool.ActiveBrush == b));
                }
            }
            // A strip of buttons, as Unity's Tools: rounded at its two ends only.
            if (strip.childCount > 0)
            {
                strip[0].AddToClassList("mx-brush--first");
                strip[strip.childCount - 1].AddToClassList("mx-brush--last");
            }
        }

        private static Button Tool(BrushGlyph glyph, string name, string tip, bool withName, Action pick, Func<bool> active)
        {
            var button = new Button { text = string.Empty, tooltip = name + ": " + tip };
            button.AddToClassList("mx-brush");
            var icon = new BrushIcon(glyph);
            icon.AddToClassList("mx-brush__icon");
            button.Add(icon);
            if (withName)
            {
                var label = new Label(name) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("mx-brush__name");
                button.Add(label);
            }
            MixerUi.Press(button, () => { button.AddToClassList("mx-brush--on"); pick(); });
            MixerOverlays.Follow(button, () => button.EnableInClassList("mx-brush--on", active()));
            return button;
        }
    }

    /// <summary>What the Mixer overlays share: Mixer's stylesheet, toolbar icons, following the tool's state, a first placement.</summary>
    internal static class MixerOverlays
    {
        private static readonly StyleSheet Style = AssetDatabase.LoadAssetAtPath<StyleSheet>(MixerInfo.BasePath + "/Editor/UI/mixer.uss");

        /// <summary>True in the modes that paint with a brush (Sculpt, Weight Paint).</summary>
        public static bool Brushing => MixerSceneTool.Mode == MixerMode.Sculpt || MixerSceneTool.Mode == MixerMode.Weights;

        /// <summary>Mixer's stylesheet on an element (with the light skin's colours when Unity uses it), and a class of its own.</summary>
        public static void Dress(VisualElement element, string className = null)
        {
            if (className != null) element.AddToClassList(className);
            if (!EditorGUIUtility.isProSkin) element.AddToClassList("mx-skin-light");
            if (Style != null) element.styleSheets.Add(Style);
            Orbiters.Mixer.Editor.MixerAccent.Apply(element);
        }

        /// <summary>A vector picture in the place and size of a Unity toolbar icon (16 points, before the label).</summary>
        public static T Glyph<T>(VisualElement element, T icon) where T : VisualElement
        {
            icon.pickingMode = PickingMode.Ignore;
            icon.AddToClassList("mx-bar__icon");
            element.Insert(0, icon);
            return icon;
        }

        /// <summary>
        /// Runs <paramref name="refresh"/> now and whenever the tool (and, with <paramref name="session"/>, a session and
        /// the selection) changes while the element is on screen.
        /// </summary>
        public static void Follow(VisualElement element, Action refresh, bool session = false)
        {
            element.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                MixerSceneTool.Changed += refresh;
                if (session) { MixerSession.Changed += refresh; Selection.selectionChanged += refresh; }
                refresh();
            });
            element.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                MixerSceneTool.Changed -= refresh;
                if (session) { MixerSession.Changed -= refresh; Selection.selectionChanged -= refresh; }
            });
            refresh();
        }

        /// <summary>
        /// Where a Mixer overlay first appears when it floats: at a Blender-like spot (its layout is the user's, through
        /// the overlay's menu). Done once per overlay and user; afterwards the placement Unity saved stands.
        /// </summary>
        public static void FirstPlacement(Overlay overlay, Vector2 position)
        {
            string key = "Orbiters.Mixer.Overlay.Placed." + overlay.id;
            if (EditorPrefs.GetBool(key, false)) return;
            EditorPrefs.SetBool(key, true);
            EditorApplication.CallbackFunction once = null;
            once = () =>
            {
                EditorApplication.update -= once;
                try
                {
                    overlay.Undock();
                    overlay.floatingPosition = position;
                    overlay.collapsed = false;
                }
                catch (Exception) { }
            };
            EditorApplication.update += once;
        }
    }
}
