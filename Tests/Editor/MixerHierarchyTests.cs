using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor.Tests
{
    /// <summary>The hierarchy model over the open scenes, and the window driven without showing it.</summary>
    public sealed class MixerHierarchyTests
    {
        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("MixerHierarchyFixture");
            for (int i = 0; i < 3; i++)
            {
                var child = new GameObject("Child " + i);
                child.transform.SetParent(root.transform, false);
                if (i == 1) child.AddComponent<Light>();
                var grandChild = GameObject.CreatePrimitive(PrimitiveType.Cube);
                grandChild.name = "Cube " + i;
                grandChild.transform.SetParent(child.transform, false);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
        }

        [Test]
        public void TheModelFollowsExpansionAndFilters()
        {
            var model = new HierarchyModel();
            var filter = new HierarchyFilter();
            model.Rebuild(filter);
            int rootIndex = model.IndexOf(root.GetInstanceID());
            Assert.That(rootIndex, Is.GreaterThanOrEqualTo(0), "the fixture root is a row");
            Assert.That(model.Rows.Count(r => r.IsScene), Is.EqualTo(SceneManager.sceneCount), "one header per scene");
            model.SetExpanded(root.GetInstanceID(), true);
            model.Rebuild(filter);
            Assert.That(model.IndexOf(root.transform.GetChild(0).gameObject.GetInstanceID()), Is.GreaterThan(model.IndexOf(root.GetInstanceID())), "children follow their parent once open");
            Assert.That(model.IndexOf(root.transform.GetChild(0).GetChild(0).gameObject.GetInstanceID()), Is.LessThan(0), "grandchildren stay folded");
            model.SetExpanded(root.GetInstanceID(), false);
            model.Rebuild(filter);
            Assert.That(model.IndexOf(root.transform.GetChild(0).gameObject.GetInstanceID()), Is.LessThan(0));

            filter.Text = "Cube";
            model.Rebuild(filter);
            var names = model.Rows.Where(r => r.Object != null).Select(r => r.Name).ToList();
            Assert.That(names, Does.Contain("Cube 0").And.Contain("Cube 2"));
            Assert.That(names, Does.Contain("MixerHierarchyFixture"), "ancestors of matches stay, dimmed");
            Assert.That(model.Rows.First(r => r.Name == "MixerHierarchyFixture").Match, Is.False);
            Assert.That(model.Rows.First(r => r.Name == "Cube 1").Match, Is.True);
            filter.Text = "t:Light";
            model.Rebuild(filter);
            var mine = model.Rows.Where(r => r.Object != null && r.Match && r.Object.transform.IsChildOf(root.transform)).Select(r => r.Name);
            Assert.That(mine, Is.EquivalentTo(new[] { "Child 1" }), "the fixture's only light matches t:Light");
            filter.Text = ""; filter.Lights = true;
            model.Rebuild(filter);
            mine = model.Rows.Where(r => r.Object != null && r.Match && r.Object.transform.IsChildOf(root.transform)).Select(r => r.Name);
            Assert.That(mine, Is.EquivalentTo(new[] { "Child 1" }), "the Lights chip keeps the fixture's light");
        }

        [Test]
        public void TheWindowBuildsRowsAndSelectsOnClick()
        {
            var window = ScriptableObject.CreateInstance<MixerHierarchyWindow>();
            try
            {
                window.position = new Rect(-32000, -32000, 420, 600);
                var show = typeof(EditorWindow).GetMethod("ShowPopupWithMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var showMode = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");
                show.Invoke(window, new object[] { System.Enum.Parse(showMode, "PopupMenu"), false });
                var repaint = typeof(EditorWindow).GetMethod("RepaintImmediately", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                repaint.Invoke(window, null);
                window.RevealObject(root);
                repaint.Invoke(window, null);
                var rows = window.rootVisualElement.Query(className: "mxh-row").ToList().Where(r => r.resolvedStyle.display != DisplayStyle.None).ToList();
                Assert.That(rows.Count, Is.GreaterThan(1), "rows are on screen");
                var label = rows.Select(r => r.Q<Label>(className: "mxh-row__name")).FirstOrDefault(l => l != null && l.text == "MixerHierarchyFixture");
                Assert.That(label, Is.Not.Null, "the fixture root shows");
                var row = label.parent;
                var center = row.worldBound.center;
                var down = new Event { type = EventType.MouseDown, mousePosition = center, button = 0, clickCount = 1 };
                using (var evt = PointerDownEvent.GetPooled(down)) { evt.target = row; window.rootVisualElement.panel.visualTree.SendEvent(evt); }
                Assert.That(Selection.activeGameObject, Is.SameAs(root), "a click selects the object");
            }
            finally
            {
                Selection.activeObject = null;
                window.Close();
            }
        }
        [Test]
        public void TheFoldArrowOpensAndClosesABranch()
        {
            var window = ScriptableObject.CreateInstance<MixerHierarchyWindow>();
            try
            {
                window.position = new Rect(-32000, -32000, 420, 600);
                var show = typeof(EditorWindow).GetMethod("ShowPopupWithMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var showMode = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");
                show.Invoke(window, new object[] { System.Enum.Parse(showMode, "PopupMenu"), false });
                var repaint = typeof(EditorWindow).GetMethod("RepaintImmediately", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var tick = typeof(MixerHierarchyWindow).GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                repaint.Invoke(window, null);
                window.RevealObject(root);
                tick.Invoke(window, null);
                repaint.Invoke(window, null);
                System.Func<System.Collections.Generic.List<string>> shown = () => window.rootVisualElement.Query(className: "mxh-row").ToList()
                    .Where(r => r.resolvedStyle.display != DisplayStyle.None).Select(r => r.Q<Label>(className: "mxh-row__name")).Where(l => l != null).Select(l => l.text).ToList();
                System.Action press = () =>
                {
                    // Pooled rows that are off screen keep their last text: only a shown row counts.
                    var label = window.rootVisualElement.Query<Label>(className: "mxh-row__name").ToList()
                        .First(l => l.text == "MixerHierarchyFixture" && l.parent.resolvedStyle.display != DisplayStyle.None && l.parent.layout.y >= 0);
                    var chevron = label.parent.Q(className: "mxh-row__chevron");
                    var down = new Event { type = EventType.MouseDown, mousePosition = chevron.worldBound.center, button = 0, clickCount = 1 };
                    using (var evt = PointerDownEvent.GetPooled(down)) { evt.target = chevron; window.rootVisualElement.panel.visualTree.SendEvent(evt); }
                    tick.Invoke(window, null);
                    repaint.Invoke(window, null);
                };
                bool openAtFirst = shown().Contains("Child 0");
                press();
                Assert.That(shown().Contains("Child 0"), Is.EqualTo(!openAtFirst), "one press on the arrow folds or unfolds the branch");
                press();
                Assert.That(shown().Contains("Child 0"), Is.EqualTo(openAtFirst), "a second press puts it back");
            }
            finally
            {
                Selection.activeObject = null;
                window.Close();
            }
        }
    }
}
