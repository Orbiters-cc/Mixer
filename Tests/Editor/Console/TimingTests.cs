using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.Logger.Editor.Tests
{
    public class TimingTests
    {
        // A "Domain Reload Profiling" tree as Unity writes it with EnableDomainReloadTimings on (trimmed).
        private const string Block =
            "Domain Reload Profiling: 6578ms\n" +
            " Thread #206572\n" +
            "\tFinalizeReload: 4807ms\n" +
            "\t\tSetupLoadedEditorAssemblies: 2824ms\n" +
            "\t\t\tProcessInitializeOnLoadMethodAttributes: 1344ms\n" +
            "\t\t\t\tInitializeOnLoad VFInitHook.Init: 816ms\n" +
            "\t\t\t\t\tProcessInitializeOnLoadMethodAttribute(VF.Hooks.VFInitHook::Init): 816ms\n" +
            "\t\t\t\t\t\tGC.Alloc(40): 3ms in 53050 occurrences\n" +
            "\t\t\tProcessInitializeOnLoadAttributes: 1139ms\n" +
            "\t\t\t\tInitializeOnLoad LogCapture: 161ms\n" +
            "\t\tAwakeInstancesAfterBackupRestoration: 1347ms\n" +
            "\t\t\tAwakeScriptedObjects: 1347ms\n" +
            "\t\t\t\tAwakeInstanceAfterBackupRestoration(MCBEditor, mcb.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null): 452ms in 7 occurrences\n" +
            "\t\tAssemblyReloadEvents.afterAssemblyReload: UnityEditor.InspectorWindow.OnAfterAssemblyReload: 1ms in 8 occurrences\n" +
            "\tBeginReloadAssembly: 1545ms\n" +
            "\t\tCreateAndSetChildDomain: 777ms\n" +
            "\t\tAssemblyReloadEvents.beforeAssemblyReload: callback in VF.Utils.HarmonyUtils: 235ms\n" +
            "\t\tAssemblyReloadEvents.beforeAssemblyReload: Orbiters.Logger.Editor.LogCapture.BeforeReload: 32ms\n" +
            "\tRebuildCommonClasses: 29ms";

        [Test]
        public void ProfilesSurviveTheirText()
        {
            var profile = new TimingProfile { Kind = TimingKind.AvatarUpload, TotalMilliseconds = 98765.4, Subject = "Rexouium v2", Outcome = "upload failed" };
            profile.Parts.Add(new TimingPart("VRCFury", "Avatar build steps", "VrcPreuploadHook  |  odd", 12345.6));
            profile.Parts.Add(new TimingPart("MCB", "Avatar build steps", "NativeMeshAvatarBuildHook", 2345.1));
            string text = profile.Format();
            Assert.IsTrue(TimingProfile.IsTimings(text));
            var read = TimingProfile.Parse(text);
            Assert.AreEqual(TimingKind.AvatarUpload, read.Kind);
            Assert.AreEqual(98765.4, read.TotalMilliseconds, 0.01);
            Assert.AreEqual("Rexouium v2", read.Subject);
            Assert.AreEqual("upload failed", read.Outcome);
            Assert.AreEqual(2, read.Parts.Count);
            Assert.AreEqual("VRCFury", read.Parts[0].Source);
            Assert.AreEqual("VrcPreuploadHook / odd", read.Parts[0].Label, "the separator can't appear in a field");
            Assert.AreEqual(12345.6, read.Parts[0].Milliseconds, 0.01);
            Assert.AreEqual("Built and uploaded Rexouium v2 in 1 min 39 s (upload failed)", profile.Message());
        }

        [Test]
        public void TheEngineGetsWhatNoPackageClaimed()
        {
            var profile = new TimingProfile { Kind = TimingKind.ScriptReload, TotalMilliseconds = 1000 };
            profile.Parts.Add(new TimingPart("VRCFury", "Startup methods", "A", 300));
            profile.Parts.Add(new TimingPart("MCB", "Startup code", "B", 100));
            profile.Parts.Add(new TimingPart("VRCFury", "Before the reload", "C", 100));
            var shares = profile.Shares;
            Assert.AreEqual(new[] { "Unity", "VRCFury", "MCB" }, shares.Select(s => s.Source).ToArray());
            Assert.AreEqual(500, shares[0].Milliseconds, 0.01);
            Assert.AreEqual(400, shares[1].Milliseconds, 0.01);
            Assert.AreEqual("VRCFury 400 ms  ·  MCB 100 ms", profile.Summary(3));
        }

        [Test]
        public void ReloadTreesAreSplitByPackage()
        {
            var profile = ReloadTimings.Parse(Block, AssemblySources.Take());
            Assert.IsNotNull(profile);
            Assert.AreEqual(6578, profile.TotalMilliseconds, 0.01);
            var vrcfury = profile.Shares.First(s => s.Source == "VRCFury");
            Assert.AreEqual(816 + 235, vrcfury.Milliseconds, 0.01, "the startup method and the before-reload callback");
            Assert.IsTrue(vrcfury.Parts.Any(p => p.Phase == "Startup methods" && p.Label == "VFInitHook.Init"));
            // The console's own startup and before-reload callback: it is part of Mixer's package.
            var mixer = profile.Shares.First(s => s.Source == "Mixer");
            Assert.AreEqual(161 + 32, mixer.Milliseconds, 0.01);
            // Unity's own steps, without what packages took inside them.
            var unity = profile.Shares.First(s => s.Source == "Unity");
            Assert.IsTrue(unity.Parts.Any(p => p.Label == "Creating the script domain" && Math.Abs(p.Milliseconds - 777) < 0.01));
            double sum = profile.Shares.Sum(s => s.Milliseconds);
            Assert.AreEqual(profile.TotalMilliseconds, sum, 0.5, "every millisecond is someone's");
        }

        [Test]
        public void AReloadTreeIsReadOnceItIsComplete()
        {
            string path = Path.Combine(Path.GetTempPath(), "logger-reload-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                File.WriteAllText(path, "Earlier line\n" + Block + "\n");
                Assert.IsFalse(ReloadTimings.TryFindBlock(path, 0, new FileInfo(path).Length, out _, out _), "the tree may still be written");
                File.AppendAllText(path, "Asset Pipeline Refresh: Total: 4.9 seconds\n");
                Assert.IsTrue(ReloadTimings.TryFindBlock(path, 0, new FileInfo(path).Length, out string block, out long end));
                StringAssert.StartsWith("Domain Reload Profiling: 6578ms", block);
                StringAssert.EndsWith("RebuildCommonClasses: 29ms", block.TrimEnd());
                Assert.IsFalse(ReloadTimings.TryFindBlock(path, end, new FileInfo(path).Length, out _, out _), "read once");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void BuildStepsMergeAcrossAvatars()
        {
            BuildSteps.Take(0);
            long start = DateTime.UtcNow.Ticks;
            for (int avatar = 0; avatar < 2; avatar++)
            {
                BuildSteps.Begin("Avatar " + avatar);
                BuildSteps.Step("VRCFury", "VrcPreuploadHook", 100);
                BuildSteps.Step("MCB", "NativeMeshAvatarBuildHook", 10);
                BuildSteps.End(true);
            }

            var runs = BuildSteps.Take(start);
            Assert.AreEqual(2, runs.Count);
            var profile = new TimingProfile { Kind = TimingKind.PlayMode, TotalMilliseconds = 500 };
            Assert.AreEqual(220, BuildSteps.AddTo(profile, runs), 0.01);
            Assert.AreEqual(2, profile.Parts.Count);
            Assert.AreEqual("VrcPreuploadHook (2 avatars)", profile.Parts[0].Label);
            Assert.AreEqual(200, profile.Parts[0].Milliseconds, 0.01);
            Assert.AreEqual(0, BuildSteps.Take(start).Count, "taken once");
        }

        [Test]
        public void ImporterMessagesBelongToTheirAsset()
        {
            Assert.AreEqual("Packages/orbiters.toolkit/Editor/UI/orbit-sphere.uss",
                SourceCatalog.LeadingAssetPath("Packages/orbiters.toolkit/Editor/UI/orbit-sphere.uss (line 27): warning: Unknown property 'outline-width'"));
            Assert.AreEqual("Assets/Avatars/Body.fbx", SourceCatalog.LeadingAssetPath("Assets/Avatars/Body.fbx: Import Error Code:(4)"));
            Assert.IsNull(SourceCatalog.LeadingAssetPath("Assets are reloading"));
            Assert.IsNull(SourceCatalog.LeadingAssetPath("[MCB] Packages/orbiters.mcb was updated"));

            var store = new LogStore();
            int id = store.Intern("Packages/orbiters.toolkit/Editor/UI/orbit-sphere.uss (line 27): warning: Unknown property 'outline-width'",
                "Thry.ThryEditor.ThirdPartyIncluder:Include () (at Assets/_PoiyomiShaders/Scripts/ThirdPartyIncluder.cs:40)\n", LogVariant.Warning);
            Assert.AreEqual("Orbiters Toolkit", store.SourceOf(id).Name);
        }

        [Test]
        public void TimingLogsHaveTheirOwnSourceAndSummary()
        {
            var profile = new TimingProfile { Kind = TimingKind.ScriptReload, TotalMilliseconds = 2000 };
            profile.Parts.Add(new TimingPart("VRCFury", "Startup methods", "VFInitHook.Init", 800));
            var store = new LogStore();
            int id = store.Intern(profile.Message(), profile.Format(), LogVariant.Log);
            Assert.AreEqual(LogStore.TimingSource, store.SourceOf(id).Name);
            Assert.AreEqual("VRCFury 800 ms", store.Texts[store.Message(id).Callsite]);
        }

        [Test]
        public void BundleBuildLogReadsUnitysOwnTimings()
        {
            var log = BundleBuildLog.Parse(new[]
            {
                "Asset Pipeline Refresh (id=0526e2dbcb988684a8bf4cc7554a2467): Total: 10.235 seconds - Initiated by StopAssetImportingV2(NoUpdateAssetOptions)",
                "\tSummary:",
                "\t\tImports: total=1439 (actual=5, local cache=1434, cache server=0)",
                "\t\tAsset DB Callback time: managed=9190 ms, native=30 ms",
                "\tPostProcessAllAssets: 8708.480ms",
                "Asset Pipeline Refresh (id=001002d15efba1d47bd8b8837f49dc10): Total: 0.110 seconds - Initiated by RefreshV2(NoUpdateAssetOptions)",
                "ExitCode: 0 Duration: 1s451ms",
                "ExitCode: 0 Duration: 1m5s127ms",
                "Compiling shader \"Standard\" pass \"FORWARD\" (vp)",
                "Compiling shader \"Standard\" pass \"FORWARD\" (fp)",
                "Total compressed size 106.0 MB. Total uncompressed size 266.1 MB.",
                "Total compressed size 1.1 KB. Total uncompressed size 4.2 KB.",
            });
            Assert.AreEqual(2, log.Refreshes);
            Assert.AreEqual(10345d, log.RefreshMilliseconds, 0.01d);
            Assert.AreEqual(9190d, log.CallbackMilliseconds, 0.01d);
            Assert.AreEqual(1439, log.Imports);
            Assert.AreEqual(5, log.ActualImports);
            Assert.AreEqual(1434, log.CachedImports);
            Assert.AreEqual(1451d + 65127d, log.ScriptMilliseconds, 0.01d);
            Assert.AreEqual(2, log.ShaderPasses);
            Assert.AreEqual("106.0 MB", log.CompressedSize);
            Assert.AreEqual("266.1 MB", log.UncompressedSize);
            Assert.IsTrue(log.Found);
            Assert.IsFalse(BundleBuildLog.Parse(new[] { "Unrelated line" }).Found);
        }
    }
}
