using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor.Tests
{
    public class ConsoleReplacementTests
    {
        private static ConsoleSpot Console(bool docked = true, bool selected = true, bool alone = false) =>
            new ConsoleSpot { Docked = docked, Selected = selected, Alone = alone };

        private static LoggerSpot Logger(bool docked = true, bool visible = false, bool sameArea = false) =>
            new LoggerSpot { Open = true, Docked = docked, Visible = visible, SameArea = sameArea };

        private static readonly LoggerSpot NoLogger = new LoggerSpot();

        [Test]
        public void ReplacingTheConsoleIsOnUntilTurnedOff()
        {
            Assert.IsTrue(ConsoleReplacement.DefaultOn);
            if (!EditorPrefs.HasKey(ConsoleReplacement.PrefKey))
            {
                Assert.IsTrue(ConsoleReplacement.Enabled);
            }
        }

        [Test]
        public void ThisUnityCanDockTheLoggerWhereTheConsoleIs()
        {
            Assert.IsTrue(ConsoleReplacement.CanDock, "DockArea.AddTab, RemoveTab, selected or m_Panes moved: the Mixer console falls back to a window of its own");
            Assert.IsTrue(ConsoleReplacement.CanWatch, "ConsoleWindow.ms_ConsoleWindow moved: a Console opened later in the session stays");
        }

        [Test]
        public void WithoutALoggerOneTakesTheConsolesPlace()
        {
            Assert.AreEqual(ConsoleSwap.AddTab, ConsoleReplacement.Decide(Console(), NoLogger));
            Assert.AreEqual(ConsoleSwap.AddTab, ConsoleReplacement.Decide(Console(selected: false), NoLogger), "a hidden Console tab becomes a hidden Mixer console tab");
            Assert.AreEqual(ConsoleSwap.AddTab, ConsoleReplacement.Decide(Console(alone: true), NoLogger), "a Console window of its own becomes a Mixer console window");
            Assert.AreEqual(ConsoleSwap.OpenFloating, ConsoleReplacement.Decide(Console(docked: false), NoLogger));
        }

        [Test]
        public void ALoggerInTheSameDockAreaIsShownWhereTheConsoleWas()
        {
            Assert.AreEqual(ConsoleSwap.ShowLogger, ConsoleReplacement.Decide(Console(), Logger(sameArea: true)));
            Assert.AreEqual(ConsoleSwap.CloseOnly, ConsoleReplacement.Decide(Console(selected: false), Logger(sameArea: true)));
            Assert.AreEqual(ConsoleSwap.CloseOnly, ConsoleReplacement.Decide(Console(selected: false), Logger(sameArea: true, visible: true)));
        }

        [Test]
        public void ALoggerElsewhereOnlyMovesWhenItWasHiddenAndTheConsoleShown()
        {
            Assert.AreEqual(ConsoleSwap.CloseOnly, ConsoleReplacement.Decide(Console(selected: false), Logger()), "a hidden Console just goes");
            Assert.AreEqual(ConsoleSwap.CloseOnly, ConsoleReplacement.Decide(Console(), Logger(visible: true)), "a Mixer console on screen stays where it is");
            Assert.AreEqual(ConsoleSwap.MoveTab, ConsoleReplacement.Decide(Console(), Logger()));
            Assert.AreEqual(ConsoleSwap.ShowLogger, ConsoleReplacement.Decide(Console(alone: true), Logger()),
                "Window > General > Console doesn't pull the Mixer console out of the user's dock");
            Assert.AreEqual(ConsoleSwap.ShowLogger, ConsoleReplacement.Decide(Console(docked: false), Logger()));
            Assert.AreEqual(ConsoleSwap.ShowLogger, ConsoleReplacement.Decide(Console(), Logger(docked: false)));
        }

        // The VRChat SDK's buttons that open the Console (by their name, or what they say), a press on their label too.
        [Test]
        public void ConsoleLinksAreTheButtonsThatOpenTheConsole()
        {
            var check = new Button { name = "open-unity-console", text = "Check Console for more details" };
            var label = new Label("inside");
            check.Add(label);
            Assert.IsTrue(ConsoleLinks.IsConsoleLink(check));
            Assert.IsTrue(ConsoleLinks.IsConsoleLink(label), "a press on what the button holds");
            Assert.IsTrue(ConsoleLinks.IsConsoleLink(new Button { name = "action-button", text = "Show Console" }));
            Assert.IsTrue(ConsoleLinks.IsConsoleLink(new Button { text = "Open the Unity Console" }));
            Assert.IsFalse(ConsoleLinks.IsConsoleLink(new Button { name = "action-button", text = "Build and Test" }));
            Assert.IsFalse(ConsoleLinks.IsConsoleLink(new Label("Check Console for more details")), "only buttons");
            Assert.IsFalse(ConsoleLinks.IsConsoleLink(null));
        }

        // What "Check Console for more details" means: the newest error, not a later warning nor a compile error that a
        // later compilation reported again or no longer reports.
        [Test]
        public void TheLatestErrorIsTheNewestErrorStillStanding()
        {
            var store = new LogStore();
            Assert.AreEqual(-1, LoggerWindow.LatestError(store));
            store.Add(1, "Building avatar", string.Empty, LogVariant.Log);
            int failed = store.Add(2, "Error building Player because scripts have compile errors in the editor", string.Empty, LogVariant.Error);
            store.Add(3, "Something minor", string.Empty, LogVariant.Warning);
            Assert.AreEqual(failed, LoggerWindow.LatestError(store));
            int exception = store.Add(4, "NullReferenceException: Object reference not set", "at A.B ()", LogVariant.Exception);
            Assert.AreEqual(exception, LoggerWindow.LatestError(store));
            int compile = store.AddCompile(5, "Assets/A.cs(1,1): error CS1002: ; expected", true, store.AssemblyId("Assembly-CSharp"), "Assets/A.cs", 1, 1);
            Assert.AreEqual(compile, LoggerWindow.LatestError(store));
            store.ResolveCompile(-1, errorsOnly: true);
            Assert.AreEqual(exception, LoggerWindow.LatestError(store), "a compile error no longer reported");
        }

        // The SDK wraps what failed in an UploadException that has only its message: the inner one says where.
        [Test]
        public void AFailedUploadSaysWhereItHappened()
        {
            System.Exception thrown;
            try
            {
                object nothing = null;
                nothing.ToString();
                thrown = null;
            }
            catch (System.Exception e)
            {
                thrown = e;
            }

            var wrapped = new System.Exception(thrown.Message, thrown);
            string text = SdkUploadErrors.Describe(wrapped);
            StringAssert.StartsWith(SdkUploadErrors.Prefix, text);
            StringAssert.Contains(nameof(AFailedUploadSaysWhereItHappened), text, "the inner exception's stack");
            Assert.IsNull(SdkUploadErrors.Describe(new System.Exception("Failed to find the built avatar bundle")), "a message alone adds nothing");
            Assert.IsNull(SdkUploadErrors.Describe(null));

            // "Check Console for more details" shows that log, even when the SDK's own message came after it.
            var store = new LogStore();
            store.Add(System.DateTime.UtcNow.Ticks, "Building avatar", string.Empty, LogVariant.Log);
            int where = store.Add(System.DateTime.UtcNow.Ticks, text, string.Empty, LogVariant.Error);
            int sdk = store.Add(System.DateTime.UtcNow.Ticks, "Upload Failed: Object reference not set to an instance of an object", string.Empty, LogVariant.Error);
            Assert.AreEqual(sdk, LoggerWindow.LatestError(store));
            Assert.AreEqual(where, LoggerWindow.ErrorToShow(store));
            int later = store.Add(System.DateTime.UtcNow.Ticks + System.TimeSpan.TicksPerHour, "Another failure an hour later", string.Empty, LogVariant.Error);
            Assert.AreEqual(later, LoggerWindow.ErrorToShow(store), "an old upload's log is not what a new error means");
        }
    }
}
