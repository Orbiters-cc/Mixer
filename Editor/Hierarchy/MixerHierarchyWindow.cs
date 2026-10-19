using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>The Mixer Hierarchy window; built in the link phase. Opening it now shows a placeholder.</summary>
    internal sealed partial class MixerHierarchyWindow : EditorWindow
    {
        [MenuItem(MixerInfo.HierarchyMenuPath, priority = 2001)]
        public static void Open()
        {
            var window = GetWindow<MixerHierarchyWindow>();
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Mixer Hierarchy", MixerLogo.TabIcon(), "Orbiters Mixer Hierarchy");
            minSize = new Vector2(260f, 200f);
        }
    }
}
