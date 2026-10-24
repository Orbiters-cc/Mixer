using System;
using System.Globalization;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>Dates and durations as Mixer Git shows them.</summary>
    internal static class UnitGitTime
    {
        /// <summary>The format of commit dates in the log and details (Git's <c>%m/%d/%Y %I:%M %p</c>).</summary>
        internal const string DateFormat = "MM/dd/yyyy hh:mm tt";
        internal const string DateTimeFormat = "MM/dd/yyyy hh:mm:ss tt";
        internal const string TimeFormat = "hh:mm:ss tt";

        /// <summary>Reads an ISO-8601 time (as releases store them) as UTC.</summary>
        internal static bool TryParseUtc(string iso, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(iso)) return false;
            if (!DateTime.TryParse(iso.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)) return false;
            utc = parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
            return true;
        }

        /// <summary>An ISO-8601 time in local time, as commit dates are shown; the text itself when it is not a time.</summary>
        internal static string LocalDate(string iso) =>
            TryParseUtc(iso, out DateTime utc) ? utc.ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture) : iso ?? string.Empty;

        /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", "12 days ago", then the date.</summary>
        internal static string Relative(DateTime time) => Relative(time, DateTime.Now);

        internal static string Relative(DateTime time, DateTime now)
        {
            if (time.Kind == DateTimeKind.Utc) time = time.ToLocalTime();
            if (now.Kind == DateTimeKind.Utc) now = now.ToLocalTime();
            var span = now - time;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " min ago";
            if (span.TotalDays < 1) return (int)span.TotalHours + " h ago";
            if (span.TotalDays < 2) return "yesterday";
            if (span.TotalDays < 30) return (int)span.TotalDays + " days ago";
            return time.ToString("d", CultureInfo.CurrentCulture);
        }

        /// <summary>"850 ms", "34.5 s", "2 min 05 s", "1 h 02 min".</summary>
        internal static string Duration(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = span.Negate();
            if (span.TotalSeconds < 1) return (int)Math.Round(span.TotalMilliseconds) + " ms";
            if (span.TotalSeconds < 60) return (Math.Floor(span.TotalSeconds * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture) + " s";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " min " + span.Seconds.ToString("00", CultureInfo.InvariantCulture) + " s";
            return (int)span.TotalHours + " h " + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + " min";
        }
    }
}
