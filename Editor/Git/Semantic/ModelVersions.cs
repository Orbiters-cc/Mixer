using System;
using System.IO;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnitGit.Editor.Semantic
{
    /// <summary>
    /// Versions of a model or prefab for Mixer Git's 3D view. Models are read straight from the file by Toolkit's
    /// <see cref="MeshComparison"/>; prefabs are imported into a temporary folder of the project (they only reference their
    /// meshes, so it costs little), kept out of Git, and deleted when no longer shown or at the next editor start.
    /// </summary>
    internal static class ModelVersions
    {
        public const string Folder = "Assets/__UnitGit Compare";

        // Models read directly from the file: nothing is imported, so nothing lands in the project's import cache.
        public static bool IsModelPath(string path)
        {
            string extension = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return extension == ".fbx" || extension == ".obj";
        }

        /// <summary>Files the 3D compare can show: models and prefabs.</summary>
        public static bool IsPreviewable(string path)
        {
            return IsModelPath(path) || string.Equals(System.IO.Path.GetExtension(path ?? string.Empty), ".prefab", StringComparison.OrdinalIgnoreCase);
        }

        [InitializeOnLoadMethod]
        private static void CleanUpLeftovers()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
            };
        }

        /// <summary>Imports a version file (a copy anywhere on disk) as a temporary asset, with the original's model settings.</summary>
        public static string Import(string file, string originalPath, string label)
        {
            // Always this Unity project: only its importer can read the model.
            string projectRoot = UnitGitService.GetUnityProjectRoot();
            ExcludeFromGit(projectRoot);
            string folder = Folder + "/" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Directory.CreateDirectory(System.IO.Path.Combine(projectRoot, folder));
            string asset = folder + "/" + label + System.IO.Path.GetExtension(originalPath);
            File.Copy(file, System.IO.Path.Combine(projectRoot, asset), true);
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(asset) is ModelImporter importer)
            {
                // The same scale and meshes as the project's copy; no rig, animation or extracted materials for a preview.
                if (AssetImporter.GetAtPath(originalPath) is ModelImporter original)
                {
                    importer.globalScale = original.globalScale;
                    importer.useFileScale = original.useFileScale;
                    importer.importBlendShapes = original.importBlendShapes;
                    importer.importNormals = original.importNormals;
                    importer.bakeAxisConversion = original.bakeAxisConversion;
                }
                importer.animationType = ModelImporterAnimationType.None;
                importer.importAnimation = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.SaveAndReimport();
            }
            return asset;
        }

        public static void Release(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return;
            string folder = System.IO.Path.GetDirectoryName(asset)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder) && folder.StartsWith(Folder, StringComparison.Ordinal)) AssetDatabase.DeleteAsset(folder);
        }

        // The temporary folder never shows up as a change: it is listed in the repository's local exclude file.
        private static void ExcludeFromGit(string projectRoot)
        {
            // Git says where the exclude file is: in a linked worktree it is in the main repository's folder.
            string exclude = UnitGitPaths.GitPath(projectRoot, "info/exclude");
            if (exclude == null) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exclude));
            string existing = File.Exists(exclude) ? File.ReadAllText(exclude) : string.Empty;
            if (existing.Contains("/" + Folder + "/")) return;
            File.AppendAllText(exclude, (existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal) ? "\n" : string.Empty) +
                "# Mixer Git 3D compare (temporary imports)\n/" + Folder + "/\n/" + Folder + ".meta\n");
        }
    }
}
