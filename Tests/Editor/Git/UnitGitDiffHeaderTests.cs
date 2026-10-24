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
                type.GetField("selectedChangePath", flags).SetValue(window, "notes.txt");
                type.GetField("loadedDiff", flags).SetValue(window, initial);
                type.GetField("requestedDiffPath", flags).SetValue(window, "notes.txt");
                var pane = (VisualElement)type.GetMethod("BuildDiffViewerPane", flags).Invoke(window, null);
                var headers = pane.Query<Label>(className: "unitgit-diff-header-cell").ToList();
                Assert.That(headers.Select(label => label.text), Is.EqualTo(new[] { "Repository", "Current version" }));
                var read = (UnitGitLatestRequest<UnitGitDiff>)type.GetField("diffRead", flags).GetValue(window);
                // The test never opens an Editor window. Poll still exercises the actual accepted-result path.
                foreach (var titles in new[] { new[] { "HEAD", "Staged" }, new[] { "HEAD / Index", "Index / Working tree" }, new[] { "Repository", "Current version" } })
                {
                    var next = new UnitGitDiff { LeftTitle = titles[0], RightTitle = titles[1] };
                    read.Request(_ => next);
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!ReferenceEquals(type.GetField("loadedDiff", flags).GetValue(window), next) && DateTime.UtcNow < deadline)
                    {
                        type.GetMethod("PollDiffRead", flags).Invoke(window, null);
                        Thread.Sleep(1);
                    }
                    Assert.That(type.GetField("loadedDiff", flags).GetValue(window), Is.SameAs(next));
                    Assert.That(headers.Select(label => label.text), Is.EqualTo(titles));
                    Assert.That(pane.Query<Label>(className: "unitgit-diff-header-cell").ToList(), Is.EqualTo(headers));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }
    }
}
