using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;

namespace NxInfinity;

public sealed class DuneTheme
{
    private static FontFamily? _font;

    public ThemePreset Palette { get; }

    public bool Night => Palette.Night;

    public IBrush Canvas { get; }

    public IBrush Surface { get; }

    public IBrush Ink { get; }

    public IBrush Muted { get; }

    public IBrush Line { get; }

    public IBrush Hover { get; }

    public IBrush Accent { get; }

    public IBrush AccentInk { get; }

    public static FontFamily Font => _font ?? (_font = FontManager.Current.SystemFonts.FirstOrDefault((FontFamily f) => f.Name == "Trebuchet MS") ?? new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"));

    public DuneTheme(bool night = false)
        : this(new ThemeChoice(night ? "night" : "dune"))
    {
    }

    public DuneTheme(ThemeChoice choice)
    {
        Palette = ThemePresets.Resolve(choice);
        Canvas = Brush.Parse(Palette.Canvas);
        Surface = Brush.Parse(Palette.Surface);
        Ink = Brush.Parse(Palette.Ink);
        Muted = Brush.Parse(Palette.Muted);
        Line = Brush.Parse(Palette.Line);
        Hover = Brush.Parse(Palette.Hover);
        Accent = Brush.Parse(Palette.Accent);
        AccentInk = Brush.Parse(ThemePresets.AccentText(Palette));
    }

    public TextBlock Text(string text, double size = 14.0, bool muted = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = Font,
            Foreground = (muted ? Muted : Ink),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public Styles Styles()
    {
        return new Styles
        {
            new Style((Selector? x) => x.OfType<Button>())
            {
                Setters =
                {
                    (SetterBase)new Setter(Layoutable.MinHeightProperty, 34.0),
                    (SetterBase)new Setter(TemplatedControl.PaddingProperty, new Thickness(12.0, 6.0)),
                    (SetterBase)new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4.0)),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1.0)),
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Surface),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Ink),
                    (SetterBase)new Setter(TemplatedControl.FontSizeProperty, 13.0),
                    (SetterBase)new Setter(TemplatedControl.FontFamilyProperty, Font),
                    (SetterBase)new Setter(TemplatedControl.FontWeightProperty, FontWeight.Normal)
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("dune-quiet"))
            {
                Setters = { (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent) }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("dune-active"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Hover),
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse(ThemePresets.FocusColor(Palette)))
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class(":pointerover"))
            {
                Setters = { (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Line) }
            },
            new Style((Selector? x) => x.OfType<Button>().Class(":focus-visible"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse(ThemePresets.FocusColor(Palette))),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(2.0))
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("dune-cover"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.PaddingProperty, new Thickness(0.0)),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0.0)),
                    (SetterBase)new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(1.0)),
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent)
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("dune-cover").Class("dune-selected"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse(ThemePresets.FocusColor(Palette))),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(2.0))
                }
            },
            new Style((Selector? x) => x.OfType<TextBox>())
            {
                Setters =
                {
                    (SetterBase)new Setter(Layoutable.MinHeightProperty, 32.0),
                    (SetterBase)new Setter(TemplatedControl.FontSizeProperty, 13.0),
                    (SetterBase)new Setter(TemplatedControl.FontFamilyProperty, Font),
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Surface),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Ink),
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Line),
                    (SetterBase)new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4.0))
                }
            },
            new Style((Selector? x) => x.OfType<ComboBox>())
            {
                Setters =
                {
                    (SetterBase)new Setter(Layoutable.MinHeightProperty, 34.0),
                    (SetterBase)new Setter(TemplatedControl.FontSizeProperty, 13.0),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Ink)
                }
            }
        };
    }

    public Control Adapt(Control root)
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
                textBlock.FontFamily = Font;
                if (textBlock.FontSize >= 26.0)
                {
                    textBlock.FontSize = 24.0;
                }
            }
            if (item is NavGlyph navGlyph)
            {
                navGlyph.Stroke = Map(navGlyph.Stroke) ?? Ink;
                navGlyph.InvalidateVisual();
            }
            if (item is Panel panel)
            {
                panel.Background = Map(panel.Background);
            }
            if (item is Border border)
            {
                border.Background = Map(border.Background);
                border.BorderBrush = Map(border.BorderBrush);
                if (border.Classes.Contains("service-section"))
                {
                    border.Background = Brushes.Transparent;
                    border.BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0);
                    border.CornerRadius = new CornerRadius(0.0);
                    border.Padding = new Thickness(0.0, 12.0, 0.0, 18.0);
                }
            }
            if (item is Button button && !button.Classes.Contains("dune-cover"))
            {
                bool flag = button.Classes.Contains("primary");
                bool flag2 = button.Classes.Contains("selected");
                IBrush background;
                if (!flag)
                {
                    if (!flag2)
                    {
                        if (!button.Classes.Contains("subtle"))
                        {
                            background = Surface;
                        }
                        else
                        {
                            IBrush transparent = Brushes.Transparent;
                            background = transparent;
                        }
                    }
                    else
                    {
                        background = Hover;
                    }
                }
                else
                {
                    background = Accent;
                }
                button.Background = background;
                if (flag2)
                {
                    button.BorderBrush = Brush.Parse(ThemePresets.FocusColor(Palette));
                    button.BorderThickness = new Thickness(0.0, 0.0, 0.0, 2.0);
                }
                button.Foreground = (flag ? AccentInk : Ink);
                button.CornerRadius = new CornerRadius(4.0);
                button.FontFamily = Font;
                if (flag)
                {
                    foreach (TextBlock item2 in button.GetLogicalDescendants().OfType<TextBlock>())
                    {
                        item2.Foreground = AccentInk;
                    }
                }
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
                    if (value != VisualTheme.Canvas && value != VisualTheme.Sidebar)
                    {
                        if (value != VisualTheme.Panel && value != VisualTheme.Soft)
                        {
                            if (value != VisualTheme.Line)
                            {
                                if (value != VisualTheme.Blue && value != VisualTheme.PrimaryFill)
                                {
                                    if (value != VisualTheme.AccentSoft)
                                    {
                                        if (value != VisualTheme.Green)
                                        {
                                            if (value != VisualTheme.Red)
                                            {
                                                return value;
                                            }
                                            return Brush.Parse(Night ? "#FF99A2" : "#AC2032");
                                        }
                                        return Brush.Parse(Night ? "#A0CEA9" : "#246E38");
                                    }
                                    return Surface;
                                }
                                return Ink;
                            }
                            return Line;
                        }
                        return Surface;
                    }
                    return Canvas;
                }
                return Muted;
            }
            return Ink;
        }
    }
}
