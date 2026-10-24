using System.Collections.Generic;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // What Visit This Commit and Back check and ask before Git rewrites the project.
    public sealed class UnitGitVisitChecksTests
    {
        [Test]
        public void EnterShelvesAndEscapeCancels()
        {
            // DisplayDialogComplex answers 0 for its ok button (Enter), 1 for cancel (Escape, closing the dialog), 2 for the third.
            Assert.That(UnitGitVisitChecks.ChangesChoice(0), Is.EqualTo(UnitGitVisitChangesChoice.Shelve));
            Assert.That(UnitGitVisitChecks.ChangesChoice(1), Is.EqualTo(UnitGitVisitChangesChoice.Cancel));
            Assert.That(UnitGitVisitChecks.ChangesChoice(2), Is.EqualTo(UnitGitVisitChangesChoice.Discard));
            Assert.That(UnitGitVisitChecks.ChangesChoice(-1), Is.EqualTo(UnitGitVisitChangesChoice.Cancel));
        }

        [Test]
        public void InTheWayFindsListedFilesAndFilesOnDiskInListedFolders()
        {
            var incoming = new[] { "Assets/a.meta", "Library/cache.asset", "Library/missing.asset", "Assets/b.txt", "Temp/x" };
            var listed = new[] { "assets/A.meta", "Library/", "Assets/other.txt" };
            var onDisk = new HashSet<string> { "Library/cache.asset" };

            Assert.That(UnitGitVisitChecks.InTheWay(incoming, listed, onDisk.Contains), Is.EqualTo(new[] { "Assets/a.meta", "Library/cache.asset" }));
            Assert.That(UnitGitVisitChecks.InTheWay(incoming, new string[0], _ => true), Is.Empty);
        }

        [Test]
        public void PackageVersionReadsTheManifest()
        {
            Assert.That(UnitGitVisitChecks.PackageVersion("{ \"name\": \"orbiters.mixer\", \"version\": \"0.2.10\", \"vpmDependencies\": { \"orbiters.toolkit\": \">=0.4.0\" } }"),
                Is.EqualTo("0.2.10"));
            Assert.That(UnitGitVisitChecks.PackageVersion("not json"), Is.Empty);
            Assert.That(UnitGitVisitChecks.PackageVersion(null), Is.Empty);
        }

        [Test]
        public void ToolChangeTextWarnsOnlyWhenTheVisitSwapsUnitGitOrTheToolkit()
        {
            Assert.That(UnitGitVisitChecks.ToolChangeText(new[] { UnitGit(Json("0.2.10"), Json("0.2.10")) }, "abc1234", "main", false), Is.Null, "The same version.");
            Assert.That(UnitGitVisitChecks.ToolChangeText(new[] { UnitGit(null, null) }, "abc1234", "main", false), Is.Null, "Not in Git: the visit leaves it alone.");

            string older = UnitGitVisitChecks.ToolChangeText(new[] { UnitGit(Json("0.2.10"), Json("0.2.4")) }, "abc1234", "main", true);
            Assert.That(older, Does.Contain("Mixer 0.2.4 (0.2.10 now)"));
            Assert.That(older, Does.Contain("Back button"));
            Assert.That(older, Does.Contain("git checkout main"));
            Assert.That(older, Does.Contain("git stash pop"));

            string gone = UnitGitVisitChecks.ToolChangeText(new[] { UnitGit(Json("0.2.10"), null) }, "abc1234", "main", false);
            Assert.That(gone, Does.Contain("no Mixer"));
            Assert.That(gone, Does.Not.Contain("stash"));

            var toolkit = new UnitGitToolPackage { Id = "orbiters.toolkit", Name = "Orbiters Toolkit", Running = "0.4.1", AtHead = Json("0.4.1"), AtTarget = Json("0.3.22") };
            string mixed = UnitGitVisitChecks.ToolChangeText(new[] { UnitGit(Json("0.2.10"), Json("0.2.10")), toolkit }, "abc1234", "main", false);
            Assert.That(mixed, Does.Contain("Orbiters Toolkit 0.3.22 (0.4.1 now)"));
            Assert.That(mixed, Does.Not.Contain("Mixer 0.2.10"));
        }

        [Test]
        public void DialogTextsNameWhatTheyAreAbout()
        {
            string changed = UnitGitVisitChecks.ChangedWhileVisitingText(2, 1, "abc1234", "‘main’");
            Assert.That(changed, Does.Contain("2 files changed"));
            Assert.That(changed, Does.Contain("1 new file was made where ‘main’ has its own"));
            // Without files in the way the text names none (it still says other new files stay).
            Assert.That(UnitGitVisitChecks.ChangedWhileVisitingText(1, 0, "abc1234", "‘main’"), Does.Not.Contain("made where"));

            string ignored = UnitGitVisitChecks.IgnoredInTheWayText(new[] { "Assets/local.cfg" }, "abc1234");
            Assert.That(ignored, Does.Contain("1 ignored file is"));
            Assert.That(ignored, Does.Contain("Assets/local.cfg"));

            Assert.That(UnitGitVisitChecks.CommitsLeftBehindText(2, "going back to ‘main’"), Does.Contain("2 commits made here belong to no branch"));
            Assert.That(UnitGitVisitChecks.ListPaths(new[] { "a", "b", "c", "d" }, 2, ", "), Is.EqualTo("a, b and 2 more"));
            Assert.That(UnitGitVisitChecks.ListPaths(new[] { "a" }, 2, ", "), Is.EqualTo("a"));
        }

        private static UnitGitToolPackage UnitGit(string atHead, string atTarget)
        {
            return new UnitGitToolPackage { Id = "orbiters.mixer", Name = "Mixer", Running = "0.2.10", AtHead = atHead, AtTarget = atTarget };
        }

        private static string Json(string version) => "{ \"name\": \"x\", \"version\": \"" + version + "\" }";
    }
}
