using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The Log tab's state (the branches, the history, the commit or release shown in its details): what used to be
    /// fields of <see cref="UnitGitWindow"/> shared by every partial file. The window holds one; the views read and write it.
    /// </summary>
    internal sealed class UnitGitLogState
    {
        internal UnitGitCommit selectedCommit;
        internal readonly HashSet<string> selectedCommitHashes = new HashSet<string>(StringComparer.Ordinal);
        internal string selectionAnchorHash = string.Empty;
        internal string selectedReleaseId = string.Empty;
        internal UnitGitCommitDetails selectedDetails;
        internal UnitGitBranch selectedBranch;
        internal VisualElement commitDetailsRoot;
        internal string branchSearch = string.Empty;
        internal string logSearch = string.Empty;
        internal string logSearchDraft = string.Empty;
        internal int historyLimit = UnitGitWindow.CommitPageSize * 3;
        internal bool logSearchRefreshQueued;
        internal double queuedLogSearchRefreshTime;
        // The history as it streams: "load more" and searches read on from where it stopped.
        internal UnitGitHistory history;
        internal VisualElement branchesListRoot;
        internal string selectedCommitFile = string.Empty;
        internal readonly UnitGitLatestRequest<UnitGitWindow.SelectionDetails> detailsRead = new UnitGitLatestRequest<UnitGitWindow.SelectionDetails>();
        internal readonly Dictionary<string, UnitGitCommitDetails> detailsCache = new Dictionary<string, UnitGitCommitDetails>();
        internal UnitGitWindow.SelectionDetails loadedSelection;
        internal string requestedSelection;
        internal readonly UnitGitLogFilter logFilter = new UnitGitLogFilter();
        internal readonly List<UnitGitWindow.LogItem> logItems = new List<UnitGitWindow.LogItem>();
        internal ListView logList;
        internal Label logCountLabel;
        internal float graphWidth = 40f;
        // The commit graph laid out so far, and the commit ids it was laid out for (see LayOutGraph).
        internal UnitGitCommitGraph.Builder graphBuilder;
        internal readonly List<UnitGitCommitGraph.Row> graphRows = new List<UnitGitCommitGraph.Row>();
        internal readonly List<string> graphHashes = new List<string>();
        internal bool loadingMoreHistory;
        internal bool historyOnlyRequested;
        // The release thumbnail, kept while the same file is shown: details are rebuilt on every refresh.
        internal string releaseThumbnailPath = string.Empty;
        internal DateTime releaseThumbnailWrite;
        internal Texture2D releaseThumbnail;
        // The entrance plays when another release is selected, not on each refresh of the same one.
        internal string releaseShownId = string.Empty;

        /// <summary>After a snapshot: the branch, commits and details selected follow what the repository now has (the current branch, the newest commit, when they are gone).</summary>
        internal void Reconcile(UnitGitSnapshot snapshot)
        {
            string selectedRef = selectedBranch?.FullRef;
            selectedBranch = snapshot.Branches.FirstOrDefault(branch => branch.FullRef == selectedRef)
                ?? snapshot.Branches.FirstOrDefault(branch => branch.IsCurrent)
                ?? snapshot.Branches.FirstOrDefault();

            if (snapshot.HasRepository && snapshot.Commits.Count > 0)
            {
                var availableHashes = new HashSet<string>(snapshot.Commits.Select(commit => commit.FullHash), StringComparer.Ordinal);
                selectedCommitHashes.RemoveWhere(hash => !availableHashes.Contains(hash));
                if (!string.IsNullOrWhiteSpace(selectionAnchorHash) &&
                    snapshot.Commits.All(commit => commit.FullHash != selectionAnchorHash))
                {
                    selectionAnchorHash = string.Empty;
                }

                if (selectedCommit == null || snapshot.Commits.All(commit => commit.FullHash != selectedCommit.FullHash))
                {
                    selectedCommit = selectedCommitHashes.Count > 0
                        ? snapshot.Commits.FirstOrDefault(commit => selectedCommitHashes.Contains(commit.FullHash)) ?? snapshot.Commits[0]
                        : snapshot.Commits[0];
                    selectedReleaseId = string.Empty;
                }

                if (selectedCommitHashes.Count == 0 && selectedCommit != null && string.IsNullOrEmpty(selectedReleaseId))
                {
                    selectedCommitHashes.Add(selectedCommit.FullHash);
                    selectionAnchorHash = selectedCommit.FullHash;
                }

                if (selectedDetails == null ||
                         selectedDetails.Commit == null ||
                         selectedCommit == null ||
                         selectedDetails.Commit.FullHash != selectedCommit.FullHash)
                {
                    selectedDetails = null;
                }
            }
            else
            {
                selectedCommit = null;
                selectedCommitHashes.Clear();
                selectionAnchorHash = string.Empty;
                selectedReleaseId = string.Empty;
                selectedDetails = null;
            }
        }
    }
}
