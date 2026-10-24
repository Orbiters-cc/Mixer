using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    /// <summary>Common frame of the Mixer console's popups: its stylesheet, look and Escape to close.</summary>
    internal abstract class LoggerPopup : PopupWindowContent
    {
        private const string StyleSheetPath = "Packages/orbiters.mixer/Editor/Console/UI/logger.uss";

        public override void OnGUI(Rect rect)
        {
        }

        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            root.AddToClassList("lg-root");
            root.AddToClassList("lg-popup");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null)
            {
                root.styleSheets.Add(sheet);
            }

            root.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    editorWindow.Close();
                }
            }, TrickleDown.TrickleDown);
            Build(root);
        }

        protected abstract void Build(VisualElement root);

        protected static VisualElement Header(string title, string detail = null)
        {
            var header = LoggerUi.Box("lg-popup__header");
            header.Add(LoggerUi.Text(title, "lg-popup__title"));
            if (!string.IsNullOrEmpty(detail))
            {
                header.Add(LoggerUi.Text(detail, "lg-popup__detail"));
            }

            return header;
        }
    }

    /// <summary>Which sources show: a check per source with its colour and count, and "only this".</summary>
    internal sealed class SourcesPopup : LoggerPopup
    {
        private readonly LoggerWindow window;
        private VisualElement list;
        private string filter = string.Empty;

        private SourcesPopup(LoggerWindow window)
        {
            this.window = window;
        }

        public static void Show(Rect activator, LoggerWindow window) => UnityEditor.PopupWindow.Show(activator, new SourcesPopup(window));

        public override Vector2 GetWindowSize()
        {
            int rows = LogCapture.Store?.Sources.Count ?? 4;
            return new Vector2(330f, Mathf.Clamp(118f + rows * 30f, 190f, 470f));
        }

        protected override void Build(VisualElement root)
        {
            root.Add(Header("Sources", "Where logs come from: the package or folder of the code that logged them."));
            var search = new TextField();
            search.AddToClassList("lg-popup__search");
            var placeholder = LoggerUi.Text("Find a source", "lg-popup__placeholder");
            placeholder.pickingMode = PickingMode.Ignore;
            search.Add(placeholder);
            search.RegisterValueChangedCallback(evt =>
            {
                filter = evt.newValue ?? string.Empty;
                placeholder.style.display = filter.Length == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                Fill();
            });
            root.Add(search);

            var actions = LoggerUi.Box("lg-popup__actions");
            actions.Add(LoggerUi.Pill("Show all", () =>
            {
                window.ShowAllSources();
                Fill();
            }, "ghost", LoggerGlyph.Eye));
            root.Add(actions);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("lg-popup__scroll");
            list = scroll.contentContainer;
            root.Add(scroll);
            Fill();
            search.schedule.Execute(() => search.Q(className: "unity-text-field__input")?.Focus()).StartingIn(30);
        }

        private void Fill()
        {
            list.Clear();
            var store = LogCapture.Store;
            var state = window.CurrentState;
            if (store == null)
            {
                return;
            }

            var hidden = new HashSet<string>(window.HiddenSources, StringComparer.OrdinalIgnoreCase);
            var sources = store.Sources.All
                .Where(s => filter.Length == 0 || s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(s => (source: s, count: state != null && s.Id < state.SourceCounts.Length ? state.SourceCounts[s.Id] : 0))
                .Where(s => s.count > 0 || hidden.Contains(s.source.Key))
                .OrderByDescending(s => s.count)
                .ThenBy(s => s.source.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var (source, count) in sources)
            {
                bool on = !hidden.Contains(source.Key);
                var row = new Button();
                row.AddToClassList("lg-source-row");
                row.EnableInClassList("lg-source-row--off", !on);
                var check = LoggerUi.Box("lg-check", PickingMode.Ignore);
                check.EnableInClassList("lg-check--on", on);
                check.Add(new LoggerIcon(LoggerGlyph.Check));
                row.Add(check);
                var dot = LoggerUi.Box("lg-source-row__dot", PickingMode.Ignore);
                dot.style.backgroundColor = source.Color;
                row.Add(dot);
                row.Add(LoggerUi.Text(source.Name, "lg-source-row__name"));
                row.Add(LoggerUi.Text(LoggerUi.Compact(count), "lg-source-row__count"));
                var only = new Button { text = "Only", tooltip = "Show only " + source.Name };
                only.AddToClassList("lg-source-row__only");
                var key = source.Key;
                LoggerUi.Press(only, () =>
                {
                    window.SetOnlySource(key);
                    Fill();
                });
                row.Add(only);
                LoggerUi.Press(row, () =>
                {
                    window.SetSourceHidden(key, on);
                    Fill();
                });
                list.Add(row);
            }

            if (sources.Count == 0)
            {
                list.Add(LoggerUi.Text(filter.Length > 0 ? "No source matches “" + filter + "”" : "No logs yet", "lg-popup__empty"));
            }
        }
    }

    /// <summary>Messages hidden with "Hide messages like this", each with a way back.</summary>
    internal sealed class MutedPopup : LoggerPopup
    {
        private readonly LoggerWindow window;
        private VisualElement list;

        private MutedPopup(LoggerWindow window)
        {
            this.window = window;
        }

        public static void Show(Rect activator, LoggerWindow window) => UnityEditor.PopupWindow.Show(activator, new MutedPopup(window));

        public override Vector2 GetWindowSize() => new Vector2(420f, Mathf.Clamp(110f + window.Muted.Count * 34f, 160f, 440f));

        protected override void Build(VisualElement root)
        {
            root.Add(Header("Hidden messages", "Messages with this text don't show. Their logs are still recorded."));
            var actions = LoggerUi.Box("lg-popup__actions");
            actions.Add(LoggerUi.Pill("Show all again", () =>
            {
                window.UnmuteAll();
                editorWindow.Close();
            }, "ghost", LoggerGlyph.Eye));
            root.Add(actions);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("lg-popup__scroll");
            list = scroll.contentContainer;
            root.Add(scroll);
            Fill();
        }

        private void Fill()
        {
            list.Clear();
            foreach (string key in window.Muted.ToList())
            {
                var row = LoggerUi.Box("lg-muted-row");
                var label = LoggerUi.Text(RichText.Literal(RichText.FirstLine(RichText.Strip(key), 140)), "lg-muted-row__text");
                label.tooltip = RichText.Strip(key);
                row.Add(label);
                string captured = key;
                row.Add(LoggerUi.Pill("Show", () =>
                {
                    window.Unmute(captured);
                    Fill();
                }, "ghost"));
                list.Add(row);
            }

            if (window.Muted.Count == 0)
            {
                list.Add(LoggerUi.Text("Nothing hidden", "lg-popup__empty"));
            }
        }
    }

    /// <summary>
    /// Settings. Clear on Play / Build / Recompile and Error Pause are Unity's console settings, shared with it.
    /// </summary>
    internal sealed class SettingsPopup : LoggerPopup
    {
        private static readonly int[] LogLimits = { 250_000, 1_000_000, 3_000_000, 6_000_000 };
        private readonly LoggerWindow window;

        private SettingsPopup(LoggerWindow window)
        {
            this.window = window;
        }

        public static void Show(Rect activator, LoggerWindow window)
        {
            var rect = new Rect(activator.xMax - 330f, activator.y, 330f, activator.height);
            UnityEditor.PopupWindow.Show(rect, new SettingsPopup(window));
        }

        public override Vector2 GetWindowSize() => new Vector2(340f, 560f);

        protected override void Build(VisualElement root)
        {
            root.Add(Header("Mixer console settings"));
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("lg-popup__scroll");
            root.Add(scroll);
            var body = scroll.contentContainer;

            body.Add(Section("Clearing", "Shared with Unity's console."));
            body.Add(Switch("Clear on Play", "Start each Play Mode session with an empty log.", UnityConsole.FlagClearOnPlay));
            body.Add(Switch("Clear on Build", "Empty the log when a player build starts.", UnityConsole.FlagClearOnBuild));
            body.Add(Switch("Clear on Recompile", "Empty the log when scripts compile.", UnityConsole.FlagClearOnRecompile));
            body.Add(Switch("Error Pause", "Pause Play Mode when an error is logged.", UnityConsole.FlagErrorPause));

            body.Add(Section("Display", null));
            body.Add(Switch("Group similar messages", "Messages that only differ by numbers, ids or hashes share a group.",
                () => LogCapture.Store.Grouping == GroupingMode.SimilarText,
                on => LogCapture.SetGrouping(on ? GroupingMode.SimilarText : GroupingMode.SameText)));
            body.Add(Switch("Compact rows", "One line per log, without the line it was logged from.", () => window.IsCompact, window.SetCompact));
            body.Add(Switch("Monospace font", "Messages in a fixed-width font, like code.", () => window.IsMonospace, window.SetMonospace));

            body.Add(Section("Activity chart", "How much time the chart above the list covers. The list and the counts always cover every log."));
            var windows = LoggerUi.Box("lg-limits");
            int currentWindow = TimelineWindow.Minutes;
            foreach (int minutes in TimelineWindow.Choices)
            {
                int captured = minutes;
                var button = LoggerUi.Pill(TimelineWindow.Label(minutes), () =>
                {
                    TimelineWindow.Minutes = captured;
                    foreach (var other in windows.Children())
                    {
                        other.EnableInClassList("lg-button--primary", other.userData is int value && value == captured);
                    }
                }, currentWindow == minutes ? "primary" : null, null, minutes == 0
                    ? "Every log of the session: the chart stretches as it grows, so an old burst shrinks"
                    : "Only the last " + TimelineWindow.Label(minutes) + ", at a steady scale");
                button.userData = minutes;
                windows.Add(button);
            }

            body.Add(windows);

            body.Add(Section("Explanations", "What known messages mean and how to fix them."));
            body.Add(Switch("Explanations from Orbiters", "Keep them up to date from Orbiters between releases; members signed in through Orbiters Toolkit also get the members' entries.",
                () => RemoteExplanations.Enabled, on => RemoteExplanations.Enabled = on));

            body.Add(Section("Timings", "Logged as they happen, with each package's part."));
            body.Add(Switch("Script reloads", "How long every reload takes, and which steps of it.",
                () => ReloadTimings.Enabled, on => ReloadTimings.Enabled = on));
            body.Add(Switch("Script reloads by package", "What each package costs at every reload. Turns on Unity's reload timing diagnostics, which leak a little editor memory per reload and flood the log with allocation warnings in Unity 2022.3: leave it off unless you are hunting a slow reload.",
                () => ReloadTimings.ByPackage, on => ReloadTimings.ByPackage = on));
            body.Add(Switch("Entering Play Mode", "From the Play button to the first frames, with each tool's avatar build steps.",
                () => PlayModeTimings.Enabled, on => PlayModeTimings.Enabled = on));
            body.Add(Switch("Avatar builds and uploads", "Each tool's build steps, the asset bundle and the upload (VRChat SDK).",
                () => EditorPrefs.GetBool(TimingSettings.AvatarUploadsPref, true), on => EditorPrefs.SetBool(TimingSettings.AvatarUploadsPref, on)));

            body.Add(Section("Beta", null));
            body.Add(Switch("Undo history timeline", "Your undo steps under the activity chart, with a Scene view snapshot of each. Click, drag or scroll it to go back and forth.",
                () => UndoHistory.Enabled, on => UndoHistory.Enabled = on));

            body.Add(Section("Memory", "The oldest logs go first when the limit is reached."));
            var limits = LoggerUi.Box("lg-limits");
            int current = LogCapture.Store.MaxOccurrences;
            foreach (int limit in LogLimits)
            {
                int captured = limit;
                var button = LoggerUi.Pill(LoggerUi.Compact(limit), () =>
                {
                    LogCapture.SetMaxLogs(captured);
                    foreach (var other in limits.Children())
                    {
                        other.EnableInClassList("lg-button--primary", other.userData is int value && value == captured);
                    }
                }, current == limit ? "primary" : null);
                button.userData = limit;
                limits.Add(button);
            }

            body.Add(limits);

            body.Add(Section("Unity's console", null));
            var tools = LoggerUi.Box("lg-popup__tools");
            tools.Add(LoggerUi.Pill("Open Unity's Console", () => EditorApplication.ExecuteMenuItem("Window/General/Console"), "ghost", LoggerGlyph.External));
            tools.Add(LoggerUi.Pill("Read it again", () =>
            {
                LogCapture.RebuildFromUnityConsole();
                editorWindow.Close();
            }, "ghost", LoggerGlyph.Reload, "Start over from what Unity's console holds (times to the second)"));
            body.Add(tools);
            body.Add(LoggerUi.Text("Mixer console " + PackageVersion() + " · Orbiters", "lg-popup__version"));
        }

        private static string PackageVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SettingsPopup).Assembly);
                return info != null ? info.version : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static VisualElement Section(string title, string detail)
        {
            var section = LoggerUi.Box("lg-popup__section");
            section.Add(LoggerUi.Text(title, "lg-popup__section-title"));
            if (!string.IsNullOrEmpty(detail))
            {
                section.Add(LoggerUi.Text(detail, "lg-popup__section-detail"));
            }

            return section;
        }

        private static VisualElement Switch(string title, string detail, int flag) =>
            Switch(title, detail, () => UnityConsole.HasFlag(flag), on => UnityConsole.SetFlag(flag, on));

        private static VisualElement Switch(string title, string detail, Func<bool> get, Action<bool> set)
        {
            var row = new Button();
            row.AddToClassList("lg-switch-row");
            var text = LoggerUi.Box("lg-switch-row__text", PickingMode.Ignore);
            text.Add(LoggerUi.Text(title, "lg-switch-row__title"));
            text.Add(LoggerUi.Text(detail, "lg-switch-row__detail"));
            row.Add(text);
            var track = LoggerUi.Box("lg-switch", PickingMode.Ignore);
            track.Add(LoggerUi.Box("lg-switch__knob", PickingMode.Ignore));
            row.Add(track);
            track.EnableInClassList("lg-switch--on", get());
            LoggerUi.Press(row, () =>
            {
                bool on = !get();
                set(on);
                track.EnableInClassList("lg-switch--on", get());
            });
            return row;
        }
    }
}
