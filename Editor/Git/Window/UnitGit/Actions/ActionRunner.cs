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
        private readonly UnitGitLatestRequest<GitCommandResult> promptRead = new UnitGitLatestRequest<GitCommandResult>();
        private Action<GitCommandResult> promptReadComplete;

        private void ReadForPrompt(Func<GitCommandResult> read, Action<GitCommandResult> complete)
        {
            promptReadComplete = complete;
            promptRead.Request(stale => ExecuteGitAction(read));
            EnsureEditorUpdatePump();
        }

        private void PollPromptRead()
        {
            if (!promptRead.Poll(out GitCommandResult result, out Exception error))
                return;
            var complete = promptReadComplete;
            promptReadComplete = null;
            if (error != null || result == null || !result.Success)
            {
                EditorUtility.DisplayDialog("Mixer Git", error?.Message ?? result?.Message ?? "Could not read Git state.", "OK");
                return;
            }
            complete?.Invoke(result);
        }

        // What the top bar says while an action runs, e.g. "Pushing…".
        private string busyLabel;
        // The notification a successful action shows, if any.
        private string busySuccess;

        /// <param name="holdEditor">The action checks out other files (a visit, Back): no asset refresh or script reload may
        /// stop Git midway, and the project is refreshed once it is done (<see cref="UnitGitCheckoutHold"/>).</param>
        private void RunAction(string label, Func<GitCommandResult> action, Action<GitCommandResult> onComplete = null, string working = null, string success = null,
            bool holdEditor = false)
        {
            // A commit waiting for its files goes first: nothing may change the repository under it.
            if (busy || commitWhenIncluded != null)
            {
                return;
            }

            busy = true;
            busyLabel = working ?? char.ToUpperInvariant(label[0]) + label.Substring(1) + "…";
            busySuccess = success;
            AppendConsole(label, "started");
            RefreshTopBar();
            EnsureEditorUpdatePump();
            if (holdEditor) UnitGitCheckoutHold.Begin();

            Task.Run(() =>
                {
                    gitService.ProcessLogReceived = line => QueueConsoleLine(label, line);
                    try
                    {
                        return ExecuteGitAction(action);
                    }
                    finally
                    {
                        gitService.ProcessLogReceived = null;
                        if (holdEditor) UnitGitCheckoutHold.WorkEnded();
                    }
                })
                .ContinueWith(task => QueueMainThreadAction(() => CompleteGitAction(label, task, onComplete)));
        }

        private static GitCommandResult ExecuteGitAction(Func<GitCommandResult> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                return new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = ex.Message
                };
            }
        }

        private void CompleteGitAction(string label, Task<GitCommandResult> task, Action<GitCommandResult> onComplete)
        {
            if (this == null)
            {
                return;
            }

            busy = false;
            string success = busySuccess;
            busyLabel = null;
            busySuccess = null;
            GitCommandResult result = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = task.Exception != null ? task.Exception.GetBaseException().Message : "The command failed."
                };
            AppendConsole(label, result);
            if (result == null || !result.Success)
            {
                string message = result != null && !string.IsNullOrWhiteSpace(result.Message)
                    ? UnitGitRedaction.Redact(result.Message.Trim())
                    : "The command failed.";
                if (message.Length > 260) message = message.Substring(0, 257) + "…";
                ShowToast(message, error: true, "Console", () => SetActiveTab(UnitGitTab.Console));
            }
            else if (!string.IsNullOrEmpty(success))
            {
                ShowToast(success);
            }

            try
            {
                onComplete?.Invoke(result);
            }
            catch (Exception ex)
            {
                AppendConsole(label, "completion callback failed: " + ex.Message);
            }

            // Commits, fetches, merges, gc… change the size of the Git data.
            gitStorageStale = true;
            RefreshSnapshot();
        }
    }
}
