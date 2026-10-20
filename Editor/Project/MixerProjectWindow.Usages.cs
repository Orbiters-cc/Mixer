using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    // The target button: where an asset is used. One place is selected at once; several open the side panel, which
    // lists them by kind (in the scene, the animators playing an animation, other assets) to select with a click and
    // frame or open with a double click.
    internal sealed partial class MixerProjectWindow
    {
        private const int UsageLimit = 300;

        // Not kept across a script reload: the lookup they wait for is gone then.
        [NonSerialized] private string usagePath;
        [NonSerialized] private bool usageBusy;
        private Label usageTitle, usageSubtitle;
        private ScrollView usageList;
        private readonly List<(VisualElement row, Object target)> usageRows = new List<(VisualElement, Object)>();
        private Object lastUsePressed;
        private double lastUsePressTime;

        private void HookContent()
        {
            foreach (var viewport in new[] { content.Viewport, grid.Viewport })
            {
                viewport.AddManipulator(new ContextualMenuManipulator(BuildContextMenu));
                viewport.RegisterCallback<PointerDownEvent>(OnViewportPointerDown);
                viewport.RegisterCallback<PointerMoveEvent>(OnViewportPointerMove);
                viewport.RegisterCallback<PointerUpEvent>(_ => EndDrag());
                viewport.RegisterCallback<DragUpdatedEvent>(evt => OnDragUpdated(evt, false));
                viewport.RegisterCallback<DragPerformEvent>(evt => OnDragPerform(evt, false));
                viewport.RegisterCallback<DragLeaveEvent>(_ => SetDropFolder(null));
                viewport.RegisterCallback<DragExitedEvent>(_ => EndDrag());
            }
            tree.Viewport.AddManipulator(new ContextualMenuManipulator(BuildTreeMenu));
            tree.Viewport.RegisterCallback<DragUpdatedEvent>(evt => OnDragUpdated(evt, true));
            tree.Viewport.RegisterCallback<DragPerformEvent>(evt => OnDragPerform(evt, true));
            tree.Viewport.RegisterCallback<DragLeaveEvent>(_ => SetDropFolder(null));
            tree.Viewport.RegisterCallback<DragExitedEvent>(_ => EndDrag());
        }

        private VisualElement BuildUsagePanel()
        {
            var panel = MixerUi.Box("mxp-usages");
            var header = MixerUi.Box("mxp-usages__header");
            var icon = new VectorIcon(IconGlyph.Target);
            icon.AddToClassList("mxp-usages__icon");
            header.Add(icon);
            var titles = MixerUi.Box("mxp-usages__titles");
            usageTitle = MixerUi.Text("", "mxp-usages__title");
            usageSubtitle = MixerUi.Text("", "mxp-usages__subtitle");
            titles.Add(usageTitle);
            titles.Add(usageSubtitle);
            header.Add(titles);
            header.Add(MixerUi.Icon(IconGlyph.Refresh, "Look again", () => { if (usagePath != null) ShowUsages(usagePath); }, "mxp-usages__button"));
            header.Add(MixerUi.Icon(IconGlyph.Close, "Close (Esc)", CloseUsages, "mxp-usages__button"));
            panel.Add(header);
            usageList = new ScrollView(ScrollViewMode.Vertical);
            usageList.AddToClassList("mxp-usages__list");
            panel.Add(usageList);
            MixerUi.Show(panel, false);
            return panel;
        }

        private bool UsagePanelOpen => usagePanel != null && usagePanel.style.display != DisplayStyle.None;

        /// <summary>
        /// Where <paramref name="path"/> is used. The button lights up at once; the search runs a frame later (it reads
        /// the open scenes and the project's references). One place is selected, several fill the side panel.
        /// </summary>
        internal void ShowUsages(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            usagePath = path;
            usageBusy = true;
            RefreshContent();
            if (UsagePanelOpen)
            {
                usageTitle.text = System.IO.Path.GetFileName(path);
                usageSubtitle.text = "Looking…";
            }
            root.schedule.Execute(() => FindUsages(path)).StartingIn(16);
        }

        /// <summary>Runs the search <see cref="ShowUsages"/> started without waiting for its frame (tests).</summary>
        internal void FindUsagesNow()
        {
            if (usagePath != null && usageBusy) FindUsages(usagePath);
        }

        private void FindUsages(string path)
        {
            if (usagePath != path || !usageBusy) return;
            List<AssetUsages.Use> uses;
            try { uses = AssetUsages.Of(path); }
            catch (Exception e) { uses = new List<AssetUsages.Use>(); toasts.Show("Could not look for its uses: " + e.Message, true); }
            usageBusy = false;
            string name = System.IO.Path.GetFileName(path);
            if (uses.Count == 0)
            {
                toasts.Show(name + " is not used in the open scenes nor by other assets.");
                if (UsagePanelOpen) FillUsages(path, uses); else usagePath = null;
            }
            else if (uses.Count == 1 && !UsagePanelOpen)
            {
                usagePath = null;
                GoTo(uses[0]);
                toasts.Show("Used once: " + uses[0].Title);
            }
            else FillUsages(path, uses);
            RefreshContent();
        }

        private void FillUsages(string path, List<AssetUsages.Use> uses)
        {
            MixerUi.Show(usagePanel, true);
            usageTitle.text = System.IO.Path.GetFileName(path);
            usageTitle.tooltip = path;
            int scene = uses.Count(u => u.Group == AssetUsages.InScene);
            usageSubtitle.text = uses.Count == 0 ? "Not used in the open scenes nor by other assets"
                : (uses.Count == 1 ? "1 place" : uses.Count + " places") + (scene > 0 ? " · " + scene + " in the scene" : "");
            usageList.Clear();
            usageRows.Clear();
            foreach (var group in uses.Take(UsageLimit).GroupBy(u => u.Group))
            {
                var heading = MixerUi.Box("mxp-usages__group");
                heading.Add(MixerUi.Text(group.Key, "mxp-usages__group-name"));
                heading.Add(MixerUi.Text(group.Count().ToString(), "mxp-usages__group-count"));
                usageList.Add(heading);
                foreach (var use in group) usageList.Add(UsageRow(use));
            }
            if (uses.Count > UsageLimit) usageList.Add(MixerUi.Text("And " + (uses.Count - UsageLimit) + " more", "mxp-usages__more"));
            SyncUsageRows();
        }

        private VisualElement UsageRow(AssetUsages.Use use)
        {
            var row = MixerUi.Box("mxp-use");
            var icon = new Image { image = use.Icon, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            icon.AddToClassList("mxp-use__icon");
            row.Add(icon);
            var texts = MixerUi.Box("mxp-use__texts", PickingMode.Ignore);
            texts.Add(MixerUi.Text(use.Title, "mxp-use__title"));
            if (!string.IsNullOrEmpty(use.Detail)) texts.Add(MixerUi.Text(use.Detail, "mxp-use__detail"));
            row.Add(texts);
            row.tooltip = use.Title + (string.IsNullOrEmpty(use.Detail) ? "" : "\n" + use.Detail) + "\nClick: select · double click: " + (use.Target is GameObject g && g.scene.IsValid() ? "frame in the Scene view" : "open");
            MixerUi.Press(row, () =>
            {
                double now = EditorApplication.timeSinceStartup;
                bool second = lastUsePressed == use.Target && now - lastUsePressTime < 0.4;
                lastUsePressed = use.Target;
                lastUsePressTime = now;
                if (second) Open(use); else GoTo(use);
            });
            usageRows.Add((row, use.Target));
            return row;
        }

        // Selects the place (a scene object, an asset), shows it in the Hierarchy or here.
        private void GoTo(AssetUsages.Use use)
        {
            if (use.Target == null) return;
            Selection.activeObject = use.Target;
            MixerPing.Ping(use.Target);
            SyncUsageRows();
        }

        private static void Open(AssetUsages.Use use)
        {
            if (use.Target == null) return;
            Selection.activeObject = use.Target;
            if (use.Target is GameObject go && go.scene.IsValid()) SceneView.FrameLastActiveSceneView();
            else AssetDatabase.OpenAsset(use.Target);
        }

        private void SyncUsageRows()
        {
            var active = Selection.activeObject;
            foreach (var (row, target) in usageRows) row.EnableInClassList("mxp-use--on", target != null && target == active);
        }

        private void CloseUsages()
        {
            usagePath = null;
            usageBusy = false;
            MixerUi.Show(usagePanel, false);
            usageList.Clear();
            usageRows.Clear();
            RefreshContent();
        }
    }
}
