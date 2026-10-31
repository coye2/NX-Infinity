using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using NxInfinity.Runtime;

namespace NxInfinity;

public static class Dialogs
{
    internal static Window Create(string title, double width = 720.0, double height = 570.0)
    {
        Window dialog = new Window
        {
            Title = title + " · NX Infinity",
            Width = width,
            Height = height,
            MinWidth = 520.0,
            MinHeight = 380.0,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = VisualTheme.Canvas,
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NxInfinity/Assets/nx-infinity.ico")))
        };
        dialog.KeyDown += (object? _, KeyEventArgs e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
            else
            {
                Key key = e.Key;
                bool flag = (uint)(key - 23) <= 3u;
                if (flag && !(dialog.FocusManager?.GetFocusedElement() is TextBox))
                {
                    Navigation.Move(dialog, e.Key);
                    e.Handled = true;
                }
            }
        };
        return dialog;
    }

    internal static async Task<T> ShowAsync<T>(Window owner, Window dialog, CancellationToken cancellationToken = default(CancellationToken))
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (owner.Bounds.Width > 0.0 && owner.Bounds.Height > 0.0)
        {
            dialog.Width = Math.Min(dialog.Width, Math.Max(dialog.MinWidth, owner.Bounds.Width - 32.0));
            dialog.Height = Math.Min(dialog.Height, Math.Max(dialog.MinHeight, owner.Bounds.Height - 32.0));
        }
        if (owner is MainWindow { IsHomeMode: not false } || owner.Classes.Contains("home-dialog"))
        {
            HomeTheme homeTheme;
            if (owner is MainWindow mainWindow2)
            {
                homeTheme = mainWindow2.HomePalette;
            }
            else
            {
                homeTheme = ((owner.Resources.TryGetValue("NxHomePalette", out object value) && value is HomeTheme homeTheme2) ? homeTheme2 : new HomeTheme());
            }
            dialog.Resources["NxHomePalette"] = homeTheme;
            dialog.Classes.Add("home-dialog");
            dialog.RequestedThemeVariant = (homeTheme.Palette.Night ? ThemeVariant.Dark : ThemeVariant.Light);
            dialog.Background = homeTheme.Canvas;
            dialog.Foreground = homeTheme.Ink;
            dialog.Styles.Add(homeTheme.Styles());
            if (dialog.Content is Control root)
            {
                homeTheme.Adapt(root);
            }
        }
        else
        {
            DuneTheme duneTheme;
            if (owner is MainWindow mainWindow3)
            {
                duneTheme = mainWindow3.DesktopPalette;
            }
            else
            {
                duneTheme = ((owner.Resources.TryGetValue("NxDesktopPalette", out object value2) && value2 is DuneTheme duneTheme2) ? duneTheme2 : new DuneTheme(owner.RequestedThemeVariant == ThemeVariant.Dark));
            }
            dialog.Resources["NxDesktopPalette"] = duneTheme;
            dialog.RequestedThemeVariant = (duneTheme.Night ? ThemeVariant.Dark : ThemeVariant.Light);
            dialog.Background = duneTheme.Canvas;
            dialog.Foreground = duneTheme.Ink;
            dialog.Styles.Add(duneTheme.Styles());
            if (dialog.Content is Control root2)
            {
                duneTheme.Adapt(root2);
            }
        }
        Window previousWindow = Navigation.CurrentWindow;
        Control previousFocus = owner.FocusManager?.GetFocusedElement() as Control;
        dialog.Opened += (object? _, EventArgs _) =>
        {
            Navigation.CurrentWindow = dialog;
            Dispatcher.UIThread.Post(() =>
            {
                Navigation.FocusFirst(dialog);
            }, DispatcherPriority.Loaded);
        };
        using (cancellationToken.Register(() =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            });
        }))
        {
            try
            {
                return await dialog.ShowDialog<T>(owner);
            }
            finally
            {
                Navigation.CurrentWindow = ((previousWindow != null && previousWindow.IsVisible) ? previousWindow : owner);
                if (owner.IsVisible)
                {
                    owner.Activate();
                    if (previousFocus != null && previousFocus.IsEnabled && previousFocus.IsVisible && TopLevel.GetTopLevel(previousFocus) == owner)
                    {
                        Navigation.Focus(previousFocus);
                    }
                    else
                    {
                        Navigation.FocusFirst(owner);
                    }
                }
            }
        }
    }

    private static Border Body(Control child)
    {
        return new Border
        {
            Padding = new Thickness(26.0),
            Child = child
        };
    }

    private static ScrollViewer Scroll(Control content)
    {
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    public static async Task MessageAsync(Window owner, string title, string message, CancellationToken cancellationToken = default(CancellationToken))
    {
        Window dialog = Create(title, 730.0, 480.0);
        Button button = VisualTheme.LocalizedButton("Close", () =>
        {
            dialog.Close();
        }, "primary");
        button.Tag = "primary-focus";
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 18.0
        };
        grid.Children.Add(VisualTheme.Text(title, 25.0, null, FontWeight.DemiBold));
        ScrollViewer scrollViewer = Scroll(VisualTheme.Text(message));
        Grid.SetRow(scrollViewer, 1);
        grid.Children.Add(scrollViewer);
        Grid.SetRow(button, 2);
        grid.Children.Add(button);
        dialog.Content = Body(grid);
        await ShowAsync<object>(owner, dialog, cancellationToken);
    }

    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string? confirm = null, CancellationToken cancellationToken = default(CancellationToken), bool gameInput = false)
    {
        Window dialog = Create(title, 710.0, 410.0);
        if (gameInput)
        {
            dialog.Tag = "game-input";
        }
        Button button = VisualTheme.LocalizedButton("Back", () =>
        {
            dialog.Close(false);
        });
        Button button2 = VisualTheme.Button(confirm ?? UiText.Get("Continue"), () =>
        {
            dialog.Close(true);
        }, "primary");
        (gameInput ? button2 : button).Tag = "primary-focus";
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 20.0
        };
        grid.Children.Add(VisualTheme.Text(title, 25.0, null, FontWeight.DemiBold));
        ScrollViewer scrollViewer = Scroll(VisualTheme.Text(message));
        Grid.SetRow(scrollViewer, 1);
        grid.Children.Add(scrollViewer);
        StackPanel stackPanel = VisualTheme.Row(button, button2);
        Grid.SetRow(stackPanel, 2);
        grid.Children.Add(stackPanel);
        dialog.Content = Body(grid);
        return ShowAsync<bool>(owner, dialog, cancellationToken);
    }

    public static Task<string?> ChooseAsync(Window owner, string title, IReadOnlyList<(string Id, string Label)> options, string? explanation = null, CancellationToken cancellationToken = default(CancellationToken), bool gameInput = false)
    {
        Window dialog = Create(title, 740.0, 590.0);
        if (gameInput)
        {
            dialog.Tag = "game-input";
        }
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 10.0
        };
        foreach (var option in options)
        {
            string id = option.Id;
            string item = option.Label;
            Button button = VisualTheme.Button(item, () =>
            {
                dialog.Close(id);
            });
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Content = VisualTheme.Text(item);
            stackPanel.Children.Add(button);
        }
        if (options.Count == 0)
        {
            stackPanel.Children.Add(VisualTheme.LocalizedText("No available choices.", 15.0, VisualTheme.Muted));
        }
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 16.0
        };
        grid.Children.Add(VisualTheme.Text(title, 25.0, null, FontWeight.DemiBold));
        TextBlock textBlock = VisualTheme.Text(explanation ?? UiText.Get("Choose an item."), 14.0, VisualTheme.Muted);
        Grid.SetRow(textBlock, 1);
        grid.Children.Add(textBlock);
        ScrollViewer scrollViewer = Scroll(stackPanel);
        Grid.SetRow(scrollViewer, 2);
        grid.Children.Add(scrollViewer);
        Button button2 = VisualTheme.LocalizedButton("Back", () =>
        {
            dialog.Close();
        });
        Grid.SetRow(button2, 3);
        grid.Children.Add(button2);
        dialog.Content = Body(grid);
        return ShowAsync<string>(owner, dialog, cancellationToken);
    }

    public static Task<string?> TextAsync(Window owner, string title, string initial = "", int maximumLength = 4096, bool secret = false, int minimumLength = 0, CancellationToken cancellationToken = default(CancellationToken))
    {
        Window dialog = Create(title, 860.0, 610.0);
        dialog.MinWidth = 560.0;
        TextBox input = new TextBox
        {
            Text = initial,
            MaxLength = maximumLength,
            Tag = "onscreen-keyboard",
            PlaceholderText = title,
            PasswordChar = (secret ? '●' : '\0'),
            Name = "DialogTextInput",
            ClearSelectionOnLostFocus = false
        };
        StackPanel keyboard = new StackPanel
        {
            Spacing = 7.0
        };
        bool upper = false;
        bool symbols = false;
        BuildKeys();
        Button button = VisualTheme.LocalizedButton("Shift", () =>
        {
            upper = !upper;
            BuildKeys();
        });
        Button button2 = VisualTheme.LocalizedButton("Symbols", () =>
        {
            symbols = !symbols;
            BuildKeys();
        });
        Button button3 = VisualTheme.LocalizedButton("Space", () =>
        {
            Insert(" ");
        });
        Button button4 = VisualTheme.LocalizedButton("Backspace", () =>
        {
            string text = input.Text ?? "";
            int num = Math.Clamp(input.CaretIndex, 0, text.Length);
            int num2 = Math.Clamp(Math.Min(input.SelectionStart, input.SelectionEnd), 0, text.Length);
            int num3 = Math.Clamp(Math.Max(input.SelectionStart, input.SelectionEnd), num2, text.Length);
            if (num2 != num3)
            {
                input.Text = text.Remove(num2, num3 - num2);
                input.CaretIndex = num2;
            }
            else if (num > 0)
            {
                input.Text = text.Remove(num - 1, 1);
                input.CaretIndex = num - 1;
            }
            TextBox textBox = input;
            int selectionStart = (input.SelectionEnd = input.CaretIndex);
            textBox.SelectionStart = selectionStart;
        });
        Button done = VisualTheme.LocalizedButton("Done", () =>
        {
            dialog.Close(input.Text ?? "");
        }, "primary");
        done.Name = "DialogTextDone";
        input.TextChanged += (object? _, TextChangedEventArgs _) =>
        {
            ValidateLength();
        };
        ValidateLength();
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            RowSpacing = 12.0
        };
        grid.Children.Add(VisualTheme.Text(title, 25.0, null, FontWeight.DemiBold));
        Grid.SetRow(input, 1);
        grid.Children.Add(input);
        ScrollViewer scrollViewer = Scroll(keyboard);
        Grid.SetRow(scrollViewer, 2);
        grid.Children.Add(scrollViewer);
        StackPanel stackPanel = VisualTheme.Row(button, button2, button3, button4);
        Grid.SetRow(stackPanel, 3);
        grid.Children.Add(stackPanel);
        StackPanel stackPanel2 = VisualTheme.Row(VisualTheme.LocalizedButton("Back", () =>
        {
            dialog.Close();
        }), done);
        Grid.SetRow(stackPanel2, 4);
        grid.Children.Add(stackPanel2);
        dialog.Content = Body(grid);
        dialog.Opened += (object? _, EventArgs _) =>
        {
            input.CaretIndex = (input.Text ?? "").Length;
        };
        return ShowAsync<string>(owner, dialog, cancellationToken);
        void BuildKeys()
        {
            keyboard.Children.Clear();
            string[] array = ((!symbols) ? new string[4] { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm" } : new string[4] { "1234567890", "!@#$%^&*()", "[]{}<>_+-=", "/\\:;.,?~|" });
            foreach (string text in array)
            {
                UniformGrid uniformGrid = new UniformGrid
                {
                    Rows = 1,
                    Columns = text.Length,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                string text2 = text;
                for (int j = 0; j < text2.Length; j++)
                {
                    char c = text2[j];
                    string value = (upper ? char.ToUpperInvariant(c).ToString() : c.ToString());
                    Button button5 = VisualTheme.Button(value, () =>
                    {
                        Insert(value);
                    });
                    button5.Margin = new Thickness(3.0);
                    button5.Padding = new Thickness(4.0);
                    button5.HorizontalAlignment = HorizontalAlignment.Stretch;
                    button5.HorizontalContentAlignment = HorizontalAlignment.Center;
                    button5.VerticalContentAlignment = VerticalAlignment.Center;
                    button5.MinWidth = 44.0;
                    button5.MinHeight = 44.0;
                    uniformGrid.Children.Add(button5);
                }
                keyboard.Children.Add(uniformGrid);
            }
        }
        void Insert(string value)
        {
            string text = input.Text ?? "";
            int num = Math.Clamp(Math.Min(input.SelectionStart, input.SelectionEnd), 0, text.Length);
            int num2 = Math.Clamp(Math.Max(input.SelectionStart, input.SelectionEnd), num, text.Length);
            if (num == num2)
            {
                num = (num2 = Math.Clamp(input.CaretIndex, 0, text.Length));
            }
            string text2 = text.Substring(0, num) + value + text.Substring(num2);
            if (text2.Length <= maximumLength)
            {
                input.Text = text2;
                input.CaretIndex = num + value.Length;
                TextBox textBox = input;
                int selectionStart = (input.SelectionEnd = input.CaretIndex);
                textBox.SelectionStart = selectionStart;
            }
        }
        void ValidateLength()
        {
            done.IsEnabled = (input.Text ?? "").Length >= minimumLength && (input.Text ?? "").Length <= maximumLength;
        }
    }

    public static Task<string?> BrowseAsync(Window owner, string title, string[] extensions, bool directory, string? initialDirectory = null)
    {
        return PickPathAsync(owner.StorageProvider, title, extensions, directory, initialDirectory);
    }

    public static async Task<string?> PickPathAsync(IStorageProvider provider, string title, string[] extensions, bool directory, string? initialDirectory = null)
    {
        if (directory ? (!provider.CanPickFolder) : (!provider.CanOpen))
        {
            throw new NotSupportedException("The desktop file picker is unavailable. Check your desktop portal or window session.");
        }
        IStorageFolder initial = null;
        if (initialDirectory != null && Path.IsPathFullyQualified(initialDirectory) && Directory.Exists(initialDirectory))
        {
            initial = await provider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(initialDirectory)));
        }
        try
        {
            if (directory)
            {
                IReadOnlyList<IStorageFolder> readOnlyList = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    SuggestedStartLocation = initial
                });
                try
                {
                    return (readOnlyList.Count == 0) ? null : LocalPickerPath(readOnlyList[0]);
                }
                finally
                {
                    foreach (IStorageFolder item in readOnlyList)
                    {
                        item.Dispose();
                    }
                }
            }
            FilePickerOpenOptions filePickerOpenOptions = new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = initial
            };
            object fileTypeFilter;
            if (extensions.Length != 0)
            {
                IReadOnlyList<FilePickerFileType> readOnlyList2 =
                [
                    new FilePickerFileType("Supported files")
                    {
                        Patterns = extensions.Select((string extension) => "*" + extension).ToArray()
                    }
                ];
                fileTypeFilter = readOnlyList2;
            }
            else
            {
                fileTypeFilter = null;
            }
            filePickerOpenOptions.FileTypeFilter = (IReadOnlyList<FilePickerFileType>?)fileTypeFilter;
            IReadOnlyList<IStorageFile> readOnlyList3 = await provider.OpenFilePickerAsync(filePickerOpenOptions);
            try
            {
                return (readOnlyList3.Count == 0) ? null : LocalPickerPath(readOnlyList3[0]);
            }
            finally
            {
                foreach (IStorageFile item2 in readOnlyList3)
                {
                    item2.Dispose();
                }
            }
        }
        finally
        {
            initial?.Dispose();
        }
    }

    private static string LocalPickerPath(IStorageItem item)
    {
        return item.TryGetLocalPath() ?? throw new NotSupportedException("Choose a file or folder available on this computer. This core cannot read a remote document URI.");
    }

    public static async Task<string?> SavePathAsync(Window owner, string title, string defaultName)
    {
        if (!owner.StorageProvider.CanSave)
        {
            throw new NotSupportedException("The desktop save dialog is unavailable.");
        }
        using IStorageFile file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultName,
            DefaultExtension = Path.GetExtension(defaultName).TrimStart('.'),
            ShowOverwritePrompt = true
        });
        if (file == null)
        {
            return null;
        }
        string text = LocalPickerPath(file);
        if (File.Exists(text) || Directory.Exists(text))
        {
            await MessageAsync(owner, UiText.Get("Choose a new file name"), UiText.Get("A file with this name already exists. Existing exports are preserved."));
            return null;
        }
        return text;
    }

    public static async Task<InteractionResponse> InteractionAsync(Window owner, InteractionRequest request, CancellationToken cancellationToken = default(CancellationToken))
    {
        bool flag;
        switch (request.Kind)
        {
            case "keyboard":
            case "software_keyboard":
            case "text":
            case "amiibo_name":
            case "dynamic_text":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag)
        {
            int num = Math.Clamp(Number("maximumLength", Number("maxLength", 4096)), 0, 4096);
            int minimumLength = Math.Clamp(Number("minimumLength", 0), 0, num);
            string text = await TextAsync(owner, Text("headerText", Text("title", UiText.Get("Game keyboard"))), Text("initialText", Text("text")), num, Has("password", out var value) && value.ValueKind == JsonValueKind.True, minimumLength, cancellationToken);
            return new InteractionResponse
            {
                InteractionId = request.InteractionId,
                Accepted = (text != null),
                Text = text,
                CursorBegin = text?.Length,
                CursorEnd = text?.Length,
                Key = ((!(request.Kind == "dynamic_text")) ? null : ((text == null) ? "escape" : "enter"))
            };
        }
        switch (request.Kind)
        {
            case "message":
            case "error":
            case "confirmation":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag)
        {
            if (Has("buttons", out var value2) && value2.ValueKind == JsonValueKind.Array)
            {
                int arrayLength = value2.GetArrayLength();
                if (arrayLength > 0 && arrayLength <= 32)
                {
                    (string, string)[] options = value2.EnumerateArray().Select((JsonElement button, int index) => (index.ToString(CultureInfo.InvariantCulture), (button.ValueKind == JsonValueKind.String) ? (button.GetString() ?? "Continue") : "Continue")).ToArray();
                    string text2 = await ChooseAsync(owner, Text("title", UiText.Get("Game message")), options, Text("message"), cancellationToken, gameInput: true);
                    return new InteractionResponse
                    {
                        InteractionId = request.InteractionId,
                        Accepted = (text2 != null),
                        ButtonIndex = ((text2 != null) ? int.Parse(text2, CultureInfo.InvariantCulture) : 0)
                    };
                }
            }
            bool accepted = await ConfirmAsync(owner, Text("title", UiText.Get("Game message")), Text("message", UiText.Get("The game requested confirmation.")), UiText.Get("OK"), cancellationToken, gameInput: true);
            return new InteractionResponse
            {
                InteractionId = request.InteractionId,
                Accepted = accepted,
                ButtonIndex = 0
            };
        }
        string kind = request.Kind;
        if ((kind == "controller" || kind == "controller_support") ? true : false)
        {
            await MessageAsync(owner, UiText.Get("Controller request"), UiText.Get("The game requested native controller setup. This development frontend supports one assigned player; this applet's requested player/style setup has not been qualified and will be canceled. Return to Controls to review the actual assignment."), cancellationToken);
            return new InteractionResponse
            {
                InteractionId = request.InteractionId,
                Accepted = false
            };
        }
        await MessageAsync(owner, UiText.Get("Game interaction unavailable"), UiText.Get("This core requested an interaction that this frontend does not support: ") + request.Kind + UiText.Get(". The request will be canceled."), cancellationToken);
        return new InteractionResponse
        {
            InteractionId = request.InteractionId,
            Accepted = false
        };
        bool Has(string key, out JsonElement reference)
        {
            reference = default;
            if (request.Details.ValueKind == JsonValueKind.Object)
            {
                return request.Details.TryGetProperty(key, out reference);
            }
            return false;
        }
        int Number(string key, int fallback)
        {
            if (!Has(key, out var value3) || !value3.TryGetInt32(out var value4))
            {
                return fallback;
            }
            return value4;
        }
        string Text(string key, string fallback = "")
        {
            if (!Has(key, out var value3) || value3.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }
            return value3.GetString() ?? fallback;
        }
    }

    public static async Task InteractionStreamAsync(Window owner, InteractionConversation conversation, CancellationToken ct)
    {
        bool flag;
        switch (conversation.Initial.Kind)
        {
            case "keyboard":
            case "software_keyboard":
            case "text":
            case "amiibo_name":
            case "dynamic_text":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (!flag)
        {
            InteractionConversation interactionConversation = conversation;
            await interactionConversation.RespondAsync(await InteractionAsync(owner, conversation.Initial, ct), ct);
            return;
        }
        Window dialog = Create(UiText.Get("Game keyboard"), 860.0, 650.0);
        dialog.Name = "GameKeyboard";
        dialog.Tag = "game-input";
        TextBox input = new TextBox
        {
            Name = "GameKeyboardInput",
            Tag = "onscreen-keyboard",
            ClearSelectionOnLostFocus = false,
            AcceptsReturn = false,
            AcceptsTab = false,
            PlaceholderText = UiText.Get("Type with your keyboard or click the keys below")
        };
        TextBlock heading = VisualTheme.LocalizedText("Game keyboard", 24.0);
        TextBlock description = VisualTheme.LocalizedText("", 14.0, VisualTheme.Muted);
        TextBlock status = VisualTheme.LocalizedText("", 13.0, VisualTheme.Muted);
        status.Name = "GameKeyboardStatus";
        StackPanel keys = new StackPanel
        {
            Spacing = 5.0
        };
        InteractionRequest current = conversation.Initial;
        bool live = current.Kind == "dynamic_text";
        bool suppress = false;
        bool dirty = false;
        bool sending = false;
        bool waiting = false;
        bool ended = false;
        int minimum = 0;
        int maximum = 4096;
        int flags = 0;
        string mode = "";
        string extraSymbols = ".";
        string validation = "";
        long? generation = null;
        SemaphoreSlim responses = new SemaphoreSlim(1, 1);
        InteractionResponse lastSent = null;
        bool upper = false;
        bool symbols = false;
        Button submit = VisualTheme.LocalizedButton("Done", () => Send("enter", validation == "confirm"), "primary");
        submit.Name = "GameKeyboardSubmit";
        Button cancel = VisualTheme.LocalizedButton("Cancel", () => Send("escape"));
        cancel.Name = "GameKeyboardCancel";
        Button retry = VisualTheme.LocalizedButton("Edit text", () =>
        {
            validation = "";
            waiting = false;
            input.IsReadOnly = false;
            status.Text = "";
            FocusInput();
        });
        retry.IsVisible = false;
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto"),
            RowSpacing = 12.0
        };
        grid.Children.Add(heading);
        Grid.SetRow(description, 1);
        grid.Children.Add(description);
        Grid.SetRow(input, 2);
        grid.Children.Add(input);
        ScrollViewer scrollViewer = Scroll(keys);
        Grid.SetRow(scrollViewer, 3);
        grid.Children.Add(scrollViewer);
        StackPanel stackPanel = VisualTheme.Row(VisualTheme.LocalizedButton("Shift", () =>
        {
            upper = !upper;
            BuildKeys();
        }), VisualTheme.LocalizedButton("Symbols", () =>
        {
            symbols = !symbols;
            BuildKeys();
        }), VisualTheme.LocalizedButton("Space", () =>
        {
            Insert(" ");
        }), VisualTheme.LocalizedButton("Delete", (Action)Erase, (string?)null));
        Grid.SetRow(stackPanel, 4);
        grid.Children.Add(stackPanel);
        foreach (Button item in stackPanel.Children.OfType<Button>())
        {
            KeepMouseTyping(item);
        }
        Grid.SetRow(status, 5);
        grid.Children.Add(status);
        StackPanel stackPanel2 = VisualTheme.Row(cancel, retry, submit, VisualTheme.LocalizedText("Keyboard / mouse · Enter: done   Esc: cancel   A: select   B: back   +: done", 12.0, VisualTheme.Muted));
        Grid.SetRow(stackPanel2, 6);
        grid.Children.Add(stackPanel2);
        dialog.Content = Body(grid);
        input.PropertyChanged += (object? _, AvaloniaPropertyChangedEventArgs e) =>
        {
            if (!suppress)
            {
                AvaloniaProperty property = e.Property;
                if ((object)property != null && (property == TextBox.TextProperty || property == TextBox.SelectionStartProperty || property == TextBox.SelectionEndProperty))
                {
                    dirty = live;
                    if (!waiting)
                    {
                        status.Text = Invalid(false) ?? "";
                    }
                }
            }
        };
        dialog.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (cancel.IsEnabled)
                {
                    _ = Send("escape");
                }
            }
            else if (e.Key == Key.Back && Flag("disableBackspace") && dialog.FocusManager?.GetFocusedElement() == input)
            {
                e.Handled = true;
            }
            else if (e.Key == Key.Return && dialog.FocusManager?.GetFocusedElement() == input)
            {
                e.Handled = true;
                if (submit.IsEnabled)
                {
                    _ = Send("enter", validation == "confirm");
                }
            }
        }, RoutingStrategies.Tunnel);
        dialog.AddHandler(InputElement.TextInputEvent, (object? _, TextInputEventArgs e) =>
        {
            if (dialog.FocusManager?.GetFocusedElement() != input && !string.IsNullOrEmpty(e.Text))
            {
                e.Handled = true;
                if (!waiting && !ended && !input.IsReadOnly)
                {
                    Insert(e.Text);
                    FocusInput();
                }
            }
        }, RoutingStrategies.Tunnel);
        dialog.Closing += (object? _, WindowClosingEventArgs e) =>
        {
            if (!ct.IsCancellationRequested && !ended)
            {
                e.Cancel = true;
                if (cancel.IsEnabled)
                {
                    _ = Send("escape");
                }
            }
        };
        BuildKeys();
        Apply(current);
        DispatcherTimer timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40L)
        };
        timer.Tick += async (object? _, EventArgs _) =>
        {
            if (dirty && !waiting && !sending)
            {
                await Send();
            }
        };
        timer.Start();
        Task reader = ReadUpdates();
        try
        {
            await ShowAsync<object>(owner, dialog, ct);
        }
        finally
        {
            ended = true;
            timer.Stop();
            await reader;
        }
        void Apply(InteractionRequest request)
        {
            if (ended || request.InteractionId != conversation.Initial.InteractionId)
            {
                return;
            }
            long? num = generation;
            current = request;
            suppress = true;
            try
            {
                maximum = Math.Clamp(Number("maximumLength", Number("maxLength", 4096)), 0, 4096);
                minimum = Math.Clamp(Number("minimumLength", 0), 0, maximum);
                generation = ((Has("generation", out var value) && value.TryGetInt64(out var value2)) ? new long?(value2) : ((long?)null));
                mode = Text("keyboardMode", Text("mode"));
                flags = Number("disabledCharacters", 0);
                extraSymbols = Text("extraSymbols", ".");
                validation = Text("validation");
                heading.Text = Text("headerText", Text("title", UiText.Get("Game keyboard")));
                description.Text = string.Join("\n", new string[2]
                {
                    Text("subtitle"),
                    Text("guide")
                }.Where((string s) => s.Length != 0));
                input.MaxLength = maximum;
                input.PasswordChar = (Flag("password") ? '●' : '\0');
                string text = Text("text", Text("initialText", input.Text ?? ""));
                bool flag2 = num == generation;
                if (!(live & flag2) || validation.Length != 0 || (object)lastSent == null || lastSent.Key != null || !(text == lastSent.Text) || !(text != (input.Text ?? "")))
                {
                    if (input.Text != text)
                    {
                        input.Text = text;
                    }
                    int num2 = Math.Clamp(Number("cursorBegin", text.Length), 0, text.Length);
                    int num3 = Math.Clamp(Number("cursorEnd", num2), 0, text.Length);
                    input.CaretIndex = num3;
                    input.SelectionStart = num2;
                    input.SelectionEnd = num3;
                    dirty = false;
                }
                if (!(live & flag2 & waiting) || validation.Length != 0)
                {
                    waiting = false;
                }
                input.IsReadOnly = waiting || validation == "confirm";
                retry.IsVisible = validation == "confirm";
                submit.Content = ((validation == "confirm") ? UiText.Get("Confirm") : Text("submitText", UiText.Get("Done")));
                cancel.IsEnabled = !Flag("disableCancel");
                submit.IsEnabled = !Flag("disableReturn");
                if (!waiting)
                {
                    status.Text = Text("message");
                }
            }
            finally
            {
                suppress = false;
            }
        }
        void BuildKeys()
        {
            keys.Children.Clear();
            string[] array = ((!symbols) ? new string[4] { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm" } : new string[4] { "1234567890", "!@#$%^&*()", "[]{}<>_+-=", "/\\:;.,?~|" });
            foreach (string text in array)
            {
                UniformGrid uniformGrid = new UniformGrid
                {
                    Rows = 1,
                    Columns = text.Length
                };
                string text2 = text;
                for (int j = 0; j < text2.Length; j++)
                {
                    char c = text2[j];
                    string value = (upper ? char.ToUpperInvariant(c).ToString() : c.ToString());
                    Button button = VisualTheme.Button(value, () =>
                    {
                        Insert(value);
                    });
                    button.Margin = new Thickness(2.0);
                    button.Padding = new Thickness(3.0);
                    button.HorizontalAlignment = HorizontalAlignment.Stretch;
                    button.MinWidth = 44.0;
                    button.MinHeight = 44.0;
                    KeepMouseTyping(button);
                    uniformGrid.Children.Add(button);
                }
                keys.Children.Add(uniformGrid);
            }
        }
        void Edit(Action action)
        {
            if (!(waiting | ended) && !input.IsReadOnly)
            {
                action();
                dirty = live;
                status.Text = Invalid(false) ?? "";
            }
        }
        void Erase()
        {
            if (!Flag("disableBackspace"))
            {
                string text = input.Text ?? "";
                int begin = Math.Clamp(Math.Min(input.SelectionStart, input.SelectionEnd), 0, text.Length);
                int end = Math.Clamp(Math.Max(input.SelectionStart, input.SelectionEnd), 0, text.Length);
                if (begin == end && begin > 0)
                {
                    begin--;
                    if (begin > 0 && char.IsLowSurrogate(text[begin]) && char.IsHighSurrogate(text[begin - 1]))
                    {
                        begin--;
                    }
                }
                Edit(() =>
                {
                    input.Text = text.Substring(0, begin) + text.Substring(end);
                    input.CaretIndex = begin;
                    TextBox textBox = input;
                    int selectionStart = (input.SelectionEnd = begin);
                    textBox.SelectionStart = selectionStart;
                });
            }
        }
        bool Flag(string key)
        {
            if (Has(key, out var value))
            {
                return value.ValueKind == JsonValueKind.True;
            }
            return false;
        }
        void FocusInput()
        {
            if (dialog.IsVisible && !waiting && !ended && !input.IsReadOnly)
            {
                input.Focus(NavigationMethod.Pointer);
            }
        }
        bool Has(string key, out JsonElement value)
        {
            value = default;
            if (current.Details.ValueKind == JsonValueKind.Object)
            {
                return current.Details.TryGetProperty(key, out value);
            }
            return false;
        }
        void Insert(string value)
        {
            string text = input.Text ?? "";
            int begin = Math.Clamp(Math.Min(input.SelectionStart, input.SelectionEnd), 0, text.Length);
            int end = Math.Clamp(Math.Max(input.SelectionStart, input.SelectionEnd), 0, text.Length);
            if (text.Length - (end - begin) + value.Length <= maximum)
            {
                Edit(() =>
                {
                    input.Text = text.Substring(0, begin) + value + text.Substring(end);
                    input.CaretIndex = begin + value.Length;
                    TextBox textBox = input;
                    int selectionStart = (input.SelectionEnd = input.CaretIndex);
                    textBox.SelectionStart = selectionStart;
                });
            }
        }
        string? Invalid(bool flag2)
        {
            string text = input.Text ?? "";
            if (text.Length > maximum || (flag2 && text.Length < minimum))
            {
                return UiText.Format("Use {0}–{1} characters.", minimum, maximum);
            }
            string text2 = text;
            foreach (char c in text2)
            {
                if ((mode.Equals("numeric", StringComparison.OrdinalIgnoreCase) || mode.Equals("numberpad", StringComparison.OrdinalIgnoreCase)) && (c < '0' || c > '9') && !extraSymbols.Contains(c))
                {
                    return UiText.Get("Use the game's numeric keys.");
                }
                if (mode.Equals("ascii", StringComparison.OrdinalIgnoreCase) && c > '\u007f')
                {
                    return UiText.Get("This game field accepts ASCII characters.");
                }
                if (((flags & 2) != 0 && c == ' ') || ((flags & 0x104) != 0 && c == '@') || ((flags & 0x108) != 0 && c == '%') || ((flags & 0x10) != 0 && c == '/') || ((flags & 0x120) != 0 && c == '\\') || ((flags & 0x40) != 0 && c >= '0' && c <= '9'))
                {
                    return UiText.Get("This game field does not accept that character.");
                }
            }
            return null;
        }
        void KeepMouseTyping(Button button)
        {
            button.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    Dispatcher.UIThread.Post(FocusInput, DispatcherPriority.Background);
                }
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        int Number(string key, int fallback)
        {
            if (!Has(key, out var value) || !value.TryGetInt32(out var value2))
            {
                return fallback;
            }
            return value2;
        }
        async Task ReadUpdates()
        {
            _ = 2;
            try
            {
                await foreach (InteractionRequest request in conversation.Updates.ReadAllAsync(ct))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Apply(request);
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
        async Task Send(string? key = null, bool confirmed = false)
        {
            if (((ended || ct.IsCancellationRequested) | waiting) || ((key == null) & sending))
            {
                return;
            }
            if (key != "escape")
            {
                string text = Invalid(key != null);
                if (text != null)
                {
                    status.Text = text;
                    return;
                }
            }
            InteractionResponse response = Snapshot(key, confirmed);
            if (key != null)
            {
                waiting = true;
                input.IsReadOnly = true;
                status.Text = UiText.Get("Waiting for the game…");
            }
            else
            {
                sending = true;
            }
            dirty = false;
            bool acquired = false;
            try
            {
                await responses.WaitAsync(ct);
                acquired = true;
                if (!ended && !ct.IsCancellationRequested)
                {
                    lastSent = response;
                    await conversation.RespondAsync(response, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (NxException ex2)
            {
                waiting = false;
                input.IsReadOnly = false;
                status.Text = UiText.Get("The game did not accept this response: ") + ex2.Code;
            }
            finally
            {
                if (acquired)
                {
                    responses.Release();
                }
                if (key == null)
                {
                    sending = false;
                }
            }
        }
        InteractionResponse Snapshot(string? key, bool confirmed = false)
        {
            return new InteractionResponse
            {
                InteractionId = current.InteractionId,
                Accepted = (key != "escape"),
                Text = (input.Text ?? ""),
                CursorBegin = Math.Clamp(input.SelectionStart, 0, (input.Text ?? "").Length),
                CursorEnd = Math.Clamp(input.SelectionEnd, 0, (input.Text ?? "").Length),
                Key = key,
                Generation = generation,
                Confirmed = (confirmed ? new bool?(true) : ((bool?)null))
            };
        }
        string Text(string key, string fallback = "")
        {
            if (!Has(key, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }
            return value.GetString() ?? fallback;
        }
    }
}
