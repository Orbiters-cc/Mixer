using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    // The recent assets as a side panel at the right when the window is wide enough: the open folder stays in view beside
    // them, and a click shows the asset in its folder there. Narrower, they are the tree's Recent line (shown in the right
    // pane) as before.
    internal sealed partial class MixerProjectWindow
    {
        // The panes' width from which the recent assets dock at the right (the list keeps about 470 points).
        private const float RecentDockWidth = 760f;
        private const int RecentPanelLimit = 80;

        private VisualElement recentPanel;
        private ScrollView recentList;
        private Label recentSubtitle;
        private readonly List<(VisualElement row, string path)> recentRows = new List<(VisualElement, string)>();
        [NonSerialized] private bool recentDocked, recentPanelDirty = true;
        private string recentDragPath, lastRecentPressed;
        private Vector2 recentDragStart;
        private double lastRecentPressTime;

        private VisualElement BuildRecentPanel()
        {
            var panel = MixerUi.Box("mxp-usages");
            panel.AddToClassList("mxp-recent");
            var header = MixerUi.Box("mxp-usages__header");
            var icon = new VectorIcon(IconGlyph.Clock);
            icon.AddToClassList("mxp-usages__icon");
            header.Add(icon);
            var titles = MixerUi.Box("mxp-usages__titles");
            titles.Add(MixerUi.Text("Recent", "mxp-usages__title"));
            recentSubtitle = MixerUi.Text("", "mxp-usages__subtitle");
            titles.Add(recentSubtitle);
            header.Add(titles);
            panel.Add(header);
            recentList = new ScrollView(ScrollViewMode.Vertical);
            recentList.AddToClassList("mxp-usages__list");
            recentList.RegisterCallback<PointerMoveEvent>(OnRecentPointerMove);
            recentList.RegisterCallback<PointerUpEvent>(_ => recentDragPath = null);
            panel.Add(recentList);
            panel.AddManipulator(new ContextualMenuManipulator(evt => evt.menu.AppendAction("Forget the recent assets", _ => RecentAssets.Clear())));
            MixerUi.Show(panel, false);
            return panel;
        }

        // Docks the panel when the panes are wide enough (beside the usage panel when that is open), else gives the
        // recent assets back to the tree's Recent line.
        private void SyncRecentDock()
        {
            if (panes == null || recentPanel == null) return;
            float width = panes.layout.width;
            if (float.IsNaN(width) || width <= 0f) return;
            bool dock = width >= RecentDockWidth + (UsagePanelOpen ? usagePanel.layout.width : 0f);
            if (dock == recentDocked) return;
            recentDocked = dock;
            MixerUi.Show(recentPanel, dock);
            treeDirty = true;
            if (!dock) return;
            recentPanelDirty = true;
            // The right pane goes back to the open folder: the recent assets are beside it now.
            if (showRecent)
            {
                showRecent = false;
                MarkDirty();
            }
        }

        private void FillRecentPanel()
        {
            recentPanelDirty = false;
            recentList.Clear();
            recentRows.Clear();
            var list = RecentAssets.List();
            recentSubtitle.text = list.Count == 0 ? "The assets you select or open show here" : "Selected or opened lately";
            string section = null;
            var today = DateTime.Now.Date;
            foreach (var (path, when) in list.Take(RecentPanelLimit))
            {
                string heading = RecentHeading(when, today);
                if (heading != section)
                {
                    var group = MixerUi.Box("mxp-usages__group");
                    group.Add(MixerUi.Text(heading, "mxp-usages__group-name"));
                    recentList.Add(group);
                    section = heading;
                }
                recentList.Add(RecentRow(path, when));
            }
            SyncRecentRows();
        }

        // Today, Yesterday, This week or Earlier: the sections of the recent assets (`when` in UTC).
        private static string RecentHeading(DateTime when, DateTime today)
        {
            var day = when.ToLocalTime().Date;
            return day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day > today.AddDays(-7) ? "This week" : "Earlier";
        }

        private VisualElement RecentRow(string path, DateTime when)
        {
            var row = MixerUi.Box("mxp-use");
            var icon = new Image
            {
                image = AssetDatabase.GetCachedIcon(path) ?? EditorGUIUtility.IconContent("DefaultAsset Icon").image, scaleMode = ScaleMode.ScaleToFit,
                pickingMode = PickingMode.Ignore,
            };
            icon.AddToClassList("mxp-use__icon");
            row.Add(icon);
            var texts = MixerUi.Box("mxp-use__texts", PickingMode.Ignore);
            texts.Add(MixerUi.Text(System.IO.Path.GetFileNameWithoutExtension(path), "mxp-use__title"));
            // Its folder's name and when (the whole path is in the tooltip: the panel is narrow).
            texts.Add(MixerUi.Text(System.IO.Path.GetFileName(ParentOf(path)) + " · " + ProjectFiles.Ago(when), "mxp-use__detail"));
            row.Add(texts);
            row.tooltip = path + "\nClick: select and show in its folder · double click: open · drag it where it goes";
            // Before the press (which stops the event): where a drag would start.
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                recentDragPath = path;
                recentDragStart = evt.position;
            }, TrickleDown.TrickleDown);
            MixerUi.Press(row, () =>
            {
                double now = EditorApplication.timeSinceStartup;
                bool second = lastRecentPressed == path && now - lastRecentPressTime < 0.4;
                lastRecentPressed = path;
                lastRecentPressTime = now;
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null) return;
                if (second) AssetDatabase.OpenAsset(asset);
                // The window follows Unity's selection: the asset shows lit in its folder.
                else Selection.activeObject = asset;
                SyncRecentRows();
            });
            recentRows.Add((row, path));
            return row;
        }

        private void OnRecentPointerMove(PointerMoveEvent evt)
        {
            if (recentDragPath == null) return;
            if ((evt.pressedButtons & 1) == 0) { recentDragPath = null; return; }
            if (((Vector2)evt.position - recentDragStart).magnitude < 5f) return;
            string path = recentDragPath;
            recentDragPath = null;
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new[] { asset };
            DragAndDrop.paths = new[] { path };
            DragAndDrop.StartDrag(asset.name);
        }

        private void SyncRecentRows()
        {
            string active = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            foreach (var (row, path) in recentRows) row.EnableInClassList("mxp-use--on", path == active);
        }
    }
}
