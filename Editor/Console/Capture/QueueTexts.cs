using System.Threading;

namespace Orbiters.Logger.Editor
{
    /// <summary>A text held by queued logs, with how many of them hold it: its characters count until the last one is filed.</summary>
    internal sealed class SharedText
    {
        public readonly string Text;
        internal int Refs;

        public SharedText(string text)
        {
            Text = text;
        }
    }

    /// <summary>
    /// The texts of logs waiting to be filed, bounded by characters (besides LogCapture's count). Unity hands new strings
    /// for every log: a log equal to the one before it on the same thread keeps the earlier instance instead
    /// (<see cref="Recent"/>), so a loop that logs one warning a million times queues one message and one stack trace,
    /// the copies are garbage at once, and the main thread finds them by reference. A shared text is charged once and
    /// stays charged while any queued log holds it. Admission reserves its characters atomically before the log is
    /// queued; one log is always admitted into an empty queue (texts are cut at <see cref="LogStore.MaxTextLength"/>
    /// first, so that log is bounded). Any thread; no lock.
    /// </summary>
    internal sealed class QueueTexts
    {
        private readonly long max;
        private long chars;

        public QueueTexts(long maxChars)
        {
            max = maxChars;
        }

        /// <summary>Characters held by queued logs.</summary>
        public long Chars => Interlocked.Read(ref chars);

        /// <summary>The previous log's texts on one thread.</summary>
        internal sealed class Recent
        {
            public SharedText Condition;
            public SharedText Stack;
        }

        /// <summary>
        /// Takes a log's texts (already cut) into the queue's charge, sharing them with the thread's previous log when
        /// equal. False, with nothing charged or shared, when the queue holds too many characters for them.
        /// </summary>
        public bool Admit(Recent recent, string condition, string stack, out SharedText sharedCondition, out SharedText sharedStack)
        {
            bool reuseCondition = Same(recent.Condition, condition);
            bool reuseStack = Same(recent.Stack, stack);
            long need = (reuseCondition ? 0 : condition?.Length ?? 0) + (reuseStack ? 0 : stack?.Length ?? 0);
            if (need > 0)
            {
                long after = Interlocked.Add(ref chars, need);
                if (after > max && after != need)
                {
                    Interlocked.Add(ref chars, -need);
                    sharedCondition = sharedStack = null;
                    return false;
                }
            }

            sharedCondition = Take(ref recent.Condition, condition, reuseCondition);
            sharedStack = Take(ref recent.Stack, stack, reuseStack);
            return true;
        }

        /// <summary>A queued log was filed: its texts' charge goes with the last log holding them.</summary>
        public void Release(SharedText text)
        {
            if (text != null && Interlocked.Decrement(ref text.Refs) == 0)
            {
                Interlocked.Add(ref chars, -text.Text.Length);
            }
        }

        private SharedText Take(ref SharedText previous, string text, bool reuse)
        {
            if (text == null)
            {
                return null;
            }

            if (!reuse)
            {
                // Charged by Admit.
                previous = new SharedText(text) { Refs = 1 };
                return previous;
            }

            if (Interlocked.Increment(ref previous.Refs) == 1)
            {
                // Every log that held it was filed in between, and its charge went: it is charged again.
                Interlocked.Add(ref chars, text.Length);
            }

            return previous;
        }

        // A block compare, which stops at the first difference.
        private static bool Same(SharedText previous, string text) =>
            text != null && previous != null && previous.Text.Length == text.Length && string.Equals(previous.Text, text);
    }
}
