using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Every public and internal method of Mixer Git's service files, and what it may do to the repository. Read from the
    // package's sources: a new method fails here until it is classified, and a classification its body contradicts fails.
    // Leased: its own body holds the operation lease. Via:<entry>: it reaches the repository only through that leased entry.
    // Reads: it runs reads only. Change: fetch/push-like, outside the lease by design (GitAccess.Change). Runner: the runner
    // itself (an exclusive command with no lease there takes one and is recorded for the tests). Helper: a write step only
    // ever called inside an operation (checked at run time by the suite-wide recording). None: no Git.
    public sealed class UnitGitEntryPointTests
    {
        private static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            ["UnitGitBackups.Check"] = "Change", ["UnitGitBackups.Create"] = "Leased", ["UnitGitBackups.DefaultFolder"] = "None",
            ["UnitGitBackups.Delete"] = "None", ["UnitGitBackups.Folder"] = "None", ["UnitGitBackups.IsDue"] = "None",
            ["UnitGitBackups.IsInsideProject"] = "None", ["UnitGitBackups.IsOwn"] = "None", ["UnitGitBackups.List"] = "None",
            ["UnitGitBackups.OwnPrefix"] = "None", ["UnitGitBackups.ProjectName"] = "None", ["UnitGitBackups.RefsToBackUp"] = "Reads",
            ["UnitGitBackups.RestoreCopy"] = "Leased",

            ["UnitGitConflicts.Abort"] = "Leased", ["UnitGitConflicts.Blocks"] = "Reads", ["UnitGitConflicts.Compose"] = "None",
            ["UnitGitConflicts.Describe"] = "None", ["UnitGitConflicts.DetachedBy"] = "None", ["UnitGitConflicts.FileState"] = "None",
            ["UnitGitConflicts.Finish"] = "Leased", ["UnitGitConflicts.IsBisecting"] = "None", ["UnitGitConflicts.IsConflict"] = "None",
            ["UnitGitConflicts.MergeBranch"] = "Leased", ["UnitGitConflicts.Operation"] = "None", ["UnitGitConflicts.Parse"] = "None",
            ["UnitGitConflicts.ParseConflicts"] = "None", ["UnitGitConflicts.Resolve"] = "Leased", ["UnitGitConflicts.Stage"] = "Reads",
            ["UnitGitConflicts.Stages"] = "Reads", ["UnitGitConflicts.TakeSide"] = "Leased", ["UnitGitConflicts.UnityMerge"] = "None",
            ["UnitGitConflicts.UnityMergeTool"] = "None",

            ["UnitGitOverview.ConnectRemoteAsync"] = "Leased", ["UnitGitOverview.CreateRemoteAsync"] = "Via:CreateGitHubRemoteRepository",
            ["UnitGitOverview.InitializeAsync"] = "Via:Initialize", ["UnitGitOverview.IsCliAvailableAsync"] = "None",
            ["UnitGitOverview.LoadAsync"] = "Reads", ["UnitGitOverview.OpenWindow"] = "None",

            ["UnitGitProjectInitializer.EnsureDefaultRules"] = "None", ["UnitGitProjectInitializer.Initialize"] = "Leased",

            ["UnitGitReleases.BuildDefaultCommitTitle"] = "None", ["UnitGitReleases.CommitAll"] = "Leased",
            ["UnitGitReleases.CommitFiles"] = "Leased", ["UnitGitReleases.CommitProjectFilesAsync"] = "Via:CommitFiles",
            ["UnitGitReleases.FindById"] = "None", ["UnitGitReleases.GenerateReleaseId"] = "None", ["UnitGitReleases.GetCapabilities"] = "None",
            ["UnitGitReleases.GetReleasesFilePath"] = "None", ["UnitGitReleases.HideRelease"] = "Leased", ["UnitGitReleases.IsHidden"] = "None",
            ["UnitGitReleases.Load"] = "None", ["UnitGitReleases.NotifyChangedExternally"] = "None",
            ["UnitGitReleases.PublishRelease"] = "Via:RecordRelease", ["UnitGitReleases.PublishReleaseAll"] = "Via:RecordRelease",
            ["UnitGitReleases.TryLoad"] = "None",

            ["UnitGitService.MatchesSearch"] = "None", ["UnitGitService.ParseBranchStatus"] = "None", ["UnitGitService.ParseBranches"] = "None",
            ["UnitGitService.ParseCommit"] = "None", ["UnitGitService.ParseCommitDetails"] = "None", ["UnitGitService.ParseCommitDetailsList"] = "None",
            ["UnitGitService.ParseCommits"] = "None", ["UnitGitService.ParseStashList"] = "None", ["UnitGitService.ParseStatus"] = "None",
            ["UnitGitService.ParseStatusOutput"] = "None", ["UnitGitService.ParseStatusV2"] = "None", ["UnitGitService.ParseStatusV2Output"] = "None",
            ["UnitGitService.ParseUnifiedDiff"] = "None", ["UnitGitService.SplitLines"] = "None", ["UnitGitService.UnquotePath"] = "None",
            ["UnitGitService.EscapeArgument"] = "None", ["UnitGitService.FitsCommandLine"] = "None", ["UnitGitService.FormatCommandLine"] = "None",
            ["UnitGitService.GetGitVersion"] = "Reads", ["UnitGitService.IsGitAvailable"] = "Reads", ["UnitGitService.NulSeparated"] = "None",
            ["UnitGitService.ParseGitVersion"] = "None", ["UnitGitService.ReadGitStream"] = "Runner", ["UnitGitService.RunGit"] = "Runner",
            ["UnitGitService.RunGitForPaths"] = "Helper", ["UnitGitService.RunGitWithEnvironment"] = "Runner",
            ["UnitGitService.RunGitWithInput"] = "Runner", ["UnitGitService.RunWithList"] = "Runner",
            ["UnitGitService.CreateGitHubRemoteRepository"] = "Leased", ["UnitGitService.CreateGitLabRemoteRepository"] = "Leased",
            ["UnitGitService.GitHubLogin"] = "None", ["UnitGitService.GitLabLogin"] = "None", ["UnitGitService.IsGitHubCliAvailable"] = "None",
            ["UnitGitService.IsGitHubCliSignedIn"] = "None", ["UnitGitService.IsGitLabCliAvailable"] = "None", ["UnitGitService.IsGitLabCliSignedIn"] = "None",
            ["UnitGitService.CopyDifference"] = "None", ["UnitGitService.ParseRawCommitForTests"] = "None", ["UnitGitService.RenameCommit"] = "Leased",
            ["UnitGitService.SquashCommits"] = "Leased", ["UnitGitService.SweepScratchRefs"] = "Leased",
            ["UnitGitService.BuildSnapshot"] = "Reads", ["UnitGitService.CanReturnTo"] = "Reads", ["UnitGitService.Checkout"] = "Leased",
            ["UnitGitService.CherryPick"] = "Leased", ["UnitGitService.Commit"] = "Leased", ["UnitGitService.CommitAmend"] = "Leased",
            ["UnitGitService.CommitLogFormat"] = "None", ["UnitGitService.CreateBranch"] = "Leased", ["UnitGitService.DeleteBranch"] = "Leased",
            ["UnitGitService.Fetch"] = "Change", ["UnitGitService.FindShelf"] = "Reads", ["UnitGitService.FindShelfIn"] = "None",
            ["UnitGitService.FormatBytes"] = "None", ["UnitGitService.GetCommitDetails"] = "Reads", ["UnitGitService.GetCommitFileDiff"] = "Reads",
            ["UnitGitService.GetCommitMessage"] = "Reads", ["UnitGitService.GetCommits"] = "Reads", ["UnitGitService.GetFileDiff"] = "Reads",
            ["UnitGitService.GetHeadCommitMessage"] = "Reads", ["UnitGitService.GetRemoteNames"] = "Reads", ["UnitGitService.GetUnityProjectRoot"] = "None",
            ["UnitGitService.HasCommits"] = "Reads", ["UnitGitService.HasRepository"] = "Reads", ["UnitGitService.IsHeadDetached"] = "Reads",
            ["UnitGitService.IsUnityProject"] = "None", ["UnitGitService.PullFastForward"] = "Leased", ["UnitGitService.Push"] = "Change",
            ["UnitGitService.ReadChangeCount"] = "Reads", ["UnitGitService.ReadCommitsOnNoBranch"] = "Reads", ["UnitGitService.ReadFileAt"] = "Reads",
            ["UnitGitService.ReadFilesInTheWay"] = "Reads", ["UnitGitService.ReadHeadState"] = "Reads", ["UnitGitService.ResetCurrentBranch"] = "Leased",
            ["UnitGitService.ReturnFromVisit"] = "Leased", ["UnitGitService.RevertCommit"] = "Leased", ["UnitGitService.Rollback"] = "Leased",
            ["UnitGitService.RunCommitWithMessageFile"] = "Helper", ["UnitGitService.ShelveAll"] = "Leased", ["UnitGitService.ShelveChanges"] = "Leased",
            ["UnitGitService.Signature"] = "None", ["UnitGitService.Stage"] = "Leased", ["UnitGitService.StageAll"] = "Leased",
            ["UnitGitService.StopTracking"] = "Leased", ["UnitGitService.Unstage"] = "Leased", ["UnitGitService.UnstageAll"] = "Leased",
            ["UnitGitService.UpdateBranch"] = "Leased", ["UnitGitService.VisitCommit"] = "Leased", ["UnitGitService.WhitespaceArguments"] = "None",
            ["UnitGitService.WriteBlob"] = "Reads",
        };

        private static readonly string[] Files =
        {
            "UnitGitService.cs", "UnitGitService.Process.cs", "UnitGitService.Parsing.cs", "UnitGitService.Rewrite.cs",
            "UnitGitService.Providers.cs", "UnitGitReleases.cs", "UnitGitBackups.cs", "UnitGitConflicts.cs", "UnitGitOverview.cs",
            "UnitGitProjectInitializer.cs"
        };

        private static readonly Regex Signature = new Regex(
            @"\n        (?:public|internal) (?:static )?(?:async )?(?!class|sealed|enum|struct|const|readonly|event|delegate)[\w<>\[\],. ?]+? (\w+)\(",
            RegexOptions.Compiled);

        // Every public or internal method of the files, by class and name (overloads together), with their bodies.
        private static Dictionary<string, List<string>> Methods(bool includePrivate)
        {
            string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnitGitService).Assembly).resolvedPath;
            var found = new Dictionary<string, List<string>>();
            var signature = includePrivate
                ? new Regex(@"\n        (?:public|internal|private) (?:static )?(?:async )?(?!class|sealed|enum|struct|const|readonly|event|delegate)[\w<>\[\],. ?]+? (\w+)\(", RegexOptions.Compiled)
                : Signature;
            foreach (string file in Files)
            {
                string text = File.ReadAllText(Path.Combine(package, "Editor", "Git", file)).Replace("\r\n", "\n");
                string owner = file.Split('.')[0];
                foreach (Match match in signature.Matches(text))
                {
                    int at = match.Index + match.Length, depth = 1;
                    while (depth > 0 && at < text.Length) { if (text[at] == '(') depth++; if (text[at] == ')') depth--; at++; }
                    string rest = text.Substring(at, Math.Min(200, text.Length - at)).TrimStart();
                    int end;
                    if (rest.StartsWith("=>", StringComparison.Ordinal)) end = text.IndexOf(';', at);
                    else if (rest.StartsWith("{", StringComparison.Ordinal))
                    {
                        int open = text.IndexOf('{', at), braces = 0;
                        end = open;
                        for (; end < text.Length; end++)
                        {
                            if (text[end] == '{') braces++;
                            if (text[end] == '}' && --braces == 0) break;
                        }
                    }
                    else continue;
                    string key = owner + "." + match.Groups[1].Value;
                    if (!found.TryGetValue(key, out var bodies)) found[key] = bodies = new List<string>();
                    bodies.Add(text.Substring(match.Index, end - match.Index));
                }
            }
            return found;
        }

        [Test]
        public void EveryPublicEntryPointIsClassifiedAndItsBodyAgrees()
        {
            Dictionary<string, List<string>> methods = Methods(false);
            Assert.That(methods.Count, Is.GreaterThan(120), "The methods were found in the sources.");
            Dictionary<string, List<string>> all = Methods(true);
            var missing = methods.Keys.Where(key => !Table.ContainsKey(key)).OrderBy(key => key).ToList();
            Assert.That(missing, Is.Empty, "Classify these in the table (leased, via, reads, change, runner, helper or none).");
            var wrong = new List<string>();
            foreach (var entry in methods)
            {
                string kind = Table[entry.Key];
                bool writes = entry.Value.Any(body => body.Contains("GitAccess.Exclusive") || body.Contains("GitAccess.Change"));
                if (kind == "Leased" && !entry.Value.All(body => body.Contains("UnitGitLease.Hold") || CallsLeasedOverload(body, entry.Key, all)))
                    wrong.Add(entry.Key + ": not inside UnitGitLease.Hold");
                if ((kind == "Reads" || kind == "None") && writes) wrong.Add(entry.Key + ": runs a change but is listed as " + kind);
                if (kind.StartsWith("Via:", StringComparison.Ordinal))
                {
                    string target = kind.Substring(4);
                    string owner = entry.Key.Split('.')[0];
                    string targetKey = all.Keys.FirstOrDefault(key => key.EndsWith("." + target, StringComparison.Ordinal) &&
                                                                       all[key].Any(body => body.Contains("UnitGitLease.Hold")));
                    string name = entry.Key.Split('.')[1];
                    bool someReach = all[entry.Key].Any(body => body.Contains(target + "("));
                    if (!someReach || !entry.Value.All(body => body.Contains(target + "(") || body.Contains(name + "(")) || targetKey == null)
                        wrong.Add(entry.Key + ": does not reach the leased " + target);
                }
            }
            Assert.That(wrong, Is.Empty);
        }

        // An overload that forwards to a leased overload of the same name.
        private static bool CallsLeasedOverload(string body, string key, Dictionary<string, List<string>> methods)
        {
            string name = key.Split('.')[1];
            return methods[key].Any(other => other.Contains("UnitGitLease.Hold")) && body.Contains(name + "(");
        }
    }
}
