using System;
using UnityEngine;
using UnityEngine.SceneManagement;
#if MIXER_CAPTURE_PPV2
using UnityEditor;
using UnityEngine.Rendering.PostProcessing;
#endif

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>
    /// Post Processing Stack v2 on the capture camera: the scene's own layer and volumes (as VRChat applies the world's
    /// reference camera), or a test preset in a hidden global volume on a layer nothing else listens to, in the scene the
    /// camera renders. The preset volume is only enabled during the capture camera's own render, so no other camera ever
    /// sees it. Without the package installed every choice renders without post-processing.
    /// </summary>
    internal sealed class CapturePostRig : IDisposable
    {
        /// <summary>What the frames actually got, for the window and the log.</summary>
        public string Note { get; }

        public static bool Available
        {
            get
            {
#if MIXER_CAPTURE_PPV2
                return true;
#else
                return false;
#endif
            }
        }

#if MIXER_CAPTURE_PPV2
        private const string ResourcesPath = "Packages/com.unity.postprocessing/PostProcessing/PostProcessResources.asset";
        private PostProcessVolume volume;

        /// <summary>Adds the layer to <paramref name="cameraObject"/>, which must still be inactive (the layer reads its settings in OnEnable).</summary>
        public CapturePostRig(GameObject cameraObject, CapturePost post, Scene scene)
        {
            if (post == CapturePost.None)
            {
                Note = "No post-processing.";
                return;
            }
            var resources = AssetDatabase.LoadAssetAtPath<PostProcessResources>(ResourcesPath);
            if (post == CapturePost.Scene)
            {
                var reference = CaptureScene.ReferenceCamera(scene);
                var source = reference != null ? reference.GetComponent<PostProcessLayer>() : null;
                if (source == null || !source.enabled)
                {
                    Note = "The scene's camera has no Post-process Layer: frames have no post-processing.";
                    return;
                }
                var sceneLayer = cameraObject.AddComponent<PostProcessLayer>();
                EditorUtility.CopySerialized(source, sceneLayer);
                sceneLayer.volumeTrigger = cameraObject.transform;
                sceneLayer.Init(resources);
                Note = "The post-processing of " + reference.name + " (" + LayerNames(sceneLayer.volumeLayer) + ").";
                return;
            }
            int layerIndex = FreeLayer();
            var layer = cameraObject.AddComponent<PostProcessLayer>();
            layer.Init(resources);
            layer.volumeTrigger = cameraObject.transform;
            layer.volumeLayer = 1 << layerIndex;
            layer.antialiasingMode = post == CapturePost.Typical ? PostProcessLayer.Antialiasing.FastApproximateAntialiasing : PostProcessLayer.Antialiasing.None;
            volume = PostProcessManager.instance.QuickVolume(layerIndex, 100f, Preset(post));
            volume.gameObject.name = CaptureScene.ObjectPrefix + " post volume";
            // In the scene the camera renders: in the editor PPv2 skips volumes outside it (StageUtility), so a preview
            // scene's capture never saw a preset made in the open scene.
            if (volume.gameObject.scene != scene) SceneManager.MoveGameObjectToScene(volume.gameObject, scene);
            volume.gameObject.SetActive(false);
            Note = Describe(post);
        }

        public void BeforeRender()
        {
            if (volume != null) volume.gameObject.SetActive(true);
        }

        public void AfterRender()
        {
            if (volume != null) volume.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            if (volume != null) RuntimeUtilities.DestroyVolume(volume, true, true);
            volume = null;
        }

        private static PostProcessEffectSettings[] Preset(CapturePost post)
        {
            switch (post)
            {
                case CapturePost.Typical:
                {
                    var bloom = Effect<Bloom>();
                    bloom.intensity.Override(1f);
                    bloom.threshold.Override(1f);
                    var grading = Effect<ColorGrading>();
                    grading.tonemapper.Override(Tonemapper.ACES);
                    var vignette = Effect<Vignette>();
                    vignette.intensity.Override(0.3f);
                    return new PostProcessEffectSettings[] { bloom, grading, vignette };
                }
                case CapturePost.Bloom:
                {
                    var bloom = Effect<Bloom>();
                    bloom.intensity.Override(3f);
                    bloom.threshold.Override(1f);
                    return new PostProcessEffectSettings[] { bloom };
                }
                case CapturePost.HeavyBloom:
                {
                    var bloom = Effect<Bloom>();
                    bloom.intensity.Override(10f);
                    bloom.threshold.Override(0.5f);
                    return new PostProcessEffectSettings[] { bloom };
                }
                case CapturePost.Grading:
                {
                    var grading = Effect<ColorGrading>();
                    grading.hueShift.Override(60f);
                    grading.saturation.Override(40f);
                    grading.contrast.Override(40f);
                    grading.mixerRedOutRedIn.Override(80f);
                    grading.mixerRedOutGreenIn.Override(20f);
                    grading.mixerBlueOutGreenIn.Override(15f);
                    return new PostProcessEffectSettings[] { grading };
                }
                default:
                {
                    var aberration = Effect<ChromaticAberration>();
                    aberration.intensity.Override(0.3f);
                    return new PostProcessEffectSettings[] { aberration };
                }
            }
        }

        private static T Effect<T>() where T : PostProcessEffectSettings
        {
            var effect = ScriptableObject.CreateInstance<T>();
            effect.hideFlags = HideFlags.HideAndDontSave;
            effect.enabled.Override(true);
            return effect;
        }

        /// <summary>A layer no Post-process Layer or Volume of the open scenes uses (preferring unnamed ones), from 31 down.</summary>
        private static int FreeLayer()
        {
            int used = 0;
            foreach (var layer in UnityEngine.Object.FindObjectsOfType<PostProcessLayer>()) used |= layer.volumeLayer.value;
            foreach (var volume in UnityEngine.Object.FindObjectsOfType<PostProcessVolume>()) used |= 1 << volume.gameObject.layer;
            for (int pass = 0; pass < 2; pass++)
            for (int i = 31; i >= 8; i--)
                if ((used & (1 << i)) == 0 && (pass == 1 || string.IsNullOrEmpty(LayerMask.LayerToName(i)))) return i;
            return 31;
        }

        private static string LayerNames(LayerMask mask)
        {
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 32; i++)
                if ((mask.value & (1 << i)) != 0) names.Add(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? "layer " + i : LayerMask.LayerToName(i));
            return names.Count == 0 ? "no volume layers" : names.Count > 3 ? names.Count + " volume layers" : string.Join(", ", names);
        }
