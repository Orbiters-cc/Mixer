using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Orbiters.Mixer.Editor;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow : EditorWindow
    {
        private const string StyleSheetPath = "Packages/orbiters.mixer/Editor/Git/Styles/unitgit.uss";
        private const int MaxConsoleLines = 80;
        private const string SplitPrefPrefix = "Orbiters.UnitGit.Split.";
        private const string BranchLogSplitPref = SplitPrefPrefix + "BranchLog";
        private const string LogDetailsSplitPref = SplitPrefPrefix + "LogDetails";
        private const string LocalChangesDiffSplitPref = SplitPrefPrefix + "LocalChangesDiff";
        private const string ShelfConsoleSplitPref = SplitPrefPrefix + "ShelfConsole";
        private const string FoldPrefPrefix = "Orbiters.UnitGit.Fold.";
        private const string BranchLocalFoldPref = FoldPrefPrefix + "Branch.Local";
        private const string BranchRemoteFoldPref = FoldPrefPrefix + "Branch.Remote";
        private const double EditorEventRefreshDelaySeconds = 0.35d;
        private const double EditorEventRefreshCooldownSeconds = 1.25d;
        private const double LogSearchRefreshDelaySeconds = 0.25d;
        internal const int CommitPageSize = 100;

        private static readonly Color[] BranchGraphPalette =
        {
            new Color(0.32f, 0.56f, 0.94f, 1f),
            new Color(0.33f, 0.73f, 0.52f, 1f),
            new Color(0.79f, 0.46f, 0.89f, 1f),
            new Color(0.87f, 0.62f, 0.28f, 1f),
            new Color(0.86f, 0.36f, 0.47f, 1f),
            new Color(0.34f, 0.75f, 0.80f, 1f)
        };

        private readonly List<string> consoleLines = new List<string>();
        private readonly ConcurrentQueue<string> pendingConsoleLines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<Action> pendingMainThreadActions = new ConcurrentQueue<Action>();
        private readonly Dictionary<string, Vector2> scrollOffsets = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> splitSizes = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, Color> branchGraphColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private UnitGitService gitService;
        private UnitGitProjectInitializer initializer;
        private UnitGitSnapshot snapshot;
        private string selectedChangePath = string.Empty;
        private string selectedShelf = string.Empty;
        private VisualElement contentRoot;
        private VisualElement localChangesListRoot;
        private VisualElement localDiffPaneRoot;
        private UnitGitTab activeTab = UnitGitTab.Log;
        private string diffSearch = string.Empty;
        private int diffSearchMatchIndex;
        private TextField diffSearchField;
        [SerializeField] private string commitMessage = string.Empty;
        [SerializeField] private string commitMessageBeforeAmend = string.Empty;
        [SerializeField] private bool commitAmend;
        [SerializeField] private string pendingCommitDescription;
        [SerializeField] private string commitIssue;
        private bool excludePackageFolderFromInitialCommit = true;
        private bool busy;
        private bool refreshQueued;
        private bool refreshingSnapshot;
        private bool refreshAgainRequested;
        private bool editorUpdatePumpActive;
        private double queuedRefreshTime;
        private double lastEditorEventRefreshTime = -1000d;
        private int refreshRequestId;
        // Hidden (another tab of the dock in front): editor events only mark the view stale; it reads Git when shown again.
        private bool visible = true;
        private bool staleWhileHidden;

        // The Log tab: its selection, search, history and the reads it waits for.
        private readonly UnitGitLogState logTab = new UnitGitLogState();

        [MenuItem("Tools/Orbiters/Mixer Git", priority = 2003)]
        internal static void OpenWindow()
        {
            var window = GetWindow<UnitGitWindow>();
            window.titleContent = new GUIContent(UnitGitInfo.TabTitle, MixerLogo.TabIcon(), UnitGitInfo.DisplayName);
            window.minSize = new Vector2(980f, 540f);
            window.Show();
        }

        private void OnEnable()
        {
            // A tab restored from the layout gets its name and Mixer's icon too.
            titleContent = new GUIContent(UnitGitInfo.TabTitle, MixerLogo.TabIcon(), UnitGitInfo.DisplayName);
            if (!string.IsNullOrEmpty(pendingCommitDescription))
            {
                commitIssue = "Unity reloaded during " + pendingCommitDescription + ". Your message is preserved. Check Log for a completed commit before retrying; no commit or push was restarted automatically.";
                pendingCommitDescription = null;
            }
            ResetTransientAsyncState();
            gitService = new UnitGitService();
            initializer = new UnitGitProjectInitializer();
            EditorApplication.projectChanged += OnProjectChanged;
            EditorApplication.focusChanged += OnEditorFocusChanged;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            UnitGitReleases.ChangedExternally += OnReleasesChangedExternally;
        }

        private void OnDisable()
        {
            includeGeneration++;
            refreshRequestId++;
            diffRead.Dispose();
            logTab.detailsRead.Dispose();
            promptRead.Dispose();
            logTab.history?.Dispose();
            logTab.history = null;
            if (diffFont != null)
                DestroyImmediate(diffFont);
            ReleaseThumbnailCacheClear();
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.focusChanged -= OnEditorFocusChanged;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            UnitGitReleases.ChangedExternally -= OnReleasesChangedExternally;
            EditorApplication.delayCall -= RunQueuedRefresh;
            EditorApplication.update -= RunQueuedRefresh;
            EditorApplication.update -= RunQueuedLogSearchRefresh;
            EditorApplication.update -= DrainEditorQueues;
        }

        private void ResetTransientAsyncState()
        {
            includeGeneration++;
            includeRunning = false;
            includeRunningCount = 0;
            includeQueue.Clear();
            pendingInclude.Clear();
            commitWhenIncluded = null;
            refreshRequestId++;
            requestedDiffPath = null;
            logTab.requestedSelection = null;
            busy = false;
            busyLabel = null;
            busySuccess = null;
            // A visit or a Back under way lost its task with the reload (and Unity brings a null string back as ""): what
            // Git reads next is what the banner shows.
            pendingVisit = null;
            pendingReturnBranch = null;
            visitActionDone = 0;
            visitReturning = false;
            refreshQueued = false;
            refreshingSnapshot = false;
            refreshAgainRequested = false;
            editorUpdatePumpActive = false;
            logTab.logSearchRefreshQueued = false;
            gitStorageMeasuring = false;
            while (pendingConsoleLines.TryDequeue(out _))
            {
            }

            while (pendingMainThreadActions.TryDequeue(out _))
            {
            }

            EditorApplication.update -= RunQueuedRefresh;
            EditorApplication.update -= RunQueuedLogSearchRefresh;
            EditorApplication.update -= DrainEditorQueues;
        }

        public void CreateGUI()
        {
            BuildShell();
            refreshAgainRequested = false;
            RefreshSnapshot();
        }

        private enum UnitGitTab
        {
            LocalChanges,
            Shelf,
            Log,
            Console,
            Settings,
            Backups,
            Conflicts
        }
    }
}
