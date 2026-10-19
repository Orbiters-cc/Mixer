using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Orbiters.Toolkit.Editor.LiveLink;
using UnityEngine;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The wire between Mixer and its Blender extension: TCP on 127.0.0.1, frames of Toolkit's <see cref="LiveLinkFormat"/>
    /// (magic "MIXR", version 1, type, token, payload). Blender finds the port and token in
    /// <c>%LOCALAPPDATA%\Orbiters\Mixer\unity-&lt;pid&gt;.json</c>. JSON payloads are UTF-8; mesh payloads are binary,
    /// little-endian, described on <see cref="MeshDataCodec"/>. Coordinates travel in Unity's space; Blender converts.
    /// </summary>
    internal static class MixerLinkProtocol
    {
        public const uint Magic = 0x5258494D; // "MIXR"
        public const byte Version = 1;
        public const int MaxPayloadBytes = 64 * 1024 * 1024;
        public const string EndpointKind = "orbiters.mixer.link";

        public enum FrameType : byte
        {
            Hello = 1,          // Blender to Unity: {blender, extension, blendPath, sceneGuid}
            Welcome = 2,        // Unity to Blender: {unity, mixer, projectPath, sceneGuid, sceneName}
            SceneGraph = 3,     // Unity to Blender: the whole scene (SceneGraph JSON)
            SceneDelta = 4,     // either way: {transforms:[{id,p,r,s}], active:[{id,active}], renamed:[{id,name}], removed:[id], reparented:[{id,parent}]}
            MeshData = 5,       // either way: binary mesh (MeshDataCodec)
            Positions = 6,      // either way: binary {id, shape, count, positions} of the welded vertices
            Weights = 7,        // either way: binary {id, channel, isGroup, count, (index, weight)}
            Armature = 8,       // either way: {id, bones:[{id, name, parent, head, tail, matrix}]}
            Claim = 9,          // either way: {id, side}
            Release = 10,       // either way: {id, side}
            Status = 11,        // either way: {id, message, level}
            RenderRequest = 12, // Unity to Blender: {jobId, output, width, height, samples, engine, camera:{p,r,fov}}
            Bye = 13,
            RenderResult = 14,  // Blender to Unity: {jobId, ok, path, error}
            RequestMesh = 15,   // Blender to Unity: {id}: send this mesh's data (again)
        }

        public static readonly LiveLinkFormat Format = new LiveLinkFormat(Magic, Version, MaxPayloadBytes, type => type >= 1 && type <= 15);

        public sealed class Hello { public string blender; public string extension; public string blendPath; public string sceneGuid; }
        public sealed class Welcome { public string unity; public string mixer; public string projectPath; public string sceneGuid; public string sceneName; public int protocolVersion = Version; }
        public sealed class Ownership { public string id; public string side; }
        public sealed class StatusMessage { public string id; public string message; public string level = "info"; }
        public sealed class RequestMesh { public string id; }
        public sealed class RenderResult { public string jobId; public bool ok; public string path; public string error; }

        public sealed class Endpoint
        {
            public string kind = EndpointKind;
            public int protocolVersion = Version;
            public int pid;
            public string host = "127.0.0.1";
            public int port;
            public string token;
            public string projectPath;
            public string sceneGuid;
            public string sceneName;
            public string blendPath;
            public string updatedUtc;
        }

        public static string EndpointFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "Mixer");
        public static string EndpointPath => Path.Combine(EndpointFolder, "unity-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json");

        public static byte[] Json(object value) => LiveLinkJson.Encode(value);
        public static T Read<T>(byte[] payload) => LiveLinkJson.Decode<T>(payload);
    }

    /// <summary>A frame as decoded on the reader thread: the type and either the JSON text or the binary mesh payload.</summary>
    internal sealed class MixerLinkFrame
    {
        public MixerLinkProtocol.FrameType Type;
        public byte[] Payload;
        /// <summary>For mesh frames: the mesh id read ahead, so the newest frame per mesh can replace older ones.</summary>
        public string MeshId;
    }
}
