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
        private readonly Dictionary<int, float> maskBefore = new Dictionary<int, float>();
        // Everything a step works in comes from the target's stroke scratch, kept between strokes: a stroke allocates
        // nothing (a garbage collection would stall the editor as the brush starts). Per welded vertex, marked with a
        // stamp instead of filling sets: what Gather saw, the mesh-space move a step gives (and its mirror).
        private readonly StrokeScratch scratch;
        private readonly BrushContext ctx;
        private readonly List<int> triangles, moved, local, mirrored;
        private readonly int[] gatherStamp, moveStamp, mirrorStamp;
        private readonly Vector3[] moveOf, mirrorOf;
        private Vector3 planePoint;
        private Vector3 lastSample;
        private bool begun;

        public string BrushName => brush.Name;
        public string TargetName => target.Name;
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
            scratch = target.Scratch;
            ctx = scratch.Context;
            triangles = scratch.Triangles; moved = scratch.Moved; local = scratch.Local; mirrored = scratch.Mirrored;
            gatherStamp = scratch.GatherMark; moveStamp = scratch.MoveMark; mirrorStamp = scratch.MirrorMark;
            moveOf = scratch.MoveOf; mirrorOf = scratch.MirrorOf;
            recorder = new StrokeRecorder(target, brush.Name + (target.Document.Layer != null ? " on " + target.Document.Layer : ""), scratch);
            ctx.Welded.Clear();
            ctx.Weight.Clear();
            ctx.Delta = 0f;
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
            target.BeginStroke();
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
            if (brush.PaintsMask)
            {
                if (maskBefore.Count == 0) return null;
                var mask = target.Document.EnsureMask();
                var maskEdit = new MaskEdit { Target = target, Label = "Mask", Indices = new int[maskBefore.Count], Before = new float[maskBefore.Count], After = new float[maskBefore.Count] };
                int k = 0;
                foreach (var pair in maskBefore) { maskEdit.Indices[k] = pair.Key; maskEdit.Before[k] = pair.Value; maskEdit.After[k] = mask[pair.Key]; k++; }
                if (cancel) { maskEdit.Undo(); return null; }
                MixerHistory.Push(maskEdit);
                MixerSession.NoteEdited(target);
                return null;
            }
            // The normals of the last moves first: a cancel then puts the recorded ones back over them.
            target.SettleNormals();
            var edit = recorder.Finish();
            if (edit == null) return null;
            if (cancel)
            {
                edit.Undo();
                return null;
            }
            target.EndStroke();
            MixerHistory.Push(edit);
            MixerSession.NoteEdited(target);
            return edit;
        }

        private int NextStamp() => scratch.NextMark();

        // The welded vertices within the radius (each once, by its representative), with their falloff and mask weight.
        private void Gather(Vector3 center)
        {
            var doc = target.Document;
            var n = doc.Neighbors;
            int mark = NextStamp();
            target.Bvh.TrianglesNear(center, Radius, triangles);
            ctx.Welded.Clear();
            ctx.Weight.Clear();
            var world = target.World;
            var tris = doc.Triangles;
            float r2 = Radius * Radius;
            float inverse = 1f / Radius;
            bool painting = brush.PaintsMask;
            foreach (int t in triangles)
            {
                for (int k = t * 3, end = k + 3; k < end; k++)
                {
                    int w = n.Weld[tris[k]];
                    if (gatherStamp[w] == mark) continue;
                    gatherStamp[w] = mark;
                    int rep = n.Representative[w];
                    var p = world[rep];
                    float dx = p.x - center.x, dy = p.y - center.y, dz = p.z - center.z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 > r2) continue;
                    float weight = Brush.Curve(falloff, (float)Math.Sqrt(d2) * inverse);
                    if (!painting) weight *= doc.Free(rep);
                    if (weight <= 0f) continue;
                    ctx.Welded.Add(w);
                    ctx.Weight.Add(weight);
                }
            }
            if (scratch.Moves.Length < ctx.Welded.Count) scratch.Moves = new Vector3[Mathf.NextPowerOfTwo(ctx.Welded.Count)];
        }

        private void Apply()
        {
            if (ctx.Welded.Count == 0) return;
            Steps++;
            if (brush.PaintsMask) { PaintMask(); return; }
            var moves = scratch.Moves;
            brush.Step(ctx, moves);
            var n = target.Document.Neighbors;
            var skin = target.Skin;
            int mark = NextStamp();
            // Mesh-space move per welded vertex; mirrored onto the other side when symmetry is on.
            local.Clear();
            for (int i = 0; i < ctx.Welded.Count; i++)
            {
                var m = moves[i];
                if (m.sqrMagnitude < 1e-16f) continue;
                int w = ctx.Welded[i];
                Add(local, moveStamp, moveOf, mark, w, skin.ToMeshVector(n.Representative[w], m));
            }
            if (symmetry)
            {
                var mirror = target.Mirror;
                mirrored.Clear();
                foreach (int w in local)
                {
                    int other = mirror.Of[w];
                    if (other < 0) continue;
                    var d = moveOf[w];
                    // A vertex on the mirror plane keeps its own move without the part across the plane.
                    if (other == w) { if (mirrorStamp[w] != mark) { mirrorStamp[w] = mark; mirrored.Add(w); } mirrorOf[w] = new Vector3(0f, d.y, d.z) - d; continue; }
                    Add(mirrored, mirrorStamp, mirrorOf, mark, other, new Vector3(-d.x, d.y, d.z));
                }
                foreach (int w in mirrored) Add(local, moveStamp, moveOf, mark, w, mirrorOf[w]);
            }
            target.MoveWelded(local, moveOf, recorder, moved);
            if (moved.Count > 0) StrokeTimings.Moved();
        }

        private static void Add(List<int> list, int[] stamps, Vector3[] values, int mark, int w, Vector3 d)
        {
            if (stamps[w] != mark) { stamps[w] = mark; values[w] = d; list.Add(w); }
            else values[w] += d;
        }

        // The mask brush: adds to the mask under it (Ctrl takes away), on every member of each welded vertex and, with
        // symmetry, on the mirror side.
        private void PaintMask()
        {
            var doc = target.Document;
            var n = doc.Neighbors;
            var mask = doc.EnsureMask();
            float amount = Strength * 0.35f * (ctx.Invert ? -1f : 1f);
            var welded = new List<int>(ctx.Welded.Count * 2);
            var weights = new List<float>(ctx.Welded.Count * 2);
            for (int i = 0; i < ctx.Welded.Count; i++) { welded.Add(ctx.Welded[i]); weights.Add(ctx.Weight[i]); }
            if (symmetry)
            {
                var mirror = target.Mirror;
                for (int i = 0; i < ctx.Welded.Count; i++)
                {
                    int other = mirror.Of[ctx.Welded[i]];
                    if (other >= 0 && other != ctx.Welded[i]) { welded.Add(other); weights.Add(ctx.Weight[i]); }
                }
            }
            for (int i = 0; i < welded.Count; i++)
            {
                foreach (int v in n.Members[welded[i]])
                {
                    if (!maskBefore.ContainsKey(v)) maskBefore[v] = mask[v];
                    mask[v] = Mathf.Clamp01(mask[v] + amount * weights[i]);
                }
            }
            doc.Revision++;
        }
    }
}
