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
            // A text equal to the thread's previous one is held first; one that every queued log had already let go of
            // (held again from zero) needs a charge again, reserved like a new text's.
            var heldCondition = Hold(recent.Condition, condition, out bool reviveCondition);
            var heldStack = Hold(recent.Stack, stack, out bool reviveStack);
            long need = (heldCondition == null || reviveCondition ? condition?.Length ?? 0 : 0) +
                        (heldStack == null || reviveStack ? stack?.Length ?? 0 : 0);
            if (need > 0)
            {
                long after = Interlocked.Add(ref chars, need);
                if (after > max && after != need)
                {
                    Interlocked.Add(ref chars, -need);
                    // Let go of what was held: a revived text was never charged; a live one goes like any release.
                    Unhold(heldCondition, reviveCondition);
                    Unhold(heldStack, reviveStack);
                    sharedCondition = sharedStack = null;
                    return false;
                }
            }

            sharedCondition = heldCondition ?? New(ref recent.Condition, condition);
            sharedStack = heldStack ?? New(ref recent.Stack, stack);
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

        // The thread's previous text, held once more, when equal; `revived` when nobody held it any more (no charge).
        private static SharedText Hold(SharedText previous, string text, out bool revived)
        {
            revived = false;
            if (!Same(previous, text))
            {
                return null;
            }

            revived = Interlocked.Increment(ref previous.Refs) == 1;
            return previous;
        }

        private void Unhold(SharedText held, bool revived)
        {
            if (held == null)
            {
                return;
            }

            if (revived)
            {
                Interlocked.Decrement(ref held.Refs);
            }
            else
            {
                Release(held);
            }
        }

        // Charged by Admit.
        private static SharedText New(ref SharedText previous, string text)
        {
            if (text == null)
            {
                return null;
            }

            previous = new SharedText(text) { Refs = 1 };
            return previous;
        }

        // A block compare, which stops at the first difference.
        private static bool Same(SharedText previous, string text) =>
            text != null && previous != null && previous.Text.Length == text.Length && string.Equals(previous.Text, text);
    }
}
