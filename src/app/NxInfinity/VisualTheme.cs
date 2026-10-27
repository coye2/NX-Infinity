using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace NxInfinity;

public static class VisualTheme
{
    public static readonly IBrush Ink = Brush.Parse("#ECECE8");

    public static readonly IBrush Muted = Brush.Parse("#ACACA8");

    public static readonly IBrush Blue = Brush.Parse("#FF5268");

    public static readonly IBrush PrimaryFill = Brush.Parse("#D32F46");

    public static readonly IBrush Red = Brush.Parse("#FF8592");

    public static readonly IBrush Line = Brush.Parse("#30302F");

    public static readonly IBrush Soft = Brush.Parse("#252525");

    public static readonly IBrush Canvas = Brush.Parse("#141414");

    public static readonly IBrush Panel = Brush.Parse("#1B1B1B");

    public static readonly IBrush Sidebar = Brush.Parse("#171717");

    public static readonly IBrush AccentSoft = Brush.Parse("#2C2425");

    public static readonly IBrush Green = Brush.Parse("#8DDDAD");

    public static Styles Create()
    {
        return new Styles
        {
            new Style((Selector? x) => x.OfType<Window>())
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.FontSizeProperty, 15.0),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Ink)
                }
            },
            new Style((Selector? x) => x.OfType<Button>())
            {
                Setters =
                {
                    (SetterBase)new Setter(Control.FocusAdornerProperty, null),
                    (SetterBase)new Setter(Layoutable.MinHeightProperty, 44.0),
                    (SetterBase)new Setter(TemplatedControl.PaddingProperty, new Thickness(16.0, 10.0)),
                    (SetterBase)new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3.0)),
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Soft),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(2.0)),
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Ink),
                    (SetterBase)new Setter(TemplatedControl.FontWeightProperty, FontWeight.Medium)
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("primary"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, PrimaryFill),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Brushes.White)
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("danger"))
            {
                Setters = { (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Red) }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("nav"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    (SetterBase)new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Muted)
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("selected"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, AccentSoft),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Ink)
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("subtle"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Muted)
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class("game-card"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.PaddingProperty, new Thickness(7.0)),
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(2.0)),
                    (SetterBase)new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3.0))
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class(":pointerover"))
            {
                Setters = { (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#5A6377")) }
            },
            new Style((Selector? x) => x.OfType<Button>().Class(":focus-visible"))
            {
                Setters =
                {
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Blue),
                    (SetterBase)new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(2.0))
                }
            },
            new Style((Selector? x) => x.OfType<Button>().Class(":disabled"))
            {
                Setters = { (SetterBase)new Setter(Visual.OpacityProperty, 0.45) }
            },
            new Style((Selector? x) => x.OfType<TextBox>())
            {
                Setters =
                {
                    (SetterBase)new Setter(Layoutable.MinHeightProperty, 44.0),
                    (SetterBase)new Setter(TemplatedControl.FontSizeProperty, 14.0),
                    (SetterBase)new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3.0)),
                    (SetterBase)new Setter(TemplatedControl.BackgroundProperty, Panel),
                    (SetterBase)new Setter(TemplatedControl.BorderBrushProperty, Line),
                    (SetterBase)new Setter(TemplatedControl.ForegroundProperty, Ink)
                }
            },
            new Style((Selector? x) => x.OfType<ComboBox>())
            {
                Setters =
                {
                    (SetterBase)new Setter(Layoutable.MinHeightProperty, 44.0),
                    (SetterBase)new Setter(Layoutable.MinWidthProperty, 150.0)
                }
            }
        };
    }

    public static TextBlock Text(string text, double size = 15.0, IBrush? color = null, FontWeight? weight = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = (color ?? Ink),
            FontWeight = (weight ?? FontWeight.Normal),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static Button Button(string label, Func<Task> action, string? style = null)
    {
        Button button = new Button
        {
            Content = label
        };
        if (style != null)
        {
            button.Classes.Add(style);
        }
        button.Click += async (object? _, RoutedEventArgs _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                if (TopLevel.GetTopLevel(button) is Window { IsVisible: not false } window)
                {
                    await Dialogs.MessageAsync(window, UiText.Get("Action needs attention"), ex.GetType().Name + "\n" + ex.Message);
                }
            }
        };
        return button;
    }

    public static Button Button(string label, Action action, string? style = null)
    {
        return Button(label, () =>
        {
            action();
            return Task.CompletedTask;
        }, style);
    }

    public static TextBlock LocalizedText(string source, double size = 15.0, IBrush? color = null, FontWeight? weight = null)
    {
        TextBlock textBlock = Text(UiText.Get(source), size, color, weight);
        if (textBlock.Text == source)
        {
            UiText.TranslateLater(textBlock, source);
        }
        return textBlock;
    }

    public static CheckBox LocalizedCheckBox(string source, bool selected)
    {
        CheckBox checkBox = new CheckBox
        {
            Content = UiText.Get(source),
            IsChecked = selected
        };
        if (checkBox.Content as string == source)
        {
            UiText.TranslateLater(checkBox, source);
        }
        return checkBox;
    }

    public static Button LocalizedButton(string source, Func<Task> action, string? style = null)
    {
        Button button = Button(UiText.Get(source), action, style);
        if (button.Content as string == source)
        {
            UiText.TranslateLater(button, source);
        }
        return button;
    }

    public static Button LocalizedButton(string source, Action action, string? style = null)
    {
        return LocalizedButton(source, () =>
        {
            action();
            return Task.CompletedTask;
        }, style);
    }

    public static Button IconButton(string icon, string label, Func<Task> action, string? style = null)
    {
        Button button = Button(label, action, style);
        Control[] array = new Control[2];
        NavGlyph navGlyph = new NavGlyph
        {
            Kind = icon,
            Width = 18.0,
            Height = 18.0
        };
        IBrush stroke;
        if (!(style == "primary"))
        {
            stroke = Ink;
        }
        else
        {
            IBrush white = Brushes.White;
            stroke = white;
        }
        navGlyph.Stroke = stroke;
        array[0] = navGlyph;
        IBrush color;
        if (!(style == "primary"))
        {
            color = Ink;
        }
        else
        {
            IBrush white = Brushes.White;
            color = white;
        }
        array[1] = Text(label, 14.0, color, FontWeight.Medium);
        button.Content = Row(array);
        AutomationProperties.SetName(button, label);
        return button;
    }

    public static Button IconButton(string icon, string label, Action action, string? style = null)
    {
        return IconButton(icon, label, () =>
        {
            action();
            return Task.CompletedTask;
        }, style);
    }

    public static StackPanel Row(params Control[] children)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10.0
        }.WithChildren(children);
    }

    public static StackPanel Column(params Control[] children)
    {
        return new StackPanel
        {
            Spacing = 12.0
        }.WithChildren(children);
    }

    private static StackPanel WithChildren(this StackPanel panel, IEnumerable<Control> children)
    {
        foreach (Control child in children)
        {
            panel.Children.Add(child);
        }
        return panel;
    }
}
