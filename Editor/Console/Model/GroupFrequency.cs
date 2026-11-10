using System;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// One group's occurrence times in order, kept up to date by reading only the occurrences added since (a group
    /// seen at the start and the end of a session spans the whole store), and their peak per second.
    /// </summary>
    internal sealed class GroupFrequency
    {
        private LogStore store;
        private int group = -1;
        private int epoch = -1;
        private int messagesVersion = -1;
        private int firstId = -1;
        private int scanned;
        private int window;

        public long[] Times = new long[64];
        public int Count;
        public int Peak;

        public void Update(LogStore source, int shown)
        {
            if (!ReferenceEquals(source, store) || shown != group || source.Epoch != epoch || source.MessagesVersion != messagesVersion ||
                source.FirstId != firstId)
            {
                // Another group, or occurrences gone or added in front (trimmed, merged) or superseded: from the start.
                store = source;
                group = shown;
                epoch = source.Epoch;
                messagesVersion = source.MessagesVersion;
                firstId = source.FirstId;
                scanned = Math.Max(source.FirstId, source.Group(shown).First);
                window = 0;
                Count = 0;
                Peak = 0;
                if (Times.Length > 1 << 16)
                {
                    Times = new long[64];
                }
            }

            int end = Math.Min(source.Group(shown).Last + 1, source.EndId);
            for (int i = scanned; i < end; i++)
            {
                if (source.Message(source.MessageAt(i)).Group != shown || (source.FlagsAt(i) & LogStore.Superseded) != 0)
                {
                    continue;
                }

                long t = source.TimeAt(i);
                if (Count == Times.Length)
                {
                    Array.Resize(ref Times, Times.Length * 2);
                }

                Times[Count++] = t;
                // Peak per second: a window sliding over the (ordered) times.
                while (t - Times[window] > TimeSpan.TicksPerSecond)
                {
                    window++;
                }

                Peak = Math.Max(Peak, Count - window);
            }

            scanned = Math.Max(scanned, end);
        }
    }
}
