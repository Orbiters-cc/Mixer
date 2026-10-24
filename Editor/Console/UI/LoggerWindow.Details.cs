using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class LoggerWindow
    {
        private const int MaxDetailChars = 24_000;
        private const int MaxFrames = 120;

        private VisualElement detailsPane;
        private LoggerIcon detailsIcon;
        private Label detailsKind;
        private SourceChip detailsSource;
        private Label detailsMeta;
        private VisualElement detailsActions;
        private Button openButton;
        private Button contextButton;
        private ScrollView detailsScroll;
        private VisualElement detailsBody;
        private VisualElement detailsEmpty;
        private Font monoFont;

        private int shownKey = int.MinValue;
        private bool shownGrouped;
        private int shownSelectionCount = -1;
        private int shownStoreEpoch = -1;
        private double lastFrequency;
        private int frequencyVersion = -1;
        private FrequencyChart frequencyChart;
        private Label[] frequencyStats;
        private int frequencyGroup = -1;

        private VisualElement BuildDetailsPane()
        {
            detailsPane = LoggerUi.Box("lg-details");

            var header = LoggerUi.Box("lg-details__header");
            detailsIcon = new LoggerIcon(LoggerGlyph.Info);
            detailsIcon.AddToClassList("lg-details__icon");
            header.Add(detailsIcon);
            detailsKind = LoggerUi.Text(string.Empty, "lg-details__kind");
            header.Add(detailsKind);
            detailsSource = new SourceChip("lg-details__source");
            header.Add(detailsSource);
            detailsMeta = LoggerUi.Text(string.Empty, "lg-details__meta");
            header.Add(detailsMeta);
            header.Add(LoggerUi.Spacer());
            detailsActions = LoggerUi.Box("lg-details__actions");
            detailsActions.Add(LoggerUi.Icon(LoggerGlyph.Copy, "Copy the text (Ctrl+C)", () => Copy(false)));
            detailsActions.Add(LoggerUi.Icon(LoggerGlyph.CopyStack, "Copy the text with its stack trace (Ctrl+Shift+C)", () => Copy(true)));
            openButton = LoggerUi.Icon(LoggerGlyph.Open, "Open the source file at this line (Enter or double-click)", OpenCursor);
            detailsActions.Add(openButton);
            contextButton = LoggerUi.Icon(LoggerGlyph.Target, "Select the object this log is about", SelectContext);
            detailsActions.Add(contextButton);
            detailsActions.Add(LoggerUi.Icon(LoggerGlyph.EyeOff, "Hide messages like this (Delete)", HideSelected));
            detailsActions.Add(LoggerUi.Icon(LoggerGlyph.Lifebuoy, "Get help with this: a project report about this log", AskForHelp));
            header.Add(detailsActions);
            detailsPane.Add(header);

            detailsScroll = new ScrollView(ScrollViewMode.Vertical);
            detailsScroll.AddToClassList("lg-details__scroll");
            detailsBody = LoggerUi.Box("lg-details__body");
            detailsScroll.Add(detailsBody);
            detailsPane.Add(detailsScroll);

            detailsEmpty = LoggerUi.Box("lg-details__empty", PickingMode.Ignore);
            detailsEmpty.Add(LoggerUi.Text("Select a log to see its message, stack trace, explanation and how often it happens.", "lg-details__empty-title"));
            var keys = LoggerUi.Box("lg-keys", PickingMode.Ignore);
            foreach (var (key, what) in new[]
                     {
                         ("↑ ↓", "move"), ("Shift", "range"), ("Ctrl+C", "copy"), ("Ctrl+Shift+C", "with stack trace"),
                         ("Enter", "open file"), ("F3", "next error"), ("Delete", "hide"), ("Ctrl+F", "search")
                     })
            {
                var item = LoggerUi.Box("lg-keys__item", PickingMode.Ignore);
                item.Add(LoggerUi.Text(key, "lg-keys__key"));
                item.Add(LoggerUi.Text(what, "lg-keys__what"));
                keys.Add(item);
            }

            detailsEmpty.Add(keys);
            detailsPane.Add(detailsEmpty);
            return detailsPane;
        }

        private void DisposeDetails()
        {
            if (monoFont != null)
            {
                DestroyImmediate(monoFont);
                monoFont = null;
            }
        }

        private Font MonoFont
        {
            get
            {
                if (monoFont == null)
                {
                    monoFont = Font.CreateDynamicFontFromOSFont(new[] { "JetBrains Mono", "Cascadia Mono", "Consolas", "Menlo", "DejaVu Sans Mono" }, 12);
                    if (monoFont != null)
                    {
                        monoFont.hideFlags = HideFlags.HideAndDontSave;
                    }
                }

                return monoFont;
            }
        }

        private void UpdateDetails()
        {
            detailsDirty = false;
            if (!built || detailsPane == null)
            {
                return;
            }

            var store = Store;
            int count = SelectedCount;
            int key = cursor >= 0 && IsSelected(cursor) ? cursor : count == 1 ? FirstSelected() : -1;
            bool nothing = state == null || store == null || count == 0 && cursor < 0;
            LoggerUi.Show(detailsEmpty, nothing);
            detailsScroll.style.display = nothing ? DisplayStyle.None : DisplayStyle.Flex;
            detailsPane.Q(className: "lg-details__header").style.display = nothing ? DisplayStyle.None : DisplayStyle.Flex;
            if (nothing)
            {
                shownKey = int.MinValue;
                return;
            }

            if (count > 1)
            {
                if (shownSelectionCount != count || shownKey != -1 || shownStoreEpoch != store.Epoch)
                {
                    ShowSelectionSummary(store, count);
                }

                return;
            }

            if (key < 0)
            {
                key = cursor;
            }

            bool sameLog = key == shownKey && grouped == shownGrouped && shownStoreEpoch == store.Epoch && shownSelectionCount == count;
            if (!sameLog)
            {
                ShowLog(store, key);
            }
            else
            {
                RefreshFrequency(store, force: false);
            }
        }

        private int FirstSelected()
        {
            foreach (int key in selection)
            {
                return key;
            }

            return -1;
        }

        private void ShowLog(LogStore store, int key)
        {
            shownKey = key;
            shownGrouped = grouped;
            shownStoreEpoch = store.Epoch;
            shownSelectionCount = SelectedCount;
            detailsBody.Clear();
            int messageId = MessageOfKey(key);
            if (messageId < 0)
            {
                return;
            }

            ref var message = ref store.Message(messageId);
            int occurrence = grouped ? state.GroupLast[key] : key;
            var source = store.Sources[message.Source];
            var level = message.Level;

            detailsPane.EnableInClassList("lg-details--error", level == LogLevel.Error);
            detailsPane.EnableInClassList("lg-details--warning", level == LogLevel.Warning);
            detailsIcon.Glyph = LoggerIcon.ForLevel(level);
            detailsKind.text = LogVariants.Label(message.Variant);
            detailsSource.Set(source, 10f);
            detailsSource.style.display = DisplayStyle.Flex;
            var flags = store.FlagsAt(occurrence);
            string time = LoggerUi.Moment(store.TimeAt(occurrence));
            if ((flags & OccurrenceFlags.ApproximateTime) == 0)
            {
                time = LoggerUi.Time(store.TimeAt(occurrence), true);
            }

            detailsMeta.text = grouped
                ? "×" + LoggerUi.Count(state.GroupCount[key]) + "  ·  last " + time
                : time + ((flags & OccurrenceFlags.BackgroundThread) != 0 ? "  ·  background thread" : string.Empty) +
                  ((flags & OccurrenceFlags.FromUnityConsole) != 0 ? "  ·  from Unity's console" : string.Empty);
            openButton.SetEnabled(message.File > 0);
            int contextId = 0;
            bool hasContext = !grouped && store.TryGetContext(occurrence, out contextId) && EditorUtility.InstanceIDToObject(contextId) != null;
            contextButton.style.display = hasContext ? DisplayStyle.Flex : DisplayStyle.None;

            var columns = LoggerUi.Box("lg-details__columns");
            var main = LoggerUi.Box("lg-details__main");
            var side = LoggerUi.Box("lg-details__side");
            columns.Add(main);
            columns.Add(side);
            detailsBody.Add(columns);
            detailsPane.EnableInClassList("lg-details--timing", false);
            if (timings.TryGet(messageId, out var timing))
            {
                ShowTiming(store, messageId, occurrence, timing, main, side);
                detailsScroll.scrollOffset = Vector2.zero;
                return;
            }

            // Message
            var messageCard = Card(main, null);
            string text = store.Texts.Plain(message.Text);
            bool cut = text.Length > MaxDetailChars;
            var messageLabel = new Label(RichText.Literal(cut ? text.Substring(0, MaxDetailChars) : text));
            messageLabel.AddToClassList("lg-details__message");
            messageLabel.selection.isSelectable = true;
            messageCard.Add(messageLabel);
            if (cut)
            {
                messageCard.Add(LoggerUi.Text("Message shortened here: " + LoggerUi.Count(text.Length) + " characters. Copy it to get everything.", "lg-details__note"));
            }

            if (hasContext)
            {
                var target = EditorUtility.InstanceIDToObject(contextId);
                var contextRow = LoggerUi.Box("lg-context");
                contextRow.Add(new LoggerIcon(LoggerGlyph.Target));
                contextRow.Add(LoggerUi.Text("About " + (target != null ? target.name : "an object") + (target != null ? "  (" + target.GetType().Name + ")" : string.Empty), "lg-context__label"));
                contextRow.Add(LoggerUi.Pill("Select", SelectContext, "ghost"));
                messageCard.Add(contextRow);
            }

            // Stack trace
            if (LogVariants.IsCompile(message.Variant))
            {
                if (message.File > 0)
                {
                    var location = Card(main, "Location");
                    location.Add(FrameRow(new StackFrame(store.Texts[message.File], string.Empty, store.Texts[message.File], message.Line, StackFrameKind.User), message.Column));
                }
            }
            else if (message.Stack > 0 && StackTraces.IsOutput(store.Texts[message.Stack]))
            {
                string output = store.Texts[message.Stack].Substring(StackTraces.OutputPrefix.Length).TrimEnd();
                var outputLabel = new Label(RichText.Literal(output.Length > MaxDetailChars ? output.Substring(0, MaxDetailChars) : output));
                outputLabel.AddToClassList("lg-details__output");
                outputLabel.selection.isSelectable = true;
                if (MonoFont != null)
                {
                    outputLabel.style.unityFont = MonoFont;
                }

                Card(main, "Output").Add(outputLabel);
            }
            else if (message.Stack > 0)
            {
                BuildStack(Card(main, "Stack trace"), store.Texts[message.Stack]);
            }

            // Explanation
            int explanation = ExplanationOf(messageId);
            if (explanation >= 0)
            {
                var explained = LogExplanations.Explain(explanation, text);
                if (explained != null)
                {
                    side.Add(BuildExplanation(explained));
                }
            }

            // Since when (and since which commit) it keeps appearing
            BuildSince(side, store, message.Group, text);

            // Frequency
            var frequency = Card(side, "Frequency");
            frequency.AddToClassList("lg-card--frequency");
            frequencyChart = new FrequencyChart();
            frequency.Add(frequencyChart);
            var stats = LoggerUi.Box("lg-stats");
            frequencyStats = new Label[6];
            string[] names = { "Times", "First", "Last", "Peak", "Every", "Variants" };
            for (int i = 0; i < names.Length; i++)
            {
                var cell = LoggerUi.Box("lg-stats__cell");
                cell.Add(LoggerUi.Text(names[i], "lg-stats__name"));
                frequencyStats[i] = LoggerUi.Text("–", "lg-stats__value");
                cell.Add(frequencyStats[i]);
                stats.Add(cell);
            }

            frequency.Add(stats);
            frequencyGroup = message.Group;
            RefreshFrequency(store, force: true);
            detailsScroll.scrollOffset = Vector2.zero;
        }

        // For a message that keeps coming back (several times, or in earlier sessions): when it was first seen and the
        // commit the project was at then, from the ledger and Git's own history of HEAD.
        private void BuildSince(VisualElement parent, LogStore store, int groupId, string text)
        {
            if (!LogLedger.TryGet(text, out var firstUtc, out _))
            {
                return;
            }

            var group = store.Group(groupId);
            bool earlierSession = store.Count > 0 && firstUtc.Ticks < store.FirstTime - TimeSpan.TicksPerMinute;
            if (group.Count < 2 && !earlierSession)
            {
                return;
            }

            var card = Card(parent, "Appearing since");
            card.AddToClassList("lg-since");
            var row = LoggerUi.Box("lg-since__row");
            bool known = GitHistory.At(firstUtc, out var move, out int commitsSince);
            if (known)
            {
                var icon = new LoggerIcon(LoggerGlyph.Commit);
                icon.AddToClassList("lg-since__icon");
                row.Add(icon);
                var hash = new Button { tooltip = move.Hash + "\nClick to copy" };
                hash.AddToClassList("lg-since__hash");
                hash.Add(LoggerUi.Text(move.ShortHash, "lg-since__hash-label"));
                string full = move.Hash;
                LoggerUi.Press(hash, () =>
                {
                    EditorGUIUtility.systemCopyBuffer = full;
                    ShowToast("Copied the commit hash");
                });
                row.Add(hash);
                string subject = string.IsNullOrEmpty(move.Subject) ? move.Action : move.Subject;
                var label = LoggerUi.Text(subject, "lg-since__subject");
                label.tooltip = subject;
                row.Add(label);
            }
            else
            {
                row.Add(LoggerUi.Text(GitHistory.Available ? "Before the Git history kept in this project" : "No Git history in this project",
                    "lg-since__subject"));
            }

            card.Add(row);
            string when = "First seen " + LoggerUi.Moment(firstUtc.Ticks) + "  ·  " + LoggerUi.Ago(firstUtc.Ticks);
            if (known)
            {
                when += "  ·  " + (commitsSince == 0 ? "at the current commit" : LoggerUi.Plural(commitsSince, "commit") + " ago");
            }

            card.Add(LoggerUi.Text(when, "lg-since__meta"));
        }

        private VisualElement Card(VisualElement parent, string title)
        {
            var card = LoggerUi.Box("lg-card");
            if (!string.IsNullOrEmpty(title))
            {
                card.Add(LoggerUi.Text(title, "lg-card__title"));
            }

            parent.Add(card);
            return card;
        }

        private void BuildStack(VisualElement card, string stack)
        {
            var frames = StackTraces.Parse(stack);
            int hidden = 0;
            var hiddenFrames = new List<StackFrame>();
            int shown = 0;
            foreach (var frame in frames)
            {
                if (frame.Kind == StackFrameKind.Logging)
                {
                    hidden++;
                    hiddenFrames.Add(frame);
                    continue;
                }

                if (shown++ >= MaxFrames)
                {
                    card.Add(LoggerUi.Text("… " + LoggerUi.Plural(frames.Count - MaxFrames, "more frame"), "lg-details__note"));
                    break;
                }

                card.Add(FrameRow(frame, 0));
            }

            if (hidden > 0)
            {
                var more = LoggerUi.Text(LoggerUi.Plural(hidden, "logging frame") + " hidden (UnityEngine.Debug…)", "lg-frames__hidden");
                more.tooltip = string.Join("\n", hiddenFrames.ConvertAll(f => f.Text));
                card.Add(more);
            }

            if (frames.Count == 0)
            {
                card.Add(LoggerUi.Text("No stack trace: logged by the engine.", "lg-details__note"));
            }
        }

        private VisualElement FrameRow(StackFrame frame, int column)
        {
            var row = LoggerUi.Box("lg-frame");
            row.EnableInClassList("lg-frame--internal", frame.Kind == StackFrameKind.Internal);
            row.EnableInClassList("lg-frame--note", frame.Kind == StackFrameKind.Note);
            string method = string.IsNullOrEmpty(frame.Method) ? string.Empty : frame.Method;
            var methodLabel = new Label(RichText.Literal(method));
            methodLabel.AddToClassList("lg-frame__method");
            methodLabel.selection.isSelectable = true;
            if (MonoFont != null)
            {
                methodLabel.style.unityFont = MonoFont;
            }

            row.Add(methodLabel);
            if (frame.HasFile)
            {
                string path = StackTraces.NormalizePath(frame.File);
                var link = new Button { tooltip = "Open " + path + " at line " + frame.Line };
                link.AddToClassList("lg-frame__link");
                link.Add(LoggerUi.Text(StackTraces.FileName(path) + ":" + frame.Line, "lg-frame__file"));
                int line = frame.Line;
                LoggerUi.Press(link, () =>
                {
                    if (!OpenFile(path, line, column))
                    {
                        ShowToast("Can't open " + path, error: true);
                    }
                });
                row.Add(link);
            }

            return row;
        }

        private VisualElement BuildExplanation(ExplainedLog explained)
        {
            var entry = explained.Explanation;
            var card = LoggerUi.Box("lg-card");
            card.AddToClassList("lg-explain");
            card.AddToClassList("lg-explain--" + entry.Severity.ToString().ToLowerInvariant());
            var head = LoggerUi.Box("lg-explain__head");
            var bulb = new LoggerIcon(LoggerGlyph.Bulb);
            bulb.AddToClassList("lg-explain__icon");
            head.Add(bulb);
            head.Add(LoggerUi.Text(SeverityLabel(entry.Severity), "lg-explain__severity"));
            // The entry's texts are shown as written: they come from the server and their placeholders from the log, where
            // a name holding <size=0> must not hide or restyle anything.
            if (!string.IsNullOrEmpty(entry.Source))
            {
                head.Add(LoggerUi.Plain(entry.Source, "lg-explain__source"));
            }

            card.Add(head);
            card.Add(LoggerUi.Plain(explained.Title, "lg-explain__title"));
            if (!string.IsNullOrEmpty(explained.Summary))
            {
                card.Add(LoggerUi.Plain(explained.Summary, "lg-explain__summary"));
            }

            if (explained.Fixes.Length > 0)
            {
                card.Add(LoggerUi.Text(entry.Severity <= LogExplanationSeverity.Info ? "If you want to act on it" : "How to fix it", "lg-explain__fix-title"));
                for (int i = 0; i < explained.Fixes.Length; i++)
                {
                    var step = LoggerUi.Box("lg-explain__step");
                    step.Add(LoggerUi.Text((i + 1).ToString(), "lg-explain__step-number"));
                    step.Add(LoggerUi.Plain(explained.Fixes[i], "lg-explain__step-text"));
                    card.Add(step);
                }
            }

            string url = entry.WebLink;
            if (url != null)
            {
                card.Add(LoggerUi.Pill(string.IsNullOrEmpty(entry.LinkLabel) ? "Learn more" : RichText.Literal(entry.LinkLabel), () => Application.OpenURL(url), "ghost",
                    LoggerGlyph.External));
            }

            return card;
        }

        private static string SeverityLabel(LogExplanationSeverity severity)
        {
            switch (severity)
            {
                case LogExplanationSeverity.Harmless:
                    return "Safe to ignore";
                case LogExplanationSeverity.Info:
                    return "Good to know";
                case LogExplanationSeverity.Warning:
                    return "Worth a look";
                case LogExplanationSeverity.Problem:
                    return "Needs a fix";
                default:
                    return "Blocks Play Mode, builds or uploads";
            }
        }

        // How often the shown message's group happens over the session. Updated at most once a second.
        private void RefreshFrequency(LogStore store, bool force)
        {
            if (frequencyChart == null || frequencyGroup < 0 || frequencyGroup >= store.GroupCount || state == null)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (!force && (now - lastFrequency < 1d || store.Version == frequencyVersion))
            {
                return;
            }

            lastFrequency = now;
            frequencyVersion = store.Version;
            var group = store.Group(frequencyGroup);
            if (group.Count == 0 || group.First < 0)
            {
                return;
            }

            // A message's frequency covers the whole session, whatever window the activity chart shows.
            long chartFrom = store.Count > 0 ? store.FirstTime : state.ChartFrom;
            long chartTo = Math.Max(state.ChartTo, store.LastTime);
            int buckets = Mathf.Clamp((int)(Math.Max(120f, frequencyChart.layout.width) / 4f), 30, 240);
            var values = new int[buckets];
            long span = Math.Max(1L, chartTo - chartFrom);
            int count = 0;
            long first = 0;
            long last = 0;
            int peak = 0;
            int windowStart = -1;
            var times = new List<long>(Math.Min(group.Count, 1 << 16));
            for (int i = group.First; i <= group.Last && i < store.Count; i++)
            {
                if (store.Message(store.MessageAt(i)).Group != frequencyGroup || (store.FlagsAt(i) & LogStore.Superseded) != 0)
                {
                    continue;
                }

                long t = store.TimeAt(i);
                if (count == 0)
                {
                    first = t;
                }

                last = t;
                count++;
                long offset = t - chartFrom;
                int bucket = offset <= 0 ? 0 : offset >= span ? buckets - 1 : (int)(offset * buckets / span);
                values[bucket]++;

                // Peak per second: a window sliding over the (ordered) times.
                times.Add(t);
                if (windowStart < 0)
                {
                    windowStart = 0;
                }

                while (t - times[windowStart] > TimeSpan.TicksPerSecond)
                {
                    windowStart++;
                }

                peak = Math.Max(peak, times.Count - windowStart);
                if (times.Count > 1 << 16)
                {
                    times.RemoveRange(0, windowStart);
                    windowStart = 0;
                }
            }

            frequencyChart.Set(values, chartFrom, chartTo, ChartColors.Level(group.Level));
            frequencyStats[0].text = LoggerUi.Count(count);
            frequencyStats[1].text = count > 0 ? LoggerUi.Moment(first) : "–";
            frequencyStats[2].text = count > 0 ? LoggerUi.Ago(last) : "–";
            frequencyStats[3].text = count > 1 ? LoggerUi.Count(peak) + " / s" : "–";
            frequencyStats[4].text = count > 1 ? LoggerUi.Duration(TimeSpan.FromTicks((last - first) / (count - 1))).Trim() : "once";
            frequencyStats[5].text = LoggerUi.Count(Math.Max(1, group.Variants));
            frequencyStats[5].tooltip = "Different stack traces or types logged with this text";
        }

        private void ShowSelectionSummary(LogStore store, int count)
        {
            shownKey = -1;
            shownGrouped = grouped;
            shownSelectionCount = count;
            shownStoreEpoch = store.Epoch;
            frequencyChart = null;
            frequencyGroup = -1;
            detailsBody.Clear();
            detailsPane.EnableInClassList("lg-details--error", false);
            detailsPane.EnableInClassList("lg-details--warning", false);
            detailsPane.EnableInClassList("lg-details--timing", false);
            detailsIcon.Glyph = LoggerGlyph.List;
            detailsKind.text = LoggerUi.Plural(count, grouped ? "message" : "log") + " selected";
            detailsSource.style.display = DisplayStyle.None;
            openButton.SetEnabled(false);
            contextButton.style.display = DisplayStyle.None;

            var keys = SelectedKeysInOrder(5000);
            var levels = new int[3];
            foreach (int key in keys)
            {
                int messageId = MessageOfKey(key);
                if (messageId >= 0)
                {
                    levels[(int)store.Message(messageId).Level]++;
                }
            }

            detailsMeta.text = (levels[2] > 0 ? LoggerUi.Plural(levels[2], "error") + "  " : string.Empty) +
                               (levels[1] > 0 ? LoggerUi.Plural(levels[1], "warning") + "  " : string.Empty) +
                               (levels[0] > 0 ? LoggerUi.Plural(levels[0], "log") : string.Empty) +
                               (keys.Count < count ? "  (in the first " + LoggerUi.Count(keys.Count) + ")" : string.Empty);

            var card = Card(detailsBody, null);
            card.AddToClassList("lg-card--selection");
            int shown = 0;
            foreach (int key in keys)
            {
                if (shown++ >= 40)
                {
                    card.Add(LoggerUi.Text("… and " + LoggerUi.Count(count - 40) + " more. Copy them with Ctrl+C.", "lg-details__note"));
                    break;
                }

                int messageId = MessageOfKey(key);
                if (messageId < 0)
                {
                    continue;
                }

                var message = store.Message(messageId);
                var line = LoggerUi.Box("lg-selection-line");
                var icon = new LoggerIcon(LoggerIcon.ForLevel(message.Level));
                icon.AddToClassList("lg-selection-line__icon");
                icon.AddToClassList("lg-level-color--" + message.Level.ToString().ToLowerInvariant());
                line.Add(icon);
                line.Add(LoggerUi.Text(RichText.Literal(RichText.FirstLine(store.Texts.Plain(message.Text), 200)), "lg-selection-line__text"));
                card.Add(line);
            }
        }

        // The report window, its note started with the log in view.
        private void AskForHelp()
        {
            string note = null;
            int messageId = cursor >= 0 ? MessageOfKey(cursor) : -1;
            if (messageId >= 0)
            {
                var store = Store;
                ref var message = ref store.Message(messageId);
                note = "I get this " + LogVariants.Label(message.Variant).ToLowerInvariant() + ": " + DiagnosisClient.FirstLine(store.Texts.Plain(message.Text));
            }

            ReportWindow.Open(note);
        }

        private void SelectContext()
        {
            var store = Store;
            if (grouped || cursor < 0 || !store.TryGetContext(cursor, out int instanceId))
            {
                return;
            }

            if (EditorUtility.InstanceIDToObject(instanceId) == null)
            {
                ShowToast("That object no longer exists", error: true);
                return;
            }

            Selection.activeInstanceID = instanceId;
            EditorGUIUtility.PingObject(instanceId);
        }
    }
}
