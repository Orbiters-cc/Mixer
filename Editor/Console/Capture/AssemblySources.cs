using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using Assembly = System.Reflection.Assembly;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Which source (a package, an Assets folder or Unity) each loaded assembly and type belongs to, to say who took the
    /// time in a timing log. <see cref="Take"/> runs on the main thread (the compilation pipeline and the type cache live
    /// there); <see cref="Index"/> and the lookups then run on any thread.
    /// </summary>
    internal sealed class AssemblySources
    {
        private readonly Dictionary<string, string> byAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> initTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> typeAssemblies = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        // Scripts of Unity's predefined assemblies (Assembly-CSharp…), which mix every Assets folder: by file name.
        private readonly Dictionary<string, string> predefinedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        private Assembly[] loaded = new Assembly[0];
        private string unityRoot = string.Empty;
        private bool indexed;

        private AssemblySources()
        {
        }

        /// <summary>Main thread: assemblies with their files, and the types Unity runs on load.</summary>
        public static AssemblySources Take()
        {
            var sources = new AssemblySources();
            try
            {
                sources.unityRoot = NormalizeDirectory(Path.GetDirectoryName(EditorApplication.applicationPath));
            }
            catch (Exception)
            {
                // Without the editor's folder, engine assemblies are recognised by name only.
            }

            try
            {
                foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
                {
                    if (assembly == null || assembly.sourceFiles == null || assembly.sourceFiles.Length == 0)
                    {
                        continue;
                    }

                    if (IsPredefined(assembly.name))
                    {
                        sources.byAssembly[assembly.name] = "Project";
                        foreach (string file in assembly.sourceFiles)
                        {
                            string stem = Path.GetFileNameWithoutExtension(file);
                            if (!sources.predefinedFiles.ContainsKey(stem))
                            {
                                sources.predefinedFiles[stem] = file;
                            }
                        }

                        continue;
                    }

                    sources.byAssembly[assembly.name] = SourceCatalog.NameForPath(assembly.sourceFiles[0]) ?? "Project";
                }
            }
            catch (Exception)
            {
                // Compiled assemblies then fall back to their file location.
            }

            sources.loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in sources.loaded)
            {
                string name = Name(assembly);
                if (name.Length == 0 || sources.byAssembly.ContainsKey(name))
                {
                    continue;
                }

                sources.byAssembly[name] = sources.FromLocation(assembly);
            }

            try
            {
                foreach (var type in TypeCache.GetTypesWithAttribute<InitializeOnLoadAttribute>())
                {
                    if (type != null && !sources.initTypes.ContainsKey(type.Name))
                    {
                        sources.initTypes[type.Name] = Name(type.Assembly);
                    }
                }
            }
            catch (Exception)
            {
                // Startup types then resolve through the full type index.
            }

            return sources;
        }

        /// <summary>Any thread: indexes the types of every assembly that isn't the engine's own.</summary>
        public void Index()
        {
            if (indexed)
            {
                return;
            }

            indexed = true;
            foreach (var assembly in loaded)
            {
                string name = Name(assembly);
                if (name.Length == 0 || IsEngine(name) || assembly.IsDynamic)
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var type in types)
                {
                    string fullName = type?.FullName;
                    if (fullName != null && !typeAssemblies.ContainsKey(fullName))
                    {
                        typeAssemblies[fullName.Replace('+', '.')] = name;
                    }
                }
            }
        }

        /// <summary>A type of a known assembly: in Unity's predefined assemblies, by the script it is in.</summary>
        public string ForType(string typeName, string assembly)
        {
            if (!string.IsNullOrEmpty(assembly) && IsPredefined(assembly.Trim()))
            {
                string stem = ShortName(typeName);
                if (predefinedFiles.TryGetValue(stem, out string file))
                {
                    return SourceCatalog.NameForPath(file) ?? "Project";
                }

                return "Project";
            }

            return ForAssembly(assembly);
        }

        public string ForAssembly(string assembly)
        {
            if (string.IsNullOrEmpty(assembly))
            {
                return TimingProfile.UnitySource;
            }

            return byAssembly.TryGetValue(assembly.Trim(), out string source) ? source : IsEngine(assembly) ? TimingProfile.UnitySource : "Project";
        }

        /// <summary>A type Unity runs on load, by its name without namespace (as the reload profile prints it).</summary>
        public string ForStartupType(string shortName)
        {
            if (!string.IsNullOrEmpty(shortName) && initTypes.TryGetValue(shortName.Trim(), out string assembly))
            {
                return ForType(shortName, assembly);
            }

            return ForTypeName(shortName);
        }

        /// <summary>
        /// A type or a method of a type, by name ("VF.Hooks.VFInitHook", "Orbiters.Logger.Editor.LogCapture.BeforeReload",
        /// "VF.Utils.RecorderUtils.&lt;Init&gt;g__Cleanup|2_0").
        /// </summary>
        public string ForTypeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return TimingProfile.UnitySource;
            }

            if (resolved.TryGetValue(name, out string cached))
            {
                return cached;
            }

            string source = Resolve(name);
            resolved[name] = source;
            return source;
        }

        private string Resolve(string name)
        {
            string candidate = name.Trim();
            int generated = candidate.IndexOf('<');
            if (generated >= 0)
            {
                candidate = candidate.Substring(0, generated);
            }

            candidate = candidate.Replace("::", ".").TrimEnd('.');
            Index();
            while (candidate.Length > 0)
            {
                if (typeAssemblies.TryGetValue(candidate, out string assembly))
                {
                    return ForType(candidate, assembly);
                }

                if (initTypes.TryGetValue(candidate, out assembly))
                {
                    return ForType(candidate, assembly);
                }

                int dot = candidate.LastIndexOf('.');
                if (dot <= 0)
                {
                    break;
                }

                candidate = candidate.Substring(0, dot);
            }

            return SourceCatalog.NameForNamespace(name) ?? TimingProfile.UnitySource;
        }

        private string FromLocation(Assembly assembly)
        {
            string location;
            try
            {
                location = assembly.IsDynamic ? string.Empty : assembly.Location;
            }
            catch (Exception)
            {
                location = string.Empty;
            }

            if (string.IsNullOrEmpty(location))
            {
                return TimingProfile.UnitySource;
            }

            string path = NormalizeDirectory(location);
            if (unityRoot.Length > 0 && path.StartsWith(unityRoot, StringComparison.OrdinalIgnoreCase))
            {
                return TimingProfile.UnitySource;
            }

            return SourceCatalog.NameForPath(location) ?? (IsEngine(Name(assembly)) ? TimingProfile.UnitySource : "Project");
        }

        private static string Name(Assembly assembly)
        {
            try
            {
                return assembly.GetName().Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsPredefined(string assembly) => assembly.StartsWith("Assembly-CSharp", StringComparison.Ordinal);

        // "Ns.Outer.Inner`1" → "Inner".
        private static string ShortName(string typeName)
        {
            string name = typeName ?? string.Empty;
            int generic = name.IndexOf('`');
            if (generic > 0)
            {
                name = name.Substring(0, generic);
            }

            int dot = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+'));
            return dot >= 0 ? name.Substring(dot + 1) : name;
        }

        private static bool IsEngine(string assembly) =>
            assembly.StartsWith("UnityEngine", StringComparison.Ordinal) || assembly.StartsWith("UnityEditor", StringComparison.Ordinal) ||
            assembly.StartsWith("System", StringComparison.Ordinal) || assembly.StartsWith("Mono.", StringComparison.Ordinal) ||
            assembly == "mscorlib" || assembly == "netstandard" || assembly.StartsWith("Microsoft.", StringComparison.Ordinal);

        private static string NormalizeDirectory(string path) => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/') + "/";
    }
}
