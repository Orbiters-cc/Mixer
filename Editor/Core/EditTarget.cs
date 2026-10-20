using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Meshes;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// One renderer being edited: its original mesh (untouched), the working mesh shown in its place, the document
    /// the tools edit, and the world-space picture of it brushes aim at (positions through the skinning and the
    /// other blendshapes, indexed by a BVH built when a brush first needs it).
    /// </summary>
    internal sealed class EditTarget
    {
        public readonly Renderer Renderer;
        public readonly MeshFilter Filter;
        public readonly Mesh Original;
        public readonly Mesh Working;
        public readonly MeshDocument Document;
        public readonly string Id;
        /// <summary>The renderer's instance id, to find it again when an undo brings it back.</summary>
        public readonly int RendererId;
        public SkinningMap Skin;
        /// <summary>World position of every vertex as shown (active layer + the other shapes' weights, skinned).</summary>
        public Vector3[] World;
        private readonly Vector3[] display;
        private Vector3[] shapeOffset;
        private int shapeOffsetHash;
        private float[] savedWeight;
        private MirrorMap mirror;
        private int flushedRevision = -1;
        private int flushes;
        private MeshBvh bvh;
        private bool worldStale;

        /// <summary>The original's id (<see cref="SceneGraphExporter.IdOf"/>), read once.</summary>
        public string OriginalId => originalId ?? (originalId = SceneGraphExporter.IdOf(Original));
        private string originalId;

        public string Name => Renderer != null ? Renderer.name : "(gone)";
        public bool Alive => Renderer != null && Working != null;
        public SkinnedMeshRenderer Skinned => Renderer as SkinnedMeshRenderer;
        public MirrorMap Mirror => mirror ?? (mirror = MirrorMap.Build(Document.OriginalPositions, Document.Neighbors, Document.Original.bounds.size.magnitude * 1e-4f));

        /// <summary>
        /// The triangles of the world picture, for brushes and picking (built on first use). After a weight upload during a
        /// stroke the picture catches up at the stroke's end or the next <see cref="RefreshWorld"/>, not on every query.
        /// </summary>
        public MeshBvh Bvh
        {
            get
            {
                if (bvh == null) { bvh = MeshBvh.Build(World, Document.Triangles); bvh.Mirrored = Mirrored(); }
                return bvh;
            }
        }

        // A negative scale turns the front faces round in the world picture: raycasts and face normals follow.
        private bool Mirrored()
        {
            var toWorld = Skin?.ToWorld;
            float determinant = toWorld != null && toWorld.Length > 0 ? toWorld[0].determinant : Renderer != null ? Renderer.transform.localToWorldMatrix.determinant : 1f;
            return determinant < 0f;
        }

        public EditTarget(Renderer renderer, Mesh original, Mesh working)
        {
            Renderer = renderer;
            RendererId = renderer.GetInstanceID();
            Filter = renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>() : null;
            Original = original;
            Working = working;
            // The object's id (what the link and the hierarchy call the node), with a fallback for objects that have none.
            Id = SceneGraphExporter.IdOf(renderer.gameObject);
            Document = new MeshDocument(original, working);
            display = new Vector3[Document.VertexCount];
            Document.DisplayPositions(display);
            World = new Vector3[Document.VertexCount];
            Skin = SkinningMap.For(renderer, working);
            RebuildWorld();
        }

        /// <summary>The mesh the renderer shows now.</summary>
        public Mesh Shown => Filter != null ? Filter.sharedMesh : Skinned != null ? Skinned.sharedMesh : null;

        public void Show(Mesh mesh)
        {
            if (Filter != null) Filter.sharedMesh = mesh; else if (Skinned != null) Skinned.sharedMesh = mesh;
        }

        /// <summary>Brings the world picture up to date with the pose and shape weights; true when it changed.</summary>
        public bool RefreshWorld()
        {
            bool changed = Skin.Refresh(Renderer, Working) || worldStale;
            worldStale = false;
            changed |= RefreshShapeOffset();
            if (changed) RebuildWorld();
            if (bvh != null) bvh.Mirrored = Mirrored();
            return changed;
        }

        /// <summary>The skin changed in a way the next <see cref="RefreshWorld"/> (or a brush) catches up with, not now.</summary>
        public void MarkWorldStale()
        {
            Skin.MarkStale();
            worldStale = true;
        }

        // The working mesh's shapes the renderer must not show: the edited layer (shown through the working vertices),
        // the original a renamed active layer carries, and deleted shapes no live shape carries.
        private bool Hidden(string name)
        {
            var doc = Document;
            if (name == doc.Layer) return true;
            if (!doc.DeletedShapes.Contains(name)) return false;
            if (doc.Layer != null && doc.ShapeEdits.TryGetValue(doc.Layer, out var active) && active.Source == name) return true;
            return !doc.IsCarried(name);
        }

        // The other blendshapes at their renderer weights, in mesh space, added before skinning as Unity does.
        private bool RefreshShapeOffset()
        {
            var skinned = Skinned;
            int hash = 17;
            var mesh = Working;
            int count = mesh != null ? mesh.blendShapeCount : 0;
            if (skinned != null)
            {
                for (int i = 0; i < count; i++)
                {
                    float w = skinned.GetBlendShapeWeight(i);
                    if (w != 0f) hash = unchecked(hash * 31 + i * 7919 + (int)(w * 1000f));
                }
            }
            hash = unchecked(hash * 31 + (Document.Layer != null ? Document.Layer.GetHashCode() : 0) + Document.DeletedShapes.Count * 104729);
            if (shapeOffset != null && hash == shapeOffsetHash) return false;
            shapeOffsetHash = hash;
            if (shapeOffset == null) shapeOffset = new Vector3[Document.VertexCount];
            Array.Clear(shapeOffset, 0, shapeOffset.Length);
            if (skinned == null) return true;
            var deltas = new Vector3[Document.VertexCount];
            for (int i = 0; i < count; i++)
            {
                float w = skinned.GetBlendShapeWeight(i) / 100f;
                if (w == 0f) continue;
                string name = mesh.GetBlendShapeName(i);
                if (Hidden(name)) continue;
                int frame = mesh.GetBlendShapeFrameCount(i) - 1;
                mesh.GetBlendShapeFrameVertices(i, frame, deltas, null, null);
                for (int v = 0; v < deltas.Length; v++) shapeOffset[v] += deltas[v] * w;
            }
            return true;
        }

        private void RebuildWorld()
        {
            Document.DisplayPositions(display);
            var toWorld = Skin.ToWorld;
            for (int v = 0; v < World.Length; v++)
            {
                var p = display[v];
                if (shapeOffset != null) p += shapeOffset[v];
                World[v] = toWorld[v].MultiplyPoint3x4(p);
            }
            bvh?.Refit();
        }

        /// <summary>Applies the document's dirty vertices to the world picture (after a brush step or an undo).</summary>
        public void AfterEdit(IReadOnlyList<int> moved, bool fullRefresh = false)
        {
            if (fullRefresh || worldStale)
            {
                if (worldStale) { Skin.Refresh(Renderer, Working); worldStale = false; }
                RefreshShapeOffset();
                RebuildWorld();
                return;
            }
            var toWorld = Skin.ToWorld;
            for (int i = 0; i < moved.Count; i++)
            {
                int v = moved[i];
                var p = Document.LayerPosition(v);
                if (shapeOffset != null) p += shapeOffset[v];
                World[v] = toWorld[v].MultiplyPoint3x4(p);
            }
            bvh?.RefitVertices(moved, Document.Neighbors);
        }

        /// <summary>The active layer changed: the shown positions, normals and the other shapes' weights are different now.</summary>
        public void AfterLayerChange()
        {
            ApplyLayerWeight();
            shapeOffsetHash = 0;
            RefreshShapeOffset();
            RebuildWorld();
            Document.MarkAllDirty();
            RecomputeAllNormals();
        }

        // While a shape is edited the renderer shows it at 100 % through the working vertices, so its own weight is held
        // at 0 (else it would show twice); the weight it had comes back when another layer is picked. Deleted shapes are
        // held at 0 too, as they will not be in the commit.
        private void ApplyLayerWeight()
        {
            var skinned = Skinned;
            if (skinned == null || Working == null) return;
            if (savedWeight == null || savedWeight.Length != Working.blendShapeCount) { savedWeight = new float[Working.blendShapeCount]; for (int i = 0; i < savedWeight.Length; i++) savedWeight[i] = float.NaN; }
            for (int i = 0; i < Working.blendShapeCount; i++)
            {
                string name = Working.GetBlendShapeName(i);
                if (Hidden(name))
                {
                    if (float.IsNaN(savedWeight[i])) savedWeight[i] = skinned.GetBlendShapeWeight(i);
                    skinned.SetBlendShapeWeight(i, 0f);
                }
                else if (!float.IsNaN(savedWeight[i]))
                {
                    skinned.SetBlendShapeWeight(i, savedWeight[i]);
                    savedWeight[i] = float.NaN;
                }
            }
        }

        /// <summary>Puts the renderer's blendshape weights back as they were before layers were edited.</summary>
        public void RestoreWeights()
        {
            var skinned = Skinned;
            if (skinned == null || savedWeight == null || Working == null) return;
            for (int i = 0; i < savedWeight.Length && i < Working.blendShapeCount; i++)
                if (!float.IsNaN(savedWeight[i])) { skinned.SetBlendShapeWeight(i, savedWeight[i]); savedWeight[i] = float.NaN; }
        }

        /// <summary>
        /// The renderer's own weight of each blendshape held at 0 while it is the edited layer (NaN: not held): what a
        /// scene save, an upload or a script reload must keep instead of the 0 shown.
        /// </summary>
        public float[] HeldWeights => savedWeight;

        /// <summary>Gives the renderer its held weights back for a moment (a scene save); <see cref="HoldLayerWeight"/> holds them again.</summary>
        public void ReleaseLayerWeight() => RestoreWeights();

        /// <summary>
        /// Holds the edited layer's weight at 0 again, remembering the renderer's own; after a script reload,
        /// <paramref name="remembered"/> (the <see cref="HeldWeights"/> from before) says what the renderer had, since it shows 0 now.
        /// </summary>
        public void HoldLayerWeight(float[] remembered = null)
        {
            if (remembered != null && Working != null && remembered.Length == Working.blendShapeCount) savedWeight = (float[])remembered.Clone();
            ApplyLayerWeight();
        }

        /// <summary>
        /// The renderer's weight for each shape a commit writes, by name: what it shows now for a shape kept or renamed
        /// (with the held weights given back), 0 for a new one. Call after <see cref="RestoreWeights"/>.
        /// </summary>
        public Dictionary<string, float> WeightsByName()
        {
            var result = new Dictionary<string, float>();
            var skinned = Skinned;
            if (skinned == null || Working == null) return result;
            var doc = Document;
            foreach (string name in doc.LiveShapes())
            {
                string source = doc.ShapeEdits.TryGetValue(name, out var layer) ? layer.Source : name;
                int index = source != null ? Working.GetBlendShapeIndex(source) : -1;
                float weight = index >= 0 ? skinned.GetBlendShapeWeight(index) : 0f;
                if (index >= 0 && savedWeight != null && index < savedWeight.Length && !float.IsNaN(savedWeight[index])) weight = savedWeight[index];
                result[name] = weight;
            }
            return result;
        }

        /// <summary>
        /// The normals as a commit would make them, for the active layer: the original's, recomputed where the base
        /// moved, plus the active shape's turn of the welded normals (what its normal deltas will be).
        /// </summary>
        public void RecomputeAllNormals()
        {
            var doc = Document;
            doc.DisplayPositions(display);
            var normals = doc.Normals;
            Array.Copy(doc.OriginalNormals, normals, normals.Length);
            bool layerMoves = false;
            Vector3[] deltas = null;
            if (doc.Layer != null && doc.ShapeEdits.TryGetValue(doc.Layer, out var layer))
            {
                deltas = layer.Deltas;
                for (int v = 0; v < deltas.Length && !layerMoves; v++) layerMoves = deltas[v].sqrMagnitude > 0f;
            }
            if (doc.MovedBase.Count > 0 || layerMoves)
            {
                var weldedBase = MeshBuilder.WeldedNormals(doc.Positions, doc);
                if (doc.MovedBase.Count > 0)
                    foreach (int v in MeshBuilder.MovedRing(doc)) normals[v] = weldedBase[v];
                if (layerMoves)
                {
                    var shaped = MeshBuilder.WeldedNormals(display, doc);
                    for (int v = 0; v < normals.Length; v++)
                    {
                        var d = shaped[v] - weldedBase[v];
                        if (d.sqrMagnitude < 1e-12f) continue;
                        var n = normals[v] + d;
                        float length = Mathf.Sqrt(n.sqrMagnitude);
                        if (length > 0f) normals[v] = n / length;
                    }
                }
            }
            for (int v = 0; v < normals.Length; v++) doc.Dirty.Add(v);
        }

        /// <summary>Uploads the dirty vertices to the working mesh (once a frame); true when something was sent.</summary>
        public bool Flush()
        {
            var doc = Document;
            if (doc.Dirty.Count == 0 && flushedRevision == doc.Revision) return false;
            if (Working == null) return false;
            if (doc.Dirty.Count > 0)
            {
                doc.DisplayPositions(display);
                Working.SetVertices(display, 0, display.Length, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices | UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers);
                Working.SetNormals(doc.Normals, 0, doc.Normals.Length, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices | UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers);
                doc.Dirty.Clear();
                flushes++;
                if (doc.BoundsDirty && flushes % 10 == 0) { Working.RecalculateBounds(); doc.BoundsDirty = false; }
            }
            flushedRevision = doc.Revision;
            return true;
        }

        /// <summary>Per-vertex weights shown by the weight paint overlay (a group's), or null for the bones'.</summary>
        public float[] OverlayWeights;
        public WeightChannel ShownChannel = WeightChannel.None;
        private double lastWeightUpload;

        /// <summary>
        /// Uploads the influences to the working mesh when they changed (throttled unless forced) and refreshes the
        /// overlay. Forced (a stroke's end, an undo) it also brings the skinning and the world picture up to date; while a
        /// stroke paints they catch up at its end, so a stroke over a large mesh stays light.
        /// </summary>
        public void FlushWeights(bool force)
        {
            var doc = Document;
            if (!doc.WeightsDirty || Working == null || !doc.HasSkin) return;
            double now = EditorApplication.timeSinceStartup;
            if (!force && now - lastWeightUpload < 0.08) return;
            lastWeightUpload = now;
            doc.BuildBoneWeights(out var perVertex, out var weights);
            try { Working.SetBoneWeights(perVertex, weights); }
            finally { perVertex.Dispose(); weights.Dispose(); }
            doc.WeightsDirty = false;
            Skin.InvalidateWeights();
            if (force)
            {
                Skin.Refresh(Renderer, Working);
                worldStale = false;
                RebuildWorld();
            }
            else worldStale = true;
            RefreshOverlay();
        }

        /// <summary>
        /// Uploads the document's bind poses to the working mesh. The world picture catches up when a brush or the next
        /// <see cref="RefreshWorld"/> needs it (a bone drag in rest-pose editing uploads every frame for every rig mate).
        /// </summary>
        public void FlushBindposes()
        {
            var doc = Document;
            if (!doc.BindPosesDirty || Working == null) return;
            Working.bindposes = doc.BindPoses;
            doc.BindPosesDirty = false;
            MarkWorldStale();
        }

        /// <summary>A vertex group changed: the overlay shows it when it is the shown channel.</summary>
        public void GroupsChanged()
        {
            if (ShownChannel.IsGroup) RefreshOverlay();
        }

        /// <summary>Points the Toolkit's weight paint overlay at a bone or group of this target, or turns it off with None.</summary>
        public void ShowChannel(WeightChannel channel)
        {
            ShownChannel = channel;
            var skinned = Skinned;
            if (skinned == null) return;
            if (channel.IsNone) { Orbiters.Toolkit.Editor.XRay.XRayWeightPaintService.Unpin(skinned); OverlayWeights = null; return; }
            RefreshOverlay();
        }

        private void RefreshOverlay()
        {
            var skinned = Skinned;
            if (skinned == null || ShownChannel.IsNone) return;
            if (ShownChannel.IsGroup)
            {
                var group = Document.Group(ShownChannel.Group);
                if (OverlayWeights == null || OverlayWeights.Length != Document.VertexCount) OverlayWeights = new float[Document.VertexCount];
                Array.Clear(OverlayWeights, 0, OverlayWeights.Length);
                if (group != null) foreach (var pair in group.Weights) if (pair.Key >= 0 && pair.Key < OverlayWeights.Length) OverlayWeights[pair.Key] = pair.Value;
                Orbiters.Toolkit.Editor.XRay.XRayWeightPaintService.Pin(skinned, null, OverlayWeights);
            }
            else
            {
                var bones = skinned.bones;
                var bone = ShownChannel.Bone >= 0 && ShownChannel.Bone < bones.Length ? bones[ShownChannel.Bone] : null;
                Orbiters.Toolkit.Editor.XRay.XRayWeightPaintService.Pin(skinned, bone != null ? new[] { bone } : new Transform[0], null);
            }
        }

        /// <summary>At a stroke's end: bounds and the normals around what moved.</summary>
        public void FinishStroke(IEnumerable<int> moved)
        {
            RefreshNormals(moved);
            Working.RecalculateBounds();
            Document.BoundsDirty = false;
            Flush();
        }

        /// <summary>The normals around the given vertices, from the shown positions (after an undo, a redo or a stroke).</summary>
        public void RefreshNormals(IEnumerable<int> vertices)
        {
            Document.DisplayPositions(display);
            Document.RefreshNormals(vertices, display);
        }

        /// <summary>The display positions (active layer) as last computed.</summary>
        public Vector3[] Display => display;
    }
}
