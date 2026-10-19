using System;
using System.Collections.Generic;
using Orbiters.Contracts;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.Mixer.Editor.Capture.Tests
{
    /// <summary>
    /// A preview scene of the tests' own: a floor, three boxes at known places and a light. Captures render it with
    /// <c>camera.scene</c> set to it, so the user's open scene is never drawn, read or touched. Objects are created
    /// hidden and unsaved (the way the rig makes its own), so the open scene never sees them either.
    /// </summary>
    internal sealed class CaptureTestScene : IDisposable
    {
        public readonly Scene Scene;
        public readonly GameObject Root;
        /// <summary>Front face of the middle box (facing -Z), at this z.</summary>
        public const float FrontFaceZ = -0.5f;
        private readonly List<Object> owned = new List<Object>();

        public CaptureTestScene()
        {
            Scene = EditorSceneManager.NewPreviewScene();
            Root = EditorUtility.CreateGameObjectWithHideFlags("CaptureTestScene", HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene(Root, Scene);
            var material = new Material(Shader.Find("Standard")) { color = new Color(0.8f, 0.55f, 0.3f), hideFlags = HideFlags.HideAndDontSave };
            owned.Add(material);
            var floorMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.35f, 0.4f, 0.45f), hideFlags = HideFlags.HideAndDontSave };
            owned.Add(floorMaterial);
            Box("Floor", new Vector3(0, -0.05f, 0), new Vector3(20, 0.1f, 20), floorMaterial);
            Box("Middle", new Vector3(0, 0.5f, 0), Vector3.one, material);
            Box("Left", new Vector3(-2.5f, 0.75f, 1.5f), new Vector3(1, 1.5f, 1), material);
            Box("Right", new Vector3(2.2f, 0.4f, -1f), new Vector3(0.8f, 0.8f, 0.8f), material);
            var light = EditorUtility.CreateGameObjectWithHideFlags("Light", HideFlags.HideAndDontSave, typeof(Light)).GetComponent<Light>();
            light.type = LightType.Directional;
            light.transform.SetParent(Root.transform, false);
            light.transform.rotation = Quaternion.Euler(50, -30, 0);
        }

        private void Box(string name, Vector3 position, Vector3 size, Material material)
        {
            var box = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave, typeof(MeshFilter), typeof(MeshRenderer));
            box.transform.SetParent(Root.transform, false);
            box.transform.localPosition = position;
            box.transform.localScale = size;
            box.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        public static CaptureSettings Settings(int width, int height, int depthEvery = 4) => new CaptureSettings
        {
            Width = width, Height = height, Fps = 30f, DepthEvery = depthEvery, FieldOfView = 60f, Near = 0.05f, Far = 1000f, Post = CapturePost.None,
        };

        /// <summary>Top-down image of a frame (as the writer stores it).</summary>
        public static ImageBuffer TopDown(CaptureFrame frame) => ImageBuffer.FromBottomUp(frame.Width, frame.Height, frame.Colour);

        /// <summary>True depth at a top-down pixel.</summary>
        public static float DepthAt(CaptureFrame frame, int x, int y) => frame.Depth[(frame.Height - 1 - y) * frame.Width + x];

        public void Dispose()
        {
            if (Root != null) Object.DestroyImmediate(Root);
            EditorSceneManager.ClosePreviewScene(Scene);
            foreach (var o in owned)
                if (o != null) Object.DestroyImmediate(o);
        }
    }
}
