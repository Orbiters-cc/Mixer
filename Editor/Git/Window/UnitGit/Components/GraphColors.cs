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
        private Color GetGraphColor(UnitGitCommit commit)
        {
            string key = GetCommitBranchKey(commit);
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "HEAD";
            }

            return GetGraphColorForKey(key);
        }

        private Color GetGraphColorForKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "HEAD";
            }

            if (!branchGraphColors.TryGetValue(key, out var color))
            {
                color = BranchGraphPalette[branchGraphColors.Count % BranchGraphPalette.Length];
                branchGraphColors[key] = color;
            }

            return color;
        }

        private string GetCommitBranchKey(UnitGitCommit commit)
        {
            string decorationBranch = GetFirstDecorationBranch(commit != null ? commit.Decorations : null);
            if (!string.IsNullOrWhiteSpace(decorationBranch))
            {
                return decorationBranch;
            }

            if (selectedBranch != null && !string.IsNullOrWhiteSpace(selectedBranch.Name))
            {
                return selectedBranch.Name;
            }

            return snapshot != null ? snapshot.CurrentBranch : "HEAD";
        }

        private static string GetFirstDecorationBranch(string decorations)
        {
            if (string.IsNullOrWhiteSpace(decorations))
            {
                return string.Empty;
            }

            string[] parts = decorations.Split(',');
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int arrowIndex = part.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrowIndex >= 0)
                {
                    part = part.Substring(arrowIndex + 4);
                }

                return part;
            }

            return string.Empty;
        }
    }
}
