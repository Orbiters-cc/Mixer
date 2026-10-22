using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Contracts;

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>
    /// Decodes a sample of a capture's written frames the way Orbiters desktop will (QOI from disk, then
    /// <see cref="CaptureStrip.TryDecode"/> and <see cref="DepthFrames.TryDecode"/>) and compares them with the ground
    /// truth: strip payloads bit for bit, depth codes within ±1 of the quantised true depth at every sample pixel.
    /// </summary>
    internal static class CaptureSelfCheck
    {
        /// <summary>Frames checked per kind (colour, depth), spread evenly over the capture.</summary>
        internal const int SamplePerKind = 40;

        internal sealed class Result
        {
            public int StripChecked, StripExact, StripUndecoded, StripFlipped;
            public long CorrectedBits;
            public float MinContrast = float.MaxValue;
            public int DepthFramesChecked;
            public long DepthSamples, DepthWithinOne, DepthExact;
            public int TilesOk, TilesRejected;
            public readonly List<string> Problems = new List<string>();

            public float StripRate => StripChecked == 0 ? 0f : StripExact / (float)StripChecked;
            public float DepthRate => DepthSamples == 0 ? 0f : DepthWithinOne / (float)DepthSamples;
            public float DepthExactRate => DepthSamples == 0 ? 0f : DepthExact / (float)DepthSamples;
            public bool Passed => StripChecked > 0 && StripExact == StripChecked && (DepthSamples == 0 || DepthWithinOne == DepthSamples);

            public override string ToString() =>
                "strip " + StripExact + "/" + StripChecked + " exact" + (StripUndecoded > 0 ? ", " + StripUndecoded + " undecoded" : "") +
                (StripFlipped > 0 ? ", " + StripFlipped + " flipped" : "") + ", corrected bits " + CorrectedBits +
                (DepthFramesChecked > 0
                    ? "; depth " + DepthWithinOne + "/" + DepthSamples + " samples within ±1 (" + DepthExact + " exact) in " + DepthFramesChecked +
                      " frames, tiles " + TilesOk + " ok / " + TilesRejected + " rejected"
                    : "");
        }

        /// <summary>Frame indices to check: up to <see cref="SamplePerKind"/> colour and as many depth frames, evenly spread.</summary>
        public static int[] Sample(int frameCount, Func<int, bool> isDepthFrame, int perKind = SamplePerKind)
        {
            var colour = new List<int>();
            var depth = new List<int>();
            for (int i = 0; i < frameCount; i++) (isDepthFrame(i) ? depth : colour).Add(i);
            return Spread(colour, perKind).Concat(Spread(depth, perKind)).OrderBy(i => i).ToArray();
        }

        private static IEnumerable<int> Spread(List<int> items, int count)
        {
            if (items.Count <= count) return items;
            var picked = new SortedSet<int>();
            for (int k = 0; k < count; k++) picked.Add(items[(int)Math.Round(k * (items.Count - 1) / (double)(count - 1))]);
            return picked;
        }

        /// <param name="expectedWords">Payload words per frame index (what each strip must decode to).</param>
        /// <param name="expectedDepth">Depth codes per depth frame index (null for frames without).</param>
        public static Result Run(string folder, IReadOnlyList<int> frames, Func<int, uint[]> expectedWords, Func<int, ushort[]> expectedDepth,
            int threads, CancellationToken cancel, Action<int> progress = null)
        {
            var result = new Result();
            var gate = new object();
            int done = 0;
            Parallel.ForEach(frames, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = cancel }, index =>
            {
                var local = Check(folder, index, expectedWords(index), expectedDepth(index));
                lock (gate) Merge(result, local);
                progress?.Invoke(Interlocked.Increment(ref done));
            });
            return result;
        }

        private static Result Check(string folder, int index, uint[] words, ushort[] depthCodes)
        {
            var r = new Result();
            var image = Qoi.Decode(File.ReadAllBytes(Path.Combine(folder, RawCapture.FramesDir, RawCapture.FrameFile(index))));
            r.StripChecked = 1;
            bool flipped = false;
            if (!CaptureStrip.TryDecode(image, out var decoded, out string error))
            {
                r.StripUndecoded = 1;
                r.Problems.Add("frame " + index + ": " + error);
            }
            else
            {
                flipped = decoded.Flipped;
                if (flipped) r.StripFlipped = 1;
                r.CorrectedBits = decoded.CorrectedBits;
                r.MinContrast = decoded.MinContrast;
                var got = decoded.Payload.ToWords();
                if (words != null && got.SequenceEqual(words)) r.StripExact = 1;
                else r.Problems.Add("frame " + index + ": strip decodes to other values (first different word " + FirstDifference(got, words) + ")");
            }
            if (depthCodes == null) return r;
            r.DepthFramesChecked = 1;
            if (!DepthFrames.TryDecode(image, flipped, out var layout, out var codes, out r.TilesOk, out r.TilesRejected))
            {
                r.Problems.Add("frame " + index + ": no depth layout");
                return r;
            }
            int gw = layout.GridWidth;
            for (int gy = 0; gy < layout.GridHeight; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                if (!layout.GridSampled(gx, gy)) continue;
                int at = gy * gw + gx;
                int difference = Math.Abs(codes[at] - depthCodes[at]);
                r.DepthSamples++;
                if (difference <= 1) r.DepthWithinOne++;
                if (difference == 0) r.DepthExact++;
            }
            if (r.DepthWithinOne < r.DepthSamples)
                r.Problems.Add("frame " + index + ": " + (r.DepthSamples - r.DepthWithinOne) + " depth samples off by more than 1 (" + r.TilesRejected + " tiles rejected)");
            return r;
        }

        private static int FirstDifference(uint[] a, uint[] b)
        {
            if (b == null) return 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return i;
            return Math.Min(a.Length, b.Length);
        }

        private static void Merge(Result into, Result r)
        {
            into.StripChecked += r.StripChecked;
            into.StripExact += r.StripExact;
            into.StripUndecoded += r.StripUndecoded;
            into.StripFlipped += r.StripFlipped;
            into.CorrectedBits += r.CorrectedBits;
            into.MinContrast = Math.Min(into.MinContrast, r.MinContrast);
            into.DepthFramesChecked += r.DepthFramesChecked;
            into.DepthSamples += r.DepthSamples;
            into.DepthWithinOne += r.DepthWithinOne;
            into.DepthExact += r.DepthExact;
            into.TilesOk += r.TilesOk;
            into.TilesRejected += r.TilesRejected;
            foreach (var p in r.Problems)
                if (into.Problems.Count < 20) into.Problems.Add(p);
        }
    }
}
