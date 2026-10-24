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
        private void AppendConsole(string label, GitCommandResult result)
        {
            string status = result != null && result.Success ? "ok" : "error";
            string message = result != null ? result.Message : "No result.";
            AppendConsole(label, status + ": " + message);
        }

        private void AppendConsole(string label, string message)
        {
            AddConsoleLine(FormatConsoleLine(label, message));
        }

        private void QueueConsoleLine(string label, string message)
        {
            pendingConsoleLines.Enqueue(FormatConsoleLine(label, SanitizeConsoleMessage(message)));
            while (pendingConsoleLines.Count > MaxConsoleLines * 2)
                pendingConsoleLines.TryDequeue(out _);
        }

        private void QueueMainThreadAction(Action action)
        {
            if (action != null)
            {
                pendingMainThreadActions.Enqueue(action);
            }
        }

        private void EnsureEditorUpdatePump()
        {
            if (editorUpdatePumpActive)
            {
                return;
            }

            editorUpdatePumpActive = true;
            EditorApplication.update += DrainEditorQueues;
        }

        private void DrainEditorQueues()
        {
            UpdateCommitSummary();
            PollDiffRead();
            PollCommitDetails();
            PollPromptRead();
            var budget = System.Diagnostics.Stopwatch.StartNew();
            while (pendingMainThreadActions.TryDequeue(out Action action))
            {
                action();
                if (budget.ElapsedMilliseconds >= 4)
                    break;
            }

            bool changed = false;
            while (pendingConsoleLines.TryDequeue(out string line))
            {
                AddConsoleLine(line);
                changed = true;
                if (budget.ElapsedMilliseconds >= 4)
                    break;
            }

            if (!busy && !includeRunning && includeQueue.Count == 0 && commitWhenIncluded == null && !refreshingSnapshot && !diffRead.IsBusy && !detailsRead.IsBusy && !promptRead.IsBusy && !gitStorageMeasuring && pendingConsoleLines.IsEmpty && pendingMainThreadActions.IsEmpty)
            {
                editorUpdatePumpActive = false;
                EditorApplication.update -= DrainEditorQueues;
            }

            if (changed && activeTab == UnitGitTab.Console)
                RefreshConsole(scrollToEnd: false);
        }

        private void AddConsoleLine(string line)
        {
            consoleLines.Insert(0, line);
            while (consoleLines.Count > MaxConsoleLines)
            {
                consoleLines.RemoveAt(consoleLines.Count - 1);
            }
        }

        private static string FormatConsoleLine(string label, string message)
        {
            return DateTime.Now.ToString("HH:mm:ss") + " " + label + " - " + SanitizeConsoleMessage(message);
        }

        private static bool ShouldLogRefreshProcessLine(string line)
        {
            return line != null &&
                   (line.StartsWith(">", StringComparison.Ordinal) ||
                    line.StartsWith("cwd:", StringComparison.Ordinal) ||
                    line.StartsWith("exit:", StringComparison.Ordinal) ||
                    line.StartsWith("timed out", StringComparison.Ordinal) ||
                    line.StartsWith("error:", StringComparison.Ordinal));
        }

        private static string SanitizeConsoleMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            // Remote URLs and Git's errors can carry a token: the console never shows one.
            message = UnitGitRedaction.Redact(message);
            bool hasControlCharacter = false;
            for (int i = 0; i < message.Length; i++)
            {
                if (char.IsControl(message[i]) && message[i] != '\t')
                {
                    hasControlCharacter = true;
                    break;
                }
            }

            if (!hasControlCharacter)
            {
                return message;
            }

            var sanitized = new System.Text.StringBuilder(message.Length);
            for (int i = 0; i < message.Length; i++)
            {
                char character = message[i];
                if (char.IsControl(character) && character != '\t')
                {
                    sanitized.Append("\\x");
                    sanitized.Append(((int)character).ToString("X2"));
                    continue;
                }

                sanitized.Append(character);
            }

            return sanitized.ToString();
        }
    }
}
