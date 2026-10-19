using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
        private readonly bool symmetry, normalize, paintMirror;
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
            // The other side takes the stroke on the mirror bone, or on the same bone when it sits on the middle line;
            // a side bone without a mirror leaves the other side alone (painting it there would skin it to this side).
            paintMirror = symmetry && (channel.IsGroup || mirrorChannel.Bone != channel.Bone || IsCentered(target, channel.Bone));
            string name = channel.IsGroup ? channel.Group : BoneName(target, channel.Bone);
            recorder = new WeightRecorder(target, channel.Group, kind + " weights of " + name);
        }

        public static string BoneName(EditTarget target, int bone)
        {
            var skinned = target.Skinned;
            if (skinned == null || bone < 0 || bone >= skinned.bones.Length || skinned.bones[bone] == null) return "bone " + bone;
            return skinned.bones[bone].name;
        }

        /// <summary>
        /// The bone on the other side: the humanoid pair when the avatar is humanoid, else by name (Left/Right anywhere in
        /// it, or an L/R token: Leg_L, Hand.L, J_Bip_L_UpperArm, mixamorig:LeftArm), or the same bone when none is found.
        /// </summary>
        public static int MirrorBone(EditTarget target, int bone)
        {
            var skinned = target.Skinned;
            if (skinned == null || bone < 0 || bone >= skinned.bones.Length || skinned.bones[bone] == null) return bone;
            var bones = skinned.bones;
            var human = HumanMirror(skinned, bones[bone]);
            if (human != null)
            {
                int index = Array.IndexOf(bones, human);
                if (index >= 0) return index;
            }
            string name = bones[bone].name;
            string mirrored = MirrorName(name);
            if (mirrored == name) return bone;
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && bones[i].name == mirrored) return i;
            return bone;
        }

        // The humanoid bone on the other side (LeftUpperArm <-> RightUpperArm...), or null when the avatar is not humanoid
        // or the bone is not one of its human bones.
        private static Transform HumanMirror(SkinnedMeshRenderer skinned, Transform bone)
        {
            var animator = skinned.GetComponentInParent<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.isHuman) return null;
            foreach (HumanBodyBones human in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (human == HumanBodyBones.LastBone || animator.GetBoneTransform(human) != bone) continue;
                string name = human.ToString();
                string other = name.StartsWith("Left", StringComparison.Ordinal) ? "Right" + name.Substring(4)
                    : name.StartsWith("Right", StringComparison.Ordinal) ? "Left" + name.Substring(5) : null;
                return other != null && Enum.TryParse(other, out HumanBodyBones pair) ? animator.GetBoneTransform(pair) : null;
            }
            return null;
        }

        /// <summary>Whether a bone sits on the mesh's middle line (X = 0 in mesh space at bind time), where both sides share it.</summary>
        internal static bool IsCentered(EditTarget target, int bone)
        {
            var poses = target.Document.BindPoses;
            if (poses == null || bone < 0 || bone >= poses.Length) return true;
            var origin = poses[bone].inverse.GetColumn(3);
            return Mathf.Abs(origin.x) <= Mathf.Max(1e-4f, target.Document.Original.bounds.size.magnitude * 0.01f);
        }

        private static readonly Regex SideWord = new Regex("Left|Right|left|right|LEFT|RIGHT", RegexOptions.CultureInvariant);
        // A lone L or R between separators (or at an end): Leg_L, Hand.L, L_Arm, "Hand L", J_Bip_L_UpperArm.
        private static readonly Regex SideLetter = new Regex(@"(?<=^|[_.\s:\-])[LRlr](?=$|[_.\s:\-])", RegexOptions.CultureInvariant);

        internal static string MirrorName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string swapped = SideWord.Replace(name, m => Swap(m.Value));
            return swapped != name ? swapped : SideLetter.Replace(name, m => Swap(m.Value));
        }

        private static string Swap(string side)
        {
            switch (side)
            {
                case "Left": return "Right";
                case "Right": return "Left";
                case "left": return "right";
                case "right": return "left";
                case "LEFT": return "RIGHT";
                case "RIGHT": return "LEFT";
                case "L": return "R";
                case "R": return "L";
                case "l": return "r";
                case "r": return "l";
                default: return side;
            }
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
                if (!paintMirror) continue;
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
