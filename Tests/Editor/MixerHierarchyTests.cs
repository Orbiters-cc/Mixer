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

        [Test]
        public void CutThenPasteMovesTheObjectsAndKeepsWhatPointsAtThem()
        {
            var moving = root.transform.Find("Child 0").gameObject;
            var cube = moving.transform.Find("Cube 0");
            var destination = root.transform.Find("Child 2").gameObject;
            // Something pointing at the cut object and inside it, as a skin's bones or a PhysBone would.
            var holder = new GameObject("Holder");
            SceneManager.MoveGameObjectToScene(holder, scene);
            var skin = holder.AddComponent<SkinnedMeshRenderer>();
            skin.rootBone = cube;
            var window = OpenHidden();
            string cutBefore = SessionState.GetString("Orbiters.Mixer.Hierarchy.Cut", "");
            try
            {
                Selection.activeGameObject = moving;
                window.Cut();
                Assert.That(moving != null && moving.transform.parent == root.transform, Is.True, "Cut leaves the object where it is");
                Selection.activeGameObject = destination;
                Undo.IncrementCurrentGroup();
                window.Paste(destination);
                Assert.That(moving.transform.parent, Is.SameAs(destination.transform), "Paste as Child moves it under the clicked object");
                Assert.That(skin.rootBone, Is.SameAs(cube), "what pointed at it still does: the same objects, not copies");
                Assert.That(root.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Child 0"), Is.EqualTo(1), "no copy was made");
                Undo.PerformUndo();
                Assert.That(moving.transform.parent, Is.SameAs(root.transform), "one Undo puts it back");
            }
            finally
            {
                SessionState.SetString("Orbiters.Mixer.Hierarchy.Cut", cutBefore);
                window.Close();
            }
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

            var baseUpdate = new UpdateFor();
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

                // A newer version of its custom base (MCB): the avatar's row says it is the base's, not its items' (each
                // item has its own line).
                Orbiters.Toolkit.Editor.AvatarObjectTools.RegisterUpdates(baseUpdate);
                baseUpdate.Item = avatar;
                window.RevealObject(avatar);
                Repaint(window);
                ScrollToTop(window, root);
                Repaint(window);
                var pill = RowNamed(window, "Fixture avatar").Q<Button>(className: "mxh-avatar__update");
                Assert.That(pill.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "Update beside the custom base's name");
                Assert.That(pill.Q<Label>().text, Is.EqualTo("Update base"));
                StringAssert.Contains("the custom base Fixture avatar from 1.0.1 to 1.0.2", pill.tooltip);
            }
            finally
            {
                Orbiters.Toolkit.Editor.AvatarObjectTools.UnregisterUpdates(baseUpdate);
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

        // ---- Unity's hooks into its Hierarchy (UnityWindowHooks) ---------------------------------------------------------

        private const System.Reflection.BindingFlags AnyStatic = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        private static readonly System.Type HierarchyWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneHierarchyWindow");

        [Test]
        public void UnitysPingOpensTheWayToTheObjectAndFlashesItsRow()
        {
            UnityWindowHooks.Keep();
            Assume.That(UnityWindowHooks.ListensToPings, Is.True, "this Unity version lets Mixer hear its pings");
            var window = OpenHidden();
            var onlyBefore = UnityWindowHooks.OnlyTo;
            // The user's own Mixer windows are left as they are.
            UnityWindowHooks.OnlyTo = new EditorWindow[] { window };
            try
            {
                var cube = root.transform.Find("Child 2/Cube 2").gameObject;
                // A component's ping, as an Inspector field's click sends: its object is shown.
                EditorGUIUtility.PingObject(cube.GetComponent<MeshRenderer>());
                UnityWindowHooks.Poll();
                Assert.That(window.PingedId, Is.EqualTo(cube.GetInstanceID()), "the pinged object's row flashes");
                ScrollToTop(window, root);
                Repaint(window);
                var row = RowNamed(window, "Cube 2");
                Assert.That(row, Is.Not.Null, "the folds above it opened");
                Assert.That(row.ClassListContains("mxh-row--ping"), Is.True);

                // The listener is no window of Unity's: GetWindow (Window › General › Hierarchy) still opens a real one.
                Assert.That(Resources.FindObjectsOfTypeAll(HierarchyWindowType).Cast<EditorWindow>().Any(w => (w.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave),
                    Is.False, "no hidden Hierarchy for GetWindow to find");
                // Unity's last clicked Hierarchy, which its visibility shortcuts read without a check, answers.
                var last = HierarchyWindowType.GetProperty("lastInteractedHierarchyWindow", AnyStatic).GetValue(null);
                Assert.That(last, Is.Not.Null);
                var scenes = new System.Collections.Generic.List<Scene>();
                Assert.DoesNotThrow(() => HierarchyWindowType.GetMethod("GetSelectedScenes").Invoke(last, new object[] { scenes }));
            }
            finally
            {
                UnityWindowHooks.OnlyTo = onlyBefore;
                window.Close();
            }
        }

        [Test]
        public void FindReferencesInSceneShowsInTheSearchAndLeavingItClearsTheSceneViews()
        {
            var views = SceneView.sceneViews.Cast<SceneView>().Where(v => v != null).ToList();
            Assume.That(views.Count > 0 && UnityWindowHooks.SceneViewSearch(views[0]) != null, Is.True, "a Scene view takes Unity's search");
            var searchesBefore = views.ToDictionary(v => v, v => UnityWindowHooks.SceneViewSearch(v));
            var target = root.transform.Find("Child 0");
            var holder = new GameObject("Holder");
            SceneManager.MoveGameObjectToScene(holder, scene);
            holder.AddComponent<SkinnedMeshRenderer>().rootBone = target;
            var window = OpenHidden();
            var onlyBefore = UnityWindowHooks.OnlyTo;
            UnityWindowHooks.OnlyTo = new EditorWindow[] { window };
            try
            {
                // What the Scene views held before is not news.
                UnityWindowHooks.Poll();
                typeof(SearchableEditorWindow).GetMethod("SearchForReferencesToInstanceID", AnyStatic).Invoke(null, new object[] { target.GetInstanceID() });
                UnityWindowHooks.Poll();
                Assert.That(window.Search, Does.StartWith("ref:"), "the Mixer Hierarchy shows Unity's search");
                var model = (HierarchyModel)typeof(MixerHierarchyWindow).GetField("model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
                var matches = model.Rows.Where(r => r.Object != null && r.Match).Select(r => r.Object).ToList();
                Assert.That(matches, Has.Member(holder), "what refers to it matches");
                Assert.That(matches, Has.No.Member(target.gameObject));

                // The user clears the field: the Scene views Unity greyed show everything again.
                window.rootVisualElement.Q<TextField>(className: "mxh-search").value = string.Empty;
                Assert.That(views.All(v => string.IsNullOrEmpty(UnityWindowHooks.SceneViewSearch(v))), Is.True);
            }
            finally
            {
                UnityWindowHooks.OnlyTo = onlyBefore;
                window.Close();
                var set = typeof(SearchableEditorWindow).GetMethod("SetSearchFilter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                    new[] { typeof(string), typeof(SearchableEditorWindow.SearchMode), typeof(bool), typeof(bool) }, null);
                foreach (var pair in searchesBefore)
                    if (pair.Key != null && UnityWindowHooks.SceneViewSearch(pair.Key) != pair.Value)
                        set.Invoke(pair.Key, new object[] { pair.Value ?? string.Empty, SearchableEditorWindow.SearchMode.All, false, false });
                UnityWindowHooks.Poll();
            }
        }

        [Test]
        public void OtherToolsRowDecorationsDrawOnTheRowsAndGetTheirClicks()
        {
            var drawn = new System.Collections.Generic.Dictionary<int, Rect>();
            int clicked = 0;
            int rootId = root.GetInstanceID();
            // As Easy Quest Switch decorates: an icon at the right end of Unity's row, a click on it used.
            EditorApplication.HierarchyWindowItemCallback decorate = (id, rect) =>
            {
                drawn[id] = rect;
                var icon = new Rect(rect.xMax - 18f, rect.y, 18f, rect.height);
                if (id == rootId && Event.current.type == EventType.MouseUp && icon.Contains(Event.current.mousePosition)) { clicked++; Event.current.Use(); }
            };
            var before = EditorApplication.hierarchyWindowItemOnGUI;
            EditorApplication.hierarchyWindowItemOnGUI += decorate;
            var window = OpenHidden();
            try
            {
                window.RevealObject(root);
                ScrollToTop(window, root);
                Repaint(window);
                Assert.That(drawn.ContainsKey(rootId), Is.True, "the tool draws on the fixture's row");
                var rect = drawn[rootId];
                Assert.That(rect.height, Is.EqualTo(16f), "a row of Unity's height");
                Assert.That(rect.width, Is.GreaterThan(80f), "the name's line, up to the row's own icons");

                var row = RowNamed(window, "MixerHierarchyFixture");
                var decorations = row.Q<IMGUIContainer>(className: "mxh-row__decorations");
                Click(window, decorations.LocalToWorld(new Vector2(rect.xMax - 9f, rect.center.y)));
                Assert.That(clicked, Is.EqualTo(1), "a click on the tool's icon reaches it");

                // A click the tool does not use (on the name) still selects the row.
                Selection.activeObject = null;
                Click(window, decorations.LocalToWorld(new Vector2(rect.x + 30f, rect.center.y)));
                Assert.That(Selection.activeGameObject, Is.SameAs(root), "the row under it still selects");
            }
            finally
            {
                EditorApplication.hierarchyWindowItemOnGUI = before;
                window.Close();
            }
        }

        // A tool that has a newer version for one object (as My Avatar for a gallery item).
        private sealed class UpdateFor : Orbiters.Toolkit.Editor.IAvatarObjectUpdates
        {
            public GameObject Item;
            public int Started;
            public bool Running;
            public Orbiters.Toolkit.Editor.AvatarObjectUpdate UpdateOf(GameObject go) => go != Item ? null : new Orbiters.Toolkit.Editor.AvatarObjectUpdate
            {
                Name = go.name, From = "1.0.1", To = "1.0.2", Running = Running, Start = () => { Started++; Running = true; },
            };
        }

        [Test]
        public void AnItemWithANewerVersionShowsUpdateWhichStartsItAtThePress()
        {
            var item = root.transform.GetChild(0).gameObject;
            var tool = new UpdateFor { Item = item };
            Orbiters.Toolkit.Editor.AvatarObjectTools.RegisterUpdates(tool);
            var window = OpenHidden();
            try
            {
                window.RevealObject(item);
                Repaint(window);
                ScrollToTop(window, root);
                Repaint(window);
                var row = RowNamed(window, "Child 0");
                Assert.That(row.ClassListContains("mxh-row--tall"), Is.True, "the update gives the row its second line");
                var pill = row.Q<Button>(className: "mxh-row__update");
                Assert.That(pill.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "Update shows on the item's line");
                Assert.That(pill.Q<Label>().text, Is.EqualTo("Update"));
                StringAssert.Contains("1.0.1", pill.tooltip);
                StringAssert.Contains("1.0.2", pill.tooltip);
                Assert.That(RowNamed(window, "Child 2").Q(className: "mxh-row__update").resolvedStyle.display, Is.EqualTo(DisplayStyle.None), "nothing newer: no pill");
                Click(window, pill.worldBound.center);
                Assert.That(tool.Started, Is.EqualTo(1), "the press starts the tool's update");
                Assert.That(pill.Q<Label>().text, Is.EqualTo("Updating…"), "the pill says so at once");
                Assert.That(pill.enabledSelf, Is.False, "and waits for the tool");
                Click(window, pill.worldBound.center);
                Assert.That(tool.Started, Is.EqualTo(1), "a second press while it runs starts nothing");
            }
            finally
            {
                Orbiters.Toolkit.Editor.AvatarObjectTools.UnregisterUpdates(tool);
                window.Close();
            }
        }

        // Updated elsewhere since the rows were made (its own Inspector, or the item's line in My Avatar): a press on the
        // pill still shown starts nothing, and the pill goes.
        [Test]
        public void APressAfterTheUpdateWasDoneElsewhereStartsNothing()
        {
            var item = root.transform.GetChild(0).gameObject;
            var tool = new UpdateFor { Item = item };
            Orbiters.Toolkit.Editor.AvatarObjectTools.RegisterUpdates(tool);
            var window = OpenHidden();
            try
            {
                window.RevealObject(item);
                Repaint(window);
                ScrollToTop(window, root);
                Repaint(window);
                var pill = RowNamed(window, "Child 0").Q<Button>(className: "mxh-row__update");
                Assert.That(pill.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                tool.Item = null;
                Click(window, pill.worldBound.center);
                Assert.That(tool.Started, Is.Zero, "nothing newer waits: no update runs");
                Assert.That(pill.style.display.value, Is.EqualTo(DisplayStyle.None), "the pill goes at once");
            }
            finally
            {
                Orbiters.Toolkit.Editor.AvatarObjectTools.UnregisterUpdates(tool);
                window.Close();
            }
        }

        /// <summary>
        /// The net under the incremental model: random edits of the fixture (with Undo, and a few made by script without
        /// it), one per editor frame, each reaching the model only through the editor's change events; after each one the
        /// kept rows must equal rows built from scratch, also while searching and after Undo and Redo, prefab revert and
        /// apply included. The seed is fixed so a failure repeats.
        /// </summary>
        [UnityTest]
        public System.Collections.IEnumerator TheKeptRowsEqualRowsBuiltFromScratchAfterRandomEdits()
        {
            const string prefabPath = "Assets/__MixerHierarchyRandomEdits.prefab";
            const string secondPath = "Assets/__MixerHierarchyRandomEdits.unity";
            undoable = redoable = 0;
            var model = new HierarchyModel();
            var filter = new HierarchyFilter();
            System.Action<Toolkit.Editor.EditorChanges> listen = changes => model.Apply(changes);
            Toolkit.Editor.EditorChangeBus.Changed += listen;
            GameObject instance = null;
            second = default;
            try
            {
                // A prefab instance in the fixture, for apply, revert and unpack; a second scene, saved, for objects moved
                // between scenes and for unloading and loading.
                var source = new GameObject("Prefab source");
                new GameObject("Prefab child").transform.SetParent(source.transform, false);
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
                Object.DestroyImmediate(source);
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                instance.transform.SetParent(root.transform, false);
                second = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                EditorSceneManager.SaveScene(second, secondPath);
                model.SetExpanded(root.GetInstanceID(), true, recursive: true);
                model.Rebuild(filter);
                var random = new System.Random(20261110);
                int made = 0;
                for (int step = 0; step < 120; step++)
                {
                    string what = RandomEdit(random, instance, ref made);
                    // Unity publishes its change events with the next editor frame.
                    yield return null;
                    Toolkit.Editor.EditorChangeBus.Flush();
                    // Branches fold and open again at random: rows kept under a folded branch must be right when it opens.
                    var objects = root.GetComponentsInChildren<Transform>(true).Concat(second.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true))).ToList();
                    for (int fold = random.Next(4); fold > 0 && objects.Count > 0; fold--)
                    {
                        int id = objects[random.Next(objects.Count)].gameObject.GetInstanceID();
                        if (!model.Expanded.Remove(id)) model.Expanded.Add(id);
                    }
                    model.Expanded.Add(root.GetInstanceID());
                    filter.Pattern = step % 5 == 4 ? new Toolkit.Editor.SearchPattern(step % 10 == 4 ? "Made" : "Made 1", false, false) : Toolkit.Editor.SearchPattern.Empty;
                    model.Refresh(filter);
                    var differences = model.Verify(filter);
                    Assert.That(differences, Is.Empty, "after step " + step + " (" + what + "): " + string.Join("; ", differences));
                }
                // Unloading and loading the second scene (the window forgets every object on scene events: Reset).
                filter.Pattern = Toolkit.Editor.SearchPattern.Empty;
                EditorSceneManager.SaveScene(second);
                EditorSceneManager.CloseScene(second, false);
                yield return null;
                Toolkit.Editor.EditorChangeBus.Flush();
                model.Reset();
                model.Refresh(filter);
                Assert.That(model.Verify(filter), Is.Empty, "after unloading a scene");
                second = EditorSceneManager.OpenScene(secondPath, OpenSceneMode.Additive);
                yield return null;
                Toolkit.Editor.EditorChangeBus.Flush();
                model.Reset();
                model.Refresh(filter);
                Assert.That(model.Verify(filter), Is.Empty, "after loading it again");
            }
            finally
            {
                Toolkit.Editor.EditorChangeBus.Changed -= listen;
                if (second.IsValid()) EditorSceneManager.CloseScene(second, true);
                AssetDatabase.DeleteAsset(secondPath);
                AssetDatabase.DeleteAsset(prefabPath);
            }
        }

        // Review of MXC-29: a branch opened (its rows kept), folded, a row below it renamed and an object made below it by
        // a script without Undo, then opened again: the rows show the new name and the new object.
        [UnityTest]
        public System.Collections.IEnumerator AFoldedBranchShowsScriptEditsMadeWhileFolded()
        {
            var model = new HierarchyModel();
            var filter = new HierarchyFilter();
            System.Action<Toolkit.Editor.EditorChanges> listen = changes => model.Apply(changes);
            Toolkit.Editor.EditorChangeBus.Changed += listen;
            try
            {
                var child = root.transform.GetChild(0);
                var cube = child.GetChild(0).gameObject;
                model.SetExpanded(root.GetInstanceID(), true);
                model.SetExpanded(child.gameObject.GetInstanceID(), true);
                model.Rebuild(filter);
                Assert.That(model.IndexOf(cube.GetInstanceID()), Is.GreaterThanOrEqualTo(0));
                model.SetExpanded(child.gameObject.GetInstanceID(), false);
                model.Refresh(filter);
                Assert.That(model.IndexOf(cube.GetInstanceID()), Is.LessThan(0), "folded");
                cube.name = "Renamed while folded";
                var deeper = new GameObject("Made while folded");
                deeper.transform.SetParent(cube.transform, false);
                yield return null;
                Toolkit.Editor.EditorChangeBus.Flush();
                model.Refresh(filter);
                model.SetExpanded(child.gameObject.GetInstanceID(), true);
                model.SetExpanded(cube.GetInstanceID(), true);
                model.Refresh(filter);
                Assert.That(model[model.IndexOf(cube.GetInstanceID())].Name, Is.EqualTo("Renamed while folded"));
                Assert.That(model.IndexOf(deeper.GetInstanceID()), Is.GreaterThanOrEqualTo(0), "the object made below the folded branch is a row");
                Assert.That(model.Verify(filter), Is.Empty);
            }
            finally { Toolkit.Editor.EditorChangeBus.Changed -= listen; }
        }

        // Review of the pictures: scrolling binds rows and makes pictures between rebuilds; the window's tick frees the
        // oldest past the cap, never one on screen.
        [Test]
        public void PicturesPastTheCapAreFreedButNeverOneOnScreen()
        {
            var window = OpenHidden();
            try
            {
                window.RevealObject(root);
                Repaint(window);
                ScrollToTop(window, root);
                Repaint(window);
                window.HoldPicture(root.GetInstanceID(), "mixer-hierarchy-test|on screen");
                for (int i = 0; i < MixerHierarchyWindow.PicturesKept + 100; i++) window.HoldPicture(-1000 - i, "mixer-hierarchy-test|" + i);
                window.ReleaseUnusedPictures();
                Assert.That(window.PicturesHeld, Is.LessThanOrEqualTo(MixerHierarchyWindow.PicturesKept));
                Assert.That(window.HoldsPicture(root.GetInstanceID()), Is.True, "the oldest, but on screen: kept");
                Assert.That(window.HoldsPicture(-1000), Is.False, "the oldest off screen went");
                Assert.That(window.HoldsPicture(-1000 - MixerHierarchyWindow.PicturesKept - 99), Is.True, "the newest stayed");
            }
            finally { window.Close(); }
        }

        // Review of the lasting picture keys: what the picture is rendered with beyond the assets (placement, the bones'
        // pose, the shapes' weights) changes the key; the same state gives the same key.
        [Test]
        public void ThePictureKeyFollowsPlacementPoseAndShapes()
        {
            var cube = root.transform.GetChild(0).GetChild(0);
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            ulong before = MixerHierarchyWindow.RenderedState(root.transform, renderers);
            Assert.That(MixerHierarchyWindow.RenderedState(root.transform, renderers), Is.EqualTo(before), "the same state, the same key");
            cube.localPosition += new Vector3(0.5f, 0f, 0f);
            Assert.That(MixerHierarchyWindow.RenderedState(root.transform, renderers), Is.Not.EqualTo(before), "a part moved");
            cube.localPosition -= new Vector3(0.5f, 0f, 0f);
            Assert.That(MixerHierarchyWindow.RenderedState(root.transform, renderers), Is.EqualTo(before), "back where it was");

            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.boneWeights = new[] { new BoneWeight { boneIndex0 = 0, weight0 = 1f }, new BoneWeight { boneIndex0 = 0, weight0 = 1f }, new BoneWeight { boneIndex0 = 0, weight0 = 1f } };
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.AddBlendShapeFrame("Smile", 100f, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
            var holder = new GameObject("Skinned");
            holder.transform.SetParent(root.transform, false);
            var bone = new GameObject("Bone").transform;
            bone.SetParent(holder.transform, false);
            var skinned = holder.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = mesh;
            skinned.bones = new[] { bone };
            try
            {
                renderers = root.GetComponentsInChildren<Renderer>(true);
                ulong rest = MixerHierarchyWindow.RenderedState(root.transform, renderers);
                skinned.SetBlendShapeWeight(0, 60f);
                ulong smiling = MixerHierarchyWindow.RenderedState(root.transform, renderers);
                Assert.That(smiling, Is.Not.EqualTo(rest), "a shape's weight");
                bone.localRotation = Quaternion.Euler(0f, 0f, 30f);
                Assert.That(MixerHierarchyWindow.RenderedState(root.transform, renderers), Is.Not.EqualTo(smiling), "a bone posed");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // Review of the pictures: an asset changed (a material saved and imported) moves every kept row's picture
        // revision, so its key, with the assets' dependency hashes, is worked out again.
        [UnityTest]
        public System.Collections.IEnumerator AnAssetChangeMakesThePictureKeysBeWorkedOutAgain()
        {
            const string materialPath = "Assets/__MixerHierarchyPicture.mat";
            var model = new HierarchyModel();
            var filter = new HierarchyFilter();
            System.Action<Toolkit.Editor.EditorChanges> listen = changes => model.Apply(changes);
            Toolkit.Editor.EditorChangeBus.Changed += listen;
            try
            {
                var material = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(material, materialPath);
                var cube = root.transform.GetChild(0).GetChild(0).gameObject;
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                model.SetExpanded(root.GetInstanceID(), true);
                model.Rebuild(filter);
                yield return null;
                Toolkit.Editor.EditorChangeBus.Flush();
                int before = model.Known(root.GetInstanceID()).PictureRevision;
                material.color = Color.red;
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceUpdate);
                yield return null;
                Toolkit.Editor.EditorChangeBus.Flush();
                Assert.That(model.Known(root.GetInstanceID()).PictureRevision, Is.GreaterThan(before));
            }
            finally
            {
                Toolkit.Editor.EditorChangeBus.Changed -= listen;
                AssetDatabase.DeleteAsset(materialPath);
            }
        }

        private int undoable, redoable;
        private Scene second;


        // An edit that found nothing to do recorded no Undo step.
        private string Nothing()
        {
            undoable--;
            return "nothing";
        }

        private string RandomEdit(System.Random random, GameObject instance, ref int made)
        {
            var all = root.GetComponentsInChildren<Transform>(true).Where(t => t != root.transform).ToList();
            Transform Pick() => all.Count > 0 ? all[random.Next(all.Count)] : root.transform;
            int kind = random.Next(17);
            // Undo and Redo only steps this test made: the user's own steps before it are never touched.
            if (kind == 10 && undoable == 0 || kind == 11 && redoable == 0) return "nothing";
            if (kind < 10 || kind == 16) { Undo.IncrementCurrentGroup(); undoable++; redoable = 0; }
            switch (kind)
            {
                case 0:
                {
                    var go = new GameObject("Made " + made++);
                    Undo.RegisterCreatedObjectUndo(go, "Create");
                    Undo.SetTransformParent(go.transform, Pick(), "Create");
                    return "create";
                }
                case 1:
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "Made " + made++;
                    Undo.RegisterCreatedObjectUndo(go, "Create mesh");
                    Undo.SetTransformParent(go.transform, Pick(), "Create mesh");
                    return "create a mesh";
                }
                case 2:
                {
                    var t = Pick();
                    if (PrefabUtility.IsPartOfPrefabInstance(t) || all.Count < 6) return Nothing();
                    Undo.DestroyObjectImmediate(t.gameObject);
                    return "delete";
                }
                case 3:
                {
                    var t = Pick();
                    var to = Pick();
                    if (to == t || to.IsChildOf(t) || PrefabUtility.IsPartOfPrefabInstance(t) && !PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) return Nothing();
                    Undo.SetTransformParent(t, to, "Reparent");
                    return "reparent";
                }
                case 4:
                {
                    var t = Pick();
                    if (PrefabUtility.IsPartOfPrefabInstance(t) && !PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) return Nothing();
                    Undo.SetSiblingIndex(t, random.Next(Mathf.Max(1, t.parent.childCount)), "Reorder");
                    return "reorder";
                }
                case 5:
                {
                    var go = Pick().gameObject;
                    Undo.RecordObject(go, "Rename");
                    go.name = "Made " + made++;
                    return "rename";
                }
                case 6:
                {
                    var go = Pick().gameObject;
                    Undo.RecordObject(go, "Activate");
                    go.SetActive(!go.activeSelf);
                    return "activate";
                }
                case 7:
                {
                    var go = Pick().gameObject;
                    if (go.GetComponent<Light>() != null) return Nothing();
                    Undo.AddComponent<Light>(go);
                    return "add a light";
                }
                case 8:
                {
                    var filterComponent = Pick().GetComponent<MeshFilter>();
                    if (filterComponent == null) return Nothing();
                    Undo.RecordObject(filterComponent, "No mesh");
                    filterComponent.sharedMesh = null;
                    return "take a mesh away";
                }
                case 9:
                {
                    if (instance == null || !PrefabUtility.IsOutermostPrefabInstanceRoot(instance) || instance.transform.childCount == 0) return Nothing();
                    var child = instance.transform.GetChild(0).gameObject;
                    Undo.RecordObject(child, "Override");
                    child.name = "Made " + made++;
                    if (random.Next(2) == 0) { PrefabUtility.RevertPrefabInstance(instance, InteractionMode.UserAction); return "prefab revert"; }
                    PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.UserAction);
                    return "prefab apply";
                }
                case 10: Undo.PerformUndo(); undoable--; redoable++; return "undo";
                case 11: Undo.PerformRedo(); redoable--; undoable++; return "redo";
                case 12:
                {
                    // A script's change without Undo: only hierarchyChanged tells it.
                    var go = Pick().gameObject;
                    go.name = "Made " + made++;
                    return "rename without Undo";
                }
                case 13:
                {
                    var go = new GameObject("Made " + made++);
                    go.transform.SetParent(Pick(), false);
                    return "create without Undo";
                }
                case 14:
                {
                    // Hidden from (or shown again in) the hierarchy by a tool: no object change, only hierarchyChanged.
                    var go = Pick().gameObject;
                    go.hideFlags ^= HideFlags.HideInHierarchy;
                    return "hide flags";
                }
                case 15:
                {
                    var t = Pick();
                    if (!second.IsValid() || t == root.transform || PrefabUtility.IsPartOfPrefabInstance(t) && !PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) return "nothing";
                    t.SetParent(null, true);
                    SceneManager.MoveGameObjectToScene(t.gameObject, second);
                    return "move to another scene";
                }
                default:
                {
                    if (instance == null || !PrefabUtility.IsOutermostPrefabInstanceRoot(instance)) return Nothing();
                    PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.UserAction);
                    return "prefab unpack";
                }
            }
        }

        // A press and a release at a point, through the window's own event handling: IMGUI code (the decorations) reads

        // its mouse position only from events Unity hands the window itself.
        private static void Click(EditorWindow window, Vector2 point)
        {
            // A press another test sent without its release (its window is gone) still holds the pointer and would take
            // this release: it is let go first, at the window's bottom corner, away from the rows.
            window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = new Vector2(2f, window.position.height - 2f), button = 0 });
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = point, button = 0, clickCount = 1 });
            window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = point, button = 0, clickCount = 1 });
        }
    }
}
