using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private static string GetTopFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "root";
            }

            string normalized = path.Replace('\\', '/');
            int slash = normalized.IndexOf('/');
            return slash > 0 ? normalized.Substring(0, slash) : "root";
        }

        private UnitGitStatusEntry GetSelectedChange()
        {
            if (snapshot == null || snapshot.Changes.Count == 0)
            {
                return null;
            }

            var selected = snapshot.Changes.FirstOrDefault(change =>
                string.Equals(change.Path, selectedChangePath, StringComparison.OrdinalIgnoreCase));
            return selected ?? snapshot.Changes[0];
        }

        private static string GetFileLeaf(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return Path.GetFileName(path.Replace('\\', '/'));
        }

        private static string GetDirectoryLabel(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            return slash > 0 ? normalized.Substring(0, slash) : string.Empty;
        }

        private void OpenProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || snapshot == null)
            {
                return;
            }

            string normalized = projectPath.Replace('\\', '/');
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                return;
            }

            string fullPath = Path.GetFullPath(Path.Combine(snapshot.ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                EditorUtility.RevealInFinder(fullPath);
            }
        }

        private Image BuildProjectFileIcon(string path, string className)
        {
            var icon = new Image();
            icon.AddToClassList(className);
            icon.image = GetProjectFileIcon(path);
            return icon;
        }

        private Texture2D GetProjectFileIcon(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            if (fileTypeIcons.TryGetValue(extension, out Texture2D cached))
                return cached;
            string iconName;
            switch (extension)
            {
                case ".cs":
                    iconName = "cs Script Icon";
                    break;
                case ".js":
                case ".jsx":
                case ".ts":
                case ".tsx":
                    iconName = "TextAsset Icon";
                    break;
                case ".prefab":
                    iconName = "Prefab Icon";
                    break;
                case ".mat":
                    iconName = "Material Icon";
                    break;
                case ".anim":
                    iconName = "AnimationClip Icon";
                    break;
                case ".controller":
                    iconName = "AnimatorController Icon";
                    break;
                case ".fbx":
                case ".obj":
                    iconName = "PrefabModel Icon";
                    break;
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga":
                case ".psd":
                    iconName = "Texture2D Icon";
                    break;
                case ".shader":
                    iconName = "Shader Icon";
                    break;
                default:
                    iconName = "DefaultAsset Icon";
                    break;
            }

            var texture = (EditorGUIUtility.IconContent(iconName).image as Texture2D)
                   ?? EditorGUIUtility.FindTexture(iconName)
                   ?? (EditorGUIUtility.IconContent("DefaultAsset Icon").image as Texture2D)
                   ?? EditorGUIUtility.FindTexture("DefaultAsset Icon");
            fileTypeIcons[extension] = texture;
            return texture;
        }

        private readonly Dictionary<string, Texture2D> fileTypeIcons = new Dictionary<string, Texture2D>();

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            if (maxLength <= 3)
            {
                return value.Substring(0, maxLength);
            }

            return value.Substring(0, maxLength - 3) + "...";
        }
    }
}
