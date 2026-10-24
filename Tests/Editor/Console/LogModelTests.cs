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
        public void SnapshotRoundTrip()
        {
            var store = new LogStore();
            store.Add(100, "<color=red>Red</color> thing", LogStack, LogVariant.Log);
            store.Add(200, "Boom", ExceptionStack, LogVariant.Exception);
            store.AddCompile(300, "Assets/B.cs(1,1): warning CS0618: obsolete", false, store.AssemblyId("Asm"), null, 0, 0);
            store.SetContext(1, 42);
            store.AddEvent(150, SessionEventKind.EnterPlayMode);
            store.SetGrouping(GroupingMode.SimilarText);

            var info = new SnapshotInfo { UnityRows = 12, UnityAnchor = "anchor", HistoryFrom = 0, HistoryTo = 4, HistoryAnchor = "h" };
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                LogSnapshot.Write(writer, store, info, 1234, 5678);
            }

            stream.Position = 0;
            LogStore loaded;
            SnapshotInfo loadedInfo;
            using (var reader = new BinaryReader(stream))
            {
                Assert.IsNull(LogSnapshot.Read(reader, 999, 5678, out _), "another editor process");
                stream.Position = 0;
                loaded = LogSnapshot.Read(reader, 1234, 5678, out loadedInfo);
            }

            Assert.IsNotNull(loaded);
            Assert.AreEqual(3, loaded.Count);
            Assert.AreEqual(GroupingMode.SimilarText, loaded.Grouping);
            Assert.AreEqual("Boom", loaded.Texts[loaded.Message(loaded.MessageAt(1)).Text]);
            Assert.AreEqual(store.SourceOf(store.MessageAt(1)).Name, loaded.SourceOf(loaded.MessageAt(1)).Name);
            Assert.AreEqual(200, loaded.TimeAt(1));
            Assert.IsTrue(loaded.TryGetContext(1, out int context) && context == 42);
            Assert.AreEqual(1, loaded.Events.Count);
            Assert.AreEqual(12, loadedInfo.UnityRows);
            Assert.AreEqual("anchor", loadedInfo.UnityAnchor);
            Assert.AreEqual(4, loadedInfo.HistoryTo);
            Assert.IsTrue(loaded.HasActiveCompileMessage("Assets/B.cs(1,1): warning CS0618: obsolete", out _));
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
    }
}
