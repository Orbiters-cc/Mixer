using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.XRay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// Weight Paint's bone sub-selection: a bone clicked in the Toolkit's armature overlay or the Mixer Hierarchy becomes
    /// the painted bone and the weight overlay's while Unity's selection stays on the mesh; it can be posed, and leaving
    /// the mode, a vertex group or another selection ends it.
    /// </summary>
    public sealed class MixerBoneSubSelectionTests
    {
        private Scene scene, openScene;
        private GameObject root;
        private SkinnedMeshRenderer renderer;
        private Transform lower, upper, toe, prop;
        private Mesh mesh;
        private Object[] selectionBefore;
        private bool toolsHiddenBefore;
        private HashSet<EditTarget> targetsBefore;
        private MixerMode modeBefore;
        private EditTarget focusBefore;

        [SetUp]
        public void SetUp()
        {
            selectionBefore = Selection.objects;
            toolsHiddenBefore = Tools.hidden;
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            modeBefore = MixerSceneTool.Mode;
            focusBefore = MixerSceneTool.FocusTarget;
        }

        // The skinned cylinder of the weight tests on Leg_L and Leg_R, a toe under Leg_L it is not skinned to, and a prop
        // (an object with a renderer) under Leg_R. In a preview scene, or for the hierarchy (which lists open scenes) in
        // an untitled scene opened beside the user's and removed afterwards.
        private void Build(bool inPreviewScene)
        {
            var active = SceneManager.GetActiveScene();
            if (!inPreviewScene)
            {
                openScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(openScene);
            }
            root = new GameObject("MixerBoneSubSelectionFixture");
            if (inPreviewScene)
            {
                scene = EditorSceneManager.NewPreviewScene();
                SceneManager.MoveGameObjectToScene(root, scene);
            }
            lower = new GameObject("SubSelection Leg_L").transform; lower.SetParent(root.transform, false);
            upper = new GameObject("SubSelection Leg_R").transform; upper.SetParent(lower, false); upper.localPosition = new Vector3(0, 1f, 0);
            toe = new GameObject("SubSelection Toe_L").transform; toe.SetParent(lower, false); toe.localPosition = new Vector3(0, -0.2f, 0.2f);
            prop = new GameObject("SubSelection Prop", typeof(MeshFilter), typeof(MeshRenderer)).transform; prop.SetParent(upper, false);
            var body = new GameObject("SubSelection Body"); body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();
            mesh = MixerCoreTestsFixtures.Cylinder(16, 12, 0.3f, 2f);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder("Assets/Orbiters/MixerSubSelectionTests");
            AssetDatabase.CreateAsset(mesh, "Assets/Orbiters/MixerSubSelectionTests/BoneSubSelectionFixture.asset");
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { lower, upper };
            renderer.rootBone = lower;
            if (!inPreviewScene && active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
        }

        private EditTarget EnterWeightPaint()
        {
            var target = MixerSession.Edit(renderer);
            MixerSceneTool.FocusTarget = target;
            MixerSceneTool.Mode = MixerMode.Weights;
            Selection.activeGameObject = renderer.gameObject;
            return target;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            MixerSceneTool.Mode = MixerMode.None;
            MixerSceneTool.FocusTarget = focusBefore;
            MixerSceneTool.Mode = modeBefore;
            Selection.objects = selectionBefore;
            Tools.hidden = toolsHiddenBefore;
            if (root != null) Object.DestroyImmediate(root);
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            if (openScene.IsValid()) EditorSceneManager.CloseScene(openScene, true);
            AssetDatabase.DeleteAsset("Assets/Orbiters/MixerSubSelectionTests");
        }

        [Test]
        public void ABoneClickedInTheOverlayIsPaintedWhileTheMeshStaysSelected()
        {
            Build(inPreviewScene: true);
            EnterWeightPaint();
            Assert.That(XRayBonePickHandlers.TryPick(upper), Is.True, "Mixer takes the overlay's click in Weight Paint");
            Assert.That(BoneSubSelection.Bone, Is.SameAs(upper), "the bone is sub-selected");
            Assert.That(MixerSceneTool.Channel.IsGroup, Is.False);
            Assert.That(MixerSceneTool.Channel.Bone, Is.EqualTo(1), "the brush paints it");
            Assert.That(Selection.activeGameObject, Is.SameAs(renderer.gameObject), "Unity's selection stays on the painted mesh");
            Assert.That(XRayWeightPaintService.ActiveBones, Has.Member(upper), "the weight overlay shows its weights");
            var picked = new HashSet<Transform>();
            XRayBonePickHandlers.CollectPicked(picked);
            Assert.That(picked, Has.Member(upper), "the overlay highlights it");
            Assert.That(Tools.hidden, Is.True, "Unity's handle of the mesh gives way to the bone's");

            Assert.That(XRayBonePickHandlers.TryPick(lower), Is.True);
            Assert.That(BoneSubSelection.Bone, Is.SameAs(lower), "another bone replaces it");
            Assert.That(MixerSceneTool.Channel.Bone, Is.EqualTo(0));

            Assert.That(XRayBonePickHandlers.TryPick(toe), Is.True, "a bone of the rig the mesh is not skinned to can be posed");
            Assert.That(BoneSubSelection.Bone, Is.SameAs(toe));
            Assert.That(MixerSceneTool.Channel.Bone, Is.EqualTo(0), "the painted bone stays");
            Assert.That(XRayBonePickHandlers.TryPick(prop), Is.False, "an object with a renderer is selected as usual, not sub-selected");
            Assert.That(BoneSubSelection.Bone, Is.SameAs(toe));

            MixerSceneTool.SetChannel(WeightChannel.OfGroup("Belt"));
            Assert.That(BoneSubSelection.Bone, Is.Null, "painting a vertex group drops the bone");
            Assert.That(Tools.hidden, Is.EqualTo(toolsHiddenBefore), "and gives Unity its handles back");

            Assert.That(XRayBonePickHandlers.TryPick(upper), Is.True);
            MixerSceneTool.Mode = MixerMode.None;
            Assert.That(BoneSubSelection.Bone, Is.Null, "leaving Weight Paint drops the bone");
            Assert.That(Tools.hidden, Is.EqualTo(toolsHiddenBefore));
            Assert.That(XRayBonePickHandlers.TryPick(upper), Is.False, "outside Weight Paint a bone click selects it as before");
        }

        [Test]
        public void ASubSelectedBoneStaysThroughAPoseAndItsUndo()
        {
            Build(inPreviewScene: true);
            var target = EnterWeightPaint();
            Assert.That(BoneSubSelection.Pick(upper), Is.True);
            var before = upper.localRotation;
            var top = Enumerable.Range(0, target.World.Length).OrderByDescending(v => target.World[v].y).First();
            var topBefore = target.World[top];
            // What the bone's rotation handle does with a turn.
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(upper, "Rotate " + upper.name);
            upper.rotation = Quaternion.AngleAxis(30f, Vector3.forward) * upper.rotation;
            Undo.IncrementCurrentGroup();
            Assert.That(Quaternion.Angle(upper.localRotation, before), Is.GreaterThan(29f));
            Assert.That(BoneSubSelection.Bone, Is.SameAs(upper), "posing keeps the sub-selection");
            // The weight overlay copied the working mesh on entry: the brush's picture still follows the pose.
            Assert.DoesNotThrow(() => target.RefreshWorld(), "the posed mesh's world picture refreshes");
            Assert.That(Vector3.Distance(target.World[top], topBefore), Is.GreaterThan(0.1f), "the brush aims at the posed mesh");
            Undo.PerformUndo();
            Assert.That(Quaternion.Angle(upper.localRotation, before), Is.LessThan(0.01f), "the pose undoes");
        }

        [Test]
        public void ABoneRowClickedInTheMixerHierarchyIsSubSelected()
        {
            Build(inPreviewScene: false);
            EnterWeightPaint();
            var window = ScriptableObject.CreateInstance<MixerHierarchyWindow>();
            try
            {
                window.position = new Rect(-32000, -32000, 420, 600);
                var show = typeof(EditorWindow).GetMethod("ShowPopupWithMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var showMode = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");
                show.Invoke(window, new object[] { System.Enum.Parse(showMode, "PopupMenu"), false });
                var repaint = typeof(EditorWindow).GetMethod("RepaintImmediately", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                repaint.Invoke(window, null);
                window.RevealObject(upper.gameObject);
                repaint.Invoke(window, null);
                VisualElement RowNamed(string name) => window.rootVisualElement.Query(className: "mxh-row").ToList()
                    .Where(r => r.resolvedStyle.display != DisplayStyle.None && r.layout.y >= 0)
                    .FirstOrDefault(r => r.Q<Label>(className: "mxh-row__name")?.text == name);
                var row = RowNamed("SubSelection Leg_R");
                Assert.That(row, Is.Not.Null, "the bone's row shows");
                var down = new Event { type = EventType.MouseDown, mousePosition = row.worldBound.center, button = 0, clickCount = 1 };
                using (var evt = PointerDownEvent.GetPooled(down)) { evt.target = row; window.rootVisualElement.panel.visualTree.SendEvent(evt); }
                Assert.That(BoneSubSelection.Bone, Is.SameAs(upper), "a click on the bone's row sub-selects it");
                Assert.That(Selection.activeGameObject, Is.SameAs(renderer.gameObject), "the mesh stays selected");
                Assert.That(MixerSceneTool.Channel.Bone, Is.EqualTo(1), "the brush paints the bone");
                Assert.That(RowNamed("SubSelection Leg_R")?.ClassListContains("mxh-row--subselected"), Is.True, "the row shows the sub-selection at once");

                var bodyRow = RowNamed("SubSelection Body");
                Assert.That(bodyRow, Is.Not.Null, "the mesh's row shows");
                down = new Event { type = EventType.MouseDown, mousePosition = bodyRow.worldBound.center, button = 0, clickCount = 1 };
                using (var evt = PointerDownEvent.GetPooled(down)) { evt.target = bodyRow; window.rootVisualElement.panel.visualTree.SendEvent(evt); }
                Assert.That(BoneSubSelection.Bone, Is.Null, "a click on the mesh's row goes back to the mesh");
                Assert.That(Selection.activeGameObject, Is.SameAs(renderer.gameObject));
            }
            finally
            {
                window.Close();
            }
        }
    }
}
