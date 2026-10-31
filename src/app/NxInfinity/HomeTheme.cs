using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;

namespace NxInfinity;

internal sealed class HomeTheme
{
    internal readonly BoxShadows CardShadow = BoxShadows.Parse("0 4 14 0 #102B3441");

    internal readonly BoxShadows FocusShadow = BoxShadows.Parse("0 8 24 0 #294D52D9");

    internal ThemeChoice Choice { get; }

    internal ThemePreset Palette { get; }

    internal IBrush Canvas { get; }

    internal IBrush Surface { get; }

    internal IBrush Ink { get; }

    internal IBrush Muted { get; }

    internal IBrush Focus { get; }

    internal IBrush Soft { get; }

    internal IBrush Line { get; }

    internal IBrush AccentInk { get; }

    internal IBrush Dock { get; } = Brush.Parse("#454A4E");

    internal IBrush Red { get; } = Brush.Parse("#E50024");

    internal HomeTheme(ThemeChoice? choice = null)
    {
        Choice = choice ?? new ThemeChoice("home");
        Palette = ThemePresets.Resolve(Choice);
        Canvas = Brush.Parse(Palette.Canvas);
        Surface = Brush.Parse(Palette.Surface);
        Ink = Brush.Parse(Palette.Ink);
        Muted = Brush.Parse(Palette.Muted);
        Focus = Brush.Parse(ThemePresets.FocusColor(Palette));
        Soft = Brush.Parse(Palette.Hover);
        Line = Brush.Parse(Palette.Line);
        AccentInk = Brush.Parse(ThemePresets.AccentText(Palette with
        {
            Accent = ThemePresets.FocusColor(Palette)
        }));
    }

    internal TextBlock Text(string text, double size = 15.0, IBrush? color = null, FontWeight? weight = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = (color ?? Ink),
            FontWeight = (weight ?? FontWeight.Normal),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    internal Styles Styles()
    {
        return new Styles
        {
            new Style((Selector? x) => x.OfType<Button>().Class("home-control"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Surface),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Ink),
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(2.0)),
                    (SetterBase)new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(30.0)),
                    (SetterBase)new Setter(Layoutable.MinHeightProperty, 44.0)
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("home-control").Class(":pointerover"))
            {
                Setters = { (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#9B9FD9")) }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("home-control").Class(":focus-visible"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Focus),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(3.0))
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("home-control").Class("home-filter-selected"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Focus),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, AccentInk)
                }
            }
        };
    }

    internal Control Adapt(Control root)
    {
        foreach (Control item in root.GetLogicalDescendants().OfType<Control>().Prepend(root))
        {
            if (item.Classes.Contains("game-artwork") || item.GetLogicalAncestors().OfType<Control>().Any((Control c) => c.Classes.Contains("game-artwork")))
            {
                continue;
            }
            if (item is TextBlock textBlock)
            {
                textBlock.Foreground = Map(textBlock.Foreground);
            }
            if (item is NavGlyph navGlyph)
            {
                navGlyph.Stroke = Map(navGlyph.Stroke) ?? Ink;
                navGlyph.InvalidateVisual();
            }
            if (item is Border border)
            {
                border.Background = Map(border.Background);
                border.BorderBrush = Map(border.BorderBrush);
            }
            if (item is Panel panel)
            {
                panel.Background = Map(panel.Background);
            }
            if (item is Button button)
            {
                button.Classes.Add("home-control");
                bool flag = button.Classes.Contains("primary") || button.Classes.Contains("selected");
                button.Background = (flag ? Focus : Soft);
                button.Foreground = (flag ? AccentInk : Ink);
                foreach (TextBlock item2 in button.GetLogicalDescendants().OfType<TextBlock>())
                {
                    item2.Foreground = button.Foreground;
                }
                button.CornerRadius = new CornerRadius(10.0);
            }
            if (item is TextBox textBox)
            {
                textBox.Background = Surface;
                textBox.Foreground = Ink;
                textBox.BorderBrush = Line;
            }
        }
        return root;
        IBrush? Map(IBrush? value)
        {
            if (value != VisualTheme.Ink)
            {
                if (value != VisualTheme.Muted)
                {
                    if (value != VisualTheme.Blue && value != VisualTheme.PrimaryFill)
                    {
                        if (value != VisualTheme.Canvas && value != VisualTheme.Sidebar)
                        {
                            if (value != VisualTheme.Panel)
                            {
                                if (value != VisualTheme.Soft)
                                {
                                    if (value != VisualTheme.Line)
                                    {
                                        if (value != VisualTheme.AccentSoft)
                                        {
                                            if (value != VisualTheme.Green)
                                            {
                                                if (value != VisualTheme.Red)
                                                {
                                                    return value;
                                                }
                                                return Brush.Parse(Palette.Night ? "#FF99A2" : "#B3263C");
                                            }
                                            return Brush.Parse(Palette.Night ? "#A0CEA9" : "#247144");
                                        }
                                        return Soft;
                                    }
                                    return Line;
                                }
                                return Soft;
                            }
                            return Surface;
                        }
                        return Canvas;
                    }
                    return Focus;
                }
                return Muted;
            }
            return Ink;
        }
    }
}
