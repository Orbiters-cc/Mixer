using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// For each welded vertex, the welded vertex at its mirror image across the mesh's X = 0 plane (its own index on
    /// the plane, -1 when none is within the tolerance). Built on the original positions, so symmetry keeps working
    /// after the vertices moved.
    /// </summary>
    internal sealed class MirrorMap
    {
        public int[] Of;
        public int Matched;

        public static MirrorMap Build(Vector3[] positions, MeshNeighbors neighbors, float tolerance)
        {
            tolerance = Mathf.Max(tolerance, 1e-6f);
            float cell = tolerance * 4f;
            var grid = new Dictionary<long, List<int>>();
            int count = neighbors.WeldedCount;
            for (int w = 0; w < count; w++)
            {
                var p = positions[neighbors.Representative[w]];
                long key = Key(p, cell);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>(2);
                list.Add(w);
            }
            var map = new MirrorMap { Of = new int[count] };
            float tol2 = tolerance * tolerance;
            for (int w = 0; w < count; w++)
            {
                var p = positions[neighbors.Representative[w]];
                var m = new Vector3(-p.x, p.y, p.z);
                int best = -1;
                float bestD = tol2;
                int cx = (int)Math.Floor(m.x / cell), cy = (int)Math.Floor(m.y / cell), cz = (int)Math.Floor(m.z / cell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var list)) continue;
                            foreach (int c in list)
                            {
                                float d = (positions[neighbors.Representative[c]] - m).sqrMagnitude;
                                if (d < bestD) { bestD = d; best = c; }
                            }
                        }
                map.Of[w] = best;
                if (best >= 0) map.Matched++;
            }
            return map;
        }

        private static long Key(Vector3 p, float cell) => Key((int)Math.Floor(p.x / cell), (int)Math.Floor(p.y / cell), (int)Math.Floor(p.z / cell));
        private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }
}
