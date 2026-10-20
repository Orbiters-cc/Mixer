using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// The hierarchy model over the open scenes, and the window driven without showing it (a popup far off screen). The
    /// fixture lives in an untitled scene of its own, opened beside the user's scenes and removed afterwards.
    /// </summary>
    public sealed class MixerHierarchyTests
    {
        private Scene scene, activeBefore;
        private GameObject root;
        private Object[] selectionBefore;

        [SetUp]
        public void SetUp()
        {
            selectionBefore = Selection.objects;
            activeBefore = SceneManager.GetActiveScene();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            // New objects go to the active scene: the fixture's own while it is built.
            SceneManager.SetActiveScene(scene);
            root = new GameObject("MixerHierarchyFixture");
            for (int i = 0; i < 3; i++)
            {
                var child = new GameObject("Child " + i);
                child.transform.SetParent(root.transform, false);
                if (i == 1) child.AddComponent<Light>();
                var grandChild = GameObject.CreatePrimitive(PrimitiveType.Cube);
                grandChild.name = "Cube " + i;
                grandChild.transform.SetParent(child.transform, false);
            }
            if (activeBefore.IsValid() && activeBefore.isLoaded) SceneManager.SetActiveScene(activeBefore);
        }

        [TearDown]
        public void TearDown()
        {
            Selection.objects = selectionBefore;
            if (root != null) SceneVisibilityManager.instance.Show(root, true);
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            if (activeBefore.IsValid() && activeBefore.isLoaded && SceneManager.GetActiveScene() != activeBefore) SceneManager.SetActiveScene(activeBefore);
        }

        // The window as a popup far off screen: built and laid out, never shown to the user.
        private static MixerHierarchyWindow OpenHidden()
        {
            var window = ScriptableObject.CreateInstance<MixerHierarchyWindow>();
            window.position = new Rect(-32000, -32000, 420, 600);
            var show = typeof(EditorWindow).GetMethod("ShowPopupWithMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var showMode = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");
            show.Invoke(window, new object[] { System.Enum.Parse(showMode, "PopupMenu"), false });
            Repaint(window);
            return window;
        }

        // The user's own scenes come first and may fill the window (rows with pictures are tall): the fixture's root goes
        // to the top, so what opens under it is on screen.
        private static void ScrollToTop(MixerHierarchyWindow window, GameObject go)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var list = (Orbiters.Toolkit.Editor.VirtualList)typeof(MixerHierarchyWindow).GetField("list", flags).GetValue(window);
            var model = (HierarchyModel)typeof(MixerHierarchyWindow).GetField("model", flags).GetValue(window);
            int index = model.IndexOf(go.GetInstanceID());
            list.SetFirst(list.First + list.TopOf(index) / list.RowHeight);
        }

        private static void Repaint(EditorWindow window) =>
            typeof(EditorWindow).GetMethod("RepaintImmediately", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);

        // Pooled rows that are off screen keep their last text: only a shown row counts.
        private static VisualElement RowNamed(MixerHierarchyWindow window, string name) => window.rootVisualElement.Query(className: "mxh-row").ToList()
            .Where(r => r.resolvedStyle.display != DisplayStyle.None && r.layout.y >= 0)
            .FirstOrDefault(r => r.Q<Label>(className: "mxh-row__name")?.text == name);

        private static void Press(MixerHierarchyWindow window, VisualElement element, EventModifiers modifiers = EventModifiers.None)
        {
            var down = new Event { type = EventType.MouseDown, mousePosition = element.worldBound.center, button = 0, clickCount = 1, modifiers = modifiers };
            using (var evt = PointerDownEvent.GetPooled(down)) { evt.target = element; window.rootVisualElement.panel.visualTree.SendEvent(evt); }
        }

        [Test]
        public void TheModelFollowsExpansionAndFilters()
        {
            var model = new HierarchyModel();
            var filter = new HierarchyFilter();
            model.Rebuild(filter);
            int rootIndex = model.IndexOf(root.GetInstanceID());
            Assert.That(rootIndex, Is.GreaterThanOrEqualTo(0), "the fixture root is a row");
            Assert.That(model.Rows.Count(r => r.IsScene), Is.EqualTo(SceneManager.sceneCount), "one header per scene");
            model.SetExpanded(root.GetInstanceID(), true);
            model.Rebuild(filter);
            Assert.That(model.IndexOf(root.transform.GetChild(0).gameObject.GetInstanceID()), Is.GreaterThan(model.IndexOf(root.GetInstanceID())), "children follow their parent once open");
            Assert.That(model.IndexOf(root.transform.GetChild(0).GetChild(0).gameObject.GetInstanceID()), Is.LessThan(0), "grandchildren stay folded");
            model.SetExpanded(root.GetInstanceID(), false);
            model.Rebuild(filter);
            Assert.That(model.IndexOf(root.transform.GetChild(0).gameObject.GetInstanceID()), Is.LessThan(0));

            // Folding the fixture's scene hides its objects and nothing else (scene folds are kept apart from objects').
            model.SetSceneFolded(scene, true);
            model.Rebuild(filter);
            Assert.That(model.IndexOf(root.GetInstanceID()), Is.LessThan(0), "a folded scene hides its roots");
            Assert.That(model.Rows.Any(r => r.IsScene && r.Scene == scene && !r.Expanded), Is.True);
            model.SetSceneFolded(scene, false);
            model.Rebuild(filter);
            Assert.That(model.IndexOf(root.GetInstanceID()), Is.GreaterThanOrEqualTo(0));

            filter.Pattern = new Toolkit.Editor.SearchPattern("Cube", false, false);
            model.Rebuild(filter);
            var names = model.Rows.Where(r => r.Object != null).Select(r => r.Name).ToList();
            Assert.That(names, Does.Contain("Cube 0").And.Contain("Cube 2"));
            Assert.That(names, Does.Contain("MixerHierarchyFixture"), "ancestors of matches stay, dimmed");
            Assert.That(model.Rows.First(r => r.Name == "MixerHierarchyFixture").Match, Is.False);
            Assert.That(model.Rows.First(r => r.Name == "Cube 1").Match, Is.True);
            filter.Pattern = new Toolkit.Editor.SearchPattern("t:Light", false, false);
            model.Rebuild(filter);
            var mine = model.Rows.Where(r => r.Object != null && r.Match && r.Object.transform.IsChildOf(root.transform)).Select(r => r.Name);
            Assert.That(mine, Is.EquivalentTo(new[] { "Child 1" }), "the fixture's only light matches t:Light");

            // A regular expression over names, with the case when asked.
            filter.Pattern = new Toolkit.Editor.SearchPattern("^Cube [02]$", true, false);
            model.Rebuild(filter);
            mine = model.Rows.Where(r => r.Object != null && r.Match && r.Object.transform.IsChildOf(root.transform)).Select(r => r.Name);
            Assert.That(mine, Is.EquivalentTo(new[] { "Cube 0", "Cube 2" }), "a regular expression matches names");
            filter.Pattern = new Toolkit.Editor.SearchPattern("^cube", true, true);
            model.Rebuild(filter);
            Assert.That(model.Rows.Any(r => r.Object != null && r.Match && r.Object.transform.IsChildOf(root.transform)), Is.False, "Match case keeps \"cube\" from matching \"Cube\"");
            filter.Pattern = new Toolkit.Editor.SearchPattern("cube 1", false, true);
            model.Rebuild(filter);
            Assert.That(model.Rows.Any(r => r.Object != null && r.Match && r.Object.transform.IsChildOf(root.transform)), Is.False, "Match case applies to plain words too");
            filter.Pattern = new Toolkit.Editor.SearchPattern("(Cube", true, false);
            Assert.That(filter.Pattern.Error, Is.Not.Null, "an invalid expression says why");
            Assert.That(filter.Any, Is.False, "and filters nothing out");

            filter.Pattern = Toolkit.Editor.SearchPattern.Empty; filter.Lights = true;
            model.Rebuild(filter);
            mine = model.Rows.Where(r => r.Object != null && r.Match && r.Object.transform.IsChildOf(root.transform)).Select(r => r.Name);
            Assert.That(mine, Is.EquivalentTo(new[] { "Child 1" }), "the Lights chip keeps the fixture's light");
        }

        [Test]
        public void HiddenChildrenGiveNoFoldArrow()
        {
            var leaf = root.transform.GetChild(2).GetChild(0);
            var helper = new GameObject("__XRayGizmos_helper") { hideFlags = HideFlags.HideInHierarchy };
            helper.transform.SetParent(leaf, false);
            var model = new HierarchyModel();
            model.Reveal(leaf.gameObject);
            model.Rebuild(new HierarchyFilter());
            var row = model[model.IndexOf(leaf.gameObject.GetInstanceID())];
            Assert.That(row, Is.Not.Null);
            Assert.That(row.HasChildren, Is.False, "a leaf whose only child is hidden from the hierarchy has no chevron");
        }

        [Test]
        public void TheWindowBuildsRowsAndSelectsOnClick()
        {
            var window = OpenHidden();
            try
            {
                window.RevealObject(root);
                Repaint(window);
                var rows = window.rootVisualElement.Query(className: "mxh-row").ToList().Where(r => r.resolvedStyle.display != DisplayStyle.None).ToList();
                Assert.That(rows.Count, Is.GreaterThan(1), "rows are on screen");
                var row = RowNamed(window, "MixerHierarchyFixture");
                Assert.That(row, Is.Not.Null, "the fixture root shows");
                Press(window, row);
                Assert.That(Selection.activeGameObject, Is.SameAs(root), "a click selects the object");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void TheFoldArrowOpensAndClosesABranch()
        {
            var window = OpenHidden();
            try
            {
                var tick = typeof(MixerHierarchyWindow).GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                window.RevealObject(root);
                tick.Invoke(window, null);
                ScrollToTop(window, root);
                Repaint(window);
                System.Func<System.Collections.Generic.List<string>> shown = () => window.rootVisualElement.Query(className: "mxh-row").ToList()
                    .Where(r => r.resolvedStyle.display != DisplayStyle.None).Select(r => r.Q<Label>(className: "mxh-row__name")).Where(l => l != null).Select(l => l.text).ToList();
                System.Action press = () =>
                {
                    var chevron = RowNamed(window, "MixerHierarchyFixture").Q(className: "mxh-row__chevron");
                    Press(window, chevron);
                    // No Tick: the press itself shows the branch open or folded (the 60 ms burst throttle is for hierarchy changes).
                    // A fold shortens the list, which scrolls the fixture down: back to the top, so its children are on screen.
                    ScrollToTop(window, root);
                    Repaint(window);
                };
                bool openAtFirst = shown().Contains("Child 0");
                press();
                Assert.That(shown().Contains("Child 0"), Is.EqualTo(!openAtFirst), "one press on the arrow folds or unfolds the branch at once");
                press();
                Assert.That(shown().Contains("Child 0"), Is.EqualTo(openAtFirst), "a second press puts it back");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void TheEyeHidesTheBranchAndAltClickTheObjectAlone()
        {
            var child = root.transform.GetChild(0).gameObject;
            var window = OpenHidden();
            try
            {
                window.RevealObject(child);
                Repaint(window);
                var visibility = SceneVisibilityManager.instance;
                Press(window, RowNamed(window, "MixerHierarchyFixture").Q(className: "mxh-row__eye"));
                Assert.That(visibility.IsHidden(root), Is.True, "a click hides the object");
                Assert.That(visibility.IsHidden(child), Is.True, "and its children, as in Unity's Hierarchy");
                Press(window, RowNamed(window, "MixerHierarchyFixture").Q(className: "mxh-row__eye"));
                Assert.That(visibility.IsHidden(root) || visibility.IsHidden(child), Is.False, "a second click shows them again");
                Press(window, RowNamed(window, "MixerHierarchyFixture").Q(className: "mxh-row__eye"), EventModifiers.Alt);
                Assert.That(visibility.IsHidden(root), Is.True, "Alt+click hides the object");
                Assert.That(visibility.IsHidden(child), Is.False, "but not its children");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void DeletingOneObjectWorksAndUndoes()
        {
            var child = root.transform.GetChild(0).gameObject;
            var window = OpenHidden();
            try
            {
                Selection.activeGameObject = child;
                Assert.DoesNotThrow(() => window.DeleteSelection(), "a single delete names the object before it is gone");
                Assert.That(child == null, Is.True, "the object is deleted");
                Undo.PerformUndo();
                Assert.That(root.transform.Find("Child 0"), Is.Not.Null, "one Ctrl+Z brings it back");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void ObjectsWithMeshesGetTallRowsWithAPictureAndLightsABall()
        {
            var model = new HierarchyModel();
            model.SetExpanded(root.GetInstanceID(), true);
            model.Rebuild(new HierarchyFilter());
            var rootRow = model[model.IndexOf(root.GetInstanceID())];
            var lightRow = model[model.IndexOf(root.transform.GetChild(1).gameObject.GetInstanceID())];
            Assert.That(rootRow.HasMeshes, Is.True, "the fixture root has cubes below: tall, with a picture");
            Assert.That(lightRow.Light, Is.Not.Null, "the light row knows its light");
            Assert.That(rootRow.VrchatAvatar || rootRow.Bones != null, Is.False, "a plain object is no avatar and no armature");

            var window = OpenHidden();
            try
            {
                window.RevealObject(root.transform.GetChild(1).gameObject);
                Repaint(window);
                var row = RowNamed(window, "MixerHierarchyFixture");
                Assert.That(row.ClassListContains("mxh-row--tall"), Is.True);
                Assert.That(row.layout.height, Is.EqualTo(48f).Within(0.5f), "a tall row takes two lines");
                Assert.That(row.Q(className: "mxh-row__thumbnail").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "the picture shows (or waits for its render)");
                Assert.That(row.Q<Label>(className: "mxh-row__detail").text, Does.Contain("meshes below"));
                var light = RowNamed(window, "Child 1");
                Assert.That(light.Q(className: "mxh-row__sphere").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "a light row turns its light with a ball");
                Assert.That(light.Q<Label>(className: "mxh-row__detail").text, Does.Contain("light"));
                var child = RowNamed(window, "Child 0");
                Assert.That(child.layout.y, Is.GreaterThanOrEqualTo(row.layout.yMax - 0.5f), "rows below a tall one start under it");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void TheActiveBoxSwitchesTheObjectInOneUndoStep()
        {
            var child = root.transform.GetChild(0).gameObject;
            var window = OpenHidden();
            try
            {
                window.RevealObject(child);
                Repaint(window);
                var box = RowNamed(window, "Child 0").Q(className: "mxh-row__active");
                Assert.That(box.ClassListContains("mxh-row__active--on"), Is.True, "an active object shows a ticked box");
                Press(window, box);
                Assert.That(child.activeSelf, Is.False, "a press deactivates it");
                Assert.That(box.ClassListContains("mxh-row__active--on"), Is.False, "the box follows at once");
                Assert.That(Selection.activeGameObject, Is.Not.SameAs(child), "the press does not select the object");
                Undo.PerformUndo();
                Assert.That(child.activeSelf, Is.True, "Ctrl+Z activates it again");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator AVrchatAvatarShowsTheLogoAndItsArmatureSelectsTheMainBones()
        {
            System.Type descriptor = null;
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                if ((descriptor = assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false)) != null) break;
            if (descriptor == null) { Assert.Ignore("The VRChat avatars SDK is not in this project."); yield break; }
            // Built in the fixture's scene (new objects go to the active scene), never in the user's.
            SceneManager.SetActiveScene(scene);
            var avatar = new GameObject("Fixture avatar");
            avatar.transform.SetParent(root.transform, false);
            var armature = new GameObject("Armature").transform; armature.SetParent(avatar.transform, false);
            Transform Make(string name, Transform parent) { var t = new GameObject(name).transform; t.SetParent(parent, false); return t; }
            var hips = Make("Hips", armature);
            var spine = Make("Spine", hips); var chest = Make("Chest", spine); var neck = Make("Neck", chest); var head = Make("Head", neck);
            var leftFoot = Make("LeftFoot", Make("LeftLowerLeg", Make("LeftUpperLeg", hips)));
            var leftHand = Make("LeftHand", Make("LeftLowerArm", Make("LeftUpperArm", Make("LeftShoulder", chest))));
            avatar.AddComponent(descriptor);
            if (activeBefore.IsValid() && activeBefore.isLoaded) SceneManager.SetActiveScene(activeBefore);
            var model = new HierarchyModel();
            model.SetExpanded(root.GetInstanceID(), true);
            model.SetExpanded(avatar.GetInstanceID(), true);
            model.Rebuild(new HierarchyFilter());
            var avatarRow = model[model.IndexOf(avatar.GetInstanceID())];
            Assert.That(avatarRow.VrchatAvatar, Is.True, "the descriptor marks a VRChat avatar");
            var armatureRow = model[model.IndexOf(armature.gameObject.GetInstanceID())];
            Assert.That(armatureRow.Bones, Is.Not.Null, "the root's child above the hips is the armature");
            Assert.That(armatureRow.Bones.Head, Is.SameAs(head));
            Assert.That(armatureRow.Bones.Chest, Is.SameAs(chest));

            var window = OpenHidden();
            try
            {
                window.RevealObject(armature.gameObject);
                Repaint(window);
                Assert.That(RowNamed(window, "Fixture avatar").Q(className: "mxh-row__vrchat"), Is.Not.Null, "the avatar's row shows VRChat's logo");
                var row = RowNamed(window, "Armature");
                Assert.That(row.ClassListContains("mxh-row--tall"), Is.True);
                var headButton = row.Query<Button>(className: "mxh-bone").ToList().First(b => b.text == "Head");
                Press(window, headButton);
                Assert.That(Selection.activeGameObject, Is.SameAs(head.gameObject), "Head selects the head bone");
            }
            finally
            {
                window.Close();
            }
            // An open VRChat SDK panel keeps the descriptor in its avatar list and throws (MissingReferenceException in
            // its AvatarSelector) when it next lists avatars: the avatar goes now, and the SDK's refresh happens while this
            // test still runs, its error expected here rather than failing the next test.
            Selection.activeObject = null;
            Object.DestroyImmediate(avatar);
            LogAssert.ignoreFailingMessages = true;
            var poke = new GameObject("Fixture refresh");
            SceneManager.MoveGameObjectToScene(poke, scene);
            for (int i = 0; i < 4; i++) yield return null;
            Object.DestroyImmediate(poke);
            for (int i = 0; i < 4; i++) yield return null;
        }

        [Test]
        public void ClosingADirtySceneAsksToSaveIt()
        {
            EditorSceneManager.MarkSceneDirty(scene);
            var before = MixerHierarchyWindow.AskToSaveScene;
            int asked = 0;
            try
            {
                Assert.That(MixerHierarchyWindow.CloseScene(scene, remove: false), Is.False, "an untitled scene cannot be loaded back: Unload is not offered");
                Assert.That(asked, Is.EqualTo(0));
                MixerHierarchyWindow.AskToSaveScene = s => { asked++; return false; };
                Assert.That(MixerHierarchyWindow.CloseScene(scene, remove: true), Is.False, "Cancel keeps the scene");
                Assert.That(asked, Is.EqualTo(1), "the user was asked");
                Assert.That(scene.IsValid() && scene.isLoaded, Is.True, "the scene and its changes are still there");
                MixerHierarchyWindow.AskToSaveScene = s => { asked++; return true; };
                Assert.That(MixerHierarchyWindow.CloseScene(scene, remove: true), Is.True, "Save or Don't Save closes it");
                Assert.That(asked, Is.EqualTo(2));
                Assert.That(scene.IsValid(), Is.False);
            }
            finally
            {
                MixerHierarchyWindow.AskToSaveScene = before;
            }
        }
    }
}
