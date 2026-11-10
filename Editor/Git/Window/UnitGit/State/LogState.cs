using System;
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
        internal bool loadingMoreHistory;
        internal bool historyOnlyRequested;
        // The release thumbnail, kept while the same file is shown: details are rebuilt on every refresh.
        internal string releaseThumbnailPath = string.Empty;
        internal DateTime releaseThumbnailWrite;
        internal Texture2D releaseThumbnail;
        // The entrance plays when another release is selected, not on each refresh of the same one.
        internal string releaseShownId = string.Empty;
    }
}
