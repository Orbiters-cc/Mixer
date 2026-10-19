using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// One stroke of a brush on a target: finds the vertices under the brush in the world, asks the brush how to
    /// move them, pushes the moves back into mesh space through the skinning, mirrors them when symmetry is on, and
    /// records what moved for Ctrl+Z. Grab brushes keep the vertices of their first step and follow the cursor on the
    /// view plane; the others sample the surface at every step.
    /// </summary>
    internal sealed class SculptStroke
    {
        private readonly EditTarget target;
        private readonly Brush brush;
        private readonly Falloff falloff;
        private readonly bool symmetry;
        private readonly Vector3 viewDirection;
        private readonly StrokeRecorder recorder;
        private readonly BrushContext ctx = new BrushContext();
        private readonly List<int> scratch = new List<int>();
        private readonly List<int> moved = new List<int>();
        private readonly HashSet<int> movedAll = new HashSet<int>();
        private Vector3[] moves = new Vector3[256];
        private Vector3 planePoint;
        private Vector3 lastSample;
        private bool begun;

        public float Radius { get; }
        public float Strength { get; }
        public Vector3 Center => ctx.Center;
        public Vector3 Normal => ctx.Normal;
        public int Steps { get; private set; }

        public SculptStroke(EditTarget target, Brush brush, float radius, float strength, Falloff falloff, bool symmetry, Vector3 viewDirection)
        {
            this.target = target;
            this.brush = brush;
            this.falloff = falloff;
            this.symmetry = symmetry;
            this.viewDirection = viewDirection.sqrMagnitude > 0f ? viewDirection.normalized : Vector3.forward;
            Radius = radius;
            Strength = strength;
            recorder = new StrokeRecorder(target, brush.Name + (target.Document.Layer != null ? " on " + target.Document.Layer : ""));
            ctx.Target = target;
            ctx.Radius = radius;
            ctx.Strength = strength;
            ctx.ViewDirection = this.viewDirection;
        }

        public void Begin(Vector3 point, Vector3 normal, Ray ray, bool invert)
        {
            ctx.Center = point;
            ctx.Previous = point;
            ctx.Normal = normal;
            ctx.Invert = invert;
            ctx.First = true;
            planePoint = point;
            lastSample = point;
            begun = true;
            Gather(point);
            if (brush.Grabs) return; // grabs move nothing until the cursor moves
            Apply();
            ctx.First = false;
        }

        /// <summary>A pointer move during the stroke; <paramref name="surface"/> is where the ray hits the mesh, null off it.</summary>
        public void Step(Ray ray, Vector3? surface, Vector3? surfaceNormal, bool invert, bool shiftSmooth)
        {
            if (!begun) return;
            ctx.Invert = invert;
            if (brush.Grabs)
            {
                // The brush centre slides on the plane through the first point, facing the view: the surface follows the
                // cursor even where it has left the mesh.
                var plane = new Plane(-viewDirection, planePoint);
                if (!plane.Raycast(ray, out float enter)) return;
                var now = ray.GetPoint(enter);
                ctx.Previous = ctx.Center;
                ctx.Center = now;
                Apply();
                ctx.First = false;
                return;
            }
            if (surface == null) return;
            // Spacing: a step every quarter radius along the surface, so speed does not change how much is applied.
            if ((surface.Value - lastSample).magnitude < Radius * 0.25f && Steps > 0) return;
            lastSample = surface.Value;
            ctx.Previous = ctx.Center;
            ctx.Center = surface.Value;
            ctx.Normal = surfaceNormal ?? ctx.Normal;
            Gather(ctx.Center);
            Apply();
            ctx.First = false;
        }

        /// <summary>Ends the stroke: normals, bounds, history. Returns the recorded edit (null when nothing moved).</summary>
        public PositionEdit End(bool cancel)
        {
            if (!begun) return null;
            begun = false;
            var edit = recorder.Finish();
            if (edit == null) return null;
            if (cancel)
            {
                edit.Undo();
                return null;
            }
            target.FinishStroke(movedAll);
            MixerHistory.Push(edit);
            MixerSession.NoteEdited(target);
            return edit;
        }

        private void Gather(Vector3 center)
        {
            var doc = target.Document;
            var n = doc.Neighbors;
            target.Bvh.VerticesInSphere(center, Radius, scratch);
            ctx.Welded.Clear();
            ctx.Weight.Clear();
            var seen = new HashSet<int>();
            var world = target.World;
            foreach (int v in scratch)
            {
                int w = n.Weld[v];
                if (!seen.Add(w)) continue;
                float d = (world[n.Representative[w]] - center).magnitude / Radius;
                ctx.Welded.Add(w);
                ctx.Weight.Add(Brush.Curve(falloff, d));
            }
            if (moves.Length < ctx.Welded.Count) moves = new Vector3[Mathf.NextPowerOfTwo(ctx.Welded.Count)];
        }

        private void Apply()
        {
            if (ctx.Welded.Count == 0) return;
            Steps++;
            brush.Step(ctx, moves);
            var doc = target.Document;
            var n = doc.Neighbors;
            var toMesh = target.Skin.ToMesh;
            moved.Clear();
            // Mesh-space move per welded vertex; mirrored onto the other side when symmetry is on.
            var local = new Dictionary<int, Vector3>(ctx.Welded.Count * 2);
            for (int i = 0; i < ctx.Welded.Count; i++)
            {
                var m = moves[i];
                if (m.sqrMagnitude < 1e-16f) continue;
                int w = ctx.Welded[i];
                var d = toMesh[n.Representative[w]].MultiplyVector(m);
                local[w] = local.TryGetValue(w, out var existing) ? existing + d : d;
            }
            if (symmetry)
            {
                var mirror = target.Mirror;
                var mirrored = new Dictionary<int, Vector3>(local.Count);
                foreach (var pair in local)
                {
                    int other = mirror.Of[pair.Key];
                    if (other < 0) continue;
                    var d = new Vector3(-pair.Value.x, pair.Value.y, pair.Value.z);
                    if (other == pair.Key) { mirrored[other] = new Vector3(0f, pair.Value.y, pair.Value.z) - pair.Value; continue; }
                    mirrored[other] = mirrored.TryGetValue(other, out var e) ? e + d : d;
                }
                foreach (var pair in mirrored) local[pair.Key] = local.TryGetValue(pair.Key, out var e) ? e + pair.Value : pair.Value;
            }
            foreach (var pair in local)
            {
                foreach (int v in n.Members[pair.Key])
                {
                    recorder.Touch(v);
                    doc.Move(v, pair.Value);
                    moved.Add(v);
                    movedAll.Add(v);
                }
            }
            if (moved.Count == 0) return;
            doc.MarkEdited(moved);
            target.AfterEdit(moved);
            doc.RefreshNormals(moved, target.Display);
        }
    }
}
