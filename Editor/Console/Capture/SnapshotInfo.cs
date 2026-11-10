namespace Orbiters.Logger.Editor
{
    /// <summary>What the snapshot remembers besides the logs.</summary>
    internal sealed class SnapshotInfo
    {
        /// <summary>Entries in Unity's console when the snapshot was saved: rows after it arrived during the reload.</summary>
        public int UnityRows;
        /// <summary>The last of those rows (first two lines), to check the console wasn't cleared since.</summary>
        public string UnityAnchor = string.Empty;
        /// <summary>Console history not imported yet when scripts reloaded (rows <see cref="HistoryFrom"/> to <see cref="HistoryTo"/>).</summary>
        public int HistoryFrom = -1;
        public int HistoryTo = -1;
        public string HistoryAnchor = string.Empty;
        /// <summary>The first row of that range (first two lines), checked when the import goes on.</summary>
        public string HistoryFirstAnchor = string.Empty;
        /// <summary>The range was measured against the store already (HistoryFrom is the first row that fits).</summary>
        public bool HistoryMeasured;
        /// <summary>
        /// The import's time state when it started, restored exactly: the day and time of day row times are anchored on
        /// (local ticks), whether that day is the first timed row's own (measured) rather than the editor start's, and
        /// the time rows without one get.
        /// </summary>
        public long HistoryAnchorTicks;
        public bool HistoryAnchorIsRowDay;
        public long HistoryFallback;
    }
}
