using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    /// <summary>The report's files: the zip written under a temporary name, and Git run safely. Everything in a temporary folder.</summary>
    public class ReportFileTests
    {
        private string folder;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "LoggerReportFiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void TearDown()
        {
            GitCli.ExecutableOverride = null;
            if (Directory.Exists(folder))
            {
                // Git makes its objects read-only.
                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(folder, true);
            }
        }

        private string Staging()
        {
            string staging = Path.Combine(folder, "staging");
            Directory.CreateDirectory(Path.Combine(staging, "scene"));
            File.WriteAllText(Path.Combine(staging, "README.md"), "# Report\n" + new string('a', 4000));
            File.WriteAllBytes(Path.Combine(staging, "scene", "Avatar.unitypackage"), Enumerable.Repeat((byte)7, 4000).ToArray());
            return staging;
        }

        [Test]
        public void PackedReportsTakeTheirNameOnlyOnceComplete()
        {
            string zip = Path.Combine(folder, "out", "report.zip");
            Assert.IsTrue(ReportZip.Pack(Staging(), zip, () => false, () => true));
            Assert.IsTrue(File.Exists(zip));
            Assert.IsFalse(File.Exists(ReportZip.PartialOf(zip)));
            using (var archive = ZipFile.OpenRead(zip))
            {
                CollectionAssert.AreEquivalent(new[] { "README.md", "scene/Avatar.unitypackage" }, archive.Entries.Select(e => e.FullName));
                // Unity's Mono ignores the "no compression" request, so only the content is checked here.
                using (var package = archive.GetEntry("scene/Avatar.unitypackage").Open())
                using (var bytes = new MemoryStream())
                {
                    package.CopyTo(bytes);
                    CollectionAssert.AreEqual(Enumerable.Repeat((byte)7, 4000).ToArray(), bytes.ToArray());
                }
                Assert.Less(archive.GetEntry("README.md").CompressedLength, 4000);
            }
        }

        [Test]
        public void CancelledOrRefusedPacksLeaveNothing()
        {
            string staging = Staging();
            string zip = Path.Combine(folder, "report.zip");
            Assert.IsFalse(ReportZip.Pack(staging, zip, () => true, () => true));
            Assert.IsFalse(ReportZip.Pack(staging, zip, () => false, () => false));
            Assert.IsFalse(File.Exists(zip));
            Assert.IsFalse(File.Exists(ReportZip.PartialOf(zip)));
        }

        [Test]
        public void RewritingReplacesTextsAndKeepsEverythingElse()
        {
            string zip = Path.Combine(folder, "report.zip");
            ReportZip.Pack(Staging(), zip, () => false, () => true);
            ReportZip.Rewrite(zip, new Dictionary<string, string> { ["README.md"] = "new readme", ["diagnosis.md"] = "# AI diagnosis" });
            Assert.IsFalse(File.Exists(ReportZip.PartialOf(zip)));
            using (var archive = ZipFile.OpenRead(zip))
            {
                CollectionAssert.AreEquivalent(new[] { "README.md", "scene/Avatar.unitypackage", "diagnosis.md" }, archive.Entries.Select(e => e.FullName));
                using (var reader = new StreamReader(archive.GetEntry("README.md").Open()))
                {
                    Assert.AreEqual("new readme", reader.ReadToEnd());
                }

                Assert.AreEqual(4000, archive.GetEntry("scene/Avatar.unitypackage").Length);
            }
        }

        [Test]
        public void MissingSpaceIsSaidBeforeWriting()
        {
            var error = Assert.Throws<IOException>(() => ReportZip.EnsureSpace(folder, long.MaxValue / 4, "the report"));
            StringAssert.StartsWith("Not enough free space on ", error.Message);
            Assert.DoesNotThrow(() => ReportZip.EnsureSpace(folder, 1024, "the report"));
        }

        [Test]
        public void GitArgumentsAreQuotedTheWayTheyAreSplit()
        {
            Assert.AreEqual("log", GitCli.Quote("log"));
            Assert.AreEqual("\"--format=commit %H%n\"", GitCli.Quote("--format=commit %H%n"));
            Assert.AreEqual("\"\"", GitCli.Quote(string.Empty));
            Assert.AreEqual("\"a \\\"b\\\"\"", GitCli.Quote("a \"b\""));
            Assert.AreEqual("\"C:\\dir with space\\\\\"", GitCli.Quote("C:\\dir with space\\"));
        }

        [Test]
        public void WithoutGitTheReportSaysSo()
        {
            Directory.CreateDirectory(Path.Combine(folder, ".git"));
            GitCli.ExecutableOverride = Path.Combine(folder, "no-git.exe");
            var result = GitCli.Run(folder, "status");
            Assert.IsFalse(result.Completed);
            StringAssert.Contains("not installed", result.Error);
            var digest = GitReport.Look(folder);
            Assert.IsTrue(digest.Repository);
            StringAssert.Contains("not installed", digest.Error);
        }

        [Test]
        public void ANewRepositoryIsNotAnError()
        {
            if (GitCli.Executable == null)
            {
                Assert.Ignore("Git is not installed.");
            }

            Assert.IsTrue(GitCli.Run(folder, "init", "-q").Success);
            var digest = GitReport.Look(folder);
            Assert.IsTrue(digest.Repository);
            Assert.IsNull(digest.Error);
            Assert.IsTrue(digest.NoCommits);
            Assert.AreEqual(0, digest.Commits);
            Assert.IsNotEmpty(digest.Branch);
        }

        [Test]
        public void GitIsStoppedAtItsTimeout()
        {
            if (GitCli.Executable == null)
            {
                Assert.Ignore("Git is not installed.");
            }

            var result = GitCli.Run(folder, 0, "--version");
            Assert.IsFalse(result.Completed);
            StringAssert.Contains("took longer", result.Error);
        }

        [Test]
        public void AnonymizedRemotesKeepTheirNames()
        {
            Assert.AreEqual("origin\t[remote] (fetch)\norigin\t[remote] (push)",
                GitReport.Remotes("origin\thttps://github.com/jane/avatar.git (fetch)\norigin\tgit@github.com:jane/avatar.git (push)\n", true));
            Assert.AreEqual("origin\thttps://x (fetch)\n", GitReport.Remotes("origin\thttps://x (fetch)\n", false));
        }
    }
}
