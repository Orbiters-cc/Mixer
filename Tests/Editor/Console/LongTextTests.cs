using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor.Tests
{
    // A long log in the details pane: never one label past Unity's vertex budget ("Generated text will be truncated
    // because it exceeds 49152 vertices", "A VisualElement must not allocate more than 65535 vertices").
    public class LongTextTests
    {
        private const int Length = 20_000;

        // An FBX import's warnings joined into one message: a single 20,000-character line.
        private static string FbxWarnings()
        {
            var text = new StringBuilder("ImportFBX Warnings:\n");
            for (int i = 0; text.Length < Length; i++)
            {
                text.Append("Can't generate normals for blendshape 'Shape ").Append(i).Append("' on mesh 'Body', mesh has no smoothing groups.");
            }

            return text.ToString(0, Length);
        }

        // A Burst error listing every folder it searched: hundreds of short lines.
        private static string BurstError()
        {
            var text = new StringBuilder("Failed to find entry-points:\nMono.Cecil.AssemblyResolutionException: Failed to resolve assembly: 'Orbiters.Mixer.Editor.VRChat'");
            for (int i = 0; text.Length < Length; i++)
            {
                text.Append("\nH:\\metaverse\\unity projects\\N4N0 sample avatar\\Library\\PackageCache\\com.unity.package").Append(i).Append("@1.0.0\\Runtime");
            }

            return text.ToString(0, Length);
        }

        private static string[] Parts(VisualElement box) =>
            box.Query<Label>(className: LongText.PartClass).ToList().Select(label => label.text).ToArray();

        [Test]
        public void PiecesJoinBackIntoTheTextAndStayUnderTheLimit()
        {
            foreach (string text in new[] { FbxWarnings(), BurstError(), new string('x', Length), string.Concat(Enumerable.Repeat("\U0001F600", Length / 2)) })
            {
                var parts = LongText.Split(text);
                Assert.AreEqual(Length, text.Length);
                Assert.AreEqual(text, string.Concat(parts));
                Assert.Greater(parts.Count, 1);
                foreach (string part in parts)
                {
                    Assert.LessOrEqual(part.Length, LongText.ChunkChars);
                    Assert.Greater(part.Length, 0);
                    Assert.IsFalse(char.IsHighSurrogate(part[part.Length - 1]), "a surrogate pair stays in one piece");
                }
            }

            Assert.IsEmpty(LongText.Split(string.Empty));
            Assert.AreEqual(new[] { "short" }, LongText.Split("short").ToArray());
        }

        [Test]
        public void PiecesEndAtLineEndsOrSpaces()
        {
            var lines = LongText.Split(BurstError());
            foreach (string part in lines.Take(lines.Count - 1))
            {
                Assert.AreEqual('\n', part[part.Length - 1]);
            }

            var words = LongText.Split(FbxWarnings());
            foreach (string part in words.Take(words.Count - 1))
            {
                Assert.AreEqual(' ', part[part.Length - 1], "a single long line is cut between words");
            }
        }

        [Test]
        public void ATwentyThousandCharacterMessageNeverGoesInOneLabel()
        {
            string text = FbxWarnings();
            var box = LongText.Build(text, "lg-details__message");
            foreach (var label in box.Query<Label>().ToList())
            {
                // Four vertices per glyph: 16,000 at most, a quarter of what one element may hold.
                Assert.LessOrEqual(label.text.Length, LongText.ChunkChars);
                Assert.Less(label.text.Length * 4, 49_152);
            }

            Assert.LessOrEqual(Parts(box).Sum(part => part.Length), LongText.InitialChars, "the first pieces only, then Show all");
            Assert.IsNotNull(box.Q(className: LongText.MoreClass));
            StringAssert.StartsWith("ImportFBX Warnings:\nCan't generate normals", Parts(box)[0]);

            Assert.IsTrue(LongText.ShowAll(box));
            Assert.IsNull(box.Q(className: LongText.MoreClass));
            Assert.AreEqual(text, string.Concat(Parts(box)), "Show all adds the rest, each piece in a label of its own");
            Assert.IsTrue(Parts(box).All(part => part.Length <= LongText.ChunkChars));
            Assert.IsFalse(LongText.ShowAll(box));
        }

        [Test]
        public void LinesCutBetweenLabelsKeepNoEmptyLine()
        {
            string text = BurstError();
            var box = LongText.Build(text, "lg-details__output");
            LongText.ShowAll(box);
            var parts = Parts(box);
            Assert.Greater(parts.Length, 1);
            Assert.IsTrue(parts.Take(parts.Length - 1).All(part => !part.EndsWith("\n")), "the break between two labels is the line break");
            Assert.AreEqual(text, string.Join("\n", parts));
        }

        // The text's own last line break is not a gap between two labels: it stays, as one label would show it.
        [Test]
        public void TheLastLineBreakOfTheTextStays()
        {
            string body = BurstError();
            foreach (string text in new[] { body.TrimEnd('\n') + "\n", body.TrimEnd('\n'), "one line\n", "one line" })
            {
                var box = LongText.Build(text, "lg-details__output");
                LongText.ShowAll(box);
                var parts = Parts(box);
                Assert.AreEqual(text, string.Join("\n", parts), text.Length + " characters, ending with a line break: " + text.EndsWith("\n"));
                Assert.AreEqual(text.EndsWith("\n"), parts.Last().EndsWith("\n"));
            }
        }

        [Test]
        public void TagsInALongMessageStayText()
        {
            string text = string.Concat(Enumerable.Repeat("<color=red>List<int></color> ", 1000));
            var box = LongText.Build(text, "lg-details__message");
            LongText.ShowAll(box);
            foreach (string part in Parts(box))
            {
                StringAssert.StartsWith("<noparse>", part);
                Assert.LessOrEqual(part.Length, LongText.ChunkChars + "<noparse></noparse>".Length);
            }
        }

        [Test]
        public void ShortMessagesAreOneLabel()
        {
            var box = LongText.Build("NullReferenceException: Object reference not set to an instance of an object", "lg-details__message");
            Assert.AreEqual(1, Parts(box).Length);
            Assert.IsNull(box.Q(className: LongText.MoreClass));
            Assert.IsFalse(LongText.ShowAll(box));
        }

        [Test]
        public void EvenShowAllStopsAtTheLimit()
        {
            string text = new string('a', LongText.MaxChars + 5000);
            var box = LongText.Build(text, "lg-details__message");
            StringAssert.StartsWith("Showing ", box.Q(className: LongText.MoreClass).Q<Label>().text);
            LongText.ShowAll(box);
            Assert.AreEqual(LongText.MaxChars, Parts(box).Sum(part => part.Length));
            StringAssert.Contains("Copy it to get everything", box.Query<Label>(className: "lg-details__note").Last().text);
        }

        [Test]
        public void TooltipsAreCapped()
        {
            Assert.IsNull(LongText.Tooltip(null));
            Assert.AreEqual("short", LongText.Tooltip("short"));
            string tooltip = LongText.Tooltip(FbxWarnings());
            Assert.AreEqual(LongText.TooltipChars + 1, tooltip.Length);
            StringAssert.EndsWith("…", tooltip);
        }

        [Test]
        public void PlaceholdersTakeAShortPartOfALongLog()
        {
            var entries = KnownLogs.Parse(@"{ ""explanations"": [
                { ""id"": ""tests.long"", ""needle"": ""ZZ-long"", ""pattern"": ""ZZ-long (?<all>.+)"", ""severity"": ""info"", ""source"": ""Tests"", ""title"": ""About {all}"" } ] }");
            try
            {
                LogExplanations.SetRemote(entries);
                string message = "ZZ-long " + new string('y', Length);
                var explained = LogExplanations.Explain(LogExplanations.Match(message, "", LogLevel.Warning), message);
                Assert.AreEqual("About " + new string('y', LogExplanations.MaxPlaceholderChars) + "…", explained.Title);
            }
            finally
            {
                LogExplanations.SetRemote(null);
            }
        }
    }
}
