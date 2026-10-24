using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Orbiters.Logger.Editor
{
    /// <summary>
    /// The README at the top of a report: what the person says is wrong, the project at a glance, the AI diagnosis,
    /// the messages that matter most and what each file holds. The first thing a helper opens.
    /// </summary>
    internal static class ReportReadme
    {
        internal static string Write(ReportContext context, IReadOnlyList<ReportStep> steps)
        {
            var text = new StringBuilder();
            var project = context.Project;
            text.Append("# ").Append(project?.Name ?? ReportOptions.ProjectName).Append(" · project report\n\n");
            text.Append("Made with the Mixer console ").Append(project?.LoggerVersion ?? string.Empty).Append(" on ")
                .Append(context.CreatedLocal.ToString("yyyy-MM-dd 'at' HH:mm", CultureInfo.InvariantCulture)).Append(".\n\n");

            if (!string.IsNullOrWhiteSpace(context.Note))
            {
                text.Append("## What's going wrong\n\n");
                // The note can hold a pasted log: escaped like everything that comes from elsewhere.
                foreach (string line in context.Note.Split('\n'))
                {
                    text.Append("> ").Append(MarkdownText.Escape(line.TrimEnd('\r'))).Append('\n');
                }

                text.Append('\n');
            }

            if (project != null)
            {
                text.Append("## Project\n\n");
                text.Append("| | |\n|---|---|\n");
                text.Append("| Unity | ").Append(project.Unity).Append(" |\n");
                text.Append("| Platform | ").Append(project.Platform).Append(" |\n");
                text.Append("| Rendering | ").Append(project.RenderPipeline).Append(" |\n");
                text.Append("| System | ").Append(Cell(project.OperatingSystem)).Append(" |\n");
                text.Append("| Packages | ").Append(project.Packages.Count).Append(" (see project/project.txt) |\n");
                var logs = context.Logs;
                if (logs != null && context.Has(ReportPart.Logs))
                {
                    text.Append("| Logs | ").Append(LoggerUi.Count(logs.Total)).Append(" · ").Append(LoggerUi.Plural(logs.Errors, "error")).Append(" · ")
                        .Append(LoggerUi.Plural(logs.Warnings, "warning")).Append(" |\n");
                }

                var git = context.Git;
                if (git != null && git.Repository && git.Error == null)
                {
                    text.Append("| Git | ").Append(Cell(git.Branch)).Append(" at ").Append(git.Head).Append(" · ").Append(LoggerUi.Plural(git.Changed, "changed file")).Append(" |\n");
                }

                var scene = context.Has(ReportPart.Scene) ? context.Scene : null;
                if (scene != null)
                {
                    text.Append("| Scene | ").Append(Cell(string.Join(", ", scene.Names))).Append(" · ").Append(LoggerUi.Plural(scene.Objects, "object"))
                        .Append(scene.MissingScripts > 0 ? " · **" + LoggerUi.Plural(scene.MissingScripts, "missing script") + "**" : string.Empty)
                        .Append(scene.Unsaved ? " · unsaved changes" : string.Empty).Append(" |\n");
                }

                text.Append('\n');
            }

            if (context.Diagnosis != null && context.DiagnosisError == null)
            {
                // One level down: the README's own title stays the only first-level one.
                text.Append(DiagnosisClient.Markdown(context.Diagnosis, context.Logs?.Groups, 2)).Append('\n');
            }
            else if (context.Has(ReportPart.Diagnosis) && context.DiagnosisError != null)
            {
                text.Append("## AI diagnosis\n\nNot available: ").Append(MarkdownText.Escape(context.DiagnosisError)).Append("\n\n");
            }

            var groups = context.Logs?.Groups;
            if (context.Has(ReportPart.Logs) && groups != null && groups.Any(g => g.Level != LogLevel.Info))
            {
                text.Append("## The messages that matter most\n\n");
                foreach (var group in groups.Where(g => g.Level != LogLevel.Info).Take(15))
                {
                    text.Append("- **").Append(group.Level == LogLevel.Error ? "Error" : "Warning").Append("** ×").Append(group.Count).Append(" · ")
                        .Append(MarkdownText.Escape(group.Source)).Append(" · `").Append(DiagnosisClient.FirstLine(group.Text)).Append('`');
                    if (!string.IsNullOrEmpty(group.Known))
                    {
                        text.Append("  \n  Known: ").Append(MarkdownText.Escape(group.Known));
                    }

                    text.Append('\n');
                }

                text.Append("\nEvery message with its stack trace is in logs/messages.txt.\n\n");
            }

            var parts = steps.Where(s => s.State == ReportStepState.Failed || s.State == ReportStepState.Skipped && s.Detail != "Not included").ToList();
            if (parts.Count > 0)
            {
                text.Append("## Left out\n\n");
                foreach (var step in parts)
                {
                    text.Append("- ").Append(step.Title).Append(": ").Append(MarkdownText.Escape(step.Detail)).Append('\n');
                }

                text.Append('\n');
            }

            text.Append("## Contents\n\n");
            foreach (var entry in context.Entries.Where(e => e.Name != "README.md").OrderBy(e => e.Name, System.StringComparer.OrdinalIgnoreCase))
            {
                text.Append("- `").Append(entry.Name).Append("` ").Append(entry.Label).Append('\n');
            }

            text.Append('\n').Append(MarkdownText.Escape(context.Privacy.Describe())).Append('\n');
            return text.ToString();
        }

        private static string Cell(string value) => (value ?? string.Empty).Replace("|", "\\|");
    }
}
