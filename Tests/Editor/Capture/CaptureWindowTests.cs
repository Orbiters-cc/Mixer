using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor.Capture.Tests
{
    /// <summary>The Capture window builds without being shown, with one path page at a time and its actions.</summary>
    public sealed class CaptureWindowTests
    {
        [Test]
        public void TheWindowBuildsItsCardsAndShowsOnePathPageAtATime()
        {
            var window = ScriptableObject.CreateInstance<CaptureWindow>();
            try
            {
                window.CreateGUI();
                var root = window.rootVisualElement;
                var titles = root.Query<Label>(className: "mx-card__title").ToList().Select(l => l.text).ToList();
                Assert.That(titles, Is.SupersetOf(new[] { "FRAMES", "POST-PROCESSING", "SELF-CHECK" }));
                var pages = root.Query(className: "mx-capture-page").ToList();
                Assert.That(pages.Count, Is.EqualTo(3));
                string[] expected = { "SCENE VIEW FLIGHT", "WAYPOINTS", "ORBIT" };
                for (int i = 0; i < 3; i++)
                {
                    window.SelectPath(i, false);
                    Assert.That(root.Query<Label>(className: "mx-card__title").ToList().Select(l => l.text), Has.Member(expected[i]));
                    Assert.That(pages.Count(p => p.style.display.value == DisplayStyle.Flex), Is.EqualTo(1));
                    Assert.That(pages[i].style.display.value, Is.EqualTo(DisplayStyle.Flex));
                }
                var buttons = root.Query<Label>(className: "mx-button__label").ToList().Select(l => l.text).ToList();
                Assert.That(buttons, Is.SupersetOf(new[] { "Preview", "Capture", "Record", "Add selection", "Add Scene view" }));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
    }
}
