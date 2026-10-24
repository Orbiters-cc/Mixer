using NUnit.Framework;
using UnityEditor;

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
    }
}
