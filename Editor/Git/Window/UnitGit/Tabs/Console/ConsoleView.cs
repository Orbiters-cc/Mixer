using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // Every Git command Mixer Git ran and what it answered, newest at the bottom like a terminal: commands in blue,
    // failures in red, the rest quiet. New lines arrive in place, following the end unless the user scrolled up.
    internal sealed partial class UnitGitWindow
    {
        private readonly List<string> consoleView = new List<string>();
        private ListView consoleList;
        private string consoleFilter = string.Empty;

        private VisualElement BuildConsoleBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-console-full");

            var toolbar = new VisualElement();
            toolbar.AddToClassList("ug-toolbar");
            toolbar.Add(UnitGitUi.Search(consoleFilter, "Filter output", value =>
            {
                consoleFilter = value ?? string.Empty;
                RefreshConsole(scrollToEnd: true);
            }, "ug-toolbar__search"));
            toolbar.Add(UnitGitUi.Spacer());
            toolbar.Add(UnitGitUi.Text(snapshot.ProjectRoot, "ug-console-root"));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.Copy, "Copy the output", () =>
            {
                EditorGUIUtility.systemCopyBuffer = string.Join("\n", Enumerable.Reverse(consoleLines));
                ShowToast("Copied " + consoleLines.Count + " lines");
            }));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.Delete, "Clear", () =>
            {
                consoleLines.Clear();
                RefreshConsole(scrollToEnd: true);
            }));
            workspace.Add(toolbar);

            consoleList = new ListView
            {
                itemsSource = consoleView,
                fixedItemHeight = 20,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                makeItem = MakeConsoleRow,
                bindItem = (row, index) => BindConsoleRow(row, consoleView[index]),
            };
            consoleList.AddToClassList("ug-console");
            consoleList.style.flexGrow = 1;
            consoleList.Q<ScrollView>().name = "unitgit-console-scroll";
            workspace.Add(consoleList);
            RefreshConsole(scrollToEnd: true);
            return workspace;
        }

        private void RefreshConsole(bool scrollToEnd)
        {
            if (consoleList == null) return;
            var scroll = consoleList.Q<ScrollView>();
            bool atEnd = scrollToEnd || scroll == null || scroll.verticalScroller.value >= scroll.verticalScroller.highValue - 4;
            consoleView.Clear();
            string term = consoleFilter.Trim();
            for (int i = consoleLines.Count - 1; i >= 0; i--)
                if (term.Length == 0 || consoleLines[i].IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    consoleView.Add(consoleLines[i]);
            consoleList.itemsSource = consoleView;
            consoleList.RefreshItems();
            if (atEnd && consoleView.Count > 0) consoleList.schedule.Execute(() => consoleList.ScrollToItem(consoleView.Count - 1));
        }

        private static VisualElement MakeConsoleRow()
        {
            var row = new VisualElement();
            row.AddToClassList("ug-console-row");
            row.Add(UnitGitUi.Text(string.Empty, "ug-console-row__time"));
            row.Add(UnitGitUi.Text(string.Empty, "ug-console-row__label"));
            var message = UnitGitUi.Text(string.Empty, "ug-console-row__message");
            message.selection.isSelectable = true;
            row.Add(message);
            return row;
        }

        // "HH:mm:ss label - message"
        private static void BindConsoleRow(VisualElement row, string line)
        {
            string time = line.Length >= 8 ? line.Substring(0, 8) : string.Empty;
            string rest = line.Length > 9 ? line.Substring(9) : line;
            int dash = rest.IndexOf(" - ", StringComparison.Ordinal);
            string label = dash > 0 ? rest.Substring(0, dash) : string.Empty;
            string message = dash > 0 ? rest.Substring(dash + 3) : rest;
            ((Label)row[0]).text = time;
            ((Label)row[1]).text = label;
            var text = (Label)row[2];
            text.text = message.Length > 600 ? message.Substring(0, 600) + " …" : message;
            row.tooltip = message.Length > 120 ? message : null;
            bool command = message.StartsWith(">", StringComparison.Ordinal);
            bool failure = message.StartsWith("error", StringComparison.OrdinalIgnoreCase) || message.StartsWith("timed out", StringComparison.Ordinal) ||
                           (message.StartsWith("exit:", StringComparison.Ordinal) && message.Trim() != "exit: 0");
            bool quiet = message.StartsWith("cwd:", StringComparison.Ordinal) || message.Trim() == "exit: 0";
            row.EnableInClassList("ug-console-row--command", command);
            row.EnableInClassList("ug-console-row--error", failure);
            row.EnableInClassList("ug-console-row--quiet", quiet);
            row.EnableInClassList("ug-console-row--ok", message.StartsWith("ok", StringComparison.Ordinal));
        }
    }
}
