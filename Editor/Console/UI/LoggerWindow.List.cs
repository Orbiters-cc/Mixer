using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class LoggerWindow
    {
        private const string MarkColor = "#00DA6D59";
        private const int MaxCopiedLogs = 100_000;
        private const int MaxCopiedChars = 32 * 1024 * 1024;

        private readonly HashSet<int> selection = new HashSet<int>();
        private readonly List<(int start, int length)> ranges = new List<(int start, int length)>();
        private bool selectAll;
        private int cursor = -1;
        private int anchor = -1;
        private VisualElement listPane;
        private VirtualList list;
        private Label listSummary;
        private VisualElement rangeChip;
        private Button sortButton;
        private Button mutedChip;
        private Button newLogsPill;
        private VisualElement emptyState;
        private Label emptyTitle;
        private Label emptyBody;
        private VisualElement emptyActions;

        private sealed class RowParts
        {
            public VisualElement Edge;
            public LoggerIcon Icon;
            public Label Time;
            public SourceChip Source;
            public Label Message;
            public Label Sub;
            public LoggerIcon Bulb;
            public LoggerIcon Context;
            public Sparkline Spark;
            public Label Count;
            public TimingStrip Strip;
        }

        private VisualElement BuildListPane()
        {
            listPane = LoggerUi.Box("lg-list-pane");
            listPane.EnableInClassList("lg-list-pane--grouped", grouped);

            var header = LoggerUi.Box("lg-list-header");
            listSummary = LoggerUi.Text(string.Empty, "lg-list-header__summary");
            header.Add(listSummary);
            rangeChip = LoggerUi.Box("lg-range");
            rangeChip.Add(new LoggerIcon(LoggerGlyph.Clock));
            rangeChip.Add(LoggerUi.Text(string.Empty, "lg-range__label"));
            rangeChip.Add(LoggerUi.Icon(LoggerGlyph.Close, "Show the whole session (Escape, or double-click the chart)", () => SetRange(0, 0), "lg-range__clear"));
            rangeChip.style.display = DisplayStyle.None;
            header.Add(rangeChip);
            header.Add(LoggerUi.Spacer());
            mutedChip = new Button { tooltip = "Messages you hid. Click to show them again." };
            mutedChip.AddToClassList("lg-chip-button");
            mutedChip.Add(new LoggerIcon(LoggerGlyph.EyeOff));
            mutedChip.Add(LoggerUi.Text(string.Empty, "lg-chip-button__label"));
            LoggerUi.Press(mutedChip, () => MutedPopup.Show(mutedChip.worldBound, this));
            header.Add(mutedChip);
            sortButton = new Button { tooltip = "Order of the groups" };
            sortButton.AddToClassList("lg-chip-button");
            sortButton.Add(LoggerUi.Text(string.Empty, "lg-chip-button__label"));
            var chevron = new LoggerIcon(LoggerGlyph.Chevron);
            chevron.AddToClassList("lg-chip-button__chevron");
            sortButton.Add(chevron);
            LoggerUi.Press(sortButton, ShowSortMenu);
            header.Add(sortButton);
            header.Add(LoggerUi.Icon(LoggerGlyph.ArrowDown, "Jump to the newest log (End)", () =>
            {
                list.ScrollToEnd();
                newRows = 0;
                chromeDirty = true;
            }, "lg-list-header__end"));
            listPane.Add(header);

            list = new VirtualList(RowHeight, MakeRow, BindRow);
            list.AddToClassList("lg-list");
            list.Scrolled += () =>
            {
                if (list.AtEnd && newRows > 0)
                {
                    newRows = 0;
                    chromeDirty = true;
                }
            };
            list.Viewport.RegisterCallback<PointerDownEvent>(OnListPointerDown);
            list.Viewport.AddManipulator(new ContextualMenuManipulator(BuildContextMenu));
            listPane.Add(list);

            newLogsPill = new Button();
            newLogsPill.AddToClassList("lg-new-logs");
            newLogsPill.Add(new LoggerIcon(LoggerGlyph.ArrowDown));
            newLogsPill.Add(LoggerUi.Text(string.Empty, "lg-new-logs__label"));
            LoggerUi.Press(newLogsPill, () =>
            {
                list.ScrollToEnd();
                newRows = 0;
                chromeDirty = true;
            });
            newLogsPill.style.display = DisplayStyle.None;
            listPane.Add(newLogsPill);

            emptyState = LoggerUi.Box("lg-empty", PickingMode.Ignore);
            var emptyMark = LoggerUi.Box("lg-empty__mark", PickingMode.Ignore);
            emptyMark.Add(new LoggerIcon(LoggerGlyph.Logo));
            emptyState.Add(emptyMark);
            emptyTitle = LoggerUi.Text(string.Empty, "lg-empty__title");
            emptyBody = LoggerUi.Text(string.Empty, "lg-empty__body");
            emptyActions = LoggerUi.Box("lg-empty__actions");
            emptyState.Add(emptyTitle);
            emptyState.Add(emptyBody);
            emptyState.Add(emptyActions);
            listPane.Add(emptyState);
            return listPane;
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("lg-row");
            var parts = new RowParts
            {
                Edge = LoggerUi.Box("lg-row__edge", PickingMode.Ignore),
                Icon = new LoggerIcon(LoggerGlyph.Info),
                Time = LoggerUi.Text(string.Empty, "lg-row__time"),
                Source = new SourceChip("lg-row__source"),
                Message = LoggerUi.Text(string.Empty, "lg-row__message"),
                Sub = LoggerUi.Text(string.Empty, "lg-row__sub"),
                Bulb = new LoggerIcon(LoggerGlyph.Bulb),
                Context = new LoggerIcon(LoggerGlyph.Target),
                Spark = new Sparkline(),
                Count = LoggerUi.Text(string.Empty, "lg-row__count"),
                Strip = new TimingStrip(false, "lg-row__timing")
            };
            parts.Icon.AddToClassList("lg-row__icon");
            parts.Bulb.AddToClassList("lg-row__bulb");
            parts.Context.AddToClassList("lg-row__context");
            parts.Message.enableRichText = true;
            parts.Sub.enableRichText = false;
            parts.Time.pickingMode = PickingMode.Ignore;
            parts.Message.pickingMode = PickingMode.Ignore;
            parts.Sub.pickingMode = PickingMode.Ignore;
            parts.Count.pickingMode = PickingMode.Ignore;

            row.Add(parts.Edge);
            row.Add(parts.Icon);
            row.Add(parts.Time);
            var main = LoggerUi.Box("lg-row__main", PickingMode.Ignore);
            var top = LoggerUi.Box("lg-row__top", PickingMode.Ignore);
            top.Add(parts.Source);
            top.Add(parts.Message);
            main.Add(top);
            main.Add(parts.Sub);
            main.Add(parts.Strip);
            row.Add(main);
            var trail = LoggerUi.Box("lg-row__trail", PickingMode.Ignore);
            trail.Add(parts.Context);
            trail.Add(parts.Bulb);
            trail.Add(parts.Spark);
            trail.Add(parts.Count);
            row.Add(trail);
            row.userData = parts;
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            var parts = (RowParts)row.userData;
            var store = Store;
            if (state == null || store == null || index < 0 || index >= ItemCount)
            {
                return;
            }

            int key = grouped ? state.GroupRows[index] : state.Rows[index];
            int occurrence = grouped ? state.GroupLast[key] : key;
            if (occurrence < 0 || occurrence >= store.Count)
            {
                return;
            }

            int messageId = store.MessageAt(occurrence);
            ref var message = ref store.Message(messageId);
            var level = message.Level;
            var source = store.Sources[message.Source];

            row.EnableInClassList("lg-row--error", level == LogLevel.Error);
            row.EnableInClassList("lg-row--warning", level == LogLevel.Warning);
            row.EnableInClassList("lg-row--selected", IsSelected(key));
            row.EnableInClassList("lg-row--cursor", key == cursor);
            row.EnableInClassList("lg-row--odd", (index & 1) == 1);
            bool timing = timings.TryGet(messageId, out var profile);
            row.EnableInClassList("lg-row--timing", timing);
            parts.Icon.Glyph = timing ? LoggerGlyph.Stopwatch : LoggerIcon.ForLevel(level);

            var flags = store.FlagsAt(occurrence);
            bool approximate = (flags & OccurrenceFlags.ApproximateTime) != 0;
            parts.Time.text = LoggerUi.Time(store.TimeAt(occurrence), !approximate);

            parts.Source.Set(source);

            parts.Message.text = DisplayLine(store, ref message);
            string sub = message.Callsite > 0 ? store.Texts[message.Callsite] : RichText.SecondLine(store.Texts.Plain(message.Text), 200);
            if ((flags & OccurrenceFlags.BackgroundThread) != 0)
            {
                sub = string.IsNullOrEmpty(sub) ? "background thread" : sub + "   ·   background thread";
            }

            parts.Sub.text = sub;
            LoggerUi.Show(parts.Sub, !timing);
            LoggerUi.Show(parts.Strip, timing);
            if (timing)
            {
                parts.Strip.Set(profile, (float)(profile.TotalMilliseconds / timings.Slowest(profile.Kind)));
            }

            int explanation = ExplanationOf(messageId);
            LoggerUi.Show(parts.Bulb, explanation >= 0);
            if (explanation >= 0)
            {
                var entry = LogExplanations.Get(explanation);
                parts.Bulb.tooltip = entry != null ? entry.Title : null;
                parts.Bulb.EnableInClassList("lg-row__bulb--harmless", entry != null && entry.Severity <= LogExplanationSeverity.Info);
            }

            LoggerUi.Show(parts.Context, !grouped && store.TryGetContext(occurrence, out _));
            // Rows are recycled between modes: a sparkline shown in Groups must not stay on a List row.
            LoggerUi.Show(parts.Spark, false);
            parts.Count.EnableInClassList("lg-row__count--timing", timing && !grouped);
            if (timing && !grouped)
            {
                parts.Count.text = TimingProfile.FormatDuration(profile.TotalMilliseconds);
                parts.Count.tooltip = TimingProfile.KindLabel(profile.Kind);
                parts.Count.EnableInClassList("lg-row__count--many", false);
            }
            else if (!grouped)
            {
                parts.Count.text = string.Empty;
                parts.Count.tooltip = null;
            }

            if (grouped)
            {
                int count = state.GroupCount[key];
                parts.Count.text = "×" + LoggerUi.Compact(count);
                parts.Count.tooltip = LoggerUi.Plural(count, "time") + "\nfirst " + LoggerUi.Moment(store.TimeAt(state.GroupFirst[key])) +
                                      "\nlast " + LoggerUi.Moment(store.TimeAt(state.GroupLast[key]));
                parts.Count.EnableInClassList("lg-row__count--many", count >= 100);
                bool spark = state.Sparks != null && key < state.SparkGroups;
                LoggerUi.Show(parts.Spark, spark);
                if (spark)
                {
                    parts.Spark.Set(state.Sparks, key * FilterState.SparkBuckets, ChartColors.Level(level));
                }
            }
        }

        // The first line of the message: tags rendered as Unity does, or as plain text with the search matches marked.
        private string DisplayLine(LogStore store, ref LogMessage message)
        {
            if (query.IsEmpty)
            {
                return RichText.FirstLine(store.Texts[message.Text], 320);
            }

            string plain = RichText.FirstLine(store.Texts.Plain(message.Text), 320);
            query.FindMatches(plain, ranges);
            return RichText.Highlight(plain, ranges, MarkColor);
        }

        private int ExplanationOf(int messageId)
        {
            var store = Store;
            ref var message = ref store.Message(messageId);
            if (message.Explanation == -2 || explanationVersion != LogExplanations.Version)
            {
                if (explanationVersion != LogExplanations.Version)
                {
                    explanationVersion = LogExplanations.Version;
                    for (int i = 0; i < store.MessageCount; i++)
                    {
                        store.Message(i).Explanation = -2;
                    }
                }

                message.Explanation = LogExplanations.Match(store.Texts.Plain(message.Text), store.Texts[message.Stack], message.Level);
            }

            return message.Explanation;
        }

        private int explanationVersion = -1;

        private void UpdateListChrome()
        {
            int count = ItemCount;
            var store = Store;
            if (grouped)
            {
                listSummary.text = state == null ? string.Empty : LoggerUi.Plural(count, "group") + "  ·  " + LoggerUi.Plural(state.RowCount, "log");
            }
            else
            {
                listSummary.text = state == null ? string.Empty : LoggerUi.Plural(count, "log");
            }

            bool ranged = rangeTo > rangeFrom && rangeTo > 0;
            rangeChip.style.display = ranged ? DisplayStyle.Flex : DisplayStyle.None;
            if (ranged)
            {
                ((Label)rangeChip[1]).text = LoggerUi.Time(rangeFrom, false) + " – " + LoggerUi.Time(rangeTo, false) + "  ·  " +
                                             LoggerUi.Duration(TimeSpan.FromTicks(rangeTo - rangeFrom)).Trim();
            }

            sortButton.style.display = grouped ? DisplayStyle.Flex : DisplayStyle.None;
            ((Label)sortButton[0]).text = SortLabel(sort);
            mutedChip.style.display = muted.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            ((Label)mutedChip[1]).text = LoggerUi.Count(muted.Count) + " hidden";

            bool showNew = newRows > 0 && !list.AtEnd;
            newLogsPill.style.display = showNew ? DisplayStyle.Flex : DisplayStyle.None;
            if (showNew)
            {
                ((Label)newLogsPill[1]).text = LoggerUi.Compact(newRows) + " new";
            }

            bool empty = count == 0 && state != null;
            emptyState.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            if (!empty)
            {
                return;
            }

            emptyActions.Clear();
            if (store == null || store.Count == 0)
            {
                emptyTitle.text = LogCapture.History != null ? "Reading Unity's console…" : "All quiet";
                emptyBody.text = LogCapture.History != null
                    ? "Logs from before the Mixer console started appear in a moment."
                    : "Logs, warnings and errors from Unity and your tools appear here as they happen.";
                return;
            }

            emptyTitle.text = "Nothing matches";
            var reasons = new List<string>();
            if (!query.IsEmpty)
            {
                reasons.Add("the search");
                emptyActions.Add(LoggerUi.Pill("Clear search", () => SetSearch(string.Empty), "ghost", LoggerGlyph.Close));
            }

            if (levelMask != 7)
            {
                reasons.Add("hidden levels");
                emptyActions.Add(LoggerUi.Pill("Show all levels", () =>
                {
                    levelMask = 7;
                    OnLevelsChanged();
                }, "ghost", LoggerGlyph.Eye));
            }

            if (hiddenSources.Count > 0)
            {
                reasons.Add("hidden sources");
                emptyActions.Add(LoggerUi.Pill("Show all sources", ShowAllSources, "ghost", LoggerGlyph.Filter));
            }

            if (rangeTo > 0)
            {
                reasons.Add("the time range");
                emptyActions.Add(LoggerUi.Pill("Whole session", () => SetRange(0, 0), "ghost", LoggerGlyph.Clock));
            }

            emptyBody.text = reasons.Count > 0
                ? LoggerUi.Plural(store.Count, "log") + " hidden by " + string.Join(", ", reasons) + "."
                : LoggerUi.Plural(store.Count, "log") + " hidden.";
        }

        private static string SortLabel(GroupSort value)
        {
            switch (value)
            {
                case GroupSort.LastSeen:
                    return "Last seen";
                case GroupSort.Count:
                    return "Most frequent";
                case GroupSort.Level:
                    return "Errors first";
                default:
                    return "First seen";
            }
        }

        private void ShowSortMenu()
        {
            var menu = new GenericMenu();
            foreach (GroupSort value in Enum.GetValues(typeof(GroupSort)))
            {
                var captured = value;
                menu.AddItem(new GUIContent(SortLabel(value)), sort == value, () => SetSort(captured));
            }

            // Opened a frame later, once the press that asked for it is over.
            var bounds = sortButton.worldBound;
            sortButton.schedule.Execute(() => menu.DropDown(bounds));
        }

        // ---- Selection ------------------------------------------------------------------------------------------

        private bool HasSelection => selectAll || selection.Count > 0;

        private int SelectedCount => selectAll ? ItemCount : selection.Count;

        private bool IsSelected(int key) => selectAll || selection.Contains(key);

        private int KeyAt(int row) => grouped ? state.GroupRows[row] : state.Rows[row];

        private int RowOf(int key)
        {
            if (state == null || key < 0)
            {
                return -1;
            }

            return grouped ? state.GroupRowOf(key) : state.RowOf(key);
        }

        /// <summary>
        /// Turns off only the filters <paramref name="hidden"/> names for <paramref name="occurrence"/> (its level, its
        /// source, its hidden message, the search, the time range) so it can show; what changed, for a toast.
        /// </summary>
        private List<string> RevealOccurrence(LogStore store, int occurrence, HiddenBy hidden)
        {
            var changes = new List<string>();
            int id = store.MessageAt(occurrence);
            var message = store.Message(id);
            if ((hidden & HiddenBy.Level) != 0)
            {
                levelMask |= 1 << (int)message.Level;
                UpdateLevelPills();
                changes.Add(LevelNoun(message.Level) + "s turned on");
            }

            if ((hidden & HiddenBy.Source) != 0)
            {
                var source = store.SourceOf(id);
                hiddenSources.RemoveAll(key => string.Equals(key, source.Key, StringComparison.OrdinalIgnoreCase));
                changes.Add(source.Name + " turned on");
            }

            if ((hidden & HiddenBy.Muted) != 0)
            {
                muted.Remove(store.Texts[store.Group(message.Group).Key]);
                changes.Add("its message unhidden");
            }

            if ((hidden & HiddenBy.Search) != 0)
            {
                SetSearch(string.Empty);
                changes.Add("search cleared");
            }

            if ((hidden & HiddenBy.Range) != 0)
            {
                rangeFrom = rangeTo = 0;
                changes.Add("whole session shown");
            }

            if (changes.Count > 0)
            {
                QueueRefilter();
                chromeDirty = true;
            }

            return changes;
        }

        private void ClearSelection()
        {
            bool had = HasSelection || cursor >= 0;
            selection.Clear();
            selectAll = false;
            cursor = -1;
            anchor = -1;
            if (had)
            {
                list?.RefreshRows();
                detailsDirty = true;
                chromeDirty = true;
            }
        }

        private void ShiftSelection(int offset)
        {
            if (selection.Count == 0 && cursor < 0)
            {
                return;
            }

            var shifted = new List<int>(selection.Count);
            foreach (int key in selection)
            {
                if (key + offset >= 0)
                {
                    shifted.Add(key + offset);
                }
            }

            selection.Clear();
            foreach (int key in shifted)
            {
                selection.Add(key);
            }

            cursor = cursor >= 0 && cursor + offset >= 0 ? cursor + offset : -1;
            anchor = anchor >= 0 && anchor + offset >= 0 ? anchor + offset : -1;
        }

        private void SelectAll()
        {
            if (ItemCount == 0)
            {
                return;
            }

            selectAll = true;
            selection.Clear();
            list.RefreshRows();
            detailsDirty = true;
            chromeDirty = true;
        }

        private void SelectRow(int row, bool extend, bool toggle)
        {
            if (state == null || row < 0 || row >= ItemCount)
            {
                return;
            }

            Select(KeyAt(row), extend, toggle);
        }

        private void Select(int key, bool extend, bool toggle)
        {
            if (selectAll)
            {
                // Materialise "everything" before changing part of it (bounded: Ctrl+A then Ctrl+click on a huge list).
                selectAll = false;
                if (toggle && ItemCount <= 500_000)
                {
                    for (int row = 0; row < ItemCount; row++)
                    {
                        selection.Add(KeyAt(row));
                    }
                }
            }

            if (extend && anchor >= 0 && RowOf(anchor) >= 0)
            {
                int from = RowOf(anchor);
                int to = RowOf(key);
                if (to >= 0)
                {
                    if (!toggle)
                    {
                        selection.Clear();
                    }

                    int step = from <= to ? 1 : -1;
                    for (int row = from; ; row += step)
                    {
                        selection.Add(KeyAt(row));
                        if (row == to)
                        {
                            break;
                        }
                    }
                }
            }
            else if (toggle)
            {
                if (!selection.Remove(key))
                {
                    selection.Add(key);
                }

                anchor = key;
            }
            else
            {
                selection.Clear();
                selection.Add(key);
                anchor = key;
            }

            cursor = key;
            list.RefreshRows();
            detailsDirty = true;
            chromeDirty = true;
        }

        private void MoveCursor(int delta, bool extend)
        {
            if (ItemCount == 0)
            {
                return;
            }

            int row = cursor >= 0 ? RowOf(cursor) : -1;
            if (row < 0)
            {
                row = delta > 0 ? Math.Max(0, (int)Math.Floor(list.First)) - 1 : Math.Min(ItemCount, (int)Math.Ceiling(list.First + list.VisibleRows));
            }

            MoveCursorTo(Mathf.Clamp(row + delta, 0, ItemCount - 1), extend);
        }

        private void MoveCursorTo(int row, bool extend)
        {
            if (row < 0 || row >= ItemCount)
            {
                return;
            }

            if (extend && anchor < 0)
            {
                anchor = cursor >= 0 ? cursor : KeyAt(row);
            }

            SelectRow(row, extend, toggle: false);
            list.Reveal(row);
        }

        private void OnListPointerDown(PointerDownEvent evt)
        {
            var local = list.WorldToLocal(evt.position);
            int row = list.ItemAt(local);
            list.Focus();
            if (row < 0)
            {
                if (evt.button == 0 && !evt.ctrlKey && !evt.commandKey && !evt.shiftKey)
                {
                    ClearSelection();
                }

                return;
            }

            int key = KeyAt(row);
            if (evt.button == 1)
            {
                // Right-click keeps a multi-selection that contains the row.
                if (!IsSelected(key))
                {
                    Select(key, extend: false, toggle: false);
                }

                return;
            }

            if (evt.button != 0)
            {
                return;
            }

            bool toggle = evt.ctrlKey || evt.commandKey;
            Select(key, evt.shiftKey, toggle);
            if (evt.clickCount >= 2)
            {
                OpenKey(key);
            }
            else if (!grouped && !toggle && !evt.shiftKey && Store.TryGetContext(key, out int instanceId))
            {
                // Like Unity's console: a click shows the object the log was about.
                EditorGUIUtility.PingObject(instanceId);
            }
        }

        private bool SelectionTouchesNewData()
        {
            if (!grouped || cursor < 0)
            {
                return false;
            }

            return true;
        }

        // ---- Actions --------------------------------------------------------------------------------------------

        /// <summary>The selected occurrences (flat) or groups, in list order, at most <paramref name="max"/>.</summary>
        private List<int> SelectedKeysInOrder(int max)
        {
            var keys = new List<int>();
            if (state == null)
            {
                return keys;
            }

            if (selectAll)
            {
                for (int row = 0; row < ItemCount && keys.Count < max; row++)
                {
                    keys.Add(KeyAt(row));
                }

                return keys;
            }

            if (selection.Count == 0)
            {
                if (cursor >= 0)
                {
                    keys.Add(cursor);
                }

                return keys;
            }

            var rows = new List<int>(selection.Count);
            foreach (int key in selection)
            {
                int row = RowOf(key);
                if (row >= 0)
                {
                    rows.Add(row);
                }
            }

            rows.Sort();
            foreach (int row in rows)
            {
                if (keys.Count >= max)
                {
                    break;
                }

                keys.Add(KeyAt(row));
            }

            return keys;
        }

        private int MessageOfKey(int key)
        {
            var store = Store;
            if (grouped)
            {
                int occurrence = state.GroupLast[key];
                return occurrence >= 0 && occurrence < store.Count ? store.MessageAt(occurrence) : -1;
            }

            return key >= 0 && key < store.Count ? store.MessageAt(key) : -1;
        }

        private void Copy(bool withStack)
        {
            var store = Store;
            var keys = SelectedKeysInOrder(MaxCopiedLogs + 1);
            if (keys.Count == 0)
            {
                ShowToast("Select logs to copy");
                return;
            }

            bool truncated = keys.Count > MaxCopiedLogs;
            var builder = new StringBuilder();
            int copied = 0;
            foreach (int key in keys)
            {
                if (copied >= MaxCopiedLogs || builder.Length >= MaxCopiedChars)
                {
                    truncated = true;
                    break;
                }

                int messageId = MessageOfKey(key);
                if (messageId < 0)
                {
                    continue;
                }

                ref var message = ref store.Message(messageId);
                if (copied > 0)
                {
                    builder.Append(withStack ? "\n\n" : "\n");
                }

                builder.Append(store.Texts.Plain(message.Text));
                if (withStack && message.Stack > 0)
                {
                    builder.Append('\n').Append(store.Texts[message.Stack].TrimEnd('\n', '\r'));
                }

                copied++;
            }

            EditorGUIUtility.systemCopyBuffer = builder.ToString();
            string what = LoggerUi.Plural(copied, grouped ? "message" : "log");
            ShowToast("Copied " + what + (withStack ? " with stack traces" : string.Empty) + (truncated ? " (the first ones)" : string.Empty));
        }

        private void OpenCursor()
        {
            if (cursor >= 0)
            {
                OpenKey(cursor);
            }
        }

        private void OpenKey(int key)
        {
            int messageId = MessageOfKey(key);
            if (messageId < 0)
            {
                return;
            }

            var store = Store;
            ref var message = ref store.Message(messageId);
            if (!OpenFile(store.Texts[message.File], message.Line, message.Column))
            {
                ShowToast("No source file for this log", error: true);
            }
        }

        internal static bool OpenFile(string path, int line, int column = 0)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                if (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (asset != null && AssetDatabase.OpenAsset(asset, Math.Max(1, line), Math.Max(0, column)))
                    {
                        return true;
                    }
                }

                string full = Path.GetFullPath(path);
                return File.Exists(full) && InternalEditorUtility.OpenFileAtLineExternal(full, Math.Max(1, line), Math.Max(0, column));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void JumpToLevel(LogLevel level, int direction)
        {
            if (state == null || ItemCount == 0)
            {
                return;
            }

            var store = Store;
            int start = cursor >= 0 ? RowOf(cursor) : direction > 0 ? -1 : ItemCount;
            for (int row = start + direction; row >= 0 && row < ItemCount; row += direction)
            {
                int messageId = MessageOfKey(KeyAt(row));
                if (messageId >= 0 && store.Message(messageId).Level == level)
                {
                    SelectRow(row, extend: false, toggle: false);
                    list.Center(row);
                    return;
                }
            }

            ShowToast("No more " + (level == LogLevel.Error ? "errors" : "warnings") + (direction > 0 ? " below" : " above"));
        }

        private void HideSelected()
        {
            var store = Store;
            var keys = SelectedKeysInOrder(1000);
            int added = 0;
            foreach (int key in keys)
            {
                int messageId = MessageOfKey(key);
                if (messageId < 0)
                {
                    continue;
                }

                string groupKey = store.Texts[store.Group(store.Message(messageId).Group).Key];
                if (!muted.Contains(groupKey))
                {
                    muted.Add(groupKey);
                    added++;
                }
            }

            if (added == 0)
            {
                return;
            }

            ClearSelection();
            QueueRefilter();
            ShowToast("Hid " + LoggerUi.Plural(added, "message") + " (shown again from the list header)");
        }

        private void BuildContextMenu(ContextualMenuPopulateEvent evt)
        {
            var store = Store;
            int row = list.ItemAt(list.WorldToLocal(evt.mousePosition));
            if (row < 0 || state == null)
            {
                evt.menu.AppendAction("Select all", _ => SelectAll(), ItemCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                return;
            }

            int key = KeyAt(row);
            int messageId = MessageOfKey(key);
            if (messageId < 0)
            {
                return;
            }

            var message = store.Message(messageId);
            var source = store.Sources[message.Source];
            int count = Math.Max(1, SelectedCount);
            string suffix = count > 1 ? " (" + LoggerUi.Count(count) + ")" : string.Empty;
            evt.menu.AppendAction("Copy" + suffix + "\tCtrl+C", _ => Copy(false));
            evt.menu.AppendAction("Copy with stack trace" + suffix + "\tCtrl+Shift+C", _ => Copy(true));
            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Open source file\tEnter", _ => OpenKey(key), message.File > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            bool hasContext = !grouped && store.TryGetContext(key, out int contextId) && EditorUtility.InstanceIDToObject(contextId) != null;
            evt.menu.AppendAction("Select the object it's about", _ =>
            {
                if (store.TryGetContext(key, out int id))
                {
                    Selection.activeInstanceID = id;
                    EditorGUIUtility.PingObject(id);
                }
            }, hasContext ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Show only " + source.Name, _ => SetOnlySource(source.Key));
            evt.menu.AppendAction("Hide " + source.Name, _ => SetSourceHidden(source.Key, true));
            evt.menu.AppendAction("Hide messages like this" + suffix + "\tDelete", _ => HideSelected());
            evt.menu.AppendAction("Search for this text", _ => SetSearch(RichText.FirstLine(store.Texts.Plain(message.Text), 120)));
            evt.menu.AppendSeparator();
            evt.menu.AppendAction(grouped ? "Show each log" : "Group logs with the same text", _ => SetGrouped(!grouped));
        }
    }
}
