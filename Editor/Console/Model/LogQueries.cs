using System;

namespace Orbiters.Logger.Editor
{
    /// <summary>Questions about the store that no window owns (moved out of LoggerWindow, MXL-3).</summary>
    internal static class LogQueries
    {
        /// <summary>The newest error in the log (an exception, an assert, a compile error still standing), or -1.</summary>
        internal static int LatestError(LogStore store)
        {
            if (store == null)
            {
                return -1;
            }

            for (int occurrence = store.EndId - 1, seen = 0; occurrence >= store.FirstId && seen < 1_000_000; occurrence--, seen++)
            {
                var message = store.Message(store.MessageAt(occurrence));
                if (message.Level == LogLevel.Error && !message.Resolved && (store.FlagsAt(occurrence) & LogStore.Superseded) == 0)
                {
                    return occurrence;
                }
            }

            return -1;
        }

        /// <summary>
        /// The error a tool's "Check Console for more details" means: where a VRChat upload failed
        /// (<see cref="SdkUploadErrors"/>) when it was logged with the newest error (within a few minutes of it), else the
        /// newest error.
        /// </summary>
        internal static int ErrorToShow(LogStore store)
        {
            int latest = LatestError(store);
            if (latest < 0)
            {
                return -1;
            }

            long since = store.TimeAt(latest) - TimeSpan.TicksPerMinute * 5;
            for (int occurrence = store.EndId - 1, seen = 0; occurrence >= store.FirstId && seen < 20_000 && store.TimeAt(occurrence) >= since; occurrence--, seen++)
            {
                var message = store.Message(store.MessageAt(occurrence));
                if (message.Level == LogLevel.Error && store.Texts[message.Text].StartsWith(SdkUploadErrors.Prefix, StringComparison.Ordinal))
                {
                    return occurrence;
                }
            }

            return latest;
        }
    }
}
