using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Shows an object without selecting it, as Unity's ping does, in Mixer's windows too (Unity pings only its own
    /// Hierarchy and Project windows, which Mixer's may have replaced): a scene object in the Mixer Hierarchy, an asset in
    /// the Mixer Project.
    /// </summary>
    internal static class MixerPing
    {
        public static void Ping(Object target)
        {
            if (target == null) return;
            var go = target is Component component ? component.gameObject : target as GameObject;
            if (go != null && go.scene.IsValid())
            {
                foreach (var window in Resources.FindObjectsOfTypeAll<MixerHierarchyWindow>()) window.RevealObject(go);
                EditorGUIUtility.PingObject(go);
                return;
            }
            string path = AssetDatabase.GetAssetPath(target);
            if (!string.IsNullOrEmpty(path))
                foreach (var window in Resources.FindObjectsOfTypeAll<MixerProjectWindow>()) window.Ping(path);
            EditorGUIUtility.PingObject(target);
        }
    }
}
