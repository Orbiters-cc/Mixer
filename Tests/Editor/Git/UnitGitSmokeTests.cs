using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitSmokeTests
    {
        [Test]
        public void UnicodeFilesPreviewAndRenameWithQuotedGitPaths()
        {
            RequireGit();
            string root = CreateTempFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);
                AssertGit(service.RunGit(30000, "config", "core.quotePath", "true"));
                AssertGit(service.RunGit(30000, "config", "commit.gpgsign", "false"));
                const string original = "caf\u00e9.txt";
                const string renamed = "\u670d\U0001F43E.txt";
                File.WriteAllText(Path.Combine(root, original), "visible content\n");
                var status = service.RunGit(30000, "status", "--porcelain=v1", "-b", "-uall");
                AssertGit(status);
                var change = UnitGitService.ParseStatusOutput(status.StandardOutput).Changes.Single();
                Assert.That(change.Path, Is.EqualTo(original));
                Assert.That(service.GetFileDiff(change).Lines.Any(line => line.Right == "visible content"), Is.True);
                AssertGit(service.StageAll());
                AssertGit(service.Commit("initial"));
                AssertGit(service.RunGit(30000, "mv", "--", original, renamed));
                status = service.RunGit(30000, "status", "--porcelain=v1", "-b", "-uall");
                AssertGit(status);
                change = UnitGitService.ParseStatusOutput(status.StandardOutput).Changes.Single();
                Assert.That(change.Path, Is.EqualTo(renamed));
                Assert.That(change.OriginalPath, Is.EqualTo(original));
                File.AppendAllText(Path.Combine(root, renamed), "modified content\n");
                status = service.RunGit(30000, "status", "--porcelain=v1", "-b", "-uall");
                change = UnitGitService.ParseStatusOutput(status.StandardOutput).Changes.Single();
                Assert.That(service.GetFileDiff(change).Lines.Any(line => line.Right == "modified content"), Is.True);
            }
            finally { DeleteTempFolder(root); }
        }

        [Test]
        public void TempRepoStagesCommitsShowsMixedDiffAndStashes()
        {
            RequireGit();
            string root = CreateTempFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                string filePath = Path.Combine(root, "file.txt");
                File.WriteAllText(filePath, "one\nbase\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("initial"));

                File.WriteAllText(filePath, "one\nstaged\n");
                AssertGit(service.StageAll());
                File.WriteAllText(filePath, "one\nunstaged\n");

                UnitGitDiff diff = service.GetFileDiff(new UnitGitStatusEntry
                {
                    Path = "file.txt",
                    IndexStatus = 'M',
                    WorkTreeStatus = 'M'
                });

                Assert.That(diff.Lines.Any(line => line.Right.Contains("Staged changes")), Is.True);
                Assert.That(diff.Lines.Any(line => line.Right.Contains("Unstaged changes")), Is.True);
                Assert.That(diff.Lines.Any(line => line.Right == "staged"), Is.True);
                Assert.That(diff.Lines.Any(line => line.Right == "unstaged"), Is.True);

                AssertGit(service.ShelveAll("unitgit smoke shelf"));
                GitCommandResult stashList = service.RunGit(30000, "stash", "list");
                AssertGit(stashList);
                Assert.That(UnitGitService.ParseStashList(stashList.StandardOutput).Any(line => line.Contains("unitgit smoke shelf")), Is.True);
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void UntrackedLargeBinaryPreviewIsSkipped()
        {
            string root = CreateTempFolder();
            try
            {
                var service = new UnitGitService(root);
                var bytes = new byte[600 * 1024];
                bytes[0] = 0;
                File.WriteAllBytes(Path.Combine(root, "large.bin"), bytes);

                UnitGitDiff diff = service.GetFileDiff(new UnitGitStatusEntry
                {
                    Path = "large.bin",
                    IndexStatus = '?',
                    WorkTreeStatus = '?'
                });

                Assert.That(diff.Lines, Has.Count.EqualTo(1));
                Assert.That(diff.Lines[0].Right, Does.Contain("preview skipped"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void PublishReleaseRollsBackReleaseFileWhenScopedCommitFails()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                string assetPath = Path.Combine(root, "Assets", "file.txt");
                File.WriteAllText(assetPath, "initial\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("initial"));

                var entry = new UnitGitReleaseEntry
                {
                    id = "release-rollback-test",
                    tool = "MCB",
                    type = "mcb-version",
                    name = "Test Asset",
                    version = "1.0.0"
                };

                UnitGitReleaseResult result = UnitGitReleases.PublishRelease(
                    entry,
                    "MCB : v1.0.0",
                    service,
                    "Assets/file.txt",
                    "Assets/missing-file.txt");

                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Does.Contain("Staging files failed"));
                Assert.That(File.Exists(UnitGitReleases.GetReleasesFilePath(root)), Is.False);

                GitCommandResult releaseStatus = service.RunGit(30000, "status", "--porcelain=v1", "--", UnitGitReleases.ReleasesFileName);
                AssertGit(releaseStatus);
                Assert.That(releaseStatus.StandardOutput.Trim(), Is.Empty);
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void ScopedReleaseSkipsIgnoredPackagesAndPreservesUnrelatedStaging()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);
                File.WriteAllText(Path.Combine(root, ".gitignore"), "*.unitypackage\n*.unitypackage.meta\n");
                File.WriteAllText(Path.Combine(root, "Assets", "other.txt"), "base");
                AssertGit(service.StageAll()); AssertGit(service.Commit("initial"));
                File.WriteAllText(Path.Combine(root, "Assets", "other.txt"), "staged");
                AssertGit(service.StageAll());
                File.WriteAllText(Path.Combine(root, "Assets", "other.txt"), "unstaged");
                File.WriteAllText(Path.Combine(root, "Assets", "version.json"), "version");
                File.WriteAllText(Path.Combine(root, "Assets", "logic.unitypackage"), "generated");
                File.WriteAllText(Path.Combine(root, "Assets", "logic.unitypackage.meta"), "generated meta");
                string before = service.RunGit(30000, "diff", "--cached", "--binary").StandardOutput;
                var result = UnitGitReleases.PublishRelease(new UnitGitReleaseEntry { id = "scoped-test", tool = "MCB", version = "1.0.0" },
                    "release test", service, "Assets/version.json", "Assets/logic.unitypackage", "Assets/logic.unitypackage.meta");
                Assert.That(result.Success, Is.True, result.Message);
                Assert.That(service.RunGit(30000, "diff", "--cached", "--binary").StandardOutput, Is.EqualTo(before));
                Assert.That(service.RunGit(30000, "show", "HEAD:Assets/other.txt").StandardOutput.TrimEnd('\r', '\n'), Is.EqualTo("base"));
                Assert.That(File.ReadAllText(Path.Combine(root, "Assets", "other.txt")), Is.EqualTo("unstaged"));
                Assert.That(service.RunGit(30000, "ls-tree", "-r", "HEAD").StandardOutput, Does.Not.Contain("unitypackage"));
                Assert.That(service.RunGit(30000, "show", "HEAD:Assets/version.json").StandardOutput.TrimEnd('\r', '\n'), Is.EqualTo("version"));
            }
            finally { DeleteTempFolder(root); }
        }

        [Test]
        public void FailedScopedReleaseLeavesEveryIndexEntryUnchanged()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root); ConfigureTempRepository(service);
                File.WriteAllText(Path.Combine(root, "Assets", "body.txt"), "base");
                AssertGit(service.StageAll()); AssertGit(service.Commit("initial"));
                File.WriteAllText(Path.Combine(root, "Assets", "body.txt"), "staged");
                AssertGit(service.StageAll());
                File.WriteAllText(Path.Combine(root, "Assets", "body.txt"), "unstaged");
                string before = service.RunGit(30000, "ls-files", "--stage", "-z").StandardOutput;
                string head = GetHead(service);
                var result = UnitGitReleases.PublishRelease(new UnitGitReleaseEntry { id = "failure-test" }, "release test",
                    service, "Assets/body.txt", "Assets/missing.txt");
                Assert.That(result.Success, Is.False);
                Assert.That(service.RunGit(30000, "ls-files", "--stage", "-z").StandardOutput, Is.EqualTo(before));
                Assert.That(GetHead(service), Is.EqualTo(head));
                Assert.That(File.ReadAllText(Path.Combine(root, "Assets", "body.txt")), Is.EqualTo("unstaged"));
                Assert.That(File.Exists(UnitGitReleases.GetReleasesFilePath(root)), Is.False);
            }
            finally { DeleteTempFolder(root); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FailedFullCheckpointPreservesStagedUnstagedAndUntrackedFiles(bool release)
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);
                string tracked = Path.Combine(root, "Assets", "body.txt");
                string releasePath = UnitGitReleases.GetReleasesFilePath(root);
                File.WriteAllText(tracked, "base");
                File.WriteAllText(releasePath, "{\"releases\":[]}");
                AssertGit(service.StageAll()); AssertGit(service.Commit("initial"));
                File.WriteAllText(tracked, "staged");
                File.WriteAllText(releasePath, "{\"releases\":[]}\n");
                AssertGit(service.StageAll());
                File.WriteAllText(tracked, "unstaged");
                File.WriteAllText(releasePath, "{\"releases\":[]}\n\n");
                File.WriteAllText(Path.Combine(root, "Assets", "new.txt"), "untracked");
                string before = GetPorcelainStatus(service);
                string entries = service.RunGit(30000, "ls-files", "--stage", "-z").StandardOutput;
                string contents = File.ReadAllText(releasePath);
                string head = GetHead(service);
                AssertGit(service.RunGit(10000, "config", "commit.gpgsign", "true"));
                AssertGit(service.RunGit(10000, "config", "gpg.format", "openpgp"));
                AssertGit(service.RunGit(10000, "config", "gpg.program", Path.Combine(root, "missing-gpg.exe")));
                var result = release
                    ? UnitGitReleases.PublishReleaseAll(new UnitGitReleaseEntry { id = "failed-checkpoint" }, "checkpoint", service, false)
                    : UnitGitReleases.CommitAll("checkpoint", "paragraph", service, false);
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Does.Contain("Commit failed"));
                Assert.That(GetHead(service), Is.EqualTo(head));
                Assert.That(GetPorcelainStatus(service), Is.EqualTo(before));
                Assert.That(service.RunGit(30000, "ls-files", "--stage", "-z").StandardOutput, Is.EqualTo(entries));
                Assert.That(File.ReadAllText(tracked), Is.EqualTo("unstaged"));
                Assert.That(File.ReadAllText(releasePath), Is.EqualTo(contents));
                Assert.That(Directory.GetFiles(Path.Combine(root, ".git"), "index.unitgit-*").Length, Is.Zero);
            }
            finally { DeleteTempFolder(root); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FullCheckpointRespectsExistingIndexLock(bool release)
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root); ConfigureTempRepository(service);
                AssertGit(service.StageAll()); AssertGit(service.Commit("initial"));
                string head = GetHead(service);
                string indexPath = Path.Combine(root, ".git", "index");
                byte[] index = File.ReadAllBytes(indexPath);
                string lockPath = indexPath + ".lock";
                File.WriteAllText(lockPath, "other process reservation");
                var result = release
                    ? UnitGitReleases.PublishReleaseAll(new UnitGitReleaseEntry { id = "locked" }, "checkpoint", service, false)
                    : UnitGitReleases.CommitAll("checkpoint", null, service, false);
                Assert.That(result.Success, Is.False);
                Assert.That(GetHead(service), Is.EqualTo(head));
                Assert.That(File.ReadAllBytes(indexPath), Is.EqualTo(index));
                Assert.That(File.ReadAllText(lockPath), Is.EqualTo("other process reservation"));
                Assert.That(File.Exists(UnitGitReleases.GetReleasesFilePath(root)), Is.False);
            }
            finally { DeleteTempFolder(root); }
        }

        [Test]
        public void PublishReleaseAllStagesProjectChangesAndReleaseTrailer()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                string assetPath = Path.Combine(root, "Assets", "avatar.txt");
                string settingsPath = Path.Combine(root, "ProjectSettings", "AvatarUpload.asset");
                File.WriteAllText(assetPath, "initial\n");
                File.WriteAllText(settingsPath, "initial\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("initial"));

                File.WriteAllText(assetPath, "uploaded avatar\n");
                File.WriteAllText(settingsPath, "uploaded settings\n");

                var entry = new UnitGitReleaseEntry
                {
                    id = "avatar-upload-test",
                    tool = "VRChat SDK",
                    type = "avatar upload",
                    name = "Test Avatar",
                    scope = "PC"
                };

                UnitGitReleaseResult result = UnitGitReleases.PublishReleaseAll(
                    entry,
                    "vrchat avatar upload: Test Avatar",
                    service,
                    false);

                Assert.That(result.Success, Is.True, result.Message);
                Assert.That(result.ReleaseId, Is.EqualTo("avatar-upload-test"));

                UnitGitCommitDetails details = service.GetCommitDetails(result.CommitHash);
                Assert.That(details.Commit.ReleaseId, Is.EqualTo("avatar-upload-test"));
                Assert.That(details.ChangedFiles, Does.Contain("Assets/avatar.txt"));
                Assert.That(details.ChangedFiles, Does.Contain("ProjectSettings/AvatarUpload.asset"));
                Assert.That(details.ChangedFiles, Does.Contain(UnitGitReleases.ReleasesFileName));

                UnitGitReleaseEntry saved = UnitGitReleases.FindById(UnitGitReleases.Load(root), "avatar-upload-test");
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved.type, Is.EqualTo("avatar upload"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void CommitMessageWithSpacesAndAmendUseMessageFile()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "one\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("message with spaces"));

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "two\n");
                AssertGit(service.StageAll());
                AssertGit(service.CommitAmend("amended message with spaces"));

                GitCommandResult message = service.GetHeadCommitMessage();
                AssertGit(message);
                Assert.That(message.StandardOutput, Is.EqualTo("amended message with spaces"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void HideReleaseStoresHiddenIdWithoutDeletingReleaseEntry()
        {
            string root = CreateTempUnityProjectFolder();
            try
            {
                var file = new UnitGitReleaseFile();
                file.releases.Add(new UnitGitReleaseEntry
                {
                    id = "release-hide-test",
                    name = "Hidden Release"
                });
                File.WriteAllText(UnitGitReleases.GetReleasesFilePath(root), UnityEngine.JsonUtility.ToJson(file, true));

                UnitGitReleaseResult result = UnitGitReleases.HideRelease(root, "release-hide-test");

                Assert.That(result.Success, Is.True, result.Message);
                UnitGitReleaseFile loaded = UnitGitReleases.Load(root);
                Assert.That(UnitGitReleases.FindById(loaded, "release-hide-test"), Is.Not.Null);
                Assert.That(UnitGitReleases.IsHidden(loaded, "release-hide-test"), Is.True);
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void RenameCommitRewritesSelectedCommitMessage()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "one\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("one"));
                string first = GetHead(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "two\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("two"));

                AssertGit(service.RenameCommit(first, "renamed one", GetHeadState(service)));

                GitCommandResult log = service.RunGit(30000, "log", "--format=%s");
                AssertGit(log);
                Assert.That(log.StandardOutput, Does.Contain("renamed one"));
                Assert.That(log.StandardOutput, Does.Contain("two"));
                Assert.That(log.StandardOutput, Does.Not.Contain("\none\n"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void RenameCommitKeepsSnapshotUsableWithLocalChanges()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                string assetPath = Path.Combine(root, "Assets", "file.txt");
                File.WriteAllText(assetPath, "one\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("one"));
                string first = GetHead(service);

                File.WriteAllText(assetPath, "two\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("two"));

                File.WriteAllText(assetPath, "dirty local change\n");

                AssertGit(service.RenameCommit(first, "renamed one", GetHeadState(service)));
                UnitGitSnapshot snapshot = service.BuildSnapshot(string.Empty);

                Assert.That(snapshot.HasRepository, Is.True);
                Assert.That(snapshot.HasCommits, Is.True);
                Assert.That(snapshot.LastError, Is.Empty);
                Assert.That(snapshot.Commits, Is.Not.Empty);
                Assert.That(snapshot.Changes.Any(change => change.Path == "Assets/file.txt"), Is.True);
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void SquashCommitsRewritesContiguousSelectionIntoOneCommit()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "one\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("one"));
                string first = GetHead(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "two\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("two"));
                string second = GetHead(service);

                AssertGit(service.SquashCommits(new[] { first, second }, "one and two", GetHeadState(service)));

                GitCommandResult log = service.RunGit(30000, "log", "--format=%s");
                AssertGit(log);
                Assert.That(log.StandardOutput, Does.Contain("one and two"));
                Assert.That(log.StandardOutput, Does.Not.Contain("\none\n"));
                Assert.That(log.StandardOutput, Does.Not.Contain("\ntwo\n"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void ResetCurrentBranchSupportsSoftMixedHardAndKeep()
        {
            RequireGit();

            string softRoot = CreateTempUnityProjectFolder();
            try
            {
                UnitGitService service = CreateTwoCommitRepo(softRoot, out string first, out _, out string assetPath);
                AssertGit(service.ResetCurrentBranch(first, UnitGitResetMode.Soft, GetHeadState(service)));
                Assert.That(GetPorcelainStatus(service), Does.Contain("M  Assets/file.txt"));
                Assert.That(File.ReadAllText(assetPath), Is.EqualTo("two\n"));
            }
            finally
            {
                DeleteTempFolder(softRoot);
            }

            string mixedRoot = CreateTempUnityProjectFolder();
            try
            {
                UnitGitService service = CreateTwoCommitRepo(mixedRoot, out string first, out _, out string assetPath);
                AssertGit(service.ResetCurrentBranch(first, UnitGitResetMode.Mixed, GetHeadState(service)));
                Assert.That(GetPorcelainStatus(service), Does.Contain(" M Assets/file.txt"));
                Assert.That(File.ReadAllText(assetPath), Is.EqualTo("two\n"));
            }
            finally
            {
                DeleteTempFolder(mixedRoot);
            }

            string hardRoot = CreateTempUnityProjectFolder();
            try
            {
                UnitGitService service = CreateTwoCommitRepo(hardRoot, out string first, out _, out string assetPath);
                AssertGit(service.ResetCurrentBranch(first, UnitGitResetMode.Hard, GetHeadState(service)));
                Assert.That(GetPorcelainStatus(service).Trim(), Is.Empty);
                Assert.That(File.ReadAllText(assetPath), Is.EqualTo("one\n"));
            }
            finally
            {
                DeleteTempFolder(hardRoot);
            }

            string keepRoot = CreateTempUnityProjectFolder();
            try
            {
                UnitGitService service = CreateTwoCommitRepo(keepRoot, out string first, out _, out string assetPath);
                File.WriteAllText(Path.Combine(keepRoot, "Assets", "local.txt"), "local\n");
                AssertGit(service.ResetCurrentBranch(first, UnitGitResetMode.Keep, GetHeadState(service)));
                Assert.That(GetPorcelainStatus(service), Does.Contain("?? Assets/local.txt"));
                Assert.That(File.ReadAllText(assetPath), Is.EqualTo("one\n"));
            }
            finally
            {
                DeleteTempFolder(keepRoot);
            }
        }

        private static void RequireGit()
        {
            var service = new UnitGitService(Path.GetTempPath());
            if (!service.IsGitAvailable())
            {
                Assert.Ignore("Git is not available on PATH.");
            }
        }

        private static string CreateTempFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "unitgit-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static string CreateTempUnityProjectFolder()
        {
            string root = CreateTempFolder();
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "Packages"));
            File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), "{}\n");
            return root;
        }

        private static UnitGitService CreateTwoCommitRepo(string root, out string first, out string second, out string assetPath)
        {
            var service = new UnitGitService(root);
            ConfigureTempRepository(service);

            assetPath = Path.Combine(root, "Assets", "file.txt");
            File.WriteAllText(assetPath, "one\n");
            AssertGit(service.StageAll());
            AssertGit(service.Commit("one"));
            first = GetHead(service);

            File.WriteAllText(assetPath, "two\n");
            AssertGit(service.StageAll());
            AssertGit(service.Commit("two"));
            second = GetHead(service);

            return service;
        }

        private static string GetPorcelainStatus(UnitGitService service)
        {
            GitCommandResult result = service.RunGit(30000, "status", "--porcelain=v1", "-uall");
            AssertGit(result);
            return result.StandardOutput;
        }

        private static void ConfigureTempRepository(UnitGitService service)
        {
            AssertGit(service.RunGit(30000, "init"));
            AssertGit(service.RunGit(30000, "config", "user.email", "unitgit@example.test"));
            AssertGit(service.RunGit(30000, "config", "user.name", "Mixer Git Tests"));
            AssertGit(service.RunGit(30000, "config", "core.autocrlf", "false"));
            AssertGit(service.RunGit(30000, "config", "commit.gpgsign", "false"));
            string hooks = Path.Combine(service.ProjectRoot, ".git", "audit-empty-hooks");
            Directory.CreateDirectory(hooks);
            AssertGit(service.RunGit(30000, "config", "core.hooksPath", hooks));
        }

        private static UnitGitHeadState GetHeadState(UnitGitService service)
        {
            AssertGit(service.ReadHeadState(out UnitGitHeadState state));
            return state;
        }

        private static string GetHead(UnitGitService service)
        {
            GitCommandResult result = service.RunGit(30000, "rev-parse", "HEAD");
            AssertGit(result);
            return result.StandardOutput.Trim();
        }

        private static void DeleteTempFolder(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            File.SetAttributes(root, FileAttributes.Normal);
            Directory.Delete(root, true);
        }

        private static void AssertGit(GitCommandResult result)
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Success, Is.True, result.Message);
        }
    }
}
