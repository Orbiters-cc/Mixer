using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Shows an object without selecting it, as Unity's ping does, in Mixer's windows too: a scene object in the Mixer
    /// Hierarchy, an asset in the Mixer Project. Unity's ping reaches them through <see cref="UnityWindowHooks"/>; where it
    /// cannot (a Unity version without the internals it uses), they are told here.
    /// </summary>
    internal static class MixerPing
    {
        public static void Ping(Object target)
        {
            if (target == null) return;
            bool hooked = UnityWindowHooks.ListensToPings;
            var go = target is Component component ? component.gameObject : target as GameObject;
            if (go != null && go.scene.IsValid())
            {
                if (!hooked) foreach (var window in Resources.FindObjectsOfTypeAll<MixerHierarchyWindow>()) window.RevealObject(go);
                EditorGUIUtility.PingObject(go);
                return;
            }
            string path = AssetDatabase.GetAssetPath(target);
            if (!hooked && !string.IsNullOrEmpty(path))
                foreach (var window in Resources.FindObjectsOfTypeAll<MixerProjectWindow>()) window.Ping(path);
            EditorGUIUtility.PingObject(target);
        }
    }
}
