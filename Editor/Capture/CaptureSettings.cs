using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Mixer.Editor.Capture
{
    /// <summary>How the capture camera moves through the scene.</summary>
    internal enum CapturePathKind
    {
        /// <summary>The Scene view's eye, recorded while you fly.</summary>
        SceneView,
        /// <summary>A smooth path through chosen objects and kept views.</summary>
        Waypoints,
        /// <summary>A circle around a target, looking at it.</summary>
        Orbit,
    }

    /// <summary>Post-processing the frames go through: none, the scene's own PPv2 setup, or a test preset.</summary>
    internal enum CapturePost
    {
        None,
        Scene,
        Typical,
        HeavyBloom,
        Grading,
        ChromaticAberration,
    }

    /// <summary>A camera position and rotation (Unity world, the camera looks down its +Z).</summary>
    internal readonly struct CapturePose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public CapturePose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    /// <summary>Everything a capture needs besides its path. The window keeps one and serializes it.</summary>
    [Serializable]
    internal sealed class CaptureSettings
    {
        public int Width = 1920;
        public int Height = 1080;
        public float Fps = 30f;
        /// <summary>Length of waypoint and orbit paths, in seconds (a recording has its own).</summary>
        public float Duration = 8f;
        /// <summary>Every Nth frame is a depth frame (frames N-1, 2N-1, …); 0 = none.</summary>
        public int DepthEvery = 4;
        /// <summary>Vertical field of view in degrees.</summary>
        public float FieldOfView = 60f;
        public float Near = 0.05f;
        public float Far = 1000f;
        public CapturePost Post = CapturePost.None;
        public CapturePathKind Path = CapturePathKind.Orbit;
        /// <summary>Folder that receives one sub-folder per capture; empty = Documents/Orbiters/Raw captures.</summary>
        public string OutputRoot = "";

        public CaptureSettings Clone() => (CaptureSettings)MemberwiseClone();

        public bool IsDepthFrame(int index) => DepthEvery > 0 && (index + 1) % DepthEvery == 0;

        /// <summary>Game time of a frame: frames are spaced evenly at the frame rate from 0.</summary>
        public float TimeOf(int index) => (float)(index / (double)Fps);

        /// <summary>Name written to <c>capture.json</c> (<c>post_profile</c>).</summary>
        public static string PostId(CapturePost post)
        {
            switch (post)
            {
                case CapturePost.Scene: return "scene";
                case CapturePost.Typical: return "typical";
                case CapturePost.HeavyBloom: return "heavy-bloom";
                case CapturePost.Grading: return "grading";
                case CapturePost.ChromaticAberration: return "chromatic-aberration";
                default: return "none";
            }
        }

        public static string PostLabel(CapturePost post)
        {
            switch (post)
            {
                case CapturePost.Scene: return "Scene";
                case CapturePost.Typical: return "Typical";
                case CapturePost.HeavyBloom: return "Bloom";
                case CapturePost.Grading: return "Grading";
                case CapturePost.ChromaticAberration: return "Aberration";
                default: return "None";
            }
        }
    }

    /// <summary>A waypoint: a scene object (its pose is read when the path is built) or a view kept from the Scene view.</summary>
    [Serializable]
    internal sealed class CaptureWaypoint
    {
        public Transform Target;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public string Label;

        public bool IsView => Target == null && !string.IsNullOrEmpty(Label);

        public static CaptureWaypoint Of(Transform target) => new CaptureWaypoint { Target = target, Label = target.name };

        public static CaptureWaypoint View(string label, Vector3 position, Quaternion rotation) =>
            new CaptureWaypoint { Label = label, Position = position, Rotation = rotation };

        /// <summary>False when the waypoint's object was deleted.</summary>
        public bool TryGetPose(out CapturePose pose)
        {
            if (Target != null)
            {
                pose = new CapturePose(Target.position, Target.rotation);
                return true;
            }
            pose = new CapturePose(Position, Rotation);
            return IsView;
        }
    }

    /// <summary>The Scene view's eye, sampled on every editor update while recording.</summary>
    [Serializable]
    internal sealed class SceneViewRecording
    {
        public List<float> Times = new List<float>();
        public List<Vector3> Positions = new List<Vector3>();
        public List<Quaternion> Rotations = new List<Quaternion>();

        public int Count => Times.Count;
        public float Duration => Times.Count > 1 ? Times[Times.Count - 1] - Times[0] : 0f;

        public void Clear()
        {
            Times.Clear();
            Positions.Clear();
            Rotations.Clear();
        }

        /// <summary>Adds a sample; samples arrive in time order, a repeated time replaces the last one.</summary>
        public void Add(float time, Vector3 position, Quaternion rotation)
        {
            int last = Times.Count - 1;
            if (last >= 0 && time <= Times[last])
            {
                Positions[last] = position;
                Rotations[last] = rotation;
                return;
            }
            Times.Add(time);
            Positions.Add(position);
            Rotations.Add(rotation);
        }
    }

    /// <summary>Orbit path settings: a circle at <see cref="Height"/> above the target's centre, looking at it.</summary>
    [Serializable]
    internal sealed class OrbitSettings
    {
        /// <summary>None: the whole scene.</summary>
        public Transform Target;
        public float Radius = 3f;
        public float Height = 0.6f;
        public float Laps = 1f;
    }
}
