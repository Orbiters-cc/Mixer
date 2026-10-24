using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// Keeps Unity from refreshing assets or reloading scripts while Git rewrites the project (a visit, Back): a reload stops
    /// git.exe midway, leaving a half-checked-out tree and Git's index.lock behind. Quitting asks first, and Play mode waits.
    /// The hold ends on the main thread once the Git work is done, whether the window still exists or not, and the project
    /// is then refreshed once.
    /// </summary>
    internal static class UnitGitCheckoutHold
    {
        // Main thread: holds begun and not released yet.
        private static int held;
        // Any thread: holds whose Git work has ended.
        private static int finished;

        /// <summary>On the main thread, right before the Git work starts. Each call needs one <see cref="WorkEnded"/>.</summary>
        internal static void Begin()
        {
            AssetDatabase.DisallowAutoRefresh();
            EditorApplication.LockReloadAssemblies();
            if (held++ > 0) return;
            EditorApplication.update += ReleaseFinished;
            EditorApplication.wantsToQuit += ConfirmQuit;
            EditorApplication.playModeStateChanged += WaitWithPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
        }

        /// <summary>From any thread, once the Git work has ended (in a finally): the hold ends on the next editor update.</summary>
        internal static void WorkEnded()
        {
            Interlocked.Increment(ref finished);
        }

        private static void ReleaseFinished()
        {
            int ended = Interlocked.Exchange(ref finished, 0);
            if (ended == 0) return;
            Release(Math.Min(ended, held));
            if (held == 0) AssetDatabase.Refresh();
        }

        // A reload that happens anyway must not leave Unity's own counters held for good (no refresh or reload ever again).
        private static void ReleaseAll()
        {
            Release(held);
        }

        private static void Release(int count)
        {
            for (int i = 0; i < count; i++)
            {
                EditorApplication.UnlockReloadAssemblies();
                AssetDatabase.AllowAutoRefresh();
            }

            held -= count;
            if (held > 0) return;
            EditorApplication.update -= ReleaseFinished;
            EditorApplication.wantsToQuit -= ConfirmQuit;
            EditorApplication.playModeStateChanged -= WaitWithPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAll;
        }

        private static bool ConfirmQuit()
        {
            if (Application.isBatchMode) return true;
            return !EditorUtility.DisplayDialog("Mixer Git",
                "Git is still checking out files. Quitting now stops it midway, and the project is left half one version, half the other.",
                "Wait", "Quit Anyway");
        }

        // Entering Play mode reloads scripts, which would stop Git midway.
        private static void WaitWithPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode) return;
            EditorApplication.isPlaying = false;
            Debug.LogWarning("[Mixer Git] Play mode waits until Git has checked out the files.");
        }
    }
}
