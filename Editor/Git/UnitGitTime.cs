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

        /// <summary>
        /// A commit's age worded as Git's <c>--date=relative</c> / <c>%ar</c> words it ("5 minutes ago", "2 years, 3 months
        /// ago"), the same rounding included (show_date_relative in Git's date.c). Computed when shown, so a history kept
        /// between refreshes never shows a stale age.
        /// </summary>
        internal static string GitRelative(long unixSeconds) => GitRelative(unixSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        internal static string GitRelative(long unixSeconds, long nowSeconds)
        {
            if (nowSeconds < unixSeconds) return "in the future";
            long diff = nowSeconds - unixSeconds;
            if (diff < 90) return Ago(diff, "second");
            diff = (diff + 30) / 60; // minutes
            if (diff < 90) return Ago(diff, "minute");
            diff = (diff + 30) / 60; // hours
            if (diff < 36) return Ago(diff, "hour");
            diff = (diff + 12) / 24; // days from here on
            if (diff < 14) return Ago(diff, "day");
            if (diff < 70) return Ago((diff + 3) / 7, "week");
            if (diff < 365) return Ago((diff + 15) / 30, "month");
            if (diff < 1825)
            {
                long totalMonths = (diff * 12 * 2 + 365) / (365 * 2);
                long years = totalMonths / 12;
                long months = totalMonths % 12;
                return months == 0 ? Ago(years, "year") : Count(years, "year") + ", " + Ago(months, "month");
            }
            return Ago((diff + 183) / 365, "year");
        }

        private static string Count(long count, string unit) =>
            count.ToString(CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? string.Empty : "s");

        private static string Ago(long count, string unit) => Count(count, unit) + " ago";

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
