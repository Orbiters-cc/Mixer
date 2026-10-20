using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    internal enum ProjectSort { Name, Added, Modified }

    /// <summary>Kinds of assets the Project window's filter keeps.</summary>
    [Flags]
    internal enum ProjectKinds
    {
        None = 0,
        Folders = 1 << 0, Scenes = 1 << 1, Prefabs = 1 << 2, Models = 1 << 3, Materials = 1 << 4, Textures = 1 << 5,
        Animations = 1 << 6, Animators = 1 << 7, Audio = 1 << 8, Scripts = 1 << 9, Shaders = 1 << 10, Other = 1 << 11,
    }

    /// <summary>One line of the Project window: a folder or a file, as deep as it is open.</summary>
    internal sealed class ProjectItem
    {
        public string Path;
        public string Name;
        public string Extension;
        public bool IsFolder;
        public bool HasChildren;
        /// <summary>A folder holding nothing at all (the tree, which only lists folders, tells it apart).</summary>
        public bool Empty;
        public int Depth;
        public bool Expanded;
        public DateTime Added, Modified;
        /// <summary>The file's size in bytes (0 for a folder).</summary>
        public long Size;
        /// <summary>Added in the last day.</summary>
        public bool IsNew;
        /// <summary>When it was used (the Recent view).</summary>
        public DateTime Used;
        /// <summary>What matched inside it (content search): "parameter", "GestureLeft".</summary>
        public string MatchLabel, MatchValue;
        /// <summary>A heading line in a list (search sections), not an asset.</summary>
        public bool IsHeading;
    }

    /// <summary>
    /// The project's folders and files as the Project window lists them, read from the disk (only what Unity imported:
    /// entries with a .meta, nothing hidden). Dates are the files' own: created (added to the project) and modified.
    /// </summary>
    internal static class ProjectFiles
    {
        public static readonly TimeSpan NewFor = TimeSpan.FromHours(24);

        /// <summary>The roots of the folder tree: Assets, then each package (by its display name) under "Packages".</summary>
        public static List<(string path, string name)> Packages()
        {
            var result = new List<(string, string)>();
            try
            {
                foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (package == null || package.source == UnityEditor.PackageManager.PackageSource.BuiltIn) continue;
                    result.Add((package.assetPath, string.IsNullOrEmpty(package.displayName) ? package.name : package.displayName));
                }
            }
            catch (Exception) { }
            result.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        // Where an asset path is on the disk: packages from the registry live in Library/PackageCache, not under Packages/.
        private static string Full(string assetPath)
        {
            try { return System.IO.Path.GetFullPath(FileUtil.GetPhysicalPath(assetPath)); } catch (Exception) { return null; }
        }

        /// <summary>Whether things can be created, renamed or moved there: the project's Assets, embedded and local packages.</summary>
        public static bool IsWritable(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (assetPath == "Assets" || assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return true;
            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return false;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            return package != null && (package.source == UnityEditor.PackageManager.PackageSource.Embedded || package.source == UnityEditor.PackageManager.PackageSource.Local);
        }

        /// <summary>A folder's folders (asset paths), by name.</summary>
        public static List<string> Folders(string folder)
        {
            var result = new List<string>();
            string full = Full(folder);
            if (full == null || !Directory.Exists(full)) return result;
            try
            {
                foreach (string entry in Directory.EnumerateDirectories(full))
                {
                    string name = System.IO.Path.GetFileName(entry);
                    if (Hidden(name) || !File.Exists(entry + ".meta")) continue;
                    result.Add(folder + "/" + name);
                }
            }
            catch (Exception) { }
            result.Sort((a, b) => EditorUtility.NaturalCompare(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
            return result;
        }

        /// <summary>Every folder below <paramref name="folder"/>, nearest first, at most <paramref name="limit"/>.</summary>
        public static List<string> FoldersBelow(string folder, int limit)
        {
            var result = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(folder);
            while (queue.Count > 0 && result.Count < limit)
            {
                foreach (string child in Folders(queue.Dequeue()))
                {
                    if (result.Count >= limit) break;
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
            return result;
        }

        private static bool Hidden(string name) => name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal);

        /// <summary>Whether a folder holds anything Unity shows (folders only with <paramref name="foldersOnly"/>).</summary>
        public static bool HasChildren(string folder, bool foldersOnly)
        {
            string full = Full(folder);
            if (full == null || !Directory.Exists(full)) return false;
            try
            {
                foreach (string entry in foldersOnly ? Directory.EnumerateDirectories(full) : Directory.EnumerateFileSystemEntries(full))
                {
                    string name = System.IO.Path.GetFileName(entry);
                    if (Hidden(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(entry + ".meta")) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>A folder's folders and files, sorted (folders first).</summary>
        public static List<ProjectItem> Children(string folder, ProjectSort sort, ProjectKinds kinds, bool foldersOnly = false)
        {
            var items = new List<ProjectItem>();
            string full = Full(folder);
            if (full == null || !Directory.Exists(full)) return items;
            var now = DateTime.UtcNow;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(full).ToList(); } catch (Exception) { return items; }
            foreach (string entry in entries)
            {
                string name = System.IO.Path.GetFileName(entry);
                if (Hidden(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(entry + ".meta")) continue;
                bool isFolder = Directory.Exists(entry);
                if (foldersOnly && !isFolder) continue;
                var item = Make(folder + "/" + name, entry, isFolder, now);
                if (!isFolder && !Keeps(kinds, item)) continue;
                items.Add(item);
            }
            Sort(items, sort);
            return items;
        }

        /// <summary>The item for an asset path (a search result, a recent asset).</summary>
        public static ProjectItem Item(string assetPath)
        {
            string full = Full(assetPath);
            bool isFolder = full != null && Directory.Exists(full);
            return Make(assetPath, full, isFolder, DateTime.UtcNow);
        }

        private static ProjectItem Make(string assetPath, string full, bool isFolder, DateTime now)
        {
            var item = new ProjectItem { Path = assetPath, IsFolder = isFolder };
            string name = System.IO.Path.GetFileName(assetPath);
            item.Extension = isFolder ? string.Empty : System.IO.Path.GetExtension(name);
            item.Name = isFolder ? name : System.IO.Path.GetFileNameWithoutExtension(name);
            try
            {
                if (full != null)
                {
                    if (isFolder)
                    {
                        item.Added = Directory.GetCreationTimeUtc(full);
                        item.Modified = Directory.GetLastWriteTimeUtc(full);
                    }
                    else
                    {
                        var info = new FileInfo(full);
                        item.Added = info.CreationTimeUtc;
                        item.Modified = info.LastWriteTimeUtc;
                        item.Size = info.Exists ? info.Length : 0;
                    }
                }
            }
            catch (Exception) { }
            item.IsNew = item.Added > now - NewFor && item.Added <= now + TimeSpan.FromMinutes(5);
            item.HasChildren = isFolder && HasChildren(assetPath, false);
            return item;
        }

        public static void Sort(List<ProjectItem> items, ProjectSort sort)
        {
            items.Sort((a, b) =>
            {
                if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
                switch (sort)
                {
                    case ProjectSort.Added: { int c = b.Added.CompareTo(a.Added); if (c != 0) return c; break; }
                    case ProjectSort.Modified: { int c = b.Modified.CompareTo(a.Modified); if (c != 0) return c; break; }
                }
                return EditorUtility.NaturalCompare(a.Name, b.Name);
            });
        }

        /// <summary>The kind of a file, from its extension (no asset is loaded).</summary>
        public static ProjectKinds KindOf(ProjectItem item)
        {
            if (item.IsFolder) return ProjectKinds.Folders;
            switch (item.Extension.ToLowerInvariant())
            {
                case ".unity": return ProjectKinds.Scenes;
                case ".prefab": return ProjectKinds.Prefabs;
                case ".fbx": case ".obj": case ".blend": case ".dae": case ".3ds": case ".gltf": case ".glb": case ".vrm": return ProjectKinds.Models;
                case ".mat": return ProjectKinds.Materials;
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd": case ".tif": case ".tiff": case ".exr": case ".hdr": case ".bmp": case ".gif": return ProjectKinds.Textures;
                case ".anim": return ProjectKinds.Animations;
                case ".controller": case ".overridecontroller": case ".mask": return ProjectKinds.Animators;
                case ".wav": case ".mp3": case ".ogg": case ".aif": case ".aiff": case ".flac": return ProjectKinds.Audio;
                case ".cs": case ".asmdef": case ".asmref": case ".dll": return ProjectKinds.Scripts;
                case ".shader": case ".cginc": case ".hlsl": case ".shadergraph": case ".compute": return ProjectKinds.Shaders;
                default: return ProjectKinds.Other;
            }
        }

        /// <summary>Whether the filter keeps the item (no kind picked keeps everything).</summary>
        public static bool Keeps(ProjectKinds kinds, ProjectItem item) => kinds == ProjectKinds.None || (kinds & KindOf(item)) != 0;

        /// <summary>A file name made of an object's name (characters files cannot hold become "_").</summary>
        public static string FileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "New" : name.Trim();
        }

        /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", "12 Oct", "12 Oct 2025".</summary>
        public static string Ago(DateTime utc)
        {
            if (utc == default) return string.Empty;
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " min ago";
            if (span.TotalDays < 1) return (int)span.TotalHours + " h ago";
            if (span.TotalDays < 2) return "yesterday";
            var local = utc.ToLocalTime();
            return local.Year == DateTime.Now.Year ? local.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture) : local.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>A new asset path in <paramref name="folder"/> that nothing has yet ("New Material 1.mat").</summary>
        public static string Unique(string folder, string fileName) => AssetDatabase.GenerateUniqueAssetPath(folder.TrimEnd('/') + "/" + fileName);
    }
}
