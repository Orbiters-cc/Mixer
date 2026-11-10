using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Orbiters.Logger.Editor
{
    /// <summary>What a timing log measured.</summary>
    internal enum TimingKind : byte
    {
        ScriptReload,
        PlayMode,
        AvatarBuild,
        AvatarUpload
    }

    /// <summary>One measured piece: who (a package, as a source name), at which step, doing what, for how long.</summary>
    internal readonly struct TimingPart
    {
        public readonly string Source;
        public readonly string Phase;
        public readonly string Label;
        public readonly double Milliseconds;

        public TimingPart(string source, string phase, string label, double milliseconds)
        {
            Source = string.IsNullOrEmpty(source) ? "Unity" : source;
            Phase = phase ?? string.Empty;
            Label = label ?? string.Empty;
            Milliseconds = Math.Max(0d, milliseconds);
        }
    }

    /// <summary>All the time one source took in a profile.</summary>
    internal sealed class TimingShare
    {
        public string Source;
        public double Milliseconds;
        public readonly List<TimingPart> Parts = new List<TimingPart>();
    }

    /// <summary>
    /// How long a script reload, an entry into Play Mode or an avatar build or upload took, and which package took
    /// what. A timing log carries one in its "stack trace" (see <see cref="Format"/>), so it is kept, copied and
    /// searched like any other log.
    /// </summary>
    internal sealed class TimingProfile
    {
        internal const string Prefix = "Timings:\n";
        internal const string UnitySource = "Unity";
        private const string Separator = "  |  ";

        public TimingKind Kind;
        public double TotalMilliseconds;
        /// <summary>What was built or uploaded (an avatar's name), empty otherwise.</summary>
        public string Subject = string.Empty;
        /// <summary>Set when the build or upload failed or was cancelled.</summary>
        public string Outcome = string.Empty;
        public readonly List<TimingPart> Parts = new List<TimingPart>();

        private List<TimingShare> shares;

        public static bool IsTimings(string stack) => stack != null && stack.StartsWith(Prefix, StringComparison.Ordinal);

        public static string KindLabel(TimingKind kind)
        {
            switch (kind)
            {
                case TimingKind.PlayMode:
                    return "Entering Play Mode";
                case TimingKind.AvatarBuild:
                    return "Avatar build";
                case TimingKind.AvatarUpload:
                    return "Avatar upload";
                default:
                    return "Script reload";
            }
        }

        /// <summary>Time per source, largest first; <see cref="UnitySource"/> holds what no package claimed.</summary>
        public IReadOnlyList<TimingShare> Shares
        {
            get
            {
                if (shares != null)
                {
                    return shares;
                }

                var bySource = new Dictionary<string, TimingShare>(StringComparer.OrdinalIgnoreCase);
                double claimed = 0d;
                foreach (var part in Parts)
                {
                    if (!bySource.TryGetValue(part.Source, out var share))
                    {
                        share = new TimingShare { Source = part.Source };
                        bySource[part.Source] = share;
                    }

                    share.Milliseconds += part.Milliseconds;
                    share.Parts.Add(part);
                    claimed += part.Milliseconds;
                }

                double rest = TotalMilliseconds - claimed;
                if (rest > 0.5d)
                {
                    if (!bySource.TryGetValue(UnitySource, out var unity))
                    {
                        unity = new TimingShare { Source = UnitySource };
                        bySource[UnitySource] = unity;
                    }

                    unity.Milliseconds += rest;
                    unity.Parts.Add(new TimingPart(UnitySource, "Engine", RestLabel, rest));
                }

                shares = new List<TimingShare>(bySource.Values);
                shares.Sort((a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
                foreach (var share in shares)
                {
                    share.Parts.Sort((a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
                }

                return shares;
            }
        }

        private string RestLabel
        {
            get
            {
                switch (Kind)
                {
                    case TimingKind.ScriptReload:
                        return "Loading assemblies and the rest of the reload";
                    case TimingKind.PlayMode:
                        return "Scene load, scripts' Awake and the first frame";
                    case TimingKind.AvatarBuild:
                        return "Asset bundle build and the SDK's own steps";
                    default:
                        return "Upload and the SDK's own steps";
                }
            }
        }

        /// <summary>"VRCFury 1.0 s · MCB 0.52 s · +12": the largest shares, the engine's left out.</summary>
        public string Summary(int count)
        {
            var text = new StringBuilder();
            int shown = 0;
            int others = 0;
            foreach (var share in Shares)
            {
                if (share.Source == UnitySource)
                {
                    continue;
                }

                if (shown >= count)
                {
                    others++;
                    continue;
                }

                text.Append(shown > 0 ? "  ·  " : string.Empty).Append(share.Source).Append(' ').Append(FormatDuration(share.Milliseconds));
                shown++;
            }

            if (others > 0)
            {
                text.Append("  ·  +").Append(others);
            }

            return shown == 0 ? "Unity only" : text.ToString();
        }

        /// <summary>The message of the timing log: "Script reload took 6.6 s".</summary>
        public string Message()
        {
            string duration = FormatDuration(TotalMilliseconds);
            string subject = string.IsNullOrEmpty(Subject) ? string.Empty : " " + Subject;
            string outcome = string.IsNullOrEmpty(Outcome) ? string.Empty : " (" + Outcome + ")";
            switch (Kind)
            {
                case TimingKind.PlayMode:
                    return "Entered Play Mode in " + duration;
                case TimingKind.AvatarBuild:
                    return "Built" + (subject.Length > 0 ? subject : " the avatar") + " in " + duration + outcome;
                case TimingKind.AvatarUpload:
                    return "Built and uploaded" + (subject.Length > 0 ? subject : " the avatar") + " in " + duration + outcome;
                default:
                    return "Script reload took " + duration;
            }
        }

        /// <summary>The "stack trace" of the timing log: a header, then one line per part, readable when copied.</summary>
        public string Format()
        {
            var builder = new StringBuilder(Prefix);
            builder.Append("kind").Append(Separator).Append(Kind).Append('\n');
            builder.Append("total").Append(Separator).Append(Number(TotalMilliseconds)).Append(" ms\n");
            if (!string.IsNullOrEmpty(Subject))
            {
                builder.Append("subject").Append(Separator).Append(Clean(Subject)).Append('\n');
            }

            if (!string.IsNullOrEmpty(Outcome))
            {
                builder.Append("outcome").Append(Separator).Append(Clean(Outcome)).Append('\n');
            }

            foreach (var part in Parts)
            {
                builder.Append(Number(part.Milliseconds).PadLeft(9)).Append(" ms").Append(Separator)
                    .Append(Clean(part.Source)).Append(Separator)
                    .Append(Clean(part.Phase)).Append(Separator)
                    .Append(Clean(part.Label)).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>Reads back what <see cref="Format"/> wrote; null when <paramref name="stack"/> is not a timing log.</summary>
        public static TimingProfile Parse(string stack)
        {
            if (!IsTimings(stack))
            {
                return null;
            }

            var profile = new TimingProfile();
            int start = Prefix.Length;
            while (start < stack.Length)
            {
                int end = stack.IndexOf('\n', start);
                if (end < 0)
                {
                    end = stack.Length;
                }

                string line = stack.Substring(start, end - start).TrimEnd('\r');
                start = end + 1;
                var fields = line.Split(new[] { Separator }, StringSplitOptions.None);
                if (fields.Length == 2)
                {
                    string key = fields[0].Trim();
                    string value = fields[1].Trim();
                    switch (key)
                    {
                        case "kind":
                            if (Enum.TryParse(value, out TimingKind kind))
                            {
                                profile.Kind = kind;
                            }

                            break;
                        case "total":
                            profile.TotalMilliseconds = ParseMilliseconds(value);
                            break;
                        case "subject":
                            profile.Subject = value;
                            break;
                        case "outcome":
                            profile.Outcome = value;
                            break;
                    }
                }
                else if (fields.Length == 4)
                {
                    profile.Parts.Add(new TimingPart(fields[1].Trim(), fields[2].Trim(), fields[3].Trim(), ParseMilliseconds(fields[0].Trim())));
                }
            }

            return profile;
        }

        public static string FormatDuration(double milliseconds)
        {
            if (milliseconds < 1000d)
            {
                return Math.Round(milliseconds).ToString("0", CultureInfo.InvariantCulture) + " ms";
            }

            double seconds = milliseconds / 1000d;
            if (seconds < 60d)
            {
                return seconds.ToString(seconds < 10d ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";
            }

            int minutes = (int)(seconds / 60d);
            int rest = (int)Math.Round(seconds - minutes * 60d);
            if (rest == 60)
            {
                minutes++;
                rest = 0;
            }

            return minutes + " min" + (rest > 0 ? " " + rest + " s" : string.Empty);
        }

        private static double ParseMilliseconds(string text)
        {
            if (text.EndsWith("ms", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 2).Trim();
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0d;
        }

        private static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

        private static string Clean(string text) => (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Replace(Separator, " / ");
    }
}
