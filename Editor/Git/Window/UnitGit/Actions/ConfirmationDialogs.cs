using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private bool ConfirmGitOperation(string title, string actionLabel, string command, string summary, string warning, int? affectedFileCount)
        {
            return EditorUtility.DisplayDialog(
                title,
                BuildOperationPreview(command, summary, warning, affectedFileCount),
                actionLabel,
                "Cancel");
        }

        private string BuildOperationPreview(string command, string summary, string warning, int? affectedFileCount)
        {
            var builder = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                builder.AppendLine(summary.Trim());
                builder.AppendLine();
            }

            builder.AppendLine("Current branch: " + GetPreviewBranchName());
            builder.AppendLine("Affected files: " + (affectedFileCount.HasValue ? affectedFileCount.Value.ToString() : "not counted"));
            builder.AppendLine("Command:");
            foreach (string line in SplitPreviewLines(command))
            {
                builder.AppendLine("  " + line);
            }

            if (!string.IsNullOrWhiteSpace(warning))
            {
                builder.AppendLine();
                builder.AppendLine("Warning: " + warning.Trim());
            }

            return builder.ToString().TrimEnd();
        }

        private static IEnumerable<string> SplitPreviewLines(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                yield return "(none)";
                yield break;
            }

            foreach (string line in command.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return line;
                }
            }
        }

        private string GetPreviewBranchName()
        {
            if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.CurrentBranch))
            {
                return snapshot.CurrentBranch;
            }

            if (IsVisiting)
            {
                return "none (visiting " + snapshot.DetachedCommit.ShortHash + ")";
            }

            return "(none)";
        }

        private int GetLocalChangeCount()
        {
            return snapshot != null ? snapshot.Changes.Count : 0;
        }

        private int GetStagedChangeCount()
        {
            return snapshot != null ? snapshot.Changes.Count(change => change.IsStaged) : 0;
        }

        private static string GitCommand(params string[] arguments)
        {
            return UnitGitService.FormatCommandLine("git", arguments);
        }

        private static string RemoteCliCommand(UnitGitRemoteProvider provider, params string[] arguments)
        {
            return UnitGitService.FormatCommandLine(provider == UnitGitRemoteProvider.GitLab ? "glab" : "gh", arguments);
        }
    }
}
