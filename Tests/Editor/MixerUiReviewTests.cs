using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// The 2026-10-20 review of Mixer's UI: Revert asks before edits are lost (once, and only then), Edit Armature edits
    /// the rest pose from wherever it is entered, the Scene view's Commit and Revert never fall back to a mesh nobody
    /// opened, the selection moves the focus, and Join keeps Unity's order with the active object first.
    /// </summary>
    public sealed class MixerUiReviewTests
    {
        private const string Folder = "Assets/Orbiters/MixerUiReviewTests";
        private Scene scene;
        private GameObject root;
        private Transform lower, upper;
        private HashSet<EditTarget> targetsBefore;
        private MixerMode modeBefore;
        private EditTarget focusBefore;
        private bool restPoseBefore, symmetryBefore;
        private Object[] selectionBefore;
        private System.Func<string, string, string, bool> askBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            modeBefore = MixerSceneTool.Mode;
            focusBefore = MixerSceneTool.FocusTarget;
            restPoseBefore = ArmatureEdit.Active;
            symmetryBefore = MixerSceneTool.Symmetry;
            selectionBefore = Selection.objects;
            askBefore = MixerConfirm.Ask;
            MixerSceneTool.Symmetry = false;
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("MixerUiReviewFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            lower = new GameObject("Leg_L").transform; lower.SetParent(root.transform, false);
            upper = new GameObject("Leg_R").transform; upper.SetParent(root.transform, false); upper.localPosition = new Vector3(0, 1f, 0);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder(Folder);
        }

        [TearDown]
        public void TearDown()
        {
            MixerConfirm.Ask = askBefore;
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            MixerSceneTool.Mode = MixerMode.None;
            ArmatureEdit.Active = restPoseBefore;
            MixerSceneTool.FocusTarget = focusBefore;
            MixerSceneTool.Mode = modeBefore;
            MixerSceneTool.Symmetry = symmetryBefore;
            Selection.objects = selectionBefore;
            if (root != null) Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset(Folder);
        }

        private SkinnedMeshRenderer Skinned(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var skinned = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = MixerCoreTestsFixtures.Cylinder(16, 8, 0.3f, 2f);
            AssetDatabase.CreateAsset(mesh, Folder + "/" + name + ".asset");
            skinned.sharedMesh = mesh;
            skinned.bones = new[] { lower, upper };
            skinned.rootBone = lower;
            return skinned;
        }

        private static void Inflate(EditTarget target) =>
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), new Vector3(0.3f, 1f, 0f), new Vector3(0.3f, 1.1f, 0f), 3, 0.3f, 1f, Vector3.forward);

        [Test]
        public void RevertAsksOnceAndOnlyWhenEditsWouldBeLost()
        {
            var body = MixerSession.Edit(Skinned("Body"));
            var shirt = MixerSession.Edit(Skinned("Shirt"));
            int asked = 0;
            bool answer = false;
            MixerConfirm.Ask = (title, message, ok) => { asked++; return answer; };

            Assert.That(MixerConfirm.Revert(new[] { body, shirt }), Is.EqualTo(2), "nothing to lose: both revert");
            Assert.That(asked, Is.EqualTo(0), "without asking");

            body = MixerSession.Edit(body.Renderer);
            shirt = MixerSession.Edit(shirt.Renderer);
            Inflate(body);
            Inflate(shirt);
            Assert.That(body.Document.Changed && shirt.Document.Changed, Is.True);
            Assert.That(MixerConfirm.Revert(new[] { body, shirt }), Is.EqualTo(0), "Cancel keeps the edits");
            Assert.That(asked, Is.EqualTo(1), "one question for both meshes");
            Assert.That(MixerSession.Targets, Has.Member(body).And.Member(shirt));
            answer = true;
            Assert.That(MixerConfirm.Revert(new[] { body, shirt }), Is.EqualTo(2));
            Assert.That(asked, Is.EqualTo(2));
            Assert.That(MixerSession.Targets, Has.No.Member(body).And.No.Member(shirt), "both originals are back");
        }

        [Test]
        public void EditArmatureEditsTheRestPoseWhereverItIsEntered()
        {
            var body = MixerSession.Edit(Skinned("Body"));
            ArmatureEdit.Active = false;
            MixerSceneTool.FocusTarget = body;
            MixerSceneTool.Mode = MixerMode.Armature;
            Assert.That(ArmatureEdit.Active, Is.True, "the mode strip, the MCP tool and the window all set the mode: rest-pose editing comes with it");
            MixerSceneTool.Mode = MixerMode.None;
            Assert.That(ArmatureEdit.Active, Is.False, "and leaves with it");
        }

        [Test]
        public void TheSceneViewCommandsNeverActOnAMeshNobodyOpened()
        {
            var body = MixerSession.Edit(Skinned("Body"));
            var shirt = MixerSession.Edit(Skinned("Shirt"));
            Selection.objects = new Object[0];
            MixerSceneTool.FocusTarget = body;
            Assert.That(MixerModes.CommandTarget(), Is.SameAs(body), "the focused mesh");
            MixerSession.Revert(body);
            Assert.That(MixerSceneTool.FocusTarget, Is.Null);
            Assert.That(MixerModes.CommandTarget(), Is.Null, "once it is gone, not the other mesh still open (a rig mate)");
            Selection.activeGameObject = shirt.Renderer.gameObject;
            Assert.That(MixerModes.CommandTarget(), Is.SameAs(shirt), "the selected edited mesh");
        }

        [Test]
        public void SelectingAnEditedMeshFocusesIt()
        {
            var body = MixerSession.Edit(Skinned("Body"));
            var shirt = MixerSession.Edit(Skinned("Shirt"));
            MixerSceneTool.FocusTarget = body;
            Selection.activeGameObject = shirt.Renderer.gameObject;
            MixerSceneTool.FollowSelection();
            Assert.That(MixerSceneTool.FocusTarget, Is.SameAs(shirt), "the selected mesh is the one the pages and Commit act on");
            Assert.That(MixerWindow.CurrentTarget(), Is.SameAs(shirt));
            Selection.activeGameObject = root;
            MixerSceneTool.FollowSelection();
            Assert.That(MixerSceneTool.FocusTarget, Is.SameAs(shirt), "selecting the avatar keeps the focused mesh");
            Selection.activeGameObject = upper.gameObject;
            MixerSceneTool.FollowSelection();
            Assert.That(MixerSceneTool.FocusTarget, Is.SameAs(shirt), "so does selecting one of its bones");
            Assert.That(MixerSceneTool.HoverTarget, Is.Null, "nothing is hovered outside the Scene view");
        }

        [Test]
        public void JoinKeepsUnitysOrderWithTheActiveObjectFirst()
        {
            var first = new GameObject("A"); first.transform.SetParent(root.transform, false);
            var deep = new GameObject("A child", typeof(MeshFilter), typeof(MeshRenderer)); deep.transform.SetParent(first.transform, false);
            var second = new GameObject("B", typeof(MeshFilter), typeof(MeshRenderer)); second.transform.SetParent(root.transform, false);
            var helper = new GameObject("__XRayGizmos_bones", typeof(MeshFilter), typeof(MeshRenderer)); helper.transform.SetParent(root.transform, false);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try
            {
                foreach (var go in new[] { deep, second, helper }) go.GetComponent<MeshFilter>().sharedMesh = mesh;
                Assert.That(MixerWindow.CompareHierarchy(deep.transform, second.transform), Is.LessThan(0), "a child of the first root comes before the second root, whatever its sibling index");
                Selection.objects = new Object[] { second, first, helper };
                Selection.activeGameObject = second;
                var candidates = MixerWindow.JoinCandidates();
                Assert.That(candidates.Select(r => r.name), Is.EqualTo(new[] { "B", "A child" }), "the active object first (its space wins), the gizmos' helper left out");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        private sealed class InspectorHost : EditorWindow { }

        [Test]
        public void TheShapeKeyPanelLinesUpItsColumnsAndItsSliderShowsTheEditedShape()
        {
            const string longName = "orbit face look in fix right with a name far too long for its column";
            var skinned = Skinned("Body");
            var deltas = new Vector3[skinned.sharedMesh.vertexCount];
            for (int v = 0; v < deltas.Length; v++) deltas[v] = new Vector3(0.01f, 0f, 0f);
            skinned.sharedMesh.AddBlendShapeFrame("A", 100f, deltas, null, null);
            skinned.sharedMesh.AddBlendShapeFrame(longName, 100f, deltas, null, null);
            var target = MixerSession.Edit(skinned);
            MixerSession.SetLayer(target, "Fresh", true);
            Inflate(target);

            var host = ScriptableObject.CreateInstance<InspectorHost>();
            var editor = UnityEditor.Editor.CreateEditor(skinned, typeof(SkinnedMeshRendererInspector));
            try
            {
                host.position = new Rect(-32000, -32000, 420, 900);
                var show = typeof(EditorWindow).GetMethod("ShowPopupWithMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                show.Invoke(host, new object[] { System.Enum.Parse(typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode"), "PopupMenu"), false });
                host.rootVisualElement.Add(editor.CreateInspectorGUI());
                var refresh = typeof(SkinnedMeshRendererInspector).GetMethod("Refresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                void Lay() { refresh.Invoke(editor, null); typeof(EditorWindow).GetMethod("RepaintImmediately", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(host, null); }
                Lay();
                VisualElement Row(string name) => host.rootVisualElement.Query(className: "mx-shape").ToList().First(r => r.name == name);
                Slider SliderOf(string name) => Row(name).Q<Slider>(className: "mx-shape__slider");

                var rows = host.rootVisualElement.Query(className: "mx-shape").ToList();
                Assert.That(rows.Select(r => r.name), Is.SupersetOf(new[] { "A", longName, "Fresh" }));
                foreach (var row in rows)
                {
                    var slider = row.Q<Slider>(className: "mx-shape__slider");
                    Assert.That(slider.worldBound.x, Is.EqualTo(SliderOf("A").worldBound.x).Within(0.01f), "every slider starts at the same place, whatever the name");
                    Assert.That(row.Q<Label>(className: "mx-shape__value").worldBound.x, Is.EqualTo(Row("A").Q<Label>(className: "mx-shape__value").worldBound.x).Within(0.01f), "and so does every value");
                    Assert.That(row.Q<Label>(className: "mx-shape__name").worldBound.xMax, Is.LessThanOrEqualTo(slider.worldBound.x), "a long name is cut short, not pushing the columns");
                    var track = slider.Q(className: "unity-base-slider__tracker").worldBound;
                    var knob = slider.Q(className: "unity-base-slider__dragger").worldBound;
                    Assert.That(knob.center.y, Is.EqualTo(track.center.y).Within(0.01f), "the knob sits on its track");
                }

                var fresh = SliderOf("Fresh");
                Assert.That(fresh.enabledSelf, Is.True, "the edited shape's slider can be moved");
                Assert.That(fresh.value, Is.EqualTo(100f), "shown whole while it is edited");
                fresh.value = 40f;
                Assert.That(target.Document.LayerWeight, Is.EqualTo(0.4f).Within(1e-4f), "the slider shows the new shape at its weight");
                Assert.That(Row("Fresh").Q<Label>(className: "mx-shape__value").text, Is.EqualTo("40"));

                MixerSession.SetLayer(target, "Other", true);
                Lay();
                SliderOf("Fresh").value = 70f;
                Assert.That(target.Document.Layer, Is.EqualTo("Fresh"), "a shape only Mixer has becomes the edited one when its slider moves");
                Assert.That(target.Document.LayerWeight, Is.EqualTo(0.7f).Within(1e-4f));

                SliderOf("A").value = 25f;
                Assert.That(skinned.GetBlendShapeWeight(skinned.sharedMesh.GetBlendShapeIndex("A")), Is.EqualTo(25f), "the mesh's own shapes still set the renderer's weight");
            }
            finally
            {
                host.Close();
                Object.DestroyImmediate(editor);
            }
        }
    }
}
