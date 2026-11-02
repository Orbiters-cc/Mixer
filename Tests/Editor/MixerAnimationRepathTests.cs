using System;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// Mixer's renames, moves and joins offer to keep the avatar's animations on their objects: the question names what the
    /// animations point at, Update rewrites them, a join moves the sources' shapes, switches and toggles to its result and
    /// warns first when the sources are toggled by different animations.
    /// </summary>
    public sealed class MixerAnimationRepathTests
    {
        private Scene scene;
        private GameObject avatar;
        private Transform jacket, pants;
        private AnimationClip clip;
        private string folder;
        private UnityEngine.Object[] selectionBefore;

        [SetUp]
        public void SetUp()
        {
            selectionBefore = Selection.objects;
            scene = EditorSceneManager.NewPreviewScene();
            avatar = new GameObject("RepathFixture");
            SceneManager.MoveGameObjectToScene(avatar, scene);
            folder = "Assets/OrbitersMixerRepathTests_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/fx.controller");
            clip = new AnimationClip { name = "outfit" };
            AssetDatabase.CreateAsset(clip, folder + "/outfit.anim");
            controller.layers[0].stateMachine.AddState("Play").motion = clip;
            avatar.AddComponent<Animator>().runtimeAnimatorController = controller;
            jacket = Cube("Jacket");
            pants = Cube("Pants");
        }

        [TearDown]
        public void TearDown()
        {
            AnimationPathWatch.Accept(avatar);
            Selection.objects = selectionBefore;
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset(folder);
        }

        private Transform Cube(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(avatar.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            go.AddComponent<MeshRenderer>();
            return go.transform;
        }

        private void Toggle(string path) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));

        private string[] Paths() => AnimationUtility.GetCurveBindings(clip).Select(b => b.path).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        [Test]
        public void ARenameAsksThenUpdatesTheAnimations()
        {
            Toggle("Jacket");
            var captured = AnimationRepath.Capture(new[] { jacket.gameObject });
            jacket.name = "Coat";
            var notice = AnimationRepath.After(captured).Single();
            Assert.AreEqual("1 animation points at “Jacket”. Update it?", AnimationRepath.Question(notice));
            AnimationPathWatch.Fix(notice);
            CollectionAssert.AreEqual(new[] { "Coat" }, Paths());
        }

        [Test]
        public void AMoveUnderAnotherObjectKeepsTheBinding()
        {
            Toggle("Jacket");
            var captured = AnimationRepath.Capture(new[] { jacket.gameObject });
            jacket.SetParent(pants, false);
            var notice = AnimationRepath.After(captured).Single();
            StringAssert.StartsWith("Moving “Jacket”", notice.What);
            AnimationPathWatch.Fix(notice);
            CollectionAssert.AreEqual(new[] { "Pants/Jacket" }, Paths());
        }

        [Test]
        public void NothingBoundNothingAsked()
        {
            var captured = AnimationRepath.Capture(new[] { jacket.gameObject });
            jacket.name = "Coat";
            Assert.AreEqual(0, AnimationRepath.After(captured).Count);
            Assert.AreEqual(0, AnimationRepath.Capture(new[] { avatar }).Count, "the avatar's root binds nothing");
        }

        [Test]
        public void SourcesToggledDifferentlyAreNamedBeforeAJoin()
        {
            var renderers = new Renderer[] { jacket.GetComponent<Renderer>(), pants.GetComponent<Renderer>() };
            Assert.AreEqual(0, AnimationRepath.DifferentToggles(renderers).Count);
            Toggle("Jacket");
            AnimationPaths.Invalidate();
            CollectionAssert.AreEquivalent(new[] { "Jacket", "Pants" }, AnimationRepath.DifferentToggles(renderers));
            Toggle("Pants");
            AnimationPaths.Invalidate();
            Assert.AreEqual(0, AnimationRepath.DifferentToggles(renderers).Count, "the same clip and keys toggle both");
        }

        [Test]
        public void AJoinPointsTheSourcesAnimationsAtItsResult()
        {
            Toggle("Jacket");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Pants", typeof(MeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, 1, 1));
            var sources = new[] { jacket.GetComponent<Renderer>(), pants.GetComponent<Renderer>() };
            var result = MeshJoin.Join(sources, new MeshJoin.Options { HideSources = true });
            try
            {
                var plan = AnimationRepath.PlanJoin(sources, result.Renderer);
                Assert.IsNotNull(plan);
                Assert.AreEqual(2, plan.Fixable, plan.ToString());
                AnimationPaths.Apply(plan);
                var bindings = AnimationUtility.GetCurveBindings(clip);
                Assert.IsTrue(bindings.All(b => b.path == result.Renderer.name), string.Join(", ", bindings.Select(b => b.path)));
                Assert.IsTrue(bindings.Any(b => b.propertyName == "m_Enabled" && b.type == typeof(SkinnedMeshRenderer)));
            }
            finally
            {
                if (result.Mesh != null) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(result.Mesh));
            }
        }
    }
}
