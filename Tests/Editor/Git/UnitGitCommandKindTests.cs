using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Every call into Mixer Git's runner declares what its command does (GitAccess). The guard (KnownRead) only answers
    // "known read" for the exact read-only shapes the code builds; anything else may write. These tests read the calls from
    // the package's own sources, so a new call is checked the day it is written.
    public sealed class UnitGitCommandKindTests
    {
        private static readonly Regex Call = new Regex(
            @"\b(RunGitWithEnvironment|RunGitWithInput|ReadGitStream|RunWithList|RunGit|index\.Run)\(\s*GitAccess\.(Read|Change|Exclusive)\s*,", RegexOptions.Compiled);

        // Arguments before the Git arguments, per entry point (RunGitWithInput with or without its environment).
        private static int Lead(string call, IList<string> args)
        {
            switch (call)
            {
                case "RunGit": return 1;
                case "RunGitWithEnvironment": case "ReadGitStream": return 2;
                case "RunGitWithInput": return args.Count > 2 && (args[1].Contains("Timeout") || Regex.IsMatch(args[1], @"^\d+$")) ? 3 : 2;
                case "index.Run": return 0;
                default: return -1; // RunWithList: its lists are built in code
            }
        }

        private sealed class Site
        {
            public string File, Call, Access;
            public int Line;
            public List<string> Literal = new List<string>();
            public bool Complete;
            public override string ToString() => File + ":" + Line + " " + Access + " " + string.Join(" ", Literal) + (Complete ? string.Empty : " ...");
        }

        private static List<Site> Sites()
        {
            string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnitGitService).Assembly).resolvedPath;
            var sites = new List<Site>();
            foreach (string path in Directory.GetFiles(Path.Combine(package, "Editor", "Git"), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(path);
                foreach (Match match in Call.Matches(text))
                {
                    int end = Close(text, match.Index + match.Groups[1].Length);
                    List<string> args = Split(text.Substring(match.Index + match.Length, end - match.Index - match.Length));
                    var site = new Site
                    {
                        File = Path.GetFileName(path), Call = match.Groups[1].Value, Access = match.Groups[2].Value,
                        Line = text.Take(match.Index).Count(c => c == '\n') + 1
                    };
                    int lead = Lead(site.Call, args);
                    site.Complete = lead >= 0;
                    for (int i = Math.Max(0, lead); lead >= 0 && i < args.Count; i++)
                    {
                        Match literal = Regex.Match(args[i], "^\"((?:[^\"\\\\]|\\\\.)*)\"$");
                        if (!literal.Success) { site.Complete = false; break; }
                        site.Literal.Add(Regex.Unescape(literal.Groups[1].Value));
                    }
                    sites.Add(site);
                }
            }
            return sites;
        }

        private static int Close(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') { i++; while (text[i] != '"') { if (text[i] == '\\') i++; i++; } continue; }
                if (c == '(') depth++;
                if (c == ')' && --depth == 0) return i;
            }
            return text.Length;
        }

        private static List<string> Split(string text)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    int start = i++;
                    while (text[i] != '"') { if (text[i] == '\\') i++; i++; }
                    current.Append(text, start, i - start + 1);
                    continue;
                }
                if ("([{".IndexOf(c) >= 0) depth++;
                if (")]}".IndexOf(c) >= 0) depth--;
                if (c == ',' && depth == 0) { args.Add(current.ToString().Trim()); current.Clear(); continue; }
                current.Append(c);
            }
            if (current.ToString().Trim().Length > 0) args.Add(current.ToString().Trim());
            return args;
        }

        [Test]
        public void EveryDeclaredReadIsAKnownReadAndNoDeclaredWriteLooksLikeOne()
        {
            List<Site> sites = Sites();
            Assert.That(sites.Count, Is.GreaterThan(120), "The calls were found in the sources.");
            var wrong = sites.Where(site => site.Complete && site.Literal.Count > 0 &&
                                            (site.Access == "Read") != UnitGitWriteGate.KnownRead(site.Literal)).Select(site => site.ToString()).ToList();
            Assert.That(wrong, Is.Empty, "Declared access against the guard.");
        }

        // Reads whose command line is built partly in code: each reviewed, by file and literal start, with a full line it
        // builds. A new one fails here until it is reviewed and added.
        private static readonly (string file, string start, string example)[] ReviewedReads =
        {
            ("UnitGitBackups.cs", "stash list --format=%H", "stash list --format=%H"),
            ("UnitGitBackups.cs", "bundle list-heads", "bundle list-heads b.bundle"),
            ("UnitGitBackups.cs", "log -1 --format=%s", "log -1 --format=%s abc"),
            ("UnitGitBackups.cs", "for-each-ref --format=delete %(refname)", "for-each-ref --format=delete %(refname) refs/stash"),
            ("UnitGitConflicts.cs", "-c core.quotePath=true --literal-pathspecs ls-files --unmerged --", "-c core.quotePath=true --literal-pathspecs ls-files --unmerged -- a"),
            ("UnitGitConflicts.cs", "merge-file --diff3 -L", "merge-file --diff3 -L mine -L base -L theirs m b t"),
            ("UnitGitOverview.cs", "remote get-url", "remote get-url origin"),
            ("UnitGitPaths.cs", "", "rev-parse --git-path index --git-path HEAD"),
            ("UnitGitService.cs", "", "status --porcelain=v2 --branch -z --untracked-files=normal"),
            ("UnitGitService.cs", "for-each-ref", "for-each-ref --format=%(objectname) %(refname) refs/heads refs/remotes refs/tags"),
            ("UnitGitService.cs", "ls-files -z --cached --ignored --exclude-standard --", "ls-files -z --cached --ignored --exclude-standard -- Assets/MCB/assets"),
            ("UnitGitService.cs", "rev-list --count", "rev-list --count HEAD..refs/heads/main"),
            ("UnitGitService.cs", "log -1", "log -1 --pretty=format:%h abc --"),
            ("UnitGitService.cs", "diff-tree -r -z --name-only --no-renames --diff-filter=A HEAD", "diff-tree -r -z --name-only --no-renames --diff-filter=A HEAD abc"),
            ("UnitGitService.cs", "cat-file blob", "cat-file blob abc:Assets/a.txt"),
            ("UnitGitService.cs", "rev-parse --verify --quiet", "rev-parse --verify --quiet abc^{commit}"),
            ("UnitGitService.cs", "config --get", "config --get branch.main.merge"),
            ("UnitGitService.cs", "log -1 --format=%B", "log -1 --format=%B abc"),
            ("UnitGitService.cs", "rev-parse --verify", "rev-parse --verify abc"),
            ("UnitGitService.cs", "show -s --format=%h%x1f%p%x1f%P", "show -s --format=%h%x1f%p%x1f%P abc"),
            ("UnitGitService.cs", "--literal-pathspecs cat-file -s", "--literal-pathspecs cat-file -s abc:Assets/a.txt"),
            ("UnitGitService.cs", "-c core.quotePath=true diff --no-ext-diff --name-status -M --diff-filter=R", "-c core.quotePath=true diff --no-ext-diff --name-status -M --diff-filter=R a b"),
            ("UnitGitService.cs", "check-ref-format --branch", "check-ref-format --branch topic"),
            ("UnitGitService.cs", "--literal-pathspecs cat-file blob", "--literal-pathspecs cat-file blob abc:Assets/a.txt"),
            ("UnitGitService.cs", "", "--literal-pathspecs diff --no-ext-diff --unified=3 -- Assets/a.txt"),
            ("UnitGitService.cs", "", "--literal-pathspecs diff --no-ext-diff --find-renames=100% a b -- Assets/a.txt"),
            ("UnitGitService.cs", "", "-c core.quotePath=true show --no-ext-diff --name-status -M --date=format-local:%m/%d/%Y --pretty=format:%h abc"),
            ("UnitGitService.cs", "", "rev-list --count HEAD --not --branches --tags --remotes --"),
            ("UnitGitService.Rewrite.cs", "rev-parse --verify", "rev-parse --verify refs/heads/main"),
            ("UnitGitService.Rewrite.cs", "rev-list --topo-order --reverse", "rev-list --topo-order --reverse abc"),
            ("UnitGitService.Rewrite.cs", "cat-file commit", "cat-file commit abc"),
            ("UnitGitService.Rewrite.cs", "rev-parse --verify --quiet", "rev-parse --verify --quiet refs/mixer-git/rewrite/abc"),
            ("UnitGitService.Rewrite.cs", "for-each-ref --format=%(objectname) %(refname)", "for-each-ref --format=%(objectname) %(refname) refs/mixer-git/rewrite/"),
            ("UnitGitReleases.cs", "", "diff --cached --name-only --no-renames -z"),
            ("ShelfView.cs", "stash show --name-status -z", "stash show --name-status -z stash@{0}"),
            ("ShelfView.cs", "show --name-status -z --format=", "show --name-status -z --format= stash@{0}^3"),
            ("UnitGitService.Process.cs", "", "--literal-pathspecs ls-tree -z HEAD -- Assets/a.txt"),
            ("UnitGitService.Process.cs", "", "ls-files -z --cached"),
        };

        [Test]
        public void ReadsBuiltPartlyInCodeAreReviewed()
        {
            var unreviewed = Sites().Where(site => site.Access == "Read" && (!site.Complete || site.Literal.Count == 0) && site.Call != "RunWithList")
                .Where(site => !ReviewedReads.Any(read => read.file == site.File && read.start == string.Join(" ", site.Literal)))
                .Select(site => site.ToString()).ToList();
            Assert.That(unreviewed, Is.Empty, "Review these reads and add them with a full line they build.");
            var stale = ReviewedReads.Where(read => !Sites().Any(site => site.Access == "Read" && site.File == read.file && string.Join(" ", site.Literal) == read.start))
                .Select(read => read.file + ": " + read.start).ToList();
            Assert.That(stale, Is.Empty, "Reviewed reads no call builds any more.");
            foreach (var read in ReviewedReads)
                Assert.That(UnitGitWriteGate.KnownRead(read.example.Split(' ')), Is.True, read.example);
        }

        [Test]
        public void AnythingNotExactlyAKnownReadMayWrite()
        {
            foreach (string line in new[]
            {
                // The reviewer's counterexamples: an option's value taken for the operation, combined short flags.
                "remote -v add x C:/path", "notes --ref show add -m x", "hash-object -wt blob --stdin",
                // Global options Mixer Git does not put first.
                "-c x=y status", "-C path status", "--no-pager log", "--git-dir=x log", "--git-dir x log", "-c core.quotePath=true -c x=y log",
                // Writing modes and options of reading verbs.
                "log --output=x", "diff --output=x", "stash", "stash push", "stash list extra", "config user.name x", "config --unset user.name",
                "config --get", "branch topic", "branch -v topic", "branch --list -d x", "tag v1", "symbolic-ref HEAD refs/heads/x",
                "symbolic-ref -d HEAD", "bundle create b --all", "bundle unbundle b", "worktree add w", "remote add origin url",
                "remote set-url origin url", "fetch", "push", "update-ref refs/x y", "reflog expire --all", "gc", "lfs fetch", "", "-c",
            })
                Assert.That(UnitGitWriteGate.KnownRead(line.Length == 0 ? new string[0] : line.Split(' ')), Is.False, line);

            foreach (string line in new[]
            {
                "status --porcelain=v2 --branch -z", "--literal-pathspecs status --porcelain", "-c core.quotePath=true ls-files --cached",
                "stash list", "stash show --name-status -z stash@{0}", "branch -vv", "branch --show-current", "config --bool --get commit.gpgSign",
                "config --list", "remote", "remote -v", "remote get-url origin", "symbolic-ref --quiet HEAD", "bundle list-heads b",
                "bundle verify b", "worktree list --porcelain", "--version",
            })
                Assert.That(UnitGitWriteGate.KnownRead(line.Split(' ')), Is.True, line);
        }

        [Test]
        public void AWrongReadDeclarationRunsAsAChangeAndAChangeIsNeverDowngraded()
        {
            Assert.That(UnitGitWriteGate.Effective(GitAccess.Read, new[] { "remote", "-v", "add", "x", "p" }), Is.EqualTo(GitAccess.Exclusive));
            Assert.That(UnitGitWriteGate.Effective(GitAccess.Read, new[] { "status", "-z" }), Is.EqualTo(GitAccess.Read));
            Assert.That(UnitGitWriteGate.Effective(GitAccess.Change, new[] { "status", "-z" }), Is.EqualTo(GitAccess.Change));
            Assert.That(UnitGitWriteGate.Effective(GitAccess.Exclusive, new[] { "status", "-z" }), Is.EqualTo(GitAccess.Exclusive));
        }
    }
}
