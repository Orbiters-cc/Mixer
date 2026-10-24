using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private const string ConflictStyleSheetPath = "Packages/orbiters.mixer/Editor/Git/Styles/unitgit-conflicts.uss";

        /// <summary>Everything read for one conflicted file: its sides, Unity's attempt, the parts to choose.</summary>
        private sealed class ConflictWork
        {
            public string Path = string.Empty;
            // Which sides have the file, as Git lists them: a side without it deleted it.
            public bool HasMine, HasTheirs;
            public string Mine, Base, Theirs;
            public bool Binary;
            public UnityMergeResult Unity;
            public List<ConflictBlock> Blocks = new List<ConflictBlock>();
            // The file on disk before anything was read: a save made from this read is refused once it changes.
            public string FileState = string.Empty;
            public string Error;
        }

        private string selectedConflict;
        // Files resolved since the operation began: they stay listed, ticked, until it is finished or cancelled.
        private readonly List<string> resolvedConflicts = new List<string>();
        private readonly Dictionary<string, Task<ConflictWork>> conflictWork = new Dictionary<string, Task<ConflictWork>>();

        private List<UnitGitStatusEntry> Conflicted()
        {
            return snapshot == null ? new List<UnitGitStatusEntry>() : snapshot.Changes.Where(UnitGitConflicts.IsConflict).ToList();
        }

        private bool HasConflictWork()
        {
            return snapshot != null && snapshot.HasRepository && (Conflicted().Count > 0 || UnitGitConflicts.Operation(gitService.ProjectRoot, out _) != UnitGitOperation.None);
        }

        private VisualElement BuildConflictsBody()
        {
            var page = new VisualElement();
            page.AddToClassList("ugc");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(ConflictStyleSheetPath);
            if (sheet != null) page.styleSheets.Add(sheet);

            var conflicts = Conflicted();
            var operation = UnitGitConflicts.Operation(gitService.ProjectRoot, out string title);
            if (operation == UnitGitOperation.None && conflicts.Count == 0) resolvedConflicts.Clear();
            foreach (var entry in conflicts) resolvedConflicts.Remove(entry.Path);
            int total = conflicts.Count + resolvedConflicts.Count;

            page.Add(ConflictBanner(operation, title, conflicts.Count, total));
            if (total == 0)
            {
                var done = new VisualElement();
                done.AddToClassList("ugc-empty");
                var big = new Label("No conflicts");
                big.AddToClassList("ugc-empty__title");
                done.Add(big);
                var small = new Label("When a merge or a shelf touches the same lines as your work, the files show up here to resolve.");
                small.AddToClassList("ugc-empty__text");
                done.Add(small);
                page.Add(done);
                return page;
            }

            if (string.IsNullOrEmpty(selectedConflict) || (!conflicts.Any(c => c.Path == selectedConflict) && !resolvedConflicts.Contains(selectedConflict)))
                selectedConflict = conflicts.Count > 0 ? conflicts[0].Path : resolvedConflicts[0];

            var split = BuildTrackedSplit("Orbiters.UnitGit.ConflictsSplit", 0, 300f);
            split.AddToClassList("ugc-split");
            var files = new ScrollView();
            files.AddToClassList("ugc-files");
            foreach (var entry in conflicts) files.Add(ConflictFileRow(entry.Path, UnitGitConflicts.Describe(entry), false));
            foreach (string path in resolvedConflicts) files.Add(ConflictFileRow(path, "Resolved", true));
            split.Add(files);

            var workbench = new VisualElement();
            workbench.AddToClassList("ugc-workbench");
            var selectedEntry = conflicts.FirstOrDefault(c => c.Path == selectedConflict);
            if (selectedEntry == null) workbench.Add(ResolvedPanel(selectedConflict));
            else workbench.Add(Workbench(selectedEntry));
            split.Add(workbench);
            page.Add(split);
            return page;
        }

        // What Git is doing, how far along the resolving is, and the way out (finish or cancel).
        private VisualElement ConflictBanner(UnitGitOperation operation, string title, int left, int total)
        {
            var banner = new VisualElement();
            banner.AddToClassList("ugc-banner");
            var texts = new VisualElement();
            texts.AddToClassList("ugc-banner__texts");
            banner.Add(texts);
            var heading = new VisualElement();
            heading.AddToClassList("ugc-banner__heading");
            texts.Add(heading);
            var name = new Label(operation == UnitGitOperation.None ? "Conflicts" : title);
            name.AddToClassList("ugc-banner__title");
            heading.Add(name);
            var beta = new Label("BETA");
            beta.AddToClassList("ugc-beta");
            heading.Add(beta);
            var progressText = new Label(total == 0 ? "Nothing to resolve." : left == 0 ? "All " + total + " file" + (total == 1 ? "" : "s") + " resolved." : (total - left) + " of " + total + " files resolved");
            progressText.AddToClassList("ugc-banner__progress-text");
            texts.Add(progressText);
            var track = new VisualElement();
            track.AddToClassList("ugc-progress");
            var fill = new VisualElement();
            fill.AddToClassList("ugc-progress__fill");
            track.Add(fill);
            texts.Add(track);
            float ratio = total == 0 ? 1f : (total - left) / (float)total;
            // The bar grows from where it was, so each resolved file visibly moves it.
            fill.style.width = new Length(lastConflictRatio * 100f, LengthUnit.Percent);
            fill.schedule.Execute(() => fill.style.width = new Length(ratio * 100f, LengthUnit.Percent)).StartingIn(30);
            lastConflictRatio = ratio;

            var actions = new VisualElement();
            actions.AddToClassList("ugc-banner__actions");
            banner.Add(actions);
            if (operation != UnitGitOperation.None)
            {
                string cancel = operation == UnitGitOperation.Merge ? "Cancel merge" : "Cancel";
                actions.Add(ConflictButton(cancel, "ugc-button", "Go back to how things were before, as if it never started.", () =>
                {
                    if (!EditorUtility.DisplayDialog("Mixer Git", cancel + "? Your resolved files go back to how they were before.", cancel, "Keep resolving")) return;
                    RunAction(cancel, () => UnitGitConflicts.Abort(gitService, operation), _ => { resolvedConflicts.Clear(); AssetDatabase.Refresh(); });
                }));
                string finish = operation == UnitGitOperation.Merge ? "Finish merge" : "Continue";
                var finishButton = ConflictButton(finish, "ugc-button ugc-button--primary", left == 0 ? "Record the result with Git's prepared message." : "Resolve every file first.", () =>
                    RunAction(finish, () => UnitGitConflicts.Finish(gitService, operation), result =>
                    {
                        if (result != null && result.Success) resolvedConflicts.Clear();
                    }));
                finishButton.SetEnabled(left == 0 && !busy);
                actions.Add(finishButton);
            }
            return banner;
        }

        private float lastConflictRatio;

        private VisualElement ConflictFileRow(string path, string description, bool resolved)
        {
            var row = new VisualElement();
            row.AddToClassList("ugc-file");
            row.EnableInClassList("ugc-file--selected", path == selectedConflict);
            row.EnableInClassList("ugc-file--resolved", resolved);
            row.Add(BuildProjectFileIcon(path, "ugc-file__icon"));
            var texts = new VisualElement();
            texts.AddToClassList("ugc-file__texts");
            row.Add(texts);
            var name = new Label(GetFileLeaf(path)) { tooltip = path };
            name.AddToClassList("ugc-file__name");
            texts.Add(name);
            var what = new Label(description);
            what.AddToClassList("ugc-file__what");
            texts.Add(what);
            var state = new Label(resolved ? "✓" : "!");
            state.AddToClassList("ugc-file__state");
            state.AddToClassList(resolved ? "ugc-file__state--done" : "ugc-file__state--todo");
            row.Add(state);
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || selectedConflict == path) return;
                selectedConflict = path;
                // The highlight moves on press; the workbench follows.
                foreach (var other in row.parent.Children()) other.EnableInClassList("ugc-file--selected", other == row);
                row.schedule.Execute(RebuildContent).StartingIn(40);
            });
            return row;
        }

        private VisualElement ResolvedPanel(string path)
        {
            var panel = new VisualElement();
            panel.AddToClassList("ugc-resolved");
            var title = new Label("✓  " + GetFileLeaf(path) + " is resolved");
            title.AddToClassList("ugc-resolved__title");
            panel.Add(title);
            var text = new Label("It is staged with the version you chose. Resolve the other files, then finish.");
            text.AddToClassList("ugc-resolved__text");
            panel.Add(text);
            return panel;
        }

        // ---- The workbench of one file -----------------------------------------------------------------------------------

        private VisualElement Workbench(UnitGitStatusEntry entry)
        {
            string path = entry.Path;
            var panel = new VisualElement();
            panel.AddToClassList("ugc-panel");

            var head = new VisualElement();
            head.AddToClassList("ugc-panel__head");
            panel.Add(head);
            var texts = new VisualElement();
            texts.AddToClassList("ugc-panel__texts");
            head.Add(texts);
            var name = new Label(GetFileLeaf(path));
            name.AddToClassList("ugc-panel__title");
            texts.Add(name);
            var where = new Label(UnitGitConflicts.Describe(entry) + " · " + path);
            where.AddToClassList("ugc-panel__path");
            texts.Add(where);
            head.Add(ConflictButton("Keep mine", "ugc-button ugc-button--mine", "Use your version of the whole file.", () => TakeWholeSide(path, true)));
            head.Add(ConflictButton("Take theirs", "ugc-button ugc-button--theirs", "Use the incoming version of the whole file.", () => TakeWholeSide(path, false)));

            var scroll = new ScrollView();
            scroll.AddToClassList("ugc-panel__body");
            panel.Add(scroll);
            var content = new VisualElement();
            content.AddToClassList("ugc-panel__content");
            scroll.Add(content);
            // Models and prefabs load their 3D view on their own, next to the rest of the workbench.
            if (Semantic.ModelVersions.IsPreviewable(path)) content.Add(ModelSection(entry));
            var body = new VisualElement();
            content.Add(body);

            // These exact versions: the sides Git holds (its index) and the file on disk, which may be edited by hand meanwhile.
            string key = path + "|" + IndexStamp(gitService.ProjectRoot) + "|" + DiskStamp(Path.Combine(gitService.ProjectRoot, path));
            if (!conflictWork.TryGetValue(key, out var task))
            {
                // An older read of this file is never shown or saved again.
                foreach (string old in conflictWork.Keys.Where(k => k.StartsWith(path + "|", StringComparison.Ordinal)).ToList()) conflictWork.Remove(old);
                string root = gitService.ProjectRoot;
                string tool = DiffYaml(path) ? UnitGitConflicts.UnityMergeTool() : null;
                bool yaml = DiffYaml(path);
                task = Task.Run(() => ReadConflict(root, path, yaml, tool));
                conflictWork[key] = task;
            }
            if (!task.IsCompleted)
            {
                body.Add(Working(DiffYaml(path) ? "Asking Unity to merge this file…" : "Reading both versions…"));
                bool shown = false;
                panel.schedule.Execute(() =>
                {
                    if (shown || !task.IsCompleted) return;
                    shown = true;
                    body.Clear();
                    FillWorkbench(body, entry, task);
                }).Every(50).Until(() => shown);
            }
            else FillWorkbench(body, entry, task);
            return panel;
        }

        private static bool DiffYaml(string path) => IsUnityYamlPath(path);

        // When Git last wrote its index, wherever this worktree keeps it.
        private static string IndexStamp(string root)
        {
            string index = UnitGitPaths.GitPath(root, "index");
            return index != null && File.Exists(index) ? File.GetLastWriteTimeUtc(index).Ticks.ToString() : "none";
        }

        private static string DiskStamp(string fullPath)
        {
            var file = new FileInfo(fullPath);
            return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing";
        }

        // Models and prefabs in 3D: each side coloured by what it changed since the common version.
        private VisualElement ModelSection(UnitGitStatusEntry entry)
        {
            var section = new VisualElement();
            section.AddToClassList("ugc-model");
            string root = gitService.ProjectRoot;
            string path = entry.Path;
            var view = new ModelCompareView("Mine", "Theirs");
            view.Load("model|conflict|" + root + "|" + path + "|" + IndexStamp(root), root, path,
                destination => new UnitGitService(root).WriteBlob(":2:" + path, destination),
                destination => new UnitGitService(root).WriteBlob(":3:" + path, destination),
                destination => new UnitGitService(root).WriteBlob(":1:" + path, destination));
            section.Add(view);
            return section;
        }

        private static ConflictWork ReadConflict(string root, string path, bool yaml, string tool)
        {
            var work = new ConflictWork { Path = path };
            try
            {
                var git = new UnitGitService(root);
                work.FileState = UnitGitConflicts.FileState(Path.Combine(root, path));
                // Which sides have the file comes from Git's list, never from a read that failed.
                var stages = UnitGitConflicts.Stages(git, path);
                if (stages.Count == 0)
                {
                    work.Error = "It is not in conflict any more. Refresh to see where it stands.";
                    return work;
                }
                work.HasMine = stages.Contains(2);
                work.HasTheirs = stages.Contains(3);
                // Binary files are only kept whole: which sides have the file is all that matters here.
                work.Binary = !IsTextFile(path);
                if (work.Binary || !work.HasMine || !work.HasTheirs) return work;
                work.Mine = UnitGitConflicts.Stage(git, path, 2);
                work.Theirs = UnitGitConflicts.Stage(git, path, 3);
                if (stages.Contains(1)) work.Base = UnitGitConflicts.Stage(git, path, 1);
                // A side that turns out binary has no text, and is kept whole too.
                work.Binary = work.Mine == null || work.Theirs == null;
                if (work.Binary) return work;
                if (yaml) work.Unity = UnitGitConflicts.UnityMerge(tool, work.Mine, work.Base, work.Theirs, Path.GetExtension(path));
                work.Blocks = UnitGitConflicts.Blocks(git, work.Mine, work.Base ?? string.Empty, work.Theirs);
            }
            catch (Exception ex)
            {
                work.Error = ex.Message;
            }
            return work;
        }

        private static bool IsTextFile(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".fbx": case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd": case ".exr": case ".wav": case ".mp3": case ".ogg": case ".bin": case ".dll": case ".unitypackage": case ".zip":
                    return false;
                default:
                    return true;
            }
        }

        private void FillWorkbench(VisualElement body, UnitGitStatusEntry entry, Task<ConflictWork> task)
        {
            var work = task.IsFaulted ? new ConflictWork { Error = task.Exception?.GetBaseException().Message } : task.Result;
            if (!string.IsNullOrEmpty(work.Error)) { body.Add(ConflictCard("Could not read this conflict", work.Error, "ugc-card--warning")); return; }
            if (!work.HasMine || !work.HasTheirs)
            {
                bool youDeleted = !work.HasMine;
                var row = new VisualElement();
                row.AddToClassList("ugc-choices--wide");
                if (!work.HasMine && !work.HasTheirs)
                {
                    // Nothing is left to keep: deleting is the only way to resolve it.
                    body.Add(ConflictCard("Both sides deleted this file", "Delete it to mark it resolved.", "ugc-card--info"));
                    row.Add(ConflictButton("Delete it", "ugc-button ugc-button--primary", "Remove the file.", () => TakeWholeSide(entry.Path, true)));
                    body.Add(row);
                    return;
                }
                body.Add(ConflictCard(youDeleted ? "You deleted this file; they changed it" : "They deleted this file; you changed it",
                    "Keep the file with its changes, or delete it.", "ugc-card--info"));
                row.Add(ConflictButton("Keep the file", "ugc-button ugc-button--primary", "Keep the changed version.", () => TakeWholeSide(entry.Path, youDeleted ? false : true)));
                row.Add(ConflictButton("Delete it", "ugc-button", "Remove the file.", () => TakeWholeSide(entry.Path, youDeleted)));
                body.Add(row);
                return;
            }
            if (work.Binary)
            {
                body.Add(ConflictCard("This file can't be merged part by part",
                    Semantic.ModelVersions.IsModelPath(entry.Path)
                        ? "A model changes as a whole: compare both sides above, then keep one with the buttons at the top."
                        : "Images and other binary files change as a whole. Keep one side with the buttons above.", "ugc-card--info"));
                return;
            }
            if (work.Unity != null) body.Add(UnityMergeCard(entry.Path, work.Unity, work.FileState));
            AddParts(body, entry.Path, work);
        }

        private VisualElement UnityMergeCard(string path, UnityMergeResult unity, string fileState)
        {
            if (!unity.Available) return ConflictCard("Unity's merge is not available", unity.Message, "ugc-card--info");
            if (unity.Clean)
            {
                var card = ConflictCard("Unity merged both sides", "It combined the changes object by object: nothing conflicts. Use its result, or choose parts yourself below.", "ugc-card--good");
                var use = ConflictButton("Use Unity's merge", "ugc-button ugc-button--primary", "Save Unity's result and mark the file resolved.", () =>
                    RunAction("Resolve " + GetFileLeaf(path), () => UnitGitConflicts.Resolve(gitService, path, unity.Output, fileState), result => AfterResolved(path, result)));
                use.AddToClassList("ugc-card__action");
                card.Add(use);
                return card;
            }
            var conflicted = ConflictCard("Unity merged everything but " + unity.Conflicts.Count + " value" + (unity.Conflicts.Count == 1 ? "" : "s"),
                unity.Conflicts.Count == 0 ? unity.Message : "Both sides changed these differently. Choose the parts below, or keep a whole side.", "ugc-card--warning");
            foreach (var conflict in unity.Conflicts.Take(12))
            {
                var row = new VisualElement();
                row.AddToClassList("ugc-unity-row");
                var what = new Label(Semantic.UnitySemanticDiff.Label(conflict.Property));
                what.AddToClassList("ugc-unity-row__what");
                what.tooltip = conflict.Object + "." + conflict.Property;
                row.Add(what);
                var mine = new Label(conflict.Mine);
                mine.AddToClassList("ugc-value");
                mine.AddToClassList("ugc-value--mine");
                row.Add(mine);
                var theirs = new Label(conflict.Theirs);
                theirs.AddToClassList("ugc-value");
                theirs.AddToClassList("ugc-value--theirs");
                row.Add(theirs);
                conflicted.Add(row);
            }
            return conflicted;
        }

        private void AddParts(VisualElement body, string path, ConflictWork work)
        {
            var parts = work.Blocks.Where(b => b.IsConflict).ToList();
            if (parts.Count == 0)
            {
                var card = ConflictCard("Git found no conflicting lines", "The sides can be combined as they are. Save to mark the file resolved.", "ugc-card--good");
                card.Add(ConflictButton("Save and mark resolved", "ugc-button ugc-button--primary ugc-card__action", "Save the combined file.", () =>
                    RunAction("Resolve " + GetFileLeaf(path), () => UnitGitConflicts.Resolve(gitService, path, UnitGitConflicts.Compose(work.Blocks), work.FileState), result => AfterResolved(path, result))));
                body.Add(card);
                return;
            }
            var header = new VisualElement();
            header.AddToClassList("ugc-parts__header");
            var title = new Label("Choose each part");
            title.AddToClassList("ugc-parts__title");
            header.Add(title);
            var legend = new Label("Mine is your version, Theirs the incoming one.");
            legend.AddToClassList("ugc-parts__legend");
            header.Add(legend);
            body.Add(header);

            Button save = null;
            Label count = null;
            void Update()
            {
                int chosen = parts.Count(p => p.Choice != ConflictChoice.None);
                count.text = chosen + " of " + parts.Count + " part" + (parts.Count == 1 ? "" : "s") + " chosen";
                save.SetEnabled(chosen == parts.Count && !busy);
            }
            for (int i = 0; i < parts.Count; i++)
            {
                var part = PartCard(parts[i], i, parts.Count, () => Update());
                body.Add(part);
                part.AddToClassList("ugc-part--enter");
                part.schedule.Execute(() => part.RemoveFromClassList("ugc-part--enter")).StartingIn(30 + Math.Min(i, 8) * 40);
            }
            var footer = new VisualElement();
            footer.AddToClassList("ugc-footer");
            count = new Label();
            count.AddToClassList("ugc-footer__count");
            footer.Add(count);
            footer.Add(ConflictButton("All mine", "ugc-button", "Choose Mine for every part.", () => { foreach (var p in parts) p.Choice = ConflictChoice.Mine; RebuildContent(); }));
            footer.Add(ConflictButton("All theirs", "ugc-button", "Choose Theirs for every part.", () => { foreach (var p in parts) p.Choice = ConflictChoice.Theirs; RebuildContent(); }));
            save = ConflictButton("Save and mark resolved", "ugc-button ugc-button--primary", "Write the file with your choices and mark it resolved.", () =>
            {
                string text = UnitGitConflicts.Compose(work.Blocks);
                if (text == null) return;
                RunAction("Resolve " + GetFileLeaf(path), () => UnitGitConflicts.Resolve(gitService, path, text, work.FileState), result => AfterResolved(path, result));
            });
            footer.Add(save);
            body.Add(footer);
            Update();
        }

        private VisualElement PartCard(ConflictBlock part, int index, int total, Action changed)
        {
            var card = new VisualElement();
            card.AddToClassList("ugc-part");
            var head = new VisualElement();
            head.AddToClassList("ugc-part__head");
            card.Add(head);
            var title = new Label("Part " + (index + 1) + " of " + total);
            title.AddToClassList("ugc-part__title");
            head.Add(title);
            if (!string.IsNullOrEmpty(part.Context))
            {
                var context = new Label("in " + part.Context);
                context.AddToClassList("ugc-part__context");
                head.Add(context);
            }

            var sides = new VisualElement();
            sides.AddToClassList("ugc-sides");
            card.Add(sides);
            var mine = Side("Mine", part.Mine, "ugc-side--mine");
            var theirs = Side("Theirs", part.Theirs, "ugc-side--theirs");
            sides.Add(mine);
            sides.Add(theirs);

            var choices = new VisualElement();
            choices.AddToClassList("ugc-choices");
            card.Add(choices);
            var buttons = new Dictionary<ConflictChoice, VisualElement>();
            void Show()
            {
                foreach (var pair in buttons) pair.Value.EnableInClassList("ugc-choice--on", pair.Key == part.Choice);
                mine.EnableInClassList("ugc-side--dim", part.Choice == ConflictChoice.Theirs);
                theirs.EnableInClassList("ugc-side--dim", part.Choice == ConflictChoice.Mine);
                card.EnableInClassList("ugc-part--chosen", part.Choice != ConflictChoice.None);
            }
            foreach (var option in new[] { ConflictChoice.Mine, ConflictChoice.Theirs, ConflictChoice.Both })
            {
                var value = option;
                var choice = new Label(value == ConflictChoice.Both ? "Both (mine, then theirs)" : "Use " + value.ToString().ToLowerInvariant());
                choice.AddToClassList("ugc-choice");
                choice.AddToClassList("ugc-choice--" + value.ToString().ToLowerInvariant());
                // Chooses on press: the highlight and the dimmed side follow at once.
                choice.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    part.Choice = value;
                    Show();
                    changed();
                });
                buttons[value] = choice;
                choices.Add(choice);
            }
            Show();
            return card;
        }

        private static VisualElement Side(string title, string text, string className)
        {
            var side = new VisualElement();
            side.AddToClassList("ugc-side");
            side.AddToClassList(className);
            var label = new Label(title);
            label.AddToClassList("ugc-side__title");
            side.Add(label);
            string shown = string.IsNullOrEmpty(text) ? "(nothing)" : text.TrimEnd('\n');
            var lines = shown.Split('\n');
            if (lines.Length > 40) shown = string.Join("\n", lines.Take(40)) + "\n… " + (lines.Length - 40) + " more lines";
            var code = new Label(shown);
            code.AddToClassList("ugc-side__code");
            code.selection.isSelectable = true;
            side.Add(code);
            return side;
        }

        private VisualElement Working(string text)
        {
            var card = new VisualElement();
            card.AddToClassList("ugc-working");
            var label = new Label(text);
            label.AddToClassList("ugc-working__text");
            card.Add(label);
            var track = new VisualElement();
            track.AddToClassList("ugc-progress");
            var fill = new VisualElement();
            fill.AddToClassList("ugc-progress__sweep");
            track.Add(fill);
            card.Add(track);
            fill.schedule.Execute(() => fill.ToggleInClassList("ugc-progress__sweep--end")).Every(650);
            return card;
        }

        private static VisualElement ConflictCard(string title, string text, string className)
        {
            var card = new VisualElement();
            card.AddToClassList("ugc-card");
            card.AddToClassList(className);
            var t = new Label(title);
            t.AddToClassList("ugc-card__title");
            card.Add(t);
            if (!string.IsNullOrEmpty(text))
            {
                var b = new Label(text);
                b.AddToClassList("ugc-card__text");
                card.Add(b);
            }
            return card;
        }

        private void TakeWholeSide(string path, bool mine)
        {
            RunAction((mine ? "Keep mine: " : "Take theirs: ") + GetFileLeaf(path), () => UnitGitConflicts.TakeSide(gitService, path, mine), result => AfterResolved(path, result));
        }

        private void AfterResolved(string path, GitCommandResult result)
        {
            if (result == null || !result.Success) return;
            if (!resolvedConflicts.Contains(path)) resolvedConflicts.Add(path);
            // Unity picks the new file content up; the next conflict opens by itself.
            AssetDatabase.Refresh();
            selectedConflict = null;
        }

        private static Button ConflictButton(string text, string classes, string tooltip, Action action)
        {
            var button = new Button(action) { text = text, tooltip = tooltip };
            foreach (string name in classes.Split(' ')) button.AddToClassList(name);
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("ugc-button--pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("ugc-button--pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("ugc-button--pressed"));
            return button;
        }
    }
}
