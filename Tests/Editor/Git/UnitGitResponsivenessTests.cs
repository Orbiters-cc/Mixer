using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitResponsivenessTests
    {
        [Test]
        public void FilePreparationKeepsTheCompletionPumpAlive()
        {
            var window = ScriptableObject.CreateInstance<UnitGitWindow>();
            try
            {
                Set(window, "includeRunning", true);
                Invoke(window, "EnsureEditorUpdatePump");
                Invoke(window, "DrainEditorQueues");
                Assert.That(typeof(UnitGitWindow).GetField("editorUpdatePumpActive", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window), Is.True);
                bool delivered = false;
                Invoke(window, "QueueMainThreadAction", (Action)(() => { delivered = true; Set(window, "includeRunning", false); }));
                Invoke(window, "DrainEditorQueues");
                Assert.True(delivered);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void ReloadKeepsDraftAndExplainsInterruptedCommit()
        {
            var window = ScriptableObject.CreateInstance<UnitGitWindow>();
            var restored = ScriptableObject.CreateInstance<UnitGitWindow>();
            try
            {
                Set(window, "commitMessage", "my pending work");
                Set(window, "pendingCommitDescription", "preparing files for your commit");
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(window), restored);
                Invoke(restored, "OnDisable"); Invoke(restored, "OnEnable");
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Assert.That(typeof(UnitGitWindow).GetField("commitMessage", flags).GetValue(restored), Is.EqualTo("my pending work"));
                StringAssert.Contains("Unity reloaded", (string)typeof(UnitGitWindow).GetField("commitIssue", flags).GetValue(restored));
                Assert.IsNull(typeof(UnitGitWindow).GetField("commitWhenIncluded", flags).GetValue(restored));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); UnityEngine.Object.DestroyImmediate(restored); }
        }

        [UnityTest]
        public IEnumerator CommitImmediatelyAfterIncludingFilesCompletesWithoutAnotherClick()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Commit("initial"); repo.Write("Assets/new.txt", "new content");
                var window = ScriptableObject.CreateInstance<UnitGitWindow>();
                try
                {
                    Set(window, "gitService", repo.Git);
                    var snapshot = repo.Git.BuildSnapshot("");
                    Set(window, "snapshot", snapshot);
                    Invoke(window, "BuildShell");
                    Set(window, "commitMessage", "queued commit");
                    Invoke(window, "SetIncluded", snapshot.Changes, true);
                    Invoke(window, "CommitStaged", false);
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    double until = EditorApplication.timeSinceStartup + 20;
                    while (EditorApplication.timeSinceStartup < until && !string.IsNullOrEmpty((string)typeof(UnitGitWindow).GetField("pendingCommitDescription", flags).GetValue(window))) yield return null;
                    Assert.That(repo.Git.GetHeadCommitMessage().StandardOutput.Trim(), Is.EqualTo("queued commit"));
                    // Let the final snapshot finish before disposing its temporary repository.
                    while (EditorApplication.timeSinceStartup < until && (bool)typeof(UnitGitWindow).GetField("refreshingSnapshot", flags).GetValue(window)) yield return null;
                }
                finally { UnityEngine.Object.DestroyImmediate(window); }
            }
        }

        [Test]
        public void CollapsingFoldersKeepsSiblingsAndRootFiles()
        {
            var list = UnitGitFileList.FromChanges(new[]
            {
                new UnitGitStatusEntry { Path = "root/a.txt" },
                new UnitGitStatusEntry { Path = "Assets/a.txt" },
                new UnitGitStatusEntry { Path = "README.md" }
            });
            var visible = list.GetVisibleRows(row => row.Path != "Assets");
            Assert.That(visible.Select(row => row.Path), Is.EqualTo(new[] { "README.md", "Assets", "root", "root/a.txt" }));
            Assert.That(list.GetVisibleRows(row => true), Is.SameAs(list.Rows));
        }

        [Test]
        public void SupersededReadDoesNotStartAnotherGitProcess()
        {
            int started = 0;
            var service = new UnitGitService(System.IO.Path.GetTempPath())
            {
                ReadSuperseded = () => true,
                ProcessLogReceived = line => started++
            };
            var result = service.RunGit(1000, "--version");
            Assert.That(result.Success, Is.False);
            Assert.That(started, Is.Zero);
        }

        [Test]
        public void RapidSelectionsRunOnlyTheActiveAndLatestRead()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var read = new UnitGitLatestRequest<int>())
            {
                int executed = 0;
                read.Request(stale =>
                {
                    Interlocked.Increment(ref executed);
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return -1;
                });
                Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                for (int i = 0; i < 1000; i++)
                {
                    int value = i;
                    read.Request(stale => { Interlocked.Increment(ref executed); return value; });
                }
                release.Set();
                int result = -1;
                Exception error = null;
                Assert.That(SpinWait.SpinUntil(() => read.Poll(out result, out error), 5000), Is.True);
                Assert.That(error, Is.Null);
                Assert.That(result, Is.EqualTo(999));
                Assert.That(executed, Is.EqualTo(2));
                Assert.That(read.IsBusy, Is.False);
            }
        }

        [Test]
        public void DisposedReadCannotDeliverItsResult()
        {
            using (var release = new ManualResetEventSlim())
            {
                var read = new UnitGitLatestRequest<int>();
                read.Request(stale => { release.Wait(TimeSpan.FromSeconds(5)); return 1; });
                read.Dispose();
                release.Set();
                bool delivered = false;
                Assert.That(SpinWait.SpinUntil(() =>
                {
                    delivered |= read.Poll(out _, out _);
                    return !read.IsBusy;
                }, 5000), Is.True);
                Assert.That(delivered, Is.False);
            }
        }

        [Test]
        public void DiffComparisonDetectsContentChangesWithUnchangedStatus()
        {
            var first = new UnitGitDiff { Path = "Assets/test.txt", DifferenceCount = 1 };
            first.Lines.Add(new UnitGitDiffLine { Right = "first", Kind = UnitGitDiffLineKind.Added });
            var second = new UnitGitDiff { Path = first.Path, DifferenceCount = 1 };
            second.Lines.Add(new UnitGitDiffLine { Right = "second", Kind = UnitGitDiffLineKind.Added });
            Assert.That(UnitGitWindow.SameDiff(first, second), Is.False);
            second.Lines[0].Right = "first";
            Assert.That(UnitGitWindow.SameDiff(first, second), Is.True);
            second.Lines[0].Kind = UnitGitDiffLineKind.Removed;
            Assert.That(UnitGitWindow.SameDiff(first, second), Is.False);
        }

        [UnityTest]
        public IEnumerator LocalChangesAndLongDiffKeepTheVisualTreeBounded()
        {
            var owner = ScriptableObject.CreateInstance<UnitGitWindow>();
            var host = ScriptableObject.CreateInstance<UnitGitListTestHost>();
            try
            {
                Invoke(owner, "OnDisable");
                var snapshot = new UnitGitSnapshot { HasCommits = true, HasRepository = true, GitAvailable = true, IsUnityProject = true };
                snapshot.Changes = Enumerable.Range(0, 100000)
                    .Select(i => new UnitGitStatusEntry { Path = "file" + i.ToString("D6") + ".txt", WorkTreeStatus = 'M' }).ToList();
                snapshot.ChangeList = UnitGitFileList.FromChanges(snapshot.Changes);
                var diff = new UnitGitDiff { Path = snapshot.Changes[0].Path, MaxLineLength = 1000000 };
                diff.Lines = Enumerable.Range(0, 100000).Select(i => new UnitGitDiffLine { Right = "line " + i }).ToList();
                diff.Lines[0].Right = new string('x', 500000) + "needle" + new string('y', 499994);
                Set(owner, "snapshot", snapshot);
                Set(owner, "selectedChangePath", diff.Path);
                Set(owner, "requestedDiffPath", diff.Path);
                Set(owner, "loadedDiff", diff);
                host.position = new Rect(0, 0, 1200, 700);
                host.rootVisualElement.style.width = 1200;
                host.rootVisualElement.style.height = 700;
                host.rootVisualElement.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.mixer/Editor/Git/Styles/unitgit.uss"));
                var timer = System.Diagnostics.Stopwatch.StartNew();
                host.rootVisualElement.Add((VisualElement)Invoke(owner, "BuildLocalChangesBody"));
                timer.Stop();
                host.ShowUtility();
                for (int i = 0; i < 10; i++)
                    yield return null;
                var lists = host.rootVisualElement.Query<ListView>().ToList();
                Assert.That(lists, Has.Count.EqualTo(2));
                // The changes list adds its group header ("Changes") above the files.
                Assert.That(lists[0].itemsSource.Count, Is.EqualTo(100001));
                Assert.That(lists[1].itemsSource.Count, Is.EqualTo(100000));
                Assert.That(host.rootVisualElement.Query<Button>(className: "unitgit-change-file-row").ToList().Count, Is.LessThan(60));
                var labels = host.rootVisualElement.Query<Label>().ToList();
                Assert.That(labels.Count, Is.GreaterThan(20));
                Assert.That(labels, Has.Count.LessThan(350));
                Assert.That(labels.All(label => (label.text ?? "").Length < 2000), Is.True);
                Set(owner, "diffSearch", "needle");
                Invoke(owner, "UpdateDiffSearch", true);
                for (int i = 0; i < 5; i++)
                    yield return null;
                var diffScroll = lists[1].Q<ScrollView>();
                TestContext.WriteLine("Diff scroll: " + diffScroll.scrollOffset + "; max " + diffScroll.horizontalScroller.highValue + "; viewport " + diffScroll.contentViewport.layout);
                Assert.That(host.rootVisualElement.Query<Label>().ToList().Any(label => (label.text ?? "").Contains("needle")), Is.True);
                Assert.That(diff.Lines[0].Right.Length, Is.EqualTo(1000000));
                TestContext.WriteLine("100000 files + 100000 diff lines: build " + timer.ElapsedMilliseconds + "ms; " + labels.Count + " live labels; million-character line searchable.");
            }
            finally
            {
                host.Close();
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static object Invoke(object target, string method, params object[] arguments)
        {
            return target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, arguments);
        }

        private static void Set(object target, string field, object value)
        {
            target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        [UnityTest]
        public IEnumerator HundredThousandRowsOnlyBuildVisibleElements()
        {
            var host = ScriptableObject.CreateInstance<UnitGitListTestHost>();
            try
            {
                host.position = new Rect(0, 0, 500, 400);
                host.rootVisualElement.style.width = 500;
                host.rootVisualElement.style.height = 400;
                var items = Enumerable.Range(0, 100000).ToList();
                int built = 0;
                var list = UnitGitWindow.BuildVirtualList(items, 28, value =>
                {
                    built++;
                    return new Label(value.ToString());
                }, "test-scroll");
                host.rootVisualElement.Add(list);
                host.ShowUtility();
                for (int i = 0; i < 10; i++)
                    yield return null;
                Assert.That(built, Is.GreaterThan(0).And.LessThan(100));
                list.ScrollToItem(50000);
                for (int i = 0; i < 5; i++)
                    yield return null;
                Assert.That(built, Is.LessThan(200));
                list.ScrollToItem(99999);
                for (int i = 0; i < 5; i++)
                    yield return null;
                Assert.That(built, Is.LessThan(300));
                Assert.That(list.Query<Label>().ToList().Any(label => label.text == "99999"), Is.True);
                TestContext.WriteLine("100000 rows: " + built + " row builds after opening and two distant scrolls.");
            }
            finally
            {
                host.Close();
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }

    public sealed class UnitGitListTestHost : EditorWindow { }
}
