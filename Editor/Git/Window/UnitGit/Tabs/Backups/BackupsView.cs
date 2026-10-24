using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private const string BackupStyleSheetPath = "Packages/orbiters.mixer/Editor/Git/Styles/unitgit-backups.uss";
        // The backup just made or checked: its row flashes once so the eye finds it.
        private string highlightedBackup;

        private VisualElement BuildBackupsBody()
        {
            var page = new ScrollView();
            page.AddToClassList("ugb");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(BackupStyleSheetPath);
            if (sheet != null) page.styleSheets.Add(sheet);
            var column = new VisualElement();
            column.AddToClassList("ugb-column");
            page.Add(column);

            string root = gitService.ProjectRoot;
            string folder = UnitGitBackups.Folder(root);
            var backups = UnitGitBackups.List(folder);

            // Hero: what a backup is, where it goes, the main action.
            var hero = new VisualElement();
            hero.AddToClassList("ugb-hero");
            column.Add(hero);
            var title = new Label("Backups");
            title.AddToClassList("ugb-hero__title");
            hero.Add(title);
            var text = new Label("A backup is one file with every commit of every branch. Keep it on another drive or in a synced folder (OneDrive, Google Drive, Dropbox): if this disk fails or the project is deleted, you can bring the whole history back.");
            text.AddToClassList("ugb-hero__text");
            hero.Add(text);

            var place = new VisualElement();
            place.AddToClassList("ugb-folder");
            hero.Add(place);
            var folderLabel = new Label(folder) { tooltip = folder };
            folderLabel.AddToClassList("ugb-folder__path");
            place.Add(folderLabel);
            place.Add(BackupButton("Change…", "ugb-button", "Choose where backups go.", () =>
            {
                string chosen = EditorUtility.OpenFolderPanel("Where should Mixer Git keep backups?", Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder), string.Empty);
                if (string.IsNullOrEmpty(chosen)) return;
                if (UnitGitBackups.IsInsideProject(chosen, root) &&
                    !EditorUtility.DisplayDialog("Mixer Git", "This folder is inside the project: a backup there is lost together with the project. Use it anyway?", "Use it", "Cancel"))
                    return;
                UnitGitSettings.BackupFolder = chosen;
                RebuildContent();
            }));
            place.Add(BackupButton("Open", "ugb-button", "Open this folder.", () =>
            {
                Directory.CreateDirectory(folder);
                EditorUtility.RevealInFinder(folder);
            }));

            if (UnitGitBackups.IsInsideProject(folder, root))
                hero.Add(BackupNotice("This folder is inside the project, so a backup would be lost together with it. Choose a folder outside the project.", true));
            if (snapshot.Changes.Count > 0)
                hero.Add(BackupNotice(snapshot.Changes.Count + " change" + (snapshot.Changes.Count == 1 ? " isn't" : "s aren't") + " committed yet and won't be in the backup. Commit them first to include them.", false));
            if (UsesLfs(root))
                hero.Add(BackupNotice("This project stores some files with Git LFS: backups hold their pointers, not the files themselves.", true));

            var options = new VisualElement();
            options.AddToClassList("ugb-options");
            hero.Add(options);
            var automatic = new Toggle { text = "Back up automatically once a day", value = UnitGitSettings.AutomaticBackups };
            automatic.tooltip = "When Unity opens this project and the last backup is a day old, Mixer Git makes a new one in the background if the history changed.";
            automatic.AddToClassList("ugb-toggle");
            automatic.RegisterValueChangedCallback(evt => UnitGitSettings.AutomaticBackups = evt.newValue);
            options.Add(automatic);
            var keep = new IntegerField("Keep the last") { value = UnitGitSettings.BackupsToKeep, isDelayed = true };
            keep.tooltip = "This project's older backups in this folder are deleted after a new one is made. Other files there are never touched.";
            keep.AddToClassList("ugb-keep");
            keep.RegisterValueChangedCallback(evt =>
            {
                UnitGitSettings.BackupsToKeep = Mathf.Clamp(evt.newValue, 1, 200);
                keep.SetValueWithoutNotify(UnitGitSettings.BackupsToKeep);
            });
            options.Add(keep);

            var now = BackupButton(busy ? "Working…" : "Back up now", "ugb-button--primary", "Save every commit of every branch to a new backup file, then read it back to check it.", () =>
            {
                RunAction("Back up project", () => UnitGitBackups.Create(gitService, folder, UnitGitSettings.BackupsToKeep), result =>
                {
                    if (result != null && result.Success) highlightedBackup = UnitGitBackups.List(folder).FirstOrDefault()?.Path;
                    RebuildContent();
                });
                // Show the moving bar right away.
                RebuildContent();
            });
            now.AddToClassList("ugb-now");
            now.SetEnabled(!busy);
            hero.Add(now);
            if (busy)
            {
                // An indeterminate bar while Git writes and reads the backup back.
                var track = new VisualElement();
                track.AddToClassList("ugb-progress");
                var fill = new VisualElement();
                fill.AddToClassList("ugb-progress__fill");
                track.Add(fill);
                hero.Add(track);
                fill.schedule.Execute(() => fill.ToggleInClassList("ugb-progress__fill--end")).Every(700);
            }

            // The list.
            var listHeader = new VisualElement();
            listHeader.AddToClassList("ugb-list-header");
            column.Add(listHeader);
            var listTitle = new Label("In this folder");
            listTitle.AddToClassList("ugb-list-title");
            listHeader.Add(listTitle);
            var listCount = new Label(backups.Count == 0 ? "none yet" : backups.Count + " backup" + (backups.Count == 1 ? "" : "s") + " · " + UnitGitService.FormatBytes(backups.Sum(b => b.Bytes)));
            listCount.AddToClassList("ugb-list-count");
            listHeader.Add(listCount);

            if (backups.Count == 0)
            {
                var empty = new Label("No backup here yet. Back up now to make the first one.");
                empty.AddToClassList("ugb-empty");
                column.Add(empty);
            }
            for (int i = 0; i < backups.Count; i++) column.Add(BackupRow(backups[i], i, root));
            return page;
        }

        private VisualElement BackupRow(UnitGitBackup backup, int index, string root)
        {
            var row = new VisualElement();
            row.AddToClassList("ugb-row");
            row.AddToClassList("ugb-row--enter");
            row.schedule.Execute(() => row.RemoveFromClassList("ugb-row--enter")).StartingIn(20 + Math.Min(index, 10) * 30);
            if (string.Equals(backup.Path, highlightedBackup, StringComparison.OrdinalIgnoreCase))
            {
                row.AddToClassList("ugb-row--flash");
                row.schedule.Execute(() => row.RemoveFromClassList("ugb-row--flash")).StartingIn(900);
            }

            var state = new VisualElement();
            state.AddToClassList("ugb-state");
            state.AddToClassList(backup.Healthy == true ? "ugb-state--ok" : backup.Healthy == false ? "ugb-state--bad" : "ugb-state--unknown");
            state.tooltip = backup.Healthy == true ? "Checked: every commit reads back." : backup.Healthy == false ? "Damaged: " + backup.Problem : "Not checked in this session.";
            row.Add(state);

            var texts = new VisualElement();
            texts.AddToClassList("ugb-row__texts");
            row.Add(texts);
            var date = new Label(backup.Created.ToString("dddd d MMMM yyyy · HH:mm", CultureInfo.CurrentCulture));
            date.AddToClassList("ugb-row__date");
            texts.Add(date);
            var parts = new List<string> { UnitGitTime.Relative(backup.Created), UnitGitService.FormatBytes(backup.Bytes) };
            if (backup.Branches > 0) parts.Add(backup.Branches + " branch" + (backup.Branches == 1 ? "" : "es"));
            if (backup.Healthy == false) parts.Add("damaged");
            var meta = new Label(string.Join(" · ", parts)) { tooltip = backup.Path };
            meta.AddToClassList("ugb-row__meta");
            meta.EnableInClassList("ugb-row__meta--bad", backup.Healthy == false);
            texts.Add(meta);

            var actions = new VisualElement();
            actions.AddToClassList("ugb-row__actions");
            row.Add(actions);
            actions.Add(BackupButton("Check", "ugb-button", "Read the whole backup back to make sure every commit is intact.", () =>
                RunAction("Check backup", () =>
                {
                    var checkedBackup = UnitGitBackups.Check(gitService, backup.Path);
                    return checkedBackup.Healthy == true
                        ? new GitCommandResult { StandardOutput = "The backup is intact (" + checkedBackup.Branches + " branches)." }
                        : new GitCommandResult { ExitCode = 1, StandardError = "The backup is damaged: " + checkedBackup.Problem };
                }, _ =>
                {
                    highlightedBackup = backup.Path;
                    RebuildContent();
                })));
            actions.Add(BackupButton("Restore…", "ugb-button", "Recreate the project from this backup in a new, empty folder. This project is not touched.", () =>
            {
                string parent = EditorUtility.OpenFolderPanel("Choose an empty folder for the restored project", Path.GetDirectoryName(root), string.Empty);
                if (string.IsNullOrEmpty(parent)) return;
                string destination = Directory.Exists(parent) && Directory.EnumerateFileSystemEntries(parent).Any()
                    ? Path.Combine(parent, UnitGitBackups.ProjectName(root) + " (restored " + backup.Created.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture) + ")")
                    : parent;
                if (!EditorUtility.DisplayDialog("Restore backup", "Mixer Git will recreate the project from this backup in:\n\n" + destination + "\n\nThis project stays as it is. Open the copy with Unity Hub afterwards.", "Restore", "Cancel"))
                    return;
                RunAction("Restore backup", () => UnitGitBackups.RestoreCopy(gitService, backup.Path, destination), result =>
                {
                    if (result != null && result.Success) EditorUtility.RevealInFinder(destination);
                });
            }));
            actions.Add(BackupButton("Show", "ugb-button", "Show the file.", () => EditorUtility.RevealInFinder(backup.Path)));
            actions.Add(BackupButton("Delete", "ugb-button ugb-button--danger", "Delete this backup file.", () =>
            {
                if (!EditorUtility.DisplayDialog("Delete backup", "Delete the backup from " + backup.Created.ToString("g", CultureInfo.CurrentCulture) + "? This cannot be undone.", "Delete", "Cancel")) return;
                row.AddToClassList("ugb-row--leave");
                row.schedule.Execute(() =>
                {
                    UnitGitBackups.Delete(backup.Path);
                    RebuildContent();
                }).StartingIn(180);
            }));
            return row;
        }

        private static VisualElement BackupNotice(string text, bool warning)
        {
            var notice = new Label(text);
            notice.AddToClassList("ugb-notice");
            notice.EnableInClassList("ugb-notice--warning", warning);
            return notice;
        }

        private static Button BackupButton(string text, string classes, string tooltip, Action action)
        {
            var button = new Button(action) { text = text, tooltip = tooltip };
            foreach (string name in classes.Split(' ')) button.AddToClassList(name);
            // Visible feedback on press; the click follows Unity's usual release behaviour.
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("ugb-button--pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("ugb-button--pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("ugb-button--pressed"));
            return button;
        }

        private static bool UsesLfs(string root)
        {
            string attributes = Path.Combine(root, ".gitattributes");
            return File.Exists(attributes) && File.ReadAllText(attributes).IndexOf("filter=lfs", StringComparison.Ordinal) >= 0;
        }
    }
}