#else
        public CapturePostRig(GameObject cameraObject, CapturePost post, Scene scene)
        {
            Note = post == CapturePost.None ? "No post-processing." : "Post Processing Stack v2 is not installed: frames have no post-processing.";
        }

        public void BeforeRender() { }

        public void AfterRender() { }

        public void Dispose() { }
#endif

        /// <summary>The scene camera whose Post-process Layer <see cref="CapturePost.Scene"/> copies, or null when there is none.</summary>
        public static string SceneLayerOwner(Scene scene)
        {
#if MIXER_CAPTURE_PPV2
            var reference = CaptureScene.ReferenceCamera(scene);
            var layer = reference != null ? reference.GetComponent<PostProcessLayer>() : null;
            return layer != null && layer.enabled ? reference.name : null;
#else
            return null;
#endif
        }

        public static string Describe(CapturePost post)
        {
            switch (post)
            {
                case CapturePost.Scene: return "The scene's own post-processing: its camera's Post-process Layer and the volumes it listens to.";
                case CapturePost.Typical: return "Typical world: bloom 1, ACES tonemapping, vignette 0.3 and FXAA.";
                case CapturePost.Bloom: return "Bloom on its own, as a bright world sets it: intensity 3 from a threshold of 1.";
                case CapturePost.HeavyBloom: return "Heavy bloom: intensity 10 from a threshold of 0.5.";
                case CapturePost.Grading: return "Wild grading: hue 60, saturation and contrast +40, a channel mixer.";
                case CapturePost.ChromaticAberration: return "Chromatic aberration 0.3: known to break depth frames.";
                default: return "Frames as the scene renders, without post-processing.";
            }
        }
    }
}
