using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.UnitGit.Editor.Semantic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// Two versions of a model or prefab side by side in 3D, with one camera for both. Each side is coloured by what it
    /// changed compared to the reference (the common base of a conflict, or the other side): moved vertices as a heat map
    /// from yellow to red, whole parts moved, re-materialed, with other blendshapes or bones, or added. A table lists every
    /// part with its numbers. Models are read straight from the file (nothing is imported); prefabs are imported into a
    /// temporary folder, which costs little because they only reference their meshes.
    /// </summary>
    internal sealed class ModelCompareView : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.mixer/Editor/Git/Styles/unitgit-semantic.uss";
        private const string ShaderName = "Hidden/Orbiters/UnitGitChanges";

        /// <summary>Writes one version of the file to the given path; false when that version has no such file.</summary>
        internal delegate bool VersionWriter(string destination);

        private sealed class Side
        {
            public ModelInfo Info;
            public List<PartDiff> Diff;
            public readonly Dictionary<string, Mesh> Coloured = new Dictionary<string, Mesh>();
        }

        private sealed class Loaded
        {
            public string Key;
            public readonly List<string> Assets = new List<string>();
            public readonly List<ModelInfo> Infos = new List<ModelInfo>();
            public Side Left = new Side(), Right = new Side();
            public bool AgainstBase;
            public string Error;

            public void Release()
            {
                foreach (var mesh in Left.Coloured.Values.Concat(Right.Coloured.Values)) if (mesh != null) Object.DestroyImmediate(mesh);
                foreach (var part in Infos.Where(i => i != null).SelectMany(i => i.Parts).Where(p => p.Owned)) if (part.Mesh != null) Object.DestroyImmediate(part.Mesh);
                foreach (string asset in Assets) ModelVersions.Release(asset);
            }
        }

        // What the background read produced for one version: the parsed meshes of a model, or the copied file of a prefab.
        private sealed class Read
        {
            public string File;
            public byte[] Bytes;
            public List<FbxMesh> Meshes;
        }

        private sealed class Orbit
        {
            public float Yaw = 180f, Pitch = 8f, Distance = -1f;
            public Vector3 Pan;
        }

        // One loaded comparison at a time: the window rebuilding itself reuses it instead of reading again.
        private static Loaded current;
        private static string loading;
        private static readonly Dictionary<string, Orbit> Orbits = new Dictionary<string, Orbit>();
        private static bool highlight = true;

        private readonly string leftTitle, rightTitle;
        private readonly VisualElement stage, details;
        private readonly Label status;
        private IMGUIContainer viewport;
        private PreviewRenderUtility leftPreview, rightPreview;
        private Material changesMaterial, plainMaterial;
        private Orbit orbit;
        private Bounds frame;
        private string key;
        private Loaded shown;

        public ModelCompareView(string leftTitle, string rightTitle)
        {
            this.leftTitle = leftTitle;
            this.rightTitle = rightTitle;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null) styleSheets.Add(sheet);
            AddToClassList("ugm");
            status = new Label();
            status.AddToClassList("ugm-status");
            Add(status);
            stage = new VisualElement();
            stage.AddToClassList("ugm-stage");
            Add(stage);
            details = new VisualElement();
            details.AddToClassList("ugm-details");
            Add(details);
            RegisterCallback<DetachFromPanelEvent>(_ => DisposePreviews());
        }

        /// <summary>
        /// Loads both versions (and the reference, when there is one) and shows them. <paramref name="cacheKey"/> names
        /// these exact versions.
        /// </summary>
        public void Load(string cacheKey, string projectRoot, string originalPath, VersionWriter left, VersionWriter right, VersionWriter reference)
        {
            key = cacheKey;
            if (!Orbits.TryGetValue(key, out orbit)) Orbits[key] = orbit = new Orbit();
            if (current != null && current.Key == key)
            {
                Show(current);
                return;
            }
            Working("Reading both versions…");
            // A view rebuilt while the same versions are being read waits for that read instead of starting another.
            if (loading == cacheKey)
            {
                schedule.Execute(() => { if (current != null && current.Key == key) Show(current); }).Every(100).Until(() => shown != null || key != cacheKey);
                return;
            }
            loading = cacheKey;
            bool model = ModelVersions.IsModelPath(originalPath);
            string scratch = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "Library", "UnitGitCompare", Guid.NewGuid().ToString("N").Substring(0, 12));
            string extension = Path.GetExtension(originalPath);
            // Everything that does not need Unity runs in the background: Git writing the versions, reading the models
            // and comparing them. The editor only imports prefabs and makes the meshes, one per frame.
            var reads = Task.Run(() =>
            {
                Directory.CreateDirectory(scratch);
                Read Take(VersionWriter writer, string name)
                {
                    if (writer == null) return null;
                    string file = Path.Combine(scratch, name + extension);
                    if (!writer(file)) return null;
                    if (!model) return new Read { File = file };
                    // Models are parsed here and their copy deleted: only prefabs are imported.
                    var bytes = File.ReadAllBytes(file);
                    File.Delete(file);
                    return new Read { Bytes = bytes, Meshes = MeshComparison.Parse(originalPath, bytes) };
                }
                var result = new[] { Take(left, "mine"), Take(right, "theirs"), Take(reference, "base") };
                if (!model) return (result, (Loaded)null);
                var loaded = new Loaded { Key = cacheKey };
                ModelInfo Info(Read read)
                {
                    if (read == null) return null;
                    var info = MeshComparison.FromFile(read.Bytes.LongLength, read.Meshes);
                    loaded.Infos.Add(info);
                    return info;
                }
                Compare(loaded, Info(result[0]), Info(result[1]), Info(result[2]));
                return (result, loaded);
            });
            WhenDone(reads, cacheKey, outcome =>
            {
                if (outcome.IsFaulted)
                {
                    Done(cacheKey, scratch);
                    if (Showing(cacheKey)) Message("Could not read the versions", outcome.Exception?.GetBaseException().Message);
                    return;
                }
                if (model)
                {
                    Finish(cacheKey, scratch, outcome.Result.Item2);
                    return;
                }
                if (Showing(cacheKey)) Working("Loading the prefab versions…");
                // A few frames later, so the message shows before the import.
                var start = DateTime.UtcNow;
                Pump(() =>
                {
                    if ((DateTime.UtcNow - start).TotalMilliseconds < 50) return false;
                    LoadPrefabs(cacheKey, scratch, originalPath, outcome.Result.Item1);
                    return true;
                });
            });
        }

        // Prefabs need Unity's importer: imported and described here, then compared in the background.
        private void LoadPrefabs(string cacheKey, string scratch, string originalPath, Read[] reads)
        {
            var loaded = new Loaded { Key = cacheKey };
            ModelInfo left, right, baseInfo;
            try
            {
                ModelInfo Take(Read read, string label)
                {
                    if (read == null) return null;
                    string asset = ModelVersions.Import(read.File, originalPath, label);
                    loaded.Assets.Add(asset);
                    var info = MeshComparison.Describe(AssetDatabase.LoadAssetAtPath<GameObject>(asset), new FileInfo(read.File).Length);
                    MeshComparison.Snapshot(info);
                    loaded.Infos.Add(info);
                    return info;
                }
                left = Take(reads[0], "mine");
                right = Take(reads[1], "theirs");
                baseInfo = Take(reads[2], "base");
            }
            catch (Exception ex)
            {
                loaded.Error = ex.Message;
                Finish(cacheKey, scratch, loaded);
                return;
            }
            if (Showing(cacheKey)) Working("Comparing the versions…");
            WhenDone(Task.Run(() => Compare(loaded, left, right, baseInfo)), cacheKey, outcome =>
            {
                if (outcome.IsFaulted) loaded.Error = outcome.Exception?.GetBaseException().Message;
                Finish(cacheKey, scratch, loaded);
            });
        }

        // Any thread: what each side changed, with the colours of its highlight.
        private static void Compare(Loaded loaded, ModelInfo left, ModelInfo right, ModelInfo baseInfo)
        {
            loaded.Left.Info = left;
            loaded.Right.Info = right;
            loaded.AgainstBase = baseInfo != null;
            var empty = new ModelInfo();
            if (left != null) loaded.Left.Diff = MeshComparison.Compare(baseInfo ?? right ?? empty, left);
            if (right != null) loaded.Right.Diff = MeshComparison.Compare(baseInfo ?? left ?? empty, right);
        }

        // The view is still on screen and still showing these versions.
        private bool Showing(string cacheKey) => panel != null && key == cacheKey;

        // Runs on the editor's update until it returns true. Not on the view's scheduler: a view taken off screen (another
        // file selected) stops its scheduler, and the load must still end (another view of the same versions waits on it).
        private static void Pump(Func<bool> tick)
        {
            void Update()
            {
                bool done;
                try { done = tick(); }
                catch (Exception ex) { Debug.LogException(ex); done = true; }
                if (done) EditorApplication.update -= Update;
            }
            EditorApplication.update += Update;
        }

        // Hands a background task over once it ended.
        private static void WhenDone<T>(T task, string cacheKey, Action<T> then) where T : Task
        {
            Pump(() =>
            {
                if (!task.IsCompleted) return false;
                then(task);
                return true;
            });
        }

        // The meshes are made a few milliseconds' worth per frame (the editor never waits on a whole avatar), then the
        // comparison shows.
        private void Finish(string cacheKey, string scratch, Loaded loaded)
        {
            DeleteFolder(scratch);
            var steps = new Queue<Action>();
            if (string.IsNullOrEmpty(loaded.Error))
            {
                foreach (var side in new[] { loaded.Left, loaded.Right })
                    foreach (var step in MeshSteps(side)) steps.Enqueue(step);
                foreach (var info in loaded.Infos.Where(i => i != null && i != loaded.Left.Info && i != loaded.Right.Info))
                    foreach (var part in info.Parts.Where(p => p.Mesh == null && p.Points != null))
                        steps.Enqueue(() => part.Mesh = MeshComparison.CreateMesh(part));
            }
            int total = steps.Count;
            if (total > 0 && Showing(cacheKey)) Working("Preparing the meshes…");
            Pump(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (steps.Count > 0 && watch.ElapsedMilliseconds < 12) steps.Dequeue()();
                }
                catch (Exception ex)
                {
                    loaded.Error = ex.Message;
                    steps.Clear();
                }
                if (steps.Count > 0)
                {
                    if (Showing(cacheKey)) status.text = "Preparing the meshes… " + (total - steps.Count) + " of " + total;
                    return false;
                }
                Done(cacheKey, null);
                current?.Release();
                current = loaded;
                if (Showing(cacheKey)) Show(loaded);
                return true;
            });
        }

        private static void Done(string cacheKey, string scratch)
        {
            if (loading == cacheKey) loading = null;
            if (scratch != null) DeleteFolder(scratch);
        }

        // Each part's own mesh when read from a file, and a copy with the change colours in its vertex colours (the
        // version's own mesh is never touched).
        private static IEnumerable<Action> MeshSteps(Side side)
        {
            if (side.Info == null) yield break;
            foreach (var part in side.Info.Parts)
            {
                var diff = side.Diff?.FirstOrDefault(d => d.Path == part.Path);
                var colors = diff?.Colors != null && diff.Colors.Length == part.VertexCount ? diff.Colors : null;
                if (part.Mesh == null && part.Points != null)
                {
                    yield return () =>
                    {
                        part.Mesh = MeshComparison.CreateMesh(part);
                        if (colors != null) side.Coloured[part.Path] = MeshComparison.CreateMesh(part, colors);
                    };
                }
                else if (colors != null && part.Mesh != null)
                {
                    yield return () =>
                    {
                        var copy = Object.Instantiate(part.Mesh);
                        copy.hideFlags = HideFlags.HideAndDontSave;
                        copy.colors = colors;
                        side.Coloured[part.Path] = copy;
                    };
                }
            }
        }

        // ---- Showing ---------------------------------------------------------------------------------------------------

        private void Show(Loaded loaded)
        {
            DisposePreviews();
            shown = loaded;
            stage.Clear();
            details.Clear();
            if (!string.IsNullOrEmpty(loaded.Error)) { Message("Could not load this model", loaded.Error); return; }
            status.text = loaded.AgainstBase ? "Each side is coloured by what it changed since the common version." : "Each side is coloured by how it differs from the other.";

            var shader = Shader.Find(ShaderName);
            changesMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            plainMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            plainMaterial.SetColor("_Tint", MeshComparison.Unchanged);
            leftPreview = NewPreview();
            rightPreview = NewPreview();
            frame = loaded.Left.Info?.Bounds ?? loaded.Right.Info?.Bounds ?? new Bounds(Vector3.zero, Vector3.one);
            if (loaded.Right.Info != null) frame.Encapsulate(loaded.Right.Info.Bounds);
            if (orbit.Distance <= 0f) orbit.Distance = FrameDistance();

            var bar = new VisualElement();
            bar.AddToClassList("ugm-bar");
            stage.Add(bar);
            bar.Add(Caption(leftTitle, loaded.Left.Info == null ? "not in this version" : Summary(loaded.Left.Info), "ugm-caption--left"));
            bar.Add(Caption(rightTitle, loaded.Right.Info == null ? "not in this version" : Summary(loaded.Right.Info), "ugm-caption--right"));

            viewport = new IMGUIContainer(DrawViewports);
            viewport.AddToClassList("ugm-viewport");
            stage.Add(viewport);
            RegisterOrbit(viewport);

            var tools = new VisualElement();
            tools.AddToClassList("ugm-tools");
            stage.Add(tools);
            var toggle = new Toggle { text = "Highlight changes", value = highlight };
            toggle.AddToClassList("ugm-toggle");
            toggle.RegisterValueChangedCallback(evt =>
            {
                highlight = evt.newValue;
                viewport.MarkDirtyRepaint();
            });
            tools.Add(toggle);
            tools.Add(Legend());
            var reset = new Button(() => { ResetOrbit(); viewport.MarkDirtyRepaint(); }) { text = "Reset view", tooltip = "Back to the front view. Drag to turn, right-drag to move, scroll to zoom, double-click to reset." };
            reset.AddToClassList("ugs-chip");
            tools.Add(reset);

            details.Add(PartsTable(loaded));
            AddToClassList("ugm--enter");
            schedule.Execute(() => RemoveFromClassList("ugm--enter")).StartingIn(20);
        }

        private static string Summary(ModelInfo info)
        {
            int points = info.Parts.Sum(p => p.Mesh.vertexCount);
            return info.Parts.Count + " mesh" + (info.Parts.Count == 1 ? "" : "es") + " · " + points.ToString("N0") + " vertices · " + UnitGitService.FormatBytes(info.FileBytes);
        }

        private static VisualElement Caption(string title, string text, string className)
        {
            var caption = new VisualElement();
            caption.AddToClassList("ugm-caption");
            caption.AddToClassList(className);
            var t = new Label(title);
            t.AddToClassList("ugm-caption__title");
            caption.Add(t);
            var d = new Label(text);
            d.AddToClassList("ugm-caption__text");
            caption.Add(d);
            return caption;
        }

        private static PreviewRenderUtility NewPreview()
        {
            var preview = new PreviewRenderUtility();
            preview.camera.fieldOfView = 30f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(0.16f, 0.17f, 0.19f);
            preview.ambientColor = new Color(0.45f, 0.45f, 0.45f);
            return preview;
        }

        private void DrawViewports()
        {
            var rect = viewport.contentRect;
            if (rect.width < 20f || rect.height < 20f || Event.current.type != EventType.Repaint || shown == null) return;
            float half = (rect.width - 6f) * 0.5f;
            Draw(leftPreview, shown.Left, new Rect(rect.x, rect.y, half, rect.height));
            Draw(rightPreview, shown.Right, new Rect(rect.x + half + 6f, rect.y, half, rect.height));
        }

        private void Draw(PreviewRenderUtility preview, Side side, Rect rect)
        {
            if (preview == null || side.Info == null)
            {
                EditorGUI.DrawRect(rect, new Color(0.16f, 0.17f, 0.19f));
                GUI.Label(rect, "Not in this version", new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 11 });
                return;
            }
            preview.BeginPreview(rect, GUIStyle.none);
            var camera = preview.camera;
            var rotation = Quaternion.Euler(orbit.Pitch, orbit.Yaw, 0f);
            var pivot = frame.center + orbit.Pan;
            camera.transform.rotation = rotation;
            camera.transform.position = pivot - rotation * Vector3.forward * orbit.Distance;
            camera.nearClipPlane = Mathf.Max(0.001f, orbit.Distance * 0.01f);
            camera.farClipPlane = orbit.Distance * 20f + frame.size.magnitude * 4f;
            preview.lights[0].intensity = 1.1f;
            preview.lights[0].transform.rotation = rotation * Quaternion.Euler(25f, 25f, 0f);
            preview.lights[1].intensity = 0.6f;
            foreach (var part in side.Info.Parts)
            {
                Mesh copy = null;
                bool coloured = highlight && side.Coloured.TryGetValue(part.Path, out copy);
                var mesh = coloured ? copy : part.Mesh;
                for (int sub = 0; sub < Mathf.Max(1, mesh.subMeshCount); sub++)
                {
                    Material material = coloured ? changesMaterial
                        : !highlight && part.Rendered != null && sub < part.Rendered.Length && part.Rendered[sub] != null ? part.Rendered[sub]
                        : plainMaterial;
                    preview.DrawMesh(mesh, part.Placement, material, sub);
                }
            }
            preview.Render(true);
            GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.StretchToFill, false);
        }

        private float FrameDistance()
        {
            float radius = Mathf.Max(frame.extents.magnitude, 0.01f);
            return radius / Mathf.Sin(15f * Mathf.Deg2Rad) * 0.95f;
        }

        private void ResetOrbit()
        {
            orbit.Yaw = 180f;
            orbit.Pitch = 8f;
            orbit.Pan = Vector3.zero;
            orbit.Distance = FrameDistance();
        }

        // Drag to turn, right or middle drag to move, scroll to zoom, double-click to reset. Both views follow.
        private void RegisterOrbit(VisualElement target)
        {
            int button = -1;
            Vector2 last = Vector2.zero;
            target.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.clickCount == 2) { ResetOrbit(); target.MarkDirtyRepaint(); return; }
                button = evt.button;
                last = evt.position;
                target.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            target.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (button < 0 || !target.HasPointerCapture(evt.pointerId)) return;
                Vector2 delta = (Vector2)evt.position - last;
                last = evt.position;
                if (button == 0)
                {
                    orbit.Yaw += delta.x * 0.45f;
                    orbit.Pitch = Mathf.Clamp(orbit.Pitch + delta.y * 0.45f, -85f, 85f);
                }
                else
                {
                    var rotation = Quaternion.Euler(orbit.Pitch, orbit.Yaw, 0f);
                    float scale = orbit.Distance * 0.0022f;
                    orbit.Pan += rotation * new Vector3(-delta.x * scale, delta.y * scale, 0f);
                }
                target.MarkDirtyRepaint();
            });
            target.RegisterCallback<PointerUpEvent>(evt =>
            {
                button = -1;
                target.ReleasePointer(evt.pointerId);
            });
            target.RegisterCallback<WheelEvent>(evt =>
            {
                orbit.Distance = Mathf.Max(0.02f, orbit.Distance * (1f + evt.delta.y * 0.05f));
                target.MarkDirtyRepaint();
                evt.StopPropagation();
            });
        }

        private static VisualElement Legend()
        {
            var legend = new VisualElement();
            legend.AddToClassList("ugm-legend");
            var heat = new VisualElement { tooltip = "Moved vertices: yellow moved a little, red moved the most (the table gives the distances)." };
            heat.AddToClassList("ugm-legend__heat");
            legend.Add(heat);
            var text = new Label("moved");
            text.AddToClassList("ugm-legend__text");
            legend.Add(text);
            void Chip(Color color, string label, string tooltip)
            {
                var dot = new VisualElement { tooltip = tooltip };
                dot.AddToClassList("ugm-legend__dot");
                dot.style.backgroundColor = color;
                legend.Add(dot);
                var name = new Label(label) { tooltip = tooltip };
                name.AddToClassList("ugm-legend__text");
                legend.Add(name);
            }
            Chip(MeshComparison.Added, "added", "A mesh that only this side has.");
            Chip(MeshComparison.Moved, "placed", "The whole mesh moved, turned or scaled.");
            Chip(MeshComparison.Materials, "materials", "Other materials on the same mesh.");
            Chip(MeshComparison.Shapes, "blendshapes", "Same surface, other blendshapes or bones.");
            Chip(MeshComparison.Unchanged, "same", "Unchanged.");
            return legend;
        }

        private VisualElement PartsTable(Loaded loaded)
        {
            var table = new VisualElement();
            table.AddToClassList("ugm-table");
            var head = new VisualElement();
            head.AddToClassList("ugm-row");
            head.AddToClassList("ugm-row--head");
            head.Add(Cell("Part", "ugm-cell--part"));
            head.Add(Cell(leftTitle, "ugm-cell"));
            head.Add(Cell(rightTitle, "ugm-cell"));
            table.Add(head);
            var paths = (loaded.Left.Diff ?? new List<PartDiff>()).Select(d => d.Path).Concat((loaded.Right.Diff ?? new List<PartDiff>()).Select(d => d.Path)).Distinct().ToList();
            // Changed parts first, then the rest.
            paths = paths.OrderBy(p => IsSame(Find(loaded.Left.Diff, p)) && IsSame(Find(loaded.Right.Diff, p))).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string path in paths)
            {
                var row = new VisualElement();
                row.AddToClassList("ugm-row");
                var name = Cell(path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path, "ugm-cell--part");
                name.tooltip = path;
                row.Add(name);
                row.Add(DiffCell(Find(loaded.Left.Diff, path)));
                row.Add(DiffCell(Find(loaded.Right.Diff, path)));
                table.Add(row);
            }
            if (paths.Count == 0) table.Add(Cell("No meshes in these versions.", "ugm-cell--part"));
            return table;
        }

        private static PartDiff Find(List<PartDiff> diffs, string path) => diffs?.FirstOrDefault(d => d.Path == path);
        private static bool IsSame(PartDiff diff) => diff == null || diff.Change == PartChange.Same;

        private static VisualElement DiffCell(PartDiff diff)
        {
            var cell = new VisualElement();
            cell.AddToClassList("ugm-cell");
            if (diff == null) { cell.Add(Cell("—", "ugm-cell__muted")); return cell; }
            var chip = new Label(ChangeName(diff.Change));
            chip.AddToClassList("ugm-chip");
            chip.AddToClassList("ugm-chip--" + diff.Change.ToString().ToLowerInvariant());
            cell.Add(chip);
            var facts = new List<string>();
            if (diff.Change == PartChange.Reshaped) facts.Add(diff.MovedVertices.ToString("N0") + " vertices moved, up to " + MeshComparison.Length(diff.MaxDistance));
            if (diff.VerticesBefore != diff.VerticesAfter && diff.Change != PartChange.Added && diff.Change != PartChange.Removed)
                facts.Add(diff.VerticesBefore.ToString("N0") + " → " + diff.VerticesAfter.ToString("N0") + " vertices");
            if (diff.Change == PartChange.Added) facts.Add(diff.VerticesAfter.ToString("N0") + " vertices");
            if (diff.ShapesAdded.Count > 0) facts.Add(Names(diff.ShapesAdded, "new"));
            if (diff.ShapesRemoved.Count > 0) facts.Add(Names(diff.ShapesRemoved, "removed"));
            if (diff.ShapesChanged.Count > 0) facts.Add(Names(diff.ShapesChanged, "reshaped"));
            if (diff.BonesChanged) facts.Add("bones changed");
            if (facts.Count > 0) cell.Add(Cell(string.Join(" · ", facts), "ugm-cell__muted"));
            return cell;
        }

        // "36 blendshapes new: Jawline, Goatee…"
        private static string Names(List<string> names, string what)
        {
            return names.Count + " blendshape" + (names.Count == 1 ? " " : "s ") + what + ": " + string.Join(", ", names.Take(4)) + (names.Count > 4 ? "…" : "");
        }

        private static string ChangeName(PartChange change)
        {
            switch (change)
            {
                case PartChange.Reshaped: return "Reshaped";
                case PartChange.Moved: return "Placed";
                case PartChange.Materials: return "Materials";
                case PartChange.Shapes: return "Blendshapes";
                case PartChange.Added: return "Added";
                case PartChange.Removed: return "Removed";
                default: return "Same";
            }
        }

        private static Label Cell(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        private void Working(string text)
        {
            stage.Clear();
            details.Clear();
            status.text = text;
            var track = new VisualElement();
            track.AddToClassList("ugm-progress");
            var sweep = new VisualElement();
            sweep.AddToClassList("ugm-progress__sweep");
            track.Add(sweep);
            stage.Add(track);
            sweep.schedule.Execute(() => sweep.ToggleInClassList("ugm-progress__sweep--end")).Every(650);
        }

        private void Message(string title, string text)
        {
            stage.Clear();
            status.text = title + (string.IsNullOrEmpty(text) ? string.Empty : ": " + text);
        }

        private static void DeleteFolder(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private void DisposePreviews()
        {
            leftPreview?.Cleanup();
            rightPreview?.Cleanup();
            leftPreview = rightPreview = null;
            if (changesMaterial != null) Object.DestroyImmediate(changesMaterial);
            if (plainMaterial != null) Object.DestroyImmediate(plainMaterial);
            changesMaterial = plainMaterial = null;
            shown = null;
        }
    }
}
