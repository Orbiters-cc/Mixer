using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.LiveLink;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>The link's codecs and scene graph, and a fake Blender over a real socket: Hello, Welcome, SceneGraph, MeshData, Claim, Positions, Release.</summary>
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

        [Test]
        public void MeshDataRoundTripsWeldedVerticesSkinAndShapes()
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
            var fromAsset = MeshDataCodec.Decode(MeshDataCodec.Encode("mesh", mesh, renderer));
            Assert.That(fromAsset.Vertices.Length, Is.EqualTo(decoded.Vertices.Length), "the asset path welds the same");
            var positions = MeshDataCodec.DecodePositions(MeshDataCodec.EncodePositions("mesh", "Wide", decoded.Vertices));
            Assert.That(positions.Shape, Is.EqualTo("Wide"));
            Assert.That(positions.Positions.Length, Is.EqualTo(decoded.Vertices.Length));
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

        [Test]
        public void AFakeBlenderGetsTheSceneAndItsEditsLandAsOneUndoStep()
        {
            MixerLinkService.Enabled = true;
            MixerLinkService.EnsureRunning();
            Assert.That(MixerLinkService.Running, Is.True);
            var endpoint = LiveLinkEndpointFile.Read<MixerLinkProtocol.Endpoint>(MixerLinkProtocol.EndpointPath);
            Assert.That(endpoint, Is.Not.Null, "the endpoint file is written");
            Assert.That(endpoint.port, Is.EqualTo(MixerLinkService.Port));
            string token = endpoint.token;
            using (var client = new TcpClient("127.0.0.1", endpoint.port) { NoDelay = true })
            {
                var stream = client.GetStream();
                Send(stream, token, MixerLinkProtocol.FrameType.Hello, MixerLinkProtocol.Json(new MixerLinkProtocol.Hello { blender = "5.2.0", extension = "0.1.0", sceneGuid = endpoint.sceneGuid }));
                var frames = Pump(stream, token, expectTypes: new[] { MixerLinkProtocol.FrameType.Welcome, MixerLinkProtocol.FrameType.SceneGraph }, seconds: 5);
                Assert.That(frames.Any(f => f.type == MixerLinkProtocol.FrameType.Welcome), Is.True, "Unity answered Hello with Welcome");
                var graphFrame = frames.FirstOrDefault(f => f.type == MixerLinkProtocol.FrameType.SceneGraph);
                Assert.That(graphFrame.payload, Is.Not.Null, "Unity sent the scene graph");
                var graph = MixerLinkProtocol.Read<SceneGraph>(graphFrame.payload);
                // The active scene is the user's, not the preview one: it holds whatever the editor has open.
                Assert.That(graph.nodes, Is.Not.Null);
                Assert.That(MixerLinkService.Connected, Is.True);

                // Blender claims the fixture body and moves the welded vertices up by 0.1.
                string nodeId = SceneGraphExporter.IdOf(renderer.gameObject);
                Send(stream, token, MixerLinkProtocol.FrameType.Claim, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = nodeId, side = "blender" }));
                Pump(stream, token, new MixerLinkProtocol.FrameType[0], 1.5);
                var target = MixerSession.TargetOf(renderer);
                Assert.That(target, Is.Not.Null, "a claim starts editing the renderer");
                Assert.That(MixerLinkService.IsBlenderOwned(target), Is.True);
                var n = target.Document.LinkWeld;
                var positions = new Vector3[n.WeldedCount];
                for (int w = 0; w < positions.Length; w++) positions[w] = target.Document.Positions[n.Representative[w]] + Vector3.up * 0.1f;
                string meshId = MixerLinkService.MeshIdOf(renderer);
                Send(stream, token, MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(meshId, null, positions));
                Pump(stream, token, new MixerLinkProtocol.FrameType[0], 1.5);
                Assert.That(target.Document.Positions[0].y, Is.EqualTo(mesh.vertices[0].y + 0.1f).Within(1e-5f), "Blender's positions show in the document");
                int before = MixerHistory.Count;
                Send(stream, token, MixerLinkProtocol.FrameType.Release, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = nodeId, side = "blender" }));
                Pump(stream, token, new MixerLinkProtocol.FrameType[0], 1.5);
                Assert.That(MixerLinkService.IsBlenderOwned(target), Is.False);
                Assert.That(MixerHistory.Count, Is.EqualTo(before + 1), "the release recorded one history entry");
                Undo.PerformUndo();
                Assert.That(target.Document.Positions[0].y, Is.EqualTo(mesh.vertices[0].y).Within(1e-5f), "and Ctrl+Z takes Blender's edit back");
                Send(stream, token, MixerLinkProtocol.FrameType.Bye, null);
            }
            Pump(null, token, new MixerLinkProtocol.FrameType[0], 1.0);
        }

        private static void Send(NetworkStream stream, string token, MixerLinkProtocol.FrameType type, byte[] payload)
        {
            var data = MixerLinkProtocol.Format.Encode((byte)type, token, payload ?? new byte[0]);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        // Pumps the editor's server and reads frames from the client for a while (the test has no editor loop). The client
        // reads on a thread of its own: Unity sends big meshes synchronously, and a reader on the same thread would deadlock.
        private static readonly object readLock = new object();
        private static readonly List<byte> incoming = new List<byte>();
        private static System.Threading.Thread readerThread;

        private static void StartReader(NetworkStream stream)
        {
            lock (readLock) incoming.Clear();
            readerThread = new System.Threading.Thread(() =>
            {
                var temp = new byte[1 << 16];
                try
                {
                    while (true)
                    {
                        int read = stream.Read(temp, 0, temp.Length);
                        if (read <= 0) break;
                        lock (readLock) incoming.AddRange(temp.Take(read));
                    }
                }
                catch (Exception) { }
            }) { IsBackground = true, Name = "fake Blender reader" };
            readerThread.Start();
        }

        private static List<(MixerLinkProtocol.FrameType type, byte[] payload)> Pump(NetworkStream stream, string token, MixerLinkProtocol.FrameType[] expectTypes, double seconds)
        {
            var frames = new List<(MixerLinkProtocol.FrameType, byte[])>();
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var tick = typeof(MixerLinkService).GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var serverField = typeof(MixerLinkService).GetField("server", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (stream != null && (readerThread == null || !readerThread.IsAlive)) StartReader(stream);
            while (DateTime.UtcNow < deadline)
            {
                // The server hands frames to the main thread from EditorApplication.update, which does not run inside a test: pump it by hand.
                var server = serverField.GetValue(null);
                server?.GetType().GetMethod("Pump", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(server, null);
                tick.Invoke(null, null);
                lock (readLock)
                {
                    while (incoming.Count >= LiveLinkFormat.HeaderBytes)
                    {
                        var head = incoming.Take(LiveLinkFormat.HeaderBytes).ToArray();
                        if (!MixerLinkProtocol.Format.TryReadHeader(head, out byte type, out int tokenLength, out int payloadLength)) { incoming.Clear(); break; }
                        int total = LiveLinkFormat.HeaderBytes + tokenLength + payloadLength;
                        if (incoming.Count < total) break;
                        var payload = incoming.Skip(LiveLinkFormat.HeaderBytes + tokenLength).Take(payloadLength).ToArray();
                        incoming.RemoveRange(0, total);
                        frames.Add(((MixerLinkProtocol.FrameType)type, payload));
                    }
                }
                if (expectTypes.Length > 0 && expectTypes.All(t => frames.Any(f => f.Item1 == t))) break;
                System.Threading.Thread.Sleep(20);
            }
            return frames;
        }
    }
}
