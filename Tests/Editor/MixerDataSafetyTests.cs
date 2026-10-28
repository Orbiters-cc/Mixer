using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// The data-loss cases of the 2026-10-20 review: Ctrl+Z after Commit and Revert never leaves a renderer on a destroyed
    /// mesh; nothing done just before a reload is lost; a deleted object, Play and a duplicate keep their sessions; scene
    /// saves and uploads during rest-pose editing get the original avatar; shape names can be reused and renames keep
    /// frames and weights; the history undoes the right step after another mesh was committed. And those of the second
    /// review: a reload after a reimport, a working mesh no session has, in-place commits (Undo, other users), a pose
    /// tried in Weight Paint, undo across Play, a session set aside by an undo across a reload.
    /// </summary>
    public sealed class MixerDataSafetyTests
    {
        private const string Folder = "Assets/Orbiters/MixerSafetyTests";
        private Scene scene;
        private GameObject root;
        private Transform lower, upper;
        private SkinnedMeshRenderer renderer;
        private Mesh mesh;
        private bool symmetryBefore, restPoseBefore;
        private Object[] selectionBefore;
        // Sessions open before the test (another tool's, the user's): left alone.
        private HashSet<EditTarget> targetsBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            // Adding a bone selects it: the user's selection comes back after the test.
            selectionBefore = Selection.objects;
            symmetryBefore = MixerSceneTool.Symmetry;
            restPoseBefore = ArmatureEdit.Active;
            MixerSceneTool.Symmetry = false;
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("MixerSafetyFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            lower = new GameObject("Lower").transform; lower.SetParent(root.transform, false);
            upper = new GameObject("Upper").transform; upper.SetParent(lower, false); upper.localPosition = new Vector3(0, 1f, 0);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder(Folder);
            mesh = Asset(MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f), "Body");
            renderer = Skinned(root, "Body", mesh);
        }

        [TearDown]
        public void TearDown()
        {
            ArmatureEdit.Active = restPoseBefore;
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            MixerSceneTool.Symmetry = symmetryBefore;
            foreach (var go in scene.GetRootGameObjects()) Object.DestroyImmediate(go);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.DeleteAsset("Assets/Orbiters/Mixer/Meshes/MixerSafetyFixture");
            Selection.objects = selectionBefore;
        }

        private SkinnedMeshRenderer Skinned(GameObject parent, string name, Mesh m)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = m;
            r.bones = new[] { lower, upper };
            r.rootBone = lower;
            return r;
        }

        private static Mesh Asset(Mesh m, string file)
        {
            AssetDatabase.CreateAsset(m, Folder + "/" + file + ".asset");
            return m;
        }

        private static PositionEdit Inflate(EditTarget target, Vector3 at) =>
            MixerSceneTool.StrokeBetween(target, Brushes.Named("Inflate"), at, at + Vector3.up * 0.1f, 3, 0.3f, 1f, Vector3.forward);

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

        private static bool ShowsMissingMesh(SkinnedMeshRenderer r) =>
            r.sharedMesh == null && new SerializedObject(r).FindProperty("m_Mesh").objectReferenceInstanceIDValue != 0;

        // A rest-pose edit: the upper bone moved while the skin stays, as the Scene view's handle does it.
        private EditTarget MoveUpperBoneInRestPose()
        {
            var target = MixerSession.Edit(renderer);
            ArmatureEdit.Active = true;
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(upper, "Move bone");
            upper.localPosition += new Vector3(0.3f, 0.2f, 0f);
            Assert.That(ArmatureEdit.Recompute(target, joinCurrentGroup: true), Is.GreaterThan(0));
            Undo.IncrementCurrentGroup();
            return target;
        }

        // ---- Commit, Revert, Undo ------------------------------------------------------------------------------------

        [Test]
        public void UndoAfterCommitNeverLeavesTheRendererOnADestroyedMesh()
        {
            var target = MixerSession.Edit(renderer);
            // Another tool's step kept the whole renderer while it showed the working mesh. (Undo.RecordObject keeps only
            // the properties a step changed, so a slider's step alone never brings a mesh back.)
            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(renderer, "Blend shape weight");
            renderer.SetBlendShapeWeight(0, 30f);
            Undo.IncrementCurrentGroup();
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var asset = MixerSession.Commit(target);
            Assert.That(renderer.sharedMesh, Is.SameAs(asset));

            Undo.PerformUndo();
            Assert.That(ShowsMissingMesh(renderer), Is.False, "undoing the commit never shows the destroyed working mesh");
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "undoing the commit shows the mesh from before the session");
            Undo.PerformRedo();
            Assert.That(renderer.sharedMesh, Is.SameAs(asset), "redo shows the committed mesh again");

            // Further back: the slider's step put the working mesh back; the guard shows the commit instead.
            Undo.PerformUndo();
            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.That(ShowsMissingMesh(renderer), Is.False, "an older step of the session never shows the destroyed working mesh");
            Assert.That(renderer.sharedMesh, Is.SameAs(asset), "it shows the committed edits");
        }

        [Test]
        public void UndoAfterRevertNeverLeavesTheRendererOnADestroyedMesh()
        {
            var target = MixerSession.Edit(renderer);
            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(renderer, "Blend shape weight");
            renderer.SetBlendShapeWeight(0, 30f);
            Undo.IncrementCurrentGroup();
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            MixerSession.Revert(target);
            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.That(ShowsMissingMesh(renderer), Is.False);
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "after a revert the original stays");
        }

        [Test]
        public void ACommitAfterAnotherMeshesCommitUndoesTheRightStrokes()
        {
            var second = Skinned(root, "Shirt", Asset(MixerCoreTestsFixtures.Cylinder(24, 12, 0.35f, 1.5f), "Shirt"));
            var a = MixerSession.Edit(renderer);
            var b = MixerSession.Edit(second);
            Inflate(a, new Vector3(0.3f, 0.5f, 0f));
            var bBefore = (Vector3[])b.Document.Positions.Clone();
            Inflate(b, new Vector3(0.35f, 0.5f, 0f));
            var bAfter = (Vector3[])b.Document.Positions.Clone();
            Inflate(a, new Vector3(-0.3f, 1.5f, 0f));
            MixerSession.Commit(a);

            Undo.PerformUndo(); // the commit
            Undo.PerformUndo(); // a's second stroke: a is committed, nothing of b changes
            Assert.That(MaxDistance(b.Document.Positions, bAfter), Is.LessThan(1e-6f), "b keeps its stroke while a's steps are undone");
            Undo.PerformUndo(); // b's stroke
            Assert.That(MaxDistance(b.Document.Positions, bBefore), Is.LessThan(1e-6f), "then b's own step undoes it");
        }

        [Test]
        public void AMixerMeshAnotherRendererUsesGetsAFileOfItsOwn()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var first = MixerSession.Commit(target);
            var other = Skinned(root, "Copy", first);
            var before = first.vertices;

            var again = MixerSession.Edit(renderer);
            Inflate(again, new Vector3(-0.3f, 1.5f, 0f));
            var second = MixerSession.Commit(again);
            Assert.That(AssetDatabase.GetAssetPath(second), Is.Not.EqualTo(AssetDatabase.GetAssetPath(first)), "the other renderer's mesh is not changed under it");
            Assert.That(other.sharedMesh, Is.SameAs(first));
            Assert.That(MaxDistance(first.vertices, before), Is.LessThan(1e-7f));

            // Only this renderer uses the second one: it is updated in place, with the edits.
            Object.DestroyImmediate(other.gameObject);
            var third = MixerSession.Edit(renderer);
            var content = second.vertices;
            Inflate(third, new Vector3(0.3f, 1f, 0f));
            var edited = (Vector3[])third.Document.Positions.Clone();
            Assert.That(MaxDistance(edited, content), Is.GreaterThan(1e-4f), "the stroke moved the mesh");
            var replaced = MixerSession.Commit(third);
            Assert.That(renderer.sharedMesh, Is.SameAs(replaced));
            Assert.That(replaced.vertexCount, Is.EqualTo(edited.Length));
            Assert.That(MaxDistance(replaced.vertices, edited), Is.LessThan(1e-6f), "the committed mesh holds the edits");
            Assert.That(replaced, Is.SameAs(second), "in place: what points at the asset follows");
        }

        [Test]
        public void UndoingAnInPlaceCommitGivesTheAssetWhatItHeld()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var first = MixerSession.Commit(target);
            var held = first.vertices;
            var again = MixerSession.Edit(renderer);
            Inflate(again, new Vector3(-0.3f, 1.5f, 0f));
            var edited = (Vector3[])again.Document.Positions.Clone();
            var second = MixerSession.Commit(again);
            Assert.That(second, Is.SameAs(first), "nothing else uses it: updated in place");
            Assert.That(MaxDistance(first.vertices, edited), Is.LessThan(1e-6f));

            Undo.PerformUndo();
            Assert.That(renderer.sharedMesh, Is.SameAs(first));
            Assert.That(MaxDistance(first.vertices, held), Is.LessThan(1e-6f), "Ctrl+Z gives the asset what the first commit saved");
            Undo.PerformRedo();
            Assert.That(MaxDistance(first.vertices, edited), Is.LessThan(1e-6f), "Ctrl+Y the second commit");
        }

        [Test]
        public void AMixerMeshAPrefabUsesGetsAFileOfItsOwn()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var first = MixerSession.Commit(target);
            var user = new GameObject("PrefabUser");
            SceneManager.MoveGameObjectToScene(user, scene);
            user.AddComponent<MeshFilter>().sharedMesh = first;
            PrefabUtility.SaveAsPrefabAsset(user, Folder + "/User.prefab");
            Object.DestroyImmediate(user);
            var before = first.vertices;

            var again = MixerSession.Edit(renderer);
            Inflate(again, new Vector3(-0.3f, 1.5f, 0f));
            var second = MixerSession.Commit(again);
            Assert.That(AssetDatabase.GetAssetPath(second), Is.Not.EqualTo(AssetDatabase.GetAssetPath(first)), "the prefab's mesh is not changed under it");
            Assert.That(MaxDistance(first.vertices, before), Is.LessThan(1e-7f));
        }

        [Test]
        public void AMixerMeshTheSessionOfADeletedObjectUsesGetsAFileOfItsOwn()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var first = MixerSession.Commit(target);
            var copy = Skinned(root, "Copy", first);
            var copySession = MixerSession.Edit(copy);
            Inflate(copySession, new Vector3(0.3f, 1f, 0f));
            Undo.IncrementCurrentGroup();
            Undo.DestroyObjectImmediate(copy.gameObject);
            MixerSession.Tick();
            Assert.That(MixerSession.Targets, Has.No.Member(copySession), "the deleted copy's session waits for an Undo");
            var before = first.vertices;

            var again = MixerSession.Edit(renderer);
            Inflate(again, new Vector3(-0.3f, 1.5f, 0f));
            var second = MixerSession.Commit(again);
            Assert.That(AssetDatabase.GetAssetPath(second), Is.Not.EqualTo(AssetDatabase.GetAssetPath(first)), "the waiting session's mesh is not changed under it");
            Assert.That(MaxDistance(first.vertices, before), Is.LessThan(1e-7f));

            // Back to the copy (the commit, the stroke, the delete): its session comes back, and the tear-down reverts it.
            Undo.PerformUndo();
            Undo.PerformUndo();
            Undo.PerformUndo();
            MixerSession.Tick();
        }

        [Test]
        public void PickingALayerOrUndoingEverythingIsNothingToCommit()
        {
            var target = MixerSession.Edit(renderer);
            MixerSession.SetLayer(target, "Wide", createIfMissing: false);
            MixerSession.SetLayer(target, null, false);
            Assert.That(target.Document.Changed, Is.False, "picking a layer is no edit");
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            Assert.That(target.Document.Changed, Is.True);
            Undo.PerformUndo();
            Assert.That(target.Document.HasDifferences(), Is.False, "the stroke undone, the mesh is the original again");
            Assert.That(MixerSession.Commit(target), Is.Null, "nothing is saved");
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "the renderer keeps its mesh");
            Assert.That(AssetDatabase.IsValidFolder("Assets/Orbiters/Mixer/Meshes/MixerSafetyFixture") && AssetDatabase.FindAssets("t:Mesh", new[] { "Assets/Orbiters/Mixer/Meshes/MixerSafetyFixture" }).Length > 0, Is.False);
        }

        [Test]
        public void ACommitAfterTheOriginalChangedKeepsTheMeshThatWasEdited()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var edited = (Vector3[])target.Document.Positions.Clone();
            // A reimport with the same vertex count but the vertices in another order.
            var reordered = mesh.vertices.Reverse().ToArray();
            mesh.vertices = reordered;
            var asset = MixerSession.Commit(target);
            Assert.That(asset.vertexCount, Is.EqualTo(edited.Length));
            Assert.That(MaxDistance(asset.vertices, edited), Is.LessThan(1e-6f), "the commit is the mesh as it was edited, not the stale edits written into the new import");
        }

        // ---- Reloads, deletes, Play, duplicates ---------------------------------------------------------------------

        [Test]
        public void WhatIsWrittenBeforeAReloadKeepsStrokesWeightsAndBindPoses()
        {
            var target = MoveUpperBoneInRestPose();
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var at = new Vector3(0.3f, 0.1f, 0f);
            MixerSceneTool.WeightStrokeBetween(target, WeightBrushKind.Draw, WeightChannel.OfBone(1), at, at + Vector3.forward * 0.2f, 4, 0.25f, 1f, 1f, normalize: true);
            int painted = target.Document.WeightEdited.First();
            float weight = target.Document.BoneWeight(painted, 1);
            var positions = (Vector3[])target.Document.Positions.Clone();
            var poses = (Matrix4x4[])target.Document.BindPoses.Clone();

            // What a script reload does first, whatever the 3 s autosave throttle said.
            MixerSession.FlushAutosavesNow();
            Assert.That(MixerAutosave.Exists(target.Id), Is.True, "written before the reload, not in the background");
            var fresh = new EditTarget(renderer, target.Original, target.Working);
            Assert.That(MixerAutosave.Restore(fresh), Is.True);
            Assert.That(MaxDistance(fresh.Document.Positions, positions), Is.LessThan(1e-7f), "strokes");
            Assert.That(fresh.Document.WeightEdited, Has.Member(painted), "painted weights are edits a commit writes");
            Assert.That(fresh.Document.BoneWeight(painted, 1), Is.EqualTo(weight).Within(1e-6f));
            Assert.That(fresh.Document.BindPosesEdited, Is.True);
            Assert.That(fresh.Document.BindPoses, Is.EqualTo(poses), "bind poses");
            var built = MeshBuilder.Build(fresh);
            try
            {
                Assert.That(built.bindposes, Is.EqualTo(poses), "a commit after the reload carries the moved bind poses");
                Assert.That(built.GetBonesPerVertex()[painted], Is.EqualTo(2), "and the painted weights");
            }
            finally { Object.DestroyImmediate(built); }
        }

        [Test]
        public void AReloadKeepsTheWorkingMeshAndTheEdits()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var edited = (Vector3[])target.Document.Positions.Clone();
            var working = target.Working;
            MixerSession.SimulateReload(scene);
            var back = MixerSession.TargetOf(renderer);
            Assert.That(back, Is.Not.Null.And.Not.SameAs(target), "the session is made again");
            Assert.That(back.Working, Is.SameAs(working), "on the working mesh it had");
            Assert.That(renderer.sharedMesh, Is.SameAs(working));
            Assert.That(MaxDistance(back.Document.Positions, edited), Is.LessThan(1e-7f), "with its edits");
        }

        [Test]
        public void AReloadAfterAReimportWithAsManyVerticesStartsFromTheNewMesh()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var working = target.Working;
            // A reimport with the same vertex count but the vertices in another order.
            var reordered = mesh.vertices.Reverse().ToArray();
            MixerSession.SimulateReload(scene, () => mesh.vertices = reordered);
            var back = MixerSession.TargetOf(renderer);
            Assert.That(working == null, Is.True, "the working mesh made from the old import is not reused");
            Assert.That(back, Is.Not.Null);
            Assert.That(renderer.sharedMesh, Is.SameAs(back.Working));
            Assert.That(MaxDistance(back.Working.vertices, reordered), Is.LessThan(1e-6f), "the renderer shows the new import");
            Assert.That(back.Document.HasDifferences(), Is.False, "the edits made on the old one are not applied to it");
        }

        [Test]
        public void AnAutosaveAReloadCannotUseGivesAFreshWorkingMesh()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var working = target.Working;
            string path = MixerAutosave.PathFor(target.Id);
            // An autosave of another format (another version of Mixer wrote it).
            MixerSession.SimulateReload(scene, () => System.IO.File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("MIXA").Concat(System.BitConverter.GetBytes(-1)).ToArray()));
            var back = MixerSession.TargetOf(renderer);
            Assert.That(working == null, Is.True, "the working mesh showing what the autosave held is not reused");
            Assert.That(back, Is.Not.Null);
            Assert.That(renderer.sharedMesh, Is.SameAs(back.Working));
            Assert.That(MaxDistance(back.Working.vertices, mesh.vertices), Is.LessThan(1e-6f), "the renderer shows what the document holds: the original");
        }

        [Test]
        public void AWorkingMeshNoSessionHasGetsItsOriginalBack()
        {
            // What a session whose restore failed after a reload leaves: its working mesh on its own renderer, no session.
            var orphan = Object.Instantiate(mesh);
            orphan.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                MixerMeshGuard.Shown(orphan, mesh, renderer);
                renderer.sharedMesh = orphan;
                MixerMeshGuard.RepairUnder(root);
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "the renderer gets its original, which a scene saves");
            }
            finally
            {
                MixerMeshGuard.Released(orphan, mesh);
                Object.DestroyImmediate(orphan);
            }
        }

        [Test]
        public void ASessionSetAsideByAnUndoIsTakenUpAgainAfterAReload()
        {
            // A step made before the session kept the whole renderer, with the mesh it had then.
            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(renderer, "Blend shape weight");
            renderer.SetBlendShapeWeight(0, 30f);
            Undo.IncrementCurrentGroup();
            var target = MixerSession.Edit(renderer);
            var working = target.Working;
            // Edits outside Mixer's history (as an autosave brings them back after a reload): undoing past the session's
            // start keeps them.
            var doc = target.Document;
            int top = Enumerable.Range(0, doc.VertexCount).OrderByDescending(v => doc.Positions[v].y).First();
            doc.SetLayerPosition(null, top, doc.Positions[top] + Vector3.up * 0.1f);
            doc.MarkEdited(new[] { top });
            target.Flush();
            var edited = (Vector3[])doc.Positions.Clone();

            Undo.PerformUndo(); // the step before the session: the renderer gets the mesh it had then, the session waits
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(MixerSession.TargetOf(renderer), Is.Null);
            MixerSession.SimulateReload(scene);
            Assert.That(MixerSession.TargetOf(renderer), Is.Null, "after a reload it still waits");
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));

            var again = MixerSession.Edit(renderer);
            Assert.That(again.Working, Is.SameAs(working), "editing it again takes it up, on its working mesh");
            Assert.That(renderer.sharedMesh, Is.SameAs(working));
            Assert.That(MaxDistance(again.Document.Positions, edited), Is.LessThan(1e-7f), "with its edits, not a new session over them");
        }

        [Test]
        public void AutosavedShapesKeepDeltasEditedBackToZero()
        {
            var target = MixerSession.Edit(renderer);
            MixerSession.SetLayer(target, "Wide", createIfMissing: false);
            var doc = target.Document;
            int top = Enumerable.Range(0, doc.VertexCount).First(v => doc.Shape("Wide").Deltas[v].sqrMagnitude > 0f);
            doc.SetLayerPosition("Wide", top, doc.Positions[top]);
            doc.MarkEdited(new[] { top });
            MixerSession.FlushAutosavesNow();
            var fresh = new EditTarget(renderer, target.Original, target.Working);
            Assert.That(MixerAutosave.Restore(fresh), Is.True);
            Assert.That(fresh.Document.Shape("Wide").Deltas[top], Is.EqualTo(Vector3.zero), "the original's delta does not come back");
        }

        // A file cut short (a crash while it was written by an older Mixer, a full disk) is read whole before anything of it
        // is applied: the document stays as it was, and the file is kept aside.
        [Test]
        public void ATruncatedAutosaveChangesNothing()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var belt = MixerSession.AddGroup(target, "Belt");
            belt.Weights[3] = 0.5f;
            target.Document.Revision++;
            MixerSession.FlushAutosavesNow();
            string path = MixerAutosave.PathFor(target.Id);
            var whole = System.IO.File.ReadAllBytes(path);
            // Cut after the moved vertices, inside what follows.
            System.IO.File.WriteAllBytes(path, whole.Take(whole.Length - 24).ToArray());
            var fresh = new EditTarget(renderer, target.Original, target.Working);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("could not be read"));
            Assert.That(MixerAutosave.Restore(fresh), Is.False);
            Assert.That(fresh.Document.HasDifferences(), Is.False, "nothing of the cut file was applied");
            Assert.That(fresh.Document.MovedBase.Count, Is.EqualTo(0));
            Assert.That(System.IO.File.Exists(path), Is.False);
            Assert.That(System.IO.File.Exists(path + ".corrupt"), Is.True, "kept for a look");
            System.IO.File.Delete(path + ".corrupt");
        }

        // The same positions with other triangles (an FBX reimported with its faces changed) are another mesh: an autosave
        // made on the first is not applied to it.
        [Test]
        public void AnAutosaveIsNotAppliedToAMeshReimportedWithOtherTriangles()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            MixerSession.FlushAutosavesNow();
            var other = Object.Instantiate(mesh);
            try
            {
                var triangles = other.triangles;
                (triangles[1], triangles[2]) = (triangles[2], triangles[1]);
                other.triangles = triangles;
                Assert.That(other.vertices, Is.EqualTo(mesh.vertices), "the same positions");
                Assert.That(MixerAutosave.FingerprintOf(other), Is.Not.EqualTo(MixerAutosave.FingerprintOf(mesh)));
                var fresh = new EditTarget(renderer, other, target.Working);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("changed since its edits were autosaved"));
                Assert.That(MixerAutosave.Restore(fresh), Is.False);
                Assert.That(MixerAutosave.FingerprintOf(mesh), Is.EqualTo(target.Document.OriginalFingerprint), "the document's fingerprint is the mesh's");
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void RestoredSessionsKeepTheVertexGroupsBesideTheMesh()
        {
            var target = MixerSession.Edit(renderer);
            var belt = MixerSession.AddGroup(target, "Belt");
            belt.Weights[3] = 0.5f;
            target.Document.Revision++;
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var asset = MixerSession.Commit(target);
            var again = MixerSession.Edit(renderer);
            Assert.That(again.Document.Group("Belt"), Is.Not.Null);
            Assert.That(again.Document.HasDifferences(), Is.False, "groups loaded from beside the mesh are no edit");
            Inflate(again, new Vector3(-0.3f, 1.5f, 0f));
            MixerSession.FlushAutosavesNow();
            // What a reload rebuilds: the groups beside the mesh, then the autosave (which has the session's own).
            var fresh = new EditTarget(renderer, asset, again.Working);
            MixerSidecar.Load(asset, fresh.Document);
            Assert.That(MixerAutosave.Restore(fresh), Is.True);
            Assert.That(fresh.Document.Groups.Count, Is.EqualTo(1), "the groups are there once");
            Assert.That(fresh.Document.Group("Belt").Weights[3], Is.EqualTo(0.5f));
        }

        [Test]
        public void ADeletedObjectComesBackWithItsEditsOnUndo()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var edited = (Vector3[])target.Document.Positions.Clone();
            var body = renderer.gameObject;
            Undo.IncrementCurrentGroup();
            Undo.DestroyObjectImmediate(body);
            MixerSession.Tick();
            Assert.That(MixerSession.Targets, Has.No.Member(target), "the session waits while the object is gone");

            Undo.PerformUndo();
            var back = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name == "Body");
            Assert.That(back, Is.Not.Null, "Undo brings the object back");
            Assert.That(ShowsMissingMesh(back), Is.False, "never on the destroyed working mesh");
            MixerSession.Tick();
            var session = MixerSession.TargetOf(back);
            Assert.That(session, Is.Not.Null, "its session comes back");
            Assert.That(MaxDistance(session.Document.Positions, edited), Is.LessThan(1e-7f), "with its edits");
            Assert.That(back.sharedMesh, Is.SameAs(session.Working));
        }

        [Test]
        public void UndoPastTheStartOfASessionLetsItWaitAndEditingAgainTakesItUp()
        {
            // A step made before the session kept the whole renderer, with the mesh it had then.
            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(renderer, "Blend shape weight");
            renderer.SetBlendShapeWeight(0, 30f);
            Undo.IncrementCurrentGroup();
            var target = MixerSession.Edit(renderer);
            var working = target.Working;
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var edited = (Vector3[])target.Document.Positions.Clone();

            Undo.PerformUndo(); // the stroke
            Undo.PerformUndo(); // the step before the session: the renderer gets the mesh it had then
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(MixerSession.TargetOf(renderer), Is.Null, "the session waits instead of drawing over the undone state");
            MixerSession.Tick();
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "and does not come back by itself");

            // Unity's redo of that step gives the renderer what the step left (the original mesh), never the working one.
            Undo.PerformRedo();
            Assert.That(MixerSession.TargetOf(renderer), Is.Null);
            Assert.That(MixerSession.Edit(renderer), Is.SameAs(target), "editing it again takes the same session up");
            Assert.That(renderer.sharedMesh, Is.SameAs(working));
            Undo.PerformRedo(); // the stroke, from the session's own history
            Assert.That(MaxDistance(target.Document.Positions, edited), Is.LessThan(1e-7f));
        }

        [Test]
        public void ADuplicateOfAnEditedObjectGetsASessionOfItsOwn()
        {
            var target = MixerSession.Edit(renderer);
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var edited = (Vector3[])target.Document.Positions.Clone();
            // Ctrl+D copies the renderer with the working mesh it shows.
            var copyObject = Object.Instantiate(renderer.gameObject, root.transform);
            var copy = copyObject.GetComponent<SkinnedMeshRenderer>();
            Assert.That(copy.sharedMesh, Is.SameAs(target.Working));
            MixerMeshGuard.RepairUnder(copyObject);
            var copySession = MixerSession.TargetOf(copy);
            Assert.That(copySession, Is.Not.Null, "the copy is edited too");
            Assert.That(copy.sharedMesh, Is.Not.SameAs(target.Working), "with a working mesh of its own");
            Assert.That(MaxDistance(copySession.Document.Positions, edited), Is.LessThan(1e-7f), "and the same edits");

            var asset = MixerSession.Commit(target);
            Assert.That(copy.sharedMesh, Is.SameAs(copySession.Working), "committing the source leaves the copy's mesh alone");
            MixerSession.SimulateSceneSave(scene, () =>
            {
                Assert.That(copy.sharedMesh, Is.SameAs(mesh), "a scene save writes the copy with the original mesh, not none");
            });
            Assert.That(copy.sharedMesh, Is.SameAs(copySession.Working));
            Assert.That(renderer.sharedMesh, Is.SameAs(asset));
        }

        [Test]
        public void PlayModeRoundTripKeepsTheEditsAndTheRestPose()
        {
            var target = MoveUpperBoneInRestPose();
            Inflate(target, new Vector3(0.3f, 0.5f, 0f));
            var tip = ArmatureEdit.AddLeafBone(target, upper, "Tip");
            var shown = Baked(renderer);
            var positions = (Vector3[])target.Document.Positions.Clone();
            var poses = (Matrix4x4[])target.Document.BindPoses.Clone();
            var moved = upper.localPosition;

            MixerSession.SimulatePlay(scene, () =>
            {
                Assert.That(MixerSession.TargetOf(renderer), Is.Null, "nothing is edited in Play");
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "Play shows the original mesh");
                Assert.That(renderer.bones.Length, Is.EqualTo(2), "without the added leaf bone");
                Assert.That(Vector3.Distance(upper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-4f), "and the bones where the original bind poses want them");
            });
            var back = MixerSession.TargetOf(renderer);
            Assert.That(back, Is.Not.Null, "the session comes back when Play stops");
            Assert.That(MaxDistance(back.Document.Positions, positions), Is.LessThan(1e-7f));
            Assert.That(back.Document.BindPoses, Is.EqualTo(poses));
            Assert.That(renderer.bones.Length, Is.EqualTo(3));
            Assert.That(renderer.bones[2], Is.SameAs(tip));
            Assert.That(Vector3.Distance(upper.localPosition, moved), Is.LessThan(1e-4f), "the bones are where the edit put them");
            back.Flush();
            Assert.That(MaxDistance(Baked(renderer), shown), Is.LessThan(1e-4f), "the mesh shows as before Play");
        }

        [Test]
        public void UndoingABoneMoveFromBeforePlayKeepsTheSkin()
        {
            var rest = Baked(renderer);
            var target = MoveUpperBoneInRestPose();
            var original = (Matrix4x4[])target.Document.OriginalBindPoses.Clone();
            MixerSession.SimulatePlay(scene, null);
            var back = MixerSession.TargetOf(renderer);
            Assert.That(back, Is.Not.Null.And.Not.SameAs(target), "the session comes back when Play stops");

            Undo.PerformUndo();
            Assert.That(Vector3.Distance(upper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-6f), "the move from before Play is undone");
            Assert.That(back.Document.BindPoses, Is.EqualTo(original), "with its bind poses");
            Assert.That(MaxDistance(Baked(renderer), rest), Is.LessThan(1e-4f), "so the mesh is not deformed");
        }

        // ---- Saves and uploads during rest-pose editing ----------------------------------------------------------------

        [Test]
        public void ASceneSavedDuringRestPoseEditingHoldsTheOriginalAvatar()
        {
            var target = MoveUpperBoneInRestPose();
            ArmatureEdit.AddLeafBone(target, upper, "Tip");
            var shown = Baked(renderer);
            var moved = upper.localPosition;
            MixerSession.SimulateSceneSave(scene, () =>
            {
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "the file gets the original mesh");
                Assert.That(renderer.bones.Length, Is.EqualTo(2), "with as many bones as it has bind poses");
                Assert.That(Vector3.Distance(upper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-4f), "and the bone where the original bind pose wants it");
                Assert.That(MaxDistance(Baked(renderer), shown), Is.LessThan(1e-4f), "so the saved avatar is not deformed");
            });
            Assert.That(renderer.sharedMesh, Is.SameAs(target.Working), "editing goes on after the save");
            Assert.That(renderer.bones.Length, Is.EqualTo(3));
            Assert.That(Vector3.Distance(upper.localPosition, moved), Is.LessThan(1e-6f));
        }

        [Test]
        public void AnUploadCopyCarriesTheOriginalAvatar()
        {
            var target = MoveUpperBoneInRestPose();
            ArmatureEdit.AddLeafBone(target, upper, "Tip");
            var shown = Baked(renderer);
            // The SDK's build copy; made under an object of the test's scene, never in the open scene.
            var holder = new GameObject("BuildCopies");
            SceneManager.MoveGameObjectToScene(holder, scene);
            var copy = Object.Instantiate(root, holder.transform);
            var copyRenderer = copy.GetComponentInChildren<SkinnedMeshRenderer>();
            var copyUpper = copy.transform.Find("Lower/Upper");
            MixerSession.PrepareCopyForExport(copy);
            Assert.That(copyRenderer.sharedMesh, Is.SameAs(mesh), "the build copy gets the original mesh");
            Assert.That(copyRenderer.bones.Length, Is.EqualTo(2));
            Assert.That(Vector3.Distance(copyUpper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-4f), "its bones where the original bind poses want them");
            var copied = Baked(copyRenderer);
            var offset = copy.transform.position - root.transform.position;
            Assert.That(MaxDistance(copied.Select(v => v - offset).ToArray(), shown), Is.LessThan(1e-4f), "the uploaded avatar is not deformed");
            Assert.That(renderer.sharedMesh, Is.SameAs(target.Working), "the scene's own avatar is not touched");
            Object.DestroyImmediate(holder);
        }

        [Test]
        public void APoseTriedInWeightPaintIsNeitherSavedNorUploadedAndGoesWhenItEnds()
        {
            var modeBefore = MixerSceneTool.Mode;
            var focusBefore = MixerSceneTool.FocusTarget;
            var target = MixerSession.Edit(renderer);
            var rest = upper.localRotation;
            try
            {
                MixerSceneTool.FocusTarget = target;
                MixerSceneTool.Mode = MixerMode.Weights;
                Assert.That(BoneSubSelection.Pick(upper), Is.True);
                // What the bone's rotation rings do with a turn.
                BoneSubSelection.RotateBone(upper, Quaternion.AngleAxis(30f, Vector3.forward) * upper.rotation);
                var posed = upper.localRotation;
                Assert.That(Quaternion.Angle(posed, rest), Is.GreaterThan(29f));

                MixerSession.SimulateSceneSave(scene, () =>
                    Assert.That(Quaternion.Angle(upper.localRotation, rest), Is.LessThan(0.01f), "the scene file gets the bone as it was"));
                Assert.That(Quaternion.Angle(upper.localRotation, posed), Is.LessThan(0.01f), "the pose goes on after the save");

                // The SDK's build copy; made under an object of the test's scene, never in the open scene.
                var holder = new GameObject("BuildCopies");
                SceneManager.MoveGameObjectToScene(holder, scene);
                var copy = Object.Instantiate(root, holder.transform);
                MixerSession.PrepareCopyForExport(copy);
                Assert.That(Quaternion.Angle(copy.transform.Find("Lower/Upper").localRotation, rest), Is.LessThan(0.01f), "the upload gets the bone as it was");
                Assert.That(Quaternion.Angle(upper.localRotation, posed), Is.LessThan(0.01f), "the scene's own bone stays posed");
                Object.DestroyImmediate(holder);

                MixerSceneTool.Mode = MixerMode.None;
                Assert.That(Quaternion.Angle(upper.localRotation, rest), Is.LessThan(0.01f), "leaving Weight Paint puts the bone back");
            }
            finally
            {
                MixerSceneTool.Mode = MixerMode.None;
                MixerSceneTool.FocusTarget = focusBefore;
                MixerSceneTool.Mode = modeBefore;
            }
        }

        [Test]
        public void RevertPutsTheBonesBackAndDropsAddedLeafBones()
        {
            var target = MoveUpperBoneInRestPose();
            var tip = ArmatureEdit.AddLeafBone(target, upper, "Tip");
            var before = Baked(renderer);
            MixerSession.Revert(target);
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(renderer.bones.Length, Is.EqualTo(2), "the leaf bone leaves the renderer");
            Assert.That(tip == null, Is.True, "and the scene");
            Assert.That(Vector3.Distance(upper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-4f), "the bone is back");
            Assert.That(MaxDistance(Baked(renderer), before), Is.LessThan(1e-4f), "the skin did not jump");
        }

        [Test]
        public void UndoingACommitOfRestPoseEditsShowsTheOriginalOnItsOwnRestPose()
        {
            var original = Baked(renderer);
            var target = MoveUpperBoneInRestPose();
            ArmatureEdit.AddLeafBone(target, upper, "Tip");
            var asset = MixerSession.Commit(target);
            Assert.That(renderer.sharedMesh, Is.SameAs(asset));
            Assert.That(renderer.bones.Length, Is.EqualTo(3));

            Undo.PerformUndo();
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "undoing the commit shows the mesh from before the session");
            Assert.That(renderer.bones.Length, Is.EqualTo(2), "with its own bone list (as many bones as bind poses)");
            Assert.That(Vector3.Distance(upper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-4f), "and the bone where its bind poses want it");
            Assert.That(MaxDistance(Baked(renderer), original), Is.LessThan(1e-4f), "so it shows as it was, not skinned by the moved bone");

            Undo.PerformRedo();
            Assert.That(renderer.sharedMesh, Is.SameAs(asset), "redo shows the commit again");
            Assert.That(renderer.bones.Length, Is.EqualTo(3));
        }

        [Test]
        public void UndoAfterARevertedRestPoseEditLeavesTheBonesWhereTheOriginalWantsThem()
        {
            var original = Baked(renderer);
            var target = MoveUpperBoneInRestPose();
            MixerSession.Revert(target);
            Undo.PerformUndo();
            Assert.That(Vector3.Distance(upper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-4f), "the reverted move does not come back under the original");
            Assert.That(MaxDistance(Baked(renderer), original), Is.LessThan(1e-4f));
        }

        [Test]
        public void ADuplicatedShapeStartsAtZeroOnTheRenderer()
        {
            string name = mesh.GetBlendShapeName(0);
            renderer.SetBlendShapeWeight(0, 60f);
            var target = MixerSession.Edit(renderer);
            Assert.That(target.Document.DuplicateShape(name, name + " copy"), Is.Not.Null);
            var asset = MixerSession.Commit(target);
            Assert.That(renderer.GetBlendShapeWeight(asset.GetBlendShapeIndex(name)), Is.EqualTo(60f).Within(1e-4f), "the shape keeps its weight");
            Assert.That(renderer.GetBlendShapeWeight(asset.GetBlendShapeIndex(name + " copy")), Is.EqualTo(0f), "its copy is new: 0, not shown twice");
        }

        [Test]
        public void UndoingABoneMoveOrAnAddedBoneAfterAReloadKeepsTheSkin()
        {
            var target = MixerSession.Edit(renderer);
            var rest = Baked(renderer);
            var original = (Matrix4x4[])target.Document.BindPoses.Clone();
            ArmatureEdit.Active = true;
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(upper, "Move bone");
            upper.localPosition += new Vector3(0.3f, 0.2f, 0f);
            ArmatureEdit.Recompute(target, joinCurrentGroup: true);
            Undo.IncrementCurrentGroup();
            // A reload clears Mixer's own history; Unity's stays.
            MixerHistory.Forget(target);
            Undo.PerformUndo();
            Assert.That(Vector3.Distance(upper.localPosition, new Vector3(0, 1f, 0)), Is.LessThan(1e-6f));
            Assert.That(target.Document.BindPoses, Is.EqualTo(original), "the bind poses come back with the bone");
            Assert.That(MaxDistance(Baked(renderer), rest), Is.LessThan(1e-4f), "the mesh is not deformed");

            ArmatureEdit.AddLeafBone(target, upper, "Tip");
            MixerHistory.Forget(target);
            Undo.PerformUndo();
            Assert.That(renderer.bones.Length, Is.EqualTo(2));
            Assert.That(target.Document.BindPoses.Length, Is.EqualTo(2), "as many bind poses as bones");
            Assert.That(renderer.sharedMesh.bindposes.Length, Is.EqualTo(2));
        }

        // ---- Shapes --------------------------------------------------------------------------------------------------

        [Test]
        public void AShapeNameCanBeDeletedAndUsedAgain()
        {
            var target = MixerSession.Edit(renderer);
            var doc = target.Document;
            MixerSession.DeleteShape(target, "Wide");
            MixerSession.SetLayer(target, "Wide", createIfMissing: true);
            Assert.That(doc.HasShape("Wide"), Is.True, "the new shape shows");
            Assert.That(doc.Layer, Is.EqualTo("Wide"));
            Assert.That(doc.Shape("Wide").Deltas.All(d => d == Vector3.zero), Is.True, "it starts empty, not as the deleted one");
            Inflate(target, new Vector3(0.3f, 1f, 0f));
            var deltas = (Vector3[])doc.Shape("Wide").Deltas.Clone();
            var asset = MixerSession.Commit(target);
            Assert.That(asset.blendShapeCount, Is.EqualTo(1));
            Assert.That(asset.GetBlendShapeName(0), Is.EqualTo("Wide"));
            var got = new Vector3[asset.vertexCount];
            asset.GetBlendShapeFrameVertices(0, 0, got, null, null);
            Assert.That(MaxDistance(got, deltas), Is.LessThan(1e-6f), "the commit has the new shape");

            var again = MixerSession.Edit(renderer);
            MixerSession.DeleteShape(again, "Wide");
            MixerSession.SetLayer(again, "Puff", createIfMissing: true);
            MixerSession.RenameShape(again, "Puff", "Wide");
            Assert.That(again.Document.ShapeOrder.Count(n => n == "Wide"), Is.EqualTo(1), "a name is in the order once");
            Inflate(again, new Vector3(-0.3f, 1f, 0f));
            var second = MixerSession.Commit(again);
            Assert.That(second.blendShapeCount, Is.EqualTo(1));
        }

        [Test]
        public void ARenamedShapeKeepsItsFramesAndShapesKeepTheirWeightsByName()
        {
            var m = MixerCoreTestsFixtures.Cylinder(32, 24, 0.3f, 2f);
            var half = new Vector3[m.vertexCount];
            var full = new Vector3[m.vertexCount];
            for (int v = 0; v < full.Length; v++) if (m.vertices[v].y < 0.5f) { full[v] = new Vector3(0f, -0.2f, 0f); half[v] = new Vector3(0f, -0.05f, 0f); }
            m.AddBlendShapeFrame("Steps", 50f, half, null, null);
            m.AddBlendShapeFrame("Steps", 100f, full, null, null);
            m.AddBlendShapeFrame("Last", 100f, full, null, null);
            var r = Skinned(root, "Steps", Asset(m, "Steps"));
            r.SetBlendShapeWeight(0, 10f);  // Wide
            r.SetBlendShapeWeight(1, 40f);  // Steps
            r.SetBlendShapeWeight(2, 70f);  // Last
            var target = MixerSession.Edit(r);
            MixerSession.DeleteShape(target, "Wide");
            MixerSession.RenameShape(target, "Steps", "Stairs");
            var asset = MixerSession.Commit(target);
            Assert.That(Enumerable.Range(0, asset.blendShapeCount).Select(asset.GetBlendShapeName), Is.EqualTo(new[] { "Stairs", "Last" }));
            int stairs = asset.GetBlendShapeIndex("Stairs");
            Assert.That(asset.GetBlendShapeFrameCount(stairs), Is.EqualTo(2), "the in-between goes with the rename");
            Assert.That(asset.GetBlendShapeFrameWeight(stairs, 0), Is.EqualTo(50f));
            var got = new Vector3[asset.vertexCount];
            asset.GetBlendShapeFrameVertices(stairs, 0, got, null, null);
            Assert.That(MaxDistance(got, half), Is.LessThan(1e-6f));
            Assert.That(r.GetBlendShapeWeight(stairs), Is.EqualTo(40f), "the renamed shape keeps its weight");
            Assert.That(r.GetBlendShapeWeight(asset.GetBlendShapeIndex("Last")), Is.EqualTo(70f), "a deleted shape does not shift the others' weights");
        }

        [Test]
        public void MirroringAShapeLeavesTheCentreLineSymmetric()
        {
            var target = MixerSession.Edit(renderer);
            MixerSession.SetLayer(target, "Bump", createIfMissing: true);
            var doc = target.Document;
            // A delta with a sideways part on the centre line (x = 0) and one on the +X side.
            var centre = Enumerable.Range(0, doc.VertexCount).First(v => Mathf.Abs(doc.Positions[v].x) < 1e-5f && doc.Positions[v].z > 0f && doc.Positions[v].y > 0.9f);
            doc.SetLayerPosition("Bump", centre, doc.Positions[centre] + new Vector3(0.05f, 0f, 0.05f));
            doc.MarkEdited(new[] { centre });
            MixerSession.MirrorShape(target, "Bump", fromPositiveX: true);
            Assert.That(doc.Shape("Bump").Deltas[centre].x, Is.EqualTo(0f), "on the centre line the mirrored shape does not move sideways");
            Assert.That(doc.Shape("Bump").Deltas[centre].z, Is.EqualTo(0.05f).Within(1e-6f));
        }
    }
}
