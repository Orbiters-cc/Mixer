using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    public class LogModelTests
    {
        private const string LogStack =
            "UnityEngine.Debug:Log (object)\n" +
            "MCBLogger:Log (string) (at ./Packages/orbiters.mcb/Runtime/MCBLogger.cs:59)\n" +
            "VersionActions:Infer (System.Collections.Generic.IReadOnlyList`1<CustomBaseVersion>) (at ./Packages/orbiters.mcb/Editor/Features/VersionActions.cs:3214)\n" +
            "UnityEditor.EditorApplication:Internal_CallUpdateFunctions ()\n";

        private const string ExceptionStack =
            "VRC.SDK3A.Editor.VRCSdkControlPanelAvatarBuilder.get_SelectedAvatar () (at ./Packages/com.vrchat.avatars/Editor/VRCSDK/SDK3A/VRCSdkControlPanelAvatarBuilder.cs:3523)\n" +
            "Orbiters.MyAvatar.Editor.VrcSdkThumbnail.Update () (at ./Packages/orbiters.myavatar/Editor/VrcSdkThumbnail.cs:41)\n" +
            "UnityEditor.EditorApplication.Internal_CallUpdateFunctions () (at <4b2d1ab6b4d34d3c9f4d4dbd2b8b1b2a>:0)\n";

        [Test]
        public void TextTableInternsOnceAndStripsRichText()
        {
            var texts = new TextTable(16);
            int a = texts.Intern("<b><color=#2EA3FF>MCP-FOR-UNITY</color></b>: ready");
            int b = texts.Intern("<b><color=#2EA3FF>MCP-FOR-UNITY</color></b>: ready");
            int c = texts.Intern("List<int> is fine");
            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
            Assert.AreEqual(0, texts.Intern(string.Empty));
            Assert.AreEqual("MCP-FOR-UNITY: ready", texts.Plain(a));
            Assert.AreEqual("List<int> is fine", texts.Plain(c), "generic types are not tags");
            for (int i = 0; i < 100; i++)
            {
                texts.Intern("grow " + i);
            }

            Assert.AreEqual("List<int> is fine", texts[c]);
        }

        [Test]
        public void RichTextHelpers()
        {
            Assert.AreEqual("first", RichText.FirstLine("first\r\nsecond", 100));
            Assert.AreEqual("second", RichText.SecondLine("first\nsecond\nthird", 100));
            Assert.AreEqual("abc…", RichText.FirstLine("abcdef", 3));
            var ranges = new List<(int, int)> { (2, 3) };
            string marked = RichText.Highlight("a <b> c", ranges, "#00DA6D59");
            StringAssert.Contains("<mark=#00DA6D59>", marked);
            StringAssert.Contains("<noparse><b></noparse>", marked);
        }

        [Test]
        public void StackTracesFindTheCallerBehindLoggerWrappers()
        {
            var frames = StackTraces.Parse(LogStack);
            Assert.AreEqual(4, frames.Count);
            Assert.AreEqual(StackFrameKind.Logging, frames[0].Kind);
            Assert.AreEqual(StackFrameKind.User, frames[1].Kind);
            Assert.AreEqual(StackFrameKind.Internal, frames[3].Kind);
            int callsite = StackTraces.FindCallsite(frames);
            Assert.AreEqual(2, callsite, "MCBLogger:Log is a wrapper: the caller is VersionActions");
            Assert.AreEqual("VersionActions.Infer", StackTraces.ShortMethod(frames[callsite].Method));
            Assert.AreEqual(3214, frames[callsite].Line);
            Assert.AreEqual("Packages/orbiters.mcb/Editor/Features/VersionActions.cs", StackTraces.NormalizePath(frames[callsite].File));
            var patched = StackTraces.ParseFrame("(wrapper dynamic-method) UnityEngine.Debug:UnityEngine.Debug.LogError_Patch1 (object)");
            Assert.AreEqual(StackFrameKind.Logging, patched.Kind, "a Harmony-patched Debug.LogError is still logging");
        }

        [Test]
        public void StackTracesReadExceptionFrames()
        {
            var frames = StackTraces.Parse(ExceptionStack);
            Assert.AreEqual(3, frames.Count);
            Assert.AreEqual(0, StackTraces.FindCallsite(frames));
            Assert.AreEqual("VRCSdkControlPanelAvatarBuilder.get_SelectedAvatar", StackTraces.ShortMethod(frames[0].Method));
            Assert.IsFalse(frames[2].HasFile, "<hash>:0 is no file");
            var mono = StackTraces.ParseFrame("Foo.Bar.Baz (System.Int32 x) [0x00012] in C:\\Projects\\Foo\\Assets\\Baz.cs:42");
            Assert.AreEqual(42, mono.Line);
            Assert.AreEqual("Foo.Bar.Baz (System.Int32 x)", mono.Method);
            Assert.AreEqual("Packages/com.vrchat.base/Editor/X.cs",
                StackTraces.NormalizePath("./Library/PackageCache/com.vrchat.base@3.7.0/Editor/X.cs"));
            Assert.AreEqual("Outer.Method", StackTraces.ShortMethod("Outer/<Method>d__12:MoveNext ()"));
        }

        [Test]
        public void CompilerLocations()
        {
            Assert.IsTrue(StackTraces.TryParseCompilerLocation("Assets/My Scripts/Foo (old).cs(10,5): error CS0103: The name 'x' does not exist",
                out string file, out int line, out int column));
            Assert.AreEqual("Assets/My Scripts/Foo (old).cs", file);
            Assert.AreEqual(10, line);
            Assert.AreEqual(5, column);
            Assert.IsTrue(LogCapture.IsCompilerMessage("Assets/Foo.cs(1,2): warning CS0618: 'X' is obsolete", out bool error));
            Assert.IsFalse(error);
            Assert.IsFalse(LogCapture.IsCompilerMessage("Loaded (3 assets): error count 0", out _));
        }

        [Test]
        public void SourcesComeFromPackagesFoldersAndTags()
        {
            var store = new LogStore();
            int sdk = store.Intern("MissingReferenceException: …", ExceptionStack, LogVariant.Exception);
            int mcb = store.Intern("[VersionActions] Inferred", LogStack, LogVariant.Log);
            int native = store.Intern("[VRCFury] Building", string.Empty, LogVariant.Log);
            int engine = store.Intern("Shader warning in 'Hidden/Foo'", string.Empty, LogVariant.Warning);
            int compile = store.AddCompile(0, "Assets/Foo.cs(1,1): error CS0246: The type or namespace name 'VRC' could not be found", true, 0, null, 0, 0);
            int assets = store.Intern("hi", "UnityEngine.Debug:Log (object)\nThing:Do () (at Assets/VRCFury/Scripts/Thing.cs:3)\n", LogVariant.Log);
            Assert.AreEqual("VRChat SDK", store.SourceOf(sdk).Name);
            Assert.AreEqual("MCB", store.SourceOf(mcb).Name);
            Assert.AreEqual("VRCFury", store.SourceOf(native).Name);
            Assert.AreEqual("Unity", store.SourceOf(engine).Name);
            Assert.AreEqual("Compiler", store.SourceOf(store.MessageAt(compile)).Name);
            Assert.AreEqual(store.SourceOf(native).Id, store.SourceOf(assets).Id, "VRCFury in Assets and as a tag are one source");

            // Orbiters tools without a source file in the stack (or without a stack): by namespace, class prefix and tag.
            int mixerTag = store.Intern("[Mixer] Rest-pose edit kept", string.Empty, LogVariant.Log);
            int mixer = store.Intern("mixer", "UnityEngine.Debug:LogWarning (object)\nOrbiters.Mixer.Editor.MixerSession:Commit ()\n", LogVariant.Warning);
            int refit = store.Intern("refit", "UnityEngine.Debug:LogWarning (object)\nOrbiters.ReFit.Editor.ReFitEngine:Run ()\n", LogVariant.Warning);
            int mcbClass = store.Intern("mcb", "UnityEngine.Debug:LogWarning (object)\nMCBUtils:CopyFile (string,string)\n", LogVariant.Warning);
            int lookalike = store.Intern("lookalike", "UnityEngine.Debug:LogWarning (object)\nMCBuildHelper:Run ()\n", LogVariant.Warning);
            Assert.AreEqual("Mixer", store.SourceOf(mixerTag).Name);
            Assert.AreEqual("Mixer", store.SourceOf(mixer).Name);
            Assert.AreEqual("ReFit", store.SourceOf(refit).Name);
            Assert.AreEqual("MCB", store.SourceOf(mcbClass).Name);
            Assert.AreNotEqual("MCB", store.SourceOf(lookalike).Name, "a class whose name only starts with the letters MCB");
            Assert.AreEqual("MCB", SourceCatalog.NameForNamespace("MCBEditor"));
            Assert.IsNull(SourceCatalog.NameForNamespace("MCBridge.Client"));
        }

        [Test]
        public void StackTracesAreReadOncePerStackWithTheSameResult()
        {
            const string unityOnly = "UnityEngine.Debug:LogWarning (object)\nUnityEditor.EditorApplication:Internal_CallUpdateFunctions ()\n";
            var conditions = new[]
            {
                "Count 1", "Count 2", "[Mixer] tagged", "Packages/orbiters.toolkit/Editor/UI/orbit.uss (line 27): warning", "[VRCFury] Building"
            };

            // One store sees every text with each stack (cached after the first); each fresh store sees one message only.
            var shared = new LogStore();
            foreach (string stack in new[] { LogStack, ExceptionStack, unityOnly, string.Empty })
            {
                foreach (string condition in conditions)
                {
                    int a = shared.Intern(condition, stack, LogVariant.Warning);
                    var fresh = new LogStore();
                    int b = fresh.Intern(condition, stack, LogVariant.Warning);
                    ref var cached = ref shared.Message(a);
                    ref var alone = ref fresh.Message(b);
                    string label = condition + " / " + stack.Length;
                    Assert.AreEqual(fresh.SourceOf(b).Name, shared.SourceOf(a).Name, label);
                    Assert.AreEqual(fresh.Texts[alone.Callsite], shared.Texts[cached.Callsite], label);
                    Assert.AreEqual(fresh.Texts[alone.File], shared.Texts[cached.File], label);
                    Assert.AreEqual(alone.Line, cached.Line, label);
                }
            }
        }

        [Test]
        public void TextsAreBoundedBySizeNotOnlyByCount()
        {
            var store = new LogStore { MaxTextChars = 1 << 20 };
            string padding = new string('x', 100);
            for (int i = 0; i < 40_000; i++)
            {
                store.Add(1000 + i, "Distinct log " + i + " " + padding, LogStack, LogVariant.Log);
            }

            Assert.Less(store.Texts.TotalChars, 2L << 20, "texts nobody logs any more were let go");
            Assert.Less(store.Count, 40_000);
            Assert.AreEqual(1000 + 39_999, store.LastTime);
            Assert.AreEqual("Distinct log 39999 " + padding, store.Texts[store.Message(store.MessageAt(store.EndId - 1)).Text]);
            Assert.AreEqual(store.Count, CountAll(store), "counters still add up after the trims");
        }

        [Test]
        public void TheTextBudgetHoldsForFewLargeLogsHistoryAndStrippedCopies()
        {
            // Few logs (far under any count limit), each large.
            var store = new LogStore { MaxTextChars = 1 << 20 };
            string large = new string('a', 200_000);
            for (int i = 0; i < 40; i++)
            {
                store.Add(1000 + i, i + " " + large, string.Empty, LogVariant.Log);
                Assert.LessOrEqual(store.Texts.TotalChars, (1 << 20) + 300_000, "after log " + i);
            }

            Assert.AreEqual("39 " + large, store.Texts[store.Message(store.MessageAt(store.EndId - 1)).Text], "the newest log stays");

            // One log larger than the whole budget is cut, and says so.
            var huge = new LogStore();
            string original = new string('h', LogStore.MaxTextLength + 5000);
            int id = huge.Intern(original, string.Empty, LogVariant.Log);
            string kept = huge.Texts[huge.Message(id).Text];
            Assert.LessOrEqual(kept.Length, LogStore.MaxTextLength, "the note included");
            StringAssert.EndsWith(" more characters left out by the Mixer console]", kept);
            int shown = kept.IndexOf('\n');
            StringAssert.Contains("[… " + (original.Length - shown).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " more", kept, "the count is what was left out");
            Assert.AreSame(kept, LogStore.Capped(kept), "capping a capped text changes nothing");
            Assert.AreEqual(kept, LogStore.Capped(original));
            Assert.AreEqual(id, huge.Intern(LogStore.Capped(original), string.Empty, LogVariant.Log), "cut in the queue first, the same message");

            // Rich text keeps a stripped copy: it counts.
            var tags = new TextTable();
            tags.Intern("<b>bold</b>");
            Assert.AreEqual("<b>bold</b>".Length + "bold".Length, tags.TotalChars);

            // History put in front counts against the same budget.
            var history = new LogStore { MaxTextChars = 1 << 20 };
            history.Add(5_000, "live", string.Empty, LogVariant.Log);
            history.Pin();
            var owners = new int[30];
            for (int i = 0; i < owners.Length; i++)
            {
                owners[i] = history.Intern("old " + i + " " + large, string.Empty, LogVariant.Log);
            }

            var times = new long[owners.Length];
            for (int i = 0; i < times.Length; i++)
            {
                times[i] = 100 + i;
            }

            history.Unpin();
            history.Prepend(times, owners, new byte[owners.Length], owners.Length, null);
            Assert.LessOrEqual(history.Texts.TotalChars, (1 << 20) + 300_000);
            Assert.AreEqual("live", history.Texts[history.Message(history.MessageAt(history.EndId - 1)).Text]);
            Assert.AreEqual(history.Count, CountAll(history));
        }

        [Test]
        public void PinnedMessagesSurviveATrim()
        {
            var store = new LogStore();
            int imported = store.Intern("From Unity's console", string.Empty, LogVariant.Log);
            store.Pin();
            for (int i = 0; i < 6000; i++)
            {
                store.Add(100 + i, "Distinct " + i, string.Empty, LogVariant.Log);
            }

            store.Trim(1000);
            Assert.AreEqual("From Unity's console", store.Texts[store.Message(imported).Text], "no compaction while pinned");
            store.Unpin();
            store.Prepend(new long[] { 1 }, new[] { imported }, new byte[1], 1, null);
            Assert.AreEqual("From Unity's console", store.Texts[store.Message(store.MessageAt(store.FirstId)).Text]);
            long texts = store.Texts.Generation;
            store.Trim(500);
            Assert.AreNotEqual(texts, store.Texts.Generation, "unpinned, the dead messages go");
            Assert.AreEqual("Distinct 5999", store.Texts[store.Message(store.MessageAt(store.EndId - 1)).Text]);
            Assert.AreEqual(store.Count, CountAll(store));
        }

        private static int CountAll(LogStore store)
        {
            int total = 0;
            for (int i = 0; i < store.MessageCount; i++)
            {
                total += store.Message(i).Count;
            }

            return total;
        }

        [Test]
        public void RepeatedLogsShareTheirTexts()
        {
            var texts = new QueueTexts(1000);
            var recent = new QueueTexts.Recent();
            Assert.IsTrue(texts.Admit(recent, "Boom", "at Thing.Do ()" + 1, out var condition, out var stack));
            Assert.AreEqual(4 + 15, texts.Chars);
            Assert.IsTrue(texts.Admit(recent, "Bo" + "om", "at Thing.Do ()" + 1, out var condition2, out var stack2));
            Assert.AreSame(condition, condition2, "an equal text keeps the earlier instance");
            Assert.AreSame(stack, stack2);
            Assert.AreEqual(19, texts.Chars, "and adds nothing to the queue");

            // The charge stays until the last queued log holding the text is filed.
            texts.Release(condition);
            texts.Release(stack);
            Assert.AreEqual(19, texts.Chars);
            texts.Release(condition2);
            texts.Release(stack2);
            Assert.AreEqual(0, texts.Chars);

            // Filed in between, then logged again: charged again.
            Assert.IsTrue(texts.Admit(recent, "Boom", null, out var again, out var none));
            Assert.AreSame(condition, again);
            Assert.IsNull(none);
            Assert.AreEqual(4, texts.Chars);
            texts.Release(again);

            // Over the budget: refused with nothing charged; one log alone always fits into an empty queue.
            Assert.IsTrue(texts.Admit(recent, new string('x', 990), null, out var big, out _));
            Assert.IsFalse(texts.Admit(recent, new string('y', 20), null, out _, out _));
            Assert.AreEqual(990, texts.Chars);
            texts.Release(big);
            Assert.IsTrue(texts.Admit(new QueueTexts.Recent(), new string('z', 5000), null, out var huge, out _));
            texts.Release(huge);
            Assert.AreEqual(0, texts.Chars);

            // The main thread finds a shared instance again by reference, with the same id.
            var table = new TextTable();
            int id = table.Intern(condition.Text);
            Assert.AreEqual(id, table.Intern(again.Text));
            Assert.AreEqual(id, table.Intern(string.Copy(condition.Text)));
        }

        [Test]
        public void QueueChargesAddUpUnderConcurrentAdmission()
        {
            var texts = new QueueTexts(long.MaxValue / 4);
            var queue = new System.Collections.Concurrent.ConcurrentQueue<SharedText>();
            int producers = 6;
            int done = 0;
            var threads = new System.Threading.Thread[producers];
            for (int p = 0; p < producers; p++)
            {
                int seed = p;
                threads[p] = new System.Threading.Thread(() =>
                {
                    var recent = new QueueTexts.Recent();
                    for (int i = 0; i < 20_000; i++)
                    {
                        // Runs of equal texts (shared) and new ones, per thread.
                        string text = "log " + seed + " " + i / 7;
                        if (texts.Admit(recent, text, "stack " + seed, out var c, out var s))
                        {
                            queue.Enqueue(c);
                            queue.Enqueue(s);
                        }
                    }

                    System.Threading.Interlocked.Increment(ref done);
                });
                threads[p].Start();
            }

            // The main thread files while they log.
            while (System.Threading.Volatile.Read(ref done) < producers || !queue.IsEmpty)
            {
                if (queue.TryDequeue(out var text))
                {
                    texts.Release(text);
                }
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            while (queue.TryDequeue(out var text))
            {
                texts.Release(text);
            }

            Assert.AreEqual(0, texts.Chars, "every charge went with the last log holding its text");

            // A small budget, texts filed and logged again (held again from zero): the bound holds throughout.
            var small = new QueueTexts(10_000);
            var held = new System.Collections.Concurrent.ConcurrentQueue<SharedText>();
            long peak = 0;
            done = 0;
            for (int p = 0; p < producers; p++)
            {
                int seed = p;
                threads[p] = new System.Threading.Thread(() =>
                {
                    var recent = new QueueTexts.Recent();
                    string repeated = new string((char)('a' + seed), 100);
                    for (int i = 0; i < 20_000; i++)
                    {
                        if (small.Admit(recent, repeated, i % 5 == 0 ? new string('s', 100) + i : null, out var c, out var s))
                        {
                            held.Enqueue(c);
                            if (s != null)
                            {
                                held.Enqueue(s);
                            }

                            long now = small.Chars;
                            long seen;
                            while (now > (seen = System.Threading.Interlocked.Read(ref peak)) &&
                                   System.Threading.Interlocked.CompareExchange(ref peak, now, seen) != seen)
                            {
                            }
                        }
                    }

                    System.Threading.Interlocked.Increment(ref done);
                });
                threads[p].Start();
            }

            while (System.Threading.Volatile.Read(ref done) < producers || !held.IsEmpty)
            {
                if (held.TryDequeue(out var text))
                {
                    small.Release(text);
                }
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            while (held.TryDequeue(out var text))
            {
                small.Release(text);
            }

            // Each thread may have one refused reservation in flight (taken back at once).
            Assert.LessOrEqual(peak, 10_000 + producers * 250, "a revived text is reserved like a new one");
            Assert.AreEqual(0, small.Chars);
        }

        [Test]
        public void StoreGroupsAndCounts()
        {
            var store = new LogStore();
            for (int i = 0; i < 10; i++)
            {
                store.Add(1000 + i, "Same text", LogStack, LogVariant.Log);
            }

            store.Add(2000, "Same text", ExceptionStack, LogVariant.Log);
            store.Add(3000, "Same text", LogStack, LogVariant.Warning);
            store.Add(1500, "Loaded 12 assets in 3.4 ms", string.Empty, LogVariant.Log);
            store.Add(4000, "Loaded 7 assets in 1.2 ms", string.Empty, LogVariant.Log);

            Assert.AreEqual(14, store.Count);
            Assert.AreEqual(3000, store.TimeAt(12), "times never go back");
            int sameGroup = store.Message(store.MessageAt(0)).Group;
            Assert.AreEqual(sameGroup, store.Message(store.MessageAt(10)).Group, "same text, different stack: one group");
            Assert.AreNotEqual(sameGroup, store.Message(store.MessageAt(11)).Group, "a warning with that text is another group");
            Assert.AreEqual(11, store.Group(sameGroup).Count);
            Assert.AreEqual(2, store.Group(sameGroup).Variants);
            Assert.AreNotEqual(store.Message(store.MessageAt(12)).Group, store.Message(store.MessageAt(13)).Group);

            store.SetGrouping(GroupingMode.SimilarText);
            Assert.AreEqual(store.Message(store.MessageAt(12)).Group, store.Message(store.MessageAt(13)).Group, "numbers differ only");
            Assert.AreEqual(11, store.Group(store.Message(store.MessageAt(0)).Group).Count);
            Assert.AreEqual("Loaded # assets in # ms", LogStore.Similar("Loaded 12 assets in 3.4 ms"));
            Assert.AreEqual("Asset # missing", LogStore.Similar("Asset 35de1ecde25af3ed71054f4ed6ea89f0 missing"));
            Assert.AreEqual("deadbeefcafe stays", LogStore.Similar("deadbeefcafe stays"), "letters only are words");
        }

        [Test]
        public void SameTextGroupsMessagesThatOnlyDifferByAMemoryAddress()
        {
            var store = new LogStore();
            store.Add(1000, "Allocation of 49 bytes at 000002848112DD10", string.Empty, LogVariant.Log);
            store.Add(1001, "Allocation of 49 bytes at 0000028480FCBA80", string.Empty, LogVariant.Log);
            store.Add(1002, "Allocation of 81 bytes at 0000028480FCBA80", string.Empty, LogVariant.Log);

            Assert.AreEqual(store.Message(store.MessageAt(0)).Group, store.Message(store.MessageAt(1)).Group, "only the address differs");
            Assert.AreNotEqual(store.Message(store.MessageAt(0)).Group, store.Message(store.MessageAt(2)).Group, "another size is another message");
            Assert.AreEqual("at # and #", LogStore.FoldAddresses("at 0x7ff6a2b4c010 and 000002848112DD10"));
            const string asset = "Asset 35de1ecde25af3ed71054f4ed6ea89f0 missing";
            Assert.AreSame(asset, LogStore.FoldAddresses(asset), "a GUID is not an address");
            Assert.AreEqual("Build 20261007", LogStore.FoldAddresses("Build 20261007"), "short numbers stay");
        }

        [Test]
        public void CompileErrorsAreReplacedAndResolved()
        {
            var store = new LogStore();
            int assembly = store.AssemblyId("Assembly-CSharp");
            const string text = "Assets/A.cs(3,4): error CS0103: The name 'x' does not exist in the current context";
            store.AddCompile(10, text, true, assembly, null, 0, 0);
            int version = store.MessagesVersion;
            store.ResolveCompile(assembly, errorsOnly: false);
            Assert.Greater(store.MessagesVersion, version);
            Assert.IsTrue(store.Message(store.MessageAt(0)).Resolved);
            store.AddCompile(20, text, true, assembly, null, 0, 0);
            Assert.IsFalse(store.Message(store.MessageAt(1)).Resolved, "reported again: back");
            Assert.AreNotEqual(0, (int)(store.FlagsAt(0) & LogStore.Superseded), "the earlier report is replaced");
            Assert.AreEqual("Assets/A.cs", store.Texts[store.Message(store.MessageAt(1)).File]);

            store.Add(30, "a log", string.Empty, LogVariant.Log);
            store.Clear(40);
            Assert.AreEqual(1, store.Count, "clearing keeps standing compile errors");
            Assert.AreEqual(LogVariant.CompileError, store.Message(store.MessageAt(0)).Variant);
        }

        // Was TrimKeepsTheNewestAndShifts: ids no longer shift, the first one moves up.
        [Test]
        public void TrimKeepsTheNewestAndTheirIds()
        {
            var store = new LogStore { MaxOccurrences = 10_000 };
            int emptied = 0;
            store.Emptied += () => emptied++;
            for (int i = 0; i < 10_001; i++)
            {
                int occurrence = store.Add(i, i % 2 == 0 ? "even" : "odd", string.Empty, LogVariant.Log);
                Assert.AreEqual(i, occurrence, "an id is the log's for good");
                if (i == 9_999)
                {
                    store.SetContext(occurrence, 1234);
                }
            }

            Assert.AreEqual(8_000, store.Count);
            Assert.AreEqual(2_001, store.FirstId);
            Assert.AreEqual(10_001, store.EndId);
            Assert.AreEqual(0, emptied);
            Assert.AreEqual(10_000, store.TimeAt(store.EndId - 1));
            Assert.IsFalse(store.Contains(2_000));
            Assert.IsTrue(store.TryGetContext(9_999, out int context));
            Assert.AreEqual(1234, context);
            Assert.AreEqual(4_000, store.Group(store.Message(store.MessageAt(store.FirstId)).Group).Count);
        }

        // Logs that never repeat (an id in each) used to keep every text ever seen after their occurrences were trimmed.
        [Test]
        public void TrimLetsGoOfTextsNoLogUsesAnyMore()
        {
            var store = new LogStore { MaxOccurrences = 10_000 };
            int regrouped = 0;
            store.Regrouped += () => regrouped++;
            store.Add(0, "first and only", "at Somewhere ()", LogVariant.Warning);
            for (int i = 1; i <= 50_000; i++)
            {
                store.Add(i, "Downloaded item " + i.ToString("D6") + " of the gallery", "at Gallery.Fetch () [0x" + i.ToString("x6") + "]", LogVariant.Log);
            }

            Assert.LessOrEqual(store.MessageCount, 15_000, "messages without logs are gone (50,001 without compaction)");
            Assert.LessOrEqual(store.Texts.Count, 3 * 15_000, "and so are their texts");
            Assert.Greater(regrouped, 0);
            for (int occurrence = store.FirstId; occurrence < store.EndId; occurrence++)
            {
                var message = store.Message(store.MessageAt(occurrence));
                Assert.AreEqual(1, message.Count);
                Assert.AreEqual(occurrence, message.Last);
                StringAssert.EndsWith(" of the gallery", store.Texts[message.Text]);
                Assert.AreEqual(1, store.Group(message.Group).Count, "groups recounted");
            }

            string last = "Downloaded item 050000 of the gallery";
            Assert.AreEqual(last, store.Texts[store.Message(store.MessageAt(store.EndId - 1)).Text]);
            // A message logged again after the compaction is the same message, found by its text.
            int again = store.Add(60_000, last, "at Gallery.Fetch () [0x" + 50_000.ToString("x6") + "]", LogVariant.Log);
            Assert.AreEqual(store.MessageAt(again), store.MessageAt(again - 1));
            Assert.AreEqual(2, store.Message(store.MessageAt(again)).Count);
        }

        // A store whose ids start at 0 has no room in front: it renumbers (as a clear does, for whoever remembers ids).
        [Test]
        public void PrependPutsHistoryFirst()
        {
            var store = new LogStore();
            int emptied = 0;
            store.Emptied += () => emptied++;
            int live = store.Add(5_000, "live", string.Empty, LogVariant.Log);
            store.SetContext(live, 77);
            int old = store.Intern("old", string.Empty, LogVariant.Warning);
            store.Prepend(new long[] { 100, 200, 9_999 }, new[] { old, old, old }, new byte[3], 3, new Dictionary<int, int> { { 1, 55 } });
            Assert.AreEqual(4, store.Count);
            Assert.AreEqual(1, emptied, "renumbered");
            Assert.AreEqual(0, store.FirstId);
            Assert.AreEqual(5_000, store.TimeAt(2), "history never ends after the live logs");
            Assert.IsTrue(store.TryGetContext(3, out int moved) && moved == 77);
            Assert.IsTrue(store.TryGetContext(1, out int kept) && kept == 55);
            Assert.AreEqual(3, store.Message(old).Count);
        }

        [Test]
        public void ConsoleTimestamps()
        {
            Assert.IsTrue(UnityConsole.TrySplitTimestamp("[21:50:43] Hello", out int seconds, out string text));
            Assert.AreEqual(21 * 3600 + 50 * 60 + 43, seconds);
            Assert.AreEqual("Hello", text);
            Assert.IsFalse(UnityConsole.TrySplitTimestamp("[Tag] Hello", out _, out string untouched));
            Assert.AreEqual("[Tag] Hello", untouched);
            Assert.AreEqual(LogVariant.Exception, UnityConsole.VariantOf(12714240));
            Assert.AreEqual(LogVariant.Log, UnityConsole.VariantOf(8406016));
            Assert.AreEqual(LogVariant.Warning, UnityConsole.VariantOf(512));
            Assert.AreEqual(LogVariant.CompileError, UnityConsole.VariantOf(2048 | 256));
        }

        [Test]
        public void UnityConsoleIsReachable()
        {
            // Every LogEntries member is bound (directly or by expression): a failure would turn history import off.
            Assert.IsTrue(UnityConsole.Available);
            Assert.GreaterOrEqual(UnityConsole.Total(), 0);
            using (var reader = UnityConsole.Reader.Open())
            {
                Assert.IsNotNull(reader);
                if (reader.Count > 0)
                {
                    Assert.IsNotNull(reader.Lines(0, 1, out _));
                    Assert.IsTrue(reader.TryRead(0, out _));
                }
            }
        }

        [Test]
        public void AJournalBelongsToOneProcessStart()
        {
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            long started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            Assert.IsTrue(UnityConsole.ShowAll.IsRunning(self, started));
            Assert.IsFalse(UnityConsole.ShowAll.IsRunning(self, started - TimeSpan.TicksPerMinute), "the same id given to a new process");
            Assert.IsFalse(UnityConsole.ShowAll.IsRunning(self, 0L));
        }

        [Test]
        public void BorrowedConsoleSettingsComeBackOnce()
        {
            Assume.That(UnityConsole.Available, "this Unity's console can't be read");
            Assume.That(!UnityConsole.ShowAll.Held, "an import is borrowing the console");
            bool collapse = UnityConsole.HasFlag(UnityConsole.FlagCollapse);
            bool warnings = UnityConsole.HasFlag(UnityConsole.FlagLogLevelWarning);
            bool timestamps = UnityConsole.HasFlag(UnityConsole.FlagShowTimestamp);
            try
            {
                UnityConsole.SetFlag(UnityConsole.FlagCollapse, true);
                UnityConsole.SetFlag(UnityConsole.FlagLogLevelWarning, false);
                UnityConsole.SetFlag(UnityConsole.FlagShowTimestamp, false);

                Assert.IsTrue(UnityConsole.ShowAll.Take(timestamps: true));
                Assert.IsTrue(UnityConsole.ShowAll.Held);
                Assert.IsTrue(UnityConsole.RowsAreComplete);
                Assert.IsTrue(UnityConsole.HasFlag(UnityConsole.FlagShowTimestamp));
                Assert.IsFalse(UnityConsole.ShowAll.Take(timestamps: true), "a second slice changes nothing");

                // The user turns collapse back on while borrowed: that choice stays.
                UnityConsole.SetFlag(UnityConsole.FlagCollapse, true);
                UnityConsole.ShowAll.Release();
                Assert.IsFalse(UnityConsole.ShowAll.Held);
                Assert.IsTrue(UnityConsole.HasFlag(UnityConsole.FlagCollapse));
                Assert.IsFalse(UnityConsole.HasFlag(UnityConsole.FlagLogLevelWarning));
                Assert.IsFalse(UnityConsole.HasFlag(UnityConsole.FlagShowTimestamp));
                Assert.AreEqual(string.Empty, ConsoleHistory.Anchor(0), "no anchor check while the console hides rows");
            }
            finally
            {
                UnityConsole.ShowAll.Release();
                UnityConsole.SetFlag(UnityConsole.FlagCollapse, collapse);
                UnityConsole.SetFlag(UnityConsole.FlagLogLevelWarning, warnings);
                UnityConsole.SetFlag(UnityConsole.FlagShowTimestamp, timestamps);
            }
        }
    }
}
