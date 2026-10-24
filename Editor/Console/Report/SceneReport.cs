using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Logger.Editor
{
    /// <summary>The open scenes in numbers (card, README and AI diagnosis).</summary>
    internal sealed class SceneDigest
    {
        public readonly List<string> Names = new List<string>();
        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Roots = new List<string>();
        public int Objects;
        public int Inactive;
        public int MissingScripts;
        public bool Unsaved;
        public bool Untitled;

        public string Title => Names.Count == 0 ? "No scene" : Names.Count == 1 ? Names[0] : Names[0] + " + " + (Names.Count - 1);
    }

    /// <summary>The files the open scenes use, with their total size.</summary>
    internal sealed class SceneFiles
    {
        public string[] Paths = Array.Empty<string>();

        /// <summary>Where each file is on disk: a registry package's "Packages/…" lives in Library/PackageCache.</summary>
        public string[] Physical = Array.Empty<string>();
        public long[] Sizes = Array.Empty<long>();
        public long Bytes;
    }

    /// <summary>
    /// The scene part of a report: every object of the open scenes as a tree with its components (missing scripts
    /// flagged), the project files they use as a folder tree with sizes, and, separately, the scenes exported as a
    /// Unity package with everything they depend on.
    /// </summary>
    internal static class SceneReport
    {
        private const int MaxObjects = 60000;

        /// <summary>Counts the open scenes' objects; main thread.</summary>
        internal static SceneDigest Look()
        {
            var digest = new SceneDigest();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }

                digest.Names.Add(string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name);
                if (string.IsNullOrEmpty(scene.path))
                {
                    digest.Untitled = true;
                }
                else
                {
                    digest.Paths.Add(scene.path);
                }

                digest.Unsaved |= scene.isDirty;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (digest.Roots.Count < 40)
                    {
                        digest.Roots.Add(root.name);
                    }

                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        digest.Objects++;
                        if (!transform.gameObject.activeSelf)
                        {
                            digest.Inactive++;
                        }

                        digest.MissingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                    }
                }
            }

            return digest;
        }

        /// <summary>The files the saved open scenes depend on (main thread); sizes are read by <see cref="Measure"/>.</summary>
        internal static SceneFiles Dependencies(SceneDigest digest)
        {
            var files = new SceneFiles();
            if (digest.Paths.Count > 0)
            {
                files.Paths = AssetDatabase.GetDependencies(digest.Paths.ToArray(), true).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                files.Physical = files.Paths.Select(Physical).ToArray();
            }

            return files;
        }

        private static string Physical(string path)
        {
            try
            {
                return FileUtil.GetPhysicalPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>Reads the size of every file; safe on a worker thread.</summary>
        internal static void Measure(SceneFiles files)
        {
            files.Sizes = new long[files.Paths.Length];
            long total = 0;
            for (int i = 0; i < files.Paths.Length; i++)
            {
                try
                {
                    var info = new FileInfo(files.Physical.Length > i ? files.Physical[i] : files.Paths[i]);
                    files.Sizes[i] = info.Exists ? info.Length : 0;
                }
                catch (Exception)
                {
                    files.Sizes[i] = 0;
                }

                total += files.Sizes[i];
            }

            files.Bytes = total;
        }

        /// <summary>Writes the hierarchy of the open scenes; main thread.</summary>
        internal static string WriteHierarchy(ReportContext context)
        {
            var digest = Look();
            context.Scene = digest;
            var text = new StringBuilder(1 << 16);
            int written = 0;
            text.Append("# The open scenes, every object with its components. (off) = inactive, ⚠ = missing script, ▸ = prefab instance.\n");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }

                text.Append("\n").Append(string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name);
                text.Append("  (").Append(string.IsNullOrEmpty(scene.path) ? "not saved" : scene.path).Append(scene.isDirty ? ", unsaved changes" : string.Empty).Append(")\n");
                var roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    Describe(text, roots[r].transform, string.Empty, r == roots.Length - 1, ref written);
                }
            }

            if (written >= MaxObjects)
            {
                text.Append("\n[Stopped after ").Append(MaxObjects.ToString("N0")).Append(" objects.]\n");
            }

            context.WriteText("scene/hierarchy.txt", text.ToString(), "Every object of the open scenes with its components");
            return LoggerUi.Plural(digest.Objects, "object") + (digest.MissingScripts > 0 ? " · " + LoggerUi.Plural(digest.MissingScripts, "missing script") : string.Empty);
        }

        private static void Describe(StringBuilder text, Transform transform, string indent, bool last, ref int written)
        {
            if (written++ >= MaxObjects)
            {
                return;
            }

            var gameObject = transform.gameObject;
            text.Append(indent).Append(last ? "└─ " : "├─ ");
            if (!gameObject.activeSelf)
            {
                text.Append("(off) ");
            }

            text.Append(gameObject.name);
            if (PrefabUtility.IsAnyPrefabInstanceRoot(gameObject))
            {
                string source = AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(gameObject));
                text.Append("  ▸ ").Append(string.IsNullOrEmpty(source) ? "missing prefab" : source);
            }

            var names = new List<string>();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    names.Add("⚠ Missing script");
                }
                else if (!(component is Transform))
                {
                    names.Add(component.GetType().Name + (component is Behaviour behaviour && !behaviour.enabled ? " (off)" : string.Empty));
                }
            }

            if (names.Count > 0)
            {
                text.Append("  [").Append(string.Join(", ", names)).Append(']');
            }

            text.Append('\n');
            string childIndent = indent + (last ? "   " : "│  ");
            for (int i = 0; i < transform.childCount; i++)
            {
                Describe(text, transform.GetChild(i), childIndent, i == transform.childCount - 1, ref written);
            }
        }

        /// <summary>The files the scenes use as a folder tree with sizes; safe on a worker thread.</summary>
        internal static void WriteFiles(ReportContext context, SceneFiles files)
        {
            var root = new Folder(string.Empty);
            for (int i = 0; i < files.Paths.Length; i++)
            {
                string[] parts = files.Paths[i].Split('/');
                var folder = root;
                for (int p = 0; p < parts.Length - 1; p++)
                {
                    folder = folder.Child(parts[p]);
                }

                folder.Files.Add(new KeyValuePair<string, long>(parts[parts.Length - 1], files.Sizes.Length > i ? files.Sizes[i] : 0));
            }

            root.Total();
            var text = new StringBuilder();
            text.Append("# ").Append(LoggerUi.Plural(files.Paths.Length, "file")).Append(" used by the open scenes, ").Append(Size(files.Bytes)).Append(" in all.\n\n");
            var top = root.Folders.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < top.Count; i++)
            {
                top[i].Write(text, string.Empty, i == top.Count - 1 && root.Files.Count == 0);
            }

            context.WriteText("scene/files.txt", text.ToString(), "The project files the open scenes use, as folders with sizes");
        }

        /// <summary>Exports the saved open scenes with their dependencies as a Unity package; main thread.</summary>
        internal static string Export(ReportContext context, SceneDigest digest)
        {
            if (digest.Paths.Count == 0)
            {
                throw new InvalidOperationException("Save the scene first: an untitled scene has no file to export.");
            }

            string name = "scene/" + Safe(digest.Names.FirstOrDefault() ?? "Scene") + ".unitypackage";
            string path = context.PathOf(name);
            AssetDatabase.ExportPackage(digest.Paths.ToArray(), path, ExportPackageOptions.IncludeDependencies);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("Unity did not write the package.");
            }

            context.Add(name, "The open scenes with every file they use, as a Unity package (import it into an empty project)");
            return Size(new FileInfo(path).Length) + (digest.Unsaved ? " · unsaved changes not included" : string.Empty);
        }

        internal static string Size(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            double value = bytes / 1024d;
            string[] units = { "KB", "MB", "GB" };
            int unit = 0;
            while (value >= 1024d && unit < units.Length - 1)
            {
                value /= 1024d;
                unit++;
            }

            return value.ToString(value < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
        }

        private static string Safe(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '-');
            }

            return name.Trim();
        }

        private sealed class Folder
        {
            public readonly string Name;
            public readonly Dictionary<string, Folder> Folders = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
            public readonly List<KeyValuePair<string, long>> Files = new List<KeyValuePair<string, long>>();
            public long Bytes;
            public int Count;

            public Folder(string name) => Name = name;

            public Folder Child(string name)
            {
                if (!Folders.TryGetValue(name, out var child))
                {
                    Folders[name] = child = new Folder(name);
                }

                return child;
            }

            public void Total()
            {
                Bytes = Files.Sum(f => f.Value);
                Count = Files.Count;
                foreach (var child in Folders.Values)
                {
                    child.Total();
                    Bytes += child.Bytes;
                    Count += child.Count;
                }
            }

            public void Write(StringBuilder text, string indent, bool last)
            {
                text.Append(indent).Append(last ? "└─ " : "├─ ").Append(Name).Append("/  (").Append(LoggerUi.Plural(Count, "file")).Append(", ").Append(Size(Bytes)).Append(")\n");
                string inner = indent + (last ? "   " : "│  ");
                var folders = Folders.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var files = Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < folders.Count; i++)
                {
                    folders[i].Write(text, inner, i == folders.Count - 1 && files.Count == 0);
                }

                for (int i = 0; i < files.Count; i++)
                {
                    text.Append(inner).Append(i == files.Count - 1 ? "└─ " : "├─ ").Append(files[i].Key).Append("  ").Append(Size(files[i].Value)).Append('\n');
                }
            }
        }
    }
}
