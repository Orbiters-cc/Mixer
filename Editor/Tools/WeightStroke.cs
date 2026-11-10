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
        // A dab works in the target's stroke scratch (marks per welded vertex instead of sets) and these lists, kept for
        // the stroke: a dab allocates nothing of its own.
        private readonly StrokeScratch scratch;
        private readonly List<int> welded = new List<int>();
        private readonly List<float> weights = new List<float>();
        // Each welded vertex and channel a dab paints, once: the strongest falloff that reaches it (straight or through its
        // mirror) and whose blur target it takes. A mirror pair both under the brush on the same channel (a vertex group, a
        // bone on the middle line) is painted once, not once for each.
        private readonly List<(int w, bool mirror, float f, int blurFrom)> plan = new List<(int, bool, float, int)>();
        // The groups a dab reads and writes, looked up once a dab (by name, in a list).
        private VertexGroup channelGroup, mirrorGroup;
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
            scratch = target.Scratch;
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

        public EditTarget Target => target;

        /// <summary>The pointer held still: the brush paints again where it is, without the spacing a moving pointer needs.</summary>
        public void Dab(Vector3 surface, Vector3 normal, bool invert)
        {
            if (!begun) return;
            lastSample = surface;
            Center = surface;
            Normal = normal;
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
            SendToLink(edit);
            return edit;
        }

        // Blender gets every channel the stroke changed: the painted group, or every bone whose weight moved on the
        // touched vertices (normalizing scales the vertex's other bones, which Blender must get too).
        private void SendToLink(HistoryEntry edit)
        {
            if (edit is GroupWeightEdit group) { MixerLinkService.SendWeights(target, channel, new List<int>(group.Indices)); return; }
            if (!(edit is WeightEdit weights)) return;
            var byBone = new Dictionary<int, List<int>>();
            void Add(int bone, int v)
            {
                if (bone < 0) return;
                if (!byBone.TryGetValue(bone, out var list)) byBone[bone] = list = new List<int>();
                if (list.Count == 0 || list[list.Count - 1] != v) list.Add(v);
            }
            for (int i = 0; i < weights.Indices.Length; i++)
            {
                int v = weights.Indices[i];
                foreach (var influence in weights.Before[i]) Add(influence.Bone, v);
                foreach (var influence in weights.After[i]) Add(influence.Bone, v);
            }
            foreach (var pair in byBone) MixerLinkService.SendWeights(target, WeightChannel.OfBone(pair.Key), pair.Value);
        }

        private void Apply(bool invert)
        {
            Steps++;
            var doc = target.Document;
            var n = doc.Neighbors;
            StrokeGather.Sphere(target, scratch, Center, radius, falloff, false, welded, weights);
            if (welded.Count == 0) return;
            plan.Clear();
            channelGroup = channel.IsGroup ? doc.Group(channel.Group) : null;
            mirrorGroup = mirrorChannel.IsGroup ? doc.Group(mirrorChannel.Group) : null;
            int mark = scratch.NextMark();
            // Blur reads the neighbours' current weights before anything changes.
            if (kind == WeightBrushKind.Blur)
            {
                foreach (int w in welded)
                {
                    var adjacent = n.Adjacent[w];
                    if (adjacent.Length == 0) continue;
                    float mean = 0f;
                    foreach (int a in adjacent) mean += Read(n.Representative[a], channel, channelGroup);
                    scratch.ValueOf[w] = mean / adjacent.Length;
                    scratch.ValueMark[w] = mark;
                }
            }
            float goal = invert ? 0f : value;
            bool sameChannel = mirrorChannel.Bone == channel.Bone && mirrorChannel.Group == channel.Group;
            for (int i = 0; i < welded.Count; i++)
            {
                int w = welded[i];
                Plan(mark, w, false, weights[i], -1);
                if (!paintMirror) continue;
                int other = target.Mirror.Of[w];
                if (other < 0 || other == w) continue;
                Plan(mark, other, !sameChannel, weights[i], w);
            }
            foreach (var (w, mirror, f, blurFrom) in plan) PaintWelded(mark, w, f, goal, mirror, blurFrom);
            if (channel.IsGroup) target.GroupsChanged();
            else if (EditorTime() - lastFlush > 0.08) { lastFlush = EditorTime(); target.FlushWeights(force: false); }
            doc.Revision++;
        }

        private static double EditorTime() => UnityEditor.EditorApplication.timeSinceStartup;

        private void Plan(int mark, int w, bool mirror, float f, int blurFrom)
        {
            int key = 2 * w + (mirror ? 1 : 0);
            if (scratch.PlanMark[key] == mark)
            {
                int at = scratch.PlanAt[key];
                if (f > plan[at].f) plan[at] = (w, mirror, f, blurFrom);
                return;
            }
            scratch.PlanMark[key] = mark;
            scratch.PlanAt[key] = plan.Count;
            plan.Add((w, mirror, f, blurFrom));
        }

        private void PaintWelded(int mark, int w, float f, float goal, bool mirror, int blurFrom)
        {
            var on = mirror ? mirrorChannel : channel;
            var doc = target.Document;
            var n = doc.Neighbors;
            int rep = n.Representative[w];
            float current = Read(rep, on, mirror ? mirrorGroup : channelGroup);
            float next;
            switch (kind)
            {
                case WeightBrushKind.Replace: next = Mathf.Lerp(current, goal, Mathf.Clamp01(f * strength * 4f)); break;
                case WeightBrushKind.Blur:
                {
                    int from = blurFrom >= 0 ? blurFrom : w;
                    if (scratch.ValueMark[from] != mark) return;
                    next = Mathf.Lerp(current, scratch.ValueOf[from], strength * f);
                    break;
                }
                default: next = Mathf.Lerp(current, goal, strength * f * 0.5f); break;
            }
            if (Mathf.Abs(next - current) < 1e-5f) return;
            foreach (int v in n.Members[w])
            {
                recorder.Touch(v);
                Write(v, on, next, mirror);
            }
        }

        private float Read(int v, WeightChannel on, VertexGroup group)
        {
            if (on.IsGroup) return group != null && group.Weights.TryGetValue(v, out float w) ? w : 0f;
            return target.Document.BoneWeight(v, on.Bone);
        }

        private void Write(int v, WeightChannel on, float weight, bool mirror)
        {
            if (on.IsGroup)
            {
                var g = mirror ? mirrorGroup : channelGroup;
                if (g == null)
                {
                    var doc = target.Document;
                    g = doc.Group(on.Group);
                    if (g == null) { g = new VertexGroup { Name = on.Group }; doc.Groups.Add(g); }
                    // The same group both ways (a group's mirror is itself).
                    if (on.Group == channel.Group) channelGroup = g;
                    if (on.Group == mirrorChannel.Group) mirrorGroup = g;
                }
                if (weight > 0.0005f) g.Weights[v] = Mathf.Clamp01(weight); else g.Weights.Remove(v);
                return;
            }
            target.Document.SetBoneWeight(v, on.Bone, weight, normalize);
        }
    }
}
