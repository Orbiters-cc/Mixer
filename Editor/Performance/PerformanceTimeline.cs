using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
#if MIXER_PERFORMANCE_TIMELINE
using UnityEngine.Timeline;
#endif

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>A Timeline next to a baked clip: an animation track with the clip and one audio track per sound.</summary>
    internal static class PerformanceTimeline
    {
        /// <summary>Creates the Timeline asset; returns its path (null without the Timeline package).</summary>
        public static string Create(string folder, string avatarName, AnimationClip clip, IReadOnlyList<string> audioPaths)
        {
#if MIXER_PERFORMANCE_TIMELINE
            var asset = ScriptableObject.CreateInstance<TimelineAsset>();
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(clip)) + ".playable");
            AssetDatabase.CreateAsset(asset, path);
            var animation = asset.CreateTrack<AnimationTrack>(null, avatarName);
            animation.CreateClip(clip).start = 0;
            foreach (string audioPath in audioPaths)
            {
                var sound = AssetDatabase.LoadAssetAtPath<AudioClip>(audioPath);
                if (sound == null) continue;
                var track = asset.CreateTrack<AudioTrack>(null, Path.GetFileNameWithoutExtension(audioPath));
                track.CreateClip(sound).start = 0;
            }
            EditorUtility.SetDirty(asset);
            return path;
#else
            return null;
#endif
        }
    }
}
