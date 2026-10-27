using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace NxInfinity;

public static class Navigation
{
    public static Window? CurrentWindow { get; set; }

    public static IReadOnlyList<Control> Targets(Window window)
    {
        IEnumerable<Visual> visualDescendants;
        if (window is MainWindow mainWindow)
        {
            Control desktopFlyoutContent = mainWindow.DesktopFlyoutContent;
            if (desktopFlyoutContent != null)
            {
                visualDescendants = desktopFlyoutContent.GetVisualDescendants();
                goto IL_0022;
            }
        }
        visualDescendants = window.GetVisualDescendants();
        goto IL_0022;
    IL_0022:
        return visualDescendants.OfType<Control>().Where((Control c) =>
        {
            bool flag = c.Focusable && c.IsVisible && c.IsEnabled && c.Bounds.Width > 0.0 && c.Bounds.Height > 0.0 && c.GetVisualAncestors().OfType<Control>().All((Control a) => a.IsVisible && a.IsEnabled);
            if (flag)
            {
                bool flag2 = ((c is Button || c is TextBox || c is ComboBox || c is Slider) ? true : false);
                flag = flag2;
            }
            return flag;
        }).Distinct()
            .ToArray();
    }

    public static void FocusFirst(Window window)
    {
        if (!(window is MainWindow mainWindow) || !mainWindow.FocusHomeDefault())
        {
            IReadOnlyList<Control> source = Targets(window);
            Focus(source.FirstOrDefault((Control c) => object.Equals(c.Tag, "primary-focus")) ?? source.FirstOrDefault());
        }
    }

    public static void Focus(Control? control)
    {
        if (control != null)
        {
            control.Focus(NavigationMethod.Directional);
            control.BringIntoView();
        }
    }

    public static void Move(Window window, Key direction)
    {
        if (window is MainWindow mainWindow && mainWindow.MoveHomeFocus(direction))
        {
            return;
        }
        IReadOnlyList<Control> source = Targets(window);
        Control current = window.FocusManager?.GetFocusedElement() as Control;
        if (current == null || !source.Contains(current))
        {
            FocusFirst(window);
            return;
        }
        Slider slider = current as Slider;
        bool flag = slider != null;
        if (flag)
        {
            Key key = direction;
            bool flag2 = ((key == Key.Left || key == Key.Right) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            slider.Value = Math.Clamp(slider.Value + (double)((direction == Key.Right) ? 1 : (-1)) * ((slider.SmallChange > 0.0) ? slider.SmallChange : ((slider.Maximum - slider.Minimum) / 20.0)), slider.Minimum, slider.Maximum);
            return;
        }
        if (current is ComboBox { IsDropDownOpen: not false } comboBox)
        {
            ComboBox comboBox2 = comboBox;
            int selectedIndex = comboBox.SelectedIndex;
            Key key = direction;
            flag = (uint)(key - 25) <= 1u;
            comboBox2.SelectedIndex = Math.Clamp(selectedIndex + (flag ? 1 : (-1)), 0, Math.Max(0, comboBox.ItemCount - 1));
            return;
        }
        Point? point = Center(current);
        if (point.HasValue)
        {
            Point origin = point.GetValueOrDefault();
            (Control, double, double)[] array = (from x in (from x in (from c in source
                                                                       where c != current
                                                                       select (Control: c, Point: Center(c))).Where(((Control Control, Point? Point) x) =>
                                                                   {
                                                                       Point? item = x.Point;
                                                                       return item.HasValue;
                                                                   })
                                                            select (Control: x.Control, Dx: x.Point.Value.X - origin.X, Dy: x.Point.Value.Y - origin.Y)).Select(((Control Control, double Dx, double Dy) x) =>
                                                        {
                                                            Control item = x.Control;
                                                            double item2 = direction switch
                                                            {
                                                                Key.Right => x.Dx,
                                                                Key.Left => 0.0 - x.Dx,
                                                                Key.Down => x.Dy,
                                                                _ => 0.0 - x.Dy,
                                                            };
                                                            Key key2 = direction;
                                                            bool flag3 = ((key2 == Key.Left || key2 == Key.Right) ? true : false);
                                                            return (Control: item, Forward: item2, Across: flag3 ? Math.Abs(x.Dy) : Math.Abs(x.Dx));
                                                        })
                                                 where x.Forward > 1.0
                                                 orderby x.Forward + x.Across * 3.2
                                                 select x).ToArray();
            if (array.Length != 0)
            {
                Focus(array[0].Item1);
            }
        }
        Point? Center(Control c)
        {
            return window.PointToClient(c.PointToScreen(new Point(c.Bounds.Width / 2.0, c.Bounds.Height / 2.0)));
        }
    }

    public static async Task ActivateAsync(Window window)
    {
        if (!(window.FocusManager?.GetFocusedElement() is Control control) || (TopLevel.GetTopLevel(control) != window && (!(window is MainWindow mainWindow) || !mainWindow.DesktopFlyoutContains(control))) || !control.IsEnabled || !control.IsVisible || control.GetVisualAncestors().OfType<Control>().Any((Control ancestor) => !ancestor.IsEnabled || !ancestor.IsVisible))
        {
            return;
        }
        Control control2 = control;
        if (!(control2 is ToggleButton toggleButton))
        {
            if (!(control2 is Button button))
            {
                if (!(control2 is TextBox text))
                {
                    if (control2 is ComboBox comboBox)
                    {
                        comboBox.IsDropDownOpen = !comboBox.IsDropDownOpen;
                    }
                }
                else if (!object.Equals(text.Tag, "onscreen-keyboard"))
                {
                    string text2 = await Dialogs.TextAsync(window, text.PlaceholderText ?? "Enter text", text.Text ?? "", Math.Max(1, (text.MaxLength == 0) ? 4096 : text.MaxLength));
                    if (text2 != null)
                    {
                        text.Text = text2;
                    }
                }
            }
            else
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }
        else
        {
            toggleButton.IsChecked = toggleButton.IsChecked != true;
        }
    }

    public static void Scroll(Window window, double vertical)
    {
        ScrollViewer scrollViewer = (window.FocusManager?.GetFocusedElement() as Control)?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() ?? window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer != null)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Clamp(scrollViewer.Offset.Y + vertical, 0.0, Math.Max(0.0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height)));
        }
    }
}
