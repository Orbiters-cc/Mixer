using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.LiveLink;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The live link to Blender: one server per editor, found by the extension through an endpoint file. On connect
    /// Unity sends the scene graph and the meshes; afterwards it streams what changes (transforms, hierarchy, Mixer
    /// strokes) and applies what Blender sends back (edited meshes, weights, transforms) through the Mixer session,
    /// so Blender's edits show at once, undo like strokes and are committed like them. Each mesh has an owner side;
    /// the other side only previews it.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerLinkService
    {
        private const string TokenKey = "Orbiters.Mixer.Link.Token";
        private const string PortKey = "Orbiters.Mixer.Link.Port";
        private const string EnabledPref = "Orbiters.Mixer.Link.Enabled";

        private static LiveLinkServer server;
        private static string token;
        private static SceneGraph lastGraph;
        private static double graphDueAt;
        private static bool graphDirty;
        private static readonly Dictionary<string, double> positionsDue = new Dictionary<string, double>();
        private static readonly HashSet<string> blenderOwned = new HashSet<string>();
        private static readonly List<string> log = new List<string>();
        private static MixerLinkProtocol.Hello peer;
        private static int connection = -1;
        private static double lastEndpointWrite;

        public static event Action Changed;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, true);
            set { EditorPrefs.SetBool(EnabledPref, value); if (value) EnsureRunning(); else Stop(); Changed?.Invoke(); }
        }

        public static bool Running => server != null;
        public static bool Connected => server != null && connection >= 0;
        public static int Port => server != null ? server.Port : 0;
        public static MixerLinkProtocol.Hello Peer => peer;
        public static IReadOnlyList<string> Log => log;
        /// <summary>Mesh ids (renderer GlobalObjectIds) Blender is editing now.</summary>
        public static IReadOnlyCollection<string> BlenderOwned => blenderOwned;

        static MixerLinkService()
        {
            EditorApplication.update += Tick;
            EditorApplication.hierarchyChanged += () => MarkGraphDirty(0.25);
            ObjectChangeEvents.changesPublished += OnChanges;
            EditorSceneManager.activeSceneChangedInEditMode += (a, b) => { lastGraph = null; MarkGraphDirty(0.1); };
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += () => { server?.Dispose(); server = null; };
            MixerSession.Changed += () => MarkGraphDirty(0.3);
            if (Enabled) EditorApplication.delayCall += EnsureRunning;
        }

        public static void EnsureRunning()
        {
            if (server != null || !Enabled) return;
            token = SessionState.GetString(TokenKey, null);
            if (string.IsNullOrEmpty(token))
            {
                var bytes = new byte[24];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                SessionState.SetString(TokenKey, token);
            }
            try
            {
                server = new LiveLinkServer(MixerLinkProtocol.Format, token, SessionState.GetInt(PortKey, 0), singleClient: true, accept: Accept, collapseKey: CollapseKey, closingType: (byte)MixerLinkProtocol.FrameType.Bye, threadName: "Mixer Blender link");
                SessionState.SetInt(PortKey, server.Port);
                server.Received += OnReceived;
                server.ConnectionChanged += OnConnection;
                WriteEndpoint();
                Say("Listening on port " + server.Port + ".");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mixer] The Blender link could not listen: " + e.Message);
                server = null;
            }
            Changed?.Invoke();
        }

        public static void Stop()
        {
            if (server == null) return;
            server.Dispose();
            server = null;
            connection = -1;
            peer = null;
            LiveLinkEndpointFile.Delete(MixerLinkProtocol.EndpointPath);
            Changed?.Invoke();
        }

        private static void Say(string text)
        {
            log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            while (log.Count > 60) log.RemoveAt(0);
            Changed?.Invoke();
        }

        // ---- Endpoint ----------------------------------------------------------------------------------------------

        public static MixerLinkSettings SettingsFor(Scene scene, bool create)
        {
            string guid = AssetDatabase.AssetPathToGUID(scene.path);
            if (string.IsNullOrEmpty(guid)) guid = "untitled";
            string folder = "Assets/Orbiters/Mixer/Link";
            string path = folder + "/" + guid + ".asset";
            var settings = AssetDatabase.LoadAssetAtPath<MixerLinkSettings>(path);
            if (settings == null && create)
            {
                MeshAssets.EnsureFolder(folder);
                settings = ScriptableObject.CreateInstance<MixerLinkSettings>();
                settings.name = scene.name + " link";
                AssetDatabase.CreateAsset(settings, path);
            }
            return settings;
        }

        private static void WriteEndpoint()
        {
            if (server == null) return;
            var scene = SceneManager.GetActiveScene();
            var settings = SettingsFor(scene, false);
            var endpoint = new MixerLinkProtocol.Endpoint
            {
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                port = server.Port,
                token = token,
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                sceneGuid = AssetDatabase.AssetPathToGUID(scene.path),
                sceneName = scene.name,
                blendPath = settings != null ? settings.blendPath : null,
                updatedUtc = DateTime.UtcNow.ToString("o"),
            };
            LiveLinkEndpointFile.Write(MixerLinkProtocol.EndpointPath, endpoint);
            lastEndpointWrite = EditorApplication.timeSinceStartup;
        }

        // ---- Frames in ----------------------------------------------------------------------------------------------

        private static bool Accept(LiveLinkFrame raw)
        {
            var type = (MixerLinkProtocol.FrameType)raw.Type;
            var frame = new MixerLinkFrame { Type = type, Payload = raw.Payload };
            if (type == MixerLinkProtocol.FrameType.MeshData || type == MixerLinkProtocol.FrameType.Positions || type == MixerLinkProtocol.FrameType.Weights)
                frame.MeshId = MeshDataCodec.PeekId(raw.Payload);
            raw.Decoded = frame;
            return true;
        }

        private static string CollapseKey(LiveLinkFrame raw) =>
            raw.Decoded is MixerLinkFrame frame && frame.Type == MixerLinkProtocol.FrameType.Positions && frame.MeshId != null ? "pos:" + frame.MeshId : null;

        private static void OnConnection(int id, bool up)
        {
            if (up) { connection = id; Say("Blender connected."); }
            else if (id == connection)
            {
                connection = -1; peer = null;
                foreach (string owned in blenderOwned.ToList()) ReleaseFromBlender(owned);
                Say("Blender left.");
            }
            Changed?.Invoke();
        }

        private static void OnReceived(LiveLinkFrame raw)
        {
            if (!(raw.Decoded is MixerLinkFrame frame)) return;
            try
            {
                switch (frame.Type)
                {
                    case MixerLinkProtocol.FrameType.Hello: OnHello(MixerLinkProtocol.Read<MixerLinkProtocol.Hello>(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Claim: OnClaim(MixerLinkProtocol.Read<MixerLinkProtocol.Ownership>(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Release: OnRelease(MixerLinkProtocol.Read<MixerLinkProtocol.Ownership>(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Positions: LinkApply.Positions(MeshDataCodec.DecodePositions(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.MeshData: LinkApply.Mesh(MeshDataCodec.Decode(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Weights: LinkApply.Weights(MeshDataCodec.DecodeWeights(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.SceneDelta: LinkApply.Delta(MixerLinkProtocol.Read<SceneDelta>(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Status: { var s = MixerLinkProtocol.Read<MixerLinkProtocol.StatusMessage>(frame.Payload); Say("Blender: " + s.message); if (s.level == "error") MixerSession.Say(s.message, true); break; }
                    case MixerLinkProtocol.FrameType.RequestMesh: { var r = MixerLinkProtocol.Read<MixerLinkProtocol.RequestMesh>(frame.Payload); SendMesh(r.id); break; }
                    case MixerLinkProtocol.FrameType.RenderResult: MixerRenderer.OnResult(MixerLinkProtocol.Read<MixerLinkProtocol.RenderResult>(frame.Payload)); break;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Say("A " + frame.Type + " frame failed: " + e.Message);
            }
        }

        private static void OnHello(MixerLinkProtocol.Hello hello)
        {
            peer = hello;
            Say("Blender " + hello.blender + ", extension " + hello.extension + (string.IsNullOrEmpty(hello.blendPath) ? "" : " (" + Path.GetFileName(hello.blendPath) + ")"));
            var scene = SceneManager.GetActiveScene();
            Send(MixerLinkProtocol.FrameType.Welcome, MixerLinkProtocol.Json(new MixerLinkProtocol.Welcome
            {
                unity = Application.unityVersion,
                mixer = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MixerLinkService).Assembly)?.version,
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                sceneGuid = AssetDatabase.AssetPathToGUID(scene.path),
                sceneName = scene.name,
            }));
            lastGraph = null;
            SendGraph(force: true);
            SendAllMeshes();
            Changed?.Invoke();
        }

        private static void OnClaim(MixerLinkProtocol.Ownership claim)
        {
            if (claim == null || string.IsNullOrEmpty(claim.id)) return;
            var renderer = SceneGraphExporter.Resolve(claim.id) as GameObject;
            var r = renderer != null ? renderer.GetComponent<Renderer>() : null;
            if (r == null) { Status(claim.id, "Unity has no renderer for this object.", "error"); return; }
            if (!MixerSession.CanEdit(r, out string reason)) { Status(claim.id, reason, "error"); return; }
            try
            {
                var target = MixerSession.Edit(r);
                if (MixerSceneTool.Stroking && MixerSceneTool.FocusTarget == target) { Status(claim.id, "Unity is editing this mesh.", "error"); return; }
                blenderOwned.Add(claim.id);
                Status(claim.id, "Blender edits " + r.name + ".", "info");
                Say("Blender edits " + r.name + ".");
            }
            catch (Exception e) { Status(claim.id, e.Message, "error"); }
            Changed?.Invoke();
        }

        private static void OnRelease(MixerLinkProtocol.Ownership release)
        {
            if (release == null) return;
            ReleaseFromBlender(release.id);
        }

        private static void ReleaseFromBlender(string id)
        {
            if (!blenderOwned.Remove(id)) return;
            LinkApply.EndBlenderEdit(id);
            Changed?.Invoke();
        }

        public static bool IsBlenderOwned(EditTarget target) => target != null && blenderOwned.Contains(target.Id);

        // ---- Frames out ---------------------------------------------------------------------------------------------

        public static bool Send(MixerLinkProtocol.FrameType type, byte[] payload = null)
        {
            if (server == null || connection < 0) return false;
            return server.Send(connection, (byte)type, payload);
        }

        public static void Status(string id, string message, string level) =>
            Send(MixerLinkProtocol.FrameType.Status, MixerLinkProtocol.Json(new MixerLinkProtocol.StatusMessage { id = id, message = message, level = level }));

        private static void MarkGraphDirty(double delay)
        {
            graphDirty = true;
            graphDueAt = Math.Max(graphDueAt, EditorApplication.timeSinceStartup + delay);
        }

        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            if (!Connected) return;
            for (int i = 0; i < stream.length; i++)
            {
                var kind = stream.GetEventType(i);
                if (kind == ObjectChangeKind.ChangeGameObjectOrComponentProperties || kind == ObjectChangeKind.ChangeGameObjectParent || kind == ObjectChangeKind.CreateGameObjectHierarchy ||
                    kind == ObjectChangeKind.DestroyGameObjectHierarchy || kind == ObjectChangeKind.ChangeGameObjectStructure || kind == ObjectChangeKind.ChangeGameObjectStructureHierarchy ||
                    kind == ObjectChangeKind.UpdatePrefabInstances || kind == ObjectChangeKind.ChangeScene)
                { MarkGraphDirty(0.12); return; }
            }
        }

        /// <summary>The exclusions changed: the hierarchy and the popup refresh.</summary>
        public static void NoteSettingsChanged() => Changed?.Invoke();

        /// <summary>Sends the whole graph, or the delta since the last one.</summary>
        public static void SendGraph(bool force)
        {
            if (!Connected) return;
            var scene = SceneManager.GetActiveScene();
            var graph = SceneGraphExporter.Build(scene, SettingsFor(scene, false));
            if (force || lastGraph == null || lastGraph.sceneGuid != graph.sceneGuid)
            {
                Send(MixerLinkProtocol.FrameType.SceneGraph, MixerLinkProtocol.Json(graph));
            }
            else
            {
                var delta = SceneGraphExporter.Diff(lastGraph, graph);
                if (!delta.Empty)
                {
                    Send(MixerLinkProtocol.FrameType.SceneDelta, MixerLinkProtocol.Json(delta));
                    foreach (var node in delta.added) if (node.meshId != null) SendMesh(node.id);
                }
            }
            lastGraph = graph;
            graphDirty = false;
        }

        private static readonly Queue<string> meshQueue = new Queue<string>();
        private static readonly HashSet<string> queuedMeshes = new HashSet<string>();
        private static System.Threading.Tasks.Task encoding;

        private static void SendAllMeshes()
        {
            if (lastGraph == null) return;
            var sent = new HashSet<string>();
            foreach (var node in lastGraph.nodes)
            {
                if (node.meshId == null || !sent.Add(node.meshId)) continue;
                SendMesh(node.id);
            }
        }

        /// <summary>Queues the mesh of a node; one leaves per editor tick, welded and encoded on a thread of its own.</summary>
        public static void SendMesh(string nodeId)
        {
            if (!Connected || string.IsNullOrEmpty(nodeId)) return;
            if (queuedMeshes.Add(nodeId)) meshQueue.Enqueue(nodeId);
        }

        public static int MeshesWaiting => meshQueue.Count + (encoding != null && !encoding.IsCompleted ? 1 : 0);

        // The main thread only reads the mesh's arrays (milliseconds); welding and writing the bytes happen on a dedicated thread.
        private static void SendNextMesh()
        {
            if (encoding != null && !encoding.IsCompleted) return;
            if (meshQueue.Count == 0) return;
            string nodeId = meshQueue.Dequeue();
            queuedMeshes.Remove(nodeId);
            if (!Connected) return;
            var go = SceneGraphExporter.Resolve(nodeId) as GameObject;
            var renderer = go != null ? go.GetComponent<Renderer>() : null;
            if (renderer == null) return;
            var target = MixerSession.TargetOf(renderer);
            string meshId = MeshIdOf(renderer);
            try
            {
                if (target != null)
                {
                    Send(MixerLinkProtocol.FrameType.MeshData, MeshDataCodec.Encode(meshId, target));
                    return;
                }
                var mesh = renderer is SkinnedMeshRenderer s ? s.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) return;
                if (!mesh.isReadable) { Status(nodeId, mesh.name + " is not readable: enable Read/Write on its import settings.", "warning"); return; }
                var snapshot = MeshDataCodec.Snapshot(meshId, mesh, renderer as SkinnedMeshRenderer);
                var connectionServer = server;
                int connectionId = connection;
                encoding = DedicatedTask.Run(() =>
                {
                    var payload = MeshDataCodec.Encode(snapshot);
                    if (connectionServer == server && connectionId == connection && connection >= 0) connectionServer.Send(connectionId, (byte)MixerLinkProtocol.FrameType.MeshData, payload);
                });
            }
            catch (Exception e) { Say("Mesh of " + renderer.name + " not sent: " + e.Message); }
        }

        /// <summary>The id a mesh travels under: its asset's GlobalObjectId (shared by every renderer showing it).</summary>
        public static string MeshIdOf(Renderer renderer)
        {
            var shown = renderer is SkinnedMeshRenderer s ? s.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var mesh = MixerMeshGuard.OriginalOf(shown) ?? shown;
            return mesh != null ? SceneGraphExporter.IdOf(mesh) : null;
        }

        /// <summary>A Mixer stroke moved a target: Blender gets the new positions soon (collapsed, at most every 100 ms).</summary>
        public static void NotePositions(EditTarget target)
        {
            if (!Connected || target == null) return;
            positionsDue[target.Id] = Math.Min(positionsDue.TryGetValue(target.Id, out double due) ? due : double.MaxValue, EditorApplication.timeSinceStartup + 0.1);
        }

        /// <summary>A Mixer stroke changed weights: Blender gets the sparse change now.</summary>
        public static void SendWeights(EditTarget target, WeightChannel channel, IReadOnlyList<int> vertices)
        {
            if (!Connected || target == null || channel.IsNone) return;
            var doc = target.Document;
            var n = doc.Neighbors;
            string name = channel.IsGroup ? channel.Group : WeightStroke.BoneName(target, channel.Bone);
            var indices = new List<int>(); var weights = new List<float>();
            var seen = new HashSet<int>();
            foreach (int v in vertices)
            {
                int w = n.Weld[v];
                if (!seen.Add(w)) continue;
                indices.Add(w);
                int rep = n.Representative[w];
                weights.Add(channel.IsGroup ? (doc.Group(channel.Group)?.Weights.TryGetValue(rep, out float gw) == true ? gw : 0f) : doc.BoneWeight(rep, channel.Bone));
            }
            Send(MixerLinkProtocol.FrameType.Weights, MeshDataCodec.EncodeWeights(MeshIdOf(target.Renderer), name, channel.IsGroup, indices, weights));
        }

        /// <summary>Unity starts editing a target: Blender shows it as taken.</summary>
        public static void ClaimForUnity(EditTarget target)
        {
            if (!Connected || target == null) return;
            Send(MixerLinkProtocol.FrameType.Claim, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = target.Id, side = "unity" }));
        }

        public static void ReleaseForUnity(EditTarget target)
        {
            if (!Connected || target == null) return;
            Send(MixerLinkProtocol.FrameType.Release, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = target.Id, side = "unity" }));
        }

        private static void Tick()
        {
            if (server == null) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - lastEndpointWrite > 10.0) WriteEndpoint();
            if (!Connected) return;
            SendNextMesh();
            if (graphDirty && now >= graphDueAt) SendGraph(force: false);
            if (positionsDue.Count > 0)
            {
                foreach (var pair in positionsDue.Where(p => p.Value <= now).ToList())
                {
                    positionsDue.Remove(pair.Key);
                    var target = MixerSession.Targets.FirstOrDefault(t => t.Id == pair.Key);
                    if (target == null || !target.Alive) continue;
                    var n = target.Document.Neighbors;
                    var positions = new Vector3[n.WeldedCount];
                    for (int w = 0; w < positions.Length; w++) positions[w] = target.Document.LayerPosition(n.Representative[w]);
                    Send(MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(MeshIdOf(target.Renderer), target.Document.Layer, positions));
                }
            }
        }
    }
}
