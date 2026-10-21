using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.LiveLink;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>
    /// The link's codecs and scene graph, and a fake Blender over a real socket on a private link host (its own port,
    /// token and preview scene: the user's Blender link, its endpoint file and the Enabled setting are left alone).
    /// </summary>
    public sealed class MixerLinkTests
    {
        private Scene scene;
        private GameObject root;
        private SkinnedMeshRenderer renderer;
        private Mesh mesh;
        // Sessions open before the test (another tool's, the user's): left alone.
        private HashSet<EditTarget> targetsBefore;

        [SetUp]
        public void SetUp()
        {
            targetsBefore = new HashSet<EditTarget>(MixerSession.Targets);
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("LinkFixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            var lower = new GameObject("Hip").transform; lower.SetParent(root.transform, false);
            var upper = new GameObject("Chest").transform; upper.SetParent(lower, false); upper.localPosition = new Vector3(0, 1f, 0);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            renderer = body.AddComponent<SkinnedMeshRenderer>();
            mesh = MixerCoreTestsFixtures.Cylinder(16, 8, 0.3f, 2f);
            Toolkit.Editor.Meshes.MeshAssets.EnsureFolder("Assets/Orbiters/MixerTests");
            AssetDatabase.CreateAsset(mesh, "Assets/Orbiters/MixerTests/LinkFixture.asset");
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { lower, upper };
            renderer.rootBone = lower;
            var light = new GameObject("Sun"); light.transform.SetParent(root.transform, false); light.AddComponent<Light>().type = LightType.Directional;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var t in MixerSession.Targets.ToList()) if (!targetsBefore.Contains(t)) MixerSession.Revert(t);
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset("Assets/Orbiters/MixerTests");
        }

        [Test]
        public void SceneGraphListsObjectsWithKindsAndBones()
        {
            var graph = SceneGraphExporter.Build(scene, null);
            Assert.That(graph.nodes.Count, Is.EqualTo(5), "root, two bones, body, sun");
            var body = graph.nodes.Single(n => n.name == "Body");
            Assert.That(body.type, Is.EqualTo("Skinned"));
            Assert.That(body.bones.Length, Is.EqualTo(2));
            Assert.That(body.vertexCount, Is.EqualTo(mesh.vertexCount));
            Assert.That(graph.nodes.Single(n => n.name == "Hip").type, Is.EqualTo("Bone"));
            Assert.That(graph.nodes.Single(n => n.name == "Chest").parent, Is.EqualTo(graph.nodes.Single(n => n.name == "Hip").id));
            Assert.That(graph.nodes.Single(n => n.name == "Sun").type, Is.EqualTo("Light"));
            var settings = ScriptableObject.CreateInstance<MixerLinkSettings>();
            settings.excludedIds.Add(graph.nodes.Single(n => n.name == "Sun").id);
            var filtered = SceneGraphExporter.Build(scene, settings);
            Assert.That(filtered.nodes.Any(n => n.name == "Sun"), Is.False, "an excluded object is left out");
            root.transform.Find("Body").localPosition = new Vector3(1, 2, 3);
            var delta = SceneGraphExporter.Diff(graph, SceneGraphExporter.Build(scene, null));
            Assert.That(delta.transforms.Count, Is.EqualTo(1));
            Assert.That(delta.transforms[0].p, Is.EqualTo(new[] { 1f, 2f, 3f }));
            UnityEngine.Object.DestroyImmediate(settings);
        }

        // A node whose target FBX, materials, bones or light changed is resent without its mesh; one whose mesh changed, with it.
        [Test]
        public void ADeltaResendsChangedNodesAndOnlyChangedMeshes()
        {
            var before = SceneGraphExporter.Build(scene, null);
            var after = SceneGraphExporter.Build(scene, null);
            var body = after.nodes.Single(n => n.name == "Body");
            body.targetFbx = "Assets/Base.fbx";
            after.nodes.Single(n => n.name == "Sun").light.intensity = 3f;
            var delta = SceneGraphExporter.Diff(before, after);
            Assert.That(delta.added, Is.Empty, "no mesh goes again for a target FBX or a light");
            Assert.That(delta.changed.Select(n => n.name), Is.EquivalentTo(new[] { "Body", "Sun" }));
            var again = SceneGraphExporter.Build(scene, null);
            again.nodes.Single(n => n.name == "Body").meshId = "another mesh";
            Assert.That(SceneGraphExporter.Diff(before, again).added.Select(n => n.name), Is.EqualTo(new[] { "Body" }), "a node showing another mesh is resent with it");
            Assert.That(SceneGraphExporter.Diff(before, SceneGraphExporter.Build(scene, null)).Empty, Is.True);
        }

        [Test]
        public void MeshDataRoundTripsWeldedVerticesSkinShapesAndTopology()
        {
            var target = MixerSession.Edit(renderer);
            var payload = MeshDataCodec.Encode("mesh", target);
            var decoded = MeshDataCodec.Decode(payload);
            var n = target.Document.LinkWeld;
            Assert.That(decoded.Vertices.Length, Is.EqualTo(n.WeldedCount), "welded: the seam column is one vertex");
            Assert.That(decoded.Vertices.Length, Is.LessThan(mesh.vertexCount));
            Assert.That(decoded.CornerVertex.Length, Is.EqualTo(mesh.triangles.Length));
            Assert.That(decoded.Bones, Is.EqualTo(new[] { "Hip", "Chest" }));
            Assert.That(decoded.Influences.Length, Is.EqualTo(n.WeldedCount));
            Assert.That(decoded.Shapes.Count, Is.EqualTo(1));
            Assert.That(decoded.Shapes[0].name, Is.EqualTo("Wide"));
            Assert.That(decoded.Shapes[0].indices.Length, Is.GreaterThan(10));
            Assert.That(decoded.Topology, Is.EqualTo(MeshDataCodec.Topology(target.Document)).And.Not.EqualTo(0UL), "the mesh says which topology it is");
            var fromAsset = MeshDataCodec.Decode(MeshDataCodec.Encode("mesh", mesh, renderer));
            Assert.That(fromAsset.Vertices.Length, Is.EqualTo(decoded.Vertices.Length), "the asset path welds the same");
            Assert.That(fromAsset.Topology, Is.EqualTo(decoded.Topology), "and has the same topology");
            var positions = MeshDataCodec.DecodePositions(MeshDataCodec.EncodePositions("mesh", "Wide", 77UL, decoded.Vertices));
            Assert.That(positions.Shape, Is.EqualTo("Wide"));
            Assert.That(positions.Topology, Is.EqualTo(77UL));
            Assert.That(positions.Positions.Length, Is.EqualTo(decoded.Vertices.Length));
            var weights = MeshDataCodec.DecodeWeights(MeshDataCodec.EncodeWeights("mesh", "Chest", false, 1, 77UL, new[] { 3, 4 }, new[] { 0.25f, 1f }));
            Assert.That((weights.Bone, weights.Topology), Is.EqualTo((1, 77UL)));
            Assert.That(weights.Indices, Is.EqualTo(new[] { 3, 4 }));
            Assert.That(weights.Weights, Is.EqualTo(new[] { 0.25f, 1f }));
            Assert.That(MeshDataCodec.PeekId(payload), Is.EqualTo("mesh"));
        }

        // A double-sided sheet (front and back at the same places, facing apart) with a second UV channel of three
        // components (channel 1 empty), vertex colours, a shape with an in-between frame, and two bones' bind poses.
        [Test]
        public void MeshDataCarriesUvsColoursFramesAndBindPosesAndKeepsSheetsApart()
        {
            var sheet = new Mesh { name = "Sheet" };
            var places = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
            sheet.vertices = places.Concat(places).ToArray();
            sheet.normals = Enumerable.Repeat(Vector3.back, 4).Concat(Enumerable.Repeat(Vector3.forward, 4)).ToArray();
            sheet.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6 };
            sheet.SetUVs(0, sheet.vertices.Select(v => new Vector2(v.x, v.y)).ToList());
            var uv2 = Enumerable.Range(0, 8).Select(i => new Vector3(i, i * 0.5f, i * 0.25f)).ToList();
            sheet.SetUVs(2, uv2);
            var colors = Enumerable.Range(0, 8).Select(i => new Color32((byte)(i * 30), (byte)(255 - i), 7, 200)).ToArray();
            sheet.colors32 = colors;
            sheet.boneWeights = Enumerable.Range(0, 8).Select(i => new BoneWeight { boneIndex0 = i % 2, weight0 = 1f }).ToArray();
            var poses = new[] { Matrix4x4.identity, Matrix4x4.TRS(new Vector3(0, -1, 0), Quaternion.Euler(0, 0, 30), Vector3.one) };
            sheet.bindposes = poses;
            var full = new Vector3[8]; var half = new Vector3[8];
            for (int v = 0; v < 8; v++) if (sheet.vertices[v].y > 0.5f) { full[v] = Vector3.up * 0.4f; half[v] = Vector3.up * 0.1f; }
            sheet.AddBlendShapeFrame("Lift", 50f, half, null, null);
            sheet.AddBlendShapeFrame("Lift", 100f, full, null, null);
            AssetDatabase.CreateAsset(sheet, "Assets/Orbiters/MixerTests/Sheet.asset");
            var go = new GameObject("Sheet"); go.transform.SetParent(root.transform, false);
            var sheetRenderer = go.AddComponent<SkinnedMeshRenderer>();
            sheetRenderer.sharedMesh = sheet;
            sheetRenderer.bones = renderer.bones;

            var fromAsset = MeshDataCodec.Decode(MeshDataCodec.Encode("sheet", sheet, sheetRenderer));
            var target = MixerSession.Edit(sheetRenderer);
            var fromDocument = MeshDataCodec.Decode(MeshDataCodec.Encode("sheet", target));
            foreach (var decoded in new[] { fromAsset, fromDocument })
            {
                Assert.That(decoded.Vertices.Length, Is.EqualTo(8), "front and back stay two sheets: no face is a duplicate");
                Assert.That(decoded.CornerUvs.Keys, Is.EqualTo(new[] { 2 }), "only the channels the mesh has");
                var (dimension, values) = decoded.CornerUvs[2];
                Assert.That(dimension, Is.EqualTo(3));
                for (int corner = 0; corner < sheet.triangles.Length; corner++)
                {
                    var expected = uv2[sheet.triangles[corner]];
                    Assert.That(new Vector3(values[corner * 3], values[corner * 3 + 1], values[corner * 3 + 2]), Is.EqualTo(expected));
                    var c = colors[sheet.triangles[corner]];
                    Assert.That(decoded.CornerColors.Skip(corner * 4).Take(4).ToArray(), Is.EqualTo(new[] { c.r, c.g, c.b, c.a }));
                }
                Assert.That(decoded.Shapes.Single().name, Is.EqualTo("Lift"));
                var (weight, inBetweens) = decoded.ShapeFrames[0];
                Assert.That(weight, Is.EqualTo(100f));
                Assert.That(inBetweens.Count, Is.EqualTo(1));
                Assert.That(inBetweens[0].Weight, Is.EqualTo(50f));
                Assert.That(inBetweens[0].Deltas.All(d => d == Vector3.up * 0.1f), Is.True);
                Assert.That(inBetweens[0].Indices.Length, Is.EqualTo(4), "the top two places of both sheets");
                Assert.That(decoded.BindPoses, Is.EqualTo(poses));
            }
            Assert.That(MeshDataCodec.IsInBetweenKey("Lift @50"), Is.True);
            Assert.That(MeshDataCodec.IsInBetweenKey("Lift @12.5"), Is.True);
            Assert.That(MeshDataCodec.IsInBetweenKey("Lift"), Is.False);
            Assert.That(MeshDataCodec.IsInBetweenKey("me @home"), Is.False);
            Assert.That(MeshDataCodec.IsInBetweenOf(target.Document, "Lift @50"), Is.True, "a key of an existing shape's name and a weight is an in-between");
            Assert.That(MeshDataCodec.IsInBetweenOf(target.Document, "Other @50"), Is.False, "not without the shape");
        }

        // Two quads meeting on a row of split vertices (same places, normals and skin). When a shape opens the seam (moves
        // the two rows apart) they stay two rows in Blender; when every shape moves them alike (a UV seam) they weld.
        [Test]
        public void ASeamThatOpensInAShapeStaysTwoRowsAndAUvSeamWelds()
        {
            foreach (bool opens in new[] { true, false })
            {
                var strip = new Mesh { name = opens ? "Zipper" : "UvSeam" };
                strip.vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0),   // lower quad
                    new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(0, 2, 0), new Vector3(1, 2, 0),   // upper quad
                };
                strip.normals = Enumerable.Repeat(Vector3.back, 8).ToArray();
                strip.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 };
                strip.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, 8).ToArray();
                strip.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
                var deltas = new Vector3[8];
                deltas[2] = deltas[3] = Vector3.down * 0.1f;
                deltas[4] = deltas[5] = opens ? Vector3.up * 0.1f : Vector3.down * 0.1f;
                strip.AddBlendShapeFrame("Open", 100f, deltas, null, null);
                var go = new GameObject(strip.name); go.transform.SetParent(root.transform, false);
                var stripRenderer = go.AddComponent<SkinnedMeshRenderer>();
                stripRenderer.sharedMesh = strip;
                stripRenderer.bones = renderer.bones;
                var decoded = MeshDataCodec.Decode(MeshDataCodec.Encode(strip.name, strip, stripRenderer));
                Assert.That(decoded.Vertices.Length, Is.EqualTo(opens ? 8 : 6), opens ? "the seam that opens keeps both rows" : "the UV seam welds");
                UnityEngine.Object.DestroyImmediate(strip);
            }
        }

        // A card: two quads side by side split by a UV seam, and the same card again on the same places with the same
        // normals. The copy gets vertices of its own, and still welds across its own seam, whatever each seam vertex's
        // number of faces.
        [Test]
        public void ACopiedSheetWeldsAcrossItsOwnSeam()
        {
            var left = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
            var right = new[] { new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(1, 1, 0), new Vector3(2, 1, 0) };
            var card = left.Concat(right).ToArray();
            var positions = card.Concat(card).ToArray();
            var normals = Enumerable.Repeat(Vector3.back, positions.Length).ToArray();
            // The left quad's seam corner (1) is on two triangles, the right quad's (4) on one: valences differ across the seam.
            var faces = new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 };
            var triangles = faces.Concat(faces.Select(i => i + 8)).ToArray();
            var n = MeshDataCodec.LinkWeld(positions, triangles, normals, 1e-5f, null);
            Assert.That(n.WeldedCount, Is.EqualTo(12), "six places on the card, six on its copy");
            Assert.That(n.Weld[1], Is.EqualTo(n.Weld[4]), "the card welds across its seam");
            Assert.That(n.Weld[9], Is.EqualTo(n.Weld[12]), "and so does its copy");
            Assert.That(n.Weld[1], Is.Not.EqualTo(n.Weld[9]), "the copy is apart from the card");
        }

        [Test]
        public void TheTopologySignatureChangesWithFacesOrVertexOrderNotWithPositions()
        {
            var positions = mesh.vertices;
            var triangles = mesh.triangles;
            var n = MeshDataCodec.LinkWeld(positions, triangles, mesh.normals, 1e-6f, null);
            ulong topology = MeshDataCodec.Topology(n, triangles);
            var moved = positions.Select(p => p * 1.5f).ToArray();
            Assert.That(MeshDataCodec.Topology(MeshDataCodec.LinkWeld(moved, triangles, mesh.normals, 1e-6f, null), triangles), Is.EqualTo(topology), "moving vertices keeps the topology");
            var flipped = (int[])triangles.Clone();
            int t = flipped[0]; flipped[0] = flipped[3]; flipped[3] = t; // two faces exchange a corner: same vertex count
            Assert.That(MeshDataCodec.Topology(MeshDataCodec.LinkWeld(positions, flipped, mesh.normals, 1e-6f, null), flipped), Is.Not.EqualTo(topology), "other faces on the same vertices are another topology");
        }

        [Test]
        public void ABoneOfBlendersListIsFoundByItsIndexBeforeItsName()
        {
            var a = new GameObject("Twin").transform;
            var b = new GameObject("Twin").transform;
            try
            {
                var bones = new[] { a, b, null };
                Assert.That(MeshDataCodec.ResolveBone(bones, 1, "Twin"), Is.EqualTo(1), "two bones of one name stay apart");
                Assert.That(MeshDataCodec.ResolveBone(bones, 2, ""), Is.EqualTo(2), "a bone without a transform keeps its index");
                Assert.That(MeshDataCodec.ResolveBone(bones, 7, "Twin"), Is.EqualTo(0), "else the first of the name");
                Assert.That(MeshDataCodec.ResolveBone(bones, 0, "Gone"), Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a.gameObject);
                UnityEngine.Object.DestroyImmediate(b.gameObject);
            }
        }

        [Test]
        public void DecodersRefuseCountsThePayloadDoesNotHold()
        {
            var bogus = new MemoryStream();
            using (var w = new BinaryWriter(bogus))
            {
                w.Write((ushort)1); w.Write((byte)'m'); w.Write((ushort)0);
                w.Write(int.MaxValue); // vertices
                w.Write(new byte[64]);
            }
            Assert.Throws<InvalidDataException>(() => MeshDataCodec.Decode(bogus.ToArray()), "a mesh announcing more vertices than it holds");
            var positions = MeshDataCodec.EncodePositions("m", null, 1UL, new Vector3[4]);
            Array.Resize(ref positions, positions.Length - 20);
            Assert.Throws<InvalidDataException>(() => MeshDataCodec.DecodePositions(positions));
            var weights = MeshDataCodec.EncodeWeights("m", "g", true, -1, 1UL, new[] { 1, 2 }, new[] { 1f, 1f });
            BitConverter.GetBytes(1 << 29).CopyTo(weights, weights.Length - 2 * 8 - 4);
            Assert.Throws<InvalidDataException>(() => MeshDataCodec.DecodeWeights(weights));
            // A Link section that says it is 1 byte long and holds 10: it would be read again from inside itself.
            var rewinding = new MemoryStream();
            using (var w = new BinaryWriter(rewinding))
            {
                w.Write((ushort)1); w.Write((byte)'m'); w.Write((ushort)0);
                w.Write(0); w.Write(0); w.Write(0); // vertices, corners, faces
                w.Write((byte)0); // no skin
                w.Write((ushort)0); w.Write((ushort)0); // shapes, groups
                w.Write((ushort)1);
                w.Write(MeshDataCodec.LinkSection); w.Write(1); w.Write(7UL); w.Write((ushort)0);
            }
            Assert.Throws<InvalidDataException>(() => MeshDataCodec.Decode(rewinding.ToArray()), "a section that holds more than its length");
        }

        [Test]
        public void MeshesLargerThanAFrameTravelInChunksThatMustFollowEachOther()
        {
            var payload = new byte[MixerLinkProtocol.MaxPayloadBytes + 5 * 1024 * 1024];
            new System.Random(7).NextBytes(payload);
            var chunks = MixerLinkProtocol.Chunks(payload, 42).ToList();
            Assert.That(chunks.Count, Is.GreaterThan(1));
            Assert.That(chunks.All(c => c.Length <= MixerLinkProtocol.MaxPayloadBytes), Is.True, "every chunk fits a frame");
            var assembler = new MeshChunkAssembler();
            byte[] whole = null;
            foreach (var chunk in chunks) whole = assembler.Add(chunk) ?? whole;
            Assert.That(whole, Is.EqualTo(payload), "put back together exactly");
            assembler.Add(chunks[0]);
            Assert.Throws<InvalidDataException>(() => assembler.Add(chunks[2]), "a chunk out of order drops the mesh");
            Assert.That(assembler.Busy, Is.False);
            // A first chunk announcing more than the link carries is refused before anything is allocated.
            var huge = new byte[MixerLinkProtocol.ChunkHeaderBytes + 4];
            BitConverter.GetBytes(9u).CopyTo(huge, 0);
            BitConverter.GetBytes(0u).CopyTo(huge, 4);
            BitConverter.GetBytes(30u).CopyTo(huge, 8);
            BitConverter.GetBytes((uint)MixerLinkProtocol.MaxMeshBytes + 1).CopyTo(huge, 12);
            Assert.Throws<InvalidDataException>(() => assembler.Add(huge), "a mesh past the link's limit is refused loudly");
        }

        // Unity's Commit facade on a session that changed nothing closes it and gives no asset (MCB took the original for a new one).
        [Test]
        public void CommittingAnUnchangedSessionGivesNoMesh()
        {
            MixerSession.Edit(renderer);
            Assert.That(MixerLink.Commit(renderer), Is.Null);
            Assert.That(MixerSession.TargetOf(renderer), Is.Null, "the session is closed");
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "the renderer shows its mesh");
        }

        [Test]
        public void PositionsOfTwoShapesOfOneMeshNeverCollapseIntoEachOther()
        {
            var accept = typeof(MixerLinkHost).GetMethod("Accept", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var collapse = typeof(MixerLinkHost).GetMethod("CollapseKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            string KeyOf(string shape)
            {
                var raw = new LiveLinkFrame { Type = (byte)MixerLinkProtocol.FrameType.Positions, Payload = MeshDataCodec.EncodePositions("mesh", shape, 1UL, new Vector3[2]) };
                accept.Invoke(null, new object[] { raw });
                return (string)collapse.Invoke(null, new object[] { raw });
            }
            Assert.That(KeyOf("A"), Is.Not.EqualTo(KeyOf("B")));
            Assert.That(KeyOf("A"), Is.EqualTo(KeyOf("A")));
            Assert.That(KeyOf(null), Is.Not.EqualTo(KeyOf("A")));
        }

        // ---- A fake Blender on a private host ---------------------------------------------------------------------------

        [Test]
        public void AFakeBlenderGetsTheSceneAndItsEditsLandAsOneUndoStep()
        {
            bool enabledBefore = MixerLinkService.Enabled, runningBefore = MixerLinkService.Running;
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                var frames = blender.Hello();
                Assert.That(frames.Any(f => f.type == MixerLinkProtocol.FrameType.Welcome), Is.True, "Unity answered Hello with Welcome");
                var graphFrame = frames.FirstOrDefault(f => f.type == MixerLinkProtocol.FrameType.SceneGraph);
                Assert.That(graphFrame.payload, Is.Not.Null, "Unity sent the scene graph");
                var graph = MixerLinkProtocol.Read<SceneGraph>(graphFrame.payload);
                Assert.That(graph.nodes.Any(n => n.name == "Body" && n.type == "Skinned"), Is.True, "the private host's scene");
                Assert.That(host.Connected, Is.True);
                frames.AddRange(blender.Pump(5, MixerLinkProtocol.FrameType.MeshData));
                var meshFrame = frames.FirstOrDefault(f => f.type == MixerLinkProtocol.FrameType.MeshData);
                Assert.That(meshFrame.payload, Is.Not.Null, "the body's mesh came");
                var sentMesh = MeshDataCodec.Decode(meshFrame.payload);

                // Blender claims the body and moves the welded vertices up by 0.1.
                var answer = blender.Claim(renderer);
                Assert.That(answer.Any(f => f.type == MixerLinkProtocol.FrameType.Claim), Is.False, "Unity does not claim back the mesh Blender just took");
                Assert.That(answer.Any(f => f.type == MixerLinkProtocol.FrameType.Release), Is.False, "nor refuses it");
                var target = MixerSession.TargetOf(renderer);
                Assert.That(target, Is.Not.Null, "a claim starts editing the renderer");
                Assert.That(MixerLinkService.IsBlenderOwned(target), Is.True);
                var n = target.Document.LinkWeld;
                var positions = new Vector3[n.WeldedCount];
                for (int w = 0; w < positions.Length; w++) positions[w] = target.Document.Positions[n.Representative[w]] + Vector3.up * 0.1f;
                string meshId = MixerLinkService.MeshIdOf(renderer);
                blender.Send(MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(meshId, null, sentMesh.Topology, positions));
                blender.Pump(1.0);
                Assert.That(target.Document.Positions[0].y, Is.EqualTo(mesh.vertices[0].y + 0.1f).Within(1e-5f), "Blender's positions show in the document");
                int before = MixerHistory.Count;
                blender.Release(renderer);
                Assert.That(MixerLinkService.IsBlenderOwned(target), Is.False);
                Assert.That(MixerHistory.Count, Is.EqualTo(before + 1), "the release recorded one history entry");
                Undo.PerformUndo();
                Assert.That(target.Document.Positions[0].y, Is.EqualTo(mesh.vertices[0].y).Within(1e-5f), "and Ctrl+Z takes Blender's edit back");
                blender.Send(MixerLinkProtocol.FrameType.Bye, null);
                blender.Pump(0.5);
            }
            Assert.That(MixerLinkService.Enabled, Is.EqualTo(enabledBefore), "the user's link setting is left as it was");
            Assert.That(MixerLinkService.Running, Is.EqualTo(runningBefore), "and so is the user's link");
        }

        [Test]
        public void AHelloOfAnotherProtocolIsToldWhyAndGetsNothingElse()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                var frames = blender.Hello(protocolVersion: MixerLinkProtocol.ProtocolVersion - 1);
                frames.AddRange(blender.Pump(1.0));
                var welcome = frames.Where(f => f.type == MixerLinkProtocol.FrameType.Welcome).Select(f => MixerLinkProtocol.Read<MixerLinkProtocol.Welcome>(f.payload)).SingleOrDefault();
                Assert.That(welcome, Is.Not.Null);
                Assert.That(welcome.protocolVersion, Is.EqualTo(MixerLinkProtocol.ProtocolVersion));
                Assert.That(welcome.error, Does.Contain("protocol"), "the Welcome says why");
                Assert.That(frames.Any(f => f.type == MixerLinkProtocol.FrameType.SceneGraph || f.type == MixerLinkProtocol.FrameType.MeshData), Is.False, "nothing else is sent");
                Assert.That(host.Connected, Is.False);
                var answer = blender.Claim(renderer);
                Assert.That(MixerSession.TargetOf(renderer), Is.Null, "and nothing it sends is taken");
            }
        }

        [Test]
        public void ARefusedOrForeignEditIsAnsweredAndAppliesNothing()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                // A claim for a mesh the renderer no longer shows (Blender's copy is of another mesh).
                var answer = blender.Claim(renderer, meshId: "not the body's mesh");
                var release = answer.Where(f => f.type == MixerLinkProtocol.FrameType.Release).Select(f => MixerLinkProtocol.Read<MixerLinkProtocol.Ownership>(f.payload)).FirstOrDefault();
                Assert.That(release, Is.Not.Null, "Unity answers a claim it does not take");
                Assert.That(release.side, Is.EqualTo("blender"));
                Assert.That(release.reason, Is.Not.Empty);
                Assert.That(MixerSession.TargetOf(renderer), Is.Null);

                // Positions for a mesh Blender does not hold: not applied, and no session is opened for them.
                string meshId = MixerLinkService.MeshIdOf(renderer);
                var target = MixerSession.Edit(renderer);
                var n = target.Document.LinkWeld;
                var moved = Enumerable.Range(0, n.WeldedCount).Select(w => target.Document.Positions[n.Representative[w]] + Vector3.up).ToArray();
                blender.Send(MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(meshId, null, MeshDataCodec.Topology(target.Document), moved));
                blender.Pump(0.5);
                Assert.That(target.Document.Positions[0], Is.EqualTo(mesh.vertices[0]), "an unclaimed mesh takes nothing from Blender");
            }
        }

        [Test]
        public void EditsMadeForAnotherTopologyOrOfAnInBetweenKeyAreNotApplied()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                blender.Claim(renderer);
                var target = MixerSession.TargetOf(renderer);
                Assert.That(target, Is.Not.Null);
                var doc = target.Document;
                var n = doc.LinkWeld;
                string meshId = MixerLinkService.MeshIdOf(renderer);
                var moved = Enumerable.Range(0, n.WeldedCount).Select(w => doc.Positions[n.Representative[w]] + Vector3.up).ToArray();
                // Same vertex count, other faces: the copy in Blender is of another topology.
                var statuses = blender.SendAndPump(MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(meshId, null, MeshDataCodec.Topology(doc) ^ 0x5555UL, moved), 0.5);
                Assert.That(doc.Positions[0], Is.EqualTo(mesh.vertices[0]), "positions for another topology are not applied");
                Assert.That(statuses.Any(f => f.type == MixerLinkProtocol.FrameType.Status && MixerLinkProtocol.Read<MixerLinkProtocol.StatusMessage>(f.payload).level == "error"), Is.True, "and Blender is told");
                // An in-between key edited live stays Blender's: no shape of that name appears.
                blender.SendAndPump(MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(meshId, "Wide @50", MeshDataCodec.Topology(doc), moved), 0.5);
                Assert.That(doc.HasShape("Wide @50"), Is.False, "an in-between key is not taken for a shape");
                Assert.That(doc.Positions[0], Is.EqualTo(mesh.vertices[0]));
                blender.Release(renderer);
            }
        }

        // Blender sends its whole mesh without the shape it was sent: the shape is deleted in Unity, in the same undo step.
        [Test]
        public void AShapeDeletedInBlenderIsDeletedInUnityWithUndo()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                blender.Claim(renderer);
                var target = MixerSession.TargetOf(renderer);
                Assert.That(target.Document.HasShape("Wide"), Is.True);
                string meshId = MixerLinkService.MeshIdOf(renderer);
                var snapshot = MeshDataCodec.Snapshot(meshId, target);
                snapshot.Shapes.RemoveAll(s => s.Name == "Wide");
                blender.Send(MixerLinkProtocol.FrameType.MeshData, MeshDataCodec.Encode(snapshot));
                blender.Release(renderer);
                Assert.That(target.Document.HasShape("Wide"), Is.False, "Blender deleted it");
                Undo.PerformUndo();
                Assert.That(target.Document.HasShape("Wide"), Is.True, "Ctrl+Z brings it back");
            }
        }

        // Live positions of a shape Unity deleted meanwhile do not bring it back, as a whole mesh does not.
        [Test]
        public void LivePositionsDoNotBringBackAShapeUnityDeleted()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                blender.Claim(renderer);
                var target = MixerSession.TargetOf(renderer);
                var doc = target.Document;
                doc.DeleteShape("Wide");
                var n = doc.LinkWeld;
                var moved = Enumerable.Range(0, n.WeldedCount).Select(w => doc.Positions[n.Representative[w]] + Vector3.up).ToArray();
                blender.SendAndPump(MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(MixerLinkService.MeshIdOf(renderer), "Wide", MeshDataCodec.Topology(doc), moved), 0.5);
                Assert.That(doc.HasShape("Wide"), Is.False, "the deleted shape stays deleted");
                blender.Release(renderer);
            }
        }

        // A shape Unity deletes while Blender holds the mesh reaches Blender once it lets go (the check of the shape list
        // waits for the release instead of being skipped).
        [Test]
        public void ShapesUnityChangedWhileBlenderHeldTheMeshGoAfterItsRelease()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                blender.Claim(renderer);
                var target = MixerSession.TargetOf(renderer);
                string meshId = MixerLinkService.MeshIdOf(renderer);
                var blenderCopy = MeshDataCodec.Encode(MeshDataCodec.Snapshot(meshId, target));
                target.Document.DeleteShape("Wide");
                blender.Pump(0.5);
                blender.Send(MixerLinkProtocol.FrameType.MeshData, blenderCopy);
                var frames = blender.Release(renderer);
                if (!frames.Any(f => f.type == MixerLinkProtocol.FrameType.MeshData)) frames.AddRange(blender.Pump(5, MixerLinkProtocol.FrameType.MeshData));
                var resent = frames.Where(f => f.type == MixerLinkProtocol.FrameType.MeshData).Select(f => MeshDataCodec.Decode(f.payload)).LastOrDefault();
                Assert.That(resent, Is.Not.Null, "Blender gets the mesh again");
                Assert.That(resent.Shapes.Any(s => s.name == "Wide"), Is.False, "without the shape Unity deleted");
            }
        }

        // A mesh Unity sent as Blender claimed it is dropped by Blender: Unity does not count its shapes as Blender's, so
        // the one Unity added is not taken for one Blender deleted.
        [Test]
        public void AMeshSentAsBlenderClaimsIsNotCountedAsBlenders()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                var target = MixerSession.Edit(renderer);
                string meshId = MixerLinkService.MeshIdOf(renderer);
                blender.Pump(0.3);
                var blenderCopy = MeshDataCodec.Encode(MeshDataCodec.Snapshot(meshId, target));
                target.Document.Shape("Added", create: true);
                target.Document.Revision++;
                blender.Acknowledge = false;
                var frames = blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                Assert.That(frames.Any(f => f.type == MixerLinkProtocol.FrameType.MeshData && MeshDataCodec.Decode(f.payload).Shapes.Any(s => s.name == "Added")), Is.True, "the mesh with the new shape went");
                blender.Claim(renderer);
                blender.Acknowledge = true;
                blender.Send(MixerLinkProtocol.FrameType.MeshReceived, MixerLinkProtocol.Json(new MixerLinkProtocol.MeshReceived { id = meshId }));
                blender.Send(MixerLinkProtocol.FrameType.MeshData, blenderCopy);
                blender.Release(renderer);
                Assert.That(target.Document.HasShape("Added"), Is.True, "Unity's new shape stays");
            }
        }

        private static MixerLinkProtocol.Ownership BlenderRelease(List<(MixerLinkProtocol.FrameType type, byte[] payload)> frames) =>
            frames.Where(f => f.type == MixerLinkProtocol.FrameType.Release).Select(f => MixerLinkProtocol.Read<MixerLinkProtocol.Ownership>(f.payload)).LastOrDefault(o => o.side == "blender");

        // Blender's edit of the body (welded vertices raised by lift), applied live under the claim of that number.
        private static void Lift(FakeBlender blender, EditTarget target, string meshId, Vector3[] from, float lift)
        {
            var n = target.Document.LinkWeld;
            var positions = new Vector3[n.WeldedCount];
            for (int w = 0; w < positions.Length; w++) positions[w] = from[n.Representative[w]] + Vector3.up * lift;
            blender.Send(MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(meshId, null, MeshDataCodec.Topology(target.Document), positions));
            blender.Pump(1.0);
        }

        // Claims carry Blender's number: Unity answers a refusal, and ends a claim it no longer takes, with that number, so
        // Blender tells them from a late answer to an older claim of the mesh.
        [Test]
        public void RefusedAndEndedClaimsAreAnsweredWithTheirNumber()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                var refused = BlenderRelease(blender.Claim(renderer, meshId: "not the body's mesh", number: 7));
                Assert.That(refused, Is.Not.Null, "Unity refuses a claim of another mesh");
                Assert.That(refused.claim, Is.EqualTo(7), "with the claim's number");
                blender.Claim(renderer, number: 8);
                var target = MixerSession.TargetOf(renderer);
                Assert.That(target != null && MixerLinkService.IsBlenderOwned(target), Is.True);
                MixerSession.Revert(target);
                var ended = BlenderRelease(blender.Pump(1.0, MixerLinkProtocol.FrameType.Release));
                Assert.That(ended, Is.Not.Null, "a revert ends Blender's claim");
                Assert.That(ended.claim, Is.EqualTo(8), "the claim held, by its number");
                Assert.That(ended.reason, Does.Contain("stays in Blender"), "and says Blender keeps its edits");
            }
        }

        // Ctrl+Z and Ctrl+Y in Unity reach Blender: the layer an undone or redone step moved comes as live positions.
        [Test]
        public void UndoAndRedoReachBlenderAsPositions()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                blender.Claim(renderer, number: 1);
                var target = MixerSession.TargetOf(renderer);
                string meshId = MixerLinkService.MeshIdOf(renderer);
                var original = (Vector3[])target.Document.Positions.Clone();
                Lift(blender, target, meshId, original, 0.1f);
                blender.Release(renderer);
                int rep = target.Document.LinkWeld.Representative[0];

                Undo.PerformUndo();
                var undone = blender.Pump(1.0, MixerLinkProtocol.FrameType.Positions).Where(f => f.type == MixerLinkProtocol.FrameType.Positions).Select(f => MeshDataCodec.DecodePositions(f.payload)).LastOrDefault();
                Assert.That(undone, Is.Not.Null, "Blender hears of the undo");
                Assert.That(undone.Id, Is.EqualTo(meshId));
                Assert.That(undone.Shape, Is.Null, "the base layer");
                Assert.That(undone.Positions[0].y, Is.EqualTo(original[rep].y).Within(1e-5f), "as Unity has it after the undo");

                Undo.PerformRedo();
                var redone = blender.Pump(1.0, MixerLinkProtocol.FrameType.Positions).Where(f => f.type == MixerLinkProtocol.FrameType.Positions).Select(f => MeshDataCodec.DecodePositions(f.payload)).LastOrDefault();
                Assert.That(redone, Is.Not.Null, "Blender hears of the redo");
                Assert.That(redone.Positions[0].y, Is.EqualTo(original[rep].y + 0.1f).Within(1e-5f));
            }
        }

        // An undo of a mesh Blender holds ends Blender's claim: Unity's document is what its history says (Blender's edit so
        // far is taken back, without a step of its own), and Blender is told it keeps its edits.
        [Test]
        public void AnUndoOfAMeshBlenderHoldsEndsTheClaimAndTakesItsEditBack()
        {
            using (var host = MixerLinkHost.OpenPrivate(scene))
            using (var blender = new FakeBlender(host))
            {
                blender.Hello();
                blender.Pump(5, MixerLinkProtocol.FrameType.MeshData);
                blender.Claim(renderer, number: 1);
                var target = MixerSession.TargetOf(renderer);
                string meshId = MixerLinkService.MeshIdOf(renderer);
                var original = (Vector3[])target.Document.Positions.Clone();
                Lift(blender, target, meshId, original, 0.1f);
                blender.Release(renderer);
                blender.Claim(renderer, number: 2);
                Lift(blender, target, meshId, original, 0.3f);
                Assert.That(target.Document.Positions[0].y, Is.EqualTo(original[0].y + 0.3f).Within(1e-5f), "Blender's second edit shows");
                int count = MixerHistory.Count;

                Undo.PerformUndo();
                var ended = BlenderRelease(blender.Pump(1.0, MixerLinkProtocol.FrameType.Release));
                Assert.That(ended, Is.Not.Null, "the claim Blender holds ends");
                Assert.That(ended.claim, Is.EqualTo(2));
                Assert.That(MixerLinkService.IsBlenderOwned(target), Is.False);
                Assert.That(target.Document.Positions[0].y, Is.EqualTo(original[0].y).Within(1e-5f), "Blender's edit in progress and the undone one are both gone");
                Assert.That(MixerHistory.Count, Is.EqualTo(count), "taking Blender's edit back adds no step");
                Assert.That(MixerHistory.CanRedo, Is.True, "and the redo stays");
            }
        }

        /// <summary>A Blender extension speaking the link over a real socket, pumping the host by hand (a test has no editor loop).</summary>
        private sealed class FakeBlender : IDisposable
        {
            private readonly MixerLinkHost host;
            private readonly TcpClient client;
            private readonly NetworkStream stream;
            private readonly object gate = new object();
            private readonly List<byte> incoming = new List<byte>();
            private readonly MeshChunkAssembler chunks = new MeshChunkAssembler();
            private readonly System.Reflection.MethodInfo pumpServer;
            /// <summary>Whether meshes are acknowledged as they come, as Blender does (off: still on their way).</summary>
            public bool Acknowledge = true;

            public FakeBlender(MixerLinkHost host)
            {
                this.host = host;
                pumpServer = typeof(LiveLinkServer).GetMethod("Pump", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                client = new TcpClient("127.0.0.1", host.Port) { NoDelay = true };
                stream = client.GetStream();
                // Unity sends big meshes while the test waits: a reader on a thread of its own keeps the socket drained.
                new System.Threading.Thread(() =>
                {
                    var temp = new byte[1 << 16];
                    try
                    {
                        while (true)
                        {
                            int read = stream.Read(temp, 0, temp.Length);
                            if (read <= 0) break;
                            lock (gate) for (int i = 0; i < read; i++) incoming.Add(temp[i]);
                        }
                    }
                    catch (Exception) { }
                }) { IsBackground = true, Name = "fake Blender reader" }.Start();
            }

            public void Send(MixerLinkProtocol.FrameType type, byte[] payload)
            {
                var data = MixerLinkProtocol.Format.Encode((byte)type, host.Token, payload ?? new byte[0]);
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }

            public List<(MixerLinkProtocol.FrameType type, byte[] payload)> SendAndPump(MixerLinkProtocol.FrameType type, byte[] payload, double seconds)
            {
                Send(type, payload);
                return Pump(seconds);
            }

            public List<(MixerLinkProtocol.FrameType type, byte[] payload)> Hello(int protocolVersion = MixerLinkProtocol.ProtocolVersion)
            {
                Send(MixerLinkProtocol.FrameType.Hello, MixerLinkProtocol.Json(new MixerLinkProtocol.Hello { blender = "5.2.0", extension = BlenderProjectLauncher.ExtensionVersion, protocolVersion = protocolVersion }));
                return Pump(5, MixerLinkProtocol.FrameType.Welcome, protocolVersion == MixerLinkProtocol.ProtocolVersion ? MixerLinkProtocol.FrameType.SceneGraph : MixerLinkProtocol.FrameType.Welcome);
            }

            public List<(MixerLinkProtocol.FrameType type, byte[] payload)> Claim(Renderer target, string meshId = null, int number = 0)
            {
                Send(MixerLinkProtocol.FrameType.Claim, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = SceneGraphExporter.IdOf(target.gameObject), meshId = meshId ?? MixerLinkService.MeshIdOf(target), side = "blender", claim = number }));
                return Pump(1.0);
            }

            public List<(MixerLinkProtocol.FrameType type, byte[] payload)> Release(Renderer target)
            {
                Send(MixerLinkProtocol.FrameType.Release, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = SceneGraphExporter.IdOf(target.gameObject), meshId = MixerLinkService.MeshIdOf(target), side = "blender" }));
                return Pump(1.0);
            }

            /// <summary>Pumps the host and reads its frames for a while, or until every type of <paramref name="until"/> came; meshes are acknowledged as Blender does.</summary>
            public List<(MixerLinkProtocol.FrameType type, byte[] payload)> Pump(double seconds, params MixerLinkProtocol.FrameType[] until)
            {
                var frames = new List<(MixerLinkProtocol.FrameType, byte[])>();
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (host.Server != null) pumpServer?.Invoke(host.Server, null);
                    host.Tick();
                    lock (gate)
                    {
                        while (incoming.Count >= LiveLinkFormat.HeaderBytes)
                        {
                            var head = incoming.GetRange(0, LiveLinkFormat.HeaderBytes).ToArray();
                            if (!MixerLinkProtocol.Format.TryReadHeader(head, out byte type, out int tokenLength, out int payloadLength)) { incoming.Clear(); break; }
                            int total = LiveLinkFormat.HeaderBytes + tokenLength + payloadLength;
                            if (incoming.Count < total) break;
                            var payload = incoming.GetRange(LiveLinkFormat.HeaderBytes + tokenLength, payloadLength).ToArray();
                            incoming.RemoveRange(0, total);
                            var frameType = (MixerLinkProtocol.FrameType)type;
                            if (frameType == MixerLinkProtocol.FrameType.MeshChunk)
                            {
                                payload = chunks.Add(payload);
                                if (payload == null) continue;
                                frameType = MixerLinkProtocol.FrameType.MeshData;
                            }
                            frames.Add((frameType, payload));
                            if (frameType == MixerLinkProtocol.FrameType.MeshData && Acknowledge)
                                Send(MixerLinkProtocol.FrameType.MeshReceived, MixerLinkProtocol.Json(new MixerLinkProtocol.MeshReceived { id = MeshDataCodec.PeekId(payload) }));
                        }
                    }
                    if (until.Length > 0 && until.All(t => frames.Any(f => f.Item1 == t))) break;
                    System.Threading.Thread.Sleep(20);
                }
                return frames;
            }

            public void Dispose()
            {
                try { client.Close(); }
                catch (Exception) { }
            }
        }
    }
}
