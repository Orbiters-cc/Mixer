using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Where things are used: an asset in the open scenes (the components referencing it, directly or through another
    /// asset: a material using a texture, an animator controller playing a clip) and in the project (the assets using it,
    /// the animator controllers first, with the layers and states that play a clip); a blendshape of a skinned mesh (the
    /// animations animating it on that mesh, the controllers playing them, and what other tools register through
    /// <see cref="MixerLink.BlendshapeUses"/>: MCB's corrective shapes, VRCFury's BlendShape Links).
    /// </summary>
    internal static class AssetUsages
    {
        public sealed class Use
        {
            /// <summary>The section it is listed in ("In the scene", "Animators"...).</summary>
            public string Group;
            public string Title;
            public string Detail;
            /// <summary>What a click selects: a scene object, a component's object or an asset.</summary>
            public Object Target;
            public Texture Icon;
        }

        public const string InScene = "In the scene";
        public const string Animators = "Animators";
        public const string Animations = "Animations";
        public const string InProject = "In the project";

        private static readonly string[] ProjectFilters =
        {
            "t:AnimatorController", "t:AnimatorOverrideController", "t:Prefab", "t:Material", "t:AnimationClip", "t:ScriptableObject", "t:Scene",
        };

        // ---- An asset ---------------------------------------------------------------------------------------------------

        /// <summary>Every place <paramref name="assetPath"/> is used: in the open scenes, then in the project.</summary>
        public static List<Use> Of(string assetPath)
        {
            var uses = InScenes(assetPath);
            uses.AddRange(InTheProject(assetPath));
            return uses;
        }

        /// <summary>
        /// The components of the open scenes referencing the asset (or one of its sub-assets: a mesh of a model), directly
        /// or through another asset they reference (its material, its controller).
        /// </summary>
        public static List<Use> InScenes(string assetPath)
        {
            var uses = new List<Use>();
            if (string.IsNullOrEmpty(assetPath)) return uses;
            var ids = new HashSet<int>(AssetDatabase.LoadAllAssetsAtPath(assetPath).Where(o => o != null).Select(o => o.GetInstanceID()));
            if (ids.Count == 0) return uses;
            // Which referenced assets use it in turn (a material its texture, a controller its clip), asked once each.
            var through = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool Uses(string other)
            {
                if (string.IsNullOrEmpty(other) || string.Equals(other, assetPath, StringComparison.OrdinalIgnoreCase)) return false;
                if (!through.TryGetValue(other, out bool result))
                    through[other] = result = AssetDatabase.GetDependencies(other, true).Contains(assetPath, StringComparer.OrdinalIgnoreCase);
                return result;
            }
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    // Unity's own walk of what an object uses, everything below included: most roots never use the asset.
                    if (!EditorUtility.CollectDependencies(new Object[] { root }).Any(d => d != null && ids.Contains(d.GetInstanceID()))) continue;
                    foreach (var component in root.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null || component is Transform) continue;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        using (var serialized = new SerializedObject(component))
                        {
                            var property = serialized.GetIterator();
                            while (property.Next(true))
                            {
                                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                                var value = property.objectReferenceValue;
                                if (value == null) continue;
                                string via = null;
                                if (!ids.Contains(value.GetInstanceID()))
                                {
                                    string other = AssetDatabase.GetAssetPath(value);
                                    if (!Uses(other)) continue;
                                    via = value.name;
                                }
                                string field = Field(property);
                                if (!seen.Add(field + via)) continue;
                                uses.Add(new Use
                                {
                                    Group = InScene,
                                    Title = PathOf(component.transform),
                                    Detail = ObjectNames.NicifyVariableName(component.GetType().Name) + " · " + field + (via != null ? " (through " + via + ")" : ""),
                                    Target = component.gameObject,
                                    Icon = AssetPreview.GetMiniThumbnail(component),
                                });
                            }
                        }
                    }
                }
            }
            return uses;
        }

        /// <summary>The project's assets using <paramref name="assetPath"/> directly: animator controllers first.</summary>
        public static List<Use> InTheProject(string assetPath)
        {
            var uses = new List<Use>();
            if (string.IsNullOrEmpty(assetPath)) return uses;
            var users = ProjectReferences.DirectUsers(new[] { assetPath }, ProjectFilters);
            if (!users.TryGetValue(assetPath, out var list)) return uses;
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            foreach (string user in list.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var type = AssetDatabase.GetMainAssetTypeAtPath(user);
                var asset = AssetDatabase.LoadMainAssetAtPath(user);
                bool animator = typeof(RuntimeAnimatorController).IsAssignableFrom(type);
                string detail = animator && clip != null ? StatesPlaying(asset as AnimatorController, clip) : ObjectNames.NicifyVariableName(type != null ? type.Name : "Asset");
                uses.Add(new Use { Group = animator ? Animators : InProject, Title = System.IO.Path.GetFileNameWithoutExtension(user), Detail = detail, Target = asset, Icon = AssetDatabase.GetCachedIcon(user) });
            }
            uses.Sort((a, b) => a.Group == b.Group ? string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase) : a.Group == Animators ? -1 : 1);
            return uses;
        }

        /// <summary>Where a controller plays a clip: "Layer › State", the first few.</summary>
        public static string StatesPlaying(AnimatorController controller, AnimationClip clip)
        {
            if (controller == null || clip == null) return "Animator controller";
            var places = new List<string>();
            foreach (var layer in controller.layers)
                if (layer.stateMachine != null) Walk(layer.stateMachine, layer.name, clip, places);
            if (places.Count == 0) return "Animator controller";
            return places.Count <= 3 ? string.Join(", ", places) : string.Join(", ", places.Take(3)) + " and " + (places.Count - 3) + " more";
        }

        private static void Walk(AnimatorStateMachine machine, string path, AnimationClip clip, List<string> places)
        {
            foreach (var child in machine.states)
                if (child.state != null && Plays(child.state.motion, clip)) places.Add(path + " › " + child.state.name);
            foreach (var sub in machine.stateMachines)
                if (sub.stateMachine != null) Walk(sub.stateMachine, path + " › " + sub.stateMachine.name, clip, places);
        }

        private static bool Plays(Motion motion, AnimationClip clip)
        {
            if (motion == null) return false;
            if (motion == clip) return true;
            if (motion is BlendTree tree) foreach (var child in tree.children) if (Plays(child.motion, clip)) return true;
            return false;
        }

        // ---- A blendshape -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Where a blendshape of <paramref name="renderer"/> is used: the animations animating it on that renderer (a
        /// binding at its path from one of the animators above it), the controllers playing those, and the sources other
        /// tools register. <paramref name="complete"/> is false while the project's content index is still being read.
        /// </summary>
        public static List<Use> OfBlendshape(SkinnedMeshRenderer renderer, string shape, out bool complete)
        {
            var uses = new List<Use>();
            complete = !AssetContentIndex.Building;
            if (renderer == null || string.IsNullOrEmpty(shape)) return uses;
            string property = "blendShape." + shape;
            // Paths the renderer has from each object an animator could stand on (any ancestor: an Animator, the avatar root).
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (var t = renderer.transform; t != null; t = t.parent) paths.Add(AnimationUtility.CalculateTransformPath(renderer.transform, t));
            var clips = new List<string>();
            foreach (string clipPath in AssetContentIndex.WithFact("attribute", property, ".anim"))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (clip == null) continue;
                var binding = AnimationUtility.GetCurveBindings(clip).FirstOrDefault(b => b.propertyName == property && b.type == typeof(SkinnedMeshRenderer) && paths.Contains(b.path));
                if (binding.propertyName == null) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                string values = curve != null && curve.length > 0 ? " to " + string.Join(", ", curve.keys.Select(k => k.value).Distinct().Take(3).Select(v => v.ToString("0.#"))) : "";
                clips.Add(clipPath);
                uses.Add(new Use { Group = Animations, Title = clip.name, Detail = "Animates \"" + binding.path + "\"" + values + " · " + System.IO.Path.GetDirectoryName(clipPath)?.Replace('\\', '/'), Target = clip, Icon = AssetDatabase.GetCachedIcon(clipPath) });
            }
            if (clips.Count > 0)
            {
                var users = ProjectReferences.DirectUsers(clips, "t:AnimatorController", "t:AnimatorOverrideController");
                var avatar = renderer.transform.root.gameObject;
                var onAvatar = new HashSet<string>(EditorUtility.CollectDependencies(new Object[] { avatar }).OfType<RuntimeAnimatorController>().Select(AssetDatabase.GetAssetPath), StringComparer.OrdinalIgnoreCase);
                foreach (var controllerPath in users.SelectMany(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var controller = AssetDatabase.LoadMainAssetAtPath(controllerPath);
                    var playing = clips.Where(c => users[c].Contains(controllerPath, StringComparer.OrdinalIgnoreCase)).Select(c => AssetDatabase.LoadAssetAtPath<AnimationClip>(c)).ToList();
                    string states = string.Join(", ", playing.Select(c => StatesPlaying(controller as AnimatorController, c)).Where(s => s != "Animator controller").Distinct());
                    uses.Add(new Use
                    {
                        Group = Animators,
                        Title = controller != null ? controller.name : System.IO.Path.GetFileNameWithoutExtension(controllerPath),
                        Detail = (onAvatar.Contains(controllerPath) ? "On this avatar" : "Not on this avatar") + (states.Length > 0 ? " · " + states : ""),
                        Target = controller,
                        Icon = AssetDatabase.GetCachedIcon(controllerPath),
                    });
                }
            }
            foreach (var external in MixerLink.ExternalBlendshapeUses(renderer, shape))
                uses.Add(new Use { Group = external.Group, Title = external.Title, Detail = external.Detail, Target = external.Target, Icon = external.Target != null ? AssetPreview.GetMiniThumbnail(external.Target) : null });
            return uses;
        }

        // ---- Helpers ------------------------------------------------------------------------------------------------------

        // The property's name as a person reads it: "Materials" for m_Materials.Array.data[0], "Base Animation Layers › Animator Controller".
        private static string Field(SerializedProperty property)
        {
            string path = property.propertyPath.Replace(".Array.data[", "[");
            var parts = path.Split('.').Select(p =>
            {
                int bracket = p.IndexOf('[');
                string name = bracket >= 0 ? p.Substring(0, bracket) : p;
                return ObjectNames.NicifyVariableName(name);
            }).Where(p => p.Length > 0).Distinct();
            return string.Join(" › ", parts);
        }

        public static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
