using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>What a commit changed in one scene or prefab, object by object (the Scene view of a past commit).</summary>
    internal sealed class SceneChangesWindow : EditorWindow
    {
        [SerializeField] private string root, path, commit, subject;

        public static void Show(string projectRoot, string file, string commitHash, string commitSubject)
        {
            var window = CreateInstance<SceneChangesWindow>();
            window.root = projectRoot;
            window.path = file;
            window.commit = commitHash;
            window.subject = commitSubject;
            window.titleContent = new GUIContent(Path.GetFileName(file) + " · " + (commitHash.Length > 7 ? commitHash.Substring(0, 7) : commitHash));
            window.minSize = new Vector2(460f, 360f);
            window.Show();
        }

        public void CreateGUI()
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.mixer/Editor/Git/Styles/unitgit-semantic.uss");
            if (sheet != null) rootVisualElement.styleSheets.Add(sheet);
            Orbiters.Mixer.Editor.MixerAccent.Apply(rootVisualElement);
            rootVisualElement.AddToClassList("ugs-window");

            var top = new VisualElement();
            top.AddToClassList("ugs-window__top");
            rootVisualElement.Add(top);
            var name = new Label(Path.GetFileName(path));
            name.AddToClassList("ugs-window__title");
            top.Add(name);
            var beta = new Label("BETA");
            beta.AddToClassList("ugs-beta");
            top.Add(beta);
            var detail = new Label((commit.Length > 7 ? commit.Substring(0, 7) : commit) + "  " + subject);
            detail.AddToClassList("ugs-window__detail");
            rootVisualElement.Add(detail);

            string repo = root, file = path, hash = commit;
            var modes = UnitGitWindow.ModesFor(file);
            modes.Remove(UnitGitWindow.DiffMode.Text);
            var mode = UnitGitWindow.ModeFor(file);
            if (!modes.Contains(mode)) mode = modes[0];
            var content = new VisualElement();
            content.style.flexGrow = 1;
            void Build(UnitGitWindow.DiffMode shown)
            {
                content.Clear();
                if (shown == UnitGitWindow.DiffMode.Scene)
                {
                    var view = new SemanticDiffView();
                    content.Add(view);
                    view.Load("commit|" + repo + "|" + hash + "|" + file, () => Show(repo, hash + "^:" + file), () => Show(repo, hash + ":" + file), "Before this commit", "After", file);
                    return;
                }
                var model = new ModelCompareView("Before this commit", "After");
                var scroll = new ScrollView();
                scroll.style.flexGrow = 1;
                scroll.Add(model);
                content.Add(scroll);
                model.Load("model|commit|" + repo + "|" + hash + "|" + file, repo, file,
                    destination => new UnitGitService(repo).WriteBlob(hash + "^:" + file, destination),
                    destination => new UnitGitService(repo).WriteBlob(hash + ":" + file, destination),
                    null);
            }
            if (modes.Count > 1)
            {
                var switchRow = new VisualElement();
                switchRow.style.flexDirection = FlexDirection.Row;
                switchRow.style.paddingLeft = 12;
                switchRow.style.paddingBottom = 6;
                switchRow.Add(UnitGitWindow.BuildModeSwitch(modes, mode, value =>
                {
                    UnitGitWindow.RememberMode(file, value);
                    Build(value);
                }));
                rootVisualElement.Add(switchRow);
            }
            rootVisualElement.Add(content);
            Build(mode);
        }

        // A file as it was at a revision; empty when it did not exist there (added, deleted, first commit).
        private static string Show(string repo, string revisionPath)
        {
            var result = new UnitGitService(repo).RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "show", revisionPath);
            return result.Success ? result.StandardOutput : string.Empty;
        }
    }
}
