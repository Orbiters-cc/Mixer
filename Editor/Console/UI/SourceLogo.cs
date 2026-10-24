using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;
using Orbiters.Mixer.Editor;

namespace Orbiters.Logger.Editor
{
    internal enum SourceLogoKind
    {
        None,
        Mcb,
        MyAvatar,
        ReFit,
        Mixer
    }

    /// <summary>
    /// Micro logos of the Orbiters tools for source chips, drawn from each tool's own geometry (MCB's polygons, ReFit's
    /// bars, Mixer's M, My Avatar's SVG) so they stay crisp at 9 px and need no texture or package reference.
    /// </summary>
    internal sealed class SourceLogo : VisualElement
    {
        private static readonly Vector2[][] McbPaths =
        {
            new[] { V(0.5f, 0.5f), V(0.5f, 207.5f), V(224.5f, 207.5f), V(193.5f, 104f), V(224.5f, 0.5f), V(142.442f, 0.5f), V(103.471f, 68f), V(64.5f, 0.5f) },
            new[] { V(450.5f, 0.5f), V(246.5f, 0.5f), V(212.5f, 104f), V(246.5f, 207.5f), V(450.5f, 207.5f), V(421.5f, 143.5f), V(328.5f, 143.5f), V(328.5f, 68f), V(421.5f, 68f) },
            new[] { V(606.5f, 0.5f), V(470.5f, 0.5f), V(427.755f, 104f), V(470.5f, 207.5f), V(606.5f, 207.5f), V(643.5f, 155.5f), V(606.5f, 104f), V(643.5f, 52f) }
        };

        private static readonly Rect[] ReFitBars =
        {
            new Rect(0f, 0f, 39f, 3.72f), new Rect(0f, 12.69f, 39f, 9.87f), new Rect(0f, 31.92f, 9.27f, 3.08f),
            new Rect(14.99f, 31.92f, 9.27f, 3.08f), new Rect(29.73f, 31.92f, 9.27f, 3.08f)
        };

        // My Avatar's logo (309 x 258); the fifth path is filled even-odd.
        private static readonly string[] MyAvatarPaths =
        {
            "M15.7479 118.748C2.74792 89.7475 -3.7487 61.7451 2.25195 55.7451C10.3145 47.6835 32.2519 65.2468 44.752 70.2468C51.252 49.7466 52.3968 3.82939 62.2481 0.247256C72.0997 -3.33499 91.0814 32.9139 102.748 50.2473C115.248 66.0802 138.151 99.1481 131.748 108.247C122.248 121.747 109.248 105.246 92.7481 77.7468C79.5481 55.7472 75.2558 51.7434 71.752 43.2451C70.4186 56.7451 63.852 86.6451 58.252 92.2451C53.252 97.2451 34.9148 88.4135 24.7481 83.2468C25.9148 91.9135 32.5773 109.551 37.752 121.747C44.752 138.245 58.252 154.745 52.752 160.245C47.252 165.745 28.7479 147.748 15.7479 118.748Z",
            "M157.248 142.747L164.248 121.747C167.848 110.947 169.415 104.913 169.748 103.247C172.581 98.2468 179.448 86.5468 184.248 79.7468C189.048 72.9468 195.085 63.2451 198.252 59.2451C192.585 60.4118 181.861 64.6314 176.248 70.2468C166.752 79.7468 164.081 84.4135 161.248 87.7468C159.581 85.5802 154.748 80.1468 148.748 75.7468C142.748 71.3468 135.752 68.2468 128.748 68.2468L152.248 103.247C152.581 116.413 154.048 142.747 157.248 142.747Z",
            "M142.248 235.747C137.248 243.247 152.248 257.747 154.748 257.247C157.248 256.747 172.248 240.747 169.748 238.247C167.248 235.747 163.748 230.247 154.748 231.247C145.748 232.247 144.748 231.747 142.248 235.747Z",
            "M129.248 177.247C123.248 160.747 97.7052 142.635 79.2481 159.247C60.791 175.858 63.7481 190.247 65.2481 190.247C69.1532 190.247 82.2481 165.247 96.2481 168.747C110.248 172.247 112.748 177.247 116.248 185.247C116.685 186.245 117.075 187.547 117.433 189.061C114.989 187.222 112.429 186.439 110.248 187.747C105.248 190.747 104.252 198.982 106.748 207.247C108.409 212.745 115.953 217.747 118.748 216.747C119.516 216.472 120.579 215.518 121.655 214.144C122.566 218.07 123.688 220.747 125.248 220.747C134.251 220.747 134.249 207.55 134.248 199.52L134.248 199.247C134.248 194.148 133.248 188.247 129.248 177.247Z",
            "M223.248 54.2468C240.252 38.7451 280.248 13.2468 295.748 21.7468C310.361 29.7603 312.086 80.4363 302.181 113.863C305.31 118.144 306.455 121.596 304.248 123.247C302.914 124.245 301.027 124.387 298.725 123.888C287.273 154.645 276.261 175.512 265.248 170.245C254.206 164.964 271.3 134.748 278.845 113.51C270.856 108.259 262.268 102.13 254.752 97.7451C243.76 91.3319 233.081 87.2288 224.434 84.3926C215.211 97.1598 200.277 116.001 191.752 108.247C180.756 98.2451 205.698 70.2468 223.248 54.2468ZM281.748 46.2468C277.883 44.7009 252.808 58.3904 237.696 70.2281C247.675 74.0982 258.924 79.1065 265.248 82.7468C270.369 85.6947 276.776 90.1841 282.958 95.1387C285.522 75.0135 286.123 47.9969 281.748 46.2468Z",
            "M180.963 176.711C187.037 160.007 212.895 141.673 231.579 158.489C250.264 175.305 247.27 189.871 245.752 189.871C241.799 189.871 228.542 164.563 214.37 168.106C200.197 171.649 197.666 176.711 194.123 184.809C193.681 185.82 193.286 187.138 192.924 188.67C195.398 186.809 197.99 186.016 200.197 187.34C205.259 190.377 207.14 199.247 203.74 207.08C200.197 215.245 194.422 217.71 191.593 216.698C190.815 216.419 189.739 215.454 188.65 214.063C187.727 218.037 186.592 220.747 185.012 220.747C175.898 220.747 175.9 207.387 175.901 199.258L175.901 198.982C175.901 193.82 176.914 187.846 180.963 176.711Z"
        };

