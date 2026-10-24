using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitConflictTests
    {
        private const string Prefab = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &100\nGameObject:\n  m_Name: {0}\n  m_IsActive: 1\n--- !u!4 &101\nTransform:\n  m_GameObject: {{fileID: 100}}\n  m_LocalPosition: {{x: 0, y: {1}, z: 0}}\n  m_Father: {{fileID: 0}}\n";

        // A merge stopped on "path", changed on both sides. A null side deletes the file.
        private static UnitGitTestRepo Conflicted(string path, byte[] baseBytes, byte[] mine, byte[] theirs)
        {
            var repo = UnitGitTestRepo.Create();
            Put(repo, path, baseBytes);
            repo.Write("Assets/keep.txt", "keep\n");
            repo.Commit("base");
            repo.Run("branch", "theirs");
            Put(repo, path, mine);
            repo.Commit("mine");
            repo.Run("checkout", "-q", "theirs");
            Put(repo, path, theirs);
            repo.Commit("theirs");
            repo.Run("checkout", "-q", "main");
            var merge = UnitGitConflicts.MergeBranch(repo.Git, "theirs");
            Assert.That(merge.Success, Is.True, merge.Message);
            Assert.That(UnitGitConflicts.Stages(repo.Git, path), Is.Not.Empty, path + " is in conflict.");
            return repo;
        }

        private static UnitGitTestRepo Conflicted(string path, string baseText, string mine, string theirs)
        {
            return Conflicted(path, Bytes(baseText), Bytes(mine), Bytes(theirs));
        }

        private static byte[] Bytes(string text) => text == null ? null : Encoding.UTF8.GetBytes(text);

        private static void Put(UnitGitTestRepo repo, string path, byte[] bytes)
        {
            if (bytes == null) { File.Delete(repo.Full(path)); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(repo.Full(path)));
            File.WriteAllBytes(repo.Full(path), bytes);
        }

        private static List<ConflictBlock> ReadBlocks(UnitGitTestRepo repo, string path)
        {
            return UnitGitConflicts.Blocks(repo.Git, UnitGitConflicts.Stage(repo.Git, path, 2), UnitGitConflicts.Stage(repo.Git, path, 1), UnitGitConflicts.Stage(repo.Git, path, 3));
        }

        private static GitCommandResult Save(UnitGitTestRepo repo, string path, IList<ConflictBlock> blocks)
        {
            return UnitGitConflicts.Resolve(repo.Git, path, UnitGitConflicts.Compose(blocks), UnitGitConflicts.FileState(repo.Full(path)));
        }

        private static UnitGitTestRepo Diverged(string mineName, string mineY, string theirName, string theirY, out string branch)
        {
            var repo = UnitGitTestRepo.Create();
            repo.Write("Assets/Hat.prefab", string.Format(Prefab, "Hat", "0"));
            repo.Write("Assets/notes.txt", "one\ntwo\nthree\n");
            repo.Commit("base");
            branch = "theirs";
            repo.Run("branch", branch);
            repo.Write("Assets/Hat.prefab", string.Format(Prefab, mineName, mineY));
            repo.Write("Assets/notes.txt", "one\nmine\nthree\n");
            repo.Commit("mine");
            repo.Run("checkout", "-q", branch);
            repo.Write("Assets/Hat.prefab", string.Format(Prefab, theirName, theirY));
            repo.Write("Assets/notes.txt", "one\ntheirs\nthree\n");
            repo.Commit("theirs");
            repo.Run("checkout", "-q", "main");
            return repo;
        }

        [Test]
        public void MergeWithConflictsIsResolvedPartByPartAndFinished()
        {
            using (var repo = Diverged("Hat", "1", "Hat", "0", out string branch))
            {
                var merge = UnitGitConflicts.MergeBranch(repo.Git, branch);
                Assert.That(merge.Success, Is.True, merge.Message);
                Assert.That(UnitGitConflicts.Operation(repo.Root, out _), Is.EqualTo(UnitGitOperation.Merge));
                var conflicted = repo.Git.BuildSnapshot(string.Empty).Changes.Where(UnitGitConflicts.IsConflict).Select(c => c.Path).ToList();
                CollectionAssert.Contains(conflicted, "Assets/notes.txt");

                string mine = UnitGitConflicts.Stage(repo.Git, "Assets/notes.txt", 2);
                string baseText = UnitGitConflicts.Stage(repo.Git, "Assets/notes.txt", 1);
                string theirs = UnitGitConflicts.Stage(repo.Git, "Assets/notes.txt", 3);
                var blocks = UnitGitConflicts.Blocks(repo.Git, mine, baseText, theirs);
                var part = blocks.Single(b => b.IsConflict);
                Assert.That(part.Mine, Is.EqualTo("mine\n"));
                Assert.That(part.Theirs, Is.EqualTo("theirs\n"));
                Assert.That(UnitGitConflicts.Compose(blocks), Is.Null, "Nothing is written while a part has no choice.");
                part.Choice = ConflictChoice.Both;
                string state = UnitGitConflicts.FileState(repo.Full("Assets/notes.txt"));
                Assert.That(UnitGitConflicts.Resolve(repo.Git, "Assets/notes.txt", UnitGitConflicts.Compose(blocks), state).Success, Is.True);
                Assert.That(repo.Read("Assets/notes.txt"), Is.EqualTo("one\nmine\ntheirs\nthree\n"));

                // Only the prefab changed on one side: Git merged it. Anything left is resolved by keeping theirs.
                foreach (string path in repo.Git.BuildSnapshot(string.Empty).Changes.Where(UnitGitConflicts.IsConflict).Select(c => c.Path).ToList())
                    Assert.That(UnitGitConflicts.TakeSide(repo.Git, path, false).Success, Is.True);
                Assert.That(repo.Git.BuildSnapshot(string.Empty).Changes.Any(UnitGitConflicts.IsConflict), Is.False);
                var finish = UnitGitConflicts.Finish(repo.Git, UnitGitOperation.Merge);
                Assert.That(finish.Success, Is.True, finish.Message);
                Assert.That(UnitGitConflicts.Operation(repo.Root, out _), Is.EqualTo(UnitGitOperation.None));
                Assert.That(repo.Run("log", "-1", "--format=%P").Trim().Split(' ').Length, Is.EqualTo(2), "A merge commit with both parents.");
            }
        }

        [Test]
        public void MergeCanBeCancelled()
        {
            using (var repo = Diverged("Hat", "1", "Cap", "2", out string branch))
            {
                string before = repo.Run("rev-parse", "HEAD");
                UnitGitConflicts.MergeBranch(repo.Git, branch);
                Assert.That(UnitGitConflicts.Abort(repo.Git, UnitGitOperation.Merge).Success, Is.True);
                Assert.That(repo.Run("rev-parse", "HEAD"), Is.EqualTo(before));
                Assert.That(repo.Read("Assets/notes.txt"), Is.EqualTo("one\nmine\nthree\n"));
            }
        }

        // Different properties changed on each side: Unity merges them by object, where Git sees one conflict.
        [Test]
        public void UnityMergesDifferentPropertiesOfOneObject()
        {
            string tool = UnitGitConflicts.UnityMergeTool();
            if (tool == null) Assert.Ignore("UnityYAMLMerge is not installed with this editor.");
            string baseText = string.Format(Prefab, "Hat", "0");
            var clean = UnitGitConflicts.UnityMerge(tool, string.Format(Prefab, "Hat", "1"), baseText, string.Format(Prefab, "Cap", "0"), ".prefab");
            Assert.That(clean.Clean, Is.True, clean.Message);
            StringAssert.Contains("m_Name: Cap", clean.Output);
            StringAssert.Contains("y: 1", clean.Output);

            var conflict = UnitGitConflicts.UnityMerge(tool, string.Format(Prefab, "Mine", "0"), baseText, string.Format(Prefab, "Theirs", "0"), ".prefab");
            Assert.That(conflict.Clean, Is.False);
            var name = conflict.Conflicts.Single();
            Assert.That(name.Property, Is.EqualTo("m_Name"));
            Assert.That(name.Mine, Is.EqualTo("Mine"));
            Assert.That(name.Theirs, Is.EqualTo("Theirs"));
        }

        [Test]
        public void UnityPartsNameTheirObject()
        {
            string merged = "--- !u!1 &100\nGameObject:\n  m_Name: Hips\n<<<<<<< Mine\n  m_IsActive: 1\n||||||| Base\n  m_IsActive: 0\n=======\n  m_IsActive: 0\n>>>>>>> Theirs\n";
            var part = UnitGitConflicts.Parse(merged).Single(b => b.IsConflict);
            Assert.That(part.Context, Is.EqualTo("Hips (Game Object)"));
        }

        // A version Git cannot read is not a deleted side: nothing is removed, the file on disk stays as it was.
        [Test]
        public void TakeSideKeepsAFileWhoseVersionCannotBeRead()
        {
            using (var repo = Conflicted("Assets/notes.txt", "one\ntwo\n", "one\nmine\n", "one\ntheirs\n"))
            {
                string marked = repo.Read("Assets/notes.txt");
                string blob = repo.Run("rev-parse", ":2:Assets/notes.txt").Trim();
                string loose = Path.Combine(repo.Root, ".git", "objects", blob.Substring(0, 2), blob.Substring(2));
                if (!File.Exists(loose)) Assert.Ignore("Git packed the object, so this test cannot take it away.");
                File.SetAttributes(loose, FileAttributes.Normal);
                File.Delete(loose);

                var kept = UnitGitConflicts.TakeSide(repo.Git, "Assets/notes.txt", true);
                Assert.That(kept.Success, Is.False);
                Assert.That(repo.Read("Assets/notes.txt"), Is.EqualTo(marked));
                Assert.That(UnitGitConflicts.Stages(repo.Git, "Assets/notes.txt"), Is.EquivalentTo(new[] { 1, 2, 3 }), "Still in conflict, not deleted.");
                Assert.Throws<IOException>(() => UnitGitConflicts.Stage(repo.Git, "Assets/notes.txt", 2));
            }
        }

        // A list that is out of date (the file was resolved meanwhile) never turns a choice into a deletion.
        [Test]
        public void TakeSideRefusesAFileThatIsNotInConflict()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                repo.Write("Assets/a.txt", "a\n");
                repo.Commit("a");
                Assert.That(UnitGitConflicts.TakeSide(repo.Git, "Assets/a.txt", false).Success, Is.False);
                Assert.That(repo.Read("Assets/a.txt"), Is.EqualTo("a\n"));
                Assert.That(repo.Run("ls-files", "--", "Assets/a.txt").Trim(), Is.EqualTo("Assets/a.txt"));
            }
        }

        [Test]
        public void DeletedSideIsTakenWhenGitHasNoVersionOfIt()
        {
            using (var repo = Conflicted("Assets/gone.txt", "base\n", "mine\n", null))
            {
                Assert.That(UnitGitConflicts.Stages(repo.Git, "Assets/gone.txt"), Is.EquivalentTo(new[] { 1, 2 }));
                var taken = UnitGitConflicts.TakeSide(repo.Git, "Assets/gone.txt", false);
                Assert.That(taken.Success, Is.True, taken.Message);
                Assert.That(File.Exists(repo.Full("Assets/gone.txt")), Is.False);
                Assert.That(UnitGitConflicts.Stages(repo.Git, "Assets/gone.txt"), Is.Empty);
            }
        }

        // A binary side is written by Git byte for byte and never comes back through its text output.
        [Test]
        public void KeepingABinarySideWritesItByteForByte()
        {
            byte[] baseBytes = { 0x89, 0x50, 0x00, 0x01, 0x0D, 0x0A };
            byte[] mine = { 0x89, 0x50, 0x00, 0x02, 0x0D, 0x0A, 0x0D };
            byte[] theirs = { 0x89, 0x50, 0x00, 0x03, 0x0D, 0x0A, 0x0D, 0x41, 0xFF };
            using (var repo = Conflicted("Assets/Skin.png", baseBytes, mine, theirs))
            {
                var commands = new List<string>();
                repo.Git.ProcessLogReceived = line => { if (line.StartsWith("> ", StringComparison.Ordinal)) commands.Add(line); };
                var taken = UnitGitConflicts.TakeSide(repo.Git, "Assets/Skin.png", false);
                repo.Git.ProcessLogReceived = null;
                Assert.That(taken.Success, Is.True, taken.Message);
                Assert.That(File.ReadAllBytes(repo.Full("Assets/Skin.png")), Is.EqualTo(theirs));
                Assert.That(commands.Any(c => c.Contains(" show ") || c.Contains(" cat-file ")), Is.False, string.Join("\n", commands));
                Assert.That(UnitGitConflicts.Stages(repo.Git, "Assets/Skin.png"), Is.Empty);
                Assert.That(repo.Run("rev-parse", ":0:Assets/Skin.png"), Is.EqualTo(repo.Run("rev-parse", "theirs:Assets/Skin.png")));
                Assert.That(Directory.GetFiles(repo.Root, ".merge_file_*"), Is.Empty, "No temporary file is left behind.");
            }
        }

        // A text asset that holds binary data (Unity can serialize assets that way) is never decoded as text.
        [Test]
        public void BinaryVersionOfATextAssetHasNoText()
        {
            using (var repo = Conflicted("Assets/Lighting.asset", new byte[] { 1, 0, 1 }, new byte[] { 2, 0, 2 }, new byte[] { 3, 0, 3 }))
            {
                Assert.That(UnitGitConflicts.Stage(repo.Git, "Assets/Lighting.asset", 2), Is.Null);
                Assert.That(UnitGitConflicts.Stage(repo.Git, "Assets/Lighting.asset", 3), Is.Null);
            }
        }

        // A merge that failed or never ran (here: every Git call refused) is an error, never a file with no conflicts.
        [Test]
        public void FailedMergeIsNeverTakenForAResult()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                var refused = new UnitGitService(repo.Root) { ReadSuperseded = () => true };
                Assert.Throws<IOException>(() => UnitGitConflicts.Blocks(refused, "one\nmine\n", "one\ntwo\n", "one\ntheirs\n"));
                Assert.That(UnitGitConflicts.Blocks(repo.Git, "one\nmine\n", "one\ntwo\n", "one\ntheirs\n").Count(b => b.IsConflict), Is.EqualTo(1));

                repo.Write("Assets/a.txt", "keep\n");
                var unchosen = UnitGitConflicts.Resolve(repo.Git, "Assets/a.txt", null, UnitGitConflicts.FileState(repo.Full("Assets/a.txt")));
                Assert.That(unchosen.Success, Is.False);
                Assert.That(repo.Read("Assets/a.txt"), Is.EqualTo("keep\n"));
            }
        }

        // Neither the sides nor the merged parts gain a final line break the file did not have.
        [Test]
        public void ResolvedFileKeepsAMissingFinalLineBreak()
        {
            using (var repo = Conflicted("Assets/middle.txt", "a\nb\nc", "a\nB\nc", "a\nX\nc"))
            {
                Assert.That(UnitGitConflicts.Stage(repo.Git, "Assets/middle.txt", 2), Is.EqualTo("a\nB\nc"));
                var blocks = ReadBlocks(repo, "Assets/middle.txt");
                blocks.Single(b => b.IsConflict).Choice = ConflictChoice.Mine;
                Assert.That(UnitGitConflicts.Compose(blocks), Is.EqualTo("a\nB\nc"));
                Assert.That(Save(repo, "Assets/middle.txt", blocks).Success, Is.True);
                Assert.That(File.ReadAllBytes(repo.Full("Assets/middle.txt")), Is.EqualTo(Bytes("a\nB\nc")));
            }
            // The conflict reaches the end of the file: each side keeps its own ending.
            using (var repo = Conflicted("Assets/end.txt", "x\ny", "x\nY\n", "x\nZ"))
            {
                var blocks = ReadBlocks(repo, "Assets/end.txt");
                var part = blocks.Single(b => b.IsConflict);
                part.Choice = ConflictChoice.Mine;
                Assert.That(UnitGitConflicts.Compose(blocks), Is.EqualTo("x\nY\n"));
                part.Choice = ConflictChoice.Both;
                Assert.That(UnitGitConflicts.Compose(blocks), Is.EqualTo("x\nY\nZ"));
                part.Choice = ConflictChoice.Theirs;
                Assert.That(UnitGitConflicts.Compose(blocks), Is.EqualTo("x\nZ"));
                Assert.That(Save(repo, "Assets/end.txt", blocks).Success, Is.True);
                Assert.That(File.ReadAllBytes(repo.Full("Assets/end.txt")), Is.EqualTo(Bytes("x\nZ")));
            }
        }

        // Parts read before the file was edited by hand never overwrite that edit.
        [Test]
        public void SaveIsRefusedWhenTheFileChangedAfterItWasRead()
        {
            using (var repo = Conflicted("Assets/notes.txt", "one\ntwo\n", "one\nmine\n", "one\ntheirs\n"))
            {
                string state = UnitGitConflicts.FileState(repo.Full("Assets/notes.txt"));
                var blocks = ReadBlocks(repo, "Assets/notes.txt");
                blocks.Single(b => b.IsConflict).Choice = ConflictChoice.Theirs;
                repo.Write("Assets/notes.txt", "one\nfixed by hand\n");

                var stale = UnitGitConflicts.Resolve(repo.Git, "Assets/notes.txt", UnitGitConflicts.Compose(blocks), state);
                Assert.That(stale.Success, Is.False);
                Assert.That(stale.Message, Does.Contain("changed on disk"));
                Assert.That(repo.Read("Assets/notes.txt"), Is.EqualTo("one\nfixed by hand\n"));
                Assert.That(UnitGitConflicts.Stages(repo.Git, "Assets/notes.txt"), Is.Not.Empty, "Still unresolved.");

                // Read again, the same choice is saved.
                Assert.That(Save(repo, "Assets/notes.txt", blocks).Success, Is.True);
                Assert.That(repo.Read("Assets/notes.txt"), Is.EqualTo("one\ntheirs\n"));
            }
        }

        // In a linked worktree ".git" is a file: Git's own paths still find the merge, its index and its versions.
        [Test]
        public void ConflictsAreFoundInALinkedWorktree()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string worktree = UnitGitTestRepo.TempFolder();
                try
                {
                    repo.Write("Assets/notes.txt", "one\ntwo\n");
                    repo.Commit("base");
                    repo.Run("worktree", "add", "-q", "-b", "side", worktree);
                    var tree = new UnitGitService(worktree);
                    File.WriteAllText(Path.Combine(worktree, "Assets", "notes.txt"), "one\nside\n");
                    Assert.That(tree.RunGit(60000, "commit", "-q", "-am", "side").Success, Is.True);
                    repo.Write("Assets/notes.txt", "one\nmain\n");
                    repo.Commit("main");
                    Assert.That(File.Exists(Path.Combine(worktree, ".git")), Is.True, "A linked worktree has a .git file.");

                    Assert.That(UnitGitConflicts.MergeBranch(tree, "main").Success, Is.True);
                    Assert.That(UnitGitConflicts.Operation(worktree, out _), Is.EqualTo(UnitGitOperation.Merge));
                    Assert.That(UnitGitConflicts.Operation(repo.Root, out _), Is.EqualTo(UnitGitOperation.None), "Only the worktree is merging.");
                    string index = UnitGitPaths.GitPath(worktree, "index");
                    Assert.That(File.Exists(index), Is.True, index);
                    Assert.That(index, Is.Not.EqualTo(Path.Combine(worktree, ".git", "index")));

                    Assert.That(UnitGitConflicts.Stages(tree, "Assets/notes.txt"), Is.EquivalentTo(new[] { 1, 2, 3 }));
                    var taken = UnitGitConflicts.TakeSide(tree, "Assets/notes.txt", false);
                    Assert.That(taken.Success, Is.True, taken.Message);
                    Assert.That(File.ReadAllText(Path.Combine(worktree, "Assets", "notes.txt")), Is.EqualTo("one\nmain\n"));
                    Assert.That(UnitGitConflicts.Finish(tree, UnitGitOperation.Merge).Success, Is.True);
                    Assert.That(UnitGitConflicts.Operation(worktree, out _), Is.EqualTo(UnitGitOperation.None));
                }
                finally
                {
                    UnitGitTestRepo.DeleteTempFolder(worktree);
                }
            }
        }

        // A stuck tool that keeps its output streams open still stops at the timeout.
        [Test]
        public void UnityMergeThatHangsStopsAtItsTimeout()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("The stand-in merge tool is a Windows batch file.");
            string folder = UnitGitTestRepo.TempFolder();
            try
            {
                // The ping it starts keeps both streams open for about fifteen seconds after the tool itself is stopped.
                string tool = Path.Combine(folder, "stuck-merge.cmd");
                File.WriteAllText(tool, "@echo off\r\necho stuck 1>&2\r\nping -n 16 127.0.0.1 >nul\r\n");
                var clock = Stopwatch.StartNew();
                var result = UnitGitConflicts.UnityMerge(tool, "mine\n", "base\n", "theirs\n", ".prefab", 1000);
                clock.Stop();
                Assert.That(result.Clean, Is.False);
                Assert.That(result.Message, Does.Contain("did not finish"));
                Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(10));
            }
            finally
            {
                UnitGitTestRepo.DeleteTempFolder(folder);
            }
        }

        // A clean exit is not a merge unless the tool wrote one.
        [Test]
        public void UnityMergeWithoutAResultIsNotClean()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("The stand-in merge tool is a Windows batch file.");
            string folder = UnitGitTestRepo.TempFolder();
            try
            {
                string tool = Path.Combine(folder, "silent-merge.cmd");
                File.WriteAllText(tool, "@echo off\r\nexit /b 0\r\n");
                var result = UnitGitConflicts.UnityMerge(tool, "mine\n", "base\n", "theirs\n", ".prefab");
                Assert.That(result.Clean, Is.False);
                Assert.That(result.Output, Is.Null);
                Assert.That(result.Message, Does.Contain("without a result"));
            }
            finally
            {
                UnitGitTestRepo.DeleteTempFolder(folder);
            }
        }
    }
}
