using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// The brush tiles and brush settings of the sculpt and weight modes, built the same way for the Mixer window's pages
    /// and the Scene view overlay: a builder returns the element and a refresh that follows the tool's state.
    /// </summary>
    internal static class MixerBrushTiles
    {
        public static readonly (WeightBrushKind Kind, IconGlyph Glyph, string Tip)[] WeightBrushes =
        {
            (WeightBrushKind.Draw, IconGlyph.Sculpt, "Paint the weight toward the brush value; Ctrl toward 0."),
            (WeightBrushKind.Blur, IconGlyph.Broom, "Average the weight with the neighbours'."),
            (WeightBrushKind.Replace, IconGlyph.Layers, "Set the weight to the brush value outright; Ctrl to 0."),
        };

        /// <summary>Tiles of the sculpt brushes; picking one enters Sculpt mode. The returned action highlights the active one.</summary>
        public static VisualElement SculptTiles(bool shortTiles, Action onPicked, out Action refresh)
        {
            var tiles = MixerUi.Box("mx-tiles");
            var byBrush = new Dictionary<string, Button>();
            foreach (var brush in Brushes.All)
            {
                var b = brush;
                var tile = Tile(brush.Icon, brush.Name, brush.Description, shortTiles, () =>
                {
                    MixerSceneTool.ActiveBrush = b;
                    if (MixerSceneTool.Mode != MixerMode.Sculpt) MixerModes.Enter(MixerMode.Sculpt);
                    onPicked?.Invoke();
                });
                tiles.Add(tile);
                byBrush[brush.Name] = tile;
            }
            refresh = () =>
            {
                foreach (var pair in byBrush) pair.Value.EnableInClassList("mx-tile--on", MixerSceneTool.ActiveBrush.Name == pair.Key && MixerSceneTool.Mode == MixerMode.Sculpt);
            };
            return tiles;
        }

        /// <summary>Tiles of the weight brushes; picking one enters Weight paint.</summary>
        public static VisualElement WeightTiles(bool shortTiles, Action onPicked, out Action refresh)
        {
            var tiles = MixerUi.Box("mx-tiles");
            var byKind = new Dictionary<WeightBrushKind, Button>();
            foreach (var (kind, glyph, tip) in WeightBrushes)
            {
                var k = kind;
                var tile = Tile(glyph, kind.ToString(), tip, shortTiles, () =>
                {
                    MixerSceneTool.WeightKind = k;
                    if (MixerSceneTool.Mode != MixerMode.Weights) MixerModes.Enter(MixerMode.Weights);
                    onPicked?.Invoke();
                });
                tiles.Add(tile);
                byKind[kind] = tile;
            }
            refresh = () =>
            {
                foreach (var pair in byKind) pair.Value.EnableInClassList("mx-tile--on", MixerSceneTool.WeightKind == pair.Key && MixerSceneTool.Mode == MixerMode.Weights);
            };
            return tiles;
        }

        private static Button Tile(IconGlyph glyph, string name, string tooltip, bool shortTile, Action action)
        {
            var tile = new Button { text = string.Empty, tooltip = tooltip };
            tile.AddToClassList("mx-tile");
            if (shortTile) tile.AddToClassList("mx-tile--short");
            var icon = new VectorIcon(glyph) { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("mx-tile__icon");
            tile.Add(icon);
            tile.Add(MixerUi.Text(name, "mx-tile__name"));
            MixerUi.Press(tile, action);
            return tile;
        }

        /// <summary>Radius and strength sliders, falloff and symmetry (sculpt) or weight value and auto normalize (weights).</summary>
        public static VisualElement Settings(bool weights, out Action refresh)
        {
            var box = MixerUi.Box("mx-overlay__settings");
            var radiusSlider = new Slider(0f, 1f) { value = RadiusToSlider(MixerSceneTool.Radius) };
            radiusSlider.AddToClassList("mx-slider");
            var radiusValue = MixerUi.Text(MixerUi.Metres(MixerSceneTool.Radius), "mx-chip mx-chip__text");
            radiusSlider.RegisterValueChangedCallback(evt => { MixerSceneTool.Radius = SliderToRadius(evt.newValue); radiusValue.text = MixerUi.Metres(MixerSceneTool.Radius); });
            var strengthSlider = new Slider(0f, 1f) { value = MixerSceneTool.Strength };
            strengthSlider.AddToClassList("mx-slider");
            var strengthValue = MixerUi.Text(MixerUi.Percent(MixerSceneTool.Strength), "mx-chip mx-chip__text");
            strengthSlider.RegisterValueChangedCallback(evt => { MixerSceneTool.Strength = evt.newValue; strengthValue.text = MixerUi.Percent(evt.newValue); });
            Slider weightSlider = null;
            Label weightValue = null;
            ToggleSwitch normalize = null, symmetry = null;
            SegmentedControl falloff = null;
            if (weights)
            {
                weightSlider = new Slider(0f, 1f) { value = MixerSceneTool.WeightValue };
                weightSlider.AddToClassList("mx-slider");
                weightValue = MixerUi.Text(MixerSceneTool.WeightValue.ToString("0.00"), "mx-chip mx-chip__text");
                weightSlider.RegisterValueChangedCallback(evt => { MixerSceneTool.WeightValue = evt.newValue; weightValue.text = evt.newValue.ToString("0.00"); });
                box.Add(MixerUi.Setting("Weight", "What Draw and Replace paint toward", Pair(weightSlider, weightValue), first: true));
            }
            box.Add(MixerUi.Setting("Radius", "How far the brush reaches, in the world", Pair(radiusSlider, radiusValue), first: !weights));
            box.Add(MixerUi.Setting("Strength", "How much each step moves", Pair(strengthSlider, strengthValue)));
            if (weights)
            {
                normalize = new ToggleSwitch(MixerSceneTool.AutoNormalize, on => MixerSceneTool.AutoNormalize = on);
                box.Add(MixerUi.Setting("Normalize", "Keep each vertex's bone weights summing to 1", normalize));
            }
            else
            {
                falloff = new SegmentedControl(new[] { "Smooth", "Sphere", "Linear", "Sharp", "Flat" }, index => MixerSceneTool.Falloff = (Falloff)index);
                falloff.AddToClassList("orb-segmented--compact");
                falloff.SetIndex((int)MixerSceneTool.Falloff);
                box.Add(MixerUi.Setting("Falloff", "How the effect fades toward the edge", falloff));
                symmetry = new ToggleSwitch(MixerSceneTool.Symmetry, on => MixerSceneTool.Symmetry = on);
                box.Add(MixerUi.Setting("Symmetry", "Mirror every stroke across the mesh's X axis", symmetry));
            }
            refresh = () =>
            {
                if (Math.Abs(radiusSlider.value - RadiusToSlider(MixerSceneTool.Radius)) > 1e-4f) radiusSlider.SetValueWithoutNotify(RadiusToSlider(MixerSceneTool.Radius));
                radiusValue.text = MixerUi.Metres(MixerSceneTool.Radius);
                if (Math.Abs(strengthSlider.value - MixerSceneTool.Strength) > 1e-4f) strengthSlider.SetValueWithoutNotify(MixerSceneTool.Strength);
                strengthValue.text = MixerUi.Percent(MixerSceneTool.Strength);
                if (weightSlider != null && Math.Abs(weightSlider.value - MixerSceneTool.WeightValue) > 1e-4f) { weightSlider.SetValueWithoutNotify(MixerSceneTool.WeightValue); weightValue.text = MixerSceneTool.WeightValue.ToString("0.00"); }
                normalize?.SetValueWithoutNotify(MixerSceneTool.AutoNormalize);
                symmetry?.SetValueWithoutNotify(MixerSceneTool.Symmetry);
                if (falloff != null && falloff.Index != (int)MixerSceneTool.Falloff) falloff.SetIndex((int)MixerSceneTool.Falloff);
            };
            return box;
        }

        public static VisualElement Pair(VisualElement a, VisualElement b)
        {
            var row = MixerUi.Row("mx-pair");
            row.Add(a);
            row.Add(b);
            return row;
        }

        // Radius on a log scale from 1 mm to 2 m.
        public static float RadiusToSlider(float radius) => Mathf.InverseLerp(Mathf.Log10(0.001f), Mathf.Log10(2f), Mathf.Log10(Mathf.Clamp(radius, 0.001f, 2f)));
        public static float SliderToRadius(float value) => Mathf.Pow(10f, Mathf.Lerp(Mathf.Log10(0.001f), Mathf.Log10(2f), value));
    }
}
