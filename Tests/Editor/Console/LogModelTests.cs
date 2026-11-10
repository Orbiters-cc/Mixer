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
            Assert.AreEqual("Distinct log 39999 " + padding, store.Texts[store.Message(store.MessageAt(store.Count - 1)).Text]);
            Assert.AreEqual(store.Count, CountAll(store), "counters still add up after the trims");
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
            Assert.AreEqual("From Unity's console", store.Texts[store.Message(store.MessageAt(0)).Text]);
            long texts = store.Texts.Generation;
            store.Trim(500);
            Assert.AreNotEqual(texts, store.Texts.Generation, "unpinned, the dead messages go");
            Assert.AreEqual("Distinct 5999", store.Texts[store.Message(store.MessageAt(store.Count - 1)).Text]);
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
            var recent = new LogCapture.Recent();
            int chars = 0;
            string first = LogCapture.Recent.Share(ref recent.Stack, "at Thing.Do ()" + 1, ref chars);
            Assert.AreEqual(15, chars);
            string again = LogCapture.Recent.Share(ref recent.Stack, "at Thing.Do ()" + 1, ref chars);
            Assert.AreSame(first, again, "an equal text keeps the earlier instance");
            Assert.AreEqual(15, chars, "and adds nothing to the queue");
            string other = LogCapture.Recent.Share(ref recent.Stack, "at Thing.Do ()" + 2, ref chars);
            Assert.AreNotSame(first, other);
            Assert.AreEqual(30, chars);
            Assert.IsNull(LogCapture.Recent.Share(ref recent.Condition, null, ref chars));

            // The main thread finds a shared instance again by reference, with the same id.
            var texts = new TextTable();
            int id = texts.Intern(first);
            Assert.AreEqual(id, texts.Intern(again));
            Assert.AreEqual(id, texts.Intern(string.Copy(first)));
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

        [Test]
        public void TrimKeepsTheNewestAndShifts()
        {
            var store = new LogStore { MaxOccurrences = 10_000 };
            int shifted = 0;
            store.Shifted += offset => shifted += offset;
            for (int i = 0; i < 10_001; i++)
            {
                int occurrence = store.Add(i, i % 2 == 0 ? "even" : "odd", string.Empty, LogVariant.Log);
                if (i == 9_999)
                {
                    store.SetContext(occurrence, 1234);
                }
            }

            Assert.AreEqual(8_000, store.Count);
            Assert.AreEqual(-2_001, shifted);
            Assert.AreEqual(10_000, store.TimeAt(store.Count - 1));
            Assert.IsTrue(store.TryGetContext(9_999 - 2_001, out int context));
            Assert.AreEqual(1234, context);
            Assert.AreEqual(4_000, store.Group(store.Message(store.MessageAt(0)).Group).Count);
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
            for (int occurrence = 0; occurrence < store.Count; occurrence++)
            {
                var message = store.Message(store.MessageAt(occurrence));
                Assert.AreEqual(1, message.Count);
                Assert.AreEqual(occurrence, message.Last);
                StringAssert.EndsWith(" of the gallery", store.Texts[message.Text]);
                Assert.AreEqual(1, store.Group(message.Group).Count, "groups recounted");
            }

            string last = "Downloaded item 050000 of the gallery";
            Assert.AreEqual(last, store.Texts[store.Message(store.MessageAt(store.Count - 1)).Text]);
            // A message logged again after the compaction is the same message, found by its text.
            int again = store.Add(60_000, last, "at Gallery.Fetch () [0x" + 50_000.ToString("x6") + "]", LogVariant.Log);
            Assert.AreEqual(store.MessageAt(again), store.MessageAt(again - 1));
            Assert.AreEqual(2, store.Message(store.MessageAt(again)).Count);
        }

        [Test]
        public void PrependPutsHistoryFirst()
        {
            var store = new LogStore();
            int shifted = 0;
            store.Shifted += offset => shifted = offset;
            int live = store.Add(5_000, "live", string.Empty, LogVariant.Log);
            store.SetContext(live, 77);
            int old = store.Intern("old", string.Empty, LogVariant.Warning);
            store.Prepend(new long[] { 100, 200, 9_999 }, new[] { old, old, old }, new byte[3], 3, new Dictionary<int, int> { { 1, 55 } });
            Assert.AreEqual(4, store.Count);
            Assert.AreEqual(3, shifted);
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
