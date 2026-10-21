using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// The data-loss cases of the 2026-10-19 review: commits never overwrite another mesh of the same name, a picked shape
    /// is the shape from the first stroke on, unedited shapes keep their frames through a commit, edited ones keep their
    /// in-betweens and get seamless normals, side bones without a mirror leave the other side alone, rest-pose editing
    /// carries every mesh of the rig, and the autosave brings rest-pose edits back.
    /// </summary>
    public sealed class MixerReviewFixTests
    {
        private const string Folder = "Assets/Orbiters/MixerReviewTests";
        private Scene scene;
        private GameObject root;
        private Transform lower, upper;
        private bool symmetryBefore, restPoseBefore;
        private MixerMode modeBefore;
        // Sessions open before the test (another tool's, the user's): left alone.
        private System.Collections.Generic.HashSet<EditTarget> targetsBefore;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = Root("MixerReviewFixture");
            lower = new GameObject("Root").transform; lower.SetParent(root.transform, false);
            upper = new GameObject("Spine").transform; upper.SetParent(lower, false); upper.localPosition = new Vector3(0, 1f, 0);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder(Folder);
            symmetryBefore = MixerSceneTool.Symmetry;
            modeBefore = MixerSceneTool.Mode;
            restPoseBefore = ArmatureEdit.Active;
            targetsBefore = new System.Collections.Generic.HashSet<EditTarget>(MixerSession.Targets);
            MixerSceneTool.Symmetry = false;
        }

        [TearDown]
        public void TearDown()
        {
            ArmatureEdit.Active = restPoseBefore;
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            MixerSceneTool.Mode = modeBefore;
            MixerSceneTool.Symmetry = symmetryBefore;
            foreach (var go in scene.GetRootGameObjects()) Object.DestroyImmediate(go);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerReviewFixture");
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/Twin");
        }

        private GameObject Root(string name)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            return go;
        }

        private SkinnedMeshRenderer Skinned(GameObject parent, string name, Mesh mesh, Transform[] bones)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = mesh;
            r.bones = bones;
            r.rootBone = bones[0];
            return r;
        }

        private static Mesh Asset(Mesh mesh, string file)
        {
            string path = Folder + "/" + file + ".asset";
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder(path.Substring(0, path.LastIndexOf('/')));
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static void Inflate(EditTarget target, Vector3 at) =>
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), at, at + Vector3.up * 0.1f, 3, 0.3f, 1f, Vector3.forward);

        [Test]
        public void ACommitNeverOverwritesAnotherMeshOfTheSameName()
        {
            var first = Root("Twin");
            var second = Root("Twin");
            // Two FBXs' "Body" under two avatars of the same name: the same folder and file name for Mixer.
            var thin = Asset(MixerCoreTestsFixtures.Cylinder(16, 8, 0.3f, 2f), "A/Body");
            var thick = Asset(MixerCoreTestsFixtures.Cylinder(24, 12, 0.3f, 2f), "B/Body");
            var a = Skinned(first, "Body", thin, new[] { lower, upper });
            var b = Skinned(second, "Body", thick, new[] { lower, upper });

            var targetA = MixerSession.Edit(a);
            Inflate(targetA, new Vector3(0.3f, 0.5f, 0f));
            var assetA = MixerSession.Commit(targetA);
            var targetB = MixerSession.Edit(b);
            Inflate(targetB, new Vector3(0.3f, 0.5f, 0f));
            var assetB = MixerSession.Commit(targetB);

            Assert.That(AssetDatabase.GetAssetPath(assetB), Is.Not.EqualTo(AssetDatabase.GetAssetPath(assetA)), "the second avatar's Body gets a file of its own");
            Assert.That(a.sharedMesh, Is.SameAs(assetA));
            Assert.That(assetA.vertexCount, Is.EqualTo(thin.vertexCount), "the first commit is untouched by the second");
            Assert.That(assetB.vertexCount, Is.EqualTo(thick.vertexCount));

            // Editing a committed mesh again updates that same asset.
            string path = AssetDatabase.GetAssetPath(assetA);
            var again = MixerSession.Edit(a);
            Inflate(again, new Vector3(-0.3f, 1.5f, 0f));
            var recommitted = MixerSession.Commit(again);
            Assert.That(recommitted, Is.SameAs(assetA));
            Assert.That(AssetDatabase.GetAssetPath(recommitted), Is.EqualTo(path));
        }

        // Unity's step of an entry forgotten with its session (committed, reverted, ended for Play) still comes back on
        // Ctrl+Z: it changes nothing in Mixer, and a notice says so instead of the undo looking broken.
        [Test]
        public void UndoingAStepOfAForgottenSessionSaysItChangesNothing()
        {
            var mesh = Asset(MixerCoreTestsFixtures.Cylinder(16, 8, 0.3f, 2f), "Forgotten");
            var r = Skinned(root, "Body", mesh, new[] { lower, upper });
            var target = MixerSession.Edit(r);
            int before = MixerHistory.Count;
            Inflate(target, target.World[0]);
            Assert.That(MixerHistory.Count, Is.GreaterThan(before), "the stroke is a step");
            MixerHistory.Forget(target);
            var notices = new System.Collections.Generic.List<string>();
            System.Action<string> listen = notices.Add;
            MixerHistory.Notice += listen;
            try { Undo.PerformUndo(); }
            finally { MixerHistory.Notice -= listen; }
            Assert.That(notices.Any(n => n.Contains("changes nothing in Mixer")), Is.True, "the undo says it changed nothing");
        }

        [Test]
        public void APickedShapeShowsAtOnceAndItsFirstStrokeUndoesToIt()
        {
            var mesh = Asset(MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f), "Shape");
            var r = Skinned(root, "Body", mesh, new[] { lower, upper });
            var original = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(0, 0, original, null, null);
            int top = Enumerable.Range(0, mesh.vertexCount).First(v => original[v].sqrMagnitude > 0f);

            var target = MixerSession.Edit(r);
            MixerSession.SetLayer(target, "Wide", createIfMissing: false);
            Assert.That(Vector3.Distance(target.Document.LayerPosition(top), mesh.vertices[top] + original[top]), Is.LessThan(1e-5f), "the layer shows the shape before any stroke");
            Assert.That(Vector3.Distance(target.World[top], r.transform.TransformPoint(mesh.vertices[top] + original[top])), Is.LessThan(1e-4f), "brushes aim at the shape");

            Inflate(target, target.World[top]);
            Assert.That(target.Document.Shape("Wide").Edited, Is.True);
            Undo.PerformUndo();
            var deltas = target.Document.Shape("Wide").Deltas;
            float worst = 0f;
            for (int v = 0; v < deltas.Length; v++) worst = Mathf.Max(worst, Vector3.Distance(deltas[v], original[v]));
            Assert.That(worst, Is.LessThan(1e-5f), "undoing the first stroke gives the shape back whole");
        }

        [Test]
        public void ShapesKeepTheirFramesAndGetSeamlessNormalsThroughACommit()
        {
            var mesh = MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f);
            var half = new Vector3[mesh.vertexCount];
            var full = new Vector3[mesh.vertexCount];
            for (int v = 0; v < full.Length; v++) if (mesh.vertices[v].y < 0.5f) { full[v] = new Vector3(0f, -0.2f, 0f); half[v] = new Vector3(0f, -0.05f, 0f); }
            mesh.AddBlendShapeFrame("Steps", 50f, half, null, null);
            mesh.AddBlendShapeFrame("Steps", 100f, full, null, null);
            Asset(mesh, "Frames");
            var r = Skinned(root, "Body", mesh, new[] { lower, upper });

            var target = MixerSession.Edit(r);
            // Steps only looked at; Wide sculpted (which rebuilds the shape list).
            MixerSession.SetLayer(target, "Steps", createIfMissing: false);
            MixerSession.SetLayer(target, "Wide", createIfMissing: false);
            int top = Enumerable.Range(0, mesh.vertexCount).First(v => mesh.vertices[v].y > 1.99f);
            Inflate(target, target.World[top]);
            var asset = MixerSession.Commit(target);

            int steps = asset.GetBlendShapeIndex("Steps");
            Assert.That(asset.GetBlendShapeFrameCount(steps), Is.EqualTo(2), "an unedited shape keeps its in-between");
            Assert.That(asset.GetBlendShapeFrameWeight(steps, 0), Is.EqualTo(50f));
            var got = new Vector3[mesh.vertexCount];
            asset.GetBlendShapeFrameVertices(steps, 0, got, null, null);
            Assert.That(Enumerable.Range(0, got.Length).Max(v => Vector3.Distance(got[v], half[v])), Is.LessThan(1e-6f));

            // The cylinder's seam doubles its vertices: the sculpted shape turns both copies' normals alike.
            int wide = asset.GetBlendShapeIndex("Wide");
            var normals = new Vector3[mesh.vertexCount];
            asset.GetBlendShapeFrameVertices(wide, 0, got, normals, null);
            var vertices = asset.vertices;
            float worstSeam = 0f;
            int pairs = 0;
            for (int v = 0; v < vertices.Length; v++)
                for (int w = v + 1; w < vertices.Length; w++)
                    if ((vertices[v] - vertices[w]).sqrMagnitude < 1e-12f && (got[v].sqrMagnitude > 0f || got[w].sqrMagnitude > 0f))
                    {
                        pairs++;
                        worstSeam = Mathf.Max(worstSeam, Vector3.Distance(normals[v], normals[w]));
                    }
            Assert.That(pairs, Is.GreaterThan(0), "the shape moves seam vertices");
            Assert.That(worstSeam, Is.LessThan(1e-4f), "no seam in the shape's lighting");
        }

        [Test]
        public void AnEditedShapeKeepsItsInBetweensInProportion()
        {
            var mesh = MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f);
            var half = new Vector3[mesh.vertexCount];
            var full = new Vector3[mesh.vertexCount];
            for (int v = 0; v < full.Length; v++) if (mesh.vertices[v].y > 1.5f) { full[v] = new Vector3(0f, 0.2f, 0f); half[v] = new Vector3(0f, 0.05f, 0f); }
            mesh.AddBlendShapeFrame("Tall", 50f, half, null, null);
            mesh.AddBlendShapeFrame("Tall", 100f, full, null, null);
            Asset(mesh, "Tall");
            var r = Skinned(root, "Body", mesh, new[] { lower, upper });

            var target = MixerSession.Edit(r);
            MixerSession.SetLayer(target, "Tall", createIfMissing: false);
            int top = Enumerable.Range(0, mesh.vertexCount).First(v => mesh.vertices[v].y > 1.99f);
            Inflate(target, target.World[top]);
            var edited = (Vector3[])target.Document.Shape("Tall").Deltas.Clone();
            var asset = MixerSession.Commit(target);

            int tall = asset.GetBlendShapeIndex("Tall");
            Assert.That(asset.GetBlendShapeFrameCount(tall), Is.EqualTo(2));
            Assert.That(asset.GetBlendShapeFrameWeight(tall, 0), Is.EqualTo(50f));
            Assert.That(asset.GetBlendShapeFrameWeight(tall, 1), Is.EqualTo(100f));
            var mid = new Vector3[mesh.vertexCount];
            var last = new Vector3[mesh.vertexCount];
            asset.GetBlendShapeFrameVertices(tall, 0, mid, null, null);
            asset.GetBlendShapeFrameVertices(tall, 1, last, null, null);
            for (int v = 0; v < mid.Length; v++)
            {
                Assert.That(Vector3.Distance(last[v], edited[v]), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(mid[v], half[v] + (edited[v] - full[v]) * 0.5f), Is.LessThan(1e-5f), "the in-between takes half the edit");
            }
        }

        [Test]
        public void MirrorNamesCoverPrefixedAndInfixRigs()
        {
            Assert.That(WeightStroke.MirrorName("mixamorig:LeftArm"), Is.EqualTo("mixamorig:RightArm"));
            Assert.That(WeightStroke.MirrorName("J_Bip_L_UpperArm"), Is.EqualTo("J_Bip_R_UpperArm"));
            Assert.That(WeightStroke.MirrorName("J_Bip_R_Hand"), Is.EqualTo("J_Bip_L_Hand"));
            Assert.That(WeightStroke.MirrorName("Upper_Leg.L"), Is.EqualTo("Upper_Leg.R"));
            Assert.That(WeightStroke.MirrorName("Left leg"), Is.EqualTo("Right leg"));
            Assert.That(WeightStroke.MirrorName("Hips"), Is.EqualTo("Hips"));
            Assert.That(WeightStroke.MirrorName("Chest"), Is.EqualTo("Chest"));
        }

        [Test]
        public void ASideBoneWithoutAMirrorLeavesTheOtherSideAlone()
        {
            var side = new GameObject("Pouch").transform; side.SetParent(lower, false); side.localPosition = new Vector3(0.3f, 0f, 0f);
            var mesh = MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f);
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(new Vector3(-0.3f, 0f, 0f)) };
            var r = Skinned(root, "Body", Asset(mesh, "Side"), new[] { lower, side });
            MixerSceneTool.Symmetry = true;
            var target = MixerSession.Edit(r);
            Assert.That(WeightStroke.MirrorBone(target, 1), Is.EqualTo(1), "no bone mirrors the pouch");
            var at = new Vector3(0.3f, 0.1f, 0f);
            int mirror = Enumerable.Range(0, mesh.vertexCount).OrderBy(v => Vector3.Distance(mesh.vertices[v], new Vector3(-0.3f, 0.1f, 0f))).First();
            int painted = Enumerable.Range(0, mesh.vertexCount).OrderBy(v => Vector3.Distance(mesh.vertices[v], at)).First();
            MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Draw, WeightChannel.OfBone(1), at, at + Vector3.forward * 0.05f, 4, 0.2f, 1f, 1f, normalize: true);
            Assert.That(target.Document.BoneWeight(painted, 1), Is.GreaterThan(0.1f), "the stroke paints its side");
            Assert.That(target.Document.BoneWeight(mirror, 1), Is.EqualTo(0f), "the other side is not skinned to this side's bone");
        }

        [Test]
        public void RestPoseEditingCarriesEveryMeshOfTheRigAndComesBackFromTheAutosave()
        {
            var body = Skinned(root, "Body", Asset(MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f), "RigBody"), new[] { lower, upper });
            var shirt = Skinned(root, "Shirt", Asset(MixerCoreTestsFixtures.Cylinder(24, 12, 0.35f, 1.5f), "RigShirt"), new[] { lower, upper });
            var target = MixerSession.Edit(body);
            MixerSceneTool.Mode = MixerMode.Armature;
            ArmatureEdit.Active = true;
            var shirtTarget = MixerSession.TargetOf(shirt);
            Assert.That(shirtTarget, Is.Not.Null, "the shirt shares the bones, so it is carried too");
            var shirtBefore = Baked(shirt);

            Undo.RecordObject(upper, "Move bone");
            upper.localPosition += new Vector3(0.3f, 0.2f, 0f);
            ArmatureEdit.Recompute(target, joinCurrentGroup: false);
            ArmatureEdit.Recompute(shirtTarget, joinCurrentGroup: false);
            Assert.That(MaxDistance(shirtBefore, Baked(shirt)), Is.LessThan(1e-4f), "the shirt stays where it was");

            var edited = (Matrix4x4[])target.Document.BindPoses.Clone();
            MixerAutosave.Delete(target.Id);
            MixerAutosave.Write(target);
            var deadline = System.DateTime.UtcNow.AddSeconds(5);
            while (!MixerAutosave.Exists(target.Id) && System.DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(20);
            System.Threading.Thread.Sleep(100);
            var fresh = new EditTarget(body, target.Original, target.Working);
            Assert.That(MixerAutosave.Restore(fresh), Is.True);
            Assert.That(fresh.Document.BindPosesEdited, Is.True, "rest-pose edits survive a reload");
            Assert.That(fresh.Document.BindPoses[1], Is.EqualTo(edited[1]));
            MixerAutosave.Delete(target.Id);
        }

        [Test]
        public void TheEditedShapesOwnWeightIsKeptForSavesUploadsAndReloads()
        {
            var r = Skinned(root, "Body", Asset(MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f), "Held"), new[] { lower, upper });
            r.SetBlendShapeWeight(0, 60f);
            var target = MixerSession.Edit(r);
            MixerSession.SetLayer(target, "Wide", createIfMissing: false);
            Assert.That(r.GetBlendShapeWeight(0), Is.EqualTo(0f), "the edited shape shows through the working vertices, not twice");
            Assert.That(target.HeldWeights[0], Is.EqualTo(60f));

            // A scene save writes the renderer's own weight, then the shape is held again.
            target.ReleaseLayerWeight();
            Assert.That(r.GetBlendShapeWeight(0), Is.EqualTo(60f));
            target.HoldLayerWeight();
            Assert.That(r.GetBlendShapeWeight(0), Is.EqualTo(0f));
            Assert.That(target.HeldWeights[0], Is.EqualTo(60f));

            // After a script reload the renderer shows 0: the weights remembered from before bring 60 back, not 0.
            var remembered = (float[])target.HeldWeights.Clone();
            var reloaded = new EditTarget(r, target.Original, target.Working);
            reloaded.Document.Layer = "Wide";
            reloaded.HoldLayerWeight(remembered);
            Assert.That(reloaded.HeldWeights[0], Is.EqualTo(60f));
            reloaded.RestoreWeights();
            Assert.That(r.GetBlendShapeWeight(0), Is.EqualTo(60f), "reverting after a reload gives the user's weight back");
        }

        private static Vector3[] Baked(SkinnedMeshRenderer r)
        {
            var baked = new Mesh();
            r.BakeMesh(baked, true);
            var vertices = baked.vertices;
            Object.DestroyImmediate(baked);
            var matrix = r.transform.localToWorldMatrix;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            return vertices;
        }

        private static float MaxDistance(Vector3[] a, Vector3[] b)
        {
            float max = 0f;
            for (int i = 0; i < a.Length; i++) max = Mathf.Max(max, Vector3.Distance(a[i], b[i]));
            return max;
        }
    }
}
