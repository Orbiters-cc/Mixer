using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Logger.Editor
{
    internal sealed class LogSource
    {
        public readonly int Id;
        public readonly string Key;
        public readonly string Name;
        /// <summary>One of Orbiters' own tools: its chip wears Mixer's accent, whichever it is now.</summary>
        public readonly bool Accent;
        private readonly Color color;

        public LogSource(int id, string key, string name, Color color, bool accent = false)
        {
            Id = id;
            Key = key;
            Name = name;
            this.color = color;
            Accent = accent;
        }

        public Color Color => Accent ? Orbiters.Mixer.Editor.MixerAccent.Text : color;
    }

    /// <summary>
    /// Who logged a message: the package or Assets folder of the code that logged it (VRChat SDK, VRCFury, MCB…),
    /// <c>Unity</c> for the engine and <c>Compiler</c> for compiler messages. Sources keep their id for the session and
    /// their colour for good (it comes from their name).
    /// </summary>
    internal sealed class SourceCatalog
    {
        public const int Unity = 0;
        public const int Compiler = 1;
        public const int Project = 2;

        // Packages that read better under one familiar name.
        private static readonly Dictionary<string, string> PackageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "com.vrchat.base", "VRChat SDK" },
            { "com.vrchat.avatars", "VRChat SDK" },
            { "com.vrchat.worlds", "VRChat SDK" },
            { "com.vrchat.core.vpm-resolver", "VRChat Package Resolver" },
            { "com.vrcfury.vrcfury", "VRCFury" },
            { "com.vrcfury.temp", "VRCFury" },
            { "nadena.dev.modular-avatar", "Modular Avatar" },
            { "nadena.dev.ndmf", "NDMF" },
            { "com.poiyomi.toon", "Poiyomi" },
            { "jp.lilxyzw.liltoon", "lilToon" },
            { "vrchat.blackstartx.gesture-manager", "Gesture Manager" },
            { "com.merlin.udonsharp", "UdonSharp" },
            { "com.vrchat.udonsharp", "UdonSharp" },
            { "com.anatawa12.avatar-optimizer", "Avatar Optimizer" },
            { "d4rkpl4y3r.d4rkavataroptimizer", "d4rkAvatarOptimizer" },
            { "com.coplaydev.unity-mcp", "MCP for Unity" },
            { "orbiters.mcb", "MCB" },
            { "orbiters.myavatar", "My Avatar" },
            { "orbiters.toolkit", "Orbiters Toolkit" },
            { "orbiters.refit", "ReFit" },
            { "orbiters.mixer", "Mixer" },
            { "orbiters.unitypackagemanager", "UnityPackageManager" },
            { "orbiters.touchworld", "Touch World" },
        };

        // Tools often installed in Assets rather than as packages.
        private static readonly Dictionary<string, string> AssetFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "VRCSDK", "VRChat SDK" },
            { "VRCFury", "VRCFury" },
            { "_PoiyomiShaders", "Poiyomi" },
            { "_PoiyomiToonShader", "Poiyomi" },
            { "lilToon", "lilToon" },
            { "UdonSharp", "UdonSharp" },
            { "Udon", "VRChat SDK" },
            { "GestureManager", "Gesture Manager" },
            { "Thry", "Poiyomi" },
            { "d4rkAvatarOptimizer", "d4rkAvatarOptimizer" },
            { "AV3Manager", "Av3Manager" },
            { "TextMesh Pro", "TextMesh Pro" },
            { "Editor", "Project" },
            { "Scripts", "Project" },
            { "Plugins", "Plugins" },
        };

        // Namespaces of code without a source file (precompiled DLLs), and [Tag] prefixes of messages without a stack.
        // A namespace counts up to a word: "MCB" is MCBEditor and MCBUtils, not MCBuildHelper.
        private static readonly (string prefix, string name)[] Namespaces =
        {
            ("VRC.", "VRChat SDK"), ("VRCSDK", "VRChat SDK"), ("VF.", "VRCFury"), ("nadena.dev.modular_avatar", "Modular Avatar"),
            ("nadena.dev.ndmf", "NDMF"), ("Thry", "Poiyomi"), ("lilToon", "lilToon"), ("UdonSharp", "UdonSharp"),
            ("BlackStartX.GestureManager", "Gesture Manager"), ("Anatawa12.AvatarOptimizer", "Avatar Optimizer"),
            ("Orbiters.MyAvatar", "My Avatar"), ("Orbiters.Toolkit", "Orbiters Toolkit"), ("Orbiters.UnitGit", "Mixer Git"),
            ("Orbiters.ReFit", "ReFit"), ("Orbiters.Mixer", "Mixer"), ("Orbiters.Logger", "Mixer console"),
            // MCB's code has no namespace of its own: its classes start with MCB (MCBLogger, MCBEditor, MCBUtils…).
            ("MCB", "MCB"), ("MCPForUnity", "MCP for Unity"), ("TMPro", "TextMesh Pro"),
        };

        private static readonly (string tag, string name)[] Tags =
        {
            ("[VRCFury", "VRCFury"), ("[Modular Avatar", "Modular Avatar"), ("[MA]", "Modular Avatar"), ("[NDMF", "NDMF"),
            ("[UdonSharp", "UdonSharp"), ("[Poiyomi", "Poiyomi"), ("[Thry", "Poiyomi"), ("[lilToon", "lilToon"),
            ("[Gesture Manager", "Gesture Manager"), ("[VRCSDK", "VRChat SDK"), ("[VRC", "VRChat SDK"), ("[AAO", "Avatar Optimizer"),
            ("[MCB", "MCB"), ("[My Avatar", "My Avatar"), ("[Mixer Git", "Mixer Git"), ("[Mixer console", "Mixer console"), ("[ReFit", "ReFit"), ("[Mixer", "Mixer"),
            ("[Package Manager", "Package Manager"), ("[Licensing", "Unity"), ("[Physics.PhysX", "Unity"),
            ("[Worker", "Asset import"), ("MCP-FOR-UNITY", "MCP for Unity"),
        };

        private static readonly Color[] Palette =
        {
            Hex("#6aa7ff"), Hex("#b890ff"), Hex("#4fd1c5"), Hex("#f78fb3"), Hex("#a3e635"), Hex("#67e8f9"),
            Hex("#f0a35e"), Hex("#8b9cff"), Hex("#6ee7b7"), Hex("#fda4af"), Hex("#c4b5fd"), Hex("#fbbf24")
        };

        private static readonly Dictionary<string, Color> KnownColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "unity", Hex("#9aa4b2") },
            { "compiler", Hex("#ff9f7a") },
            { "project", Hex("#e2e2e2") },
            { "vrchat sdk", Hex("#6aa7ff") },
            { "vrcfury", Hex("#b890ff") },
            { "modular avatar", Hex("#4fd1c5") },
            { "ndmf", Hex("#a3e635") },
            { "poiyomi", Hex("#f78fb3") },
            { "liltoon", Hex("#fda4af") },
            { "udonsharp", Hex("#67e8f9") },
            { "gesture manager", Hex("#f0a35e") },
            { "mcp for unity", Hex("#2ea3ff") },
            { "orbiters toolkit", Hex("#35d08a") },
            { "timings", Hex("#f2c46d") },
        };

        // Orbiters' own tools, in Mixer's accent.
        private static readonly HashSet<string> AccentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "mcb", "my avatar", "mixer git", "refit", "mixer", "mixer console" };

        private static Dictionary<string, string> registeredPackages;

        private readonly List<LogSource> sources = new List<LogSource>();
        private readonly Dictionary<string, int> byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> byFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public SourceCatalog()
        {
            Get("Unity");
            Get("Compiler");
            Get("Project");
        }

        public int Count => sources.Count;

        public LogSource this[int id] => id >= 0 && id < sources.Count ? sources[id] : sources[Unity];

        public IReadOnlyList<LogSource> All => sources;

        public bool TryFind(string key, out int id) => byKey.TryGetValue(key ?? string.Empty, out id);

        /// <summary>The source named <paramref name="name"/>, added when new.</summary>
        public int Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Unity;
            }

            string key = name.Trim().ToLowerInvariant();
            if (byKey.TryGetValue(key, out int id))
            {
                return id;
            }

            id = sources.Count;
            sources.Add(new LogSource(id, key, name.Trim(), ColorFor(key), AccentKeys.Contains(key)));
            byKey[key] = id;
            return id;
        }

        /// <summary>Restores a source saved with the session at its old id.</summary>
        public void Restore(string key, string name)
        {
            if (byKey.ContainsKey(key))
            {
                return;
            }

            int id = sources.Count;
            sources.Add(new LogSource(id, key, name, ColorFor(key), AccentKeys.Contains(key)));
            byKey[key] = id;
        }

        public int Resolve(LogVariant variant, string condition, List<StackFrame> frames, int callsite, string compileFile)
        {
            if (LogVariants.IsCompile(variant))
            {
                return Compiler;
            }

            // An importer's message about an asset ("Packages/x/y.uss (line 27): …") belongs to that asset's package,
            // not to the code that happened to start the import.
            string asset = LeadingAssetPath(condition);
            if (asset != null)
            {
                return FromFile(asset);
            }

            // The frame that logged it, else any frame with a source file, else the first frame's namespace.
            if (callsite >= 0 && callsite < frames.Count && frames[callsite].HasFile)
            {
                return FromFile(frames[callsite].File);
            }

            foreach (var frame in frames)
            {
                if (frame.Kind == StackFrameKind.User)
                {
                    return FromFile(frame.File);
                }
            }

            if (callsite >= 0 && callsite < frames.Count)
            {
                int byNamespace = FromNamespace(frames[callsite].Method);
                if (byNamespace >= 0)
                {
                    return byNamespace;
                }
            }

            int byTag = FromTag(condition);
            return byTag >= 0 ? byTag : Unity;
        }

        /// <summary>The source of a project file (for logs whose stack trace was cut, by the file Unity's console names).</summary>
        public int ForFile(string file) => string.IsNullOrEmpty(file) ? Unity : FromFile(file);

        // "Packages/orbiters.toolkit/Editor/UI/orbit-sphere.uss (line 27): warning: …" → that path.
        internal static string LeadingAssetPath(string condition)
        {
            if (string.IsNullOrEmpty(condition) ||
                !(condition.StartsWith("Assets/", StringComparison.Ordinal) || condition.StartsWith("Packages/", StringComparison.Ordinal)))
            {
                return null;
            }

            int end = 0;
            while (end < condition.Length && end < 400)
            {
                char c = condition[end];
                if (c == '\n' || c == '(' || c == ':' || c == ' ' && end + 1 < condition.Length && condition[end + 1] == '(')
                {
                    break;
                }

                end++;
            }

            string path = condition.Substring(0, end).TrimEnd();
            int slash = path.LastIndexOf('/');
            int dot = path.LastIndexOf('.');
            return dot > slash && slash > 0 && dot < path.Length - 1 ? path : null;
        }

        private int FromFile(string file)
        {
            string path = StackTraces.NormalizePath(file);
            int slash = path.IndexOf('/');
            if (slash <= 0)
            {
                return Project;
            }

            string root = path.Substring(0, slash);
            int second = path.IndexOf('/', slash + 1);
            string folder = second > slash ? path.Substring(slash + 1, second - slash - 1) : string.Empty;
            string cacheKey = root + "/" + folder;
            if (byFolder.TryGetValue(cacheKey, out int cached))
            {
                return cached;
            }

            int id;
            if (root.Equals("Packages", StringComparison.OrdinalIgnoreCase) && folder.Length > 0)
            {
                id = Get(PackageDisplayName(folder));
            }
            else if (root.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                if (folder.Length == 0)
                {
                    id = Project;
                }
                else if (AssetFolders.TryGetValue(folder, out string known))
                {
                    id = Get(known);
                }
                else
                {
                    id = Get("Assets/" + folder);
                }
            }
            else
            {
                id = Project;
            }

            byFolder[cacheKey] = id;
            return id;
        }

        private int FromNamespace(string method)
        {
            if (string.IsNullOrEmpty(method))
            {
                return -1;
            }

            string name = NameForNamespace(method);
            return name != null ? Get(name) : StackTraces.IsEngine(method) ? Unity : -1;
        }

        private int FromTag(string condition)
        {
            if (string.IsNullOrEmpty(condition))
            {
                return -1;
            }

            string text = RichText.Strip(condition.Length > 160 ? condition.Substring(0, 160) : condition).TrimStart();
            foreach (var (tag, name) in Tags)
            {
                if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    return Get(name);
                }
            }

            return -1;
        }

        /// <summary>
        /// The source name of a file of the project or of a package: "Packages/com.vrcfury.vrcfury/…" and
        /// "Library/PackageCache/com.vrcfury.vrcfury@…/…" → "VRCFury", "Assets/VRCFury/…" → "VRCFury", other Assets
        /// folders → "Assets/Folder". Null for files outside the project.
        /// </summary>
        internal static string NameForPath(string path)
        {
            // Package cache paths come out of NormalizePath as Packages/<name>/…
            string normalized = StackTraces.NormalizePath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            int slash = normalized.IndexOf('/');
            if (slash <= 0)
            {
                return null;
            }

            string root = normalized.Substring(0, slash);
            int second = normalized.IndexOf('/', slash + 1);
            string folder = second > slash ? normalized.Substring(slash + 1, second - slash - 1) : string.Empty;
            if (root.Equals("Packages", StringComparison.OrdinalIgnoreCase) && folder.Length > 0)
            {
                return PackageDisplayName(folder);
            }

            if (root.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                if (folder.Length == 0)
                {
                    return "Project";
                }

                return AssetFolders.TryGetValue(folder, out string known) ? known : "Assets/" + folder;
            }

            return null;
        }

        /// <summary>The source name of a package ("com.vrcfury.vrcfury" → "VRCFury").</summary>
        internal static string NameForPackage(string package) => string.IsNullOrEmpty(package) ? null : PackageDisplayName(package);

        /// <summary>The source a namespace belongs to ("VF.Hooks.VFInitHook" → "VRCFury"), or null.</summary>
        internal static string NameForNamespace(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            foreach (var (prefix, name) in Namespaces)
            {
                if (typeName.StartsWith(prefix, StringComparison.Ordinal) && (typeName.Length == prefix.Length || !char.IsLower(typeName[prefix.Length])))
                {
                    return name;
                }
            }

            return null;
        }

        private static string PackageDisplayName(string package)
        {
            if (PackageNames.TryGetValue(package, out string known))
            {
                return known;
            }

            if (package.StartsWith("com.vrchat.", StringComparison.OrdinalIgnoreCase))
            {
                return "VRChat SDK";
            }

            if (registeredPackages == null)
            {
                registeredPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var info in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                    {
                        if (info != null && !string.IsNullOrEmpty(info.name))
                        {
                            registeredPackages[info.name] = string.IsNullOrWhiteSpace(info.displayName) ? info.name : info.displayName;
                        }
                    }
                }
                catch (Exception)
                {
                    // Package Manager not ready (very early in a domain load): fall back to the package name.
                }
            }

            return registeredPackages.TryGetValue(package, out string display) ? display : package;
        }

        internal static Color ColorFor(string key)
        {
            if (AccentKeys.Contains(key))
            {
                return Orbiters.Mixer.Editor.MixerAccent.Text;
            }

            if (KnownColors.TryGetValue(key, out var known))
            {
                return known;
            }

            // Stable across sessions: FNV-1a of the name, not string.GetHashCode.
            uint hash = 2166136261;
            foreach (char c in key)
            {
                hash = (hash ^ c) * 16777619;
            }

            return Palette[hash % (uint)Palette.Length];
        }

        internal static Color Hex(string hex)
        {
            // Parsed here rather than with ColorUtility: these run in static initializers, on any thread.
            uint value = Convert.ToUInt32(hex.TrimStart('#'), 16);
            return new Color(((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f, 1f);
        }
    }
}
