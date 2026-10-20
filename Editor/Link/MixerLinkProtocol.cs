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
    /// (magic "MIXR", frame version 2, type, token, payload). Blender finds the port and token in
    /// <c>%LOCALAPPDATA%\Orbiters\Mixer\unity-&lt;pid&gt;.json</c>. JSON payloads are UTF-8; mesh payloads are binary,
    /// little-endian, described on <see cref="MeshDataCodec"/>. Coordinates travel in Unity's space; Blender converts.
    /// <para>
    /// The frame layout stays at version 2 so that a peer of another link protocol can still be read and told so: the
    /// protocol both sides speak is <see cref="ProtocolVersion"/>, in the endpoint file, Hello and Welcome. Unity answers
    /// a Hello of another protocol with a Welcome that says why and sends nothing else; Blender does not connect to an
    /// endpoint of another protocol.
    /// </para>
    /// <para>
    /// Protocol 3: claims name the mesh; positions and weights frames carry the topology they were made for; mesh payloads
    /// end with a Link section (topology, asset key); meshes larger than a frame travel as <see cref="FrameType.MeshChunk"/>
    /// frames; Blender acknowledges every mesh (<see cref="FrameType.MeshReceived"/>) before Unity sends the next.
    /// </para>
    /// </summary>
    internal static class MixerLinkProtocol
    {
        public const uint Magic = 0x5258494D; // "MIXR"
        /// <summary>The frame layout (the header's version byte).</summary>
        public const byte FrameVersion = 2;
        /// <summary>The link protocol both sides must speak.</summary>
        public const int ProtocolVersion = 3;
        public const int MaxPayloadBytes = 64 * 1024 * 1024;
        /// <summary>A mesh payload larger than one frame goes in chunks of this size.</summary>
        public const int ChunkBytes = 16 * 1024 * 1024;
        /// <summary>
        /// The largest mesh payload the link carries (a 48k-vertex body with 497 shapes is about 40 MB). It stays below
        /// the live link server's cap on bytes waiting to be written, as one mesh is in flight at a time.
        /// </summary>
        public const int MaxMeshBytes = 384 * 1024 * 1024;
        public const string EndpointKind = "orbiters.mixer.link";

        public enum FrameType : byte
        {
            Hello = 1,          // Blender to Unity: {blender, extension, blendPath, sceneGuid, protocolVersion, meshes:{meshId: key}}
            Welcome = 2,        // Unity to Blender: {unity, mixer, projectPath, sceneGuid, sceneName, protocolVersion, error}
            SceneGraph = 3,     // Unity to Blender: the whole scene (SceneGraph JSON)
            SceneDelta = 4,     // either way: {transforms, active, renamed, removed, reparented, added, changed, materials}
            MeshData = 5,       // either way: binary mesh (MeshDataCodec)
            Positions = 6,      // either way: binary {id, shape, topology, count, positions} of the welded vertices
            Weights = 7,        // either way: binary {id, channel, isGroup, bone, topology, count, (index, weight)}
            Armature = 8,       // either way: {id, bones:[{id, name, parent, head, tail, matrix}]}
            Claim = 9,          // either way: {id, meshId, side}: that side edits the mesh now
            Release = 10,       // either way: {id, meshId, side, reason}: that side stopped (from Unity with side "blender": Blender's claim is refused or ended)
            Status = 11,        // either way: {id, message, level}
            RenderRequest = 12, // Unity to Blender: {jobId, output, width, height, samples, engine, camera:{p,r,fov}}
            Bye = 13,
            RenderResult = 14,  // Blender to Unity: {jobId, ok, path, error}
            RequestMesh = 15,   // Blender to Unity: {id}: send this node's mesh (again)
            MeshChunk = 16,     // either way: binary {u32 transfer, u32 index, u32 count, u32 total bytes, bytes} of one mesh payload
            MeshReceived = 17,  // Blender to Unity: {id}: the mesh arrived (Unity sends the next one)
        }

        public static readonly LiveLinkFormat Format = new LiveLinkFormat(Magic, FrameVersion, MaxPayloadBytes, type => type >= 1 && type <= 17);

        public sealed class Hello
        {
            public string blender; public string extension; public string blendPath; public string sceneGuid; public int protocolVersion;
            /// <summary>Unity's asset meshes Blender holds from before, with the key Unity sent them with (a mesh whose key still matches is not sent again).</summary>
            public Dictionary<string, string> meshes;
        }

        public sealed class Welcome
        {
            public string unity; public string mixer; public string projectPath; public string sceneGuid; public string sceneName;
            public int protocolVersion = ProtocolVersion;
            /// <summary>Why Unity does not link with this Blender (another protocol), or null.</summary>
            public string error;
        }

        public sealed class Ownership { public string id; public string meshId; public string side; public string reason; }
        public sealed class StatusMessage { public string id; public string message; public string level = "info"; }
        public sealed class RequestMesh { public string id; }
        public sealed class MeshReceived { public string id; }
        public sealed class RenderResult { public string jobId; public bool ok; public string path; public string error; }

        public sealed class Endpoint
        {
            public string kind = EndpointKind;
            public int protocolVersion = ProtocolVersion;
            public int pid;
            public string host = "127.0.0.1";
            public int port;
            public string token;
            /// <summary>The server answers a proof request (Toolkit's live link proof): Blender checks Unity holds the token before it sends anything.</summary>
            public bool proof;
            public string projectPath;
            public string sceneGuid;
            public string sceneName;
            public string blendPath;
            public string updatedUtc;
        }

        public static string EndpointFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "Mixer");
        public static string EndpointPath => Path.Combine(EndpointFolder, "unity-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json");

        /// <summary>Whether the Toolkit's live link server answers proof requests (Blender asks before it trusts the endpoint).</summary>
        public static bool ServerProvesToken => typeof(LiveLinkFormat).GetMethod("Proof", new[] { typeof(string), typeof(int), typeof(byte[]) }) != null;

        public static byte[] Json(object value) => LiveLinkJson.Encode(value);
        public static T Read<T>(byte[] payload) => LiveLinkJson.Decode<T>(payload);

        // ---- Chunks --------------------------------------------------------------------------------------------------

        public const int ChunkHeaderBytes = 16;

        /// <summary>A mesh payload as MeshChunk payloads of at most <see cref="ChunkBytes"/> bytes each.</summary>
        public static IEnumerable<byte[]> Chunks(byte[] payload, uint transfer, int chunkBytes = ChunkBytes)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (chunkBytes <= 0 || chunkBytes > MaxPayloadBytes - ChunkHeaderBytes) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            if (payload.Length > MaxMeshBytes) throw new InvalidDataException("A mesh of " + Megabytes(payload.Length) + " is more than the link carries (" + Megabytes(MaxMeshBytes) + ").");
            int count = Math.Max(1, (payload.Length + chunkBytes - 1) / chunkBytes);
            for (int i = 0; i < count; i++)
            {
                int start = i * chunkBytes;
                int length = Math.Min(chunkBytes, payload.Length - start);
                var chunk = new byte[ChunkHeaderBytes + length];
                BitConverter.GetBytes(transfer).CopyTo(chunk, 0);
                BitConverter.GetBytes((uint)i).CopyTo(chunk, 4);
                BitConverter.GetBytes((uint)count).CopyTo(chunk, 8);
                BitConverter.GetBytes((uint)payload.Length).CopyTo(chunk, 12);
                Buffer.BlockCopy(payload, start, chunk, ChunkHeaderBytes, length);
                yield return chunk;
            }
        }

        public static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// Puts a mesh payload back together from its MeshChunk frames, which come in order on one connection (other frames
    /// may come between them). A chunk that does not follow the one before drops the transfer.
    /// </summary>
    internal sealed class MeshChunkAssembler
    {
        private uint transfer;
        private int next, count, total, filled;
        private byte[] buffer;

        public bool Busy => buffer != null;

        public void Reset() { buffer = null; next = count = total = filled = 0; }

        /// <summary>Adds one chunk; the whole payload once its last chunk came, else null. Throws on a chunk that does not fit.</summary>
        public byte[] Add(byte[] chunk)
        {
            if (chunk == null || chunk.Length < MixerLinkProtocol.ChunkHeaderBytes) { Reset(); throw new InvalidDataException("A mesh chunk is too short."); }
            uint id = BitConverter.ToUInt32(chunk, 0);
            uint index = BitConverter.ToUInt32(chunk, 4);
            uint chunks = BitConverter.ToUInt32(chunk, 8);
            uint length = BitConverter.ToUInt32(chunk, 12);
            int data = chunk.Length - MixerLinkProtocol.ChunkHeaderBytes;
            if (index == 0)
            {
                if (chunks == 0 || length > MixerLinkProtocol.MaxMeshBytes) { Reset(); throw new InvalidDataException("A mesh of " + MixerLinkProtocol.Megabytes(length) + " is more than the link carries (" + MixerLinkProtocol.Megabytes(MixerLinkProtocol.MaxMeshBytes) + ")."); }
                Reset();
                transfer = id;
                count = (int)chunks;
                total = (int)length;
                buffer = new byte[total];
            }
            else if (buffer == null || id != transfer || index != next || chunks != count || length != total)
            {
                Reset();
                throw new InvalidDataException("A mesh chunk came out of order: the mesh it belongs to is dropped.");
            }
            if (filled + data > total) { Reset(); throw new InvalidDataException("A mesh's chunks hold more than it announced."); }
            Buffer.BlockCopy(chunk, MixerLinkProtocol.ChunkHeaderBytes, buffer, filled, data);
            filled += data;
            next = (int)index + 1;
            if (next < count) return null;
            if (filled != total) { Reset(); throw new InvalidDataException("A mesh's chunks hold less than it announced."); }
            var whole = buffer;
            Reset();
            return whole;
        }
    }

    /// <summary>A frame as decoded on the reader thread: the type and either the JSON text or the binary mesh payload.</summary>
    internal sealed class MixerLinkFrame
    {
        public MixerLinkProtocol.FrameType Type;
        public byte[] Payload;
        /// <summary>For mesh frames: the mesh id read ahead, so the newest frame per mesh can replace older ones.</summary>
        public string MeshId;
        /// <summary>For positions frames: the shape they are of (null for the base).</summary>
        public string Shape;
    }
}
