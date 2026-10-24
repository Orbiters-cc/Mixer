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
        private void OnEditorFocusChanged(bool focused)
        {
            if (focused)
            {
                QueueRefreshFromEditorEvent();
            }
        }

        private void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            QueueRefreshFromEditorEvent();
        }

        private void QueueRefreshFromEditorEvent()
        {
            double now = EditorApplication.timeSinceStartup;
            double nextAllowedRefresh = lastEditorEventRefreshTime + EditorEventRefreshCooldownSeconds;
            queuedRefreshTime = Math.Max(now + EditorEventRefreshDelaySeconds, nextAllowedRefresh);
            if (refreshQueued)
            {
                return;
            }

            refreshQueued = true;
            EditorApplication.update += RunQueuedRefresh;
        }

        private void RunQueuedRefresh()
        {
            if (this == null)
            {
                EditorApplication.update -= RunQueuedRefresh;
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.timeSinceStartup < queuedRefreshTime)
            {
                return;
            }

            EditorApplication.update -= RunQueuedRefresh;
            refreshQueued = false;
            if (busy)
            {
                refreshAgainRequested = true;
                return;
            }

            lastEditorEventRefreshTime = EditorApplication.timeSinceStartup;
            RefreshSnapshot();
        }

        private void QueueLogSearchRefresh()
        {
            historyLimit = CommitPageSize * 3;
            queuedLogSearchRefreshTime = EditorApplication.timeSinceStartup + LogSearchRefreshDelaySeconds;
            if (logSearchRefreshQueued)
            {
                return;
            }

            logSearchRefreshQueued = true;
            EditorApplication.update += RunQueuedLogSearchRefresh;
        }

        private void RunQueuedLogSearchRefresh()
        {
            if (this == null)
            {
                EditorApplication.update -= RunQueuedLogSearchRefresh;
                return;
            }

            if (EditorApplication.timeSinceStartup < queuedLogSearchRefreshTime)
            {
                return;
            }

            EditorApplication.update -= RunQueuedLogSearchRefresh;
            logSearchRefreshQueued = false;
            if (string.Equals(logSearch, logSearchDraft, StringComparison.Ordinal))
            {
                return;
            }

            logSearch = logSearchDraft;
            RefreshSnapshot();
        }

        private void RefreshSnapshot()
        {
            if (gitService == null)
            {
                return;
            }

            RememberScrollOffsets();
            if (busy)
            {
                refreshAgainRequested = true;
                return;
            }
            if (refreshingSnapshot)
            {
                if (!editorUpdatePumpActive && pendingMainThreadActions.IsEmpty)
                {
                    refreshingSnapshot = false;
                    AppendConsole("refresh", "recovered stale refresh state");
                }
                else
                {
                    refreshAgainRequested = true;
                    return;
                }
            }

            if (refreshingSnapshot)
            {
                refreshAgainRequested = true;
                return;
            }

            refreshingSnapshot = true;
            int requestId = ++refreshRequestId;
            string projectRoot = gitService.ProjectRoot;
            string search = logSearch;
            int requestedHistoryLimit = historyLimit;
            // Read before the worker starts (EditorPrefs is main-thread only); what was read before a visit began is older than it.
            string visitBranch = UnitGitSettings.Visit?.branch;
            long readStarted = DateTime.UtcNow.Ticks;
            UnitGitSnapshot previousSnapshot = snapshot;
            var filter = new UnitGitLogFilter { Branch = logFilter.Branch, Author = logFilter.Author, Since = logFilter.Since, Path = logFilter.Path };
            try
            {
                EnsureEditorUpdatePump();
                Task.Run(() => BuildSnapshotRefreshResult(
                        projectRoot,
                        search,
                        filter,
                        previousSnapshot,
                        line =>
                        {
                            if (ShouldLogRefreshProcessLine(line))
                            {
                                QueueConsoleLine("refresh", line);
                            }
                        }, requestedHistoryLimit, () => requestId != refreshRequestId || logSearchDraft != search, visitBranch))
                    .ContinueWith(task => QueueMainThreadAction(() => ApplySnapshotRefresh(requestId, task, readStarted)));
            }
            catch (Exception ex)
            {
                refreshingSnapshot = false;
                AppendConsole("refresh", "error: " + ex.Message);
                RebuildContent();
            }
        }

        private static SnapshotRefreshResult BuildSnapshotRefreshResult(
            string projectRoot,
            string search,
            UnitGitLogFilter filter,
            UnitGitSnapshot previousSnapshot,
            UnitGitProcessLogHandler logHandler, int historyLimit = 300, Func<bool> superseded = null, string visitBranch = null)
        {
            var service = new UnitGitService(projectRoot);
            service.ProcessLogReceived = logHandler;
            bool wasSuperseded = false;
            service.ReadSuperseded = () => wasSuperseded = wasSuperseded || (superseded?.Invoke() ?? false);
            UnitGitSnapshot nextSnapshot = service.BuildSnapshot(search, historyLimit, filter, visitBranch);
            var result = new SnapshotRefreshResult
            {
                Snapshot = nextSnapshot,
                Superseded = wasSuperseded,
                ChangesUnchanged = previousSnapshot != null &&
                    previousSnapshot.Changes.Count == nextSnapshot.Changes.Count &&
                    previousSnapshot.Changes.Zip(nextSnapshot.Changes, (a, b) =>
                        a.Path == b.Path && a.OriginalPath == b.OriginalPath &&
                        a.IndexStatus == b.IndexStatus && a.WorkTreeStatus == b.WorkTreeStatus).All(equal => equal)
            };

            return result;
        }

        private void ApplySnapshotRefresh(int requestId, Task<SnapshotRefreshResult> task, long readStarted)
        {
            if (this == null)
            {
                return;
            }

            if (requestId != refreshRequestId)
            {
                return;
            }

            refreshingSnapshot = false;
            loadingMoreHistory = false;
            if (task.Status == TaskStatus.RanToCompletion && task.Result.Superseded)
            {
                // Keep the current view while the search debounce or the replacement refresh runs.
                refreshAgainRequested = false;
                if (logSearch == logSearchDraft) RefreshSnapshot();
                return;
            }
            if (task.Status != TaskStatus.RanToCompletion && !string.IsNullOrEmpty(logFilter.Branch))
            {
                // The branch the log was filtered to is gone (deleted, renamed): back to every branch.
                logFilter.Branch = string.Empty;
                ShowToast("That branch no longer exists: showing every branch.");
                RefreshSnapshot();
                return;
            }
            if (task.Status != TaskStatus.RanToCompletion)
            {
                Exception error = task.Exception != null ? task.Exception.GetBaseException() : null;
                AppendConsole("refresh", "error: " + (error != null ? error.Message : "Failed to refresh Git state."));
            }
            else if (task.Result != null && task.Result.Snapshot != null)
            {
                snapshot = task.Result.Snapshot;
                ReconcileSnapshotSelection();
                ReconcilePendingInclude();
                ReconcileVisit(readStarted);
                if (snapshot.HasRepository) RequestGitStorage(false);
            }

            bool preserveLocalView = activeTab == UnitGitTab.LocalChanges && localChangesListRoot != null &&
                task.Status == TaskStatus.RanToCompletion && snapshot.HasRepository && snapshot.HasCommits &&
                string.IsNullOrWhiteSpace(snapshot.LastError);
            if (preserveLocalView)
            {
                UpdateChangesNotices();
                if (!task.Result.ChangesUnchanged)
                    RefreshChangesList();
                else
                {
                    changesListView?.RefreshItems();
                    UpdateCommitSummary();
                }
                var change = GetSelectedChange();
                bool changedPath = requestedDiffPath != (change != null ? change.Path : string.Empty);
                RequestLocalDiff(change);
                if (changedPath)
                    RebuildLocalDiffPane();
                RefreshTopBar();
            }
            else if (activeTab == UnitGitTab.Log && logList != null && logList.panel != null && task.Status == TaskStatus.RanToCompletion &&
                     snapshot.HasRepository && snapshot.HasCommits && string.IsNullOrWhiteSpace(snapshot.LastError) && snapshot.Commits.Count > 0)
            {
                UpdateTopBar();
                RefreshLogInPlace();
            }
            else
            {
                requestedDiffPath = null;
                UpdateTopBar();
                RebuildContent();
            }
            RestoreScrollOffsets();

            if (refreshAgainRequested)
            {
                refreshAgainRequested = false;
                RefreshSnapshot();
            }
        }

        private void ReconcileSnapshotSelection()
        {
            string selectedRef = selectedBranch?.FullRef;
            selectedBranch = snapshot.Branches.FirstOrDefault(branch => branch.FullRef == selectedRef)
                ?? snapshot.Branches.FirstOrDefault(branch => branch.IsCurrent)
                ?? snapshot.Branches.FirstOrDefault();

            var present = new HashSet<string>(snapshot.Changes.Select(change => change.Path), StringComparer.OrdinalIgnoreCase);
            selectedChangePaths.RemoveWhere(path => !present.Contains(path));
            if (snapshot.Changes.Count > 0 &&
                (string.IsNullOrWhiteSpace(selectedChangePath) ||
                 snapshot.Changes.All(change => !string.Equals(change.Path, selectedChangePath, StringComparison.OrdinalIgnoreCase))))
            {
                selectedChangePath = snapshot.Changes[0].Path;
            }
            else if (snapshot.Changes.Count == 0)
            {
                selectedChangePath = string.Empty;
            }
            if (selectedChangePaths.Count == 0 && !string.IsNullOrEmpty(selectedChangePath))
                selectedChangePaths.Add(selectedChangePath);

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

        private sealed class SnapshotRefreshResult
        {
            public UnitGitSnapshot Snapshot;
            public bool Superseded;
            public bool ChangesUnchanged;
        }
    }
}
