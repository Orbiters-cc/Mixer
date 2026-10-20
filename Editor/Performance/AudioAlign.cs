using System;
using System.IO;
using Orbiters.Contracts;

namespace Orbiters.Mixer.Editor.Performance
{
    /// <summary>
    /// A recorded audio track (32-bit float WAVE, contracts <c>audio.json</c>) placed on the session clock: sample 0
    /// of the result plays at <c>start</c> seconds, the device clock's drift is undone with the track's sync points,
    /// and the result is a 16-bit PCM WAVE Unity imports as it is.
    /// </summary>
    internal static class AudioAlign
    {
        /// <summary>Reads a float WAVE written by Orbiters desktop: (sample rate, channels, interleaved samples).</summary>
        public static (int rate, int channels, float[] samples) ReadFloatWave(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') throw new InvalidDataException(path + " is not a WAVE file");
            int rate = 0, channels = 0, format = 0, bits = 0, dataOffset = -1, dataLength = 0;
            int at = 12;
            while (at + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                int size = BitConverter.ToInt32(bytes, at + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(bytes, at + 8);
                    channels = BitConverter.ToUInt16(bytes, at + 10);
                    rate = BitConverter.ToInt32(bytes, at + 12);
                    bits = BitConverter.ToUInt16(bytes, at + 22);
                }
                else if (id == "data")
                {
                    dataOffset = at + 8;
                    // A recording that ended abruptly may claim more than the file holds.
                    dataLength = Math.Min(size, bytes.Length - dataOffset);
                    break;
                }
                at += 8 + size + (size & 1);
            }
            if (format != 3 || bits != 32 || dataOffset < 0 || channels <= 0) throw new InvalidDataException(path + " is not a 32-bit float WAVE file");
            var samples = new float[dataLength / 4];
            Buffer.BlockCopy(bytes, dataOffset, samples, 0, samples.Length * 4);
            return (rate, channels, samples);
        }

        /// <summary>
        /// Writes <paramref name="seconds"/> of <paramref name="track"/> from session time <paramref name="start"/> as
        /// 16-bit PCM to <paramref name="output"/>; silence where the track has no sound.
        /// </summary>
        public static void Write(AudioTracks.Track track, string sessionFolder, double start, double seconds, string output)
        {
            var (rate, channels, samples) = ReadFloatWave(Path.Combine(sessionFolder, track.File));
            long frames = samples.Length / channels;
            long outFrames = (long)Math.Ceiling(seconds * rate);
            var pcm = new short[outFrames * channels];
            // Session time of a source frame is piecewise linear (sync points): walk both clocks together.
            int segment = 1;
            for (long n = 0; n < outFrames; n++)
            {
                double tNs = (start + n / (double)rate) * 1e9;
                double source = SourceFrame(track, frames, tNs, ref segment);
                if (source < 0 || source >= frames - 1) continue;
                long i = (long)source;
                float k = (float)(source - i);
                for (int ch = 0; ch < channels; ch++)
                {
                    float v = samples[i * channels + ch] * (1f - k) + samples[(i + 1) * channels + ch] * k;
                    pcm[n * channels + ch] = (short)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 32767f);
                }
            }
            using var file = new BinaryWriter(File.Create(output));
            int dataBytes = pcm.Length * 2;
            file.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            file.Write(36 + dataBytes);
            file.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            file.Write(16);
            file.Write((short)1);
            file.Write((short)channels);
            file.Write(rate);
            file.Write(rate * channels * 2);
            file.Write((short)(channels * 2));
            file.Write((short)16);
            file.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            file.Write(dataBytes);
            var raw = new byte[dataBytes];
            Buffer.BlockCopy(pcm, 0, raw, 0, dataBytes);
            file.Write(raw);
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
