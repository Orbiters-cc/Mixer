using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    // The icon grid, as in Unity's Project window: the slider in the status bar picks the icons' size, and all the way
    // left gives the list back. The grid shows the open folder (or the recent assets, or a search, under their
    // headings) as tiles with a picture of each asset; clicks, keys, drags, the context menu and the target button work
    // as in the list.
    internal sealed partial class MixerProjectWindow
    {
        private const float MinIcon = 36f, MaxIcon = 128f;
        // The slider's far left (below this) is the list.
        private const float ListStop = 0.04f;

        private sealed class GridLine
        {
            public int Heading = -1;
            public int First, Count;
        }

        private readonly List<GridLine> gridLines = new List<GridLine>();
        private VirtualList grid;
        private Slider sizeSlider;
        private int gridColumns = -1;

        /// <summary>Whether the right pane shows icons (else the list).</summary>
        internal bool GridMode => iconSize >= MinIcon;
        private float TileWidth => Mathf.Max(iconSize + 20f, 76f);
        private int TileSpan => Mathf.CeilToInt((iconSize + 32f) / RowHeight);

        private VirtualList BuildGrid()
        {
            grid = new VirtualList(RowHeight, MakeGridLine, BindGridLine);
            grid.SpanOf = index => index >= 0 && index < gridLines.Count && gridLines[index].Heading >= 0 ? 1 : TileSpan;
            grid.AddToClassList("mxp-grid");
            grid.RegisterCallback<GeometryChangedEvent>(_ => { if (GridMode && GridColumns() != gridColumns) LayOutGrid(); });
            return grid;
        }

        /// <summary>The icons' size in points; under the smallest icons, the list (tests; the slider does the same).</summary>
        internal void SetIconSize(float size)
        {
            bool wasGrid = GridMode;
            iconSize = size < MinIcon ? 0f : Mathf.Min(size, MaxIcon);
            sizeSlider?.SetValueWithoutNotify(SliderOf(iconSize));
            if (wasGrid != GridMode) { ApplyViewMode(); RebuildNow(); }
            else if (GridMode) LayOutGrid();
        }

        private static float SizeOf(float slider) => slider < ListStop ? 0f : Mathf.Lerp(MinIcon, MaxIcon, (slider - ListStop) / (1f - ListStop));
        private static float SliderOf(float size) => size < MinIcon ? 0f : ListStop + (1f - ListStop) * Mathf.InverseLerp(MinIcon, MaxIcon, size);

        private void ApplyViewMode()
        {
            if (grid == null) return;
            MixerUi.Show(content, !GridMode);
            MixerUi.Show(grid, GridMode);
            root.EnableInClassList("mxp-root--grid", GridMode);
            previewCache.Clear();
        }

        private VisualElement BuildStatusBar()
        {
            var bar = MixerUi.Box("mxp-footer");
            footer = MixerUi.Text("", "mxp-footer__text");
            bar.Add(footer);
            sizeSlider = new Slider(0f, 1f) { value = SliderOf(iconSize), tooltip = "Icon size: all the way left for the list, further right for bigger icons" };
            sizeSlider.AddToClassList("mxp-size");
            sizeSlider.RegisterValueChangedCallback(evt => SetIconSize(SizeOf(evt.newValue)));
            bar.Add(sizeSlider);
            return bar;
        }

        private int GridColumns()
        {
            float width = grid != null ? grid.Viewport.layout.width : float.NaN;
            if (float.IsNaN(width) || width <= 0f) width = Mathf.Max(200f, position.width - treeWidth - 20f);
            return Mathf.Max(1, Mathf.FloorToInt((width - 12f) / (TileWidth + 4f)));
        }

        // The lines of tiles: a heading takes a line of its own, the assets between headings fill lines of columns.
        private void LayOutGrid()
        {
            if (grid == null) return;
            gridColumns = GridColumns();
            gridLines.Clear();
            for (int i = 0; i < rows.Count;)
            {
                if (rows[i].IsHeading) { gridLines.Add(new GridLine { Heading = i }); i++; continue; }
                var line = new GridLine { First = i };
                while (i < rows.Count && !rows[i].IsHeading && line.Count < gridColumns) { line.Count++; i++; }
                gridLines.Add(line);
            }
            grid.SetItemCount(gridLines.Count, keepAtEnd: false);
            grid.RefreshRows();
        }

        private VisualElement MakeGridLine()
        {
            var line = MixerUi.Box("mxp-gline");
            line.Add(MixerUi.Text("", "mxp-gline__heading"));
            return line;
        }

        private void BindGridLine(VisualElement element, int index)
        {
            if (index < 0 || index >= gridLines.Count) return;
            var line = gridLines[index];
            var heading = (Label)element[0];
            bool isHeading = line.Heading >= 0;
            MixerUi.Show(heading, isHeading);
            if (isHeading) heading.text = rows[line.Heading].Name;
            int needed = isHeading ? 0 : line.Count;
            while (element.childCount - 1 < needed) element.Add(MakeTile());
            for (int t = 1; t < element.childCount; t++)
            {
                var tile = element[t];
                bool used = t - 1 < needed;
                MixerUi.Show(tile, used);
                if (used) BindTile(tile, line.First + t - 1);
                else tile.userData = -1;
            }
        }

        private static int TileIndex(VisualElement tile) => tile.userData is int index ? index : -1;

        private VisualElement MakeTile()
        {
            var tile = MixerUi.Box("mxp-tile");
            var picture = MixerUi.Box("mxp-tile__picture", PickingMode.Ignore);
            var image = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            image.AddToClassList("mxp-tile__image");
            picture.Add(image);
            tile.Add(picture);
            var chip = MixerUi.Text("new", "mxp-new mxp-tile__new");
            chip.pickingMode = PickingMode.Ignore;
            tile.Add(chip);
            tile.Add(MixerUi.Icon(IconGlyph.Target, "Where it is used: in the open scenes and by other assets", null, evt =>
            {
                var item = Row(TileIndex(tile));
                if (item != null && !item.IsHeading && !item.IsFolder) ShowUsages(item.Path);
                evt.StopPropagation();
            }, "mxp-tile__target"));
            tile.Add(MixerUi.Text("", "mxp-tile__name"));
            var rename = new TextField();
            rename.AddToClassList("mxp-tile__rename");
            MixerUi.Show(rename, false);
            tile.Add(rename);
            tile.RegisterCallback<PointerDownEvent>(evt => OnItemPointerDown(TileIndex(tile), evt));
            rename.RegisterCallback<FocusOutEvent>(_ => CommitRename(rename, true));
            rename.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { CommitRename(rename, true); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { CommitRename(rename, false); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            return tile;
        }

        private void BindTile(VisualElement tile, int index)
        {
            var item = Row(index);
            if (item == null) return;
            tile.userData = index;
            tile.style.width = TileWidth;
            tile.style.height = TileSpan * RowHeight - 4f;
            var picture = tile.Q(className: "mxp-tile__picture");
            picture.style.width = iconSize;
            picture.style.height = iconSize;
            tile.Q<Image>(className: "mxp-tile__image").image = PreviewOf(item);
            var name = tile.Q<Label>(className: "mxp-tile__name");
            name.text = item.Name;
            MixerUi.Show(tile.Q(className: "mxp-tile__new"), item.IsNew);
            var target = tile.Q(className: "mxp-tile__target");
            MixerUi.Show(target, !item.IsFolder);
            target.EnableInClassList("mxp-row__target--on", usagePath == item.Path);
            target.EnableInClassList("mxp-row__target--busy", usageBusy && usagePath == item.Path);
            tile.EnableInClassList("mxp-tile--picked", picked.Contains(item.Path));
            tile.EnableInClassList("mxp-tile--cursor", item.Path == cursor);
            tile.EnableInClassList("mxp-tile--drop", item.IsFolder && dropFolder != null && item.Path == dropFolder);
            tile.EnableInClassList("mxp-tile--ping", pingPath != null && item.Path == pingPath);
            tile.EnableInClassList("mxp-tile--folder", item.IsFolder);
            tile.tooltip = item.Name + item.Extension + "\n" + (item.MatchLabel != null ? item.MatchLabel + ": " + item.MatchValue + "\n" : "")
                           + (Searching || showRecent ? ParentOf(item.Path) + "\n" : "") + KindAndSize(item) + "\n" + DateText(item);
            var rename = tile.Q<TextField>(className: "mxp-tile__rename");
            bool renaming = renamingPath != null && item.Path == renamingPath;
            MixerUi.Show(name, !renaming);
            MixerUi.Show(rename, renaming);
            var previous = rename.userData as string;
            rename.userData = renaming ? item.Path : null;
            if (renaming && previous != item.Path)
            {
                rename.SetValueWithoutNotify(item.Name);
                rename.schedule.Execute(() => { rename.Q(TextField.textInputUssName)?.Focus(); rename.SelectAll(); }).StartingIn(10);
            }
        }

        // ---- What the list and the grid share ---------------------------------------------------------------------------

        private void RefreshContent()
        {
            if (GridMode) grid?.RefreshRows();
            else content?.RefreshRows();
        }

        /// <summary>The asset under a point (world coordinates) in the list or the grid, or -1.</summary>
        private int ItemAtPoint(Vector2 world)
        {
            if (!GridMode) return content.ItemAt(content.WorldToLocal(world));
            for (var element = grid.panel?.Pick(world); element != null && element != grid; element = element.parent)
                if (element.ClassListContains("mxp-tile")) return TileIndex(element);
            return -1;
        }

        private void RevealIndex(int index)
        {
            if (index < 0) return;
            if (!GridMode) { content.Reveal(index); return; }
            for (int l = 0; l < gridLines.Count; l++)
            {
                var line = gridLines[l];
                if (line.Heading < 0 && index >= line.First && index < line.First + line.Count) { grid.Reveal(l); return; }
            }
        }

        // Up and Down in the grid: the tile of the same column on the line above or below (headings skipped).
        private void StepLines(int current, int direction, bool extend)
        {
            int at = -1, column = 0;
            for (int l = 0; l < gridLines.Count && current >= 0; l++)
            {
                var line = gridLines[l];
                if (line.Heading < 0 && current >= line.First && current < line.First + line.Count) { at = l; column = current - line.First; break; }
            }
            if (at < 0) { Step(-1, 1, extend); return; }
            for (int l = at + direction; l >= 0 && l < gridLines.Count; l += direction)
            {
                var line = gridLines[l];
                if (line.Heading >= 0) continue;
                int index = line.First + Mathf.Min(column, line.Count - 1);
                Pick(index, extend && IndexOf(anchor) >= 0, false);
                RevealIndex(index);
                return;
            }
        }
    }
}
