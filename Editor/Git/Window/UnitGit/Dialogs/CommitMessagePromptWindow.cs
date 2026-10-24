using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>A message to write (a commit's, a shelf's name): Ctrl+Enter confirms, Escape cancels.</summary>
    internal sealed class UnitGitCommitMessagePromptWindow : EditorWindow
    {
        private Action<string> onSubmit;
        private string message = string.Empty;
        private string summary = string.Empty;
        private string submitLabel = "Apply";

        public static void Open(string title, string summary, string initialMessage, string submitLabel, Action<string> onSubmit)
        {
            var window = CreateInstance<UnitGitCommitMessagePromptWindow>();
            window.titleContent = new GUIContent(title);
            window.summary = summary ?? string.Empty;
            window.message = initialMessage ?? string.Empty;
            window.submitLabel = string.IsNullOrWhiteSpace(submitLabel) ? "Apply" : submitLabel;
            window.onSubmit = onSubmit;
            window.minSize = new Vector2(480f, 250f);
            window.ShowUtility();
        }

        private void CreateGUI()
        {
            var body = UnitGitDialog.Setup(this, titleContent != null ? titleContent.text : "Message", summary);
            var field = new TextField { multiline = true, value = message };
            field.AddToClassList("ug-dialog__field");
            field.AddToClassList("ug-dialog__field--multiline");
            field.RegisterValueChangedCallback(evt => message = evt.newValue);
            body.Add(field);

            var actions = UnitGitDialog.Actions(this);
            actions.Insert(0, UnitGitUi.Text("Ctrl+Enter", "ug-dialog__shortcut"));
            actions.Add(UnitGitUi.Pill("Cancel", Close, "ghost"));
            void Submit()
            {
                onSubmit?.Invoke(message);
                Close();
            }
            actions.Add(UnitGitUi.Pill(submitLabel, Submit, "primary"));
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if ((evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) || !(evt.ctrlKey || evt.commandKey)) return;
                evt.StopPropagation();
                Submit();
            }, TrickleDown.TrickleDown);
            field.schedule.Execute(() =>
            {
                field.Focus();
                field.SelectAll();
            }).StartingIn(30);
        }
    }
}
