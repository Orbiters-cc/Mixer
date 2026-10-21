using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// The Mixer Project window and what it stands on: the folders and dates read from the disk, the recent assets, the
    /// content index, where assets and blendshapes are used; the window driven without showing it (a popup far off
    /// screen): folder clicks leaving Unity's selection, folding in place, the create buttons, search, delete and
    /// duplicate. The fixture is a folder of Assets and an untitled scene, both removed afterwards; the user's favourites,
    /// recent assets, search switches and copied assets are put back as they were.
    /// </summary>
    public sealed class MixerProjectTests
    {
        private const string Root = "Assets/Orbiters/MixerProjectTests";
        private static readonly string FavouritesFile = Path.Combine("UserSettings", "Orbiters", "Mixer", "favourites.json");
        private static readonly string RecentFile = Path.Combine("Library", "Orbiters", "Mixer", "recent.json");
        private static readonly string[] Prefs = { "Orbiters.Mixer.Search.Project.Regex", "Orbiters.Mixer.Search.Project.MatchCase" };
        private const string ClipboardKey = "Orbiters.Mixer.Project.Clipboard";
        private Scene scene, activeBefore;
        private Object[] selectionBefore;
        private Func<string, string, bool> askBefore;
        private byte[] favouritesBefore, recentBefore;
        private readonly Dictionary<string, bool?> prefsBefore = new Dictionary<string, bool?>();
        private string clipboardBefore;

        [SetUp]
        public void SetUp()
        {
            selectionBefore = Selection.objects;
            askBefore = MixerProjectWindow.AskToDelete;
            activeBefore = SceneManager.GetActiveScene();
            favouritesBefore = File.Exists(FavouritesFile) ? File.ReadAllBytes(FavouritesFile) : null;
            recentBefore = File.Exists(RecentFile) ? File.ReadAllBytes(RecentFile) : null;
            foreach (string key in Prefs) prefsBefore[key] = EditorPrefs.HasKey(key) ? EditorPrefs.GetBool(key) : (bool?)null;
            clipboardBefore = SessionState.GetString(ClipboardKey, "");
            if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder(Root + "/Sub/Deep");
            AssetDatabase.CreateAsset(new AnimationClip(), Root + "/Walk.anim");
            AssetDatabase.CreateAsset(new AnimationClip(), Root + "/Sub/Run.anim");
            AssetDatabase.CreateAsset(new AnimationClip(), Root + "/Sub/Deep/Jump.anim");
        }

        [TearDown]
        public void TearDown()
        {
            MixerProjectWindow.AskToDelete = askBefore;
            Selection.objects = selectionBefore;
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            if (activeBefore.IsValid() && activeBefore.isLoaded && SceneManager.GetActiveScene() != activeBefore) SceneManager.SetActiveScene(activeBefore);
            if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
            Restore(FavouritesFile, favouritesBefore, typeof(ProjectFavourites));
            Restore(RecentFile, recentBefore, typeof(RecentAssets));
            foreach (var pair in prefsBefore) { if (pair.Value.HasValue) EditorPrefs.SetBool(pair.Key, pair.Value.Value); else EditorPrefs.DeleteKey(pair.Key); }
            SessionState.SetString(ClipboardKey, clipboardBefore);
        }

        // The file as it was, and its class reading it again (its list is kept in memory).
        private static void Restore(string file, byte[] before, Type owner)
        {
            if (before != null) File.WriteAllBytes(file, before);
            else if (File.Exists(file)) File.Delete(file);
            owner.GetField("entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, null);
        }

        // ---- Helpers ------------------------------------------------------------------------------------------------------

        private static MixerProjectWindow OpenHidden()
        {
            var window = ScriptableObject.CreateInstance<MixerProjectWindow>();
            window.position = new Rect(-32000, -32000, 760, 600);
            var show = typeof(EditorWindow).GetMethod("ShowPopupWithMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var showMode = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");
            show.Invoke(window, new object[] { Enum.Parse(showMode, "PopupMenu"), false });
            Repaint(window);
            return window;
        }

        private static void Repaint(EditorWindow window) =>
            typeof(EditorWindow).GetMethod("RepaintImmediately", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, null);

        private static VisualElement RowNamed(MixerProjectWindow window, string name) => window.rootVisualElement.Query(className: "mxp-row").ToList()
            .Where(r => r.resolvedStyle.display != DisplayStyle.None && r.layout.y >= 0)
            .FirstOrDefault(r => r.Q<Label>(className: "mxp-row__name")?.text == name);

        private static void Press(MixerProjectWindow window, VisualElement element, EventModifiers modifiers = EventModifiers.None, int clicks = 1)
        {
            var down = new Event { type = EventType.MouseDown, mousePosition = element.worldBound.center, button = 0, clickCount = clicks, modifiers = modifiers };
            using (var evt = PointerDownEvent.GetPooled(down)) { evt.target = element; window.rootVisualElement.panel.visualTree.SendEvent(evt); }
            var up = new Event { type = EventType.MouseUp, mousePosition = element.worldBound.center, button = 0, clickCount = clicks, modifiers = modifiers };
            using (var evt = PointerUpEvent.GetPooled(up)) { evt.target = element; window.rootVisualElement.panel.visualTree.SendEvent(evt); }
        }

        private static void SetTimes(string assetPath, DateTime created, DateTime modified)
        {
            string full = Path.GetFullPath(assetPath);
            File.SetCreationTimeUtc(full, created);
            File.SetLastWriteTimeUtc(full, modified);
        }

        // ---- The model ------------------------------------------------------------------------------------------------------

        [Test]
        public void FoldersComeFirstAndDatesSortAndMarkNewAssets()
        {
            var now = DateTime.UtcNow;
            SetTimes(Root + "/Walk.anim", now.AddDays(-3), now.AddMinutes(-5));
            AssetDatabase.CreateAsset(new AnimationClip(), Root + "/Attack.anim");
            SetTimes(Root + "/Attack.anim", now.AddHours(-2), now.AddDays(-2));

            var byName = ProjectFiles.Children(Root, ProjectSort.Name, ProjectKinds.None);
            Assert.That(byName.Select(i => i.Name), Is.EqualTo(new[] { "Sub", "Attack", "Walk" }), "folders first, then by name");
            var added = ProjectFiles.Children(Root, ProjectSort.Added, ProjectKinds.None).Where(i => !i.IsFolder).Select(i => i.Name);
            Assert.That(added, Is.EqualTo(new[] { "Attack", "Walk" }), "newest added first");
            var modified = ProjectFiles.Children(Root, ProjectSort.Modified, ProjectKinds.None).Where(i => !i.IsFolder).Select(i => i.Name);
            Assert.That(modified, Is.EqualTo(new[] { "Walk", "Attack" }), "newest change first");
            Assert.That(byName.First(i => i.Name == "Attack").IsNew, Is.True, "added two hours ago: new");
            Assert.That(byName.First(i => i.Name == "Walk").IsNew, Is.False, "added three days ago: not new");
            Assert.That(ProjectFiles.Children(Root, ProjectSort.Name, ProjectKinds.Materials).Select(i => i.Name), Is.EqualTo(new[] { "Sub" }), "the kind filter keeps folders");
            Assert.That(ProjectFiles.FoldersBelow(Root, 10), Is.EqualTo(new[] { Root + "/Sub", Root + "/Sub/Deep" }));
            Assert.That(ProjectFiles.Ago(now.AddMinutes(-5)), Is.EqualTo("5 min ago"));
            Assert.That(ProjectFiles.IsWritable(Root), Is.True);
        }

        [Test]
        public void UsedAssetsComeFirstInTheRecentList()
        {
            RecentAssets.Note(Root + "/Walk.anim");
            RecentAssets.Note(Root + "/Sub/Run.anim");
            var recent = RecentAssets.List().Select(r => r.path).ToList();
            Assert.That(recent.Take(2), Is.EqualTo(new[] { Root + "/Sub/Run.anim", Root + "/Walk.anim" }));
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(Root + "/Walk.anim");
            // Unity may tell the selection change on its next update: the handler runs now.
            typeof(RecentAssets).GetMethod("OnSelectionChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
            Assert.That(RecentAssets.List()[0].path, Is.EqualTo(Root + "/Walk.anim"), "selecting an asset anywhere counts as a use");
            RecentAssets.Note(Root + "/Sub");
            Assert.That(RecentAssets.List()[0].path, Is.EqualTo(Root + "/Walk.anim"), "folders are places, not things used");
        }

        // ---- Where things are used --------------------------------------------------------------------------------------

        private AnimatorController ControllerPlaying(AnimationClip clip, params string[] states)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Root + "/Fixture.controller");
            controller.AddParameter("MixerProjectTestParam", AnimatorControllerParameterType.Float);
            foreach (string state in states) controller.layers[0].stateMachine.AddState(state).motion = clip;
            AssetDatabase.SaveAssets();
            return controller;
        }

        private GameObject SceneObject(string name)
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var go = new GameObject(name);
            if (activeBefore.IsValid() && activeBefore.isLoaded) SceneManager.SetActiveScene(activeBefore);
            return go;
        }

        [Test]
        public void AnAnimationListsTheAnimatorsPlayingItAndTheSceneObjectsUsingThem()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Root + "/Walk.anim");
            var controller = ControllerPlaying(clip, "Walking", "Strolling");
            var avatar = SceneObject("MixerProjectFixtureAvatar");
            avatar.AddComponent<Animator>().runtimeAnimatorController = controller;

            var uses = AssetUsages.Of(Root + "/Walk.anim");
            var animator = uses.FirstOrDefault(u => u.Group == AssetUsages.Animators);
            Assert.That(animator, Is.Not.Null, "the controller playing it is listed");
            Assert.That(animator.Target, Is.EqualTo(controller));
            Assert.That(animator.Detail, Does.Contain("Base Layer › Walking").And.Contain("Base Layer › Strolling"), "with the states playing it");
            var inScene = uses.FirstOrDefault(u => u.Group == AssetUsages.InScene);
            Assert.That(inScene, Is.Not.Null, "the object whose Animator plays it, through its controller");
            Assert.That(inScene.Target, Is.EqualTo(avatar));
            Assert.That(inScene.Detail, Does.Contain("through Fixture"));
            Assert.That(AssetUsages.Of(Root + "/Sub/Run.anim"), Is.Empty, "an animation nothing plays is used nowhere");
        }

        [Test]
        public void ABlendshapeListsItsAnimationsControllersAndOtherToolsUses()
        {
            var avatar = SceneObject("MixerProjectFixtureAvatar");
            var body = new GameObject("Body");
            body.transform.SetParent(avatar.transform, false);
            var renderer = body.AddComponent<SkinnedMeshRenderer>();
            var mesh = MixerCoreTestsFixtures.Cylinder(8, 4, 0.3f, 1f);
            var deltas = new Vector3[mesh.vertexCount];
            for (int i = 0; i < deltas.Length; i++) deltas[i] = Vector3.up * 0.01f;
            mesh.AddBlendShapeFrame("MixerProjectSmile", 100f, deltas, null, null);
            AssetDatabase.CreateAsset(mesh, Root + "/Body.asset");
            renderer.sharedMesh = mesh;
            // The fixture cylinder is skinned to two bones.
            var lower = new GameObject("Lower").transform;
            lower.SetParent(avatar.transform, false);
            var upper = new GameObject("Upper").transform;
            upper.SetParent(lower, false);
            upper.localPosition = new Vector3(0, 1f, 0);
            renderer.bones = new[] { lower, upper };
            renderer.rootBone = lower;
            var clip = new AnimationClip();
            clip.SetCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.MixerProjectSmile", AnimationCurve.Constant(0f, 1f, 100f));
            AssetDatabase.CreateAsset(clip, Root + "/Smile.anim");
            var other = new AnimationClip();
            other.SetCurve("Elsewhere/Body", typeof(SkinnedMeshRenderer), "blendShape.MixerProjectSmile", AnimationCurve.Constant(0f, 1f, 100f));
            AssetDatabase.CreateAsset(other, Root + "/OtherSmile.anim");
            var controller = ControllerPlaying(clip, "Smiling");
            avatar.AddComponent<Animator>().runtimeAnimatorController = controller;
            AssetDatabase.SaveAssets();
            Assert.That(AssetContentIndex.RefreshNow(), Is.True, "the content index was read in time");

            Func<SkinnedMeshRenderer, string, IEnumerable<MixerLink.BlendshapeUse>> source = (r, shape) =>
                r == renderer && shape == "MixerProjectSmile" ? new[] { new MixerLink.BlendshapeUse { Group = "Test tool", Title = "Linked", Target = avatar } } : null;
            MixerLink.BlendshapeUses += source;
            try
            {
                var uses = AssetUsages.OfBlendshape(renderer, "MixerProjectSmile", out bool complete);
                Assert.That(complete, Is.True, "the index was read");
                Assert.That(uses.Where(u => u.Group == AssetUsages.Animations).Select(u => AssetDatabase.GetAssetPath(u.Target)), Is.EqualTo(new[] { Root + "/Smile.anim" }), "the animation animating it on this renderer, not one animating another path");
                var animator = uses.Single(u => u.Group == AssetUsages.Animators);
                Assert.That(animator.Target, Is.EqualTo(controller));
                Assert.That(animator.Detail, Does.StartWith("On this avatar").And.Contain("Smiling"));
                Assert.That(uses.Any(u => u.Group == "Test tool" && u.Title == "Linked"), Is.True, "other tools' uses come through MixerLink");
            }
            finally { MixerLink.BlendshapeUses -= source; }
        }

        [Test]
        public void TheContentIndexFindsWhatAssetsHoldInside()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Root + "/Walk.anim");
            ControllerPlaying(clip, "MixerProjectWaving");
            Assert.That(AssetContentIndex.RefreshNow(), Is.True, "the content index was read in time");
            var hit = AssetContentIndex.Search(new Toolkit.Editor.SearchPattern("MixerProjectTestParam", false, false), p => p.StartsWith("Assets/", StringComparison.Ordinal)).FirstOrDefault(h => h.Path == Root + "/Fixture.controller");
            Assert.That(hit, Is.Not.Null, "a controller's parameter is found");
            Assert.That(hit.Label, Is.EqualTo("parameter"));
            var state = AssetContentIndex.Search(new Toolkit.Editor.SearchPattern("MixerProjectWaving", false, false), p => p.StartsWith("Assets/", StringComparison.Ordinal)).FirstOrDefault(h => h.Path == Root + "/Fixture.controller");
            Assert.That(state?.Label, Is.EqualTo("state"));
        }

        // ---- The window -------------------------------------------------------------------------------------------------

        [Test]
        public void AFolderClickLeavesUnitysSelectionAndAFileClickSelects()
        {
            var window = OpenHidden();
            try
            {
                var before = AssetDatabase.LoadMainAssetAtPath(Root + "/Walk.anim");
                Selection.activeObject = before;
                window.Navigate(Root);
                Repaint(window);
                var folder = RowNamed(window, "Sub");
                Assert.That(folder, Is.Not.Null, "the subfolder is listed");
                Press(window, folder);
                Assert.That(Selection.activeObject, Is.EqualTo(before), "a folder click leaves the selection (and the Inspector) alone");
                Assert.That(window.Picked, Is.EquivalentTo(new[] { Root + "/Sub" }), "the folder lights up here");
                Assert.That(folder.ClassListContains("mxp-row--picked"), Is.True, "at once");

                var parent = new GameObject("MixerProjectNotAnAsset");
                try
                {
                    Selection.activeObject = parent;
                    Repaint(window);
                    Press(window, RowNamed(window, "Walk"));
                    Assert.That(Selection.activeObject, Is.EqualTo(before), "a file click selects it");
                }
                finally { Object.DestroyImmediate(parent); }

                Press(window, RowNamed(window, "Sub"), clicks: 2);
                Assert.That(window.Folder, Is.EqualTo(Root + "/Sub"), "a double click opens the folder");
            }
            finally { window.Close(); }
        }

        [Test]
        public void TheSliderGivesAnIconGridThatSelectsAsTheListDoes()
        {
            var window = OpenHidden();
            try
            {
                var walk = AssetDatabase.LoadMainAssetAtPath(Root + "/Walk.anim");
                window.Navigate(Root);
                window.SetIconSize(64f);
                Repaint(window);
                Assert.That(window.GridMode, Is.True);
                Assert.That(window.IndexOf(Root + "/Sub/Run.anim"), Is.LessThan(0), "the grid shows the open folder's own contents");
                VisualElement Tile(string name) => window.rootVisualElement.Query(className: "mxp-tile").ToList()
                    .FirstOrDefault(t => t.resolvedStyle.display != DisplayStyle.None && t.Q<Label>(className: "mxp-tile__name").text == name);
                Assert.That(Tile("Sub"), Is.Not.Null, "the folder is a tile");
                Assert.That(Tile("Walk").Q(className: "mxp-tile__picture").resolvedStyle.width, Is.EqualTo(64f), "at the size asked");

                Selection.activeObject = walk;
                var parent = new GameObject("MixerProjectNotAnAsset");
                try
                {
                    Selection.activeObject = parent;
                    Press(window, Tile("Sub"));
                    Assert.That(Selection.activeObject, Is.EqualTo(parent), "a folder tile leaves the selection alone");
                    Assert.That(window.Picked, Is.EquivalentTo(new[] { Root + "/Sub" }));
                    Press(window, Tile("Walk"));
                    Assert.That(Selection.activeObject, Is.EqualTo(walk), "a file tile selects it");
                }
                finally { Object.DestroyImmediate(parent); }

                Press(window, Tile("Sub"), clicks: 2);
                Assert.That(window.Folder, Is.EqualTo(Root + "/Sub"), "a double click opens the folder");
                window.SetIconSize(0f);
                Repaint(window);
                Assert.That(window.GridMode, Is.False, "all the way left is the list");
                Assert.That(window.rootVisualElement.Q(className: "mxp-grid").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(RowNamed(window, "Run"), Is.Not.Null, "the list again");
            }
            finally { window.Close(); }
        }

        [Test]
        public void FoldersUnfoldInPlaceOneOrAllAtOnce()
        {
            var window = OpenHidden();
            try
            {
                window.Navigate(Root);
                Repaint(window);
                Assert.That(window.IndexOf(Root + "/Sub/Run.anim"), Is.LessThan(0), "folded at first");
                Press(window, RowNamed(window, "Sub").Q(className: "mxp-row__chevron"));
                Assert.That(window.IndexOf(Root + "/Sub/Run.anim"), Is.GreaterThan(window.IndexOf(Root + "/Sub")), "its arrow unfolds it in place");
                Assert.That(window.IndexOf(Root + "/Sub/Deep/Jump.anim"), Is.LessThan(0), "only that folder");
                Assert.That(window.Folder, Is.EqualTo(Root), "without opening it");
                window.UnfoldAll();
                Assert.That(window.IndexOf(Root + "/Sub/Deep/Jump.anim"), Is.GreaterThanOrEqualTo(0), "Unfold all reaches every folder below");
                Assert.That(window.Rows[window.IndexOf(Root + "/Sub/Deep/Jump.anim")].Depth, Is.EqualTo(2));
                window.FoldAll();
                Assert.That(window.IndexOf(Root + "/Sub/Run.anim"), Is.LessThan(0), "Fold all folds them back");
            }
            finally { window.Close(); }
        }

        [Test]
        public void TheCreateButtonsMakeAssetsInTheFolderUnderTheKeyboard()
        {
            var window = OpenHidden();
            try
            {
                var keep = AssetDatabase.LoadMainAssetAtPath(Root + "/Walk.anim");
                window.Navigate(Root);
                Repaint(window);
                Assert.That(window.CreateTarget(), Is.EqualTo(Root), "the open folder by default");

                Selection.activeObject = keep;
                string folder = window.CreateFolder();
                Assert.That(AssetDatabase.IsValidFolder(folder), Is.True);
                Assert.That(ParentOf(folder), Is.EqualTo(Root));
                Assert.That(Selection.activeObject, Is.EqualTo(keep), "a new folder is not selected in Unity either");

                window.Pick(window.IndexOf(Root + "/Sub"), false, false);
                Assert.That(window.CreateTarget(), Is.EqualTo(Root + "/Sub"), "the highlighted folder");
                string material = window.CreateMaterial();
                Assert.That(ParentOf(material), Is.EqualTo(Root + "/Sub"));
                Assert.That(AssetDatabase.LoadAssetAtPath<Material>(material), Is.Not.Null);
                Assert.That(Selection.activeObject, Is.EqualTo(AssetDatabase.LoadAssetAtPath<Material>(material)), "a new file is selected, as Unity does");
                Assert.That(window.IndexOf(material), Is.GreaterThan(window.IndexOf(Root + "/Sub")), "on screen, its folder unfolded");
                Assert.That(window.CreateTarget(), Is.EqualTo(Root + "/Sub"), "the new file's folder next");

                Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(window.CreateAnimation()), Is.Not.Null);
                Assert.That(AssetDatabase.LoadAssetAtPath<AnimatorController>(window.CreateAnimator()), Is.Not.Null);
            }
            finally { window.Close(); }
        }

        [Test]
        public void SearchFindsNamesThenWhatAssetsHold()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Root + "/Walk.anim");
            ControllerPlaying(clip, "Idle");
            Assert.That(AssetContentIndex.RefreshNow(), Is.True, "the content index was read in time");
            var window = OpenHidden();
            try
            {
                window.SetSearch("MixerProjectTestParam");
                int index = window.IndexOf(Root + "/Fixture.controller");
                Assert.That(index, Is.GreaterThanOrEqualTo(0), "the controller holding the parameter is found");
                Assert.That(window.Rows[index].MatchLabel, Is.EqualTo("parameter"));
                Assert.That(window.Rows.Take(index).Any(r => r.IsHeading && r.Name.StartsWith("Inside assets", StringComparison.Ordinal)), Is.True, "under its heading");
                window.SetSearch("Walk");
                Assert.That(window.IndexOf(Root + "/Walk.anim"), Is.GreaterThanOrEqualTo(0), "names are searched as Unity does");
                window.SetSearch(string.Empty);
            }
            finally { window.Close(); }
        }

        [Test]
        public void DeleteAsksAndDuplicateCopiesTheLitLines()
        {
            var window = OpenHidden();
            try
            {
                window.Navigate(Root);
                Repaint(window);
                window.Pick(window.IndexOf(Root + "/Walk.anim"), false, false);
                window.DuplicatePicked();
                Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(Root + "/Walk 1.anim"), Is.Not.Null, "a copy beside it");
                Assert.That(window.Picked, Is.EquivalentTo(new[] { Root + "/Walk 1.anim" }), "the copy lights up");

                string asked = null;
                MixerProjectWindow.AskToDelete = (title, message) => { asked = title; return false; };
                window.DeletePicked();
                Assert.That(asked, Does.Contain("Walk 1.anim"), "it asks first");
                Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(Root + "/Walk 1.anim"), Is.Not.Null, "Cancel keeps it");
                MixerProjectWindow.AskToDelete = (title, message) => true;
                window.DeletePicked();
                Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(Root + "/Walk 1.anim"), Is.Null, "Delete removes it");
            }
            finally { window.Close(); }
        }

        [Test]
        public void TheUsagePanelListsSeveralPlacesAndOneIsSelectedAtOnce()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Root + "/Walk.anim");
            var controller = ControllerPlaying(clip, "Walking");
            var avatar = SceneObject("MixerProjectFixtureAvatar");
            avatar.AddComponent<Animator>().runtimeAnimatorController = controller;
            var window = OpenHidden();
            try
            {
                window.Navigate(Root);
                Repaint(window);
                // The clip: its controller and the avatar playing it, two places: the side panel lists them.
                window.ShowUsages(Root + "/Walk.anim");
                window.FindUsagesNow();
                Repaint(window);
                var panel = window.rootVisualElement.Q(className: "mxp-usages");
                Assert.That(panel.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), "several places open the panel");
                var rows = panel.Query(className: "mxp-use").ToList();
                Assert.That(rows.Count, Is.EqualTo(2));
                Press(window, rows.First(r => r.Q<Label>(className: "mxp-use__title").text.EndsWith("MixerProjectFixtureAvatar", StringComparison.Ordinal)));
                Assert.That(Selection.activeObject, Is.EqualTo(avatar), "a row selects its place");
            }
            finally { window.Close(); }
        }

        // ---- What Unity's Project window also does ---------------------------------------------------------------------

        [Test]
        public void FavouritesKeepFoldersAssetsAndSearches()
        {
            var window = OpenHidden();
            try
            {
                window.Navigate(Root);
                ProjectFavourites.AddAssets(new[] { Root + "/Sub", Root + "/Walk.anim" });
                var entry = ProjectFavourites.Entries.First(e => e.AssetPath == Root + "/Sub");
                window.OpenFavourite(entry);
                Assert.That(window.Folder, Is.EqualTo(Root + "/Sub"), "a favourite folder opens");
                window.OpenFavourite(ProjectFavourites.Entries.First(e => e.AssetPath == Root + "/Walk.anim"));
                Assert.That(window.Folder, Is.EqualTo(Root), "a favourite asset opens its folder");
                Assert.That(Selection.activeObject, Is.EqualTo(AssetDatabase.LoadMainAssetAtPath(Root + "/Walk.anim")), "and is selected");

                var search = ProjectFavourites.AddSearch("^Run", true, false);
                window.Navigate(Root);
                window.OpenFavourite(search);
                Assert.That(window.IndexOf(Root + "/Sub/Run.anim"), Is.GreaterThanOrEqualTo(0), "a saved search runs, with its regular expression");
                Assert.That(window.IndexOf(Root + "/Walk.anim"), Is.LessThan(0));
                ProjectFavourites.Remove(search);
                Assert.That(ProjectFavourites.FindSearch("^Run", true, false), Is.Null);
            }
            finally
            {
                ProjectFavourites.RemoveAsset(Root + "/Sub");
                ProjectFavourites.RemoveAsset(Root + "/Walk.anim");
                window.SetSearch(string.Empty, false, false);
                window.Close();
            }
        }

        [Test]
        public void AFileUnfoldsToItsSubAssetsWhichSelectAsObjects()
        {
            var main = new Mesh { name = "Pack" };
            AssetDatabase.CreateAsset(main, Root + "/Pack.asset");
            var part = new Mesh { name = "MixerProjectPart" };
            AssetDatabase.AddObjectToAsset(part, main);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(Root + "/Pack.asset");
            var window = OpenHidden();
            try
            {
                window.Navigate(Root);
                Repaint(window);
                int index = window.IndexOf(Root + "/Pack.asset");
                Assert.That(window.Rows[index].HasSubAssets, Is.True, "Unity's view of the folder says it holds sub-assets");
                Press(window, RowNamed(window, "Pack").Q(className: "mxp-row__chevron"));
                var sub = window.Rows.FirstOrDefault(r => r.IsSubAsset && r.Name == "MixerProjectPart");
                Assert.That(sub, Is.Not.Null, "its arrow unfolds it to its sub-assets");
                Assert.That(sub.AssetPath, Is.EqualTo(Root + "/Pack.asset"));
                window.Pick(window.IndexOf(sub.Path), false, false);
                Assert.That(Selection.activeObject, Is.EqualTo(AssetDatabase.LoadAllAssetRepresentationsAtPath(Root + "/Pack.asset").First(o => o.name == "MixerProjectPart")), "a sub-asset selects as itself");
            }
            finally { window.Close(); }
        }

        [Test]
        public void SearchLooksWhereItIsToldWithRegularExpressionsToo()
        {
            var window = OpenHidden();
            try
            {
                window.Navigate(Root + "/Sub");
                window.SetScope(SearchScope.Folder);
                window.SetSearch("Run", false, false);
                Assert.That(window.IndexOf(Root + "/Sub/Run.anim"), Is.GreaterThanOrEqualTo(0), "the open folder's assets are found");
                window.SetSearch("Walk", false, false);
                Assert.That(window.IndexOf(Root + "/Walk.anim"), Is.LessThan(0), "not those outside it");
                window.SetScope(SearchScope.Assets);
                Assert.That(window.IndexOf(Root + "/Walk.anim"), Is.GreaterThanOrEqualTo(0), "Assets finds it");
                window.SetSearch(@"^(Walk|Jump)\.anim$", true, false);
                Assert.That(window.IndexOf(Root + "/Walk.anim"), Is.GreaterThanOrEqualTo(0), "a regular expression matches file names");
                Assert.That(window.IndexOf(Root + "/Sub/Deep/Jump.anim"), Is.GreaterThanOrEqualTo(0));
                Assert.That(window.IndexOf(Root + "/Sub/Run.anim"), Is.LessThan(0));
                window.SetSearch("^walk", true, true);
                Assert.That(window.IndexOf(Root + "/Walk.anim"), Is.LessThan(0), "Match case");
            }
            finally
            {
                window.SetScope(SearchScope.Assets);
                window.SetSearch(string.Empty, false, false);
                window.Close();
            }
        }

        [Test]
        public void CopyPasteTypeAheadAndTheCreateMenu()
        {
            var window = OpenHidden();
            try
            {
                window.Navigate(Root);
                Repaint(window);
                window.Pick(window.IndexOf(Root + "/Walk.anim"), false, false);
                window.CopyPicked();
                window.Pick(window.IndexOf(Root + "/Sub"), false, false);
                window.Paste();
                Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(Root + "/Sub/Walk.anim"), Is.Not.Null, "Paste copies into the highlighted folder");

                window.Navigate(Root);
                window.TypeAhead('w');
                Assert.That(window.Picked, Is.EquivalentTo(new[] { Root + "/Walk.anim" }), "typing a name's start jumps to it");

                var labels = window.CreateEntries(Root).Where(e => e.label != null).Select(e => e.label).ToList();
                Assert.That(labels, Does.Contain("Folder").And.Contain("Material").And.Contain("C# Script"), "the + menu holds Unity's create items too");
            }
            finally { window.Close(); }
        }

        [Test]
        public void NonAsciiBlendshapesAreFoundAsUnityNamesThem()
        {
            Assert.That(AssetContentIndex.Unescape("blendShape.\\u307E\\u3070\\u305F\\u304D"), Is.EqualTo("blendShape.まばたき"), "Unity's YAML escapes are read back");
            Assert.That(AssetContentIndex.Unescape("say \\\"hi\\\" \\\\ done"), Is.EqualTo("say \"hi\" \\ done"));
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.まばたき"), AnimationCurve.Constant(0f, 1f, 100f));
            AssetDatabase.CreateAsset(clip, Root + "/Blink.anim");
            AssetDatabase.SaveAssets();
            Assert.That(File.ReadAllText(Root + "/Blink.anim"), Does.Contain("\\u"), "Unity writes the name escaped (else this test proves nothing)");
            Assert.That(AssetContentIndex.RefreshNow(), Is.True, "the content index was read in time");
            Assert.That(AssetContentIndex.WithFact("attribute", "blendShape.まばたき", ".anim"), Does.Contain(Root + "/Blink.anim"), "the animation of a Japanese blendshape is found");
        }

        [Test]
        public void ARegularExpressionTooSlowForOneValueStopsAtOnce()
        {
            var pattern = new Toolkit.Editor.SearchPattern("(a+)+$", true, false);
            string slow = new string('a', 28) + "!";
            Assert.That(pattern.Matches(slow), Is.False);
            Assert.That(pattern.TooSlow, Is.True, "a timeout marks the expression too slow");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) pattern.Matches(slow);
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(50), "and nothing more is tried");
        }

        [Test]
        public void PasteBesideAFolderCopiedIntoItselfAndTheLitLinesFollowTheFolder()
        {
            var window = OpenHidden();
            try
            {
                window.Navigate(Root);
                Repaint(window);
                window.Pick(window.IndexOf(Root + "/Sub"), false, false);
                window.CopyPicked();
                window.Paste();
                Assert.That(AssetDatabase.IsValidFolder(Root + "/Sub 1"), Is.True, "a folder pasted onto itself goes beside it");
                Assert.That(AssetDatabase.IsValidFolder(Root + "/Sub/Sub"), Is.False, "never into itself");

                window.Navigate(Root);
                window.Pick(window.IndexOf(Root + "/Walk.anim"), false, false);
                window.Navigate(Root + "/Sub");
                Assert.That(window.Picked, Is.Empty, "a line of another folder is not what Delete acts on");
            }
            finally { window.Close(); }
        }

        private static string ParentOf(string path) => path.Substring(0, path.LastIndexOf('/'));

        // ---- Unity's hooks into its Project window (UnityWindowHooks, UnityCreateFlow) -------------------------------------

        private static readonly Type BrowserType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProjectBrowser");

        [Test]
        public void UnitysPingShowsTheAssetInTheMixerProject()
        {
            UnityWindowHooks.Keep();
            Assume.That(UnityWindowHooks.ListensToPings, Is.True, "this Unity version lets Mixer hear its pings");
            var window = OpenHidden();
            var onlyBefore = UnityWindowHooks.OnlyTo;
            // The user's own Mixer windows are left as they are.
            UnityWindowHooks.OnlyTo = new EditorWindow[] { window };
            try
            {
                window.Navigate(Root);
                Repaint(window);
                string path = Root + "/Sub/Deep/Jump.anim";
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(path));
                UnityWindowHooks.Poll();
                Assert.That(window.PingedPath, Is.EqualTo(path), "the asset's line flashes");
                Assert.That(window.IndexOf(path), Is.GreaterThanOrEqualTo(0), "its folders unfolded in place");
            }
            finally
            {
                UnityWindowHooks.OnlyTo = onlyBefore;
                window.Close();
            }
        }

        [Test]
        public void UnitysCreateItemsCreateInTheFolderAskedForAndAreNamedInPlace()
        {
            Assume.That(UnityCreateFlow.Available, Is.True, "this Unity version lets a stand-in take Unity's naming");
            var window = OpenHidden();
            try
            {
                window.Navigate(Root);
                Repaint(window);
                string target = Root + "/Sub";
                // Unity's Avatar Mask item hands its new asset over to be named (as Assets › Create items do).
                RunUnityCreate(window, "Assets/Create/Avatar Mask", target);
                var pending = window.PendingCreate;
                Assert.That(pending, Is.Not.Null, "Unity handed the naming over");
                Assert.That(pending.Folder, Is.EqualTo(target));
                Assert.That(window.IndexOf(MixerProjectWindow.PendingPath), Is.GreaterThanOrEqualTo(0), "its line shows in the folder, being renamed");
                Assert.That(AssetDatabase.FindAssets("t:AvatarMask", new[] { target }), Is.Empty, "nothing is made before it is named");
                // The stand-in is gone: no ProjectBrowser left behind, none as Unity's last clicked one.
                Assert.That(Resources.FindObjectsOfTypeAll(BrowserType).Cast<EditorWindow>().Any(w => (w.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave), Is.False);
                var last = BrowserType.GetField("s_LastInteractedProjectBrowser", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(null) as EditorWindow;
                Assert.That(last == null || (last.hideFlags & HideFlags.HideAndDontSave) != HideFlags.HideAndDontSave, Is.True);

                window.EndPending("Hands Only", true);
                Assert.That(AssetDatabase.LoadAssetAtPath<AvatarMask>(target + "/Hands Only.mask"), Is.Not.Null, "Enter makes it under the name typed, where it was asked for");
                Assert.That(window.PendingCreate, Is.Null);
                Assert.That(window.Picked, Is.EquivalentTo(new[] { target + "/Hands Only.mask" }), "and lights it up");

                // Unity's Folder item, then Escape: nothing is made; again with a name: the folder is made.
                RunUnityCreate(window, "Assets/Create/Folder", Root);
                Assert.That(window.PendingCreate, Is.Not.Null);
                Assert.That(window.PendingCreate.IsFolder, Is.True);
                window.EndPending(null, false);
                Assert.That(AssetDatabase.IsValidFolder(Root + "/New Folder"), Is.False, "Escape makes nothing");
                RunUnityCreate(window, "Assets/Create/Folder", Root);
                window.EndPending("Made Here", true);
                Assert.That(AssetDatabase.IsValidFolder(Root + "/Made Here"), Is.True);
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void ImportNewAssetIsTheMixerProjectsOwn()
        {
            // The context menu brings files into the folder clicked (Unity's own imports into its Project window's folder,
            // or Assets): Unity's item is left out of the Assets items listed there.
            Assert.That(Unsupported.GetSubmenus("Assets"), Does.Contain("Assets/Import New Asset..."), "the item Mixer's replaces is Unity's");
            var items = (List<string>)typeof(MixerProjectWindow).GetMethod("UnityAssetItems", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);
            Assert.That(items, Does.Not.Contain("Assets/Import New Asset..."));
        }

        private static void RunUnityCreate(MixerProjectWindow window, string menuPath, string target) =>
            typeof(MixerProjectWindow).GetMethod("RunUnityCreate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(window, new object[] { menuPath, target });
    }
}
