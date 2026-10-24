using System;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    public class LogExplanationTests
    {
        private static string IdOf(string condition, string stack = "", LogLevel level = LogLevel.Error)
        {
            int index = LogExplanations.Match(condition, stack, level);
            return index >= 0 ? LogExplanations.Get(index).Id : null;
        }

        [Test]
        public void CommonUnityMessages()
        {
            Assert.AreEqual("csharp.null-reference", IdOf("NullReferenceException: Object reference not set to an instance of an object"));
            Assert.AreEqual("unity.imgui-layout", IdOf("ArgumentException: Getting control 1's position in a group with only 1 controls when doing repaint"));
            Assert.AreEqual("unity.tls-allocator", IdOf("TLS Allocator ALLOC_TEMP_TLS, underlying allocator ALLOC_TEMP_MAIN has unfreed allocations, size 37", level: LogLevel.Warning));
            Assert.AreEqual("net.insecure", IdOf("Non-secure network connections disabled in Player Settings", level: LogLevel.Warning));
            Assert.AreEqual("asset.material-property", IdOf("Material 'Body' with Shader 'Standard' doesn't have a texture property '_EmissionMap'"));
            Assert.IsNull(IdOf("Everything is fine, nothing to see"));
        }

        [Test]
        public void SpecificBeforeGeneral()
        {
            const string destroyed = "MissingReferenceException: The object of type 'VRCAvatarDescriptor' has been destroyed but you are still trying to access it.";
            Assert.AreEqual("vrchat.sdk-panel-destroyed-avatar", IdOf(destroyed, "VRC.SDK3A.Editor.VRCSdkControlPanelAvatarBuilder.get_SelectedAvatar ()"));
            Assert.AreEqual("unity.destroyed", IdOf(destroyed, "Some.Other:Code ()"));
            Assert.AreEqual("compiler.cs0246", IdOf("Library/PackageCache/com.foo@1.0.0/Editor/A.cs(3,7): error CS0246: The type or namespace name 'VRC' could not be found"));
            Assert.AreEqual("compiler.package", IdOf("Library/PackageCache/com.foo@1.0.0/Editor/A.cs(3,7): error CS1002: ; expected"));
            Assert.AreEqual("compiler.error", IdOf("Assets/A.cs(3,7): error CS1002: ; expected"));
        }

        [Test]
        public void PlaceholdersComeFromTheMessage()
        {
            const string missing = "The referenced script (Unknown) on this Behaviour (Game Object 'Body') is missing!";
            int index = LogExplanations.Match(missing, string.Empty, LogLevel.Warning);
            var explained = LogExplanations.Explain(index, missing);
            Assert.AreEqual("Missing script on 'Body'", explained.Title);

            const string compile = "Assets/A.cs(1,1): error CS0246: The type or namespace name 'VRCFury' could not be found (are you missing a using directive?)";
            explained = LogExplanations.Explain(LogExplanations.Match(compile, string.Empty, LogLevel.Error), compile);
            Assert.AreEqual("Unknown type or namespace 'VRCFury'", explained.Title);
            StringAssert.Contains("'VRCFury'", explained.Fixes[0]);
        }

        [Test]
        public void RegisteredExplanationsComeFirst()
        {
            LogExplanations.Register(new LogExplanation("tests.custom-null", "NullReferenceException", "Custom")
            {
                StackNeedle = "MyTool.",
                Summary = "From a test."
            });
            try
            {
                Assert.AreEqual("tests.custom-null", IdOf("NullReferenceException: x", "MyTool.Run ()"));
                Assert.AreEqual("csharp.null-reference", IdOf("NullReferenceException: x", "Other.Run ()"));
            }
            finally
            {
                // Replace it with an entry that can never match, so other tests see the built-ins only.
                LogExplanations.Register(new LogExplanation("tests.custom-null", "\u0001never\u0001", "Unused"));
            }
        }

        [Test]
        public void LevelsRestrictMatches()
        {
            Assert.AreEqual("vrchat.vrcfury-error", IdOf("VRCFury failed to build the avatar", level: LogLevel.Error));
            Assert.AreNotEqual("vrchat.vrcfury-error", IdOf("VRCFury failed to build the avatar", level: LogLevel.Info));
            Assert.IsNull(IdOf("Something unknown is building", level: LogLevel.Info));
        }

        [Test]
        public void TheSdkOwnershipErrorIsExplained()
        {
            Assert.AreEqual("vrchat.avatar-not-yours",
                IdOf("Failed to load avatar data: Avatar's current ID belongs to a different user, assign a different ID"));
        }

        [Test]
        public void ServerEntriesReplaceTheirLocalCopyAndPriorityOrdersThem()
        {
            var entries = KnownLogs.Parse(@"{ ""explanations"": [
                { ""id"": ""tests.broad"", ""needle"": ""ZZ-probe"", ""priority"": -10, ""severity"": ""info"", ""source"": ""Tests"", ""title"": ""Broad"" },
                { ""id"": ""tests.exact"", ""needle"": ""ZZ-probe exact"", ""severity"": ""problem"", ""source"": ""Tests"", ""title"": ""Exact {what}"",
                  ""pattern"": ""ZZ-probe exact (?<what>\\w+)"" },
                { ""id"": ""csharp.null-reference"", ""needle"": ""NullReferenceException"", ""severity"": ""warning"", ""source"": ""Tests"", ""title"": ""From the server"" }
            ] }");
            Assert.AreEqual(3, entries.Count);
            try
            {
                LogExplanations.SetRemote(entries);
                Assert.AreEqual("tests.exact", IdOf("ZZ-probe exact thing"));
                Assert.AreEqual("tests.broad", IdOf("ZZ-probe other"));
                Assert.AreEqual("Exact thing", LogExplanations.Explain(LogExplanations.Match("ZZ-probe exact thing", "", LogLevel.Error), "ZZ-probe exact thing").Title);
                Assert.AreEqual("From the server", LogExplanations.Get(LogExplanations.Match("NullReferenceException: x", "", LogLevel.Error)).Title);
            }
            finally
            {
                LogExplanations.SetRemote(null);
            }

            Assert.AreEqual("csharp.null-reference", IdOf("NullReferenceException: x"));
            Assert.AreNotEqual("From the server", LogExplanations.Get(LogExplanations.Match("NullReferenceException: x", "", LogLevel.Error)).Title);
        }

        [Test]
        public void AServerCopyOlderThanTheLoggersOwnEntryDoesNotReplaceIt()
        {
            const string condition = "TLS Allocator ALLOC_TEMP_TLS, underlying allocator ALLOC_TEMP_MAIN has unfreed allocations, size 37";
            string own = LogExplanations.Get(LogExplanations.Match(condition, "", LogLevel.Warning)).Title;
            string Server(string updated) => @"{ ""explanations"": [
                { ""id"": ""unity.tls-allocator"", ""needle"": ""has unfreed allocations"", ""severity"": ""info"", ""source"": ""Tests"", ""title"": ""From the server""" +
                (updated == null ? string.Empty : @", ""updated"": """ + updated + @"""") + " } ] }";
            try
            {
                LogExplanations.SetRemote(KnownLogs.Parse(Server(null)));
                Assert.AreEqual(own, LogExplanations.Get(LogExplanations.Match(condition, "", LogLevel.Warning)).Title,
                    "A server copy that does not say when it changed is older than the Mixer console's dated entry.");
                LogExplanations.SetRemote(KnownLogs.Parse(Server("2026-01-01")));
                Assert.AreEqual(own, LogExplanations.Get(LogExplanations.Match(condition, "", LogLevel.Warning)).Title);
                LogExplanations.SetRemote(KnownLogs.Parse(Server("2099-01-01")));
                Assert.AreEqual("From the server", LogExplanations.Get(LogExplanations.Match(condition, "", LogLevel.Warning)).Title,
                    "A newer server copy (or a translation of the same text) replaces it.");
            }
            finally
            {
                LogExplanations.SetRemote(null);
            }
        }

        [Test]
        public void NoExplanationNamesTheFormerXRayGizmosPackage()
        {
            var own = KnownLogs.Load().ToList();
            Assert.Greater(own.Count, 300, "The Mixer console's own list was read.");
            foreach (var entry in own)
            {
                foreach (string text in new[] { entry.Title, entry.Summary }.Concat(entry.Fixes))
                {
                    StringAssert.DoesNotContain("XRay Gizmos", text, entry.Id + ": the avatar gizmos are part of Orbiters Toolkit now.");
                }
            }
        }

        [Test]
        public void AnInvalidPatternNeverMatches()
        {
            var entries = KnownLogs.Parse(@"{ ""explanations"": [
                { ""id"": ""tests.invalid"", ""needle"": ""ZZ-invalid"", ""pattern"": ""(unclosed"", ""severity"": ""info"", ""source"": ""Tests"", ""title"": ""Invalid"" } ] }");
            try
            {
                LogExplanations.SetRemote(entries);
                Assert.IsNull(IdOf("ZZ-invalid here"));
            }
            finally
            {
                LogExplanations.SetRemote(null);
            }
        }

        [Test]
        public void PlaceholdersAreFilledInOnePass()
        {
            var entries = KnownLogs.Parse(@"{ ""explanations"": [
                { ""id"": ""tests.fill"", ""needle"": ""ZZ-fill"", ""pattern"": ""ZZ-fill (?<a>\\S+) (?<b>\\S+)"", ""severity"": ""info"", ""source"": ""Tests"",
                  ""title"": ""{a} and {b}, not {c}"" } ] }");
            try
            {
                LogExplanations.SetRemote(entries);
                int index = LogExplanations.Match("ZZ-fill {b} x", "", LogLevel.Error);
                Assert.AreEqual("{b} and x, not {c}", LogExplanations.Explain(index, "ZZ-fill {b} x").Title, "a value holding a placeholder stays as it is");
                Assert.AreEqual("\u2026 and \u2026, not {c}", LogExplanations.Explain(index, "ZZ-other").Title, "a text the pattern doesn't match");
            }
            finally
            {
                LogExplanations.SetRemote(null);
            }
        }

        [Test]
        public void APatternThatKeepsTimingOutIsGivenUp()
        {
            var entries = KnownLogs.Parse(@"{ ""explanations"": [
                { ""id"": ""tests.slow"", ""needle"": ""ZZ-slow"", ""pattern"": ""ZZ-slow (a+)+$"", ""severity"": ""info"", ""source"": ""Tests"", ""title"": ""Slow"" } ] }");
            var slow = entries[0];
            string message = "ZZ-slow " + new string('a', 30) + "!";
            var timeout = LogExplanation.MatchTimeout;
            try
            {
                // The pattern is compiled at its first use, with the short timeout.
                LogExplanation.MatchTimeout = TimeSpan.FromMilliseconds(1);
                LogExplanations.SetRemote(entries);
                for (int i = 0; i < 3; i++)
                {
                    Assert.IsNull(IdOf(message));
                }

                Assert.IsTrue(slow.Broken, "After three timeouts the pattern is no longer tried.");
                Assert.IsNull(IdOf(message));
            }
            finally
            {
                LogExplanation.MatchTimeout = timeout;
                LogExplanations.SetRemote(null);
            }
        }

        [Test]
        public void OnlyWebPagesOpen()
        {
            string Link(string link) => new LogExplanation("tests.link", "ZZ-link", "Link") { Link = link }.WebLink;
            Assert.AreEqual("https://orbiters.cc/docs", Link("https://orbiters.cc/docs"));
            Assert.AreEqual("http://localhost:4100/docs", Link("http://localhost:4100/docs"));
            Assert.IsNull(Link("http://example.com/"), "plain http only for a server on this computer");
            Assert.IsNull(Link("file:///C:/Windows/System32/calc.exe"));
            Assert.IsNull(Link(@"C:\Windows\System32\calc.exe"));
            Assert.IsNull(Link("steam://run/438100"));
            Assert.IsNull(Link("javascript:alert(1)"));
            Assert.IsNull(Link(null));
        }

        [Test]
        public void NamesWithParenthesesAndApostrophesFillThePlaceholders()
        {
            var ids = new[]
            {
                "toolkit.refit-kept-fits", "toolkit.refit-body-shrink", "mcb.file-kept-changed", "mixer.commit-new-file", "mixer.autosave-unreadable",
                "unity.test-ignore-failing-messages"
            };
            // The Mixer console's own texts as the server's too: an older server copy doesn't decide.
            var own = KnownLogs.Load().Where(e => ids.Contains(e.Id)).ToList();
            Assert.AreEqual(ids.Length, own.Count);
            ExplainedLog Explained(string message, LogLevel level) => LogExplanations.Explain(LogExplanations.Match(message, "", level), message);
            try
            {
                LogExplanations.SetRemote(own);
                var kept = Explained("[Orbiters] ReFit: Hoodie (1) offers 3 fits in the menu (Fits/Hoodie (1)).", LogLevel.Info);
                Assert.AreEqual("ReFit: Hoodie (1) gets 3 fits in the menu", kept.Title);
                StringAssert.Contains("a menu at Fits/Hoodie (1) switches", kept.Summary);
                Assert.AreEqual("ReFit: Body shrinks under Hoodie (1) in the upload",
                    Explained("[Orbiters] ReFit: Body shrinks under Hoodie (1) (120 vertices, off: the clothing starts hidden and nothing shows it).", LogLevel.Info).Title);
                Assert.AreEqual("MCB kept your copy of Assets/Bob's Avatar/Body.fbx",
                    Explained("[FileManager] Kept 'Assets/Bob's Avatar/Body.fbx': this version ships a different copy, but the project's copy is not one MCB installed, so it may hold your changes.",
                        LogLevel.Warning).Title);
                Assert.AreEqual("Mixer saved Body (1)'s commit in a new file",
                    Explained("[Mixer] 'Assets/Bob's/Body.asset' could not be updated in place: the commit of Body (1) goes to a file of its own.", LogLevel.Warning).Title);
                Assert.AreEqual("mixer.autosave-unreadable", IdOf("[Mixer] The autosave of 'Body' could not be read: Unexpected end of file", level: LogLevel.Warning));
                Assert.AreNotEqual("mixer.autosave-unreadable",
                    IdOf("[Mixer] The autosave of 'Body' could not be written (Disk full): its edits are only in memory until the next autosave works.", level: LogLevel.Warning));
                Assert.AreEqual("A test set LogAssert.ignoreFailingMessages to true", Explained("\nIgnoreFailingMessages:true", LogLevel.Info).Title);
            }
            finally
            {
                LogExplanations.SetRemote(null);
            }
        }

        [Test]
        public void AvatarUploadRefusals()
        {
            const string thumbnailStack = "VRC.SDK3A.Editor.VRCSdkControlPanelAvatarBuilder:HandleUploadError (System.Exception)\n" +
                                          "VRC.SDKBase.Editor.Api.VRCApi/<UpdateAvatarImage>d__33:MoveNext ()";
            Assert.AreEqual("vrchat.thumbnail-already-uploaded", IdOf("This file was already uploaded", thumbnailStack));
            Assert.AreEqual("vrchat.file-already-uploaded", IdOf("This file was already uploaded, you should make a new build", "VRC.SDKBase.Editor.Api.VRCApi/<UpdateAvatarBundle>d__34:MoveNext ()"));
            Assert.AreEqual("vrchat.upload-failed", IdOf("Failed to upload avatar!", level: LogLevel.Info));
            Assert.AreEqual("vrchat.build-aborted-by-thumbnail-check", IdOf("VRCSDK build was aborted at the request of the 'VrcSdkThumbnailCheck' VRCSDKBuildRequestedCallback"));
            Assert.AreEqual("myavatar.thumbnail-duplicate-stopped", IdOf("[My Avatar] Build & Publish stopped before the build: the thumbnail waiting in the VRChat SDK (a.png) is already the thumbnail of A (avtr_x) on VRChat", level: LogLevel.Warning));
        }
    }
}
