using System;
using System.IO;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitStorageTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "UnitGitStorageTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(root) && root.Contains("UnitGitStorageTests-") && Directory.Exists(root))
                Directory.Delete(root, true);
        }

        private void WriteBytes(string relative, int count)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[count]);
        }

        // History, LFS and the rest are counted apart; the working files beside .git are not counted at all.
        [Test]
        public void MeasureSplitsObjectsLfsAndTheRestAndSkipsWorkingFiles()
        {
            WriteBytes(".git/objects/pack/pack-1.pack", 4000);
            WriteBytes(".git/objects/ab/cdef", 100);
            WriteBytes(".git/lfs/objects/12/34/1234", 2000);
            WriteBytes(".git/index", 30);
            WriteBytes(".git/refs/heads/main", 41);
            WriteBytes("Assets/big.fbx", 50000);

            string gitDirectory = UnitGitStorage.ResolveGitDirectory(root);
            UnitGitStorageSize size = UnitGitStorage.Measure(gitDirectory);

            Assert.AreEqual(Path.GetFullPath(Path.Combine(root, ".git")), gitDirectory);
            Assert.AreEqual(4100, size.Objects);
            Assert.AreEqual(2000, size.Lfs);
            Assert.AreEqual(71, size.Other);
            Assert.AreEqual(6171, size.Total);
        }

        // A worktree's .git file points to its folder in the main repository, whose common directory holds the history.
        [Test]
        public void GitFileOfAWorktreeResolvesToTheCommonDirectory()
        {
            WriteBytes("main/.git/objects/pack/pack-1.pack", 10);
            WriteBytes("main/.git/worktrees/feature/HEAD", 1);
            File.WriteAllText(Path.Combine(root, "main", ".git", "worktrees", "feature", "commondir"), "../..\n");
            Directory.CreateDirectory(Path.Combine(root, "feature"));
            File.WriteAllText(Path.Combine(root, "feature", ".git"), "gitdir: ../main/.git/worktrees/feature\n");

            Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "main", ".git")), UnitGitStorage.ResolveGitDirectory(Path.Combine(root, "feature")));
        }

        [Test]
        public void NoRepositoryMeasuresNothing()
        {
            Assert.AreEqual(string.Empty, UnitGitStorage.ResolveGitDirectory(root));
            Assert.AreEqual(0, UnitGitStorage.Measure(string.Empty).Total);
        }

        // The background measurement fills the cache the top bar reads first.
        [Test]
        public void MeasureAsyncCachesTheResult()
        {
            WriteBytes(".git/objects/x", 1234);
            UnitGitStorageSize measured = UnitGitStorage.MeasureAsync(root).Result;
            Assert.AreEqual(1234, measured.Total);
            Assert.AreSame(measured, UnitGitStorage.Cached(root));
        }

        [Test]
        public void LabelUsesTheSharedByteFormat()
        {
            Assert.AreEqual("Git " + UnitGitService.FormatBytes(1288490189L), UnitGitStorage.Label(new UnitGitStorageSize { Total = 1288490189L }));
            Assert.AreEqual("Git …", UnitGitStorage.Label(null));
            StringAssert.Contains("not counted", UnitGitStorage.Tooltip(null));
        }
    }
}
