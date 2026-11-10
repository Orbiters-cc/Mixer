using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Splats
{
    /// <summary>
    /// When and how the renderer sorts a cloud back to front for a camera: SplatSort.compute's bitonic network over a
    /// power-of-two count (its dispatches, in order), its key (the distance from the eye, so turning never reorders), and
    /// how far the eye may move before an order is no longer good enough.
    /// </summary>
    internal static class SplatSorting
    {
        /// <summary>
        /// The eye may move this far, plus <see cref="ResortFraction"/> of its distance to the cloud's bounds, before the
        /// splats are sorted again. Why that is enough: a splat's key, its distance d to the eye, changes along a move δ
        /// by the move's component along the direction u from the splat to the eye, so two splats' difference d_i − d_j
        /// changes by at most |δ|·|u_i − u_j| ≤ |δ|·θ, θ the angle between them seen from the eye. Two splats can only
        /// swap if their distances differed by less than that; and their order only shows where they cover a common
        /// pixel, which needs θ below the sum of their angular radii, (r_i + r_j) / D with D ≥ the distance to the
        /// bounds. So any pair drawn in the wrong order before the next sort covers a common pixel with centres closer
        /// in depth than (0.5 mm / D + 0.1 %) · (r_i + r_j): within 1.1 % of the sum of their reaches (the renderer
        /// draws nothing nearer than 5 cm), 0.2 % beyond half a metre. Such Gaussians pass deep through each other,
        /// where no order of their centres is right anyway.
        /// </summary>
        public const float ResortMinimum = 0.0005f;

        public const float ResortFraction = 0.001f;

        public enum Kernel
        {
            CalcKeys,
            SortLocal,
            MergeGlobal,
            MergeLocal,
        }

        /// <summary>One dispatch of the sort: its kernel, the stage <see cref="K"/> and distance <see cref="J"/> it
        /// sets (where it reads them) and its thread groups.</summary>
        public struct Step
        {
            public Kernel Kernel;
            public int K, J, Groups;
        }

        /// <summary>The count the sort runs over: a power of two, at least one block of 512.</summary>
        public static int Padded(int count) => Mathf.Max(512, Mathf.NextPowerOfTwo(count));

        /// <summary>SplatSort.compute's dispatches for <paramref name="padded"/> keys, in order: the keys, every stage up
        /// to blocks of 512 in shared memory, then for each longer stage its steps at distances of 512 and more through
        /// memory and the rest in shared memory.</summary>
        public static Step[] Schedule(int padded)
        {
            var steps = new List<Step>
            {
                new Step { Kernel = Kernel.CalcKeys, Groups = (padded + 255) / 256 },
                new Step { Kernel = Kernel.SortLocal, Groups = padded / 512 },
            };
            for (int k = 1024; k <= padded; k <<= 1)
            {
                for (int j = k >> 1; j >= 512; j >>= 1)
                    steps.Add(new Step { Kernel = Kernel.MergeGlobal, K = k, J = j, Groups = (padded / 2 + 255) / 256 });
                steps.Add(new Step { Kernel = Kernel.MergeLocal, K = k, Groups = padded / 512 });
            }
            return steps.ToArray();
        }

        /// <summary>SplatSort.compute's key for a splat <paramref name="distance"/> from the eye: far first (positive
        /// floats order like their bits), the padding (all bits) last.</summary>
        public static uint Key(float distance) => ~SplatLayout.AsUint(distance);

        /// <summary>How far the eye at <paramref name="eye"/> may move before a cloud with <paramref name="bounds"/>
        /// (its own space) under <paramref name="objectToWorld"/> must be sorted again (<see cref="ResortMinimum"/>).</summary>
        public static float ResortDistance(Bounds bounds, Matrix4x4 objectToWorld, Vector3 eye)
        {
            Vector3 c = bounds.center, e = bounds.extents;
            var world = new Bounds(objectToWorld.MultiplyPoint3x4(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
                world.Encapsulate(objectToWorld.MultiplyPoint3x4(c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z)));
            float distance = Mathf.Sqrt(world.SqrDistance(eye));
            return ResortMinimum + ResortFraction * (float.IsNaN(distance) || float.IsInfinity(distance) ? 0f : distance);
        }

        /// <summary>What a camera's order was sorted for. Every input of the keys is here: the uploaded splats (by
        /// upload), the cloud's transform and the eye; <see cref="Stamp"/> names the order for what reads it.</summary>
        public struct Sorted
        {
            public int Upload;
            public Matrix4x4 ObjectToWorld;
            public Vector3 Eye;
            public int Stamp;

            /// <summary>Whether this order still serves: the same upload, exactly the same transform, the eye within
            /// <see cref="ResortDistance"/> of where it was.</summary>
            public bool Serves(int upload, Matrix4x4 objectToWorld, Vector3 eye, Bounds bounds) =>
                Upload == upload && ObjectToWorld.Equals(objectToWorld) && (eye - Eye).magnitude <= ResortDistance(bounds, objectToWorld, Eye);
        }
    }
}
