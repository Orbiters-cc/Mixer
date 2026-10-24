using System;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitParsingTests
    {
        [Test]
        public void QuotedStatusPathsDecodeUtf8AndPreserveFilenameCharacters()
        {
            var snapshot = UnitGitService.ParseStatusOutput(
                "?? \"caf\\303\\251.txt\"\n" +
                "??   leading space.txt\n" +
                "?? literal -> arrow.txt\n" +
                "R  \"old -> caf\\303\\251.txt\" -> \"new\\tname.txt\"\n");
            Assert.That(snapshot.Changes.Select(change => change.Path), Is.EqualTo(new[]
            { "caf\u00e9.txt", "  leading space.txt", "literal -> arrow.txt", "new\tname.txt" }));
            Assert.That(snapshot.Changes[2].OriginalPath, Is.Empty);
            Assert.That(snapshot.Changes[3].OriginalPath, Is.EqualTo("old -> caf\u00e9.txt"));
            Assert.That(UnitGitService.UnquotePath("\"\\346\\234\\215\\360\\237\\220\\276\\\\\\\".txt\""),
                Is.EqualTo("\u670d\U0001F43E\\\".txt"));
        }

        [Test]
        public void ParseStatusPreservesBranchTrackingRenamesAndMixedStates()
        {
            string output =
                "## feature/test...origin/feature/test [ahead 2, behind 1]\n" +
                "MM Assets/Scripts/Test.cs\n" +
                "R  \"Assets/old name.txt\" -> \"Assets/new name.txt\"\n" +
                "?? \"Assets/untracked file.txt\"\n";

            UnitGitSnapshot snapshot = UnitGitService.ParseStatusOutput(output);

            Assert.That(snapshot.CurrentBranch, Is.EqualTo("feature/test"));
            Assert.That(snapshot.Upstream, Is.EqualTo("origin/feature/test"));
            Assert.That(snapshot.Ahead, Is.EqualTo(2));
            Assert.That(snapshot.Behind, Is.EqualTo(1));
            Assert.That(snapshot.Changes, Has.Count.EqualTo(3));

            UnitGitStatusEntry mixed = snapshot.Changes[0];
            Assert.That(mixed.IsStaged, Is.True);
            Assert.That(mixed.IsUnstaged, Is.True);

            UnitGitStatusEntry renamed = snapshot.Changes[1];
            Assert.That(renamed.OriginalPath, Is.EqualTo("Assets/old name.txt"));
            Assert.That(renamed.Path, Is.EqualTo("Assets/new name.txt"));

            Assert.That(snapshot.Changes[2].IsUntracked, Is.True);
        }

        [Test]
        public void ParseBranchesSortsBranchesAndSkipsRemoteHead()
        {
            string output =
                "refs/remotes/origin/HEAD\torigin/HEAD\t \t\tabc0000\n" +
                "refs/remotes/origin/main\torigin/main\t \t\tabc1111\n" +
                "refs/heads/main\tmain\t*\torigin/main\tabc2222\n" +
                "refs/heads/feature/z\tfeature/z\t \t\tabc3333\n";

            var branches = UnitGitService.ParseBranches(output);

            Assert.That(branches.Select(branch => branch.Name), Is.EqualTo(new[] { "feature/z", "main", "origin/main" }));
            Assert.That(branches[1].IsCurrent, Is.True);
            Assert.That(branches[2].IsRemote, Is.True);
        }

        [Test]
        public void ParseBranchesDropsTheDetachedHeadPseudoBranch()
        {
            string output =
                "(HEAD detached at abc1234)\t(HEAD detached at abc1234)\t*\t\tabc1234\n" +
                "refs/heads/main\tmain\t \torigin/main\tabc2222\n";

            var branches = UnitGitService.ParseBranches(output);

            Assert.That(branches.Select(branch => branch.Name), Is.EqualTo(new[] { "main" }));
            Assert.That(branches.Any(branch => branch.IsCurrent), Is.False);
        }

        [Test]
        public void ParseStatusReportsADetachedHeadWithoutABranch()
        {
            UnitGitSnapshot detached = UnitGitService.ParseStatusOutput("## HEAD (no branch)\n M Assets/file.txt\n");
            Assert.That(detached.IsDetached, Is.True);
            Assert.That(detached.CurrentBranch, Is.Empty);
            Assert.That(detached.Upstream, Is.Empty);
            Assert.That(detached.Changes.Select(change => change.Path), Is.EqualTo(new[] { "Assets/file.txt" }));

            UnitGitSnapshot onBranch = UnitGitService.ParseStatusOutput("## main...origin/main [behind 3]\n");
            Assert.That(onBranch.IsDetached, Is.False);
            Assert.That(onBranch.CurrentBranch, Is.EqualTo("main"));
            Assert.That(onBranch.Behind, Is.EqualTo(3));

            Assert.That(UnitGitService.ParseStatusOutput("## No commits yet on main\n").IsDetached, Is.False);
        }

        [Test]
        public void OnlyTheCheckedOutCommitIsHeadNotARemotesDefaultBranch()
        {
            Assert.That(UnitGitCommitGraph.IsHead("HEAD -> main, origin/main"), Is.True);
            Assert.That(UnitGitCommitGraph.IsHead("HEAD"), Is.True);
            Assert.That(UnitGitCommitGraph.IsHead("HEAD, tag: v1.0"), Is.True);
            Assert.That(UnitGitCommitGraph.IsHead("origin/main, origin/HEAD"), Is.False);
            Assert.That(UnitGitCommitGraph.IsHead("tag: HEADLINE, main"), Is.False);
            Assert.That(UnitGitCommitGraph.IsHead(string.Empty), Is.False);
        }

        [Test]
        public void FindShelfMatchesTheWholeMessageOnAnyBranch()
        {
            string list = "stash@{0}\x1fOn (no branch): Changed while visiting abc1234 (2026-10-20 12:00:01)\n" +
                          "stash@{1}\x1fOn main: Before visiting abc1234 (2026-10-20 12:00:00)\n" +
                          "stash@{2}\x1fOn main: older Before visiting abc1234 (2026-10-20 12:00:00) copy\n";
            Assert.That(UnitGitService.FindShelfIn(list, "Before visiting abc1234 (2026-10-20 12:00:00)"), Is.EqualTo("stash@{1}"));
            Assert.That(UnitGitService.FindShelfIn(list, "Before visiting abc1234"), Is.Empty);
            Assert.That(UnitGitService.FindShelfIn(list, string.Empty), Is.Empty);
        }

        [Test]
        public void ParseCommitsFiltersBySubjectAuthorHashAndDecorations()
        {
            const string separator = "\x1f";
            string output = string.Join("\n", new[]
            {
                string.Join(separator, "a1b2c3d", "full-1", "Add graph", "Ada", "ada@example.test", "2 days ago", "HEAD -> main"),
                string.Join(separator, "d4e5f6g", "full-2", "Fix window", "Grace", "grace@example.test", "1 day ago", "tag: v1")
            });

            var subjectMatches = UnitGitService.ParseCommits(output, "graph");
            var decorationMatches = UnitGitService.ParseCommits(output, "v1");

            Assert.That(subjectMatches, Has.Count.EqualTo(1));
            Assert.That(subjectMatches[0].FullHash, Is.EqualTo("full-1"));
            Assert.That(decorationMatches, Has.Count.EqualTo(1));
            Assert.That(decorationMatches[0].AuthorName, Is.EqualTo("Grace"));
        }

        [Test]
        public void ParseStashListDropsBlankLines()
        {
            var shelves = UnitGitService.ParseStashList("stash@{0}: On main: one\n\nstash@{1}: On main: two\n");

            Assert.That(shelves, Is.EqualTo(new[] { "stash@{0}: On main: one", "stash@{1}: On main: two" }));
        }

        [Test]
        public void ParseUnifiedDiffPairsChangedLinesAndCountsAdditions()
        {
            string output =
                "diff --git a/file.txt b/file.txt\n" +
                "@@ -1,2 +1,3 @@\n" +
                "-old\n" +
                "+new\n" +
                " context\n" +
                "+added\n";

            UnitGitDiff diff = UnitGitService.ParseUnifiedDiff(output);

            Assert.That(diff.DifferenceCount, Is.EqualTo(2));
            Assert.That(diff.Lines.Any(line => line.Kind == UnitGitDiffLineKind.Changed && line.Left == "old" && line.Right == "new"), Is.True);
            Assert.That(diff.Lines.Any(line => line.Kind == UnitGitDiffLineKind.Added && line.Right == "added"), Is.True);
        }

        [Test]
        public void EscapeArgumentDoesNotDoubleOrdinaryWindowsPathBackslashes()
        {
            const string path = @"H:\metaverse\unity projects\MCB Test";

            string escaped = UnitGitService.EscapeArgument(path);
            string command = UnitGitService.FormatCommandLine("gh", "repo", "create", "--source", path);

            Assert.That(escaped, Is.EqualTo("\"" + path + "\""));
            Assert.That(command, Does.Contain("\"" + path + "\""));
            Assert.That(command, Does.Not.Contain(@"H:\\metaverse\\unity projects\\MCB Test"));
        }
    }
}
