using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Orbiters.Logger.Editor
{
    /// <summary>Where the project stands in Git, for the README and the AI diagnosis.</summary>
    internal sealed class GitDigest
    {
        public bool Repository;

        /// <summary>A repository made with git init and nothing committed yet.</summary>
        public bool NoCommits;
        public string Branch = string.Empty;
        public string Head = string.Empty;
        public int Commits;
        public int Changed;
        public readonly List<string> Recent = new List<string>();
        public readonly List<string> Extras = new List<string>();
        public string Error;
    }

    /// <summary>
    /// The Git part of a report: the history with the files each commit changed, what is changed now, where HEAD went
    /// (reflog), the remotes without their passwords, and what Git-aware packages add (Mixer Git releases). Anonymizing,
    /// commits are listed without their authors and the remotes without their addresses.
    /// </summary>
    internal static class GitReport
    {
        private const int MaxOutput = 6 * 1024 * 1024;
        private const long MaxExtraBytes = 64L * 1024 * 1024;

        /// <summary>The numbers shown on the card; safe on a worker thread.</summary>
        internal static GitDigest Look(string root)
        {
            var digest = new GitDigest { Repository = GitCli.IsRepository(root) };
            if (!digest.Repository)
            {
                return digest;
            }

            // HEAD resolves only once something is committed: a new repository is not an error (-q keeps that quiet,
            // a broken one still says "fatal: …").
            var head = GitCli.Run(root, "rev-parse", "-q", "--verify", "HEAD");
            if (!head.Completed || !head.Success && head.Error.Trim().Length > 0)
            {
                digest.Error = FirstLine(head.Error);
                return digest;
            }

            digest.NoCommits = !head.Success;
            var branch = GitCli.Run(root, "symbolic-ref", "--short", "-q", "HEAD");
            digest.Branch = branch.Success ? branch.Output.Trim() : digest.NoCommits ? "(no branch)" : "(detached HEAD)";
            if (!digest.NoCommits)
            {
                digest.Head = GitCli.Run(root, "rev-parse", "--short", "HEAD").Output.Trim();
                int.TryParse(GitCli.Run(root, "rev-list", "--count", "HEAD").Output.Trim(), out digest.Commits);
                digest.Recent.AddRange(Lines(GitCli.Run(root, "log", "-n", "15", "--format=%s").Output));
            }

            digest.Changed = Lines(GitCli.Run(root, "status", "--porcelain=v1").Output).Count;
            return digest;
        }

        /// <summary>Writes the Git files; safe on a worker thread.</summary>
        internal static string Write(ReportContext context, string root, IReadOnlyList<ReportExtra> extras, Action<float> progress)
        {
            var digest = Look(root);
            context.Git = digest;
            if (!digest.Repository)
            {
                return "Not a Git repository";
            }

            if (digest.Error != null)
            {
                throw new InvalidOperationException(digest.Error);
            }

            bool anonymize = context.Anonymize;
            progress?.Invoke(0.2f);
            var summary = new StringBuilder();
            summary.Append("Branch   ").Append(digest.Branch).Append('\n');
            if (digest.NoCommits)
            {
                summary.Append("HEAD     (nothing committed yet)\n");
            }
            else
            {
                string format = anonymize ? "--format=%H%n         %s%n         %ad" : "--format=%H%n         %s%n         %an <%ae>, %ad";
                summary.Append("HEAD     ").Append(GitCli.Run(root, "log", "-1", "--date=iso-local", format).Output.TrimEnd()).Append('\n');
            }

            summary.Append("Commits  ").Append(digest.Commits).Append('\n');
            summary.Append("Changed  ").Append(digest.Changed).Append(" files not committed\n");
            string upstream = GitCli.Run(root, "status", "-sb", "--porcelain=v1").Output;
            summary.Append("Tracking ").Append(Lines(upstream).FirstOrDefault()?.TrimStart('#', ' ') ?? string.Empty).Append('\n');
            summary.Append("\nRemotes\n").Append(Indent(Remotes(GitCli.Run(root, "remote", "-v").Output, anonymize)));
            summary.Append("\nLarge files (Git LFS)\n").Append(Indent(GitCli.Run(root, "lfs", "ls-files").Output, "  (none or Git LFS not installed)"));
            context.WriteText("git/summary.txt", summary.ToString(), "Branch, last commit, remotes and Git LFS files");
            progress?.Invoke(0.35f);

            if (digest.NoCommits)
            {
                Save(context, root, "git/status.txt", "Files changed, added or deleted (nothing is committed yet)", "status", "--porcelain=v1", "-uall");
            }
            else
            {
                // Without rename detection: it reads every big binary file of each commit (45 s instead of 0.2 s on an
                // avatar project). An explicit format: the author's name and email only when not anonymizing.
                string log = anonymize ? "--format=commit %H%nDate:   %ad%n%n    %s%n" : "--format=commit %H%nAuthor: %an <%ae>%nDate:   %ad%n%n%w(0,4,4)%B";
                Save(context, root, "git/log.txt", anonymize
                        ? "The last 300 commits with the files each one added, changed or deleted (no authors: anonymized)"
                        : "The last 300 commits with their authors and the files each one added, changed or deleted",
                    "log", "-n", "300", "--name-status", "--no-renames", "--date=iso-local", log);
                progress?.Invoke(0.6f);
                Save(context, root, "git/status.txt", "Files changed, added or deleted since the last commit", "status", "--porcelain=v1", "-uall");
                Save(context, root, "git/diff-stat.txt", "How much each uncommitted file changed", "diff", "--stat=160", "--no-renames", "HEAD");
                Save(context, root, "git/reflog.txt", "Where the project went: commits, checkouts, pulls and resets", "reflog", "-n", "300", "--date=iso-local");
                Save(context, root, "git/stashes.txt", "Changes put aside with git stash", "stash", "list");
            }

            progress?.Invoke(0.8f);
            foreach (var extra in extras)
            {
                if (CopyExtra(context, extra))
                {
                    digest.Extras.Add(extra.Label);
                }
            }

            progress?.Invoke(1f);
            string commits = digest.NoCommits ? "No commits yet" : LoggerUi.Plural(digest.Commits, "commit");
            return commits + " · " + digest.Branch + (digest.Extras.Count > 0 ? " · " + string.Join(", ", digest.Extras) : string.Empty);
        }

        // "origin\thttps://…  (fetch)": anonymizing, the address goes and the name stays.
        internal static string Remotes(string output, bool anonymize)
        {
            if (!anonymize)
            {
                return output;
            }

            return string.Join("\n", Lines(output).Select(line =>
            {
                int tab = line.IndexOfAny(new[] { '\t', ' ' });
                int kind = line.LastIndexOf(" (", StringComparison.Ordinal);
                return tab < 0 ? line : line.Substring(0, tab) + "\t" + ReportPrivacy.HiddenRemote + (kind > tab ? line.Substring(kind) : string.Empty);
            }));
        }

        private static void Save(ReportContext context, string root, string name, string label, params string[] arguments)
        {
            var result = GitCli.Run(root, 30000, arguments);
            string text = result.Success ? result.Output : "git " + string.Join(" ", arguments) + " failed:\n" + result.Error;
            context.WriteText(name, text, label, MaxOutput);
        }

        private static bool CopyExtra(ReportContext context, ReportExtra extra)
        {
            // Pictures and other binaries are copied as they are: nothing in them can be anonymized.
            if (context.Anonymize && extra.Binary)
            {
                return false;
            }

            try
            {
                if (File.Exists(extra.Path))
                {
                    if (!CopyFile(context, extra.Path, extra.Name))
                    {
                        return false;
                    }

                    context.Add(extra.Name, extra.Label);
                    return true;
                }

                if (!Directory.Exists(extra.Path))
                {
                    return false;
                }

                // The newest files first, up to a size: a folder of release pictures can grow without end.
                int copied = 0, skipped = 0;
                long bytes = 0;
                var folder = new DirectoryInfo(extra.Path);
                foreach (var file in folder.GetFiles("*", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    if (bytes + file.Length > MaxExtraBytes)
                    {
                        skipped++;
                        continue;
                    }

                    string relative = file.FullName.Substring(folder.FullName.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (CopyFile(context, file.FullName, extra.Name + "/" + relative))
                    {
                        copied++;
                        bytes += file.Length;
                    }
                }

                if (copied == 0)
                {
                    return false;
                }

                context.Add(extra.Name + "/", extra.Label + (skipped > 0 ? " (the newest " + SceneReport.Size(MaxExtraBytes) + "; " + LoggerUi.Plural(skipped, "older file") + " left out)" : string.Empty));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".txt", ".md", ".yaml", ".yml", ".xml", ".csv", ".log", ".ini"
        };

        // Text files are masked like everything the report writes; pictures and other binaries are copied as they are,
        // or left out when anonymizing.
        private static bool CopyFile(ReportContext context, string from, string name)
        {
            if (TextExtensions.Contains(Path.GetExtension(from)))
            {
                File.WriteAllText(context.PathOf(name), context.Privacy.Mask(File.ReadAllText(from)), new UTF8Encoding(false));
                return true;
            }

            if (context.Anonymize)
            {
                return false;
            }

            File.Copy(from, context.PathOf(name), true);
            return true;
        }

        internal static List<string> Lines(string text) =>
            (text ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();

        private static string FirstLine(string text) => Lines(text).FirstOrDefault() ?? "Git did not answer.";

        private static string Indent(string text, string empty = "  (none)")
        {
            var lines = Lines(text);
            return lines.Count == 0 ? empty + "\n" : string.Concat(lines.Select(line => "  " + line + "\n"));
        }
    }
}
