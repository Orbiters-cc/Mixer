using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class LoggerWindow
    {
        private readonly Button[] levelPills = new Button[3];
        private readonly Label[] levelCounts = new Label[3];
        private readonly int[] shownCounts = { -1, -1, -1 };
        private SearchBox searchBox;
        private Button sourcesButton;
        private Label sourcesBadge;
        private Button consoleToggle;
        private VisualElement consoleSwitch;
        private SegmentedControl viewToggle;
        private VisualElement statusArea;
        private Label statusText;
        private TimelineView timeline;
        private Label footerLeft;
        private Label footerRight;
        private VisualElement footerProgress;
        private VisualElement footerProgressFill;

        private VisualElement BuildTopBar()
        {
            var bar = LoggerUi.Box("lg-topbar");

            var levels = LoggerUi.Box("lg-levels");
            levels.Add(LevelPill(LogLevel.Error, "Errors, exceptions and failed assertions"));
            levels.Add(LevelPill(LogLevel.Warning, "Warnings"));
            levels.Add(LevelPill(LogLevel.Info, "Logs"));
            bar.Add(levels);

            searchBox = new SearchBox(search, matchCase, useRegex, searchStack);
            searchBox.TextChanged += text =>
            {
                search = text;
                ApplyQuery(delay: QueryDelay());
            };
            searchBox.OptionsChanged += () => ApplyQuery(delay: 0d);
            searchBox.Submitted += () => ApplyQuery(delay: 0d);
            searchBox.Escaped += () => list?.Focus();
            searchBox.DownPressed += () =>
            {
                list.Focus();
                if (cursor < 0 && ItemCount > 0)
                {
                    MoveCursorTo(ItemCount - 1, false);
                }
            };
            bar.Add(searchBox);

            sourcesButton = new Button { tooltip = "Show or hide logs by where they come from" };
            sourcesButton.AddToClassList("lg-tool-button");
            sourcesButton.Add(new LoggerIcon(LoggerGlyph.Filter));
            sourcesButton.Add(LoggerUi.Text("Sources", "lg-tool-button__label"));
            sourcesBadge = LoggerUi.Text(string.Empty, "lg-badge");
            sourcesBadge.pickingMode = PickingMode.Ignore;
            sourcesButton.Add(sourcesBadge);
            LoggerUi.Press(sourcesButton, () => SourcesPopup.Show(sourcesButton.worldBound, this));
            bar.Add(sourcesButton);

            viewToggle = new SegmentedControl(index => SetGrouped(index == 1));
            viewToggle.AddOption("List", LoggerGlyph.List, "Every log in order");
            viewToggle.AddOption("Groups", LoggerGlyph.Groups, "Logs with the same text as one row, with how often they happen");
            viewToggle.Select(grouped ? 1 : 0);
            bar.Add(viewToggle);

            bar.Add(LoggerUi.Spacer());

            statusArea = LoggerUi.Box("lg-status", PickingMode.Ignore);
            statusArea.Add(LoggerUi.Spinner());
            statusText = LoggerUi.Text(string.Empty, "lg-status__text");
            statusArea.Add(statusText);
            bar.Add(statusArea);

            var help = new Button { tooltip = "Get help: a project report with your logs, Git history and scene, and an AI diagnosis" };
            help.AddToClassList("lg-tool-button");
            help.AddToClassList("lg-help-button");
            help.Add(new LoggerIcon(LoggerGlyph.Lifebuoy));
            help.Add(LoggerUi.Text("Get help", "lg-tool-button__label"));
            LoggerUi.Press(help, ReportWindow.Open);
            bar.Add(help);

            consoleToggle = new Button();
            consoleToggle.AddToClassList("lg-tool-button");
            consoleToggle.AddToClassList("lg-console-toggle");
            consoleSwitch = LoggerUi.Box("lg-switch", PickingMode.Ignore);
            consoleSwitch.AddToClassList("lg-switch--mini");
            consoleSwitch.Add(LoggerUi.Box("lg-switch__knob", PickingMode.Ignore));
            consoleToggle.Add(consoleSwitch);
            consoleToggle.Add(LoggerUi.Text("Replace console", "lg-tool-button__label"));
            LoggerUi.Press(consoleToggle, ToggleConsoleReplacement);
            UpdateConsoleToggle(ConsoleReplacement.Enabled);
            bar.Add(consoleToggle);

            var actions = LoggerUi.Box("lg-topbar__actions");
            actions.Add(LoggerUi.Icon(LoggerGlyph.Trash, "Clear (Ctrl+L). Also clears Unity's console; compile errors stay.", ClearLogs));
            actions.Add(LoggerUi.Icon(LoggerGlyph.Sliders, "Settings", () => SettingsPopup.Show(actions.worldBound, this)));
            bar.Add(actions);
            return bar;
        }

        // The switch flips at once; closing the open Consoles (turning it on) waits for the next tick.
        private void ToggleConsoleReplacement()
        {
            bool on = !ConsoleReplacement.Enabled;
            UpdateConsoleToggle(on);
            ConsoleReplacement.SetEnabled(on);
            if (on)
            {
                consoleToggle.schedule.Execute(ConsoleReplacement.ReplaceOpenConsoles);
            }

            ShowToast(on ? "The Mixer console replaces Unity's Console" : "Unity's Console opens as usual again");
        }

        private void UpdateConsoleToggle(bool on)
        {
            consoleSwitch.EnableInClassList("lg-switch--on", on);
            consoleToggle.tooltip = on
                ? "Replace console: on\nThe Mixer console takes the place of Unity's Console: when Unity starts, a Console tab becomes a Mixer console tab, " +
                  "and Window › General › Console (Ctrl+Shift+C) or a click on the status bar opens the Mixer console.\nClick to keep Unity's Console."
                : "Replace console: off\nUnity's Console opens as usual.\nClick to let the Mixer console take its place (Consoles open now are closed).";
        }

        private Button LevelPill(LogLevel level, string tooltip)
        {
            var pill = new Button { tooltip = tooltip + "\nClick to show or hide; Alt+click to show only these." };
            pill.AddToClassList("lg-level");
            pill.AddToClassList("lg-level--" + level.ToString().ToLowerInvariant());
            pill.Add(new LoggerIcon(LoggerIcon.ForLevel(level)));
            var count = LoggerUi.Text("0", "lg-level__count");
            count.pickingMode = PickingMode.Ignore;
            pill.Add(count);
            levelPills[(int)level] = pill;
            levelCounts[(int)level] = count;
            pill.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button == 0 && evt.altKey)
                {
                    pill.AddToClassList(LoggerUi.PressedClass);
                    levelMask = 1 << (int)level;
                    OnLevelsChanged();
                    evt.StopImmediatePropagation();
                }
            }, TrickleDown.TrickleDown);
            LoggerUi.Press(pill, () =>
            {
                levelMask ^= 1 << (int)level;
                if (levelMask == 0)
                {
                    levelMask = 7;
                }

                OnLevelsChanged();
            });
            return pill;
        }

        private void OnLevelsChanged()
        {
            UpdateLevelPills();
            QueueRefilter();
        }

        private void UpdateLevelPills()
        {
            for (int i = 0; i < 3; i++)
            {
                levelPills[i]?.EnableInClassList("lg-level--off", (levelMask & (1 << i)) == 0);
            }
        }

        /// <summary>Puts <paramref name="text"/> in the search field and filters by it at once.</summary>
        private void SetSearch(string text)
        {
            searchBox.Value = text;
            search = text;
            ApplyQuery(0d);
            searchBox.Sync();
        }

        // Typing waits a little more when each keystroke means a full pass over a big store.
        private double QueryDelay() => Store != null && Store.Count > 200_000 ? 0.18d : 0.06d;

        private void ApplyQuery(double delay)
        {
            useRegex = searchBox.Regex;
            matchCase = searchBox.MatchCase;
            searchStack = searchBox.Stack;
            var next = new LogQuery(search, useRegex, matchCase, searchStack);
            searchBox.SetError(next.Error);
            searchBox.Sync();
            // An unfinished regular expression keeps the last results instead of flashing an empty list.
            if (next.Error != null)
            {
                return;
            }

            if (next.SameAs(query))
            {
                return;
            }

            query = next;
            QueueRefilter(delay);
        }

        private void SetGrouped(bool value)
        {
            if (grouped == value)
            {
                return;
            }

            // Keep looking at the same log: its group, or the group's newest log.
            int focusKey = -1;
            if (cursor >= 0 && state != null)
            {
                focusKey = value ? Store.Message(Store.MessageAt(cursor)).Group : state.GroupLast[cursor];
            }

            grouped = value;
            viewToggle.Select(value ? 1 : 0);
            ClearSelection();
            list.SetItemCount(ItemCount, keepAtEnd: false);
            if (focusKey >= 0 && RowOf(focusKey) >= 0)
            {
                Select(focusKey, extend: false, toggle: false);
                list.Center(RowOf(focusKey));
            }
            else
            {
                list.ScrollToEnd();
            }

            list.RefreshRows();
            listPane.EnableInClassList("lg-list-pane--grouped", grouped);
            chromeDirty = true;
            detailsDirty = true;
            if (state != null && state.Spec.Sort != sort)
            {
                QueueRefilter();
            }
        }

        internal void SetSort(GroupSort value)
        {
            if (sort == value)
            {
                return;
            }

            sort = value;
            QueueRefilter();
            chromeDirty = true;
        }

        private VisualElement BuildTimeline()
        {
            timeline = new TimelineView();
            timeline.RangeSelected += SetRange;
            timeline.TimeClicked += JumpToTime;
            timeline.TimingClicked += SelectTimingLog;
            timeline.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                if (Math.Abs(evt.oldRect.width - evt.newRect.width) > 24f)
                {
                    QueueRefilter(0.15d);
                }
            });
            return timeline;
        }

        private void SetRange(long from, long to)
        {
            if (to <= from)
            {
                from = 0;
                to = 0;
            }

            rangeFrom = from;
            rangeTo = to;
            QueueRefilter();
        }

        private void JumpToTime(long time)
        {
            if (state == null || ItemCount == 0)
            {
                return;
            }

            int row;
            if (grouped)
            {
                // The group with a log closest after that moment.
                int best = -1;
                long bestGap = long.MaxValue;
                for (int i = 0; i < state.GroupRowCount; i++)
                {
                    int group = state.GroupRows[i];
                    long last = Store.TimeAt(state.GroupLast[group]);
                    long gap = Math.Abs(last - time);
                    if (gap < bestGap)
                    {
                        bestGap = gap;
                        best = i;
                    }
                }

                row = best;
            }
            else
            {
                int occurrence = Store.LowerBound(time);
                row = Math.Min(state.RowAtOrAfter(occurrence), state.RowCount - 1);
            }

            if (row >= 0)
            {
                list.Center(row);
                SelectRow(row, extend: false, toggle: false);
                list.Focus();
            }
        }

        private VisualElement BuildFooter()
        {
            var footer = LoggerUi.Box("lg-footer");
            footerLeft = LoggerUi.Text(string.Empty, "lg-footer__text");
            footer.Add(footerLeft);
            footer.Add(LoggerUi.Spacer());
            footerProgress = LoggerUi.Box("lg-progress", PickingMode.Ignore);
            footerProgressFill = LoggerUi.Box("lg-progress__fill", PickingMode.Ignore);
            footerProgress.Add(footerProgressFill);
            footer.Add(footerProgress);
            footerRight = LoggerUi.Text(string.Empty, "lg-footer__text");
            footerRight.AddToClassList("lg-footer__text--right");
            footer.Add(footerRight);
            return footer;
        }

        /// <summary>Counts, badges, status and footer, refreshed at most ten times a second.</summary>
        private void UpdateChrome(bool force)
        {
            if (!built)
            {
                return;
            }

            lastChrome = EditorApplication.timeSinceStartup;
            chromeDirty = false;
            var store = Store;
            UpdateLevelPills();
            for (int i = 0; i < 3; i++)
            {
                int value = state != null ? state.LevelCounts[i] : 0;
                if (value != shownCounts[i] || force)
                {
                    bool grew = shownCounts[i] >= 0 && value > shownCounts[i];
                    shownCounts[i] = value;
                    levelCounts[i].text = LoggerUi.Compact(value);
                    levelPills[i].tooltip = LevelTooltip((LogLevel)i, value);
                    levelPills[i].EnableInClassList("lg-level--empty", value == 0);
                    if (grew && i == (int)LogLevel.Error)
                    {
                        LoggerUi.Enter(levelPills[i], "lg-level--pulse", 180);
                    }
                }
            }

            sourcesBadge.text = hiddenSources.Count.ToString();
            sourcesBadge.style.display = hiddenSources.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            sourcesButton.EnableInClassList("lg-tool-button--on", hiddenSources.Count > 0);
            UpdateConsoleToggle(ConsoleReplacement.Enabled);

            var history = LogCapture.History;
            string status = history != null
                ? "Reading Unity's console · " + LoggerUi.Compact(history.Done) + " / " + LoggerUi.Compact(history.Total)
                : runner.Busy ? "Filtering…" : string.Empty;
            statusText.text = status;
            statusArea.EnableInClassList("lg-status--idle", string.IsNullOrEmpty(status));
            LoggerUi.Show(footerProgress, history != null);
            if (history != null)
            {
                footerProgressFill.style.width = Length.Percent(Mathf.Clamp01(history.Progress) * 100f);
            }

            timeline.Set(state, store?.Events, TimelineMarkers.All, timings.Entries, rangeFrom, rangeTo);
            UpdateListChrome();

            if (store == null || state == null)
            {
                footerLeft.text = string.Empty;
                footerRight.text = string.Empty;
                return;
            }

            string left = LoggerUi.Plural(store.Count, "log");
            if (grouped)
            {
                left += "  ·  " + LoggerUi.Plural(state.GroupRowCount, "group") + " shown";
            }
            else if (state.RowCount != store.Count)
            {
                left += "  ·  " + LoggerUi.Count(state.RowCount) + " shown";
            }

            int selected = SelectedCount;
            if (selected > 0)
            {
                left += "  ·  " + LoggerUi.Count(selected) + " selected";
            }

            if (LogCapture.Dropped > 0)
            {
                left += "  ·  " + LoggerUi.Count(LogCapture.Dropped) + " dropped (too fast)";
            }

            footerLeft.text = left;
            footerRight.text = state.Milliseconds > 0 ? "filtered in " + state.Milliseconds.ToString(state.Milliseconds < 10 ? "0.0" : "0") + " ms" : string.Empty;
        }

        private static string LevelTooltip(LogLevel level, int value) =>
            LoggerUi.Plural(value, LevelNoun(level)) + " match the other filters.\nClick to show or hide; Alt+click to show only these.";

        private static string LevelNoun(LogLevel level) => level == LogLevel.Error ? "error" : level == LogLevel.Warning ? "warning" : "log";

        private void ClearLogs()
        {
            int count = Store?.Count ?? 0;
            LogCapture.Clear();
            ClearSelection();
            rangeFrom = rangeTo = 0;
            Refilter(allowAsync: false);
            ShowToast(count > 0 ? "Cleared " + LoggerUi.Plural(count, "log") : "Nothing to clear");
        }

        // ---- Shared with the popups -----------------------------------------------------------------------------

        internal FilterState CurrentState => state;
        internal IReadOnlyList<string> HiddenSources => hiddenSources;

        internal void SetSourceHidden(string key, bool hidden)
        {
            bool has = hiddenSources.Contains(key);
            if (hidden && !has)
            {
                hiddenSources.Add(key);
            }
            else if (!hidden && has)
            {
                hiddenSources.Remove(key);
            }
            else
            {
                return;
            }

            QueueRefilter();
            chromeDirty = true;
        }

        internal void SetOnlySource(string key)
        {
            hiddenSources.Clear();
            foreach (var source in Store.Sources.All)
            {
                if (!string.Equals(source.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    hiddenSources.Add(source.Key);
                }
            }

            QueueRefilter();
            chromeDirty = true;
        }

        internal void ShowAllSources()
        {
            if (hiddenSources.Count == 0)
            {
                return;
            }

            hiddenSources.Clear();
            QueueRefilter();
            chromeDirty = true;
        }

        internal IReadOnlyList<string> Muted => muted;

        internal void Unmute(string key)
        {
            if (muted.Remove(key))
            {
                QueueRefilter();
                chromeDirty = true;
            }
        }

        internal void UnmuteAll()
        {
            if (muted.Count == 0)
            {
                return;
            }

            muted.Clear();
            QueueRefilter();
            chromeDirty = true;
        }
    }
}
