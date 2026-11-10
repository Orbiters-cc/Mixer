using System;
using System.Collections.Generic;

namespace Orbiters.Logger.Editor
{
    /// <summary>A timing log of the store: when, which message, what it measured.</summary>
    internal readonly struct TimingEntry
    {
        public readonly int Message;
        public readonly long Time;
        public readonly TimingProfile Profile;

        public TimingEntry(int message, long time, TimingProfile profile)
        {
            Message = message;
            Time = time;
            Profile = profile;
        }
    }

    /// <summary>
    /// The timing logs of the store, parsed once: for the rows' bars (scaled to the slowest of their kind), the
    /// timeline's lane and the details' history. Messages are only added, so new ones are read as they come.
    /// </summary>
    internal sealed class TimingIndex
    {
        private readonly List<TimingEntry> entries = new List<TimingEntry>();
        private readonly Dictionary<int, TimingProfile> byMessage = new Dictionary<int, TimingProfile>();
        private readonly double[] slowest = new double[4];
        private LogStore store;
        private int epoch = -1;
        private int firstId = -1;
        private int scanned;
        private int version = -1;

        public IReadOnlyList<TimingEntry> Entries => entries;

        public int Changes { get; private set; }

        public void Update(LogStore source)
        {
            if (source == null)
            {
                return;
            }

            if (!ReferenceEquals(source, store) || source.Epoch != epoch || source.FirstId != firstId)
            {
                store = source;
                epoch = source.Epoch;
                firstId = source.FirstId;
                entries.Clear();
                byMessage.Clear();
                Array.Clear(slowest, 0, slowest.Length);
                scanned = 0;
                version = -1;
                Changes++;
            }

            if (source.Version == version)
            {
                return;
            }

            version = source.Version;
            bool added = false;
            for (int i = scanned; i < source.MessageCount; i++)
            {
                ref var message = ref source.Message(i);
                if (message.Stack <= 0 || message.Count == 0 || (message.Flags & MessageFlags.Timing) == 0)
                {
                    continue;
                }

                var profile = source.TimingOf(i);
                if (profile == null)
                {
                    continue;
                }

                byMessage[i] = profile;
                entries.Add(new TimingEntry(i, source.TimeAt(message.Last), profile));
                slowest[(int)profile.Kind] = Math.Max(slowest[(int)profile.Kind], profile.TotalMilliseconds);
                added = true;
            }

            scanned = source.MessageCount;
            if (added)
            {
                entries.Sort((a, b) => a.Time.CompareTo(b.Time));
                Changes++;
            }
        }

        public bool TryGet(int message, out TimingProfile profile) => byMessage.TryGetValue(message, out profile);

        public double Slowest(TimingKind kind) => Math.Max(1d, slowest[(int)kind]);
    }
}
