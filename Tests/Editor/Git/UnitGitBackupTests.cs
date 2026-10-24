using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitBackupTests
    {
        // A backup holds every branch, reads back as healthy, and restores into a working copy of the project.
        [Test]
        public void BackupHoldsEveryBranchAndRestoresTheProject()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string scratch = UnitGitTestRepo.TempFolder();
                string folder = Path.Combine(scratch, "backups");
                string restored = Path.Combine(scratch, "restored");
                try
                {
                    repo.Write("Assets/Avatar.prefab", "v1");
                    repo.Commit("first");
                    repo.Run("branch", "experiment");
                    repo.Write("Assets/Avatar.prefab", "v2");
                    repo.Commit("second");

                    Assert.That(UnitGitBackups.IsDue(repo.Git, folder), Is.True);
                    var result = UnitGitBackups.Create(repo.Git, folder, 10);
                    Assert.That(result.Success, Is.True, result.Message);

                    var backup = UnitGitBackups.List(folder).Single();
                    Assert.That(backup.Healthy, Is.True);
                    Assert.That(backup.Branches, Is.EqualTo(2));
                    Assert.That(UnitGitBackups.IsDue(repo.Git, folder), Is.False, "A fresh backup is not due again.");

                    var restore = UnitGitBackups.RestoreCopy(repo.Git, backup.Path, restored);
                    Assert.That(restore.Success, Is.True, restore.Message);
                    Assert.That(File.ReadAllText(Path.Combine(restored, "Assets", "Avatar.prefab")), Is.EqualTo("v2"));
                    var copy = new UnitGitService(restored);
                    Assert.That(copy.RunGit(30000, "symbolic-ref", "--short", "HEAD").StandardOutput.Trim(), Is.EqualTo("main"));
                    Assert.That(copy.RunGit(30000, "rev-parse", "--verify", "experiment").Success, Is.True);
                    Assert.That(copy.RunGit(30000, "remote").StandardOutput.Trim(), Is.Empty);
                }
                finally
                {
                    UnitGitTestRepo.DeleteTempFolder(scratch);
                }
            }
        }

        [Test]
        public void DamagedBackupIsReportedAndOldOnesArePruned()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string scratch = UnitGitTestRepo.TempFolder();
                string folder = Path.Combine(scratch, "backups");
                try
                {
                    repo.Write("Assets/a.txt", "a");
                    repo.Commit("a");
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.That(UnitGitBackups.Create(repo.Git, folder, 2).Success, Is.True);
                    }
                    var backups = UnitGitBackups.List(folder);
                    Assert.That(backups.Count, Is.EqualTo(2));

                    byte[] bytes = File.ReadAllBytes(backups[0].Path);
                    File.WriteAllBytes(backups[0].Path, bytes.Take(bytes.Length / 2).ToArray());
                    Assert.That(UnitGitBackups.Check(repo.Git, backups[0].Path).Healthy, Is.False);

                    string restored = Path.Combine(scratch, "not-empty");
                    Directory.CreateDirectory(restored);
                    File.WriteAllText(Path.Combine(restored, "keep.txt"), "x");
                    Assert.That(UnitGitBackups.RestoreCopy(repo.Git, backups[1].Path, restored).Success, Is.False);
                    Assert.That(File.ReadAllText(Path.Combine(restored, "keep.txt")), Is.EqualTo("x"));
                }
                finally
                {
                    UnitGitTestRepo.DeleteTempFolder(scratch);
                }
            }
        }

        [Test]
        public void FolderInsideTheProjectIsDetected()
        {
            Assert.That(UnitGitBackups.IsInsideProject(@"C:\Projects\Avatar\Backups", @"C:\Projects\Avatar"), Is.True);
            Assert.That(UnitGitBackups.IsInsideProject(@"C:\Projects\Avatar Backups", @"C:\Projects\Avatar"), Is.False);
        }

        // Projects can share a backup folder, even under the same name: each one only ever removes its own old backups.
        [Test]
        public void PruningOnlyRemovesThisProjectsOwnBackups()
        {
            using (var mine = UnitGitTestRepo.Create())
            using (var other = UnitGitTestRepo.Create())
            {
                string shared = UnitGitTestRepo.TempFolder();
                try
                {
                    mine.Write("Assets/a.txt", "a");
                    mine.Commit("a");
                    other.Write("Assets/b.txt", "b");
                    other.Commit("b");
                    Assert.That(UnitGitBackups.Create(other.Git, shared, 10).Success, Is.True);
                    Assert.That(UnitGitBackups.Create(other.Git, shared, 10).Success, Is.True);
                    var others = Directory.GetFiles(shared).ToList();
                    // Named like this project's backups but not made by it: an older naming, another project of the same name,
                    // and a file that only shares the extension.
                    string name = UnitGitBackups.ProjectName(mine.Root);
                    var lookalikes = new[] { name + "_2001-01-01_00-00-00.bundle", name + "_deadbeef_2001-01-01_00-00-00.bundle", "notes.bundle" }
                        .Select(file => Path.Combine(shared, file)).ToList();
                    foreach (string path in lookalikes)
                    {
                        File.Copy(others[0], path);
                        File.SetLastWriteTime(path, new DateTime(2001, 1, 1));
                    }

                    for (int i = 0; i < 3; i++) Assert.That(UnitGitBackups.Create(mine.Git, shared, 1).Success, Is.True);

                    Assert.That(Directory.GetFiles(shared).Count(path => UnitGitBackups.IsOwn(path, mine.Root)), Is.EqualTo(1), "This project keeps its newest.");
                    foreach (string path in others.Concat(lookalikes)) Assert.That(File.Exists(path), Is.True, path + " is not this project's.");
                    Assert.That(UnitGitBackups.IsDue(other.Git, shared), Is.False, "Another project's fresh backup still counts for it.");
                }
                finally
                {
                    UnitGitTestRepo.DeleteTempFolder(shared);
                }
            }
        }

        // A backup made with no branch checked out restores that very commit, not a branch's.
        [Test]
        public void RestoreBringsBackTheCheckedOutCommitWithoutABranch()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string scratch = UnitGitTestRepo.TempFolder();
                try
                {
                    repo.Write("Assets/Avatar.prefab", "v1");
                    string first = repo.Commit("first");
                    repo.Write("Assets/Avatar.prefab", "v2");
                    repo.Commit("second");
                    repo.Run("checkout", "-q", "--detach", first);
                    repo.Write("Assets/Avatar.prefab", "detached work");
                    string detached = repo.Commit("detached");
                    string folder = Path.Combine(scratch, "backups");
                    Assert.That(UnitGitBackups.Create(repo.Git, folder, 10).Success, Is.True);

                    string restored = Path.Combine(scratch, "restored");
                    var restore = UnitGitBackups.RestoreCopy(repo.Git, UnitGitBackups.List(folder).Single().Path, restored);
                    Assert.That(restore.Success, Is.True, restore.Message);
                    Assert.That(restore.Message, Does.Contain("no branch checked out"));
                    var copy = new UnitGitService(restored);
                    Assert.That(copy.RunGit(30000, "rev-parse", "HEAD").StandardOutput.Trim(), Is.EqualTo(detached));
                    Assert.That(copy.RunGit(30000, "symbolic-ref", "-q", "HEAD").Success, Is.False, "Detached, as it was.");
                    Assert.That(File.ReadAllText(Path.Combine(restored, "Assets", "Avatar.prefab")), Is.EqualTo("detached work"));
                }
                finally
                {
                    UnitGitTestRepo.DeleteTempFolder(scratch);
                }
            }
        }

        // Two branches at the checked-out commit: the copy stays on that commit and names both, rather than pick one.
        [Test]
        public void RestoreDoesNotGuessBetweenBranchesAtTheSameCommit()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string scratch = UnitGitTestRepo.TempFolder();
                try
                {
                    repo.Write("Assets/a.txt", "a");
                    string commit = repo.Commit("a");
                    repo.Run("checkout", "-q", "-b", "feature");
                    string folder = Path.Combine(scratch, "backups");
                    Assert.That(UnitGitBackups.Create(repo.Git, folder, 10).Success, Is.True);

                    string restored = Path.Combine(scratch, "restored");
                    var restore = UnitGitBackups.RestoreCopy(repo.Git, UnitGitBackups.List(folder).Single().Path, restored);
                    Assert.That(restore.Success, Is.True, restore.Message);
                    Assert.That(restore.Message, Does.Contain("feature").And.Contain("main"));
                    var copy = new UnitGitService(restored);
                    Assert.That(copy.RunGit(30000, "rev-parse", "HEAD").StandardOutput.Trim(), Is.EqualTo(commit));
                    Assert.That(copy.RunGit(30000, "symbolic-ref", "-q", "HEAD").Success, Is.False, "No branch is picked for the user.");
                }
                finally
                {
                    UnitGitTestRepo.DeleteTempFolder(scratch);
                }
            }
        }
    }
}
