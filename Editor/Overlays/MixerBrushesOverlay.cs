using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Blender's toolbar in the Scene view: the brushes of the sculpt or weight mode as a strip of small icon buttons at
    /// the left (two columns as a panel, one line as a toolbar), shown while one of those modes is on and hidden in
    /// Object mode. The brush settings live in the header strip (<see cref="MixerModeOverlay"/>).
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Mixer brushes", true)]
    internal sealed class MixerBrushesOverlay : Overlay, ICreateVerticalToolbar, ICreateHorizontalToolbar
    {
        public const string Id = "orbiters.mixer.brushes";
        private readonly List<Action> refreshers = new List<Action>();
        private MixerMode built = MixerMode.None;
        private VisualElement panel;
        private OverlayToolbar toolbar;

        public override void OnCreated()
        {
            MixerSceneTool.Changed += OnToolChanged;
            MixerOverlays.FirstPlacement(this, new Vector2(4f, 182f));
            // The canvas applies the saved or default display after OnCreated: hide after that, on the next tick.
            EditorApplication.CallbackFunction once = null;
            once = () => { EditorApplication.update -= once; OnToolChanged(); };
            EditorApplication.update += once;
            OnToolChanged();
        }

        public override void OnWillBeDestroyed() => MixerSceneTool.Changed -= OnToolChanged;

        private void OnToolChanged()
        {
            bool brushes = MixerSceneTool.Mode == MixerMode.Sculpt || MixerSceneTool.Mode == MixerMode.Weights;
            if (displayed != brushes) displayed = brushes;
            if (brushes && built != MixerSceneTool.Mode)
            {
                if (panel != null) Fill(panel);
                if (toolbar != null) Fill(toolbar);
            }
            foreach (var refresh in refreshers) refresh();
        }

        // Panel layout: two columns of icons. Toolbar layouts: one line of them.
        public override VisualElement CreatePanelContent()
        {
            panel = MixerOverlays.Content("mx-strip");
            panel.AddToClassList("mx-strip--grid");
            Fill(panel);
            return panel;
        }

        public OverlayToolbar CreateVerticalToolbarContent() => CreateToolbar();
        public OverlayToolbar CreateHorizontalToolbarContent() => CreateToolbar();

        private OverlayToolbar CreateToolbar()
        {
            toolbar = new OverlayToolbar();
            MixerOverlays.Dress(toolbar, "mx-strip");
            Fill(toolbar);
            return toolbar;
        }

        private void Fill(VisualElement strip)
        {
            strip.Clear();
            refreshers.Clear();
            built = MixerSceneTool.Mode;
            if (built == MixerMode.Weights)
            {
                foreach (var (kind, glyph, tip) in MixerBrushTiles.WeightBrushes)
                {
                    var k = kind;
                    strip.Add(Tool(glyph, kind + ": " + tip, () => { MixerSceneTool.WeightKind = k; if (MixerSceneTool.Mode != MixerMode.Weights) MixerModes.Enter(MixerMode.Weights); }, () => MixerSceneTool.WeightKind == k));
                }
            }
            else
            {
                foreach (var brush in Brushes.All)
                {
                    var b = brush;
                    strip.Add(Tool(brush.Icon, brush.Name + ": " + brush.Description, () => { MixerSceneTool.ActiveBrush = b; if (MixerSceneTool.Mode != MixerMode.Sculpt) MixerModes.Enter(MixerMode.Sculpt); }, () => MixerSceneTool.ActiveBrush == b));
                }
            }
        }

        private Button Tool(IconGlyph glyph, string tooltip, Action action, Func<bool> active)
        {
            var button = MixerOverlays.ToolButton(glyph, tooltip, action);
            Action refresh = () => button.EnableInClassList("mx-tool--on", active());
            refreshers.Add(refresh);
            refresh();
            return button;
        }
    }

    /// <summary>What the Mixer overlays share: Mixer's stylesheet, a transparent chrome, small tool buttons, a first placement.</summary>
    internal static class MixerOverlays
    {
        private static readonly StyleSheet Style = AssetDatabase.LoadAssetAtPath<StyleSheet>(MixerInfo.BasePath + "/Editor/UI/mixer.uss");

        /// <summary>A content root with Mixer's styles and a see-through overlay around it.</summary>
        public static VisualElement Content(string className)
        {
            var root = new VisualElement();
            Dress(root, className);
            return root;
        }

        /// <summary>Mixer's stylesheet on an element, and the overlay chrome around it made transparent once it is on screen.</summary>
        public static void Dress(VisualElement element, string className)
        {
            element.AddToClassList(className);
            if (Style != null) element.styleSheets.Add(Style);
            element.RegisterCallback<AttachToPanelEvent>(_ => element.schedule.Execute(() => ClearChrome(element)).StartingIn(1));
        }

        // The overlay's own frame (background, border) goes: only the buttons and controls remain over the scene.
        private static void ClearChrome(VisualElement element)
        {
            int depth = 0;
            for (var e = element.parent; e != null && depth < 8; e = e.parent, depth++)
            {
                bool chrome = false;
                foreach (string c in e.GetClasses()) if (c.StartsWith("unity-overlay", StringComparison.Ordinal) || c.StartsWith("overlay", StringComparison.Ordinal)) { chrome = true; break; }
                if (!chrome) continue;
                e.style.backgroundColor = Color.clear;
                e.style.borderTopWidth = 0; e.style.borderBottomWidth = 0; e.style.borderLeftWidth = 0; e.style.borderRightWidth = 0;
                e.style.backgroundImage = null;
            }
        }

        /// <summary>A small icon button of the strips (26 px).</summary>
        public static Button ToolButton(IconGlyph glyph, string tooltip, Action action)
        {
            var button = new Button(action) { tooltip = tooltip, text = string.Empty };
            button.AddToClassList("mx-tool");
            var icon = new VectorIcon(glyph) { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("mx-tool__icon");
            button.Add(icon);
            return button;
        }

        /// <summary>
        /// Where a Mixer overlay first appears: floating at a Blender-like spot (its layout is the user's, through the
        /// overlay's menu). Done once per overlay and user; afterwards the placement Unity saved stands.
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
