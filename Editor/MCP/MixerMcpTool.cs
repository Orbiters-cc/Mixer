using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor.MCP
{
    [McpForUnityTool("mixer_tool", Description =
        "Drive and verify Orbiters Mixer (Blender's tools inside the Unity editor) without the desktop; never changes the selection or the user's windows. " +
        "status: the open sessions (targets with vertices, shapes, layer, bones), the Scene tool's mode and brush, Mixer's history and the Blender link. " +
        "start {target, mode?}: edit a renderer's mesh in Mixer (target: instance ID, Mixer node id or hierarchy path 'Root/Child' / 'Scene:Root/Child'; an object without a renderer takes its largest skinned mesh) and pick the mode sculpt | weights | armature | none. " +
        "stroke {brush?, from, to, radius?, strength?, steps?, invert?, target?}: a sculpt stroke in world space between two points ([x,y,z]), recorded as one Undo step like a mouse stroke; brushes Grab, Smooth, Inflate, Flatten, Clay, Pinch, Crease, Mask. " +
        "undo / redo: Mixer's history through Unity's Undo. commit {target?}: save the edits as a native mesh asset shown on the renderer (one Undo step); revert {target?}: the original mesh back. " +
        "link_status: the Blender link (port, peer, meshes waiting, log tail). hierarchy {search?, limit?}: the scene objects with Mixer's flags (kept out of Blender, edited in Blender, edited in Mixer); search takes words, t:Type and c:Component like the Mixer Hierarchy. " +
        "exclude {target, excluded}: keep an object and its children out of the Blender project, or send them back.")]
    public static class MixerMcpTool
    {
        public sealed class Parameters
        {
            [ToolParameter("status, start, stroke, undo, redo, commit, revert, link_status, hierarchy or exclude")] public string action { get; set; }
            [ToolParameter("Renderer or object: instance ID, Mixer node id, or hierarchy path 'Root/Child' or 'Scene:Root/Child'; stroke, commit and revert default to the focused target", Required = false)] public string target { get; set; }
            [ToolParameter("start: sculpt, weights, armature or none (default: the current mode, or sculpt)", Required = false)] public string mode { get; set; }
            [ToolParameter("stroke: brush name (default: the active brush)", Required = false)] public string brush { get; set; }
            [ToolParameter("stroke: world start [x,y,z]", Required = false)] public float[] from { get; set; }
            [ToolParameter("stroke: world end [x,y,z]", Required = false)] public float[] to { get; set; }
            [ToolParameter("stroke: radius in metres (default: the tool's)", Required = false)] public float? radius { get; set; }
            [ToolParameter("stroke: strength 0..1 (default: the tool's)", Required = false)] public float? strength { get; set; }
            [ToolParameter("stroke: steps between from and to, 1..256 (default 8)", Required = false)] public int? steps { get; set; }
            [ToolParameter("stroke: inverted brush (Ctrl)", Required = false)] public bool invert { get; set; }
            [ToolParameter("hierarchy: search text; words match names, t:Type and c:Component match components", Required = false)] public string search { get; set; }
            [ToolParameter("hierarchy: most rows returned (default 400)", Required = false)] public int? limit { get; set; }
            [ToolParameter("exclude: true keeps the object out of Blender, false sends it back (default true)", Required = false)] public bool? excluded { get; set; }
        }

        public static object HandleCommand(JObject arguments)
        {
            try
            {
                var p = arguments?.ToObject<Parameters>() ?? throw new ArgumentException("Parameters are required.");
                switch ((p.action ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "status": return new SuccessResponse(StatusLine(), Status());
                    case "start": return Start(p);
                    case "stroke": return Stroke(p);
                    case "undo":
                    {
                        if (!MixerHistory.CanUndo) throw new InvalidOperationException("Nothing to undo in Mixer.");
                        string label = MixerHistory.UndoLabel;
                        MixerHistory.PerformUndo();
                        return new SuccessResponse("Undid " + label + ".", Status());
                    }
                    case "redo":
                    {
                        if (!MixerHistory.CanRedo) throw new InvalidOperationException("Nothing to redo in Mixer.");
                        string label = MixerHistory.RedoLabel;
                        MixerHistory.PerformRedo();
                        return new SuccessResponse("Redid " + label + ".", Status());
                    }
                    case "commit":
                    {
                        var target = TargetOf(p);
                        string name = target.Name;
                        var mesh = MixerSession.Commit(target);
                        string path = mesh != null ? AssetDatabase.GetAssetPath(mesh) : null;
                        return new SuccessResponse("Committed " + name + (path != null ? " to " + path : "") + ".", new { asset = path, vertices = mesh != null ? mesh.vertexCount : 0, blendShapes = mesh != null ? mesh.blendShapeCount : 0, status = Status() });
                    }
                    case "revert":
                    {
                        var target = TargetOf(p);
                        string name = target.Name;
                        MixerSession.Revert(target);
                        return new SuccessResponse("Reverted " + name + ": its original mesh is back.", Status());
                    }
                    case "link_status": return new SuccessResponse(LinkLine(), Link());
                    case "hierarchy": return Hierarchy(p);
                    case "exclude":
                    {
                        var go = ResolveObject(p.target) ?? throw new ArgumentException("target not found: give an instance ID, a Mixer node id or a hierarchy path.");
                        bool excluded = p.excluded ?? true;
                        MixerHierarchyWindow.SetExcluded(new[] { go }, excluded);
                        return new SuccessResponse(go.name + (excluded ? " is kept out of the Blender project." : " goes to the Blender project."), new { id = SceneGraphExporter.IdOf(go), excluded, link = Link() });
                    }
                    default: throw new ArgumentException("action must be status, start, stroke, undo, redo, commit, revert, link_status, hierarchy or exclude.");
                }
            }
            catch (Exception ex) { return new ErrorResponse(ex.Message); }
        }

        private static object Start(Parameters p)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) throw new InvalidOperationException("Wait for Unity to be idle.");
            var go = ResolveObject(p.target) ?? throw new ArgumentException("target not found: give an instance ID, a Mixer node id or a hierarchy path such as 'Avatar/Body'.");
            var renderer = RendererOf(go) ?? throw new ArgumentException(go.name + " has no mesh to edit (no SkinnedMeshRenderer or MeshFilter on it or under it).");
            var target = MixerSession.TargetOf(renderer);
            if (target == null)
            {
                if (!MixerSession.CanEdit(renderer, out string reason)) throw new InvalidOperationException(reason);
                target = MixerSession.Edit(renderer);
            }
            var mode = ParseMode(p.mode) ?? (MixerSceneTool.Mode == MixerMode.None ? MixerMode.Sculpt : MixerSceneTool.Mode);
            MixerSceneTool.Mode = mode;
            MixerSceneTool.FocusTarget = target;
            return new SuccessResponse("Editing " + renderer.name + " in Mixer (" + mode + " mode): " + target.Document.VertexCount + " vertices, " + target.Document.ShapeOrder.Count + " blendshapes, " + target.Document.BoneCount + " bones.", Status());
        }

        private static object Stroke(Parameters p)
        {
            var target = TargetOf(p);
            if (p.from == null || p.from.Length != 3 || p.to == null || p.to.Length != 3) throw new ArgumentException("from and to are world positions [x,y,z].");
            var brush = string.IsNullOrEmpty(p.brush) ? MixerSceneTool.ActiveBrush : Brushes.Named(p.brush);
            if (brush == null) throw new ArgumentException("Unknown brush '" + p.brush + "'. Brushes: " + string.Join(", ", Brushes.All.Select(b => b.Name)) + ".");
            float radius = p.radius ?? MixerSceneTool.Radius;
            float strength = p.strength ?? MixerSceneTool.Strength;
            int steps = Mathf.Clamp(p.steps ?? 8, 1, 256);
            var from = new Vector3(p.from[0], p.from[1], p.from[2]);
            var to = new Vector3(p.to[0], p.to[1], p.to[2]);
            var view = SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null ? SceneView.lastActiveSceneView.camera.transform.forward : Vector3.forward;
            var edit = MixerSceneTool.StrokeBetween(target, brush, from, to, steps, radius, strength, view, p.invert);
            int moved = edit != null && edit.Indices != null ? edit.Indices.Length : 0;
            bool mask = brush.Name == "Mask";
            return new SuccessResponse(brush.Name + " stroke on " + target.Name + ": " + moved + " vertices changed" + (moved == 0 && !mask ? " (nothing within the radius of the path: from and to are world positions on the mesh)" : "") + ".",
                new { target = target.Name, brush = brush.Name, moved, layer = target.Document.Layer ?? "base", radius, strength, steps, history = HistoryInfo() });
        }

        private static object Hierarchy(Parameters p)
        {
            int limit = Mathf.Clamp(p.limit ?? 400, 1, 5000);
            string text = (p.search ?? string.Empty).Trim();
            var words = new List<string>();
            var types = new List<string>();
            foreach (string token in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("t:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("c:", StringComparison.OrdinalIgnoreCase)) types.Add(token.Substring(2));
                else words.Add(token);
            }
            var settings = MixerLinkService.SettingsFor(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), create: false);
            var excludedIds = new HashSet<string>(settings != null ? settings.excludedIds : new List<string>());
            var owned = new HashSet<string>(MixerLinkService.BlenderOwned);
            var edited = new HashSet<GameObject>(MixerSession.Targets.Where(t => t.Alive).Select(t => t.Renderer.gameObject));
            bool needIds = excludedIds.Count > 0 || owned.Count > 0;
            var rows = new List<object>();
            int total = 0;
            Action<Transform, int, string, bool> walk = null;
            walk = (t, depth, parentPath, excludedByParent) =>
            {
                var go = t.gameObject;
                if ((go.hideFlags & HideFlags.HideInHierarchy) != 0) return;
                string path = string.IsNullOrEmpty(parentPath) ? go.name : parentPath + "/" + go.name;
                string id = needIds ? SceneGraphExporter.IdOf(go) : null;
                bool excluded = excludedByParent || (id != null && excludedIds.Contains(id));
                bool matches = words.All(w => go.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)
                               && types.All(name => go.GetComponents<Component>().Any(c => c != null && (c.GetType().Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.GetType().FullName.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))));
                if (matches)
                {
                    total++;
                    if (rows.Count < limit)
                        rows.Add(new
                        {
                            name = go.name, path = go.scene.name + ":" + path, instanceId = go.GetInstanceID(), depth, active = go.activeInHierarchy,
                            children = t.childCount, prefab = PrefabUtility.IsPartOfPrefabInstance(go),
                            renderer = go.GetComponent<SkinnedMeshRenderer>() != null ? "skinned" : go.GetComponent<MeshFilter>() != null ? "mesh" : null,
                            excludedFromBlender = excluded, editedInBlender = id != null && owned.Contains(id), editedInMixer = edited.Contains(go),
                        });
                }
                for (int i = 0; i < t.childCount; i++) walk(t.GetChild(i), depth + 1, path, excluded);
            };
            foreach (var scene in SceneReference.Loaded())
                foreach (var root in scene.GetRootGameObjects()) walk(root.transform, 0, null, false);
            return new SuccessResponse(total + " object(s)" + (text.Length > 0 ? " match '" + text + "'" : "") + (total > rows.Count ? ", first " + rows.Count + " listed" : "") + ".", new { total, rows });
        }

        private static EditTarget TargetOf(Parameters p)
        {
            if (!string.IsNullOrEmpty(p.target))
            {
                var go = ResolveObject(p.target) ?? throw new ArgumentException("target not found: " + p.target);
                var renderer = RendererOf(go);
                var found = renderer != null ? MixerSession.TargetOf(renderer) : null;
                if (found == null) throw new InvalidOperationException(go.name + " is not being edited in Mixer: call start first.");
                return found;
            }
            var target = MixerSceneTool.FocusTarget ?? MixerSession.First;
            if (target == null || !target.Alive) throw new InvalidOperationException("No Mixer session is open: call start with a target.");
            return target;
        }

        private static GameObject ResolveObject(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            reference = reference.Trim();
            try
            {
                var byId = SceneGraphExporter.Resolve(reference);
                var go = byId as GameObject ?? (byId as Component)?.gameObject;
                if (go != null) return go;
            }
            catch (Exception) { /* not a node id: a path or an instance ID */ }
            return SceneReference.Resolve(reference);
        }

        private static Renderer RendererOf(GameObject go)
        {
            var own = go.GetComponent<Renderer>();
            if (own is SkinnedMeshRenderer || (own is MeshRenderer && go.GetComponent<MeshFilter>() != null)) return own;
            return go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s.sharedMesh != null).OrderByDescending(s => s.sharedMesh.vertexCount).FirstOrDefault()
                   ?? (Renderer)go.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(m => m.GetComponent<MeshFilter>()?.sharedMesh != null);
        }

        private static MixerMode? ParseMode(string mode)
        {
            switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "": return null;
                case "sculpt": return MixerMode.Sculpt;
                case "weights": case "weight": case "paint": return MixerMode.Weights;
                case "armature": case "bones": return MixerMode.Armature;
                case "none": case "off": return MixerMode.None;
                default: throw new ArgumentException("mode must be sculpt, weights, armature or none.");
            }
        }

        private static string StatusLine()
        {
            var targets = MixerSession.Targets.Where(t => t.Alive).ToList();
            return (targets.Count == 0 ? "No Mixer session." : targets.Count + " mesh(es) in Mixer: " + string.Join(", ", targets.Select(t => t.Name)) + ".")
                   + " Mode " + MixerSceneTool.Mode + ", brush " + (MixerSceneTool.ActiveBrush != null ? MixerSceneTool.ActiveBrush.Name : "none") + ". " + LinkLine();
        }

        private static object Status() => new
        {
            targets = MixerSession.Targets.Where(t => t.Alive).Select(t => new
            {
                name = t.Name, id = t.Id, instanceId = t.Renderer.GetInstanceID(), path = Path(t.Renderer.transform),
                vertices = t.Document.VertexCount, blendShapes = t.Document.ShapeOrder.Count, layer = t.Document.Layer ?? "base",
                bones = t.Document.BoneCount, bindPosesEdited = t.Document.BindPosesEdited, maskedVertices = t.Document.MaskedCount,
                edited = t.Document.Changed, revision = t.Document.Revision,
                focused = MixerSceneTool.FocusTarget == t, editedInBlender = MixerLinkService.IsBlenderOwned(t),
            }).ToList(),
            mode = MixerSceneTool.Mode.ToString(),
            brush = MixerSceneTool.ActiveBrush != null ? MixerSceneTool.ActiveBrush.Name : null,
            radius = MixerSceneTool.Radius, strength = MixerSceneTool.Strength, symmetry = MixerSceneTool.Symmetry,
            restPoseEditing = ArmatureEdit.Active,
            brushes = Brushes.All.Select(b => b.Name).ToArray(),
            history = HistoryInfo(),
            link = Link(),
        };

        private static object HistoryInfo() => new
        {
            steps = MixerHistory.Count, cursor = MixerHistory.Cursor, bytes = MixerHistory.Bytes,
            canUndo = MixerHistory.CanUndo, undo = MixerHistory.UndoLabel, canRedo = MixerHistory.CanRedo, redo = MixerHistory.RedoLabel,
        };

        private static string LinkLine() =>
            !MixerLinkService.Enabled ? "Blender link off." :
            !MixerLinkService.Running ? "Blender link not listening." :
            MixerLinkService.Connected ? "Blender " + (MixerLinkService.Peer != null ? MixerLinkService.Peer.blender : "") + " linked on port " + MixerLinkService.Port + "." :
            "Blender link listening on port " + MixerLinkService.Port + ", no Blender connected.";

        private static object Link()
        {
            var log = MixerLinkService.Log;
            var peer = MixerLinkService.Peer;
            return new
            {
                enabled = MixerLinkService.Enabled, listening = MixerLinkService.Running, connected = MixerLinkService.Connected, port = MixerLinkService.Port,
                peer = peer == null ? null : new { blender = peer.blender, extension = peer.extension, blendPath = peer.blendPath },
                meshesWaiting = MixerLinkService.MeshesWaiting, editedInBlender = MixerLinkService.BlenderOwned.ToArray(),
                endpointFile = MixerLinkProtocol.EndpointPath,
                log = log.Skip(Math.Max(0, log.Count - 20)).ToArray(),
            };
        }

        private static string Path(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return t.gameObject.scene.name + ":" + path;
        }
    }
}
