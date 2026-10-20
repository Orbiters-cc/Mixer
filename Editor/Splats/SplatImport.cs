using System;
using System.IO;
using System.Linq;
using Orbiters.Mixer.Splats;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Brings a session's reconstruction (Orbiters desktop's <c>reconstruction/splats.ply</c>) into the project: the
    /// PLY becomes <c>Assets/Orbiters/Splats/&lt;name&gt;.splats</c>, and an EditorOnly object in the open scene draws it
    /// where the world was captured.
    /// </summary>
    public static class SplatImport
    {
        public const string Folder = "Assets/Orbiters/Splats";

        /// <summary><c>Documents/Orbiters/Sessions</c>, where Orbiters desktop writes sessions by default.</summary>
        static string Library => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Orbiters", "Sessions");

        [MenuItem("Tools/Orbiters/Mixer/Import Splats…")]
        static void ImportFromMenu()
        {
            string start = Directory.Exists(Library) ? Library : "";
            string path = EditorUtility.OpenFilePanel("Splats from Orbiters desktop (a session's reconstruction/splats.ply)", start, "ply");
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                Import(path, TitleOf(path));
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Import Splats", "The splats could not be imported:\n\n" + e.Message, "OK");
            }
        }

        /// <summary>A name for splats at <paramref name="ply"/>: its session's (the folder above
        /// <c>reconstruction/</c>), else the file's.</summary>
        public static string TitleOf(string ply)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(ply));
            if (dir != null && Path.GetFileName(dir) == "reconstruction" && Path.GetDirectoryName(dir) != null)
                return Path.GetFileName(Path.GetDirectoryName(dir));
            return Path.GetFileNameWithoutExtension(ply);
        }

        /// <summary>Copies the PLY into the project, imports it and adds a renderer for it to the open scene, selected
        /// and framed in the Scene view.</summary>
        public static GameObject Import(string ply, string title)
        {
            if (!File.Exists(ply))
                throw new FileNotFoundException("No splats at " + ply);
            Directory.CreateDirectory(Folder);
            string safe = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            string asset = AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + (safe.Length > 0 ? safe : "Splats") + ".splats");
            File.Copy(ply, asset);
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
            var cloud = AssetDatabase.LoadAssetAtPath<SplatCloud>(asset);
            if (cloud == null)
                throw new InvalidDataException("Unity did not import " + asset + " (see the Console).");
            var go = new GameObject("Splats · " + title) { tag = "EditorOnly" };
            go.AddComponent<SplatRenderer>().cloud = cloud;
            Undo.RegisterCreatedObjectUndo(go, "Import splats");
            Selection.activeGameObject = go;
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null)
            {
                view.Frame(cloud.core, false);
                view.ShowNotification(new GUIContent("Splats of " + title + ": " + cloud.Count.ToString("N0")), 3.0);
            }
            return go;
        }
    }
}
