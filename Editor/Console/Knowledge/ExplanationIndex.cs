using System;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// Which explanation each message of the store has (looked up the first time a row or the details show it), shared
    /// by every window. It was a field of the store's messages, written by the windows (MXL-5): the store's hot struct
    /// now only holds what the store itself decides. Main thread.
    /// </summary>
    internal static class ExplanationIndex
    {
        // What a cached answer reads: the message's text, stack and level, which never change for a message id within
        // one store, epoch (compactions and clears renumber messages) and text table; and the explanations themselves
        // (their version). Any of these changed: everything is looked up again.
        private static LogStore store;
        private static int epoch = -1;
        private static TextTable texts;
        private static int version = -1;
        // An explanation's index + 2 by message id (0: not looked up yet, 1: none).
        private static int[] byMessage = new int[0];

        /// <summary>The index in <see cref="LogExplanations"/> of the explanation for a message, or -1.</summary>
        public static int Of(LogStore source, int message)
        {
            if (source == null || message < 0 || message >= source.MessageCount)
            {
                return -1;
            }

            if (!ReferenceEquals(source, store) || source.Epoch != epoch || !ReferenceEquals(source.Texts, texts) || LogExplanations.Version != version)
            {
                store = source;
                epoch = source.Epoch;
                texts = source.Texts;
                version = LogExplanations.Version;
                Array.Clear(byMessage, 0, byMessage.Length);
            }

            if (message >= byMessage.Length)
            {
                Array.Resize(ref byMessage, Math.Max(message + 1, Math.Max(256, byMessage.Length * 2)));
            }

            if (byMessage[message] == 0)
            {
                ref var entry = ref source.Message(message);
                byMessage[message] = LogExplanations.Match(source.Texts.Plain(entry.Text), source.Texts[entry.Stack], entry.Level) + 2;
            }

            return byMessage[message] - 2;
        }
    }
}
