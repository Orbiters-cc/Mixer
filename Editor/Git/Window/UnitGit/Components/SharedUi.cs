using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private VisualElement BuildSectionHeader(string title, string detail)
        {
            var header = new VisualElement();
            header.AddToClassList("unitgit-section-header");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("unitgit-section-title");
            header.Add(titleLabel);

            var detailLabel = new Label(detail);
            detailLabel.AddToClassList("unitgit-section-detail");
            header.Add(detailLabel);

            return header;
        }

        private VisualElement BuildMessagePanel(string title, string body)
        {
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-message-panel");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("unitgit-message-title");
            panel.Add(titleLabel);

            var bodyLabel = new Label(body);
            bodyLabel.AddToClassList("unitgit-message-body");
            panel.Add(bodyLabel);

            return panel;
        }

        private VisualElement BuildEmptyState(string text)
        {
            var label = new Label(text);
            label.AddToClassList("unitgit-empty");
            return label;
        }

        private Label BuildHeaderLabel(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList("unitgit-log-header-label");
            label.AddToClassList(className);
            return label;
        }

    }
}
