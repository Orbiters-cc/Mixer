using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private readonly UnitGitLatestRequest<UnitGitDiff> diffRead = new UnitGitLatestRequest<UnitGitDiff>();
        private UnitGitDiff loadedDiff;
        private string requestedDiffPath;
        private UnitGitDiffViewer diffViewer;
        private Label diffCountLabel;
        // A newly selected file opens at its first change; a refresh of the same file keeps the scroll.
        private string jumpedToFirstChange;
        private Font diffFont;
        private const string DiffModePref = "Orbiters.UnitGit.Diff.Mode";

        internal enum DiffMode { Scene, Model, Text }

        // Text assets Unity writes as YAML: shown object by object in the Scene view.
        internal static bool IsUnityYamlPath(string path)
        {
            switch (Path.GetExtension(path ?? string.Empty).ToLowerInvariant())
            {
                case ".unity": case ".prefab": case ".asset": case ".mat": case ".controller": case ".anim": case ".overridecontroller": case ".mask":
                    return true;
                default:
                    return false;
            }
        }

        // The views a file offers: the Scene view for Unity YAML, 3D for models and prefabs, and always Text.
        internal static List<DiffMode> ModesFor(string path)
        {
            var modes = new List<DiffMode>();
            if (IsUnityYamlPath(path)) modes.Add(DiffMode.Scene);
            if (Semantic.ModelVersions.IsPreviewable(path)) modes.Add(DiffMode.Model);
            modes.Add(DiffMode.Text);
            return modes;
        }

        // The last view chosen for this kind of file, or the richest one it offers.
        internal static DiffMode ModeFor(string path)
        {
            var modes = ModesFor(path);
            var saved = (DiffMode)EditorPrefs.GetInt(DiffModePref + Path.GetExtension(path ?? string.Empty).ToLowerInvariant(), (int)modes[0]);
            return modes.Contains(saved) ? saved : modes[0];
        }

        internal static void RememberMode(string path, DiffMode mode)
        {
            EditorPrefs.SetInt(DiffModePref + Path.GetExtension(path ?? string.Empty).ToLowerInvariant(), (int)mode);
        }

        internal static VisualElement BuildModeSwitch(IList<DiffMode> modes, DiffMode current, Action<DiffMode> changed)
        {
            var group = new VisualElement();
            group.AddToClassList("ugs-switch");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.mixer/Editor/Git/Styles/unitgit-semantic.uss");
            if (sheet != null) group.styleSheets.Add(sheet);
            var selected = current;
            foreach (var option in modes)
            {
                var value = option;
                var button = new Button
                {
                    tooltip = value == DiffMode.Scene ? "Objects, components and values that changed, as the Inspector shows them (beta)."
                        : value == DiffMode.Model ? "Both versions in 3D, with what changed highlighted (beta)."
                        : "The raw lines of the file."
                };
                button.AddToClassList("ugs-switch__option");
                button.EnableInClassList("ugs-switch__option--on", current == value);
                var label = new Label(value == DiffMode.Scene ? "Scene view" : value == DiffMode.Model ? "3D" : "Text");
                label.pickingMode = PickingMode.Ignore;
                button.Add(label);
                if (value != DiffMode.Text)
                {
                    var beta = new Label("BETA");
                    beta.AddToClassList("ugs-beta");
                    beta.pickingMode = PickingMode.Ignore;
                    button.Add(beta);
                }
                // Switches on press: the highlight moves at once, the content follows.
                button.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    button.AddToClassList("ugs-switch__option--pressed");
                    foreach (var other in group.Children()) other.EnableInClassList("ugs-switch__option--on", other == button);
                    if (selected == value) return;
                    selected = value;
                    group.schedule.Execute(() => changed(value)).StartingIn(60);
                }, TrickleDown.TrickleDown);
                button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("ugs-switch__option--pressed"));
                button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("ugs-switch__option--pressed"));
                group.Add(button);
            }
            return group;
        }

        private VisualElement BuildLocalModelView(UnitGitStatusEntry change)
        {
            var view = new ModelCompareView("Repository", "Current version");
            string root = gitService.ProjectRoot;
            string path = change.Path;
            string full = Path.Combine(root, path);
            view.Load(LocalKey("model", change), root, path,
                destination => !change.IsUntracked && new UnitGitService(root).WriteBlob("HEAD:" + path, destination),
                destination =>
                {
                    if (!File.Exists(full)) return false;
                    File.Copy(full, destination, true);
                    return true;
                },
                null);
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            scroll.Add(view);
            return scroll;
        }

        // These exact versions: the file on disk and the commit HEAD points to.
        private string LocalKey(string kind, UnitGitStatusEntry change)
        {
            string root = gitService.ProjectRoot;
            var file = new FileInfo(Path.Combine(root, change.Path));
            // Where Git keeps HEAD's log (elsewhere in a linked worktree): it changes with every commit and checkout.
            string head = UnitGitPaths.GitPath(root, "logs/HEAD");
            return kind + "|local|" + root + "|" + change.Path + "|" + (file.Exists ? file.LastWriteTimeUtc.Ticks + ":" + file.Length : "deleted") +
                "|" + (head != null && File.Exists(head) ? File.GetLastWriteTimeUtc(head).Ticks : 0);
        }

        private VisualElement BuildLocalSceneView(UnitGitStatusEntry change)
        {
            var view = new SemanticDiffView();
            string root = gitService.ProjectRoot;
            string path = change.Path;
            string full = Path.Combine(root, path);
            view.Load(LocalKey("scene", change),
                () =>
                {
                    if (change.IsUntracked) return string.Empty;
                    var result = new UnitGitService(root).RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "show", "HEAD:" + path);
                    return result.Success ? result.StandardOutput : string.Empty;
                },
                () => File.Exists(full) ? File.ReadAllText(full) : string.Empty,
                "Repository", "Current version", path);
            return view;
        }

        private VisualElement BuildDiffViewerPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-diff-pane");
            UnitGitStatusEntry change = GetSelectedChange();
            string path = change != null ? change.Path : string.Empty;
            if (!string.Equals(requestedDiffPath, path, StringComparison.Ordinal))
            {
                loadedDiff = null;
                RequestLocalDiff(change);
            }
            diffViewer = null;
            diffCountLabel = null;
            if (change == null)
            {
                pane.Add(BuildDiffPlaceholder("Select a file to see its changes"));
                return pane;
            }

            var modes = ModesFor(path);
            var mode = ModeFor(path);
            pane.Add(BuildDiffToolbar(change, modes, mode));
            if (mode != DiffMode.Text)
            {
                pane.Add(mode == DiffMode.Scene ? BuildLocalSceneView(change) : BuildLocalModelView(change));
                return pane;
            }
            if (loadedDiff == null)
            {
                pane.Add(BuildDiffPlaceholder("Loading the diff…", spinner: true));
                return pane;
            }
            diffViewer = new UnitGitDiffViewer { CanRevert = true };
            diffViewer.RevertRequested += block => RevertLocalBlock(change.Path, block);
            diffViewer.SetDiff(loadedDiff);
            diffViewer.Search(diffSearch, false);
            JumpToFirstChange(path);
            diffViewer.List.focusable = true;
            diffViewer.List.RegisterCallback<KeyDownEvent>(OnDiffKeyDown);
            pane.Add(diffViewer);
            UpdateDiffCount();
            return pane;
        }

        private VisualElement BuildDiffToolbar(UnitGitStatusEntry change, IList<DiffMode> modes, DiffMode mode)
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("ug-toolbar");
            toolbar.AddToClassList("unitgit-diff-toolbar");
            toolbar.Add(BuildProjectFileIcon(change.Path, "ug-diff-file__icon"));
            var name = UnitGitUi.Text(GetFileLeaf(change.Path), "ug-diff-file__name");
            name.AddToClassList("ug-status--" + StatusKey(change));
            toolbar.Add(name);
            toolbar.Add(UnitGitUi.Text(GetDirectoryLabel(change.Path), "ug-diff-file__path"));
            toolbar.Add(UnitGitUi.Spacer());
            if (modes.Count > 1)
            {
                toolbar.Add(BuildModeSwitch(modes, mode, value =>
                {
                    RememberMode(change.Path, value);
                    RebuildLocalDiffPane();
                }));
            }
            if (mode == DiffMode.Text)
            {
                toolbar.Add(UnitGitDiffViewer.BuildViewOptions(() => diffViewer, ReloadLocalDiff));
                toolbar.Add(UnitGitUi.Separator());
                toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.PreviousDifference, "Previous change (Shift+F7) or search match", () => StepDiff(-1)));
                toolbar.Add(UnitGitUi.Icon(UnitGitIconKind.NextDifference, "Next change (F7) or search match; after the last one, the next file", () => StepDiff(1)));
                var search = UnitGitUi.Search(diffSearch, "Search", value =>
                {
                    diffSearch = value ?? string.Empty;
                    diffSearchMatchIndex = 0;
                    UpdateDiffSearch(true);
                }, "ug-diff-search");
                diffSearchField = search;
                toolbar.Add(search);
                diffCountLabel = UnitGitUi.Chip(loadedDiff == null ? "…" : string.Empty, null);
                diffCountLabel.AddToClassList("ug-diff-count");
                toolbar.Add(diffCountLabel);
            }
            return toolbar;
        }

        private VisualElement BuildDiffPlaceholder(string text, bool spinner = false)
        {
            var placeholder = new VisualElement();
            placeholder.AddToClassList("ug-diff-placeholder");
            if (spinner) placeholder.Add(UnitGitUi.Spinner());
            else
            {
                var icon = new UnitGitIconElement(UnitGitIconKind.Diff);
                icon.AddToClassList("ug-diff-placeholder__icon");
                placeholder.Add(icon);
            }
            placeholder.Add(UnitGitUi.Text(text, "ug-diff-placeholder__text"));
            return placeholder;
        }

        private void JumpToFirstChange(string path)
        {
            if (diffViewer == null || loadedDiff == null || loadedDiff.Lines.Count == 0 || jumpedToFirstChange == path) return;
            jumpedToFirstChange = path;
            var viewer = diffViewer;
            viewer.schedule.Execute(() =>
            {
                viewer.MoveChange(1);
                UpdateDiffCount();
            }).StartingIn(30);
        }

        private void UpdateDiffCount()
        {
            if (diffCountLabel == null) return;
            if (diffViewer == null) { diffCountLabel.text = "…"; return; }
            if (!string.IsNullOrWhiteSpace(diffSearch))
                diffCountLabel.text = diffViewer.MatchCount == 0 ? "No match" : diffViewer.MatchIndex + 1 + " of " + diffViewer.MatchCount;
            else
                diffCountLabel.text = diffViewer.DifferenceCount == 0 ? "No change"
                    : diffViewer.CurrentChange >= 0 ? diffViewer.CurrentChange + 1 + " of " + diffViewer.DifferenceCount + (diffViewer.DifferenceCount == 1 ? " change" : " changes")
                    : diffViewer.DifferenceCount + (diffViewer.DifferenceCount == 1 ? " change" : " changes");
        }

        // Arrows: the search matches while searching, the changes otherwise; past the last change, the next file (as F7 does
        // in JetBrains IDEs).
        private void StepDiff(int step)
        {
            if (diffViewer == null) return;
            if (!string.IsNullOrWhiteSpace(diffSearch)) MoveDiffSearchMatch(step);
            else if (!diffViewer.MoveChange(step) && !SelectAdjacentChange(step))
                ShowToast(step > 0 ? "That was the last change" : "That was the first change");
            UpdateDiffCount();
        }

        // The whitespace option changed: the same file is read again and opens at its first change.
        private void ReloadLocalDiff()
        {
            jumpedToFirstChange = null;
            requestedDiffPath = null;
            RequestLocalDiff(GetSelectedChange());
        }

        // "»": the change goes back to the left side's lines in the file on disk; the notification can undo it.
        private void RevertLocalBlock(string path, UnitGitDiffBlock block)
        {
            if (string.IsNullOrEmpty(path) || block == null || diffViewer == null) return;
            string full = Path.Combine(gitService.ProjectRoot, path);
            GitCommandResult result;
            byte[] before = null, after = null;
            try
            {
                result = UnitGitBlockRevert.Apply(full, block.RightStart, block.Right, block.Left, diffViewer.Diff.LeftEndsWithoutNewline, out before, out after);
            }
            catch (Exception ex)
            {
                result = new GitCommandResult { ExitCode = 1, StandardError = ex.Message };
            }
            if (!result.Success)
            {
                ShowToast(result.Message, error: true);
                ReloadLocalDiff();
                return;
            }
            AppendConsole("revert change", path + ": lines " + block.RightStart + "-" + (block.RightStart + Math.Max(1, block.Right.Count) - 1) +
                                           " put back as the left side has them");
            ImportIfProjectAsset(path);
            ShowToast("Reverted a change in ‘" + GetFileLeaf(path) + "’", false, "Undo", () =>
            {
                var undone = UnitGitBlockRevert.Undo(full, before, after);
                if (!undone.Success)
                {
                    ShowToast(undone.Message, error: true);
                    return;
                }
                AppendConsole("revert change", path + ": undone");
                ImportIfProjectAsset(path);
                RefreshAfterLocalEdit();
            });
            RefreshAfterLocalEdit();
        }

        private void RefreshAfterLocalEdit()
        {
            var change = GetSelectedChange();
            if (change != null) RequestLocalDiff(change);
            RefreshSnapshot();
        }

        // Unity notices the edit at once (a script recompiles, an asset reimports) instead of on the next focus.
        private static void ImportIfProjectAsset(string path)
        {
            if (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        private void OnDiffKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.F7)
            {
                StepDiff(evt.shiftKey ? -1 : 1);
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.F && (evt.ctrlKey || evt.commandKey))
            {
                diffSearchField?.Focus();
                evt.StopPropagation();
            }
        }

        private void RequestLocalDiff(UnitGitStatusEntry change)
        {
            string path = change != null ? change.Path : string.Empty;
            bool changedPath = requestedDiffPath != path;
            requestedDiffPath = path;
            if (changedPath)
                loadedDiff = null;
            var previous = loadedDiff;
            string root = gitService.ProjectRoot;
            var whitespace = UnitGitDiffViewer.Whitespace;
            diffRead.Request(stale =>
            {
                var next = new UnitGitService(root) { ReadSuperseded = stale }.GetFileDiff(change, whitespace);
                PrepareDiff(next);
                if (previous != null && SameDiff(previous, next))
                    return previous;
                return next;
            });
            EnsureEditorUpdatePump();
        }

        // Tabs show as four spaces; the exact text stays for copying and for putting a change back.
        internal static void PrepareDiff(UnitGitDiff diff)
        {
            foreach (var line in diff.Lines)
            {
                string left = line.Left ?? string.Empty, right = line.Right ?? string.Empty;
                if (left.IndexOf('\t') >= 0)
                {
                    line.RawLeft = left;
                    left = left.Replace("\t", "    ");
                }
                if (right.IndexOf('\t') >= 0)
                {
                    line.RawRight = right;
                    right = right.Replace("\t", "    ");
                }
                line.Left = left;
                line.Right = right;
                diff.MaxLineLength = Math.Max(diff.MaxLineLength, Math.Max(left.Length, right.Length));
            }
        }

        internal static bool SameDiff(UnitGitDiff a, UnitGitDiff b)
        {
            if (a.Path != b.Path || a.LeftTitle != b.LeftTitle || a.RightTitle != b.RightTitle ||
                a.DifferenceCount != b.DifferenceCount || a.Lines.Count != b.Lines.Count)
                return false;
            for (int i = 0; i < a.Lines.Count; i++)
                if (a.Lines[i].Left != b.Lines[i].Left || a.Lines[i].Right != b.Lines[i].Right || a.Lines[i].Kind != b.Lines[i].Kind)
                    return false;
            return true;
        }

        private void PollDiffRead()
        {
            if (!diffRead.Poll(out UnitGitDiff diff, out Exception error))
                return;
            if (ReferenceEquals(loadedDiff, diff) && error == null)
                return;
            loadedDiff = diff ?? new UnitGitDiff();
            if (error != null)
                loadedDiff.Lines.Add(new UnitGitDiffLine { Right = "Could not load diff: " + error.Message });
            if (diffViewer != null)
            {
                diffViewer.SetDiff(loadedDiff);
                JumpToFirstChange(loadedDiff.Path);
                UpdateDiffCount();
                return;
            }
            if (activeTab == UnitGitTab.LocalChanges)
            {
                // The Scene view reads the file itself: the text diff arriving changes nothing there.
                if (localDiffPaneRoot != null && (localDiffPaneRoot.Q<SemanticDiffView>() != null || localDiffPaneRoot.Q<ModelCompareView>() != null))
                    return;
                RememberScrollOffsets();
                RebuildLocalDiffPane();
                RestoreScrollOffsets();
            }
        }

        private void UpdateDiffSearch(bool scrollToMatch)
        {
            diffViewer?.Search(diffSearch, scrollToMatch);
            UpdateDiffCount();
        }

        private void PreviousDiffSearchMatch() { MoveDiffSearchMatch(-1); }
        private void NextDiffSearchMatch() { MoveDiffSearchMatch(1); }
        private void MoveDiffSearchMatch(int step)
        {
            if (diffViewer == null || diffViewer.MatchCount == 0)
            {
                diffSearchField?.Focus();
                return;
            }
            diffViewer.MoveMatch(step);
            diffSearchMatchIndex = diffViewer.MatchIndex;
            UpdateDiffCount();
        }
    }
}