        private static List<(char command, float[] args)>[] myAvatarCommands;
        private SourceLogoKind kind;
        private Color color = Color.white;

        public SourceLogo()
        {
            AddToClassList("lg-source-logo");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        public static SourceLogoKind For(LogSource source)
        {
            switch (source?.Key)
            {
                case "mcb":
                    return SourceLogoKind.Mcb;
                case "my avatar":
                    return SourceLogoKind.MyAvatar;
                case "refit":
                    return SourceLogoKind.ReFit;
                case "mixer":
                case "mixer console":
                case "mixer git":
                    return SourceLogoKind.Mixer;
                default:
                    return SourceLogoKind.None;
            }
        }

        /// <summary>Width over height of each logo.</summary>
        public static float Aspect(SourceLogoKind kind) =>
            kind == SourceLogoKind.Mcb ? 645f / 208f : kind == SourceLogoKind.MyAvatar ? 309f / 258f : kind == SourceLogoKind.ReFit ? 39f / 35f :
            kind == SourceLogoKind.Mixer ? MixerLogo.Size.x / MixerLogo.Size.y : 1f;

        public void Set(SourceLogoKind value, Color tint, float height)
        {
            style.display = value == SourceLogoKind.None ? DisplayStyle.None : DisplayStyle.Flex;
            style.height = height;
            style.width = Mathf.Round(height * Aspect(value) * 10f) / 10f;
            if (value != kind || tint != color)
            {
                kind = value;
                color = tint;
                MarkDirtyRepaint();
            }
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (kind == SourceLogoKind.None || rect.width <= 0 || rect.height <= 0)
            {
                return;
            }

            if (kind == SourceLogoKind.Mixer)
            {
                MixerLogo.Fill(context.painter2D, rect, color);
                return;
            }

            Vector2 size = kind == SourceLogoKind.Mcb ? new Vector2(645f, 208f) : kind == SourceLogoKind.MyAvatar ? new Vector2(309f, 258f)
                : new Vector2(39f, 35f);
            float scale = Mathf.Min(rect.width / size.x, rect.height / size.y);
            var origin = new Vector2(rect.x + (rect.width - size.x * scale) * 0.5f, rect.y + (rect.height - size.y * scale) * 0.5f);
            Vector2 P(float x, float y) => origin + new Vector2(x, y) * scale;
            var painter = context.painter2D;
            painter.fillColor = color;
            switch (kind)
            {
                case SourceLogoKind.Mcb:
                    foreach (var path in McbPaths)
                    {
                        painter.BeginPath();
                        painter.MoveTo(P(path[0].x, path[0].y));
                        for (int i = 1; i < path.Length; i++)
                        {
                            painter.LineTo(P(path[i].x, path[i].y));
                        }

                        painter.ClosePath();
                        painter.Fill();
                    }

                    break;
                case SourceLogoKind.ReFit:
                    foreach (var bar in ReFitBars)
                    {
                        painter.BeginPath();
                        painter.MoveTo(P(bar.xMin, bar.yMin));
                        painter.LineTo(P(bar.xMax, bar.yMin));
                        painter.LineTo(P(bar.xMax, bar.yMax));
                        painter.LineTo(P(bar.xMin, bar.yMax));
                        painter.ClosePath();
                        painter.Fill();
                    }

                    break;
                case SourceLogoKind.MyAvatar:
                    var commands = MyAvatarCommands();
                    for (int i = 0; i < commands.Length; i++)
                    {
                        painter.BeginPath();
                        foreach (var (command, a) in commands[i])
                        {
                            switch (command)
                            {
                                case 'M':
                                    painter.MoveTo(P(a[0], a[1]));
                                    break;
                                case 'L':
                                    painter.LineTo(P(a[0], a[1]));
                                    break;
                                case 'C':
                                    painter.BezierCurveTo(P(a[0], a[1]), P(a[2], a[3]), P(a[4], a[5]));
                                    break;
                                default:
                                    painter.ClosePath();
                                    break;
                            }
                        }

                        painter.Fill(i == 4 ? FillRule.OddEven : FillRule.NonZero);
                    }

                    break;
            }
        }

        // Absolute M, L, C and Z only: what the logo uses.
        private static List<(char command, float[] args)>[] MyAvatarCommands()
        {
            if (myAvatarCommands != null)
            {
                return myAvatarCommands;
            }

            var parsed = new List<(char, float[])>[MyAvatarPaths.Length];
            for (int p = 0; p < MyAvatarPaths.Length; p++)
            {
                var list = new List<(char, float[])>();
                string d = MyAvatarPaths[p];
                int i = 0;
                while (i < d.Length)
                {
                    char c = d[i];
                    if (c != 'M' && c != 'L' && c != 'C' && c != 'Z')
                    {
                        i++;
                        continue;
                    }

                    i++;
                    int count = c == 'C' ? 6 : c == 'Z' ? 0 : 2;
                    var args = new float[count];
                    for (int n = 0; n < count; n++)
                    {
                        while (i < d.Length && (d[i] == ' ' || d[i] == ','))
                        {
                            i++;
                        }

                        int start = i;
                        if (i < d.Length && d[i] == '-')
                        {
                            i++;
                        }

                        while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.'))
                        {
                            i++;
                        }

                        args[n] = float.Parse(d.Substring(start, i - start), CultureInfo.InvariantCulture);
                    }

                    list.Add((c, args));
                }

                parsed[p] = list;
            }

            myAvatarCommands = parsed;
            return parsed;
        }

        private static Vector2 V(float x, float y) => new Vector2(x, y);
    }

