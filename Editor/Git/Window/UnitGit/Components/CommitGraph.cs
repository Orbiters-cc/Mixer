using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// The lanes of the commit graph, from each commit's parents, as JetBrains IDEs draw them: a commit sits on its lane,
    /// its first parent continues the lane, other parents branch off, and lanes that meet at a commit merge into it. Each
    /// line keeps its colour from where it starts.
    /// </summary>
    internal static class UnitGitCommitGraph
    {
        internal struct Edge
        {
            public int From, To, Color;
            public Edge(int from, int to, int color) { From = from; To = to; Color = color; }
        }

        internal sealed class Row
        {
            public int Lane;
            public int Color;
            public bool Head;
            public readonly List<Edge> Top = new List<Edge>();
            public readonly List<Edge> Bottom = new List<Edge>();
            public int Width;
        }

        /// <summary>One row per commit, in the given (newest first) order, and the widest row's lane count.</summary>
        internal static List<Row> Layout(IList<UnitGitCommit> commits, out int lanes)
        {
            var builder = new Builder();
            var rows = new List<Row>(commits.Count);
            foreach (var commit in commits) rows.Add(builder.Add(commit));
            lanes = builder.Lanes;
            return rows;
        }

        /// <summary>
        /// The layout one commit after the other (newest first), so rows read later continue it instead of starting over.
        /// Each row costs its parents plus the lanes it draws: the lowest lane waiting for a commit comes from a per-commit
        /// index and the lowest free lane from a sorted set, instead of searching the lanes for each commit and parent.
        /// </summary>
        internal sealed class Builder
        {
            // expected[i]: the commit lane i waits for (null: free); colors[i]: its line colour.
            private readonly List<string> expected = new List<string>();
            private readonly List<int> colors = new List<int>();
            private readonly Dictionary<string, List<int>> lanesWaitingFor = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            private readonly SortedSet<int> free = new SortedSet<int>();
            private readonly List<bool> mergedInto = new List<bool>();
            private int nextColor;

            /// <summary>The widest row's lane count so far (at least 1).</summary>
            internal int Lanes { get; private set; } = 1;

            /// <summary>How many commits were added.</summary>
            internal int Count { get; private set; }

            internal Row Add(UnitGitCommit commit)
            {
                Count++;
                string hash = commit.FullHash;
                var row = new Row();
                int lane = LowestWaitingFor(hash);
                if (lane < 0)
                {
                    lane = TakeFreeLane();
                    colors[lane] = nextColor++;
                }
                row.Lane = lane;
                row.Color = colors[lane];
                row.Head = IsHead(commit.Decorations);

                // Above the dot: lines passing by, and lines that end here (this commit is their parent).
                for (int i = 0; i < expected.Count; i++)
                {
                    if (expected[i] == null) continue;
                    if (expected[i] == hash) row.Top.Add(new Edge(i, lane, colors[i]));
                    else row.Top.Add(new Edge(i, i, colors[i]));
                }
                if (lanesWaitingFor.TryGetValue(hash, out List<int> waiting))
                    foreach (int i in waiting.ToArray())
                        if (i != lane) Set(i, null);

                // Below the dot: the first parent keeps the lane; others join a lane already waiting for them, or open one.
                var parents = commit.Parents ?? Array.Empty<string>();
                Set(lane, parents.Length > 0 ? parents[0] : null);
                while (mergedInto.Count < expected.Count) mergedInto.Add(false);
                for (int p = 1; p < parents.Length; p++)
                {
                    int target = LowestWaitingFor(parents[p]);
                    if (target < 0)
                    {
                        target = TakeFreeLane();
                        Set(target, parents[p]);
                        colors[target] = nextColor++;
                    }
                    while (mergedInto.Count < expected.Count) mergedInto.Add(false);
                    mergedInto[target] = true;
                    row.Bottom.Add(new Edge(lane, target, colors[target]));
                }
                for (int i = 0; i < expected.Count; i++)
                {
                    if (expected[i] == null) continue;
                    if (i == lane) row.Bottom.Add(new Edge(lane, lane, colors[lane]));
                    else if (!mergedInto[i]) row.Bottom.Add(new Edge(i, i, colors[i]));
                }
                foreach (var edge in row.Bottom)
                    if (edge.From == lane && edge.To < mergedInto.Count) mergedInto[edge.To] = false;
                while (expected.Count > 0 && expected[expected.Count - 1] == null)
                {
                    free.Remove(expected.Count - 1);
                    expected.RemoveAt(expected.Count - 1);
                    colors.RemoveAt(colors.Count - 1);
                }

                int width = lane + 1;
                foreach (var edge in row.Top) width = Math.Max(width, Math.Max(edge.From, edge.To) + 1);
                foreach (var edge in row.Bottom) width = Math.Max(width, Math.Max(edge.From, edge.To) + 1);
                row.Width = width;
                Lanes = Math.Max(Lanes, width);
                return row;
            }

            private int LowestWaitingFor(string hash)
            {
                if (hash == null || !lanesWaitingFor.TryGetValue(hash, out List<int> lanes) || lanes.Count == 0) return -1;
                int lowest = lanes[0];
                for (int i = 1; i < lanes.Count; i++) lowest = Math.Min(lowest, lanes[i]);
                return lowest;
            }

            // The lowest free lane, or a new one at the end (free, colour 0, as the old layout added it).
            private int TakeFreeLane()
            {
                if (free.Count > 0) return free.Min;
                expected.Add(null);
                colors.Add(0);
                free.Add(expected.Count - 1);
                return expected.Count - 1;
            }

            private void Set(int lane, string hash)
            {
                string old = expected[lane];
                if (old == hash) return;
                if (old != null && lanesWaitingFor.TryGetValue(old, out List<int> was))
                {
                    was.Remove(lane);
                    if (was.Count == 0) lanesWaitingFor.Remove(old);
                }
                expected[lane] = hash;
                if (hash == null) { free.Add(lane); return; }
                free.Remove(lane);
                if (!lanesWaitingFor.TryGetValue(hash, out List<int> now)) lanesWaitingFor[hash] = now = new List<int>(1);
                now.Add(lane);
            }
        }

        /// <summary>
        /// Whether Git's decorations (%D) mark the checked-out commit: "HEAD -> main", or a bare "HEAD" for a detached HEAD.
        /// "origin/HEAD" (a remote's default branch) is not the checked-out commit.
        /// </summary>
        internal static bool IsHead(string decorations)
        {
            if (string.IsNullOrEmpty(decorations)) return false;
            foreach (string raw in decorations.Split(','))
            {
                string part = raw.Trim();
                if (part == "HEAD" || part.StartsWith("HEAD -> ", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>A row with only the lines passing by (a release row between two commits).</summary>
        internal static Row PassThrough(Row below)
        {
            var row = new Row { Lane = -1, Width = below?.Width ?? 1 };
            if (below == null) return row;
            foreach (var edge in below.Bottom)
            {
                if (row.Top.Exists(e => e.From == edge.To)) continue;
                row.Top.Add(new Edge(edge.To, edge.To, edge.Color));
                row.Bottom.Add(new Edge(edge.To, edge.To, edge.Color));
            }
            return row;
        }
    }

    /// <summary>One row of the commit graph, drawn with the vector API.</summary>
    internal sealed class UnitGitGraphCell : VisualElement
    {
        internal const float LaneWidth = 14f;
        internal const float Inset = 11f;
        private static readonly Color[] Palette =
        {
            new Color32(0, 218, 109, 255),
            new Color32(106, 167, 255, 255),
            new Color32(255, 179, 71, 255),
            new Color32(200, 140, 255, 255),
            new Color32(255, 117, 141, 255),
            new Color32(72, 214, 214, 255),
            new Color32(240, 220, 90, 255),
        };

        private UnitGitCommitGraph.Row row;
        private bool selected;

        public UnitGitGraphCell()
        {
            AddToClassList("ug-graph");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        // The first lane (the checked-out branch's) is Mixer's accent.
        internal static Color ColorOf(int index)
        {
            int lane = ((index % Palette.Length) + Palette.Length) % Palette.Length;
            return lane == 0 ? (Color)Orbiters.Mixer.Editor.MixerAccent.Color : Palette[lane];
        }

        public void Bind(UnitGitCommitGraph.Row value, bool isSelected)
        {
            row = value;
            selected = isSelected;
            MarkDirtyRepaint();
        }

        private static float X(int lane) => Inset + lane * LaneWidth;

        private void Draw(MeshGenerationContext context)
        {
            if (row == null) return;
            var rect = contentRect;
            float height = rect.height, middle = height * 0.5f;
            var p = context.painter2D;
            p.lineWidth = 2f;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            foreach (var edge in row.Top) Segment(p, X(edge.From), 0f, X(edge.To), middle, ColorOf(edge.Color));
            foreach (var edge in row.Bottom) Segment(p, X(edge.From), middle, X(edge.To), height, ColorOf(edge.Color));
            if (row.Lane < 0) return;

            var color = ColorOf(row.Color);
            float x = X(row.Lane);
            if (row.Head || selected)
            {
                // The checked-out commit (and the selected one) is a ring around its dot.
                p.fillColor = new Color(0.169f, 0.169f, 0.169f, 1f); // the log's background (--ug-bg)
                p.BeginPath(); p.Arc(new Vector2(x, middle), 7f, 0f, 360f); p.Fill();
                p.strokeColor = color;
                p.lineWidth = 2f;
                p.BeginPath(); p.Arc(new Vector2(x, middle), 6.5f, 0f, 360f); p.Stroke();
            }
            p.fillColor = color;
            p.BeginPath(); p.Arc(new Vector2(x, middle), row.Head || selected ? 3.6f : 4.4f, 0f, 360f); p.Fill();
        }

        private static void Segment(Painter2D p, float x1, float y1, float x2, float y2, Color color)
        {
            p.strokeColor = color;
            p.BeginPath();
            p.MoveTo(new Vector2(x1, y1));
            if (Mathf.Approximately(x1, x2)) p.LineTo(new Vector2(x2, y2));
            else
            {
                float mid = (y1 + y2) * 0.5f;
                p.BezierCurveTo(new Vector2(x1, mid), new Vector2(x2, mid), new Vector2(x2, y2));
            }
            p.Stroke();
        }
    }
}
