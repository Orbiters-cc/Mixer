using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Processes;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // The refresh read in slices: porcelain v2 status, one for-each-ref, the history only when refs or filters changed and
    // read on as it streams, the remotes, shelves and HEAD's message only when their own files changed or when asked.
    public sealed class UnitGitRefreshTests
    {
        [Test]
        public void StatusV2HeadersAndEntriesReadAsV1Did()
        {
            string output = string.Join("\0",
                "# branch.oid 0a7b6ff8604a4dd208f5e043d986b6e76af226e9",
                "# branch.head main",
                "# branch.upstream origin/main",
                "# branch.ab +2 -3",
                "1 .M N... 100644 100644 100644 aaaa aaaa Assets/with space.txt",
                "1 A. N... 000000 100644 100644 0000 bbbb Assets/-dash é.txt",
                "2 R. N... 100644 100644 100644 cccc cccc R100 Assets/new name.txt",
                "Assets/old name.txt",
                "u UU N... 100644 100644 100644 100644 d1 d2 d3 Assets/conflict.unity",
                "? Assets/new\nline.txt",
                "") ;
            UnitGitSnapshot snapshot = UnitGitService.ParseStatusV2Output(output);
            Assert.That(snapshot.Head, Is.EqualTo("0a7b6ff8604a4dd208f5e043d986b6e76af226e9"));
            Assert.That(snapshot.HasCommits, Is.True);
            Assert.That(snapshot.CurrentBranch, Is.EqualTo("main"));
            Assert.That(snapshot.Upstream, Is.EqualTo("origin/main"));
            Assert.That(snapshot.Ahead, Is.EqualTo(2));
            Assert.That(snapshot.Behind, Is.EqualTo(3));
            Assert.That(snapshot.Changes.Select(c => c.IndexStatus + "" + c.WorkTreeStatus + " " + c.Path + "|" + c.OriginalPath), Is.EqualTo(new[]
            {
                " M Assets/with space.txt|", "A  Assets/-dash é.txt|", "R  Assets/new name.txt|Assets/old name.txt",
                "UU Assets/conflict.unity|", "?? Assets/new\nline.txt|"
            }));

            UnitGitSnapshot detached = UnitGitService.ParseStatusV2Output("# branch.oid 0a7b\0# branch.head (detached)\0");
            Assert.That(detached.IsDetached, Is.True);
            Assert.That(detached.CurrentBranch, Is.Empty);
            UnitGitSnapshot initial = UnitGitService.ParseStatusV2Output("# branch.oid (initial)\0# branch.head main\0");
            Assert.That(initial.HasCommits, Is.False);
            Assert.That(initial.IsDetached, Is.False);
            Assert.That(initial.CurrentBranch, Is.EqualTo("main"));
        }

        [Test]
        public void TheSnapshotSeesWhatStatusV1Saw()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/moved.txt", "moved\n");
                repo.Write("Assets/edited.txt", "one\n");
                repo.Write("Assets/gone.txt", "gone\n");
                repo.Commit("base");
                Directory.CreateDirectory(repo.Full("Assets/Folder with space"));
                repo.Run("mv", "Assets/moved.txt", "Assets/Folder with space/moved é.txt");
                repo.Write("Assets/edited.txt", "two\n");
                File.Delete(repo.Full("Assets/gone.txt"));
                repo.Write("Assets/-new/[x] ü.txt", "new\n");
                repo.Run("add", "Assets/edited.txt");
                repo.Write("Assets/edited.txt", "three\n");

                UnitGitSnapshot snapshot = repo.Git.BuildSnapshot(string.Empty);
                var v1 = UnitGitService.ParseStatusOutput(repo.Run("status", "--porcelain=v1", "-b", "-uall", "--find-renames=100%"));
                Assert.That(Describe(snapshot.Changes), Is.EqualTo(Describe(v1.Changes)));
                Assert.That(snapshot.CurrentBranch, Is.EqualTo(v1.CurrentBranch));
                Assert.That(snapshot.Head, Is.EqualTo(repo.Run("rev-parse", "HEAD").Trim()));
                Assert.That(snapshot.Branches.Select(b => b.FullRef), Is.EqualTo(new[] { "refs/heads/main" }), "Tags and the stash are no branches.");
            }
        }

        [Test]
        public void SlicesAreReadAgainOnlyWhenTheirFilesChanged()
        {
            using (var repo = UnitGitTestRepo.Create())
            using (var history = new UnitGitHistory())
            {
                repo.Write("Assets/a.txt", "one\n");
                repo.Commit("first");
                repo.Run("tag", "v1");
                var commands = new List<string>();
                repo.Git.ProcessLogReceived = line => { if (line.StartsWith("> ", StringComparison.Ordinal)) commands.Add(line); };
                UnitGitSnapshot Read(UnitGitSnapshot previous, bool headMessage = false)
                {
                    commands.Clear();
                    return repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { Previous = previous, History = history, ReadHeadMessage = headMessage });
                }

                UnitGitSnapshot first = Read(null);
                Assert.That(commands.Any(line => line.Contains(" log ")), Is.True);
                Assert.That(commands.Any(line => line.EndsWith(" remote", StringComparison.Ordinal)), Is.True);
                Assert.That(first.HeadMessage, Is.Null, "HEAD's message is read only when asked for.");

                repo.Write("Assets/a.txt", "edited\n");
                UnitGitSnapshot second = Read(first);
                Assert.That(commands.Any(line => line.Contains(" log ")), Is.False, "Same refs: the history is not read again.");
                Assert.That(commands.Any(line => line.EndsWith(" remote", StringComparison.Ordinal)), Is.False, "Same config: the remotes are not read again.");
                Assert.That(commands.Any(line => line.Contains(" stash list")), Is.False, "Same stash: the shelves are not read again.");
                Assert.That(commands.Any(line => line.Contains(" rev-parse --verify HEAD")), Is.False, "Status tells whether there are commits.");
                Assert.That(Hashes(second.Commits), Is.EqualTo(Hashes(first.Commits)));
                Assert.That(second.Changes.Single().Path, Is.EqualTo("Assets/a.txt"));

                string newer = repo.Commit("second");
                repo.Run("remote", "add", "origin", "https://example.test/repo.git");
                repo.Run("stash", "push", "--include-untracked", "-m", "nothing to keep");
                UnitGitSnapshot third = Read(second, headMessage: true);
                Assert.That(third.Commits[0].FullHash, Is.EqualTo(newer), "A new commit reads the history again.");
                Assert.That(third.Remotes, Is.EqualTo(new[] { "origin" }));
                Assert.That(third.HeadMessage, Is.EqualTo("second"));

                UnitGitSnapshot fourth = Read(third, headMessage: true);
                Assert.That(commands.Any(line => line.Contains(" log -1 --format=%B")), Is.False, "Same HEAD: its message is kept.");
                Assert.That(fourth.HeadMessage, Is.EqualTo("second"));
            }
        }

        [Test]
        public void LoadMoreAndSearchReadOnFromWhereTheHistoryStopped()
        {
            using (var repo = UnitGitTestRepo.Create())
            using (var history = new UnitGitHistory())
            {
                repo.Write("Assets/a.txt", "0\n");
                repo.Commit("ancient needle");
                for (int i = 1; i < 45; i++) repo.Run("commit", "-q", "--allow-empty", "-m", "commit " + i);

                UnitGitSnapshot first = repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { History = history, HistoryLimit = 10 });
                Assert.That(first.Commits.Count, Is.EqualTo(10));
                Assert.That(first.HasMoreCommits, Is.True);
                UnitGitSnapshot more = repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { History = history, HistoryLimit = 20, Previous = first, HistoryOnly = true });
                Assert.That(more.Commits.Count, Is.EqualTo(20));
                Assert.That(Hashes(more.Commits.Take(10)), Is.EqualTo(Hashes(first.Commits)));
                Assert.That(more.Changes, Is.SameAs(first.Changes), "Load more reads no status.");
                UnitGitSnapshot all = repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { History = history, HistoryLimit = 100, Previous = more, HistoryOnly = true });
                Assert.That(all.Commits.Count, Is.EqualTo(45));
                Assert.That(all.HasMoreCommits, Is.False);
                Assert.That(history.ProcessesStarted, Is.EqualTo(3), "One git log per page, each starting after the last commit read (nothing outlives a read).");
                Assert.That(Hashes(all.Commits), Is.EqualTo(Hashes(repo.Git.GetCommits(string.Empty, 1000))));

                UnitGitSnapshot found = repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { History = history, LogSearch = "ancient needle" });
                Assert.That(found.Commits.Single().Subject, Is.EqualTo("ancient needle"));
                Assert.That(history.ProcessesStarted, Is.EqualTo(4), "A search is one more git log, read to its end once.");
            }
        }

        [Test]
        public void ReadingStopsWhenItsReaderIsDisposedAndNoProcessIsLeft()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "0\n");
                repo.Commit("first");
                for (int i = 1; i < 30; i++) repo.Run("commit", "-q", "--allow-empty", "-m", "c" + i);
                int before = EditorProcessRunner.ActiveCount;
                var history = new UnitGitHistory();
                repo.Git.BuildSnapshot(new UnitGitSnapshotRequest { History = history, HistoryLimit = 5 });
                Assert.That(EditorProcessRunner.ActiveCount, Is.EqualTo(before), "No Git process outlives the read.");
                history.Dispose();
                Assert.That(EditorProcessRunner.ActiveCount, Is.EqualTo(before));
                Assert.That(history.Read(repo.Root, "k", new[] { "--all", "--" }, string.Empty, 5, null, out _, out _).Success, Is.False,
                    "A disposed reader reads nothing.");
            }
        }

        [Test]
        public void AgesAreWordedAsGitWordsThem()
        {
            const long now = 2000000000;
            // Expected values from "git log --format=%ar" for the same ages (checked against Git 2.45).
            var cases = new Dictionary<long, string>
            {
                { 0, "0 seconds ago" }, { 1, "1 second ago" }, { 89, "89 seconds ago" }, { 90, "2 minutes ago" },
                { 5370, "2 hours ago" }, { 3599, "60 minutes ago" }, { 86400, "24 hours ago" }, { 129600, "2 days ago" },
                { 14 * 86400, "2 weeks ago" }, { 69 * 86400, "10 weeks ago" }, { 70 * 86400, "2 months ago" },
                { 364 * 86400, "12 months ago" }, { 365 * 86400, "1 year ago" }, { 400 * 86400, "1 year, 1 month ago" },
                { 1000 * 86400, "2 years, 9 months ago" }, { 1825 * 86400, "5 years ago" }, { 4000 * 86400, "11 years ago" }
            };
            foreach (var item in cases)
                Assert.That(UnitGitTime.GitRelative(now - item.Key, now), Is.EqualTo(item.Value), item.Key + " s");
            Assert.That(UnitGitTime.GitRelative(now + 5, now), Is.EqualTo("in the future"));
            Assert.That(new UnitGitCommit { Timestamp = 0 }.RelativeDate, Is.Empty);
        }

        private static List<string> Describe(IEnumerable<UnitGitStatusEntry> changes) =>
            changes.Select(c => c.IndexStatus + "" + c.WorkTreeStatus + " " + c.Path + "|" + c.OriginalPath).OrderBy(x => x, StringComparer.Ordinal).ToList();

        private static List<string> Hashes(IEnumerable<UnitGitCommit> commits) => commits.Select(commit => commit.FullHash).ToList();
    }
}
