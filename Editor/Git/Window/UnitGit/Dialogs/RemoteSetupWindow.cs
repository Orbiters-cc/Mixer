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
        private sealed class UnitGitGitHubSetupWindow : EditorWindow
        {
            private UnitGitRemoteProvider provider = UnitGitRemoteProvider.GitHub;
            private string repositoryName;
            private string remoteName;
            private bool isPrivate = true;
            private Action<UnitGitRemoteProvider> onLogin;
            private Action<UnitGitRemoteProvider, string, string, bool> onCreateRemote;

            public static void Open(string defaultRepositoryName, Action<UnitGitRemoteProvider> onLogin, Action<UnitGitRemoteProvider, string, string, bool> onCreateRemote)
            {
                var window = CreateInstance<UnitGitGitHubSetupWindow>();
                window.titleContent = new GUIContent("Remote Setup");
                window.repositoryName = defaultRepositoryName;
                window.remoteName = "origin";
                window.onLogin = onLogin;
                window.onCreateRemote = onCreateRemote;
                window.minSize = new Vector2(460f, 290f);
                window.maxSize = new Vector2(640f, 340f);
                window.ShowUtility();
            }

            public void CreateGUI()
            {
                var body = UnitGitDialog.Setup(this, "Connect a remote",
                    "Signs in with the GitHub or GitLab command line tool, creates the repository and adds it as a remote. Nothing is pushed until you push.");

                var providers = new VisualElement();
                providers.AddToClassList("ug-segmented");
                var buttons = new System.Collections.Generic.List<Button>();
                foreach (var option in new[] { UnitGitRemoteProvider.GitHub, UnitGitRemoteProvider.GitLab })
                {
                    var value = option;
                    var button = new Button { text = value.ToString() };
                    button.AddToClassList("ug-segmented__option");
                    UnitGitUi.Press(button, () =>
                    {
                        provider = value;
                        foreach (var other in buttons) other.EnableInClassList("ug-segmented__option--on", other == button);
                    });
                    button.EnableInClassList("ug-segmented__option--on", provider == value);
                    buttons.Add(button);
                    providers.Add(button);
                }
                body.Add(Field("Service", providers));

                var repositoryField = new TextField { value = repositoryName };
                repositoryField.AddToClassList("ug-dialog__field");
                repositoryField.RegisterValueChangedCallback(evt => repositoryName = evt.newValue);
                body.Add(Field("Repository", repositoryField));

                var remoteField = new TextField { value = remoteName };
                remoteField.AddToClassList("ug-dialog__field");
                remoteField.RegisterValueChangedCallback(evt => remoteName = evt.newValue);
                body.Add(Field("Remote name", remoteField));

                body.Add(Field("Private repository", new Orbiters.Toolkit.Editor.ToggleSwitch(isPrivate, value => isPrivate = value)));

                var actions = UnitGitDialog.Actions(this);
                actions.Insert(0, UnitGitUi.Pill("Sign in", () => onLogin?.Invoke(provider), "ghost", UnitGitIconKind.User,
                    "Opens the GitHub or GitLab command line sign-in"));
                actions.Add(UnitGitUi.Pill("Cancel", Close, "ghost"));
                actions.Add(UnitGitUi.Pill("Create remote", () =>
                {
                    onCreateRemote?.Invoke(provider, repositoryName, remoteName, isPrivate);
                    Close();
                }, "primary", UnitGitIconKind.Remote));
            }

            private static VisualElement Field(string label, VisualElement control)
            {
                var row = new VisualElement();
                row.AddToClassList("ug-dialog__row");
                row.Add(UnitGitUi.Text(label, "ug-dialog__label"));
                if (control is TextField) control.style.flexGrow = 1;
                row.Add(control);
                return row;
            }
        }
    }
}
