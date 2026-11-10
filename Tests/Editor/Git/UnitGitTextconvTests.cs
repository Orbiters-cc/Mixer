using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // A read never runs the user's textconv driver: with diff.<driver>.cachetextconv, Git writes the converted text to
    // refs/notes/textconv/<driver>, which made a "read" diff write. Mixer Git's diffs, details and shelf lists pass
    // --no-textconv; plain git diff, run by the test, shows the driver does run when allowed to.
    public sealed class UnitGitTextconvTests
    {
        [Test]
        public void ReadsRunNoTextconvDriverAndWriteNoTextconvNotes()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                // The driver leaves a mark in the repository's folder each time it runs, then prints the file.
                repo.Run("config", "diff.marker.textconv", "sh -c 'echo ran >> textconv-ran.txt; cat \"$1\"' -");
                repo.Run("config", "diff.marker.cachetextconv", "true");
                repo.Write(".gitattributes", "*.mark diff=marker\n");
                repo.Write("Assets/a.mark", "one\n");
                string first = repo.Commit("first");
                repo.Write("Assets/a.mark", "two\n");
                string second = repo.Commit("second");
                repo.Write("Assets/a.mark", "three\n");
                repo.Write("Assets/new.txt", "x\n");
                repo.Git.Stage(new[] { "Assets/new.txt" });

                repo.Git.GetFileDiff(new UnitGitStatusEntry { Path = "Assets/a.mark", WorkTreeStatus = 'M' });
                repo.Git.GetCommitFileDiff(second, "Assets/a.mark");
                repo.Git.GetCommitDetails(second);
                repo.Git.GetCommitDetails(new[] { first, second });
                repo.Git.BuildSnapshot(string.Empty, 10);
                Assert.That(File.Exists(repo.Full("textconv-ran.txt")), Is.False, "No read ran the textconv driver.");
                Assert.That(repo.Run("for-each-ref", "refs/notes/").Trim(), Is.Empty, "No read wrote a textconv note.");

                // The driver works when Git is allowed to use it: the checks above could fail.
                repo.Run("diff", first, second, "--", "Assets/a.mark");
                Assert.That(File.Exists(repo.Full("textconv-ran.txt")), Is.True, "Plain git diff ran the driver.");
                Assert.That(repo.Run("for-each-ref", "refs/notes/").Trim(), Does.Contain("refs/notes/textconv/marker"));
            }
        }
    }
}
