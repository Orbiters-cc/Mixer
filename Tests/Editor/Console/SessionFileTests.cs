using System;
using System.IO;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    public class SessionFileTests
    {
        private const int Process = 1234;
        private const long Start = 5678;

        private const string LogStack =
            "UnityEngine.Debug:Log (object)\n" +
            "MCBLogger:Log (string) (at ./Packages/orbiters.mcb/Runtime/MCBLogger.cs:59)\n";

        private const string ExceptionStack =
            "Orbiters.MyAvatar.Editor.VrcSdkThumbnail.Update () (at ./Packages/orbiters.myavatar/Editor/VrcSdkThumbnail.cs:41)\n";

        private string folder;
        private SessionFile saving;
        private SessionFile loading;
        private readonly System.Collections.Generic.List<SessionFile> opened = new System.Collections.Generic.List<SessionFile>();

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "mixer-session-" + Guid.NewGuid().ToString("N"));
            saving = Fresh();
            loading = Fresh();
        }

        [TearDown]
        public void TearDown()
        {
            saving.Close();
            loading.Close();
            foreach (var file in opened)
            {
                file.Close();
            }

            opened.Clear();
            try
            {
                if (Directory.Exists(folder) && Path.GetFileName(folder).StartsWith("mixer-session-", StringComparison.Ordinal))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (IOException)
            {
                // A text read after its session closed reopened the file: the temporary folder stays.
            }
        }

        // Another domain's view of the same folder.
        private SessionFile Fresh()
        {
            var file = new SessionFile(folder);
            opened.Add(file);
            return file;
        }

        private static SnapshotInfo Info() => new SnapshotInfo { UnityRows = 12, UnityAnchor = "anchor", HistoryFrom = 0, HistoryTo = 4, HistoryAnchor = "h" };

        private LogStore Load(SessionFile file = null)
        {
            var store = (file ?? loading).Load(Process, Start, out var info, out string problem);
            Assert.IsNull(problem, problem);
            Assert.IsNotNull(store);
            Assert.AreEqual(12, info.UnityRows);
            return store;
        }

        private static void AssertSame(LogStore expected, LogStore actual)
        {
            Assert.AreEqual(expected.Count, actual.Count);
            Assert.AreEqual(expected.MessageCount, actual.MessageCount);
            Assert.AreEqual(expected.GroupCount, actual.GroupCount);
            Assert.AreEqual(expected.Grouping, actual.Grouping);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.AreEqual(expected.TimeAt(i), actual.TimeAt(i));
                Assert.AreEqual(expected.FlagsAt(i), actual.FlagsAt(i));
                ref var a = ref expected.Message(expected.MessageAt(i));
                ref var b = ref actual.Message(actual.MessageAt(i));
                Assert.AreEqual(expected.Texts[a.Text], actual.Texts[b.Text]);
                Assert.AreEqual(expected.Texts.Plain(a.Text), actual.Texts.Plain(b.Text));
                Assert.AreEqual(expected.Texts[a.Stack], actual.Texts[b.Stack]);
                Assert.AreEqual(expected.Texts[a.Callsite], actual.Texts[b.Callsite]);
                Assert.AreEqual(a.Count, b.Count);
                Assert.AreEqual(a.First, b.First);
                Assert.AreEqual(a.Last, b.Last);
                Assert.AreEqual(a.Group, b.Group);
                Assert.AreEqual(a.Flags, b.Flags);
                Assert.AreEqual(expected.Group(a.Group).Count, actual.Group(b.Group).Count);
                Assert.AreEqual(expected.SourceOf(expected.MessageAt(i)).Name, actual.SourceOf(actual.MessageAt(i)).Name);
            }
        }

        [Test]
        public void RoundTrip()
        {
            var store = new LogStore();
            store.Add(100, "<color=red>Red</color> thing", LogStack, LogVariant.Log);
            store.Add(200, "Boom", ExceptionStack, LogVariant.Exception);
            store.Add(250, "Boom", ExceptionStack, LogVariant.Exception, OccurrenceFlags.BackgroundThread);
            store.AddCompile(300, "Assets/B.cs(1,1): warning CS0618: obsolete", false, store.AssemblyId("Asm"), null, 0, 0);
            store.Add(400, "Half a pair \ud83d", string.Empty, LogVariant.Warning);
            store.SetContext(1, 42);
            store.AddEvent(150, SessionEventKind.EnterPlayMode);
            store.SetGrouping(GroupingMode.SimilarText);
            saving.Save(store, Info(), Process, Start);

            Assert.IsNull(Fresh().Load(999, Start, out _, out string other), "another editor process");
            Assert.IsNull(other, "another editor's session is not a problem");

            var loaded = Load();
            AssertSame(store, loaded);
            Assert.AreEqual("Half a pair \ud83d", loaded.Texts[loaded.Message(loaded.MessageAt(4)).Text], "UTF-16 units come back unchanged");
            Assert.IsTrue(loaded.TryGetContext(1, out int context) && context == 42);
            Assert.AreEqual(1, loaded.Events.Count);
            Assert.IsTrue(loaded.HasActiveCompileMessage("Assets/B.cs(1,1): warning CS0618: obsolete", out _));
        }

        [Test]
        public void TextsAreReadOnlyWhenAsked()
        {
            var store = new LogStore();
            for (int i = 0; i < 50; i++)
            {
                store.Add(100 + i, "Message " + i, LogStack, LogVariant.Log);
            }

            saving.Save(store, Info(), Process, Start);
            var loaded = Load();
            int text = loaded.Message(loaded.MessageAt(7)).Text;
            Assert.IsFalse(loaded.Texts.IsLoaded(text), "nothing is read at load");
            Assert.AreEqual("Message 7", loaded.Texts[text]);
            Assert.IsTrue(loaded.Texts.IsLoaded(text));
            Assert.IsFalse(loaded.Texts.IsLoaded(loaded.Message(loaded.MessageAt(8)).Text));

            // A known text logged again finds its old id and message.
            Assert.AreEqual(loaded.MessageAt(9), loaded.MessageAt(loaded.Add(500, "Message 9", LogStack, LogVariant.Log)));
            Assert.AreEqual(51, loaded.Count);
            Assert.AreEqual(2, loaded.Message(loaded.MessageAt(9)).Count);
        }

        [Test]
        public void SecondSaveAppendsOnlyWhatIsNew()
        {
            var store = new LogStore();
            store.Add(100, "First", LogStack, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            long texts = TextsLength();

            store.Add(200, "Second", LogStack, LogVariant.Warning);
            store.Add(300, "First", LogStack, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            Assert.AreEqual(texts + 4 + "Second".Length * 2, TextsLength(), "only the new text (its callsite is the same) is appended");
            AssertSame(store, Load());

            // The domain after the reload goes on appending to the same files.
            var next = Fresh();
            var loaded = Load(next);
            loaded.Add(400, "Third", string.Empty, LogVariant.Error);
            next.Save(loaded, Info(), Process, Start);
            next.Close();
            AssertSame(loaded, Load(Fresh()));
        }

        [Test]
        public void TruncatedFilesAreNeverRead()
        {
            var store = new LogStore();
            store.Add(100, "First", LogStack, LogVariant.Log);
            store.Add(200, "Second", ExceptionStack, LogVariant.Error);
            saving.Save(store, Info(), Process, Start);
            saving.Close();

            string texts = Directory.GetFiles(folder, "texts-*.bin")[0];
            byte[] whole = File.ReadAllBytes(texts);
            File.WriteAllBytes(texts, Cut(whole, 6));
            Assert.IsNull(Fresh().Load(Process, Start, out _, out string problem));
            Assert.IsNotNull(problem, "a cut texts file is a problem to report");
            File.WriteAllBytes(texts, whole);

            string head = Directory.GetFiles(folder, "head-*.bin")[0];
            byte[] headBytes = File.ReadAllBytes(head);
            File.WriteAllBytes(head, Cut(headBytes, 3));
            Assert.IsNull(Fresh().Load(Process, Start, out _, out problem));
            Assert.IsNotNull(problem, "a cut head is a problem to report");
            File.WriteAllBytes(head, headBytes);

            string times = Directory.GetFiles(folder, "times-*.bin")[0];
            File.WriteAllBytes(times, Cut(File.ReadAllBytes(times), 1));
            Assert.IsNull(Fresh().Load(Process, Start, out _, out problem));
            Assert.IsNotNull(problem, "cut occurrences are a problem to report");
        }

        [Test]
        public void TailsBeyondTheHeadAreIgnoredThenCut()
        {
            var store = new LogStore();
            store.Add(100, "First", LogStack, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            saving.Close();
            long texts = TextsLength();

            // A save that failed after appending, before its head.
            string textsPath = Directory.GetFiles(folder, "texts-*.bin")[0];
            string timesPath = Directory.GetFiles(folder, "times-*.bin")[0];
            File.AppendAllText(textsPath, "half a text");
            File.AppendAllText(timesPath, "12345");

            var next = Fresh();
            var loaded = Load(next);
            AssertSame(store, loaded);
            loaded.Add(200, "Second", string.Empty, LogVariant.Log);
            next.Save(loaded, Info(), Process, Start);
            next.Close();
            Assert.AreEqual(texts + 4 + "Second".Length * 2, TextsLength(), "the tail was cut before appending");
            AssertSame(loaded, Load(Fresh()));
        }

        [Test]
        public void NewGenerationsStartNewFiles()
        {
            var store = new LogStore();
            store.Add(100, "Before the clear", LogStack, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            store.Clear(150);
            store.Add(200, "After the clear", string.Empty, LogVariant.Warning);
            saving.Save(store, Info(), Process, Start);

            Assert.AreEqual(1, Directory.GetFiles(folder, "texts-*.bin").Length, "the old generation's files are gone");
            Assert.AreEqual(1, Directory.GetFiles(folder, "times-*.bin").Length);
            var loaded = Load();
            AssertSame(store, loaded);
            Assert.AreEqual("After the clear", loaded.Texts[loaded.Message(loaded.MessageAt(0)).Text]);
        }

        [Test]
        public void TrimAndCompactionRewriteTheirParts()
        {
            var store = new LogStore();
            for (int i = 0; i < 6000; i++)
            {
                store.Add(100 + i, "Distinct " + i, LogStack, LogVariant.Log);
            }

            saving.Save(store, Info(), Process, Start);
            long rows = store.RowsGeneration;
            long texts = store.Texts.Generation;
            store.Trim(1000);
            Assert.AreNotEqual(rows, store.RowsGeneration);
            Assert.AreNotEqual(texts, store.Texts.Generation, "the dead messages were compacted");
            store.Add(9000, "Distinct 5999", LogStack, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            var loaded = Load();
            AssertSame(store, loaded);
            Assert.AreEqual(1001, loaded.Count);
            Assert.AreEqual(2, loaded.Message(loaded.MessageAt(1000)).Count);

            // A trim that keeps every message keeps the texts' files and rewrites the occurrences only.
            texts = loaded.Texts.Generation;
            var next = Fresh();
            loaded = Load(next);
            loaded.Trim(900);
            Assert.AreEqual(texts, loaded.Texts.Generation);
            next.Save(loaded, Info(), Process, Start);
            next.Close();
            AssertSame(loaded, Load(Fresh()));
        }

        [Test]
        public void ADamagedTextIsNeverTakenForALog()
        {
            var store = new LogStore();
            store.Add(100, "First text", string.Empty, LogVariant.Log);
            store.Add(200, "Second text", string.Empty, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            saving.Close();

            // The same length, other bytes: the second text's length prefix, then (separately) one of its characters.
            string texts = Directory.GetFiles(folder, "texts-*.bin")[0];
            byte[] whole = File.ReadAllBytes(texts);
            int second = 4 + "First text".Length * 2;
            foreach (int at in new[] { second, second + 6 })
            {
                var damaged = (byte[])whole.Clone();
                damaged[at] ^= 0x5A;
                File.WriteAllBytes(texts, damaged);
                var session = Fresh();
                var loaded = Load(session);
                Assert.AreEqual("First text", loaded.Texts[loaded.Message(loaded.MessageAt(0)).Text]);
                int text = loaded.Message(loaded.MessageAt(1)).Text;
                Assert.AreEqual(TextTable.Unreadable, loaded.Texts[text]);
                Assert.IsTrue(loaded.Texts.Faulted, "the store must start over");
                Assert.IsFalse(loaded.Texts.IsLoaded(text), "the placeholder is never cached");
                Assert.Throws<InvalidDataException>(() => session.Save(loaded, Info(), Process, Start), "and never saved");
                session.Close();
            }
        }

        [Test]
        public void ACompactionThatMeetsADamagedTextKeepsTheOldTable()
        {
            var store = new LogStore();
            for (int i = 0; i < 2000; i++)
            {
                store.Add(100 + i, "Distinct " + i, string.Empty, LogVariant.Log);
            }

            saving.Save(store, Info(), Process, Start);
            saving.Close();

            // Damage a text the compaction will copy (the newest log's), keeping the file's length.
            string path = Directory.GetFiles(folder, "texts-*.bin")[0];
            byte[] bytes = File.ReadAllBytes(path);
            byte[] needle = System.Text.Encoding.Unicode.GetBytes("Distinct 1999");
            int at = IndexOf(bytes, needle);
            Assert.Greater(at, 0);
            bytes[at + 2] ^= 0x20;
            File.WriteAllBytes(path, bytes);

            var session = Fresh();
            var loaded = Load(session);
            long generation = loaded.Texts.Generation;
            int newest = loaded.Message(loaded.MessageAt(loaded.Count - 1)).Text;
            loaded.Trim(500);
            Assert.AreEqual(500, loaded.Count, "the trim itself happened");
            Assert.AreEqual(generation, loaded.Texts.Generation, "the compaction was dropped");
            Assert.IsTrue(loaded.Texts.Faulted, "the store must start over");
            Assert.AreEqual(newest, loaded.Message(loaded.MessageAt(loaded.Count - 1)).Text, "ids still name the old table");
            Assert.IsFalse(loaded.Texts.IsLoaded(newest), "and the placeholder was never kept");
            session.Close();
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                {
                    j++;
                }

                if (j == needle.Length)
                {
                    return i;
                }
            }

            return -1;
        }

        [Test]
        public void HugeCountsInADamagedHeadAllocateNothing()
        {
            var store = new LogStore();
            store.Add(100, "First", string.Empty, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            saving.Close();
            string head = Directory.GetFiles(folder, "head-*.bin")[0];
            byte[] bytes = File.ReadAllBytes(head);
            // Every 4-byte field in turn set to a huge count: the load refuses, quickly, without running out of memory.
            for (int at = 8; at + 4 <= bytes.Length; at += 4)
            {
                var damaged = (byte[])bytes.Clone();
                BitConverter.GetBytes(400_000_000).CopyTo(damaged, at);
                File.WriteAllBytes(head, damaged);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Fresh().Load(Process, Start, out _, out _);
                Assert.Less(watch.ElapsedMilliseconds, 2000, "field at " + at);
            }
        }

        [Test]
        public void ANewHeadNeverReplacesTheOldOneInPlace()
        {
            var store = new LogStore();
            store.Add(100, "First", string.Empty, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            store.Add(200, "Second", string.Empty, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            var heads = Directory.GetFiles(folder, "head-*.bin");
            Assert.AreEqual(1, heads.Length, "the older head goes once the new one is in place");
            StringAssert.EndsWith("head-0000000002.bin", heads[0]);
            Assert.AreEqual(2, Load().Count);
        }

        [Test]
        public void AFailedSaveKeepsTheLastCommittedSession()
        {
            var store = new LogStore();
            store.Add(100, "Saved before", string.Empty, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);

            // The next save fails after appending (its head can't be written): nothing committed is touched.
            store.Add(200, "Never committed", string.Empty, LogVariant.Log);
            Directory.CreateDirectory(Path.Combine(folder, "head.tmp"));
            Assert.Catch<Exception>(() => saving.Save(store, Info(), Process, Start));
            saving.MarkSaveFailed();
            Directory.Delete(Path.Combine(folder, "head.tmp"));

            var loaded = Fresh().Load(Process, Start, out var info, out string problem);
            Assert.IsNotNull(loaded, "the committed session is still there");
            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(12, info.UnityRows);
            StringAssert.Contains("save before this reload failed", problem, "and the next load says so");
        }

        [Test]
        public void TwoFailedSavesInARowKeepTheCommittedSession()
        {
            var store = new LogStore();
            store.Add(100, "Saved before", string.Empty, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            string blocker = Path.Combine(folder, "head.tmp");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                store.Add(200 + attempt, "Never committed " + attempt, string.Empty, LogVariant.Log);
                Directory.CreateDirectory(blocker);
                Assert.Catch<Exception>(() => saving.Save(store, Info(), Process, Start));
                saving.MarkSaveFailed();
                Directory.Delete(blocker);
            }

            var loaded = Fresh().Load(Process, Start, out _, out string problem);
            Assert.AreEqual(1, loaded.Count);
            Assert.IsNotNull(problem, "LogCapture then starts over from Unity's console");
        }

        [Test]
        public void ARecoverySavedMidImportResumesAndRetiresTheOldSession()
        {
            var store = new LogStore();
            store.Add(100, "Saved before", string.Empty, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            saving.MarkSaveFailed();

            // The recovery: an empty store importing Unity's console, rows 0 to 900, when scripts reload again.
            Assert.IsNotNull(Fresh().Load(Process, Start, out _, out string problem));
            Assert.IsNotNull(problem);
            var recovery = new LogStore();
            recovery.Add(500, "Logged during the import", string.Empty, LogVariant.Log);
            var next = Fresh();
            next.Save(recovery, new SnapshotInfo { UnityRows = 950, HistoryFrom = 300, HistoryTo = 900, HistoryAnchor = "row 899" }, Process, Start);
            next.Close();

            var resumed = Fresh().Load(Process, Start, out var info, out problem);
            Assert.IsNull(problem, "the recovery's save is a normal session: the marker and the old head are gone");
            Assert.AreEqual(1, Directory.GetFiles(folder, "head-*.bin").Length);
            Assert.AreEqual(1, resumed.Count);
            Assert.AreEqual(300, info.HistoryFrom, "the import goes on where it was");
            Assert.AreEqual(900, info.HistoryTo);
            Assert.AreEqual("row 899", info.HistoryAnchor);
        }

        [Test]
        public void TheNewestValidHeadWins()
        {
            var store = new LogStore();
            store.Add(100, "First", string.Empty, LogVariant.Log);
            saving.Save(store, Info(), Process, Start);
            saving.Close();
            string committed = Directory.GetFiles(folder, "head-*.bin")[0];
            byte[] bytes = File.ReadAllBytes(committed);

            // A newer head that is damaged (cut): the older, whole one is read, and the damage is said.
            var cut = new byte[bytes.Length - 3];
            Array.Copy(bytes, cut, cut.Length);
            File.WriteAllBytes(Path.Combine(folder, "head-0000000009.bin"), cut);
            var loaded = Fresh().Load(Process, Start, out _, out string problem);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(1, loaded.Count);
            Assert.IsNotNull(problem);
        }

        [Test]
        public void StrippedCopiesReadBackCountAgainstTheBudget()
        {
            var store = new LogStore();
            for (int i = 0; i < 3000; i++)
            {
                store.Add(100 + i, "<b>Rich text log number " + i + " with some padding</b>", string.Empty, LogVariant.Log);
            }

            saving.Save(store, Info(), Process, Start);
            var loaded = Load();
            long raw = loaded.Texts.TotalChars;
            loaded.MaxTextChars = raw + 1000;
            loaded.KeepWithinTextBudget();
            Assert.AreEqual(3000, loaded.Count, "within the budget: nothing goes");

            // A search reads every text back, with its stripped copy, while no log arrives.
            for (int i = 0; i < loaded.MessageCount; i++)
            {
                loaded.Texts.Plain(loaded.Message(i).Text);
            }

            Assert.Greater(loaded.Texts.TotalChars, loaded.MaxTextChars, "the copies are counted");
            loaded.KeepWithinTextBudget();
            Assert.LessOrEqual(loaded.Texts.TotalChars, loaded.MaxTextChars);
            Assert.Less(loaded.Count, 3000);
        }

        [Test]
        public void StableHashIsTheSameEverywhere()
        {
            Assert.AreEqual(unchecked((int)2166136261u), TextTable.Hash(string.Empty));
            Assert.AreEqual(TextTable.Hash("Boom"), TextTable.Hash("Bo" + "om"));
            Assert.AreNotEqual(TextTable.Hash("Boom"), TextTable.Hash("Bomo"));
        }

        private long TextsLength() => new FileInfo(Directory.GetFiles(folder, "texts-*.bin")[0]).Length;

        private static byte[] Cut(byte[] bytes, int by)
        {
            var cut = new byte[bytes.Length - by];
            Array.Copy(bytes, cut, cut.Length);
            return cut;
        }
    }
}
