using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnitGit.Editor.Semantic
{
    /// <summary>Which tool a component belongs to, shown as a small badge next to its name.</summary>
    internal enum ComponentFamily { Unity, VRChat, VRCFury, ModularAvatar, Orbiters, Script }

    /// <summary>
    /// Names for what a scene or prefab file only knows by ID: scripts, assets and objects inside other prefabs or models,
    /// read from the AssetDatabase (main thread only) and cached. VRChat, VRCFury and Modular Avatar components get the
    /// names creators know them by.
    /// </summary>
    internal sealed class UnitySemanticNames
    {
        private static readonly Regex Vector = new Regex(@"^\{([xyzwrgba]): ([^,}]+)(?:, ([xyzwrgba]): ([^,}]+))*\}$", RegexOptions.Compiled);
        private readonly Dictionary<string, (string label, ComponentFamily family)> scripts = new Dictionary<string, (string, ComponentFamily)>();
        private readonly Dictionary<string, string> assets = new Dictionary<string, string>();
        private readonly Dictionary<string, Dictionary<long, string>> inside = new Dictionary<string, Dictionary<long, string>>();

        public (string label, ComponentFamily family) Component(SemanticComponent component)
        {
            if (!string.IsNullOrEmpty(component.ScriptGuid)) return Script(component.ScriptGuid, component.ScriptFileId);
            return (Words(component.TypeName), ComponentFamily.Unity);
        }

        public (string label, ComponentFamily family) Script(string guid, long fileId)
        {
            string key = guid + ":" + fileId.ToString(CultureInfo.InvariantCulture);
            if (scripts.TryGetValue(key, out var known)) return known;
            var result = ("Missing script", ComponentFamily.Script);
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
            {
                MonoScript script = null;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path).OfType<MonoScript>())
                {
                    // Scripts in a DLL share its GUID and differ by local ID; a .cs file holds one.
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long local) && (local == fileId || fileId == 11500000)) { script = asset; break; }
                }
                var type = script != null ? script.GetClass() : null;
                result = type != null ? Friendly(type.FullName ?? type.Name, type.Name) : (script != null ? (Words(script.name), ComponentFamily.Script) : result);
            }
            scripts[key] = result;
            return result;
        }

        internal static (string label, ComponentFamily family) Friendly(string fullName, string name)
        {
            if (fullName.StartsWith("VF.", StringComparison.Ordinal)) return (name == "VRCFury" ? "VRCFury" : "VRCFury " + Words(name), ComponentFamily.VRCFury);
            if (fullName.StartsWith("nadena.dev.modular_avatar", StringComparison.Ordinal))
                return ("MA " + Words(name.StartsWith("ModularAvatar", StringComparison.Ordinal) ? name.Substring(13) : name), ComponentFamily.ModularAvatar);
            if (fullName.StartsWith("Orbiters.", StringComparison.Ordinal)) return (Words(name.StartsWith("Orbiters", StringComparison.Ordinal) ? name.Substring(8) : name), ComponentFamily.Orbiters);
            if (fullName.StartsWith("VRC.", StringComparison.Ordinal) || fullName == "PipelineManager")
            {
                switch (name)
                {
                    case "VRCAvatarDescriptor": return ("Avatar Descriptor", ComponentFamily.VRChat);
                    case "PipelineManager": return ("Pipeline Manager (avatar ID)", ComponentFamily.VRChat);
                    case "VRCPhysBone": return ("PhysBone", ComponentFamily.VRChat);
                    case "VRCPhysBoneCollider": return ("PhysBone Collider", ComponentFamily.VRChat);
                }
                return (Words(name.StartsWith("VRC", StringComparison.Ordinal) ? name.Substring(3) : name), ComponentFamily.VRChat);
            }
            return (Words(name), ComponentFamily.Script);
        }

        /// <summary>An asset by GUID: its file name, or the name of the object inside it the file ID points to.</summary>
        public string Asset(string guid, long fileId)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (guid == "0000000000000000e000000000000000" || guid == "0000000000000000f000000000000000") return "Built-in";
            string inner = Inside(guid, fileId);
            if (inner != null) return inner;
            if (!assets.TryGetValue(guid, out string name))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                name = string.IsNullOrEmpty(path) ? "Missing asset" : Path.GetFileName(path);
                assets[guid] = name;
            }
            return name;
        }

        /// <summary>The object with this local file ID inside a prefab or model: "Hips", or "Body (Skinned Mesh Renderer)".</summary>
        public string Inside(string guid, long fileId)
        {
            if (string.IsNullOrEmpty(guid) || fileId == 0) return null;
            if (!inside.TryGetValue(guid, out var objects))
            {
                objects = new Dictionary<long, string>();
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string extension = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path).ToLowerInvariant();
                // Only containers of scene objects: loading every sub-asset of other assets would be slow and pointless.
                if (extension == ".prefab" || extension == ".fbx" || extension == ".blend" || extension == ".obj")
                {
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        if (asset == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long local)) continue;
                        objects[local] = asset is Component component
                            ? component.gameObject.name + " (" + Words(component.GetType().Name) + ")"
                            : asset.name;
                    }
                }
                inside[guid] = objects;
            }
            return objects.TryGetValue(fileId, out string name) ? name : null;
        }

        /// <summary>A value as the Inspector would show it: references by name, vectors in brackets, rotations in degrees.</summary>
        public string Value(string raw, string path, UnityYamlFile file)
        {
            if (raw == null) return null;
            if (raw.StartsWith("{fileID:", StringComparison.Ordinal))
            {
                var map = UnityYaml.Parse("%YAML 1.1\n--- !u!1 &1\nX:\n  v: " + raw).Documents.FirstOrDefault()?.Body["v"];
                var reference = UnitySemanticDiff.Reference(map);
                if (reference.fileId == 0) return "None";
                if (!string.IsNullOrEmpty(reference.guid)) return Asset(reference.guid, reference.fileId);
                return Local(file, reference.fileId);
            }
            if ((path ?? string.Empty).IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) >= 0 && UnitySemanticDiff.TryEuler(raw, out string euler)) return euler;
            var vector = Vector.Match(raw);
            if (vector.Success)
            {
                var values = new List<string>();
                for (int g = 2; g < vector.Groups.Count; g += 2)
                    foreach (Capture capture in vector.Groups[g].Captures) values.Add(Number(capture.Value.Trim()));
                return "(" + string.Join(", ", values) + ")";
            }
            if (raw.Length == 0) return "(empty)";
            return raw.Length > 160 ? raw.Substring(0, 157) + "…" : raw;
        }

        private static string Number(string text)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value.ToString("0.####", CultureInfo.InvariantCulture)
                : text;
        }

        // An object of the same file: a GameObject by name, a component as "Owner (Type)".
        internal static string Local(UnityYamlFile file, long fileId)
        {
            if (file == null || !file.ById.TryGetValue(fileId, out var document)) return "Missing object";
            if (document.ClassId == 1) return document.Body.Text("m_Name") ?? "GameObject";
            long owner = UnitySemanticDiff.Reference(document.Body["m_GameObject"]).fileId;
            string name = owner != 0 ? UnitySemanticDiff.GameObjectName(file, owner) : document.Body.Text("m_Name");
            return (string.IsNullOrEmpty(name) ? "?" : name) + " (" + Words(document.TypeName) + ")";
        }

        internal static string Words(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return Regex.Replace(Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2"), "([A-Z]+)([A-Z][a-z])", "$1 $2").Replace('_', ' ').Trim();
        }
    }
}
