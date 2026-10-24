using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitMcbDownloadsTests
    {
        private const string Patch = "Assets/MCB/assets/14/versions/u1.0.0/Body.fbx.bin";
        private const string Avatar = "Assets/MCB/assets/14/versions/u1.0.0/Body.fbx avatar.asset";

        // An existing repository that committed MCB downloads: the rule is added, the snapshot lists the tracked patch
        // (not the avatar definition next to it), and stopping tracking keeps the file on disk.
        [Test]
        public void ExistingRepositoryGetsTheRuleAndCanStopTrackingDownloads()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write(Patch, "patch bytes");
                repo.Write(Patch + ".meta", "guid: 1");
                repo.Write(Avatar, "avatar");
                repo.Commit("MCB version");

                var snapshot = repo.Git.BuildSnapshot(string.Empty);
                StringAssert.Contains("/[Aa]ssets/MCB/assets/*/versions/**/*.bin", repo.Read(".gitignore"));
                CollectionAssert.AreEquivalent(new[] { Patch, Patch + ".meta" }, snapshot.TrackedIgnoredDownloads);
                Assert.That(snapshot.TrackedIgnoredDownloadBytes, Is.GreaterThan(0));

                Assert.That(repo.Git.StopTracking(snapshot.TrackedIgnoredDownloads).Success, Is.True);
                Assert.That(repo.Read(Patch), Is.EqualTo("patch bytes"));
                repo.Commit("stop tracking");
                Assert.That(repo.Run("ls-files", "--", "Assets/MCB").Trim(), Is.EqualTo("\"" + Avatar + "\"").Or.EqualTo(Avatar));
                Assert.That(repo.Git.BuildSnapshot(string.Empty).TrackedIgnoredDownloads, Is.Empty);
            }
        }

        [Test]
        public void ProjectsWithoutMcbKeepTheirIgnoreFile()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/file.txt", "one");
                repo.Commit("one");
                repo.Git.BuildSnapshot(string.Empty);
                Assert.That(System.IO.File.Exists(repo.Full(".gitignore")), Is.False);
            }
        }
    }
}
