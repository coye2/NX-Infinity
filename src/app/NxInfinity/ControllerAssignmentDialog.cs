using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NxInfinity.Runtime;

namespace NxInfinity;

internal static class ControllerAssignmentDialog
{
    internal static async Task<string?> ChooseAsync(Window owner, ControllerAssignment assignment)
    {
        Window dialog = Dialogs.Create(UiText.Get("Controller for player 1"), 760.0, 620.0);
        dialog.Name = "ControllerAssignmentDialog";
        dialog.MinWidth = 560.0;
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 16.0
        };
        grid.Children.Add(VisualTheme.LocalizedText("Controller for player 1", 25.0, null, FontWeight.DemiBold));
        TextBlock summary = VisualTheme.LocalizedText("", 14.0, VisualTheme.Muted);
        summary.Name = "ControllerSelectionSummary";
        Grid.SetRow(summary, 1);
        grid.Children.Add(summary);
        StackPanel choices = new StackPanel
        {
            Spacing = 10.0
        };
        ScrollViewer scrollViewer = new ScrollViewer
        {
            Content = choices,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scrollViewer, 2);
        grid.Children.Add(scrollViewer);
        Button button = VisualTheme.LocalizedButton("Back · keep current selection", () =>
        {
            dialog.Close();
        });
        button.Name = "ControllerSelectionBack";
        Grid.SetRow(button, 3);
        grid.Children.Add(button);
        dialog.Content = new Border
        {
            Padding = new Thickness(24.0),
            Child = grid
        };
        (string Id, string Name, ushort VendorId, ushort ProductId, HostControllerConnection Connection)[] previousDevices = Array.Empty<(string, string, ushort, ushort, HostControllerConnection)>();
        ControllerAssignmentState previousState = null;
        Refresh();
        DispatcherTimer refresh = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200L)
        };
        refresh.Tick += (object? _, EventArgs _) =>
        {
            Refresh();
        };
        dialog.Opened += (object? _, EventArgs _) =>
        {
            refresh.Start();
        };
        dialog.Closed += (object? _, EventArgs _) =>
        {
            refresh.Stop();
        };
        try
        {
            return await Dialogs.ShowAsync<string>(owner, dialog);
        }
        finally
        {
            refresh.Stop();
        }
        void Refresh()
        {
            ControllerAssignmentState state = assignment.State;
            (string, string, ushort, ushort, HostControllerConnection)[] array = assignment.Devices.Select((HostControllerSnapshot d) => (Id: d.DeviceId, Name: d.Name, VendorId: d.VendorId, ProductId: d.ProductId, Connection: d.Connection)).ToArray();
            TextBlock textBlock = summary;
            textBlock.Text = state.Phase switch
            {
                ControllerAssignmentPhase.Assigned => "Selected: " + state.PreferredName + ". Closing this window or resuming play keeps this selection.",
                ControllerAssignmentPhase.Disconnected => "Saved preference: " + state.PreferredName + ". It is disconnected. Connect a controller and confirm its row below.",
                ControllerAssignmentPhase.ConfirmConnection => "Saved preference: " + state.PreferredName + ". Confirm the attached controller below. Its name alone cannot verify that it is the same device.",
                ControllerAssignmentPhase.Ambiguous => "Saved preference: " + state.PreferredName + ". Several controllers have this name. Choose the intended connection explicitly.",
                ControllerAssignmentPhase.PreferenceUnreadable => "The saved controller preference could not be read. Make a new explicit selection to save it again.",
                _ => "No frontend gamepad is assigned. Native keyboard controls remain available where the selected core supports them.",
            };
            summary.Text += "\nAll connected controllers can navigate NX Infinity. After changing gameplay assignment, release every button and stick before returning to play.";
            if (!(previousState == state) || !previousDevices.SequenceEqual(array))
            {
                string focusedId = (dialog.FocusManager?.GetFocusedElement() as Button)?.CommandParameter as string;
                previousState = state;
                previousDevices = array;
                choices.Children.Clear();
                (string, string, ushort, ushort, HostControllerConnection)[] array2 = array;
                for (int num = 0; num < array2.Length; num++)
                {
                    (string Id, string Name, ushort VendorId, ushort ProductId, HostControllerConnection Connection) device = array2[num];
                    bool flag = state.DeviceId == device.Id;
                    bool flag2 = array.Count(((string Id, string Name, ushort VendorId, ushort ProductId, HostControllerConnection Connection) d) => d.Name == device.Name) > 1;
                    string text;
                    if (flag)
                    {
                        text = "✓ Selected for player 1";
                    }
                    else
                    {
                        text = ((state.PreferredName == device.Name) ? "Saved preference · confirm this connection" : "Choose for player 1");
                    }
                    if (flag2)
                    {
                        text = text + " · " + device.Id + " (same-name devices)";
                    }
                    if (device.Connection == HostControllerConnection.Wireless)
                    {
                        text += " · Wireless";
                    }
                    if (flag2 && device.VendorId != 0 && device.ProductId != 0)
                    {
                        text += $" · Device {device.VendorId:X4}:{device.ProductId:X4}";
                    }
                    Button button2 = VisualTheme.Button(device.Name, () =>
                    {
                        dialog.Close(device.Id);
                    }, flag ? "primary" : null);
                    button2.Name = "ControllerChoice";
                    button2.CommandParameter = device.Id;
                    button2.HorizontalAlignment = HorizontalAlignment.Stretch;
                    button2.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    button2.MinHeight = 60.0;
                    button2.Content = VisualTheme.Column(VisualTheme.Text(device.Name, 16.0, null, FontWeight.DemiBold), VisualTheme.Text(text, 13.0));
                    AutomationProperties.SetName(button2, device.Name + ", " + text);
                    if (flag || (state.DeviceId == null && state.MatchingNameCount == 1 && state.PreferredName == device.Name))
                    {
                        button2.Tag = "primary-focus";
                    }
                    choices.Children.Add(button2);
                }
                if (array.Length == 0)
                {
                    choices.Children.Add(VisualTheme.LocalizedText("No gamepad detected. Connect one to show its available selection.", 15.0, VisualTheme.Muted));
                }
                choices.Children.Add(VisualTheme.LocalizedText("USB wireless receivers work through the same controller list. For a GameSir Tarantula Pro on PC, use its paired receiver in XInput mode; it may appear with an Xbox controller name. After reconnecting or changing modes, confirm its new connection here.", 13.0, VisualTheme.Muted));
                bool flag3 = state.Phase == ControllerAssignmentPhase.Released;
                Button button3 = VisualTheme.Button(flag3 ? UiText.Get("✓ Selected: native keyboard / no frontend gamepad") : UiText.Get("Release gamepad · use native keyboard where supported"), () =>
                {
                    dialog.Close("none");
                }, flag3 ? "primary" : null);
                button3.Name = "ControllerRelease";
                button3.CommandParameter = "none";
                button3.HorizontalAlignment = HorizontalAlignment.Stretch;
                button3.HorizontalContentAlignment = HorizontalAlignment.Left;
                button3.MinHeight = 48.0;
                if (flag3)
                {
                    button3.Tag = "primary-focus";
                }
                choices.Children.Add(button3);
                if (dialog.IsVisible)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Button button4 = choices.Children.OfType<Button>().FirstOrDefault((Button b) => object.Equals(b.CommandParameter, focusedId));
                        if (button4 != null)
                        {
                            Navigation.Focus(button4);
                        }
                        else
                        {
                            Navigation.FocusFirst(dialog);
                        }
                    }, DispatcherPriority.Loaded);
                }
            }
        }
    }
}
