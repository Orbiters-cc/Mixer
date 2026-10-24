using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Logger.Editor
{
    internal sealed partial class ReportWindow
    {
        private VisualElement workPage;
        private VisualElement donePage;
        private Label workTitle;
        private Label workPercent;
        private VisualElement barFill;
        private VisualElement barGlow;
        private readonly List<VisualElement> stepRows = new List<VisualElement>();
        private readonly List<ReportStepState> shownStates = new List<ReportStepState>();
        private float shownPercent;
        private VisualElement diagnosisHost;
        private Label doneFile;
        private VisualElement doneParts;
        private string shownDiagnosis;

        // ---- Start, cancel, start over --------------------------------------------------------------------------

        private void OnGo()
        {
            if (EditorApplication.timeSinceStartup - goChangedAt < 0.6d)
            {
                return;
            }

            if (running == null)
            {
                Create();
            }
            else if (!running.Finished)
            {
                running.Cancel();
            }
            else
            {
                StartOver();
            }
        }

        private void Create()
        {
            var parts = ReportOptions.Parts.Where(IsChosen).ToList();
            string folder = ReportOptions.Folder;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception exception)
            {
                ShowToast("Can't save in " + folder + ": " + exception.Message, true);
                return;
            }

            ReportBuilder builder;
            try
            {
                builder = ReportBuilder.Start(parts, noteField?.value, folder, ReportOptions.Anonymize);
            }
            catch (Exception exception)
            {
                ShowToast("The report could not start: " + exception.Message, true);
                return;
            }

            AttachBuilder(builder, animate: true);
        }

        private void AttachBuilder(ReportBuilder builder, bool animate)
        {
            if (running != null)
            {
                running.Changed -= OnBuilderChanged;
            }

            running = builder;
            builder.Changed += OnBuilderChanged;
            BuildWorkPage(builder, animate);
            ShowPage(workPage, animate);
            orbit.Speed = 4.5f;
            subtitle.text = "Making your report. Keep working in Unity, or close this window: it carries on. A script reload stops it.";
            SetGo("Cancel", LoggerGlyph.Close, quiet: true);
            destination.SetEnabled(false);
            UpdateDestination();
            estimate.text = string.Empty;
            OnBuilderChanged();
        }

        private void StartOver()
        {
            if (running != null)
            {
                running.Changed -= OnBuilderChanged;
            }

            running = null;
            shownDiagnosis = null;
            orbit.Glyph = LoggerGlyph.Lifebuoy;
            orbit.Speed = 1f;
            subtitle.text = "Everything someone needs to see what's going on, in one file. Choose what goes in.";
            subtitle.tooltip = null;
            SetGo("Create report", LoggerGlyph.Package, quiet: false);
            destination.SetEnabled(true);
            UpdateDestination();
            ShowPage(choosePage, animate: true);
            PlayCardsEntrance();
            sceneDirty = true;
            RefreshFacts(git: true);
        }

        private void OnBuilderChanged()
        {
            var builder = running;
            if (builder == null || workPage == null)
            {
                return;
            }

            UpdateSteps(builder);
            if (builder.Finished && shownPage == workPage)
            {
                // A breath on the full bar before the result.
                rootVisualElement.schedule.Execute(() => ShowDone(builder)).StartingIn(builder.Cancelled ? 0 : 420);
            }
            else if (builder.Finished && shownPage == donePage)
            {
                FillParts(builder);
                ShowDiagnosis(builder);
            }
        }

        // ---- Progress -------------------------------------------------------------------------------------------

        private void BuildWorkPage(ReportBuilder builder, bool animate)
        {
            workPage = LoggerUi.Box("rp-page");
            var head = LoggerUi.Box("rp-work__head");
            workTitle = LoggerUi.Text("Making your report", "rp-work__title");
            head.Add(workTitle);
            workPercent = LoggerUi.Text("0%", "rp-work__percent");
            head.Add(workPercent);
            workPage.Add(head);

            var bar = LoggerUi.Box("rp-bar", PickingMode.Ignore);
            barFill = LoggerUi.Box("rp-bar__fill", PickingMode.Ignore);
            barGlow = LoggerUi.Box("rp-bar__glow", PickingMode.Ignore);
            barFill.Add(barGlow);
            bar.Add(barFill);
            workPage.Add(bar);

            var list = LoggerUi.Box("rp-steps");
            stepRows.Clear();
            shownStates.Clear();
            shownPercent = 0f;
            for (int i = 0; i < builder.Steps.Count; i++)
            {
                var row = StepRow(builder.Steps[i]);
                list.Add(row);
                stepRows.Add(row);
                shownStates.Add((ReportStepState)(-1));
                if (animate)
                {
                    LoggerUi.Enter(row, "rp-step--enter", 60 + i * 55);
                }
            }

            workPage.Add(list);
            // The percent counts smoothly to the builder's progress; a light runs along the bar. Both stop with the report.
            IVisualElementScheduledItem animation = null;
            animation = workPage.schedule.Execute(() =>
            {
                if (running == null)
                {
                    animation?.Pause();
                    return;
                }

                float target = running.Progress * 100f;
                shownPercent = Mathf.MoveTowards(shownPercent, target, Mathf.Max(0.6f, (target - shownPercent) * 0.18f));
                workPercent.text = Mathf.FloorToInt(shownPercent) + "%";
                barFill.style.width = Length.Percent(Mathf.Max(2f, running.Progress * 100f));
                float width = barFill.resolvedStyle.width;
                float cycle = (float)(EditorApplication.timeSinceStartup % 1.4d / 1.4d);
                barGlow.style.left = -70f + (width + 140f) * cycle;
                barGlow.style.opacity = running.Finished ? 0f : 0.8f;
                if (running.Finished && shownPercent >= target)
                {
                    animation?.Pause();
                }
            }).Every(16);
        }

        private static VisualElement StepRow(ReportStep step)
        {
            var row = LoggerUi.Box("rp-step");
            var tile = LoggerUi.Box("rp-step__tile", PickingMode.Ignore);
            tile.Add(new LoggerIcon(step.Glyph));
            row.Add(tile);
            var texts = LoggerUi.Box("rp-step__texts", PickingMode.Ignore);
            texts.Add(LoggerUi.Text(step.Title, "rp-step__title"));
            var detail = LoggerUi.Text(string.Empty, "rp-step__detail");
            detail.name = "detail";
            texts.Add(detail);
            row.Add(texts);
            var status = LoggerUi.Box("rp-step__status", PickingMode.Ignore);
            status.name = "status";
            row.Add(status);
            return row;
        }

        private void UpdateSteps(ReportBuilder builder)
        {
            for (int i = 0; i < builder.Steps.Count && i < stepRows.Count; i++)
            {
                var step = builder.Steps[i];
                var row = stepRows[i];
                row.Q<Label>("detail").text = step.Detail;
                if (shownStates[i] == step.State)
                {
                    continue;
                }

                shownStates[i] = step.State;
                foreach (ReportStepState state in Enum.GetValues(typeof(ReportStepState)))
                {
                    row.EnableInClassList("rp-step--" + state.ToString().ToLowerInvariant(), state == step.State);
                }

                var status = row.Q("status");
                status.Clear();
                switch (step.State)
                {
                    case ReportStepState.Waiting:
                        status.Add(LoggerUi.Box("rp-step__dot", PickingMode.Ignore));
                        break;
                    case ReportStepState.Running:
                        status.Add(LoggerUi.Spinner());
                        break;
                    default:
                        var check = LoggerUi.Box("rp-step__check", PickingMode.Ignore);
                        check.Add(new LoggerIcon(step.State == ReportStepState.Done ? LoggerGlyph.Check : step.State == ReportStepState.Failed ? LoggerGlyph.Close : LoggerGlyph.EyeOff));
                        status.Add(check);
                        LoggerUi.Enter(check, "rp-step__check--pop", 16);
                        break;
                }
            }

            workTitle.text = builder.Finished ? builder.Result != null ? "Done" : "Stopped" : "Making your report";
        }

        // ---- Result ---------------------------------------------------------------------------------------------

        private void ShowDone(ReportBuilder builder)
        {
            if (running != builder || shownPage == donePage)
            {
                return;
            }

            if (builder.Cancelled)
            {
                ShowToast("Report cancelled");
                StartOver();
                return;
            }

            bool made = builder.Result != null;
            donePage = LoggerUi.Box("rp-page");
            var hero = LoggerUi.Box("rp-done__hero");
            var mark = new DoneMark();
            hero.Add(mark);
            hero.Add(LoggerUi.Text(made ? "Your report is ready" : "The report could not be made", "rp-done__title"));

            doneFile = LoggerUi.Text(string.Empty, "rp-done__file");
            hero.Add(doneFile);
            doneParts = LoggerUi.Box("rp-done__parts");
            hero.Add(doneParts);
            FillParts(builder);
            var actions = LoggerUi.Box("rp-done__actions");
            // With the scene export the zip holds the project's files as they are (purchased ones too): never a public channel.
            bool exported = builder.Steps.Any(s => s.Title == "Scene export" && s.State == ReportStepState.Done);
            if (made)
            {
                string path = builder.Result;
                actions.Add(LoggerUi.Pill("Show in " + FileBrowserName, () => EditorUtility.RevealInFinder(path), "primary", LoggerGlyph.Folder));
                actions.Add(LoggerUi.Pill("Copy a message to send", () =>
                {
                    EditorGUIUtility.systemCopyBuffer = Message(builder);
                    ShowToast(exported ? "Copied: send it with the file to someone you trust" : "Copied: paste it with the file in a help channel or a DM");
                }, null, LoggerGlyph.Copy));
                actions.Add(LoggerUi.Pill("Copy the path", () =>
                {
                    EditorGUIUtility.systemCopyBuffer = path;
                    ShowToast("Path copied");
                }, "ghost", LoggerGlyph.CopyStack));
            }
            else
            {
                actions.Add(LoggerUi.Pill("Try again", StartOver, "primary", LoggerGlyph.Reload));
            }

            hero.Add(actions);
            if (made)
            {
                hero.Add(LoggerUi.Text(exported
                    ? "Send the file only to someone you trust, not in a public channel: the scene export holds your scene's files as they are, purchased ones " +
                      "included. Unzip it to read README.md first."
                    : "Send the file to whoever helps you, or drop it in a help channel with the message. Unzip it to read README.md first.", "rp-done__hint"));
            }

            var burst = new Burst();
            hero.Add(burst);
            donePage.Add(hero);
            diagnosisHost = LoggerUi.Box("rp-diagnosis-host");
            donePage.Add(diagnosisHost);

            ShowPage(donePage, animate: true);
            orbit.Speed = 1.4f;
            orbit.Glyph = made ? LoggerGlyph.Check : LoggerGlyph.Warning;
            string folder = made ? Path.GetDirectoryName(builder.Result) : null;
            subtitle.text = !made ? "Nothing was saved."
                : string.Equals(folder, ReportOptions.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? "Saved on your Desktop."
                : "Saved in the " + Path.GetFileName(folder) + " folder.";
            subtitle.tooltip = made ? builder.Result : null;
            SetGo("New report", LoggerGlyph.Reload, quiet: true);
            destination.SetEnabled(made);
            UpdateDestination();
            hero.schedule.Execute(() =>
            {
                mark.Play(!made);
                if (made)
                {
                    var center = mark.ChangeCoordinatesTo(burst, mark.contentRect.center);
                    burst.Play(center);
                }
            }).StartingIn(80);
            ShowDiagnosis(builder);
        }

        // The file line and one chip per part, again when the diagnosis asked again changes them.
        private void FillParts(ReportBuilder builder)
        {
            if (doneParts == null)
            {
                return;
            }

            doneFile.text = builder.Result != null
                ? Path.GetFileName(builder.Result) + "  ·  " + SceneReport.Size(builder.ResultBytes)
                : builder.Error ?? "Something went wrong while packing it.";
            doneParts.Clear();
            foreach (var step in builder.Steps.Where(s => s.Detail != "Not included"))
            {
                bool ok = step.State == ReportStepState.Done;
                doneParts.Add(PartChip(step.Title, ok, step.Detail));
            }
        }

        private static VisualElement PartChip(string title, bool ok, string why)
        {
            var chip = LoggerUi.Box("rp-stat", PickingMode.Position);
            chip.AddToClassList(ok ? "rp-stat--done" : "rp-stat--warning");
            chip.Add(new LoggerIcon(ok ? LoggerGlyph.Check : LoggerGlyph.Warning));
            chip.Add(LoggerUi.Text(title, "rp-stat__text"));
            chip.tooltip = why;
            return chip;
        }

        /// <summary>
        /// A short message to paste beside the file: the project, the numbers, the note and the AI's headline, escaped for a
        /// chat (no link, formatting or mention can come from a log or the AI).
        /// </summary>
        private static string Message(ReportBuilder builder)
        {
            var context = builder.Context;
            var text = new StringBuilder();
            text.Append("Hi! Here is a report of my Unity project");
            if (context.Project != null)
            {
                text.Append(" (Unity ").Append(context.Project.Unity).Append(", ").Append(context.Project.Platform).Append(')');
            }

            if (context.Logs != null)
            {
                text.Append(": ").Append(LoggerUi.Plural(context.Logs.Errors, "error")).Append(" and ").Append(LoggerUi.Plural(context.Logs.Warnings, "warning")).Append(" logged");
            }

            text.Append(".\n");
            if (!string.IsNullOrWhiteSpace(context.Note))
            {
                text.Append("What's going wrong: ").Append(MarkdownText.ForChat(context.Note.Trim())).Append('\n');
            }

            if (context.Diagnosis != null && !string.IsNullOrWhiteSpace(context.Diagnosis.headline))
            {
                text.Append("The Orbiters AI thinks: ").Append(MarkdownText.ForChat(context.Diagnosis.headline.Trim())).Append('\n');
            }

            text.Append("The file: ").Append(MarkdownText.ForChat(Path.GetFileName(builder.Result))).Append(" (open README.md first)");
            return text.ToString();
        }

        // ---- AI diagnosis ---------------------------------------------------------------------------------------

        private void ShowDiagnosis(ReportBuilder builder)
        {
            if (diagnosisHost == null || !builder.Context.Has(ReportPart.Diagnosis))
            {
                return;
            }

            string key = builder.AskingAgain ? "asking" : builder.Diagnosis != null && builder.DiagnosisError == null ? "result" : "error:" + builder.DiagnosisError;
            if (key == shownDiagnosis)
            {
                UpdateDiagnosisFoot(builder);
                return;
            }

            shownDiagnosis = key;
            diagnosisHost.Clear();
            var card = LoggerUi.Box("rp-diagnosis");
            var head = LoggerUi.Box("rp-diagnosis__head");
            head.Add(new LoggerIcon(LoggerGlyph.Sparkle));
            head.Add(LoggerUi.Text("AI DIAGNOSIS", "rp-diagnosis__label"));
            card.Add(head);
            diagnosisHost.Add(card);

            if (builder.AskingAgain)
            {
                head.Add(LoggerUi.Spinner());
                card.Add(LoggerUi.Text("Orbiters is reading the report…", "rp-diagnosis__summary"));
                return;
            }

            var diagnosis = builder.Diagnosis;
            if (diagnosis == null || builder.DiagnosisError != null)
            {
                card.AddToClassList("rp-diagnosis--failed");
                card.Add(LoggerUi.Plain(builder.DiagnosisError ?? "No diagnosis.", "rp-diagnosis__summary"));
                if (!OrbitersLink.SignedIn && OrbitersLink.CanSignIn)
                {
                    var signIn = OrbitersLink.SignInElement("Connect, then ask again: the AI reads what this report found.", () => builder.AskAgain());
                    if (signIn != null)
                    {
                        card.Add(signIn);
                    }
                }
                else if (OrbitersLink.SignedIn)
                {
                    var again = LoggerUi.Pill("Ask again", builder.AskAgain, null, LoggerGlyph.Reload);
                    again.style.alignSelf = Align.FlexStart;
                    again.style.marginLeft = 0;
                    again.style.marginBottom = 8;
                    card.Add(again);
                }

                Reveal(card, 0);
                return;
            }

            var copy = LoggerUi.Icon(LoggerGlyph.Copy, "Copy the diagnosis (Markdown)", () =>
            {
                EditorGUIUtility.systemCopyBuffer = DiagnosisClient.Markdown(diagnosis, builder.Groups);
                ShowToast("Diagnosis copied");
            });
            head.Add(copy);
            // The AI's words as plain text: a log can talk it into writing rich text tags.
            int order = 0;
            if (!string.IsNullOrWhiteSpace(diagnosis.headline))
            {
                card.Add(Reveal(LoggerUi.Plain(diagnosis.headline.Trim(), "rp-diagnosis__headline"), order++));
            }

            if (!string.IsNullOrWhiteSpace(diagnosis.summary))
            {
                card.Add(Reveal(LoggerUi.Plain(diagnosis.summary.Trim(), "rp-diagnosis__summary"), order++));
            }

            var causes = diagnosis.causes ?? Array.Empty<DiagnosisCause>();
            for (int i = 0; i < causes.Length; i++)
            {
                card.Add(Reveal(CauseView(causes[i], i + 1, builder.Groups, open: i == 0), order++));
            }

            diagnosisFoot = LoggerUi.Text(string.Empty, "rp-diagnosis__foot");
            card.Add(Reveal(diagnosisFoot, order));
            UpdateDiagnosisFoot(builder);
        }

        private Label diagnosisFoot;

        // Whether the file has it changes once a diagnosis asked again is added to the zip.
        private void UpdateDiagnosisFoot(ReportBuilder builder)
        {
            if (diagnosisFoot == null || diagnosisFoot.panel == null)
            {
                return;
            }

            diagnosisFoot.text = "Written by Orbiters' AI from this report. It can be wrong: check before you change your project. " +
                                 (builder.DiagnosisInFile ? "Also in the report as diagnosis.md."
                                     : builder.AddingDiagnosis ? "Adding it to the report file…"
                                     : builder.DiagnosisFileError != null ? builder.DiagnosisFileError + " Copy it with the button above."
                                     : builder.Result != null ? "Not in the report file: copy it with the button above." : string.Empty);
        }

        private static VisualElement Reveal(VisualElement element, int order)
        {
            element.AddToClassList("rp-reveal");
            LoggerUi.Enter(element, "rp-reveal--hidden", 500 + order * 110);
            return element;
        }

        private static VisualElement CauseView(DiagnosisCause cause, int number, IReadOnlyList<LogDigestGroup> groups, bool open)
        {
            var view = LoggerUi.Box("rp-cause");
            var header = new Button();
            header.AddToClassList("rp-cause__header");
            header.Add(LoggerUi.Text(number.ToString(), "rp-cause__number"));
            var title = LoggerUi.Plain(cause.title, "rp-cause__title");
            title.pickingMode = PickingMode.Ignore;
            header.Add(title);
            string likelihood = DiagnosisClient.Likelihood(cause.likelihood);
            var chip = LoggerUi.Text(likelihood == "high" ? "Most likely" : likelihood == "low" ? "Less likely" : "Likely", "rp-likelihood");
            chip.AddToClassList("rp-likelihood--" + likelihood);
            chip.pickingMode = PickingMode.Ignore;
            header.Add(chip);
            var chevron = new LoggerIcon(LoggerGlyph.Chevron);
            chevron.AddToClassList("rp-cause__chevron");
            header.Add(chevron);
            view.Add(header);

            var body = LoggerUi.Box("rp-cause__body");
            if (!string.IsNullOrWhiteSpace(cause.explanation))
            {
                body.Add(LoggerUi.Plain(cause.explanation.Trim(), "rp-cause__explanation"));
            }

            var steps = cause.steps ?? Array.Empty<string>();
            for (int i = 0; i < steps.Length; i++)
            {
                var step = LoggerUi.Box("lg-explain__step");
                step.Add(LoggerUi.Text((i + 1).ToString(), "lg-explain__step-number"));
                step.Add(LoggerUi.Plain(steps[i], "lg-explain__step-text"));
                body.Add(step);
            }

            var related = (cause.related ?? Array.Empty<string>()).Select(id => groups?.FirstOrDefault(g => g.Id == id)).Where(g => g != null).ToList();
            if (related.Count > 0)
            {
                var logs = LoggerUi.Box("rp-cause__logs");
                foreach (var group in related)
                {
                    var log = LoggerUi.Plain(DiagnosisClient.FirstLine(group.Text), "rp-cause__log");
                    if (group.Level != LogLevel.Error)
                    {
                        log.AddToClassList("rp-cause__log--warning");
                    }

                    log.tooltip = RichText.Strip(group.Text) + "\n\n×" + group.Count + " · " + RichText.Strip(group.Source);
                    logs.Add(log);
                }

                body.Add(logs);
            }

            view.Add(body);
            void Set(bool value)
            {
                view.EnableInClassList("rp-cause--open", value);
                LoggerUi.Show(body, value);
            }

            Set(open);
            LoggerUi.Press(header, () => Set(!view.ClassListContains("rp-cause--open")));
            return view;
        }
    }
}
