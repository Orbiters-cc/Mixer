using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // The commit graph's lanes from the incremental builder against the layout it replaced (kept below as the reference),
    // on random histories: merges, octopus merges, a parent named twice, several roots, histories cut short (parents not
    // in the list), and layouts continued in chunks as "load more" does.
    public sealed class UnitGitCommitGraphTests
    {
        [Test]
        public void TheBuilderLaysOutEveryHistoryAsTheOldLayoutDid()
        {
            var random = new Random(20261110);
            for (int round = 0; round < 300; round++)
            {
                List<UnitGitCommit> commits = History(random, random.Next(1, 160), random.Next(1, 8));
                List<UnitGitCommitGraph.Row> expected = Reference(commits, out int expectedLanes);
                List<UnitGitCommitGraph.Row> whole = UnitGitCommitGraph.Layout(commits, out int lanes);
                AssertSame(expected, whole, "round " + round);
                Assert.That(lanes, Is.EqualTo(expectedLanes), "round " + round);

                // Continued in chunks: the rows are the same as one layout of the whole list.
                var builder = new UnitGitCommitGraph.Builder();
                var chunked = new List<UnitGitCommitGraph.Row>();
                int at = 0;
                while (at < commits.Count)
                {
                    int take = Math.Min(commits.Count - at, random.Next(1, 40));
                    for (int i = 0; i < take; i++) chunked.Add(builder.Add(commits[at + i]));
                    at += take;
                    Reference(commits.Take(at).ToList(), out int lanesSoFar);
                    Assert.That(builder.Lanes, Is.EqualTo(lanesSoFar), "round " + round + " after " + at);
                }
                AssertSame(expected, chunked, "round " + round + " in chunks");
            }
        }

        // Commits oldest first with parents among older ones (some outside the list: a cut history), returned newest first.
        private static List<UnitGitCommit> History(Random random, int count, int branchiness)
        {
            var hashes = Enumerable.Range(0, count).Select(i => i.ToString("x40")).ToList();
            var commits = new List<UnitGitCommit>();
            for (int i = 0; i < count; i++)
            {
                var parents = new List<string>();
                int roll = random.Next(100);
                int parentCount = i == 0 || roll < 4 ? 0 : roll < 70 ? 1 : roll < 92 ? 2 : random.Next(3, 6);
                for (int p = 0; p < parentCount; p++)
                {
                    if (random.Next(30) == 0) { parents.Add("f" + random.Next(1000).ToString("x39")); continue; } // not in the list
                    int span = Math.Min(i, Math.Max(1, branchiness * 4));
                    parents.Add(hashes[i - 1 - random.Next(span)]);
                }
                if (parents.Count > 1 && random.Next(20) == 0) parents.Add(parents[0]); // a parent named twice
                commits.Add(new UnitGitCommit
                {
                    FullHash = hashes[i],
                    Parents = parents.ToArray(),
                    Decorations = random.Next(25) == 0 ? "HEAD -> main" : string.Empty
                });
            }
            commits.Reverse();
            return commits;
        }

        private static void AssertSame(List<UnitGitCommitGraph.Row> expected, List<UnitGitCommitGraph.Row> actual, string where)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), where);
            for (int i = 0; i < expected.Count; i++)
            {
                string at = where + ", row " + i;
                Assert.That(actual[i].Lane, Is.EqualTo(expected[i].Lane), at);
                Assert.That(actual[i].Color, Is.EqualTo(expected[i].Color), at);
                Assert.That(actual[i].Head, Is.EqualTo(expected[i].Head), at);
                Assert.That(actual[i].Width, Is.EqualTo(expected[i].Width), at);
                Assert.That(Edges(actual[i].Top), Is.EqualTo(Edges(expected[i].Top)), at + " top");
                Assert.That(Edges(actual[i].Bottom), Is.EqualTo(Edges(expected[i].Bottom)), at + " bottom");
            }
        }

        private static string[] Edges(List<UnitGitCommitGraph.Edge> edges) =>
            edges.Select(edge => edge.From + ">" + edge.To + ":" + edge.Color).ToArray();

        // The layout before the builder, unchanged: lanes searched with IndexOf, merge targets with Exists.
        private static List<UnitGitCommitGraph.Row> Reference(IList<UnitGitCommit> commits, out int lanes)
        {
            var rows = new List<UnitGitCommitGraph.Row>(commits.Count);
            var expected = new List<string>();
            var colors = new List<int>();
            int nextColor = 0;
            lanes = 1;
            foreach (var commit in commits)
            {
                var row = new UnitGitCommitGraph.Row();
                int lane = expected.IndexOf(commit.FullHash);
                bool continued = lane >= 0;
                if (!continued)
                {
                    lane = expected.IndexOf(null);
                    if (lane < 0) { lane = expected.Count; expected.Add(null); colors.Add(0); }
                    colors[lane] = nextColor++;
                }
                row.Lane = lane;
                row.Color = colors[lane];
                row.Head = UnitGitCommitGraph.IsHead(commit.Decorations);
                for (int i = 0; i < expected.Count; i++)
                {
                    if (expected[i] == null) continue;
                    if (expected[i] == commit.FullHash) row.Top.Add(new UnitGitCommitGraph.Edge(i, lane, colors[i]));
                    else row.Top.Add(new UnitGitCommitGraph.Edge(i, i, colors[i]));
                }
                for (int i = 0; i < expected.Count; i++)
                    if (i != lane && expected[i] == commit.FullHash) expected[i] = null;
                var parents = commit.Parents ?? Array.Empty<string>();
                expected[lane] = parents.Length > 0 ? parents[0] : null;
                for (int p = 1; p < parents.Length; p++)
                {
                    int target = expected.IndexOf(parents[p]);
                    if (target < 0)
                    {
                        target = expected.IndexOf(null);
                        if (target < 0) { target = expected.Count; expected.Add(null); colors.Add(0); }
                        expected[target] = parents[p];
                        colors[target] = nextColor++;
                    }
                    row.Bottom.Add(new UnitGitCommitGraph.Edge(lane, target, colors[target]));
                }
                for (int i = 0; i < expected.Count; i++)
                {
                    if (expected[i] == null) continue;
                    if (i == lane) row.Bottom.Add(new UnitGitCommitGraph.Edge(lane, lane, colors[lane]));
                    else if (!row.Bottom.Exists(edge => edge.To == i && edge.From == lane)) row.Bottom.Add(new UnitGitCommitGraph.Edge(i, i, colors[i]));
                }
                while (expected.Count > 0 && expected[expected.Count - 1] == null) { expected.RemoveAt(expected.Count - 1); colors.RemoveAt(colors.Count - 1); }
                int width = lane + 1;
                foreach (var edge in row.Top) width = Math.Max(width, Math.Max(edge.From, edge.To) + 1);
                foreach (var edge in row.Bottom) width = Math.Max(width, Math.Max(edge.From, edge.To) + 1);
                row.Width = width;
                lanes = Math.Max(lanes, width);
                rows.Add(row);
            }
            return rows;
        }
    }
}
