using System;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // The top bar's "Git 1.2 GB": the size of the local Git data, measured in the background after refreshes (at most every
    // 20 seconds for refreshes from editor events; at once after a Git action, an explicit refresh or a click on it).
    internal sealed partial class UnitGitWindow
    {
        private const double GitStorageMinIntervalSeconds = 20d;
        private Button storageWidget;
        private Label storageLabel;
        private bool gitStorageStale = true;
        private bool gitStorageMeasuring;
        private bool gitStorageAsked;

        private Button BuildStorageWidget()
        {
            storageWidget = new Button();
            storageWidget.AddToClassList("ug-storage");
            storageWidget.Add(new UnitGitIconElement(UnitGitIconKind.Storage));
            storageLabel = UnitGitUi.Text(UnitGitStorage.Label(null), "ug-storage__label");
            storageWidget.Add(storageLabel);
            UnitGitUi.Press(storageWidget, () =>
            {
                gitStorageAsked = true;
                RequestGitStorage(true);
            });
            ShowGitStorage(gitService != null ? UnitGitStorage.Cached(gitService.ProjectRoot) : null);
            return storageWidget;
        }

        // F5 and the Refresh button: everything, the Git size too.
        private void RefreshAll()
        {
            gitStorageStale = true;
            RefreshSnapshot();
        }

        private void OnReleasesChangedExternally()
        {
            // An upload commit was made outside this window.
            gitStorageStale = true;
            QueueRefreshFromEditorEvent();
        }

        private void RequestGitStorage(bool force)
        {
            if (gitService == null || storageWidget == null) return;
            string root = gitService.ProjectRoot;
            UnitGitStorageSize cached = UnitGitStorage.Cached(root);
            bool recent = cached != null && (DateTime.UtcNow - cached.MeasuredUtc).TotalSeconds < GitStorageMinIntervalSeconds;
            if (!force && !gitStorageStale && recent)
            {
                ShowGitStorage(cached);
                return;
            }

            gitStorageStale = false;
            gitStorageMeasuring = true;
            ShowGitStorage(cached);
            EnsureEditorUpdatePump();
            UnitGitStorage.MeasureAsync(root).ContinueWith(task => QueueMainThreadAction(() =>
            {
                if (this == null) return;
                gitStorageMeasuring = false;
                gitStorageAsked = false;
                ShowGitStorage(task.Status == TaskStatus.RanToCompletion ? task.Result : UnitGitStorage.Cached(root));
            }));
        }

        private void ShowGitStorage(UnitGitStorageSize size)
        {
            if (storageWidget == null) return;
            storageLabel.text = UnitGitStorage.Label(size);
            storageWidget.tooltip = UnitGitStorage.Tooltip(size);
            // Only a measurement the user asked for dims the figure; background ones keep it steady.
            storageWidget.EnableInClassList("ug-storage--measuring", gitStorageMeasuring && (gitStorageAsked || size == null));
        }
    }
}
