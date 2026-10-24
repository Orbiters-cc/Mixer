using System;
using System.IO;
using Orbiters.Contracts;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// A recorded audio track (32-bit float WAVE, contracts <c>audio.json</c>) placed on the session clock: sample 0
    /// of the result plays at <c>start</c> seconds, the device clock's drift is undone with the track's sync points,
    /// and the result is a 16-bit PCM WAVE Unity imports as it is. Both files are streamed: only the part of the
    /// track the clip covers is read, a block at a time.
    /// </summary>
    internal static class AudioAlign
    {
        /// <summary>Frames read or written at a time.</summary>
        const int BlockFrames = 1 << 16;

        /// <summary>A float WAVE written by Orbiters - N4N0, read a block of frames at a time.</summary>
        internal sealed class FloatWave : IDisposable
        {
            public readonly int Rate, Channels;
            public readonly long Frames;
            readonly FileStream file;
            readonly long dataStart;
            readonly float[] block;
            readonly byte[] raw;
            long blockStart = -1;
            int blockFrames;

            public FloatWave(string path)
            {
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                try
                {
                    long length = file.Length;
                    var head = new byte[16];
                    if (length < 12 || Read(head, 0, 4) < 4 || head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F')
                        throw new InvalidDataException(path + " is not a WAVE file");
                    int format = 0, bits = 0;
                    long dataLength = -1;
                    long at = 12;
                    while (at + 8 <= length)
                    {
                        file.Position = at;
                        if (Read(head, 0, 8) < 8) break;
                        string id = System.Text.Encoding.ASCII.GetString(head, 0, 4);
                        // Unsigned: every chunk moves on by at least its own header, whatever its size field says.
                        long size = BitConverter.ToUInt32(head, 4);
                        if (id == "fmt ")
                        {
                            if (size < 16 || at + 8 + 16 > length || Read(head, 0, 16) < 16)
                                throw new InvalidDataException(path + " has a broken fmt chunk");
                            format = BitConverter.ToUInt16(head, 0);
                            Channels = BitConverter.ToUInt16(head, 2);
                            Rate = BitConverter.ToInt32(head, 4);
                            bits = BitConverter.ToUInt16(head, 14);
                        }
                        else if (id == "data")
                        {
                            dataStart = at + 8;
                            // A recording that ended abruptly may claim more than the file holds.
                            dataLength = Math.Min(size, length - dataStart);
                            break;
                        }
                        at += 8 + size + (size & 1);
                    }
                    if (format != 3 || bits != 32 || dataLength < 0 || Channels <= 0 || Rate <= 0)
                        throw new InvalidDataException(path + " is not a 32-bit float WAVE file");
                    Frames = dataLength / (4L * Channels);
                    block = new float[BlockFrames * Channels];
                    raw = new byte[block.Length * 4];
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }

            int Read(byte[] buffer, int offset, int count)
            {
                int done = 0;
                while (done < count)
                {
                    int got = file.Read(buffer, offset + done, count - done);
                    if (got <= 0) break;
                    done += got;
                }
                return done;
            }

            /// <summary>Channel <paramref name="channel"/> of frame <paramref name="frame"/> (0 ≤ frame &lt; <see cref="Frames"/>).</summary>
            public float Sample(long frame, int channel)
            {
                if (frame < blockStart || frame >= blockStart + blockFrames)
                {
                    // One frame back too: interpolation reads a frame and the next, so a block boundary costs one read.
                    blockStart = Math.Max(0, frame - 1);
                    blockFrames = (int)Math.Min(BlockFrames, Frames - blockStart);
                    file.Position = dataStart + blockStart * Channels * 4L;
                    int bytes = Read(raw, 0, blockFrames * Channels * 4);
                    Array.Clear(block, 0, block.Length);
                    Buffer.BlockCopy(raw, 0, block, 0, bytes - bytes % 4);
                }
                return block[(frame - blockStart) * Channels + channel];
            }

            public void Dispose() => file.Dispose();
        }

        /// <summary>
        /// Writes <paramref name="seconds"/> of <paramref name="track"/> from session time <paramref name="start"/> as
        /// 16-bit PCM to <paramref name="output"/>; silence where the track has no sound.
        /// </summary>
        public static void Write(AudioTracks.Track track, string sessionFolder, double start, double seconds, string output)
        {
            using var wave = new FloatWave(Path.Combine(sessionFolder, track.File));
            int rate = wave.Rate, channels = wave.Channels;
            long frames = wave.Frames;
            long outFrames = (long)Math.Ceiling(seconds * rate);
            long dataBytes = outFrames * channels * 2;
            if (dataBytes > uint.MaxValue - 36)
                throw new InvalidDataException("The clip is too long for one WAVE file (" + (dataBytes >> 30) + " GB of sound): bake a shorter part.");
            using var file = new BinaryWriter(new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16));
            file.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            file.Write((uint)(36 + dataBytes));
            file.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            file.Write(16);
            file.Write((short)1);
            file.Write((short)channels);
            file.Write(rate);
            file.Write(rate * channels * 2);
            file.Write((short)(channels * 2));
            file.Write((short)16);
            file.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            file.Write((uint)dataBytes);
            var pcm = new byte[BlockFrames * channels * 2];
            int filled = 0;
            // Session time of a source frame is piecewise linear (sync points): walk both clocks together.
            int segment = 1;
            for (long n = 0; n < outFrames; n++)
            {
                double tNs = (start + n / (double)rate) * 1e9;
                double source = SourceFrame(track, frames, tNs, ref segment);
                int o = filled * channels * 2;
                if (source >= 0 && source < frames - 1)
                {
                    long i = (long)source;
                    float k = (float)(source - i);
                    for (int ch = 0; ch < channels; ch++)
                    {
                        float v = wave.Sample(i, ch) * (1f - k) + wave.Sample(i + 1, ch) * k;
                        short s = (short)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 32767f);
                        pcm[o + ch * 2] = (byte)s;
                        pcm[o + ch * 2 + 1] = (byte)(s >> 8);
                    }
                }
                else
                {
                    Array.Clear(pcm, o, channels * 2);
                }
                if (++filled == BlockFrames)
                {
                    file.Write(pcm, 0, filled * channels * 2);
                    filled = 0;
                }
            }
            file.Write(pcm, 0, filled * channels * 2);
        }

        /// <summary>The (fractional) source frame heard at session time <paramref name="tNs"/>.</summary>
        static double SourceFrame(AudioTracks.Track track, long frames, double tNs, ref int segment)
        {
            var sync = track.Sync;
            if (sync == null || sync.Count < 2)
            {
                double first = track.FirstFrameNs;
                return (tNs - first) * track.SampleRate / 1e9;
            }
            while (segment < sync.Count - 1 && sync[segment][1] < tNs) segment++;
            var a = sync[segment - 1];
            var b = sync[segment];
            double span = b[1] - a[1];
            double k = span > 0 ? (tNs - a[1]) / span : 0;
            // Before the first sync point and after the last, the nearest segment's rate carries on.
            return a[0] + k * (b[0] - a[0]);
        }
    }
}
