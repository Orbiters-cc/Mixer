using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// What sculpt strokes on one target work in, kept from one stroke to the next: per-vertex marks (a stamp per use
    /// instead of clearing), per-welded-vertex moves and the lists a step fills. A stroke allocating nothing matters more
    /// than it seems: in a large project a garbage collection stalls the editor for half a second, and the megabytes a
    /// stroke used to allocate at the press made one likely just as the brush should start moving.
    /// </summary>
    internal sealed class StrokeScratch
    {
        // Per welded vertex: what Gather saw, the step's move and its mirror.
        public readonly int[] GatherMark, MoveMark, MirrorMark;
        public readonly Vector3[] MoveOf, MirrorOf;
        // Per mesh vertex and per normal-welded vertex: what the stroke's undo record kept (the stroke's serial).
        public readonly int[] Kept, NormalKept, RingKept;
        public readonly BrushContext Context = new BrushContext();
        public readonly List<int> Triangles = new List<int>(), Moved = new List<int>(), Local = new List<int>(), Mirrored = new List<int>();
        public readonly List<int> KeptIndices = new List<int>(), KeptNormalIndices = new List<int>();
        public readonly List<Vector3> KeptPositions = new List<Vector3>(), KeptNormals = new List<Vector3>();
        public Vector3[] Moves = new Vector3[256];
        private int mark, serial;

        public StrokeScratch(int vertexCount, int weldedCount, int normalWeldedCount)
        {
            GatherMark = new int[weldedCount]; MoveMark = new int[weldedCount]; MirrorMark = new int[weldedCount];
            MoveOf = new Vector3[weldedCount]; MirrorOf = new Vector3[weldedCount];
            Kept = new int[vertexCount]; NormalKept = new int[vertexCount]; RingKept = new int[normalWeldedCount];
        }

        /// <summary>A fresh mark for the welded-vertex arrays (a gather or a step).</summary>
        public int NextMark()
        {
            if (++mark == int.MaxValue)
            {
                Array.Clear(GatherMark, 0, GatherMark.Length); Array.Clear(MoveMark, 0, MoveMark.Length); Array.Clear(MirrorMark, 0, MirrorMark.Length);
                mark = 1;
            }
            return mark;
        }

        /// <summary>A fresh serial for a stroke's undo record; its lists start empty.</summary>
        public int NextRecord()
        {
            if (++serial == int.MaxValue)
            {
                Array.Clear(Kept, 0, Kept.Length); Array.Clear(NormalKept, 0, NormalKept.Length); Array.Clear(RingKept, 0, RingKept.Length);
                serial = 1;
            }
            KeptIndices.Clear(); KeptPositions.Clear(); KeptNormalIndices.Clear(); KeptNormals.Clear();
            return serial;
        }
    }
}
