using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // The repository's lease: one operation that changes it at a time, every command of the operation under it, fetch and
    // push only between operations, the editor thread never waiting, and the lease released whatever the end.
    public sealed class UnitGitLeaseTests
    {
        // A thread that starts like a separate request (a click), not inside the operation that runs now.
        private static Thread Independent(Action work)
        {
            Thread thread;
            using (ExecutionContext.SuppressFlow()) thread = new Thread(() => work()) { IsBackground = true };
            using (ExecutionContext.SuppressFlow()) thread.Start();
            return thread;
        }

        private static UnitGitTestRepo History(out List<string> hashes)
        {
            var repo = UnitGitTestRepo.Create();
            hashes = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                repo.Write("Assets/f.txt", i + "\n");
                hashes.Add(repo.Commit("c" + i));
            }
            return repo;
        }

        [Test]
        public void AFetchRequestedDuringARewriteStartsOnlyAfterItEnded()
        {
            string remote = UnitGitTestRepo.TempFolder();
            using (var repo = History(out var hashes))
            try
            {
                // Something to fetch from.
                repo.Run("clone", "--bare", "--quiet", repo.Root, System.IO.Path.Combine(remote, "origin.git"));
                repo.Run("remote", "add", "origin", System.IO.Path.Combine(remote, "origin.git"));
                var clock = Stopwatch.StartNew();
                long renameEnded = -1, fetchStarted = -1;
                Thread fetching = null;
                repo.Git.ProcessLogReceived = line =>
                {
                    if (fetching != null || !line.Contains(" fast-import ")) return;
                    // While the rewrite writes its commits, another request asks for a fetch.
                    var other = new UnitGitService(repo.Root)
                    {
                        ProcessLogReceived = fetchLine => { if (fetchLine.Contains("> git fetch") && fetchStarted < 0) fetchStarted = clock.ElapsedTicks; }
                    };
                    fetching = Independent(() => other.Fetch());
                };
                GitCommandResult renamed = null;
                var renaming = Independent(() =>
                {
                    repo.Git.ReadHeadState(out UnitGitHeadState head);
                    renamed = repo.Git.RenameCommit(hashes[1], "renamed", head);
                    renameEnded = clock.ElapsedTicks;
                });
                Assert.That(renaming.Join(60000), Is.True);
                Assert.That(fetching, Is.Not.Null, "The fetch was requested during the rewrite.");
                Assert.That(fetching.Join(60000), Is.True);
                repo.Git.ProcessLogReceived = null;
                Assert.That(renamed.Success, Is.True, renamed.Message);
                Assert.That(fetchStarted, Is.GreaterThan(0), "The fetch ran.");
                Assert.That(fetchStarted, Is.GreaterThanOrEqualTo(renameEnded), "The fetch started only after the rewrite had ended.");
            }
            finally
            {
                UnitGitTestRepo.DeleteTempFolder(remote);
            }
        }

        [Test]
        public void TwoOperationsRequestedTogetherRunOneAfterTheOther()
        {
            var spans = new List<(long start, long end)>();
            var clock = Stopwatch.StartNew();
            using (var go = new ManualResetEventSlim(false))
            {
                var threads = Enumerable.Range(0, 3).Select(_ => Independent(() =>
                {
                    go.Wait();
                    UnitGitLease.Hold(() =>
                    {
                        long start = clock.ElapsedTicks;
                        Thread.Sleep(150);
                        lock (spans) spans.Add((start, clock.ElapsedTicks));
                        return new GitCommandResult();
                    });
                })).ToList();
                go.Set();
                threads.ForEach(thread => Assert.That(thread.Join(10000), Is.True));
            }
            var ordered = spans.OrderBy(span => span.start).ToList();
            Assert.That(ordered.Count, Is.EqualTo(3));
            for (int i = 1; i < ordered.Count; i++)
                Assert.That(ordered[i].start, Is.GreaterThanOrEqualTo(ordered[i - 1].end), "Operations overlapped.");
        }

        [Test]
        public void TheEditorThreadNeverWaitsForTheLease()
        {
            using (var repo = History(out _))
            using (var holding = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                int waitedOnMain = 0;
                UnitGitLease.WaitingForTests = kind =>
                {
                    if (UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread()) Interlocked.Increment(ref waitedOnMain);
                };
                Thread holder = Independent(() => UnitGitLease.Hold(() =>
                {
                    holding.Set();
                    release.Wait(20000);
                    return new GitCommandResult();
                }));
                try
                {
                    Assert.That(holding.Wait(10000), Is.True);
                    var elapsed = Stopwatch.StartNew();
                    repo.Write("Assets/f.txt", "changed\n");
                    GitCommandResult staged = repo.Git.Stage(new[] { "Assets/f.txt" });
                    GitCommandResult fetched = repo.Git.Fetch();
                    GitCommandResult held = UnitGitLease.Hold(() => new GitCommandResult());
                    Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(3000), "Refused at once, not after waiting.");
                    Assert.That(staged.Success, Is.False);
                    Assert.That(staged.Message, Does.Contain("Another Git change is running"));
                    Assert.That(fetched.Success, Is.False);
                    Assert.That(held.Success, Is.False);
                    Assert.That(waitedOnMain, Is.Zero, "The editor thread waited for the lease.");
                }
                finally
                {
                    release.Set();
                    holder.Join(10000);
                    UnitGitLease.WaitingForTests = null;
                }
                Assert.That(repo.Git.Stage(new[] { "Assets/f.txt" }).Success, Is.True, "Free again once the operation ended.");
            }
        }

        [Test]
        public void AFailedOrCancelledOperationReleasesTheLease()
        {
            Assert.Throws<InvalidOperationException>(() => UnitGitLease.Hold(() => throw new InvalidOperationException("failed")));
            Assert.That(UnitGitLease.Current, Is.Null);
            Assert.That(UnitGitLease.Hold(() => new GitCommandResult()).Success, Is.True, "Released after an exception.");
            var cancelled = UnitGitLease.Hold(() => new GitCommandResult { ExitCode = 1, StandardError = "Command interrupted by editor reload or shutdown." });
            Assert.That(cancelled.Success, Is.False);
            Assert.That(UnitGitLease.Hold(() => new GitCommandResult()).Success, Is.True, "Released after a cancellation.");
            // The same on another thread, whose operation ends in an exception it handles itself.
            var thread = Independent(() =>
            {
                try { UnitGitLease.Hold(() => throw new OperationCanceledException()); }
                catch (OperationCanceledException) { }
            });
            Assert.That(thread.Join(5000), Is.True);
            Assert.That(UnitGitLease.Hold(() => new GitCommandResult()).Success, Is.True, "Released after a cancelled worker.");
        }

        [Test]
        public void EveryPublicChangeRunsItsCommandsUnderItsOwnLease()
        {
            using (var repo = History(out var hashes))
            {
                repo.Write("Assets/f.txt", "changed\n");
                repo.Write("Assets/new.txt", "new\n");
                UnitGitLease.TakeUnleased();
                UnitGitLease.RecordUnleased = true;
                try
                {
                    Assert.That(repo.Git.Stage(new[] { "Assets/f.txt", "Assets/new.txt" }).Success, Is.True);
                    Assert.That(repo.Git.Unstage(new[] { "Assets/new.txt" }).Success, Is.True);
                    Assert.That(repo.Git.Stage(new[] { "Assets/new.txt" }).Success, Is.True);
                    Assert.That(repo.Git.Commit("five").Success, Is.True);
                    repo.Write("Assets/f.txt", "shelved\n");
                    Assert.That(repo.Git.ShelveAll("shelf").Success, Is.True);
                    Assert.That(repo.Git.CreateBranch("topic", null).Success, Is.True);
                    repo.Git.ReadHeadState(out UnitGitHeadState head);
                    Assert.That(repo.Git.RenameCommit(hashes[1], "renamed", head).Success, Is.True);
                    repo.Git.ReadHeadState(out head);
                    var below = new[] { repo.Run("rev-parse", "HEAD~2").Trim(), repo.Run("rev-parse", "HEAD~1").Trim() };
                    Assert.That(repo.Git.SquashCommits(below, "squashed", head).Success, Is.True);
                }
                finally
                {
                    UnitGitLease.RecordUnleased = false;
                }
                Assert.That(UnitGitLease.TakeUnleased(), Is.Empty, "Exclusive commands ran with no operation's lease around them.");
            }
        }
    }
}
