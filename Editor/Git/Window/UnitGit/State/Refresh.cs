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
                QueueRefreshFromEditorEvent(true);
            }
        }

        private void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            QueueRefreshFromEditorEvent();
        }

        private void OnProjectChanged()
        {
            QueueRefreshFromEditorEvent();
        }

        private void OnBecameVisible()
        {
            visible = true;
            if (!staleWhileHidden) return;
            staleWhileHidden = false;
            QueueRefreshFromEditorEvent();
        }

        private void OnBecameInvisible()
        {
            visible = false;
        }

        // Editor events come in bursts (a save, an import, the focus): one read after the last of them. Each read runs status
        // and the refs (another program may have changed anything); the further slices are read only when those moved.
        private void QueueRefreshFromEditorEvent(bool focusGain = false)
        {
            if (!visible)
            {
                staleWhileHidden = true;
                return;
            }

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
            if (!visible)
            {
                staleWhileHidden = true;
                return;
            }

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
            logTab.historyLimit = CommitPageSize * 3;
            logTab.queuedLogSearchRefreshTime = EditorApplication.timeSinceStartup + LogSearchRefreshDelaySeconds;
            if (logTab.logSearchRefreshQueued)
            {
                return;
            }

            logTab.logSearchRefreshQueued = true;
            EditorApplication.update += RunQueuedLogSearchRefresh;
        }

        private void RunQueuedLogSearchRefresh()
        {
            if (this == null)
            {
                EditorApplication.update -= RunQueuedLogSearchRefresh;
                return;
            }

            if (EditorApplication.timeSinceStartup < logTab.queuedLogSearchRefreshTime)
            {
                return;
            }

            EditorApplication.update -= RunQueuedLogSearchRefresh;
            logTab.logSearchRefreshQueued = false;
            if (string.Equals(logTab.logSearch, logTab.logSearchDraft, StringComparison.Ordinal))
            {
                return;
            }

            logTab.logSearch = logTab.logSearchDraft;
            RefreshSnapshot();
        }

        private void RefreshSnapshot()
        {
            // "Load more" asks for older commits only; a refresh that cannot start now runs later as a whole one.
            bool historyOnly = logTab.historyOnlyRequested;
            logTab.historyOnlyRequested = false;
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
            string search = logTab.logSearch;
            int requestedHistoryLimit = logTab.historyLimit;
            historyOnly &= snapshot != null;
            bool readHeadMessage = commitAmend;
            if (logTab.history == null) logTab.history = new UnitGitHistory();
            UnitGitHistory reader = logTab.history;
            // Read before the worker starts (EditorPrefs is main-thread only); what was read before a visit began is older than it.
            string visitBranch = UnitGitSettings.Visit?.branch;
            long readStarted = DateTime.UtcNow.Ticks;
            UnitGitSnapshot previousSnapshot = snapshot;
            var filter = new UnitGitLogFilter { Branch = logTab.logFilter.Branch, Author = logTab.logFilter.Author, Since = logTab.logFilter.Since, Path = logTab.logFilter.Path };
            try
            {
                EnsureEditorUpdatePump();
                var request = new UnitGitSnapshotRequest
                {
                    LogSearch = search, HistoryLimit = requestedHistoryLimit, Filter = filter, VisitBranch = visitBranch,
                    Previous = previousSnapshot, History = reader, ReadHeadMessage = readHeadMessage, HistoryOnly = historyOnly
                };
                Task.Run(() => BuildSnapshotRefreshResult(
                        projectRoot,
                        request,
                        line =>
                        {
                            if (ShouldLogRefreshProcessLine(line))
                            {
                                QueueConsoleLine("refresh", line);
                            }
                        }, () => requestId != refreshRequestId || logTab.logSearchDraft != search))
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
            UnitGitSnapshotRequest request,
            UnitGitProcessLogHandler logHandler, Func<bool> superseded = null)
        {
            UnitGitSnapshot previousSnapshot = request.Previous;
            var service = new UnitGitService(projectRoot);
            service.ProcessLogReceived = logHandler;
            bool wasSuperseded = false;
            service.ReadSuperseded = () => wasSuperseded = wasSuperseded || (superseded?.Invoke() ?? false);
            UnitGitSnapshot nextSnapshot = service.BuildSnapshot(request);
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
            logTab.loadingMoreHistory = false;
            if (task.Status == TaskStatus.RanToCompletion && task.Result.Superseded)
            {
                // Keep the current view while the search debounce or the replacement refresh runs.
                refreshAgainRequested = false;
                if (logTab.logSearch == logTab.logSearchDraft) RefreshSnapshot();
                return;
            }
            if (task.Status != TaskStatus.RanToCompletion && !string.IsNullOrEmpty(logTab.logFilter.Branch))
            {
                // The branch the log was filtered to is gone (deleted, renamed): back to every branch.
                logTab.logFilter.Branch = string.Empty;
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

            bool preserveLocalView = activeTab == UnitGitTab.LocalChanges && changesTab.localChangesListRoot != null &&
                task.Status == TaskStatus.RanToCompletion && snapshot.HasRepository && snapshot.HasCommits &&
                string.IsNullOrWhiteSpace(snapshot.LastError);
            if (preserveLocalView)
            {
                UpdateChangesNotices();
                if (!task.Result.ChangesUnchanged)
                    RefreshChangesList();
                else
                {
                    changesTab.changesListView?.RefreshItems();
                    UpdateCommitSummary();
                }
                var change = GetSelectedChange();
                bool changedPath = changesTab.requestedDiffPath != (change != null ? change.Path : string.Empty);
                RequestLocalDiff(change);
                if (changedPath)
                    RebuildLocalDiffPane();
                RefreshTopBar();
            }
            else if (activeTab == UnitGitTab.Log && logTab.logList != null && logTab.logList.panel != null && task.Status == TaskStatus.RanToCompletion &&
                     snapshot.HasRepository && snapshot.HasCommits && string.IsNullOrWhiteSpace(snapshot.LastError) && snapshot.Commits.Count > 0)
            {
                UpdateTopBar();
                RefreshLogInPlace();
            }
            else
            {
                changesTab.requestedDiffPath = null;
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

        // After each snapshot: every tab's selection follows what the repository now has.
        private void ReconcileSnapshotSelection()
        {
            logTab.Reconcile(snapshot);
            changesTab.Reconcile(snapshot);
        }

        private sealed class SnapshotRefreshResult
        {
            public UnitGitSnapshot Snapshot;
            public bool Superseded;
            public bool ChangesUnchanged;
        }
    }
}
