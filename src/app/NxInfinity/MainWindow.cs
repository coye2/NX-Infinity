using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NxInfinity.Runtime;
using NxInfinity.Runtime.Enhancements;
using NxInfinity.Runtime.Services;
using SkiaSharp;

namespace NxInfinity;

public sealed class MainWindow : Window
{
    private sealed record FrameRateReadouts(GameLibraryEntry? Game, TextBlock Requested, TextBlock Effective, TextBlock Measured);

    private sealed record HomeFocusTarget(Button Button, string Group, string Key);

    private const int MaximumArtworkSide = 3840;

    private readonly LeasedResourceCache<Bitmap> _artworkCache = new LeasedResourceCache<Bitmap>(134217728L);

    private readonly SemaphoreSlim _artworkDecoders = new SemaphoreSlim(2, 2);

    private CancellationTokenSource _artworkPageLifetime = new CancellationTokenSource();

    private IReadOnlyList<GameLibraryEntry>? _artworkLibrary;

    private readonly Dictionary<Border, Func<Task>> _artworkRefreshers = new Dictionary<Border, Func<Task>>();

    private readonly Dictionary<Border, long> _artworkRequests = new Dictionary<Border, long>();

    private long _artworkRequestSequence;

    private DispatcherTimer? _artworkResizeTimer;

    private int _artworkWindowBucket;

    private Bitmap? _avatarBitmap;

    private Button? _desktopAvatar;

    private Button? _homeAvatar;

    private readonly Border _bootOverlay = new Border
    {
        Name = "BootOverlay",
        IsVisible = false,
        Background = Brush.Parse("#88000000")
    };

    private readonly Border _bootCard = new Border
    {
        Name = "BootCard",
        MaxWidth = 520.0,
        Margin = new Thickness(24.0),
        Padding = new Thickness(28.0),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(18.0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _bootTitle = VisualTheme.LocalizedText("Starting game", 25.0, null, FontWeight.DemiBold);

    private readonly TextBlock _bootStage = VisualTheme.LocalizedText("Checking game files");

    private readonly TextBlock _bootHeading = VisualTheme.LocalizedText("STARTING GAME", 12.0, VisualTheme.Blue, FontWeight.DemiBold);

    private readonly ProgressBar _bootProgress = new ProgressBar
    {
        Name = "BootProgress",
        Height = 6.0,
        Minimum = 0.0,
        Maximum = 1.0
    };

    private Button? _bootCancel;

    private Control? _bootPreviousFocus;

    private bool _compatibilityView;

    private bool _compatibilityAllTitles;

    private bool _compatibilityThisCore;

    private const string GraphicsCaptureGuide = "1. Use the same core, full game build, driver, update/DLC and mod selection for each comparison. Read active graphics device to record the selected renderer. AMD Z1 Extreme and RTX 4060 Ti need separate results.\n\n2. Reproduce the same scene. For Smash's white glow, the primary report names Mario Bros. and Peach's Castle; for the separate flicker/stutter report, repeat the same eight-fighter match on Big Battlefield. For Scarlet/Violet missing buildings, use the same city location and camera sweep, including Mesagoza. Record foliage while moving the camera, leaving the area and returning.\n\n3. Start a new capture from Performance, play the route, then open the menu and choose Record current issue on the matching report. This records a fresh native observation boundary; it does not identify the exact broken draw. Export the local report and keep a matching motion recording.\n\n4. Compare original behavior with one explicit candidate at a time. Keep existing caches; use distinct clean-test and warm-test profiles when a cold-cache comparison is needed. Repeat a warm run after restarting the app. Check real game speed against wall time, animation, physics, input, audio, saves, loading and sustained play.\n\n5. Candidate AMD logic-op work remains unverified on your physical driver until tested. Source or authored-fixture success alone cannot establish a Smash or Pokémon fix. Restore the recorded setting or original core package after the comparison. Do not combine a rendering test with FPS, population, difficulty or spawn changes.";

    private ControllerMappingStore? _mappingStore;

    private Action<IReadOnlyList<HostControllerSnapshot>>? _mappingCapture;

    private bool _mappingNeedsNeutral;

    private ControllerRoster? _controllerRoster;

    private readonly ControllerGuestRouter _guestControllers = new ControllerGuestRouter();

    private readonly ContentControl _page = new ContentControl();

    private readonly TextBlock _status = VisualTheme.LocalizedText("Ready", 13.0, VisualTheme.Muted);

    private readonly TextBlock _metrics = VisualTheme.LocalizedText("", 13.0, VisualTheme.Muted);

    private readonly TextBlock _inputHint = VisualTheme.LocalizedText("Enter  Open     Esc  Back     Tab  Move focus", 12.0, VisualTheme.Muted);

    private readonly TextBlock _clock = VisualTheme.Text(DateTime.Now.ToString("h:mm tt"), 13.0, VisualTheme.Muted);

    private readonly Button _backend;

    private readonly Button _error;

    private readonly Button _cancel;

    private readonly ProgressBar _progress = new ProgressBar
    {
        Height = 3.0,
        Minimum = 0.0,
        Maximum = 1.0,
        IsVisible = false
    };

    private readonly Dictionary<string, Button> _navigation = new Dictionary<string, Button>();

    private readonly DispatcherTimer _telemetryTimer;

    private readonly DispatcherTimer _inputTimer;

    private HostControllerInput? _controllers;

    private readonly ControllerNavigation _controllerNavigation;

    private bool _pollingTelemetry;

    private bool _sendingInput;

    private bool _closing;

    private CancellationTokenSource? _validationCancellation;

    private Task<int>? _validationRun;

    private string _section = "Library";

    private long _navigationGeneration;

    private string? _selectedEntry;

    private string _search = "";

    private bool _favorites;

    private LibrarySort _sort;

    private int _sessionUpdateQueued;

    private readonly SemaphoreSlim _detailInventoryGate = new SemaphoreSlim(1, 1);

    private long _detailInventoryGeneration;

    private DuneTheme _dune = new DuneTheme();

    private DesktopPreferenceStore _desktopPreferenceStore;

    private DesktopPreferences _desktopPreferences = new DesktopPreferences();

    private IReadOnlyList<GameLibraryEntry> _desktopGames = Array.Empty<GameLibraryEntry>();

    private ProfileSnapshot? _desktopRoutes;

    private DuneLibraryShelf? _duneShelf;

    private ContentControl? _duneOverview;

    private TextBlock? _duneEmpty;

    private double _desktopLibraryOffset;

    private DispatcherTimer? _desktopPreferenceTimer;

    private HomeAudio? _homeAudio;

    private string? _homeAudioIssue;

    private long _lastSoundFailure;

    private readonly List<FrameRateReadouts> _frameRateReadouts = new List<FrameRateReadouts>();

    private readonly ContentControl _homeSurface = new ContentControl
    {
        Name = "HomeSurface",
        IsVisible = false
    };

    private Control? _desktopSurface;

    private HomePreferenceStore? _homePreferenceStore;

    private HomePreferences _homePreferences = new HomePreferences();

    private DispatcherTimer? _homePreferenceTimer;

    private bool _homeMode;

    private bool _homeDetails;

    private string? _homeSelectedEntry;

    private string _homeFilter = "all";

    private string _homeSearch = "";

    private WindowState _desktopWindowState;

    private WindowDecorations _desktopDecorations;

    private Size _homeLayoutSize;

    private GameLibraryEntry[] _homeGames = Array.Empty<GameLibraryEntry>();

    private readonly Dictionary<string, (Button Button, Border Card)> _homeCards = new Dictionary<string, (Button, Border)>();

    private ScrollViewer? _homeRail;

    private Control? _homePageContent;

    private TextBlock? _homeClock;

    private TextBlock? _homeControllerLabel;

    private TextBlock? _homeStatus;

    private TextBlock? _homePlayLabel;

    private TextBlock? _homePosition;

    private Button? _homePlay;

    private Button? _homeStop;

    private Button? _homeOptions;

    private Button? _homeCancel;

    private readonly Dictionary<string, HomeFocusTarget> _homeTargets = new Dictionary<string, HomeFocusTarget>();

    private string? _homeFocusKey;

    private static readonly string[] HomeFocusGroups = new string[5] { "top", "filters", "games", "dock", "footer" };

    private readonly List<IDisposable> _pageImages = new List<IDisposable>();

    private int _libraryVisibleCount = 60;

    private LibraryFolderMonitor? _folderMonitor;

    private DispatcherTimer? _folderMonitorTimer;

    private string _watchedFolders = "";

    private bool _monitorScanning;

    private readonly CancellationTokenSource _maintenanceLifetime = new CancellationTokenSource();

    private bool _maintenanceInterrupted;

    private readonly Dictionary<string, NintendoCatalogSelection?> _metadataForPage = new Dictionary<string, NintendoCatalogSelection>();

    private readonly Dictionary<string, TextBlock> _performanceReadouts = new Dictionary<string, TextBlock>();

    private PresentIntervalPlot? _presentPlot;

    private DiscordPresence? _discord;

    private readonly DispatcherTimer _presenceTimer = new DispatcherTimer
    {
        Interval = TimeSpan.FromSeconds(15L)
    };

    private bool _publishingPresence;

    private bool _presenceWasEnabled;

    private bool _customizingTheme;

    private string? _presenceGame;

    private string? _customGameActivity;

    private long _presenceStarted;

    private bool _allowExperimentalProfiles;

    private string _contentTab = "Setup";

    private int _contentReviewOffset;

    private bool _settingsForGame;

    private IReadOnlyList<HostControllerSnapshot> _hostDevices = Array.Empty<HostControllerSnapshot>();

    private TextBlock? _controllerReadout;

    private TextBlock? _displayReadout;

    private string _settingsCategory = "Graphics";

    private string _settingsSearch = "";

    private const string ControllerHelp = "NX Infinity screens\nD-pad or left stick: move focus\nSouth face button: confirm\nEast face button: back\nLB / RB: change main section\nRight stick: scroll\nGuide / HOME: open HOME while a gamepad controls the game\nHold View / Select + Menu / Start for half a second: HOME fallback (toggle in HOME audio & startup). If another app intercepts Guide, use the fallback or adjust that app's controller shortcuts.\n\nHOME mode\nLeft / right: browse the cover row\nUp / down: move between rows\nLB / RB: switch All games, Favorites and Recent\nStart / Plus: selected game options\nNorth / X: stop the current game\nEast / B: back to HOME, or return to desktop from its library\nGuide / HOME: resume a paused game from the HOME library\n\nMouse: every action is clickable; wheel scrolls.\nKeyboard: Tab / Shift+Tab move focus; Enter or Space activates; arrows move between controls; Escape goes back; Home opens HOME; F11 changes the game window when a game is loaded and the launcher otherwise. Text boxes retain ordinary cursor editing. Confirming a text box with a controller opens the on-screen keyboard.\n\nGameplay via the frontend gamepad\nFace buttons retain Nintendo positions: South=B, East=A, West=Y, North=X. Shoulder buttons=L/R; triggers=ZL/ZR; stick clicks and D-pad are forwarded. Menu controls never go to the guest; returning to play requires a neutral sample.\n\nNX Infinity Core keyboard (when player 1 is released from gamepad input)\nZ/X/C/V = A/B/X/Y; arrows = D-pad; WASD = left stick; IJKL = right stick; Q/E = ZL/L; U/O = R/ZR; F/H = stick clicks; Enter/Backspace = Plus/Minus. Escape opens the game menu; F11 toggles the real render window. \n\nTouch pointer: press both stick buttons together to toggle. Right stick moves the visible pointer; A taps. Mouse clicks also reach the emulated touchscreen. Toggle off to restore normal stick/A input. Physical-device behavior still needs checking on your hardware.";

    private Grid? _shellGrid;

    private Border? _contentRegion;

    private Border? _sessionStrip;

    private readonly Dictionary<string, TextBlock> _navigationLabels = new Dictionary<string, TextBlock>();

    private readonly List<(TextBlock Label, string English)> _shellLabels = new List<(TextBlock, string)>();

    private bool _compactRail;

    private Button? _sessionResume;

    private Button? _sessionStop;

    private Button? _sessionGraphics;

    private TextBlock? _sessionTitle;

    private readonly TextBlock _backendName = SingleLine(UiText.Get("NX Infinity Core"));

    private TextBox? _desktopSearch;

    private Button? _desktopFavorites;

    private Flyout? _openDesktopFlyout;

    private StartupSequence? _startupSequence;

    private ThemePreferenceStore _themeStore;

    private LocalTranslator? _translator;

    private readonly DispatcherTimer _translationTimer = new DispatcherTimer
    {
        Interval = TimeSpan.FromSeconds(3L)
    };

    private readonly CancellationTokenSource _translationLifetime = new CancellationTokenSource();

    private bool _translating;

    private Window? _translationOverlay;

    private TextBlock? _translationText;

    private string? _lastOcr;

    private string? _lastFrame;

    private string? _translationGame;

    private string _translationStatus = "Local translation is ready to configure.";

    internal static readonly IReadOnlyDictionary<string, string> OcrLanguages = new Dictionary<string, string>
    {
        ["en"] = "eng",
        ["it"] = "ita",
        ["es"] = "spa",
        ["fr"] = "fra",
        ["de"] = "deu",
        ["pt"] = "por",
        ["ja"] = "jpn",
        ["ko"] = "kor",
        ["zh"] = "chi_sim",
        ["ru"] = "rus",
        ["ar"] = "ara",
        ["nl"] = "nld",
        ["pl"] = "pol",
        ["tr"] = "tur",
        ["uk"] = "ukr",
        ["sv"] = "swe",
        ["fi"] = "fin",
        ["cs"] = "ces",
        ["da"] = "dan",
        ["el"] = "ell",
        ["hu"] = "hun",
        ["id"] = "ind",
        ["th"] = "tha",
        ["vi"] = "vie"
    };

    public AvatarStore Avatars { get; private set; }

    private ControllerMappingStore MappingStore => _mappingStore ?? (_mappingStore = new ControllerMappingStore(Session.Paths.DataRoot));

    private ControllerRoster ControllerRoster => _controllerRoster ?? (_controllerRoster = new ControllerRoster(Session.Paths.DataRoot));

    private ControllerAssignment ControllerAssignment => ControllerRoster.Players[0];

    public ControllerAssignmentState GameplayControllerAssignment => ControllerAssignment.State;

    public IReadOnlyList<ControllerAssignmentState> GameplayControllerAssignments => ControllerRoster.Players.Select((ControllerAssignment player) => player.State).ToArray();

    public InfinitySession Session { get; }

    public string? ValidationCloseReason { get; private set; }

    public string CurrentSection => _section;

    public string? SelectedEntryId
    {
        get
        {
            if (!_homeMode)
            {
                return _selectedEntry;
            }
            return _homeSelectedEntry;
        }
    }

    private bool CanOpenKeySetup
    {
        get
        {
            if (!Session.HasGame && Session.ErrorCode != null)
            {
                OperationFailure lastFailure = Session.LastFailure;
                if ((object)lastFailure != null && lastFailure.RecoveryAction == "import_local_keys" && lastFailure.BackendId == Session.SelectedBackendId)
                {
                    return string.Equals(lastFailure.BackendRevision, Session.SelectedBackend?.Revision, StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }
    }

    public DuneTheme DesktopPalette => _dune;

    public DuneLibraryShelf? DesktopShelf => _duneShelf;

    public bool ShowStartupSequence { get; init; }

    internal bool HomeFallbackChordEnabled => _homePreferences.HomeFallbackChord;

    public bool IsHomeMode => _homeMode;

    public bool IsHomeLibrary
    {
        get
        {
            if (_homeMode && _section == "Library")
            {
                return !_homeDetails;
            }
            return false;
        }
    }

    public string? HomeSelectedEntryId => _homeSelectedEntry;

    public string HomeFilter => _homeFilter;

    public bool HomePreferenceSaved { get; private set; } = true;

    private bool HomeNavigationBlocked
    {
        get
        {
            if (!Session.GameWindowClosing)
            {
                if (Session.Busy && !Session.IsLibraryScanning)
                {
                    return !Session.IsBackgroundMaintenance;
                }
                return false;
            }
            return true;
        }
    }

    public string LauncherLanguage => _homePreferences.Language;

    private GameLibraryEntry? SelectedGame
    {
        get
        {
            object obj;
            if (_selectedEntry != null)
            {
                obj = ((_section == "Library" && !_homeMode) ? _desktopGames.FirstOrDefault((GameLibraryEntry g) => g.EntryId == _selectedEntry) : null);
                if (obj == null)
                {
                    return Session.Library.Get(_selectedEntry);
                }
            }
            else
            {
                obj = Session.ActiveGame;
            }
            return (GameLibraryEntry?)obj;
        }
    }

    private NintendoLibraryMetadata Metadata { get; }

    internal Control? DesktopFlyoutContent
    {
        get
        {
            if (!_homeMode)
            {
                Flyout? openDesktopFlyout = _openDesktopFlyout;
                if (openDesktopFlyout != null && openDesktopFlyout.IsOpen)
                {
                    return _openDesktopFlyout.Content as Control;
                }
            }
            return null;
        }
    }

    public bool StartsInHome => _homePreferences.StartInHome;

    public ThemePreferences Appearance { get; private set; } = new ThemePreferences();

    internal HomeTheme HomePalette { get; private set; } = new HomeTheme();

    private string TranslationRuntime => Path.Combine(Directory.Exists(Path.Combine(Session.Paths.InstallRoot, "translation")) ? Session.Paths.InstallRoot : AppContext.BaseDirectory, "translation");

    private Task ShowAboutAsync()
    {
        return Dialogs.MessageAsync(this, "About NX Infinity", "NX Infinity " + InfinityVersion.Application + "\n\nA Ryujinx-derived core fork through Kenji-NX, with extensive repairs and performance improvements. An entirely remade Switch-themed Desktop and HOME interface, with design origins in Dune.\n\nDeveloper\nCOYE · coye2\n\nDev Team Testers\nIL DENTE PROIBITO\nShadowEddyx12\n\nCore and open-source credits\nRyujinx and Kenji-NX contributors — original emulator foundation\nsakasakiking — Dune desktop design\nAvalonia, SkiaSharp, SDL, OpenAL, FFmpeg, .NET and their contributors\n\nSource and releases\nhttps://github.com/coye2/NX-Infinity\n\nSupport COYE\nhttps://ko-fi.com/coye2\n\nDesktop and HOME are two views of the same emulator. Component licenses and source notices are included in the package.");
    }

    private long BeginArtworkRequest(Border target)
    {
        return _artworkRequests[target] = ++_artworkRequestSequence;
    }

    private bool CurrentArtworkRequest(Border target, long request)
    {
        return _artworkRequests.GetValueOrDefault(target) == request;
    }

    private void RefreshArtworkForDisplay(bool force = false)
    {
        int num = DesktopBackgroundDecodePlan.RequestedWidth(Bounds.Width, RenderScaling);
        if (num == _artworkWindowBucket && !force)
        {
            return;
        }
        if (_artworkWindowBucket == 0 && !force)
        {
            _artworkWindowBucket = num;
            return;
        }
        _artworkWindowBucket = num;
        if (_artworkResizeTimer == null)
        {
            _artworkResizeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(180L)
            };
        }
        _artworkResizeTimer.Stop();
        _artworkResizeTimer.Tick -= RefreshResizedArtwork;
        _artworkResizeTimer.Tick += RefreshResizedArtwork;
        _artworkResizeTimer.Start();
    }

    private void RefreshResizedArtwork(object? sender, EventArgs e)
    {
        _artworkResizeTimer?.Stop();
        if (!_closing && _section == "Library" && !Session.IsLaunching)
        {
            Func<Task>[] array = _artworkRefreshers.Values.ToArray();
            for (int i = 0; i < array.Length; i++)
            {
                array[i]();
            }
        }
    }

    private void TrackArtworkSize(Border target, Func<Task> refresh)
    {
        _artworkRefreshers[target] = refresh;
        target.DetachedFromVisualTree += (object? _, VisualTreeAttachmentEventArgs _) =>
        {
            _artworkRefreshers.Remove(target);
            _artworkRequests.Remove(target);
        };
    }

    private void ResetArtworkPage()
    {
        _artworkPageLifetime.Cancel();
        _artworkPageLifetime.Dispose();
        _artworkPageLifetime = new CancellationTokenSource();
        _artworkLibrary = null;
        _artworkRefreshers.Clear();
        _artworkRequests.Clear();
    }

    private async Task PopulateArtworkAsync(Border target, GameLibraryEntry entry, double size, double? height = null)
    {
        long request = BeginArtworkRequest(target);
        CancellationTokenSource life = CancellationTokenSource.CreateLinkedTokenSource(_artworkPageLifetime.Token);
        try
        {
            target.DetachedFromVisualTree += (object? _, VisualTreeAttachmentEventArgs _) =>
            {
                try
                {
                    life.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            };
            CancellationToken ct = life.Token;
            try
            {
                if (_artworkLibrary == null)
                {
                    _artworkLibrary = Session.Library.List(new LibraryQuery(null, FavoritesOnly: false, null, LibrarySort.Name, 0, 2000));
                }
                IReadOnlyList<LocalArtwork.Source> readOnlyList = LocalArtwork.Sources(entry, CatalogSelection(entry), _artworkLibrary);
                foreach (LocalArtwork.Source source in readOnlyList)
                {
                    ct.ThrowIfCancellationRequested();
                    ArtworkDecodePlan plan = ArtworkDecodePlan.ForViewport(source.Artwork.Width, source.Artwork.Height, size, height ?? size, RenderScaling, 3840);
                    string key = source.Artwork.Sha256 + ":" + plan.CacheKey;
                    LeasedResourceCache<Bitmap>.Lease lease = _artworkCache.TryAcquire(key);
                    if (lease == null)
                    {
                        await _artworkDecoders.WaitAsync(ct);
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            lease = _artworkCache.TryAcquire(key);
                            if (lease == null)
                            {
                                Bitmap decoded = await Task.Run(() =>
                                {
                                    ct.ThrowIfCancellationRequested();
                                    byte[] array = LocalArtwork.Read(source, Session.Library, Metadata);
                                    if (array == null)
                                    {
                                        return (Bitmap)null;
                                    }
                                    using MemoryStream source2 = new MemoryStream(array);
                                    return plan.Decode(source2);
                                }, ct);
                                if (decoded == null)
                                {
                                    continue;
                                }
                                try
                                {
                                    ct.ThrowIfCancellationRequested();
                                    bool consumed = false;
                                    try
                                    {
                                        lease = _artworkCache.Acquire(key, plan.MaximumBytes, () =>
                                        {
                                            consumed = true;
                                            return (Value: decoded, Bytes: (long)decoded.PixelSize.Width * (long)decoded.PixelSize.Height * 4);
                                        });
                                    }
                                    finally
                                    {
                                        if (consumed)
                                        {
                                            decoded = null;
                                        }
                                    }
                                }
                                finally
                                {
                                    decoded?.Dispose();
                                }
                            }
                        }
                        catch (Exception error) when (ArtworkFailure(error))
                        {
                            continue;
                        }
                        finally
                        {
                            _artworkDecoders.Release();
                        }
                    }
                    if (lease == null)
                    {
                        continue;
                    }
                    if (ct.IsCancellationRequested || _closing || !CurrentArtworkRequest(target, request))
                    {
                        lease.Dispose();
                        return;
                    }
                    _pageImages.Add(lease);
                    Image image = new Image
                    {
                        Source = lease.Value,
                        Stretch = Stretch.UniformToFill
                    };
                    image.DetachedFromVisualTree += (object? _, VisualTreeAttachmentEventArgs _) =>
                    {
                        lease.Dispose();
                        _pageImages.Remove(lease);
                    };
                    if (size >= 800.0 && (double)source.Artwork.Width < size * RenderScaling / 2.0)
                    {
                        image.Stretch = Stretch.Uniform;
                        image.MaxWidth = (double)source.Artwork.Width / RenderScaling;
                        image.MaxHeight = (double)source.Artwork.Height / RenderScaling;
                        image.HorizontalAlignment = HorizontalAlignment.Center;
                        image.VerticalAlignment = VerticalAlignment.Center;
                    }
                    RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                    target.Child = image;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error2) when (ArtworkFailure(error2))
            {
            }
        }
        finally
        {
            if (life != null)
            {
                ((IDisposable)life).Dispose();
            }
        }
    }

    private static bool ArtworkFailure(Exception error)
    {
        if (error is ArgumentException || error is InvalidOperationException || error is LibraryMetadataException || error is ServiceException || error is IOException || error is UnauthorizedAccessException || error is NotSupportedException)
        {
            return true;
        }
        return false;
    }

    private void InitializeAvatars()
    {
        Avatars = new AvatarStore(Session.Paths.DataRoot);
        ReloadAvatar();
        Avatars.Changed += ReloadAvatar;
    }

    private void ReloadAvatar()
    {
        Bitmap avatarBitmap = _avatarBitmap;
        _avatarBitmap = null;
        byte[] array = Avatars.ReadImage();
        if (array != null)
        {
            try
            {
                _avatarBitmap = new Bitmap(new MemoryStream(AvatarStore.ReadCachedImage(array)));
            }
            catch (Exception ex) when ((ex is ArgumentException || ex is InvalidDataException) ? true : false)
            {
            }
        }
        if (_desktopAvatar != null)
        {
            SetAvatarContent(_desktopAvatar, 52.0);
        }
        if (_homeAvatar != null)
        {
            SetAvatarContent(_homeAvatar, _homeAvatar.Width - 8.0);
        }
        avatarBitmap?.Dispose();
    }

    private void SetAvatarContent(Button button, double size)
    {
        Image image = ((_avatarBitmap == null) ? null : new Image
        {
            Source = _avatarBitmap,
            Stretch = Stretch.Uniform,
            Name = "SelectedAvatarImage"
        });
        if (image != null)
        {
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        }
        button.Content = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2.0),
            ClipToBounds = true,
            Background = (_homeMode ? HomePalette.Canvas : _dune.Hover),
            Child = ((image != null) ? ((Control)image) : ((Control)new TextBlock
            {
                Text = StringInfo.GetNextTextElement(Avatars.Active.Name).ToUpperInvariant(),
                FontSize = size * 0.5,
                Foreground = (_homeMode ? HomePalette.Ink : _dune.Ink),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }))
        };
        string value = "Avatar · " + Avatars.Active.Name;
        ToolTip.SetTip(button, value);
        AutomationProperties.SetName(button, value);
    }

    private Button BuildAvatarButton(bool home, double scale = 1.0)
    {
        Button button = (home ? HomeButton("HomeAvatar", "Avatar", () => AvatarDialog.ShowAsync(this, Avatars), "top") : DuneIcon("user", "Avatar", () => AvatarDialog.ShowAsync(this, Avatars), "DesktopAvatar"));
        double num = (home ? Math.Clamp(64.0 * scale, 48.0, 88.0) : 52.0);
        button.CornerRadius = new CornerRadius((num + 8.0) / 2.0);
        button.Padding = new Thickness(4.0);
        double num2 = (button.MinHeight = num + 8.0);
        double width = (button.Height = num2);
        button.Width = width;
        if (home)
        {
            _homeAvatar = button;
        }
        else
        {
            _desktopAvatar = button;
        }
        SetAvatarContent(button, num);
        return button;
    }

    private Control BuildBootOverlay()
    {
        _bootTitle.Name = "BootGameTitle";
        _bootTitle.TextWrapping = TextWrapping.NoWrap;
        _bootTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _bootStage.Name = "BootStage";
        AutomationProperties.SetName(_bootProgress, "Game boot in progress");
        _bootCancel = VisualTheme.LocalizedButton("Cancel launch", (Action)Session.Cancel, (string?)null);
        _bootCancel.Name = "BootCancel";
        _bootCancel.HorizontalAlignment = HorizontalAlignment.Left;
        _bootProgress.Foreground = VisualTheme.Blue;
        _bootCard.Child = new StackPanel
        {
            Spacing = 18.0,
            Children =
            {
                (Control)_bootHeading,
                (Control)_bootTitle,
                (Control)_bootStage,
                (Control)_bootProgress,
                (Control)_bootCancel
            }
        };
        _bootOverlay.Child = _bootCard;
        return _bootOverlay;
    }

    private void UpdateBootOverlay()
    {
        bool isLaunching = Session.IsLaunching;
        bool flag = _bootOverlay.IsVisible != isLaunching;
        if (isLaunching & flag)
        {
            _bootPreviousFocus = FocusManager?.GetFocusedElement() as Control;
        }
        _bootOverlay.IsVisible = isLaunching;
        _bootProgress.IsIndeterminate = isLaunching;
        int num;
        if (!isLaunching)
        {
            StartupSequence? startupSequence = _startupSequence;
            num = ((startupSequence == null || !startupSequence.IsVisible) ? 1 : 0);
        }
        else
        {
            num = 0;
        }
        bool isEnabled = (byte)num != 0;
        if (_desktopSurface != null)
        {
            _desktopSurface.IsEnabled = isEnabled;
        }
        _homeSurface.IsEnabled = isEnabled;
        _bootTitle.Text = Session.LaunchGameName ?? UiText.Get("Starting game");
        _bootStage.Text = UiText.Get(Session.Status);
        _bootCard.Background = (_homeMode ? HomePalette.Surface : _dune.Surface);
        _bootCard.BorderBrush = (_homeMode ? HomePalette.Line : _dune.Line);
        _bootTitle.Foreground = (_homeMode ? HomePalette.Ink : _dune.Ink);
        _bootStage.Foreground = (_homeMode ? HomePalette.Muted : _dune.Muted);
        _bootHeading.Foreground = (_homeMode ? HomePalette.Focus : Brush.Parse(ThemePresets.FocusColor(_dune.Palette)));
        _bootProgress.Foreground = _bootHeading.Foreground;
        _bootProgress.Background = (_homeMode ? HomePalette.Soft : _dune.Hover);
        if (_bootCancel != null)
        {
            _bootCancel.Background = (_homeMode ? HomePalette.Soft : _dune.Hover);
            _bootCancel.Foreground = (_homeMode ? HomePalette.Ink : _dune.Ink);
            _bootCancel.BorderBrush = _bootHeading.Foreground;
        }
        if (!flag || Navigation.CurrentWindow != this)
        {
            return;
        }
        if (isLaunching)
        {
            Navigation.Focus(_bootCancel);
            return;
        }
        Control bootPreviousFocus = _bootPreviousFocus;
        if (bootPreviousFocus != null && bootPreviousFocus.IsEffectivelyVisible && bootPreviousFocus.IsEffectivelyEnabled)
        {
            Navigation.Focus(bootPreviousFocus);
        }
        _bootPreviousFocus = null;
    }

    private Control BuildCompatibilityPage()
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 17.0
        };
        stackPanel.Children.Add(Heading(UiText.Get("Compatibility"), UiText.Get("Graphics, stability, and controller reports with recorded evidence")));
        Button button = VisualTheme.LocalizedButton("Game improvements", () =>
        {
            _compatibilityView = false;
            RenderCurrentPage();
        });
        button.Name = "CompatibilityBackToProfiles";
        Button button2 = VisualTheme.LocalizedButton("Read active graphics device", () => MainActionAsync(Session.RefreshGraphicsContextAsync));
        button2.Name = "CompatibilityReadDevice";
        button2.IsEnabled = (object)Session.SelectedBackend != null;
        Button button3 = VisualTheme.LocalizedButton("Export local report", (Func<Task>)ExportPerformanceAsync, (string?)null);
        button3.Name = "CompatibilityExport";
        stackPanel.Children.Add(Actions(button, button2, button3));
        GraphicsDeviceSnapshot graphicsDeviceSnapshot = Session.GraphicsDevice?.Snapshot;
        BackendInfo backend = Session.ActiveBackend;
        string text = graphicsDeviceSnapshot?.DeviceName ?? "Active GPU not yet observed";
        object obj = graphicsDeviceSnapshot?.RendererVersionLabel;
        if (obj == null)
        {
            obj = graphicsDeviceSnapshot?.DriverVersion;
            if (obj == null)
            {
                obj = graphicsDeviceSnapshot?.DriverInfo;
                if (obj == null)
                {
                    uint? num = graphicsDeviceSnapshot?.DriverVersionRaw;
                    obj = ((!num.HasValue) ? "Driver identity unavailable" : ("Native driver version 0x" + num.GetValueOrDefault().ToString("x8")));
                }
            }
        }
        string text2 = (string)obj;
        StackPanel stackPanel2 = VisualTheme.Column(VisualTheme.Text(text, 20.0, null, FontWeight.DemiBold), VisualTheme.Text(text2, 14.0, VisualTheme.Muted), VisualTheme.Text((backend?.BackendName ?? Session.SelectedBackend?.Name ?? UiText.Get("No core selected")) + " · " + (((object)graphicsDeviceSnapshot != null && graphicsDeviceSnapshot.SelectedDeviceVerified && graphicsDeviceSnapshot.DriverIdentityKey != null) ? UiText.Get("Selected-device identity recorded") : UiText.Get("Exact hardware binding unavailable")), 13.0, VisualTheme.Blue));
        stackPanel2.Children.Add(Actions(VisualTheme.LocalizedButton("Identity details", () =>
        {
            MainWindow owner = this;
            string title = UiText.Get("Observed graphics identity");
            string backendId = backend?.BackendId;
            string backendRevision = backend?.BackendRevision;
            string adapterVersion = backend?.AdapterVersion;
            ContentInfo loadedIdentity2 = Session.LoadedIdentity;
            return Dialogs.MessageAsync(owner, title, JsonSerializer.Serialize(new
            {
                backendId = backendId,
                backendRevision = backendRevision,
                adapterVersion = adapterVersion,
                game = (((object)loadedIdentity2 != null) ? new { loadedIdentity2.TitleId, loadedIdentity2.BuildId, loadedIdentity2.DisplayVersion } : null),
                graphics = Session.GraphicsDevice
            }, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }), VisualTheme.LocalizedButton("Capture guide", () => Dialogs.MessageAsync(this, UiText.Get("Reproduce a rendering issue"), "1. Use the same core, full game build, driver, update/DLC and mod selection for each comparison. Read active graphics device to record the selected renderer. AMD Z1 Extreme and RTX 4060 Ti need separate results.\n\n2. Reproduce the same scene. For Smash's white glow, the primary report names Mario Bros. and Peach's Castle; for the separate flicker/stutter report, repeat the same eight-fighter match on Big Battlefield. For Scarlet/Violet missing buildings, use the same city location and camera sweep, including Mesagoza. Record foliage while moving the camera, leaving the area and returning.\n\n3. Start a new capture from Performance, play the route, then open the menu and choose Record current issue on the matching report. This records a fresh native observation boundary; it does not identify the exact broken draw. Export the local report and keep a matching motion recording.\n\n4. Compare original behavior with one explicit candidate at a time. Keep existing caches; use distinct clean-test and warm-test profiles when a cold-cache comparison is needed. Repeat a warm run after restarting the app. Check real game speed against wall time, animation, physics, input, audio, saves, loading and sustained play.\n\n5. Candidate AMD logic-op work remains unverified on your physical driver until tested. Source or authored-fixture success alone cannot establish a Smash or Pokémon fix. Restore the recorded setting or original core package after the comparison. Do not combine a rendering test with FPS, population, difficulty or spawn changes."))));
        stackPanel.Children.Add(Card(stackPanel2, accent: true));
        string text3 = InfinitySession.TitleOf(SelectedGame);
        Button button4 = VisualTheme.Button((_compatibilityAllTitles || text3 == null) ? UiText.Get("Showing all games") : UiText.Get("Showing selected game"), () =>
        {
            _compatibilityAllTitles = !_compatibilityAllTitles;
            RenderCurrentPage();
        });
        button4.Name = "CompatibilityGameFilter";
        button4.IsEnabled = text3 != null;
        Button button5 = VisualTheme.Button(_compatibilityThisCore ? UiText.Get("Showing selected core") : UiText.Get("Showing all reported cores"), () =>
        {
            _compatibilityThisCore = !_compatibilityThisCore;
            RenderCurrentPage();
        });
        button5.Name = "CompatibilityCoreFilter";
        button5.IsEnabled = (object)Session.SelectedBackend != null;
        stackPanel.Children.Add(Actions(button4, button5));
        string text4 = (_compatibilityAllTitles ? null : text3);
        ContentInfo loadedIdentity = Session.LoadedIdentity;
        IReadOnlyList<CompatibilityAssessment> readOnlyList = CompatibilityCatalog.Query(new CompatibilityQuery
        {
            TitleId = text4,
            BuildId = (((object)loadedIdentity != null && loadedIdentity.TitleId == text4) ? loadedIdentity.BuildId : null),
            IdentityVerified = (loadedIdentity?.TitleId == text4 && (loadedIdentity?.IdentityVerified ?? false)),
            BackendId = ((!_compatibilityThisCore) ? null : Session.SelectedBackend?.Id),
            BackendRevision = backend?.BackendRevision,
            AdapterVersion = backend?.AdapterVersion,
            Platform = (OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsLinux() ? "linux" : "other")),
            GpuVendorId = graphicsDeviceSnapshot?.VendorIdHex,
            GpuDeviceId = graphicsDeviceSnapshot?.DeviceIdHex,
            DriverVersion = graphicsDeviceSnapshot?.DriverIdentityKey,
            HardwareVerified = ((object)graphicsDeviceSnapshot != null && graphicsDeviceSnapshot.SelectedDeviceVerified && graphicsDeviceSnapshot.DriverIdentityKey != null)
        });
        stackPanel.Children.Add(VisualTheme.LocalizedText("Reviewed " + CompatibilityCatalog.CheckedUtc.ToString("yyyy-MM-dd") + " · " + readOnlyList.Count + UiText.Get(" related report(s). ") + UiText.Get("A report supplies a reproduction lead; a fix needs its own exact-build and hardware validation."), 13.0, VisualTheme.Muted));
        if (readOnlyList.Count == 0)
        {
            stackPanel.Children.Add(Card(VisualTheme.LocalizedText("No matching observation is in this catalogue. Choose all reported cores or all games to broaden the review. This does not establish that the current game is unaffected.")));
        }
        using (IEnumerator<CompatibilityAssessment> enumerator = readOnlyList.GetEnumerator())
        {
            Button button7;
            int isEnabled;
            StackPanel stackPanel3;
            Button button6;
            for (; enumerator.MoveNext(); button7.IsEnabled = (byte)isEnabled != 0, stackPanel3.Children.Add(Actions(button6, button7)), stackPanel.Children.Add(Card(stackPanel3)))
            {
                CompatibilityAssessment assessment = enumerator.Current;
                CompatibilityIssue issue = assessment.Issue;
                stackPanel3 = new StackPanel
                {
                    Spacing = 11.0
                };
                stackPanel3.Children.Add(VisualTheme.Text(issue.Title, 19.0, null, FontWeight.DemiBold));
                stackPanel3.Children.Add(VisualTheme.Text(string.Join(" · ", issue.GameNames) + " · " + ((issue.BackendIds.Count == 0) ? UiText.Get("Core unconfirmed") : string.Join(", ", issue.BackendIds)), 13.0, VisualTheme.Blue));
                stackPanel3.Children.Add(VisualTheme.Text(string.Join("; ", issue.Symptoms), 14.0));
                stackPanel3.Children.Add(VisualTheme.Text(issue.ReportedEnvironment, 13.0, VisualTheme.Muted));
                string text5;
                if (issue.Status == "closed_report_not_qualified")
                {
                    text5 = "Closed upstream report · NX qualification pending";
                }
                else
                {
                    text5 = ((issue.Status == "user_report_unconfirmed") ? "Investigation · reproduction needed" : "Reported issue · fix unverified");
                }
                stackPanel3.Children.Add(VisualTheme.Text(text5, 13.0, VisualTheme.Muted));
                button6 = VisualTheme.LocalizedButton("Evidence and validation", () => Dialogs.MessageAsync(this, issue.Title, CompatibilityDetails(assessment)));
                button6.Name = "CompatibilityEvidence";
                button7 = VisualTheme.LocalizedButton("Record current issue", () => MainActionAsync(() => Session.MarkGraphicsIssueAsync(issue.Id)));
                button7.Name = "CompatibilityMark";
                if (Session.HasGame)
                {
                    string text6 = loadedIdentity?.TitleId;
                    if (text6 != null)
                    {
                        isEnabled = ((issue.TitleIds.Count == 0 || issue.TitleIds.Contains(text6, StringComparer.OrdinalIgnoreCase)) ? 1 : 0);
                        continue;
                    }
                }
                isEnabled = 0;
            }
        }
        stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("Source inspections", 19.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("The pinned cores differ in their asynchronous shader and resource-flush paths. Source presence is recorded separately from a reproduced game fix.", 14.0, VisualTheme.Muted), VisualTheme.LocalizedButton("Inspect exact source findings", () => Dialogs.MessageAsync(this, UiText.Get("Pinned source observations"), string.Join("\n\n", CompatibilityCatalog.SourceAudits.Select((CompatibilitySourceAudit a) => a.BackendId + " · " + a.BackendRevision + "\n" + a.Summary + "\n" + string.Join("\n", a.SourceUrls))))))));
        if (Session.GraphicsIssueObservations.Count != 0)
        {
            stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.Text(Session.GraphicsIssueObservations.Count + UiText.Get(" local issue observation(s)"), 19.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("Export includes their title/build, core, graphics identity and native capture boundary. Add the matching scene recording when you choose to share a report.", 14.0, VisualTheme.Muted), VisualTheme.LocalizedButton("Observation details", () => Dialogs.MessageAsync(this, UiText.Get("Local graphics observations"), JsonSerializer.Serialize(Session.GraphicsIssueObservations, new JsonSerializerOptions
            {
                WriteIndented = true
            }))))));
        }
        stackPanel.Children.Add(VisualTheme.Text("Curated user-priority and high-impact reports reviewed on 2026-10-19; not a statistical ranking or a complete compatibility list. No listed workaround is a qualified NX game fix. No matching report means this catalogue has no matching observation, not that a title or core is unaffected." + UiText.Get("\nReports stay local until you export and share them. No community upload runs automatically."), 12.0, VisualTheme.Muted));
        return PageScroll(stackPanel);
    }

    private static string CompatibilityDetails(CompatibilityAssessment assessment)
    {
        CompatibilityIssue issue = assessment.Issue;
        return issue.Title + "\n\nReported environment\n" + issue.ReportedEnvironment + "\n\nEvidence\n" + string.Join("\n\n", issue.Evidence.Select((CompatibilityEvidence e) => e.Url + "\n" + e.SourceKind + " · " + e.ReportState + "\n" + e.Summary)) + "\n\nCurrent relevance\n" + string.Join("\n", assessment.ContextNotes) + "\n\nInvestigation actions\n" + string.Join("\n\n", assessment.ActionAssessments.Select((CompatibilityActionAssessment a) => a.Action.Label + "\n" + a.Action.Explanation + "\nStatus: " + a.Status + "\n" + string.Join("\n", a.BlockingReasons))) + "\n\nValidation needed\n" + string.Join("\n", issue.ValidationNeeded) + "\n\nOriginal settings and existing caches remain available. Nothing on this page applies a graphics patch.";
    }

    private static string MappingDevice(HostControllerSnapshot sample)
    {
        return sample.MappingGuid ?? $"{sample.VendorId:X4}:{sample.ProductId:X4}:{sample.Name}";
    }

    private InputSnapshot MapControllerInput(HostControllerSnapshot sample)
    {
        return MappingStore.Read(Session.SelectedBackendId ?? "default", Session.ActiveGame?.EntryId, MappingDevice(sample)).Apply(sample);
    }

    private async Task ConfigureControllerMappingAsync()
    {
        if (Session.HasGame)
        {
            await Session.PauseAsync();
        }
        HostControllerSnapshot device = _hostDevices.FirstOrDefault((HostControllerSnapshot item) => item.DeviceId == ControllerAssignment.State.DeviceId);
        (ControllerAssignmentState state, int player)[] assigned = (from item in GameplayControllerAssignments.Select((ControllerAssignmentState state, int player) => (state: state, player: player))
                                                                    where item.state.DeviceId != null
                                                                    select item).ToArray();
        if (assigned.Length == 1)
        {
            device = _hostDevices.FirstOrDefault((HostControllerSnapshot item) => item.DeviceId == assigned[0].state.DeviceId);
        }
        if (assigned.Length > 1)
        {
            string choice = await Dialogs.ChooseAsync(this, "Controller to configure", assigned.Select(((ControllerAssignmentState state, int player) item) => (item.state.DeviceId, $"Player {item.player + 1} · {item.state.PreferredName}")).ToArray());
            if (choice == null)
            {
                return;
            }
            device = _hostDevices.FirstOrDefault((HostControllerSnapshot item) => item.DeviceId == choice);
        }
        if ((object)device == null)
        {
            await ChooseGameControllerAsync();
            device = _hostDevices.FirstOrDefault((HostControllerSnapshot item) => item.DeviceId == ControllerAssignment.State.DeviceId);
        }
        if ((object)device == null)
        {
            return;
        }
        string core = Session.SelectedBackendId ?? "default";
        string identity = MappingDevice(device);
        MainWindow owner = this;
        IReadOnlyList<(string, string)> options;
        if ((object)SelectedGame != null)
        {
            IReadOnlyList<(string, string)> readOnlyList =
            [
                ("game", "This game"),
                ("default", "All games on this core")
            ];
            options = readOnlyList;
        }
        else
        {
            IReadOnlyList<(string, string)> readOnlyList = [("default", "All games on this core")];
            options = readOnlyList;
        }
        string text = await Dialogs.ChooseAsync(owner, "Controller profile", options);
        if (text == null)
        {
            return;
        }
        string game = ((!(text == "game")) ? null : SelectedGame?.EntryId);
        ControllerMapping controllerMapping = MappingStore.Read(core, game, identity);
        ControllerMapping draft = controllerMapping with
        {
            Buttons = new Dictionary<string, string>(controllerMapping.Buttons, StringComparer.Ordinal)
        };
        Window dialog = Dialogs.Create("Controller mapping", 800.0, 650.0);
        dialog.Name = "ControllerMapping";
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 12.0,
            Margin = new Thickness(24.0)
        };
        stackPanel.Children.Add(VisualTheme.Text(device.Name, 22.0));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Select a Switch control, then press the physical button. Guide remains reserved for HOME.", 13.0, VisualTheme.Muted));
        WrapPanel wrapPanel = new WrapPanel();
        string[] array = draft.Buttons.Keys.ToArray();
        foreach (string control in array)
        {
            Button button = null;
            button = VisualTheme.Button(control.ToUpperInvariant().Replace('_', ' ') + " · " + draft.Buttons[control], async () =>
            {
                string text2 = await CaptureControllerBindingAsync(dialog, device.DeviceId, control);
                if (text2 != null)
                {
                    draft.Buttons[control] = text2;
                    button.Content = control.ToUpperInvariant().Replace('_', ' ') + " · " + text2;
                }
            });
            button.Width = 220.0;
            button.Margin = new Thickness(0.0, 0.0, 8.0, 8.0);
            wrapPanel.Children.Add(button);
            button.Name = "MapControl_" + control;
        }
        stackPanel.Children.Add(wrapPanel);
        Slider deadZone = new Slider
        {
            Minimum = 0.0,
            Maximum = 0.5,
            Value = draft.DeadZone,
            TickFrequency = 0.01,
            IsSnapToTickEnabled = true
        };
        Slider trigger = new Slider
        {
            Minimum = 0.1,
            Maximum = 0.95,
            Value = draft.TriggerThreshold,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true
        };
        stackPanel.Children.Add(VisualTheme.LocalizedText("Stick dead zone", 14.0));
        stackPanel.Children.Add(deadZone);
        stackPanel.Children.Add(VisualTheme.LocalizedText("Trigger threshold", 14.0));
        stackPanel.Children.Add(trigger);
        TextBlock calibration = VisualTheme.LocalizedText("Release both sticks, then calibrate their center.", 13.0, VisualTheme.Muted);
        stackPanel.Children.Add(calibration);
        stackPanel.Children.Add(VisualTheme.LocalizedButton("Calibrate stick centers", () =>
        {
            HostControllerSnapshot hostControllerSnapshot = _hostDevices.FirstOrDefault((HostControllerSnapshot sample) => sample.DeviceId == device.DeviceId);
            if ((object)hostControllerSnapshot == null)
            {
                calibration.Text = "Controller disconnected. Reconnect and try again.";
            }
            else if (new double[4] { hostControllerSnapshot.LeftX, hostControllerSnapshot.LeftY, hostControllerSnapshot.RightX, hostControllerSnapshot.RightY }.Any((double value) => Math.Abs(value) > 0.25))
            {
                calibration.Text = "Release the sticks before calibrating.";
            }
            else
            {
                draft = draft with
                {
                    LeftCenterX = hostControllerSnapshot.LeftX,
                    LeftCenterY = hostControllerSnapshot.LeftY,
                    RightCenterX = hostControllerSnapshot.RightX,
                    RightCenterY = hostControllerSnapshot.RightY
                };
                calibration.Text = "Centers captured. Save to apply.";
            }
        }));
        stackPanel.Children.Add(Actions(VisualTheme.LocalizedButton("Cancel", () =>
        {
            dialog.Close();
        }), VisualTheme.LocalizedButton("Save profile", () =>
        {
            MappingStore.Save(core, game, identity, draft with
            {
                DeadZone = deadZone.Value,
                TriggerThreshold = trigger.Value
            });
            dialog.Close();
        }, "primary"), VisualTheme.LocalizedButton("Restore defaults", () =>
        {
            MappingStore.Save(core, game, identity, ControllerMapping.Default());
            dialog.Close();
        })));
        dialog.Content = PageScroll(stackPanel);
        await Dialogs.ShowAsync<object>(this, dialog);
    }

    private async Task<string?> CaptureControllerBindingAsync(Window owner, string deviceId, string control)
    {
        Window dialog = Dialogs.Create("Map " + control, 560.0, 380.0);
        dialog.Name = "ControllerBindingCapture";
        TextBlock text = VisualTheme.LocalizedText("Release all buttons, then press the button or trigger to bind.");
        dialog.Content = new Border
        {
            Padding = new Thickness(24.0),
            Child = VisualTheme.Column(text, VisualTheme.LocalizedButton("Cancel", () =>
            {
                dialog.Close();
            }))
        };
        bool armed = false;
        _mappingCapture = (IReadOnlyList<HostControllerSnapshot> samples) =>
        {
            HostControllerSnapshot hostControllerSnapshot = samples.FirstOrDefault((HostControllerSnapshot item) => item.DeviceId == deviceId);
            if ((object)hostControllerSnapshot == null)
            {
                text.Text = "Controller disconnected. Reconnect or cancel.";
                armed = false;
            }
            else if (!armed)
            {
                armed = hostControllerSnapshot.IsNeutral;
            }
            else
            {
                List<string> list = (from button in hostControllerSnapshot.Buttons
                                     where button != HostButton.Guide
                                     select button.ToString()).ToList();
                if (hostControllerSnapshot.LeftTrigger > 0.5)
                {
                    list.Add("LeftTrigger");
                }
                if (hostControllerSnapshot.RightTrigger > 0.5)
                {
                    list.Add("RightTrigger");
                }
                if (list.Count == 1)
                {
                    dialog.Close(list[0]);
                }
            }
        };
        try
        {
            return await Dialogs.ShowAsync<string>(owner, dialog);
        }
        finally
        {
            _mappingCapture = null;
            _mappingNeedsNeutral = true;
        }
    }

    public void ObserveControllerDevices(IReadOnlyList<HostControllerSnapshot> samples, IReadOnlyList<HostControllerPress>? presses = null)
    {
        _hostDevices = samples;
        ControllerRoster.Observe(samples, presses ?? Array.Empty<HostControllerPress>());
        _controllerNavigation.GameDeviceId = ControllerAssignment.State.DeviceId;
        RefreshControllerStatus();
    }

    public TextBlock CreateControllerStatusReadout()
    {
        _controllerReadout = VisualTheme.Text(ControllerText(), 14.0, VisualTheme.Muted);
        _controllerReadout.Name = "ControllerStatus";
        return _controllerReadout;
    }

    private void RefreshControllerStatus()
    {
        if (_controllerReadout != null)
        {
            _controllerReadout.Text = ControllerText();
        }
    }

    private string ControllerText()
    {
        ControllerAssignmentState state = ControllerAssignment.State;
        string text = ((state.ConnectedCount == 0) ? "No gamepad detected." : (state.ConnectedCount + " connected gamepad(s)."));
        string text2 = state.Phase switch
        {
            ControllerAssignmentPhase.Assigned => "Player 1 selected: " + state.PreferredName + ". Preference saved.",
            ControllerAssignmentPhase.Disconnected => "Saved preference: " + state.PreferredName + ". Disconnected; confirm the controller after reconnecting.",
            ControllerAssignmentPhase.ConfirmConnection => "Saved preference: " + state.PreferredName + ". Open Controls to confirm this connection.",
            ControllerAssignmentPhase.Ambiguous => "Saved preference: " + state.PreferredName + ". Multiple matching names; open Controls and choose the intended connection.",
            ControllerAssignmentPhase.PreferenceUnreadable => "Saved preference could not be read. Open Controls to make a new selection.",
            _ => "Player 1: native keyboard where supported; no frontend gamepad assigned.",
        };
        string text3;
        if (state.Phase != ControllerAssignmentPhase.Assigned)
        {
            text3 = "Gameplay gamepad forwarding: inactive.";
        }
        else if (!Session.GuestOwnsInput || Session.Busy)
        {
            text3 = "Gameplay gamepad forwarding: waiting for Resume / Play; menu input stays in NX Infinity.";
        }
        else
        {
            text3 = ((!_controllerNavigation.GuestReady) ? "Gameplay gamepad forwarding: release all buttons and sticks to enable." : "Gameplay gamepad forwarding: enabled for the selected connection. Assignment alone does not verify guest response.");
        }
        string text4 = string.Join("\n", ControllerRoster.Players.Skip(1).Select((ControllerAssignment player, int index) => $"Player {index + 2}: " + ((player.State.DeviceId == null) ? "press a button to join" : player.State.PreferredName)));
        return text + "\nPress a button on each controller to join in order, Players 1–4.\n" + text2 + "\n" + text4 + "\n" + text3;
    }

    public MainWindow(InfinitySession session)
        : this(session, null)
    {
    }

    internal MainWindow(InfinitySession session, HttpMessageHandler? metadataHandler)
    {
        _homePreferenceStore = new HomePreferenceStore(session.Paths.DataRoot);
        _homePreferences = _homePreferenceStore.Read();
        UiText.SetLanguage(_homePreferences.Language);
        Session = session;
        Title = "NX Infinity";
        Width = 1280.0;
        Height = 820.0;
        MinWidth = 740.0;
        MinHeight = 560.0;
        _desktopPreferenceStore = new DesktopPreferenceStore(session.Paths.DataRoot);
        _desktopPreferences = _desktopPreferenceStore.Read();
        InitializeThemes();
        InitializeTranslation();
        InitializePresence();
        _favorites = _desktopPreferences.FavoritesOnly;
        _sort = _desktopPreferences.Sort;
        _selectedEntry = _desktopPreferences.SelectedEntryId;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = VisualTheme.Canvas;
        Metadata = new NintendoLibraryMetadata(Path.Combine(Session.Paths.DataRoot, "frontend-metadata", "nintendo"), metadataHandler);
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NxInfinity/Assets/nx-infinity.ico")));
        _controllerNavigation = new ControllerNavigation(this);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _backend = VisualTheme.LocalizedButton("NX Infinity Core", (Func<Task>)ChooseBackendAsync, (string?)null);
        _backend.Name = "BackendSelector";
        _error = VisualTheme.LocalizedButton("Last issue", (Func<Task>)ShowLastFailureAsync, (string?)null);
        _error.Name = "LastIssue";
        _error.IsVisible = false;
        _error.Classes.Add("danger");
        _cancel = VisualTheme.LocalizedButton("Cancel", (Action)Session.Cancel, (string?)null);
        _cancel.IsVisible = false;
        InitializeAvatars();
        Content = InitializeHomeSurface(BuildShell());
        KeyDown += MainKeyDown;
        Session.Changed += SessionChanged;
        Session.MenuRequested += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_closing)
                {
                    if (_homeMode)
                    {
                        _homeSelectedEntry = Session.ActiveGame?.EntryId ?? _homeSelectedEntry;
                        _homeDetails = false;
                        _section = "Library";
                    }
                    Show();
                    Activate();
                    RenderCurrentPage();
                    UpdateLayout();
                    if (_homeMode)
                    {
                        FocusHomeSelection();
                    }
                    else
                    {
                        Navigation.FocusFirst(this);
                    }
                }
            });
        };
        Session.GameClosed += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_closing)
                {
                    _selectedEntry = null;
                    _section = "Library";
                    _homeDetails = false;
                    _navigationGeneration++;
                    Show();
                    Activate();
                    RenderCurrentPage();
                    UpdateLayout();
                    if (_homeMode)
                    {
                        FocusHomeSelection();
                    }
                    else
                    {
                        Navigation.FocusFirst(this);
                    }
                }
            });
        };
        Session.InteractionHandler = async (InteractionRequest request, CancellationToken ct) => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Dialogs.InteractionAsync(this, request, ct));
        Session.InteractionStreamHandler = async (InteractionConversation conversation, CancellationToken ct) =>
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Dialogs.InteractionStreamAsync(this, conversation, ct));
        };
        Activated += async (object? _, EventArgs _) =>
        {
            if (!Session.Busy && Session.State == RuntimeState.Running && Session.GuestOwnsInput)
            {
                await Session.PauseAsync();
            }
        };
        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1L)
        };
        _telemetryTimer.Tick += async (object? _, EventArgs _) =>
        {
            _clock.Text = DateTime.Now.ToString("h:mm tt");
            UpdateHomeReadouts();
            if (ValidationRunner.IsRequested(Program.Arguments) || _pollingTelemetry)
            {
                return;
            }
            _pollingTelemetry = true;
            try
            {
                await Session.PollTelemetryAsync();
            }
            finally
            {
                _pollingTelemetry = false;
            }
        };
        _inputTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16L)
        };
        _inputTimer.Tick += async (object? _, EventArgs _) =>
        {
            await PollControllersAsync();
        };
        Opened += async (object? _, EventArgs _) =>
        {
            OpenHomeAtStartup();
            if (_homeMode && ShowStartupSequence && _homePreferences.StartupAnimation && !ValidationRunner.IsRequested(Program.Arguments))
            {
                BeginStartupSequence();
            }
            Navigation.CurrentWindow = this;
            Navigation.FocusFirst(this);
            await StartControllersAsync();
            if (!_closing)
            {
                _telemetryTimer.Start();
                _inputTimer.Start();
                if (ValidationRunner.IsRequested(Program.Arguments))
                {
                    WindowState = WindowState.Normal;
                    WindowDecorations = WindowDecorations.Full;
                    Show();
                    Activate();
                    _validationCancellation = new CancellationTokenSource();
                    _validationRun = ValidationRunner.RunAsync(this, Session, Program.Arguments, _validationCancellation.Token);
                    try
                    {
                        try
                        {
                            Program.ExitStatus = await _validationRun;
                        }
                        catch (Exception ex)
                        {
                            Program.ExitStatus = 1;
                            await Dialogs.MessageAsync(this, UiText.Get("Validation could not finish"), ex.Message);
                        }
                        return;
                    }
                    finally
                    {
                        _validationCancellation.Dispose();
                        _validationCancellation = null;
                        Close();
                    }
                }
                string validationContent = Session.Paths.ValidationContent;
                if (validationContent != null)
                {
                    await Session.AddFilesAsync([validationContent]);
                    RenderCurrentPage();
                }
                else if (!(await LaunchFromArgumentsAsync(Program.Arguments)))
                {
                    await Session.LoadSaveMigrationPreferencesAsync();
                    if (!_closing)
                    {
                        StartLibraryMonitor();
                        _ = RunStartupMaintenanceAsync();
                    }
                }
            }
        };
        Closing += async (object? _, WindowClosingEventArgs e) =>
        {
            if (!_closing)
            {
                Task<int> validationRun = _validationRun;
                if (validationRun != null && !validationRun.IsCompleted)
                {
                    ValidationCloseReason = e.CloseReason.ToString() + (e.IsProgrammatic ? ".programmatic" : ".external");
                    e.Cancel = true;
                    _validationCancellation?.Cancel();
                    Session.Cancel();
                }
                else
                {
                    e.Cancel = true;
                    _closing = true;
                    _inputTimer.Stop();
                    _telemetryTimer.Stop();
                    _maintenanceLifetime.Cancel();
                    _artworkResizeTimer?.Stop();
                    _folderMonitorTimer?.Stop();
                    _folderMonitor?.Dispose();
                    StopTranslation();
                    _presenceTimer.Stop();
                    if (_discord != null)
                    {
                        await _discord.DisposeAsync();
                    }
                    _homePreferenceTimer?.Stop();
                    SaveHomePreferences();
                    _desktopPreferenceTimer?.Stop();
                    SaveDesktopBrowsing();
                    _controllers?.Dispose();
                    _controllers = null;
                    _homeAudio?.Dispose();
                    _homeAudio = null;
                    _startupSequence?.Stop();
                    _artworkPageLifetime.Cancel();
                    foreach (IDisposable pageImage in _pageImages)
                    {
                        pageImage.Dispose();
                    }
                    _pageImages.Clear();
                    _artworkCache.Dispose();
                    Avatars.Changed -= ReloadAvatar;
                    _avatarBitmap?.Dispose();
                    Metadata.Dispose();
                    await Session.DisposeAsync();
                    Close();
                }
            }
        };
        SizeChanged += (object? _, SizeChangedEventArgs _) =>
        {
            UpdateShellLayout();
            RefreshArtworkForDisplay();
        };
        ScalingChanged += (object? _, EventArgs _) =>
        {
            RefreshArtworkForDisplay(force: true);
        };
        EnableModDrop();
        RefreshLauncherLanguage();
    }

    private void SessionChanged()
    {
        if (Interlocked.Exchange(ref _sessionUpdateQueued, 1) != 0)
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _sessionUpdateQueued, 0);
            if (!_closing)
            {
                _status.Text = UiText.Get(Session.Status);
                _error.IsVisible = (object)Session.LastFailure != null || Session.ErrorMessage != null;
                _error.IsEnabled = !Session.Busy;
                _cancel.IsVisible = Session.Busy && !Session.GameWindowClosing;
                _page.IsEnabled = (!Session.Busy || Session.IsLibraryScanning || Session.IsBackgroundMaintenance) && !Session.GameWindowClosing;
                _progress.IsVisible = Session.Busy;
                _progress.IsIndeterminate = !Session.Progress.HasValue;
                _progress.Value = Session.Progress.GetValueOrDefault();
                UpdateBackendSelector();
                TextBlock metrics = _metrics;
                TelemetrySnapshot telemetry = Session.Telemetry;
                string text;
                if ((object)telemetry == null)
                {
                    text = "";
                }
                else
                {
                    double? emulationFps = telemetry.EmulationFps;
                    string text2;
                    if (emulationFps.HasValue)
                    {
                        double valueOrDefault = emulationFps.GetValueOrDefault();
                        text2 = $"{valueOrDefault:0.0} FPS";
                    }
                    else
                    {
                        text2 = "FPS unavailable";
                    }
                    emulationFps = telemetry.FrameTimeMs;
                    string text3;
                    if (emulationFps.HasValue)
                    {
                        double valueOrDefault2 = emulationFps.GetValueOrDefault();
                        text3 = $"  ·  {valueOrDefault2:0.0} ms";
                    }
                    else
                    {
                        text3 = "";
                    }
                    text = text2 + text3;
                }
                metrics.Text = text;
                UpdatePerformanceReadouts();
                UpdateDisplayReadout();
                UpdateFrameRateReadouts();
                UpdateSessionControls();
                UpdateHomeReadouts();
                UpdateBootOverlay();
                UpdateExperience();
            }
        });
    }

    public async Task NavigateAsync(string section)
    {
        if (!_navigation.ContainsKey(section))
        {
            return;
        }
        InterruptMaintenance();
        long navigation = ++_navigationGeneration;
        if (Session.State == RuntimeState.Running)
        {
            await Session.PauseAsync();
        }
        if (navigation != _navigationGeneration)
        {
            return;
        }
        _section = section;
        _homeDetails = false;
        RenderCurrentPage();
        if (section == "Saves" && !Session.SaveMigrationReady)
        {
            await Session.StartSaveMigrationAsync();
            if (navigation != _navigationGeneration || _section != section)
            {
                return;
            }
            RenderCurrentPage();
        }
        if (section == "Settings" && (object)Session.SelectedBackend != null)
        {
            await Session.RefreshSettingsAsync();
            if (navigation != _navigationGeneration || _section != section)
            {
                return;
            }
            RenderCurrentPage();
        }
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (navigation == _navigationGeneration)
            {
                _page.IsEnabled = !Session.Busy || Session.IsLibraryScanning || Session.IsBackgroundMaintenance;
                UpdateLayout();
                Navigation.FocusFirst(this);
            }
        }, DispatcherPriority.Background);
    }

    public void RenderCurrentPage()
    {
        string homeFocusKey = _homeFocusKey;
        _duneShelf = null;
        ResetArtworkPage();
        _page.Content = null;
        _homeSurface.Content = null;
        foreach (IDisposable pageImage in _pageImages)
        {
            pageImage.Dispose();
        }
        _pageImages.Clear();
        _metadataForPage.Clear();
        _controllerReadout = null;
        _displayReadout = null;
        _frameRateReadouts.Clear();
        _performanceReadouts.Clear();
        _presentPlot = null;
        UpdateNavigationStyle();
        try
        {
            Control control;
            if (IsHomeLibrary)
            {
                control = null;
                goto IL_0225;
            }
            string section = _section;
            if (section == null)
            {
                goto IL_021a;
            }
            int length = section.Length;
            Control control2;
            if (length <= 7)
            {
                if (length != 5)
                {
                    if (length != 7)
                    {
                        goto IL_021a;
                    }
                    char c = section[0];
                    if (c != 'C')
                    {
                        if (c != 'L' || !(section == "Library"))
                        {
                            goto IL_021a;
                        }
                        control2 = BuildLibraryPage();
                    }
                    else
                    {
                        if (!(section == "Content"))
                        {
                            goto IL_021a;
                        }
                        control2 = BuildContentPage();
                    }
                }
                else
                {
                    char c = section[0];
                    if (c != 'O')
                    {
                        if (c != 'S' || !(section == "Saves"))
                        {
                            goto IL_021a;
                        }
                        control2 = BuildSavesPage();
                    }
                    else
                    {
                        if (!(section == "Orbit"))
                        {
                            goto IL_021a;
                        }
                        control2 = BuildOrbitPage();
                    }
                }
            }
            else if (length != 11)
            {
                if (length != 12 || !(section == "Enhancements"))
                {
                    goto IL_021a;
                }
                control2 = BuildEnhancementsPage();
            }
            else
            {
                char c = section[0];
                if (c != 'D')
                {
                    if (c != 'P' || !(section == "Performance"))
                    {
                        goto IL_021a;
                    }
                    control2 = BuildPerformancePage();
                }
                else
                {
                    if (!(section == "Diagnostics"))
                    {
                        goto IL_021a;
                    }
                    control2 = BuildDiagnosticsPage();
                }
            }
            goto IL_0222;
        IL_021a:
            control2 = BuildSettingsPage();
            goto IL_0222;
        IL_0225:
            Control control3 = control;
            if (_desktopSurface != null)
            {
                _desktopSurface.IsVisible = !_homeMode;
            }
            _homeSurface.IsVisible = _homeMode;
            Control home;
            if (_homeMode)
            {
                home = BuildHomeMode(control3);
                _homeSurface.Content = home;
                _homeFocusKey = homeFocusKey;
                home.LayoutUpdated += RestoreHomeFocus;
            }
            else
            {
                _page.Content = ((_section == "Library" || control3 == null) ? control3 : _dune.Adapt(control3));
            }
            return;
        IL_0222:
            control = control2;
            goto IL_0225;
            void RestoreHomeFocus(object? sender, EventArgs args)
            {
                if (_homeSurface.Content != home || !IsHomeLibrary)
                {
                    home.LayoutUpdated -= RestoreHomeFocus;
                }
                else if (!(home.Bounds.Width <= 0.0) && !(home.Bounds.Height <= 0.0) && Navigation.CurrentWindow == this)
                {
                    _homeFocusKey = homeFocusKey;
                    if (FocusHomeDefault())
                    {
                        home.LayoutUpdated -= RestoreHomeFocus;
                    }
                }
            }
        }
        catch (Exception ex) when ((ex is IOException || ex is ServiceException || ex is NxException) ? true : false)
        {
            ScrollViewer scrollViewer = PageScroll(VisualTheme.Column(Heading(UiText.Get("This view needs attention"), ex.Message), VisualTheme.LocalizedButton("Return to library", () =>
            {
                _selectedEntry = null;
                _section = "Library";
                RenderCurrentPage();
            })));
            if (_homeMode)
            {
                _homeSurface.Content = HomePalette.Adapt(scrollViewer);
            }
            else
            {
                _page.Content = scrollViewer;
            }
        }
    }

    private async Task ChooseBackendAsync()
    {
        BackendChoice selectedBackend = Session.SelectedBackend;
        await Dialogs.MessageAsync(this, "NX Infinity Core", ((object)selectedBackend == null) ? "The NX Infinity Core files are missing. Restore the complete NX Infinity package, then restart. Your game library and saves are retained." : ("NX Infinity uses one integrated emulator core.\n\nRevision: " + selectedBackend.Revision + "\n\nGraphics, controls and core options are available in Settings."));
    }

    public async Task AddGamesAsync()
    {
        if (_homeMode)
        {
            await ManageGameFoldersAsync();
        }
        else
        {
            await AddGameFolderAsync();
        }
    }

    private async Task OpenKeySetupAsync()
    {
        await NavigateAsync("Content");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_section == "Content" && !Session.Busy)
            {
                _page.IsEnabled = true;
                UpdateLayout();
                Navigation.Focus(this.GetVisualDescendants().OfType<Button>().FirstOrDefault((Button button) => button.Name == "ImportKeys"));
            }
        }, DispatcherPriority.Background);
    }

    private async Task ShowLastFailureAsync()
    {
        if (CanOpenKeySetup)
        {
            await OpenKeySetupAsync();
            return;
        }
        if (Session.RecoveryBackendId != null)
        {
            await RecoverLastSessionAsync();
            return;
        }
        await Dialogs.MessageAsync(this, UiText.Get("Action needs attention"), (Session.LastFailure?.Code ?? Session.ErrorCode ?? UiText.Get("Error")) + UiText.Get("\nCore: ") + (Session.LastFailure?.BackendId ?? Session.SelectedBackendId ?? UiText.Get("unknown")) + "\n\n" + Session.ErrorRecoveryMessage + "\n\n" + (Session.ErrorMessage ?? Session.LastFailureMessage));
    }

    private void RefreshAfterAction()
    {
        bool num = !_homeMode && _section == "Content" && _page.Content is ScrollViewer;
        Vector offset = (_page.Content as ScrollViewer)?.Offset ?? default(Vector);
        RenderCurrentPage();
        if (!num)
        {
            return;
        }
        object content = _page.Content;
        ScrollViewer restored = content as ScrollViewer;
        if (restored == null)
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_page.Content == restored)
            {
                restored.Offset = offset;
            }
        }, DispatcherPriority.Loaded);
    }

    private async Task MainActionAsync(Func<Task> action)
    {
        InterruptMaintenance();
        long failureSequence = Session.OperationFailureSequence;
        await action();
        bool flag = Session.OperationFailureSequence > failureSequence;
        if (flag && Session.RecoveryBackendId != null)
        {
            await RecoverLastSessionAsync();
            return;
        }
        if (flag && CanOpenKeySetup)
        {
            await OpenKeySetupAsync();
            return;
        }
        if (flag && _section == "Content" && !string.IsNullOrWhiteSpace(Session.ErrorMessage))
        {
            await Dialogs.MessageAsync(this, UiText.Get("Content action needs attention"), (Session.ErrorCode ?? UiText.Get("Error")) + "\n\n" + Session.ErrorMessage);
        }
        RefreshAfterAction();
    }

    private async void MainKeyDown(object? sender, KeyEventArgs e)
    {
        if (_startupSequence?.IsVisible ?? false)
        {
            _startupSequence.Stop();
            e.Handled = true;
        }
        else if (!_homeMode && e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            CloseDesktopFlyout();
            if (_section != "Library")
            {
                await NavigateAsync("Library");
            }
            _desktopSearch?.Focus();
            _desktopSearch?.SelectAll();
        }
        else if (e.Key == Key.Home && !(FocusManager?.GetFocusedElement() is TextBox))
        {
            e.Handled = true;
            await ControllerHomeAsync();
        }
        else if (e.Key == Key.F11)
        {
            e.Handled = true;
            if (Session.HasGame)
            {
                await MainActionAsync(() => Session.SetGameFullscreenAsync(!(Session.GameWindow?.Fullscreen ?? Session.GameFullscreenPreference)));
            }
            else
            {
                WindowState = ((WindowState != WindowState.FullScreen) ? WindowState.FullScreen : WindowState.Normal);
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (!_homeMode && CloseDesktopFlyout())
            {
                e.Handled = true;
                return;
            }
            if (!_homeMode && (_desktopSearch?.IsFocused ?? false) && _search.Length != 0)
            {
                _desktopSearch.Text = "";
                e.Handled = true;
                return;
            }
            if (Session.GameWindowClosing)
            {
                e.Handled = true;
                return;
            }
            if (Session.Busy)
            {
                Session.Cancel();
                e.Handled = true;
                return;
            }
            if (Session.State == RuntimeState.Running)
            {
                await Session.PauseAsync();
            }
            else if (_homeMode && !IsHomeLibrary)
            {
                await ReturnHomeAsync();
            }
            else if (_homeMode)
            {
                await SetHomeModeAsync(enabled: false);
            }
            else if (_section != "Library" || _selectedEntry != null)
            {
                _selectedEntry = null;
                await NavigateAsync("Library");
            }
            e.Handled = true;
        }
        else
        {
            Key key = e.Key;
            bool flag = (uint)(key - 23) <= 3u;
            if (flag && !(FocusManager?.GetFocusedElement() is TextBox))
            {
                Navigation.Move(this, e.Key);
                e.Handled = true;
            }
        }
    }

    private async Task StartControllersAsync()
    {
        try
        {
            BackendChoice source = Session.Backends.FirstOrDefault((BackendChoice b) => b.Id == "nxi");
            if ((object)source == null)
            {
                return;
            }
            BackendLaunch backendLaunch = await Task.Run(() => source.VerifyAsync(Session.Paths));
            if (_closing)
            {
                return;
            }
            KeyValuePair<string, string> keyValuePair = backendLaunch.ArtifactSha256.FirstOrDefault((KeyValuePair<string, string> p) =>
            {
                switch (Path.GetFileName(p.Key))
                {
                    case "SDL3.dll":
                    case "libSDL3.so":
                    case "libSDL3.so.0":
                        return true;
                    default:
                        return false;
                }
            });
            if (keyValuePair.Key != null)
            {
                TryStartHomeAudio(keyValuePair.Key, keyValuePair.Value);
                _controllers = HostControllerInput.Open(keyValuePair.Key, keyValuePair.Value);
            }
        }
        catch (Exception ex)
        {
            _inputHint.Text = "Mouse / keyboard ready · Controller input unavailable (" + ((ex is NxException ex2) ? ex2.Code : ex.GetType().Name) + ")";
        }
    }

    private async Task PollControllersAsync()
    {
        if (_controllers == null || _sendingInput)
        {
            return;
        }
        _sendingInput = true;
        try
        {
            IReadOnlyList<HostControllerSnapshot> samples = _controllers.Poll();
            MainWindow mainWindow = this;
            IReadOnlyList<HostControllerPress> presses;
            if (_mappingCapture != null)
            {
                IReadOnlyList<HostControllerPress> readOnlyList = Array.Empty<HostControllerPress>();
                presses = readOnlyList;
            }
            else
            {
                presses = _controllers.ButtonPresses;
            }
            mainWindow.ObserveControllerDevices(samples, presses);
            if (_mappingCapture != null)
            {
                _mappingCapture(samples);
                return;
            }
            if (_mappingNeedsNeutral)
            {
                _controllerNavigation.RequireNeutral();
                if (samples.Any((HostControllerSnapshot sample) => !sample.IsNeutral))
                {
                    return;
                }
                _mappingNeedsNeutral = false;
            }
            _controllerNavigation.Update(ControllerRoster.NavigationSamples(samples), Session.GuestOwnsInput && !Session.Busy);
            foreach (InputSnapshot item in _guestControllers.Update(ControllerRoster, samples, Session.GuestOwnsInput, Session.Busy || _controllerNavigation.SendNeutral, MapControllerInput, HomeFallbackChordEnabled))
            {
                await Session.ForwardInputAsync(item);
            }
            if (samples.Count != 0)
            {
                _inputHint.Text = "D-pad / stick  Move     South  Confirm     East  Back     LB / RB  Sections     Guide  Game menu";
            }
        }
        catch (Exception ex) when ((ex is NxException || ex is IOException || ex is JsonException || ex is ArgumentException) ? true : false)
        {
            _inputHint.Text = "Controller input unavailable: " + ex.Message + " · Mouse / keyboard remain available";
            if (!Session.GuestOwnsInput)
            {
                return;
            }
            foreach (InputSnapshot item2 in _guestControllers.Update(ControllerRoster, Array.Empty<HostControllerSnapshot>(), guest: true, suspend: true, MapControllerInput, HomeFallbackChordEnabled))
            {
                await Session.ForwardInputAsync(item2);
            }
        }
        finally
        {
            _sendingInput = false;
        }
    }

    internal async Task ControllerBackAsync()
    {
        if (!_homeMode && CloseDesktopFlyout())
        {
            return;
        }
        if (_startupSequence?.IsVisible ?? false)
        {
            _startupSequence.Stop();
        }
        else if (!Session.GameWindowClosing)
        {
            if (Session.Busy)
            {
                Session.Cancel();
                return;
            }
            if (Session.State == RuntimeState.Running)
            {
                await Session.PauseAsync();
                return;
            }
            if (_homeMode && !IsHomeLibrary)
            {
                await ReturnHomeAsync();
                return;
            }
            if (_homeMode)
            {
                await SetHomeModeAsync(enabled: false);
                return;
            }
            _selectedEntry = null;
            await NavigateAsync("Library");
        }
    }

    internal void CycleSection(int step)
    {
        if (!CycleHomeFilter(step))
        {
            string[] array = _navigation.Keys.ToArray();
            int num = Array.IndexOf(array, _section);
            _ = NavigateAsync(array[(num + step + array.Length) % array.Length]);
        }
    }

    private async Task ImportDesktopBackgroundAsync(GameLibraryEntry game)
    {
        string path = await Dialogs.BrowseAsync(this, "Choose background artwork", new string[4] { ".png", ".jpg", ".jpeg", ".webp" }, directory: false);
        if (path == null)
        {
            return;
        }
        await Task.Run(() =>
        {
            if (new FileInfo(path).Length > 8388608)
            {
                throw new InvalidDataException("Use an image smaller than 8 MB.");
            }
            new DesktopArtworkStore(Session.Paths.DataRoot).Write(game.EntryId, File.ReadAllBytes(path));
        });
        SelectDuneGame(game);
    }

    private async Task PopulateDesktopBackgroundAsync(Border target, GameLibraryEntry game)
    {
        long request = BeginArtworkRequest(target);
        CancellationTokenSource life = CancellationTokenSource.CreateLinkedTokenSource(_artworkPageLifetime.Token);
        try
        {
            target.DetachedFromVisualTree += (object? _, VisualTreeAttachmentEventArgs _) =>
            {
                try
                {
                    life.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            };
            try
            {
                byte[] bytes = null;
                try
                {
                    bytes = await Task.Run(() => new DesktopArtworkStore(Session.Paths.DataRoot).Read(game.EntryId), life.Token);
                }
                catch (Exception error) when (ArtworkFailure(error))
                {
                }
                if (bytes == null)
                {
                    bytes = await Metadata.GetBackgroundAsync(game.EntryId, game.DisplayName, life.Token);
                }
                if (bytes == null)
                {
                    return;
                }
                ArtworkDecodePlan plan;
                using (MemoryStream stream = new MemoryStream(bytes))
                {
                    using SKCodec sKCodec = SKCodec.Create(stream);
                    if (sKCodec == null)
                    {
                        throw new InvalidDataException("Background artwork could not be decoded.");
                    }
                    double num = Math.Max(Bounds.Width, target.Bounds.Width);
                    plan = ArtworkDecodePlan.ForViewport(sKCodec.Info.Width, sKCodec.Info.Height, num, Math.Max(target.Bounds.Height, num / 1.67), RenderScaling, 3840);
                }
                string key = "desktop-background:" + Convert.ToHexString(SHA256.HashData(bytes)) + ":" + plan.CacheKey;
                LeasedResourceCache<Bitmap>.Lease lease = _artworkCache.TryAcquire(key);
                if (lease == null)
                {
                    await _artworkDecoders.WaitAsync(life.Token);
                    try
                    {
                        lease = _artworkCache.TryAcquire(key);
                        if (lease == null)
                        {
                            Bitmap bitmap = await Task.Run(() =>
                            {
                                using MemoryStream source = new MemoryStream(bytes);
                                return plan.Decode(source);
                            }, life.Token);
                            try
                            {
                                life.Token.ThrowIfCancellationRequested();
                                bool consumed = false;
                                try
                                {
                                    lease = _artworkCache.Acquire(key, plan.MaximumBytes, () =>
                                    {
                                        consumed = true;
                                        return (Value: bitmap, Bytes: (long)bitmap.PixelSize.Width * (long)bitmap.PixelSize.Height * 4);
                                    });
                                }
                                finally
                                {
                                    if (consumed)
                                    {
                                        bitmap = null;
                                    }
                                }
                            }
                            finally
                            {
                                bitmap?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        _artworkDecoders.Release();
                    }
                }
                if (life.IsCancellationRequested || _closing || !CurrentArtworkRequest(target, request))
                {
                    lease?.Dispose();
                }
                else if (lease != null)
                {
                    _pageImages.Add(lease);
                    Image image = new Image
                    {
                        Source = lease.Value,
                        Stretch = Stretch.UniformToFill
                    };
                    image.DetachedFromVisualTree += (object? _, VisualTreeAttachmentEventArgs _) =>
                    {
                        lease.Dispose();
                        _pageImages.Remove(lease);
                    };
                    RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                    target.Child = image;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex2) when (ArtworkFailure(ex2) || ex2 is InvalidDataException)
            {
            }
        }
        finally
        {
            if (life != null)
            {
                ((IDisposable)life).Dispose();
            }
        }
    }

    private async Task ReadDetailModsAsync(GameLibraryEntry game, TextBlock label)
    {
        long generation = ++_detailInventoryGeneration;
        BackendChoice backendChoice = EffectiveGameBackend(game);
        LibraryCoreObservation? obj = (((object)backendChoice == null) ? null : InfinitySession.ObservationFor(game, backendChoice));
        string text = InfinitySession.KnownHex(obj?.TitleId, 16);
        string text2 = InfinitySession.KnownHex(obj?.BuildId, 64);
        GameBuildIdentity binding = ((text != null && text2 != null) ? new GameBuildIdentity(text, text2) : null);
        if ((object)binding == null)
        {
            label.Text = UiText.Get("Mods: game build not identified");
            return;
        }
        await _detailInventoryGate.WaitAsync();
        try
        {
            if (generation != _detailInventoryGeneration)
            {
                return;
            }
            ModRevision modRevision = await Task.Run(() => Session.Mods.Current(binding));
            if (generation == _detailInventoryGeneration)
            {
                label.Text = UiText.Format("NX mods: {0} installed · {1} enabled", modRevision?.Mods.Count ?? 0, modRevision?.Mods.Count((ModSelection mod) => mod.Enabled) ?? 0);
            }
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ServiceException) ? true : false)
        {
            if (generation == _detailInventoryGeneration)
            {
                label.Text = UiText.Get("Mod inventory unavailable");
            }
        }
        finally
        {
            _detailInventoryGate.Release();
        }
    }

    private void QueueDesktopBrowsingSave()
    {
        if (_desktopPreferenceTimer == null)
        {
            _desktopPreferenceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350L)
            };
            _desktopPreferenceTimer.Tick += (object? _, EventArgs _) =>
            {
                _desktopPreferenceTimer.Stop();
                SaveDesktopBrowsing();
            };
        }
        _desktopPreferenceTimer.Stop();
        _desktopPreferenceTimer.Start();
    }

    private void SaveDesktopBrowsing()
    {
        DesktopPreferences desktopPreferences = _desktopPreferences with
        {
            FavoritesOnly = _favorites,
            Sort = _sort,
            SelectedEntryId = _selectedEntry
        };
        if (desktopPreferences == _desktopPreferences)
        {
            return;
        }
        try
        {
            _desktopPreferenceStore.Write(desktopPreferences);
            _desktopPreferences = desktopPreferences;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            _status.Text = "Could not save library preferences: " + ex.Message;
        }
    }

    public Task SetDesktopNightAsync(bool night)
    {
        return SetAppearanceAsync(Appearance with
        {
            Desktop = new ThemeChoice(night ? "night" : "dune")
        });
    }

    private async Task DuneSortAsync()
    {
        string text = await Dialogs.ChooseAsync(this, "Sort library",
        [
            ("name", "Name"),
            ("played", "Recently played"),
            ("added", "Recently added")
        ]);
        if (text != null)
        {
            _sort = ((text == "played") ? LibrarySort.RecentlyPlayed : ((text == "added") ? LibrarySort.RecentlyAdded : LibrarySort.Name));
            QueueDesktopBrowsingSave();
            if (_section != "Library")
            {
                await NavigateAsync("Library");
            }
            else
            {
                FilterDuneLibrary();
            }
        }
    }

    private Control BuildDuneLibrary()
    {
        _artworkLibrary = Session.Library.List(new LibraryQuery(null, FavoritesOnly: false, null, LibrarySort.Name, 0, 2000));
        _desktopGames = _artworkLibrary.Where(LibraryContentClassification.IsAvailableGame).ToArray();
        _desktopRoutes = Session.Profiles.Read();
        Grid split = new Grid
        {
            Name = "DuneSplit",
            ColumnDefinitions = new ColumnDefinitions("38*,4,62*"),
            ClipToBounds = true
        };
        split.ColumnDefinitions[0].Width = new GridLength(_desktopPreferences.LibraryFraction, GridUnitType.Star);
        split.ColumnDefinitions[2].Width = new GridLength(1.0 - _desktopPreferences.LibraryFraction, GridUnitType.Star);
        _duneOverview = new ContentControl
        {
            Name = "SelectedGameDetails",
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(14.0, 6.0, 20.0, 12.0)
        };
        IReadOnlyList<GameLibraryEntry> readOnlyList = FilteredDuneGames();
        GameLibraryEntry gameLibraryEntry = readOnlyList.FirstOrDefault((GameLibraryEntry g) => g.EntryId == _selectedEntry) ?? readOnlyList.FirstOrDefault();
        _selectedEntry = gameLibraryEntry?.EntryId;
        _duneShelf = new DuneLibraryShelf(readOnlyList, (GameLibraryEntry entry, double size) =>
        {
            Control control = Artwork(entry, size);
            double width = (control.Height = double.NaN);
            control.Width = width;
            Button button = new Button
            {
                Name = "GameCard",
                Tag = entry.EntryId,
                Content = control
            };
            AutomationProperties.SetName(button, GameTitle(entry));
            ToolTip.SetTip(button, GameTitle(entry));
            return button;
        }, SelectDuneGame, _selectedEntry, _desktopLibraryOffset);
        DuneLibraryShelf shelf = _duneShelf;
        shelf.Scrolled += (double value) =>
        {
            if (_duneShelf == shelf)
            {
                _desktopLibraryOffset = value;
            }
        };
        ref TextBlock? duneEmpty = ref _duneEmpty;
        DuneTheme dune = _dune;
        string text;
        if (_desktopGames.Count != 0)
        {
            text = "No games match your search.";
        }
        else
        {
            text = ((_artworkLibrary.Count > 0) ? "No identified games yet." : "Add a game folder to begin.");
        }
        duneEmpty = dune.Text(text, 14.0, muted: true);
        _duneEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _duneEmpty.VerticalAlignment = VerticalAlignment.Center;
        _duneEmpty.IsVisible = readOnlyList.Count == 0;
        Grid item = new Grid
        {
            Name = "DuneLibraryPanel",
            Margin = new Thickness(20.0, 6.0, 10.0, 12.0),
            Children =
            {
                (Control)_duneShelf,
                (Control)_duneEmpty
            }
        };
        split.Children.Add(item);
        GridSplitter gridSplitter = new GridSplitter
        {
            Name = "DuneDivider",
            Width = 4.0,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        gridSplitter.PointerReleased += (object? _, PointerReleasedEventArgs _) =>
        {
            double value = split.ColumnDefinitions[0].ActualWidth / Math.Max(1.0, split.Bounds.Width - 4.0);
            _desktopPreferences = _desktopPreferences with
            {
                LibraryFraction = Math.Clamp(value, 0.25, 0.6)
            };
            try
            {
                _desktopPreferenceStore.Write(_desktopPreferences);
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
            {
                _status.Text = "Could not save panel size: " + ex.Message;
            }
        };
        Grid.SetColumn(gridSplitter, 1);
        split.Children.Add(gridSplitter);
        Grid.SetColumn(_duneOverview, 2);
        split.Children.Add(_duneOverview);
        _duneOverview.Content = (((object)gameLibraryEntry == null) ? BuildDuneEmptyDetails() : BuildDuneDetails(gameLibraryEntry));
        return split;
    }

    private IReadOnlyList<GameLibraryEntry> FilteredDuneGames()
    {
        IEnumerable<GameLibraryEntry> source = _desktopGames.Where((GameLibraryEntry g) => !_favorites || g.Favorite);
        if (_search.Length != 0)
        {
            source = source.Where((GameLibraryEntry g) => g.DisplayName.Contains(_search, StringComparison.CurrentCultureIgnoreCase) || (_metadataForPage.TryGetValue(g.EntryId, out NintendoCatalogSelection value) && (value?.Game.DisplayTitle.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ?? false)));
        }
        return (_sort switch
        {
            LibrarySort.RecentlyPlayed => (IEnumerable<GameLibraryEntry>)source.OrderByDescending((GameLibraryEntry g) => g.Launches.LastOrDefault()?.StartedUtc ?? DateTimeOffset.MinValue),
            LibrarySort.RecentlyAdded => source.OrderByDescending((GameLibraryEntry g) => g.AddedUtc),
            _ => source.OrderBy((GameLibraryEntry g) => g.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        }).ToArray();
    }

    private void FilterDuneLibrary()
    {
        if (_duneShelf != null && _duneOverview != null)
        {
            IReadOnlyList<GameLibraryEntry> readOnlyList = FilteredDuneGames();
            _duneShelf.Filter(readOnlyList);
            if (_duneEmpty != null)
            {
                _duneEmpty.IsVisible = readOnlyList.Count == 0;
            }
            GameLibraryEntry gameLibraryEntry = readOnlyList.FirstOrDefault((GameLibraryEntry g) => g.EntryId == _selectedEntry) ?? readOnlyList.FirstOrDefault();
            if ((object)gameLibraryEntry != null)
            {
                SelectDuneGame(gameLibraryEntry);
                return;
            }
            _selectedEntry = null;
            _duneOverview.Content = BuildDuneEmptyDetails();
        }
    }

    private void SelectDuneGame(GameLibraryEntry game)
    {
        bool flag = _selectedEntry != game.EntryId;
        _selectedEntry = game.EntryId;
        _duneShelf?.Select(game.EntryId);
        if (_duneOverview != null)
        {
            _duneOverview.Content = BuildDuneDetails(game);
        }
        if (flag)
        {
            QueueDesktopBrowsingSave();
        }
    }

    private Control BuildDuneEmptyDetails()
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 14.0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 360.0
        };
        stackPanel.Children.Add(_dune.Text((_desktopGames.Count == 0) ? "Library" : "Select a game", 24.0));
        stackPanel.Children.Add(_dune.Text((_desktopGames.Count == 0) ? "Choose an existing game folder. Your files stay where they are." : "Clear your search or choose another filter.", 14.0, muted: true));
        stackPanel.Children.Add(VisualTheme.Button((_desktopGames.Count == 0) ? "Add game folder" : "Show all games", async () =>
        {
            if (_desktopGames.Count == 0)
            {
                await AddGamesAsync();
            }
            else
            {
                _favorites = false;
                UpdateDesktopFavorites();
                QueueDesktopBrowsingSave();
                _search = "";
                if (_desktopSearch != null)
                {
                    _desktopSearch.Text = "";
                }
                FilterDuneLibrary();
            }
        }));
        if (_desktopGames.Count == 0)
        {
            IReadOnlyList<GameLibraryEntry> artworkLibrary = _artworkLibrary;
            if (artworkLibrary != null && artworkLibrary.Count > 0)
            {
                stackPanel.Children.Add(_dune.Text(UiText.Get("Existing files are preserved in Content. Restore NX Infinity Core to identify games; updates and DLC stay separate."), 14.0, muted: true));
                stackPanel.Children.Add(VisualTheme.LocalizedButton("Review files", async () =>
                {
                    _contentTab = "Review";
                    await NavigateAsync("Content");
                }));
            }
        }
        return _dune.Adapt(stackPanel);
    }

    private Control BuildDuneDetails(GameLibraryEntry game)
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 24.0,
            Name = "GamePresentation"
        };
        Border hero = new Border
        {
            Name = "DuneHero",
            CornerRadius = new CornerRadius(16.0),
            ClipToBounds = true,
            Background = _dune.Surface,
            Height = 420.0
        };
        hero.SizeChanged += (object? _, SizeChangedEventArgs _) =>
        {
            double num4 = Math.Max(260.0, hero.Bounds.Width / 1.67);
            if (Math.Abs(hero.Height - num4) > 1.0)
            {
                hero.Height = num4;
            }
        };
        Border art = new Border
        {
            Name = "DuneHeroArtwork",
            Classes = { "game-artwork" },
            Background = _dune.Hover
        };
        TrackArtworkSize(art, () => PopulateDesktopBackgroundAsync(art, game));
        _ = PopulateDesktopBackgroundAsync(art, game);
        Grid grid = new Grid();
        grid.Children.Add(art);
        grid.Children.Add(new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.0, 0.0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.0, 1.0, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.Parse("#38000000"), 0.0),
                    new GradientStop(Colors.Transparent, 0.35),
                    new GradientStop(Color.Parse("#18000000"), 0.55),
                    new GradientStop(Color.Parse("#D9000000"), 1.0)
                }
            }
        });
        TextBlock textBlock = _dune.Text(GameTitle(game), 34.0);
        textBlock.Name = "SelectedGameTitle";
        textBlock.Foreground = Brushes.White;
        textBlock.FontWeight = FontWeight.Bold;
        textBlock.MaxLines = 3;
        textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        textBlock.Margin = new Thickness(32.0, 28.0, 32.0, 0.0);
        textBlock.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(textBlock);
        Grid grid2 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 20.0,
            Margin = new Thickness(32.0, 0.0, 28.0, 30.0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Button button = VisualTheme.Button((Session.ActiveGame?.EntryId == game.EntryId) ? UiText.Get("Resume") : UiText.Get("Play"), async () =>
        {
            if ((object)Session.SelectedBackend == null)
            {
                await ChooseBackendAsync();
                if ((object)Session.SelectedBackend == null)
                {
                    return;
                }
            }
            await MainActionAsync(() => (!(Session.ActiveGame?.EntryId == game.EntryId)) ? Session.LaunchAsync(game.EntryId) : Session.ResumeAsync());
        });
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Name = "PlayGame";
        button.Tag = "primary-focus";
        button.Width = 142.0;
        double height = (button.MinHeight = 40.0);
        button.Height = height;
        button.Background = Brushes.White;
        button.Foreground = Brush.Parse("#151515");
        button.FontWeight = FontWeight.DemiBold;
        ToolTip.SetTip(button, ((object)Session.SelectedBackend == null) ? "Restore NX Infinity Core to play" : ("Play " + GameTitle(game)));
        Button button2 = VisualTheme.LocalizedButton("Configure", async () =>
        {
            _settingsForGame = true;
            _settingsCategory = "Graphics";
            await OpenDuneGameToolsAsync(game, "Settings");
        });
        button2.HorizontalContentAlignment = HorizontalAlignment.Center;
        button2.Name = "ConfigureGame";
        height = (button2.MinHeight = 34.0);
        button2.Height = height;
        button2.Width = 142.0;
        button2.Background = Brush.Parse("#55FFFFFF");
        button2.Foreground = Brushes.White;
        StackPanel item = new StackPanel
        {
            Spacing = 6.0,
            Children =
            {
                (Control)button,
                (Control)button2
            }
        };
        grid2.Children.Add(item);
        BackendChoice backendChoice = EffectiveGameBackend(game);
        string text;
        if ((object)backendChoice == null)
        {
            text = ((EffectiveGameBackendId(game) != null) ? null : game.Observations.LastOrDefault()?.DisplayVersion);
        }
        else
        {
            text = InfinitySession.ObservationFor(game, backendChoice)?.DisplayVersion;
        }
        TextBlock textBlock2 = _dune.Text(string.Join("  ·  ", new string[3]
        {
            "Nintendo Switch",
            EffectiveGameCoreName(game),
            (text == null) ? null : ("Version " + text)
        }.Where((string v) => v != null)), 12.0);
        textBlock2.Foreground = Brush.Parse("#DDFFFFFF");
        textBlock2.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(textBlock2, 1);
        grid2.Children.Add(textBlock2);
        grid.Children.Add(grid2);
        hero.Child = grid;
        stackPanel.Children.Add(hero);
        WrapPanel wrapPanel = new WrapPanel
        {
            Name = "GameTools",
            Orientation = Orientation.Horizontal
        };
        (string, string)[] array = new (string, string)[3]
        {
            ("Mods", "Content"),
            ("Saves", "Saves"),
            ("Updates & DLC", "Content")
        };
        for (int num3 = 0; num3 < array.Length; num3++)
        {
            (string, string) tuple = array[num3];
            string item2 = tuple.Item1;
            string item3 = tuple.Item2;
            string target = item3;
            string tab = ((item2 == "Mods") ? "Mods" : "Updates");
            Button button3 = VisualTheme.LocalizedButton(item2, async () =>
            {
                if (target == "Content")
                {
                    _contentTab = tab;
                }
                await OpenDuneGameToolsAsync(game, target);
            });
            button3.Classes.Add("dune-quiet");
            wrapPanel.Children.Add(button3);
        }
        Button more = null;
        more = VisualTheme.LocalizedButton("More  ···", () =>
        {
            if (more?.Flyout is Flyout { IsOpen: false } flyout)
            {
                flyout.ShowAt(more);
            }
            return Task.CompletedTask;
        });
        more.Name = "GameMore";
        more.Classes.Add("dune-quiet");
        StackPanel options = new StackPanel
        {
            Spacing = 3.0,
            MinWidth = 220.0
        };
        Option("CoreInformation", "NX Infinity Core", ChooseBackendAsync);
        Option("CreateDesktopShortcut", "Create Desktop Shortcut", () => CreateGameShortcutAsync(game));
        Option("OpenGameDirectory", "Open Folder", () =>
        {
            DesktopShortcuts.OpenFolder(Path.GetDirectoryName(game.Content.AbsolutePath));
            return Task.CompletedTask;
        });
        Option("EditGameMetadata", "Edit cover & details", () => EditMetadataAsync(game));
        Option("ImportGameBackground", "Choose background artwork", () => ImportDesktopBackgroundAsync(game));
        Option("ToggleGameFavorite", game.Favorite ? "Remove favorite" : "Add favorite", () => MainActionAsync(() => Session.RunAsync("Updating favorite", (CancellationToken _) =>
        {
            Session.Library.SetFavorite(game.EntryId, !game.Favorite);
            return Task.CompletedTask;
        })));
        Option("InspectGame", "Inspect game", () => MainActionAsync(() => Session.InspectAgainAsync(game.EntryId)));
        Option("GameDiagnostics", "Diagnostics", () => NavigateAsync("Diagnostics"));
        Option("GameIdentity", "File & identity details", () => Dialogs.MessageAsync(this, GameTitle(game), IdentityText(game)));
        _dune.Adapt(options);
        more.Flyout = CreateDuneFlyout(more, options);
        wrapPanel.Children.Add(more);
        stackPanel.Children.Add(wrapPanel);
        Grid grid3 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 28.0
        };
        StackPanel stackPanel2 = new StackPanel
        {
            Spacing = 12.0
        };
        stackPanel2.Children.Add(_dune.Text(UiText.Get("Overview")));
        if (!string.IsNullOrWhiteSpace(game.Notes))
        {
            stackPanel2.Children.Add(_dune.Text(game.Notes, 14.0, muted: true));
        }
        NintendoCatalogGame nintendoCatalogGame = CatalogSelection(game)?.Game;
        string text2 = nintendoCatalogGame?.Publisher;
        if (text2 != null)
        {
            stackPanel2.Children.Add(_dune.Text(text2, 13.0, muted: true));
        }
        grid3.Children.Add(stackPanel2);
        StackPanel facts = new StackPanel
        {
            Spacing = 10.0,
            MinWidth = 180.0,
            MaxWidth = 260.0
        };
        facts.Children.Add(_dune.Text(UiText.Get("Details"), 13.0));
        TextBlock textBlock3 = _dune.Text(UiText.Get("Reading mod inventory…"), 12.0, muted: true);
        textBlock3.Name = "GameModCount";
        facts.Children.Add(textBlock3);
        _ = ReadDetailModsAsync(game, textBlock3);
        Fact("Core", EffectiveGameCoreName(game));
        Fact("Version", text ?? UiText.Get("Not identified"));
        Fact("Added", game.AddedUtc.ToLocalTime().ToString("d MMM yyyy"));
        Fact("Last played", game.Launches.LastOrDefault()?.StartedUtc.ToLocalTime().ToString("d MMM yyyy"));
        if ((object)nintendoCatalogGame != null)
        {
            Fact("Released", CatalogDate(nintendoCatalogGame));
        }
        Grid.SetColumn(facts, 1);
        grid3.Children.Add(facts);
        stackPanel.Children.Add(grid3);
        return new ScrollViewer
        {
            Name = "GameDetailScroll",
            Content = stackPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        void Fact(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Grid grid4 = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("96,*"),
                    ColumnSpacing = 8.0
                };
                grid4.Children.Add(_dune.Text(UiText.Get(label), 12.0, muted: true));
                TextBlock textBlock4 = _dune.Text(value, 12.0);
                if (label == "Version")
                {
                    textBlock4.Name = "GameVersion";
                }
                Grid.SetColumn(textBlock4, 1);
                grid4.Children.Add(textBlock4);
                facts.Children.Add(grid4);
            }
        }
        void Option(string name, string label, Func<Task> action)
        {
            Button button4 = VisualTheme.LocalizedButton(label, async () =>
            {
                (more.Flyout as Flyout)?.Hide();
                await action();
            });
            button4.Name = name;
            button4.HorizontalAlignment = HorizontalAlignment.Stretch;
            button4.HorizontalContentAlignment = HorizontalAlignment.Left;
            options.Children.Add(button4);
        }
    }

    private Task OpenDuneGameToolsAsync(GameLibraryEntry game, string section)
    {
        return NavigateAsync(section);
    }

    private string? EffectiveGameBackendId(GameLibraryEntry game)
    {
        return Session.SelectedBackendId;
    }

    private BackendChoice? EffectiveGameBackend(GameLibraryEntry game)
    {
        return Session.SelectedBackend;
    }

    private string? EffectiveGameCoreName(GameLibraryEntry game)
    {
        return Session.SelectedBackend?.DisplayName;
    }

    private void TryStartHomeAudio(string path, string hash)
    {
        try
        {
            _homeAudio = new HomeAudio(path, hash);
        }
        catch (Exception ex) when ((ex is InvalidOperationException || ex is DllNotFoundException || ex is BadImageFormatException || ex is EntryPointNotFoundException) ? true : false)
        {
            _homeAudioIssue = "Audio output is unavailable. Check your output device and restart NX.";
        }
        StartupSequence? startupSequence = _startupSequence;
        if (startupSequence != null && startupSequence.IsVisible)
        {
            PlayHomeSound(HomeSound.Startup, startup: true);
        }
    }

    private void PlayHomeSound(HomeSound sound, bool startup = false)
    {
        if ((_homeMode || startup) && _homePreferences.SoundsEnabled && !Session.GuestOwnsInput && _homePreferences.SoundVolume != 0.0)
        {
            _homeAudio?.Play(sound, _homePreferences.SoundVolume);
        }
    }

    private void UpdateExperience()
    {
        if (Session.GuestOwnsInput)
        {
            _homeAudio?.Stop();
        }
        if (Session.ErrorCode != null && Session.OperationFailureSequence != _lastSoundFailure)
        {
            _lastSoundFailure = Session.OperationFailureSequence;
            PlayHomeSound(HomeSound.Notice);
        }
    }

    private async Task ShowHomeExperienceAsync()
    {
        while (!_closing)
        {
            string choice = await Dialogs.ChooseAsync(this, UiText.Get("HOME audio & startup"), new (string, string)[6]
            {
                ("mute", UiText.Get("Interface sounds: ") + UiText.Get(_homePreferences.SoundsEnabled ? "On" : "Off")),
                ("volume", UiText.Format("Sound volume: {0:P0}", _homePreferences.SoundVolume)),
                ("preview", UiText.Get("Preview startup sound")),
                ("animation", UiText.Get("Startup animation: ") + UiText.Get(_homePreferences.StartupAnimation ? "On" : "Off")),
                ("homechord", UiText.Get("HOME fallback: View / Select + Menu / Start · ") + UiText.Get(_homePreferences.HomeFallbackChord ? "On" : "Off")),
                ("replay", UiText.Get("Replay NX Infinity intro"))
            }, _homeAudioIssue ?? ((_homeAudio == null) ? UiText.Get("Install a complete NX core package to enable HOME audio.") : UiText.Get("Navigation, selection, back and notification sounds. Game audio is controlled separately.")));
            if (choice == null)
            {
                break;
            }
            if (choice == "mute")
            {
                _homePreferences = _homePreferences with
                {
                    SoundsEnabled = !_homePreferences.SoundsEnabled
                };
                if (!_homePreferences.SoundsEnabled)
                {
                    _homeAudio?.Stop();
                }
            }
            if (choice == "volume")
            {
                string text = await Dialogs.ChooseAsync(this, UiText.Get("HOME sound volume"),
                [
                    ("0", UiText.Get("Mute")),
                    ("15", UiText.Get("15% · quiet")),
                    ("35", UiText.Get("35% · default")),
                    ("60", "60%"),
                    ("100", "100%")
                ]);
                if (text != null)
                {
                    _homePreferences = _homePreferences with
                    {
                        SoundVolume = (double)int.Parse(text) / 100.0
                    };
                }
            }
            if (choice == "animation")
            {
                _homePreferences = _homePreferences with
                {
                    StartupAnimation = !_homePreferences.StartupAnimation
                };
            }
            if (choice == "homechord")
            {
                _homePreferences = _homePreferences with
                {
                    HomeFallbackChord = !_homePreferences.HomeFallbackChord
                };
            }
            SaveHomePreferences();
            if (choice == "preview")
            {
                PlayHomeSound(HomeSound.Startup, startup: true);
            }
            if (choice == "replay")
            {
                BeginStartupSequence();
                break;
            }
        }
    }

    private async Task AddGameFolderAsync()
    {
        string path = await Dialogs.BrowseAsync(this, UiText.Get("Add game folder · includes subfolders"), Array.Empty<string>(), directory: true);
        if (path != null)
        {
            GameLibraryFolder folder = null;
            await Session.RunAsync("Remembering game folder", (CancellationToken _) =>
            {
                folder = Session.Library.AddFolder(path);
                return Task.CompletedTask;
            });
            if ((object)folder != null)
            {
                await ScanFoldersAsync([folder]);
            }
            else
            {
                RenderCurrentPage();
            }
        }
    }

    private async Task ScanFoldersAsync(IReadOnlyList<GameLibraryFolder>? folders = null)
    {
        await MainActionAsync(() => Session.ScanGameFoldersAsync(folders));
        if (!Session.HasGame && !Session.OrbitEnabled)
        {
            LibraryFolderImportResult? lastFolderImport = Session.LastFolderImport;
            if ((object)lastFolderImport == null || !lastFolderImport.Canceled)
            {
                await MainActionAsync(() => Session.RefreshMissingArtworkAsync());
            }
        }
        LibraryFolderImportResult lastFolderImport2 = Session.LastFolderImport;
        if ((object)lastFolderImport2 != null)
        {
            IEnumerable<string> first = from i in lastFolderImport2.Items.Where((LibraryFolderImportItem i) => i.Outcome == "failed").Take(20)
                                        select Path.GetFileName(i.AbsolutePath) + ": " + i.Detail;
            await Dialogs.MessageAsync(this, UiText.Get("Game folder scan"), InfinitySession.FolderImportSummary(lastFolderImport2) + UiText.Get("\n\nSupported files: NSP, XCI, NRO and NSO. Original files stay where they are. ") + UiText.Get("Identified updates and DLC appear in Content. Unverified entries still need native inspection before use.") + UiText.Get("\n\nSaved folders are checked at startup. Manage folders to turn automatic scanning off or scan again.") + (lastFolderImport2.Canceled ? UiText.Get("\n\nCompleted imports were kept. Scan again to continue.") : "") + "\n\n" + string.Join("\n", first.Concat(from i in lastFolderImport2.Issues.Take(20)
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             select i.RelativePath + ": " + i.Code)));
        }
    }

    private async Task ManageGameFoldersAsync()
    {
        IReadOnlyList<GameLibraryFolder> folders = Session.Library.Folders();
        List<(string, string)> list = new List<(string, string)>
        {
            ("add", UiText.Get("Add game folder")),
            ("scan", UiText.Get("Scan all saved folders")),
            ("file", UiText.Get("Add an individual file"))
        };
        list.AddRange(folders.Select((GameLibraryFolder f) => (Id: f.Id, f.AbsolutePath + (f.ScanAtStartup ? " · automatic" : " · manual"))));
        string choice = await Dialogs.ChooseAsync(this, UiText.Get("Game folders"), list, UiText.Get("Choose a whole folder once. NX finds supported files in its subfolders. Removing a folder from this list keeps your imported games and original files."));
        if (choice == "add")
        {
            await AddGameFolderAsync();
            return;
        }
        if (choice == "scan")
        {
            await ScanFoldersAsync();
            return;
        }
        if (choice == "file")
        {
            string path = await Dialogs.BrowseAsync(this, UiText.Get("Add a local game"), new string[4] { ".nro", ".nso", ".xci", ".nsp" }, directory: false);
            if (path == null)
            {
                return;
            }
            await MainActionAsync(() => Session.AddFilesAsync([path]));
            if (!Session.HasGame && !Session.OrbitEnabled)
            {
                await MainActionAsync(() => Session.RefreshMissingArtworkAsync());
            }
            return;
        }
        GameLibraryFolder folder = folders.FirstOrDefault((GameLibraryFolder f) => f.Id == choice);
        if ((object)folder == null)
        {
            return;
        }
        string action = await Dialogs.ChooseAsync(this, UiText.Get("Game folder"),
        [
            ("scan", UiText.Get("Scan this folder now")),
            ("auto", folder.ScanAtStartup ? "Turn off startup scanning" : "Turn on startup scanning"),
            ("remove", UiText.Get("Stop tracking this folder"))
        ], folder.AbsolutePath);
        if (action == "scan")
        {
            await ScanFoldersAsync([folder]);
            return;
        }
        string text = action;
        if ((!(text == "auto") && !(text == "remove")) || 1 == 0)
        {
            return;
        }
        await MainActionAsync(() => Session.RunAsync("Updating game folders", (CancellationToken _) =>
        {
            if (action == "remove")
            {
                Session.Library.RemoveFolder(folder.Id);
            }
            else
            {
                Session.Library.SetFolderStartupScan(folder.Id, !folder.ScanAtStartup);
            }
            return Task.CompletedTask;
        }));
    }

    private Control BuildFrameRateCard(GameLibraryEntry? game = null)
    {
        string prefix = (((object)game == null) ? "FrameRateGlobal" : "FrameRateGame");
        int? preference = (((object)game == null) ? new int?(Session.GlobalFrameRateTarget) : Session.GetGameFrameRateTarget(game.EntryId));
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 11.0
        };
        stackPanel.Children.Add(VisualTheme.LocalizedText(((object)game == null) ? "Frame rate · all games" : "Frame rate · this game", 21.0, null, FontWeight.DemiBold));
        stackPanel.Children.Add(VisualTheme.Text(((object)game == null) ? UiText.Get("Global preference; individual games can override it.") : GameTitle(game), 14.0, VisualTheme.Muted));
        List<Control> actions = new List<Control>();
        if ((object)game != null)
        {
            Choice(null, UiText.Get("Use global preference"), "Inherit");
        }
        Choice(0, UiText.Get("Original"), "Original");
        Choice(60, "60 FPS", "60");
        stackPanel.Children.Add(Actions(actions.ToArray()));
        TextBlock textBlock = VisualTheme.LocalizedText("", 14.0, VisualTheme.Blue);
        textBlock.Name = prefix + "Requested";
        TextBlock textBlock2 = VisualTheme.LocalizedText("", 14.0);
        textBlock2.Name = prefix + "Effective";
        TextBlock textBlock3 = VisualTheme.LocalizedText("", 14.0, VisualTheme.Muted);
        textBlock3.Name = prefix + "Measured";
        stackPanel.Children.Add(textBlock);
        stackPanel.Children.Add(textBlock2);
        stackPanel.Children.Add(textBlock3);
        _frameRateReadouts.Add(new FrameRateReadouts(game, textBlock, textBlock2, textBlock3));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Changes apply on the next launch. 60 FPS requires a timing module for the exact game build. Unsupported builds retain Original behavior.", 13.0, VisualTheme.Muted));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Experimental timing patches are selected by game version and executable ID. Some are downloaded from their author on first use and cached. Choose Original to remove the enhancement on the next launch. A patch cannot guarantee stable 60 FPS on every device.", 13.0, VisualTheme.Muted));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Measured FPS describes frame output. Normal game speed still needs a wall-time comparison of gameplay, physics, animation and audio.", 12.0, VisualTheme.Muted));
        if ((object)game != null)
        {
            FrameRateCompatibilityAssessment assessment = Session.GetFrameRateCompatibility(game);
            TextBlock textBlock4 = VisualTheme.Text(FrameRateSupportLabel(assessment), 14.0, assessment.CanApply ? VisualTheme.Blue : VisualTheme.Muted);
            textBlock4.Name = prefix + "Compatibility";
            stackPanel.Children.Add(textBlock4);
            TextBlock textBlock5 = VisualTheme.Text(FrameRateReason(assessment), 13.0, VisualTheme.Muted);
            textBlock5.Name = prefix + "Reason";
            stackPanel.Children.Add(textBlock5);
            Button button = VisualTheme.LocalizedButton("Build compatibility and sources", () => Dialogs.MessageAsync(this, UiText.Get("Frame-rate compatibility"), FrameRateDetails(assessment)));
            button.Name = prefix + "Sources";
            stackPanel.Children.Add(Actions(button));
        }
        else
        {
            stackPanel.Children.Add(VisualTheme.LocalizedText("Select a game to review its build-specific patch availability. Native-60 games keep their original synchronization.", 13.0, VisualTheme.Muted));
            if (Session.HasGame && !string.IsNullOrWhiteSpace(Session.FrameRatePolicyExplanation))
            {
                stackPanel.Children.Add(VisualTheme.LocalizedText(Session.FrameRatePolicyExplanation, 13.0, VisualTheme.Muted));
            }
        }
        UpdateFrameRateReadouts();
        return Card(stackPanel);
        void Choice(int? target, string label, string suffix)
        {
            Button button2 = VisualTheme.Button(label, () => MainActionAsync(() => ((object)game != null) ? Session.SetGameFrameRateTargetAsync(game.EntryId, target) : Session.SetGlobalFrameRateTargetAsync(target.Value)), (preference == target) ? "selected" : "subtle");
            button2.Name = prefix + suffix;
            AutomationProperties.SetName(button2, label);
            actions.Add(button2);
        }
    }

    private void UpdateFrameRateReadouts()
    {
        foreach (FrameRateReadouts frameRateReadout in _frameRateReadouts)
        {
            int? num = (((object)frameRateReadout.Game == null) ? new int?(Session.GlobalFrameRateTarget) : Session.GetGameFrameRateTarget(frameRateReadout.Game.EntryId));
            int target = (((object)frameRateReadout.Game == null) ? Session.GlobalFrameRateTarget : Session.ResolveFrameRateTarget(frameRateReadout.Game.EntryId));
            frameRateReadout.Requested.Text = ((!num.HasValue) ? UiText.Format("Next launch: global preference ({0})", FrameRateTargetLabel(target)) : UiText.Format("Next launch preference: {0}", FrameRateTargetLabel(target)));
            bool flag = Session.HasGame && ((object)frameRateReadout.Game == null || Session.ActiveGame?.EntryId == frameRateReadout.Game.EntryId);
            FrameRatePolicyState frameRatePolicyState = (flag ? Session.NativeFrameRatePolicy : null);
            if ((object)frameRatePolicyState == null)
            {
                frameRateReadout.Effective.Text = UiText.Get(flag ? "Effective: native policy not reported" : "Effective: no running game in this scope");
                frameRateReadout.Measured.Text = UiText.Get("Measured game FPS: unavailable");
                continue;
            }
            frameRateReadout.Effective.Text = UiText.Format("Active game request: {0}\nEffective: {1}", FrameRateTargetLabel(frameRatePolicyState.RequestedTarget), UiText.Get((frameRatePolicyState.EffectivePolicy == "patched_60") ? "60 FPS timing patch active" : "Original"));
            if (flag)
            {
                string experimentalTimingModuleStatus = Session.ExperimentalTimingModuleStatus;
                if (experimentalTimingModuleStatus != null)
                {
                    frameRateReadout.Effective.Text = experimentalTimingModuleStatus;
                }
            }
            TextBlock measured = frameRateReadout.Measured;
            double? measuredGameFps = frameRatePolicyState.MeasuredGameFps;
            string text;
            if (measuredGameFps.HasValue)
            {
                double valueOrDefault = measuredGameFps.GetValueOrDefault();
                text = UiText.Format("Measured game FPS: {0:0.0} · {1} frames over {2:0.0} ms", valueOrDefault, frameRatePolicyState.MeasuredGameFrames, frameRatePolicyState.MeasurementIntervalMs);
            }
            else
            {
                text = UiText.Get("Measured game FPS: unavailable");
            }
            measured.Text = text;
        }
    }

    private static string FrameRateTargetLabel(int target)
    {
        if (target != 60)
        {
            return UiText.Get("Original");
        }
        return "60 FPS";
    }

    private static string FrameRateSupportLabel(FrameRateCompatibilityAssessment assessment)
    {
        bool flag = assessment.Entry.Id.StartsWith("nxi-timing-", StringComparison.Ordinal);
        if (flag)
        {
            FrameRateIdentityMatch identityMatch = assessment.IdentityMatch;
            bool flag2 = (uint)(identityMatch - 2) <= 1u;
            flag = flag2;
        }
        string english;
        if (flag)
        {
            english = "Experimental timing enhancement available";
        }
        else if (assessment.Entry.Mechanism == FrameRateMechanism.Native60Baseline)
        {
            english = "Native 60 FPS · Original synchronization";
        }
        else if (assessment.CanApply)
        {
            english = "Qualified patch available for this build";
        }
        else
        {
            english = ((assessment.Entry.Status == FrameRateSupportState.ResearchOnly) ? "Source found · patch qualification incomplete" : "60 FPS patch unavailable for this build");
        }
        return UiText.Get(english);
    }

    private static string FrameRateReason(FrameRateCompatibilityAssessment assessment)
    {
        string applyBlockReason = assessment.ApplyBlockReason;
        string[] array = new string[2] { "This source targets a different executable build. ", "Only the upstream build-ID prefix matches; the complete executable and original bytes are unverified. " };
        foreach (string text in array)
        {
            if (applyBlockReason.StartsWith(text, StringComparison.Ordinal))
            {
                return UiText.Get(text) + UiText.Get(applyBlockReason.Substring(text.Length));
            }
        }
        return UiText.Get(applyBlockReason);
    }

    private static string FrameRateDetails(FrameRateCompatibilityAssessment assessment)
    {
        FrameRateCompatibilityEntry entry = assessment.Entry;
        string english = assessment.IdentityMatch switch
        {
            FrameRateIdentityMatch.ExactBuild => "Full reported build matches",
            FrameRateIdentityMatch.SourcePrefixOnly => "Only the source prefix matches",
            FrameRateIdentityMatch.Mismatch => "Different executable build",
            FrameRateIdentityMatch.TitleOnly => "Title known; full loaded build not verified",
            _ => "Build identity unavailable",
        };
        string english2 = entry.Mechanism switch
        {
            FrameRateMechanism.StaticExecutable => "Static executable patch",
            FrameRateMechanism.RuntimeDynamic => "Runtime timing adapter",
            FrameRateMechanism.Mixed => "Static patch and runtime timing adapter",
            FrameRateMechanism.GuestPlugin => "Guest plugin research lead",
            FrameRateMechanism.Native60Baseline => "Native-60 regression baseline",
            _ => "No complete patch mechanism",
        };
        return entry.GameName + "\n" + UiText.Get("Title: ") + entry.TitleId + "\n" + UiText.Format("Update: {0}", entry.DisplayVersion ?? UiText.Get("not established")) + "\n" + UiText.Get(english) + "\n" + UiText.Get(english2) + "\n" + UiText.Format("Verified full build: {0}", entry.VerifiedBuildId ?? UiText.Get("not established")) + "\n" + UiText.Format("Upstream identifier only: {0}", entry.UpstreamBuildIdPrefix ?? UiText.Get("not established")) + "\n\n" + UiText.Get(entry.Summary) + "\n\n" + FrameRateReason(assessment) + "\n\n" + UiText.Get("Original-byte evidence") + "\n" + UiText.Get(entry.ExpectedOriginalProvenance) + "\n\n" + UiText.Get("Sources and reuse observations") + "\n" + string.Join("\n\n", entry.SourceReferences.Select((FrameRateSourceReference source) => source.Url + "\n" + UiText.Get(source.LicenseObservation))) + "\n\n" + UiText.Get("Validation still required") + "\n" + string.Join("\n", entry.RequiredChecks.Select(UiText.Get));
    }

    private Button FullscreenPlayButton(GameLibraryEntry game)
    {
        Button button = VisualTheme.IconButton("monitor", UiText.Get("Play fullscreen"), () => PlayFullscreenAsync(game));
        button.Name = "PlayGameFullscreen";
        button.IsEnabled = (object)Session.SelectedBackend != null && !Session.Busy;
        return button;
    }

    private async Task PlayFullscreenAsync(GameLibraryEntry game)
    {
        bool flag = Session.HasGame && Session.ActiveGame?.EntryId != game.EntryId;
        if (flag)
        {
            flag = !(await Dialogs.ConfirmAsync(this, UiText.Get("Switch games?"), UiText.Get("The current game will close. Save your progress in the game before switching."), UiText.Get("Switch game")));
        }
        if (flag)
        {
            return;
        }
        await MainActionAsync(async () =>
        {
            if (Session.HasGame && Session.ActiveGame?.EntryId != game.EntryId)
            {
                await Session.StopAsync();
                if (Session.ErrorCode != null)
                {
                    return;
                }
            }
            await Session.SetGameFullscreenAsync(fullscreen: true);
            if (!Session.HasGame && Session.ErrorCode == null)
            {
                await Session.LaunchAsync(game.EntryId);
            }
        });
    }

    private Control BuildGpuPerformanceCard(string? entryId)
    {
        GpuPerformanceOptions gpuPerformanceOptions = ((entryId == null) ? Session.GlobalGpuPerformance : Session.GetGameGpuPerformance(entryId));
        GpuPerformanceOptions options = gpuPerformanceOptions ?? Session.GlobalGpuPerformance;
        Button button = Mode("Standard", "default", "GpuModeDefault");
        Button button2 = Mode("Low-end GPU · 0.75×", "low_end", "GpuModeLowEnd");
        Button button3 = Mode("Minimum load · 0.5×", "minimum", "GpuModeMinimum");
        ToggleSwitch compression = new ToggleSwitch
        {
            Name = "GpuTextureCompression",
            IsChecked = options.CompressTextures,
            OnContent = "On",
            OffContent = "Off",
            IsEnabled = (options.Mode != "default" && !Session.Busy)
        };
        AutomationProperties.SetName(compression, "Save VRAM with texture compression");
        compression.IsCheckedChanged += async (object? _, RoutedEventArgs _) =>
        {
            await MainActionAsync(() => Session.SetGpuPerformanceAsync(options with
            {
                CompressTextures = (compression.IsChecked == true)
            }, entryId));
        };
        StackPanel stackPanel = VisualTheme.Column(VisualTheme.LocalizedText("Low-end GPU performance", 18.0, null, FontWeight.DemiBold), VisualTheme.Text((((object)gpuPerformanceOptions == null) ? "Using core default: " : "Next launch: ") + options.Label, 14.0, VisualTheme.Blue), Actions(button, button2, button3), VisualTheme.LocalizedText("Low-end: 0.75× resolution, 2× filtering, handheld mode. Minimum: 0.5× resolution, basic filtering, handheld mode. At a 720p base these render at 540p and 360p. Actual resolution depends on the game.", 13.0, VisualTheme.Muted), VisualTheme.LocalizedText("Both keep shader and CPU caches on, use optimized graphics pipelines and let adaptive resolution see real GPU timings. First-time shader compilation can take longer; repeated runs reuse the cache. Original game speed and effects are preserved.", 13.0, VisualTheme.Muted), VisualTheme.Column(VisualTheme.LocalizedText("Save VRAM with texture compression", 15.0, null, FontWeight.Medium), compression, VisualTheme.LocalizedText("Optional for cards short on memory. Compresses unsupported texture formats; can reduce image quality and add CPU work. Leave off unless memory pressure is causing trouble.", 13.0, VisualTheme.Muted)), VisualTheme.LocalizedText("Applies on the next launch. Individual game settings override the preset. Standard restores core preferences and title defaults. A supported Vulkan driver is required; this cannot remove CPU limits or guarantee 60 FPS.", 13.0, VisualTheme.Muted));
        if (entryId != null)
        {
            Button button4 = VisualTheme.LocalizedButton("Use core performance mode", () => MainActionAsync(() => Session.SetGpuPerformanceAsync(null, entryId)));
            button4.Name = "GpuModeInherit";
            button4.IsEnabled = !Session.Busy && (object)gpuPerformanceOptions != null;
            stackPanel.Children.Add(button4);
        }
        if (Session.HasGame)
        {
            string appliedGpuPerformance = Session.AppliedGpuPerformance;
            if (appliedGpuPerformance != null)
            {
                stackPanel.Children.Add(VisualTheme.Text("Current game: " + appliedGpuPerformance, 13.0, VisualTheme.Muted));
            }
        }
        Border border = Card(stackPanel, accent: true);
        border.Name = "GpuPerformanceCard";
        return border;
        Button Mode(string label, string mode, string name)
        {
            Button button5 = VisualTheme.Button(label, () => MainActionAsync(() => Session.SetGpuPerformanceAsync(new GpuPerformanceOptions(mode, mode != "default" && options.CompressTextures), entryId)), (options.Mode == mode) ? "selected" : "subtle");
            button5.Name = name;
            button5.IsEnabled = !Session.Busy;
            return button5;
        }
    }

    private Control InitializeHomeSurface(Control desktop)
    {
        _desktopSurface = desktop;
        _homeSelectedEntry = _homePreferences.SelectedEntryId;
        _homeFilter = _homePreferences.Filter;
        _homeMode = Program.Arguments.Contains("--home", StringComparer.Ordinal) || (_homePreferences.StartInHome && !Program.Arguments.Contains("--desktop", StringComparer.Ordinal));
        _desktopDecorations = WindowDecorations;
        _homePreferenceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250L)
        };
        _homePreferenceTimer.Tick += (object? _, EventArgs _) =>
        {
            _homePreferenceTimer.Stop();
            SaveHomePreferences();
        };
        _homeSurface.Styles.Add(HomePalette.Styles());
        RequestedThemeVariant = ((_homeMode ? (!HomePalette.Palette.Night) : (!_dune.Night)) ? ThemeVariant.Light : ThemeVariant.Dark);
        desktop.IsVisible = !_homeMode;
        _homeSurface.IsVisible = _homeMode;
        return new Grid
        {
            Children =
            {
                desktop,
                (Control)_homeSurface,
                BuildBootOverlay(),
                BuildStartupSequence()
            }
        };
    }

    private void SaveHomePreferences()
    {
        _homePreferences = _homePreferences with
        {
            SelectedEntryId = _homeSelectedEntry,
            Filter = _homeFilter
        };
        HomePreferenceSaved = _homePreferenceStore?.Write(_homePreferences) ?? false;
    }

    private void QueueHomePreferenceSave()
    {
        _homePreferenceTimer?.Stop();
        _homePreferenceTimer?.Start();
    }

    public async Task SetHomeModeAsync(bool enabled)
    {
        if (Session.Busy || Session.GameWindowClosing)
        {
            return;
        }
        if (Session.State == RuntimeState.Running)
        {
            await Session.PauseAsync();
        }
        if (Session.State == RuntimeState.Running || Session.GuestOwnsInput)
        {
            return;
        }
        if (enabled && !_homeMode)
        {
            _desktopWindowState = WindowState;
            _desktopDecorations = WindowDecorations;
        }
        _homeMode = enabled;
        _homeDetails = false;
        RequestedThemeVariant = ((enabled ? (!HomePalette.Palette.Night) : (!_dune.Night)) ? ThemeVariant.Light : ThemeVariant.Dark);
        if (enabled)
        {
            if (_homeSelectedEntry == null)
            {
                _homeSelectedEntry = _selectedEntry ?? Session.ActiveGame?.EntryId;
            }
            _section = "Library";
            _selectedEntry = null;
            WindowDecorations = WindowDecorations.None;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _desktopWindowState;
            WindowDecorations = _desktopDecorations;
            _section = "Library";
            _selectedEntry = null;
            UpdateShellLayout();
        }
        SaveHomePreferences();
        RenderCurrentPage();
        Show();
        Activate();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpdateLayout();
            Navigation.FocusFirst(this);
        }, DispatcherPriority.Background);
    }

    private void OpenHomeAtStartup()
    {
        if (_homeMode)
        {
            _desktopWindowState = WindowState;
            WindowDecorations = WindowDecorations.None;
            WindowState = WindowState.FullScreen;
            RenderCurrentPage();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                UpdateLayout();
                FocusHomeSelection();
            }, DispatcherPriority.Loaded);
        }
    }

    private Button HomeButton(string name, string label, Func<Task> action, string group, string? key = null)
    {
        Button button = VisualTheme.Button(label, () =>
        {
            bool flag;
            switch (name)
            {
                case "HomeBack":
                case "HomeDesktop":
                case "HomeCancel":
                    flag = true;
                    break;
                default:
                    flag = false;
                    break;
            }
            PlayHomeSound((!flag) ? HomeSound.Confirm : HomeSound.Back);
            return action();
        });
        button.Name = name;
        button.Classes.Add("home-control");
        button.Padding = new Thickness(14.0, 8.0);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        RegisterHomeTarget(button, group, key ?? name);
        return button;
    }

    private Button HomeIconButton(string name, string label, string icon, Func<Task> action, string group, double size)
    {
        Button button = HomeButton(name, label, action, group);
        double width = (button.Height = Math.Max(44.0, size));
        button.Width = width;
        button.Padding = new Thickness(10.0);
        button.Content = new NavGlyph
        {
            Kind = icon,
            Stroke = HomePalette.Ink,
            Width = size * 0.43,
            Height = size * 0.43
        };
        return button;
    }

    private Control BuildHomeMode(Control? pageContent)
    {
        _homeTargets.Clear();
        _homeCards.Clear();
        _homeRail = null;
        _homePageContent = null;
        _homePlay = (_homeStop = (_homeOptions = (_homeCancel = null)));
        _homePosition = (_homePlayLabel = null);
        double num = ((Bounds.Height > 0.0) ? Bounds.Height : Height);
        double width = ((Bounds.Width > 0.0) ? Bounds.Width : Width);
        _homeLayoutSize = new Size(width, num);
        double num2 = Math.Clamp(num / 720.0, 0.75, 1.75);
        Grid grid = new Grid
        {
            Name = "HomeCanvas",
            ClipToBounds = true
        };
        grid.Children.Add(new HomeAmbientSurface(HomePalette)
        {
            IsHitTestVisible = false
        });
        Grid grid2 = new Grid();
        grid2.Margin = new Thickness(24.0 * num2, 20.0 * num2);
        grid2.RowDefinitions = new RowDefinitions(IsHomeLibrary ? FormattableString.Invariant($"{Math.Max(64.0, 80.0 * num2)},{72.0 * num2},*,{112.0 * num2},{Math.Max(44.0, 52.0 * num2)}") : FormattableString.Invariant($"{Math.Max(64.0, 80.0 * num2)},*,{Math.Max(44.0, 52.0 * num2)}"));
        Grid grid3 = grid2;
        grid3.Children.Add(BuildHomeHeader(num2, width));
        if (IsHomeLibrary)
        {
            Control control = BuildHomeFilters(num2);
            Grid.SetRow(control, 1);
            grid3.Children.Add(control);
            Control control2 = BuildHomeLibrary(num2);
            Grid.SetRow(control2, 2);
            grid3.Children.Add(control2);
            Control control3 = BuildHomeDock(num2);
            Grid.SetRow(control3, 3);
            grid3.Children.Add(control3);
            Control control4 = BuildHomeFooter(num2);
            Grid.SetRow(control4, 4);
            grid3.Children.Add(control4);
        }
        else
        {
            Border border = (Border)(_homePageContent = new Border
            {
                Padding = new Thickness(8.0 * num2, 14.0 * num2, 8.0 * num2, 12.0 * num2),
                Child = ((pageContent == null) ? null : HomePalette.Adapt(pageContent))
            });
            Grid.SetRow(border, 1);
            grid3.Children.Add(border);
            Control control5 = BuildHomeFooter(num2);
            Grid.SetRow(control5, 2);
            grid3.Children.Add(control5);
        }
        grid.Children.Add(grid3);
        UpdateHomeReadouts();
        return grid;
    }

    private Control BuildHomeHeader(double scale, double width)
    {
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 16.0
        };
        Button button = HomeButton("HomeIdentity", UiText.Get("HOME options"), ShowHomeOptionsAsync, "top");
        button.Background = Brushes.Transparent;
        button.Padding = new Thickness(2.0);
        button.MinHeight = Math.Max(44.0, 54.0 * scale);
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 1.0,
            VerticalAlignment = VerticalAlignment.Center
        };
        stackPanel.Children.Add(HomePalette.Text(UiText.Get("NX Infinity"), Math.Max(15.0, 19.0 * scale), null, FontWeight.DemiBold));
        Controls children = stackPanel.Children;
        HomeTheme homePalette = HomePalette;
        string text;
        if (IsHomeLibrary)
        {
            text = UiText.Get("HOME");
        }
        else if (_homeDetails)
        {
            text = UiText.Get("GAME DETAILS");
        }
        else
        {
            text = ((_section == "Enhancements") ? UiText.Get("IMPROVEMENTS") : UiText.Get(_section).ToUpperInvariant());
        }
        children.Add(homePalette.Text(text, Math.Max(10.0, 10.0 * scale), HomePalette.Muted, FontWeight.Medium));
        button.Content = VisualTheme.Row(new BrandMark
        {
            Width = 44.0 * scale,
            Height = 44.0 * scale
        }, stackPanel);
        Button button2 = HomeIconButton("HomeAddGame", UiText.Get("Game folders"), "plus", AddGamesAsync, "top", 44.0 * scale);
        StackPanel stackPanel2 = VisualTheme.Row(button, button2);
        stackPanel2.Spacing = 16.0 * scale;
        stackPanel2.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(stackPanel2);
        _homeClock = HomePalette.Text(DateTime.Now.ToString("h:mm tt"), Math.Max(12.0, 14.0 * scale), null, FontWeight.Medium);
        _homeControllerLabel = HomePalette.Text("", Math.Max(12.0, 13.0 * scale), HomePalette.Muted);
        Button button3 = HomeButton("HomeControllerStatus", UiText.Get("Choose the gameplay controller"), ChooseGameControllerAsync, "top");
        button3.Background = Brushes.Transparent;
        button3.Padding = new Thickness(10.0, 8.0);
        button3.Content = VisualTheme.Row(new NavGlyph
        {
            Kind = "gamepad-2",
            Stroke = HomePalette.Ink,
            Width = 21.0 * scale,
            Height = 21.0 * scale
        }, _homeControllerLabel);
        Button button4 = HomeIconButton("HomeDesktop", UiText.Get("Return to desktop mode"), "monitor", () => SetHomeModeAsync(enabled: false), "top", 48.0 * scale);
        Button button5 = HomeIconButton("HomeSessionMenu", UiText.Get("Session and application options"), "power", ShowHomeOptionsAsync, "top", 48.0 * scale);
        button5.Background = HomePalette.Dock;
        if (button5.Content is NavGlyph navGlyph)
        {
            navGlyph.Stroke = Brushes.White;
        }
        StackPanel stackPanel3 = VisualTheme.Row(BuildAvatarButton(home: true, scale), _homeClock, button3, button4, button5);
        stackPanel3.Spacing = 10.0 * scale;
        stackPanel3.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(stackPanel3, 1);
        grid.Children.Add(stackPanel3);
        _homeControllerLabel.IsVisible = width >= 1000.0;
        return grid;
    }

    private Control BuildHomeFilters(double scale)
    {
        StackPanel stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12.0 * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Button item = HomeIconButton("HomeLibraryFilter", UiText.Get("Library view options"), "sliders-horizontal", ShowHomeLibraryOptionsAsync, "filters", 44.0 * scale);
        stackPanel.Children.Add(item);
        StackPanel stackPanel2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2.0
        };
        (string, string, string)[] array = new (string, string, string)[3]
        {
            ("all", UiText.Get("All games"), "All"),
            ("favorites", UiText.Get("Favorites"), "Favorites"),
            ("recent", UiText.Get("Recent"), "Recent")
        };
        for (int i = 0; i < array.Length; i++)
        {
            (string, string, string) tuple = array[i];
            string id = tuple.Item1;
            string item2 = tuple.Item2;
            string item3 = tuple.Item3;
            Button button = HomeButton("HomeFilter" + item3, item2, () => SetHomeFilterAsync(id), "filters");
            button.Classes.Set("home-filter-selected", _homeFilter == id);
            button.Padding = new Thickness(18.0 * scale, 7.0 * scale);
            button.MinHeight = Math.Max(44.0, 44.0 * scale);
            button.Content = HomePalette.Text(item2, Math.Max(13.0, 14.0 * scale), (_homeFilter == id) ? HomePalette.AccentInk : HomePalette.Ink, FontWeight.Medium);
            stackPanel2.Children.Add(button);
        }
        stackPanel.Children.Add(new Border
        {
            Background = HomePalette.Surface,
            CornerRadius = new CornerRadius(36.0),
            Padding = new Thickness(4.0),
            Child = stackPanel2
        });
        Button item4 = HomeIconButton("HomeSearch", string.IsNullOrEmpty(_homeSearch) ? UiText.Get("Search your games") : (UiText.Get("Edit search: ") + _homeSearch), "search", SearchHomeAsync, "filters", 44.0 * scale);
        stackPanel.Children.Add(item4);
        return stackPanel;
    }

    private Control BuildHomeLibrary(double scale)
    {
        IEnumerable<GameLibraryEntry> source = Session.Library.List(new LibraryQuery(null, FavoritesOnly: false, LibraryContentKind.Game, LibrarySort.Name, 0, 2000)).Where(LibraryContentClassification.IsAvailableGame);
        if (_homeFilter == "favorites")
        {
            source = source.Where((GameLibraryEntry g) => g.Favorite);
        }
        source = ((!(_homeFilter == "recent")) ? source.OrderBy(GameTitle, StringComparer.CurrentCultureIgnoreCase) : (from g in source
                                                                                                                       where g.Launches.Count > 0
                                                                                                                       orderby g.Launches.Last().StartedUtc descending
                                                                                                                       select g));
        if (!string.IsNullOrWhiteSpace(_homeSearch))
        {
            source = source.Where((GameLibraryEntry g) => GameTitle(g).Contains(_homeSearch, StringComparison.CurrentCultureIgnoreCase));
        }
        _homeGames = source.ToArray();
        if (!_homeGames.Any((GameLibraryEntry g) => g.EntryId == _homeSelectedEntry))
        {
            _homeSelectedEntry = _homeGames.FirstOrDefault((GameLibraryEntry g) => g.EntryId == Session.ActiveGame?.EntryId)?.EntryId ?? _homeGames.FirstOrDefault()?.EntryId;
        }
        if (_homeGames.Length == 0)
        {
            StackPanel stackPanel = new StackPanel
            {
                Spacing = 16.0 * scale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 510.0,
                Children = { (Control)new NavGlyph
                {
                    Kind = "Library",
                    Stroke = HomePalette.Focus,
                    Width = 46.0 * scale,
                    Height = 46.0 * scale,
                    HorizontalAlignment = HorizontalAlignment.Center
                } }
            };
            TextBlock textBlock = HomePalette.Text((_homeFilter != "all" || _homeSearch.Length != 0) ? UiText.Get("No games in this view") : UiText.Get("No games yet"), Math.Max(22.0, 29.0 * scale), null, FontWeight.DemiBold);
            textBlock.HorizontalAlignment = HorizontalAlignment.Center;
            stackPanel.Children.Add(textBlock);
            TextBlock textBlock2 = HomePalette.Text((_homeFilter != "all" || _homeSearch.Length != 0) ? UiText.Get("Return to all games or change your search.") : UiText.Get("Add a game folder to start your library."), Math.Max(14.0, 15.0 * scale), HomePalette.Muted);
            textBlock2.HorizontalAlignment = HorizontalAlignment.Center;
            stackPanel.Children.Add(textBlock2);
            Button button = HomeButton("HomeEmptyAction", (_homeFilter != "all" || _homeSearch.Length != 0) ? UiText.Get("Show all games") : UiText.Get("Add game folder"), (_homeFilter != "all" || _homeSearch.Length != 0) ? ((Func<Task>)(() =>
            {
                _homeSearch = "";
                return SetHomeFilterAsync("all");
            })) : new Func<Task>(AddGamesAsync), "games");
            button.Background = HomePalette.Focus;
            button.Foreground = HomePalette.AccentInk;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            stackPanel.Children.Add(button);
            return stackPanel;
        }
        int count = Math.Clamp(Array.FindIndex(_homeGames, (GameLibraryEntry gameLibraryEntry) => gameLibraryEntry.EntryId == _homeSelectedEntry) - 10, 0, Math.Max(0, _homeGames.Length - 24));
        double num = Math.Floor(244.0 * scale);
        double width = num + 12.0 * scale;
        StackPanel stackPanel2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20.0 * scale,
            Margin = new Thickness(10.0 * scale, 12.0 * scale),
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (GameLibraryEntry game in _homeGames.Skip(count).Take(24))
        {
            Control control = Artwork(game, num);
            if (control is Border border)
            {
                border.CornerRadius = new CornerRadius(9.0 * scale, 9.0 * scale, 0.0, 0.0);
            }
            TextBlock textBlock3 = HomePalette.Text(GameTitle(game), Math.Max(14.0, 16.0 * scale), null, FontWeight.DemiBold);
            textBlock3.TextWrapping = TextWrapping.Wrap;
            textBlock3.MaxLines = 2;
            textBlock3.LineHeight = Math.Max(17.0, 20.0 * scale);
            textBlock3.Height = Math.Max(34.0, 40.0 * scale);
            textBlock3.VerticalAlignment = VerticalAlignment.Top;
            string text = ((Session.ActiveGame?.EntryId == game.EntryId) ? UiText.Get((Session.State == RuntimeState.Paused) ? "Paused · ready to resume" : "Playing") : (CatalogSelection(game)?.Game.Publisher ?? UiText.Get(game.Favorite ? "Favorite" : "Local game")));
            StackPanel stackPanel3 = new StackPanel
            {
                Spacing = 3.0 * scale,
                Margin = new Thickness(12.0 * scale, 10.0 * scale, 12.0 * scale, 10.0 * scale)
            };
            stackPanel3.Children.Add(textBlock3);
            stackPanel3.Children.Add(HomePalette.Text(text, Math.Max(11.0, 12.0 * scale), HomePalette.Muted));
            StackPanel child = new StackPanel
            {
                Spacing = 0.0,
                Children =
                {
                    control,
                    (Control)stackPanel3
                }
            };
            Border border2 = new Border
            {
                Name = "HomeCardSurface",
                Background = HomePalette.Surface,
                CornerRadius = new CornerRadius(12.0 * scale),
                BoxShadow = HomePalette.CardShadow,
                ClipToBounds = true,
                Child = child
            };
            Button button2 = HomeButton("HomeGameCard", GameTitle(game) + UiText.Get(", play or resume"), () => PlayHomeGameAsync(game.EntryId), "games", "game:" + game.EntryId);
            button2.Tag = game.EntryId;
            button2.Width = width;
            button2.Padding = new Thickness(3.0 * scale);
            button2.BorderThickness = new Thickness(3.0 * scale);
            button2.CornerRadius = new CornerRadius(16.0 * scale);
            button2.Background = Brushes.Transparent;
            button2.Content = border2;
            button2.GotFocus += (object? _, FocusChangedEventArgs _) =>
            {
                SelectHomeGame(game.EntryId);
            };
            button2.PointerPressed += (object? _, PointerPressedEventArgs _) =>
            {
                SelectHomeGame(game.EntryId);
            };
            stackPanel2.Children.Add(button2);
            _homeCards[game.EntryId] = (button2, border2);
        }
        _homeRail = new ScrollViewer
        {
            Name = "HomeCoverRail",
            Content = stackPanel2,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _homeRail.PointerWheelChanged += (object? _, PointerWheelEventArgs e) =>
        {
            double num2 = ((Math.Abs(e.Delta.X) > 0.01) ? e.Delta.X : e.Delta.Y);
            if (!(Math.Abs(num2) < 0.01))
            {
                MoveHomeGame((!(num2 > 0.0)) ? 1 : (-1));
                e.Handled = true;
            }
        };
        SelectHomeGame(_homeSelectedEntry, persist: false);
        return _homeRail;
    }

    private Control BuildHomeDock(double scale)
    {
        StackPanel stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18.0 * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        (string, string, string, Func<Task>)[] array = new (string, string, string, Func<Task>)[7]
        {
            ("HomeCore", UiText.Get("Core"), "monitor", ChooseBackendAsync),
            ("HomeOrbit", "Orbit", "gamepad-2", () => NavigateAsync("Orbit")),
            ("HomeContent", UiText.Get("Content"), "folder-open", () => NavigateAsync("Content")),
            ("HomeSaves", UiText.Get("Saves"), "archive", () => NavigateAsync("Saves")),
            ("HomePerformance", UiText.Get("Performance"), "activity", () => NavigateAsync("Performance")),
            ("HomeControls", UiText.Get("Controls"), "gamepad-2", ChooseGameControllerAsync),
            ("HomeGraphics", UiText.Get("Graphics"), "settings", OpenHomeGraphicsAsync)
        };
        for (int num = 0; num < array.Length; num++)
        {
            (string, string, string, Func<Task>) tuple = array[num];
            string item = tuple.Item1;
            string item2 = tuple.Item2;
            string item3 = tuple.Item3;
            Func<Task> item4 = tuple.Item4;
            double num2 = Math.Max(48.0, 64.0 * scale);
            Button button = HomeIconButton(item, item2, item3, item4, "dock", num2);
            Button button2 = button;
            IBrush background;
            if (item == "HomeCore")
            {
                background = HomePalette.Red;
            }
            else
            {
                background = ((item == "HomeGraphics") ? HomePalette.Focus : HomePalette.Dock);
            }
            button2.Background = background;
            button.CornerRadius = new CornerRadius(num2);
            button.BorderThickness = new Thickness(3.0);
            button.BorderBrush = Brushes.White;
            if (item == "HomeCore")
            {
                button.Content = new BrandMark
                {
                    Width = num2 * 0.69,
                    Height = num2 * 0.69
                };
            }
            else if (button.Content is NavGlyph navGlyph)
            {
                IBrush stroke;
                if (!(item == "HomeGraphics"))
                {
                    IBrush white = Brushes.White;
                    stroke = white;
                }
                else
                {
                    stroke = HomePalette.AccentInk;
                }
                navGlyph.Stroke = stroke;
            }
            Border ring = new Border
            {
                CornerRadius = new CornerRadius(num2),
                BorderThickness = new Thickness(3.0),
                Padding = new Thickness(2.0),
                Child = button,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            button.GotFocus += (object? _, FocusChangedEventArgs _) =>
            {
                ring.BorderBrush = HomePalette.Focus;
                ring.BoxShadow = HomePalette.FocusShadow;
            };
            button.LostFocus += (object? _, FocusChangedEventArgs _) =>
            {
                ring.BorderBrush = Brushes.Transparent;
                ring.BoxShadow = default;
            };
            TextBlock textBlock = HomePalette.Text(item2, Math.Max(11.0, 12.0 * scale), HomePalette.Muted, FontWeight.Medium);
            textBlock.HorizontalAlignment = HorizontalAlignment.Center;
            StackPanel item5 = new StackPanel
            {
                Width = Math.Max(78.0, 88.0 * scale),
                Spacing = 3.0 * scale,
                Children =
                {
                    (Control)ring,
                    (Control)textBlock
                }
            };
            stackPanel.Children.Add(item5);
        }
        return stackPanel;
    }

    private Button HomeHint(string name, string key, string label, Func<Task> action, double scale)
    {
        Button button = HomeButton(name, label, action, "footer");
        button.Padding = new Thickness(10.0 * scale, 5.0 * scale);
        Border border = new Border
        {
            CornerRadius = new CornerRadius(20.0),
            Background = HomePalette.Dock,
            Width = Math.Max(22.0, 24.0 * scale),
            Height = Math.Max(22.0, 24.0 * scale),
            Child = new TextBlock
            {
                Text = key,
                Foreground = Brushes.White,
                FontSize = Math.Max(11.0, 12.0 * scale),
                FontWeight = FontWeight.DemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        TextBlock textBlock = HomePalette.Text(label, Math.Max(12.0, 13.0 * scale), null, FontWeight.Medium);
        if (name == "HomePlay")
        {
            _homePlayLabel = textBlock;
        }
        button.Content = VisualTheme.Row(border, textBlock);
        return button;
    }

    private Control BuildHomeFooter(double scale)
    {
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            ColumnSpacing = 8.0
        };
        _homePosition = HomePalette.Text("", Math.Max(10.0, 11.0 * scale), HomePalette.Muted);
        _homePosition.IsVisible = _homeLayoutSize.Width >= 1040.0;
        grid.Children.Add(_homePosition);
        StackPanel stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8.0 * scale,
            VerticalAlignment = VerticalAlignment.Center
        };
        _homeOptions = HomeHint("HomeGameOptions", "+", UiText.Get("Options"), ShowHomeGameOptionsAsync, scale);
        stackPanel.Children.Add(_homeOptions);
        stackPanel.Children.Add(HomeHint("HomeBack", "B", IsHomeLibrary ? UiText.Get("Desktop") : UiText.Get("HOME"), IsHomeLibrary ? ((Func<Task>)(() => SetHomeModeAsync(enabled: false))) : new Func<Task>(ReturnHomeAsync), scale));
        _homeStop = HomeHint("HomeStopGame", "X", UiText.Get("Stop game"), () => MainActionAsync(Session.StopAsync), scale);
        stackPanel.Children.Add(_homeStop);
        _homePlay = HomeHint("HomePlay", "A", UiText.Get("Play"), () => PlayHomeGameAsync(_homeSelectedEntry), scale);
        stackPanel.Children.Add(_homePlay);
        Grid.SetColumn(stackPanel, 1);
        grid.Children.Add(stackPanel);
        _homeStatus = HomePalette.Text("", Math.Max(10.0, 11.0 * scale), HomePalette.Muted);
        _homeStatus.HorizontalAlignment = HorizontalAlignment.Right;
        _homeStatus.IsVisible = _homeLayoutSize.Width >= 1160.0;
        Grid.SetColumn(_homeStatus, 2);
        grid.Children.Add(_homeStatus);
        _homeCancel = HomeHint("HomeCancel", "B", UiText.Get("Cancel"), () =>
        {
            Session.Cancel();
            return Task.CompletedTask;
        }, scale);
        _homeCancel.IsVisible = false;
        Grid.SetColumn(_homeCancel, 2);
        grid.Children.Add(_homeCancel);
        return grid;
    }

    private void UpdateHomeReadouts()
    {
        if (!_homeMode)
        {
            return;
        }
        if (_homeClock != null)
        {
            _homeClock.Text = DateTime.Now.ToString("h:mm tt");
        }
        if (_homeControllerLabel != null)
        {
            _homeControllerLabel.Text = ((_hostDevices.Count == 0) ? UiText.Get("No controller") : UiText.Format("{0} connected", _hostDevices.Count));
        }
        if (_homeStatus != null)
        {
            TextBlock? homeStatus = _homeStatus;
            string text;
            if (!Session.Busy)
            {
                text = "";
            }
            else
            {
                double? progress = Session.Progress;
                if (progress.HasValue)
                {
                    double valueOrDefault = progress.GetValueOrDefault();
                    text = $"{UiText.Get(Session.Status)} · {valueOrDefault:P0}";
                }
                else
                {
                    text = UiText.Get(Session.Status);
                }
            }
            homeStatus.Text = text;
        }
        bool flag = !Session.Busy && !Session.GameWindowClosing;
        if (_homePageContent != null)
        {
            _homePageContent.IsEnabled = flag;
        }
        if (_homePlay != null)
        {
            _homePlay.IsEnabled = flag && _homeSelectedEntry != null;
        }
        if (_homeOptions != null)
        {
            _homeOptions.IsEnabled = flag && _homeSelectedEntry != null;
        }
        if (_homeStop != null)
        {
            _homeStop.IsVisible = Session.HasGame;
            _homeStop.IsEnabled = flag;
        }
        if (_homePlayLabel != null)
        {
            TextBlock? homePlayLabel = _homePlayLabel;
            string english;
            if (Session.ActiveGame?.EntryId == _homeSelectedEntry && _homeSelectedEntry != null)
            {
                english = "Resume";
            }
            else
            {
                english = (((object)Session.SelectedBackend == null && _homeSelectedEntry != null) ? "Core unavailable" : "Play");
            }
            homePlayLabel.Text = UiText.Get(english);
        }
        if (_homeCancel != null)
        {
            _homeCancel.IsVisible = Session.Busy && !Session.GameWindowClosing;
        }
        if (_homePosition != null)
        {
            int num = Array.FindIndex(_homeGames, (GameLibraryEntry game) => game.EntryId == _homeSelectedEntry);
            _homePosition.Text = ((num >= 0) ? $"{num + 1} / {_homeGames.Length}" : "NX INFINITY");
        }
        foreach (HomeFocusTarget value in _homeTargets.Values)
        {
            if (value.Button != _homeCancel && value.Button != _homePlay && value.Button != _homeOptions && value.Button != _homeStop)
            {
                value.Button.IsEnabled = flag;
            }
        }
    }

    private async Task PlayHomeGameAsync(string? entryId)
    {
        if (entryId == null || Session.Busy || Session.GameWindowClosing)
        {
            return;
        }
        SelectHomeGame(entryId);
        SaveHomePreferences();
        if ((object)Session.SelectedBackend == null)
        {
            await ChooseBackendAsync();
            if ((object)Session.SelectedBackend == null)
            {
                return;
            }
        }
        bool flag = Session.HasGame && Session.ActiveGame?.EntryId != entryId;
        if (flag)
        {
            flag = !(await Dialogs.ConfirmAsync(this, UiText.Get("Switch games?"), UiText.Get("The current game will close. Save your progress in the game before switching."), UiText.Get("Switch game")));
        }
        if (!flag)
        {
            await MainActionAsync(() => (!(Session.ActiveGame?.EntryId == entryId)) ? Session.LaunchAsync(entryId) : Session.ResumeAsync());
        }
    }

    private Task ReturnHomeAsync()
    {
        _section = "Library";
        _homeDetails = false;
        _selectedEntry = null;
        _navigationGeneration++;
        RenderCurrentPage();
        Avalonia.Threading.Dispatcher.UIThread.Post(FocusHomeSelection, DispatcherPriority.Loaded);
        return Task.CompletedTask;
    }

    private async Task OpenHomeGraphicsAsync()
    {
        _selectedEntry = _homeSelectedEntry;
        _settingsForGame = _selectedEntry != null;
        await NavigateAsync("Settings");
    }

    private async Task SearchHomeAsync()
    {
        string text = await Dialogs.TextAsync(this, UiText.Get("Search your games"), _homeSearch, 200);
        if (text != null)
        {
            _homeSearch = text.Trim();
            await ReturnHomeAsync();
        }
    }

    private Task SetHomeFilterAsync(string value)
    {
        bool flag;
        switch (value)
        {
            case "all":
            case "favorites":
            case "recent":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (!flag)
        {
            return Task.CompletedTask;
        }
        _homeFilter = value;
        QueueHomePreferenceSave();
        return ReturnHomeAsync();
    }

    private async Task ShowHomeLibraryOptionsAsync()
    {
        string text = await Dialogs.ChooseAsync(this, UiText.Get("Your library"),
        [
            ("all", UiText.Get("All games")),
            ("favorites", UiText.Get("Favorites")),
            ("recent", UiText.Get("Recently played")),
            ("clear", UiText.Get("Clear search"))
        ], UiText.Get("LB / RB change views. Use the mouse wheel or left / right to browse covers."));
        if (text == "clear")
        {
            _homeSearch = "";
            await ReturnHomeAsync();
        }
        else if (text != null)
        {
            await SetHomeFilterAsync(text);
        }
    }

    private async Task ShowHomeGameOptionsAsync()
    {
        GameLibraryEntry game = _homeGames.FirstOrDefault((GameLibraryEntry g) => g.EntryId == _homeSelectedEntry);
        if ((object)game == null && _homeSelectedEntry != null)
        {
            game = Session.Library.Get(_homeSelectedEntry);
        }
        if ((object)game == null)
        {
            return;
        }
        string text = await Dialogs.ChooseAsync(this, GameTitle(game),
        [
            ("play", (Session.ActiveGame?.EntryId == game.EntryId) ? "Resume game" : "Play game"),
            ("fullscreen", UiText.Get("Play fullscreen")),
            ("graphics", UiText.Get("Graphics & display")),
            ("controls", UiText.Get("Controls")),
            ("favorite", game.Favorite ? "Remove favorite" : "Add favorite"),
            ("cover", UiText.Get("Cover & metadata")),
            ("details", UiText.Get("Game details")),
            ("saves", UiText.Get("Saves"))
        ]);
        if (text == null)
        {
            return;
        }
        switch (text.Length)
        {
            case 8:
                switch (text[0])
                {
                    case 'g':
                        if (text == "graphics")
                        {
                            await OpenHomeGraphicsAsync();
                        }
                        break;
                    case 'c':
                        if (text == "controls")
                        {
                            await ChooseGameControllerAsync();
                        }
                        break;
                    case 'f':
                        if (text == "favorite")
                        {
                            await Session.RunAsync("Updating favorite", (CancellationToken _) =>
                            {
                                Session.Library.SetFavorite(game.EntryId, !game.Favorite);
                                return Task.CompletedTask;
                            });
                            await ReturnHomeAsync();
                        }
                        break;
                    case 'd':
                    case 'e':
                        break;
                }
                break;
            case 5:
                switch (text[0])
                {
                    case 'c':
                        if (text == "cover")
                        {
                            await EditMetadataAsync(game);
                        }
                        break;
                    case 's':
                        if (text == "saves")
                        {
                            _selectedEntry = game.EntryId;
                            await NavigateAsync("Saves");
                        }
                        break;
                }
                break;
            case 4:
                if (text == "play")
                {
                    await PlayHomeGameAsync(game.EntryId);
                }
                break;
            case 10:
                if (text == "fullscreen")
                {
                    await PlayFullscreenAsync(game);
                }
                break;
            case 7:
                if (text == "details")
                {
                    _homeDetails = true;
                    _selectedEntry = game.EntryId;
                    RenderCurrentPage();
                    Navigation.FocusFirst(this);
                }
                break;
            case 6:
            case 9:
                break;
        }
    }

    private async Task ShowHomeOptionsAsync()
    {
        List<(string, string)> list = new List<(string, string)>();
        if (Session.HasGame)
        {
            list.Add(("resume", UiText.Get("Resume game")));
        }
        list.Add(("display", UiText.Get("Graphics & game display")));
        list.Add(("experience", UiText.Get("HOME audio & startup")));
        list.Add(("themes", "Themes"));
        list.Add(("language", UiText.Get("Launcher language")));
        list.Add(("startup", UiText.Get(_homePreferences.StartInHome ? "Start next time in desktop mode" : "Start next time in HOME mode")));
        list.Add(("desktop", UiText.Get("Return to desktop mode")));
        list.Add(("exit", UiText.Get("Close NX Infinity")));
        string text = await Dialogs.ChooseAsync(this, UiText.Get("HOME options"), list, UiText.Get("HOME controls the launcher. Game fullscreen and resolution are available in Graphics & display."));
        if (text == null)
        {
            return;
        }
        switch (text.Length)
        {
            case 6:
                switch (text[0])
                {
                    case 't':
                        if (text == "themes")
                        {
                            await ShowThemesAsync();
                        }
                        break;
                    case 'r':
                        if (text == "resume")
                        {
                            await MainActionAsync(Session.ResumeAsync);
                        }
                        break;
                }
                break;
            case 7:
                switch (text[1])
                {
                    case 'i':
                        if (text == "display")
                        {
                            await OpenHomeGraphicsAsync();
                        }
                        break;
                    case 't':
                        if (text == "startup")
                        {
                            _homePreferences = _homePreferences with
                            {
                                StartInHome = !_homePreferences.StartInHome
                            };
                            SaveHomePreferences();
                        }
                        break;
                    case 'e':
                        if (text == "desktop")
                        {
                            await SetHomeModeAsync(enabled: false);
                        }
                        break;
                }
                break;
            case 8:
                if (text == "language")
                {
                    await ChooseLauncherLanguageAsync();
                }
                break;
            case 10:
                if (text == "experience")
                {
                    await ShowHomeExperienceAsync();
                }
                break;
            case 4:
                if (text == "exit")
                {
                    Close();
                }
                break;
            case 5:
            case 9:
                break;
        }
    }

    internal async Task ControllerHomeAsync()
    {
        if (_startupSequence?.IsVisible ?? false)
        {
            _startupSequence.Stop();
        }
        else if (!Session.Busy)
        {
            Window currentWindow = Navigation.CurrentWindow;
            if (currentWindow != null && currentWindow.IsVisible && currentWindow != this)
            {
                currentWindow.Close();
            }
            if (Session.State == RuntimeState.Running || !_homeMode)
            {
                await SetHomeModeAsync(enabled: true);
            }
            else if (!IsHomeLibrary || Session.State != RuntimeState.Paused)
            {
                await ReturnHomeAsync();
            }
            else
            {
                await MainActionAsync(Session.ResumeAsync);
            }
        }
    }

    private void RegisterHomeTarget(Button button, string group, string key)
    {
        _homeTargets[key] = new HomeFocusTarget(button, group, key);
        button.GotFocus += (object? _, FocusChangedEventArgs _) =>
        {
            _homeFocusKey = key;
            PlayHomeSound(HomeSound.Navigate);
        };
    }

    private void SelectHomeGame(string entryId, bool persist = true)
    {
        bool flag = _homeSelectedEntry != entryId;
        _homeSelectedEntry = entryId;
        foreach (var (text2, tuple2) in _homeCards)
        {
            bool flag2 = text2 == entryId;
            var (button, _) = tuple2;
            IBrush borderBrush;
            if (!flag2)
            {
                IBrush transparent = Brushes.Transparent;
                borderBrush = transparent;
            }
            else
            {
                borderBrush = HomePalette.Focus;
            }
            button.BorderBrush = borderBrush;
            tuple2.Item2.BoxShadow = (flag2 ? HomePalette.FocusShadow : HomePalette.CardShadow);
            tuple2.Item1.Classes.Set("home-card-selected", flag2);
        }
        UpdateHomeReadouts();
        if (flag & persist)
        {
            QueueHomePreferenceSave();
        }
    }

    internal bool FocusHomeDefault()
    {
        if (!_homeMode)
        {
            return false;
        }
        if (!IsHomeLibrary)
        {
            return false;
        }
        if (_homeFocusKey != null && _homeTargets.TryGetValue(_homeFocusKey, out HomeFocusTarget value) && HomeTargetAvailable(value.Button))
        {
            Navigation.Focus(value.Button);
            return true;
        }
        FocusHomeSelection();
        IInputElement inputElement = FocusManager?.GetFocusedElement();
        Control focused = inputElement as Control;
        if (focused != null && HomeTargetAvailable(focused))
        {
            return _homeTargets.Values.Any((HomeFocusTarget t) => t.Button == focused);
        }
        return false;
    }

    private void FocusHomeSelection()
    {
        if (!IsHomeLibrary)
        {
            Navigation.FocusFirst(this);
            return;
        }
        if (_homeSelectedEntry != null && _homeCards.TryGetValue(_homeSelectedEntry, out (Button, Border) value) && HomeTargetAvailable(value.Item1))
        {
            value.Item1.Focus(NavigationMethod.Directional);
            CenterHomeSelection();
            return;
        }
        Navigation.Focus((_homeTargets.Values.FirstOrDefault((HomeFocusTarget t) => t.Group == "games" && HomeTargetAvailable(t.Button)) ?? _homeTargets.Values.FirstOrDefault((HomeFocusTarget t) => HomeTargetAvailable(t.Button)))?.Button);
    }

    private static bool HomeTargetAvailable(Control control)
    {
        if (control.IsVisible && control.IsEnabled && control.Bounds.Width > 0.0 && control.Bounds.Height > 0.0)
        {
            return control.GetVisualAncestors().OfType<Control>().All((Control c) => c.IsVisible && c.IsEnabled);
        }
        return false;
    }

    private void CenterHomeSelection()
    {
        if (_homeRail == null || _homeSelectedEntry == null || !_homeCards.TryGetValue(_homeSelectedEntry, out (Button, Border) value) || !(_homeRail.Content is Control relativeTo))
        {
            return;
        }
        Point? point = value.Item1.TranslatePoint(new Point(value.Item1.Bounds.Width / 2.0, 0.0), relativeTo);
        if (point.HasValue)
        {
            Point valueOrDefault = point.GetValueOrDefault();
            _homeRail.Offset = new Vector(Math.Clamp(valueOrDefault.X - _homeRail.Viewport.Width / 2.0, 0.0, Math.Max(0.0, _homeRail.Extent.Width - _homeRail.Viewport.Width)), 0.0);
            ScrollViewer homeRail = _homeRail;
            if (homeRail.Transitions == null)
            {
                Transitions transitions = new Transitions
                {
                    new VectorTransition
                    {
                        Property = ScrollViewer.OffsetProperty,
                        Duration = TimeSpan.FromMilliseconds(120L),
                        Easing = new CubicEaseOut()
                    }
                };
                Transitions transitions2 = transitions;
                homeRail.Transitions = transitions;
            }
        }
    }

    private void MoveHomeGame(int step)
    {
        if (!IsHomeLibrary || HomeNavigationBlocked || _homeGames.Length == 0)
        {
            return;
        }
        int num = Array.FindIndex(_homeGames, (GameLibraryEntry game) => game.EntryId == _homeSelectedEntry);
        int num2 = Math.Clamp(num + step, 0, _homeGames.Length - 1);
        if (num2 == num)
        {
            return;
        }
        SelectHomeGame(_homeGames[num2].EntryId);
        if (!_homeCards.ContainsKey(_homeSelectedEntry))
        {
            string entryId = _homeGames[num2].EntryId;
            if (_homeRail?.Parent is Grid grid)
            {
                grid.Children.Remove(_homeRail);
                string[] array = (from p in _homeTargets
                                  where p.Value.Group == "games"
                                  select p.Key).ToArray();
                foreach (string key in array)
                {
                    _homeTargets.Remove(key);
                }
                _homeCards.Clear();
                _homeSelectedEntry = entryId;
                Control control = BuildHomeLibrary(Math.Clamp(_homeLayoutSize.Height / 720.0, 0.75, 1.75));
                Grid.SetRow(control, 2);
                grid.Children.Add(control);
                UpdateLayout();
                SelectHomeGame(entryId, persist: false);
            }
            else
            {
                RenderCurrentPage();
                UpdateLayout();
            }
        }
        FocusHomeSelection();
    }

    internal bool MoveHomeFocus(Key direction)
    {
        if (!IsHomeLibrary)
        {
            return false;
        }
        if (HomeNavigationBlocked)
        {
            return true;
        }
        IInputElement focused = FocusManager?.GetFocusedElement();
        HomeFocusTarget current = _homeTargets.Values.FirstOrDefault((HomeFocusTarget target) => target.Button == focused);
        if ((object)current == null)
        {
            FocusHomeSelection();
            return true;
        }
        bool flag = current.Group == "games";
        if (flag)
        {
            bool flag2 = ((direction == Key.Left || direction == Key.Right) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            MoveHomeGame((direction != Key.Left) ? 1 : (-1));
            return true;
        }
        if ((direction == Key.Left || direction == Key.Right) ? true : false)
        {
            HomeFocusTarget[] array = _homeTargets.Values.Where((HomeFocusTarget t) => t.Group == current.Group && HomeTargetAvailable(t.Button)).ToArray();
            int num = Array.IndexOf(array, current);
            if (num >= 0 && array.Length != 0)
            {
                Navigation.Focus(array[Math.Clamp(num + ((direction != Key.Left) ? 1 : (-1)), 0, array.Length - 1)].Button);
            }
            return true;
        }
        int num2 = Array.IndexOf(HomeFocusGroups, current.Group);
        int num3 = ((direction != Key.Up) ? 1 : (-1));
        for (int i = num2 + num3; i >= 0 && i < HomeFocusGroups.Length; i += num3)
        {
            if (HomeFocusGroups[i] == "games" && _homeGames.Length != 0)
            {
                FocusHomeSelection();
                return true;
            }
            HomeFocusTarget[] array2 = _homeTargets.Values.Where((HomeFocusTarget t) => t.Group == HomeFocusGroups[i] && HomeTargetAvailable(t.Button)).ToArray();
            if (array2.Length != 0)
            {
                double x = CenterX(current.Button);
                Navigation.Focus(array2.MinBy((HomeFocusTarget t) => Math.Abs(CenterX(t.Button) - x)).Button);
                return true;
            }
        }
        return true;
        double CenterX(Control c)
        {
            return c.TranslatePoint(new Point(c.Bounds.Width / 2.0, 0.0), this)?.X ?? 0.0;
        }
    }

    internal bool CycleHomeFilter(int step)
    {
        if (!_homeMode)
        {
            return false;
        }
        if (!IsHomeLibrary || HomeNavigationBlocked)
        {
            return true;
        }
        string[] array = new string[3] { "all", "favorites", "recent" };
        int num = Array.IndexOf(array, _homeFilter);
        SetHomeFilterAsync(array[(num + step + array.Length) % array.Length]);
        return true;
    }

    internal Task HomeControllerOptionsAsync()
    {
        if (!IsHomeLibrary || Session.Busy || Session.GameWindowClosing)
        {
            return Task.CompletedTask;
        }
        return ShowHomeGameOptionsAsync();
    }

    internal Task HomeControllerStopAsync()
    {
        if (!_homeMode || !Session.HasGame || Session.Busy)
        {
            return Task.CompletedTask;
        }
        return MainActionAsync(Session.StopAsync);
    }

    private async Task ImportIndexedSaveAsync()
    {
        string core = Session.SelectedBackend?.Id;
        if (core != "nxi")
        {
            return;
        }
        long failures = Session.OperationFailureSequence;
        await MainActionAsync(() => Session.DiscoverIndexedSaveTargetsAsync(core));
        if (Session.OperationFailureSequence != failures)
        {
            await ShowLastFailureAsync();
            return;
        }
        SaveView[] targets = Session.IndexedSaveTargets.ToArray();
        if (targets.Length == 0)
        {
            await Dialogs.MessageAsync(this, UiText.Get("No indexed save destinations"), UiText.Get("Start the game once with this core to create its native save, then close it. NX will not invent a save-index entry."));
            return;
        }
        string text = await Dialogs.ChooseAsync(this, UiText.Get("Choose the destination save"), targets.Select((SaveView saveView, int index) => (index.ToString(CultureInfo.InvariantCulture), saveView.Native.TitleId + " · " + saveView.Native.Type + UiText.Get("\nUser: ") + saveView.Native.UserId)).ToArray(), UiText.Get("Use Add portable location to select an external Ryujinx, Canary or Kenji data folder when it is not in the usual Ryujinx location. The game and user must match exactly."));
        if (text == null)
        {
            return;
        }
        SaveView view = targets[int.Parse(text, CultureInfo.InvariantCulture)];
        IndexedSaveImportReview review = await Session.ReviewIndexedSaveAsync(view);
        if ((object)review == null)
        {
            await ShowLastFailureAsync();
            return;
        }
        try
        {
            if (await Dialogs.ConfirmAsync(this, UiText.Get("Replace this save with the reviewed snapshot?"), UiText.Get("Game: ") + review.TitleId + UiText.Get("\nUser: ") + review.UserId + UiText.Get("\nSource: ") + SaveOriginName(review.SourceOrigin) + UiText.Get("\nSource files: ") + review.SourceFiles + UiText.Get("\nCurrent destination files: ") + review.DestinationFiles + UiText.Get("\n\nThis replaces the selected destination save. Its original contents are backed up first. ") + UiText.Get("A changed save, changed native index, active game or pending recovery blocks the import. ") + UiText.Get("The external source is never modified. This is a one-way reviewed import, not a live two-way merge."), UiText.Get("Back up and import snapshot")))
            {
                failures = Session.OperationFailureSequence;
                await MainActionAsync(() => Session.ApplyIndexedSaveAsync(review));
                if (Session.OperationFailureSequence == failures)
                {
                    await Dialogs.MessageAsync(this, UiText.Get("Indexed save imported"), UiText.Get("The reviewed snapshot was imported and the previous save was preserved. The receipt appears under Imported saves. Test the game before removing any backups."));
                }
                else
                {
                    await ShowLastFailureAsync();
                }
                return;
            }
        }
        finally
        {
            try
            {
                await Session.DiscardIndexedSaveAsync(review.ReviewId);
            }
            catch (Exception ex)
            {
                await Dialogs.MessageAsync(this, UiText.Get("Snapshot cleanup needs attention"), ex.Message);
            }
            RenderCurrentPage();
        }
    }

    private async Task RepairInstallationAsync()
    {
        if (Session.Busy)
        {
            return;
        }
        if (Session.HasGame)
        {
            await Dialogs.MessageAsync(this, "Close the game first", "Save your progress and stop the game before repairing its core.");
            return;
        }
        List<(string, string)> list = Session.Backends.Select((BackendChoice b) => (Id: b.Id, DisplayName: b.DisplayName)).ToList();
        string[] array = new string[1] { "nxi" };
        foreach (string id in array)
        {
            if (!list.Any(((string Id, string DisplayName) c) => c.Id == id) && CoreInstallationRepair.HasPending(Path.Combine(Session.Paths.InstallRoot, "cores", id)))
            {
                list.Add((id, id + " · interrupted repair"));
            }
        }
        if (list.Count == 0)
        {
            await Dialogs.MessageAsync(this, "No core installation found", "Install the complete NX Infinity core package, then reopen NX Infinity. This frontend component does not include native cores.");
            return;
        }
        BackendChoice backendChoice = Session.Backends.FirstOrDefault((BackendChoice b) => b.Id == "nxi");
        string target = (((object)backendChoice != null) ? Path.GetDirectoryName(backendChoice.ManifestPath) : Path.Combine(Session.Paths.InstallRoot, "cores", "nxi"));
        if (CoreInstallationRepair.HasPending(target))
        {
            if (!(await Dialogs.ConfirmAsync(this, "Recover interrupted repair?", "Restore the previous core if its directory is missing, or verify the completed replacement. Saves and backup folders are retained.", "Recover")))
            {
                return;
            }
            await MainActionAsync(() => Session.RunAsync("Recovering installation", async (CancellationToken ct) =>
            {
                string text2 = await CoreInstallationRepair.RecoverAsync(target, Session.Paths.DotnetPath, ct);
                await Dialogs.MessageAsync(this, "Installation recovery", text2 + " Restart NX Infinity to rediscover the core.");
            }));
            return;
        }
        string text = await Dialogs.BrowseAsync(this, "Choose the same core from an extracted NX Infinity package", Array.Empty<string>(), directory: true);
        if (text == null)
        {
            return;
        }
        CoreRepairPlan plan = await CoreInstallationRepair.InspectAsync(Path.Combine(target, "backend.json"), text, Session.Paths.DotnetPath);
        if (plan.ChangedFiles.Count == 0)
        {
            await Dialogs.MessageAsync(this, "Installation verified", "Every file matches the installed core's build manifest. No replacement is needed.");
        }
        else
        {
            if (!(await Dialogs.ConfirmAsync(this, "Repair nxi?", "Close any other emulator windows first. Replace " + plan.ChangedFiles.Count + " missing or damaged core files using the exact installed build. The complete previous folder, saves and settings are retained.\n\n" + string.Join("\n", plan.ChangedFiles.Take(12)), "Repair")))
            {
                return;
            }
            await MainActionAsync(() => Session.RunAsync("Repairing installation", async (CancellationToken ct) =>
            {
                await Session.StopForInstallationRepairAsync();
                CoreRepairReceipt coreRepairReceipt = await CoreInstallationRepair.ApplyAsync(plan, Session.Paths.DotnetPath, ct);
                await Dialogs.MessageAsync(this, "Installation repaired", coreRepairReceipt.ReplacedFiles + " core files replaced and verified. Previous folder: " + coreRepairReceipt.BackupDirectory + "\nRestart NX Infinity before launching a game.");
            }));
        }
    }

    private Control BuildLanguageCard()
    {
        Button button = VisualTheme.Button(UiText.LanguageName(LauncherLanguage), (Func<Task>)ChooseLauncherLanguageAsync, (string?)null);
        button.Name = "LauncherLanguage";
        AutomationProperties.SetName(button, UiText.Get("Launcher language") + ": " + UiText.LanguageName(LauncherLanguage));
        return Card(VisualTheme.Column(VisualTheme.LocalizedText("Language", 18.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("Language for NX Infinity. Games keep their own language settings.", 14.0, VisualTheme.Muted), button));
    }

    private async Task ChooseLauncherLanguageAsync()
    {
        string text = await Dialogs.ChooseAsync(this, UiText.Get("Choose the launcher language"), UiText.Languages, UiText.Get("Language changes apply immediately. Game titles, filenames and player text stay unchanged."));
        if (text == null)
        {
            return;
        }
        if (!SetLauncherLanguage(text))
        {
            await Dialogs.MessageAsync(this, UiText.Get("Could not save language"), UiText.Get("The launcher could not save this preference. Your previous language is still selected."));
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Navigation.Focus(this.GetVisualDescendants().OfType<Button>().FirstOrDefault((Button button) => button.Name == "LauncherLanguage"));
        }, DispatcherPriority.Loaded);
    }

    public bool SetLauncherLanguage(string language)
    {
        if (!UiText.Languages.Any(((string Id, string Name) option) => option.Id == language))
        {
            return false;
        }
        HomePreferences homePreferences = _homePreferences with
        {
            Language = language,
            SelectedEntryId = _homeSelectedEntry,
            Filter = _homeFilter
        };
        HomePreferenceStore? homePreferenceStore = _homePreferenceStore;
        if (homePreferenceStore == null || !homePreferenceStore.Write(homePreferences))
        {
            return false;
        }
        _homePreferences = homePreferences;
        HomePreferenceSaved = true;
        UiText.SetLanguage(language);
        RefreshLauncherLanguage();
        return true;
    }

    private void RefreshLauncherLanguage()
    {
        foreach (var (text2, element) in _navigation)
        {
            string text3 = UiText.Get((text2 == "Enhancements") ? "Improvements" : text2);
            _navigationLabels[text2].Text = text3;
            AutomationProperties.SetName(element, text3);
            ToolTip.SetTip(element, text3);
        }
        foreach (var shellLabel in _shellLabels)
        {
            TextBlock item = shellLabel.Label;
            string item2 = shellLabel.English;
            item.Text = UiText.Get(item2);
        }
        _error.Content = UiText.Get("Last issue");
        _cancel.Content = UiText.Get("Cancel");
        if (_sessionResume != null)
        {
            _sessionResume.Content = UiText.Get("Resume");
        }
        if (_sessionStop != null)
        {
            _sessionStop.Content = UiText.Get("Stop game");
        }
        if (_sessionGraphics != null)
        {
            _sessionGraphics.Content = UiText.Get("Graphics");
        }
        if (_startupSequence != null)
        {
            _startupSequence.Skip.Content = UiText.Get("Skip intro");
        }
        if (_bootCancel != null)
        {
            _bootCancel.Content = UiText.Get("Cancel launch");
        }
        _bootHeading.Text = UiText.Get("STARTING GAME");
        _inputHint.Text = UiText.Get("Enter  Open     Esc  Back     Tab  Move focus");
        if (_desktopSearch != null)
        {
            _desktopSearch.PlaceholderText = UiText.Get("Search");
        }
        RenderCurrentPage();
        SessionChanged();
    }

    private static Border Card(Control content, bool accent = false)
    {
        return new Border
        {
            Classes = { "service-section" },
            Padding = new Thickness(22.0),
            CornerRadius = new CornerRadius(3.0),
            Background = (accent ? VisualTheme.AccentSoft : VisualTheme.Panel),
            BorderBrush = VisualTheme.Line,
            BorderThickness = new Thickness(1.0),
            Child = content
        };
    }

    private static ScrollViewer PageScroll(Control content)
    {
        return new ScrollViewer
        {
            Content = content,
            Padding = new Thickness(0.0, 0.0, 12.0, 0.0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private static WrapPanel Actions(params Control[] controls)
    {
        WrapPanel wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        foreach (Control control in controls)
        {
            control.Margin = new Thickness(0.0, 0.0, 9.0, 9.0);
            wrapPanel.Children.Add(control);
        }
        return wrapPanel;
    }

    private static Control Heading(string title, string detail)
    {
        StackPanel stackPanel = VisualTheme.Column(VisualTheme.Text(title, 30.0, null, FontWeight.DemiBold), VisualTheme.Text(detail, 13.0, VisualTheme.Muted));
        stackPanel.Spacing = 6.0;
        return stackPanel;
    }

    private static TextBlock SingleLine(string text, double size = 13.0, IBrush? color = null)
    {
        TextBlock textBlock = VisualTheme.Text(text, size, color);
        textBlock.TextWrapping = TextWrapping.NoWrap;
        textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        return textBlock;
    }

    private Control Artwork(GameLibraryEntry entry, double size)
    {
        string text = string.Concat(from word in (from word in NintendoLibraryMetadata.SuggestQuery(GameTitle(entry)).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                  where word.Any(char.IsLetter)
                                                  select word).Take(2)
                                    select StringInfo.GetNextTextElement(word)).ToUpperInvariant();
        if (text.Length == 0)
        {
            text = "NX";
        }
        Grid grid = new Grid
        {
            Width = size,
            Height = size,
            Background = VisualTheme.Soft
        };
        grid.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = size * 0.24,
            FontWeight = FontWeight.Light,
            Foreground = VisualTheme.Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (size >= 150.0)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "LOCAL GAME",
                FontSize = 10.0,
                Foreground = VisualTheme.Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0.0, 0.0, 0.0, 17.0)
            });
        }
        Border frame = new Border
        {
            Classes = { "game-artwork" },
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(2.0),
            ClipToBounds = true,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = grid
            }
        };
        TrackArtworkSize(frame, () => PopulateArtworkAsync(frame, entry, (frame.Bounds.Width > 0.0) ? frame.Bounds.Width : size));
        _ = PopulateArtworkAsync(frame, entry, size);
        return frame;
    }

    private void OpenGame(GameLibraryEntry game)
    {
        if (!_homeMode && _duneOverview != null)
        {
            SelectDuneGame(game);
            return;
        }
        _selectedEntry = game.EntryId;
        RenderCurrentPage();
        Navigation.FocusFirst(this);
    }

    private Button PlayButton(GameLibraryEntry game, string name)
    {
        bool active = Session.ActiveGame?.EntryId == game.EntryId;
        string label;
        if (active)
        {
            label = ((Session.State == RuntimeState.Paused) ? UiText.Get("Resume game") : UiText.Get("Open game"));
        }
        else
        {
            label = UiText.Get("Play game");
        }
        Button button = VisualTheme.IconButton("play", label, () => MainActionAsync(() =>
        {
            _selectedEntry = game.EntryId;
            return (!active) ? Session.LaunchAsync(game.EntryId) : Session.ResumeAsync();
        }), "primary");
        button.Name = name;
        button.IsEnabled = (object)Session.SelectedBackend != null && File.Exists(game.Content.AbsolutePath);
        button.MinWidth = 140.0;
        return button;
    }

    private Control BuildLibraryPage()
    {
        if (!_homeMode)
        {
            return BuildDuneLibrary();
        }
        if (_selectedEntry != null)
        {
            return BuildGamePage(Session.Library.Get(_selectedEntry));
        }
        IEnumerable<GameLibraryEntry> source = Session.Library.List(new LibraryQuery(null, _favorites, LibraryContentKind.Game, _sort, 0, 2000)).Where(LibraryContentClassification.IsAvailableGame);
        if (!string.IsNullOrWhiteSpace(_search))
        {
            source = source.Where((GameLibraryEntry gameLibraryEntry) => GameTitle(gameLibraryEntry).Contains(_search, StringComparison.OrdinalIgnoreCase) || gameLibraryEntry.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase));
        }
        if (_sort == LibrarySort.Name)
        {
            source = source.OrderBy(GameTitle, StringComparer.CurrentCultureIgnoreCase);
        }
        GameLibraryEntry[] array = source.ToArray();
        bool flag = Height < 760.0;
        StackPanel stackPanel = new StackPanel
        {
            Spacing = ((Height < 650.0) ? 12 : 16)
        };
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 16.0
        };
        grid.Children.Add(flag ? VisualTheme.LocalizedText("Library", 30.0, null, FontWeight.DemiBold) : Heading(UiText.Get("Library"), _favorites ? UiText.Get("Your favorite games, all in one place.") : UiText.Get("Your games")));
        Button button = VisualTheme.IconButton("plus", UiText.Get("Add game folder"), (Func<Task>)AddGamesAsync, (string?)null);
        button.Name = "AddGame";
        button.VerticalAlignment = VerticalAlignment.Center;
        if (array.Length == 0)
        {
            button.Classes.Add("primary");
            button.Tag = "primary-focus";
        }
        StackPanel stackPanel2 = VisualTheme.Row(button, VisualTheme.IconButton("folder-open", UiText.Get("Folders"), (Func<Task>)ManageGameFoldersAsync, (string?)null));
        Grid.SetColumn(stackPanel2, 1);
        grid.Children.Add(stackPanel2);
        stackPanel.Children.Add(grid);
        TextBox search = new TextBox
        {
            PlaceholderText = UiText.Get("Search your library"),
            Text = _search,
            Name = "LibrarySearch",
            MaxLength = 200
        };
        AutomationProperties.SetName(search, UiText.Get("Search your library"));
        search.KeyDown += async (object? _, KeyEventArgs e) =>
        {
            if (e.Key == Key.Return)
            {
                e.Handled = true;
                await SearchAsync();
            }
        };
        Grid grid2 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 8.0
        };
        grid2.Children.Add(search);
        Button button2 = VisualTheme.IconButton("search", UiText.Get("Search"), (Func<Task>)SearchAsync, (string?)null);
        Grid.SetColumn(button2, 1);
        grid2.Children.Add(button2);
        Button button3 = VisualTheme.IconButton("star", UiText.Get("Favorites"), () =>
        {
            _favorites = !_favorites;
            RenderCurrentPage();
        });
        button3.Classes.Set("selected", _favorites);
        Grid.SetColumn(button3, 2);
        grid2.Children.Add(button3);
        string label;
        if (_sort == LibrarySort.RecentlyPlayed)
        {
            label = UiText.Get("Recent");
        }
        else
        {
            label = ((_sort == LibrarySort.RecentlyAdded) ? UiText.Get("Added") : UiText.Get("A–Z"));
        }
        Button button4 = VisualTheme.IconButton("chevron-down", label, async () =>
        {
            string text2 = await Dialogs.ChooseAsync(this, UiText.Get("Sort your library"),
            [
                ("name", UiText.Get("Name · A–Z")),
                ("played", UiText.Get("Recently played")),
                ("added", UiText.Get("Recently added"))
            ]);
            if (text2 != null)
            {
                _sort = ((text2 == "played") ? LibrarySort.RecentlyPlayed : ((text2 == "added") ? LibrarySort.RecentlyAdded : LibrarySort.Name));
                RenderCurrentPage();
            }
        });
        Grid.SetColumn(button4, 3);
        grid2.Children.Add(button4);
        stackPanel.Children.Add(grid2);
        if (array.Length == 0)
        {
            StackPanel stackPanel3 = VisualTheme.Column(new NavGlyph
            {
                Kind = "Library",
                Width = 36.0,
                Height = 36.0,
                Stroke = VisualTheme.Blue,
                Margin = new Thickness(0.0, 4.0, 0.0, 10.0),
                HorizontalAlignment = HorizontalAlignment.Left
            }, VisualTheme.Text((string.IsNullOrWhiteSpace(_search) && !_favorites) ? UiText.Get("Add your games") : UiText.Get("No games match this view."), 26.0, null, FontWeight.DemiBold), VisualTheme.Text((string.IsNullOrWhiteSpace(_search) && !_favorites) ? UiText.Get("Choose a game folder. Your files stay where they are.") : UiText.Get("Try a different title or return to all games."), 15.0, VisualTheme.Muted));
            stackPanel3.MaxWidth = 590.0;
            stackPanel3.HorizontalAlignment = HorizontalAlignment.Left;
            stackPanel3.Children.Add(Actions(VisualTheme.IconButton("folder-open", UiText.Get("Set up local content"), () => NavigateAsync("Content")), VisualTheme.IconButton("gamepad-2", UiText.Get("Controls"), (Func<Task>)ChooseGameControllerAsync, (string?)null)));
            if (!string.IsNullOrWhiteSpace(_search) || _favorites)
            {
                stackPanel3.Children.Add(VisualTheme.LocalizedButton("Show all games", () =>
                {
                    _search = "";
                    _favorites = false;
                    RenderCurrentPage();
                }));
            }
            Border border = Card(stackPanel3);
            border.Padding = new Thickness(30.0);
            stackPanel.Children.Add(border);
        }
        else
        {
            Grid grid3 = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            grid3.Children.Add(VisualTheme.Text(_favorites ? UiText.Get("Favorites") : UiText.Get("Collection"), 19.0, null, FontWeight.DemiBold));
            TextBlock textBlock = SingleLine(array.Length + ((array.Length == 1) ? UiText.Get(" game") : UiText.Get(" games")), 12.0, VisualTheme.Muted);
            Button button5 = VisualTheme.IconButton("image", UiText.Get("Refresh local covers"), () => MainActionAsync(() => Session.RefreshMissingArtworkAsync(retry: true)));
            button5.Name = "RefreshLocalCovers";
            button5.IsEnabled = !Session.HasGame && !Session.OrbitEnabled;
            StackPanel stackPanel4 = VisualTheme.Row(textBlock, button5);
            Grid.SetColumn(stackPanel4, 1);
            grid3.Children.Add(stackPanel4);
            StackPanel stackPanel5 = new StackPanel
            {
                Spacing = 12.0
            };
            if (!flag)
            {
                stackPanel5.Children.Add(grid3);
            }
            LibraryTilePanel libraryTilePanel = new LibraryTilePanel((Height < 650.0) ? 176 : (flag ? 200 : 224))
            {
                Name = "GameShelf"
            };
            foreach (GameLibraryEntry game in array.Take(_libraryVisibleCount))
            {
                TextBlock textBlock2 = VisualTheme.Text(GameTitle(game), 15.0, null, FontWeight.DemiBold);
                textBlock2.MaxLines = 2;
                textBlock2.TextTrimming = TextTrimming.CharacterEllipsis;
                textBlock2.LineHeight = 18.0;
                textBlock2.Height = 36.0;
                textBlock2.VerticalAlignment = VerticalAlignment.Top;
                NintendoCatalogGame nintendoCatalogGame = CatalogSelection(game)?.Game;
                string text = game.Observations.LastOrDefault()?.DisplayVersion;
                TextBlock item = SingleLine((!File.Exists(game.Content.AbsolutePath)) ? UiText.Get("File unavailable") : (nintendoCatalogGame?.Publisher ?? ((text != null) ? (UiText.Get("Version ") + text) : UiText.Get("Local content"))), 12.0, VisualTheme.Muted);
                Grid grid4 = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 7.0
                };
                grid4.Children.Add(item);
                if (game.Favorite)
                {
                    NavGlyph navGlyph = new NavGlyph
                    {
                        Kind = "star",
                        Width = 14.0,
                        Height = 14.0,
                        Stroke = VisualTheme.Blue
                    };
                    Grid.SetColumn(navGlyph, 1);
                    grid4.Children.Add(navGlyph);
                }
                StackPanel stackPanel6 = VisualTheme.Column(textBlock2, grid4);
                stackPanel6.Spacing = 4.0;
                stackPanel6.Margin = new Thickness(5.0, 3.0, 5.0, 6.0);
                Control control = Artwork(game, 320.0);
                StackPanel stackPanel7 = VisualTheme.Column(control, stackPanel6);
                stackPanel7.Spacing = 9.0;
                Button button6 = new Button
                {
                    Name = "GameCard",
                    Tag = game.EntryId,
                    Content = stackPanel7,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                button6.Classes.Add("game-card");
                AutomationProperties.SetName(button6, GameTitle(game) + ", open game details");
                ToolTip.SetTip(button6, GameTitle(game));
                button6.Click += (object? _, RoutedEventArgs _) =>
                {
                    OpenGame(game);
                };
                libraryTilePanel.Add(button6, control);
            }
            stackPanel5.Children.Add(libraryTilePanel);
            stackPanel.Children.Add(stackPanel5);
            if (array.Length > _libraryVisibleCount)
            {
                stackPanel.Children.Add(VisualTheme.LocalizedButton("Show more games (" + _libraryVisibleCount + UiText.Get(" of ") + array.Length + ")", () =>
                {
                    _libraryVisibleCount += 60;
                    RenderCurrentPage();
                }));
            }
        }
        return PageScroll(stackPanel);
        async Task SearchAsync()
        {
            _search = search.Text?.Trim() ?? "";
            _libraryVisibleCount = 60;
            RenderCurrentPage();
            Navigation.FocusFirst(this);
            await Task.CompletedTask;
        }
    }

    private Control FeaturedGame(GameLibraryEntry game)
    {
        bool compactRail = _compactRail;
        bool flag = Session.ActiveGame?.EntryId == game.EntryId;
        NintendoCatalogGame nintendoCatalogGame = CatalogSelection(game)?.Game;
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 6.0,
            VerticalAlignment = VerticalAlignment.Center
        };
        Controls children = stackPanel.Children;
        string text;
        if (flag)
        {
            text = ((Session.State == RuntimeState.Paused) ? UiText.Get("SESSION PAUSED") : UiText.Get("NOW PLAYING"));
        }
        else
        {
            text = UiText.Get("IN YOUR LIBRARY");
        }
        children.Add(SingleLine(text, 10.0, VisualTheme.Blue));
        TextBlock textBlock = VisualTheme.Text(GameTitle(game), compactRail ? 23 : 28, null, FontWeight.DemiBold);
        textBlock.MaxLines = 2;
        textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        stackPanel.Children.Add(textBlock);
        stackPanel.Children.Add(SingleLine(((object)nintendoCatalogGame != null) ? (UiText.Get("Nintendo Switch  ·  ") + (nintendoCatalogGame.Publisher ?? UiText.Get("Catalog artwork"))) : (UiText.Get("Local game  ·  ") + (Session.SelectedBackend?.Name ?? UiText.Get("NX Infinity Core unavailable"))), 13.0, VisualTheme.Muted));
        Button button = PlayButton(game, "FeaturedPlayGame");
        button.Tag = "primary-focus";
        WrapPanel wrapPanel = Actions(button, FullscreenPlayButton(game), VisualTheme.IconButton("arrow-right", UiText.Get("View game"), () =>
        {
            OpenGame(game);
        }, "subtle"));
        if (!compactRail && (object)game.Artwork == null && (object)nintendoCatalogGame == null)
        {
            Button button2 = VisualTheme.IconButton("image", UiText.Get("Find cover"), () => EditMetadataAsync(game), "subtle");
            button2.Margin = new Thickness(0.0, 0.0, 9.0, 9.0);
            wrapPanel.Children.Add(button2);
        }
        wrapPanel.Margin = new Thickness(0.0, 0.0, 0.0, -9.0);
        stackPanel.Children.Add(wrapPanel);
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = (compactRail ? 20 : 26),
            Children = { Artwork(game, (Height >= 860.0) ? 150 : 120) }
        };
        Grid.SetColumn(stackPanel, 1);
        grid.Children.Add(stackPanel);
        Border border = Card(grid);
        border.Name = "FeaturedGame";
        border.Padding = new Thickness(16.0);
        return border;
    }

    private Control BuildGamePage(GameLibraryEntry game)
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 20.0
        };
        stackPanel.Children.Add(Actions(VisualTheme.IconButton("chevron-left", UiText.Get("All games"), () =>
        {
            _selectedEntry = null;
            RenderCurrentPage();
            Navigation.FocusFirst(this);
        }, "subtle"), VisualTheme.IconButton("star", game.Favorite ? UiText.Get("Favorited") : UiText.Get("Add favorite"), async () =>
        {
            await MainActionAsync(() => Session.RunAsync("Updating favorite", (CancellationToken _) =>
            {
                Session.Library.SetFavorite(game.EntryId, !game.Favorite);
                return Task.CompletedTask;
            }));
        })));
        string text = InfinitySession.TitleOf(game);
        NintendoCatalogGame nintendoCatalogGame = CatalogSelection(game)?.Game;
        string text2 = Session.SelectedBackend?.DisplayName ?? "NX Infinity Core unavailable";
        Button button = PlayButton(game, "PlayGame");
        button.Tag = "primary-focus";
        Button button2 = VisualTheme.IconButton("monitor", "NX Infinity Core", (Func<Task>)ChooseBackendAsync, (string?)null);
        StackPanel stackPanel2 = new StackPanel
        {
            Spacing = 12.0,
            VerticalAlignment = VerticalAlignment.Center
        };
        stackPanel2.Children.Add(SingleLine(((object)nintendoCatalogGame != null) ? UiText.Get("NINTENDO SWITCH") : UiText.Get("LOCAL GAME"), 11.0, VisualTheme.Blue));
        TextBlock textBlock = VisualTheme.Text(GameTitle(game), _compactRail ? 25 : 34, null, FontWeight.DemiBold);
        textBlock.MaxLines = 4;
        textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        stackPanel2.Children.Add(textBlock);
        if ((object)nintendoCatalogGame != null)
        {
            stackPanel2.Children.Add(VisualTheme.Text(string.Join("  ·  ", new string[2]
            {
                nintendoCatalogGame.Publisher,
                CatalogDate(nintendoCatalogGame)
            }.Where((string value) => !string.IsNullOrEmpty(value))), 13.0, VisualTheme.Muted));
        }
        stackPanel2.Children.Add(VisualTheme.Text(text2, 13.0, VisualTheme.Muted));
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = (_compactRail ? 20 : 28)
        };
        grid.Children.Add(Artwork(game, _compactRail ? 150 : 216));
        Grid.SetColumn(stackPanel2, 1);
        grid.Children.Add(stackPanel2);
        StackPanel stackPanel3 = new StackPanel
        {
            Spacing = 22.0
        };
        stackPanel3.Children.Add(grid);
        stackPanel3.Children.Add(Actions(button, FullscreenPlayButton(game), VisualTheme.IconButton("gamepad-2", UiText.Get("Controls"), (Func<Task>)ChooseGameControllerAsync, (string?)null), button2, VisualTheme.IconButton("image", ((object)nintendoCatalogGame == null) ? UiText.Get("Find cover & details") : UiText.Get("Edit cover & details"), () => EditMetadataAsync(game)), VisualTheme.IconButton("monitor", UiText.Get("Create Desktop Shortcut"), () => CreateGameShortcutAsync(game))));
        stackPanel3.Children.Add(CreateControllerStatusReadout());
        stackPanel.Children.Add(Card(stackPanel3));
        WrapPanel wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        (string, string, string, Func<Task>)[] array = new (string, string, string, Func<Task>)[4]
        {
            ("sliders-horizontal", UiText.Get("Improvements"), "Graphics and performance presets", () => NavigateAsync("Enhancements")),
            ("settings", UiText.Get("Graphics & display"), "Resolution, fullscreen & core options", async () =>
            {
                _settingsForGame = true;
                await NavigateAsync("Settings");
            }),
            ("folder-open", UiText.Get("Content"), "Updates, DLC & mods", () => NavigateAsync("Content")),
            ("archive", UiText.Get("Saves"), "Manage your save data", () => NavigateAsync("Saves"))
        };
        for (int num = 0; num < array.Length; num++)
        {
            (string, string, string, Func<Task>) tuple = array[num];
            string item = tuple.Item1;
            string item2 = tuple.Item2;
            string item3 = tuple.Item3;
            Func<Task> item4 = tuple.Item4;
            Button button3 = VisualTheme.Button(item2, item4);
            button3.HorizontalContentAlignment = HorizontalAlignment.Left;
            double num2 = Math.Max(300.0, Width - (double)(_compactRail ? 76 : 208) - (double)(_compactRail ? 40 : 60) - 30.0);
            button3.Width = Math.Floor((num2 - 14.0) / 2.0);
            button3.Margin = new Thickness(0.0, 0.0, 14.0, 14.0);
            button3.MinHeight = 78.0;
            StackPanel stackPanel4 = VisualTheme.Column(VisualTheme.Text(item2, 15.0, null, FontWeight.Medium), SingleLine(item3, 12.0, VisualTheme.Muted));
            stackPanel4.Spacing = 5.0;
            button3.Content = VisualTheme.Row(new NavGlyph
            {
                Kind = item,
                Width = 21.0,
                Height = 21.0,
                Stroke = VisualTheme.Blue
            }, stackPanel4);
            AutomationProperties.SetName(button3, item2);
            wrapPanel.Children.Add(button3);
        }
        wrapPanel.Margin = new Thickness(0.0, 0.0, -14.0, -14.0);
        stackPanel.Children.Add(wrapPanel);
        StackPanel content = VisualTheme.Column(VisualTheme.LocalizedText("Game identity", 18.0, null, FontWeight.DemiBold), VisualTheme.Text((text == null) ? UiText.Get("This game needs a native inspection. Set up local content, then inspect it to unlock title-specific core and content options.") : (UiText.Get("Title ID  ") + text + UiText.Get("\nBuild  ") + (Session.BuildIdentityFor(game)?.BuildId ?? UiText.Get("Awaiting verification from this core revision"))), 13.0, VisualTheme.Muted), Actions(VisualTheme.IconButton("activity", UiText.Get("Inspect again"), () => MainActionAsync(() => Session.InspectAgainAsync(game.EntryId))), VisualTheme.LocalizedButton("Identity and evidence", () => Dialogs.MessageAsync(this, UiText.Get("Native identity and evidence"), IdentityText(game)))));
        stackPanel.Children.Add(Card(content));
        stackPanel.Children.Add(Actions(VisualTheme.LocalizedButton("Remove from library", async () =>
        {
            if (await Dialogs.ConfirmAsync(this, UiText.Get("Remove this library entry?"), UiText.Get("This removes the library record. The game file, saves and mod payloads stay in their current locations."), UiText.Get("Remove entry")))
            {
                await Session.RunAsync("Removing library record", (CancellationToken _) =>
                {
                    if (Session.ActiveGame?.EntryId == game.EntryId)
                    {
                        throw new NxException("invalid_state", UiText.Get("Stop this game before removing its library entry."));
                    }
                    Session.Library.Remove(game.EntryId);
                    Metadata.ClearSelection(game.EntryId);
                    _selectedEntry = null;
                    return Task.CompletedTask;
                });
                RenderCurrentPage();
            }
        }, "danger")));
        return PageScroll(stackPanel);
    }

    private string IdentityText(GameLibraryEntry game)
    {
        IEnumerable<string> values = game.Observations.Select((LibraryCoreObservation o) => $"Core: {o.BackendId}\nRevision: {o.BackendRevision}\nTitle: {o.TitleId ?? "unknown"}\nBuild: {o.BuildId ?? "unknown"}\nVersion: {o.DisplayVersion ?? "unknown"}\nNative load acknowledged: {o.LoaderAccepted}\nEvidence: {o.Provenance}");
        return "Local path: " + game.Content.AbsolutePath + "\nContent SHA-256: " + game.Content.Sha256 + "\nSize: " + game.Content.Size + " bytes\n\n" + string.Join("\n\n", values) + "\n\nCatalog artwork and titles are presentation metadata. Folder names and similar screenshots do not establish a build, an improvement, or official upgrade parity.";
    }

    private void StartLibraryMonitor()
    {
        if (_folderMonitorTimer != null)
        {
            return;
        }
        _folderMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2L)
        };
        _folderMonitorTimer.Tick += async (object? _, EventArgs _) =>
        {
            if (_closing || _monitorScanning || Session.Busy || Session.HasGame || Session.OrbitEnabled)
            {
                return;
            }
            _monitorScanning = true;
            try
            {
                GameLibraryFolder[] array = (from folder in Session.Library.Folders()
                                             where folder.ScanAtStartup
                                             select folder).ToArray();
                string text = string.Join("\n", array.Select((GameLibraryFolder folder) => folder.Id + ":" + folder.AbsolutePath + ":" + folder.Recursive));
                if (_folderMonitor == null || text != _watchedFolders)
                {
                    _folderMonitor?.Dispose();
                    _folderMonitor = new LibraryFolderMonitor(array);
                    _watchedFolders = text;
                }
                IReadOnlyList<GameLibraryFolder> changed = _folderMonitor.TakeChanged(DateTimeOffset.UtcNow);
                if (changed.Count != 0)
                {
                    await Session.RunBackgroundMaintenanceAsync(() => Session.ScanGameFoldersAsync(changed, startup: true));
                    if (!_closing && _section == "Library")
                    {
                        RenderCurrentPage();
                    }
                }
            }
            catch (Exception ex) when ((ex is IOException || ex is ServiceException || ex is UnauthorizedAccessException) ? true : false)
            {
                _status.Text = "Library refresh needs attention. Open Folders to scan again.";
            }
            finally
            {
                _monitorScanning = false;
            }
        };
        _folderMonitorTimer.Start();
    }

    private async Task ShowLicensesAsync()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "licenses");
        await Dialogs.MessageAsync(this, "Open-source licenses", "NX Infinity includes separately licensed open-source components. NX Infinity Core derives from Kenji-NX/Ryujinx MIT source with separately licensed dependencies. FFmpeg libraries are covered by the applicable LGPL/GPL terms in their build notices.\n\nThe desktop design is adapted from Dune by sakasakiking (MIT). The preserved license files cover Avalonia, Inter, SkiaSharp, HarfBuzz, .NET and other dependencies.\n\nMatching source and build instructions must accompany each public binary release. The src archive contains the emulator, host, runtime and launcher source.");
        if (Directory.Exists(folder))
        {
            DesktopShortcuts.OpenFolder(folder);
        }
        else
        {
            await Dialogs.MessageAsync(this, "License files missing", "The licenses folder is missing from this installation. Reinstall a complete package before redistributing it.");
        }
    }

    private async Task RunStartupMaintenanceAsync()
    {
        try
        {
            await Task.Delay(1500, _maintenanceLifetime.Token);
            if (CanMaintain())
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CanMaintain()
    {
        if (!_closing && !_maintenanceInterrupted && !Session.HasGame && !Session.Busy)
        {
            return !Session.OrbitEnabled;
        }
        return false;
    }

    private void InterruptMaintenance()
    {
        _maintenanceInterrupted = true;
        if (Session.IsBackgroundMaintenance || Session.IsLibraryScanning)
        {
            Session.Cancel();
        }
    }

    private NintendoCatalogSelection? CatalogSelection(GameLibraryEntry game)
    {
        if (_metadataForPage.TryGetValue(game.EntryId, out NintendoCatalogSelection value))
        {
            return value;
        }
        try
        {
            value = Metadata.GetSelection(game.EntryId);
        }
        catch (Exception ex) when ((ex is LibraryMetadataException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            value = null;
        }
        _metadataForPage[game.EntryId] = value;
        return value;
    }

    private string GameTitle(GameLibraryEntry game)
    {
        return CatalogSelection(game)?.Game.DisplayTitle ?? game.DisplayName;
    }

    private static string? CatalogDate(NintendoCatalogGame game)
    {
        if (!DateTime.TryParseExact(game.ReleaseDateText, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            return game.ReleaseDateText;
        }
        return result.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    private async Task EditMetadataAsync(GameLibraryEntry game)
    {
        Window dialog = Dialogs.Create(UiText.Get("Cover & game details"), 760.0, 690.0);
        CancellationTokenSource lifetime = new CancellationTokenSource();
        TextBox query;
        TextBlock status;
        ProgressBar progress;
        StackPanel results;
        Button search;
        Button clear;
        bool busy;
        Task pending;
        try
        {
            dialog.Closed += (object? _, EventArgs _) =>
            {
                lifetime.Cancel();
            };
            query = new TextBox
            {
                Name = "CatalogQuery",
                PlaceholderText = UiText.Get("Search Nintendo Switch games"),
                MaxLength = 160,
                Text = NintendoLibraryMetadata.SuggestQuery(GameTitle(game)),
                Tag = "primary-focus"
            };
            AutomationProperties.SetName(query, UiText.Get("Search Nintendo Switch games"));
            status = VisualTheme.LocalizedText("Search Nintendo’s public catalog, then choose the matching edition.", 13.0, VisualTheme.Muted);
            progress = new ProgressBar
            {
                Height = 3.0,
                IsIndeterminate = true,
                IsVisible = false
            };
            results = new StackPanel
            {
                Spacing = 10.0
            };
            Button button = VisualTheme.LocalizedButton("Back", () =>
            {
                dialog.Close();
            });
            search = null;
            clear = VisualTheme.LocalizedButton("Reset catalog details", () =>
            {
                Metadata.ClearSelection(game.EntryId);
                dialog.Close();
            }, "danger");
            busy = false;
            pending = Task.CompletedTask;
            search = VisualTheme.IconButton("search", UiText.Get("Search"), (Func<Task>)SearchAsync, "primary");
            search.Name = "CatalogSearch";
            query.KeyDown += async (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Return)
                {
                    e.Handled = true;
                    await SearchAsync();
                }
            };
            Grid grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 10.0
            };
            grid.Children.Add(query);
            Grid.SetColumn(search, 1);
            grid.Children.Add(search);
            StackPanel stackPanel = VisualTheme.Column(VisualTheme.LocalizedText("Cover & game details", 26.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("Choose a Nintendo Switch cover for this library entry.", 13.0, VisualTheme.Muted));
            stackPanel.Spacing = 7.0;
            Grid grid2 = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 17.0
            };
            grid2.Children.Add(Artwork(game, 80.0));
            TextBlock textBlock = VisualTheme.Text(GameTitle(game), 16.0, null, FontWeight.DemiBold);
            textBlock.MaxLines = 2;
            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            StackPanel stackPanel2 = VisualTheme.Column(textBlock, VisualTheme.Text(((object)CatalogSelection(game) != null) ? UiText.Get("Current selection  ·  Nintendo Europe") : UiText.Get("Local library details"), 12.0, VisualTheme.Muted));
            stackPanel2.Spacing = 6.0;
            Grid.SetColumn(stackPanel2, 1);
            grid2.Children.Add(stackPanel2);
            TextBlock textBlock2 = VisualTheme.LocalizedText("Only your search terms are sent. Selected artwork stays available offline. Game files and native identity stay local.", 12.0, VisualTheme.Muted);
            Button button2 = VisualTheme.LocalizedButton("Refresh local covers", async () =>
            {
                dialog.Close();
                await MainActionAsync(() => Session.RefreshMissingArtworkAsync(retry: true));
            });
            button2.Name = "RefreshLocalCovers";
            button2.IsEnabled = !Session.HasGame && !Session.OrbitEnabled;
            Grid grid3 = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            grid3.Children.Add(VisualTheme.Row(button, button2));
            button.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(clear, 1);
            grid3.Children.Add(clear);
            Grid grid4 = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,3,*,Auto,Auto"),
                RowSpacing = 16.0,
                Margin = new Thickness(26.0)
            };
            grid4.Children.Add(stackPanel);
            Grid.SetRow(grid2, 1);
            grid4.Children.Add(grid2);
            Grid.SetRow(grid, 2);
            grid4.Children.Add(grid);
            Grid.SetRow(status, 3);
            grid4.Children.Add(status);
            Grid.SetRow(progress, 4);
            grid4.Children.Add(progress);
            ScrollViewer scrollViewer = new ScrollViewer
            {
                Name = "CatalogResults",
                Content = results,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scrollViewer, 5);
            grid4.Children.Add(scrollViewer);
            Grid.SetRow(textBlock2, 6);
            grid4.Children.Add(textBlock2);
            Grid.SetRow(grid3, 7);
            grid4.Children.Add(grid3);
            dialog.Content = grid4;
            await Dialogs.ShowAsync<object>(this, dialog);
            await pending;
            RenderCurrentPage();
            Navigation.FocusFirst(this);
        }
        finally
        {
            if (lifetime != null)
            {
                ((IDisposable)lifetime).Dispose();
            }
        }
        async Task RunAsync(Func<Task> action)
        {
            if (!busy && !lifetime.IsCancellationRequested)
            {
                busy = true;
                TextBox textBox = query;
                Button button3 = search;
                Button button4 = clear;
                bool flag = (results.IsEnabled = false);
                bool flag3 = (button4.IsEnabled = flag);
                bool isEnabled = (button3.IsEnabled = flag3);
                textBox.IsEnabled = isEnabled;
                progress.IsVisible = true;
                status.Foreground = VisualTheme.Muted;
                pending = ExecuteAsync();
                await pending;
            }
            async Task ExecuteAsync()
            {
                try
                {
                    await action();
                }
                catch (OperationCanceledException)
                {
                    if (dialog.IsVisible)
                    {
                        status.Text = "Search canceled. Try again when you’re ready.";
                    }
                }
                catch (Exception ex2) when ((ex2 is LibraryMetadataException || ex2 is IOException || ex2 is UnauthorizedAccessException || ex2 is HttpRequestException) ? true : false)
                {
                    status.Text = ex2.Message;
                    status.Foreground = VisualTheme.Red;
                }
                finally
                {
                    busy = false;
                    progress.IsVisible = false;
                    TextBox textBox2 = query;
                    Button button5 = search;
                    Button button6 = clear;
                    bool flag6 = (results.IsEnabled = true);
                    bool flag8 = (button6.IsEnabled = flag6);
                    bool isEnabled2 = (button5.IsEnabled = flag8);
                    textBox2.IsEnabled = isEnabled2;
                }
            }
        }
        async Task SearchAsync()
        {
            await RunAsync(async () =>
            {
                status.Text = "Searching Nintendo’s catalog…";
                NintendoCatalogSearch nintendoCatalogSearch = await Metadata.SearchAsync(query.Text ?? "", lifetime.Token);
                if (dialog.IsVisible)
                {
                    results.Children.Clear();
                    status.Text = ((nintendoCatalogSearch.Results.Count == 0) ? "No matches. Try a shorter game title or check its spelling." : (nintendoCatalogSearch.Results.Count + ((nintendoCatalogSearch.Results.Count == 1) ? " match. Choose this edition to use its details." : " matches. Choose the edition in your library.") + (nintendoCatalogSearch.FromCache ? "  ·  Recent search" : "")));
                    foreach (NintendoCatalogGame candidate in nintendoCatalogSearch.Results)
                    {
                        Button button3 = VisualTheme.Button(candidate.DisplayTitle, () => RunAsync(async () =>
                        {
                            status.Text = "Saving your selected cover and game details…";
                            await Metadata.SelectAsync(game.EntryId, candidate, lifetime.Token);
                            if (dialog.IsVisible)
                            {
                                dialog.Close();
                            }
                        }));
                        button3.Name = "CatalogResult";
                        button3.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        button3.HorizontalAlignment = HorizontalAlignment.Stretch;
                        button3.MinHeight = 78.0;
                        StackPanel stackPanel3 = VisualTheme.Column(VisualTheme.Text(candidate.DisplayTitle, 16.0, null, FontWeight.Medium), VisualTheme.Text(string.Join("  ·  ", new string[3]
                        {
                            candidate.Publisher,
                            CatalogDate(candidate),
                            (candidate.SquareCoverUrl != null) ? UiText.Get("Square cover") : UiText.Get("Details only")
                        }.Where((string value) => !string.IsNullOrEmpty(value))), 12.0, VisualTheme.Muted));
                        stackPanel3.Spacing = 5.0;
                        Grid grid5 = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("24,*,20"),
                            ColumnSpacing = 14.0
                        };
                        grid5.Children.Add(new NavGlyph
                        {
                            Kind = "image",
                            Width = 23.0,
                            Height = 23.0,
                            Stroke = VisualTheme.Blue
                        });
                        Grid.SetColumn(stackPanel3, 1);
                        grid5.Children.Add(stackPanel3);
                        NavGlyph navGlyph = new NavGlyph
                        {
                            Kind = "arrow-right",
                            Width = 18.0,
                            Height = 18.0,
                            Stroke = VisualTheme.Muted
                        };
                        Grid.SetColumn(navGlyph, 2);
                        grid5.Children.Add(navGlyph);
                        button3.Content = grid5;
                        AutomationProperties.SetName(button3, "Use " + candidate.DisplayTitle);
                        results.Children.Add(button3);
                    }
                    if (nintendoCatalogSearch.Results.Count != 0)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            Navigation.Focus(results.Children.OfType<Button>().FirstOrDefault());
                        });
                    }
                }
            });
        }
    }

    private void EnableModDrop()
    {
        DragDrop.SetAllowDrop(this, value: true);
        AddHandler(DragDrop.DragOverEvent, (object? _, DragEventArgs e) =>
        {
            e.DragEffects = ((!Session.Busy && !Session.HasGame && e.DataTransfer.Contains(DataFormat.File)) ? DragDropEffects.Copy : DragDropEffects.None);
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, async (object? _, DragEventArgs e) =>
        {
            e.Handled = true;
            try
            {
                if (Session.Busy || Session.HasGame)
                {
                    return;
                }
                IStorageItem[] array = e.DataTransfer.TryGetFiles()?.ToArray() ?? Array.Empty<IStorageItem>();
                if (array.Length != 1)
                {
                    await Dialogs.MessageAsync(this, "Import one mod", "Drop one ZIP archive or extracted mod folder at a time.");
                    return;
                }
                string path = array[0].TryGetLocalPath();
                if (path == null)
                {
                    return;
                }
                GameLibraryEntry selectedGame = SelectedGame;
                if ((object)selectedGame == null)
                {
                    await SelectGameAsync();
                    selectedGame = SelectedGame;
                }
                if ((object)selectedGame != null)
                {
                    await ReviewModImportAsync(path, selectedGame);
                }
            }
            catch (Exception ex)
            {
                await Dialogs.MessageAsync(this, "Mod import needs attention", ex.Message);
            }
        });
    }

    private async Task ReviewModImportAsync(string source, GameLibraryEntry game)
    {
        if ((object)Session.BuildIdentityFor(game) == null)
        {
            await Dialogs.MessageAsync(this, "Inspect game first", "Set up keys and inspect this game with the selected core before importing a mod.");
        }
        else if (await Dialogs.ConfirmAsync(this, "Import mod", "Import " + Path.GetFileName(source) + " for " + GameTitle(game) + " using " + Session.SelectedBackend?.DisplayName + "?\n\nIt will start disabled. Enable it in Content before launching.", "Import"))
        {
            await MainActionAsync(() => Session.ImportModAsync(game.EntryId, source));
            await NavigateAsync("Content");
        }
    }

    private Control BuildOrbitPage()
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 17.0
        };
        stackPanel.Children.Add(Heading("Orbit", "Local Wireless with a community relay"));
        GameLibraryEntry selectedGame = SelectedGame;
        stackPanel.Children.Add(Actions(VisualTheme.Button(selectedGame?.DisplayName ?? "Choose a game", (Func<Task>)SelectGameAsync, (string?)null), VisualTheme.LocalizedButton("Go offline", () => MainActionAsync(Session.DisableOrbitAsync))));
        stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.Text(Session.OrbitEnabled ? "Online profile selected" : "Offline", 20.0, null, FontWeight.DemiBold), VisualTheme.Text(Session.OrbitMessage, 14.0, VisualTheme.Muted), VisualTheme.LocalizedText("Orbit keeps its saves separate from offline play. Each player needs the same core, game update and DLC. Choose Local Wireless inside the game.", 13.0, VisualTheme.Muted))));
        Button button = VisualTheme.LocalizedButton("Quick Play", () => PrepareOrbitUiAsync(privateRoom: false));
        button.Name = "OrbitQuickPlay";
        Button button2 = VisualTheme.LocalizedButton("Host private session", () => PrepareOrbitUiAsync(privateRoom: true));
        button2.Name = "OrbitHost";
        Button button3 = VisualTheme.LocalizedButton("Join code", (Func<Task>)JoinOrbitUiAsync, (string?)null);
        button3.Name = "OrbitJoin";
        bool flag = (button3.IsEnabled = (object)selectedGame != null && !Session.HasGame);
        bool isEnabled = (button2.IsEnabled = flag);
        button.IsEnabled = isEnabled;
        stackPanel.Children.Add(Actions(button, button2, button3));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Quick Play selects an exact-build discovery pool. Create or join a room using the game's Local Wireless menu. The relay connection alone does not confirm gameplay.", 13.0, VisualTheme.Muted));
        string prepared = Session.OrbitGameEntryId;
        if (prepared != null && selectedGame?.EntryId == prepared)
        {
            stackPanel.Children.Add(Actions(VisualTheme.LocalizedButton("Launch prepared game", () => MainActionAsync(() => Session.LaunchAsync(prepared))), VisualTheme.LocalizedButton("Read connection state", () => MainActionAsync(Session.RefreshOrbitStateAsync))));
            string code = Session.OrbitInviteCode;
            if (code != null)
            {
                stackPanel.Children.Add(Actions(VisualTheme.LocalizedButton("Copy invitation", async () =>
                {
                    if (Clipboard != null)
                    {
                        await Clipboard.SetTextAsync(code);
                    }
                }), VisualTheme.LocalizedButton("Show invitation", () => Dialogs.MessageAsync(this, "Private Orbit invitation", code + "\n\nShare only with invited players. Expires after 24 hours."))));
            }
        }
        LdnSessionState orbitState = Session.OrbitState;
        if ((object)orbitState != null)
        {
            stackPanel.Children.Add(Card(VisualTheme.LocalizedText("Native state: " + orbitState.Stage.Replace('_', ' ') + ((orbitState.ErrorCode == null) ? "" : (" · " + orbitState.ErrorCode.Replace('_', ' '))), 14.0)));
        }
        Controls children = stackPanel.Children;
        Control[] array = new Control[4]
        {
            VisualTheme.LocalizedText("Community server", 18.0, null, FontWeight.DemiBold),
            null,
            null,
            null
        };
        string orbitServer = Session.OrbitServer;
        double? orbitConnectMilliseconds = Session.OrbitConnectMilliseconds;
        string text;
        if (orbitConnectMilliseconds.HasValue)
        {
            double valueOrDefault = orbitConnectMilliseconds.GetValueOrDefault();
            text = $" · TCP connection {valueOrDefault:0} ms";
        }
        else
        {
            text = "";
        }
        array[1] = VisualTheme.Text(orbitServer + text, 14.0);
        array[2] = VisualTheme.LocalizedText("RyuLDN relay · guest Internet access off · no automatic router changes. Server reachability is checked when preparing a session.", 13.0, VisualTheme.Muted);
        array[3] = VisualTheme.LocalizedButton("Change server", async () =>
        {
            string server = await Dialogs.TextAsync(this, "Community server hostname", Session.OrbitServer, 253);
            if (server != null)
            {
                await MainActionAsync(() => Session.SetOrbitServerAsync(server));
            }
        });
        children.Add(Card(VisualTheme.Column(array)));
        stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("Nextendo Online", 18.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("Unavailable in this build. NX Infinity still needs a registered Nextendo application and its native account/game-service adapter. Orbit Local Wireless does not provide Nextendo Online.", 14.0, VisualTheme.Muted))));
        return PageScroll(stackPanel);
    }

    private async Task PrepareOrbitUiAsync(bool privateRoom)
    {
        GameLibraryEntry game = SelectedGame;
        if ((object)game != null)
        {
            await MainActionAsync(() => Session.PrepareOrbitAsync(game.EntryId, privateRoom));
        }
    }

    private async Task JoinOrbitUiAsync()
    {
        GameLibraryEntry game = SelectedGame;
        if ((object)game == null)
        {
            return;
        }
        string code = await Dialogs.TextAsync(this, "Paste an Orbit invitation", "", 4096, secret: true);
        if (code == null)
        {
            return;
        }
        try
        {
            LdnInvitation ldnInvitation = LdnInvitation.Decode(code.Trim(), DateTimeOffset.UtcNow);
            if (!(await Dialogs.ConfirmAsync(this, "Join this community session?", "Server: " + ldnInvitation.Server + "\nCore: " + ldnInvitation.Compatibility.BackendId + "\nGame update: " + ldnInvitation.Compatibility.DisplayVersion + "\n\nOnly share invitations with people you trust. This selects the separate Orbit profile.", "Prepare session")))
            {
                return;
            }
            await MainActionAsync(() => Session.PrepareOrbitAsync(game.EntryId, privateRoom: true, code));
        }
        catch (NxException ex)
        {
            await Dialogs.MessageAsync(this, "Invitation needs attention", ex.Message);
        }
    }

    private Control BuildPerformancePage()
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 18.0
        };
        stackPanel.Children.Add(Heading(UiText.Get("Performance"), UiText.Get("Frame pacing, shader progress, and supported core controls")));
        Button button = VisualTheme.LocalizedButton("New capture and play", () => MainActionAsync(() => Session.StartPerformanceCaptureAsync(resumeGame: true)), "primary");
        button.Name = "PerformanceCapture";
        button.Tag = "primary-focus";
        button.IsEnabled = Session.HasGame;
        Button button2 = VisualTheme.LocalizedButton("Review baseline", () => MainActionAsync(Session.PreviewPerformanceAsync));
        button2.Name = "PerformanceReview";
        button2.IsEnabled = (object)Session.SelectedBackend != null;
        Button button3 = VisualTheme.LocalizedButton("Export measurements", (Func<Task>)ExportPerformanceAsync, (string?)null);
        button3.Name = "PerformanceExport";
        stackPanel.Children.Add(Actions(button, button2, button3));
        StackPanel stackPanel2 = new StackPanel
        {
            Spacing = 15.0
        };
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 14.0
        };
        grid.Children.Add(VisualTheme.LocalizedText("Frame pacing", 21.0, null, FontWeight.DemiBold));
        TextBlock textBlock = Readout("captureState", 12.0, VisualTheme.Blue);
        Grid.SetColumn(textBlock, 1);
        grid.Children.Add(textBlock);
        stackPanel2.Children.Add(grid);
        stackPanel2.Children.Add(Readout("captureContext", 13.0, VisualTheme.Muted));
        WrapPanel wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 12.0,
            LineSpacing = 12.0
        };
        wrapPanel.Children.Add(PerformanceMetric("Median", "p50", "Typical interval"));
        wrapPanel.Children.Add(PerformanceMetric("95th percentile", "p95", "Slower end of the window"));
        wrapPanel.Children.Add(PerformanceMetric("99th percentile", "p99", "Rare long intervals"));
        wrapPanel.Children.Add(PerformanceMetric("Longest interval", "maximum", "Largest observed stall"));
        stackPanel2.Children.Add(wrapPanel);
        _presentPlot = new PresentIntervalPlot
        {
            Name = "PerformanceIntervalPlot",
            Height = 156.0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTip.SetTip(_presentPlot, "Actual native intervals, oldest to newest. Lines break at segment boundaries and missing sequence numbers.");
        stackPanel2.Children.Add(_presentPlot);
        stackPanel2.Children.Add(Readout("plotScope", 12.0, VisualTheme.Muted));
        stackPanel2.Children.Add(Readout("hitches", 14.0));
        stackPanel2.Children.Add(Readout("coverage", 12.0, VisualTheme.Muted));
        Button button4 = VisualTheme.LocalizedButton("Capture details", () => Dialogs.MessageAsync(this, UiText.Get("Native timing capture"), PerformanceCaptureDetails()));
        button4.Name = "PerformanceCaptureDetails";
        stackPanel2.Children.Add(Actions(button4));
        stackPanel.Children.Add(Card(stackPanel2));
        StackPanel stackPanel3 = new StackPanel
        {
            Spacing = 12.0
        };
        stackPanel3.Children.Add(VisualTheme.LocalizedText("Shaders and caches", 21.0, null, FontWeight.DemiBold));
        stackPanel3.Children.Add(Readout("shaderProgress", 16.0));
        stackPanel3.Children.Add(Readout("shaderDetail", 13.0, VisualTheme.Muted));
        stackPanel3.Children.Add(Readout("cacheState", 13.0, VisualTheme.Muted));
        stackPanel3.Children.Add(VisualTheme.LocalizedText("NX Infinity Core keeps its own caches. Cache loading covers previously encountered work; new game scenes may still need compilation. NX does not download shader packs or delete warm caches automatically.", 13.0, VisualTheme.Muted));
        stackPanel.Children.Add(Card(stackPanel3));
        stackPanel.Children.Add(BuildCorePerformanceCard());
        stackPanel.Children.Add(BuildGpuPerformanceCard(null));
        stackPanel.Children.Add(BuildPerformancePlanCard());
        stackPanel.Children.Add(BuildRendererThreadingCard());
        InfinityPerformanceRun[] array = Session.PerformanceHistory.Reverse().Take(4).ToArray();
        if (array.Length != 0)
        {
            StackPanel stackPanel4 = new StackPanel
            {
                Spacing = 12.0
            };
            stackPanel4.Children.Add(VisualTheme.LocalizedText("Recent captures", 21.0, null, FontWeight.DemiBold));
            InfinityPerformanceRun[] array2 = array;
            foreach (InfinityPerformanceRun run in array2)
            {
                PerformanceCaptureSummary capture = run.Capture;
                stackPanel4.Children.Add(Actions(VisualTheme.Text(capture.BackendId + " · " + capture.StartedUtc.ToLocalTime().ToString("HH:mm:ss") + UiText.Get(" · p95 ") + Ms(capture.P95Ms) + UiText.Get(" · p99 ") + Ms(capture.P99Ms) + " · " + capture.SampleCount.ToString("N0") + UiText.Get(" intervals"), 13.0), VisualTheme.LocalizedButton("Details", () => Dialogs.MessageAsync(this, UiText.Get("Capture evidence"), JsonSerializer.Serialize(run, new JsonSerializerOptions
                {
                    WriteIndented = true
                })))));
            }
            stackPanel4.Children.Add(VisualTheme.LocalizedText("Compare the same scene, game build, hardware, renderer mode and timing point. Use separate cold-cache and warm-cache runs. These measurements do not establish gameplay speed or rendering correctness.", 13.0, VisualTheme.Muted));
            stackPanel.Children.Add(Card(stackPanel4));
        }
        UpdatePerformanceReadouts();
        return PageScroll(stackPanel);
    }

    private TextBlock Readout(string key, double size, IBrush? brush = null)
    {
        TextBlock textBlock = VisualTheme.LocalizedText("—", size, brush);
        textBlock.Name = "Performance" + key;
        _performanceReadouts[key] = textBlock;
        return textBlock;
    }

    private Control PerformanceMetric(string label, string key, string description)
    {
        TextBlock textBlock = Readout(key, 28.0);
        textBlock.FontWeight = FontWeight.DemiBold;
        Border border = new Border();
        border.Width = 172.0;
        border.Padding = new Thickness(15.0);
        border.CornerRadius = new CornerRadius(10.0);
        border.Background = VisualTheme.Canvas;
        border.Child = VisualTheme.Column(VisualTheme.Text(label, 12.0, VisualTheme.Muted), textBlock, VisualTheme.Text(description, 11.0, VisualTheme.Muted));
        return border;
    }

    private Control BuildPerformancePlanCard()
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 13.0
        };
        stackPanel.Children.Add(VisualTheme.LocalizedText("Baseline review", 21.0, null, FontWeight.DemiBold));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Audit shader caching, CPU translation caching, native resolution and guest VSync using this core's real setting catalog. Review proposes supported changes; application verifies the effective values and records originals.", 13.0, VisualTheme.Muted));
        PerformancePlanPreview plan = Session.PerformancePlan;
        if ((object)plan != null && plan.BackendId == Session.SelectedBackendId)
        {
            stackPanel.Children.Add(VisualTheme.Text(plan.BackendId + " · " + plan.BackendRevision + "\n" + plan.ChangeCount + UiText.Get(" proposed changes · this runtime only"), 12.0, VisualTheme.Muted));
            using (IEnumerator<PerformancePlanEntry> enumerator = plan.Entries.GetEnumerator())
            {
                string text2;
                string text;
                string text3;
                PerformancePlanEntry current;
                Grid grid;
                TextBlock textBlock;
                for (; enumerator.MoveNext(); text2 = text, text3 = ((current.Requested.ValueKind == JsonValueKind.Undefined) ? "unavailable" : Display(current.Requested)), grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 12.0
                }, grid.Children.Add(VisualTheme.Column(VisualTheme.Text(current.Label, 15.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("Effective: " + text2 + (current.Included ? ("  →  " + text3) : "") + "\n" + current.Reason, 12.0, VisualTheme.Muted))), textBlock = VisualTheme.Text(PlanStatus(current.Status), 12.0, current.Included ? VisualTheme.Blue : VisualTheme.Muted), textBlock.MaxWidth = 130.0, Grid.SetColumn(textBlock, 1), grid.Children.Add(textBlock), stackPanel.Children.Add(grid))
                {
                    current = enumerator.Current;
                    SettingState original = current.Original;
                    if ((object)original != null && original.EffectiveKnown)
                    {
                        JsonElement? effective = original.Effective;
                        if (effective.HasValue)
                        {
                            JsonElement valueOrDefault = effective.GetValueOrDefault();
                            text = Display(valueOrDefault);
                            continue;
                        }
                    }
                    text = "unknown";
                }
            }
            Button button = VisualTheme.LocalizedButton("Apply reviewed changes", () => MainActionAsync(Session.ApplyPerformanceBaselineAsync), "primary");
            button.Name = "PerformanceApply";
            button.IsEnabled = Session.CanApplyPerformancePlan;
            stackPanel.Children.Add(Actions(button, VisualTheme.LocalizedButton("Review details", () => Dialogs.MessageAsync(this, UiText.Get("Baseline requirements"), string.Join("\n\n", plan.Limitations)))));
        }
        else
        {
            stackPanel.Children.Add(VisualTheme.LocalizedText("Choose Review baseline to read the selected core. No settings change during review.", 14.0, VisualTheme.Muted));
        }
        PerformancePlanReceipt receipt = Session.PerformanceReceipt;
        if ((object)receipt != null)
        {
            string text4;
            if (receipt.RolledBack)
            {
                text4 = "Original values restored and verified.";
            }
            else
            {
                text4 = (Session.PerformanceReceiptActive ? "Applied values were read back in this runtime." : "The runtime has closed; this transient plan has expired.");
            }
            stackPanel.Children.Add(VisualTheme.Text(text4, 14.0));
            Button button2 = VisualTheme.LocalizedButton("Restore recorded originals", () => MainActionAsync(Session.RollbackPerformanceBaselineAsync));
            button2.Name = "PerformanceRollback";
            button2.IsEnabled = Session.CanRollbackPerformancePlan;
            stackPanel.Children.Add(Actions(button2, VisualTheme.LocalizedButton("Application receipt", () => Dialogs.MessageAsync(this, UiText.Get("Baseline receipt"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions
            {
                WriteIndented = true
            })))));
            if (Session.PerformanceReceiptActive && !Session.CanRollbackPerformancePlan)
            {
                stackPanel.Children.Add(VisualTheme.LocalizedText("Pause before restoring live settings. A before-load change ends when this runtime closes; your saved core preferences are retained for the next launch.", 12.0, VisualTheme.Muted));
            }
        }
        stackPanel.Children.Add(VisualTheme.LocalizedText("A baseline is not a universal bug fix. FPS unlocks, asynchronous draw skipping and experimental profiles are never enabled by this action.", 12.0, VisualTheme.Muted));
        return Card(stackPanel);
    }

    private Control BuildCorePerformanceCard()
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 12.0
        };
        stackPanel.Children.Add(VisualTheme.LocalizedText("Core optimizations", 21.0, null, FontWeight.DemiBold));
        BackendInfo backendInfo = Session.ActiveBackend ?? Session.LastBackendInfo;
        if ((object)backendInfo != null && !(backendInfo.BackendId != Session.SelectedBackendId))
        {
            IReadOnlyDictionary<string, string> performancePolicies = backendInfo.PerformancePolicies;
            if (performancePolicies != null && performancePolicies.Count > 0)
            {
                stackPanel.Children.Add(VisualTheme.Text(backendInfo.BackendName + " · " + backendInfo.AdapterVersion + "\n" + backendInfo.BackendRevision, 12.0, VisualTheme.Muted));
                (string, string, string, string)[] array = new (string, string, string, string)[7]
                {
                    ("shaderCacheScheduler", UiText.Get("Shader cache queue"), "bounded-cache-v1", "Ready work can advance within bounded limits; shader commit order is preserved."),
                    ("cacheReadRecovery", UiText.Get("Truncated-cache recovery"), "bounded-cache-v1", "Zero-progress cache reads return through the loader's existing recovery path."),
                    ("shaderLinkStatus", UiText.Get("Shader link result"), "terminal-reread-v1", "Completed Vulkan shader failures remain failures when the cache queue checks them."),
                    ("shaderPoolLifetime", UiText.Get("Compiler object cleanup"), "destruction-v1", "Successfully constructed shader objects release their owned allocations."),
                    ("pipelineLoadPools", UiText.Get("Compiler storage reuse"), "bounded-pools-v1", "Cache loading reuses a bounded number of temporary pools and releases them when loading ends."),
                    ("windowDispatch", UiText.Get("Window and event scheduling"), "bounded-events-v1", "Batched events and coalesced window sizes reduce repeated work between frames."),
                    ("protocolInput", UiText.Get("Controller command transport"), "buffered-v1", "Buffered command reading and shared serializer metadata reduce repeated connection work.")
                };
                for (int i = 0; i < array.Length; i++)
                {
                    (string, string, string, string) tuple = array[i];
                    if (performancePolicies.TryGetValue(tuple.Item1, out var value))
                    {
                        bool flag = value == tuple.Item3;
                        Grid grid = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                            ColumnSpacing = 16.0
                        };
                        grid.Children.Add(VisualTheme.Column(VisualTheme.Text(tuple.Item2, 15.0, null, FontWeight.DemiBold), VisualTheme.Text(flag ? tuple.Item4 : ((value == "original-v1") ? UiText.Get("Original core policy.") : UiText.Get("This policy version has not been reviewed by this launcher.")), 12.0, VisualTheme.Muted)));
                        string text;
                        if (flag)
                        {
                            text = UiText.Get("Included");
                        }
                        else
                        {
                            text = ((value == "original-v1") ? UiText.Get("Original") : UiText.Get("Unverified"));
                        }
                        TextBlock textBlock = VisualTheme.Text(text, 12.0, flag ? VisualTheme.Blue : VisualTheme.Muted);
                        Grid.SetColumn(textBlock, 1);
                        grid.Children.Add(textBlock);
                        stackPanel.Children.Add(grid);
                    }
                }
                stackPanel.Children.Add(VisualTheme.LocalizedText("These are policies included in this exact core build. The cache readouts show actual use, and captures show observed frame pacing. A 60 FPS game option still requires the correct game build, normal gameplay speed and sufficient hardware performance.", 12.0, VisualTheme.Muted));
                return Card(stackPanel);
            }
        }
        stackPanel.Children.Add(VisualTheme.LocalizedText("Review the selected core to read its compiled performance policies. Older builds may not report these details.", 13.0, VisualTheme.Muted));
        return Card(stackPanel);
    }

    private Control BuildRendererThreadingCard()
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 12.0
        };
        stackPanel.Children.Add(VisualTheme.LocalizedText("Renderer threading", 21.0, null, FontWeight.DemiBold));
        if (!Session.Supports("initialize.renderer_threading"))
        {
            stackPanel.Children.Add(VisualTheme.Text(((object)Session.ActiveBackend == null && (object)Session.LastBackendInfo == null) ? UiText.Get("Review the selected core to check whether it exposes renderer threading.") : UiText.Get("This core does not expose a selectable renderer construction mode. Its native behavior remains in use."), 13.0, VisualTheme.Muted));
            return Card(stackPanel);
        }
        SettingState performanceRendererSetting = Session.PerformanceRendererSetting;
        string text;
        JsonElement? effective;
        if ((object)performanceRendererSetting != null && performanceRendererSetting.EffectiveKnown)
        {
            effective = performanceRendererSetting.Effective;
            if (effective.HasValue)
            {
                JsonElement valueOrDefault = effective.GetValueOrDefault();
                text = Display(valueOrDefault).Replace('_', ' ');
                goto IL_00f8;
            }
        }
        text = "Read current state";
        goto IL_00f8;
    IL_00f8:
        string text2 = text;
        Controls children = stackPanel.Children;
        effective = performanceRendererSetting?.Persisted;
        children.Add(VisualTheme.LocalizedText("Active: " + text2 + ((!effective.HasValue) ? UiText.Get("\nNext launch: core default") : string.Concat(str1: Display(effective.GetValueOrDefault()).Replace('_', ' '), str0: UiText.Get("\nNext launch preference: "))), 14.0));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Threaded rendering can reduce stalls while new effects compile. First-use hitches can still occur. Try the single GPU thread option if a game has compatibility problems. Changes apply on the next launch.", 13.0, VisualTheme.Muted));
        Button button = VisualTheme.LocalizedButton("Choose next-session mode", async () =>
        {
            string choice = await Dialogs.ChooseAsync(this, UiText.Get("Renderer mode for the next launch"),
            [
                ("threaded_renderer", UiText.Get("Threaded rendering")),
                ("single_gpu_thread", UiText.Get("Single GPU thread"))
            ], UiText.Get("This saves a preference for this exact core revision. Finish saving in the game before relaunching. The active renderer will not change."));
            if (choice != null)
            {
                await MainActionAsync(() => Session.StageRendererThreadingAsync(choice));
            }
        });
        button.Name = "PerformanceRendererMode";
        stackPanel.Children.Add(Actions(button));
        return Card(stackPanel);
    }

    private void UpdatePerformanceReadouts()
    {
        if (_section != "Performance" || _performanceReadouts.Count == 0)
        {
            return;
        }
        PerformanceCaptureSummary performanceCaptureSummary = Session.CurrentPerformance?.Capture;
        string value;
        if ((object)performanceCaptureSummary == null)
        {
            value = "Awaiting gameplay";
        }
        else if (performanceCaptureSummary.Availability != PerformanceAvailability.Available)
        {
            value = "Native timing unavailable";
        }
        else
        {
            value = ((Session.State == RuntimeState.Running) ? "Recording" : "Capture paused");
        }
        Set("captureState", value);
        Set("captureContext", ((object)performanceCaptureSummary == null) ? "Launch any game on an instrumented core. NX records actual presentation intervals and keeps pauses in separate segments." : (performanceCaptureSummary.BackendId + " · " + performanceCaptureSummary.RendererThreading?.Replace('_', ' ') + " · " + performanceCaptureSummary.SampleCount.ToString("N0") + " retained intervals" + ((performanceCaptureSummary.Availability == PerformanceAvailability.Available) ? "" : ("\n" + performanceCaptureSummary.Message))));
        Set("p50", Ms(performanceCaptureSummary?.P50Ms));
        Set("p95", Ms(performanceCaptureSummary?.P95Ms));
        Set("p99", Ms(performanceCaptureSummary?.P99Ms));
        Set("maximum", Ms(performanceCaptureSummary?.MaxMs));
        if (_presentPlot != null)
        {
            _presentPlot.Samples = performanceCaptureSummary?.Samples.TakeLast(120).ToArray() ?? Array.Empty<PerformanceInterval>();
            _presentPlot.InvalidateVisual();
        }
        int? num = performanceCaptureSummary?.SampleCount;
        Set("plotScope", (num.HasValue && num.GetValueOrDefault() > 0) ? ("Latest " + Math.Min(120, performanceCaptureSummary.SampleCount) + " native intervals · scale 0–" + (_presentPlot?.ScaleMaximum ?? 0.0).ToString("0.0") + " ms · " + performanceCaptureSummary.MeasurementScope) : "No native interval samples yet. Average FPS is not converted into frame-pacing data.");
        Set("hitches", ((object)performanceCaptureSummary == null) ? "Long intervals: not measured" : $"Over 33.3 ms: {performanceCaptureSummary.HitchesOver33Point3Ms:N0}     Over 50 ms: {performanceCaptureSummary.HitchesOver50Ms:N0}     Over 100 ms: {performanceCaptureSummary.HitchesOver100Ms:N0}");
        Set("coverage", ((object)performanceCaptureSummary == null) ? "A capture begins at launch, or at an explicit new-capture boundary." : ($"{performanceCaptureSummary.SegmentCount:N0} segments · {performanceCaptureSummary.MissingIntervals:N0} missing intervals · {performanceCaptureSummary.SkippedSegments:N0} unobserved segments · {performanceCaptureSummary.EvictedIntervals:N0} older intervals outside the rolling window" + (performanceCaptureSummary.HasGaps ? "\nCoverage is incomplete. Review the capture before comparing runs." : "")));
        Set("cacheState", UiText.Get("Shader cache consumer: ") + Known(performanceCaptureSummary?.ShaderCacheEnabled) + " · CPU translation cache: " + Known(performanceCaptureSummary?.CpuCacheEnabled));
        ShaderCompilationSnapshot shaderCompilationSnapshot = Session.ShaderCompilation?.Snapshot;
        if ((object)shaderCompilationSnapshot == null)
        {
            Set("shaderProgress", UiText.Get("Native shader progress unavailable"));
            Set("shaderDetail", Session.ShaderCompilation?.Message ?? "Launch a game to observe this core's cache-loader callbacks.");
            return;
        }
        string text = ((!shaderCompilationSnapshot.CacheLoadReturned) ? (shaderCompilationSnapshot.CacheStage switch
        {
            "prepare" => "Preparing cache",
            "build" => "Building cached shaders",
            "package" => "Packaging cache",
            "complete" => "Native cache stage complete",
            _ => "Waiting for native callbacks",
        }) : "Cache load returned");
        string text2 = text;
        long? cacheCompleted = shaderCompilationSnapshot.CacheCompleted;
        string text3;
        if (cacheCompleted.HasValue)
        {
            long valueOrDefault = cacheCompleted.GetValueOrDefault();
            cacheCompleted = shaderCompilationSnapshot.CacheTotal;
            if (cacheCompleted.HasValue)
            {
                long valueOrDefault2 = cacheCompleted.GetValueOrDefault();
                text3 = $" · {valueOrDefault:N0} / {valueOrDefault2:N0}";
                goto IL_054d;
            }
        }
        text3 = "";
        goto IL_054d;
    IL_054d:
        Set("shaderProgress", text2 + text3);
        string[] array = new string[6];
        double? cacheLoadDurationMs = shaderCompilationSnapshot.CacheLoadDurationMs;
        string text4;
        if (cacheLoadDurationMs.HasValue)
        {
            double valueOrDefault3 = cacheLoadDurationMs.GetValueOrDefault();
            text4 = "Native cache-load call: " + (valueOrDefault3 / 1000.0).ToString("0.000") + " s\n";
        }
        else
        {
            text4 = "";
        }
        array[0] = text4;
        cacheCompleted = shaderCompilationSnapshot.ActivityIndicator;
        array[1] = ((!cacheCompleted.HasValue) ? "" : ("Recent native build activity (two-second indicator): " + cacheCompleted.GetValueOrDefault().ToString("N0") + ". This is not a pending-job count.\n"));
        array[2] = "Cache requested: ";
        array[3] = Known(shaderCompilationSnapshot.CacheRequested);
        array[4] = " · Consumer observation: ";
        array[5] = (shaderCompilationSnapshot.CacheConsumerObserved ? "reported" : "not yet reported");
        Set("shaderDetail", string.Concat(array));
        void Set(string key, string text5)
        {
            if (_performanceReadouts.TryGetValue(key, out TextBlock value2))
            {
                value2.Text = text5;
            }
        }
    }

    private string PerformanceCaptureDetails()
    {
        InfinityPerformanceRun currentPerformance = Session.CurrentPerformance;
        if ((object)currentPerformance == null)
        {
            return "No active capture. Launch a game, then use New capture and play to begin a separate measurement window.";
        }
        return JsonSerializer.Serialize(currentPerformance, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private async Task ExportPerformanceAsync()
    {
        string path = await Dialogs.SavePathAsync(this, UiText.Get("Export performance and diagnostics"), "nx-infinity-performance-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip");
        if (path != null)
        {
            await MainActionAsync(() => Session.ExportDiagnosticsAsync(path));
        }
    }

    private static string Ms(double? value)
    {
        if (!value.HasValue)
        {
            return "—";
        }
        return value.GetValueOrDefault().ToString("0.0", CultureInfo.InvariantCulture) + " ms";
    }

    private static string Known(bool? value)
    {
        if (value.HasValue)
        {
            if (value == true)
            {
                return "enabled";
            }
            return "disabled";
        }
        return "unknown";
    }

    private static string PlanStatus(PerformancePlanEntryStatus status)
    {
        return status switch
        {
            PerformancePlanEntryStatus.Ready => "Change proposed",
            PerformancePlanEntryStatus.AlreadyEffective => "Already effective",
            PerformancePlanEntryStatus.Unsupported => "Unsupported",
            PerformancePlanEntryStatus.EffectiveStateUnknown => "State unknown",
            PerformancePlanEntryStatus.RestartRequired => "Fresh load required",
            PerformancePlanEntryStatus.DependencyUnverified => "Dependency unverified",
            PerformancePlanEntryStatus.PendingSetting => "Pending change",
            _ => "Unavailable",
        };
    }

    private void InitializePresence()
    {
        _discord = new DiscordPresence(Session.Paths.DataRoot, Session.Paths.InstallRoot);
        Opened += (object? _, EventArgs _) =>
        {
            string environmentVariable = Environment.GetEnvironmentVariable("NX_DISCORD_BRIDGE_DIR");
            if (!string.IsNullOrWhiteSpace(environmentVariable) && Path.IsPathFullyQualified(environmentVariable) && Directory.Exists(environmentVariable))
            {
                try
                {
                    AvatarStore.WriteAtomic(Path.Combine(environmentVariable, "frontend-ready"), new byte[1] { 1 });
                }
                catch (IOException)
                {
                }
            }
        };
        _presenceStarted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _presenceTimer.Tick += async (object? _, EventArgs _) =>
        {
            await UpdatePresenceAsync();
        };
        _presenceTimer.Start();
    }

    public DiscordActivity? CurrentDiscordActivity()
    {
        if (_discord == null || !_discord.Preferences.Enabled)
        {
            return null;
        }
        DiscordPreferences preferences = _discord.Preferences;
        string text;
        string text2;
        if (Session.HasGame && preferences.ShareGame)
        {
            text = Session.ActiveGame?.DisplayName ?? "Playing a game";
            text2 = ((Session.State == RuntimeState.Paused) ? "Paused · Taking a breather" : (_customGameActivity ?? "Playing on NX Infinity"));
        }
        else
        {
            if (Session.HasGame || !preferences.ShareLauncher)
            {
                return null;
            }
            if (_customizingTheme)
            {
                text = "Creating a new look";
            }
            else
            {
                text = (_homeMode ? "At HOME" : "In the game library");
            }
            if (_customizingTheme)
            {
                text2 = "Customizing NX Infinity themes";
            }
            else
            {
                text2 = (_homeMode ? "Choosing the next adventure" : "Browsing the collection");
            }
        }
        return new DiscordActivity(Limit(text), Limit(text2), new
        {
            large_image = _discord.LogoAsset,
            large_text = "NX Infinity · Your games. Your universe."
        }, new
        {
            start = _presenceStarted
        });
        static string Limit(string text3)
        {
            if (text3.Length > 128)
            {
                return text3.Substring(0, 125) + "…";
            }
            return text3;
        }
    }

    private async Task UpdatePresenceAsync()
    {
        if (_discord == null || _publishingPresence || _closing)
        {
            return;
        }
        if (_presenceGame != Session.ActiveGame?.EntryId)
        {
            _presenceGame = Session.ActiveGame?.EntryId;
            _customGameActivity = null;
            _presenceStarted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        if (!_discord.Preferences.Enabled && !_presenceWasEnabled)
        {
            return;
        }
        _publishingPresence = true;
        try
        {
            await _discord.PublishAsync(CurrentDiscordActivity());
            _presenceWasEnabled = _discord.Preferences.Enabled;
        }
        finally
        {
            _publishingPresence = false;
        }
    }

    private Control BuildDiscordCard()
    {
        return Card(VisualTheme.Column(VisualTheme.LocalizedText("Discord Rich Presence", 18.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("Share your current game, HOME activity, or theme session with the NXI logo. You choose what is visible.", 14.0, VisualTheme.Muted), VisualTheme.LocalizedButton("Customize Discord presence", (Func<Task>)ShowDiscordSettingsAsync, (string?)null)));
    }

    private async Task ShowDiscordSettingsAsync()
    {
        DiscordPresence service = _discord ?? throw new InvalidOperationException("Discord presence is unavailable.");
        Window dialog = Dialogs.Create("Discord Rich Presence", 740.0, 660.0);
        dialog.Name = "DiscordPresenceSettings";
        CheckBox enabled = VisualTheme.LocalizedCheckBox("Show NX Infinity on my Discord profile", service.Preferences.Enabled);
        CheckBox shareGame = VisualTheme.LocalizedCheckBox("Show the game I am playing", service.Preferences.ShareGame);
        CheckBox shareLauncher = VisualTheme.LocalizedCheckBox("Show HOME, library, and theme activity", service.Preferences.ShareLauncher);
        TextBox detail = new TextBox
        {
            Name = "DiscordGameActivity",
            Text = _customGameActivity,
            PlaceholderText = "Optional activity for the current game",
            MaxLength = 128,
            IsEnabled = Session.HasGame
        };
        ComboBox presets = new ComboBox
        {
            ItemsSource = ActivitySuggestions(Session.ActiveGame?.DisplayName),
            MinWidth = 280.0,
            PlaceholderText = "Choose an activity for this game",
            IsEnabled = Session.HasGame
        };
        presets.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
        {
            if (presets.SelectedItem is string text)
            {
                detail.Text = text;
            }
        };
        TextBlock status = VisualTheme.Text(service.Status, 13.0, VisualTheme.Muted);
        status.Name = "DiscordPresenceStatus";
        Border border = new Border();
        border.Background = Brush.Parse("#232428");
        border.CornerRadius = new CornerRadius(16.0);
        border.Padding = new Thickness(20.0);
        border.Child = VisualTheme.Row(new BrandMark
        {
            Width = 72.0,
            Height = 72.0
        }, VisualTheme.Column(VisualTheme.LocalizedText("NX INFINITY", 12.0, Brush.Parse("#CFB7FF"), FontWeight.Bold), VisualTheme.Text(Session.ActiveGame?.DisplayName ?? (_homeMode ? "At HOME" : "In the game library"), 21.0, Brushes.White, FontWeight.DemiBold), VisualTheme.Text(_customGameActivity ?? "Choosing the next adventure", 14.0, Brush.Parse("#B5BAC1"))));
        Border border2 = border;
        dialog.Content = PageScroll(VisualTheme.Column(Heading("Make your next adventure visible", "Discord desktop connects locally. No Discord password or account token is needed."), border2, enabled, shareGame, shareLauncher, VisualTheme.LocalizedText("In-game activity"), presets, detail, VisualTheme.LocalizedText("Specific locations and challenges are selected by you. The current game, paused state, HOME, and theme activity update automatically.", 12.0, VisualTheme.Muted), status, Actions(VisualTheme.LocalizedButton("Close", () =>
        {
            dialog.Close();
        }), VisualTheme.LocalizedButton("Save & connect", (Func<Task>)Save, "primary"))));
        await Dialogs.ShowAsync<object>(this, dialog);
        async Task Save()
        {
            try
            {
                service.Save(service.Preferences with
                {
                    Enabled = (enabled.IsChecked == true),
                    ShareGame = (shareGame.IsChecked == true),
                    ShareLauncher = (shareLauncher.IsChecked == true)
                });
                _presenceGame = Session.ActiveGame?.EntryId;
                _customGameActivity = ((Session.HasGame && !string.IsNullOrWhiteSpace(detail.Text)) ? detail.Text.Trim() : null);
                await UpdatePresenceAsync();
                status.Text = service.Status;
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }
    }

    public static string[] ActivitySuggestions(string? game)
    {
        if (game != null && game.Contains("Tears of the Kingdom", StringComparison.OrdinalIgnoreCase))
        {
            return new string[4] { "Wandering the Lost Woods", "Exploring Hyrule", "Diving into the Depths", "Building something impossible" };
        }
        if ((game == null || !game.Contains("Violet", StringComparison.OrdinalIgnoreCase)) && (game == null || !game.Contains("Scarlet", StringComparison.OrdinalIgnoreCase)))
        {
            if (game != null && game.Contains("Mario Kart", StringComparison.OrdinalIgnoreCase))
            {
                return new string[4] { "Racing on Rainbow Road", "Chasing the next Grand Prix trophy", "Racing with friends", "Beating a time trial ghost" };
            }
            if (game != null && game.Contains("Hello Kitty", StringComparison.OrdinalIgnoreCase))
            {
                return new string[3] { "Exploring the island with friends", "Making gifts for everyone", "Discovering a new adventure" };
            }
            return new string[4] { "Exploring a new world", "Taking on the next challenge", "Playing with friends", "Finishing one more quest" };
        }
        return new string[4] { "Battling the Pokémon League", "Exploring Paldea", "Hunting for shiny Pokémon", "Taking on a Tera Raid" };
    }

    private Task RecoverLastSessionAsync()
    {
        return ReviewCoreRecoveryAsync(Session.RecoveryBackendId ?? Session.SelectedBackendId);
    }

    private Task RecoverSelectedSessionAsync()
    {
        return ReviewCoreRecoveryAsync(Session.SelectedBackendId);
    }

    private async Task ReviewCoreRecoveryAsync(string? backendId)
    {
        BackendChoice backend = Session.Backends.FirstOrDefault((BackendChoice core) => core.Id == backendId);
        if ((object)backend == null)
        {
            await Dialogs.MessageAsync(this, UiText.Get("Recovery unavailable"), UiText.Get("Select an installed core before reviewing recovery."));
            return;
        }
        if (!Session.Busy)
        {
            try
            {
                try
                {
                    if (Session.RecoveryRequiresStop(backend.Id))
                    {
                        if (!(await Dialogs.ConfirmAsync(this, UiText.Get("Stop ") + backend.Name + UiText.Get(" before recovery?"), UiText.Get("This core still has an active or paused game. Save in-game first. Continuing stops that session normally before checking its marker; unsaved progress cannot be restored."), UiText.Get("Stop game and review"))))
                        {
                            return;
                        }
                        long beforeStop = Session.OperationFailureSequence;
                        await Session.StopAsync();
                        if (Session.OperationFailureSequence != beforeStop)
                        {
                            throw new NxException(Session.ErrorCode ?? "core_error", Session.ErrorMessage ?? "The session could not stop safely.");
                        }
                    }
                    SessionRecoveryReview review = await Session.ReviewSessionRecoveryAsync(backend.Id);
                    if (!review.Required)
                    {
                        await Dialogs.MessageAsync(this, UiText.Get("No recovery needed for ") + backend.Name, UiText.Get("This core has no abandoned session marker. Select it and try launching again. No saves, keys or firmware were changed."));
                        return;
                    }
                    if (!(await Dialogs.ConfirmAsync(this, UiText.Get("Recover ") + backend.Name + "?", UiText.Get("The previous ") + backend.Name + UiText.Get(" session did not close cleanly. NX will archive its exact marker and allow another launch. ") + UiText.Get("This action applies only to ") + backend.Name + ". " + UiText.Get("It does not delete, import or replace saves, keys, firmware, settings or caches. Active profile locks and interrupted data transactions remain protected."), UiText.Get("Archive marker and recover"))))
                    {
                        return;
                    }
                    long beforeRecovery = Session.OperationFailureSequence;
                    await Session.RecoverSessionAsync(review);
                    if (Session.OperationFailureSequence != beforeRecovery)
                    {
                        throw new NxException(Session.ErrorCode ?? "recovery_failed", Session.ErrorMessage ?? "Recovery was not applied.");
                    }
                    await Dialogs.MessageAsync(this, backend.Name + UiText.Get(" recovered"), UiText.Get("The previous marker was archived. Select ") + backend.Name + UiText.Get(" and press Play again. Your saves, keys and firmware were not changed."));
                }
                catch (Exception ex)
                {
                    await Dialogs.MessageAsync(this, UiText.Get("Recovery not applied to ") + backend.Name, ex.Message);
                }
                return;
            }
            finally
            {
                RenderCurrentPage();
            }
        }
        await Dialogs.MessageAsync(this, UiText.Get("Operation in progress"), UiText.Get("Finish or cancel the current operation before reviewing recovery."));
    }

    private Control BuildSaveMigrationCard()
    {
        string label;
        if (Session.SaveMigrationReady)
        {
            label = (Session.AutomaticSaveMigrationEnabled ? UiText.Get("Automatic import: on") : UiText.Get("Automatic import: off"));
        }
        else
        {
            label = UiText.Get("Checking preference…");
        }
        Button button = VisualTheme.Button(label, () => MainActionAsync(() => Session.SetAutomaticSaveMigrationAsync(!Session.AutomaticSaveMigrationEnabled)));
        button.Name = "AutomaticSaveMigration";
        button.IsEnabled = Session.SaveMigrationReady;
        Button button2 = VisualTheme.LocalizedButton("Scan for saves", () => MainActionAsync(Session.RefreshSaveMigrationAsync));
        button2.Name = "ScanExternalSaves";
        Button button3 = VisualTheme.LocalizedButton("Import matching saves", () => MainActionAsync(Session.RetrySaveMigrationAsync), "primary");
        button3.Name = "ImportMatchingSaves";
        int isEnabled;
        if (Session.SaveMigrationReady)
        {
            SaveMigrationDiscoveryReport? saveMigrationDiscovery = Session.SaveMigrationDiscovery;
            if ((object)saveMigrationDiscovery != null && saveMigrationDiscovery.Candidates.Count > 0)
            {
                isEnabled = (Session.Backends.Any((BackendChoice backendChoice) => SaveMigrationStore.SupportsAutomaticImport(backendChoice.Id)) ? 1 : 0);
                goto IL_0116;
            }
        }
        isEnabled = 0;
        goto IL_0116;
    IL_0116:
        button3.IsEnabled = (byte)isEnabled != 0;
        Button button4 = VisualTheme.LocalizedButton("Import Kenji / Ryujinx save", (Func<Task>)ImportIndexedSaveAsync, (string?)null);
        button4.Name = "ImportIndexedSaves";
        button4.IsEnabled = Session.SaveMigrationReady && Session.Backends.Any((BackendChoice core) => SaveMigrationStore.SupportsIndexedImport(core.Id));
        Button button5 = VisualTheme.LocalizedButton("Add portable location", (Func<Task>)AddPortableSaveLocationAsync, (string?)null);
        button5.Name = "AddPortableSaveLocation";
        Button button6 = VisualTheme.LocalizedButton("Locations and results", (Func<Task>)ReviewSaveMigrationAsync, (string?)null);
        button6.Name = "ReviewSaveMigration";
        StackPanel stackPanel = VisualTheme.Column(VisualTheme.LocalizedText("Saves from other emulators", 22.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("NX Infinity checks common emulator folders and copies verified matches into empty save destinations. Your original saves stay in place.", 14.0, VisualTheme.Muted), Actions(button, button2, button3, button4), VisualTheme.Text(SaveMigrationSummary(), 14.0));
        stackPanel.Name = "SaveMigrationPanel";
        foreach (BackendChoice backend in Session.Backends)
        {
            SaveMigrationCoreStatus saveMigrationCoreStatus = Session.SaveMigrationCoreStatuses.FirstOrDefault((SaveMigrationCoreStatus item) => item.BackendId == backend.Id);
            Grid grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("160,*"),
                ColumnSpacing = 14.0
            };
            grid.Children.Add(VisualTheme.Text(backend.DisplayName, 14.0, null, FontWeight.DemiBold));
            TextBlock textBlock = VisualTheme.Text(SaveMigrationStatusLabel(saveMigrationCoreStatus?.Status ?? "not_checked"), 14.0, VisualTheme.Muted);
            Grid.SetColumn(textBlock, 1);
            grid.Children.Add(textBlock);
            stackPanel.Children.Add(grid);
            bool flag = (object)saveMigrationCoreStatus != null;
            if (flag)
            {
                bool flag2;
                switch (saveMigrationCoreStatus.Status)
                {
                    case "target_allocation_unavailable":
                    case "target_conflict":
                    case "native_verification_unavailable":
                        flag2 = true;
                        break;
                    default:
                        flag2 = false;
                        break;
                }
                flag = flag2;
            }
            if (flag)
            {
                stackPanel.Children.Add(VisualTheme.Text(saveMigrationCoreStatus.Message, 13.0, VisualTheme.Muted));
            }
        }
        SaveMigrationDiscoveryReport saveMigrationDiscovery2 = Session.SaveMigrationDiscovery;
        if ((object)saveMigrationDiscovery2 != null)
        {
            IReadOnlyList<SaveMigrationCandidate> candidates = saveMigrationDiscovery2.Candidates;
            if (candidates != null && candidates.Count > 0)
            {
                StackPanel stackPanel2 = new StackPanel
                {
                    Spacing = 6.0
                };
                foreach (SaveMigrationCandidate item in saveMigrationDiscovery2.Candidates.Take(50))
                {
                    stackPanel2.Children.Add(VisualTheme.Text(SaveOriginName(item.Origin) + " · " + (item.TitleId ?? "Title not verified") + " · " + item.Message, 13.0, VisualTheme.Muted));
                }
                stackPanel.Children.Add(new Expander
                {
                    Header = "Discovered saves (not necessarily imported)",
                    IsExpanded = true,
                    Content = stackPanel2
                });
            }
        }
        stackPanel.Children.Add(Actions(button5, button6));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Automatic import requires an existing, native-verified empty destination. If none exists, launch the matching game with the correct core and user profile, create a save, close it, then use a reviewed import. Original saves and rollback snapshots are preserved.", 13.0, VisualTheme.Muted));
        stackPanel.Children.Add(VisualTheme.LocalizedText("This copies matching saves into NX Infinity. Playing elsewhere does not overwrite your newer NX progress, and changes are not written back to the other emulator.", 13.0, VisualTheme.Muted));
        return Card(stackPanel, accent: true);
    }

    private string SaveMigrationSummary()
    {
        if (!Session.SaveMigrationReady)
        {
            return UiText.Get("The first scan starts when the launcher opens.");
        }
        SaveMigrationDiscoveryReport saveMigrationDiscovery = Session.SaveMigrationDiscovery;
        string text = (((object)saveMigrationDiscovery == null) ? UiText.Get("No scan completed yet.") : UiText.Format("{0} possible save(s) found · Last scan {1:t}", saveMigrationDiscovery.Candidates.Count, saveMigrationDiscovery.ObservedUtc.ToLocalTime()));
        int num = Session.LastSaveMigrationReport?.Outcomes.Count((SaveMigrationOutcome outcome) => outcome.Imported) ?? 0;
        if ((object)Session.LastSaveMigrationReport != null || Session.SaveMigrationCoreStatuses.Count > 0)
        {
            text += UiText.Format(" · {0} imported in the latest attempt", num);
        }
        if (Session.PortableSaveLocations.Count > 0)
        {
            text += UiText.Format(" · {0} portable location(s)", Session.PortableSaveLocations.Count);
        }
        return text;
    }

    private static string SaveOriginName(string origin)
    {
        return origin switch
        {
            "eden" => "Eden",
            "citron" => "Citron",
            "yuzu" => "yuzu",
            "suyu" => "suyu",
            "sudachi" => "Sudachi",
            "ryujinx" => "Ryujinx",
            "ryujinx-canary" => "Ryujinx Canary",
            "kenji-nx" => "Kenji-NX",
            _ => origin,
        };
    }

    private static string SaveMigrationStatusLabel(string status)
    {
        return UiText.Get(status switch
        {
            "not_checked" => "Waiting for a scan",
            "automatic_disabled" => "Automatic import is off",
            "scanned" => "Scanned · import has not been requested",
            "no_sources" => "No supported source saves found",
            "format_verification_unavailable" => "This save format is not supported yet",
            "deferred_busy" => "Waiting for the loaded game to close",
            "target_allocation_unavailable" => "No existing empty save destination",
            "target_conflict" => "Existing saves preserved · no empty match",
            "target_limit" => "Too many destinations for one import",
            "native_verification_unavailable" => "Native verification needs attention",
            "ready" => "Native destinations verified",
            "complete" => "Import review complete",
            "rolled_back" => "Import rolled back",
            "review_required" => "Review the import results",
            _ => status.Replace('_', ' '),
        });
    }

    private async Task AddPortableSaveLocationAsync()
    {
        string origin = await Dialogs.ChooseAsync(this, UiText.Get("Which emulator uses this folder?"), new string[8] { "eden", "citron", "yuzu", "suyu", "sudachi", "ryujinx", "ryujinx-canary", "kenji-nx" }.Select((string item) => (item: item, SaveOriginName(item))).ToArray(), UiText.Get("Choose the emulator's data folder, not an individual save folder. Kenji/Ryujinx sources are verified through their native save index before importing."));
        if (origin == null)
        {
            return;
        }
        string path = await Dialogs.BrowseAsync(this, UiText.Get("Choose the emulator data folder"), Array.Empty<string>(), directory: true);
        if (path != null)
        {
            await MainActionAsync(() => Session.AddPortableSaveLocationAsync(origin, path));
        }
    }

    private async Task ReviewSaveMigrationAsync()
    {
        List<(string, string)> list = new List<(string, string)>
        {
            ("results", UiText.Get("Latest scan and per-core results")),
            ("receipts", UiText.Get("Imported saves and guarded rollback"))
        };
        if (Session.PortableSaveLocations.Count != 0)
        {
            list.Add(("locations", UiText.Get("Manage portable locations")));
        }
        switch (await Dialogs.ChooseAsync(this, UiText.Get("Saves from other emulators"), list, UiText.Get("A possible save is only a discovery result. NX Infinity verifies its native identity before copying it into an empty destination.")))
        {
            case "results":
                await Dialogs.MessageAsync(this, UiText.Get("Save scan and import results"), SaveMigrationReviewText());
                break;
            case "receipts":
                await ReviewSaveMigrationReceiptsAsync();
                break;
            case "locations":
                await ManagePortableSaveLocationsAsync();
                break;
        }
    }

    private string SaveMigrationReviewText()
    {
        List<string> list = new List<string> { SaveMigrationSummary() };
        SaveMigrationDiscoveryReport saveMigrationDiscovery = Session.SaveMigrationDiscovery;
        if ((object)saveMigrationDiscovery != null)
        {
            list.Add(UiText.Get("Locations checked") + "\n" + string.Join("\n", from location in saveMigrationDiscovery.Locations.Take(64)
                                                                                select SaveOriginName(location.Origin) + " · " + UiText.Format("{0} possible save(s)", location.CandidateCount) + " · " + location.Message));
            if (saveMigrationDiscovery.Candidates.Count != 0)
            {
                list.Add(UiText.Get("Possible saves") + "\n" + string.Join("\n", from candidate in saveMigrationDiscovery.Candidates.Take(64)
                                                                                 select SaveOriginName(candidate.Origin) + " · " + (candidate.TitleId ?? UiText.Get("Title not verified")) + " · " + candidate.Message));
            }
        }
        foreach (SaveMigrationCoreStatus saveMigrationCoreStatus in Session.SaveMigrationCoreStatuses)
        {
            list.Add(SaveOriginName(saveMigrationCoreStatus.BackendId) + " · " + SaveMigrationStatusLabel(saveMigrationCoreStatus.Status) + "\n" + saveMigrationCoreStatus.Message + "\n" + UiText.Get("Revision: ") + saveMigrationCoreStatus.BackendRevision + "\n" + UiText.Get("Native destinations checked: ") + saveMigrationCoreStatus.EligibleTargets + "\n" + UiText.Get("Imported in this attempt: ") + saveMigrationCoreStatus.ImportedCount);
        }
        SaveMigrationReport lastSaveMigrationReport = Session.LastSaveMigrationReport;
        if ((object)lastSaveMigrationReport != null)
        {
            list.Add(UiText.Get("Latest attempt") + "\n" + string.Join("\n", from outcome in lastSaveMigrationReport.Outcomes.Take(128)
                                                                             select SaveOriginName(outcome.BackendId) + " · " + outcome.TitleId + " · " + outcome.Message));
        }
        return string.Join("\n\n", list);
    }

    private async Task ManagePortableSaveLocationsAsync()
    {
        PortableSaveLocation[] source = Session.PortableSaveLocations.ToArray();
        string selected = await Dialogs.ChooseAsync(this, UiText.Get("Portable save locations"), source.Select((PortableSaveLocation location, int index) => (Id: location.Id, SaveOriginName(location.Origin) + " · " + UiText.Format("Location {0}", index + 1))).ToArray(), UiText.Get("Select a location to remove it from future scans. The emulator folder and its saves remain in place."));
        if (selected != null)
        {
            await MainActionAsync(() => Session.RemovePortableSaveLocationAsync(selected));
        }
    }

    private async Task ReviewSaveMigrationReceiptsAsync()
    {
        SaveMigrationReceipt[] receipts = Session.SaveMigrationState.Receipts.OrderByDescending((SaveMigrationReceipt saveMigrationReceipt) => saveMigrationReceipt.CreatedUtc).Take(128).ToArray();
        string selected = await Dialogs.ChooseAsync(this, UiText.Get("Imported saves"), receipts.Select((SaveMigrationReceipt saveMigrationReceipt) => (ReceiptId: saveMigrationReceipt.ReceiptId, SaveOriginName(saveMigrationReceipt.BackendId) + " · " + saveMigrationReceipt.TitleId + "\n" + saveMigrationReceipt.CreatedUtc.ToLocalTime().ToString("g") + " · " + SaveMigrationStatusLabel(saveMigrationReceipt.Status))).ToArray(), UiText.Get("Rollback is available only while the destination still matches the imported snapshot. New game progress is preserved."));
        if (selected == null)
        {
            return;
        }
        SaveMigrationReceipt receipt = receipts.Single((SaveMigrationReceipt item) => item.ReceiptId == selected);
        string text = UiText.Get("Title: ") + receipt.TitleId + UiText.Get("\nUser: ") + receipt.UserId + "\n" + UiText.Get("Core: ") + SaveOriginName(receipt.BackendId) + "\n" + UiText.Get("Revision: ") + receipt.BackendRevision + "\n" + UiText.Get("Imported from: ") + SaveOriginName(receipt.SourceOrigin) + "\n" + UiText.Get("Format: ") + receipt.FormatId + " / " + receipt.FormatVersion + "\n" + UiText.Get("Receipt: ") + receipt.ReceiptId;
        if (!receipt.CanRollback)
        {
            await Dialogs.MessageAsync(this, UiText.Get("Save import receipt"), text + UiText.Get("\n\nRollback is unavailable for this receipt."));
        }
        else if (await Dialogs.ConfirmAsync(this, UiText.Get("Roll back this save import?"), text + UiText.Get("\n\nThe preserved destination snapshot will be restored only if the imported save has not changed. A loaded game or newer progress prevents rollback. The source save stays in place."), UiText.Get("Roll back unchanged import")))
        {
            await MainActionAsync(() => Session.RollbackSaveMigrationAsync(receipt.ReceiptId));
        }
    }

    private async Task SelectGameAsync()
    {
        IEnumerable<GameLibraryEntry> source = Session.Library.List(new LibraryQuery(null, FavoritesOnly: false, LibraryContentKind.Game, LibrarySort.Name, 0, 2000)).Where(LibraryContentClassification.IsAvailableGame);
        string text = await Dialogs.ChooseAsync(this, UiText.Get("Choose a game"), source.Select((GameLibraryEntry g) => (EntryId: g.EntryId, DisplayName: g.DisplayName)).ToArray());
        if (text != null)
        {
            _selectedEntry = text;
            RenderCurrentPage();
        }
    }

    private Control BuildContentPage()
    {
        GameLibraryEntry game = SelectedGame;
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 17.0
        };
        stackPanel.Children.Add(Heading(UiText.Get("Content"), UiText.Get("Local prerequisites, updates, DLC, and exact-build mods")));
        bool flag = Session.ServicesBackendId == Session.SelectedBackendId;
        if (!_homeMode)
        {
            WrapPanel wrapPanel = new WrapPanel();
            (string, string)[] array = new (string, string)[4]
            {
                ("Setup", "Keys & firmware"),
                ("Updates", "Updates & DLC"),
                ("Mods", "Mods"),
                ("Review", "Review files")
            };
            for (int i = 0; i < array.Length; i++)
            {
                (string, string) tuple = array[i];
                string item = tuple.Item1;
                string item2 = tuple.Item2;
                string target = item;
                Button button = VisualTheme.Button(item2, () =>
                {
                    _contentTab = target;
                    RenderCurrentPage();
                }, (_contentTab == item) ? "selected" : "subtle");
                button.Name = "ContentTab" + item;
                wrapPanel.Children.Add(button);
            }
            stackPanel.Children.Add(wrapPanel);
        }
        if (!_homeMode && _contentTab == "Review")
        {
            GameLibraryEntry[] array2 = (from g in Session.Library.List(new LibraryQuery(null, FavoritesOnly: false, null, LibrarySort.Name, 0, 2000))
                                         where !LibraryContentClassification.IsGame(g)
                                         select g).ToArray();
            stackPanel.Children.Add(VisualTheme.LocalizedText("Updates, DLC and files awaiting native identification stay outside the game library.", 14.0, VisualTheme.Muted));
            _contentReviewOffset = Math.Clamp(_contentReviewOffset, 0, Math.Max(0, (array2.Length - 1) / 50 * 50));
            foreach (GameLibraryEntry file in array2.Skip(_contentReviewOffset).Take(50))
            {
                Button button2 = VisualTheme.LocalizedButton("Identify", () => MainActionAsync(() => Session.InspectAgainAsync(file.EntryId)));
                button2.IsEnabled = (object)Session.SelectedBackend != null && !Session.Busy;
                Button button3 = VisualTheme.LocalizedButton("Remove from list", async () =>
                {
                    if (await Dialogs.ConfirmAsync(this, "Remove from list?", "The original file is preserved.", "Remove"))
                    {
                        await MainActionAsync(() => Session.RunAsync("Removing library entry", (CancellationToken _) =>
                        {
                            Session.Library.Remove(file.EntryId);
                            return Task.CompletedTask;
                        }));
                    }
                });
                Grid grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                    ColumnSpacing = 10.0
                };
                string displayName = file.DisplayName;
                string text;
                if (file.Kind == LibraryContentKind.DownloadableContent)
                {
                    text = "DLC";
                }
                else
                {
                    text = ((file.Kind == LibraryContentKind.Update) ? "Update" : "Needs identification");
                }
                TextBlock textBlock = VisualTheme.Text(displayName + " · " + text, 13.0);
                textBlock.TextWrapping = TextWrapping.NoWrap;
                textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTip.SetTip(textBlock, file.DisplayName);
                grid.Children.Add(textBlock);
                Grid.SetColumn(button2, 1);
                grid.Children.Add(button2);
                Grid.SetColumn(button3, 2);
                grid.Children.Add(button3);
                stackPanel.Children.Add(grid);
            }
            if (array2.Length == 0)
            {
                stackPanel.Children.Add(VisualTheme.LocalizedText("No files need review.", 14.0));
            }
            if (array2.Length > 50)
            {
                Button button4 = VisualTheme.LocalizedButton("Previous", () =>
                {
                    _contentReviewOffset -= 50;
                    RenderCurrentPage();
                });
                button4.IsEnabled = _contentReviewOffset > 0;
                Button button5 = VisualTheme.LocalizedButton("Next", () =>
                {
                    _contentReviewOffset += 50;
                    RenderCurrentPage();
                });
                button5.IsEnabled = _contentReviewOffset + 50 < array2.Length;
                stackPanel.Children.Add(Actions(button4, button5));
            }
            return PageScroll(stackPanel);
        }
        if (_homeMode || _contentTab == "Setup")
        {
            stackPanel.Children.Add(BuildSharedSetupCard());
        }
        if (!_homeMode && _contentTab == "Setup")
        {
            return PageScroll(stackPanel);
        }
        if (Session.LastFailure?.BackendId == Session.SelectedBackendId)
        {
            string errorRecoveryMessage = Session.ErrorRecoveryMessage;
            if (errorRecoveryMessage != null)
            {
                stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("Resolve the last issue", 17.0, null, FontWeight.DemiBold), VisualTheme.Text(errorRecoveryMessage, 14.0, VisualTheme.Muted))));
            }
        }
        stackPanel.Children.Add(Actions(VisualTheme.Button(game?.DisplayName ?? UiText.Get("Choose a game"), (Func<Task>)SelectGameAsync, (string?)null)));
        if (_homeMode || _contentTab == "Updates")
        {
            stackPanel.Children.Add(Actions(VisualTheme.LocalizedButton("Add update", () => AddContentAsync(LibraryContentKind.Update)), VisualTheme.LocalizedButton("Add DLC", () => AddContentAsync(LibraryContentKind.DownloadableContent))));
        }
        if ((object)game == null)
        {
            stackPanel.Children.Add(Card(VisualTheme.LocalizedText("Choose a library game to inspect its update, DLC and mod selection.", 17.0)));
            return PageScroll(stackPanel);
        }
        GameContentConfiguration config = ((flag && Session.ContentConfiguration?.TitleId == InfinitySession.TitleOf(game)) ? Session.ContentConfiguration : null);
        bool flag2 = InfinitySession.TitleOf(game) != null;
        bool flag3 = Session.Supports("content.configuration.get") && Session.Supports("content.configuration.set");
        Button button6 = VisualTheme.LocalizedButton("Read native selection", () => MainActionAsync(() => Session.ReadContentConfigurationAsync(game.EntryId)));
        button6.IsEnabled = flag2 & flag3;
        Button button7 = VisualTheme.LocalizedButton("Choose update and DLC", () => ConfigureContentAsync(game));
        button7.IsEnabled = flag2 & flag3;
        if (_homeMode || _contentTab == "Updates")
        {
            stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("Updates and DLC", 20.0, null, FontWeight.DemiBold), VisualTheme.Text(((object)config == null) ? UiText.Get("Read the native selection before changing it. Content is validated by the core; a folder name does not establish an installed version.") : (UiText.Get("Update: ") + ((config.UpdatePath == null) ? UiText.Get("Base game") : Path.GetFileName(config.UpdatePath)) + UiText.Get("\nDLC containers: ") + config.DlcPaths.Length + UiText.Get("\nNative configuration: ") + (config.Configured ? UiText.Get("stored") : UiText.Get("not stored")) + UiText.Get(" · Loader consumption: ") + (config.Consumed ? UiText.Get("observed") : UiText.Get("not observed")) + (config.RequiresRestart ? UiText.Get(" · next load required") : "")), 14.0, VisualTheme.Muted), Actions(button6, button7, VisualTheme.LocalizedButton("Content evidence", () => Dialogs.MessageAsync(this, UiText.Get("Native content evidence"), ((object)config == null) ? UiText.Get("Read the native selection first.") : string.Join("\n", config.Evidence)))))));
        }
        if (!flag3 && (_homeMode || _contentTab == "Updates"))
        {
            stackPanel.Children.Add(VisualTheme.Text(((object)Session.LastBackendInfo == null) ? UiText.Get("Check this core's prerequisites to read its content-selection capabilities.") : UiText.Get("Native update and DLC selection is unavailable for this core revision."), 13.0, VisualTheme.Muted));
        }
        if (!flag2)
        {
            stackPanel.Children.Add(Actions(VisualTheme.LocalizedText("Inspect the game after setting up local prerequisites to unlock title-specific actions.", 13.0, VisualTheme.Muted), VisualTheme.LocalizedButton("Inspect selected game", () => MainActionAsync(() => Session.InspectAgainAsync(game.EntryId)))));
        }
        if (!_homeMode && _contentTab == "Updates")
        {
            return PageScroll(stackPanel);
        }
        GameBuildIdentity binding = Session.BuildIdentityFor(game);
        StackPanel stackPanel2 = new StackPanel
        {
            Spacing = 12.0
        };
        stackPanel2.Children.Add(VisualTheme.LocalizedText("Mods", 20.0, null, FontWeight.DemiBold));
        stackPanel2.Children.Add(VisualTheme.Text(((object)binding == null) ? UiText.Get("Inspect this game with the selected core first. Mod imports require its verified full executable build ID.") : (UiText.Get("Bound to ") + binding.TitleId + " / " + binding.BuildId + UiText.Get(". Choose a folder containing romfs/ or exefs/, or an NX manifest bundle. Imports start disabled until enabled below.")), 13.0, VisualTheme.Muted));
        Button button8 = VisualTheme.LocalizedButton("Read exact-build mods", () => MainActionAsync(() => Session.ReadModLibraryAsync(game.EntryId)));
        button8.IsEnabled = (object)binding != null;
        Button button9 = VisualTheme.LocalizedButton("Import mod", async () =>
        {
            string text2 = await Dialogs.ChooseAsync(this, "Import mod",
            [
                ("zip", "ZIP archive"),
                ("folder", "Extracted folder")
            ]);
            if (text2 != null)
            {
                string text3 = await Dialogs.BrowseAsync(this, "Choose a mod", (!(text2 == "zip")) ? Array.Empty<string>() : new string[1] { ".zip" }, text2 == "folder");
                if (text3 != null)
                {
                    await ReviewModImportAsync(text3, game);
                }
            }
        });
        button9.Name = "ImportMod";
        button9.IsEnabled = (object)binding != null;
        Button button10 = VisualTheme.LocalizedButton("Read native mount state", () => MainActionAsync(() => Session.ReadNativeModsAsync(game.EntryId)));
        button10.IsEnabled = flag2 && Session.Supports("mods.get");
        stackPanel2.Children.Add(Actions(button8, button9, button10, VisualTheme.LocalizedButton("Open mod directory", () =>
        {
            DesktopShortcuts.OpenFolder(Path.Combine(Session.Workspace.RootPath, "nx-services", "mods"));
        })));
        if ((object)binding != null && Session.ModLibraryBinding == binding)
        {
            if (Session.InstalledMods.Count == 0)
            {
                stackPanel2.Children.Add(VisualTheme.LocalizedText("No NX mod bundles are installed for this build.", 14.0, VisualTheme.Muted));
            }
            foreach (InstalledMod mod in Session.InstalledMods)
            {
                string id = mod.Selection.ModId;
                bool enabled = mod.Selection.Enabled;
                stackPanel2.Children.Add(Actions(VisualTheme.Button((enabled ? UiText.Get("Enabled  ·  ") : UiText.Get("Disabled  ·  ")) + id + "  " + mod.Receipt.Version, () => MainActionAsync(() => Session.ToggleModAsync(binding, id, !enabled))), VisualTheme.LocalizedButton("Receipt", () => Dialogs.MessageAsync(this, UiText.Get("Mod receipt"), JsonSerializer.Serialize(mod.Receipt, new JsonSerializerOptions
                {
                    WriteIndented = true
                }))), VisualTheme.LocalizedButton("Remove", async () =>
                {
                    if (await Dialogs.ConfirmAsync(this, "Remove mod?", "Remove " + id + " from this game? The original download and rollback history are preserved.", "Remove"))
                    {
                        await MainActionAsync(() => Session.RemoveModAsync(binding, id));
                    }
                })));
            }
            stackPanel2.Children.Add(VisualTheme.LocalizedButton("Restore previous mod selection", () => MainActionAsync(() => Session.RollbackModsAsync(binding))));
        }
        NativeModState nativeMods = Session.NativeMods;
        if ((object)nativeMods != null && nativeMods.TitleId == InfinitySession.TitleOf(game))
        {
            stackPanel2.Children.Add(VisualTheme.LocalizedText("Native state: " + nativeMods.Status + UiText.Get(" · Loader observation: ") + (nativeMods.LoaderObserved ? UiText.Get("reported") : UiText.Get("not reported")) + "\n" + nativeMods.Reason + "\n" + string.Join("\n", nativeMods.Evidence), 13.0, VisualTheme.Muted));
        }
        stackPanel.Children.Add(Card(stackPanel2));
        return PageScroll(stackPanel);
    }

    private async Task ImportPrerequisiteAsync(string kind)
    {
        string selected = SelectedGame?.EntryId;
        bool directory = false;
        if (kind == "firmware")
        {
            string text = await Dialogs.ChooseAsync(this, UiText.Get("Install local firmware"),
            [
                ("archive", UiText.Get("ZIP package or XCI update source")),
                ("folder", UiText.Get("Extracted firmware folder"))
            ], UiText.Get("Your source is retained once. Each installed core reports whether it supports and validates that source."));
            if (text == null)
            {
                return;
            }
            directory = text == "folder";
        }
        string source = await Dialogs.BrowseAsync(this, (kind == "keys") ? UiText.Get("Choose prod.keys, title.keys, dev.keys or console.keys") : UiText.Get("Choose your local firmware source"), (!(kind == "keys")) ? new string[2] { ".zip", ".xci" } : new string[1] { ".keys" }, directory);
        if (source == null)
        {
            return;
        }
        await MainActionAsync(() => Session.InstallSharedPrerequisiteAsync(kind, source));
        if (Session.ErrorCode == null && selected != null && !Session.HasGame && Session.SharedSetup.Outcomes.Any((SharedPrerequisiteOutcome o) => o.BackendId == Session.SelectedBackendId && o.HeaderKeyUsable == true))
        {
            await MainActionAsync(() => Session.InspectAgainAsync(selected));
        }
    }

    private async Task AddContentAsync(LibraryContentKind kind)
    {
        string path = await Dialogs.BrowseAsync(this, (kind == LibraryContentKind.Update) ? UiText.Get("Add a local update container") : UiText.Get("Add a local DLC container"), new string[2] { ".nsp", ".xci" }, directory: false);
        if (path != null)
        {
            await MainActionAsync(() => Session.AddFilesAsync([path], kind, SelectedGame?.EntryId));
        }
    }

    private async Task ConfigureContentAsync(GameLibraryEntry game)
    {
        IReadOnlyList<GameLibraryEntry> entries = Session.Library.List(new LibraryQuery(null, FavoritesOnly: false, null, LibrarySort.Name, 0, 1000));
        GameLibraryEntry[] source = entries.Where((GameLibraryEntry e) => e.Kind == LibraryContentKind.Update && (e.RelatedGameEntryId == null || e.RelatedGameEntryId == game.EntryId)).ToArray();
        string chosen = await Dialogs.ChooseAsync(this, UiText.Get("Update for ") + game.DisplayName, source.Select((GameLibraryEntry e) => (EntryId: e.EntryId, DisplayName: e.DisplayName)).Prepend(("none", UiText.Get("Base game · no external update"))).ToArray(), UiText.Get("The core validates the actual content identity before selecting this update."));
        if (chosen == null)
        {
            return;
        }
        GameLibraryEntry[] dlc = entries.Where((GameLibraryEntry e) => e.Kind == LibraryContentKind.DownloadableContent && (e.RelatedGameEntryId == null || e.RelatedGameEntryId == game.EntryId)).ToArray();
        HashSet<string> selected = new HashSet<string>(from d in dlc.Where((GameLibraryEntry d) =>
            {
                GameContentConfiguration? contentConfiguration = Session.ContentConfiguration;
                return (object)contentConfiguration != null && contentConfiguration.DlcPaths.Contains(d.Content.AbsolutePath, StringComparer.OrdinalIgnoreCase);
            })
                                                       select d.EntryId);
        while (dlc.Length != 0)
        {
            (string, string)[] options = dlc.Select((GameLibraryEntry e) => (EntryId: e.EntryId, (selected.Contains(e.EntryId) ? "Selected  ·  " : "Off  ·  ") + e.DisplayName)).Prepend(("done", UiText.Get("Use this DLC selection"))).ToArray();
            string text = await Dialogs.ChooseAsync(this, UiText.Get("DLC selection"), options, UiText.Get("Toggle a container, then choose Use this DLC selection. Entitlement and native content checks still apply."));
            if (text == null)
            {
                return;
            }
            if (text == "done")
            {
                break;
            }
            if (!selected.Add(text))
            {
                selected.Remove(text);
            }
        }
        bool flag = Session.HasGame;
        if (flag)
        {
            flag = !(await Dialogs.ConfirmAsync(this, UiText.Get("Reload required"), UiText.Get("The current game will stop before its content selection changes. Save in the game before continuing."), UiText.Get("Stop and configure")));
        }
        if (!flag)
        {
            await MainActionAsync(() => Session.ConfigureContentAsync(game.EntryId, (chosen == "none") ? null : chosen, selected.ToArray()));
        }
    }

    private Control BuildSavesPage()
    {
        if (Session.OrbitEnabled)
        {
            return PageScroll(VisualTheme.Column(Heading("Orbit saves", "Your online and offline profiles are separate."), Card(VisualTheme.LocalizedText("Save import, migration and restore tools are available in offline mode. Stop the game and choose Go offline in Orbit to use them. Online gameplay cannot overwrite the offline profile.")), VisualTheme.LocalizedButton("Open Orbit", () => NavigateAsync("Orbit"))));
        }
        GameLibraryEntry game = SelectedGame;
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 17.0
        };
        stackPanel.Children.Add(Heading(UiText.Get("Saves"), UiText.Get("Native mappings, verified backups, and recoverable imports")));
        stackPanel.Children.Add(BuildSaveMigrationCard());
        stackPanel.Children.Add(VisualTheme.LocalizedText("Backups and manual imports", 22.0, null, FontWeight.DemiBold));
        stackPanel.Children.Add(Actions(VisualTheme.Button(game?.DisplayName ?? UiText.Get("Choose a game"), (Func<Task>)SelectGameAsync, (string?)null), VisualTheme.LocalizedButton("Discover native saves", () => MainActionAsync(() => Session.DiscoverSavesAsync(game?.EntryId)))));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Backup, import and restore close the core first. Snapshots must match the exact title, user, backend and format. The previous save is preserved before an import.", 14.0, VisualTheme.Muted));
        if (Session.NativeSaves.Count == 0)
        {
            stackPanel.Children.Add(Card(VisualTheme.LocalizedText("No verified native save mapping has been read. Run the game and save, then discover its native saves.", 17.0)));
        }
        foreach (SaveView view in Session.NativeSaves)
        {
            stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("Title " + view.Native.TitleId, 20.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("User " + view.Native.UserId + "\n" + view.Native.BackendId + " · " + view.Native.FormatId + " / " + view.Native.FormatVersion + "\n" + view.Native.Provenance, 13.0, VisualTheme.Muted), Actions(VisualTheme.LocalizedButton("Create backup", async () =>
            {
                bool flag = Session.HasGame;
                if (flag)
                {
                    flag = !(await Dialogs.ConfirmAsync(this, UiText.Get("Close the game and back up?"), UiText.Get("Finish saving in the game first. The core will stop before NX Infinity reads its save directory."), UiText.Get("Stop and back up")));
                }
                if (!flag)
                {
                    await MainActionAsync(() => Session.BackupSaveAsync(view));
                }
            }), VisualTheme.LocalizedButton("Backups / restore / export", () => BrowseSaveBackupsAsync(view)), VisualTheme.LocalizedButton("Import snapshot folder", async () =>
            {
                string path = await Dialogs.BrowseAsync(this, UiText.Get("Choose an extracted NX save snapshot"), Array.Empty<string>(), directory: true);
                bool flag = path != null;
                if (flag)
                {
                    flag = await Dialogs.ConfirmAsync(this, UiText.Get("Import this exact save snapshot?"), UiText.Get("The game will close. The imported snapshot must match this native save identity. A verified copy of the current save will be preserved before replacement."), UiText.Get("Import snapshot"));
                }
                if (flag)
                {
                    await MainActionAsync(() => Session.ImportSaveAsync(view, path));
                }
            }), VisualTheme.LocalizedButton("Rollback last import", async () =>
            {
                if (await Dialogs.ConfirmAsync(this, UiText.Get("Restore the pre-import save?"), UiText.Get("The game will close. The save preserved before the last import becomes active, and the current version is backed up."), UiText.Get("Rollback import")))
                {
                    await MainActionAsync(() => Session.RollbackSaveAsync(view));
                }
            })))));
        }
        foreach (string saveLimitation in Session.SaveLimitations)
        {
            stackPanel.Children.Add(VisualTheme.Text(saveLimitation, 13.0, VisualTheme.Muted));
        }
        return PageScroll(stackPanel);
    }

    private async Task BrowseSaveBackupsAsync(SaveView view)
    {
        bool flag = Session.HasGame;
        if (flag)
        {
            flag = !(await Dialogs.ConfirmAsync(this, UiText.Get("Close the game to verify backups?"), UiText.Get("Snapshot verification needs exclusive access to this core's save storage."), UiText.Get("Stop and verify")));
        }
        if (flag)
        {
            return;
        }
        IReadOnlyList<SaveBackup> source = await Session.ListSaveBackupsAsync(view);
        RenderCurrentPage();
        string chosen = await Dialogs.ChooseAsync(this, UiText.Get("Verified backups"), source.Select((SaveBackup b) => (BackupId: b.Manifest.BackupId, b.Manifest.CreatedUtc.ToLocalTime().ToString("g") + "  ·  " + b.Manifest.Files.Count + " files  ·  " + b.Manifest.BackupId)).ToArray());
        if (chosen == null)
        {
            return;
        }
        string text = await Dialogs.ChooseAsync(this, UiText.Get("Backup action"),
        [
            ("export", UiText.Get("Export a verified snapshot ZIP")),
            ("restore", UiText.Get("Restore this snapshot and preserve the current save"))
        ]);
        if (text == "export")
        {
            string path = await Dialogs.SavePathAsync(this, UiText.Get("Export save snapshot"), "nx-save-" + chosen + ".zip");
            if (path != null)
            {
                await MainActionAsync(() => Session.ExportSaveAsync(view, chosen, path));
            }
            return;
        }
        flag = text == "restore";
        if (flag)
        {
            flag = await Dialogs.ConfirmAsync(this, UiText.Get("Restore this backup?"), UiText.Get("The current save will be preserved before replacement. Use the same title, user and core when you relaunch."), UiText.Get("Restore backup"));
        }
        if (flag)
        {
            await MainActionAsync(() => Session.RestoreSaveAsync(view, chosen));
        }
    }

    private Control BuildEnhancementsPage()
    {
        if (_compatibilityView)
        {
            return BuildCompatibilityPage();
        }
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 17.0
        };
        stackPanel.Children.Add(Heading(UiText.Get("Game improvements"), UiText.Get("Optional graphics and performance presets for your games.")));
        GameLibraryEntry selectedGame = SelectedGame;
        string title = InfinitySession.TitleOf(selectedGame);
        Button button = VisualTheme.Button(((object)selectedGame == null) ? UiText.Get("Choose a game") : UiText.Get("Change game"), (Func<Task>)SelectGameAsync, (string?)null);
        button.Name = "ImprovementsChooseGame";
        stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.Text(((object)selectedGame == null) ? UiText.Get("Select a game to get started") : GameTitle(selectedGame), 23.0, null, FontWeight.DemiBold), VisualTheme.Text(Session.SelectedBackend?.Name ?? UiText.Get("Choose a game from your library."), 14.0, VisualTheme.Muted), Actions(button)), accent: true));
        EnhancementApplicationReceipt[] active = (from r in Session.Enhancements.ListReceipts(Session.EnhancementSessionId)
                                                  where r.Status == EnhancementReceiptStatus.Applied && r.SessionId == Session.EnhancementSessionId
                                                  select r).ToArray();
        foreach (EnhancementApplicationReceipt receipt in active.Reverse())
        {
            stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.Text(receipt.Profile.DisplayName, 19.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("On for the running game", 14.0, VisualTheme.Blue), VisualTheme.LocalizedButton("Turn off", () => MainActionAsync(() => Session.RollbackEnhancementAsync(receipt.ReceiptId))))));
        }
        EnhancementProfile[] array = (from p in Session.Enhancements.List().Where((EnhancementProfile p) =>
            {
                bool flag4 = title != null && p.TitleId == title && p.BackendId == Session.SelectedBackendId;
                if (flag4)
                {
                    EnhancementStatus status = p.Status;
                    bool flag5 = (uint)(status - 1) <= 1u;
                    flag4 = flag5;
                }
                return flag4;
            })
                                      where !active.Any((EnhancementApplicationReceipt r) => r.Profile.ProfileId == p.ProfileId && r.Profile.Version == p.Version)
                                      select p).ToArray();
        if ((object)selectedGame != null && array.Length == 0 && active.Length == 0)
        {
            stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("No presets available yet", 19.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("There are no ready-to-use presets for this game and core. You can adjust graphics and sound in Settings.", 14.0, VisualTheme.Muted), VisualTheme.LocalizedButton("Open settings", () => NavigateAsync("Settings")))));
        }
        EnhancementProfile[] array2 = array;
        foreach (EnhancementProfile profile in array2)
        {
            EnhancementPlan plan = Session.EvaluateEnhancement(profile, _allowExperimentalProfiles);
            bool flag = plan.Applicable;
            if (flag)
            {
                bool flag2;
                switch (Session.State)
                {
                    case RuntimeState.Loaded:
                    case RuntimeState.Paused:
                        flag2 = true;
                        break;
                    default:
                        flag2 = false;
                        break;
                }
                flag = flag2;
            }
            bool flag3 = flag;
            string text;
            if (flag3)
            {
                text = "Ready to use";
            }
            else if ((object)Session.ActiveGame == null)
            {
                text = "Start this game, then pause to check compatibility.";
            }
            else
            {
                text = ((Session.State == RuntimeState.Running) ? "Pause the game to make changes." : "Not available with your current game or setup. Open Details to see why.");
            }
            Button button2 = VisualTheme.LocalizedButton("Turn on", () => MainActionAsync(() => Session.ApplyEnhancementAsync(profile, _allowExperimentalProfiles)), "primary");
            button2.Name = "ApplyEnhancement";
            button2.IsEnabled = flag3;
            stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.Text(profile.DisplayName, 19.0, null, FontWeight.DemiBold), VisualTheme.Text(text, 14.0, flag3 ? VisualTheme.Blue : VisualTheme.Muted), Actions(button2, VisualTheme.LocalizedButton("Details", () => Dialogs.MessageAsync(this, profile.DisplayName, ProfileEvidence(profile, plan)))))));
        }
        Button button3 = VisualTheme.LocalizedButton("Game compatibility", () =>
        {
            _compatibilityView = true;
            RenderCurrentPage();
        });
        button3.Name = "OpenCompatibility";
        stackPanel.Children.Add(Actions(button3));
        StackPanel stackPanel2 = new StackPanel
        {
            Spacing = 12.0
        };
        stackPanel2.Children.Add(VisualTheme.LocalizedText("Import a preset you already have. Nothing turns on automatically.", 14.0, VisualTheme.Muted));
        stackPanel2.Children.Add(VisualTheme.LocalizedButton("Import preset", async () =>
        {
            string path = await Dialogs.BrowseAsync(this, UiText.Get("Choose a preset"), new string[1] { ".json" }, directory: false);
            if (path != null)
            {
                await MainActionAsync(() => Session.ImportEnhancementAsync(path));
            }
        }));
        if (array.Any((EnhancementProfile p) => p.Status == EnhancementStatus.Experimental))
        {
            CheckBox experimental = new CheckBox
            {
                Name = "ExperimentalProfiles",
                Content = "Allow experimental presets",
                IsChecked = _allowExperimentalProfiles
            };
            experimental.IsCheckedChanged += (object? _, RoutedEventArgs _) =>
            {
                _allowExperimentalProfiles = experimental.IsChecked == true;
                RenderCurrentPage();
            };
            stackPanel2.Children.Add(experimental);
        }
        stackPanel.Children.Add(new Expander
        {
            Header = UiText.Get("Import and advanced options"),
            Content = stackPanel2
        });
        return PageScroll(stackPanel);
    }

    private static string Classification(EnhancementChangeKind kind)
    {
        return kind switch
        {
            EnhancementChangeKind.Settings => "Settings research",
            EnhancementChangeKind.GamePatch => "Requires a game patch",
            EnhancementChangeKind.UnsupportedPlatform => "Unsupported platform functionality",
            _ => "Separately supplied content",
        };
    }

    private static string ProfileEvidence(EnhancementProfile profile, EnhancementPlan plan)
    {
        return "Title: " + profile.TitleId + "\nFull main build: " + (profile.BuildId ?? "missing") + "\nCore: " + profile.BackendId + "\nRevision: " + profile.BackendRevision + "\nHardware constraint: " + JsonSerializer.Serialize(profile.Hardware) + "\nDependencies: " + string.Join(", ", profile.DependsOn) + "\nConflicts: " + string.Join(", ", profile.ConflictsWith) + "\n\nOriginal behavior: " + profile.OriginalBehavior + "\nRollback: " + profile.RollbackNotes + "\n\nSource: " + profile.Source + "\nEvidence: " + profile.Evidence + "\n\nEligibility issues:\n" + string.Join("\n", plan.Issues.Select((EnhancementIssue i) => i.Code + ": " + i.Detail)) + "\n\n" + string.Join("\n\n", profile.Changes.Select((EnhancementChange c) => c.ChangeId + " · " + Classification(c.Kind) + "\n" + c.Description + "\nSource: " + c.Source + "\nEvidence: " + c.Evidence + "\nDependencies: " + string.Join(", ", c.DependsOn) + "\nConflicts: " + string.Join(", ", c.ConflictsWith) + "\nPatch receipt: " + c.PatchReceiptId + "\nRequired content: " + c.ContentId));
    }

    private Control BuildDiagnosticsPage()
    {
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 17.0
        };
        stackPanel.Children.Add(Heading(UiText.Get("Diagnostics"), UiText.Get("Actual core observations and local validation reports")));
        BackendInfo backend = Session.ActiveBackend ?? Session.LastBackendInfo;
        Button button = VisualTheme.LocalizedButton("Export Diagnostics", async () =>
        {
            string path = await Dialogs.SavePathAsync(this, UiText.Get("Export diagnostics"), "nx-infinity-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip");
            if (path != null)
            {
                await MainActionAsync(() => Session.ExportDiagnosticsAsync(path));
            }
        }, "primary");
        button.Name = "ExportDiagnostics";
        stackPanel.Children.Add(Actions(button));
        Control[] array = new Control[2];
        string[] array2 = new string[10]
        {
            "State: ",
            Session.State?.ToString() ?? UiText.Get("No running core"),
            UiText.Get("\nCore: "),
            backend?.BackendName ?? Session.SelectedBackend?.Name ?? UiText.Get("unavailable"),
            UiText.Get("\nRevision: "),
            backend?.BackendRevision ?? Session.SelectedBackend?.Revision ?? UiText.Get("unknown"),
            UiText.Get("\nQualification: "),
            backend?.Qualification ?? UiText.Get("unqualified"),
            UiText.Get("\nStartup: "),
            null
        };
        double? startupSeconds = Session.StartupSeconds;
        array2[9] = ((!startupSeconds.HasValue) ? UiText.Get("not measured") : (startupSeconds.GetValueOrDefault().ToString("0.00") + UiText.Get(" seconds (hash check through start acknowledgement)")));
        array[0] = VisualTheme.LocalizedText(string.Concat(array2), 14.0, VisualTheme.Muted);
        array[1] = VisualTheme.LocalizedButton("Core capabilities", () => Dialogs.MessageAsync(this, UiText.Get("Pinned core capabilities"), ((object)backend == null) ? UiText.Get("Connect to the selected core by reading its settings or prerequisites first.") : (string.Join("\n", backend.Capabilities) + UiText.Get("\n\nDeclared limitations\n") + string.Join("\n", backend.Limitations))));
        StackPanel item = VisualTheme.Column(array);
        StackPanel stackPanel2 = new StackPanel
        {
            Spacing = 12.0
        };
        stackPanel2.Children.Add(item);
        stackPanel.Children.Add(Actions(VisualTheme.LocalizedButton("Run Health Check", () => MainActionAsync(Session.CheckPrerequisitesAsync)), VisualTheme.LocalizedButton("Open Logs", () =>
        {
            DesktopShortcuts.OpenFolder(Path.Combine(Session.Paths.DataRoot, "diagnostics"));
        }), VisualTheme.LocalizedButton("Repair Installation", (Func<Task>)RepairInstallationAsync, (string?)null)));
        TelemetrySnapshot telemetry = Session.Telemetry;
        if ((object)telemetry != null)
        {
            stackPanel2.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("Live telemetry", 20.0, null, FontWeight.DemiBold), VisualTheme.Text(UiText.Get("Emulation FPS: ") + (telemetry.EmulationFps?.ToString("0.0") ?? UiText.Get("unavailable")) + UiText.Get("\nFrame time: ") + (telemetry.FrameTimeMs?.ToString("0.0") ?? UiText.Get("unavailable")) + UiText.Get(" ms\nFrame count: ") + (telemetry.FrameCount?.ToString() ?? UiText.Get("unavailable")) + UiText.Get("\nSource: ") + telemetry.Source + UiText.Get("\nThe report records periodic backend samples, not a full per-present timing trace."), 14.0, VisualTheme.Muted))));
        }
        stackPanel.Children.Add(_homeMode ? ((Control)Card(stackPanel2)) : ((Control)new Expander
        {
            Header = "Session details",
            Content = stackPanel2
        }));
        string lastReportPath = Session.LastReportPath;
        if (lastReportPath != null)
        {
            stackPanel.Children.Add(VisualTheme.LocalizedText("Last local export: " + lastReportPath, 13.0, VisualTheme.Muted));
        }
        OperationFailure lastFailure = Session.LastFailure;
        if ((object)lastFailure != null)
        {
            stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("Last issue · " + lastFailure.Code, 18.0, VisualTheme.Red, FontWeight.DemiBold), VisualTheme.Text((lastFailure.BackendId ?? UiText.Get("No connected core")) + " · " + lastFailure.OperationId + " · " + lastFailure.Stage, 13.0, VisualTheme.Muted), VisualTheme.Text(Session.ErrorRecoveryMessage ?? UiText.Get("Review the local details."), 14.0), VisualTheme.Text(Session.LastFailureMessage ?? "", 13.0, VisualTheme.Muted))));
        }
        Button button2 = VisualTheme.LocalizedButton("Review selected core recovery", (Func<Task>)RecoverSelectedSessionAsync, (string?)null);
        button2.Name = "RecoverSelectedSession";
        button2.IsEnabled = !Session.Busy && (object)Session.SelectedBackend != null;
        stackPanel.Children.Add(button2);
        string failedCore = Session.RecoveryBackendId;
        if (failedCore != null && failedCore != Session.SelectedBackendId)
        {
            string name = Session.Backends.First((BackendChoice core) => core.Id == failedCore).Name;
            Button button3 = VisualTheme.LocalizedButton("Recover " + name + UiText.Get(" (last issue)"), (Func<Task>)RecoverLastSessionAsync, (string?)null);
            button3.Name = "RecoverFailedSession";
            button3.IsEnabled = !Session.Busy;
            stackPanel.Children.Add(button3);
        }
        return PageScroll(stackPanel);
    }

    private Control BuildSettingsPage()
    {
        GameLibraryEntry game = (_settingsForGame ? SelectedGame : null);
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 16.0,
            MaxWidth = (_homeMode ? double.PositiveInfinity : 1100.0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        stackPanel.Children.Add(Heading(UiText.Get("Settings"), (Session.SelectedBackend?.DisplayName ?? UiText.Get("NX Infinity Core")) + " · " + (((object)game == null) ? UiText.Get("Core defaults") : game.DisplayName)));
        Button button = VisualTheme.Button(((object)game == null) ? UiText.Get("Core defaults · change scope") : UiText.Get("This game · change scope"), () =>
        {
            _settingsForGame = !_settingsForGame;
            RenderCurrentPage();
        });
        button.Name = "SettingsScope";
        button.IsEnabled = (object)game != null || (object)SelectedGame != null;
        Button button2 = VisualTheme.LocalizedButton("Refresh", () => MainActionAsync(() => Session.RefreshSettingsAsync()));
        button2.Name = "GraphicsRefresh";
        button2.IsEnabled = (object)Session.SelectedBackend != null;
        Button button3 = VisualTheme.LocalizedButton("Read active values", () => MainActionAsync(() => Session.RefreshSettingsAsync(initialize: true)));
        button3.Name = "GraphicsReadEffective";
        button3.IsEnabled = (object)Session.SelectedBackend != null;
        stackPanel.Children.Add(Actions(button, button2, button3));
        if ((object)game != null)
        {
            BackendChoice selectedBackend = Session.SelectedBackend;
            if ((object)selectedBackend != null)
            {
                LibrarySettingRequestView settingRequests = Session.Library.GetSettingRequests(game.EntryId, selectedBackend.Id, selectedBackend.Revision);
                stackPanel.Children.Add(Card(VisualTheme.Column(VisualTheme.Text(settingRequests.RequiresReview ? UiText.Get("Review settings before playing") : UiText.Get("Settings for this game"), 18.0, null, FontWeight.DemiBold), VisualTheme.Text(settingRequests.RequiresReview ? UiText.Get("This core changed. Restore its defaults, then choose your settings again.") : UiText.Get("Changes here override this core's defaults for the selected game."), 14.0, VisualTheme.Muted), VisualTheme.LocalizedButton("Restore core preferences for this game", () => MainActionAsync(() => Session.ResetGameSettingsAsync(game.EntryId))))));
            }
        }
        TextBox search = new TextBox
        {
            Name = "SettingsSearch",
            PlaceholderText = UiText.Get("Search settings"),
            Text = _settingsSearch
        };
        AutomationProperties.SetName(search, UiText.Get("Search settings"));
        stackPanel.Children.Add(search);
        WrapPanel wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        string[] array = new string[6] { "All", "Graphics", "Performance", "Audio", "System", "Controls" };
        foreach (string category in array)
        {
            Button button4 = VisualTheme.LocalizedButton((category == "Controls") ? "Controllers" : category, () =>
            {
                _settingsCategory = category;
                RenderCurrentPage();
            }, (category == _settingsCategory) ? "selected" : "subtle");
            button4.Name = "SettingsCategory" + category;
            button4.Margin = new Thickness(0.0, 0.0, 6.0, 6.0);
            wrapPanel.Children.Add(button4);
        }
        stackPanel.Children.Add(wrapPanel);
        StackPanel results = new StackPanel
        {
            Spacing = 10.0,
            Name = "SettingsResults"
        };
        stackPanel.Children.Add(results);
        search.TextChanged += (object? _, TextChangedEventArgs _) =>
        {
            _settingsSearch = search.Text?.Trim() ?? "";
            Populate();
        };
        Populate();
        return PageScroll(stackPanel);
        bool Include(string text2, string text)
        {
            if (_settingsCategory == "All" || _settingsCategory == text2)
            {
                if (_settingsSearch.Length != 0)
                {
                    return text.Contains(_settingsSearch, StringComparison.OrdinalIgnoreCase);
                }
                return true;
            }
            return false;
        }
        void Populate()
        {
            results.Children.Clear();
            _frameRateReadouts.Clear();
            if (Include("System", "themes appearance colors desktop HOME"))
            {
                results.Children.Add(VisualTheme.LocalizedButton("Themes", (Func<Task>)ShowThemesAsync, (string?)null));
            }
            if (Include("System", "Discord presence profile social activity"))
            {
                results.Children.Add(BuildDiscordCard());
            }
#if !NXI_NATIVE_LINUX
            if (Include("System", "translation languages OCR game dialogue"))
            {
                results.Children.Add(BuildTranslationCard());
            }
#endif
            if (Include("System", "startup launch default desktop HOME"))
            {
                results.Children.Add(BuildStartupModeCard());
            }
            if (Include("System", "about developer credits testers version COYE"))
            {
                results.Children.Add(VisualTheme.LocalizedButton("About NX Infinity", (Func<Task>)ShowAboutAsync, (string?)null));
            }
            if (Include("System", "licenses copyright open source GPL LGPL FFmpeg Dune"))
            {
                Button button5 = VisualTheme.LocalizedButton("Open-source licenses", (Func<Task>)ShowLicensesAsync, (string?)null);
                button5.Name = "OpenSourceLicenses";
                results.Children.Add(button5);
            }
            if (Include("System", "language lingua idioma langue Sprache English Italiano Español Français Deutsch Português " + UiText.Get("Launcher language")))
            {
                results.Children.Add(BuildLanguageCard());
            }
            if (Include("Graphics", "game display fullscreen windowed resolution"))
            {
                results.Children.Add(BuildGameDisplayCard());
            }
            if (Include("Graphics", "low end GPU performance minimum VRAM memory textures resolution") || Include("Performance", "low end GPU performance minimum VRAM memory textures resolution"))
            {
                results.Children.Add(BuildGpuPerformanceCard(game?.EntryId));
            }
            if (Include("Graphics", "60 FPS frame rate timing original global game") || Include("Performance", "60 FPS frame rate timing original global game"))
            {
                results.Children.Add(BuildFrameRateCard(game));
            }
            if (Include("Audio", "HOME audio startup sounds animation"))
            {
                results.Children.Add(VisualTheme.LocalizedButton("HOME audio & startup", (Func<Task>)ShowHomeExperienceAsync, (string?)null));
            }
            if (Session.ServicesBackendId == Session.SelectedBackendId)
            {
                foreach (SettingView item in from s in Session.Settings
                                             orderby GraphicsOrder(s.Schema.Id), s.Schema.Label
                                             select s)
                {
                    if (Include(SettingCategory(item.Schema), item.Schema.Label + " " + item.Schema.Description + " " + item.Schema.Id))
                    {
                        results.Children.Add(BuildSettingCard(item, game));
                    }
                }
            }
            if (Include("Controls", "controller buttons navigation input gamepad"))
            {
                Button button6 = VisualTheme.LocalizedButton("Assign controller", (Func<Task>)ChooseGameControllerAsync, (string?)null);
                button6.Name = "AssignController";
                results.Children.Add(Card(VisualTheme.Column(VisualTheme.LocalizedText("Controller", 18.0, null, FontWeight.DemiBold), CreateControllerStatusReadout(), Actions(button6, VisualTheme.LocalizedButton("Reorder controllers", (Func<Task>)ReorderControllersAsync, (string?)null), VisualTheme.LocalizedButton("Map buttons & calibrate", (Func<Task>)ConfigureControllerMappingAsync, (string?)null), VisualTheme.LocalizedButton("Buttons and navigation", () => Dialogs.MessageAsync(this, UiText.Get("Controls"), "NX Infinity screens\nD-pad or left stick: move focus\nSouth face button: confirm\nEast face button: back\nLB / RB: change main section\nRight stick: scroll\nGuide / HOME: open HOME while a gamepad controls the game\nHold View / Select + Menu / Start for half a second: HOME fallback (toggle in HOME audio & startup). If another app intercepts Guide, use the fallback or adjust that app's controller shortcuts.\n\nHOME mode\nLeft / right: browse the cover row\nUp / down: move between rows\nLB / RB: switch All games, Favorites and Recent\nStart / Plus: selected game options\nNorth / X: stop the current game\nEast / B: back to HOME, or return to desktop from its library\nGuide / HOME: resume a paused game from the HOME library\n\nMouse: every action is clickable; wheel scrolls.\nKeyboard: Tab / Shift+Tab move focus; Enter or Space activates; arrows move between controls; Escape goes back; Home opens HOME; F11 changes the game window when a game is loaded and the launcher otherwise. Text boxes retain ordinary cursor editing. Confirming a text box with a controller opens the on-screen keyboard.\n\nGameplay via the frontend gamepad\nFace buttons retain Nintendo positions: South=B, East=A, West=Y, North=X. Shoulder buttons=L/R; triggers=ZL/ZR; stick clicks and D-pad are forwarded. Menu controls never go to the guest; returning to play requires a neutral sample.\n\nNX Infinity Core keyboard (when player 1 is released from gamepad input)\nZ/X/C/V = A/B/X/Y; arrows = D-pad; WASD = left stick; IJKL = right stick; Q/E = ZL/L; U/O = R/ZR; F/H = stick clicks; Enter/Backspace = Plus/Minus. Escape opens the game menu; F11 toggles the real render window. \n\nTouch pointer: press both stick buttons together to toggle. Right stick moves the visible pointer; A taps. Mouse clicks also reach the emulated touchscreen. Toggle off to restore normal stick/A input. Physical-device behavior still needs checking on your hardware."))))));
            }
            if ((object)Session.SelectedBackend == null && _settingsSearch.Length == 0)
            {
                results.Children.Add(VisualTheme.LocalizedText("Restore NX Infinity Core to access graphics, audio and system settings.", 14.0, VisualTheme.Muted));
            }
            else if (Session.Settings.Count == 0 && _settingsSearch.Length == 0)
            {
                results.Children.Add(VisualTheme.LocalizedText(Session.Busy ? "Reading this core's settings…" : "Settings could not be read. Refresh to retry, or open Last issue for details.", 14.0, VisualTheme.Muted));
            }
            if (results.Children.Count == 0)
            {
                results.Children.Add(VisualTheme.LocalizedText("No matching settings. Try another category or search.", 15.0, VisualTheme.Muted));
            }
            if (_homeMode)
            {
                HomePalette.Adapt(results);
            }
            else
            {
                _dune.Adapt(results);
            }
        }
    }

    private static string SettingCategory(SettingSchema schema)
    {
        string id = schema.Id;
        if (id.Contains("audio", StringComparison.Ordinal))
        {
            return "Audio";
        }
        if (IsGraphicsSetting(schema))
        {
            return "Graphics";
        }
        if (id.Contains("cpu", StringComparison.Ordinal) || id.Contains("cache", StringComparison.Ordinal) || id.Contains("compilation", StringComparison.Ordinal) || id.Contains("thread", StringComparison.Ordinal))
        {
            return "Performance";
        }
        if (!IsGraphicsSetting(schema))
        {
            return "System";
        }
        return "Graphics";
    }

    private Control BuildGameDisplayCard()
    {
        _displayReadout = VisualTheme.LocalizedText("", 14.0, VisualTheme.Muted);
        _displayReadout.Name = "GameDisplayState";
        UpdateDisplayReadout();
        Button button = VisualTheme.Button(Session.HasGame ? UiText.Get("Play windowed") : UiText.Get("Launch windowed"), () => MainActionAsync(() => Session.SetGameFullscreenAsync(fullscreen: false)));
        Button button2 = VisualTheme.Button(Session.HasGame ? UiText.Get("Play fullscreen") : UiText.Get("Launch fullscreen"), () => MainActionAsync(() => Session.SetGameFullscreenAsync(fullscreen: true)));
        button.Name = "GameWindowed";
        button2.Name = "GameFullscreen";
        bool isEnabled = (button2.IsEnabled = (object)Session.SelectedBackend != null && (!Session.HasGame || Session.Supports("window.set")));
        button.IsEnabled = isEnabled;
        Button button3 = VisualTheme.LocalizedButton("Read window state", () => MainActionAsync(Session.RefreshGameWindowAsync));
        button3.Name = "GameWindowRead";
        button3.IsEnabled = (object)Session.ActiveBackend != null && Session.Supports("window.get");
        Control control = VisualTheme.LocalizedText("Fullscreen uses your display's desktop mode. Internal game resolution stays at your selected scale. F11 toggles the game window; Guide / HOME opens HOME. If the center button is intercepted, hold View / Select + Menu / Start for half a second. Closing the game window returns to your library.", 13.0, VisualTheme.Muted);
        if (!_homeMode)
        {
            control = new Expander
            {
                Header = "Display help",
                Content = control
            };
        }
        return Card(VisualTheme.Column(VisualTheme.LocalizedText("Game display", 18.0, null, FontWeight.DemiBold), _displayReadout, Actions(button, button2, button3), control), accent: true);
    }

    private void UpdateDisplayReadout()
    {
        if (_displayReadout == null)
        {
            return;
        }
        string text = UiText.Get("Next launch: ") + UiText.Get(Session.GameFullscreenPreference ? "Fullscreen" : "Windowed");
        RenderWindowState gameWindow = Session.GameWindow;
        if ((object)gameWindow == null)
        {
            _displayReadout.Text = text ?? "";
            return;
        }
        int? pixelWidth = gameWindow.PixelWidth;
        string text2;
        if (pixelWidth.HasValue)
        {
            int valueOrDefault = pixelWidth.GetValueOrDefault();
            pixelWidth = gameWindow.PixelHeight;
            if (pixelWidth.HasValue)
            {
                int valueOrDefault2 = pixelWidth.GetValueOrDefault();
                text2 = UiText.Format("{0} × {1} presentation pixels", valueOrDefault, valueOrDefault2);
                goto IL_00e2;
            }
        }
        text2 = UiText.Format("{0} × {1} window units", gameWindow.Width, gameWindow.Height);
        goto IL_00e2;
    IL_00e2:
        string text3 = text2;
        string text4 = UiText.Get(gameWindow.Fullscreen ? "Fullscreen" : "Windowed");
        string english;
        if (gameWindow.FullscreenConfirmed == true)
        {
            english = "transition confirmed";
        }
        else
        {
            english = ((gameWindow.FullscreenConfirmed == false) ? "transition not confirmed" : "native flags read; transition confirmation unavailable");
        }
        string text5 = UiText.Get(english);
        _displayReadout.Text = text4 + " · " + text3 + "\n" + text5 + (gameWindow.Visible ? "" : UiText.Get(" · window hidden for the menu")) + "\n" + text;
    }

    private static bool IsGraphicsSetting(SettingSchema schema)
    {
        if (!schema.Id.StartsWith("graphics.", StringComparison.Ordinal) && !schema.Id.Contains(".graphics.", StringComparison.Ordinal) && !schema.Id.EndsWith("system.docked", StringComparison.Ordinal))
        {
            return schema.Id.EndsWith(".emulation.uncapped_fps", StringComparison.Ordinal);
        }
        return true;
    }

    private static bool IsResolutionSetting(SettingSchema schema)
    {
        return schema.Id.EndsWith("graphics.resolution_scale", StringComparison.Ordinal);
    }

    private static int GraphicsOrder(string id)
    {
        if (!id.EndsWith("resolution_scale", StringComparison.Ordinal))
        {
            if (!id.EndsWith("system.docked", StringComparison.Ordinal))
            {
                if (!id.EndsWith("vsync", StringComparison.Ordinal))
                {
                    if (!id.EndsWith("gpu_accuracy", StringComparison.Ordinal))
                    {
                        if (!id.Contains("experimental", StringComparison.Ordinal))
                        {
                            return 10;
                        }
                        return 100;
                    }
                    return 3;
                }
                return 2;
            }
            return 1;
        }
        return 0;
    }

    private Control BuildSettingCard(SettingView setting, GameLibraryEntry? game)
    {
        SettingSchema schema = setting.Schema;
        SettingState state = setting.State;
        JsonElement? perGame = null;
        if ((object)game != null)
        {
            BackendChoice selectedBackend = Session.SelectedBackend;
            if ((object)selectedBackend != null)
            {
                LibrarySettingRequests? stored = Session.Library.GetSettingRequests(game.EntryId, selectedBackend.Id, selectedBackend.Revision).Stored;
                if ((object)stored != null && stored.Values.TryGetValue(schema.Id, out var value))
                {
                    perGame = value;
                }
            }
        }
        string text;
        if (perGame.HasValue)
        {
            JsonElement valueOrDefault = perGame.GetValueOrDefault();
            text = UiText.Get("Game request: ") + Display(valueOrDefault);
        }
        else
        {
            JsonElement? requested = state.Requested;
            if (requested.HasValue)
            {
                JsonElement valueOrDefault2 = requested.GetValueOrDefault();
                text = UiText.Get("Requested: ") + Display(valueOrDefault2);
            }
            else
            {
                text = UiText.Get("Default: ") + Display(schema.DefaultValue) + UiText.Get(" · active request not read");
            }
        }
        string text2 = text;
        string text3;
        if (state.EffectiveKnown)
        {
            JsonElement? requested = state.Effective;
            if (requested.HasValue)
            {
                JsonElement valueOrDefault3 = requested.GetValueOrDefault();
                text3 = UiText.Get("Effective: ") + Display(valueOrDefault3);
                goto IL_018d;
            }
        }
        text3 = UiText.Get("Effective: not yet observed");
        goto IL_018d;
    IL_018d:
        string text4 = text3;
        if (state.RequiresRestart)
        {
            text4 += UiText.Get(" · restart required");
        }
        else if (state.Applied)
        {
            text4 += UiText.Get(" · acknowledged by core");
        }
        bool flag = (object)game != null && schema.Apply == "before_initialize";
        Button button = VisualTheme.Button(IsResolutionSetting(schema) ? UiText.Get("Choose resolution") : UiText.Get("Change"), () => EditSettingAsync(schema, perGame ?? state.Requested ?? schema.DefaultValue, game?.EntryId));
        button.IsEnabled = schema.Supported && !flag;
        button.Name = (IsResolutionSetting(schema) ? "GraphicsResolution" : "SettingChange");
        button.Tag = schema.Id;
        Button button2 = VisualTheme.LocalizedButton("Use original default", () => MainActionAsync(() => Session.SetSettingAsync(schema, schema.DefaultValue, game?.EntryId)));
        button2.Name = "SettingOriginalDefault";
        button2.IsEnabled = button.IsEnabled;
        button2.Tag = schema.Id;
        string text5 = schema.Apply switch
        {
            "live" => UiText.Get("Applies during the current session"),
            "before_load" => UiText.Get("Takes effect on the next game load"),
            "before_initialize" => UiText.Get("Requires a new core session"),
            _ => schema.Apply.Replace('_', ' '),
        };
        TextBlock textBlock = VisualTheme.LocalizedText(schema.Label, 17.0, null, FontWeight.DemiBold);
        JsonElement value2 = perGame ?? state.Requested ?? schema.DefaultValue;
        JsonValueKind valueKind = value2.ValueKind;
        bool flag2 = valueKind - 5 <= JsonValueKind.Object;
        TextBlock textBlock2 = VisualTheme.Text((flag2 ? UiText.Get(value2.GetBoolean() ? "On" : "Off") : SettingValueLabel(schema, value2)) + (state.RequiresRestart ? UiText.Get(" · Next launch") : ""), 14.0, (value2.ValueKind == JsonValueKind.True) ? VisualTheme.Green : VisualTheme.Blue, FontWeight.Medium);
        textBlock2.Name = "SettingValue";
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 16.0
        };
        grid.Children.Add(VisualTheme.Column(textBlock, textBlock2));
        if (schema.Id.EndsWith(".emulation.uncapped_fps", StringComparison.Ordinal) && schema.Kind == "boolean")
        {
            ToggleSwitch toggle = new ToggleSwitch
            {
                Name = "UncappedFps",
                Tag = schema.Id,
                OnContent = "On",
                OffContent = "Off",
                IsChecked = (value2.ValueKind == JsonValueKind.True),
                IsEnabled = (button.IsEnabled && !Session.Busy)
            };
            AutomationProperties.SetName(toggle, "Uncapped FPS · may speed up games");
            toggle.IsCheckedChanged += async (object? _, RoutedEventArgs _) =>
            {
                toggle.IsEnabled = false;
                await MainActionAsync(() => Session.SetSettingAsync(schema, JsonSerializer.SerializeToElement(toggle.IsChecked == true), game?.EntryId));
            };
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
        }
        else
        {
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 8.0
        };
        stackPanel.Children.Add(grid);
        stackPanel.Children.Add(VisualTheme.LocalizedText(schema.Description, 13.0, VisualTheme.Muted));
        stackPanel.Children.Add(VisualTheme.Text(text5, 12.0, VisualTheme.Muted));
        if (flag)
        {
            stackPanel.Children.Add(VisualTheme.LocalizedText("Change this option in core defaults before starting a game.", 13.0, VisualTheme.Muted));
        }
        StackPanel stackPanel2 = VisualTheme.Column(VisualTheme.Text(text2 + "\n" + text4, 13.0, VisualTheme.Muted), button2);
        stackPanel2.Children.Add(VisualTheme.LocalizedText("Setting: " + schema.Id + "\n" + (state.EffectiveSource ?? state.Reason ?? UiText.Get("Active value not read")) + ((schema.Dependencies.Length == 0) ? "" : (UiText.Get("\nRequires: ") + string.Join(", ", schema.Dependencies))), 12.0, VisualTheme.Muted));
        stackPanel.Children.Add(new Expander
        {
            Header = UiText.Get("Details & reset"),
            Content = stackPanel2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        return Card(stackPanel);
    }

    private static string SettingValueLabel(SettingSchema schema, JsonElement value)
    {
        if (schema.Id.EndsWith(".graphics.vsync", StringComparison.Ordinal) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() switch
            {
                "fifo" => "On · synchronized",
                "immediate" => "Off · tearing possible",
                "mailbox" => "Mailbox · newest frame",
                "fifo_relaxed" => "Adaptive",
                _ => Display(value),
            };
        }
        return Display(value);
    }

    private static string Display(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return value.GetRawText();
        }
        return value.GetString() ?? "";
    }

    private async Task EditSettingAsync(SettingSchema schema, JsonElement current, string? entryId)
    {
        bool flag = IsResolutionSetting(schema);
        if (flag)
        {
            string kind = schema.Kind;
            bool flag2 = ((kind == "number" || kind == "integer") ? true : false);
            flag = flag2;
        }
        JsonElement value;
        if (flag)
        {
            (string, string)[] options = new double[8] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 }.Where((double scale) => (!schema.Minimum.HasValue || scale >= schema.Minimum) && (!schema.Maximum.HasValue || scale <= schema.Maximum) && (schema.Kind != "integer" || Math.Truncate(scale) == scale)).Select((double scale) =>
            {
                string item = scale.ToString(CultureInfo.InvariantCulture);
                string text8 = scale.ToString("0.##", CultureInfo.InvariantCulture);
                string text9 = ((scale == 1.0) ? " · Original scale" : ((scale < 1.0) ? " · Lower rendering resolution" : " · Higher rendering resolution"));
                return (item, text8 + "×" + text9);
            }).Append(("custom", UiText.Get("Custom scale within the core's range")))
                .ToArray();
            string text = await Dialogs.ChooseAsync(this, UiText.Get("Resolution scale"), options, schema.Description + UiText.Get("\nCurrent: ") + Display(current));
            if (text == null)
            {
                return;
            }
            if (text == "custom")
            {
                string text2 = await Dialogs.TextAsync(this, UiText.Get("Custom resolution scale"), Display(current), 40);
                if (text2 == null)
                {
                    return;
                }
                if (!double.TryParse(text2, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result))
                {
                    await Dialogs.MessageAsync(this, UiText.Get("Enter a scale"), UiText.Get("Use a finite number within the core's advertised range."));
                    return;
                }
                value = JsonSerializer.SerializeToElement(result);
            }
            else
            {
                value = JsonSerializer.SerializeToElement(double.Parse(text, CultureInfo.InvariantCulture));
            }
        }
        else if (schema.Kind == "boolean")
        {
            string text3 = await Dialogs.ChooseAsync(this, schema.Label,
            [
                ("true", UiText.Get("On")),
                ("false", UiText.Get("Off"))
            ], schema.Description);
            if (text3 == null)
            {
                return;
            }
            value = JsonSerializer.SerializeToElement(text3 == "true");
        }
        else if (schema.Kind == "enum")
        {
            (string, string)[] options2 = schema.Values.Select((JsonElement v, int i) => (i.ToString(CultureInfo.InvariantCulture), SettingValueLabel(schema, v))).ToArray();
            string text4 = await Dialogs.ChooseAsync(this, schema.Label, options2, schema.Description);
            if (text4 == null)
            {
                return;
            }
            value = schema.Values[int.Parse(text4, CultureInfo.InvariantCulture)].Clone();
        }
        else
        {
            double? minimum = schema.Minimum;
            string text5;
            if (minimum.HasValue)
            {
                double valueOrDefault = minimum.GetValueOrDefault();
                text5 = $" ({valueOrDefault} to {schema.Maximum})";
            }
            else
            {
                text5 = "";
            }
            string text6 = text5;
            string text7 = await Dialogs.TextAsync(this, schema.Label + text6, Display(current), (schema.Kind == "string") ? 4096 : 40);
            if (text7 == null)
            {
                return;
            }
            if (schema.Kind == "string")
            {
                value = JsonSerializer.SerializeToElement(text7);
            }
            else
            {
                try
                {
                    using JsonDocument jsonDocument = JsonDocument.Parse(text7);
                    value = jsonDocument.RootElement.Clone();
                }
                catch (JsonException)
                {
                    await Dialogs.MessageAsync(this, UiText.Get("Enter a number"), UiText.Get("Use a number within the range shown by the core, with a period for decimals."));
                    return;
                }
            }
        }
        await MainActionAsync(() => Session.SetSettingAsync(schema, value, entryId));
    }

    public async Task ChooseGameControllerAsync()
    {
        if (Session.State == RuntimeState.Running)
        {
            await Session.PauseAsync();
        }
        string text = await ControllerAssignmentDialog.ChooseAsync(this, ControllerAssignment);
        if (text == null)
        {
            return;
        }
        try
        {
            bool flag = ControllerRoster.Assign(0, (text == "none") ? null : text);
            _controllerNavigation.GameDeviceId = ControllerAssignment.State.DeviceId;
            if (flag)
            {
                await Session.ReleaseRemoteInputAsync();
            }
            RefreshControllerStatus();
        }
        catch (Exception ex) when ((ex is NxException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            RefreshControllerStatus();
            await Dialogs.MessageAsync(this, UiText.Get("Controller selection needs attention"), ex.Message);
        }
    }

    private async Task ReorderControllersAsync()
    {
        if (Session.State == RuntimeState.Running)
        {
            await Session.PauseAsync();
        }
        ControllerRoster.Reorder();
        _controllerNavigation.GameDeviceId = null;
        for (int player = 0; player < 4; player++)
        {
            await Session.ReleaseRemoteInputAsync(player);
        }
        RefreshControllerStatus();
    }

    private Control BuildSharedSetupCard()
    {
        SharedPrerequisiteStatus setup = Session.SharedSetup;
        StackPanel stackPanel = new StackPanel
        {
            Spacing = 12.0
        };
        stackPanel.Children.Add(VisualTheme.LocalizedText("Keys & firmware", 21.0, null, FontWeight.DemiBold));
        stackPanel.Children.Add(VisualTheme.LocalizedText("Choose your local keys and firmware for NX Infinity Core.", 14.0, VisualTheme.Muted));
        string text = Session.PrerequisiteImportLimitation("firmware");
        if (text != null && text.Length > 0)
        {
            TextBlock textBlock = VisualTheme.Text(text, 13.0, VisualTheme.Muted);
            textBlock.Name = "SharedFirmwareLimitation";
            stackPanel.Children.Add(textBlock);
        }
        string text2 = ((setup.Sources.Count == 0) ? "No shared sources installed yet" : string.Join(" · ", setup.Sources.Select((SharedPrerequisiteRevision s) => (!(s.Kind == "firmware")) ? (s.Slot + " retained") : "Firmware source retained")));
        stackPanel.Children.Add(VisualTheme.Text(text2, 14.0, VisualTheme.Blue));
        Button button = VisualTheme.LocalizedButton("Install keys", () => ImportPrerequisiteAsync("keys"));
        button.Name = "ImportKeys";
        Button button2 = VisualTheme.LocalizedButton("Install firmware", () => ImportPrerequisiteAsync("firmware"));
        button2.Name = "ImportFirmware";
        Button button3 = VisualTheme.LocalizedButton("Apply pending setup", () => MainActionAsync(() => Session.ProvisionSharedSetupAsync()));
        button3.Name = "SharedSetupApply";
        button3.IsEnabled = setup.Sources.Count != 0 && Session.Backends.Count != 0;
        Button button4 = VisualTheme.LocalizedButton("Retry setup", () => MainActionAsync(() => Session.ProvisionSharedSetupAsync(retryFailures: true)));
        button4.Name = "SharedSetupRetry";
        button4.IsEnabled = setup.Outcomes.Any((SharedPrerequisiteOutcome o) =>
        {
            bool flag;
            switch (o.Status)
            {
                case "verified":
                case "imported":
                case "pending":
                    flag = true;
                    break;
                default:
                    flag = false;
                    break;
            }
            return !flag;
        });
        stackPanel.Children.Add(Actions(button, button2, button3, button4));
        if (Session.HasGame)
        {
            stackPanel.Children.Add(VisualTheme.LocalizedText("The active game's profile remains in use. Setup will finish before the next launch.", 13.0, VisualTheme.Muted));
        }
        stackPanel.Children.Add(VisualTheme.LocalizedText("Latest recorded setup per source and core. A result may belong to an earlier installation; Inspect NX Infinity Core checks the current one. Full history remains in Setup results & limitations.", 12.0, VisualTheme.Muted));
        foreach (BackendChoice backend in Session.Backends)
        {
            IReadOnlyList<SharedPrerequisiteOutcome> readOnlyList = SharedSetupPresentation.LatestForCore(setup, backend.Id, backend.Revision);
            string text3 = ((readOnlyList.Count == 0) ? "No recorded setup result for these sources" : string.Join("\n", readOnlyList.Select((SharedPrerequisiteOutcome o) =>
            {
                string obj = ((o.Kind == "firmware") ? "Firmware" : o.Slot);
                string text4 = SetupStatusLabel(o);
                string firmwareVersion = o.FirmwareVersion;
                return obj + " · " + text4 + ((firmwareVersion != null) ? (" · " + firmwareVersion) : "");
            })));
            stackPanel.Children.Add(VisualTheme.Column(VisualTheme.Text(backend.Name, 16.0, null, FontWeight.Medium), VisualTheme.Text(text3, 13.0, VisualTheme.Muted)));
        }
        foreach (SetupInstallationIssue sharedSetupInstallationIssue in Session.SharedSetupInstallationIssues)
        {
            stackPanel.Children.Add(VisualTheme.Text(sharedSetupInstallationIssue.BackendId + UiText.Get(" · installation could not be verified · ") + sharedSetupInstallationIssue.Code, 13.0, VisualTheme.Muted));
        }
        Button button5 = VisualTheme.LocalizedButton("Setup results & limitations", () => Dialogs.MessageAsync(this, UiText.Get("Shared setup results"), JsonSerializer.Serialize(new
        {
            state = setup,
            installationIssues = Session.SharedSetupInstallationIssues,
            report = Session.LastSharedSetupReport
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        })));
        button5.Name = "SharedSetupDetails";
        Button button6 = VisualTheme.LocalizedButton("Inspect NX Infinity Core", () => MainActionAsync(Session.CheckPrerequisitesAsync));
        button6.IsEnabled = (object)Session.SelectedBackend != null;
        stackPanel.Children.Add(Actions(button5, button6));
        foreach (SharedPrerequisiteRevision source in setup.Sources.Where((SharedPrerequisiteRevision s) => s.PreviousRevisionId != null))
        {
            Button button7 = VisualTheme.LocalizedButton("Restore previous " + ((source.Kind == "firmware") ? UiText.Get("firmware source") : source.Slot), () => MainActionAsync(() => Session.RollbackSharedSetupAsync(source.Id)));
            button7.Name = "SharedSetupRollback";
            stackPanel.Children.Add(button7);
        }
        stackPanel.Children.Add(VisualTheme.LocalizedText("Firmware results distinguish a readable version from a complete package checked against the selected source. Retry setup can recover a matching interrupted firmware transaction. Game requirements and retail behavior still need validation.", 12.0, VisualTheme.Muted));
        return Card(stackPanel, accent: true);
    }

    private static string SetupStatusLabel(SharedPrerequisiteOutcome outcome)
    {
        switch (outcome.Status)
        {
            case "verified":
                return (outcome.Kind == "firmware") ? ("Package integrity verified · " + outcome.FirmwareArchiveCount + " archives") : "Header key loaded";
            case "imported":
                return (outcome.Kind == "firmware") ? "Version detected · package not fully verified" : "Imported · review validation results";
            case "unsupported":
                return (outcome.ErrorCode == "unsupported_platform") ? "Switch 2/Ounce platform unsupported" : "Importer or package variant unavailable";
            case "deferred":
            case "deferred_busy":
                return "Queued · core profile is in use";
            case "failed":
                return "Needs attention" + ((outcome.ErrorCode == null) ? "" : (" · " + outcome.ErrorCode));
            default:
                return outcome.Status.Replace('_', ' ');
        }
    }

    private Control BuildShell()
    {
        _navigation.Clear();
        _navigationLabels.Clear();
        _shellGrid = new Grid
        {
            Name = "MainPanel",
            RowDefinitions = new RowDefinitions("68,2,Auto,*,24"),
            Background = _dune.Canvas
        };
        _shellGrid.Styles.Add(_dune.Styles());
        Grid grid = new Grid
        {
            Name = "DuneToolbar",
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 12.0,
            Margin = new Thickness(18.0, 0.0)
        };
        Button button = DuneIcon("Library", "Library", () => NavigateAsync("Library"));
        button.Content = new BrandMark
        {
            Width = 27.0,
            Height = 27.0
        };
        button.Name = "NavLibrary";
        RegisterDesktopNavigation("Library", button);
        grid.Children.Add(button);
        _desktopSearch = new TextBox
        {
            Name = "LibrarySearch",
            Text = _search,
            PlaceholderText = UiText.Get("Search"),
            Width = 170.0,
            MinHeight = 30.0,
            Height = 30.0,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLength = 200
        };
        AutomationProperties.SetName(_desktopSearch, "Search library");
        _desktopSearch.TextChanged += (object? _, TextChangedEventArgs _) =>
        {
            _search = _desktopSearch.Text?.Trim() ?? "";
            if (_section == "Library")
            {
                FilterDuneLibrary();
            }
        };
        Grid.SetColumn(_desktopSearch, 1);
        grid.Children.Add(_desktopSearch);
        StackPanel stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stackPanel.Children.Add(DuneIcon("plus", "Add game folder", AddGamesAsync, "AddGame"));
        stackPanel.Children.Add(DuneIcon("folder-open", "Library folders", ManageGameFoldersAsync, "LibraryFolders"));
        _desktopFavorites = DuneIcon("star", "Favorites", async () =>
        {
            _favorites = !_favorites;
            UpdateDesktopFavorites();
            QueueDesktopBrowsingSave();
            if (_section != "Library")
            {
                await NavigateAsync("Library");
            }
            else
            {
                FilterDuneLibrary();
            }
        }, "LibraryFavorites");
        UpdateDesktopFavorites();
        stackPanel.Children.Add(_desktopFavorites);
        stackPanel.Children.Add(DuneIcon("chevron-down", "Sort library", DuneSortAsync, "LibrarySort"));
        Grid.SetColumn(stackPanel, 2);
        grid.Children.Add(stackPanel);
        StackPanel stackPanel2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7.0,
            VerticalAlignment = VerticalAlignment.Center
        };
        stackPanel2.Children.Add(BuildAvatarButton(home: false));
        _backendName.Foreground = _dune.Muted;
        _backendName.FontFamily = DuneTheme.Font;
        _backend.Content = VisualTheme.Row(_backendName, new NavGlyph
        {
            Kind = "chevron-down",
            Width = 12.0,
            Height = 12.0,
            Stroke = _dune.Muted
        });
        _backend.Classes.Add("dune-quiet");
        Button backend = _backend;
        double height = (_backend.MinHeight = 32.0);
        backend.Height = height;
        _backend.MaxWidth = 236.0;
        _backend.Padding = new Thickness(8.0, 4.0);
        stackPanel2.Children.Add(_backend);
        Button menu = null;
        menu = DuneIcon("settings", "Library tools", () =>
        {
            if (menu?.Flyout is Flyout { IsOpen: false } flyout)
            {
                flyout.ShowAt(menu);
            }
            return Task.CompletedTask;
        }, "DesktopTools");
        StackPanel stackPanel3 = new StackPanel
        {
            Spacing = 2.0,
            MinWidth = 190.0
        };
        string[] array = new string[7] { "Orbit", "Enhancements", "Performance", "Content", "Saves", "Diagnostics", "Settings" };
        foreach (string text in array)
        {
            string section = text;
            Button button2 = VisualTheme.LocalizedButton((text == "Enhancements") ? "Improvements" : text, async () =>
            {
                (menu.Flyout as Flyout)?.Hide();
                await NavigateAsync(section);
            });
            button2.Name = "Nav" + text;
            button2.HorizontalAlignment = HorizontalAlignment.Stretch;
            button2.HorizontalContentAlignment = HorizontalAlignment.Left;
            RegisterDesktopNavigation(text, button2);
            stackPanel3.Children.Add(button2);
        }
        Button button3 = VisualTheme.LocalizedButton("Themes", async () =>
        {
            (menu.Flyout as Flyout)?.Hide();
            await ShowThemesAsync();
        });
        button3.Name = "OpenThemes";
        stackPanel3.Children.Add(button3);
        _dune.Adapt(stackPanel3);
        menu.Flyout = CreateDuneFlyout(menu, stackPanel3);
        stackPanel2.Children.Add(menu);
        stackPanel2.Children.Add(DuneIcon("maximize-2", "HOME", () => SetHomeModeAsync(enabled: true), "EnterHomeMode"));
        Grid.SetColumn(stackPanel2, 3);
        grid.Children.Add(stackPanel2);
        _shellGrid.Children.Add(grid);
        Grid.SetRow(_progress, 1);
        _shellGrid.Children.Add(_progress);
        _sessionTitle = _dune.Text("");
        _sessionResume = VisualTheme.LocalizedButton("Resume", () => MainActionAsync(Session.ResumeAsync));
        _sessionGraphics = VisualTheme.LocalizedButton("Graphics", async () =>
        {
            _settingsForGame = true;
            _settingsCategory = "Graphics";
            await NavigateAsync("Settings");
        });
        _sessionStop = VisualTheme.LocalizedButton("Stop game", () => MainActionAsync(Session.StopAsync));
        Grid grid2 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 8.0
        };
        grid2.Children.Add(_sessionTitle);
        Grid.SetColumn(_sessionResume, 1);
        grid2.Children.Add(_sessionResume);
        Grid.SetColumn(_sessionGraphics, 2);
        grid2.Children.Add(_sessionGraphics);
        Grid.SetColumn(_sessionStop, 3);
        grid2.Children.Add(_sessionStop);
        _sessionStrip = new Border
        {
            Padding = new Thickness(20.0, 6.0),
            Background = _dune.Surface,
            Child = _dune.Adapt(grid2),
            IsVisible = false
        };
        Grid.SetRow(_sessionStrip, 2);
        _shellGrid.Children.Add(_sessionStrip);
        _contentRegion = new Border
        {
            Child = _page
        };
        Grid.SetRow(_contentRegion, 3);
        _shellGrid.Children.Add(_contentRegion);
        Grid grid3 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(20.0, 0.0),
            ColumnSpacing = 12.0
        };
        _status.FontSize = 11.0;
        _status.Foreground = _dune.Muted;
        _status.TextWrapping = TextWrapping.NoWrap;
        _metrics.FontSize = 11.0;
        _metrics.Foreground = _dune.Muted;
        Button error = _error;
        height = (_cancel.MinHeight = 22.0);
        error.MinHeight = height;
        Button error2 = _error;
        Thickness padding = (_cancel.Padding = new Thickness(8.0, 1.0));
        error2.Padding = padding;
        grid3.Children.Add(_status);
        Grid.SetColumn(_metrics, 1);
        grid3.Children.Add(_metrics);
        Grid.SetColumn(_error, 2);
        grid3.Children.Add(_error);
        Grid.SetColumn(_cancel, 3);
        grid3.Children.Add(_cancel);
        Grid.SetRow(grid3, 4);
        _shellGrid.Children.Add(grid3);
        return _shellGrid;
    }

    private void UpdateDesktopFavorites()
    {
        if (_desktopFavorites != null)
        {
            _desktopFavorites.Classes.Set("dune-active", _favorites);
            string value = (_favorites ? "Favorites only · show all games" : "Show favorites");
            ToolTip.SetTip(_desktopFavorites, value);
            AutomationProperties.SetName(_desktopFavorites, value);
        }
    }

    internal bool DesktopFlyoutContains(Control control)
    {
        Control desktopFlyoutContent = DesktopFlyoutContent;
        if (desktopFlyoutContent != null)
        {
            if (desktopFlyoutContent != control)
            {
                return control.GetVisualAncestors().Contains(desktopFlyoutContent);
            }
            return true;
        }
        return false;
    }

    internal bool CloseDesktopFlyout()
    {
        Flyout? openDesktopFlyout = _openDesktopFlyout;
        if (openDesktopFlyout == null || !openDesktopFlyout.IsOpen)
        {
            return false;
        }
        _openDesktopFlyout.Hide();
        return true;
    }

    private Flyout CreateDuneFlyout(Button owner, Control content)
    {
        Flyout flyout = new Flyout
        {
            Content = content
        };
        flyout.Opened += (object? _, EventArgs _) =>
        {
            _openDesktopFlyout = flyout;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (flyout.IsOpen)
                {
                    Navigation.FocusFirst(this);
                }
            }, DispatcherPriority.Loaded);
        };
        flyout.Closed += (object? _, EventArgs _) =>
        {
            if (_openDesktopFlyout == flyout)
            {
                _openDesktopFlyout = null;
            }
            if (owner.IsEffectivelyVisible)
            {
                Navigation.Focus(owner);
            }
        };
        return flyout;
    }

    private void RegisterDesktopNavigation(string name, Button button)
    {
        _navigation[name] = button;
        TextBlock textBlock = _dune.Text(UiText.Get((name == "Enhancements") ? "Improvements" : name), 13.0);
        _navigationLabels[name] = textBlock;
        if (name != "Library")
        {
            button.Content = textBlock;
        }
    }

    private Button DuneIcon(string icon, string label, Func<Task> action, string? name = null)
    {
        Button button = VisualTheme.Button(label, action);
        button.Name = name;
        button.Content = new NavGlyph
        {
            Kind = icon,
            Width = 17.0,
            Height = 17.0,
            Stroke = _dune.Ink
        };
        button.Width = 32.0;
        double height = (button.MinHeight = 32.0);
        button.Height = height;
        button.Padding = new Thickness(6.0);
        button.Classes.Add("dune-quiet");
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        return button;
    }

    private void UpdateSessionControls()
    {
        if (_sessionStrip != null && _sessionTitle != null && _sessionResume != null && _sessionStop != null && _sessionGraphics != null)
        {
            _sessionStrip.IsVisible = Session.HasGame;
            TextBlock? sessionTitle = _sessionTitle;
            string english;
            if (Session.GameWindowClosing)
            {
                english = "Closing · ";
            }
            else
            {
                english = ((Session.State == RuntimeState.Paused) ? "Paused · " : "Playing · ");
            }
            sessionTitle.Text = UiText.Get(english) + Session.ActiveGame?.DisplayName;
            _sessionResume.IsEnabled = !Session.Busy && !Session.GameWindowClosing && Session.State == RuntimeState.Paused;
            Button? sessionStop = _sessionStop;
            bool isEnabled = (_sessionGraphics.IsEnabled = !Session.Busy && !Session.GameWindowClosing);
            sessionStop.IsEnabled = isEnabled;
        }
    }

    private void UpdateBackendSelector()
    {
        BackendChoice selectedBackend = Session.SelectedBackend;
        _backendName.Text = selectedBackend?.DisplayName ?? UiText.Get("NX Infinity Core");
        _backend.IsEnabled = !Session.Busy && !Session.GameWindowClosing;
        ToolTip.SetTip(_backend, ((object)selectedBackend == null) ? "NX Infinity Core information" : (selectedBackend.Name + "\n" + selectedBackend.Revision));
        AutomationProperties.SetName(_backend, "Core information: " + _backendName.Text);
    }

    private void UpdateShellLayout()
    {
        if (_homeMode)
        {
            if (Math.Abs(_homeLayoutSize.Width - Bounds.Width) >= 1.0 || Math.Abs(_homeLayoutSize.Height - Bounds.Height) >= 1.0)
            {
                RenderCurrentPage();
            }
            return;
        }
        _compactRail = Width < 1040.0;
        _backend.MaxWidth = ((Width < 1000.0) ? 125 : 236);
        if (_desktopSearch != null)
        {
            _desktopSearch.Width = ((Width < 1000.0) ? 130 : 170);
        }
        if (_contentRegion != null)
        {
            _contentRegion.Padding = ((_section == "Library") ? default(Thickness) : new Thickness(32.0, 16.0, 32.0, 12.0));
        }
    }

    private void UpdateNavigationStyle()
    {
        if (_contentRegion != null)
        {
            _contentRegion.Padding = ((_section == "Library") ? default(Thickness) : new Thickness(32.0, 16.0, 32.0, 12.0));
        }
        if (_desktopSearch != null)
        {
            _desktopSearch.IsEnabled = _section == "Library";
        }
        foreach (var (text2, button2) in _navigation)
        {
            button2.Classes.Set("selected", text2 == _section);
        }
    }

    private async Task CreateGameShortcutAsync(GameLibraryEntry game)
    {
        string text = InfinitySession.TitleOf(game);
        string core = ((text != null && Session.Profiles.Read().Titles.TryGetValue(text, out BackendSelection value)) ? value.BackendId : Session.SelectedBackendId);
        if (core == null)
        {
            await Dialogs.MessageAsync(this, "NX Infinity Core", "Restore NX Infinity Core before creating a game shortcut.");
            return;
        }
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            desktop = await Dialogs.BrowseAsync(this, "Choose a shortcut folder", Array.Empty<string>(), directory: true);
        }
        if (desktop == null)
        {
            return;
        }
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Launcher path is unavailable.");
        List<string> arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(Path.Combine(AppContext.BaseDirectory, "NxInfinity.dll"));
        }
        arguments.AddRange(
        [
            "--install-root",
            Session.Paths.InstallRoot,
            "--data-root",
            Session.Paths.DataRoot,
            "--launch",
            game.EntryId,
            "--core",
            core
        ]);
        string icon = null;
        LibraryArtworkData artwork = Session.Library.GetArtwork(game.EntryId);
        if ((object)artwork != null)
        {
            icon = await Task.Run(() => ShortcutArtwork.Write(Session.Paths.DataRoot, artwork.Bytes, OperatingSystem.IsWindows()));
        }
        string text2 = DesktopShortcuts.Create(desktop, GameTitle(game), game.EntryId, core, executable, arguments, icon);
        await Dialogs.MessageAsync(this, "Shortcut created", "Launches " + GameTitle(game) + " with " + core + ".\n\n" + text2 + "\n\nCreate it again here if you move NX Infinity.");
    }

    private async Task<bool> LaunchFromArgumentsAsync(string[] args)
    {
        int num = Array.IndexOf(args, "--launch");
        if (num < 0)
        {
            return false;
        }
        if (num + 1 >= args.Length || !Guid.TryParseExact(args[num + 1], "N", out var _))
        {
            await Dialogs.MessageAsync(this, "Invalid shortcut", "The shortcut has no valid game identifier. Create it again from the game page.");
            return true;
        }
        int core = Array.IndexOf(args, "--core");
        if (core < 0 || core + 1 >= args.Length)
        {
            await Dialogs.MessageAsync(this, "Invalid shortcut", "The shortcut has no core selection. Create it again from the game page.");
            return true;
        }
        _selectedEntry = args[num + 1];
        await MainActionAsync(() => Session.LaunchAsync(_selectedEntry, args[core + 1]));
        if (Session.ErrorMessage != null)
        {
            _selectedEntry = null;
            RenderCurrentPage();
        }
        return true;
    }

    private Control BuildStartupSequence()
    {
        return _startupSequence = new StartupSequence(() =>
        {
            UpdateBootOverlay();
            if (!_closing)
            {
                Navigation.FocusFirst(this);
            }
        });
    }

    private void BeginStartupSequence()
    {
        if (_startupSequence == null || Session.Busy || Session.GuestOwnsInput)
        {
            return;
        }
        _startupSequence.Begin();
        UpdateBootOverlay();
        PlayHomeSound(HomeSound.Startup, startup: true);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_startupSequence.IsVisible)
            {
                Navigation.Focus(_startupSequence.Skip);
            }
        });
    }

    public void SetStartupMode(bool home)
    {
        _homePreferences = _homePreferences with
        {
            StartInHome = home
        };
        SaveHomePreferences();
        if (!HomePreferenceSaved)
        {
            throw new IOException("The startup preference could not be saved.");
        }
    }

    private Control BuildStartupModeCard()
    {
        return Card(VisualTheme.Column(VisualTheme.LocalizedText("Start NX Infinity in", 18.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("Choose your default launcher. Switching views during a session keeps this preference.", 14.0, VisualTheme.Muted), Actions(Choice("Desktop", home: false), Choice("HOME", home: true))));
        Button Choice(string label, bool home)
        {
            Button button = VisualTheme.LocalizedButton(label, () =>
            {
                SetStartupMode(home);
                RenderCurrentPage();
            }, (StartsInHome == home) ? "selected" : "subtle");
            button.Name = (home ? "StartInHome" : "StartInDesktop");
            return button;
        }
    }

    private void InitializeThemes()
    {
        _themeStore = new ThemePreferenceStore(Session.Paths.DataRoot);
        Appearance = _themeStore.Read(_desktopPreferences.Night);
        _dune = new DuneTheme(Appearance.Desktop);
        HomePalette = new HomeTheme(Appearance.Home);
    }

    public Task SetAppearanceAsync(ThemePreferences value, bool persist = true)
    {
        if (persist)
        {
            _themeStore.Write(value);
        }
        Appearance = value;
        _dune = new DuneTheme(value.Desktop);
        HomePalette = new HomeTheme(value.Home);
        _homeSurface.Styles.Clear();
        _homeSurface.Styles.Add(HomePalette.Styles());
        RequestedThemeVariant = ((_homeMode ? HomePalette.Palette.Night : _dune.Night) ? ThemeVariant.Dark : ThemeVariant.Light);
        CloseDesktopFlyout();
        RebuildDesktopShell();
        ReloadAvatar();
        RenderCurrentPage();
        UpdateBackendSelector();
        UpdateShellLayout();
        return Task.CompletedTask;
    }

    public async Task ShowThemesAsync()
    {
        _customizingTheme = true;
        await UpdatePresenceAsync();
        try
        {
            await ThemeDialog.ShowAsync(this);
        }
        finally
        {
            _customizingTheme = false;
            await UpdatePresenceAsync();
        }
    }

    private void RebuildDesktopShell()
    {
        if (!(Content is Grid grid) || _desktopSurface == null)
        {
            return;
        }
        int val = grid.Children.IndexOf(_desktopSurface);
        grid.Children.Remove(_desktopSurface);
        Control[] array = new Control[8] { _backend, _backendName, _page, _progress, _status, _metrics, _error, _cancel };
        foreach (Control control in array)
        {
            if (control.Parent is Panel panel)
            {
                panel.Children.Remove(control);
            }
            else if (control.Parent is Border border)
            {
                border.Child = null;
            }
            else if (control.Parent is ContentControl contentControl)
            {
                contentControl.Content = null;
            }
        }
        _shellGrid?.Children.Clear();
        _desktopSurface = BuildShell();
        _desktopSurface.IsVisible = !_homeMode;
        grid.Children.Insert(Math.Max(0, val), _desktopSurface);
    }

    private void InitializeTranslation()
    {
#if !NXI_NATIVE_LINUX
        _translator = new LocalTranslator(Session.Paths);
        UiText.ConfigureLocal(_translator);
        _translationTimer.Tick += async (object? _, EventArgs _) =>
        {
            await TranslateGameFrameAsync();
        };
        _translationTimer.Start();
#endif
    }

    private void StopTranslation()
    {
        _translationTimer.Stop();
        _translationLifetime.Cancel();
        _translationOverlay?.Close();
        _translationOverlay = null;
        if (_translator != null)
        {
            UiText.ReleaseLocal(_translator);
            _translator.Dispose();
        }
    }

#if !NXI_NATIVE_LINUX
    private Control BuildTranslationCard()
    {
        return Card(VisualTheme.Column(VisualTheme.LocalizedText("Universal text translation", 18.0, null, FontWeight.DemiBold), VisualTheme.LocalizedText("Translate launcher text or read game dialogue with optional local OCR. Language packs download once; text stays on this device.", 14.0, VisualTheme.Muted), VisualTheme.LocalizedButton("Translation settings", (Func<Task>)ShowTranslationSettingsAsync, (string?)null)));
    }

    private async Task ShowTranslationSettingsAsync()
    {
        LocalTranslator service = _translator ?? throw new InvalidOperationException("Translation is unavailable.");
        Window dialog = Dialogs.Create("Translation", 740.0, 660.0);
        dialog.Name = "TranslationSettings";
        CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(_translationLifetime.Token);
        ComboBox source;
        ComboBox target;
        try
        {
            TranslationLanguage[] array = service.Languages();
            source = new ComboBox
            {
                Name = "TranslationSource",
                ItemsSource = array,
                SelectedItem = array.FirstOrDefault((TranslationLanguage l) => l.Id == service.Preferences.SourceLanguage),
                MinWidth = 230.0
            };
            target = new ComboBox
            {
                Name = "TranslationTarget",
                ItemsSource = array,
                SelectedItem = array.FirstOrDefault((TranslationLanguage l) => l.Id == service.Preferences.TargetLanguage),
                MinWidth = 230.0
            };
            CheckBox ui = VisualTheme.LocalizedCheckBox("Translate untranslated launcher text", service.Preferences.TranslateLauncher);
            CheckBox game = VisualTheme.LocalizedCheckBox("Enable in-game translation overlay", service.Preferences.GameOverlay);
            CheckBox full = VisualTheme.LocalizedCheckBox("Read the entire game window (otherwise dialogue area only)", service.Preferences.FullFrame);
            Slider interval = new Slider
            {
                Minimum = 2.0,
                Maximum = 15.0,
                TickFrequency = 1.0,
                IsSnapToTickEnabled = true,
                Value = service.Preferences.IntervalSeconds
            };
            TextBlock status = VisualTheme.Text(_translationStatus, 13.0, VisualTheme.Muted);
            status.Name = "TranslationStatus";
            Button button = VisualTheme.LocalizedButton("Download selected language packs", async () =>
            {
                _ = 2;
                try
                {
                    status.Text = "Downloading language packs… This can take several minutes. Close this window to cancel.";
                    await service.InstallPairAsync(Source(), Target(), lifetime.Token);
                    if (ui.IsChecked == true)
                    {
                        await service.InstallPairAsync("en", Target(), lifetime.Token);
                    }
                    if (game.IsChecked == true)
                    {
                        await InstallOcrLanguageAsync(Source(), lifetime.Token);
                    }
                    status.Text = "Language packs are installed. Translation is available offline.";
                }
                catch (OperationCanceledException)
                {
                    status.Text = "Download canceled.";
                }
                catch (Exception ex2)
                {
                    status.Text = ex2.Message;
                }
            });
            TextBox text = new TextBox
            {
                Name = "TranslationSample",
                Text = "Welcome! Choose a game to begin.",
                AcceptsReturn = true,
                Height = 74.0,
                MaxLength = 4000
            };
            TextBlock translated = VisualTheme.LocalizedText("", 16.0);
            translated.Name = "TranslationResult";
            Button button2 = VisualTheme.LocalizedButton("Translate text", async () =>
            {
                try
                {
                    TextBlock textBlock = translated;
                    textBlock.Text = await service.TranslateAsync(text.Text ?? "", Source(), Target(), lifetime.Token);
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                }
            });
            Button button3 = VisualTheme.LocalizedButton("Save translation settings", () =>
            {
                string value;
                if ((ui.IsChecked == true && !service.HasPair("en", Target())) || (game.IsChecked == true && !service.HasPair(Source(), Target())))
                {
                    status.Text = "Download the selected language packs before enabling translation.";
                }
                else if (game.IsChecked == true && (!OcrLanguages.TryGetValue(Source(), out value) || !File.Exists(Path.Combine(Session.Paths.DataRoot, "translation", "ocr", value + ".traineddata"))))
                {
                    status.Text = "Enable game translation, then download its OCR language pack before saving.";
                }
                else
                {
                    service.Save(new TranslationPreferences(ui.IsChecked == true, Source(), Target(), game.IsChecked == true, (int)interval.Value, full.IsChecked == true));
                    _lastOcr = (_lastFrame = null);
                    if (ui.IsChecked == true && UiText.Languages.Any(((string Id, string Name) l) => l.Id == Target()))
                    {
                        SetLauncherLanguage(Target());
                    }
                    _translationStatus = "Local translation settings saved.";
                    dialog.Close();
                    RenderCurrentPage();
                }
            }, "primary");
            dialog.Content = PageScroll(VisualTheme.Column(Heading("Translate on your device", "Offline after language-pack setup. OCR works with readable text in the active game's window; stylized fonts and exclusive fullscreen capture can reduce recognition."), VisualTheme.LocalizedText("Game text language"), source, VisualTheme.LocalizedText("Translate into"), target, ui, game, full, VisualTheme.LocalizedText("Game text refresh interval (seconds)"), interval, button, status, VisualTheme.LocalizedText("Try a translation"), text, Actions(button2), translated, Actions(VisualTheme.LocalizedButton("Close", () =>
            {
                dialog.Close();
            }), button3)));
            try
            {
                await Dialogs.ShowAsync<object>(this, dialog);
            }
            finally
            {
                lifetime.Cancel();
            }
        }
        finally
        {
            if (lifetime != null)
            {
                ((IDisposable)lifetime).Dispose();
            }
        }
        string Source()
        {
            return (source.SelectedItem as TranslationLanguage)?.Id ?? "en";
        }
        string Target()
        {
            return (target.SelectedItem as TranslationLanguage)?.Id ?? "it";
        }
    }

    private async Task InstallOcrLanguageAsync(string language, CancellationToken ct)
    {
        if (!OcrLanguages.TryGetValue(language, out string value))
        {
            throw new InvalidOperationException("Text translation supports this language, but automatic game OCR does not yet include its language pack.");
        }
        string path = Path.Combine(Session.Paths.DataRoot, "translation", "ocr", value + ".traineddata");
        if (File.Exists(path) && new FileInfo(path).Length > 1000)
        {
            return;
        }
        using HttpClient http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3L)
        };
        using HttpResponseMessage response = await http.GetAsync("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/" + value + ".traineddata", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream data = new MemoryStream();
        byte[] buffer = new byte[16384];
        int num;
        while ((num = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (data.Length + num > 33554432)
            {
                throw new InvalidDataException("OCR language download exceeds its size limit.");
            }
            await data.WriteAsync(buffer.AsMemory(0, num), ct);
        }
        if (data.Length < 1000)
        {
            throw new InvalidDataException("OCR language download was incomplete.");
        }
        AvatarStore.WriteAtomic(path, data.ToArray());
    }
#endif

    private async Task TranslateGameFrameAsync()
    {
        LocalTranslator service = _translator;
        if (_closing || service == null || _translating)
        {
            return;
        }
        _translationTimer.Interval = TimeSpan.FromSeconds(service.Preferences.IntervalSeconds);
        if (service.Preferences.GameOverlay && Session.State == RuntimeState.Running)
        {
            int? activeWorkerProcessId = Session.ActiveWorkerProcessId;
            if (activeWorkerProcessId.HasValue)
            {
                int pid = activeWorkerProcessId.GetValueOrDefault();
                if (_translationGame != Session.ActiveGame?.EntryId)
                {
                    _lastOcr = (_lastFrame = null);
                    _translationGame = Session.ActiveGame?.EntryId;
                }
                TranslationPreferences preferences = service.Preferences;
                string gameId = Session.ActiveGame?.EntryId;
                _translating = true;
                try
                {
                    CancellationToken ct = _translationLifetime.Token;
                    byte[] array = await Task.Run(() => GameTextCapture.Capture(pid, preferences.FullFrame), ct);
                    string text = Convert.ToHexStringLower(SHA256.HashData(array));
                    if (text == _lastFrame)
                    {
                        if (!string.IsNullOrWhiteSpace(_lastOcr))
                        {
                            string text2 = _translationText?.Text;
                            if (text2 != null && text2.Length > 0)
                            {
                                ShowTranslationOverlay(text2);
                            }
                        }
                        return;
                    }
                    _lastFrame = text;
                    string text3 = await GameTextCapture.RecognizeAsync(TranslationRuntime, Path.Combine(Session.Paths.DataRoot, "translation", "ocr"), array, preferences.SourceLanguage, ct);
                    if (string.IsNullOrWhiteSpace(text3))
                    {
                        _lastOcr = "";
                        _translationOverlay?.Hide();
                    }
                    else if (!(text3 == _lastOcr))
                    {
                        _lastOcr = text3;
                        string text4 = await service.TranslateAsync(text3, preferences.SourceLanguage, preferences.TargetLanguage, ct);
                        if (!_closing && !(preferences != service.Preferences) && !(Session.ActiveGame?.EntryId != gameId) && service.Preferences.GameOverlay && Session.ActiveWorkerProcessId == pid && Session.State == RuntimeState.Running)
                        {
                            ShowTranslationOverlay(text4);
                            _translationStatus = "Game text translated locally.";
                        }
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex2)
                {
                    _translationStatus = ex2.Message;
                    _lastFrame = null;
                    _translationOverlay?.Hide();
                    return;
                }
                finally
                {
                    _translating = false;
                }
            }
        }
        _translationOverlay?.Hide();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetOverlayStyle(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetOverlayStyle(nint handle, int index, nint value);

    private void ShowTranslationOverlay(string text)
    {
        if (_translationOverlay == null)
        {
            _translationText = VisualTheme.LocalizedText("", 21.0, Brushes.White, FontWeight.Medium);
            _translationOverlay = new Window
            {
                Title = "NX Infinity · Translation",
                Width = 760.0,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 260.0,
                CanResize = false,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                WindowDecorations = WindowDecorations.None,
                Background = Brush.Parse("#192131"),
                Content = new Border
                {
                    Padding = new Thickness(20.0, 12.0),
                    Child = _translationText
                }
            };
            _translationOverlay.Opened += (object? _, EventArgs _) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    IPlatformHandle platformHandle = _translationOverlay.TryGetPlatformHandle();
                    if (platformHandle != null)
                    {
                        SetOverlayStyle(platformHandle.Handle, -20, GetOverlayStyle(platformHandle.Handle, -20) | 0x8000020);
                    }
                }
            };
        }
        _translationText.Text = text;
        Screen screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if ((object)screen != null)
        {
            _translationOverlay.Position = new PixelPoint(screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - 760) / 2), screen.WorkingArea.Y + 32);
        }
        if (!_translationOverlay.IsVisible)
        {
            _translationOverlay.Show();
        }
    }
}
