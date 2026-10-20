using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// Sampled channels of a clip, written as curves with as few keys as the tolerance allows (Ramer–Douglas–Peucker
    /// on each channel, linear tangents; step channels keep only their changes). Channels that never move keep two keys.
    /// </summary>
    internal sealed class CurveSet
    {
        sealed class Channel
        {
            public EditorCurveBinding Binding;
            public float Tolerance;
            public bool Step;
            public readonly List<float> Times = new List<float>();
            public readonly List<float> Values = new List<float>();
        }

        readonly List<Channel> channels = new List<Channel>();
        readonly Dictionary<string, int> byKey = new Dictionary<string, int>();

        public int Count => channels.Count;

        static string Key(EditorCurveBinding b) => b.path + "\n" + b.type.FullName + "\n" + b.propertyName;

        /// <summary>The channel for a binding (created once).</summary>
        public int ChannelFor(EditorCurveBinding binding, float tolerance, bool step = false)
        {
            string key = Key(binding);
            if (byKey.TryGetValue(key, out int i)) return i;
            channels.Add(new Channel { Binding = binding, Tolerance = tolerance, Step = step });
            byKey[key] = channels.Count - 1;
            return channels.Count - 1;
        }

        public void Key(int channel, float time, float value)
        {
            var c = channels[channel];
            c.Times.Add(time);
            c.Values.Add(float.IsNaN(value) ? (c.Values.Count > 0 ? c.Values[c.Values.Count - 1] : 0f) : value);
        }

        /// <summary>Writes every channel to <paramref name="clip"/>; returns the number of keys kept.</summary>
        public int WriteTo(AnimationClip clip)
        {
            var bindings = new List<EditorCurveBinding>();
            var curves = new List<AnimationCurve>();
            int keys = 0;
            foreach (var c in channels)
            {
                if (c.Times.Count == 0) continue;
                var curve = c.Step ? StepCurve(c) : LinearCurve(c);
                keys += curve.length;
                bindings.Add(c.Binding);
                curves.Add(curve);
            }
            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
            return keys;
        }

        static AnimationCurve StepCurve(Channel c)
        {
            var keys = new List<Keyframe> { new Keyframe(c.Times[0], c.Values[0], float.PositiveInfinity, float.PositiveInfinity) };
            for (int i = 1; i < c.Times.Count; i++)
                if (!Mathf.Approximately(c.Values[i], c.Values[i - 1]))
                    keys.Add(new Keyframe(c.Times[i], c.Values[i], float.PositiveInfinity, float.PositiveInfinity));
            if (keys.Count == 1) keys.Add(new Keyframe(c.Times[c.Times.Count - 1], c.Values[c.Values.Count - 1], float.PositiveInfinity, float.PositiveInfinity));
            return new AnimationCurve(keys.ToArray());
        }

        static AnimationCurve LinearCurve(Channel c)
        {
            var kept = Reduce(c.Times, c.Values, c.Tolerance);
            var keys = new Keyframe[kept.Count];
            for (int k = 0; k < kept.Count; k++)
            {
                int i = kept[k];
                float inSlope = k > 0 ? Slope(c, kept[k - 1], i) : 0f;
                float outSlope = k + 1 < kept.Count ? Slope(c, i, kept[k + 1]) : 0f;
                if (k == 0) inSlope = outSlope;
                if (k + 1 == kept.Count) outSlope = inSlope;
                keys[k] = new Keyframe(c.Times[i], c.Values[i], inSlope, outSlope);
            }
            return new AnimationCurve(keys);
        }

        static float Slope(Channel c, int a, int b)
        {
            float dt = c.Times[b] - c.Times[a];
            return dt > 0f ? (c.Values[b] - c.Values[a]) / dt : 0f;
        }

        /// <summary>Indices of the samples to keep so that straight lines between them stay within the tolerance.</summary>
        public static List<int> Reduce(IReadOnlyList<float> times, IReadOnlyList<float> values, float tolerance)
        {
            int n = times.Count;
            var keep = new bool[n];
            keep[0] = true;
            keep[n - 1] = true;
            var stack = new Stack<(int, int)>();
            if (n > 2) stack.Push((0, n - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                float ta = times[a], va = values[a], tb = times[b], vb = values[b];
                float worst = -1f;
                int at = -1;
                for (int i = a + 1; i < b; i++)
                {
                    float k = tb > ta ? (times[i] - ta) / (tb - ta) : 0f;
                    float error = Mathf.Abs(values[i] - (va + (vb - va) * k));
                    if (error > worst)
                    {
                        worst = error;
                        at = i;
                    }
                }
                if (at >= 0 && worst > tolerance)
                {
                    keep[at] = true;
                    if (at - a > 1) stack.Push((a, at));
                    if (b - at > 1) stack.Push((at, b));
                }
            }
            var result = new List<int>();
            for (int i = 0; i < n; i++)
                if (keep[i])
                    result.Add(i);
            return result;
        }

        /// <summary>The clip property of a humanoid muscle: finger muscles are spelled differently in clips
        /// ("Left Thumb 1 Stretched" is "LeftHand.Thumb.1 Stretched").</summary>
        public static string MuscleProperty(string muscle)
        {
            foreach (string side in new[] { "Left", "Right" })
                foreach (string finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    string prefix = side + " " + finger + " ";
                    if (muscle.StartsWith(prefix, StringComparison.Ordinal)) return side + "Hand." + finger + "." + muscle.Substring(prefix.Length);
                }
            return muscle;
        }
    }
}
