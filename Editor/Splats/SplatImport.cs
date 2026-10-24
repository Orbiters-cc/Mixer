using System;
using System.IO;
using Orbiters.Mixer.Splats;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Splats
{
    /// <summary>
    /// Brings a session's reconstruction (Orbiters - N4N0's <c>reconstruction/splats.ply</c>, with the sky map trained
    /// with it, <c>sky.png</c>, when there is one) into the project: the PLY becomes
    /// <c>Assets/Orbiters/Splats/&lt;name&gt;.splats</c>, the sky map a <c>.splatsky</c> beside it, and an EditorOnly
    /// object in the open scene draws them where the world was captured.
    /// </summary>
    public static class SplatImport
    {
        public const string Folder = "Assets/Orbiters/Splats";

        /// <summary><c>Documents/Orbiters/Sessions</c>, where Orbiters - N4N0 writes sessions by default.</summary>
        static string Library => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Orbiters", "Sessions");

        // A sibling of the Mixer window's item: a menu path is either a command or a submenu.
        [MenuItem("Tools/Orbiters/Mixer Splats…", priority = 2003)]
        static void ImportFromMenu()
        {
            string start = Directory.Exists(Library) ? Library : "";
            string path = EditorUtility.OpenFilePanel("Splats from Orbiters - N4N0 (a session's reconstruction/splats.ply)", start, "ply");
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

        /// <summary>Copies the PLY and its sky map into the project, imports them and adds a renderer for them to the
        /// open scene, selected and framed in the Scene view. A PLY Mixer cannot read is refused before anything lands in
        /// the project.</summary>
        public static GameObject Import(string ply, string title)
        {
            SplatCloud cloud = Bring(ply, title);
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

        /// <summary>The project's cloud of the PLY at <paramref name="ply"/>: the PLY copied in as a <c>.splats</c> and,
        /// when <c>sky.png</c> lies beside it, the sky map as a <c>.splatsky</c> next to it (first, so the cloud's import
        /// finds it), both imported. A PLY Mixer cannot read is refused before anything lands in the project.</summary>
        internal static SplatCloud Bring(string ply, string title)
        {
            if (!File.Exists(ply))
                throw new FileNotFoundException("No splats at " + ply);
            PlyReader.Check(ply);
            if (!AssetPaths.EnsureFolder(Folder))
                throw new IOException("Could not create " + Folder + ".");
            string asset = AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + SafePaths.FileName(title, "Splats") + ".splats");
            SafePaths.RequireLength(Path.GetFullPath(asset));
            string skyAsset = SkyMaps.Beside(asset);
            // One left behind by splats of this name deleted earlier belongs to nothing.
            if (File.Exists(skyAsset))
                AssetDatabase.DeleteAsset(skyAsset);
            string sky = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ply)), SkyMaps.FileName);
            if (File.Exists(sky))
            {
                SafePaths.RequireLength(Path.GetFullPath(skyAsset));
                File.Copy(sky, skyAsset);
                AssetDatabase.ImportAsset(skyAsset, ImportAssetOptions.ForceSynchronousImport);
            }
            File.Copy(ply, asset);
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
            var cloud = AssetDatabase.LoadAssetAtPath<SplatCloud>(asset);
            if (cloud == null)
            {
                // Left there, they would fail again on every reimport.
                AssetDatabase.DeleteAsset(asset);
                if (File.Exists(skyAsset))
                    AssetDatabase.DeleteAsset(skyAsset);
                throw new InvalidDataException("Unity could not import the splats (see the Console).");
            }
            return cloud;
        }
    }
}
