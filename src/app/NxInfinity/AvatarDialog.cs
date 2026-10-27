using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace NxInfinity;

internal static class AvatarDialog
{
    internal static TextBlock IslandLabel(string label)
    {
        return VisualTheme.Text(label, 12.0, Brush.Parse("#582D49"));
    }

    public static async Task ShowAsync(Window owner, AvatarStore store)
    {
        Window dialog = Dialogs.Create("Avatar", 850.0, 660.0);
        dialog.Name = "AvatarDialog";
        CancellationTokenSource lifetime = new CancellationTokenSource();
        AvatarCatalog catalog;
        CancellationTokenSource pageLifetime;
        List<Bitmap> images;
        Bitmap previewBitmap;
        byte[] candidate;
        string selectedPath;
        AvatarItem[] items;
        int offset;
        int generation;
        int listingGeneration;
        bool favoritesOnly;
        bool updatingProfiles;
        TextBlock status;
        ComboBox profile;
        Image preview;
        TextBlock attribution;
        ComboBox category;
        TextBox search;
        WrapPanel tiles;
        Button previous;
        Button next;
        Button use;
        Button favorite;
        TextBlock page;
        try
        {
            catalog = new AvatarCatalog(store.Root);
            try
            {
                pageLifetime = null;
                images = new List<Bitmap>();
                previewBitmap = null;
                candidate = null;
                selectedPath = null;
                items = Array.Empty<AvatarItem>();
                offset = 0;
                generation = 0;
                listingGeneration = 0;
                favoritesOnly = false;
                updatingProfiles = false;
                status = VisualTheme.Text(store.LoadWarning ?? "Choose an image for your launcher profile.", 13.0, VisualTheme.Muted);
                status.Name = "AvatarStatus";
                profile = new ComboBox
                {
                    Name = "AvatarProfile",
                    MinWidth = 140.0,
                    ItemsSource = store.Preferences.Profiles.Select((AvatarProfile p) => p.Name).ToArray(),
                    SelectedIndex = Array.FindIndex(store.Preferences.Profiles, (AvatarProfile p) => p.Id == store.Active.Id)
                };
                preview = new Image
                {
                    Width = 104.0,
                    Height = 104.0,
                    Stretch = Stretch.UniformToFill,
                    Name = "AvatarPreview"
                };
                RenderOptions.SetBitmapInterpolationMode(preview, BitmapInterpolationMode.HighQuality);
                Preview(store.ReadImage());
                attribution = VisualTheme.LocalizedText("", 11.0, VisualTheme.Muted);
                category = new ComboBox
                {
                    Name = "AvatarCategory",
                    MinWidth = 190.0,
                    MaxWidth = 280.0,
                    PlaceholderText = "Choose a collection",
                    ItemsSource = new AvatarCategory[1] { AvatarCatalog.IslandCategory }
                };
                search = new TextBox
                {
                    Name = "AvatarSearch",
                    PlaceholderText = "Search characters",
                    MaxLength = 100,
                    MinWidth = 140.0
                };
                tiles = new WrapPanel
                {
                    Name = "AvatarGrid",
                    Orientation = Orientation.Horizontal
                };
                previous = null;
                next = null;
                use = null;
                favorite = null;
                page = VisualTheme.LocalizedText("", 12.0);
                previous = VisualTheme.LocalizedButton("Previous", () =>
                {
                    offset = Math.Max(0, offset - 24);
                    _ = RenderAsync();
                });
                next = VisualTheme.LocalizedButton("Next", () =>
                {
                    offset += 24;
                    _ = RenderAsync();
                });
                use = VisualTheme.LocalizedButton("Use avatar", () =>
                {
                    if (candidate == null)
                    {
                        return;
                    }
                    try
                    {
                        store.SelectImage(candidate);
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                    }
                }, "primary");
                use.Name = "AvatarApply";
                use.IsEnabled = false;
                favorite = VisualTheme.LocalizedButton("Add favorite", () =>
                {
                    if (selectedPath == null)
                    {
                        return;
                    }
                    try
                    {
                        store.Favorite(selectedPath, !store.Preferences.Favorites.Contains(selectedPath));
                        favorite.Content = (store.Preferences.Favorites.Contains(selectedPath) ? "Remove favorite" : "Add favorite");
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                    }
                });
                favorite.Name = "AvatarFavorite";
                favorite.IsEnabled = false;
#if !NXI_NATIVE_LINUX
                Button button = VisualTheme.LocalizedButton("Reload catalog", (Func<Task>)BrowseAsync, (string?)null);
                button.Name = "AvatarBrowse";
#endif
                Button button2 = previous;
                bool isEnabled = (next.IsEnabled = false);
                button2.IsEnabled = isEnabled;
#if !NXI_NATIVE_LINUX
                dialog.Opened += async (object? _, EventArgs _) =>
                {
                    category.SelectedIndex = 0;
                    await BrowseAsync();
                };
                Button button3 = VisualTheme.Button("Hello Kitty & Friends", async () =>
                {
                    search.Text = "";
                    favoritesOnly = false;
                    if (category.SelectedItem is AvatarCategory avatarCategory && avatarCategory.Id == AvatarCatalog.IslandCategory.Id)
                    {
                        await LoadAsync();
                    }
                    else
                    {
                        category.SelectedItem = category.ItemsSource.Cast<AvatarCategory>().First((AvatarCategory c) => c.Id == AvatarCatalog.IslandCategory.Id);
                    }
                });
                button3.Name = "HelloKittyAvatars";
#endif
                Button favorites = null;
                favorites = VisualTheme.LocalizedButton("Favorites", async () =>
                {
                    favoritesOnly = !favoritesOnly;
                    favorites.Classes.Set("selected", favoritesOnly);
                    await LoadAsync();
                });
                favorites.Name = "AvatarFavorites";
                Button button4 = VisualTheme.LocalizedButton("Import image", async () =>
                {
                    _ = 1;
                    try
                    {
                        string path = await Dialogs.PickPathAsync(dialog.StorageProvider, "Choose avatar", new string[3] { ".png", ".jpg", ".jpeg" }, directory: false);
                        if (path != null)
                        {
                            if (new FileInfo(path).Length > 8388608)
                            {
                                throw new InvalidDataException("Choose an image smaller than 8 MB.");
                            }
                            candidate = await Task.Run(() => AvatarStore.Normalize(File.ReadAllBytes(path)), lifetime.Token);
                            selectedPath = null;
                            Preview(candidate);
                            use.IsEnabled = true;
                            favorite.IsEnabled = false;
                            status.Text = "Local image · choose Use avatar to save";
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex2)
                    {
                        if (!lifetime.IsCancellationRequested)
                        {
                            status.Text = ex2.Message;
                        }
                    }
                });
                button4.Name = "AvatarImport";
                category.SelectionChanged += async (object? _, SelectionChangedEventArgs _) =>
                {
                    favoritesOnly = false;
                    favorites.Classes.Remove("selected");
                    await LoadAsync();
                };
                search.TextChanged += (object? _, TextChangedEventArgs _) =>
                {
                    offset = 0;
                    _ = RenderAsync();
                };
                profile.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
                {
                    if (updatingProfiles || profile.SelectedIndex < 0)
                    {
                        return;
                    }
                    try
                    {
                        store.SelectProfile(store.Preferences.Profiles[profile.SelectedIndex].Id);
                        candidate = null;
                        selectedPath = null;
                        Preview(store.ReadImage());
                        Button button8 = use;
                        bool isEnabled2 = (favorite.IsEnabled = false);
                        button8.IsEnabled = isEnabled2;
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                    }
                };
                Button button5 = VisualTheme.LocalizedButton("Rename", async () =>
                {
                    string text = await Dialogs.TextAsync(dialog, "Profile name", store.Active.Name, 40, secret: false, 1);
                    if (text == null)
                    {
                        return;
                    }
                    try
                    {
                        store.Rename(text);
                        RefreshProfiles();
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                    }
                });
                Button button6 = VisualTheme.LocalizedButton("Add profile", async () =>
                {
                    string text = await Dialogs.TextAsync(dialog, "Profile name", "", 40, secret: false, 1);
                    if (text == null)
                    {
                        return;
                    }
                    try
                    {
                        store.AddProfile(text);
                        RefreshProfiles();
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                    }
                });
                Button button7 = VisualTheme.LocalizedButton("Remove avatar", () =>
                {
                    try
                    {
                        store.Reset();
                        candidate = null;
                        Preview(null);
                        use.IsEnabled = false;
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message;
                    }
                });
                Grid grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
                    RowSpacing = 12.0,
                    Margin = new Thickness(22.0)
                };
                grid.Children.Add(Wrap(new Control[3]
                {
                    preview,
                    VisualTheme.Column(VisualTheme.LocalizedText("Avatar", 22.0), new TextBlock
                    {
                        Text = "Launcher appearance only · game save users stay unchanged",
                        FontSize = 12.0,
                        MaxWidth = 290.0,
                        TextWrapping = TextWrapping.Wrap
                    }),
                    profile
                }));
                WrapPanel wrapPanel = Wrap(new Control[4] { button5, button6, button7, button4 });
                Grid.SetRow(wrapPanel, 1);
                grid.Children.Add(wrapPanel);
#if !NXI_NATIVE_LINUX
                StackPanel stackPanel = VisualTheme.Column(Wrap(new Control[5] { button3, category, favorites, search, button }), attribution);
                Grid.SetRow(stackPanel, 2);
                grid.Children.Add(stackPanel);
                ScrollViewer scrollViewer = new ScrollViewer
                {
                    Content = tiles,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                Grid.SetRow(scrollViewer, 3);
                grid.Children.Add(scrollViewer);
                StackPanel stackPanel2 = VisualTheme.Row(previous, page, next, favorite);
                Grid.SetRow(stackPanel2, 4);
                grid.Children.Add(stackPanel2);
                StackPanel stackPanel3 = VisualTheme.Column(status, VisualTheme.LocalizedText("Catalog: henry-debruin/nso-icons · downloaded on request", 11.0, VisualTheme.Muted), VisualTheme.Row(use, VisualTheme.LocalizedButton("Close", () =>
#else
                StackPanel stackPanel3 = VisualTheme.Column(status, VisualTheme.Row(use, VisualTheme.LocalizedButton("Close", () =>
#endif
                {
                    dialog.Close();
                })));
                Grid.SetRow(stackPanel3, 5);
                grid.Children.Add(stackPanel3);
                dialog.Content = grid;
                dialog.Closed += (object? _, EventArgs _) =>
                {
                    lifetime.Cancel();
                    pageLifetime?.Cancel();
                    generation++;
                };
                try
                {
                    await Dialogs.ShowAsync<object>(owner, dialog);
                }
                finally
                {
                    pageLifetime?.Cancel();
                    pageLifetime?.Dispose();
                    preview.Source = null;
                    previewBitmap?.Dispose();
                    foreach (Bitmap item in images)
                    {
                        item.Dispose();
                    }
                }
            }
            finally
            {
                if (catalog != null)
                {
                    ((IDisposable)catalog).Dispose();
                }
            }
        }
        finally
        {
            if (lifetime != null)
            {
                ((IDisposable)lifetime).Dispose();
            }
        }
#if !NXI_NATIVE_LINUX
        async Task BrowseAsync()
        {
            try
            {
                string chosen = (category.SelectedItem as AvatarCategory)?.Id ?? AvatarCatalog.IslandCategory.Id;
                AvatarCategory[] array = await catalog.CategoriesAsync(lifetime.Token);
                category.ItemsSource = array;
                category.SelectedItem = array.FirstOrDefault((AvatarCategory c) => c.Id == chosen) ?? array.FirstOrDefault();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex2)
            {
                if (!lifetime.IsCancellationRequested)
                {
                    status.Text = ex2.Message;
                }
            }
        }
#endif
        async Task LoadAsync()
        {
            int listing = ++listingGeneration;
            pageLifetime?.Cancel();
            generation++;
            try
            {
                status.Text = "Loading avatars…";
                AvatarItem[] array;
                if (favoritesOnly)
                {
                    array = store.Preferences.Favorites.Select((string x) => new AvatarItem(x, Path.GetFileNameWithoutExtension(x))).ToArray();
                }
                else
                {
                    AvatarItem[] array2 = ((!(category.SelectedItem is AvatarCategory avatarCategory)) ? Array.Empty<AvatarItem>() : (await catalog.ItemsAsync(avatarCategory.Id, "characters", lifetime.Token)));
                    array = array2;
                }
                AvatarItem[] array3 = array;
                if (listing == listingGeneration && !lifetime.IsCancellationRequested)
                {
                    items = array3;
                    offset = 0;
#if !NXI_NATIVE_LINUX
                    attribution.Text = ((category.SelectedItem is AvatarCategory avatarCategory2 && avatarCategory2.Id == AvatarCatalog.IslandCategory.Id) ? "Hello Kitty Island Adventure · © 2026 SANRIO CO., LTD. · Sunblink" : "Nintendo Switch Online icon collections · henry-debruin/nso-icons");
#endif
                    status.Text = (catalog.Offline ? "Offline · showing cached avatars" : "Select an avatar to preview it.");
                    await RenderAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex2)
            {
                if (!lifetime.IsCancellationRequested)
                {
                    status.Text = ex2.Message;
                }
            }
        }
        void Preview(byte[]? bytes)
        {
            preview.Source = null;
            previewBitmap?.Dispose();
            previewBitmap = ((bytes == null) ? null : new Bitmap(new MemoryStream(bytes)));
            preview.Source = previewBitmap;
        }
        void RefreshProfiles()
        {
            updatingProfiles = true;
            try
            {
                profile.ItemsSource = store.Preferences.Profiles.Select((AvatarProfile p) => p.Name).ToArray();
                profile.SelectedIndex = Array.FindIndex(store.Preferences.Profiles, (AvatarProfile p) => p.Id == store.Active.Id);
            }
            finally
            {
                updatingProfiles = false;
            }
            candidate = null;
            selectedPath = null;
            Preview(store.ReadImage());
            Button button8 = use;
            bool isEnabled2 = (favorite.IsEnabled = false);
            button8.IsEnabled = isEnabled2;
        }
        async Task RenderAsync()
        {
            int version = ++generation;
            pageLifetime?.Cancel();
            pageLifetime?.Dispose();
            pageLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            CancellationToken ct = pageLifetime.Token;
            tiles.Children.Clear();
            foreach (Bitmap item2 in images)
            {
                item2.Dispose();
            }
            images.Clear();
            AvatarItem[] array = items.Where((AvatarItem x) => string.IsNullOrWhiteSpace(search.Text) || (x.Label + " " + x.Path).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            offset = Math.Clamp(offset, 0, Math.Max(0, (array.Length - 1) / 24 * 24));
            previous.IsEnabled = offset > 0;
            next.IsEnabled = offset + 24 < array.Length;
            page.Text = ((array.Length == 0) ? "No matching avatars" : $"{offset + 1}–{Math.Min(offset + 24, array.Length)} of {array.Length}");
            await Task.WhenAll(array.Skip(offset).Take(24).Select((Func<AvatarItem, Task>)(async (AvatarItem item) =>
            {
                Image image = new Image
                {
                    Width = 96.0,
                    Height = 96.0,
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                byte[] bytes = null;
                Button button8 = VisualTheme.Button(item.Label, () =>
                {
                    if (bytes != null)
                    {
                        candidate = bytes;
                        selectedPath = item.Path;
                        Preview(bytes);
                        Button button9 = use;
                        bool isEnabled2 = (favorite.IsEnabled = true);
                        button9.IsEnabled = isEnabled2;
                        favorite.Content = (store.Preferences.Favorites.Contains(selectedPath) ? "Remove favorite" : "Add favorite");
                        status.Text = item.Label + " · choose Use avatar to save";
                    }
                });
                button8.Name = "AvatarTile";
                bool flag2 = item.Path.StartsWith(AvatarCatalog.IslandCategory.Id + "/", StringComparison.Ordinal);
                button8.Content = (flag2 ? ((Control)VisualTheme.Column(image, IslandLabel(item.Label))) : ((Control)image));
                if (flag2)
                {
                    button8.Background = Brush.Parse("#FFF0F6");
                    button8.Foreground = Brush.Parse("#582D49");
                    button8.CornerRadius = new CornerRadius(16.0);
                }
                button8.Margin = new Thickness(3.0);
                button8.Padding = new Thickness(3.0);
                button8.IsEnabled = false;
                AutomationProperties.SetName(button8, item.Path);
                ToolTip.SetTip(button8, item.Path);
                tiles.Children.Add(button8);
                try
                {
                    bytes = await catalog.ImageAsync(item.Path, ct);
                    if (version == generation && !ct.IsCancellationRequested)
                    {
                        Bitmap bitmap = new Bitmap(new MemoryStream(bytes));
                        images.Add(bitmap);
                        image.Source = bitmap;
                        button8.IsEnabled = true;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception ex2) when ((ex2 is IOException || ex2 is HttpRequestException || ex2 is InvalidDataException || ex2 is ArgumentException || ex2 is OperationCanceledException) ? true : false)
                {
                    if (version == generation)
                    {
                        button8.Content = VisualTheme.LocalizedText("Unavailable", 11.0);
                        ToolTip.SetTip(button8, item.Path + "\n" + ex2.Message);
                        status.Text = "Some images are unavailable. Retry the collection or import a local image.";
                    }
                }
            }))
                .ToArray());
        }
        static WrapPanel Wrap(params Control[] controls)
        {
            WrapPanel wrapPanel2 = new WrapPanel
            {
                Orientation = Orientation.Horizontal
            };
            foreach (Control control in controls)
            {
                control.Margin = new Thickness(0.0, 0.0, 6.0, 4.0);
                wrapPanel2.Children.Add(control);
            }
            return wrapPanel2;
        }
    }
}
