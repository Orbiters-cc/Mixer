using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>Dialogs in Mixer Git's style: a title and a line of context, the content, then the actions; Enter and Escape work.</summary>
    internal static class UnitGitDialog
    {
        internal static VisualElement Setup(EditorWindow window, string title, string subtitle)
        {
            var root = window.rootVisualElement;
            root.Clear();
            root.AddToClassList("unitgit-root");
            root.AddToClassList("ug-dialog");
            foreach (string path in new[] { "Packages/orbiters.mixer/Editor/Git/Styles/unitgit-shell.uss", "Packages/orbiters.mixer/Editor/Git/Styles/unitgit.uss" })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            }
            root.Add(UnitGitUi.Text(title, "ug-dialog__title"));
            if (!string.IsNullOrWhiteSpace(subtitle)) root.Add(UnitGitUi.Text(subtitle, "ug-dialog__subtitle"));
            var body = new VisualElement();
            body.AddToClassList("ug-dialog__body");
            root.Add(body);
            root.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape) window.Close(); }, TrickleDown.TrickleDown);
            return body;
        }

        internal static VisualElement Actions(EditorWindow window)
        {
            var actions = new VisualElement();
            actions.AddToClassList("ug-dialog__actions");
            actions.Add(UnitGitUi.Spacer());
            window.rootVisualElement.Add(actions);
            return actions;
        }
    }

    internal sealed partial class UnitGitWindow
    {
        private sealed class UnitGitBranchPromptWindow : EditorWindow
        {
            private string branchName;
            private Action<string> onCreate;

            public static void Open(string defaultName, Action<string> onCreate)
            {
                var window = CreateInstance<UnitGitBranchPromptWindow>();
                window.titleContent = new GUIContent("New Branch");
                window.branchName = defaultName;
                window.onCreate = onCreate;
                window.minSize = new Vector2(400f, 176f);
                window.maxSize = new Vector2(560f, 176f);
                window.ShowUtility();
            }

            // Git's rules for branch names, in words.
            internal static string Problem(string name)
            {
                name = (name ?? string.Empty).Trim();
                if (name.Length == 0) return "Give the branch a name.";
                if (name.Any(char.IsWhiteSpace)) return "No spaces: use - or / instead.";
                if (name.StartsWith("-", StringComparison.Ordinal) || name.StartsWith("/", StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal))
                    return "It can't start with - or /, nor end with /.";
                if (name.Contains("..") || name.Contains("//") || name.Contains("@{") || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(".lock", StringComparison.Ordinal))
                    return "It can't contain .. or //, nor end with . or .lock.";
                if (name.IndexOfAny(new[] { '~', '^', ':', '?', '*', '[', '\\' }) >= 0) return "It can't contain ~ ^ : ? * [ or \\.";
                return null;
            }

            public void CreateGUI()
            {
                var body = UnitGitDialog.Setup(this, "New branch", "From the selected commit; Mixer Git switches to it.");
                var field = new TextField { value = branchName };
                field.AddToClassList("ug-dialog__field");
                body.Add(field);
                var hint = UnitGitUi.Text(string.Empty, "ug-dialog__hint");
                body.Add(hint);
                var actions = UnitGitDialog.Actions(this);
                actions.Add(UnitGitUi.Pill("Cancel", Close, "ghost"));
                Button create = null;
                void Submit()
                {
                    if (Problem(branchName) != null) return;
                    onCreate?.Invoke(branchName.Trim());
                    Close();
                }
                create = UnitGitUi.Pill("Create", Submit, "primary", UnitGitIconKind.CreateBranch);
                actions.Add(create);
                void Validate()
                {
                    string problem = Problem(branchName);
                    hint.text = problem ?? string.Empty;
                    create.SetEnabled(problem == null);
                }
                field.RegisterValueChangedCallback(evt => { branchName = evt.newValue; Validate(); });
                field.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                    evt.StopPropagation();
                    Submit();
                }, TrickleDown.TrickleDown);
                Validate();
                field.schedule.Execute(() =>
                {
                    field.Focus();
                    field.SelectAll();
                }).StartingIn(30);
            }
        }
    }
}
