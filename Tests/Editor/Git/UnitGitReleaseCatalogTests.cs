using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitReleaseCatalogTests
    {
        // While one release is being recorded, another one (or hiding a row) from elsewhere in the editor is refused
        // instead of writing over its catalog entry or rolling the catalog back under it.
        [Test]
        public void SecondReleaseWhileOneIsRecordedIsRefused()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                repo.Commit("initial");
                repo.Write("Assets/a.txt", "release a\n");
                UnitGitReleaseResult second = null, hide = null;
                repo.Git.ProcessLogReceived = line =>
                {
                    if (second != null || !line.StartsWith("> git commit ", StringComparison.Ordinal)) return;
                    // Other threads of the editor, while the first release commits.
                    second = Task.Run(() => UnitGitReleases.PublishRelease(new UnitGitReleaseEntry { id = "second" }, "second",
                        new UnitGitService(repo.Root), "Assets/a.txt")).Result;
                    hide = Task.Run(() => UnitGitReleases.HideRelease(repo.Root, "first")).Result;
                };
                var first = UnitGitReleases.PublishRelease(new UnitGitReleaseEntry { id = "first" }, "first", repo.Git, "Assets/a.txt");
                repo.Git.ProcessLogReceived = null;

                Assert.That(first.Success, Is.True, first.Message);
                Assert.That(second, Is.Not.Null, "The second release ran while the first one was committing.");
                Assert.That(second.Success, Is.False);
                Assert.That(second.Message, Does.Contain("Another release"));
                Assert.That(hide.Success, Is.False);
                var saved = UnitGitReleases.Load(repo.Root);
                Assert.That(saved.releases.Select(release => release.id), Is.EqualTo(new[] { "first" }));
                Assert.That(saved.hiddenReleaseIds, Is.Empty);
                Assert.That(repo.Run("status", "--porcelain", "--", UnitGitReleases.ReleasesFileName).Trim(), Is.Empty, "The committed catalog is the one on disk.");
            }
        }

        // A failed release puts the catalog back only while it still holds what the release wrote, and never rolls back
        // the index (a failed checkpoint stages in a private copy of it).
        [Test]
        public void FailedReleaseKeepsCatalogChangesMadeMeanwhile()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                string head = repo.Commit("initial");
                repo.Write("Assets/a.txt", "release a\n");
                // Every commit fails from here on, as a hook or a signing problem can make it.
                File.WriteAllText(Path.Combine(repo.Hooks, "pre-commit"), "#!/bin/sh\nexit 1\n");
                string path = UnitGitReleases.GetReleasesFilePath(repo.Root);
                const string edited = "{\"releases\":[],\"hiddenReleaseIds\":[\"edited meanwhile\"]}";
                string index = repo.Run("ls-files", "--stage");
                bool written = false;
                repo.Git.ProcessLogReceived = line =>
                {
                    if (written || !line.StartsWith("> git commit ", StringComparison.Ordinal)) return;
                    // Another program saves the catalog while the release commits.
                    written = true;
                    File.WriteAllText(path, edited);
                };
                var result = UnitGitReleases.PublishRelease(new UnitGitReleaseEntry { id = "failed" }, "release", repo.Git, "Assets/a.txt");
                repo.Git.ProcessLogReceived = null;

                Assert.That(written, Is.True);
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Does.Contain("left it as it is"));
                Assert.That(File.ReadAllText(path), Is.EqualTo(edited));
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(head));
                Assert.That(repo.Run("ls-files", "--stage"), Is.EqualTo(index));
            }
        }
    }
}
