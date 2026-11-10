using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitDiffHeaderTests
    {
        [Test]
        public void AcceptedDiffRefreshesExistingHeadersAcrossStagingStates()
        {
            var window = ScriptableObject.CreateInstance<UnitGitWindow>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(UnitGitWindow);
            try
            {
                var initial = new UnitGitDiff { Path = "notes.txt", LeftTitle = "Repository", RightTitle = "Current version" };
                var snapshot = new UnitGitSnapshot { HasCommits = true, HasRepository = true, GitAvailable = true, IsUnityProject = true };
                snapshot.Changes.Add(new UnitGitStatusEntry { Path = "notes.txt", WorkTreeStatus = 'M' });
                type.GetField("snapshot", flags).SetValue(window, snapshot);
                window.changesTab.selectedChangePath = "notes.txt";
                window.changesTab.loadedDiff = initial;
                window.changesTab.requestedDiffPath = "notes.txt";
                var pane = (VisualElement)type.GetMethod("BuildDiffViewerPane", flags).Invoke(window, null);
                var headers = pane.Query<Label>(className: "unitgit-diff-header-cell").ToList();
                Assert.That(headers.Select(label => label.text), Is.EqualTo(new[] { "Repository", "Current version" }));
                var read = (UnitGitLatestRequest<UnitGitDiff>)window.changesTab.diffRead;
                // The test never opens an Editor window. Poll still exercises the actual accepted-result path.
                foreach (var titles in new[] { new[] { "HEAD", "Staged" }, new[] { "HEAD / Index", "Index / Working tree" }, new[] { "Repository", "Current version" } })
                {
                    var next = new UnitGitDiff { LeftTitle = titles[0], RightTitle = titles[1] };
                    read.Request(_ => next);
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!ReferenceEquals(window.changesTab.loadedDiff, next) && DateTime.UtcNow < deadline)
                    {
                        type.GetMethod("PollDiffRead", flags).Invoke(window, null);
                        Thread.Sleep(1);
                    }
                    Assert.That(window.changesTab.loadedDiff, Is.SameAs(next));
                    Assert.That(headers.Select(label => label.text), Is.EqualTo(titles));
                    Assert.That(pane.Query<Label>(className: "unitgit-diff-header-cell").ToList(), Is.EqualTo(headers));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }
    }
}
