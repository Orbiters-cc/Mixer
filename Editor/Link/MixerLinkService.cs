using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
    /// The live link to Blender: one server per editor (<see cref="MixerLinkHost"/>), found by the extension through an
    /// endpoint file. On connect Unity sends the scene graph and the meshes; afterwards it streams what changes
    /// (transforms, hierarchy, Mixer strokes) and applies what Blender sends back (edited meshes, weights, transforms)
    /// through the Mixer session, so Blender's edits show at once, undo like strokes and are committed like them.
    /// <para>
    /// Each mesh has one editing side at a time: Blender claims a mesh while one of its objects is in an edit mode, and
    /// Unity applies nothing of Blender's for a mesh it has not let Blender claim; Unity holds a mesh while a Mixer
    /// stroke runs on it, and Blender takes no claim then. A session in Mixer alone does not hold the mesh, so both sides
    /// can take turns on the same document.
    /// </para>
    /// The link does not listen in Play mode: Blender keeps the edit-mode scene and waits for Play to end.
    /// Tests open hosts of their own (<see cref="MixerLinkHost.OpenPrivate"/>): no endpoint file, another token and
    /// port, and the user's Blender link untouched.
    /// </summary>
    [InitializeOnLoad]
    internal static class MixerLinkService
    {
        private const string TokenKey = "Orbiters.Mixer.Link.Token";
        private const string PortKey = "Orbiters.Mixer.Link.Port";
        private const string EnabledPref = "Orbiters.Mixer.Link.Enabled";
        private const double RetrySeconds = 10.0;

        private static MixerLinkHost live;
        private static readonly List<MixerLinkHost> hosts = new List<MixerLinkHost>();
        private static readonly List<string> log = new List<string>();
        private static double nextStart;
        private static readonly ConditionalWeakTable<EditTarget, string> targetMeshIds = new ConditionalWeakTable<EditTarget, string>();

        public static event Action Changed;
        /// <summary>A Status frame Blender sent (the Mixer extension's Export for MCB reports its manifest this way).</summary>
        internal static event Action<MixerLinkProtocol.StatusMessage> StatusReceived;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, true);
            set { EditorPrefs.SetBool(EnabledPref, value); if (value) EnsureRunning(); else Stop(); RaiseChanged(); }
        }

        public static bool Running => live != null;
        /// <summary>A Blender of this link protocol is connected (its Hello was accepted).</summary>
        public static bool Connected => live != null && live.Connected;
        public static int Port => live != null ? live.Port : 0;
        public static MixerLinkProtocol.Hello Peer => live?.Peer;
        public static IReadOnlyList<string> Log => log;
        /// <summary>Node ids (renderer GameObjects' GlobalObjectIds) of the renderers whose mesh Blender is editing now.</summary>
        public static IReadOnlyCollection<string> BlenderOwned => live != null ? live.BlenderOwnedNodes : (IReadOnlyCollection<string>)Array.Empty<string>();
        public static int MeshesWaiting => live != null ? live.MeshesWaiting : 0;

        static MixerLinkService()
        {
            EditorApplication.update += Tick;
            EditorApplication.hierarchyChanged += () => ForEach(h => h.MarkGraphDirty(0.25));
            ObjectChangeEvents.changesPublished += OnChanges;
            EditorSceneManager.activeSceneChangedInEditMode += (a, b) => ForEach(h => h.SceneSwitched());
            EditorApplication.quitting += Stop;
            // The endpoint file stays: after the reload the link listens again on the same port with the same token.
            // Blender's edit in progress is not turned into history here (the history does not survive the reload): its
            // positions so far are in the session, which the reload keeps.
            AssemblyReloadEvents.beforeAssemblyReload += () => { live?.Dispose(deleteEndpoint: false, endEdits: false); live = null; };
            EditorApplication.playModeStateChanged += OnPlayMode;
            MixerSession.Changed += () => ForEach(h => h.MarkGraphDirty(0.3));
            if (Enabled) EditorApplication.delayCall += EnsureRunning;
        }

        // Play mode is not mirrored (Blender would get the play-mode scene and lose what Play destroys): the link lets
        // Blender go when Play starts, as for a script reload (Blender keeps its edits and claims them again), and listens
        // again when Play ends.
        private static void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode && live != null)
            {
                var host = live;
                live = null;
                host.Status("link", "Unity entered Play mode: the link is back when Play ends.", "info");
                host.Dispose(deleteEndpoint: false, endEdits: false);
                RaiseChanged();
            }
            else if (change == PlayModeStateChange.EnteredEditMode && Enabled) EnsureRunning();
        }

        private static void ForEach(Action<MixerLinkHost> action)
        {
            for (int i = hosts.Count - 1; i >= 0; i--)
                if (i < hosts.Count) action(hosts[i]);
        }

        internal static void Register(MixerLinkHost host) { if (!hosts.Contains(host)) hosts.Add(host); }
        internal static void Unregister(MixerLinkHost host) => hosts.Remove(host);

        public static void EnsureRunning()
        {
            if (live != null || !Enabled || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string token = SessionState.GetString(TokenKey, null);
            if (string.IsNullOrEmpty(token))
            {
                token = MixerLinkHost.NewToken();
                SessionState.SetString(TokenKey, token);
            }
            try
            {
                live = new MixerLinkHost(isLive: true, SceneManager.GetActiveScene, token, SessionState.GetInt(PortKey, 0), writeEndpoint: true, log);
                SessionState.SetInt(PortKey, live.Port);
                Register(live);
                nextStart = 0;
                live.Say("Listening on port " + live.Port + ".");
            }
            catch (Exception e)
            {
                // The host let go of whatever it had opened; the link tries again in a while.
                Debug.LogWarning("[Mixer] The Blender link could not listen: " + e.Message + " (it tries again in " + RetrySeconds.ToString("0") + " s).");
                live = null;
                nextStart = EditorApplication.timeSinceStartup + RetrySeconds;
            }
            RaiseChanged();
        }

        public static void Stop()
        {
            nextStart = 0;
            if (live == null) return;
            var host = live;
            live = null;
            host.Dispose(deleteEndpoint: true);
            RaiseChanged();
        }

        internal static void RaiseChanged() => Changed?.Invoke();
        internal static void RaiseStatus(MixerLinkProtocol.StatusMessage status) => StatusReceived?.Invoke(status);

        private static void Tick()
        {
            if (live == null && Enabled && nextStart > 0 && EditorApplication.timeSinceStartup >= nextStart)
            {
                nextStart = 0;
                EnsureRunning();
            }
            ForEach(h => h.Tick());
        }

        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            if (hosts.Count == 0) return;
            for (int i = 0; i < stream.length; i++)
            {
                var kind = stream.GetEventType(i);
                if (kind == ObjectChangeKind.ChangeGameObjectOrComponentProperties || kind == ObjectChangeKind.ChangeGameObjectParent || kind == ObjectChangeKind.CreateGameObjectHierarchy ||
                    kind == ObjectChangeKind.DestroyGameObjectHierarchy || kind == ObjectChangeKind.ChangeGameObjectStructure || kind == ObjectChangeKind.ChangeGameObjectStructureHierarchy ||
                    kind == ObjectChangeKind.UpdatePrefabInstances || kind == ObjectChangeKind.ChangeScene)
                {
                    ForEach(h => h.MarkGraphDirty(0.12));
                    return;
                }
            }
        }

        // ---- Settings ------------------------------------------------------------------------------------------------

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

        // ---- Ids -----------------------------------------------------------------------------------------------------

        /// <summary>The id a mesh travels under: its asset's GlobalObjectId (shared by every renderer showing it).</summary>
        public static string MeshIdOf(Renderer renderer)
        {
            if (renderer == null) return null;
            var shown = renderer is SkinnedMeshRenderer s ? s.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var mesh = MixerMeshGuard.OriginalOf(shown) ?? shown;
            return mesh != null ? SceneGraphExporter.IdOf(mesh) : null;
        }

        /// <summary>The id of the mesh a session edits (its original's), kept for the session's life.</summary>
        public static string MeshIdOf(EditTarget target)
        {
            if (target == null || target.Original == null) return null;
            return targetMeshIds.GetValue(target, t => SceneGraphExporter.IdOf(MixerMeshGuard.OriginalOf(t.Original) ?? t.Original));
        }

        // ---- What the rest of Mixer tells the link --------------------------------------------------------------------

        public static bool Send(MixerLinkProtocol.FrameType type, byte[] payload = null) => live != null && live.Send(type, payload);

        public static void Status(string id, string message, string level) => live?.Status(id, message, level);

        /// <summary>Sends the whole graph, or the delta since the last one.</summary>
        public static void SendGraph(bool force) => live?.SendGraph(force);

        /// <summary>Queues the mesh of a node for Blender.</summary>
        public static void SendMesh(string nodeId) => live?.SendMesh(nodeId);

        /// <summary>Bones moved in rest-pose mode or were added: Blender gets the graph again and rebuilds its armatures.</summary>
        public static void NoteArmatureChanged() => ForEach(h => h.MarkGraphDirty(0.4));

        /// <summary>The exclusions changed: the hierarchy and the popup refresh.</summary>
        public static void NoteSettingsChanged() => RaiseChanged();

        /// <summary>A Mixer stroke moved a target: Blender gets the new positions soon (collapsed, at most every 100 ms).</summary>
        public static void NotePositions(EditTarget target) => ForEach(h => h.NotePositions(target));

        /// <summary>A Mixer stroke changed weights: Blender gets the sparse change now.</summary>
        public static void SendWeights(EditTarget target, WeightChannel channel, IReadOnlyList<int> vertices) => ForEach(h => h.SendWeights(target, channel, vertices));

        /// <summary>A Mixer session opened. It does not hold the mesh against Blender (a stroke does, while it runs).</summary>
        public static void ClaimForUnity(EditTarget target) => ForEach(h => h.SessionOpened(target));

        /// <summary>A Mixer session was committed or reverted: Blender gets the mesh Unity shows now, and lets go of its own edit of it.</summary>
        public static void ReleaseForUnity(EditTarget target) => ForEach(h => h.SessionClosed(target));

        /// <summary>
        /// Mixer's history is about to undo or redo edits of these targets: a claim Blender holds on one of their meshes
        /// ends first (what Blender changed under it so far is taken back here; Blender keeps it and says so).
        /// </summary>
        internal static void BeforeHistoryApplied(ICollection<EditTarget> targets) => ForEach(h => h.BeforeHistoryApplied(targets));

        /// <summary>Mixer's history undid or redid these entries: Blender gets the layers and weights they changed.</summary>
        internal static void HistoryApplied(IReadOnlyList<HistoryEntry> entries) => ForEach(h => h.HistoryApplied(entries));

        /// <summary>Blender is editing this session's mesh (one of its objects is in an edit mode).</summary>
        public static bool IsBlenderOwned(EditTarget target)
        {
            if (target == null) return false;
            for (int i = 0; i < hosts.Count; i++) if (hosts[i].IsBlenderOwned(target)) return true;
            return false;
        }
    }

    /// <summary>
    /// One Blender link server and what it knows of its Blender: the connection, the meshes sent and in flight, what
    /// Blender claimed. The editor's own is <see cref="MixerLinkService"/>'s; tests open private ones.
    /// </summary>
    internal sealed class MixerLinkHost : IDisposable
    {
        private sealed class SentMesh
        {
            public string Key;
            public HashSet<string> Shapes = new HashSet<string>();
            public HashSet<string> Groups = new HashSet<string>();
        }

        private sealed class Encoded
        {
            public int Connection;
            public string MeshId, NodeId, Name;
            public byte[] Payload;
            public Exception Error;
            public SentMesh Sent;
        }

        /// <summary>How long Unity waits for Blender to say a mesh arrived before it sends the next one anyway.</summary>
        private const double InFlightSeconds = 60.0;
        /// <summary>Bump when the link's weld changes: Blender's copies keyed with another weld are sent again.</summary>
        private const int WeldVersion = 2;

        public readonly bool IsLive;
        private readonly Func<Scene> sceneOf;
        private readonly bool writesEndpoint;
        private readonly List<string> log;
        internal LiveLinkServer Server { get; private set; }
        public string Token { get; }
        public int Port => Server != null ? Server.Port : 0;
        public MixerLinkProtocol.Hello Peer { get; private set; }
        private int connection = -1;
        private bool handshaken;
        public bool Connected => Server != null && connection >= 0 && handshaken;
        private double lastEndpointWrite;

        // Ownership: mesh id -> the node Blender named in its claim, and Blender's number for that claim; the mesh Unity strokes now.
        private readonly Dictionary<string, string> blenderMeshes = new Dictionary<string, string>();
        private readonly Dictionary<string, int> blenderClaims = new Dictionary<string, int>();
        private string unityEditing, unityEditingNode;
        private readonly Dictionary<string, (string message, double at)> throttled = new Dictionary<string, (string, double)>();

        // The graph Blender has.
        private SceneGraph lastGraph;
        private readonly HashSet<string> graphNodes = new HashSet<string>();
        private double graphDueAt, recheckAt;
        private bool graphDirty, sceneSwitched;

        // Meshes going out: queued by node, encoded (on a thread for assets), one in flight until Blender says it arrived.
        private readonly Queue<string> meshQueue = new Queue<string>();
        private readonly HashSet<string> queuedMeshes = new HashSet<string>();
        private readonly ConcurrentQueue<Encoded> encoded = new ConcurrentQueue<Encoded>();
        private System.Threading.Tasks.Task encoding;
        private string inFlight;
        private double inFlightSince;
        // What Blender had of the mesh in flight before it (null: nothing known), for when Blender drops that mesh.
        private SentMesh inFlightBefore;
        private bool meshesAnnounced;
        private uint nextTransfer = 1;
        private Dictionary<string, string> heldByBlender;
        private readonly Dictionary<string, SentMesh> sent = new Dictionary<string, SentMesh>();
        private readonly Dictionary<string, double> positionsDue = new Dictionary<string, double>();
        private readonly Dictionary<string, int> checkedRevision = new Dictionary<string, int>();

        private readonly MeshChunkAssembler assembler = new MeshChunkAssembler();

        public MixerLinkHost(bool isLive, Func<Scene> sceneOf, string token, int port, bool writeEndpoint, List<string> log)
        {
            IsLive = isLive;
            this.sceneOf = sceneOf ?? throw new ArgumentNullException(nameof(sceneOf));
            Token = token ?? throw new ArgumentNullException(nameof(token));
            writesEndpoint = writeEndpoint;
            this.log = log ?? new List<string>();
            Server = new LiveLinkServer(MixerLinkProtocol.Format, token, port, singleClient: true, accept: Accept, collapseKey: CollapseKey,
                closingType: (byte)MixerLinkProtocol.FrameType.Bye, threadName: isLive ? "Mixer Blender link" : "Mixer Blender link (test)",
                replacedGoodbye: MixerLinkProtocol.Json(new MixerLinkProtocol.Goodbye { replaced = true }));
            try
            {
                Server.Received += OnReceived;
                Server.ConnectionChanged += OnConnection;
                if (writesEndpoint) WriteEndpoint();
            }
            catch
            {
                // Nothing of a half-made host stays: no listener, no pump hook, no handlers holding the token.
                Server.Dispose();
                Server = null;
                throw;
            }
        }

        /// <summary>A host of a test: its own scene, token and port, no endpoint file, ticked with the editor's like the live one.</summary>
        internal static MixerLinkHost OpenPrivate(Scene scene)
        {
            var host = new MixerLinkHost(isLive: false, () => scene, NewToken(), 0, writeEndpoint: false, null);
            MixerLinkService.Register(host);
            return host;
        }

        internal static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public IReadOnlyList<string> Log => log;

        public void Dispose() => Dispose(deleteEndpoint: !IsLive);

        public void Dispose(bool deleteEndpoint, bool endEdits = true)
        {
            MixerLinkService.Unregister(this);
            if (Server == null) return;
            // What Blender edited so far becomes history, as when it leaves.
            if (endEdits) foreach (string mesh in blenderMeshes.Keys.ToList()) ReleaseFromBlender(mesh);
            blenderMeshes.Clear();
            blenderClaims.Clear();
            var server = Server;
            Server = null;
            connection = -1;
            handshaken = false;
            Peer = null;
            server.Dispose();
            if (deleteEndpoint && writesEndpoint) LiveLinkEndpointFile.Delete(MixerLinkProtocol.EndpointPath);
        }

        internal void Say(string text)
        {
            log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            while (log.Count > 60) log.RemoveAt(0);
            if (IsLive) MixerLinkService.RaiseChanged();
        }

        private void RaiseChanged() { if (IsLive) MixerLinkService.RaiseChanged(); }

        // ---- Endpoint ------------------------------------------------------------------------------------------------

        private void WriteEndpoint()
        {
            if (Server == null) return;
            var scene = sceneOf();
            var settings = MixerLinkService.SettingsFor(scene, false);
            var endpoint = new MixerLinkProtocol.Endpoint
            {
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                port = Server.Port,
                token = Token,
                proof = MixerLinkProtocol.ServerProvesToken,
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                sceneGuid = AssetDatabase.AssetPathToGUID(scene.path),
                sceneName = scene.name,
                blendPath = settings != null ? settings.blendPath : null,
                updatedUtc = DateTime.UtcNow.ToString("o"),
            };
            LiveLinkEndpointFile.Write(MixerLinkProtocol.EndpointPath, endpoint);
            lastEndpointWrite = EditorApplication.timeSinceStartup;
        }

        // ---- Frames in -------------------------------------------------------------------------------------------------

        private static bool Accept(LiveLinkFrame raw)
        {
            var type = (MixerLinkProtocol.FrameType)raw.Type;
            var frame = new MixerLinkFrame { Type = type, Payload = raw.Payload };
            if (type == MixerLinkProtocol.FrameType.MeshData || type == MixerLinkProtocol.FrameType.Positions || type == MixerLinkProtocol.FrameType.Weights)
                frame.MeshId = MeshDataCodec.PeekId(raw.Payload);
            if (type == MixerLinkProtocol.FrameType.Positions) frame.Shape = MeshDataCodec.PeekShape(raw.Payload);
            raw.Decoded = frame;
            return true;
        }

        // The newest positions of one shape of one mesh replace older ones waiting: another shape's never do.
        private static string CollapseKey(LiveLinkFrame raw) =>
            raw.Decoded is MixerLinkFrame frame && frame.Type == MixerLinkProtocol.FrameType.Positions && frame.MeshId != null ? "pos:" + frame.MeshId + "\n" + (frame.Shape ?? "") : null;

        private void OnConnection(int id, bool up)
        {
            if (up)
            {
                connection = id;
                handshaken = false;
                Peer = null;
                Say("Blender connected.");
            }
            else if (id == connection)
            {
                connection = -1;
                handshaken = false;
                Peer = null;
                ResetOutgoing();
                foreach (string owned in blenderMeshes.Keys.ToList()) ReleaseFromBlender(owned);
                Say("Blender left.");
            }
            RaiseChanged();
        }

        private void ResetOutgoing()
        {
            meshQueue.Clear();
            queuedMeshes.Clear();
            while (encoded.TryDequeue(out _)) { }
            inFlight = null;
            inFlightBefore = null;
            assembler.Reset();
            positionsDue.Clear();
            unityEditing = unityEditingNode = null;
        }

        private void OnReceived(LiveLinkFrame raw)
        {
            if (!(raw.Decoded is MixerLinkFrame frame)) return;
            // Only the connection that is Blender now counts (a replaced one's frames may still be waiting).
            if (raw.Connection != connection) return;
            if (!handshaken && frame.Type != MixerLinkProtocol.FrameType.Hello) return;
            try
            {
                switch (frame.Type)
                {
                    case MixerLinkProtocol.FrameType.Hello: OnHello(MixerLinkProtocol.Read<MixerLinkProtocol.Hello>(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Claim: OnClaim(MixerLinkProtocol.Read<MixerLinkProtocol.Ownership>(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Release: OnRelease(MixerLinkProtocol.Read<MixerLinkProtocol.Ownership>(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Positions: LinkApply.Positions(this, MeshDataCodec.DecodePositions(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.MeshData: ApplyMesh(frame.Payload); break;
                    case MixerLinkProtocol.FrameType.MeshChunk:
                    {
                        byte[] whole;
                        try { whole = assembler.Add(frame.Payload); }
                        catch (InvalidDataException e) { Say("A mesh from Blender did not arrive whole: " + e.Message); Status("link", "Unity dropped a mesh from Blender: " + e.Message, "error"); break; }
                        if (whole != null) ApplyMesh(whole);
                        break;
                    }
                    case MixerLinkProtocol.FrameType.Weights: LinkApply.Weights(this, MeshDataCodec.DecodeWeights(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.SceneDelta: LinkApply.Delta(MixerLinkProtocol.Read<SceneDelta>(frame.Payload)); break;
                    case MixerLinkProtocol.FrameType.Status:
                    {
                        var s = MixerLinkProtocol.Read<MixerLinkProtocol.StatusMessage>(frame.Payload);
                        if (s == null) break;
                        Say("Blender: " + s.message);
                        if (s.level == "error") MixerSession.Say(s.message, true);
                        if (IsLive) MixerLinkService.RaiseStatus(s);
                        break;
                    }
                    case MixerLinkProtocol.FrameType.RequestMesh: { var r = MixerLinkProtocol.Read<MixerLinkProtocol.RequestMesh>(frame.Payload); if (r != null) SendMesh(r.id); break; }
                    case MixerLinkProtocol.FrameType.MeshReceived: { var r = MixerLinkProtocol.Read<MixerLinkProtocol.MeshReceived>(frame.Payload); if (r != null && r.id == inFlight) { inFlight = null; inFlightBefore = null; } break; }
                    case MixerLinkProtocol.FrameType.RenderResult: if (IsLive) MixerRenderer.OnResult(MixerLinkProtocol.Read<MixerLinkProtocol.RenderResult>(frame.Payload)); break;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Say("A " + frame.Type + " frame failed: " + e.Message);
                Status("link", "Unity could not apply a " + frame.Type + " frame: " + e.Message, "error");
            }
        }

        private void ApplyMesh(byte[] payload)
        {
            var data = MeshDataCodec.Decode(payload);
            LinkApply.Mesh(this, data);
        }

        private void OnHello(MixerLinkProtocol.Hello hello)
        {
            if (hello == null) return;
            Peer = hello;
            var scene = sceneOf();
            var welcome = new MixerLinkProtocol.Welcome
            {
                unity = Application.unityVersion,
                mixer = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MixerLinkService).Assembly)?.version,
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                sceneGuid = AssetDatabase.AssetPathToGUID(scene.path),
                sceneName = scene.name,
            };
            if (hello.protocolVersion != MixerLinkProtocol.ProtocolVersion)
            {
                // Told once, then left alone: the connection stays open (so the extension does not reconnect in a loop)
                // and nothing else is sent or applied.
                handshaken = false;
                welcome.error = "Blender's Mixer extension " + (hello.extension ?? "?") + " speaks link protocol " + hello.protocolVersion + ", Unity's Mixer " + MixerLinkProtocol.ProtocolVersion
                    + ": open the scene in Blender from Mixer to update the extension.";
                Send(MixerLinkProtocol.FrameType.Welcome, MixerLinkProtocol.Json(welcome));
                Send(MixerLinkProtocol.FrameType.Status, MixerLinkProtocol.Json(new MixerLinkProtocol.StatusMessage { id = "link", message = welcome.error, level = "error" }));
                Say(welcome.error);
                if (IsLive) MixerSession.Say(welcome.error, true);
                RaiseChanged();
                return;
            }
            handshaken = true;
            Say("Blender " + hello.blender + ", extension " + hello.extension + (string.IsNullOrEmpty(hello.blendPath) ? "" : " (" + Path.GetFileName(hello.blendPath) + ")"));
            Send(MixerLinkProtocol.FrameType.Welcome, MixerLinkProtocol.Json(welcome));
            heldByBlender = hello.meshes;
            lastGraph = null;
            SendGraph(force: true);
            meshesAnnounced = false;
            SendAllMeshes();
            RaiseChanged();
        }

        // ---- Ownership -------------------------------------------------------------------------------------------------

        private bool UnityStrokes(string meshId)
        {
            var focus = MixerSceneTool.Stroking ? MixerSceneTool.FocusTarget : null;
            return focus != null && focus.Alive && MixerLinkService.MeshIdOf(focus) == meshId;
        }

        private void OnClaim(MixerLinkProtocol.Ownership claim)
        {
            if (claim == null || string.IsNullOrEmpty(claim.id) || (claim.side != null && claim.side != "blender")) return;
            if (!string.IsNullOrEmpty(claim.meshId)) ForgetInFlight(claim.meshId);
            var go = SceneGraphExporter.Resolve(claim.id) as GameObject;
            var r = go != null ? go.GetComponent<Renderer>() : null;
            if (r == null) { Refuse(claim, "Unity has no renderer for this object."); return; }
            string meshId = MixerLinkService.MeshIdOf(r);
            if (meshId == null) { Refuse(claim, r.name + " has no mesh in Unity."); return; }
            if (!string.IsNullOrEmpty(claim.meshId) && claim.meshId != meshId)
            {
                Refuse(claim, r.name + " shows another mesh in Unity now (committed or replaced): Blender gets it again. Enter the mode again once it arrived.");
                SendMesh(claim.id);
                return;
            }
            if (blenderMeshes.ContainsKey(meshId)) { blenderMeshes[meshId] = claim.id; blenderClaims[meshId] = claim.claim; RaiseChanged(); return; }
            if (meshId == unityEditing || UnityStrokes(meshId)) { Refuse(claim, "Unity is editing " + r.name + " right now: leave the mode in Blender and enter it again."); return; }
            if (!MixerSession.CanEdit(r, out string reason)) { Refuse(claim, reason); return; }
            // Held before the session opens: the session's own notices then know Blender edits it.
            blenderMeshes[meshId] = claim.id;
            blenderClaims[meshId] = claim.claim;
            try { MixerSession.Edit(r); }
            catch (Exception e)
            {
                blenderMeshes.Remove(meshId);
                blenderClaims.Remove(meshId);
                Refuse(claim, e.Message);
                return;
            }
            positionsDue.Remove(claim.id);
            Status(claim.id, "Blender edits " + r.name + ".", "info");
            Say("Blender edits " + r.name + ".");
            RaiseChanged();
        }

        // Blender claimed the mesh before the copy in flight reached it (frames come in order, and Blender had not said
        // it arrived): Blender drops that copy, its own being the newer, so what it has is what it had before.
        private void ForgetInFlight(string meshId)
        {
            if (inFlight != meshId) return;
            if (inFlightBefore != null) sent[meshId] = inFlightBefore; else sent.Remove(meshId);
            inFlightBefore = null;
        }

        // Blender's claim does not hold: Blender lets go of it (what it changes in that mode stays in Blender, which offers to
        // send it or take Unity's) and says why.
        private void Refuse(MixerLinkProtocol.Ownership claim, string reason)
        {
            Send(MixerLinkProtocol.FrameType.Release, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = claim.id, meshId = claim.meshId, side = "blender", claim = claim.claim, reason = reason }));
            Say("Blender's edit refused: " + reason);
        }

        /// <summary>
        /// Ends a claim Blender holds, from Unity's side: what Blender changed under it so far becomes history (or, with
        /// <paramref name="takeBack"/>, is taken back without history), and Blender is told why. Blender keeps its copy
        /// as it is until the user sends it to Unity or takes Unity's.
        /// </summary>
        internal void EndClaim(string meshId, string reason, bool takeBack)
        {
            if (meshId == null || !blenderMeshes.TryGetValue(meshId, out string node)) return;
            if (takeBack) LinkApply.RevertBlenderEdit(meshId); else LinkApply.EndBlenderEdit(meshId);
            blenderMeshes.Remove(meshId);
            blenderClaims.TryGetValue(meshId, out int number);
            blenderClaims.Remove(meshId);
            Send(MixerLinkProtocol.FrameType.Release, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = node, meshId = meshId, side = "blender", claim = number, reason = reason }));
            Say("Blender's edit ended: " + reason);
            RaiseChanged();
        }

        private void OnRelease(MixerLinkProtocol.Ownership release)
        {
            if (release == null || (release.side != null && release.side != "blender")) return;
            string meshId = release.meshId;
            if (string.IsNullOrEmpty(meshId))
            {
                var go = SceneGraphExporter.Resolve(release.id) as GameObject;
                meshId = go != null ? MixerLinkService.MeshIdOf(go.GetComponent<Renderer>()) : null;
            }
            if (meshId != null) ReleaseFromBlender(meshId);
        }

        private void ReleaseFromBlender(string meshId)
        {
            if (!blenderMeshes.ContainsKey(meshId)) return;
            // Still held while the history is written, so nothing of it echoes back to Blender.
            LinkApply.EndBlenderEdit(meshId);
            blenderMeshes.Remove(meshId);
            blenderClaims.Remove(meshId);
            RaiseChanged();
        }

        public bool OwnsMesh(string meshId) => meshId != null && blenderMeshes.ContainsKey(meshId);

        public bool IsBlenderOwned(EditTarget target) => target != null && blenderMeshes.Count > 0 && OwnsMesh(MixerLinkService.MeshIdOf(target));

        public IReadOnlyCollection<string> BlenderOwnedNodes
        {
            get
            {
                var nodes = new HashSet<string>(blenderMeshes.Values);
                if (blenderMeshes.Count > 0)
                    foreach (var t in MixerSession.Targets)
                        if (t.Alive && OwnsMesh(MixerLinkService.MeshIdOf(t))) nodes.Add(t.Id);
                return nodes;
            }
        }

        public void SessionOpened(EditTarget target) { }

        public void SessionClosed(EditTarget target)
        {
            if (target == null) return;
            string meshId = MixerLinkService.MeshIdOf(target);
            if (meshId != null && blenderMeshes.TryGetValue(meshId, out string node))
            {
                // Unity's commit or revert wins: Blender is told that what it edits in this mode is not taken any more. It
                // keeps its copy (Unity's mesh does not overwrite it) until the user sends it again or takes Unity's.
                LinkApply.DropBlenderEdit(meshId);
                blenderMeshes.Remove(meshId);
                blenderClaims.TryGetValue(meshId, out int number);
                blenderClaims.Remove(meshId);
                Send(MixerLinkProtocol.FrameType.Release, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership
                {
                    id = node, meshId = meshId, side = "blender", claim = number,
                    reason = target.Name + " was committed or reverted in Unity: what you change in this mode stays in Blender. Send it to Unity again, or take Unity's, from the Mixer panel.",
                }));
                RaiseChanged();
            }
            positionsDue.Remove(target.Id);
            checkedRevision.Remove(target.Id);
            // A revert (or a commit into the same asset) leaves the mesh id as it was: Blender gets that mesh again. A commit
            // into a new asset changes the node's mesh, which the graph's delta sends.
            if (Connected && graphNodes.Contains(target.Id) && target.Renderer != null && MixerLinkService.MeshIdOf(target.Renderer) == meshId) SendMesh(target.Id);
        }

        // Unity holds a mesh while a Mixer stroke runs on it: Blender shows it as taken and does not claim it meanwhile.
        private void UpdateUnityClaim()
        {
            var focus = MixerSceneTool.Stroking ? MixerSceneTool.FocusTarget : null;
            string meshId = focus != null && focus.Alive && graphNodes.Contains(focus.Id) && !IsBlenderOwned(focus) ? MixerLinkService.MeshIdOf(focus) : null;
            if (meshId == unityEditing) return;
            if (unityEditing != null)
                Send(MixerLinkProtocol.FrameType.Release, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = unityEditingNode, meshId = unityEditing, side = "unity" }));
            unityEditing = meshId;
            unityEditingNode = meshId != null ? focus.Id : null;
            if (meshId != null)
                Send(MixerLinkProtocol.FrameType.Claim, MixerLinkProtocol.Json(new MixerLinkProtocol.Ownership { id = focus.Id, meshId = meshId, side = "unity" }));
        }

        // ---- History -----------------------------------------------------------------------------------------------------

        public void BeforeHistoryApplied(ICollection<EditTarget> targets)
        {
            if (blenderMeshes.Count == 0) return;
            foreach (var target in targets)
            {
                if (target == null || !target.Alive) continue;
                string meshId = MixerLinkService.MeshIdOf(target);
                // Blender's copy would bring the undone edits back with its next positions: the claim ends, and Unity's
                // document is what its history says.
                if (OwnsMesh(meshId))
                    EndClaim(meshId, "Unity undid or redid an edit of " + target.Name + " while you edited it in Blender: what you change in this mode stays in Blender. Send it to Unity, or take Unity's, from the Mixer panel.", takeBack: true);
            }
        }

        /// <summary>
        /// Undo and Redo reach Blender as live frames: the positions of every layer the entries moved and the weights they
        /// changed (a mesh waiting to go whole carries them anyway). Shape and group lists that changed go whole with the
        /// next <see cref="CheckShapeLists"/>.
        /// </summary>
        public void HistoryApplied(IReadOnlyList<HistoryEntry> entries)
        {
            if (!Connected || entries == null || entries.Count == 0) return;
            var layers = new Dictionary<EditTarget, HashSet<string>>();
            foreach (var entry in entries)
            {
                var target = entry.Target;
                if (target == null || !target.Alive || !graphNodes.Contains(target.Id) || IsBlenderOwned(target) || queuedMeshes.Contains(target.Id)) continue;
                switch (entry)
                {
                    case PositionEdit moved:
                        LayersOf(layers, target).Add(moved.Layer ?? "");
                        break;
                    case ShapeDeltaEdit deltas:
                        LayersOf(layers, target).Add(deltas.Shape);
                        break;
                    case GroupWeightEdit group:
                        if (target.Document.Group(group.Group) != null) SendWeights(target, WeightChannel.OfGroup(group.Group), group.Indices);
                        break;
                    case WeightEdit weights:
                    {
                        var byBone = new Dictionary<int, List<int>>();
                        for (int i = 0; i < weights.Indices.Length; i++)
                        {
                            foreach (var influence in weights.Before[i]) VerticesOf(byBone, influence.Bone).Add(weights.Indices[i]);
                            foreach (var influence in weights.After[i]) VerticesOf(byBone, influence.Bone).Add(weights.Indices[i]);
                        }
                        foreach (var pair in byBone) if (pair.Key >= 0) SendWeights(target, WeightChannel.OfBone(pair.Key), pair.Value);
                        break;
                    }
                }
            }
            foreach (var pair in layers)
                foreach (string layer in pair.Value)
                    SendLayer(pair.Key, layer.Length == 0 ? null : layer);
        }

        private static HashSet<string> LayersOf(Dictionary<EditTarget, HashSet<string>> layers, EditTarget target)
        {
            if (!layers.TryGetValue(target, out var set)) layers[target] = set = new HashSet<string>();
            return set;
        }

        private static List<int> VerticesOf(Dictionary<int, List<int>> byBone, int bone)
        {
            if (!byBone.TryGetValue(bone, out var list)) byBone[bone] = list = new List<int>();
            return list;
        }

        // The welded positions of one layer of a session (null: the base), as live positions.
        private void SendLayer(EditTarget target, string layer)
        {
            var doc = target.Document;
            if (layer != null && !doc.HasShape(layer)) return;
            var n = doc.LinkWeld;
            var positions = new Vector3[n.WeldedCount];
            for (int w = 0; w < positions.Length; w++) positions[w] = doc.GetLayerPosition(layer, n.Representative[w]);
            Send(MixerLinkProtocol.FrameType.Positions, MeshDataCodec.EncodePositions(MixerLinkService.MeshIdOf(target), layer, MeshDataCodec.Topology(doc), positions));
        }

        // ---- What Blender was sent ---------------------------------------------------------------------------------------

        /// <summary>The shapes Blender last had of this mesh (sent, or as Blender sent them back); null when unknown.</summary>
        public IReadOnlyCollection<string> SentShapes(string meshId) => sent.TryGetValue(meshId, out var info) ? info.Shapes : null;
        public IReadOnlyCollection<string> SentGroups(string meshId) => sent.TryGetValue(meshId, out var info) ? info.Groups : null;

        /// <summary>Blender sent its whole mesh: these are the shapes and groups it has now.</summary>
        public void NoteBlenderHas(string meshId, IEnumerable<string> shapes, IEnumerable<string> groups)
        {
            if (!sent.TryGetValue(meshId, out var info)) sent[meshId] = info = new SentMesh();
            info.Shapes = new HashSet<string>(shapes);
            info.Groups = new HashSet<string>(groups);
        }

        public void NoteUnclaimed(string meshId)
        {
            double now = EditorApplication.timeSinceStartup;
            if (throttled.TryGetValue("unclaimed:" + meshId, out var last) && now - last.at < 10.0) return;
            throttled["unclaimed:" + meshId] = ("", now);
            Say("Blender sent an edit of a mesh it does not hold (its claim was refused or ended): not applied.");
        }

        // ---- Frames out --------------------------------------------------------------------------------------------------

        public bool Send(MixerLinkProtocol.FrameType type, byte[] payload = null)
        {
            if (Server == null || connection < 0) return false;
            return Server.Send(connection, (byte)type, payload);
        }

        public void Status(string id, string message, string level) =>
            Send(MixerLinkProtocol.FrameType.Status, MixerLinkProtocol.Json(new MixerLinkProtocol.StatusMessage { id = id, message = message, level = level }));

        /// <summary>A status sent at most every 5 s with the same text for the same id (problems repeat with every frame).</summary>
        public void StatusThrottled(string id, string message, string level)
        {
            double now = EditorApplication.timeSinceStartup;
            if (throttled.TryGetValue(id ?? "", out var last) && last.message == message && now - last.at < 5.0) return;
            throttled[id ?? ""] = (message, now);
            Status(id, message, level);
            Say(message);
        }

        public void MarkGraphDirty(double delay)
        {
            graphDirty = true;
            graphDueAt = Math.Max(graphDueAt, EditorApplication.timeSinceStartup + delay);
        }

        public void SceneSwitched()
        {
            lastGraph = null;
            sceneSwitched = true;
            MarkGraphDirty(0.1);
        }

        /// <summary>Sends the whole graph, or the delta since the last one (and checks once more a little later: tools answer from caches).</summary>
        public void SendGraph(bool force) => SendGraph(force, recheck: !force);

        private void SendGraph(bool force, bool recheck)
        {
            if (!Connected) return;
            var scene = sceneOf();
            var graph = SceneGraphExporter.Build(scene, MixerLinkService.SettingsFor(scene, false));
            bool whole = force || lastGraph == null || lastGraph.sceneGuid != graph.sceneGuid;
            if (whole)
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
            graphNodes.Clear();
            foreach (var node in graph.nodes) graphNodes.Add(node.id);
            graphDirty = false;
            if (sceneSwitched && whole)
            {
                sceneSwitched = false;
                meshesAnnounced = false;
                heldByBlender = null;
                SendAllMeshes();
            }
            if (recheck && recheckAt <= 0) recheckAt = EditorApplication.timeSinceStartup + 2.5;
        }

        private void SendAllMeshes()
        {
            if (lastGraph == null) return;
            var seen = new HashSet<string>();
            foreach (var node in lastGraph.nodes)
            {
                if (node.meshId == null || !seen.Add(node.meshId)) continue;
                if (BlenderHolds(node)) continue;
                SendMesh(node.id);
            }
        }

        // Blender kept this asset mesh from before (a reconnect after a script reload) and it has not changed: not sent again.
        private bool BlenderHolds(SceneNode node)
        {
            if (heldByBlender == null || !heldByBlender.TryGetValue(node.meshId, out string key) || string.IsNullOrEmpty(key)) return false;
            var go = SceneGraphExporter.Resolve(node.id) as GameObject;
            var renderer = go != null ? go.GetComponent<Renderer>() : null;
            if (renderer == null || MixerSession.TargetOf(renderer) != null) return false;
            var mesh = renderer is SkinnedMeshRenderer s ? s.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || KeyOf(mesh) != key) return false;
            var info = new SentMesh { Key = key };
            for (int i = 0; i < mesh.blendShapeCount; i++) info.Shapes.Add(mesh.GetBlendShapeName(i));
            sent[node.meshId] = info;
            return true;
        }

        /// <summary>What identifies an asset mesh's content for the link (null when it cannot be told: not an asset, or changed in memory).</summary>
        internal static string KeyOf(Mesh mesh)
        {
            if (mesh == null || EditorUtility.IsDirty(mesh)) return null;
            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path)) return null;
            var hash = AssetDatabase.GetAssetDependencyHash(path);
            if (!hash.isValid) return null;
            return MixerLinkProtocol.ProtocolVersion + "." + WeldVersion + ":" + hash + ":" + SceneGraphExporter.IdOf(mesh);
        }

        /// <summary>Queues the mesh of a node; one leaves at a time, and the next once Blender said it arrived.</summary>
        public void SendMesh(string nodeId)
        {
            if (!Connected || string.IsNullOrEmpty(nodeId)) return;
            if (queuedMeshes.Add(nodeId)) meshQueue.Enqueue(nodeId);
        }

        public int MeshesWaiting => meshQueue.Count + (encoding != null && !encoding.IsCompleted ? 1 : 0) + encoded.Count + (inFlight != null ? 1 : 0);

        private void SendNextMesh()
        {
            DrainEncoded();
            if (inFlight != null && EditorApplication.timeSinceStartup - inFlightSince < InFlightSeconds) return;
            inFlight = null;
            if (encoding != null && !encoding.IsCompleted) return;
            if (!encoded.IsEmpty) return;
            if (meshQueue.Count == 0)
            {
                if (!meshesAnnounced && Connected) { meshesAnnounced = true; Status("link", "meshes done", "info"); }
                return;
            }
            string nodeId = meshQueue.Dequeue();
            queuedMeshes.Remove(nodeId);
            var go = SceneGraphExporter.Resolve(nodeId) as GameObject;
            var renderer = go != null ? go.GetComponent<Renderer>() : null;
            if (renderer == null) return;
            string meshId = MixerLinkService.MeshIdOf(renderer);
            // Blender edits it: its own copy is the newer one.
            if (meshId == null || OwnsMesh(meshId)) return;
            var target = MixerSession.TargetOf(renderer);
            int connectionId = connection;
            string name = renderer.name;
            try
            {
                MeshDataCodec.MeshSnapshot snapshot;
                if (target != null && target.Alive) snapshot = MeshDataCodec.Snapshot(meshId, target);
                else
                {
                    var mesh = renderer is SkinnedMeshRenderer s ? s.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh == null) return;
                    snapshot = MeshDataCodec.Snapshot(meshId, mesh, renderer as SkinnedMeshRenderer);
                    snapshot.Key = KeyOf(mesh);
                }
                var info = new SentMesh { Key = snapshot.Key };
                foreach (var shape in snapshot.Shapes) info.Shapes.Add(shape.Name);
                foreach (var (group, _) in snapshot.Groups) info.Groups.Add(group);
                var job = new Encoded { Connection = connectionId, MeshId = meshId, NodeId = nodeId, Name = name, Sent = info };
                if (target != null && target.Alive)
                {
                    // A document's arrays change with every stroke: it is written here, at once.
                    job.Payload = MeshDataCodec.Encode(snapshot);
                    encoded.Enqueue(job);
                    DrainEncoded();
                }
                else
                {
                    // An asset's arrays were copied above: welding and writing happen on a thread of their own.
                    encoding = DedicatedTask.Run(() =>
                    {
                        try { job.Payload = MeshDataCodec.Encode(snapshot); }
                        catch (Exception e) { job.Error = e; }
                        encoded.Enqueue(job);
                    });
                }
            }
            catch (Exception e)
            {
                Fail(nodeId, name, e);
            }
        }

        private void DrainEncoded()
        {
            while (encoded.TryDequeue(out var job))
            {
                if (job.Connection != connection || !Connected) continue;
                // Blender claimed the mesh while it was being written: its own copy is the newer one.
                if (OwnsMesh(job.MeshId)) continue;
                if (job.Error != null) { Fail(job.NodeId, job.Name, job.Error); continue; }
                if (job.Payload.Length > MixerLinkProtocol.MaxMeshBytes)
                {
                    Fail(job.NodeId, job.Name, new InvalidDataException("its " + MixerLinkProtocol.Megabytes(job.Payload.Length) + " are more than the link carries (" + MixerLinkProtocol.Megabytes(MixerLinkProtocol.MaxMeshBytes) + "). Keep it out of Blender, or edit it there through an FBX."));
                    continue;
                }
                if (!SendMeshPayload(job.Payload)) { Say("The mesh of " + job.Name + " was not sent: Blender is gone."); continue; }
                inFlightBefore = sent.TryGetValue(job.MeshId, out var before) ? before : null;
                sent[job.MeshId] = job.Sent;
                inFlight = job.MeshId;
                inFlightSince = EditorApplication.timeSinceStartup;
            }
        }

        private bool SendMeshPayload(byte[] payload)
        {
            if (payload.Length <= MixerLinkProtocol.MaxPayloadBytes) return Send(MixerLinkProtocol.FrameType.MeshData, payload);
            uint transfer = nextTransfer++;
            foreach (var chunk in MixerLinkProtocol.Chunks(payload, transfer))
                if (!Send(MixerLinkProtocol.FrameType.MeshChunk, chunk)) return false;
            return true;
        }

        // Loud on every side: the Console, the link's log, and Blender (whose copy of the mesh stays as it was).
        private void Fail(string nodeId, string name, Exception e)
        {
            string text = "The mesh of " + name + " was not sent to Blender: " + e.Message;
            Debug.LogWarning("[Mixer] " + text);
            Say(text);
            Status(nodeId, "Unity could not send " + name + ": " + e.Message, "error");
        }

        /// <summary>A Mixer stroke moved a target: Blender gets the new positions soon (collapsed, at most every 100 ms).</summary>
        public void NotePositions(EditTarget target)
        {
            if (!Connected || target == null || !graphNodes.Contains(target.Id) || IsBlenderOwned(target)) return;
            positionsDue[target.Id] = Math.Min(positionsDue.TryGetValue(target.Id, out double due) ? due : double.MaxValue, EditorApplication.timeSinceStartup + 0.1);
        }

        /// <summary>A Mixer stroke changed weights: Blender gets the sparse change now.</summary>
        public void SendWeights(EditTarget target, WeightChannel channel, IReadOnlyList<int> vertices)
        {
            if (!Connected || target == null || channel.IsNone || !graphNodes.Contains(target.Id) || IsBlenderOwned(target)) return;
            var doc = target.Document;
            var n = doc.LinkWeld;
            var bones = target.Skinned != null ? target.Skinned.bones : null;
            string name = channel.IsGroup ? channel.Group : (bones != null && channel.Bone >= 0 && channel.Bone < bones.Length ? MeshDataCodec.BoneName(bones[channel.Bone]) : "");
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
            Send(MixerLinkProtocol.FrameType.Weights, MeshDataCodec.EncodeWeights(MixerLinkService.MeshIdOf(target), name, channel.IsGroup, channel.Bone, MeshDataCodec.Topology(doc), indices, weights));
        }

        // A session whose shapes or groups changed in Unity (deleted, renamed, duplicated...) is sent whole: Blender would
        // keep the old ones otherwise, and bring them back with its next edit.
        private void CheckShapeLists()
        {
            foreach (var target in MixerSession.Targets)
            {
                if (!target.Alive || !graphNodes.Contains(target.Id)) continue;
                var doc = target.Document;
                if (checkedRevision.TryGetValue(target.Id, out int revision) && revision == doc.Revision) continue;
                string meshId = MixerLinkService.MeshIdOf(target);
                // Not compared while Blender holds the mesh or it waits to go: the revision is checked once it can be.
                if (meshId == null || OwnsMesh(meshId) || queuedMeshes.Contains(target.Id) || !sent.TryGetValue(meshId, out var info)) continue;
                checkedRevision[target.Id] = doc.Revision;
                var shapes = new HashSet<string>(doc.ShapeOrder.Where(doc.HasShape));
                var groups = new HashSet<string>(doc.Groups.Select(g => g.Name));
                if (shapes.SetEquals(info.Shapes) && groups.SetEquals(info.Groups)) continue;
                positionsDue.Remove(target.Id);
                SendMesh(target.Id);
            }
        }

        internal void Tick()
        {
            if (Server == null) return;
            double now = EditorApplication.timeSinceStartup;
            if (writesEndpoint && now - lastEndpointWrite > 10.0)
            {
                try { WriteEndpoint(); }
                catch (Exception e) { lastEndpointWrite = now; Debug.LogWarning("[Mixer] The Blender link's endpoint file could not be written: " + e.Message); }
            }
            if (!Connected) return;
            UpdateUnityClaim();
            SendNextMesh();
            if (graphDirty && now >= graphDueAt) SendGraph(force: false, recheck: true);
            else if (recheckAt > 0 && now >= recheckAt) { recheckAt = 0; SendGraph(force: false, recheck: false); }
            CheckShapeLists();
            if (positionsDue.Count == 0) return;
            foreach (var pair in positionsDue.Where(p => p.Value <= now).ToList())
            {
                positionsDue.Remove(pair.Key);
                var target = MixerSession.Targets.FirstOrDefault(t => t.Id == pair.Key);
                if (target == null || !target.Alive || IsBlenderOwned(target) || queuedMeshes.Contains(target.Id)) continue;
                SendLayer(target, target.Document.Layer);
            }
        }
    }
}
