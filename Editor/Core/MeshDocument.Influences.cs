using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The bone influences of a document: a fixed number of slots per vertex in three flat arrays (bone, weight, count),
    /// strongest first, edited in place. A list per vertex made the first weight touch of a 500k-vertex body allocate
    /// half a million objects that every later garbage collection had to walk; flat arrays of numbers are not even
    /// scanned. The slots grow (for every vertex at once) when an edit needs more than a vertex ever had.
    /// </summary>
    internal sealed partial class MeshDocument
    {
        public struct Influence
        {
            public int Bone;
            public float Weight;
            public Influence(int bone, float weight) { Bone = bone; Weight = weight; }
        }

        /// <summary>Unity keeps at most this many influences per vertex.</summary>
        public const int MaxInfluences = 255;

        // Copies of the original's bone weights taken when the document is made: the original can be reimported or
        // replaced during a session, and Unity's arrays are views into the mesh's memory.
        private readonly byte[] originalPerVertex;
        private readonly BoneWeight1[] originalWeights;
        private readonly int[] originalStart;

        // Influence v, i is at v * slots + i, for i below count[v]; null until loaded.
        private int slots;
        private int[] influenceBone;
        private float[] influenceWeight;
        private byte[] influenceCount;
        private readonly List<Influence> influenceScratch = new List<Influence>();

        /// <summary>Whether the influences were loaded (an edit, or a reader that needed them); before, the original's are the document's.</summary>
        public bool InfluencesLoaded => influenceCount != null;
        /// <summary>The slots each vertex has (the most influences any vertex has needed, at least 4).</summary>
        public int InfluenceSlots => slots;

        /// <summary>Loads the influences from the original the first time they are needed.</summary>
        public void EnsureInfluences()
        {
            if (influenceCount != null) return;
            int most = 4;
            if (originalStart != null)
                for (int v = 0; v < VertexCount; v++) most = Math.Max(most, originalStart[v + 1] - originalStart[v]);
            slots = Math.Min(MaxInfluences, most);
            influenceBone = new int[VertexCount * slots];
            influenceWeight = new float[VertexCount * slots];
            influenceCount = new byte[VertexCount];
            if (originalStart == null) return;
            for (int v = 0; v < VertexCount; v++)
            {
                int at = v * slots, n = 0;
                for (int k = originalStart[v]; k < originalStart[v + 1] && n < slots; k++)
                {
                    var bw = originalWeights[k];
                    if (bw.weight <= 0f) continue;
                    influenceBone[at + n] = bw.boneIndex;
                    influenceWeight[at + n] = bw.weight;
                    n++;
                }
                influenceCount[v] = (byte)n;
                SortSlots(v);
            }
        }

        /// <summary>How many influences vertex <paramref name="v"/> has.</summary>
        public int InfluenceCount(int v)
        {
            if (influenceCount != null) return influenceCount[v];
            if (originalStart == null) return 0;
            int n = 0;
            for (int k = originalStart[v]; k < originalStart[v + 1]; k++) if (originalWeights[k].weight > 0f) n++;
            return n;
        }

        /// <summary>Influence <paramref name="i"/> of vertex <paramref name="v"/> (strongest first), below <see cref="InfluenceCount"/>.</summary>
        public Influence InfluenceAt(int v, int i)
        {
            EnsureInfluences();
            int at = v * slots + i;
            return new Influence(influenceBone[at], influenceWeight[at]);
        }

        /// <summary>A copy of a vertex's influences (strongest first): what undo and the autosave keep.</summary>
        public Influence[] InfluenceArray(int v)
        {
            EnsureInfluences();
            int n = influenceCount[v];
            if (n == 0) return Array.Empty<Influence>();
            var result = new Influence[n];
            for (int i = 0, at = v * slots; i < n; i++, at++) result[i] = new Influence(influenceBone[at], influenceWeight[at]);
            return result;
        }

        /// <summary>
        /// A vertex's influences in a list the document owns and fills again at the next call (no copy is kept): for a
        /// reader that compares them at once.
        /// </summary>
        public List<Influence> InfluencesOf(int v)
        {
            EnsureInfluences();
            influenceScratch.Clear();
            for (int i = 0, at = v * slots, n = influenceCount[v]; i < n; i++, at++) influenceScratch.Add(new Influence(influenceBone[at], influenceWeight[at]));
            return influenceScratch;
        }

        public float BoneWeight(int v, int bone)
        {
            if (influenceCount == null) return OriginalBoneWeight(v, bone);
            for (int i = 0, at = v * slots, n = influenceCount[v]; i < n; i++, at++) if (influenceBone[at] == bone) return influenceWeight[at];
            return 0f;
        }

        private float OriginalBoneWeight(int v, int bone)
        {
            if (originalStart == null) return 0f;
            for (int k = originalStart[v]; k < originalStart[v + 1]; k++) if (originalWeights[k].boneIndex == bone) return originalWeights[k].weight;
            return 0f;
        }

        /// <summary>Whether a vertex's influences are the original's (same bones, same weights within a millionth).</summary>
        public bool InfluencesAreOriginal(int v)
        {
            if (influenceCount == null) return true;
            int start = originalStart != null ? originalStart[v] : 0, end = originalStart != null ? originalStart[v + 1] : 0;
            int count = 0, at = v * slots, n = influenceCount[v];
            for (int k = start; k < end; k++)
            {
                var bw = originalWeights[k];
                if (bw.weight <= 0f) continue;
                count++;
                bool found = false;
                for (int i = 0; i < n && !found; i++) found = influenceBone[at + i] == bw.boneIndex && Mathf.Abs(influenceWeight[at + i] - bw.weight) < 1e-6f;
                if (!found) return false;
            }
            return count == n;
        }

        /// <summary>
        /// Sets a bone's weight on a vertex. With <paramref name="normalize"/> the other bones are scaled so the sum stays 1
        /// (when they had any weight; a lone bone goes to 1). Weights under a thousandth are dropped; a vertex is never left
        /// without a bone (see <see cref="KeepABone"/>).
        /// </summary>
        public void SetBoneWeight(int v, int bone, float weight, bool normalize)
        {
            EnsureInfluences();
            var strongest = Strongest(v);
            weight = Mathf.Clamp01(weight);
            int at = v * slots, n = influenceCount[v], index = -1;
            float others = 0f;
            for (int i = 0; i < n; i++) { if (influenceBone[at + i] == bone) index = i; else others += influenceWeight[at + i]; }
            if (normalize)
            {
                if (others > 1e-6f)
                {
                    float scale = (1f - weight) / others;
                    for (int i = 0; i < n; i++) if (influenceBone[at + i] != bone) influenceWeight[at + i] *= scale;
                }
                else weight = 1f;
            }
            if (index >= 0) influenceWeight[at + index] = weight;
            else if (weight > 0f && n < MaxInfluences)
            {
                if (n == slots) { GrowSlots(n + 1); at = v * slots; }
                influenceBone[at + n] = bone;
                influenceWeight[at + n] = weight;
                influenceCount[v] = (byte)++n;
            }
            DropFaint(v);
            KeepABone(v, strongest);
            SortSlots(v);
            WeightEdited.Add(v);
            WeightsDirty = true;
        }

        /// <summary>Sets a vertex's influences (an undo, Blender, an autosave); an empty list keeps a bone (see <see cref="KeepABone"/>).</summary>
        public void SetInfluences(int v, Influence[] influences) => SetInfluences(v, influences, 0, influences.Length);

        /// <summary><see cref="SetInfluences(int, Influence[])"/> from <paramref name="count"/> influences of <paramref name="source"/> at <paramref name="offset"/>.</summary>
        public void SetInfluences(int v, Influence[] source, int offset, int count)
        {
            EnsureInfluences();
            var strongest = Strongest(v);
            count = Math.Min(count, MaxInfluences);
            if (count > slots) GrowSlots(count);
            int at = v * slots;
            for (int i = 0; i < count; i++) { influenceBone[at + i] = source[offset + i].Bone; influenceWeight[at + i] = source[offset + i].Weight; }
            influenceCount[v] = (byte)count;
            KeepABone(v, strongest);
            // Unity reads the strongest first (and keeps only the first four at the usual quality): Blender's order is any.
            SortSlots(v);
            WeightEdited.Add(v);
            WeightsDirty = true;
        }

        // Weights under a thousandth leave, the others keep their order.
        private void DropFaint(int v)
        {
            int at = v * slots, n = influenceCount[v], kept = 0;
            for (int i = 0; i < n; i++)
            {
                if (influenceWeight[at + i] < 0.001f) continue;
                influenceBone[at + kept] = influenceBone[at + i];
                influenceWeight[at + kept] = influenceWeight[at + i];
                kept++;
            }
            influenceCount[v] = (byte)kept;
        }

        // Unity skins every vertex by some bone: one left with none would be committed on the first bone (the root or the
        // hips) and spike when the avatar moves. A change that would take a vertex's last bone away (painting it to 0 with
        // Normalize off, Blender sending the vertex without groups) leaves it the bone that weighed most on it before, as
        // it was: alone, that bone moves the vertex fully, as before the change.
        private void KeepABone(int v, Influence strongest)
        {
            if (influenceCount[v] != 0 || strongest.Weight <= 0f) return;
            influenceBone[v * slots] = strongest.Bone;
            influenceWeight[v * slots] = strongest.Weight;
            influenceCount[v] = 1;
        }

        private Influence Strongest(int v)
        {
            var best = default(Influence);
            for (int i = 0, at = v * slots, n = influenceCount[v]; i < n; i++, at++)
                if (influenceWeight[at] > best.Weight) best = new Influence(influenceBone[at], influenceWeight[at]);
            return best;
        }

        // Strongest first; a vertex has a handful of influences, so an insertion sort (stable: equal weights keep their order).
        private void SortSlots(int v)
        {
            int at = v * slots, n = influenceCount[v];
            for (int i = at + 1; i < at + n; i++)
            {
                int bone = influenceBone[i];
                float weight = influenceWeight[i];
                int j = i - 1;
                while (j >= at && influenceWeight[j] < weight) { influenceBone[j + 1] = influenceBone[j]; influenceWeight[j + 1] = influenceWeight[j]; j--; }
                influenceBone[j + 1] = bone;
                influenceWeight[j + 1] = weight;
            }
        }

        // More slots for every vertex (rare: a vertex given more bones than any had).
        private void GrowSlots(int needed)
        {
            int next = Math.Min(MaxInfluences, Math.Max(needed, slots * 2));
            if (next <= slots) return;
            var bones = new int[VertexCount * next];
            var weights = new float[VertexCount * next];
            for (int v = 0; v < VertexCount; v++)
            {
                Array.Copy(influenceBone, v * slots, bones, v * next, influenceCount[v]);
                Array.Copy(influenceWeight, v * slots, weights, v * next, influenceCount[v]);
            }
            influenceBone = bones;
            influenceWeight = weights;
            slots = next;
        }

        /// <summary>
        /// The influences as Unity's arrays (strongest first, normalized per vertex), for the working mesh or the commit.
        /// A vertex without any gets the first bone at full weight, as Unity needs one per vertex.
        /// </summary>
        public void BuildBoneWeights(out NativeArray<byte> perVertex, out NativeArray<BoneWeight1> weights)
        {
            EnsureInfluences();
            int total = 0;
            for (int v = 0; v < VertexCount; v++) total += Math.Max(1, (int)influenceCount[v]);
            perVertex = new NativeArray<byte>(VertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            weights = new NativeArray<BoneWeight1>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            InfluenceKernels.Pack(influenceBone, influenceWeight, influenceCount, slots, perVertex, weights);
        }

        /// <summary>
        /// The influences as they are (strongest first, not normalized, none for a vertex without any), copied: what the
        /// Blender link sends.
        /// </summary>
        public void CopyBoneWeights(out byte[] perVertex, out BoneWeight1[] weights)
        {
            EnsureInfluences();
            int total = 0;
            for (int v = 0; v < VertexCount; v++) total += influenceCount[v];
            perVertex = (byte[])influenceCount.Clone();
            weights = new BoneWeight1[total];
            int cursor = 0;
            for (int v = 0; v < VertexCount; v++)
                for (int i = 0, at = v * slots, n = influenceCount[v]; i < n; i++, at++)
                    weights[cursor++] = new BoneWeight1 { boneIndex = influenceBone[at], weight = influenceWeight[at] };
        }

        /// <summary>
        /// The slot arrays as they are, for the skinning and its kernels: read only, valid until the next edit of the
        /// influences (an edit may grow the slots into new arrays).
        /// </summary>
        internal void InfluenceArrays(out int[] bones, out float[] weights, out byte[] counts, out int slotCount)
        {
            EnsureInfluences();
            bones = influenceBone; weights = influenceWeight; counts = influenceCount; slotCount = slots;
        }
    }
}
