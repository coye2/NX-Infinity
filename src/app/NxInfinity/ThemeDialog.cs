using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace NxInfinity;

internal static class ThemeDialog
{
    public static async Task ShowAsync(MainWindow owner)
    {
        ThemePreferences draft;
        ThemePreferences original = (draft = owner.Appearance);
        bool accepted = false;
        bool updating = false;
        bool favoritesOnly = false;
        Window dialog = Dialogs.Create("Themes", 870.0, 700.0);
        dialog.Name = "ThemePicker";
        ComboBox target = new ComboBox
        {
            Name = "ThemeTarget",
            ItemsSource = new string[3] { "Desktop", "HOME", "Both" },
            SelectedIndex = (owner.IsHomeMode ? 1 : 0),
            MinWidth = 120.0
        };
        TextBox search = new TextBox
        {
            Name = "ThemeSearch",
            PlaceholderText = "Find a theme",
            Width = 165.0,
            MaxLength = 40
        };
        WrapPanel tiles = new WrapPanel
        {
            Name = "ThemePresets",
            Orientation = Orientation.Horizontal
        };
        TextBlock status = VisualTheme.LocalizedText("Previewing your current appearance", 13.0, VisualTheme.Muted);
        status.Name = "ThemeStatus";
        TextBlock current = VisualTheme.LocalizedText("", 17.0);
        current.Name = "CurrentTheme";
        TextBox customColor = new TextBox
        {
            Name = "ThemeAccent",
            Width = 110.0,
            MaxLength = 7
        };
        ComboBox pattern = new ComboBox
        {
            Name = "ThemePattern",
            ItemsSource = ThemePresets.Patterns,
            MinWidth = 110.0
        };
        ComboBox saved = new ComboBox
        {
            Name = "SavedThemes",
            PlaceholderText = "Your saved themes",
            MinWidth = 160.0
        };
        Button favorite = null;
        favorite = VisualTheme.LocalizedButton("Favorite", () =>
        {
            string id = Choice().Preset;
            draft = draft with
            {
                Favorites = (draft.Favorites.Contains(id) ? draft.Favorites.Where((string x) => x != id).ToArray() : draft.Favorites.Append(id).ToArray())
            };
            Refresh();
        });
        favorite.Name = "ThemeFavorite";
        CheckBox filter = new CheckBox
        {
            Name = "ThemeFavoritesOnly",
            Content = "Favorites"
        };
        filter.IsCheckedChanged += (object? _, RoutedEventArgs _) =>
        {
            favoritesOnly = filter.IsChecked == true;
            Refresh();
        };
        target.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
        {
            if (!updating)
            {
                Refresh();
            }
        };
        search.TextChanged += (object? _, TextChangedEventArgs _) =>
        {
            if (!updating)
            {
                Refresh();
            }
        };
        pattern.SelectionChanged += async (object? _, SelectionChangedEventArgs _) =>
        {
            if (!updating && pattern.SelectedItem is string pattern2)
            {
                await Preview(Choice() with
                {
                    Pattern = pattern2
                });
            }
        };
        saved.SelectionChanged += async (object? _, SelectionChangedEventArgs _) =>
        {
            if (!updating && saved.SelectedIndex >= 0)
            {
                await Preview(draft.Saved[saved.SelectedIndex].Choice);
            }
        };
        WrapPanel wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        string[] array = new string[12]
        {
            "#D93B4A", "#D67A36", "#C6A24E", "#41845E", "#23868A", "#446AB0", "#7654AD", "#B95385", "#F0BE82", "#99D2BB",
            "#B6C5EF", "#ECECEC"
        };
        foreach (string color in array)
        {
            Button button = VisualTheme.Button(color, () => Preview(Choice() with
            {
                Accent = color
            }));
            button.Width = 28.0;
            double height = (button.MinHeight = 28.0);
            button.Height = height;
            button.Padding = default;
            button.Margin = new Thickness(0.0, 0.0, 5.0, 5.0);
            button.Content = new Border
            {
                Width = 20.0,
                Height = 20.0,
                Background = Brush.Parse(color)
            };
            button.Classes.Add("game-artwork");
            ToolTip.SetTip(button, color);
            wrapPanel.Children.Add(button);
        }
        Button button2 = VisualTheme.LocalizedButton("Set color", () => Preview(Choice() with
        {
            Accent = customColor.Text?.Trim()
        }));
        Button button3 = VisualTheme.LocalizedButton("Preset color", () => Preview(Choice() with
        {
            Accent = null
        }));
        Button button4 = VisualTheme.LocalizedButton("Save as preset", async () =>
        {
            string name = await Dialogs.TextAsync(dialog, "Theme name", "My theme", 32, secret: false, 1);
            if (name != null)
            {
                SavedTheme[] array2 = draft.Saved.Where((SavedTheme x) => !x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).Append(new SavedTheme(name.Trim(), Choice())).ToArray();
                if (array2.Length > 16)
                {
                    status.Text = "You have 16 saved themes. Remove one before adding another.";
                }
                else
                {
                    draft = draft with
                    {
                        Saved = array2
                    };
                    Refresh();
                    status.Text = "Preset saved in this preview · Apply keeps it.";
                }
            }
        });
        button4.Name = "SaveThemePreset";
        Button button5 = VisualTheme.LocalizedButton("Manage saved", async () =>
        {
            string name = await Dialogs.ChooseAsync(dialog, "Remove saved theme", draft.Saved.Select((SavedTheme x) => (x.Name, x.Name)).ToArray(), "Built-in themes are always available.");
            if (name != null)
            {
                draft = draft with
                {
                    Saved = draft.Saved.Where((SavedTheme x) => x.Name != name).ToArray()
                };
                Refresh();
            }
        });
        Button button6 = VisualTheme.LocalizedButton("Surprise me", () =>
        {
            ThemePreset[] array2 = ThemePresets.All.Where((ThemePreset x) => x.Id != Choice().Preset).ToArray();
            return Preview(new ThemeChoice(array2[Random.Shared.Next(array2.Length)].Id, null, Choice().Pattern));
        });
        button6.Name = "ThemeSurprise";
        Button button7 = VisualTheme.LocalizedButton("Restore original", async () =>
        {
            if (target.SelectedIndex != 2)
            {
                await Preview(new ThemeChoice((target.SelectedIndex == 1) ? "home" : "dune"));
            }
            else
            {
                draft = draft with
                {
                    Desktop = new ThemeChoice(),
                    Home = new ThemeChoice("home")
                };
                await owner.SetAppearanceAsync(draft, persist: false);
                Refresh();
                status.Text = "Original desktop and HOME appearances · Apply keeps them.";
            }
        });
        button7.Name = "ThemeReset";
        Button button8 = VisualTheme.LocalizedButton("Apply", async () =>
        {
            try
            {
                await owner.SetAppearanceAsync(draft);
                accepted = true;
                dialog.Close();
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
            {
                status.Text = "Could not save theme: " + ex.Message;
            }
        }, "primary");
        button8.Name = "ThemeApply";
        Button button9 = VisualTheme.LocalizedButton("Cancel", () =>
        {
            dialog.Close();
        });
        button9.Name = "ThemeCancel";
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
            RowSpacing = 12.0,
            Margin = new Thickness(22.0)
        };
        grid.Children.Add(Wrap(new Control[5]
        {
            VisualTheme.LocalizedText("Themes", 23.0),
            target,
            search,
            filter,
            button6
        }));
        WrapPanel wrapPanel2 = Wrap(new Control[3] { current, favorite, saved });
        Grid.SetRow(wrapPanel2, 1);
        grid.Children.Add(wrapPanel2);
        ScrollViewer scrollViewer = new ScrollViewer
        {
            Content = tiles,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scrollViewer, 2);
        grid.Children.Add(scrollViewer);
        Expander expander = new Expander();
        expander.Name = "ThemeCustomize";
        expander.Header = "Make it yours";
        expander.Content = VisualTheme.Column(VisualTheme.LocalizedText("Accent", 13.0), wrapPanel, Wrap(new Control[3] { customColor, button2, button3 }), Wrap(new Control[2]
        {
            VisualTheme.LocalizedText("HOME backdrop", 13.0),
            pattern
        }), Wrap(new Control[2] { button4, button5 }));
        expander.HorizontalAlignment = HorizontalAlignment.Stretch;
        Expander expander2 = expander;
        Grid.SetRow(expander2, 3);
        grid.Children.Add(expander2);
        Grid.SetRow(status, 4);
        grid.Children.Add(status);
        WrapPanel wrapPanel3 = Wrap(new Control[3] { button8, button9, button7 });
        Grid.SetRow(wrapPanel3, 5);
        grid.Children.Add(wrapPanel3);
        dialog.Content = grid;
        Refresh();
        try
        {
            await Dialogs.ShowAsync<object>(owner, dialog);
        }
        finally
        {
            if (!accepted)
            {
                await owner.SetAppearanceAsync(original, persist: false);
            }
        }
        ThemeChoice Choice()
        {
            if (target.SelectedIndex != 1)
            {
                return draft.Desktop;
            }
            return draft.Home;
        }
        async Task Preview(ThemeChoice choice)
        {
            if (!ThemePresets.ValidChoice(choice))
            {
                status.Text = "Use a six-digit color such as #5B6CB0.";
            }
            else
            {
                draft = target.SelectedIndex switch
                {
                    0 => draft with
                    {
                        Desktop = choice
                    },
                    1 => draft with
                    {
                        Home = choice
                    },
                    _ => draft with
                    {
                        Desktop = choice,
                        Home = choice
                    },
                };
                await owner.SetAppearanceAsync(draft, persist: false);
                Refresh();
                status.Text = "Preview · Apply keeps this appearance; Cancel restores it.";
            }
        }
        void Refresh()
        {
            updating = true;
            ThemeChoice choice = Choice();
            ThemePreset themePreset = ThemePresets.Resolve(choice);
            current.Text = themePreset.Name;
            customColor.Text = themePreset.Accent;
            pattern.SelectedItem = choice.Pattern;
            pattern.IsEnabled = target.SelectedIndex != 0;
            favorite.Content = (draft.Favorites.Contains(choice.Preset) ? "Unfavorite" : "Favorite");
            saved.ItemsSource = draft.Saved.Select((SavedTheme x) => x.Name).ToArray();
            saved.SelectedIndex = -1;
            tiles.Children.Clear();
            foreach (ThemePreset preset in ThemePresets.All.Where((ThemePreset p) => (!favoritesOnly || draft.Favorites.Contains(p.Id)) && (string.IsNullOrWhiteSpace(search.Text) || p.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                Button button10 = VisualTheme.Button(preset.Name, () => Preview(new ThemeChoice(preset.Id, null, choice.Pattern)));
                button10.Name = "ThemePreset";
                button10.Tag = preset.Id;
                button10.Width = 184.0;
                button10.Margin = new Thickness(0.0, 0.0, 8.0, 8.0);
                button10.Padding = new Thickness(8.0);
                button10.Background = Brush.Parse(preset.Surface);
                button10.BorderBrush = Brush.Parse((choice.Preset == preset.Id) ? preset.Ink : preset.Line);
                button10.BorderThickness = new Thickness((!(choice.Preset == preset.Id)) ? 1 : 2);
                button10.Classes.Add("game-artwork");
                TextBlock textBlock = new TextBlock
                {
                    Text = preset.Name,
                    FontSize = 13.0,
                    Foreground = Brush.Parse(preset.Ink),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                StackPanel stackPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 3.0
                };
                string[] array2 = new string[4] { preset.Canvas, preset.Hover, preset.Accent, preset.Ink };
                foreach (string s in array2)
                {
                    stackPanel.Children.Add(new Border
                    {
                        Width = 36.0,
                        Height = 23.0,
                        Background = Brush.Parse(s)
                    });
                }
                button10.Content = VisualTheme.Column(textBlock, stackPanel);
                AutomationProperties.SetName(button10, preset.Name + (preset.Night ? " · dark" : " · light"));
                tiles.Children.Add(button10);
            }
            if (tiles.Children.Count == 0)
            {
                tiles.Children.Add(VisualTheme.LocalizedText("No matching themes. Clear the search or Favorites filter.", 13.0));
            }
            updating = false;
        }
        static WrapPanel Wrap(params Control[] controls)
        {
            WrapPanel wrapPanel4 = new WrapPanel();
            foreach (Control control in controls)
            {
                control.Margin = new Thickness(0.0, 0.0, 8.0, 4.0);
                wrapPanel4.Children.Add(control);
            }
            return wrapPanel4;
        }
    }
}
