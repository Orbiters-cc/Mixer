using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Processes;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Nothing a Git operation starts outlives it: every process is stopped and waited for before the operation returns, on
    // Windows a running Git keeps the repository's folder (its working directory) and the pack files it maps from being
    // moved or deleted. Checked through the runner's own count of the processes it owns, and by deleting the repository
    // the moment a rename or a squash returns, in one attempt.
    public sealed class UnitGitNoProcessLeftTests
    {
        private static void DeleteNow(string folder)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(folder, true); // no retry: a Git process still in the folder makes this throw on Windows
        }

        [Test]
        public void TheRepositoryCanBeDeletedTheMomentARenameOrASquashReturns()
        {
            foreach (bool commitTree in new[] { false, true })
            {
                foreach (bool squash in new[] { false, true })
                {
                    using (var repo = UnitGitTestRepo.Create())
                    {
                        var hashes = new List<string>();
                        for (int i = 0; i < 4; i++)
                        {
                            repo.Write("Assets/f.txt", i + "\n");
                            hashes.Add(repo.Commit("c" + i));
                        }

                        int before = EditorProcessRunner.ActiveCount;
                        UnitGitService.ForceCommitTreeRewrite = commitTree;
                        GitCommandResult result;
                        try
                        {
                            repo.Git.ReadHeadState(out UnitGitHeadState head);
                            result = squash
                                ? repo.Git.SquashCommits(new[] { hashes[1], hashes[2] }, "squashed", head)
                                : repo.Git.RenameCommit(hashes[1], "renamed", head);
                        }
                        finally
                        {
                            UnitGitService.ForceCommitTreeRewrite = false;
                        }

                        string what = (squash ? "squash" : "rename") + (commitTree ? " through commit-tree" : " through fast-import");
                        Assert.That(result.Success, Is.True, what + ": " + result.Message);
                        Assert.That(EditorProcessRunner.ActiveCount, Is.EqualTo(before), what + ": a Git process outlived it.");
                        Assert.DoesNotThrow(() => DeleteNow(repo.Root), what + ": the repository is still in use.");
                    }
                }
            }
        }

        [Test]
        public void NoGitProcessOutlivesAPublicOperation()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                string first = repo.Commit("first");
                repo.Write("Assets/a.txt", "b\n");
                string second = repo.Commit("second");
                repo.Write("Assets/a.txt", "c\n");
                repo.Write("Assets/new.txt", "new\n");
                string backups = UnitGitTestRepo.TempFolder();
                try
                {
                    var operations = new List<(string name, Action run)>
                    {
                        ("snapshot with history", () => repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { History = new UnitGitHistory(), HistoryLimit = 1 })),
                        ("snapshot", () => repo.Git.BuildSnapshot(string.Empty, 1)),
                        ("commits", () => repo.Git.GetCommits(string.Empty, 1)),
                        ("commit details", () => repo.Git.GetCommitDetails(second)),
                        ("several commits' details", () => repo.Git.GetCommitDetails(new[] { first, second })),
                        ("file at a commit", () => repo.Git.ReadFileAt(first, "Assets/a.txt")),
                        ("file at HEAD", () => repo.Git.ReadFileAt("HEAD", "Assets/a.txt")),
                        ("blob written out", () => repo.Git.WriteBlob(first + ":Assets/a.txt", Path.Combine(backups, "a.txt"))),
                        ("file diff", () => repo.Git.GetFileDiff(new UnitGitStatusEntry { Path = "Assets/a.txt", WorkTreeStatus = 'M' })),
                        ("commit file diff", () => repo.Git.GetCommitFileDiff(second, "Assets/a.txt")),
                        ("conflict parts", () => UnitGitConflicts.Blocks(repo.Git, "one\nmine\n", "one\ntwo\n", "one\ntheirs\n")),
                        ("stage", () => repo.Git.Stage(new[] { "Assets/a.txt", "Assets/new.txt" })),
                        ("unstage", () => repo.Git.Unstage(new[] { "Assets/new.txt" })),
                        ("shelve", () => repo.Git.ShelveAll("shelf")),
                        ("find shelf", () => repo.Git.FindShelf("shelf")),
                        ("backup", () => UnitGitBackups.Create(repo.Git, backups, 2)),
                        ("rename", () => { repo.Git.ReadHeadState(out UnitGitHeadState head); repo.Git.RenameCommit(first, "renamed", head); }),
                    };
                    foreach (var operation in operations)
                    {
                        int before = EditorProcessRunner.ActiveCount;
                        operation.run();
                        Assert.That(EditorProcessRunner.ActiveCount, Is.EqualTo(before), operation.name + ": a Git process outlived it.");
                    }
                }
                finally
                {
                    UnitGitTestRepo.DeleteTempFolder(backups);
                }
            }
        }
    }
}
