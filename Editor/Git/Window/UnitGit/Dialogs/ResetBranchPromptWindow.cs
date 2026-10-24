using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitResetBranchPromptWindow : EditorWindow
    {
        private Action<UnitGitResetMode> onSubmit;
        private UnitGitResetMode selectedMode = UnitGitResetMode.Mixed;
        private string branchName = string.Empty;
        private string projectRoot = string.Empty;
        private UnitGitCommit commit;

        public static void Open(string branchName, string projectRoot, UnitGitCommit commit, Action<UnitGitResetMode> onSubmit)
        {
            var window = CreateInstance<UnitGitResetBranchPromptWindow>();
            window.titleContent = new GUIContent("Git Reset");
            window.branchName = branchName ?? string.Empty;
            window.projectRoot = projectRoot ?? string.Empty;
            window.commit = commit;
            window.onSubmit = onSubmit;
            window.minSize = new Vector2(620f, 438f);
            window.maxSize = new Vector2(760f, 520f);
            window.ShowUtility();
        }

        private readonly Dictionary<UnitGitResetMode, VisualElement> modeCards = new Dictionary<UnitGitResetMode, VisualElement>();
        private Button resetButton;

        private void CreateGUI()
        {
            var body = UnitGitDialog.Setup(this, "Reset \u2018" + GetBranchLabel() + "\u2019 to here", GetCommitLabel());
            body.Add(BuildModeOption(UnitGitResetMode.Soft, "Soft", "Files stay as they are; the differences are included in the next commit."));
            body.Add(BuildModeOption(UnitGitResetMode.Mixed, "Mixed", "Files stay as they are; the differences are left out of the next commit."));
            body.Add(BuildModeOption(UnitGitResetMode.Keep, "Keep", "Files go back to this commit, but your local changes are kept."));
            body.Add(BuildModeOption(UnitGitResetMode.Hard, "Hard", "Files go back to this commit. Local changes are lost.", danger: true));

            var actions = UnitGitDialog.Actions(this);
            actions.Add(UnitGitUi.Pill("Cancel", Close, "ghost"));
            resetButton = UnitGitUi.Pill("Reset", () =>
            {
                onSubmit?.Invoke(selectedMode);
                Close();
            }, "primary", UnitGitIconKind.Rollback);
            actions.Add(resetButton);
            SelectMode(UnitGitResetMode.Mixed);
        }

        private VisualElement BuildModeOption(UnitGitResetMode mode, string title, string description, bool danger = false)
        {
            var option = new VisualElement();
            option.AddToClassList("ug-option");
            if (danger) option.AddToClassList("ug-option--danger");
            var dot = new VisualElement();
            dot.AddToClassList("ug-option__dot");
            option.Add(dot);
            var texts = new VisualElement();
            texts.AddToClassList("ug-option__texts");
            texts.Add(UnitGitUi.Text(title, "ug-option__title"));
            texts.Add(UnitGitUi.Text(description, "ug-option__text"));
            option.Add(texts);
            option.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button == 0) SelectMode(mode);
            });
            modeCards[mode] = option;
            return option;
        }

        private void SelectMode(UnitGitResetMode mode)
        {
            selectedMode = mode;
            foreach (var item in modeCards) item.Value.EnableInClassList("ug-option--on", item.Key == mode);
            if (resetButton == null) return;
            resetButton.EnableInClassList("ug-button--primary", mode != UnitGitResetMode.Hard);
            resetButton.EnableInClassList("ug-button--danger", mode == UnitGitResetMode.Hard);
            UnitGitUi.SetText(resetButton, mode == UnitGitResetMode.Hard ? "Reset and discard" : "Reset");
        }

        private string GetBranchLabel()
        {
            return string.IsNullOrWhiteSpace(branchName) ? "(detached HEAD)" : branchName.Trim();
        }

        private string GetRepositoryName()
        {
            string normalized = string.IsNullOrWhiteSpace(projectRoot)
                ? string.Empty
                : projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = string.IsNullOrWhiteSpace(normalized) ? string.Empty : Path.GetFileName(normalized);
            return string.IsNullOrWhiteSpace(name) ? "repository" : name;
        }

        private string GetCommitLabel()
        {
            if (commit == null)
            {
                return "(no commit)";
            }

            string subject = string.IsNullOrWhiteSpace(commit.Subject) ? "(no subject)" : Shorten(commit.Subject.Trim(), 58);
            string author = string.IsNullOrWhiteSpace(commit.AuthorName) ? string.Empty : " by " + commit.AuthorName.Trim();
            return commit.ShortHash + "  \u2022  " + subject + author;
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }
    }
}