    /// <summary>A source as a small coloured chip: its tool's micro logo when it has one, then its name.</summary>
    internal sealed class SourceChip : VisualElement
    {
        private readonly SourceLogo logo = new SourceLogo();
        private readonly Label label = new Label { pickingMode = PickingMode.Ignore };
        private LogSource shown;
        private Color shownColor;

        public SourceChip(string className)
        {
            AddToClassList("lg-source-chip");
            AddToClassList(className);
            pickingMode = PickingMode.Ignore;
            label.AddToClassList("lg-source-chip__label");
            Add(logo);
            Add(label);
        }

        public void Set(LogSource source, float logoHeight = 9f)
        {
            var color = source?.Color ?? Color.white;
            if (ReferenceEquals(source, shown) && color == shownColor)
            {
                return;
            }

            shown = source;
            shownColor = color;
            label.text = source?.Name ?? string.Empty;
            label.style.color = color;
            style.backgroundColor = new Color(color.r, color.g, color.b, 0.13f);
            var kind = SourceLogo.For(source);
            logo.Set(kind, color, logoHeight);
            // The MCB logo spells its name: the text would only repeat it.
            label.style.display = kind == SourceLogoKind.Mcb ? DisplayStyle.None : DisplayStyle.Flex;
            tooltip = kind == SourceLogoKind.Mcb ? source.Name : null;
            EnableInClassList("lg-source-chip--logo", kind != SourceLogoKind.None);
            EnableInClassList("lg-source-chip--logo-only", kind == SourceLogoKind.Mcb);
        }
    }
}
