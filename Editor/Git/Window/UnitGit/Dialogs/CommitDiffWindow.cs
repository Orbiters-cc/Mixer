using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>What a commit changed in one file, side by side, with the Log's diff tools (changes, search).</summary>
    internal sealed class UnitGitCommitDiffWindow : EditorWindow
    {
        [SerializeField] private string root, hash, shortHash, subject, path;
        private UnitGitDiffViewer viewer;
        private Label count;
        private string search = string.Empty;

        internal static void Show(string projectRoot, string fullHash, string shortHash, string subject, string file)
        {
            var window = GetWindow<UnitGitCommitDiffWindow>();
            window.root = projectRoot;
            window.hash = fullHash;
            window.shortHash = shortHash;
            window.subject = subject;
            window.path = file;
            window.titleContent = new GUIContent(System.IO.Path.GetFileName(file) + " • " + shortHash);
            window.minSize = new Vector2(640, 360);
            window.Build();
            window.Show();
        }

        private void CreateGUI()
        {
            if (!string.IsNullOrEmpty(hash)) Build();
        }

        private void Build()
        {
            var ui = rootVisualElement;
            ui.Clear();
            ui.AddToClassList("unitgit-root");
            foreach (string sheetPath in new[] { "Packages/orbiters.mixer/Editor/Git/Styles/unitgit-shell.uss", "Packages/orbiters.mixer/Editor/Git/Styles/unitgit.uss" })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(sheetPath);
                if (sheet != null && !ui.styleSheets.Contains(sheet)) ui.styleSheets.Add(sheet);
                Orbiters.Mixer.Editor.MixerAccent.Apply(ui);
            }

            var header = new VisualElement();
            header.AddToClassList("ug-diffwin__header");
            var texts = new VisualElement();
            texts.AddToClassList("ug-diffwin__texts");
            texts.Add(UnitGitUi.Text(System.IO.Path.GetFileName(path), "ug-diffwin__title"));
            texts.Add(UnitGitUi.Text(path, "ug-diffwin__path"));
            header.Add(texts);
            header.Add(UnitGitUi.Spacer());
            var commit = UnitGitUi.Chip(shortHash, null, subject);
            commit.AddToClassList("ug-hash-chip");
            header.Add(commit);
            header.Add(UnitGitUi.Text(subject, "ug-diffwin__subject"));
            ui.Add(header);

            var toolbar = new VisualElement();
            toolbar.AddToClassList("ug-toolbar");
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.PreviousDifference, "Previous change (Shift+F7) or search match", () => Step(-1)));
            toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.NextDifference, "Next change (F7) or search match", () => Step(1)));
            toolbar.Add(UnitGitUi.Search(search, "Search", value =>
            {
                search = value ?? string.Empty;
                viewer?.Search(search, true);
                UpdateCount();
            }, "ug-diff-search"));
            toolbar.Add(UnitGitUi.Spacer());
            toolbar.Add(UnitGitDiffViewer.BuildViewOptions(() => viewer, Load));
            count = UnitGitUi.Chip("Loading…");
            toolbar.Add(count);
            ui.Add(toolbar);

            viewer = new UnitGitDiffViewer();
            viewer.List.focusable = true;
            viewer.List.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.F7) return;
                Step(evt.shiftKey ? -1 : 1);
                evt.StopPropagation();
            });
            ui.Add(viewer);
            Load();
        }

        private void Load()
        {
            string projectRoot = root, commit = hash, file = path;
            var target = viewer;
            var whitespace = UnitGitDiffViewer.Whitespace;
            Task.Run(() =>
                {
                    var diff = new UnitGitService(projectRoot).GetCommitFileDiff(commit, file, whitespace);
                    UnitGitWindow.PrepareDiff(diff);
                    return diff;
                })
                .ContinueWith(task =>
                {
                    if (this == null || viewer != target) return;
                    var diff = task.Status == TaskStatus.RanToCompletion ? task.Result : new UnitGitDiff();
                    if (task.Exception != null) diff.Lines.Add(new UnitGitDiffLine { Right = "Could not load the diff: " + task.Exception.GetBaseException().Message });
                    viewer.SetDiff(diff);
                    viewer.schedule.Execute(() => { viewer.MoveChange(1); UpdateCount(); }).StartingIn(30);
                    UpdateCount();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Step(int step)
        {
            if (viewer == null) return;
            if (!string.IsNullOrWhiteSpace(search)) viewer.MoveMatch(step);
            else viewer.MoveChange(step);
            UpdateCount();
        }

        private void UpdateCount()
        {
            if (count == null || viewer == null) return;
            if (!string.IsNullOrWhiteSpace(search))
                count.text = viewer.MatchCount == 0 ? "No match" : viewer.MatchIndex + 1 + " of " + viewer.MatchCount;
            else
                count.text = viewer.DifferenceCount == 0 ? "No change"
                    : viewer.CurrentChange >= 0 ? viewer.CurrentChange + 1 + " of " + viewer.DifferenceCount + (viewer.DifferenceCount == 1 ? " change" : " changes")
                    : viewer.DifferenceCount + (viewer.DifferenceCount == 1 ? " change" : " changes");
        }
    }
}
