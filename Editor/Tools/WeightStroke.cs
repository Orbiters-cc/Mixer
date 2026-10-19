using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    internal enum WeightBrushKind { Draw, Blur, Replace }

    /// <summary>What a weight stroke paints: a bone of the renderer, or a vertex group kept beside the mesh.</summary>
    internal struct WeightChannel
    {
        public int Bone;
        public string Group;
        public bool IsGroup => Group != null;
        public bool IsNone => Group == null && Bone < 0;
        public static WeightChannel None => new WeightChannel { Bone = -1 };
        public static WeightChannel OfBone(int bone) => new WeightChannel { Bone = bone };
        public static WeightChannel OfGroup(string group) => new WeightChannel { Bone = -1, Group = group };
    }

    /// <summary>
    /// One stroke of a weight brush: Draw moves the channel's weight under the brush toward the brush value (Ctrl
    /// toward 0), Replace sets it, Blur averages it with the neighbours'. Bone weights can keep their sum at 1 (the
    /// other bones scaled); with symmetry the mirror vertices take the same on the mirror bone when one exists.
    /// </summary>
    internal sealed class WeightStroke
    {
        private readonly EditTarget target;
        private readonly WeightBrushKind kind;
        private readonly WeightChannel channel;
        private readonly WeightChannel mirrorChannel;
        private readonly float radius, strength, value;
        private readonly Falloff falloff;
        private readonly bool symmetry, normalize;
        private readonly WeightRecorder recorder;
        private readonly List<int> scratch = new List<int>();
        private readonly HashSet<int> touchedAll = new HashSet<int>();
        private Vector3 lastSample;
        private bool begun;
        private double lastFlush;
        public Vector3 Center { get; private set; }
        public Vector3 Normal { get; private set; }
        public int Steps { get; private set; }

        public WeightStroke(EditTarget target, WeightBrushKind kind, WeightChannel channel, float radius, float strength, float value, Falloff falloff, bool symmetry, bool normalize)
        {
            this.target = target;
            this.kind = kind;
            this.channel = channel;
            this.radius = radius;
            this.strength = strength;
            this.value = value;
            this.falloff = falloff;
            this.symmetry = symmetry;
            this.normalize = normalize && !channel.IsGroup;
            mirrorChannel = symmetry && !channel.IsGroup ? WeightChannel.OfBone(MirrorBone(target, channel.Bone)) : channel;
            string name = channel.IsGroup ? channel.Group : BoneName(target, channel.Bone);
            recorder = new WeightRecorder(target, channel.Group, kind + " weights of " + name);
        }

        public static string BoneName(EditTarget target, int bone)
        {
            var skinned = target.Skinned;
            if (skinned == null || bone < 0 || bone >= skinned.bones.Length || skinned.bones[bone] == null) return "bone " + bone;
            return skinned.bones[bone].name;
        }

        /// <summary>The bone on the other side by name (Left/Right, _L/_R, .L/.R, L_/R_), or the same bone.</summary>
        public static int MirrorBone(EditTarget target, int bone)
        {
            var skinned = target.Skinned;
            if (skinned == null || bone < 0 || bone >= skinned.bones.Length || skinned.bones[bone] == null) return bone;
            string name = skinned.bones[bone].name;
            string mirrored = MirrorName(name);
            if (mirrored == name) return bone;
            var bones = skinned.bones;
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && bones[i].name == mirrored) return i;
            return bone;
        }

        internal static string MirrorName(string name)
        {
            var pairs = new[] { ("Left", "Right"), ("left", "right"), ("_L", "_R"), (".L", ".R"), ("_l", "_r"), (".l", ".r"), (" L", " R"), ("L_", "R_"), ("l_", "r_") };
            foreach (var (a, b) in pairs)
            {
                if (name.EndsWith(a, StringComparison.Ordinal)) return name.Substring(0, name.Length - a.Length) + b;
                if (name.EndsWith(b, StringComparison.Ordinal)) return name.Substring(0, name.Length - b.Length) + a;
                if (name.StartsWith(a, StringComparison.Ordinal) && (a == "Left" || a == "left" || a == "L_" || a == "l_")) return b + name.Substring(a.Length);
                if (name.StartsWith(b, StringComparison.Ordinal) && (b == "Right" || b == "right" || b == "R_" || b == "r_")) return a + name.Substring(b.Length);
                int at = name.IndexOf(a + "_", StringComparison.Ordinal); if (at >= 0 && (a == "Left" || a == "left")) return name.Substring(0, at) + b + name.Substring(at + a.Length);
                at = name.IndexOf(b + "_", StringComparison.Ordinal); if (at >= 0 && (b == "Right" || b == "right")) return name.Substring(0, at) + a + name.Substring(at + b.Length);
            }
            return name;
        }

        public void Begin(Vector3 point, Vector3 normal, bool invert)
        {
            Center = point; Normal = normal; lastSample = point; begun = true;
            Apply(invert);
        }

        public void Step(Vector3? surface, Vector3? surfaceNormal, bool invert)
        {
            if (!begun || surface == null) return;
            if ((surface.Value - lastSample).magnitude < radius * 0.25f && Steps > 0) return;
            lastSample = surface.Value;
            Center = surface.Value;
            Normal = surfaceNormal ?? Normal;
            Apply(invert);
        }

        public HistoryEntry End(bool cancel)
        {
            if (!begun) return null;
            begun = false;
            var edit = recorder.Finish();
            if (edit == null) return null;
            if (cancel) { edit.Undo(); return null; }
            if (channel.IsGroup) target.GroupsChanged(); else target.FlushWeights(force: true);
            MixerHistory.Push(edit);
            MixerSession.NoteEdited(target);
            MixerLinkService.SendWeights(target, channel, new List<int>(touchedAll));
            if (symmetry && !mirrorChannel.Equals(channel)) MixerLinkService.SendWeights(target, mirrorChannel, new List<int>(touchedAll));
            return edit;
        }

        private void Apply(bool invert)
        {
            Steps++;
            var doc = target.Document;
            var n = doc.Neighbors;
            target.Bvh.VerticesInSphere(Center, radius, scratch);
            if (scratch.Count == 0) return;
            var world = target.World;
            var seen = new HashSet<int>();
            var welded = new List<int>();
            var weights = new List<float>();
            foreach (int v in scratch)
            {
                int w = n.Weld[v];
                if (!seen.Add(w)) continue;
                int rep = n.Representative[w];
                float d = (world[rep] - Center).magnitude / radius;
                float f = Brush.Curve(falloff, d) * doc.Free(rep);
                if (f <= 0f) continue;
                welded.Add(w);
                weights.Add(f);
            }
            // Blur reads the neighbours' current weights before anything changes.
            var blurTargets = new Dictionary<int, float>();
            if (kind == WeightBrushKind.Blur)
            {
                foreach (int w in welded)
                {
                    var adjacent = n.Adjacent[w];
                    if (adjacent.Length == 0) continue;
                    float mean = 0f;
                    foreach (int a in adjacent) mean += Read(n.Representative[a], channel);
                    blurTargets[w] = mean / adjacent.Length;
                }
            }
            float goal = invert ? 0f : value;
            for (int i = 0; i < welded.Count; i++)
            {
                int w = welded[i];
                float f = weights[i];
                PaintWelded(w, f, goal, blurTargets, channel);
                if (!symmetry) continue;
                int other = target.Mirror.Of[w];
                if (other < 0 || other == w) continue;
                PaintWelded(other, f, goal, blurTargets, mirrorChannel, w);
            }
            if (channel.IsGroup) target.GroupsChanged();
            else if (EditorTime() - lastFlush > 0.08) { lastFlush = EditorTime(); target.FlushWeights(force: false); }
            doc.Revision++;
        }

        private static double EditorTime() => UnityEditor.EditorApplication.timeSinceStartup;

        private void PaintWelded(int w, float f, float goal, Dictionary<int, float> blurTargets, WeightChannel on, int blurFrom = -1)
        {
            var doc = target.Document;
            var n = doc.Neighbors;
            int rep = n.Representative[w];
            float current = Read(rep, on);
            float next;
            switch (kind)
            {
                case WeightBrushKind.Replace: next = Mathf.Lerp(current, goal, Mathf.Clamp01(f * strength * 4f)); break;
                case WeightBrushKind.Blur:
                {
                    float mean;
                    if (!blurTargets.TryGetValue(blurFrom >= 0 ? blurFrom : w, out mean)) return;
                    next = Mathf.Lerp(current, mean, strength * f);
                    break;
                }
                default: next = Mathf.Lerp(current, goal, strength * f * 0.5f); break;
            }
            if (Mathf.Abs(next - current) < 1e-5f) return;
            foreach (int v in n.Members[w])
            {
                recorder.Touch(v);
                touchedAll.Add(v);
                Write(v, on, next);
            }
        }

        private float Read(int v, WeightChannel on)
        {
            if (on.IsGroup)
            {
                var g = target.Document.Group(on.Group);
                return g != null && g.Weights.TryGetValue(v, out float w) ? w : 0f;
            }
            return target.Document.BoneWeight(v, on.Bone);
        }

        private void Write(int v, WeightChannel on, float weight)
        {
            if (on.IsGroup)
            {
                var doc = target.Document;
                var g = doc.Group(on.Group);
                if (g == null) { g = new VertexGroup { Name = on.Group }; doc.Groups.Add(g); }
                if (weight > 0.0005f) g.Weights[v] = Mathf.Clamp01(weight); else g.Weights.Remove(v);
                return;
            }
            target.Document.SetBoneWeight(v, on.Bone, weight, normalize);
        }
    }
}
