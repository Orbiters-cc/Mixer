using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Orbiters.UnitGit.Editor.Semantic
{
    internal enum SemanticChangeKind { Added, Removed, Modified }

    internal sealed class SemanticProperty
    {
        public string Path = string.Empty;
        public string Label = string.Empty;
        public string Before;
        public string After;
        public SemanticChangeKind Kind;
    }

    internal sealed class SemanticComponent
    {
        public long FileId;
        public int ClassId;
        public string TypeName = string.Empty;
        // MonoBehaviour script, resolved later: {fileID, guid}.
        public string ScriptGuid;
        public long ScriptFileId;
        public SemanticChangeKind Kind;
        public readonly List<SemanticProperty> Properties = new List<SemanticProperty>();
    }

    /// <summary>A GameObject (or prefab instance) with what changed on it.</summary>
    internal sealed class SemanticObject
    {
        public long FileId;
        public string Name = string.Empty;
        public string Path = string.Empty;
        public SemanticChangeKind Kind;
        public bool PrefabInstance;
        public string PrefabGuid;
        // An object inside a nested prefab: named from that prefab when the file does not say.
        public string SourceGuid;
        public long SourceFileId;
        public readonly List<SemanticComponent> Components = new List<SemanticComponent>();
        // Prefab overrides: (target in the source prefab, property) with their before/after values.
        public readonly List<SemanticOverride> Overrides = new List<SemanticOverride>();

        public int ChangeCount => Components.Sum(c => Math.Max(1, c.Properties.Count)) + Overrides.Count + (Kind == SemanticChangeKind.Modified ? 0 : 1);
    }

    internal sealed class SemanticOverride
    {
        public long TargetFileId;
        public string TargetGuid;
        public string PropertyPath = string.Empty;
        public string Label = string.Empty;
        public string Before;
        public string After;
        public SemanticChangeKind Kind;
    }

    internal sealed class SemanticChangeSet
    {
        public readonly List<SemanticObject> Objects = new List<SemanticObject>();
        public UnityYamlFile Before, After;
        public int Added => Objects.Count(o => o.Kind == SemanticChangeKind.Added);
        public int Removed => Objects.Count(o => o.Kind == SemanticChangeKind.Removed);
        public int Modified => Objects.Count(o => o.Kind == SemanticChangeKind.Modified);
    }

    /// <summary>
    /// Compares two versions of a Unity scene or prefab object by object. Unity keeps each object's file ID across saves,
    /// so objects are matched by it; values are compared field by field, ignoring bookkeeping fields that change on every
    /// save. Names of scripts and other assets are resolved afterwards by <see cref="UnitySemanticNames"/>.
    /// </summary>
    internal static class UnitySemanticDiff
    {
        private const int GameObjectClass = 1, TransformClass = 4, RectTransformClass = 224, PrefabInstanceClass = 1001, MonoBehaviourClass = 114;

        private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "serializedVersion",
            "m_LocalEulerAnglesHint", "m_RootOrder", "m_Component", "m_Children", "m_GameObject", "m_EditorHideFlags",
            "m_EditorClassIdentifier", "m_Script"
        };

        public static SemanticChangeSet Compare(string beforeText, string afterText)
        {
            var before = UnityYaml.Parse(beforeText);
            var after = UnityYaml.Parse(afterText);
            var set = new SemanticChangeSet { Before = before, After = after };
            var objects = new Dictionary<long, SemanticObject>();

            SemanticObject Owner(long gameObject, SemanticChangeKind kindIfNew)
            {
                if (objects.TryGetValue(gameObject, out var existing)) return existing;
                var file = after.ById.ContainsKey(gameObject) ? after : before;
                var created = new SemanticObject { FileId = gameObject, Kind = kindIfNew, Name = GameObjectName(file, gameObject), Path = HierarchyPath(file, gameObject) };
                if (file.ById.TryGetValue(gameObject, out var source) && source.Stripped)
                {
                    var origin = Reference(source.Body["m_CorrespondingSourceObject"]);
                    created.SourceGuid = origin.guid;
                    created.SourceFileId = origin.fileId;
                }
                objects[gameObject] = created;
                return created;
            }

            var ids = new HashSet<long>(before.ById.Keys);
            ids.UnionWith(after.ById.Keys);
            foreach (long id in ids)
            {
                before.ById.TryGetValue(id, out var old);
                after.ById.TryGetValue(id, out var now);
                var document = now ?? old;
                if (document.Stripped) continue;
                var kind = old == null ? SemanticChangeKind.Added : now == null ? SemanticChangeKind.Removed : SemanticChangeKind.Modified;

                if (document.ClassId == PrefabInstanceClass)
                {
                    var instance = Owner(id, kind);
                    instance.PrefabInstance = true;
                    instance.Kind = kind;
                    instance.PrefabGuid = Reference(document.Body["m_SourcePrefab"]).guid;
                    if (string.IsNullOrEmpty(instance.Name) || instance.Name == "?") instance.Name = PrefabInstanceName(document);
                    instance.Path = PrefabInstancePath(after.ById.ContainsKey(id) ? after : before, document);
                    CompareOverrides(old, now, instance);
                    continue;
                }

                var properties = kind == SemanticChangeKind.Modified ? Properties(old.Body, now.Body) : new List<SemanticProperty>();
                if (document.ClassId == GameObjectClass)
                {
                    if (kind == SemanticChangeKind.Modified && properties.Count == 0) continue;
                    var gameObject = Owner(id, kind);
                    gameObject.Kind = kind == SemanticChangeKind.Modified ? gameObject.Kind : kind;
                    if (properties.Count > 0)
                    {
                        var own = new SemanticComponent { FileId = id, ClassId = GameObjectClass, TypeName = "GameObject", Kind = SemanticChangeKind.Modified };
                        own.Properties.AddRange(properties);
                        gameObject.Components.Insert(0, own);
                    }
                    continue;
                }

                long owner = Reference(document.Body["m_GameObject"]).fileId;
                if (kind == SemanticChangeKind.Modified && properties.Count == 0) continue;
                var component = new SemanticComponent { FileId = id, ClassId = document.ClassId, TypeName = document.TypeName, Kind = kind };
                if (document.ClassId == MonoBehaviourClass)
                {
                    var script = Reference(document.Body["m_Script"]);
                    component.ScriptGuid = script.guid;
                    component.ScriptFileId = script.fileId;
                }
                component.Properties.AddRange(properties);
                if (owner == 0)
                {
                    // Assets that are not scene objects (materials, animator states): each document stands on its own.
                    var standalone = Owner(id, kind);
                    standalone.Name = document.Body.Text("m_Name") ?? document.TypeName;
                    if (string.IsNullOrEmpty(standalone.Name)) standalone.Name = document.TypeName;
                    standalone.Path = standalone.Name;
                    standalone.Components.Add(component);
                    continue;
                }
                var parent = Owner(owner, SemanticChangeKind.Modified);
                // A component added or removed with its GameObject is part of that change, not a change of its own.
                if (parent.Kind != SemanticChangeKind.Modified && component.Kind == parent.Kind) component.Properties.Clear();
                parent.Components.Add(component);
            }

            foreach (var item in objects.Values)
            {
                item.Components.Sort((a, b) => Order(a).CompareTo(Order(b)));
                if (item.Kind != SemanticChangeKind.Modified || item.Components.Count > 0 || item.Overrides.Count > 0) set.Objects.Add(item);
            }
            set.Objects.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return set;
        }

        private static int Order(SemanticComponent component)
        {
            if (component.ClassId == GameObjectClass) return 0;
            if (component.ClassId == TransformClass || component.ClassId == RectTransformClass) return 1;
            return 2;
        }

        // ---- Properties ------------------------------------------------------------------------------------------------

        internal static List<SemanticProperty> Properties(YamlMap before, YamlMap after)
        {
            var old = new List<KeyValuePair<string, string>>();
            var now = new List<KeyValuePair<string, string>>();
            Flatten(before, string.Empty, old, 0);
            Flatten(after, string.Empty, now, 0);
            var oldMap = new Dictionary<string, string>();
            foreach (var pair in old) oldMap[pair.Key] = pair.Value;
            var nowMap = new Dictionary<string, string>();
            foreach (var pair in now) nowMap[pair.Key] = pair.Value;
            var result = new List<SemanticProperty>();
            foreach (var pair in now)
            {
                if (oldMap.TryGetValue(pair.Key, out var was))
                {
                    if (was != pair.Value) result.Add(new SemanticProperty { Path = pair.Key, Label = Label(pair.Key), Before = was, After = pair.Value, Kind = SemanticChangeKind.Modified });
                }
                else result.Add(new SemanticProperty { Path = pair.Key, Label = Label(pair.Key), After = pair.Value, Kind = SemanticChangeKind.Added });
            }
            foreach (var pair in old)
                if (!nowMap.ContainsKey(pair.Key))
                    result.Add(new SemanticProperty { Path = pair.Key, Label = Label(pair.Key), Before = pair.Value, Kind = SemanticChangeKind.Removed });
            return result;
        }

        private static void Flatten(YamlNode node, string path, List<KeyValuePair<string, string>> output, int depth)
        {
            if (node is YamlMap map && !map.Flow && depth < 12)
            {
                foreach (var entry in map.Entries)
                {
                    if (depth == 0 && Noise.Contains(entry.Key)) continue;
                    Flatten(entry.Value, path.Length == 0 ? entry.Key : path + "." + entry.Key, output, depth + 1);
                }
                return;
            }
            if (node is YamlSequence sequence && !sequence.Flow && depth < 12)
            {
                if (sequence.Items.Count == 0) { output.Add(new KeyValuePair<string, string>(path, "[]")); return; }
                for (int i = 0; i < sequence.Items.Count; i++) Flatten(sequence.Items[i], path + "[" + i + "]", output, depth + 1);
                return;
            }
            output.Add(new KeyValuePair<string, string>(path, Canonical(node)));
        }

        /// <summary>One line for a value: inline maps as "{key: value, ...}", sequences as "[a, b]".</summary>
        internal static string Canonical(YamlNode node)
        {
            if (node is YamlScalar scalar) return scalar.Value;
            var builder = new StringBuilder();
            if (node is YamlMap map)
            {
                builder.Append('{');
                for (int i = 0; i < map.Entries.Count; i++)
                {
                    if (i > 0) builder.Append(", ");
                    builder.Append(map.Entries[i].Key).Append(": ").Append(Canonical(map.Entries[i].Value));
                }
                builder.Append('}');
            }
            else if (node is YamlSequence sequence)
            {
                builder.Append('[');
                for (int i = 0; i < sequence.Items.Count; i++)
                {
                    if (i > 0) builder.Append(", ");
                    builder.Append(Canonical(sequence.Items[i]));
                }
                builder.Append(']');
            }
            return builder.ToString();
        }

        // ---- Prefab overrides ------------------------------------------------------------------------------------------

        private static void CompareOverrides(UnityYamlDocument old, UnityYamlDocument now, SemanticObject instance)
        {
            var before = Modifications(old);
            var after = Modifications(now);
            foreach (var pair in after)
            {
                before.TryGetValue(pair.Key, out var was);
                if (was == pair.Value) continue;
                instance.Overrides.Add(Override(pair.Key, was, pair.Value, was == null ? SemanticChangeKind.Added : SemanticChangeKind.Modified));
            }
            foreach (var pair in before)
                if (!after.ContainsKey(pair.Key))
                    instance.Overrides.Add(Override(pair.Key, pair.Value, null, SemanticChangeKind.Removed));
            // The rest of the instance (parent, removed components...) as ordinary properties.
            if (old != null && now != null)
            {
                var other = Properties(Without(old.Body), Without(now.Body));
                if (other.Count > 0)
                {
                    var own = new SemanticComponent { FileId = now.FileId, ClassId = PrefabInstanceClass, TypeName = "PrefabInstance", Kind = SemanticChangeKind.Modified };
                    own.Properties.AddRange(other);
                    instance.Components.Add(own);
                }
            }
        }

        private static YamlMap Without(YamlMap body)
        {
            var copy = new YamlMap();
            foreach (var entry in body.Entries)
            {
                if (entry.Key == "m_Modification" && entry.Value is YamlMap modification)
                {
                    var rest = new YamlMap();
                    foreach (var inner in modification.Entries) if (inner.Key != "m_Modifications") rest.Entries.Add(inner);
                    copy.Entries.Add(new KeyValuePair<string, YamlNode>(entry.Key, rest));
                }
                else copy.Entries.Add(entry);
            }
            return copy;
        }

        private static SemanticOverride Override(string key, string before, string after, SemanticChangeKind kind)
        {
            var parts = key.Split('|');
            return new SemanticOverride
            {
                TargetFileId = long.Parse(parts[0], CultureInfo.InvariantCulture),
                TargetGuid = parts[1],
                PropertyPath = parts[2],
                Label = Label(parts[2]),
                Before = before,
                After = after,
                Kind = kind
            };
        }

        // Key "fileId|guid|propertyPath" → the overridden value (or object reference).
        private static Dictionary<string, string> Modifications(UnityYamlDocument document)
        {
            var result = new Dictionary<string, string>();
            if (document == null) return result;
            var list = (document.Body["m_Modification"] as YamlMap)?["m_Modifications"] as YamlSequence;
            if (list == null) return result;
            foreach (var item in list.Items.OfType<YamlMap>())
            {
                var target = Reference(item["target"]);
                string property = item.Text("propertyPath") ?? string.Empty;
                var reference = Reference(item["objectReference"]);
                string value = reference.fileId != 0 ? Canonical(item["objectReference"]) : item.Text("value") ?? string.Empty;
                result[target.fileId.ToString(CultureInfo.InvariantCulture) + "|" + target.guid + "|" + property] = value;
            }
            return result;
        }

        private static string PrefabInstanceName(UnityYamlDocument document)
        {
            var list = (document.Body["m_Modification"] as YamlMap)?["m_Modifications"] as YamlSequence;
            if (list != null)
                foreach (var item in list.Items.OfType<YamlMap>())
                    if (item.Text("propertyPath") == "m_Name") return item.Text("value");
            return string.Empty;
        }

        private static string PrefabInstancePath(UnityYamlFile file, UnityYamlDocument document)
        {
            long parent = Reference((document.Body["m_Modification"] as YamlMap)?["m_TransformParent"]).fileId;
            string name = PrefabInstanceName(document);
            if (string.IsNullOrEmpty(name)) name = "Prefab";
            string parentPath = parent != 0 ? TransformPath(file, parent) : string.Empty;
            return string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
        }

        // ---- Hierarchy ----------------------------------------------------------------------------------------------------

        internal static string GameObjectName(UnityYamlFile file, long gameObject)
        {
            if (!file.ById.TryGetValue(gameObject, out var document)) return "?";
            if (!document.Stripped) return document.Body.Text("m_Name") ?? "?";
            // An object of a nested prefab: named by a name override of that instance, if there is one.
            long source = Reference(document.Body["m_CorrespondingSourceObject"]).fileId;
            long instance = Reference(document.Body["m_PrefabInstance"]).fileId;
            if (file.ById.TryGetValue(instance, out var prefab))
            {
                var list = (prefab.Body["m_Modification"] as YamlMap)?["m_Modifications"] as YamlSequence;
                if (list != null)
                    foreach (var item in list.Items.OfType<YamlMap>())
                        if (item.Text("propertyPath") == "m_Name" && Reference(item["target"]).fileId == source) return item.Text("value");
            }
            return "?";
        }

        internal static string HierarchyPath(UnityYamlFile file, long gameObject)
        {
            if (file.ById.TryGetValue(gameObject, out var stripped) && stripped.Stripped)
            {
                long instance = Reference(stripped.Body["m_PrefabInstance"]).fileId;
                string within = file.ById.TryGetValue(instance, out var prefab) ? PrefabInstancePath(file, prefab) : string.Empty;
                string own = GameObjectName(file, gameObject);
                return own == "?" || within.EndsWith("/" + own, StringComparison.Ordinal) || within == own ? within : within + "/" + own;
            }
            if (file.TransformOf == null)
            {
                file.TransformOf = new Dictionary<long, long>();
                foreach (var d in file.Documents)
                    if ((d.ClassId == TransformClass || d.ClassId == RectTransformClass) && !d.Stripped)
                        file.TransformOf[Reference(d.Body["m_GameObject"]).fileId] = d.FileId;
            }
            return file.TransformOf.TryGetValue(gameObject, out long transform) ? TransformPath(file, transform) : GameObjectName(file, gameObject);
        }

        private static string TransformPath(UnityYamlFile file, long transform)
        {
            var names = new List<string>();
            for (int guard = 0; transform != 0 && guard < 256 && file.ById.TryGetValue(transform, out var document); guard++)
            {
                if (document.Stripped)
                {
                    // The root of a nested prefab instance: named by that instance's overrides.
                    long instance = Reference(document.Body["m_PrefabInstance"]).fileId;
                    string name = file.ById.TryGetValue(instance, out var prefab) ? PrefabInstanceName(prefab) : string.Empty;
                    names.Add(string.IsNullOrEmpty(name) ? "Prefab" : name);
                    transform = file.ById.TryGetValue(instance, out prefab) ? Reference((prefab.Body["m_Modification"] as YamlMap)?["m_TransformParent"]).fileId : 0;
                    continue;
                }
                names.Add(GameObjectName(file, Reference(document.Body["m_GameObject"]).fileId));
                transform = Reference(document.Body["m_Father"]).fileId;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        internal struct Ref
        {
            public long fileId;
            public string guid;
        }

        internal static Ref Reference(YamlNode node)
        {
            var map = node as YamlMap;
            if (map == null) return new Ref { guid = string.Empty };
            long.TryParse(map.Text("fileID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileId);
            return new Ref { fileId = fileId, guid = map.Text("guid") ?? string.Empty };
        }

        /// <summary>"m_LocalPosition" → "Local Position", "m_Materials.Array.data[0]" → "Materials [0]".</summary>
        internal static string Label(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            // [SerializeReference] data (VRCFury features): the reference table is bookkeeping, the field names are the setting.
            string cleaned = System.Text.RegularExpressions.Regex.Replace(path, @"^references\.RefIds\[\d+\]\.data\.", string.Empty)
                .Replace(".Array.data[", "[").Replace(".Array.size", " count");
            var parts = cleaned.Split('.');
            var words = new List<string>();
            foreach (string part in parts)
            {
                string name = part;
                if (name.StartsWith("m_", StringComparison.Ordinal)) name = name.Substring(2);
                words.Add(Words(name));
            }
            return string.Join(" › ", words);
        }

        private static string Words(string name)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_') { builder.Append(' '); continue; }
                bool boundary = i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
                if (boundary && builder.Length > 0 && builder[builder.Length - 1] != ' ') builder.Append(' ');
                builder.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
            return builder.ToString().Replace("[", " [").Replace("  ", " ").Trim();
        }

        /// <summary>A quaternion "{x: .., y: .., z: .., w: ..}" as Euler degrees, the way the Inspector shows rotations.</summary>
        internal static bool TryEuler(string value, out string euler)
        {
            euler = null;
            if (value == null || !value.StartsWith("{", StringComparison.Ordinal) || value.IndexOf("w:", StringComparison.Ordinal) < 0) return false;
            var map = UnityYaml.Parse("%YAML 1.1\n--- !u!1 &1\nX:\n  v: " + value).Documents.FirstOrDefault()?.Body["v"] as YamlMap;
            if (map == null) return false;
            float F(string key) => float.TryParse(map.Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : float.NaN;
            var q = new Quaternion(F("x"), F("y"), F("z"), F("w"));
            if (float.IsNaN(q.x) || float.IsNaN(q.w)) return false;
            var e = q.eulerAngles;
            string D(float d) { d = Mathf.Repeat(d + 180f, 360f) - 180f; return (Mathf.Abs(d) < 0.005f ? 0f : d).ToString("0.##", CultureInfo.InvariantCulture) + "°"; }
            euler = "(" + D(e.x) + ", " + D(e.y) + ", " + D(e.z) + ")";
            return true;
        }
    }
}
