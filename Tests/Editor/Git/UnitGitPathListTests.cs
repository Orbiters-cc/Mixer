using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Path lists outside staging: shelf file lists read with -z, and a checkpoint's scoped index taking any number of paths.
    public sealed class UnitGitPathListTests
    {
        [Test]
        public void ShelfFilesKeepNonAsciiNamesExact()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/tête.txt", "one\n");
                repo.Write("Assets/old name.txt", "same content\n");
                repo.Commit("base");
                repo.Write("Assets/tête.txt", "two\n");
                repo.Run("mv", "Assets/old name.txt", "Assets/new é.txt");
                repo.Write("Assets/新しい.txt", "new\n");
                repo.Run("stash", "push", "-u", "-m", "shelf");

                var tracked = UnitGitWindow.ParseNameStatus(repo.Run("stash", "show", "--name-status", "-z", "-M", "stash@{0}"));
                Assert.That(tracked.Select(file => file.status + " " + file.path), Is.EquivalentTo(new[] { "M Assets/tête.txt", "R Assets/new é.txt" }));
                var untracked = UnitGitWindow.ParseNameStatus(repo.Run("show", "--name-status", "-z", "--format=", "stash@{0}^3"));
                Assert.That(untracked.Select(file => file.status + " " + file.path), Is.EqualTo(new[] { "A Assets/新しい.txt" }));
            }
        }

        [Test]
        public void AScopedIndexFiltersAndStagesMorePathsThanACommandLineHolds()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write(".gitignore", "*.ignored\n");
                repo.Commit("base");
                var paths = new List<string>();
                for (int i = 0; i < 400; i++)
                {
                    string path = "Assets/A rather long folder name for a checkpoint/-é " + i.ToString("000") + (i % 50 == 0 ? ".ignored" : ".txt");
                    repo.Write(path, i + "\n");
                    paths.Add(path);
                }

                using (var index = new UnitGitScopedIndex(repo.Git))
                {
                    string[] included = index.FilterIgnored(paths.ToArray());
                    Assert.That(included, Is.EqualTo(paths.Where(path => !path.EndsWith(".ignored")).ToArray()));
                    GitCommandResult add = index.RunForPaths(included, "add");
                    Assert.That(add.Success, Is.True, add.Message);
                    string staged = index.Run(GitAccess.Read, "diff", "--cached", "--name-only", "-z").StandardOutput;
                    Assert.That(staged.Split('\0').Count(path => path.Length > 0), Is.EqualTo(included.Length));
                }

                Assert.That(repo.Run("diff", "--cached", "--name-only").Trim(), Is.Empty, "The project's own index is untouched.");
            }
        }
    }
}
