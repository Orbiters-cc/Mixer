using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class LoggerWindow
    {
        private const int MaxPartsShown = 12;

        // A timing log: the total and how it compares, each source's share (click for its parts), and the same kind of
        // timing over the session.
        private void ShowTiming(LogStore store, int messageId, int occurrence, TimingProfile profile, VisualElement main, VisualElement side)
        {
            detailsIcon.Glyph = LoggerGlyph.Stopwatch;
            detailsKind.text = TimingProfile.KindLabel(profile.Kind);
            detailsPane.EnableInClassList("lg-details--timing", true);
            detailsMeta.text = LoggerUi.Moment(store.TimeAt(occurrence)) + (string.IsNullOrEmpty(profile.Subject) ? string.Empty : "  ·  " + profile.Subject);

            var card = Card(main, null);
            card.AddToClassList("lg-timing");
            var head = LoggerUi.Box("lg-timing__head");
            var total = LoggerUi.Text(TimingProfile.FormatDuration(profile.TotalMilliseconds), "lg-timing__total");
            total.style.color = Color.Lerp(TimingColors.Kind(profile.Kind), Color.white, 0.25f);
            head.Add(total);
            var titles = LoggerUi.Box("lg-timing__titles");
            titles.Add(LoggerUi.Text(Headline(profile), "lg-timing__title"));
            string comparison = Comparison(profile, messageId, out int trend);
            if (comparison.Length > 0)
            {
                var pill = LoggerUi.Text(comparison, "lg-timing__trend");
                pill.EnableInClassList("lg-timing__trend--slower", trend > 0);
                pill.EnableInClassList("lg-timing__trend--faster", trend < 0);
                titles.Add(pill);
            }

            head.Add(titles);
            card.Add(head);
            if (!string.IsNullOrEmpty(profile.Outcome))
            {
                card.Add(LoggerUi.Text(profile.Outcome, "lg-timing__outcome"));
            }

            var strip = new TimingStrip(true, "lg-timing__strip") { ShowLegend = false };
            strip.Set(profile, 1f);
            card.Add(strip);

            var shares = LoggerUi.Box("lg-shares");
            double largest = 1d;
            foreach (var share in profile.Shares)
            {
                largest = Math.Max(largest, share.Milliseconds);
            }

            var ordered = new List<TimingShare>();
            TimingShare engine = null;
            foreach (var share in profile.Shares)
            {
                if (share.Source == TimingProfile.UnitySource)
                {
                    engine = share;
                }
                else
                {
                    ordered.Add(share);
                }
            }

            if (engine != null)
            {
                ordered.Add(engine);
            }

            foreach (var share in ordered)
            {
                shares.Add(ShareRow(share, profile.TotalMilliseconds, largest));
            }

            card.Add(shares);

            if (profile.Kind == TimingKind.ScriptReload && ordered.Count <= 1)
            {
                var note = LoggerUi.Box("lg-timing__note");
                note.Add(LoggerUi.Text(ReloadTimings.Enabled && ReloadTimings.ByPackage
                    ? "Unity reports each package's part at the next reload."
                    : "Measure the next reload by package to see what each package costs (Unity leaks editor memory while it does).", "lg-details__note"));
                if (!(ReloadTimings.Enabled && ReloadTimings.ByPackage))
                {
                    note.Add(LoggerUi.Pill("Measure by package", () =>
                    {
                        ReloadTimings.Enabled = true;
                        ReloadTimings.ByPackage = true;
                        ShowToast("The next reload is measured by package");
                    }, "ghost", LoggerGlyph.Stopwatch));
                }

                card.Add(note);
            }

            // History of this kind of timing.
            var history = Card(side, HistoryTitle(profile.Kind));
            history.AddToClassList("lg-card--timing-history");
            var chart = new TimingHistoryChart();
            chart.Set(timings.Entries, messageId);
            chart.MessageClicked += SelectTimingLog;
            history.Add(chart);
            var legend = LoggerUi.Box("lg-timing-legend");
            foreach (string source in chart.Palette)
            {
                var item = LoggerUi.Box("lg-timing-legend__item");
                var dot = LoggerUi.Box("lg-timing-legend__dot");
                dot.style.backgroundColor = TimingColors.For(source);
                item.Add(dot);
                item.Add(LoggerUi.Text(source, "lg-timing-legend__name"));
                legend.Add(item);
            }

            history.Add(legend);
            history.Add(HistoryStats(profile.Kind));
            frequencyChart = null;
            frequencyGroup = -1;
        }

        private static string Headline(TimingProfile profile)
        {
            switch (profile.Kind)
            {
                case TimingKind.PlayMode:
                    return "to enter Play Mode";
                case TimingKind.AvatarBuild:
                    return "to build " + (string.IsNullOrEmpty(profile.Subject) ? "the avatar" : profile.Subject);
                case TimingKind.AvatarUpload:
                    return "to build and upload " + (string.IsNullOrEmpty(profile.Subject) ? "the avatar" : profile.Subject);
                default:
                    return "to reload scripts";
            }
        }

        private static string HistoryTitle(TimingKind kind)
        {
            switch (kind)
            {
                case TimingKind.PlayMode:
                    return "Entering Play Mode over time";
                case TimingKind.AvatarBuild:
                    return "Avatar builds over time";
                case TimingKind.AvatarUpload:
                    return "Avatar uploads over time";
                default:
                    return "Script reloads over time";
            }
        }

        // Against the median of the earlier timings of the same kind.
        private string Comparison(TimingProfile profile, int messageId, out int trend)
        {
            trend = 0;
            var earlier = new List<double>();
            foreach (var entry in timings.Entries)
            {
                if (entry.Message == messageId)
                {
                    break;
                }

                if (entry.Profile.Kind == profile.Kind)
                {
                    earlier.Add(entry.Profile.TotalMilliseconds);
                }
            }

            if (earlier.Count < 2)
            {
                return string.Empty;
            }

            if (earlier.Count > 20)
            {
                earlier.RemoveRange(0, earlier.Count - 20);
            }

            earlier.Sort();
            double median = earlier[earlier.Count / 2];
            double delta = profile.TotalMilliseconds - median;
            if (Math.Abs(delta) < Math.Max(150d, median * 0.08d))
            {
                return "About as usual";
            }

            trend = delta > 0 ? 1 : -1;
            return TimingProfile.FormatDuration(Math.Abs(delta)) + (delta > 0 ? " slower than usual" : " faster than usual");
        }

        private VisualElement ShareRow(TimingShare share, double total, double largest)
        {
            var row = LoggerUi.Box("lg-share");
            row.EnableInClassList("lg-share--engine", share.Source == TimingProfile.UnitySource);
            var header = new Button();
            header.AddToClassList("lg-share__header");
            var chip = new SourceChip("lg-share__source");
            chip.Set(TimingColors.Source(share.Source), 9f);
            header.Add(chip);
            var track = LoggerUi.Box("lg-share__track", PickingMode.Ignore);
            var fill = LoggerUi.Box("lg-share__fill", PickingMode.Ignore);
            var color = TimingColors.For(share.Source);
            fill.style.backgroundColor = color;
            fill.style.width = Length.Percent((float)(share.Milliseconds / largest * 100d));
            track.Add(fill);
            header.Add(track);
            var duration = LoggerUi.Text(TimingProfile.FormatDuration(share.Milliseconds), "lg-share__duration");
            duration.pickingMode = PickingMode.Ignore;
            header.Add(duration);
            var percent = LoggerUi.Text((total > 0 ? share.Milliseconds / total * 100d : 0d).ToString("0") + "%", "lg-share__percent");
            percent.pickingMode = PickingMode.Ignore;
            header.Add(percent);
            var chevron = new LoggerIcon(LoggerGlyph.Chevron);
            chevron.AddToClassList("lg-share__chevron");
            header.Add(chevron);
            row.Add(header);

            var parts = LoggerUi.Box("lg-share__parts");
            parts.style.display = DisplayStyle.None;
            int shown = 0;
            foreach (var part in share.Parts)
            {
                if (shown++ >= MaxPartsShown)
                {
                    parts.Add(LoggerUi.Text("… " + LoggerUi.Plural(share.Parts.Count - MaxPartsShown, "smaller part"), "lg-share__more"));
                    break;
                }

                var line = LoggerUi.Box("lg-share__part");
                line.Add(LoggerUi.Text(part.Phase, "lg-share__phase"));
                var label = LoggerUi.Text(part.Label, "lg-share__label");
                label.tooltip = part.Label;
                line.Add(label);
                line.Add(LoggerUi.Text(TimingProfile.FormatDuration(part.Milliseconds), "lg-share__part-duration"));
                parts.Add(line);
            }

            row.Add(parts);
            LoggerUi.Press(header, () =>
            {
                bool open = parts.style.display == DisplayStyle.None;
                parts.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
                row.EnableInClassList("lg-share--open", open);
            });
            return row;
        }

        private VisualElement HistoryStats(TimingKind kind)
        {
            var values = new List<double>();
            foreach (var entry in timings.Entries)
            {
                if (entry.Profile.Kind == kind)
                {
                    values.Add(entry.Profile.TotalMilliseconds);
                }
            }

            var stats = LoggerUi.Box("lg-stats");
            if (values.Count == 0)
            {
                return stats;
            }

            values.Sort();
            string[] names = { "Times", "Usual", "Fastest", "Slowest" };
            string[] cells =
            {
                LoggerUi.Count(values.Count), TimingProfile.FormatDuration(values[values.Count / 2]),
                TimingProfile.FormatDuration(values[0]), TimingProfile.FormatDuration(values[values.Count - 1])
            };
            for (int i = 0; i < names.Length; i++)
            {
                var cell = LoggerUi.Box("lg-stats__cell");
                cell.Add(LoggerUi.Text(names[i], "lg-stats__name"));
                cell.Add(LoggerUi.Text(cells[i], "lg-stats__value"));
                stats.Add(cell);
            }

            return stats;
        }

        /// <summary>
        /// Selects the log of a timing, as a click on its row would: from the history chart or the timeline. The filters
        /// that hide it are turned off (a successful upload is a log, a failed one a warning: showing only warnings and
        /// errors hides the first), and a group row showing a later log of the same text gives way to the list, so what
        /// shows is the timing that was clicked.
        /// </summary>
        private void SelectTimingLog(int messageId)
        {
            var store = Store;
            if (store == null || state == null || messageId < 0 || messageId >= store.MessageCount)
            {
                return;
            }

            ShowLog(store.Message(messageId).Last, "timing");
        }

        /// <summary>
        /// Selects a log and scrolls to it, as a click on its row would. The filters that hide it are turned off, and a
        /// group row showing a later log of the same text gives way to the list, so what shows is that log;
        /// <paramref name="noun"/> names it in the toasts ("timing", "error").
        /// </summary>
        private void ShowLog(int occurrence, string noun)
        {
            var store = Store;
            if (store == null || state == null || occurrence < 0 || occurrence >= store.Count)
            {
                return;
            }

            int messageId = store.MessageAt(occurrence);
            var hidden = BuildSpec().WhatHides(store, occurrence);
            if ((hidden & HiddenBy.Gone) != 0)
            {
                ShowToast("That " + noun + " is no longer in the log", error: true);
                return;
            }

            var changes = RevealOccurrence(store, occurrence, hidden);
            if (changes.Count > 0 || state.RowOf(occurrence) < 0)
            {
                // Filters just changed, or the list hasn't taken the newest logs in yet: the row is needed now.
                Refilter(allowAsync: false);
            }

            int group = store.Message(messageId).Group;
            if (grouped && group < state.GroupCount.Length && state.GroupCount[group] > 0 && state.GroupLast[group] != occurrence)
            {
                SetGrouped(false);
                changes.Add("list view");
            }

            int key = grouped ? group : occurrence;
            int row = RowOf(key);
            if (row < 0)
            {
                ShowToast("Couldn't show that " + noun, error: true);
                return;
            }

            Select(key, extend: false, toggle: false);
            list.Center(row);
            if (changes.Count > 0)
            {
                ShowToast("Showing that " + noun + ": " + string.Join(", ", changes));
            }
        }

        /// <summary>The newest error in the log (an exception, an assert, a compile error still standing), or -1.</summary>
        internal static int LatestError(LogStore store)
        {
            if (store == null)
            {
                return -1;
            }

            for (int occurrence = store.Count - 1, seen = 0; occurrence >= 0 && seen < 1_000_000; occurrence--, seen++)
            {
                var message = store.Message(store.MessageAt(occurrence));
                if (message.Level == LogLevel.Error && !message.Resolved && (store.FlagsAt(occurrence) & LogStore.Superseded) == 0)
                {
                    return occurrence;
                }
            }

            return -1;
        }

        /// <summary>
        /// The error a tool's "Check Console for more details" means: where a VRChat upload failed
        /// (<see cref="SdkUploadErrors"/>) when it was logged with the newest error (within a few minutes of it), else the
        /// newest error.
        /// </summary>
        internal static int ErrorToShow(LogStore store)
        {
            int latest = LatestError(store);
            if (latest < 0)
            {
                return -1;
            }

            long since = store.TimeAt(latest) - TimeSpan.TicksPerMinute * 5;
            for (int occurrence = store.Count - 1, seen = 0; occurrence >= 0 && seen < 20_000 && store.TimeAt(occurrence) >= since; occurrence--, seen++)
            {
                var message = store.Message(store.MessageAt(occurrence));
                if (message.Level == LogLevel.Error && store.Texts[message.Text].StartsWith(SdkUploadErrors.Prefix, StringComparison.Ordinal))
                {
                    return occurrence;
                }
            }

            return latest;
        }

        /// <summary>
        /// Shows <see cref="ErrorToShow"/>, selected (what a tool's "Check Console for more details" means). A window
        /// whose list isn't built yet shows it once it is.
        /// </summary>
        internal void ShowLatestError()
        {
            int occurrence = ErrorToShow(Store);
            if (occurrence < 0)
            {
                return;
            }

            if (!built || state == null || list == null)
            {
                errorToShow = occurrence;
                return;
            }

            // After the list's first layout: a reveal before it scrolls to the wrong place.
            rootVisualElement.schedule.Execute(() => ShowLog(occurrence, "error")).StartingIn(30);
        }
    }
}
