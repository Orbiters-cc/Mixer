using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    internal enum Falloff { Smooth, Sphere, Linear, Sharp, Constant }

    /// <summary>What a brush step sees: the target, the hit, the vertices under the brush and how much each takes.</summary>
    internal sealed class BrushContext
    {
        public EditTarget Target;
        public MeshDocument Document => Target.Document;
        /// <summary>Where the brush is, in the world.</summary>
        public Vector3 Center;
        /// <summary>The surface normal under the brush (world).</summary>
        public Vector3 Normal;
        /// <summary>Where the brush was at the previous step (world); the center at the first step.</summary>
        public Vector3 Previous;
        public Vector3 ViewDirection;
        public float Radius;
        public float Strength;
        public bool Invert;
        public bool First;
        /// <summary>Welded vertices under the brush, with their falloff weight (0..1).</summary>
        public readonly List<int> Welded = new List<int>();
        public readonly List<float> Weight = new List<float>();
        public float Delta;
    }

    /// <summary>A sculpt brush: moves the welded vertices under it, in the world, by a rule of its own.</summary>
    internal abstract class Brush
    {
        public abstract string Name { get; }
        public abstract IconGlyph Icon { get; }
        public virtual string Description => "";
        /// <summary>True for brushes that keep the vertices they grabbed at the first step instead of what is under the cursor.</summary>
        public virtual bool Grabs => false;
        /// <summary>True for brushes that apply once per mouse move without spacing.</summary>
        public virtual bool Continuous => true;
        /// <summary>True for the brush that paints the mask instead of moving vertices.</summary>
        public virtual bool PaintsMask => false;

        /// <summary>Returns the world-space move of each welded vertex of <paramref name="ctx"/> into <paramref name="moves"/>.</summary>
        public abstract void Step(BrushContext ctx, Vector3[] moves);

        /// <summary>The brush's falloff curve at a normalized distance (0 at the center, 1 at the edge).</summary>
        public static float Curve(Falloff falloff, float t)
        {
            t = Mathf.Clamp01(t);
            switch (falloff)
            {
                case Falloff.Sphere: return Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
                case Falloff.Linear: return 1f - t;
                case Falloff.Sharp: { float u = 1f - t; return u * u * u; }
                case Falloff.Constant: return t < 1f ? 1f : 0f;
                default: { float u = 1f - t; return u * u * (3f - 2f * u); }
            }
        }

        /// <summary>The mean world position and normal of the vertices under the brush, weighted.</summary>
        protected static void Average(BrushContext ctx, out Vector3 center, out Vector3 normal)
        {
            var world = ctx.Target.World;
            var normals = ctx.Document.Normals;
            var rep = ctx.Document.Neighbors.Representative;
            var toWorld = ctx.Target.Skin.ToWorld;
            center = Vector3.zero; normal = Vector3.zero;
            float total = 0f;
            for (int i = 0; i < ctx.Welded.Count; i++)
            {
                int v = rep[ctx.Welded[i]];
                float w = ctx.Weight[i];
                center += world[v] * w;
                normal += toWorld[v].MultiplyVector(normals[v]) * w;
                total += w;
            }
            if (total > 0f) center /= total;
            normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : ctx.Normal;
        }

        protected static Vector3 WorldNormal(BrushContext ctx, int welded)
        {
            int v = ctx.Document.Neighbors.Representative[welded];
            var n = ctx.Target.Skin.ToWorld[v].MultiplyVector(ctx.Document.Normals[v]);
            return n.sqrMagnitude > 1e-12f ? n.normalized : ctx.Normal;
        }
    }

    internal sealed class GrabBrush : Brush
    {
        public override string Name => "Grab";
        public override IconGlyph Icon => IconGlyph.Person;
        public override string Description => "Drag the surface along with the cursor.";
        public override bool Grabs => true;

        public override void Step(BrushContext ctx, Vector3[] moves)
        {
            var delta = (ctx.Center - ctx.Previous) * ctx.Strength;
            for (int i = 0; i < ctx.Welded.Count; i++) moves[i] = delta * ctx.Weight[i];
        }
    }

    internal sealed class SmoothBrush : Brush
    {
        public override string Name => "Smooth";
        public override IconGlyph Icon => IconGlyph.Broom;
        public override string Description => "Relax the surface toward the average of its neighbours.";

        public override void Step(BrushContext ctx, Vector3[] moves)
        {
            var doc = ctx.Document;
            var n = doc.Neighbors;
            var world = ctx.Target.World;
            for (int i = 0; i < ctx.Welded.Count; i++)
            {
                int w = ctx.Welded[i];
                var adjacent = n.Adjacent[w];
                if (adjacent.Length == 0) { moves[i] = Vector3.zero; continue; }
                var mean = Vector3.zero;
                foreach (int a in adjacent) mean += world[n.Representative[a]];
                mean /= adjacent.Length;
                moves[i] = (mean - world[n.Representative[w]]) * (ctx.Strength * 0.5f * ctx.Weight[i]);
            }
        }
    }

    internal sealed class InflateBrush : Brush
    {
        public override string Name => "Inflate";
        public override IconGlyph Icon => IconGlyph.Sparkle;
        public override string Description => "Push the surface out along its normals; Ctrl pulls it in.";

        public override void Step(BrushContext ctx, Vector3[] moves)
        {
            float amount = ctx.Radius * 0.05f * ctx.Strength * (ctx.Invert ? -1f : 1f);
            for (int i = 0; i < ctx.Welded.Count; i++) moves[i] = WorldNormal(ctx, ctx.Welded[i]) * (amount * ctx.Weight[i]);
        }
    }

    internal sealed class FlattenBrush : Brush
    {
        public override string Name => "Flatten";
        public override IconGlyph Icon => IconGlyph.Grid;
        public override string Description => "Level the surface toward the plane under the brush.";

        public override void Step(BrushContext ctx, Vector3[] moves)
        {
            Average(ctx, out var center, out var normal);
            var world = ctx.Target.World;
            var rep = ctx.Document.Neighbors.Representative;
            for (int i = 0; i < ctx.Welded.Count; i++)
            {
                var p = world[rep[ctx.Welded[i]]];
                float height = Vector3.Dot(p - center, normal);
                moves[i] = -normal * (height * ctx.Strength * 0.5f * ctx.Weight[i]);
            }
        }
    }

    internal sealed class ClayBrush : Brush
    {
        public override string Name => "Clay";
        public override IconGlyph Icon => IconGlyph.Plus;
        public override string Description => "Build the surface up to a plane just above it; Ctrl carves it down.";

        public override void Step(BrushContext ctx, Vector3[] moves)
        {
            Average(ctx, out var center, out var normal);
            float sign = ctx.Invert ? -1f : 1f;
            var plane = center + normal * (ctx.Radius * 0.15f * sign);
            var world = ctx.Target.World;
            var rep = ctx.Document.Neighbors.Representative;
            for (int i = 0; i < ctx.Welded.Count; i++)
            {
                var p = world[rep[ctx.Welded[i]]];
                float height = Vector3.Dot(p - plane, normal) * sign;
                moves[i] = height < 0f ? normal * (-height * sign * ctx.Strength * 0.5f * ctx.Weight[i]) : Vector3.zero;
            }
        }
    }

    internal sealed class PinchBrush : Brush
    {
        public override string Name => "Pinch";
        public override IconGlyph Icon => IconGlyph.Chevron;
        public override string Description => "Pull the surface toward the center of the brush; Ctrl spreads it.";

        public override void Step(BrushContext ctx, Vector3[] moves)
        {
            var world = ctx.Target.World;
            var rep = ctx.Document.Neighbors.Representative;
            float sign = ctx.Invert ? -1f : 1f;
            for (int i = 0; i < ctx.Welded.Count; i++)
            {
                var p = world[rep[ctx.Welded[i]]];
                var toCenter = ctx.Center - p;
                toCenter -= ctx.Normal * Vector3.Dot(toCenter, ctx.Normal);
                moves[i] = toCenter * (sign * ctx.Strength * 0.15f * ctx.Weight[i]);
            }
        }
    }

    internal sealed class CreaseBrush : Brush
    {
        public override string Name => "Crease";
        public override IconGlyph Icon => IconGlyph.Sliders;
        public override string Description => "Pinch the surface together and push it in, for a sharp fold; Ctrl raises a ridge.";

        public override void Step(BrushContext ctx, Vector3[] moves)
        {
            var world = ctx.Target.World;
            var rep = ctx.Document.Neighbors.Representative;
            float sign = ctx.Invert ? 1f : -1f;
            float depth = ctx.Radius * 0.04f * ctx.Strength;
            for (int i = 0; i < ctx.Welded.Count; i++)
            {
                var p = world[rep[ctx.Welded[i]]];
                var toCenter = ctx.Center - p;
                toCenter -= ctx.Normal * Vector3.Dot(toCenter, ctx.Normal);
                moves[i] = toCenter * (ctx.Strength * 0.1f * ctx.Weight[i]) + ctx.Normal * (sign * depth * ctx.Weight[i]);
            }
        }
    }

    internal sealed class MaskBrush : Brush
    {
        public override string Name => "Mask";
        public override IconGlyph Icon => IconGlyph.Lock;
        public override string Description => "Paint a mask: masked vertices stay still under the other brushes. Ctrl unmasks.";
        public override bool PaintsMask => true;

        public override void Step(BrushContext ctx, Vector3[] moves)
        {
            for (int i = 0; i < ctx.Welded.Count; i++) moves[i] = Vector3.zero;
        }
    }

    internal static class Brushes
    {
        public static readonly Brush[] All =
        {
            new GrabBrush(), new SmoothBrush(), new InflateBrush(), new FlattenBrush(), new ClayBrush(), new PinchBrush(), new CreaseBrush(), new MaskBrush(),
        };

        public static Brush Named(string name)
        {
            foreach (var b in All) if (b.Name == name) return b;
            return All[0];
        }
    }
}
