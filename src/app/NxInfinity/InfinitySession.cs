using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NxInfinity.Runtime;
using NxInfinity.Runtime.Enhancements;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public sealed class InfinitySession : IAsyncDisposable
{
    private sealed class PendingInteraction
    {
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();

        public TaskCompletionSource<bool> Closed { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<InteractionRequest> Updates { get; } = Channel.CreateBounded<InteractionRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    private sealed class ThrottledFolderProgress(IProgress<LibraryFolderProgress> target) : IProgress<LibraryFolderProgress>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _last;

        public void Report(LibraryFolderProgress value)
        {
            long elapsedMilliseconds = _clock.ElapsedMilliseconds;
            string stage = value.Stage;
            bool flag = ((stage == "complete" || stage == "canceled") ? true : false);
            if (flag || elapsedMilliseconds - _last >= 150)
            {
                _last = elapsedMilliseconds;
                target.Report(value);
            }
        }
    }

    private sealed record SavedPortableLocation(string Id, string Origin, string RootPath);

    private sealed record SaveMigrationPreference(int SchemaVersion, bool AutomaticEnabled, IReadOnlyList<SavedPortableLocation> PortableLocations);

    private readonly HashSet<string> _artworkInspections = new HashSet<string>();

    private readonly List<GraphicsIssueObservation> _graphicsObservations = new List<GraphicsIssueObservation>();

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private CancellationTokenSource? _operation;

    private NxRuntime? _runtime;

    private Task? _eventTask;

    private string? _launchId;

    private string? _lastReport;

    private readonly Queue<object> _events = new Queue<object>();

    private readonly List<double> _frameTimes = new List<double>();

    private bool _disposed;

    private bool _interactionOwnsInput;

    private bool _guestRequested;

    private readonly ConcurrentDictionary<BackendChoice, BackendLaunch> _verifiedLaunches = new ConcurrentDictionary<BackendChoice, BackendLaunch>();

    private long _launchGeneration;

    private long _operationFailureSequence;

    private string? _errorCode;

    private string? _lastOperationFailureCode;

    private readonly ConcurrentDictionary<string, PendingInteraction> _pendingInteractions = new ConcurrentDictionary<string, PendingInteraction>();

    private readonly SemaphoreSlim _dialogGate = new SemaphoreSlim(1, 1);

    private readonly Queue<OperationFailure> _failures = new Queue<OperationFailure>();

    private readonly Queue<object> _observations = new Queue<object>();

    private long _operationSequence;

    private string _diagnosticOperation = "other";

    private string _diagnosticStage = "requested";

    private BackendChoice? _diagnosticBackend;

    private FrameRatePreferences _frameRatePreferences = new FrameRatePreferences();

    private GpuPerformancePreferences _gpuPreferences = new GpuPerformancePreferences();

    private readonly List<LaunchPhaseTiming> _launchPhases = new List<LaunchPhaseTiming>();

    private long _launchPhaseStart;

    private string _launchPhase = "requested";

    public const string DefaultOrbitServer = "ldn.ryujinx.app";

    private LdnSessionOptions? _orbitNetwork;

    private LdnCompatibility? _orbitCompatibility;

    private readonly object _performanceGate = new object();

    private readonly List<InfinityPerformanceRun> _performanceHistory = new List<InfinityPerformanceRun>();

    private PerformanceCapture? _performanceCapture;

    private NxRuntime? _performanceRuntime;

    private NxRuntime? _performancePlanRuntime;

    private NxRuntime? _performanceReceiptRuntime;

    private string? _captureTitle;

    private string? _captureBuild;

    private string? _captureVersion;

    private const int PerformanceCaptureCapacity = 32768;

    private string _captureStartedBy = "launch";

    private long _captureLaunchGeneration;

    private bool _runtimeInitializationPending;

    private bool _performancePlanConsumed;

    private bool _performanceReceiptRequiresFreshLoad;

    private GraphicsDeviceResult? _captureGraphics;

    private ShaderCompilationResult? _captureShaders;

    private FrameRatePolicyState? _captureFrameStart;

    private FrameRatePolicyState? _captureFrameEnd;

    private static readonly JsonSerializerOptions SavePreferenceJson = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly HashSet<string> SaveOrigins = new HashSet<string>(StringComparer.Ordinal) { "eden", "citron", "yuzu", "suyu", "sudachi", "ryujinx", "ryujinx-canary", "kenji-nx" };

    private SaveMigrationPreference _saveMigrationPreference = new SaveMigrationPreference(1, AutomaticEnabled: true, Array.Empty<SavedPortableLocation>());

    private string? _saveMigrationPreferenceHash;

    private EnhancementCoordinator? _enhancementCoordinator;

    private readonly ConcurrentDictionary<NxRuntime, byte> _closingRuntimes = new ConcurrentDictionary<NxRuntime, byte>();

    private readonly ConcurrentDictionary<NxRuntime, byte> _menuRuntimes = new ConcurrentDictionary<NxRuntime, byte>();

    public IReadOnlyList<GraphicsIssueObservation> GraphicsIssueObservations
    {
        get
        {
            lock (_graphicsObservations)
            {
                return _graphicsObservations.ToArray();
            }
        }
    }

    public InfinityPaths Paths { get; }

    public NxWorkspace Workspace { get; }

    public GameLibraryStore Library { get; }

    public ProfileRouter Profiles { get; }

    public EnhancementStore Enhancements { get; }

    public ModStore Mods { get; }

    public SaveStore Saves
    {
        get
        {
            RequireOfflineSaveTools();
            return new SaveStore(Workspace, "backends/" + RequireBackend().Id + "/default");
        }
    }

    public IReadOnlyList<BackendChoice> Backends { get; }

    public string? SelectedBackendId { get; private set; }

    public GameLibraryEntry? ActiveGame { get; private set; }

    public ContentInfo? LoadedIdentity => _runtime?.LoadedContent;

    public BackendInfo? ActiveBackend => _runtime?.Backend;

    public RuntimeState? State => _runtime?.State;

    public bool HasGame
    {
        get
        {
            if ((object)ActiveGame != null)
            {
                return _runtime != null;
            }
            return false;
        }
    }

    public bool GuestOwnsInput { get; private set; }

    public bool Busy { get; private set; }

    public bool IsLaunching { get; private set; }

    public string? LaunchGameName { get; private set; }

    public string Status { get; private set; } = "Ready";

    public long LaunchGeneration => Interlocked.Read(in _launchGeneration);

    public long OperationFailureSequence => Interlocked.Read(in _operationFailureSequence);

    public string? LastOperationFailureCode => Volatile.Read(in _lastOperationFailureCode);

    public string? ErrorCode
    {
        get
        {
            return Volatile.Read(in _errorCode);
        }
        private set
        {
            Volatile.Write(ref _errorCode, value);
            if (value != null)
            {
                Volatile.Write(ref _lastOperationFailureCode, value);
                Interlocked.Increment(ref _operationFailureSequence);
            }
        }
    }

    public string? ErrorMessage { get; private set; }

    public double? Progress { get; private set; }

    public TelemetrySnapshot? Telemetry { get; private set; }

    public double? StartupSeconds { get; private set; }

    public string? LastReportPath { get; private set; }

    public ShutdownReport? LastShutdown { get; private set; }

    public ContentInfo? LastLoadedIdentity { get; private set; }

    public Func<InteractionRequest, CancellationToken, Task<InteractionResponse>>? InteractionHandler { get; set; }

    public Func<InteractionConversation, CancellationToken, Task>? InteractionStreamHandler { get; set; }

    public BackendChoice? SelectedBackend => Backends.FirstOrDefault((BackendChoice b) => b.Id == SelectedBackendId);

    public int? ActiveWorkerProcessId
    {
        get
        {
            if (!HasGame)
            {
                return null;
            }
            return _runtime?.WorkerProcessId;
        }
    }

    public OperationFailure? LastFailure { get; private set; }

    public string? LastFailureMessage { get; private set; }

    public string? ErrorRecoveryMessage
    {
        get
        {
            OperationFailure lastFailure = LastFailure;
            if ((object)lastFailure == null)
            {
                return null;
            }
            return RecoveryMessage(lastFailure.RecoveryAction);
        }
    }

    public bool IsLibraryScanning { get; private set; }

    public LibraryFolderImportResult? LastFolderImport { get; private set; }

    private FrameRatePreferenceStore FrameRatePreferenceStore => new FrameRatePreferenceStore(Paths.DataRoot);

    public int GlobalFrameRateTarget => _frameRatePreferences.GlobalTarget;

    public FrameRatePolicyState? NativeFrameRatePolicy { get; private set; }

    public string FrameRatePolicyExplanation { get; private set; } = "Original behavior is active until a qualified timing repair is applied.";

    public string? FrameRateReceiptId { get; private set; }

    public string? ExperimentalTimingModuleStatus { get; private set; }

    private TimingEnhancementRecipe? ActiveTimingRecipe { get; set; }

    private GpuPerformancePreferenceStore GpuPreferenceStore => new GpuPerformancePreferenceStore(Paths.DataRoot);

    public GpuPerformanceOptions GlobalGpuPerformance => _gpuPreferences.Global;

    public string? AppliedGpuPerformance { get; private set; }

    public IReadOnlyList<SaveView> IndexedSaveTargets { get; private set; } = Array.Empty<SaveView>();

    public IReadOnlyList<LaunchPhaseTiming> LastLaunchPhases { get; private set; } = Array.Empty<LaunchPhaseTiming>();

    public string OrbitServer { get; private set; } = "ldn.ryujinx.app";

    public bool OrbitEnabled { get; private set; }

    public string? OrbitInviteCode { get; private set; }

    public string? OrbitGameEntryId { get; private set; }

    public string OrbitMessage { get; private set; } = "Offline. Choose a game, then Quick Play or create a private invitation.";

    public LdnSessionState? OrbitState { get; private set; }

    public double? OrbitConnectMilliseconds { get; private set; }

    private string CurrentProfileId
    {
        get
        {
            if (!OrbitEnabled)
            {
                return "default";
            }
            return "orbit";
        }
    }

    public PerformancePlanPreview? PerformancePlan { get; private set; }

    public PerformancePlanReceipt? PerformanceReceipt { get; private set; }

    public SettingState? PerformanceRendererSetting { get; private set; }

    public ShaderCompilationResult? ShaderCompilation { get; private set; }

    public GraphicsDeviceResult? GraphicsDevice { get; private set; }

    public bool PerformanceReceiptActive
    {
        get
        {
            if (_runtime != null && _runtime == _performanceReceiptRuntime)
            {
                PerformancePlanReceipt performanceReceipt = PerformanceReceipt;
                if ((object)performanceReceipt != null)
                {
                    return !performanceReceipt.RolledBack;
                }
                return false;
            }
            return false;
        }
    }

    public bool CanApplyPerformancePlan
    {
        get
        {
            if (!_performancePlanConsumed)
            {
                PerformancePlanPreview? performancePlan = PerformancePlan;
                if ((object)performancePlan != null && performancePlan.CanApply)
                {
                    return _runtime == _performancePlanRuntime;
                }
            }
            return false;
        }
    }

    public bool CanRollbackPerformancePlan
    {
        get
        {
            bool flag = PerformanceReceiptActive;
            if (flag)
            {
                bool flag2;
                switch (State)
                {
                    case RuntimeState.Initialized:
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
            if (flag)
            {
                if (State != RuntimeState.Initialized)
                {
                    return !_performanceReceiptRequiresFreshLoad;
                }
                return true;
            }
            return false;
        }
    }

    public InfinityPerformanceRun? CurrentPerformance
    {
        get
        {
            lock (_performanceGate)
            {
                return (_performanceCapture == null) ? null : new InfinityPerformanceRun(_performanceCapture.Snapshot(), _captureTitle, _captureBuild, _captureLaunchGeneration, _captureStartedBy, null)
                {
                    GraphicsDevice = _captureGraphics,
                    ShaderCompilation = _captureShaders,
                    DisplayVersion = _captureVersion,
                    FrameRateAtStart = _captureFrameStart,
                    FrameRateAtEnd = _captureFrameEnd
                };
            }
        }
    }

    public IReadOnlyList<InfinityPerformanceRun> PerformanceHistory
    {
        get
        {
            lock (_performanceGate)
            {
                return _performanceHistory.ToArray();
            }
        }
    }

    public string? RecoveryBackendId
    {
        get
        {
            OperationFailure lastFailure = LastFailure;
            if ((object)lastFailure != null && lastFailure.Code == "unclean_shutdown")
            {
                string id = lastFailure.BackendId;
                if (id != null && Backends.Any((BackendChoice backend) => backend.Id == id))
                {
                    return id;
                }
            }
            return null;
        }
    }

    public bool CanRecoverSession
    {
        get
        {
            if (!Busy)
            {
                return RecoveryBackendId != null;
            }
            return false;
        }
    }

    public SaveMigrationStore SaveMigrations { get; private set; }

    public bool AutomaticSaveMigrationEnabled { get; private set; } = true;

    public bool SaveMigrationReady { get; private set; }

    public SaveMigrationDiscoveryReport? SaveMigrationDiscovery { get; private set; }

    public SaveMigrationStatus SaveMigrationState { get; private set; } = new SaveMigrationStatus(Array.Empty<SaveMigrationReceipt>(), Array.Empty<SaveMigrationOutcome>());

    public SaveMigrationReport? LastSaveMigrationReport { get; private set; }

    public IReadOnlyList<SaveMigrationCoreStatus> SaveMigrationCoreStatuses { get; private set; } = Array.Empty<SaveMigrationCoreStatus>();

    public IReadOnlyList<PortableSaveLocation> PortableSaveLocations { get; private set; } = Array.Empty<PortableSaveLocation>();

    public BackendInfo? LastBackendInfo { get; private set; }

    public PrerequisiteReport? Prerequisites { get; private set; }

    public IReadOnlyList<SettingView> Settings { get; private set; } = Array.Empty<SettingView>();

    public IReadOnlyList<SaveView> NativeSaves { get; private set; } = Array.Empty<SaveView>();

    public IReadOnlyList<string> SaveLimitations { get; private set; } = Array.Empty<string>();

    public GameContentConfiguration? ContentConfiguration { get; private set; }

    public NativeModState? NativeMods { get; private set; }

    public string? ServicesBackendId { get; private set; }

    public GameBuildIdentity? ModLibraryBinding { get; private set; }

    public IReadOnlyList<InstalledMod> InstalledMods { get; private set; } = Array.Empty<InstalledMod>();

    private EnhancementCoordinator? Coordinator
    {
        get
        {
            if ((object)_runtime?.LoadedContent == null)
            {
                return null;
            }
            return _enhancementCoordinator ?? (_enhancementCoordinator = new EnhancementCoordinator(_runtime, Enhancements, new EnhancementEnvironment()));
        }
    }

    public string? EnhancementSessionId => Coordinator?.SessionId;

    public SharedPrerequisiteStore SharedPrerequisites { get; private set; }

    public SharedPrerequisiteStatus SharedSetup { get; private set; } = new SharedPrerequisiteStatus(Array.Empty<SharedPrerequisiteRevision>(), Array.Empty<SharedPrerequisiteOutcome>());

    public SharedPrerequisiteProvisionReport? LastSharedSetupReport { get; private set; }

    public IReadOnlyList<SetupInstallationIssue> SharedSetupInstallationIssues { get; private set; } = Array.Empty<SetupInstallationIssue>();

    public RenderWindowState? GameWindow { get; private set; }

    public bool GameFullscreenPreference { get; private set; }

    public bool GameWindowClosing
    {
        get
        {
            if (_runtime != null)
            {
                return _closingRuntimes.ContainsKey(_runtime);
            }
            return false;
        }
    }

    public string? LastGameExitReason { get; private set; }

    private string DisplayPreferencePath => Path.Combine(Paths.DataRoot, "frontend-display", "display.json");

    public bool IsBackgroundMaintenance { get; private set; }

    public event Action? Changed;

    public event Action? MenuRequested;

    public event Action? GameClosed;

    public Task RefreshMissingArtworkAsync(bool retry = false)
    {
        return RunAsync("Reading local covers", async (CancellationToken ct) =>
        {
            if (_runtime != null || OrbitEnabled)
            {
                Status = "Stop the game and go offline before refreshing local covers";
                return;
            }
            List<GameLibraryEntry> missing = await Task.Run(() => (from e in Library.List(new LibraryQuery(null, FavoritesOnly: false, LibraryContentKind.Game, LibrarySort.Name, 0, 2000))
                                                                   where LocalArtwork.NeedsNativeArtwork(Library, e)
                                                                   select e).ToList(), ct);
            int restored = 0;
            int attempted = 0;
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int maximumAttempts = (retry ? 64 : 8);
            budget.CancelAfter(TimeSpan.FromSeconds(retry ? 45 : 8));
            try
            {
                foreach (BackendChoice backend in Backends.OrderBy((BackendChoice b) => (!(b.Id == SelectedBackendId)) ? 1 : 0))
                {
                    if (missing.Count == 0 || attempted >= maximumAttempts)
                    {
                        break;
                    }
                    NxRuntime inspector = null;
                    try
                    {
                        try
                        {
                            GameLibraryEntry[] array = missing.ToArray();
                            foreach (GameLibraryEntry entry in array)
                            {
                                budget.Token.ThrowIfCancellationRequested();
                                if (attempted >= maximumAttempts)
                                {
                                    break;
                                }
                                string item = entry.EntryId + ":" + entry.Content.Sha256 + ":" + backend.Id + ":" + backend.Revision;
                                if (retry || _artworkInspections.Add(item))
                                {
                                    _artworkInspections.Add(item);
                                    if (ArtworkContentUnchanged(entry))
                                    {
                                        attempted++;
                                        if (inspector == null)
                                        {
                                            inspector = await ConnectAsync(backend, budget.Token);
                                        }
                                        try
                                        {
                                            ContentInfo content = await inspector.InspectContentForProfileAsync(entry.Content.AbsolutePath, Paths.DataRoot, "default", includeArtwork: true, budget.Token);
                                            bool flag = ArtworkContentUnchanged(entry);
                                            if (flag)
                                            {
                                                flag = await Task.Run(() => LocalArtwork.TryStoreNative(Library, entry, content), budget.Token);
                                            }
                                            if (flag)
                                            {
                                                missing.Remove(entry);
                                                restored++;
                                            }
                                        }
                                        catch (Exception ex) when ((ex is NxException || ex is ServiceException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
                                        {
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex2) when ((ex2 is NxException || ex2 is ServiceException || ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
                        {
                        }
                    }
                    finally
                    {
                        if (inspector != null)
                        {
                            await DisposeInspectorAsync(inspector);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            ct.ThrowIfCancellationRequested();
            Status = ((restored == 0 && missing.Count == 0) ? "Local covers are ready" : $"Local covers · {restored} recovered · {missing.Count} still need artwork; matching cached covers remain available");
        });
    }

    private static bool ArtworkContentUnchanged(GameLibraryEntry entry)
    {
        try
        {
            FileInfo fileInfo = new FileInfo(entry.Content.AbsolutePath);
            return fileInfo.Exists && fileInfo.Length == entry.Content.Size && fileInfo.LastWriteTimeUtc == entry.Content.LastWriteUtc.UtcDateTime;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            return false;
        }
    }

    public Task RefreshGraphicsContextAsync()
    {
        return RunAsync("Reading active graphics device", async (CancellationToken ct) =>
        {
            NxRuntime runtime = await EnsureInitializedAsync(ct);
            TelemetrySnapshot snapshot = (Telemetry = await runtime.GetTelemetryAsync(ct));
            ObservePerformance(runtime, snapshot);
            GraphicsDeviceResult? graphicsDevice = GraphicsDevice;
            Status = (((object)graphicsDevice != null && graphicsDevice.Snapshot?.SelectedDeviceVerified == true) ? "Active GPU and native driver identity read" : "Available renderer information read · exact hardware may remain unknown");
        });
    }

    public Task MarkGraphicsIssueAsync(string issueId)
    {
        return RunAsync("Recording graphics observation", async (CancellationToken ct) =>
        {
            CompatibilityIssue compatibilityIssue = CompatibilityCatalog.Issues.FirstOrDefault((CompatibilityIssue issue) => issue.Id == issueId) ?? throw new NxException("invalid_argument", "Choose a listed graphics investigation.");
            NxRuntime runtime = _runtime ?? throw new NxException("invalid_state", "Launch the affected game before marking an issue.");
            bool flag = !HasGame;
            if (!flag)
            {
                RuntimeState state = runtime.State;
                bool flag2 = (uint)(state - 2) <= 2u;
                flag = !flag2;
            }
            if (flag)
            {
                throw new NxException("invalid_state", "A loaded game is required for an issue observation.");
            }
            string text = runtime.LoadedContent?.TitleId;
            if (text == null || (compatibilityIssue.TitleIds.Count != 0 && !compatibilityIssue.TitleIds.Contains(text, StringComparer.OrdinalIgnoreCase)))
            {
                throw new NxException("identity_mismatch", "This report is for a different game. Choose the matching loaded title before recording its observation.");
            }
            Stopwatch watch = Stopwatch.StartNew();
            TelemetrySnapshot telemetrySnapshot = await runtime.GetTelemetryAsync(ct);
            watch.Stop();
            Telemetry = telemetrySnapshot;
            ObservePerformance(runtime, telemetrySnapshot);
            PerformanceCaptureSummary performanceCaptureSummary = CurrentPerformance?.Capture;
            GraphicsIssueObservation item = new GraphicsIssueObservation(DateTimeOffset.UtcNow, issueId, runtime.Backend.BackendId, runtime.Backend.BackendRevision, runtime.Backend.AdapterVersion, KnownHex(runtime.LoadedContent?.TitleId, 16), KnownHex(runtime.LoadedContent?.BuildId, 64), performanceCaptureSummary?.CaptureId, performanceCaptureSummary?.LatestSegmentId, performanceCaptureSummary?.LatestSequence, watch.Elapsed.TotalMilliseconds, GraphicsDeviceTelemetry.Read(telemetrySnapshot), ShaderCompilationTelemetry.Read(telemetrySnapshot));
            lock (_graphicsObservations)
            {
                if (_graphicsObservations.Count == 32)
                {
                    _graphicsObservations.RemoveAt(0);
                }
                _graphicsObservations.Add(item);
            }
            Status = "Issue observation recorded · include the matching scene recording with your exported report";
            Log("graphics.issue_marked", new
            {
                issueId = issueId,
                backendId = SafeBackend(runtime.Backend.BackendId),
                scope = "manual_observation_boundary"
            });
        });
    }

    public string? GetLastSdkReport()
    {
        return _lastReport;
    }

    public InfinitySession(InfinityPaths paths)
    {
        Paths = paths;
        Workspace = new NxWorkspace(paths.DataRoot);
        Library = new GameLibraryStore(Workspace);
        Backends = BackendChoice.Discover(paths);
        Profiles = new ProfileRouter(Workspace, Backends.Select((BackendChoice b) => new BackendDefinition(b.Id, b.Revision, (!(b.Qualification == "qualified")) ? BackendQualification.Experimental : BackendQualification.Qualified)));
        Profiles.UseNxiCore();
        Enhancements = new EnhancementStore(Workspace);
        Mods = new ModStore(Workspace);
        InitializeSharedSetup();
        InitializeSaveMigration();
        SelectedBackendId = Backends.FirstOrDefault()?.Id;
        ReadDisplayPreference();
        _frameRatePreferences = FrameRatePreferenceStore.Read();
        _gpuPreferences = GpuPreferenceStore.Read();
        foreach (EnhancementProfile item in EnhancementBaselines.CreateResearchTemplates(SelectedBackend?.Revision))
        {
            Enhancements.Put(item);
        }
    }

    private void Notify()
    {
        Changed?.Invoke();
    }

    public void Cancel()
    {
        _operation?.Cancel();
    }

    public void ClearError()
    {
        ErrorCode = null;
        ErrorMessage = null;
        Notify();
    }

    private void Log(string name, object? data = null)
    {
        lock (_events)
        {
            if (_events.Count >= 128)
            {
                _events.Dequeue();
            }
            _events.Enqueue(new
            {
                utc = DateTimeOffset.UtcNow,
                name = name,
                data = data
            });
        }
    }

    public Task RunAsync(string status, Func<CancellationToken, Task> operation)
    {
        return RunOperationAsync(status, operation);
    }

    private async Task RunOperationAsync(string status, Func<CancellationToken, Task> operation, bool launching = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            _operation = cancellation;
            _operationSequence++;
            _diagnosticOperation = OperationId(status);
            _diagnosticStage = "requested";
            _diagnosticBackend = SelectedBackend;
            Busy = true;
            IsLaunching = launching;
            if (launching)
            {
                BeginLaunchTiming();
            }
            Status = status;
            ErrorCode = null;
            ErrorMessage = null;
            Progress = null;
            Notify();
            try
            {
                await operation(cancellation.Token);
                if (Status == status)
                {
                    Status = ((!HasGame) ? "Ready" : (State?.ToString() ?? "Session ready"));
                }
            }
            catch (OperationCanceledException)
            {
                Status = "Canceled";
                Log("operation.canceled");
            }
            catch (NxException ex2) when (launching && cancellation.IsCancellationRequested && ex2.Code == "cancelled")
            {
                Status = "Canceled";
                Log("operation.canceled");
            }
            catch (Exception ex3)
            {
                InfinitySession infinitySession = this;
                string code;
                if (ex3 is NxException ex4)
                {
                    code = ex4.Code;
                }
                else
                {
                    code = ((ex3 is ServiceException { Code: var code2 }) ? code2.ToString() : ex3.GetType().Name);
                }
                infinitySession.RecordFailure(code, ex3.Message);
                Status = "Action needs attention";
            }
            finally
            {
                if (launching)
                {
                    FinishLaunchTiming();
                }
                Busy = false;
                IsLaunching = false;
                LaunchGameName = null;
                Progress = null;
                _operation = null;
                Notify();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SelectBackendAsync(string id)
    {
        return SelectBackendAsync(id, persistDefault: true);
    }

    public Task SelectBackendAsync(string id, bool persistDefault)
    {
        return RunAsync("Changing backend", async (CancellationToken ct) =>
        {
            BackendChoice chosen = Backends.FirstOrDefault((BackendChoice b) => b.Id == id) ?? throw new NxException("missing_dependency", "The selected source-built core is not in this package.");
            if (_runtime != null)
            {
                await StopInternalAsync();
            }
            InvalidateOrbitPreparation("Core selection changed. Prepare the selected game again before launching.");
            if (persistDefault)
            {
                Profiles.SetGlobal(new BackendSelection(id, (!(chosen.Qualification == "qualified")) ? BackendSelectionMode.ExperimentalOptIn : BackendSelectionMode.QualifiedOnly));
            }
            SelectedBackendId = id;
            ClearServiceViews();
            Log("backend.selected", new
            {
                backendId = id,
                revision = chosen.Revision
            });
        });
    }

    private BackendChoice RequireBackend()
    {
        return SelectedBackend ?? throw new NxException("missing_dependency", "No matching source-built backend is installed. Restore the complete package.");
    }

    private async Task<NxRuntime> ConnectAsync(BackendChoice backend, CancellationToken ct)
    {
        if (backend.Id == "nxi")
        {
            await Task.Run(() => NxiProfileMigration.Ensure(Paths.DataRoot, CurrentProfileId), ct);
        }
        DiagnosticStage("backend.verify", backend);
        if (!_verifiedLaunches.TryGetValue(backend, out BackendLaunch launch))
        {
            launch = await Task.Run(() => backend.VerifyAsync(Paths, ct), ct);
            _verifiedLaunches.TryAdd(backend, launch);
        }
        await ProvisionSharedBeforeConnectionAsync(launch, ct);
        DiagnosticStage("backend.connect", backend);
        NxRuntime nxRuntime = await Task.Run(() => NxRuntime.ConnectAsync(launch, ct), ct);
        if (SelectedBackendId == backend.Id)
        {
            LastBackendInfo = nxRuntime.Backend;
        }
        return nxRuntime;
    }

    public async Task<ContentInfo> InspectAsync(string path, CancellationToken ct = default(CancellationToken))
    {
        BackendChoice backend = RequireBackend();
        NxRuntime runtime = _runtime;
        bool flag;
        if (runtime != null)
        {
            RuntimeState state = runtime.State;
            if ((uint)state <= 1u)
            {
                flag = true;
                goto IL_004f;
            }
        }
        flag = false;
        goto IL_004f;
    IL_004f:
        if (flag && runtime.Backend.BackendId == backend.Id)
        {
            return await InspectWithRuntimeAsync(runtime, backend, path, ct);
        }
        NxRuntime inspector = await ConnectAsync(backend, ct);
        ContentInfo result;
        try
        {
            result = await InspectWithRuntimeAsync(inspector, backend, path, ct);
        }
        finally
        {
            await DisposeInspectorAsync(inspector);
        }
        return result;
    }

    private async Task<ContentInfo> InspectWithRuntimeAsync(NxRuntime inspector, BackendChoice backend, string path, CancellationToken ct)
    {
        if (inspector.Supports("prerequisites.get"))
        {
            DiagnosticStage("prerequisites.get", backend);
            RememberPrerequisites(await inspector.GetPrerequisitesForProfileAsync(Paths.DataRoot, CurrentProfileId, ct), backend);
        }
        DiagnosticStage("content.inspect", backend);
        ContentInfo contentInfo = await inspector.InspectContentForProfileAsync(path, Paths.DataRoot, CurrentProfileId, includeArtwork: true, ct);
        RememberContentInspection(contentInfo, backend);
        return contentInfo;
    }

    public Task AddFilesAsync(IEnumerable<string> paths, LibraryContentKind kind = LibraryContentKind.Game, string? related = null)
    {
        return RunAsync("Reading local content", async (CancellationToken ct) =>
        {
            foreach (string path in paths)
            {
                ct.ThrowIfCancellationRequested();
                LibraryContentKind kind2 = ((kind == LibraryContentKind.Game) ? LibraryContentClassification.BeforeInspection(path) : kind);
                GameLibraryEntry entry = await Library.RegisterAsync(new ContentRegistrationRequest(path, kind2, null, related), new Progress<ContentHashProgress>((ContentHashProgress p) =>
                {
                    Progress = ((p.TotalBytes == 0L) ? 0.0 : ((double)p.BytesRead / (double)p.TotalBytes));
                    Notify();
                }), ct);
                Status = "Inspecting " + entry.DisplayName;
                Notify();
                BackendChoice backend = SelectedBackend;
                if ((object)backend != null)
                {
                    ContentInfo contentInfo = await InspectAsync(path, ct);
                    Observe(entry, contentInfo, backend, loaded: false);
                    ContentIssue contentIssue = contentInfo.Issues.FirstOrDefault((ContentIssue i) => i.Severity == "error");
                    if ((object)contentIssue != null)
                    {
                        Status = "Added; local prerequisites need attention";
                        RecordFailure(contentIssue.Code, contentIssue.Message);
                    }
                    else
                    {
                        LocalArtwork.TryStoreNative(Library, entry, contentInfo);
                    }
                }
            }
            if (ErrorCode == null)
            {
                Status = "Content added";
            }
        });
    }

    public static string? KnownHex(string? value, int length)
    {
        if (value == null || value.Length != length || !value.All(Uri.IsHexDigit) || !value.Any((char c) => c != '0'))
        {
            return null;
        }
        return value.ToUpperInvariant();
    }

    public static string? TitleOf(GameLibraryEntry? entry)
    {
        return entry?.Observations.Reverse().Select((LibraryCoreObservation o) => KnownHex(o.TitleId, 16)).FirstOrDefault((string x) => x != null);
    }

    public static string? BuildOf(GameLibraryEntry? entry)
    {
        return entry?.Observations.Reverse().Select((LibraryCoreObservation o) => KnownHex(o.BuildId, 64)).FirstOrDefault((string x) => x != null);
    }

    private void Observe(GameLibraryEntry entry, ContentInfo metadata, BackendChoice backend, bool loaded)
    {
        if (metadata.Issues.Any((ContentIssue issue) => issue.Severity == "error"))
        {
            return;
        }
        LibraryContentKind kind = ((metadata.Kind == null) ? entry.Kind : LibraryContentClassification.FromNative(metadata));
        string displayName = NativeDisplayName(metadata.Name);
        string text = metadata.DisplayVersion?.Trim();
        int? num = text?.Length;
        bool flag;
        if (num.HasValue)
        {
            int valueOrDefault = num.GetValueOrDefault();
            if (valueOrDefault > 64 || valueOrDefault == 0)
            {
                flag = true;
                goto IL_00a2;
            }
        }
        flag = false;
        goto IL_00a2;
    IL_00a2:
        if (flag || (text != null && text.Any(char.IsControl)))
        {
            text = null;
        }
        Library.ApplyNativeInspection(entry.EntryId, kind, new LibraryCoreObservation(backend.Id, backend.Revision, entry.Content.Sha256, KnownHex(metadata.TitleId, 16), KnownHex(metadata.BuildId, 64), displayName, text, loaded, loaded ? "NX Infinity runtime native load acknowledgement" : "NX Infinity runtime native loader inspection", DateTimeOffset.UtcNow, metadata.Kind));
    }

    private static string? NativeDisplayName(string? value)
    {
        if (value == null)
        {
            return null;
        }
        StringBuilder stringBuilder = new StringBuilder();
        bool flag = false;
        foreach (Rune item in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(item))
            {
                flag = stringBuilder.Length > 0;
            }
            else if (!Rune.IsControl(item))
            {
                if (stringBuilder.Length + (flag ? 1 : 0) + item.Utf16SequenceLength > 256)
                {
                    break;
                }
                if (flag)
                {
                    stringBuilder.Append(' ');
                }
                stringBuilder.Append(item.ToString());
                flag = false;
            }
        }
        if (stringBuilder.Length != 0)
        {
            return stringBuilder.ToString();
        }
        return null;
    }

    private async Task<NxRuntime> EnsureInitializedAsync(CancellationToken ct)
    {
        NxRuntime runtime = _runtime;
        if (runtime != null)
        {
            RuntimeState state = runtime.State;
            if (state != RuntimeState.Created && state != RuntimeState.Faulted && state != RuntimeState.Disposed && state != RuntimeState.Stopped)
            {
                return _runtime;
            }
        }
        runtime = _runtime;
        if (runtime != null && runtime.State != RuntimeState.Created)
        {
            await StopInternalAsync();
        }
        LastLoadedIdentity = null;
        LastShutdown = null;
        LastGameExitReason = null;
        Telemetry = null;
        GraphicsDevice = null;
        ShaderCompilation = null;
        GameWindow = null;
        NxRuntime runtime2 = _runtime;
        if (runtime2 == null)
        {
            runtime2 = (_runtime = await ConnectAsync(RequireBackend(), ct));
            _eventTask = ReadEventsAsync(runtime2);
        }
        try
        {
            DiagnosticStage("runtime.initialize");
            if ((object)_orbitNetwork != null)
            {
                OrbitState = await runtime2.ConfigureLdnAsync(_orbitNetwork, ct);
            }
            await runtime2.InitializeAsync(new RuntimeOptions
            {
                DataRoot = Paths.DataRoot,
                ProfileId = CurrentProfileId,
                WindowTitle = "NX Infinity",
                Headless = false
            }, ct);
            if (runtime2.Supports("window.set"))
            {
                DiagnosticStage("window.hide");
                await ChangeGameWindowAsync(runtime2, new RenderWindowChange
                {
                    Visible = false
                }, ct);
            }
            return runtime2;
        }
        catch
        {
            try
            {
                await DisposeInspectorAsync(runtime2);
            }
            catch (Exception ex)
            {
                Log("cleanup.failed", new
                {
                    code = SafeCode((ex is NxException ex2) ? ex2.Code : ex.GetType().Name)
                });
            }
            finally
            {
                if (_runtime == runtime2)
                {
                    _runtime = null;
                }
            }
            throw;
        }
    }

    public Task LaunchAsync(string entryId, string? backendOverride = null)
    {
        return RunOperationAsync("Checking game files", async (CancellationToken ct) =>
        {
            bool flag = backendOverride != null;
            if (flag)
            {
                string text = backendOverride;
                bool flag2 = ((text == "nxi" || text == "ryujinx") ? true : false);
                flag = !flag2;
            }
            if (flag)
            {
                throw new NxException("missing_dependency", "This shortcut targets a retired core. Create a new NX Infinity shortcut.");
            }
            Interlocked.Increment(ref _launchGeneration);
            LaunchGameName = Library.Get(entryId).DisplayName;
            Notify();
            Stopwatch watch = Stopwatch.StartNew();
            DiagnosticStage("content.verify");
            Progress<ContentHashProgress> hashProgress = new Progress<ContentHashProgress>((ContentHashProgress p) =>
            {
                Progress = ((p.TotalBytes == 0L) ? 0.0 : ((double)p.BytesRead / (double)p.TotalBytes));
                Notify();
            });
            LibraryLaunchTarget target = await Task.Run(() => Library.ResolveForLaunchAsync(entryId, hashProgress, ct), ct);
            if (target.Entry.Kind != LibraryContentKind.Game)
            {
                throw new NxException("invalid_content", "Launch the base game; updates and DLC are selected in Content.");
            }
            string title = TitleOf(target.Entry);
            if ((object)Profiles.Read().Global == null)
            {
                BackendChoice backendChoice = RequireBackend();
                Profiles.SetGlobal(new BackendSelection(backendChoice.Id, (!(backendChoice.Qualification == "qualified")) ? BackendSelectionMode.ExperimentalOptIn : BackendSelectionMode.QualifiedOnly));
            }
            string nextBackendId = RequireBackend().Id;
            ValidateOrbitLaunch(entryId, nextBackendId);
            if (_runtime != null && (_runtime.State != RuntimeState.Initialized || _runtime.Backend.BackendId != nextBackendId || _runtimeInitializationPending))
            {
                await StopInternalAsync();
            }
            SelectedBackendId = nextBackendId;
            BackendChoice backend = RequireBackend();
            DiagnosticStage("content.inspect", backend);
            Status = "Checking local content prerequisites";
            Progress = null;
            Notify();
            try
            {
                if (!target.Entry.Observations.Any((LibraryCoreObservation o) => o.BackendId == backend.Id && o.BackendRevision == backend.Revision && o.ContentSha256 == target.Entry.Content.Sha256 && o.LoaderAccepted))
                {
                    ContentInfo contentInfo;
                    if (_runtime == null)
                    {
                        NxRuntime nxRuntime = await ConnectAsync(backend, ct);
                        _runtime = nxRuntime;
                        _eventTask = ReadEventsAsync(nxRuntime);
                        contentInfo = await InspectWithRuntimeAsync(nxRuntime, backend, target.AbsolutePath, ct);
                    }
                    else
                    {
                        contentInfo = await InspectWithRuntimeAsync(_runtime, backend, target.AbsolutePath, ct);
                    }
                    Observe(target.Entry, contentInfo, backend, loaded: false);
                    ContentIssue contentIssue = contentInfo.Issues.FirstOrDefault((ContentIssue i) => i.Severity == "error");
                    if ((object)contentIssue != null)
                    {
                        throw new NxException(contentIssue.Code, contentIssue.Message);
                    }
                }
                Status = "Initializing " + backend.Name;
                Notify();
                NxRuntime runtime = await EnsureInitializedAsync(ct);
                DiagnosticStage("settings.apply", backend);
                LibrarySettingRequestView settingRequests = Library.GetSettingRequests(entryId, backend.Id, backend.Revision);
                if (settingRequests.RequiresReview)
                {
                    throw new NxException("settings_review_required", "The core revision changed. Review this game's saved settings before launch.");
                }
                await ApplyLaunchGraphicsAsync(runtime, entryId, title, settingRequests.Stored?.Values, ct);
                DiagnosticStage("mods.configure", backend);
                await ConfigureStoredModsBeforeLoadAsync(runtime, Library.Get(entryId), backend, ct);
                await ValidateOrbitContentAsync(runtime, target.Entry, ct);
                Status = "Loading " + target.Entry.DisplayName;
                Notify();
                DiagnosticStage("content.load", backend);
                ContentInfo identity = await runtime.LoadAsync(target.AbsolutePath, ct);
                LastLoadedIdentity = identity;
                ValidateOrbitLoaded(identity);
                if (title != null)
                {
                    string text2 = KnownHex(identity.TitleId, 16);
                    if (text2 != null && text2 != title)
                    {
                        throw new NxException("content_identity_changed", "The native loader identified a different title. Inspect the selected content again.");
                    }
                }
                Observe(target.Entry, identity, backend, loaded: true);
                ActiveGame = Library.Get(entryId);
                GuestOwnsInput = false;
                DiagnosticStage("frame_rate.apply", backend);
                await ApplyFrameRatePolicyBeforeStartAsync(runtime, ActiveGame, ct);
                BeginPerformanceCapture(runtime, "launch");
                if (runtime.Supports("input.routing.set"))
                {
                    await runtime.SetInputTargetAsync(guest: true, ct);
                }
                _guestRequested = true;
                Status = "Starting game";
                Progress = null;
                Notify();
                DiagnosticStage("runtime.start", backend);
                await runtime.StartAsync(ct);
                switch (identity.Format)
                {
                    case "xci":
                    case "nsp":
                    case "nca":
                        flag = true;
                        break;
                    default:
                        flag = false;
                        break;
                }
                if (flag)
                {
                    DiagnosticStage("runtime.first_frame", backend);
                    Status = "Preparing the first game frame";
                    Notify();
                    Stopwatch firstFrame = Stopwatch.StartNew();
                    while (runtime.State == RuntimeState.Running)
                    {
                        TelemetrySnapshot telemetrySnapshot = await runtime.GetTelemetryAsync(ct);
                        Telemetry = telemetrySnapshot;
                        if (telemetrySnapshot.FrameCount > 0)
                        {
                            break;
                        }
                        if (firstFrame.Elapsed > TimeSpan.FromMinutes(2L))
                        {
                            int num;
                            if (ServicesBackendId == backend.Id)
                            {
                                PrerequisiteReport prerequisites = Prerequisites;
                                num = (((object)prerequisites != null && prerequisites.FirmwareVersion == null) ? 1 : 0);
                            }
                            else
                            {
                                num = 0;
                            }
                            flag = (byte)num != 0;
                            if (flag)
                            {
                                string text = backend.Id;
                                bool flag2 = ((text == "eden" || text == "citron") ? true : false);
                                flag = flag2;
                            }
                            throw new NxException("startup_timeout", flag ? "The game did not produce a frame within two minutes. This core could not read an installed firmware version. Open Content, review the firmware setup result and import supported firmware if missing, then retry. This timeout does not identify the exact cause." : "The game did not produce a frame within two minutes. Check this core's setup and game settings, then retry.");
                        }
                        await Task.Delay(150, ct);
                    }
                    RuntimeState state = runtime.State;
                    if ((uint)(state - 5) <= 2u)
                    {
                        throw new NxException("core_exited", "The game stopped before its first frame.");
                    }
                }
                _launchId = Library.MarkLaunch(entryId, backend.Id, backend.Revision).LaunchId;
                StartupSeconds = watch.Elapsed.TotalSeconds;
                _frameTimes.Clear();
                if (runtime.State == RuntimeState.Running && runtime.Supports("window.set"))
                {
                    DiagnosticStage("window.show", backend);
                    await ChangeGameWindowAsync(runtime, new RenderWindowChange
                    {
                        Visible = true,
                        Fullscreen = GameFullscreenPreference
                    }, ct);
                }
                GuestOwnsInput = runtime.State == RuntimeState.Running && !_interactionOwnsInput;
                Status = (GuestOwnsInput ? ("Playing " + ActiveGame.DisplayName) : "Game exited or awaiting interaction");
                Log("game.started", new
                {
                    backendId = backend.Id,
                    revision = backend.Revision,
                    titleId = KnownHex(identity.TitleId, 16),
                    buildId = KnownHex(identity.BuildId, 64)
                });
            }
            catch
            {
                try
                {
                    await StopInternalAsync(LibraryLaunchOutcome.Failed);
                }
                catch (Exception ex)
                {
                    Log("cleanup.failed", new
                    {
                        code = SafeCode((ex is NxException ex2) ? ex2.Code : ex.GetType().Name)
                    });
                }
                throw;
            }
        }, launching: true);
    }

    public Task PauseAsync()
    {
        return RunAsync("Opening game menu", async (CancellationToken ct) =>
        {
            NxRuntime runtime = _runtime;
            if (runtime == null)
            {
                Status = "Game is already closed";
            }
            else
            {
                await PauseInternalAsync(runtime, ct);
            }
        });
    }

    public Task ResumeAsync()
    {
        return RunAsync("Resuming game", async (CancellationToken ct) =>
        {
            NxRuntime runtime = _runtime;
            if (runtime == null)
            {
                Status = "Game is already closed";
            }
            else if (!StopIfTerminal(runtime))
            {
                if ((object)CurrentPerformance == null)
                {
                    NxRuntime runtime2 = runtime;
                    BeginPerformanceCapture(runtime2, "resume_after_settings", await runtime.GetTelemetryAsync(ct));
                }
                _guestRequested = true;
                if (runtime.Supports("input.routing.set"))
                {
                    await runtime.SetInputTargetAsync(guest: true, ct);
                }
                if (runtime.State == RuntimeState.Paused)
                {
                    await runtime.ResumeAsync(ct);
                }
                if (runtime.Supports("window.set"))
                {
                    await ChangeGameWindowAsync(runtime, new RenderWindowChange
                    {
                        Visible = true
                    }, ct);
                }
                GuestOwnsInput = runtime.State == RuntimeState.Running && !_interactionOwnsInput;
                Status = "Playing " + ActiveGame?.DisplayName;
            }
        });
    }

    public Task StopAsync()
    {
        return RunAsync("Stopping game safely", (CancellationToken _) => StopInternalAsync());
    }

    public Task RestartAsync()
    {
        string text = ActiveGame?.EntryId;
        if (text != null)
        {
            return LaunchAsync(text);
        }
        return Task.CompletedTask;
    }

    private async Task StopInternalAsync(LibraryLaunchOutcome outcome = LibraryLaunchOutcome.Completed)
    {
        _guestRequested = false;
        GuestOwnsInput = false;
        foreach (PendingInteraction value in _pendingInteractions.Values)
        {
            try
            {
                value.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        NxRuntime runtime = _runtime;
        if (runtime == null)
        {
            return;
        }
        bool hadGame = (object)ActiveGame != null;
        try
        {
            RuntimeState state = runtime.State;
            if ((uint)(state - 1) <= 3u)
            {
                await runtime.StopAsync();
            }
            try
            {
                _lastReport = await runtime.CreateWorkerDiagnosticReportAsync();
            }
            catch (NxException)
            {
            }
        }
        finally
        {
            RuntimeState stateBeforeDispose = runtime.State;
            try
            {
                await runtime.DisposeAsync();
                GameLibraryEntry activeGame = ActiveGame;
                if ((object)activeGame != null)
                {
                    string launchId = _launchId;
                    if (launchId != null)
                    {
                        Library.CompleteLaunch(activeGame.EntryId, launchId, (runtime.Shutdown?.Graceful ?? false) ? outcome : LibraryLaunchOutcome.Interrupted, (runtime.Shutdown?.Graceful ?? false) ? null : "unclean_shutdown");
                    }
                }
                Log("session.disposed", new
                {
                    graceful = runtime.Shutdown?.Graceful,
                    forced = runtime.Shutdown?.ForcedTermination
                });
            }
            finally
            {
                await RememberDisposedRuntimeAsync(runtime, stateBeforeDispose);
                EndPerformanceRuntime(runtime);
                _runtime = null;
                _launchId = null;
                ActiveGame = null;
                Telemetry = null;
                GameWindow = null;
                _interactionOwnsInput = false;
                _enhancementCoordinator = null;
                NativeFrameRatePolicy = null;
                FrameRateReceiptId = null;
                if (outcome != LibraryLaunchOutcome.Failed)
                {
                    SelectedBackendId = Backends.FirstOrDefault()?.Id;
                }
                Status = "Back in your library";
                if (hadGame)
                {
                    GameClosed?.Invoke();
                }
            }
        }
    }

    private async Task ReadEventsAsync(NxRuntime runtime)
    {
        _ = 1;
        try
        {
            await foreach (RuntimeEvent item in runtime.ReadEventsAsync())
            {
                if (_runtime != runtime || _disposed)
                {
                    break;
                }
                InteractionRequest interaction = item.Interaction;
                if ((object)interaction != null)
                {
                    PendingInteraction pendingInteraction = new PendingInteraction();
                    if (!_pendingInteractions.TryAdd(interaction.InteractionId, pendingInteraction))
                    {
                        pendingInteraction.Cancellation.Dispose();
                        if (_pendingInteractions.TryGetValue(interaction.InteractionId, out PendingInteraction value))
                        {
                            value.Updates.Writer.TryWrite(interaction);
                        }
                        continue;
                    }
                    int? timeoutSeconds = interaction.TimeoutSeconds;
                    if (timeoutSeconds.HasValue)
                    {
                        int valueOrDefault = timeoutSeconds.GetValueOrDefault();
                        if (valueOrDefault > 0 && valueOrDefault <= 3600)
                        {
                            pendingInteraction.Cancellation.CancelAfter(TimeSpan.FromSeconds(interaction.TimeoutSeconds.Value + 5));
                        }
                    }
                    _interactionOwnsInput = true;
                    GuestOwnsInput = false;
                    _ = HandleInteractionAsync(runtime, interaction, pendingInteraction);
                }
                else
                {
                    string name = item.Name;
                    if ((name == "interaction.closed" || name == "interaction.canceled") ? true : false)
                    {
                        if (item.Data.TryGetProperty("interactionId", out var value2) && value2.ValueKind == JsonValueKind.String && _pendingInteractions.TryRemove(value2.GetString(), out PendingInteraction value3))
                        {
                            value3.Closed.TrySetResult(result: true);
                            value3.Updates.Writer.TryComplete();
                            try
                            {
                                value3.Cancellation.Cancel();
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        }
                    }
                    else if (!ObserveWindowEvent(runtime, item))
                    {
                        JsonElement value4;
                        double value5;
                        if (item.Name == "shader.progress")
                        {
                            ObserveShaderProgress(item.Data);
                        }
                        else if (item.Name == "progress" && item.Data.TryGetProperty("progress", out value4) && value4.TryGetDouble(out value5) && double.IsFinite(value5))
                        {
                            Progress = Math.Clamp(value5, 0.0, 1.0);
                        }
                    }
                }
                Log(item.Name);
                Notify();
            }
        }
        catch (Exception ex2)
        {
            Log("event_stream.ended", new
            {
                code = ((ex2 is NxException ex3) ? ex3.Code : ex2.GetType().Name)
            });
        }
        finally
        {
            bool flag = _runtime == runtime && !_disposed;
            if (flag)
            {
                RuntimeState state = runtime.State;
                bool flag2 = ((state == RuntimeState.Stopped || state == RuntimeState.Disposed) ? true : false);
                flag = !flag2;
            }
            if (flag)
            {
                QueueNativeClosure(runtime, "native_event_stream_ended");
            }
        }
    }

    private async Task HandleInteractionAsync(NxRuntime runtime, InteractionRequest request, PendingInteraction pending)
    {
        CancellationTokenSource cancellation = pending.Cancellation;
        bool entered = false;
        try
        {
            _ = 4;
            try
            {
                await _dialogGate.WaitAsync(cancellation.Token);
                entered = true;
                if (_runtime != runtime || _disposed)
                {
                    goto end_IL_00bd;
                }
                if (InteractionStreamHandler != null)
                {
                    await InteractionStreamHandler(new InteractionConversation(request, pending.Updates.Reader, Respond), cancellation.Token);
                }
                else
                {
                    InteractionResponse interactionResponse = ((InteractionHandler != null) ? (await InteractionHandler(request, cancellation.Token)) : new InteractionResponse
                    {
                        InteractionId = request.InteractionId,
                        Accepted = false
                    });
                    InteractionResponse response = interactionResponse;
                    await Respond(response, cancellation.Token);
                }
                await pending.Closed.Task.WaitAsync(cancellation.Token);
                goto end_IL_009e;
            end_IL_00bd:;
            }
            catch (OperationCanceledException)
            {
                Log("interaction.dismissed");
                goto end_IL_009e;
            }
            catch (Exception ex2)
            {
                Log("interaction.failed", new
                {
                    code = ((ex2 is NxException ex3) ? ex3.Code : ex2.GetType().Name)
                });
                goto end_IL_009e;
            }
        end_IL_009e:;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, PendingInteraction>>)_pendingInteractions).Remove(new KeyValuePair<string, PendingInteraction>(request.InteractionId, pending));
            pending.Updates.Writer.TryComplete();
            cancellation.Dispose();
            if (entered)
            {
                _dialogGate.Release();
            }
            if (_runtime == runtime)
            {
                _interactionOwnsInput = !_pendingInteractions.IsEmpty;
                GuestOwnsInput = false;
                if (pending.Closed.Task.IsCompletedSuccessfully && _guestRequested && !_interactionOwnsInput && runtime.State == RuntimeState.Running)
                {
                    try
                    {
                        if (runtime.Supports("input.routing.set"))
                        {
                            await runtime.SetInputTargetAsync(guest: true);
                        }
                        if (runtime.Supports("window.set"))
                        {
                            await ChangeGameWindowAsync(runtime, new RenderWindowChange
                            {
                                Visible = true
                            });
                        }
                        GuestOwnsInput = _runtime == runtime && _guestRequested && !_interactionOwnsInput && runtime.State == RuntimeState.Running;
                    }
                    catch (NxException ex4)
                    {
                        Log("interaction.input_return_failed", new
                        {
                            code = ex4.Code
                        });
                    }
                }
                Notify();
            }
        }
        async Task Respond(InteractionResponse interactionResponse2, CancellationToken token)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (_runtime != runtime || _disposed || interactionResponse2.InteractionId != request.InteractionId || !_pendingInteractions.TryGetValue(request.InteractionId, out PendingInteraction value) || value != pending)
            {
                throw new NxException("invalid_state", "This game interaction has expired.");
            }
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
            await runtime.RespondToInteractionAsync(interactionResponse2, linked.Token);
        }
    }

    public async Task PollTelemetryAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        NxRuntime runtime = _runtime;
        bool flag = runtime == null || StopIfTerminal(runtime) || Busy;
        if (!flag)
        {
            RuntimeState state = runtime.State;
            bool flag2 = (uint)(state - 3) <= 1u;
            flag = !flag2;
        }
        if (flag)
        {
            return;
        }
        try
        {
            TelemetrySnapshot telemetrySnapshot = await runtime.GetTelemetryAsync(cancellationToken);
            if (_runtime != runtime || _disposed)
            {
                return;
            }
            Telemetry = telemetrySnapshot;
            ObservePerformance(runtime, telemetrySnapshot);
            double? frameTimeMs = Telemetry.FrameTimeMs;
            if (frameTimeMs.HasValue)
            {
                double valueOrDefault = frameTimeMs.GetValueOrDefault();
                if (valueOrDefault > 0.0 && valueOrDefault < 60000.0)
                {
                    lock (_frameTimes)
                    {
                        if (_frameTimes.Count == 3600)
                        {
                            _frameTimes.RemoveAt(0);
                        }
                        _frameTimes.Add(Telemetry.FrameTimeMs.Value);
                    }
                }
            }
            Notify();
        }
        catch (NxException ex)
        {
            if (!StopIfTerminal(runtime))
            {
                Log("telemetry.failed", new
                {
                    code = SafeCode(ex.Code)
                });
            }
        }
    }

    public async Task ForwardInputAsync(InputSnapshot snapshot)
    {
        NxRuntime runtime = _runtime;
        bool flag = snapshot.Buttons.Length == 0 && snapshot.LeftX == 0.0 && snapshot.LeftY == 0.0 && snapshot.RightX == 0.0 && snapshot.RightY == 0.0;
        if (runtime == null || StopIfTerminal(runtime) || !GuestOwnsInput || (Busy && !flag) || runtime.State != RuntimeState.Running)
        {
            return;
        }
        try
        {
            await runtime.SubmitInputAsync(snapshot);
        }
        catch (NxException ex)
        {
            if (!StopIfTerminal(runtime) && ex.Code != "invalid_state")
            {
                Log("input.failed", new
                {
                    code = SafeCode(ex.Code)
                });
            }
        }
    }

    public Task ExportDiagnosticsAsync(string destination)
    {
        return RunAsync("Exporting diagnostics", async (CancellationToken ct) =>
        {
            string executableSha256 = await InfinityVersion.ExecutableSha256Async();
            RetainedWorkerDiagnostic[] retainedWorkerFailures = await ReadRetainedWorkerDiagnosticsAsync(ct);
            string report = _lastReport;
            string liveReportError = null;
            NxRuntime runtime = _runtime;
            if (runtime != null)
            {
                RuntimeState state = runtime.State;
                if (state != RuntimeState.Disposed && state != RuntimeState.Faulted)
                {
                    try
                    {
                        report = await runtime.CreateWorkerDiagnosticReportAsync(ct);
                    }
                    catch (NxException ex)
                    {
                        liveReportError = SafeCode(ex.Code);
                    }
                }
            }
            object[] events;
            lock (_events)
            {
                events = _events.ToArray();
            }
            OperationFailure[] failures;
            lock (_failures)
            {
                failures = _failures.ToArray();
            }
            object[] observations;
            lock (_observations)
            {
                observations = _observations.ToArray();
            }
            double[] frameTimeSamplesMs;
            lock (_frameTimes)
            {
                frameTimeSamplesMs = _frameTimes.ToArray();
            }
            var value = new
            {
                schemaVersion = 3,
                application = "NX Infinity",
                version = InfinityVersion.Application,
                sdkVersion = "0.3.2-dev",
                executableSha256 = executableSha256,
                applicationModuleId = typeof(InfinityVersion).Assembly.ManifestModule.ModuleVersionId,
                createdUtc = DateTimeOffset.UtcNow,
                qualification = "unqualified",
                windowsExecuted = OperatingSystem.IsWindows(),
                platform = RuntimeInformation.OSDescription,
                startupSeconds = StartupSeconds,
                launchPhases = LastLaunchPhases,
                frameTimeSamplesMs = frameTimeSamplesMs,
                frameTimeScope = "Backend telemetry samples, not a complete per-present frame-time capture",
                sdk = ((report == null) ? ((JsonElement?)null) : new JsonElement?(JsonDocument.Parse(report).RootElement.Clone())),
                events = events,
                sdkReportScope = "Most recent captured SDK report; observations and failures retain their own operation and backend context.",
                sdkReportCaptureStatus = ((report == null) ? "not_captured" : "captured"),
                liveReportError = liveReportError,
                selectedBackend = new
                {
                    backendId = SafeBackend(SelectedBackendId),
                    backendRevision = SafeRevision(SelectedBackend?.Revision)
                },
                lastFailure = LastFailure,
                failures = failures,
                observations = observations,
                retainedWorkerFailures = retainedWorkerFailures,
                retainedWorkerFailureScope = "Last private failure retained per core, possibly from an earlier session. Fixed exception, subsystem and recognized method names are exported. Arguments, source paths and raw log messages remain private; these observations do not establish a root cause.",
                performance = PerformanceDiagnosticPayload(),
                graphicsIssueObservations = GraphicsIssueObservations,
                gameWindow = GameWindow,
                lastGameExitReason = LastGameExitReason,
                sharedSetup = SharedSetup,
                sharedSetupInstallationIssues = SharedSetupInstallationIssues,
                libraryScan = LibraryScanDiagnosticPayload(),
                saveMigration = SaveMigrationDiagnosticPayload(),
                claims = new
                {
                    gameplayVerified = false,
                    officialUpgradeParity = false,
                    physicalControllerVerified = false,
                    physicalAudioVerified = false
                }
            };
            string path = Path.GetFullPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
            await using Stream stream = archive.CreateEntry("validation-report.json").Open();
            await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }, ct);
            LastReportPath = path;
            Status = "Diagnostic report exported";
        });
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await _gate.WaitAsync();
        try
        {
            if (!_disposed)
            {
                await StopInternalAsync();
                _disposed = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string OperationId(string status)
    {
        switch (status)
        {
            case "Checking game files":
                return "game.launch";
            case "Reading local content":
                return "content.register";
            case "Inspecting native content identity":
                return "content.inspect";
            case "Checking local core prerequisites":
                return "prerequisites.check";
            case "Importing user-supplied keys":
                return "prerequisites.import.keys";
            case "Importing user-supplied firmware":
                return "prerequisites.import.firmware";
            case "Setting up keys for NX Infinity Core":
                return "prerequisites.shared.keys";
            case "Setting up firmware for NX Infinity Core":
                return "prerequisites.shared.firmware";
            case "Applying shared setup to installed cores":
                return "prerequisites.shared.provision";
            case "Restoring previous shared setup":
                return "prerequisites.shared.rollback";
            case "Reading core settings":
                return "settings.read";
            case "Changing backend":
                return "backend.select";
            case "Saving this game's core selection":
                return "backend.select_for_title";
            case "Reading native update and DLC selection":
                return "content.configuration.get";
            case "Verifying selected update and DLC files":
                return "content.configuration.set";
            case "Reading native mod state":
                return "mods.native.get";
            case "Verifying exact-build mod bundle":
                return "mods.import";
            case "Verifying exact-build mod receipts":
                return "mods.inspect";
            case "Updating exact-build mod selection":
                return "mods.select";
            case "Restoring previous mod selection":
                return "mods.rollback";
            case "Discovering native save directories":
                return "saves.discover";
            case "Stopping and backing up the native save":
                return "saves.backup";
            case "Verifying saved backups":
                return "saves.verify";
            case "Restoring the exact native save backup":
                return "saves.restore";
            case "Importing the verified native save snapshot":
                return "saves.import";
            case "Restoring the save preserved before import":
                return "saves.rollback";
            case "Exporting verified save snapshot":
                return "saves.export";
            case "Reviewing enhancement profile manifest":
                return "enhancements.import";
            case "Applying exact-session enhancement transaction":
                return "enhancements.apply";
            case "Restoring recorded original settings":
                return "enhancements.rollback";
            case "Restoring core preferences for this game":
                return "settings.reset";
            case "Opening game menu":
                return "game.pause";
            case "Resuming game":
                return "game.resume";
            case "Stopping game safely":
                return "game.stop";
            case "Exporting diagnostics":
                return "diagnostics.export";
            case "Recovering closed core session":
                return "recovery.acknowledge";
            case "Updating gameplay input assignment":
                return "input.assign";
            case "Updating favorite":
                return "library.favorite";
            case "Removing library record":
                return "library.remove";
            default:
                if (status.StartsWith("Updating ", StringComparison.Ordinal))
                {
                    return "settings.set";
                }
                return "other";
        }
    }

    private void DiagnosticStage(string stage, BackendChoice? backend = null)
    {
        TrackLaunchStage(stage);
        _diagnosticStage = stage;
        if ((object)backend != null)
        {
            _diagnosticBackend = backend;
        }
    }

    private static string SafeCode(string? code)
    {
        if (code != null)
        {
            int length = code.Length;
            if (length > 0 && length <= 64 && Regex.IsMatch(code, "^[A-Za-z][A-Za-z0-9_.-]*$"))
            {
                return code;
            }
        }
        return "unclassified_error";
    }

    private static string? SafeBackend(string? id)
    {
        if (!(id == "nxi"))
        {
            return null;
        }
        return id;
    }

    private static string? SafeRevision(string? revision)
    {
        bool flag;
        if (revision != null)
        {
            int length = revision.Length;
            if (length == 40 || length == 64)
            {
                flag = true;
                goto IL_001a;
            }
        }
        flag = false;
        goto IL_001a;
    IL_001a:
        if (!flag || !revision.All(Uri.IsHexDigit))
        {
            return null;
        }
        return revision;
    }

    private void RecordFailure(string code, string localMessage)
    {
        ErrorCode = code;
        ErrorMessage = localMessage;
        LastFailureMessage = localMessage;
        var (reasonCode, recoveryAction) = ClassifyFailure(code, localMessage, _diagnosticOperation, _diagnosticStage);
        OperationFailure operationFailure = (LastFailure = new OperationFailure(DateTimeOffset.UtcNow, _operationSequence, _diagnosticOperation, _diagnosticStage, SafeBackend(_diagnosticBackend?.Id), SafeRevision(_diagnosticBackend?.Revision), SafeCode(code), reasonCode, recoveryAction));
        lock (_failures)
        {
            if (_failures.Count >= 32)
            {
                _failures.Dequeue();
            }
            _failures.Enqueue(operationFailure);
        }
        Log("operation.failed", operationFailure);
        try
        {
            string text = Path.Combine(Paths.DataRoot, "diagnostics");
            Directory.CreateDirectory(text);
            string text2 = Path.Combine(text, "last-error.json");
            string text3 = text2 + ".tmp";
            File.WriteAllText(text3, JsonSerializer.Serialize(new
            {
                version = InfinityVersion.Application,
                failure = operationFailure,
                localMessage = localMessage.Substring(0, Math.Min(localMessage.Length, 8192))
            }, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(text3, text2, overwrite: true);
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
        }
    }

    private static (string Reason, string Recovery) ClassifyFailure(string code, string message, string operation, string stage)
    {
        bool flag;
        switch (code)
        {
            case "identity_unknown":
                return (Reason: "native_identity_unavailable", Recovery: "inspect_native_identity");
            case "keys_missing":
            case "header_key_missing":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag)
        {
            return (Reason: code, Recovery: "import_local_keys");
        }
        if (IsNativeLoaderCode(code))
        {
            return (Reason: "native_content_rejected", Recovery: "review_native_content");
        }
        if (code == "missing_dependency")
        {
            if (message.Contains("header key", StringComparison.OrdinalIgnoreCase))
            {
                return (Reason: "header_key_missing", Recovery: "import_local_keys");
            }
            if (message.Contains("user-supplied keys", StringComparison.OrdinalIgnoreCase))
            {
                return (Reason: "keys_missing", Recovery: "import_local_keys");
            }
            if (message.Contains("OpenAL audio output", StringComparison.OrdinalIgnoreCase))
            {
                return (Reason: "audio_output_unavailable", Recovery: "check_audio_output");
            }
            if (message.Contains("SDL", StringComparison.OrdinalIgnoreCase) && message.Contains("video device", StringComparison.OrdinalIgnoreCase))
            {
                return (Reason: "video_device_unavailable", Recovery: "check_graphics_driver");
            }
            if (message.Contains("Vulkan surface", StringComparison.OrdinalIgnoreCase) || message.Contains("Vulkan instance extensions", StringComparison.OrdinalIgnoreCase))
            {
                return (Reason: "vulkan_surface_unavailable", Recovery: "check_graphics_driver");
            }
            if (stage == "backend.verify")
            {
                return (Reason: "backend_package_incomplete", Recovery: "restore_complete_package");
            }
            if (message.Contains("native", StringComparison.OrdinalIgnoreCase) && message.Contains("library", StringComparison.OrdinalIgnoreCase))
            {
                return (Reason: "native_library_unavailable", Recovery: "check_native_runtime");
            }
            return (Reason: "dependency_unspecified", Recovery: "check_prerequisites_and_diagnostics");
        }
        if (code == "unsupported" && operation == "prerequisites.import.firmware")
        {
            return (Reason: "firmware_import_unsupported", Recovery: "review_core_support");
        }
        if (code == "unsupported_platform")
        {
            return (Reason: "firmware_platform_unsupported", Recovery: "review_platform_support");
        }
        if (code == "unsupported" && message.Contains("Headless", StringComparison.OrdinalIgnoreCase))
        {
            return (Reason: "headless_graphics_unsupported", Recovery: "update_application");
        }
        switch (code)
        {
            case "unsupported":
                return (Reason: "operation_unsupported", Recovery: "review_core_support");
            case "settings_review_required":
                return (Reason: "settings_revision_changed", Recovery: "review_settings");
            case "unclean_shutdown":
            case "recovery_required":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag)
        {
            return (Reason: "recovery_required", Recovery: "review_recovery");
        }
        return (Reason: "operation_failed", Recovery: "review_local_error");
    }

    private static string RecoveryMessage(string action)
    {
        return action switch
        {
            "import_local_keys" => "Import your own local keys file once in Content. Shared setup provisions every supported installed core and retains per-core results. NX Infinity then reinspects the selected game with a fresh worker; content remains unverified until its native parser succeeds.",
            "review_native_content" => "The native parser rejected this content. Check this core's local prerequisites and verify your own game dump and selected update. The numeric native status is retained in Diagnostics; it does not by itself distinguish missing keys, wrong keys or damaged content.",
            "inspect_native_identity" => "Open Content and check this core's prerequisites, then choose Inspect again for the game. Title-specific routing and mods remain unavailable until the native loader supplies the required identity.",
            "check_audio_output" => "Check that Windows has an enabled audio output and that the selected device is available, then retry. This core reported no usable OpenAL output.",
            "check_graphics_driver" => "Check that the GPU driver provides Vulkan and that the app has access to a desktop display, then retry. The core reported a video or Vulkan surface failure.",
            "restore_complete_package" => "Extract the complete matching NX Infinity package into a new folder, keeping its cores and supporting files together.",
            "check_native_runtime" => "Check the package's Windows native-dependency guide and Microsoft Visual C++ v14 x64 Runtime. The report does not identify an individual missing DLL.",
            "check_prerequisites_and_diagnostics" => "Open Content and check local prerequisites, then export Diagnostics. The core did not identify which dependency is missing; do not infer keys, firmware, or a graphics driver from this code alone.",
            "review_core_support" => "Review this core's supported actions in Content. Unavailable operations require a supported integration; switching cores uses a separate content and save profile.",
            "review_platform_support" => "Switch 2/Ounce firmware requires platform functionality this Switch core does not provide. Supply firmware supported by the selected core; a similar version label does not establish platform compatibility.",
            "update_application" => "Use the current NX Infinity package. A desktop Vulkan session must not request structural headless mode.",
            "review_settings" => "Review and save this game's settings for the selected core revision before launching again.",
            "review_recovery" => "Use Last issue or Diagnostics > Review selected core recovery to review NX Infinity Core. NX preserves the marker and checks both profile locks. Interrupted save, firmware, mod or content transactions require separate recovery and are never bypassed. Reimporting keys or firmware does not clear a session marker.",
            _ => "Review the local error details below. The exported report retains the operation, stage, core and safe reason without copying private paths or raw messages.",
        };
    }

    private void RememberObservation(object observation)
    {
        lock (_observations)
        {
            if (_observations.Count >= 32)
            {
                _observations.Dequeue();
            }
            _observations.Enqueue(observation);
        }
    }

    private void RememberPrerequisites(PrerequisiteReport report, BackendChoice backend)
    {
        Prerequisites = report;
        ServicesBackendId = backend.Id;
        RememberObservation(new
        {
            utc = DateTimeOffset.UtcNow,
            operationSequence = _operationSequence,
            operationId = _diagnosticOperation,
            stage = _diagnosticStage,
            backendId = SafeBackend(backend.Id),
            backendRevision = SafeRevision(backend.Revision),
            kind = "prerequisites",
            reportCaptureStatus = "captured",
            keysPresent = report.KeysPresent,
            headerKeyUsable = report.HeaderKeyUsable,
            firmwarePresent = !string.IsNullOrEmpty(report.FirmwareVersion),
            firmwareValidationScope = report.FirmwareValidation?.Scope,
            firmwareArchiveCount = report.FirmwareValidation?.ArchiveCount,
            firmwareInventoryVerified = report.FirmwareValidation?.InstalledInventoryVerified,
            firmwareSignatureAuthenticated = report.FirmwareValidation?.SignatureAuthenticated,
            firmwareRetailQualified = report.FirmwareValidation?.RetailQualified,
            issueCodes = report.Issues.Select((ContentIssue i) => SafeCode(i.Code)).Distinct().ToArray()
        });
    }

    private static bool IsNativeLoaderCode(string? code)
    {
        ushort result;
        if (code != null && Regex.IsMatch(code, "^native_loader_[0-9]{1,5}$"))
        {
            return ushort.TryParse(code.AsSpan("native_loader_".Length), out result);
        }
        return false;
    }

    private void RememberContentInspection(ContentInfo report, BackendChoice backend)
    {
        bool flag = report.Issues.Any((ContentIssue issue) => issue.Severity == "error");
        string text;
        bool flag2;
        if (flag)
        {
            text = "rejected";
        }
        else
        {
            switch (report.NativeLoaderStatus)
            {
                case "metadata_parsed":
                case "metadata_unavailable":
                case "unrecognized":
                case "loaded":
                    flag2 = true;
                    break;
                default:
                    flag2 = false;
                    break;
            }
            text = (flag2 ? report.NativeLoaderStatus : "unclassified");
        }
        string nativeLoaderStatus = text;
        text = report.Format;
        string text2 = ((text != null && text.Length <= 20) ? report.Format.ToLowerInvariant() : null);
        switch (text2)
        {
            case "nro":
            case "nso":
            case "nsp":
            case "nca":
            case "xci":
            case "pfs0":
            case "exefs":
                flag2 = true;
                break;
            default:
                flag2 = false;
                break;
        }
        string format = (flag2 ? text2 : "unknown");
        switch (report.Kind)
        {
            case "data":
            case "game":
            case "application":
            case "homebrew":
            case "update":
            case "dlc":
            case "executable":
                flag2 = true;
                break;
            default:
                flag2 = false;
                break;
        }
        string contentKind = (flag2 ? report.Kind : "unknown");
        RememberObservation(new
        {
            utc = DateTimeOffset.UtcNow,
            operationSequence = _operationSequence,
            operationId = _diagnosticOperation,
            stage = _diagnosticStage,
            backendId = SafeBackend(backend.Id),
            backendRevision = SafeRevision(backend.Revision),
            kind = "content",
            reportCaptureStatus = "captured",
            format = format,
            contentKind = contentKind,
            nativeLoaderStatus = nativeLoaderStatus,
            rejected = flag,
            identityVerified = (!flag && report.IdentityVerified),
            titleIdAvailable = (!flag && KnownHex(report.TitleId, 16) != null),
            fullBuildIdAvailable = (!flag && KnownHex(report.BuildId, 64) != null),
            issueCodes = report.Issues.Select((ContentIssue issue) => SafeCode(issue.Code)).Distinct().Take(32)
                .ToArray(),
            nativeLoaderCodes = report.Issues.Select((ContentIssue issue) => issue.Code).Where(IsNativeLoaderCode).Distinct()
                .Take(16)
                .ToArray()
        });
    }

    private async Task RememberDisposedRuntimeAsync(NxRuntime runtime, RuntimeState stateBeforeDispose)
    {
        JsonElement? sdk = null;
        string captureError = null;
        try
        {
            if (runtime.State != RuntimeState.Disposed)
            {
                throw new NxException("invalid_state", "Runtime disposal did not complete.");
            }
            string text = await runtime.CreateWorkerDiagnosticReportAsync();
            using JsonDocument jsonDocument = JsonDocument.Parse(text);
            sdk = jsonDocument.RootElement.Clone();
            _lastReport = text;
        }
        catch (Exception ex)
        {
            captureError = SafeCode((ex is NxException ex2) ? ex2.Code : ex.GetType().Name);
        }
        LastShutdown = runtime.Shutdown;
        ShutdownReport? shutdown = runtime.Shutdown;
        if ((object)shutdown == null || !shutdown.Graceful)
        {
            try
            {
                string path = Path.Combine(Paths.DataRoot, "diagnostics");
                await runtime.SavePrivateWorkerDiagnosticAsync(Path.Combine(path, "last-worker-" + (SafeBackend(runtime.Backend.BackendId) ?? "unknown") + ".private.json"));
            }
            catch (Exception ex3)
            {
                Log("diagnostics.private_write_failed", new
                {
                    code = SafeCode((ex3 is NxException ex4) ? ex4.Code : ex3.GetType().Name)
                });
            }
        }
        RememberObservation(new
        {
            utc = DateTimeOffset.UtcNow,
            operationSequence = _operationSequence,
            operationId = _diagnosticOperation,
            stage = _diagnosticStage,
            backendId = SafeBackend(runtime.Backend.BackendId),
            backendRevision = SafeRevision(runtime.Backend.BackendRevision),
            kind = "runtime",
            stateBeforeDispose = stateBeforeDispose.ToString(),
            reportCaptureStatus = ((!sdk.HasValue) ? "failed" : "captured"),
            captureError = captureError,
            sdk = sdk
        });
    }

    private async Task DisposeInspectorAsync(NxRuntime inspector)
    {
        RuntimeState state = inspector.State;
        try
        {
            await inspector.DisposeAsync();
        }
        finally
        {
            await RememberDisposedRuntimeAsync(inspector, state);
        }
    }

    private async Task<RetainedWorkerDiagnostic[]> ReadRetainedWorkerDiagnosticsAsync(CancellationToken ct)
    {
        string[] array = new string[1] { "nxi" };
        List<RetainedWorkerDiagnostic> summaries = new List<RetainedWorkerDiagnostic>();
        string[] array2 = array;
        foreach (string backendId in array2)
        {
            List<RetainedWorkerDiagnostic> list = summaries;
            list.Add(await RetainedWorkerDiagnostic.ReadAsync(Paths.DataRoot, backendId, ct));
        }
        return summaries.ToArray();
    }

    public string? PrerequisiteImportLimitation(string kind)
    {
        BackendInfo backendInfo;
        if (ActiveBackend?.BackendId == SelectedBackendId)
        {
            backendInfo = ActiveBackend;
        }
        else
        {
            backendInfo = ((LastBackendInfo?.BackendId == SelectedBackendId) ? LastBackendInfo : null);
        }
        bool flag = kind == "firmware" && (object)backendInfo != null;
        if (flag)
        {
            string selectedBackendId = SelectedBackendId;
            bool flag2 = ((selectedBackendId == "eden" || selectedBackendId == "citron") ? true : false);
            flag = flag2;
        }
        if (!flag || backendInfo.Capabilities.Contains("prerequisites.import.firmware", StringComparer.Ordinal))
        {
            return null;
        }
        return "The selected " + (SelectedBackend?.Name ?? "core") + " adapter has no native firmware importer at this revision. Shared setup retains the local source and reports each installed core separately.";
    }

    public Task ScanGameFoldersAsync(IReadOnlyList<GameLibraryFolder>? folders = null, bool startup = false)
    {
        return RunAsync("Scanning game folders", async (CancellationToken ct) =>
        {
            LastFolderImport = null;
            IReadOnlyList<GameLibraryFolder> readOnlyList = folders ?? (from f in Library.Folders()
                                                                        where !startup || f.ScanAtStartup
                                                                        select f).ToArray();
            if (readOnlyList.Count != 0)
            {
                IsLibraryScanning = true;
                Notify();
                NxRuntime inspector = null;
                BackendChoice backend = SelectedBackend;
                ContentInfo inspected = null;
                Exception inspectionError = null;
                Exception connectionError = null;
                bool ownsInspector = false;
                bool canInspect = !HasGame && (object)backend != null;
                bool connectionAttempted = false;
                try
                {
                    Progress<LibraryFolderProgress> target = new Progress<LibraryFolderProgress>((LibraryFolderProgress p) =>
                    {
                        Status = ((p.Stage == "discovering") ? ("Finding games in " + p.FileName) : $"Scanning folders · {p.Processed}/{p.Discovered} files · {p.Added} added · {p.FileName}");
                        Progress = ((p.Stage == "discovering" || p.Discovered == 0) ? ((double?)null) : new double?(((double)p.Processed + ((p.TotalBytes > 0) ? ((double)p.BytesRead / (double)p.TotalBytes) : 0.0)) / (double)p.Discovered));
                        Notify();
                    });
                    LibraryFolderImportResult lastFolderImport = await Library.ImportFoldersAsync(readOnlyList, async (LocalContentCandidate candidate, CancellationToken token) =>
                    {
                        inspected = null;
                        inspectionError = connectionError;
                        if (canInspect && !connectionAttempted)
                        {
                            connectionAttempted = true;
                            try
                            {
                                NxRuntime runtime = _runtime;
                                bool flag;
                                if (runtime != null)
                                {
                                    RuntimeState state = runtime.State;
                                    if ((uint)state <= 1u)
                                    {
                                        flag = true;
                                        goto IL_007b;
                                    }
                                }
                                flag = false;
                                goto IL_007b;
                            IL_007b:
                                if (flag && runtime.Backend.BackendId == backend.Id)
                                {
                                    inspector = runtime;
                                }
                                else
                                {
                                    inspector = await ConnectAsync(backend, token);
                                    ownsInspector = true;
                                }
                            }
                            catch (Exception ex) when ((ex is NxException || ex is ServiceException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
                            {
                                inspectionError = ex;
                                connectionError = ex;
                            }
                        }
                        if (inspector != null)
                        {
                            try
                            {
                                inspected = await inspector.InspectContentForProfileAsync(candidate.AbsolutePath, Paths.DataRoot, CurrentProfileId, includeArtwork: true, token);
                            }
                            catch (Exception ex2) when ((ex2 is NxException || ex2 is ServiceException || ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
                            {
                                inspectionError = ex2;
                            }
                        }
                        LibraryContentKind kind = LibraryContentClassification.BeforeInspection(candidate.AbsolutePath);
                        return new ContentRegistrationRequest(candidate.AbsolutePath, kind);
                    }, (!canInspect) ? null : ((Func<GameLibraryEntry, CancellationToken, Task>)((GameLibraryEntry entry, CancellationToken _) =>
                    {
                        if (inspectionError != null)
                        {
                            throw inspectionError;
                        }
                        if ((object)inspected != null && (object)backend != null)
                        {
                            Observe(entry, inspected, backend, loaded: false);
                            LocalArtwork.TryStoreNative(Library, entry, inspected);
                        }
                        return Task.CompletedTask;
                    })), new ThrottledFolderProgress(target), ct, !startup);
                    LastFolderImport = lastFolderImport;
                    Status = FolderImportSummary(LastFolderImport);
                }
                finally
                {
                    if (ownsInspector && inspector != null)
                    {
                        await DisposeInspectorAsync(inspector);
                    }
                    IsLibraryScanning = false;
                    Notify();
                }
            }
        });
    }

    private object LibraryScanDiagnosticPayload()
    {
        try
        {
            IReadOnlyList<GameLibraryEntry> readOnlyList = Library.List(new LibraryQuery(null, FavoritesOnly: false, null, LibrarySort.Name, 0, 2000));
            IReadOnlyList<GameLibraryFolder> readOnlyList2 = Library.Folders();
            LibraryFolderImportResult lastFolderImport = LastFolderImport;
            return new
            {
                status = "captured",
                registeredFolders = readOnlyList2.Count,
                enabledStartupFolders = readOnlyList2.Count((GameLibraryFolder f) => f.ScanAtStartup),
                unavailableFolders = readOnlyList2.Count((GameLibraryFolder f) => !Directory.Exists(f.AbsolutePath)),
                indexedEntries = readOnlyList.Count,
                visibleInstalledGames = readOnlyList.Count(LibraryContentClassification.IsAvailableGame),
                missingIndexedGames = readOnlyList.Count((GameLibraryEntry e) => LibraryContentClassification.IsGame(e) && !File.Exists(e.Content.AbsolutePath)),
                unverifiedFiles = readOnlyList.Count((GameLibraryEntry e) => e.Kind == LibraryContentKind.Unidentified),
                indexedUpdates = readOnlyList.Count((GameLibraryEntry e) => e.Kind == LibraryContentKind.Update),
                indexedDlc = readOnlyList.Count((GameLibraryEntry e) => e.Kind == LibraryContentKind.DownloadableContent),
                scanObserved = ((object)lastFolderImport != null),
                scanCanceled = lastFolderImport?.Canceled,
                scanTruncated = lastFolderImport?.Truncated,
                discoveredScanEntries = lastFolderImport?.Items.Count,
                addedThisScan = lastFolderImport?.Added,
                unchangedThisScan = lastFolderImport?.Unchanged,
                failedThisScan = lastFolderImport?.Failed,
                scanIssueCodes = lastFolderImport?.Issues.GroupBy((LibraryDiscoveryIssue i) => i.Code).Select((IGrouping<string, LibraryDiscoveryIssue> g) => new
                {
                    code = SafeCode(g.Key),
                    count = g.Count()
                }).OrderBy(g => g.code)
                    .Take(64)
                    .ToArray()
            };
        }
        catch (Exception ex) when ((ex is ServiceException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
        {
            return new
            {
                status = "unavailable",
                code = "local_library_unavailable"
            };
        }
    }

    public static string FolderImportSummary(LibraryFolderImportResult result)
    {
        return $"{(result.Canceled ? "Scan canceled" : "Folder scan finished")} · {result.Added} added · {result.Unchanged} already in library · {result.Failed} need attention" + (result.Truncated ? " · scan limit reached; choose smaller subfolders" : "") + ((result.Issues.Count > 0) ? $" · {result.Issues.Count} folder/inspection issues" : "");
    }

    public int? GetGameFrameRateTarget(string entryId)
    {
        if (!_frameRatePreferences.Games.TryGetValue(entryId, out var value))
        {
            return null;
        }
        return value;
    }

    public int ResolveFrameRateTarget(string entryId)
    {
        return GetGameFrameRateTarget(entryId) ?? GlobalFrameRateTarget;
    }

    public Task SetGlobalFrameRateTargetAsync(int target)
    {
        return RunAsync("Saving global frame-rate preference", (CancellationToken _) =>
        {
            RequireFrameTarget(target);
            FrameRatePreferences frameRatePreferences = _frameRatePreferences with
            {
                GlobalTarget = target
            };
            FrameRatePreferenceStore.Write(frameRatePreferences);
            _frameRatePreferences = frameRatePreferences;
            Status = "Frame-rate preference saved for the next launch";
            return Task.CompletedTask;
        });
    }

    public Task SetGameFrameRateTargetAsync(string entryId, int? target)
    {
        return RunAsync("Saving game frame-rate preference", (CancellationToken _) =>
        {
            Library.Get(entryId);
            if (target.HasValue)
            {
                int valueOrDefault = target.GetValueOrDefault();
                RequireFrameTarget(valueOrDefault);
            }
            Dictionary<string, int> dictionary = new Dictionary<string, int>(_frameRatePreferences.Games, StringComparer.Ordinal);
            if (target.HasValue)
            {
                int valueOrDefault2 = target.GetValueOrDefault();
                dictionary[entryId] = valueOrDefault2;
            }
            else
            {
                dictionary.Remove(entryId);
            }
            FrameRatePreferences frameRatePreferences = _frameRatePreferences with
            {
                Games = dictionary
            };
            FrameRatePreferenceStore.Write(frameRatePreferences);
            _frameRatePreferences = frameRatePreferences;
            Status = "Frame-rate preference saved for the next launch";
            return Task.CompletedTask;
        });
    }

    private static void RequireFrameTarget(int target)
    {
        if ((target != 0 && target != 60) || 1 == 0)
        {
            throw new NxException("invalid_argument", "Choose Original or 60 FPS.");
        }
    }

    public FrameRateCompatibilityAssessment GetFrameRateCompatibility(GameLibraryEntry entry)
    {
        BackendChoice selectedBackend = SelectedBackend;
        LibraryCoreObservation libraryCoreObservation = (((object)selectedBackend == null) ? null : ObservationFor(entry, selectedBackend));
        string text = ((!(ActiveGame?.EntryId == entry.EntryId)) ? libraryCoreObservation?.TitleId : LoadedIdentity?.TitleId);
        string text2 = ((!(ActiveGame?.EntryId == entry.EntryId)) ? libraryCoreObservation?.BuildId : LoadedIdentity?.BuildId);
        string version = ((!(ActiveGame?.EntryId == entry.EntryId)) ? libraryCoreObservation?.DisplayVersion : LoadedIdentity?.DisplayVersion);
        TimingEnhancementRecipe timingEnhancementRecipe = TimingEnhancementCatalog.Select(text, text2, version);
        if ((object)timingEnhancementRecipe != null)
        {
            return timingEnhancementRecipe.Assessment(text2, version);
        }
        IReadOnlyList<FrameRateCompatibilityAssessment> readOnlyList = FrameRateCompatibilityCatalog.Query(KnownHex(text, 16), KnownHex(text2, 64));
        if (KnownHex(text, 16) != null && readOnlyList.Count != 0)
        {
            return (from item in readOnlyList
                    orderby item.CanApply descending, item.IdentityMatch == FrameRateIdentityMatch.ExactBuild descending, item.IdentityMatch == FrameRateIdentityMatch.SourcePrefixOnly descending, item.Entry.Mechanism == FrameRateMechanism.Native60Baseline descending
                    select item).First();
        }
        return new FrameRateCompatibilityAssessment(new FrameRateCompatibilityEntry
        {
            Id = "unknown",
            TitleId = (KnownHex(text, 16) ?? "unknown"),
            GameName = entry.DisplayName,
            Status = FrameRateSupportState.Unsupported,
            Mechanism = FrameRateMechanism.Unknown,
            Summary = "No qualified timing patch matches this loaded title, update and executable build. Original behavior is preserved.",
            ApplyBlockReason = "No qualified timing patch matches this loaded title, update and executable build. Original behavior is preserved.",
            ExpectedOriginalProvenance = "No exact executable original-byte evidence is available.",
            RequiredChecks = FrameRateCompatibilityCatalog.CommonValidationRequirements
        }, FrameRateIdentityMatch.Unknown, "No qualified timing patch matches this loaded title, update and executable build. Original behavior is preserved.");
    }

    private async Task ApplyFrameRatePolicyBeforeStartAsync(NxRuntime runtime, GameLibraryEntry entry, CancellationToken ct)
    {
        NativeFrameRatePolicy = null;
        FrameRateReceiptId = null;
        int requested = ((!OrbitEnabled) ? ResolveFrameRateTarget(entry.EntryId) : 0);
        if (!runtime.Supports("frame_rate.get") || !runtime.Supports("frame_rate.set"))
        {
            FrameRatePolicyExplanation = "This installed core does not report the native frame-rate policy. Its original behavior is preserved.";
            return;
        }
        NativeFrameRatePolicy = await runtime.SetFrameRatePolicyAsync(requested, ct);
        if (requested == 60)
        {
            TimingEnhancementRecipe recipe = ActiveTimingRecipe;
            if ((object)recipe != null)
            {
                if (!recipe.Matches(runtime.LoadedContent?.TitleId, runtime.LoadedContent?.BuildId, runtime.LoadedContent?.DisplayVersion))
                {
                    throw new NxException("timing_identity_changed", "The loaded game differs from the inspected timing recipe. Launch stopped before execution; inspect the selected update and retry.");
                }
                NativeMods = await runtime.GetNativeModsAsync(recipe.TitleId, ct);
                if (!NativeMods.Mounts.Any((NativeModMount m) => m.Enabled && m.Id == recipe.Id))
                {
                    throw new NxException("timing_mount_missing", "The selected timing enhancement is absent from the native mount readback.");
                }
                ExperimentalTimingModuleStatus = (NativeMods.LoaderObserved ? ("Experimental timing enhancement mounted; the native loader reported mod activity. Per-patch consumption and correct-speed gameplay still require verification. " + recipe.Caution) : ("Experimental timing enhancement configured; native application is not confirmed. " + recipe.Caution));
                FrameRatePolicyExplanation = ExperimentalTimingModuleStatus;
                Log("frame_rate.experimental_timing", new
                {
                    requested = requested,
                    LoaderObserved = NativeMods.LoaderObserved,
                    Id = recipe.Id,
                    fullBuildId = runtime.LoadedContent?.BuildId,
                    qualified = false
                });
                return;
            }
        }
        if (requested == 0)
        {
            FrameRatePolicyExplanation = "Original frame-rate behavior requested.";
            return;
        }
        ContentInfo identity = runtime.LoadedContent;
        FrameRateCompatibilityAssessment[] array = (from frameRateCompatibilityAssessment in FrameRateCompatibilityCatalog.Query(identity?.TitleId, identity?.BuildId)
                                                    where frameRateCompatibilityAssessment.CanApply && frameRateCompatibilityAssessment.Entry.DisplayVersion == identity?.DisplayVersion
                                                    select frameRateCompatibilityAssessment).ToArray();
        EnhancementCoordinator coordinator = Coordinator ?? throw new NxException("identity_unknown", "No loaded identity is available for timing repair.");
        List<(FrameRateCompatibilityAssessment, EnhancementPlan)> list = new List<(FrameRateCompatibilityAssessment, EnhancementPlan)>();
        FrameRateCompatibilityAssessment[] array2 = array;
        foreach (FrameRateCompatibilityAssessment candidate in array2)
        {
            foreach (EnhancementProfile item2 in from profile in Enhancements.List()
                                                 where profile.ProfileId == candidate.Entry.QualifiedPayloadId
                                                 select profile)
            {
                EnhancementPlan enhancementPlan = coordinator.Evaluate(new EnhancementSelection(item2.ProfileId, item2.Version));
                if (enhancementPlan.Applicable && string.Equals(enhancementPlan.ProfileSha256, candidate.Entry.PayloadSha256, StringComparison.OrdinalIgnoreCase) && enhancementPlan.Changes.Any((EnhancementChange change) =>
                {
                    GamePatchRecipe? gamePatch = change.GamePatch;
                    return (object)gamePatch != null && gamePatch.TargetFrameRate == 60;
                }) && enhancementPlan.Changes.Where((EnhancementChange change) => (object)change.GamePatch != null).All((EnhancementChange change) => change.GamePatch.TargetFrameRate == 60))
                {
                    list.Add((candidate, enhancementPlan));
                }
            }
        }
        if (list.Count != 1)
        {
            FrameRatePolicyExplanation = ((list.Count > 1) ? "Multiple timing repairs match. Original behavior is preserved until the conflicting catalog entries are resolved." : GetFrameRateCompatibility(entry).ApplyBlockReason);
            Log("frame_rate.original", new
            {
                requested = requested,
                reason = FrameRatePolicyExplanation,
                TitleId = identity?.TitleId,
                BuildId = identity?.BuildId,
                DisplayVersion = identity?.DisplayVersion
            });
            return;
        }
        EnhancementPlan item = list[0].Item2;
        EnhancementApplicationResult result = await coordinator.ApplyAsync(new EnhancementSelection(item.Profile.ProfileId, item.Profile.Version), ct);
        NativeFrameRatePolicy = await runtime.GetFrameRatePolicyAsync(ct);
        if (NativeFrameRatePolicy.EffectivePolicy != "patched_60" || NativeFrameRatePolicy.EffectiveTarget != 60 || !result.Receipt.NativePatches.Any((GamePatchAcknowledgement ack) => ack.TransactionId == NativeFrameRatePolicy.PatchTransactionId))
        {
            await coordinator.RollbackAsync(result.Receipt.ReceiptId, CancellationToken.None);
            throw new NxException("game_patch_acknowledgement_mismatch", "The native timing repair and frame policy did not agree; Original bytes were restored.");
        }
        FrameRateReceiptId = result.Receipt.ReceiptId;
        FrameRatePolicyExplanation = "The exact-build 60 FPS timing repair is active. Frame output and game-speed qualification remain separate observations.";
        Log("frame_rate.applied", new
        {
            requested,
            result.Receipt.ReceiptId,
            NativeFrameRatePolicy.PatchTransactionId
        });
    }

    public GpuPerformanceOptions? GetGameGpuPerformance(string entryId)
    {
        return _gpuPreferences.Games.GetValueOrDefault(entryId);
    }

    public GpuPerformanceOptions ResolveGpuPerformance(string entryId)
    {
        return GetGameGpuPerformance(entryId) ?? GlobalGpuPerformance;
    }

    public Task SetGpuPerformanceAsync(GpuPerformanceOptions? options, string? entryId = null)
    {
        return RunAsync("Saving GPU performance mode", (CancellationToken _) =>
        {
            if ((entryId == null && (object)options == null) || ((object)options != null && !GpuPerformanceOptions.IsValid(options)))
            {
                throw new NxException("invalid_argument", "Choose a supported GPU performance mode.");
            }
            if (entryId != null)
            {
                Library.Get(entryId);
            }
            Dictionary<string, GpuPerformanceOptions> dictionary = new Dictionary<string, GpuPerformanceOptions>(_gpuPreferences.Games, StringComparer.Ordinal);
            if (entryId != null)
            {
                if ((object)options == null)
                {
                    dictionary.Remove(entryId);
                }
                else
                {
                    dictionary[entryId] = options;
                }
            }
            GpuPerformancePreferences gpuPerformancePreferences = _gpuPreferences with
            {
                Global = ((entryId == null) ? options : _gpuPreferences.Global),
                Games = dictionary
            };
            GpuPreferenceStore.Write(gpuPerformancePreferences);
            _gpuPreferences = gpuPerformancePreferences;
            _runtimeInitializationPending = true;
            Status = "GPU performance mode saved for the next launch";
            return Task.CompletedTask;
        });
    }

    public static IReadOnlyDictionary<string, JsonElement> BuildLaunchGraphicsSettings(string? titleId, GpuPerformanceOptions options, IReadOnlyDictionary<string, JsonElement>? gameSettings)
    {
        Dictionary<string, JsonElement> dictionary = new Dictionary<string, JsonElement>();
        if (titleId == "010019A01E2F2000" && options.Mode == "default")
        {
            dictionary["graphics.resolution_scale"] = JsonSerializer.SerializeToElement(1.5);
            dictionary["graphics.max_anisotropy"] = JsonSerializer.SerializeToElement(16);
        }
        foreach (KeyValuePair<string, JsonElement> item in options.Settings())
        {
            dictionary[item.Key] = item.Value;
        }
        if (gameSettings != null)
        {
            foreach (KeyValuePair<string, JsonElement> gameSetting in gameSettings)
            {
                dictionary[gameSetting.Key] = gameSetting.Value.Clone();
            }
        }
        return dictionary;
    }

    private async Task ApplyLaunchGraphicsAsync(NxRuntime runtime, string entryId, string? titleId, IReadOnlyDictionary<string, JsonElement>? gameSettings, CancellationToken ct)
    {
        AppliedGpuPerformance = null;
        GpuPerformanceOptions options = ResolveGpuPerformance(entryId);
        IReadOnlyDictionary<string, JsonElement> requests = BuildLaunchGraphicsSettings(titleId, options, gameSettings);
        Dictionary<string, SettingSchema> dictionary = (await runtime.GetSettingSchemasAsync(ct)).ToDictionary((SettingSchema schema) => schema.Id);
        foreach (KeyValuePair<string, JsonElement> item in requests)
        {
            if (!dictionary.TryGetValue(item.Key, out var value) || !value.Supported)
            {
                throw new NxException("unsupported", "The installed core cannot apply " + item.Key + ".");
            }
            value.Validate(item.Value);
        }
        foreach (KeyValuePair<string, JsonElement> pair in requests)
        {
            SettingState settingState = await runtime.SetSettingAsync(pair.Key, pair.Value, persist: false, ct);
            if (settingState.Applied && settingState.EffectiveKnown && !settingState.RequiresRestart)
            {
                JsonElement? effective = settingState.Effective;
                if (effective.HasValue)
                {
                    JsonElement valueOrDefault = effective.GetValueOrDefault();
                    if (SameGpuSetting(valueOrDefault, pair.Value))
                    {
                        continue;
                    }
                }
            }
            throw new NxException("setting_not_applied", "The core did not confirm " + pair.Key + ". Launch stopped before starting the game.");
        }
        AppliedGpuPerformance = options.Label + " · " + requests.Count + " launch settings confirmed";
    }

    private static bool SameGpuSetting(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != JsonValueKind.Number || right.ValueKind != JsonValueKind.Number)
        {
            return JsonElement.DeepEquals(left, right);
        }
        return left.GetDouble() == right.GetDouble();
    }

    public Task DiscoverIndexedSaveTargetsAsync(string backendId)
    {
        return RunAsync("Reviewing indexed save destinations", async (CancellationToken ct) =>
        {
            IndexedSaveTargets = Array.Empty<SaveView>();
            await LoadSaveMigrationStateAsync(ct);
            BackendChoice backend = Backends.FirstOrDefault((BackendChoice item) => item.Id == backendId) ?? throw new NxException("not_found", "That core is no longer installed.");
            if (!SaveMigrationStore.SupportsIndexedImport(backend.Id))
            {
                throw new NxException("unsupported", "NX Infinity Core is required for indexed save import.");
            }
            if (IsProtectedSaveRuntime(backend.Id))
            {
                throw new NxException("data_in_use", "Save and close this core's game before importing saves.");
            }
            await ReleaseIdleSaveInitializerAsync(backend.Id);
            BackendLaunch launch = await Task.Run(() => backend.VerifyAsync(Paths, ct), ct);
            await Task.Run(() => ProvisionSharedBeforeConnectionAsync(launch, ct), ct);
            NxRuntime inspector = await NxRuntime.ConnectAsync(launch, ct);
            NativeSaveCatalog catalog;
            try
            {
                if (!inspector.Supports("saves.list") || !inspector.Supports("window.set"))
                {
                    throw new NxException("unsupported", "This installed core lacks indexed save discovery.");
                }
                await inspector.InitializeAsync(new RuntimeOptions
                {
                    DataRoot = Paths.DataRoot,
                    Headless = false,
                    Width = 320,
                    Height = 240,
                    WindowTitle = "NX indexed save discovery"
                }, ct);
                if ((await inspector.SetRenderWindowAsync(new RenderWindowChange
                {
                    Visible = false
                }, ct)).Visible)
                {
                    throw new NxException("unsupported", "The save observation window could not remain hidden.");
                }
                catalog = await inspector.DiscoverNativeSavesAsync(null, ct);
            }
            finally
            {
                await DisposeInspectorAsync(inspector);
            }
            ShutdownReport? shutdown = inspector.Shutdown;
            if ((object)shutdown == null || !shutdown.Graceful)
            {
                throw new NxException("core_error", "The native save observer did not close cleanly.");
            }
            List<SaveView> list = new List<SaveView>();
            foreach (NativeSaveDescriptor item in catalog.Entries.Where((NativeSaveDescriptor entry) =>
            {
                bool flag = entry.Existing && entry.AdapterVerified && entry.FormatId == "ryujinx-directory-save" && entry.FormatVersion == "1";
                if (flag)
                {
                    string type = entry.Type;
                    bool flag2 = ((type == "account" || type == "device") ? true : false);
                    flag = flag2;
                }
                return flag;
            }))
            {
                SaveDirectoryMapping mapping = NativeSaveMappings.FromNativeObservation(new NativeSaveObservation(item.BackendId, backend.Revision, item.TitleId, item.UserId, item.SaveDataId, item.Type, item.FormatId, item.FormatVersion, item.RelativePath, Existing: true, AdapterVerified: true, item.Provenance), backend.Id, backend.Revision);
                list.Add(new SaveView(item, mapping, backend.Revision));
            }
            IndexedSaveTargets = list;
            Status = list.Count + " indexed save destination(s) ready for explicit review";
        });
    }

    public async Task<IndexedSaveImportReview?> ReviewIndexedSaveAsync(SaveView view)
    {
        IndexedSaveImportReview result = null;
        await RunAsync("Verifying the external indexed save", async (CancellationToken ct) =>
        {
            await LoadSaveMigrationStateAsync(ct);
            BackendChoice backend = Backends.FirstOrDefault((BackendChoice item) => item.Id == view.Native.BackendId && item.Revision == view.BackendRevision) ?? throw new NxException("version_mismatch", "The installed core changed; discover its saves again.");
            if (IsProtectedSaveRuntime(backend.Id))
            {
                throw new NxException("data_in_use", "Close this core's game before reviewing its save.");
            }
            await ReleaseIdleSaveInitializerAsync(backend.Id);
            BackendLaunch launch = await Task.Run(() => backend.VerifyAsync(Paths, ct), ct);
            NativeSaveDescriptor native = view.Native;
            SaveMigrationTarget target = new SaveMigrationTarget
            {
                Launch = launch,
                Observation = new NativeSaveObservation(native.BackendId, backend.Revision, native.TitleId, native.UserId, native.SaveDataId, native.Type, native.FormatId, native.FormatVersion, native.RelativePath, native.Existing, native.AdapterVerified, native.Provenance)
            };
            SaveMigrationDiscoveryOptions options = new SaveMigrationDiscoveryOptions
            {
                AdditionalLocations = _saveMigrationPreference.PortableLocations.Select((SavedPortableLocation location) => new SaveMigrationLocation(location.Origin, location.RootPath)).ToArray()
            };
            result = await Task.Run(() => SaveMigrations.ReviewIndexedImportAsync(target, options, ct), ct);
            Status = "Source verified. Confirm the reviewed save before replacing any destination.";
        });
        return result;
    }

    public Task ApplyIndexedSaveAsync(IndexedSaveImportReview review)
    {
        return RunAsync("Importing the reviewed indexed save", async (CancellationToken ct) =>
        {
            if (IsProtectedSaveRuntime(review.BackendId))
            {
                throw new NxException("data_in_use", "The game is using this save. Close it and review again.");
            }
            await ReleaseIdleSaveInitializerAsync(review.BackendId);
            await Task.Run(() => SaveMigrations.ApplyIndexedImportAsync(review.ReviewId, acknowledged: true, ct), ct);
            SaveMigrationStatus saveMigrationState = await Task.Run((Func<SaveMigrationStatus>)SaveMigrations.Read, ct);
            SaveMigrationState = saveMigrationState;
            IndexedSaveTargets = Array.Empty<SaveView>();
            Status = "Save snapshot imported; original source and destination backup preserved. Launch the game to verify it.";
        });
    }

    public Task DiscardIndexedSaveAsync(string reviewId)
    {
        return Task.Run(() =>
        {
            SaveMigrations.DiscardIndexedImport(reviewId);
        });
    }

    public async Task StopForInstallationRepairAsync()
    {
        if (HasGame)
        {
            throw new InvalidOperationException("Save and stop the game before repairing its installation.");
        }
        await StopInternalAsync();
        ShutdownReport lastShutdown = LastShutdown;
        if ((object)lastShutdown != null && !lastShutdown.Graceful)
        {
            throw new InvalidOperationException("The core did not stop cleanly. Complete session recovery before replacing binaries.");
        }
    }

    private void BeginLaunchTiming()
    {
        _launchPhases.Clear();
        _launchPhase = "requested";
        _launchPhaseStart = Stopwatch.GetTimestamp();
    }

    private void TrackLaunchStage(string stage)
    {
        if (IsLaunching && _launchPhaseStart != 0L)
        {
            if (_launchPhases.Count < 128)
            {
                _launchPhases.Add(new LaunchPhaseTiming(_launchPhase, Stopwatch.GetElapsedTime(_launchPhaseStart).TotalMilliseconds));
            }
            _launchPhase = stage;
            _launchPhaseStart = Stopwatch.GetTimestamp();
        }
    }

    private void FinishLaunchTiming()
    {
        TrackLaunchStage("complete");
        LastLaunchPhases = _launchPhases.ToArray();
        _launchPhaseStart = 0L;
    }

    public Task SetOrbitServerAsync(string server)
    {
        return RunAsync("Updating the community server", async (CancellationToken _) =>
        {
            if (HasGame)
            {
                throw new NxException("invalid_state", "Stop the game before changing online settings.");
            }
            LdnInvitation.PublicOptions(server, new LdnCompatibility("nxi", new string('1', 40), new string('1', 16), new string('1', 64), "validation", new string('1', 64)));
            if (_runtime != null)
            {
                await StopInternalAsync();
            }
            InvalidateOrbitPreparation("Community server changed. Prepare the selected game again before launching.");
            OrbitServer = server.ToLowerInvariant();
            OrbitConnectMilliseconds = null;
        });
    }

    private void InvalidateOrbitPreparation(string message)
    {
        if (OrbitEnabled)
        {
            _orbitNetwork = null;
            _orbitCompatibility = null;
            OrbitInviteCode = null;
            OrbitGameEntryId = null;
            OrbitState = null;
            OrbitConnectMilliseconds = null;
            OrbitMessage = message;
        }
    }

    private void RequireOfflineSaveTools()
    {
        if (OrbitEnabled)
        {
            throw new NxException("online_profile_active", "Stop the game and choose Go offline in Orbit before using offline save tools.");
        }
    }

    public Task DisableOrbitAsync()
    {
        return RunAsync("Returning to offline play", async (CancellationToken ct) =>
        {
            if (HasGame)
            {
                throw new NxException("invalid_state", "Stop the current game before returning to offline play.");
            }
            if (_runtime != null)
            {
                await StopInternalAsync();
            }
            OrbitEnabled = false;
            _orbitNetwork = null;
            _orbitCompatibility = null;
            OrbitInviteCode = null;
            OrbitGameEntryId = null;
            OrbitState = null;
            OrbitConnectMilliseconds = null;
            ClearServiceViews();
            OrbitMessage = "Offline. Your offline profile and saves are selected.";
        });
    }

    public Task PrepareOrbitAsync(string entryId, bool privateRoom, string? invitationCode = null)
    {
        return RunAsync("Preparing Local Wireless", async (CancellationToken ct) =>
        {
            if (HasGame)
            {
                throw new NxException("invalid_state", "Stop the current game before preparing an online session.");
            }
            LdnInvitation invitation = ((invitationCode == null) ? null : LdnInvitation.Decode(invitationCode.Trim(), DateTimeOffset.UtcNow));
            BackendChoice backend = RequireBackend();
            if (!(backend.Id == "nxi"))
            {
                throw new NxException("unsupported", "Orbit Local Wireless requires NX Infinity Core.");
            }
            if ((object)invitation != null && (invitation.Compatibility.BackendId != backend.Id || invitation.Compatibility.BackendRevision != backend.Revision))
            {
                throw new NxException("network_core_mismatch", "The invitation uses a different core or build. Select the matching core before joining.");
            }
            if (_runtime != null)
            {
                await StopInternalAsync();
            }
            OrbitEnabled = true;
            _orbitNetwork = null;
            _orbitCompatibility = null;
            OrbitInviteCode = null;
            OrbitState = null;
            ClearServiceViews();
            try
            {
                LibraryLaunchTarget target = await Library.ResolveForLaunchAsync(entryId, null, ct);
                ContentInfo identity = await InspectAsync(target.AbsolutePath, ct);
                string title = KnownHex(identity.TitleId, 16) ?? throw new NxException("identity_unknown", "Inspect this game after configuring Orbit's separate local prerequisites.");
                if (identity.Issues.Any((ContentIssue issue) => issue.Severity == "error"))
                {
                    throw new NxException("invalid_content", "Resolve the game's native inspection errors before going online.");
                }
                NxRuntime inspector = await EnsureInitializedAsync(ct);
                GameContentConfiguration content = await inspector.GetContentConfigurationAsync(title, ct);
                identity = await inspector.InspectContentAsync(target.AbsolutePath, ct);
                if ((await inspector.GetNativeModsAsync(title, ct)).Mounts.Any((NativeModMount mount) => mount.Enabled))
                {
                    throw new NxException("network_mods_mismatch", "Disable native mods in the Orbit profile before preparing a session.");
                }
                string text = KnownHex(identity.BuildId, 64);
                if (text != null && (object)Mods.Current(new GameBuildIdentity(title, text)) != null && Mods.GetActiveMounts(new GameBuildIdentity(title, text)).Mounts.Count > 0)
                {
                    throw new NxException("network_mods_mismatch", "Disable gameplay mods before preparing an Orbit session.");
                }
                List<string> hashes = new List<string> { target.VerifiedSha256.ToUpperInvariant() };
                foreach (string item in ((content.UpdatePath == null) ? Array.Empty<string>() : new string[1] { content.UpdatePath }).Concat(content.DlcPaths))
                {
                    await using FileStream stream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    List<string> list = hashes;
                    list.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)));
                }
                string contentFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', hashes.Order(StringComparer.Ordinal)))));
                LdnCompatibility compatibility = new LdnCompatibility(backend.Id, backend.Revision.ToLowerInvariant(), title, KnownHex(identity.BuildId, 64) ?? "", identity.DisplayVersion ?? "", contentFingerprint);
                LdnInvitation.ValidateCompatibility(compatibility);
                if ((object)invitation != null && invitation.Compatibility != compatibility)
                {
                    throw new NxException("network_content_mismatch", "The invitation's game, update, DLC or executable differs from this Orbit profile. Match the host's Content selection and try again.");
                }
                await StopInternalAsync();
                OrbitServer = invitation?.Server ?? OrbitServer;
                await ProbeOrbitServerAsync(ct);
                LdnInvitation ldnInvitation = invitation ?? (privateRoom ? LdnInvitation.Create(OrbitServer, compatibility, DateTimeOffset.UtcNow) : null);
                _orbitNetwork = ldnInvitation?.Options() ?? LdnInvitation.PublicOptions(OrbitServer, compatibility);
                _orbitCompatibility = compatibility;
                OrbitInviteCode = ldnInvitation?.Encode();
                OrbitGameEntryId = entryId;
                Observe(target.Entry, identity, backend, loaded: false);
                OrbitMessage = "Ready for Local Wireless. Launch the game, then choose its Local Wireless mode. Orbit uses separate saves; game-session connectivity is not yet verified.";
            }
            catch
            {
                if (_runtime != null)
                {
                    await StopInternalAsync();
                }
                _orbitNetwork = null;
                _orbitCompatibility = null;
                OrbitGameEntryId = null;
                OrbitMessage = "Preparation failed. Orbit's separate profile remains selected so its setup can be reviewed in Content. Choose Go offline to return to offline saves.";
                throw;
            }
        });
    }

    private async Task ProbeOrbitServerAsync(CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5L));
        using TcpClient client = new TcpClient();
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(OrbitServer, 30456, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NxException("network_timeout", "The community server did not answer within five seconds. Try later or choose another operator's server.");
        }
        catch (SocketException)
        {
            throw new NxException("network_unavailable", "The community server could not be reached. Check the connection or choose another operator's server.");
        }
        OrbitConnectMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 1);
    }

    private void ValidateOrbitLaunch(string entryId, string backendId)
    {
        if (OrbitEnabled)
        {
            if ((object)_orbitNetwork == null || (object)_orbitCompatibility == null || OrbitGameEntryId != entryId)
            {
                throw new NxException("network_not_prepared", "Prepare this game in Orbit before launching, or choose Go offline.");
            }
            if (_orbitCompatibility.BackendId != backendId || _orbitCompatibility.BackendRevision != RequireBackend().Revision)
            {
                throw new NxException("network_core_mismatch", "This game's core selection differs from the prepared session. Match the selected core in Settings; no automatic switch was made.");
            }
        }
    }

    private void ValidateOrbitLoaded(ContentInfo identity)
    {
        if (OrbitEnabled)
        {
            LdnCompatibility orbitCompatibility = _orbitCompatibility;
            if ((object)orbitCompatibility == null || identity.TitleId?.ToUpperInvariant() != orbitCompatibility.TitleId || identity.BuildId?.ToUpperInvariant() != orbitCompatibility.BuildId || identity.DisplayVersion != orbitCompatibility.DisplayVersion)
            {
                throw new NxException("network_content_mismatch", "The loaded executable or update changed after Orbit preparation. Prepare the session again.");
            }
        }
    }

    private async Task ValidateOrbitContentAsync(NxRuntime runtime, GameLibraryEntry entry, CancellationToken ct)
    {
        if (!OrbitEnabled)
        {
            return;
        }
        LdnCompatibility expected = _orbitCompatibility;
        if ((object)expected == null)
        {
            return;
        }
        GameContentConfiguration content = await runtime.GetContentConfigurationAsync(expected.TitleId, ct);
        if ((await runtime.GetNativeModsAsync(expected.TitleId, ct)).Mounts.Any((NativeModMount mount) => mount.Enabled) || ((object)Mods.Current(new GameBuildIdentity(expected.TitleId, expected.BuildId)) != null && Mods.GetActiveMounts(new GameBuildIdentity(expected.TitleId, expected.BuildId)).Mounts.Count > 0))
        {
            throw new NxException("network_mods_mismatch", "Mods changed after preparing Orbit. Disable them and prepare the session again.");
        }
        List<string> hashes = new List<string> { entry.Content.Sha256.ToUpperInvariant() };
        foreach (string item in ((content.UpdatePath == null) ? Array.Empty<string>() : new string[1] { content.UpdatePath }).Concat(content.DlcPaths))
        {
            await using FileStream stream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
            List<string> list = hashes;
            list.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)));
        }
        if (!(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', hashes.Order(StringComparer.Ordinal))))) != expected.ContentFingerprint))
        {
            return;
        }
        throw new NxException("network_content_mismatch", "The selected game, update or DLC changed after preparing Orbit. Prepare the session again.");
    }

    public Task RefreshOrbitStateAsync()
    {
        return RunAsync("Reading Local Wireless state", async (CancellationToken ct) =>
        {
            if (_runtime != null && _runtime.Supports("network.get"))
            {
                OrbitState = await _runtime.GetLdnStateAsync(ct);
            }
            else
            {
                OrbitState = null;
            }
        });
    }

    private void BeginPerformanceCapture(NxRuntime runtime, string startedBy, TelemetrySnapshot? baseline = null)
    {
        lock (_performanceGate)
        {
            CompletePerformanceCaptureLocked("new_capture");
            _performanceCapture = new PerformanceCapture(runtime, 32768);
            if ((object)baseline != null)
            {
                _performanceCapture.StartFromNextInterval(runtime, baseline);
            }
            _performanceRuntime = runtime;
            _captureTitle = KnownHex(runtime.LoadedContent?.TitleId, 16);
            _captureBuild = KnownHex(runtime.LoadedContent?.BuildId, 64);
            string text = runtime.LoadedContent?.DisplayVersion;
            object captureVersion;
            if (text != null)
            {
                int length = text.Length;
                if (length > 0 && length <= 48 && text.All((char c) =>
                {
                    bool flag = char.IsAsciiDigit(c);
                    if (!flag)
                    {
                        bool flag2;
                        switch (c)
                        {
                            case '+':
                            case '-':
                            case '.':
                                flag2 = true;
                                break;
                            default:
                                flag2 = false;
                                break;
                        }
                        flag = flag2;
                    }
                    return flag;
                }))
                {
                    captureVersion = text;
                    goto IL_00d5;
                }
            }
            captureVersion = null;
            goto IL_00d5;
        IL_00d5:
            _captureVersion = (string?)captureVersion;
            _captureLaunchGeneration = LaunchGeneration;
            _captureStartedBy = startedBy;
            _captureGraphics = (((object)baseline == null) ? null : GraphicsDeviceTelemetry.Read(baseline));
            _captureShaders = (((object)baseline == null) ? null : ShaderCompilationTelemetry.Read(baseline));
            _captureFrameStart = (_captureFrameEnd = (((object)baseline == null) ? null : FrameRatePolicyTelemetry.Read(baseline)));
        }
    }

    private void CompletePerformanceCaptureLocked(string reason)
    {
        if (_performanceCapture != null)
        {
            _performanceHistory.Add(new InfinityPerformanceRun(_performanceCapture.Snapshot(), _captureTitle, _captureBuild, _captureLaunchGeneration, _captureStartedBy, reason)
            {
                GraphicsDevice = _captureGraphics,
                ShaderCompilation = _captureShaders,
                DisplayVersion = _captureVersion,
                FrameRateAtStart = _captureFrameStart,
                FrameRateAtEnd = _captureFrameEnd
            });
            if (_performanceHistory.Count > 10)
            {
                _performanceHistory.RemoveAt(0);
            }
            _performanceCapture = null;
            _performanceRuntime = null;
            _captureGraphics = null;
            _captureShaders = null;
            _captureFrameStart = (_captureFrameEnd = null);
        }
    }

    private void EndPerformanceRuntime(NxRuntime runtime)
    {
        lock (_performanceGate)
        {
            if (runtime == _performanceRuntime)
            {
                CompletePerformanceCaptureLocked("runtime_closed");
            }
        }
        if (runtime == _performancePlanRuntime)
        {
            PerformancePlan = null;
            _performancePlanRuntime = null;
        }
        if (runtime == _performanceReceiptRuntime)
        {
            _performanceReceiptRuntime = null;
        }
        PerformanceRendererSetting = null;
        _runtimeInitializationPending = false;
        GraphicsDevice = null;
        ShaderCompilation = null;
    }

    private void ObservePerformance(NxRuntime runtime, TelemetrySnapshot snapshot)
    {
        GraphicsDeviceResult graphicsDeviceResult = GraphicsDeviceTelemetry.Read(snapshot);
        ShaderCompilationResult shaderCompilationResult = ShaderCompilationTelemetry.Read(snapshot);
        NativeFrameRatePolicy = FrameRatePolicyTelemetry.Read(snapshot);
        lock (_performanceGate)
        {
            if (_performanceCapture != null && runtime == _performanceRuntime)
            {
                string text = _captureGraphics?.Snapshot?.DriverIdentityKey;
                if (text != null)
                {
                    string text2 = graphicsDeviceResult.Snapshot?.DriverIdentityKey;
                    if (text2 != null && text != text2)
                    {
                        CompletePerformanceCaptureLocked("graphics_device_changed");
                        BeginPerformanceCapture(runtime, "graphics_device_changed", snapshot);
                    }
                }
                if ((object)graphicsDeviceResult.Snapshot != null && (_captureGraphics?.Snapshot?.DriverIdentityKey == null || graphicsDeviceResult.Snapshot.DriverIdentityKey != null))
                {
                    _captureGraphics = graphicsDeviceResult;
                }
                if ((object)shaderCompilationResult.Snapshot != null)
                {
                    _captureShaders = shaderCompilationResult;
                }
                FrameRatePolicyState nativeFrameRatePolicy = NativeFrameRatePolicy;
                if ((object)nativeFrameRatePolicy != null)
                {
                    if ((object)_captureFrameStart == null)
                    {
                        _captureFrameStart = nativeFrameRatePolicy;
                    }
                    _captureFrameEnd = nativeFrameRatePolicy;
                }
                _performanceCapture.Observe(runtime, snapshot);
            }
        }
        GraphicsDevice = graphicsDeviceResult;
        ShaderCompilation = shaderCompilationResult;
    }

    private void ObserveShaderProgress(JsonElement data)
    {
        JsonElement jsonElement = ((data.ValueKind == JsonValueKind.Object && data.TryGetProperty("shaderCompilation", out var value)) ? value : data);
        ShaderCompilation = ShaderCompilationTelemetry.Read(new TelemetrySnapshot
        {
            BackendMetrics = new Dictionary<string, JsonElement> { ["shaderCompilation"] = jsonElement.Clone() }
        });
        ShaderCompilationSnapshot snapshot = ShaderCompilation.Snapshot;
        if ((object)snapshot == null || !Busy)
        {
            return;
        }
        if (!snapshot.CacheLoadReturned)
        {
            long? cacheTotal = snapshot.CacheTotal;
            if (cacheTotal.HasValue && cacheTotal.GetValueOrDefault() > 0)
            {
                cacheTotal = snapshot.CacheCompleted;
                if (cacheTotal.HasValue)
                {
                    long valueOrDefault = cacheTotal.GetValueOrDefault();
                    Progress = (double)valueOrDefault / (double)snapshot.CacheTotal.Value;
                    Status = $"Building shader cache · {valueOrDefault:N0} / {snapshot.CacheTotal.Value:N0}";
                    return;
                }
            }
        }
        if (!snapshot.CacheLoadReturned && snapshot.CacheStage == "prepare")
        {
            Progress = null;
            Status = "Preparing native shader cache";
        }
        else if (!snapshot.CacheLoadReturned && snapshot.CacheStage == "package")
        {
            Progress = null;
            Status = "Packaging native shader cache";
        }
        else if (snapshot.CacheLoadReturned)
        {
            Progress = null;
            Status = "Native shader cache load returned · starting game";
        }
    }

    public Task StartPerformanceCaptureAsync(bool resumeGame = false)
    {
        return RunAsync("Starting performance capture", async (CancellationToken ct) =>
        {
            NxRuntime runtime = _runtime ?? throw new NxException("invalid_state", "Launch a game before starting a frame-pacing capture.");
            bool flag = !HasGame;
            if (!flag)
            {
                RuntimeState state = runtime.State;
                bool flag2 = (uint)(state - 2) <= 2u;
                flag = !flag2;
            }
            if (flag)
            {
                throw new NxException("invalid_state", "A loaded game is required for a new capture.");
            }
            TelemetrySnapshot baseline = await runtime.GetTelemetryAsync(ct);
            BeginPerformanceCapture(runtime, "explicit_boundary", baseline);
            if (resumeGame)
            {
                _guestRequested = true;
                if (runtime.Supports("input.routing.set"))
                {
                    await runtime.SetInputTargetAsync(guest: true, ct);
                }
                if (runtime.State == RuntimeState.Paused)
                {
                    await runtime.ResumeAsync(ct);
                }
                else if (runtime.State == RuntimeState.Loaded)
                {
                    await runtime.StartAsync(ct);
                }
                if (runtime.Supports("window.set"))
                {
                    await ChangeGameWindowAsync(runtime, new RenderWindowChange
                    {
                        Visible = true
                    }, ct);
                }
                GuestOwnsInput = runtime.State == RuntimeState.Running && !_interactionOwnsInput;
            }
            Status = "Recording native presentation intervals";
            Log("performance.capture_started", new
            {
                backendId = SafeBackend(runtime.Backend.BackendId),
                revision = SafeRevision(runtime.Backend.BackendRevision)
            });
        });
    }

    public Task PreviewPerformanceAsync()
    {
        return RunAsync("Reviewing performance baseline", async (CancellationToken ct) =>
        {
            NxRuntime runtime = await EnsureInitializedAsync(ct);
            PerformancePlan = await runtime.PreviewPerformancePlanAsync(ct);
            _performancePlanRuntime = runtime;
            _performancePlanConsumed = false;
            if (runtime.Supports("initialize.renderer_threading"))
            {
                PerformanceRendererSetting = await runtime.GetSettingAsync("graphics.backend_threading", ct);
            }
            ServicesBackendId = runtime.Backend.BackendId;
            Status = ((PerformancePlan.ChangeCount == 0) ? "Supported baseline audited · no proposed changes" : "Performance changes are ready for review");
        });
    }

    public Task ApplyPerformanceBaselineAsync()
    {
        return RunAsync("Applying reviewed performance baseline", async (CancellationToken ct) =>
        {
            NxRuntime runtime = _runtime;
            if (runtime != null && runtime == _performancePlanRuntime)
            {
                PerformancePlanPreview plan = PerformancePlan;
                if ((object)plan != null)
                {
                    if (_performancePlanConsumed)
                    {
                        throw new NxException("performance_plan_stale", "Refresh the review before attempting another application.");
                    }
                    _performancePlanConsumed = true;
                    PerformanceReceipt = await runtime.ApplyPerformancePlanAsync(plan.PlanId, ct);
                    _performanceReceiptRuntime = runtime;
                    _performanceReceiptRequiresFreshLoad = plan.Entries.Any((PerformancePlanEntry row) =>
                    {
                        bool flag = row.Included;
                        if (flag)
                        {
                            string applyPhase = row.ApplyPhase;
                            bool flag2 = ((applyPhase == "before_load" || applyPhase == "restart") ? true : false);
                            flag = flag2;
                        }
                        return flag;
                    });
                    lock (_performanceGate)
                    {
                        CompletePerformanceCaptureLocked("settings_changed");
                    }
                    Status = "Reviewed settings applied and read back · saved preferences preserved";
                    Log("performance.plan_applied", new
                    {
                        backendId = SafeBackend(runtime.Backend.BackendId),
                        changeCount = PerformanceReceipt.Effective.Count,
                        scope = "runtime_only"
                    });
                    return;
                }
            }
            throw new NxException("performance_plan_stale", "Review the current runtime's performance plan first.");
        });
    }

    public Task RollbackPerformanceBaselineAsync()
    {
        return RunAsync("Restoring performance baseline originals", async (CancellationToken ct) =>
        {
            NxRuntime runtime = _runtime;
            if (runtime != null && runtime == _performanceReceiptRuntime)
            {
                PerformancePlanReceipt performanceReceipt = PerformanceReceipt;
                if ((object)performanceReceipt != null)
                {
                    PerformanceReceipt = await runtime.RollbackPerformancePlanAsync(performanceReceipt.ReceiptId, ct);
                    lock (_performanceGate)
                    {
                        CompletePerformanceCaptureLocked("settings_restored");
                    }
                    Status = "Original settings restored and read back";
                    Log("performance.plan_rolled_back", new
                    {
                        backendId = SafeBackend(runtime.Backend.BackendId),
                        restoredCount = PerformanceReceipt.Restored.Count
                    });
                    return;
                }
            }
            throw new NxException("performance_receipt_expired", "That runtime has closed. Its transient plan no longer applies; saved preferences remain available for the next launch.");
        });
    }

    public Task StageRendererThreadingAsync(string mode)
    {
        return RunAsync("Saving next-session renderer mode", async (CancellationToken ct) =>
        {
            string text = mode;
            if ((!(text == "single_gpu_thread") && !(text == "threaded_renderer")) || 1 == 0)
            {
                throw new NxException("invalid_argument", "Choose one of the core's explicit renderer modes.");
            }
            NxRuntime nxRuntime = await EnsureInitializedAsync(ct);
            if (!nxRuntime.Supports("initialize.renderer_threading"))
            {
                throw new NxException("unsupported", "This core does not expose renderer construction options.");
            }
            SettingState performanceRendererSetting = await nxRuntime.SetSettingAsync("graphics.backend_threading", mode, persist: true, ct);
            PerformanceRendererSetting = performanceRendererSetting;
            _runtimeInitializationPending = true;
            Status = "Renderer preference saved for the next launch · active renderer unchanged";
        });
    }

    private object PerformanceDiagnosticPayload()
    {
        return new
        {
            schemaVersion = 1,
            current = CurrentPerformance,
            recent = PerformanceHistory,
            shaderCompilation = ShaderCompilation,
            graphicsDevice = GraphicsDevice,
            frameRatePolicy = NativeFrameRatePolicy,
            captureLimit = 32768,
            recentCaptureLimit = 10,
            scope = "Bounded native presentation intervals with sequence coverage; no physical scanout, gameplay-speed or rendering-correctness qualification.",
            savedPreferencesModifiedByBaseline = false,
            experimentalProfilesAutomaticallyApplied = false
        };
    }

    public bool RecoveryRequiresStop(string backendId)
    {
        NxRuntime runtime = _runtime;
        bool flag = runtime != null && runtime.Backend.BackendId == backendId && !HasTerminalSession(runtime);
        if (flag)
        {
            RuntimeState state = runtime.State;
            bool flag2 = (uint)(state - 2) <= 2u;
            flag = flag2;
        }
        return flag;
    }

    private BackendChoice RecoveryBackend(string? backendId)
    {
        return Backends.FirstOrDefault((BackendChoice backend) => backend.Id == backendId) ?? throw new NxException("missing_dependency", "The affected core is not installed. Restore its complete package before recovery.");
    }

    public async Task<SessionRecoveryReview> ReviewSessionRecoveryAsync(string? backendId = null)
    {
        BackendChoice backend = RecoveryBackend(backendId ?? RecoveryBackendId ?? SelectedBackendId);
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                throw new NxException("invalid_state", "The launcher is closing.");
            }
            Busy = true;
            Notify();
            if (RecoveryRequiresStop(backend.Id))
            {
                throw new NxException("recovery_session_active", "Stop this core's game before reviewing recovery. Its active session is not bypassed.");
            }
            NxRuntime runtime = _runtime;
            if (runtime != null && runtime.Backend.BackendId == backend.Id)
            {
                string selected = SelectedBackendId;
                try
                {
                    await StopInternalAsync();
                }
                finally
                {
                    SelectedBackendId = selected;
                }
            }
            return await Task.Run(() => SessionRecovery.Inspect(Paths.DataRoot, backend.Id));
        }
        finally
        {
            Busy = false;
            _gate.Release();
            Notify();
        }
    }

    public Task RecoverSessionAsync(SessionRecoveryReview review)
    {
        return RunAsync("Recovering closed core session", (CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            BackendChoice backendChoice = RecoveryBackend(review.BackendId);
            if (!(review.ProfileId != "default"))
            {
                NxRuntime runtime = _runtime;
                if (runtime == null || !(runtime.Backend.BackendId == backendChoice.Id))
                {
                    DiagnosticStage("recovery.acknowledge", backendChoice);
                    SessionRecoveryReceipt sessionRecoveryReceipt = SessionRecovery.Acknowledge(Paths.DataRoot, review, acknowledged: true);
                    Log("recovery.acknowledged", new
                    {
                        backendId = sessionRecoveryReceipt.BackendId,
                        markerSha256 = sessionRecoveryReceipt.MarkerSha256
                    });
                    OperationFailure lastFailure = LastFailure;
                    if ((object)lastFailure != null && lastFailure.Code == "unclean_shutdown" && lastFailure.BackendId == backendChoice.Id)
                    {
                        LastFailure = null;
                        LastFailureMessage = null;
                        ErrorCode = null;
                        ErrorMessage = null;
                    }
                    Status = ((!HasGame) ? (backendChoice.Name + " recovered. Launch the game again.") : (State?.ToString() ?? "Session ready"));
                    return Task.CompletedTask;
                }
            }
            throw new NxException("recovery_changed", "This core acquired a session after review. Stop it and review recovery again.");
        });
    }

    private void InitializeSaveMigration()
    {
        SaveMigrations = new SaveMigrationStore(Workspace);
    }

    public Task LoadSaveMigrationPreferencesAsync()
    {
        return RunAsync("Reading saved preferences", LoadSaveMigrationStateAsync);
    }

    public Task StartSaveMigrationAsync()
    {
        return RunAsync("Checking saves from other emulators", (CancellationToken ct) => ScanSaveMigrationInternalAsync(requestImports: true, explicitImport: false, null, ct));
    }

    public Task RefreshSaveMigrationAsync()
    {
        return RunAsync("Scanning supported emulator saves", (CancellationToken ct) => ScanSaveMigrationInternalAsync(requestImports: false, explicitImport: false, null, ct));
    }

    public Task RetrySaveMigrationAsync()
    {
        return RunAsync("Importing matching saves into empty destinations", (CancellationToken ct) => ScanSaveMigrationInternalAsync(requestImports: true, explicitImport: true, null, ct));
    }

    public Task SetAutomaticSaveMigrationAsync(bool enabled)
    {
        return RunAsync("Saving automatic save import preference", async (CancellationToken ct) =>
        {
            await LoadSaveMigrationStateAsync(ct);
            await WriteSaveMigrationPreferenceAsync(_saveMigrationPreference with
            {
                AutomaticEnabled = enabled
            }, ct);
            if (enabled)
            {
                await ScanSaveMigrationInternalAsync(requestImports: true, explicitImport: false, null, ct);
            }
            else
            {
                foreach (BackendChoice backend in Backends)
                {
                    SetSaveMigrationCoreStatus(backend, "automatic_disabled");
                }
                Status = "Automatic save import is disabled; scanning and one-time import remain available";
            }
        });
    }

    public Task AddPortableSaveLocationAsync(string origin, string rootPath)
    {
        return RunAsync("Adding a portable emulator save location", async (CancellationToken ct) =>
        {
            await LoadSaveMigrationStateAsync(ct);
            if (!SaveOrigins.Contains(origin))
            {
                throw new NxException("unsupported", "Choose one of the supported emulator save layouts.");
            }
            string absolute = ValidatePortableSaveRoot(rootPath);
            StringComparison comparer = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            string text = Path.TrimEndingDirectorySeparator(Workspace.RootPath);
            if (absolute.Equals(text, comparer) || absolute.StartsWith(text + Path.DirectorySeparatorChar, comparer) || text.StartsWith(absolute + Path.DirectorySeparatorChar, comparer))
            {
                throw new NxException("invalid_argument", "Choose a separate emulator data directory, outside the NX workspace.");
            }
            if (_saveMigrationPreference.PortableLocations.Any((SavedPortableLocation location) => location.Origin == origin && location.RootPath.Equals(absolute, comparer)))
            {
                Status = "That portable emulator location is already selected";
            }
            else
            {
                if (_saveMigrationPreference.PortableLocations.Count >= 16)
                {
                    throw new NxException("invalid_argument", "Use at most 16 explicit portable emulator locations.");
                }
                await Task.Run(() =>
                {
                    RejectSaveMigrationLinks(absolute);
                    if (!Directory.Exists(absolute))
                    {
                        throw new NxException("invalid_argument", "Choose an existing local emulator data directory.");
                    }
                }, ct);
                SaveMigrationPreference preference = _saveMigrationPreference with
                {
                    PortableLocations = _saveMigrationPreference.PortableLocations
                        .Append(new SavedPortableLocation(Guid.NewGuid().ToString("N"), origin, absolute))
                        .ToArray()
                };
                await WriteSaveMigrationPreferenceAsync(preference, ct);
                await ScanSaveMigrationInternalAsync(requestImports: true, explicitImport: false, null, ct);
            }
        });
    }

    public Task RemovePortableSaveLocationAsync(string id)
    {
        return RunAsync("Removing a portable emulator save location", async (CancellationToken ct) =>
        {
            await LoadSaveMigrationStateAsync(ct);
            if (!_saveMigrationPreference.PortableLocations.Any((SavedPortableLocation location) => location.Id == id))
            {
                throw new NxException("not_found", "That portable location is not selected.");
            }
            await WriteSaveMigrationPreferenceAsync(_saveMigrationPreference with
            {
                PortableLocations = _saveMigrationPreference.PortableLocations.Where((SavedPortableLocation location) => location.Id != id).ToArray()
            }, ct);
            await ScanSaveMigrationInternalAsync(requestImports: false, explicitImport: false, null, ct);
        });
    }

    public Task RollbackSaveMigrationAsync(string receiptId)
    {
        return RunAsync("Rolling back an unchanged automatic save import", async (CancellationToken ct) =>
        {
            RequireOfflineSaveTools();
            await LoadSaveMigrationStateAsync(ct);
            SaveMigrationReceipt receipt = SaveMigrationState.Receipts.SingleOrDefault((SaveMigrationReceipt item) => item.ReceiptId == receiptId) ?? throw new NxException("not_found", "That automatic save migration receipt is unavailable.");
            BackendChoice backend = Backends.FirstOrDefault((BackendChoice item) => item.Id == receipt.BackendId);
            if (IsProtectedSaveRuntime(receipt.BackendId))
            {
                if ((object)backend != null)
                {
                    SetSaveMigrationCoreStatus(backend, "deferred_busy");
                }
                Status = "Save rollback is deferred while this core has a game loaded";
            }
            else
            {
                await ReleaseIdleSaveInitializerAsync(receipt.BackendId);
                DiagnosticStage("saves.migration.rollback", backend);
                SaveMigrationReport lastSaveMigrationReport = await Task.Run(() => SaveMigrations.RollbackAsync(receiptId, ct), ct);
                LastSaveMigrationReport = lastSaveMigrationReport;
                SaveMigrationStatus saveMigrationState = await Task.Run((Func<SaveMigrationStatus>)SaveMigrations.Read, ct);
                SaveMigrationState = saveMigrationState;
                if ((object)backend != null)
                {
                    SummarizeSaveMigrationCore(backend, LastSaveMigrationReport.Outcomes, 0);
                }
                Status = ((LastSaveMigrationReport.Outcomes.Single().Status == "rolled_back") ? "Automatic save import rolled back; the earlier empty destination was restored" : "Save rollback needs review; current data was preserved");
            }
        });
    }

    private async Task AfterNativeSaveDiscoveryAsync(NxRuntime runtime, IReadOnlyList<SaveView> views, CancellationToken ct)
    {
        await LoadSaveMigrationStateAsync(ct);
        if (!AutomaticSaveMigrationEnabled)
        {
            return;
        }
        BackendChoice backendChoice = Backends.FirstOrDefault((BackendChoice item) => item.Id == runtime.Backend.BackendId);
        if ((object)backendChoice != null)
        {
            if (IsProtectedSaveRuntime(backendChoice.Id))
            {
                SetSaveMigrationCoreStatus(backendChoice, "deferred_busy");
            }
            else if (views.Count != 0)
            {
                await ScanSaveMigrationInternalAsync(requestImports: true, explicitImport: false, backendChoice.Id, ct);
            }
            else
            {
                SetSaveMigrationCoreStatus(backendChoice, "target_allocation_unavailable");
            }
        }
    }

    private bool IsProtectedSaveRuntime(string backendId)
    {
        NxRuntime runtime = _runtime;
        bool flag = runtime != null && runtime.Backend.BackendId == backendId;
        if (flag)
        {
            bool flag2 = (object)ActiveGame != null;
            if (!flag2)
            {
                RuntimeState state = runtime.State;
                bool flag3 = (uint)(state - 2) <= 2u;
                flag2 = flag3;
            }
            flag = flag2;
        }
        return flag;
    }

    private async Task ReleaseIdleSaveInitializerAsync(string backendId)
    {
        NxRuntime runtime = _runtime;
        if (runtime != null && !(runtime.Backend.BackendId != backendId))
        {
            if (IsProtectedSaveRuntime(backendId))
            {
                throw new NxException("data_in_use", "The loaded game keeps ownership of its save profile.");
            }
            string selected = SelectedBackendId;
            await StopInternalAsync();
            if (selected != null && Backends.Any((BackendChoice item) => item.Id == selected))
            {
                SelectedBackendId = selected;
            }
            ShutdownReport? shutdown = runtime.Shutdown;
            if ((object)shutdown == null || !shutdown.Graceful)
            {
                throw new NxException("core_error", "The idle native save observer did not shut down cleanly.");
            }
        }
    }

    private async Task ScanSaveMigrationInternalAsync(bool requestImports, bool explicitImport, string? onlyBackend, CancellationToken ct)
    {
        RequireOfflineSaveTools();
        await LoadSaveMigrationStateAsync(ct);
        SaveMigrationDiscoveryOptions options = new SaveMigrationDiscoveryOptions
        {
            AdditionalLocations = _saveMigrationPreference.PortableLocations.Select((SavedPortableLocation location) => new SaveMigrationLocation(location.Origin, location.RootPath)).ToArray()
        };
        DiagnosticStage("saves.migration.discovery");
        SaveMigrationDiscovery = await Task.Run(() => SaveMigrations.DiscoverAsync(options, ct), ct);
        Notify();
        BackendChoice[] chosen = Backends.Where((BackendChoice backendChoice2) => onlyBackend == null || backendChoice2.Id == onlyBackend).ToArray();
        if (!requestImports || (!explicitImport && !AutomaticSaveMigrationEnabled))
        {
            BackendChoice[] array = chosen;
            foreach (BackendChoice backend in array)
            {
                SetSaveMigrationCoreStatus(backend, (!AutomaticSaveMigrationEnabled) ? "automatic_disabled" : "scanned");
            }
            Status = SaveMigrationDiscovery.Candidates.Count + " possible external save(s) found; native verification is still required";
            return;
        }
        LastSaveMigrationReport = null;
        if (SaveMigrationDiscovery.Candidates.Count == 0)
        {
            BackendChoice[] array = chosen;
            foreach (BackendChoice backendChoice in array)
            {
                SetSaveMigrationCoreStatus(backendChoice, SaveMigrationStore.SupportsAutomaticImport(backendChoice.Id) ? "no_sources" : "format_verification_unavailable");
            }
            Status = "No saves found in the supported known and selected portable locations";
            return;
        }
        List<SaveMigrationTarget> targets = new List<SaveMigrationTarget>();
        Dictionary<string, int> eligibleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        BackendChoice[] array2 = chosen;
        foreach (BackendChoice backend2 in array2)
        {
            ct.ThrowIfCancellationRequested();
            if (!SaveMigrationStore.SupportsAutomaticImport(backend2.Id))
            {
                SetSaveMigrationCoreStatus(backend2, "format_verification_unavailable");
                continue;
            }
            if (IsProtectedSaveRuntime(backend2.Id))
            {
                SetSaveMigrationCoreStatus(backend2, "deferred_busy");
                continue;
            }
            string profile = Path.Combine(Paths.DataRoot, "backends", backend2.Id, "default");
            try
            {
                if (!(await Task.Run(() =>
                {
                    RejectSaveMigrationLinks(profile);
                    return Directory.Exists(Path.Combine(profile, SaveMigrationStore.SupportsIndexedImport(backend2.Id) ? "bis/user/save" : "nand/user/save"));
                }, ct)))
                {
                    SetSaveMigrationCoreStatus(backend2, "target_allocation_unavailable");
                    continue;
                }
                await ReleaseIdleSaveInitializerAsync(backend2.Id);
                DiagnosticStage("saves.migration.backend_verify", backend2);
                BackendLaunch launch = await Task.Run(() => backend2.VerifyAsync(Paths, ct), ct);
                await Task.Run(() => ProvisionSharedBeforeConnectionAsync(launch, ct), ct);
                NativeSaveCatalog catalog = await ObserveSaveMigrationTargetsAsync(backend2, launch, ct);
                IReadOnlyList<SaveMigrationTarget> readOnlyList = await Task.Run(() => AutomaticSaveMigrationTargets(backend2, launch, catalog, profile), ct);
                if (readOnlyList.Count == 0)
                {
                    SetSaveMigrationCoreStatus(backend2, (catalog.Entries.Length == 0) ? "target_allocation_unavailable" : "target_conflict");
                    continue;
                }
                if (targets.Count + readOnlyList.Count > 64)
                {
                    SetSaveMigrationCoreStatus(backend2, "target_limit", readOnlyList.Count);
                    continue;
                }
                targets.AddRange(readOnlyList);
                eligibleCounts[backend2.Id] = readOnlyList.Count;
                SetSaveMigrationCoreStatus(backend2, "ready", readOnlyList.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex2) when ((ex2 is NxException || ex2 is ServiceException || ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
            {
                string text;
                if (ex2 is NxException ex3)
                {
                    text = ex3.Code;
                }
                else
                {
                    text = ((ex2 is ServiceException { Code: var code }) ? code.ToString() : "io_error");
                }
                if (text == "cancelled")
                {
                    throw new OperationCanceledException(ct);
                }
                BackendChoice backend3 = backend2;
                bool flag = ((text == "data_in_use" || text == "Busy") ? true : false);
                SetSaveMigrationCoreStatus(backend3, flag ? "deferred_busy" : "native_verification_unavailable");
                Log("saves.migration.target_unavailable", new
                {
                    backendId = backend2.Id,
                    code = SafeCode(text)
                });
            }
        }
        SaveMigrationReport operationReport = null;
        if (targets.Count != 0)
        {
            DiagnosticStage("saves.migration.import");
            int reporting = 1;
            Progress<SaveMigrationProgress> progress = new Progress<SaveMigrationProgress>((SaveMigrationProgress value) =>
            {
                if (Volatile.Read(in reporting) != 0)
                {
                    Progress = ((value.TotalTargets == 0) ? ((double?)null) : new double?((double)value.CompletedTargets / (double)value.TotalTargets));
                    string status2 = value.Stage switch
                    {
                        "copying_private_source" => "Copying a private save snapshot for native verification",
                        "native_source_verification" => "Verifying the source save with the exact native core",
                        "importing_empty_target" => "Importing a verified save into its empty destination",
                        _ => "Checking automatic save migration",
                    };
                    Status = status2;
                    Notify();
                }
            });
            try
            {
                operationReport = await Task.Run(() => SaveMigrations.AutoImportAsync(targets, options, progress, ct), ct);
            }
            finally
            {
                Volatile.Write(ref reporting, 0);
            }
            LastSaveMigrationReport = operationReport;
            SaveMigrationState = await Task.Run((Func<SaveMigrationStatus>)SaveMigrations.Read, ct);
            foreach (BackendChoice backend4 in chosen.Where((BackendChoice backendChoice2) => eligibleCounts.ContainsKey(backendChoice2.Id)))
            {
                SummarizeSaveMigrationCore(backend4, LastSaveMigrationReport.Outcomes.Where((SaveMigrationOutcome outcome) => outcome.BackendId == backend4.Id).ToArray(), eligibleCounts[backend4.Id]);
            }
        }
        else
        {
            SaveMigrationState = await Task.Run((Func<SaveMigrationStatus>)SaveMigrations.Read, ct);
        }
        InfinitySession infinitySession = this;
        string status;
        if (!(operationReport?.Cancelled ?? false))
        {
            int? num3 = operationReport?.Outcomes.Count((SaveMigrationOutcome outcome) => outcome.Imported);
            if (num3.HasValue)
            {
                int valueOrDefault = num3.GetValueOrDefault();
                if (valueOrDefault > 0)
                {
                    status = valueOrDefault + " save(s) imported; source snapshots and guarded rollback are available";
                    goto IL_0e9e;
                }
            }
            status = SaveMigrationDiscovery.Candidates.Count + " save(s) found; 0 imported. " + string.Join(" · ", SaveMigrationCoreStatuses.Select((SaveMigrationCoreStatus core) => core.BackendId + ": " + core.Message));
        }
        else
        {
            status = "Save migration was canceled; completed local transactions and current saves were retained";
        }
        goto IL_0e9e;
    IL_0e9e:
        infinitySession.Status = status;
    }

    private async Task<NativeSaveCatalog> ObserveSaveMigrationTargetsAsync(BackendChoice backend, BackendLaunch launch, CancellationToken ct)
    {
        NxRuntime inspector = await NxRuntime.ConnectAsync(launch, ct);
        NativeSaveCatalog catalog;
        try
        {
            string operation = (SaveMigrationStore.SupportsIndexedImport(backend.Id) ? "saves.list" : (backend.Id + ".saves.discover"));
            if (!inspector.Supports(operation) || !inspector.Supports("window.get") || !inspector.Supports("window.set"))
            {
                throw new NxException("unsupported", "This exact core does not support hidden native save observation.");
            }
            DiagnosticStage("saves.migration.target_initialize", backend);
            await inspector.InitializeAsync(new RuntimeOptions
            {
                DataRoot = Paths.DataRoot,
                Headless = false,
                Width = 320,
                Height = 240,
                WindowTitle = "NX save discovery"
            }, ct);
            if ((await inspector.SetRenderWindowAsync(new RenderWindowChange
            {
                Visible = false
            }, ct)).Visible)
            {
                throw new NxException("unsupported", "The native save observer could not keep its window hidden.");
            }
            DiagnosticStage("saves.migration.target_discovery", backend);
            catalog = await inspector.DiscoverNativeSavesAsync(null, ct);
        }
        finally
        {
            await DisposeInspectorAsync(inspector);
        }
        ShutdownReport? shutdown = inspector.Shutdown;
        if ((object)shutdown == null || !shutdown.Graceful)
        {
            throw new NxException("core_error", "The native save observer did not dispose cleanly.");
        }
        return catalog;
    }

    private static IReadOnlyList<SaveMigrationTarget> AutomaticSaveMigrationTargets(BackendChoice backend, BackendLaunch launch, NativeSaveCatalog catalog, string profile)
    {
        List<SaveMigrationTarget> list = new List<SaveMigrationTarget>();
        NativeSaveDescriptor[] entries = catalog.Entries;
        foreach (NativeSaveDescriptor nativeSaveDescriptor in entries)
        {
            bool flag = !nativeSaveDescriptor.Existing || !nativeSaveDescriptor.AdapterVerified;
            if (!flag)
            {
                string type = nativeSaveDescriptor.Type;
                bool flag2 = ((type == "account" || type == "device") ? true : false);
                flag = !flag2;
            }
            if (flag)
            {
                continue;
            }
            NativeSaveObservation observation = new NativeSaveObservation(nativeSaveDescriptor.BackendId, backend.Revision, nativeSaveDescriptor.TitleId, nativeSaveDescriptor.UserId, nativeSaveDescriptor.SaveDataId, nativeSaveDescriptor.Type, nativeSaveDescriptor.FormatId, nativeSaveDescriptor.FormatVersion, nativeSaveDescriptor.RelativePath, nativeSaveDescriptor.Existing, nativeSaveDescriptor.AdapterVerified, nativeSaveDescriptor.Provenance);
            SaveDirectoryMapping saveDirectoryMapping = NativeSaveMappings.FromNativeObservation(observation, backend.Id, backend.Revision);
            bool flag3 = SaveMigrationStore.SupportsIndexedImport(backend.Id);
            string text = (flag3 ? "ryujinx-directory-save" : (backend.Id + "-native"));
            string value = (flag3 ? "bis/user/save/" : "nand/user/save/");
            if (!(saveDirectoryMapping.Identity.FormatId != text) && !(saveDirectoryMapping.Identity.FormatVersion != "1") && saveDirectoryMapping.RelativeDirectory.StartsWith(value, StringComparison.Ordinal))
            {
                string fullPath = Path.GetFullPath(Path.Combine(profile, saveDirectoryMapping.RelativeDirectory));
                StringComparison comparisonType = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (!fullPath.StartsWith(Path.GetFullPath(profile) + Path.DirectorySeparatorChar, comparisonType))
                {
                    throw new NxException("invalid_argument", "The native save mapping escaped its isolated profile.");
                }
                RejectSaveMigrationLinks(fullPath);
                if (Directory.Exists(fullPath) && (flag3 || !Directory.EnumerateFileSystemEntries(fullPath).Any()))
                {
                    list.Add(new SaveMigrationTarget
                    {
                        Launch = launch,
                        Observation = observation
                    });
                }
            }
        }
        return list;
    }

    private void SummarizeSaveMigrationCore(BackendChoice backend, IReadOnlyList<SaveMigrationOutcome> outcomes, int eligible)
    {
        int imported = outcomes.Count((SaveMigrationOutcome outcome) => outcome.Imported);
        string status;
        if (outcomes.Count != 0)
        {
            if (outcomes.All((SaveMigrationOutcome outcome) => outcome.Status == "rolled_back"))
            {
                status = "rolled_back";
            }
            else
            {
                status = (outcomes.All((SaveMigrationOutcome outcome) =>
                {
                    string status2 = outcome.Status;
                    return (status2 == "imported" || status2 == "already_imported") ? true : false;
                }) ? "complete" : "review_required");
            }
        }
        else
        {
            status = "native_verification_unavailable";
        }
        SetSaveMigrationCoreStatus(backend, status, eligible, imported);
    }

    private void SetSaveMigrationCoreStatus(BackendChoice backend, string status, int eligible = 0, int imported = 0)
    {
        SaveMigrationCoreStatus element = new SaveMigrationCoreStatus(backend.Id, backend.Revision, status, eligible, imported, status switch
        {
            "automatic_disabled" => "Automatic save import is disabled. Scanning and an explicit one-time import remain available.",
            "scanned" => "External locations were scanned. Native target verification and import have not been requested.",
            "no_sources" => "No candidate saves were found in supported known or selected portable emulator locations.",
            "format_verification_unavailable" => "Automatic native save-container verification is unavailable for this core; no format or identifier conversion was guessed.",
            "deferred_busy" => "A game or another operation owns this core profile. Automatic migration is deferred without stopping it.",
            "target_allocation_unavailable" => "No existing native-verified empty destination is available. Automatic target allocation is unsupported.",
            "target_conflict" => "The observed destinations contain data or have no compatible empty mapping. Existing saves were preserved.",
            "target_limit" => "The bounded automatic target limit was exceeded. Review saves in a smaller explicit operation.",
            "native_verification_unavailable" => "This exact core could not complete native save verification. Review its installation, prerequisites and recovery state.",
            "ready" => "Existing destinations were observed by the exact native core; empty-save and source verification are pending.",
            "complete" => "Matching saves have migration receipts. Sources and backups were preserved; game-level reopening remains unverified.",
            "rolled_back" => "The unchanged automatic import was rolled back. Automatic setup preserves this decision.",
            _ => "Some saves need review. Current data and unsupported destinations were preserved.",
        }, DateTimeOffset.UtcNow);
        SaveMigrationCoreStatuses = (from item in SaveMigrationCoreStatuses.Where((SaveMigrationCoreStatus item) => item.BackendId != backend.Id).Append(element)
                                     orderby Array.FindIndex(Backends.ToArray(), (BackendChoice backendChoice) => backendChoice.Id == item.BackendId)
                                     select item).ToArray();
    }

    private async Task LoadSaveMigrationStateAsync(CancellationToken ct)
    {
        SaveMigrationReady = false;
        (SaveMigrationPreference, string, SaveMigrationStatus) tuple = await Task.Run(() =>
        {
            string text = Path.Combine(Paths.DataRoot, "frontend-save-migration.json");
            RejectSaveMigrationLinks(text);
            if (!File.Exists(text))
            {
                return ((SaveMigrationPreference Preference, string Hash, SaveMigrationStatus State))(Preference: new SaveMigrationPreference(1, AutomaticEnabled: true, Array.Empty<SavedPortableLocation>()), Hash: null, State: SaveMigrations.Read());
            }
            if (new FileInfo(text).Length > 262144)
            {
                throw new NxException("invalid_argument", "The saved migration preferences are oversized and were preserved for review.");
            }
            byte[] array = File.ReadAllBytes(text);
            using (JsonDocument jsonDocument = JsonDocument.Parse(array, new JsonDocumentOptions
            {
                MaxDepth = 8
            }))
            {
                ValidateSavePreferenceFields(jsonDocument.RootElement);
                bool flag = !jsonDocument.RootElement.TryGetProperty("automaticEnabled", out var value);
                if (!flag)
                {
                    JsonValueKind valueKind = value.ValueKind;
                    bool flag2 = valueKind - 5 <= JsonValueKind.Object;
                    flag = !flag2;
                }
                if (flag)
                {
                    throw new NxException("invalid_argument", "The saved automatic import preference is invalid and was preserved for review.");
                }
            }
            SaveMigrationPreference? saveMigrationPreference = JsonSerializer.Deserialize<SaveMigrationPreference>(array, SavePreferenceJson) ?? throw new NxException("invalid_argument", "The saved migration preferences are invalid and were preserved for review.");
            ValidateSaveMigrationPreference(saveMigrationPreference);
            return (Preference: saveMigrationPreference, Hash: Convert.ToHexStringLower(SHA256.HashData(array)), State: SaveMigrations.Read());
        }, ct);
        _saveMigrationPreference = tuple.Item1;
        _saveMigrationPreferenceHash = tuple.Item2;
        AutomaticSaveMigrationEnabled = tuple.Item1.AutomaticEnabled;
        PortableSaveLocations = tuple.Item1.PortableLocations.Select((SavedPortableLocation location) => new PortableSaveLocation(location.Id, location.Origin)).ToArray();
        SaveMigrationState = tuple.Item3;
        SaveMigrationReady = true;
    }

    private async Task WriteSaveMigrationPreferenceAsync(SaveMigrationPreference preference, CancellationToken ct)
    {
        ValidateSaveMigrationPreference(preference);
        string expected = _saveMigrationPreferenceHash;
        string saveMigrationPreferenceHash = await Task.Run(() =>
        {
            string text = Path.Combine(Paths.DataRoot, "frontend-save-migration.json");
            string path = text + ".lock";
            RejectSaveMigrationLinks(text);
            RejectSaveMigrationLinks(path);
            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if ((File.Exists(text) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(text))) : null) != expected)
                {
                    throw new NxException("data_in_use", "Migration preferences changed in another app session. Review them before saving again.");
                }
                byte[] array = JsonSerializer.SerializeToUtf8Bytes(preference, SavePreferenceJson);
                string text2 = text + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            File.SetUnixFileMode(text2, UnixFileMode.UserWrite | UnixFileMode.UserRead);
                        }
                        fileStream.Write(array);
                        fileStream.Flush(flushToDisk: true);
                    }
                    File.Move(text2, text, overwrite: true);
                }
                finally
                {
                    if (File.Exists(text2))
                    {
                        File.Delete(text2);
                    }
                }
                return Convert.ToHexStringLower(SHA256.HashData(array));
            }
        }, ct);
        _saveMigrationPreference = preference;
        _saveMigrationPreferenceHash = saveMigrationPreferenceHash;
        AutomaticSaveMigrationEnabled = preference.AutomaticEnabled;
        PortableSaveLocations = preference.PortableLocations.Select((SavedPortableLocation location) => new PortableSaveLocation(location.Id, location.Origin)).ToArray();
    }

    private static void ValidateSaveMigrationPreference(SaveMigrationPreference preference)
    {
        if (preference.SchemaVersion != 1 || preference.PortableLocations == null || preference.PortableLocations.Count > 16)
        {
            throw new NxException("invalid_argument", "Saved migration preferences are unsupported and were preserved for review.");
        }
        HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (SavedPortableLocation portableLocation in preference.PortableLocations)
        {
            if ((object)portableLocation == null || !Guid.TryParseExact(portableLocation.Id, "N", out var _) || !hashSet.Add(portableLocation.Id) || !SaveOrigins.Contains(portableLocation.Origin))
            {
                throw new NxException("invalid_argument", "Saved portable locations are invalid and were preserved for review.");
            }
            ValidatePortableSaveRoot(portableLocation.RootPath);
        }
    }

    private static void ValidateSavePreferenceFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            {
                foreach (JsonProperty item in value.EnumerateObject())
                {
                    if (!hashSet.Add(item.Name))
                    {
                        throw new NxException("invalid_argument", "Duplicate migration preferences were preserved for review.");
                    }
                    ValidateSavePreferenceFields(item.Value);
                }
                return;
            }
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item2 in value.EnumerateArray())
        {
            ValidateSavePreferenceFields(item2);
        }
    }

    private static string ValidatePortableSaveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path))
        {
            throw new NxException("invalid_argument", "Choose an absolute local emulator data directory.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void RejectSaveMigrationLinks(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string pathRoot = Path.GetPathRoot(fullPath);
        StringComparison comparisonType = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        string text = fullPath;
        while (text != null && !string.Equals(text, pathRoot, comparisonType))
        {
            if (new FileInfo(text).LinkTarget != null || ((File.Exists(text) || Directory.Exists(text)) && (File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0))
            {
                throw new NxException("invalid_argument", "Save migration does not follow symbolic links or reparse points.");
            }
            text = Path.GetDirectoryName(text);
        }
    }

    private object SaveMigrationDiagnosticPayload()
    {
        bool saveMigrationReady = SaveMigrationReady;
        bool? automaticEnabled = (SaveMigrationReady ? new bool?(AutomaticSaveMigrationEnabled) : ((bool?)null));
        int count = PortableSaveLocations.Count;
        SaveMigrationDiscoveryReport saveMigrationDiscovery = SaveMigrationDiscovery;
        var discovery = (((object)saveMigrationDiscovery != null) ? new
        {
            ObservedUtc = saveMigrationDiscovery.ObservedUtc,
            Scope = saveMigrationDiscovery.Scope,
            possibleSaveCount = saveMigrationDiscovery.Candidates.Count,
            locations = (from location in saveMigrationDiscovery.Locations.Take(64)
                         select new { location.Origin, location.Status, location.CandidateCount }).ToArray()
        } : null);
        var cores = (from core in SaveMigrationCoreStatuses.Take(64)
                     select new { core.BackendId, core.BackendRevision, core.Status, core.EligibleTargets, core.ImportedCount, core.ObservedUtc }).ToArray();
        SaveMigrationReport lastSaveMigrationReport = LastSaveMigrationReport;
        return new
        {
            ready = saveMigrationReady,
            automaticEnabled = automaticEnabled,
            portableLocationCount = count,
            discovery = discovery,
            cores = cores,
            lastAttempt = (((object)lastSaveMigrationReport != null) ? new
            {
                Cancelled = lastSaveMigrationReport.Cancelled,
                CompletedUtc = lastSaveMigrationReport.CompletedUtc,
                Scope = lastSaveMigrationReport.Scope,
                outcomes = (from outcome in lastSaveMigrationReport.Outcomes.Take(128)
                            select new { outcome.BackendId, outcome.TitleId, outcome.Status, outcome.ErrorCode, outcome.Imported, outcome.NativeSourceVerified }).ToArray()
            } : null),
            receipts = (from receipt in SaveMigrationState.Receipts.TakeLast(128)
                        select new { receipt.ReceiptId, receipt.BackendId, receipt.BackendRevision, receipt.TitleId, receipt.FormatId, receipt.FormatVersion, receipt.CreatedUtc, receipt.Status, receipt.CanRollback }).ToArray()
        };
    }

    public bool Supports(string command)
    {
        BackendInfo? obj = ActiveBackend ?? ((LastBackendInfo?.BackendId == SelectedBackendId) ? LastBackendInfo : null);
        if ((object)obj == null)
        {
            return false;
        }
        return obj.Capabilities.Contains(command, StringComparer.Ordinal);
    }

    public static LibraryCoreObservation? ObservationFor(GameLibraryEntry entry, BackendChoice backend)
    {
        return entry.Observations.LastOrDefault((LibraryCoreObservation o) => o.BackendId == backend.Id && string.Equals(o.BackendRevision, backend.Revision, StringComparison.OrdinalIgnoreCase) && o.ContentSha256 == entry.Content.Sha256);
    }

    public GameBuildIdentity? BuildIdentityFor(GameLibraryEntry? entry)
    {
        if ((object)entry != null)
        {
            BackendChoice selectedBackend = SelectedBackend;
            if ((object)selectedBackend != null)
            {
                LibraryCoreObservation libraryCoreObservation = ObservationFor(entry, selectedBackend);
                if ((object)libraryCoreObservation != null)
                {
                    string text = KnownHex(libraryCoreObservation.TitleId, 16);
                    string text2 = KnownHex(libraryCoreObservation.BuildId, 64);
                    if (text == null || text2 == null)
                    {
                        return null;
                    }
                    return new GameBuildIdentity(text, text2);
                }
            }
        }
        return null;
    }

    private void ClearServiceViews()
    {
        Prerequisites = null;
        Settings = Array.Empty<SettingView>();
        NativeSaves = Array.Empty<SaveView>();
        SaveLimitations = Array.Empty<string>();
        ContentConfiguration = null;
        NativeMods = null;
        ServicesBackendId = SelectedBackendId;
        LastBackendInfo = null;
        _enhancementCoordinator = null;
        ModLibraryBinding = null;
        InstalledMods = Array.Empty<InstalledMod>();
        ShaderCompilation = null;
        PerformancePlan = null;
        _performancePlanRuntime = null;
        PerformanceRendererSetting = null;
    }

    public Task InspectAgainAsync(string entryId)
    {
        return RunAsync("Inspecting native content identity", async (CancellationToken ct) =>
        {
            LibraryLaunchTarget target = await Library.ResolveForInspectionAsync(entryId, ct);
            BackendChoice backend = RequireBackend();
            ContentInfo contentInfo = await InspectAsync(target.AbsolutePath, ct);
            Observe(target.Entry, contentInfo, backend, loaded: false);
            ContentIssue contentIssue = contentInfo.Issues.FirstOrDefault((ContentIssue i) => i.Severity == "error");
            if ((object)contentIssue != null)
            {
                RecordFailure(contentIssue.Code, contentIssue.Message);
                Status = "Content could not be verified";
            }
            else
            {
                LocalArtwork.TryStoreNative(Library, target.Entry, contentInfo);
                Status = (contentInfo.IdentityVerified ? "Native identity inspected" : "Inspected; identity verification remains incomplete");
            }
        });
    }

    public Task CheckPrerequisitesAsync()
    {
        return RunAsync("Checking local core prerequisites", async (CancellationToken ct) =>
        {
            BackendChoice backend = RequireBackend();
            if (ServicesBackendId != backend.Id)
            {
                ClearServiceViews();
            }
            LastShutdown = null;
            LastLoadedIdentity = null;
            NxRuntime inspector = await ConnectAsync(backend, ct);
            try
            {
                DiagnosticStage("prerequisites.get", backend);
                RememberPrerequisites(await inspector.GetPrerequisitesForProfileAsync(Paths.DataRoot, CurrentProfileId, ct), backend);
                ContentIssue contentIssue = Prerequisites.Issues.FirstOrDefault((ContentIssue i) => i.Severity == "error");
                if ((object)contentIssue != null)
                {
                    RecordFailure(contentIssue.Code, contentIssue.Message);
                }
                Status = (Prerequisites.Issues.Any((ContentIssue i) =>
                {
                    string severity = i.Severity;
                    return (severity == "error" || severity == "warning") ? true : false;
                }) ? "Local prerequisites need attention" : "Local prerequisite check complete");
            }
            finally
            {
                await DisposeInspectorAsync(inspector);
            }
        });
    }

    public Task ImportPrerequisiteAsync(string kind, string source)
    {
        return RunAsync("Importing user-supplied " + kind, async (CancellationToken ct) =>
        {
            BackendChoice backend = RequireBackend();
            DiagnosticStage("prerequisites.import." + ((kind == "firmware") ? "firmware" : "keys"), backend);
            string text = PrerequisiteImportLimitation(kind);
            if (text != null)
            {
                throw new NxException("unsupported", text);
            }
            if (_runtime != null)
            {
                await StopInternalAsync();
            }
            SelectedBackendId = backend.Id;
            NxRuntime inspector = await ConnectAsync(backend, ct);
            try
            {
                DiagnosticStage("prerequisites.import." + ((kind == "firmware") ? "firmware" : "keys"), backend);
                PrerequisiteReport report = await inspector.ImportPrerequisiteForProfileAsync(kind, source, Paths.DataRoot, CurrentProfileId, ct);
                RememberPrerequisites(report, backend);
                ContentIssue contentIssue = Prerequisites.Issues.FirstOrDefault((ContentIssue i) => i.Severity == "error");
                if ((object)contentIssue != null)
                {
                    RecordFailure(contentIssue.Code, contentIssue.Message);
                }
                InfinitySession infinitySession = this;
                string status = (((object)contentIssue != null) ? "Imported; local prerequisites need attention" : ((kind == "keys" && !Prerequisites.HeaderKeyUsable) ? "Keys imported; encrypted-content header key is still unavailable" : ("Local " + kind + " import completed")));
                infinitySession.Status = status;
            }
            finally
            {
                await DisposeInspectorAsync(inspector);
            }
        });
    }

    public Task RefreshSettingsAsync(bool initialize = false)
    {
        return RunAsync("Reading core settings", async (CancellationToken ct) =>
        {
            BackendChoice backend = RequireBackend();
            if (ServicesBackendId != backend.Id)
            {
                ClearServiceViews();
            }
            if (initialize || _runtime != null)
            {
                NxRuntime runtime = await EnsureInitializedAsync(ct);
                List<SettingView> rows = new List<SettingView>();
                string code = default;
                foreach (SettingSchema schema in await runtime.GetSettingSchemasAsync(ct))
                {
                    SettingState state;
                    try
                    {
                        state = await runtime.GetSettingAsync(schema.Id, ct);
                    }
                    catch (NxException ex) when (((Func<bool>)delegate
                    {
                        // Could not convert BlockContainer to single expression
                        code = ex.Code;
                        return (code == "unsupported" || code == "invalid_state") ? true : false;
                    }).Invoke())
                    {
                        state = new SettingState
                        {
                            Id = schema.Id,
                            Reason = ex.Message
                        };
                    }
                    rows.Add(new SettingView(schema, state));
                }
                Settings = rows;
            }
            else
            {
                NxRuntime runtime = await ConnectAsync(backend, ct);
                try
                {
                    DiagnosticStage("settings.list", backend);
                    IReadOnlyList<SettingSchema> source = await runtime.GetSettingSchemasAsync(ct);
                    Settings = source.Select((SettingSchema s) => new SettingView(s, new SettingState
                    {
                        Id = s.Id,
                        Reason = "Initialize this core to read the current profile and any effective consumer state."
                    })).ToArray();
                }
                finally
                {
                    await DisposeInspectorAsync(runtime);
                }
            }
            ServicesBackendId = backend.Id;
            Status = "Settings read from the pinned core";
            NxRuntime runtime2 = _runtime;
            if (runtime2 != null && runtime2.Supports("window.get"))
            {
                try
                {
                    RenderWindowState gameWindow = await runtime2.GetRenderWindowAsync(ct);
                    GameWindow = gameWindow;
                }
                catch (NxException ex2) when (((Func<bool>)delegate
                {
                    // Could not convert BlockContainer to single expression
                    string code2 = ex2.Code;
                    return (code2 == "unsupported" || code2 == "invalid_state") ? true : false;
                }).Invoke())
                {
                    GameWindow = null;
                }
            }
        });
    }

    public Task SetSettingAsync(SettingSchema schema, JsonElement value, string? entryId)
    {
        return RunAsync("Updating " + schema.Label, async (CancellationToken ct) =>
        {
            schema.Validate(value);
            BackendChoice backendChoice = RequireBackend();
            SettingState observed = null;
            if (entryId != null)
            {
                if (schema.Apply == "before_initialize")
                {
                    throw new NxException("unsupported", "Use core preference scope for constructor-bound settings. Per-game replay happens after initialization.");
                }
                LibrarySettingRequestView settingRequests = Library.GetSettingRequests(entryId, backendChoice.Id, backendChoice.Revision);
                if (settingRequests.RequiresReview)
                {
                    throw new NxException("settings_review_required", "Review and resave this game's complete setting requests for the new core revision first.");
                }
                Dictionary<string, JsonElement> values = new Dictionary<string, JsonElement>(settingRequests.Stored?.Values ?? new Dictionary<string, JsonElement>()) { [schema.Id] = value.Clone() };
                Library.SetSettingRequests(entryId, backendChoice.Id, backendChoice.Revision, values);
                if (ActiveGame?.EntryId == entryId)
                {
                    NxRuntime runtime = _runtime;
                    if (runtime != null)
                    {
                        observed = await runtime.SetSettingAsync(schema.Id, value, persist: false, ct);
                    }
                }
                InfinitySession infinitySession = this;
                string status = (((object)observed == null) ? "Game setting saved for the next launch" : (observed.RequiresRestart ? "Game setting saved; restart required" : "Game setting requested; inspect effective state below"));
                infinitySession.Status = status;
            }
            else
            {
                observed = await (await EnsureInitializedAsync(ct)).SetSettingAsync(schema.Id, value, persist: true, ct);
                if (schema.Apply == "before_initialize")
                {
                    _runtimeInitializationPending = true;
                }
                Status = (observed.RequiresRestart ? "Core preference saved; restart required" : "Core preference requested; effective state read back");
            }
            if ((object)observed != null)
            {
                Settings = Settings.Select((SettingView s) => (!(s.Schema.Id == schema.Id)) ? s : s with
                {
                    State = observed
                }).ToArray();
            }
        });
    }

    public Task ResetGameSettingsAsync(string entryId)
    {
        return RunAsync("Restoring core preferences for this game", (CancellationToken _) =>
        {
            BackendChoice backendChoice = RequireBackend();
            Library.SetSettingRequests(entryId, backendChoice.Id, backendChoice.Revision, new Dictionary<string, JsonElement>());
            Status = "Game overrides cleared for the next launch";
            return Task.CompletedTask;
        });
    }

    public Task ReadContentConfigurationAsync(string gameEntryId)
    {
        return RunAsync("Reading native update and DLC selection", async (CancellationToken ct) =>
        {
            string title = TitleOf(Library.Get(gameEntryId)) ?? throw new NxException("identity_unknown", "Inspect the native title ID first.");
            GameContentConfiguration contentConfiguration = await (await EnsureInitializedAsync(ct)).GetContentConfigurationAsync(title, ct);
            ContentConfiguration = contentConfiguration;
            ServicesBackendId = SelectedBackendId;
        });
    }

    public Task ConfigureContentAsync(string gameEntryId, string? updateEntryId, IReadOnlyList<string> dlcEntryIds)
    {
        return RunAsync("Verifying selected update and DLC files", async (CancellationToken ct) =>
        {
            BackendChoice backend = RequireBackend();
            LibraryLaunchTarget verifiedGame = await Library.ResolveForLaunchAsync(gameEntryId, null, ct);
            GameLibraryEntry game = verifiedGame.Entry;
            string title = TitleOf(game) ?? throw new NxException("identity_unknown", "Inspect the native title ID first.");
            if (_runtime != null)
            {
                await StopInternalAsync();
            }
            SelectedBackendId = backend.Id;
            InvalidateOrbitPreparation("Selected content changed. Prepare the game again after saving the update and DLC selection.");
            string update = null;
            if (updateEntryId != null)
            {
                update = (await Library.ResolveForInspectionAsync(updateEntryId, ct)).AbsolutePath;
            }
            List<string> dlc = new List<string>();
            foreach (string dlcEntryId in dlcEntryIds)
            {
                List<string> list = dlc;
                list.Add((await Library.ResolveForInspectionAsync(dlcEntryId, ct)).AbsolutePath);
            }
            GameContentConfiguration contentConfiguration = await (await EnsureInitializedAsync(ct)).SetContentConfigurationAsync(title, update, dlc, ct);
            ContentConfiguration = contentConfiguration;
            NxRuntime runtime = _runtime;
            if (runtime != null)
            {
                ContentInfo metadata = await runtime.InspectContentAsync(verifiedGame.AbsolutePath, includeArtwork: false, null, ct);
                Observe(game, metadata, backend, loaded: false);
            }
            Status = (ContentConfiguration.Consumed ? "Selected content consumed by the native loader" : "Native content selection saved for the next load");
            ServicesBackendId = backend.Id;
        });
    }

    private async Task ConfigureStoredModsBeforeLoadAsync(NxRuntime runtime, GameLibraryEntry entry, BackendChoice backend, CancellationToken ct)
    {
        ExperimentalTimingModuleStatus = null;
        ActiveTimingRecipe = null;
        LibraryCoreObservation observation = ObservationFor(entry, backend);
        string title = KnownHex(observation?.TitleId, 16);
        if (title == null)
        {
            return;
        }
        string build = KnownHex(observation?.BuildId, 64);
        if (build == null)
        {
            return;
        }
        GameBuildIdentity binding = new GameBuildIdentity(title, build);
        ModRevision stored = Mods.Current(binding);
        bool requested = !OrbitEnabled && ResolveFrameRateTarget(entry.EntryId) == 60;
        TimingEnhancementRecipe recipe = (requested ? TimingEnhancementCatalog.Select(title, build, observation?.DisplayVersion) : null);
        if (!runtime.Supports("mods.set"))
        {
            if (requested)
            {
                ExperimentalTimingModuleStatus = "This installed core cannot mount timing enhancements. Update the core; Original behavior is retained.";
            }
            return;
        }
        NativeModState existing = await runtime.GetNativeModsAsync(title, ct);
        List<NativeModMount> list = (((object)stored != null) ? (await Task.Run(() => Mods.GetActiveMounts(binding), ct)).Mounts.Select((ModNativeMount m) => new NativeModMount(m.ModId, m.AbsoluteDirectory, Enabled: true)).ToList() : existing.Mounts.Where((NativeModMount m) => !TimingEnhancementCatalog.IsOwnedId(m.Id)).ToList());
        List<NativeModMount> mounts = list;
        if ((object)recipe != null && !TimingEnhancementInstaller.HasExecutableConflict(mounts))
        {
            try
            {
                Status = (recipe.Embedded ? "Preparing timing enhancement" : "Preparing version-specific timing patch");
                Notify();
                string sourcePath = await TimingEnhancementInstaller.PrepareAsync(recipe, Paths.DataRoot, ct);
                mounts.Add(new NativeModMount(recipe.Id, sourcePath, Enabled: true));
                ActiveTimingRecipe = recipe;
                ExperimentalTimingModuleStatus = "Experimental timing enhancement configured. " + recipe.Caution;
            }
            catch (Exception ex) when (((ex is IOException || ex is InvalidDataException || ex is HttpRequestException || ex is UnauthorizedAccessException) ? true : false) || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                ExperimentalTimingModuleStatus = "Timing enhancement unavailable: " + ex.Message + " Original behavior is retained; choose 60 FPS and retry when the download is available.";
                Log("frame_rate.prepare_failed", new
                {
                    Id = recipe.Id,
                    error = ex.GetType().Name
                });
            }
        }
        else if ((object)recipe != null)
        {
            ExperimentalTimingModuleStatus = "Timing enhancement skipped: an enabled ExeFS mod conflicts. Disable the conflicting mod or choose Original.";
        }
        else if (requested)
        {
            ExperimentalTimingModuleStatus = "No timing recipe matches " + (observation?.DisplayVersion ?? "unknown version") + " and this executable. Original behavior is retained.";
        }
        if (mounts.Count != 0 || existing.Mounts.Any((NativeModMount m) => TimingEnhancementCatalog.IsOwnedId(m.Id)) || (object)stored != null)
        {
            NativeMods = await runtime.ConfigureNativeModsAsync(title, mounts, build, backend.Revision, ct);
        }
    }

    public Task ReadNativeModsAsync(string gameEntryId)
    {
        return RunAsync("Reading native mod state", async (CancellationToken ct) =>
        {
            string title = TitleOf(Library.Get(gameEntryId)) ?? throw new NxException("identity_unknown", "Inspect the title ID first.");
            NativeModState nativeMods = await (await EnsureInitializedAsync(ct)).GetNativeModsAsync(title, ct);
            NativeMods = nativeMods;
        });
    }

    public Task ImportModAsync(string gameEntryId, string localBundle)
    {
        return RunAsync("Verifying exact-build mod bundle", async (CancellationToken ct) =>
        {
            GameBuildIdentity binding = BuildIdentityFor(Library.Get(gameEntryId)) ?? throw new NxException("identity_unknown", "A full main build ID from this exact core revision is required.");
            string extracted = null;
            string relative = null;
            ModReceipt receipt;
            try
            {
                if (File.Exists(localBundle))
                {
                    extracted = await Task.Run(() => ModArchive.ExtractAsync(localBundle, Workspace, ct), ct);
                }
                relative = await ImportStaging.CopyModBundleAsync(extracted ?? localBundle, Workspace, binding, ct);
                receipt = await Task.Run(() => Mods.Install(relative, binding), ct);
            }
            finally
            {
                if (relative != null)
                {
                    Directory.Delete(Path.Combine(Workspace.ImportsPath, relative), recursive: true);
                }
                if (extracted != null)
                {
                    Directory.Delete(extracted, recursive: true);
                }
            }
            ModLibraryBinding = binding;
            IReadOnlyList<InstalledMod> installedMods = await Task.Run(() => Mods.List(binding), ct);
            InstalledMods = installedMods;
            Status = "Imported " + receipt.ModId + "; disabled until you select it";
        });
    }

    public Task ReadModLibraryAsync(string gameEntryId)
    {
        return RunAsync("Verifying exact-build mod receipts", async (CancellationToken ct) =>
        {
            GameBuildIdentity binding = BuildIdentityFor(Library.Get(gameEntryId)) ?? throw new NxException("identity_unknown", "A full main build ID from this core revision is required.");
            IReadOnlyList<InstalledMod> installedMods = await Task.Run(() => Mods.List(binding), ct);
            InstalledMods = installedMods;
            ModLibraryBinding = binding;
        });
    }

    public Task ToggleModAsync(GameBuildIdentity binding, string id, bool enabled)
    {
        return RunAsync("Updating exact-build mod selection", async (CancellationToken ct) =>
        {
            await Task.Run(() => Mods.SetEnabled(binding, id, enabled), ct);
            InvalidateOrbitPreparation("Mod selection changed. Disable gameplay mods and prepare Orbit again before the next launch.");
            IReadOnlyList<InstalledMod> installedMods = await Task.Run(() => Mods.List(binding), ct);
            InstalledMods = installedMods;
            ModLibraryBinding = binding;
            Status = "Mod selection saved for the next load";
        });
    }

    public Task RemoveModAsync(GameBuildIdentity binding, string id)
    {
        return RunAsync("Removing mod selection", async (CancellationToken ct) =>
        {
            await Task.Run(() => Mods.Remove(binding, id), ct);
            InvalidateOrbitPreparation("Mod selection changed. Prepare Orbit again before the next launch.");
            IReadOnlyList<InstalledMod> installedMods = await Task.Run(() => Mods.List(binding), ct);
            InstalledMods = installedMods;
            ModLibraryBinding = binding;
            Status = "Mod removed for the next load; rollback history retained";
        });
    }

    public Task RollbackModsAsync(GameBuildIdentity binding)
    {
        return RunAsync("Restoring previous mod selection", async (CancellationToken ct) =>
        {
            ModRevision current = Mods.Current(binding);
            if (current?.ParentRevisionId == null)
            {
                throw new NxException("not_found", "No earlier mod selection exists for this exact build.");
            }
            await Task.Run(() => Mods.RollbackTo(binding, current.ParentRevisionId), ct);
            InvalidateOrbitPreparation("Mod selection changed. Disable gameplay mods and prepare Orbit again before the next launch.");
            IReadOnlyList<InstalledMod> installedMods = await Task.Run(() => Mods.List(binding), ct);
            InstalledMods = installedMods;
            ModLibraryBinding = binding;
            Status = "Previous mod selection restored for the next load";
        });
    }

    public Task DiscoverSavesAsync(string? gameEntryId)
    {
        return RunAsync("Discovering native save directories", async (CancellationToken ct) =>
        {
            RequireOfflineSaveTools();
            NxRuntime runtime = await EnsureInitializedAsync(ct);
            NativeSaveCatalog nativeSaveCatalog = await runtime.DiscoverNativeSavesAsync((gameEntryId == null) ? null : TitleOf(Library.Get(gameEntryId)), ct);
            List<SaveView> list = new List<SaveView>();
            List<string> list2 = nativeSaveCatalog.Limitations.ToList();
            NativeSaveDescriptor[] entries = nativeSaveCatalog.Entries;
            foreach (NativeSaveDescriptor nativeSaveDescriptor in entries)
            {
                if (!nativeSaveDescriptor.Existing || !nativeSaveDescriptor.AdapterVerified)
                {
                    list2.Add("An existing, adapter-verified mapping is unavailable for a discovered save.");
                }
                else
                {
                    try
                    {
                        SaveDirectoryMapping mapping = NativeSaveMappings.FromNativeObservation(new NativeSaveObservation(nativeSaveDescriptor.BackendId, runtime.Backend.BackendRevision, nativeSaveDescriptor.TitleId, nativeSaveDescriptor.UserId, nativeSaveDescriptor.SaveDataId, nativeSaveDescriptor.Type, nativeSaveDescriptor.FormatId, nativeSaveDescriptor.FormatVersion, nativeSaveDescriptor.RelativePath, nativeSaveDescriptor.Existing, nativeSaveDescriptor.AdapterVerified, nativeSaveDescriptor.Provenance), runtime.Backend.BackendId, runtime.Backend.BackendRevision);
                        list.Add(new SaveView(nativeSaveDescriptor, mapping, runtime.Backend.BackendRevision));
                    }
                    catch (ServiceException ex)
                    {
                        list2.Add(ex.Message);
                    }
                }
            }
            NativeSaves = list;
            SaveLimitations = list2.Distinct().ToArray();
            ServicesBackendId = runtime.Backend.BackendId;
            Status = list.Count + " verified native save mapping(s)";
            await AfterNativeSaveDiscoveryAsync(runtime, list, ct);
        });
    }

    private SaveStore SaveStoreFor(SaveView view)
    {
        RequireOfflineSaveTools();
        if (!Backends.Any((BackendChoice b) => b.Id == view.Native.BackendId && b.Revision == view.BackendRevision))
        {
            throw new NxException("version_mismatch", "This save mapping belongs to a different installed core revision.");
        }
        return new SaveStore(Workspace, "backends/" + view.Native.BackendId + "/default");
    }

    public Task BackupSaveAsync(SaveView view)
    {
        return RunAsync("Stopping and backing up the native save", async (CancellationToken ct) =>
        {
            await StopInternalAsync();
            SaveBackup saveBackup = await Task.Run(() => SaveStoreFor(view).CreateBackup(view.Mapping), ct);
            Status = "Verified save backup created: " + saveBackup.Manifest.BackupId;
        });
    }

    public async Task<IReadOnlyList<SaveBackup>> ListSaveBackupsAsync(SaveView view)
    {
        IReadOnlyList<SaveBackup> result = Array.Empty<SaveBackup>();
        await RunAsync("Verifying saved backups", async (CancellationToken ct) =>
        {
            await StopInternalAsync();
            result = await Task.Run(() => SaveStoreFor(view).ListBackups(view.Mapping.Identity), ct);
        });
        return result;
    }

    public Task RestoreSaveAsync(SaveView view, string backupId)
    {
        return RunAsync("Restoring the exact native save backup", async (CancellationToken ct) =>
        {
            await StopInternalAsync();
            await Task.Run(() => SaveStoreFor(view).RestoreBackup(view.Mapping, backupId), ct);
            Status = "Save restored; previous save preserved for rollback";
        });
    }

    public Task ImportSaveAsync(SaveView view, string bundleDirectory)
    {
        return RunAsync("Importing the verified native save snapshot", async (CancellationToken ct) =>
        {
            await StopInternalAsync();
            string incoming = await ImportStaging.CopyDirectoryAsync(bundleDirectory, Workspace, ct);
            await Task.Run(() => SaveStoreFor(view).ImportFromInbox(view.Mapping, incoming), ct);
            Status = "Save imported; previous save preserved for rollback";
        });
    }

    public Task RollbackSaveAsync(SaveView view)
    {
        return RunAsync("Restoring the save preserved before import", async (CancellationToken ct) =>
        {
            await StopInternalAsync();
            await Task.Run(() => SaveStoreFor(view).RollbackLastImport(view.Mapping), ct);
            Status = "Save import rolled back";
        });
    }

    public Task ExportSaveAsync(SaveView view, string backupId, string destination)
    {
        return RunAsync("Exporting verified save snapshot", async (CancellationToken ct) =>
        {
            await StopInternalAsync();
            await ImportStaging.ExportSaveAsync(await Task.Run(() => SaveStoreFor(view).InspectBackup(backupId), ct), destination, ct);
            Status = "Save snapshot exported";
        });
    }

    public EnhancementPlan EvaluateEnhancement(EnhancementProfile profile, bool experimental)
    {
        EnhancementCoordinator coordinator = Coordinator;
        if (coordinator != null)
        {
            return coordinator.Evaluate(new EnhancementSelection(profile.ProfileId, profile.Version, null, experimental));
        }
        BackendChoice selectedBackend = SelectedBackend;
        return Enhancements.Evaluate(profile.ProfileId, profile.Version, new EnhancementContext
        {
            BackendId = (selectedBackend?.Id ?? "unavailable"),
            BackendRevision = (selectedBackend?.Revision ?? ""),
            IdentityVerified = false
        }, null, experimental);
    }

    public Task ImportEnhancementAsync(string path)
    {
        return RunAsync("Reviewing enhancement profile manifest", async (CancellationToken ct) =>
        {
            string relativeProfilePath = await ImportStaging.CopyProfileAsync(path, Workspace, ct);
            EnhancementProfile enhancementProfile = Enhancements.ImportFromInbox(relativeProfilePath);
            Status = "Imported " + enhancementProfile.DisplayName + "; no profile was applied";
        });
    }

    public Task ApplyEnhancementAsync(EnhancementProfile profile, bool experimental)
    {
        return RunAsync("Applying exact-session enhancement transaction", async (CancellationToken ct) =>
        {
            if (OrbitEnabled)
            {
                throw new NxException("network_mods_mismatch", "Gameplay enhancements stay off during an Orbit session so peers retain matching behavior.");
            }
            await (Coordinator ?? throw new NxException("identity_unknown", "Load and pause the exact game before evaluating a profile.")).ApplyAsync(new EnhancementSelection(profile.ProfileId, profile.Version, null, experimental), ct);
            Status = "Preset turned on";
        });
    }

    public Task RollbackEnhancementAsync(string receiptId)
    {
        return RunAsync("Restoring recorded original settings", async (CancellationToken ct) =>
        {
            await (Coordinator ?? throw new NxException("enhancement_not_active", "The receipt's original runtime is not active.")).RollbackAsync(receiptId, ct);
            Status = "Preset turned off; previous settings restored";
        });
    }

    public Task ReleaseRemoteInputAsync(int player = 0)
    {
        return RunAsync("Updating gameplay input assignment", async (CancellationToken ct) =>
        {
            NxRuntime runtime = _runtime;
            if (runtime != null && runtime.Supports("input.assign"))
            {
                await runtime.AssignCoreInputAsync(player, null, ct);
            }
            Status = "Gameplay input assignment updated";
        });
    }

    private void InitializeSharedSetup()
    {
        SharedPrerequisites = new SharedPrerequisiteStore(Workspace);
        SharedSetup = SharedPrerequisites.Read();
    }

    public Task InstallSharedPrerequisiteAsync(string kind, string source)
    {
        return RunAsync("Setting up " + kind + " for NX Infinity Core", async (CancellationToken ct) =>
        {
            DiagnosticStage("prerequisites.shared.stage");
            await SharedPrerequisites.StageAsync(kind, source, ct);
            SharedSetup = SharedPrerequisites.Read();
            await ProvisionSharedSetupInternalAsync(SharedPrerequisiteProvisionMode.Pending, ct);
        });
    }

    public Task ProvisionSharedSetupAsync(bool retryFailures = false)
    {
        return RunAsync("Applying NX Infinity Core setup", (CancellationToken ct) => ProvisionSharedSetupInternalAsync(retryFailures ? SharedPrerequisiteProvisionMode.RetryFailures : SharedPrerequisiteProvisionMode.Pending, ct));
    }

    public Task RollbackSharedSetupAsync(string revisionId)
    {
        return RunAsync("Restoring previous shared setup", async (CancellationToken ct) =>
        {
            if (_runtime != null && !HasGame)
            {
                await StopInternalAsync();
            }
            IReadOnlyList<SharedPrerequisiteTarget> targets = await SharedTargetsAsync(ct);
            SharedPrerequisiteProvisionReport lastSharedSetupReport = await SharedPrerequisites.RollbackAsync(revisionId, targets, SharedSetupProgress(), ct);
            LastSharedSetupReport = lastSharedSetupReport;
            SharedSetup = SharedPrerequisites.Read();
            Status = "Previous shared source requested; review the setup result";
        });
    }

    private async Task ProvisionSharedSetupInternalAsync(SharedPrerequisiteProvisionMode mode, CancellationToken ct)
    {
        if (_runtime != null && !HasGame)
        {
            await StopInternalAsync();
        }
        IReadOnlyList<SharedPrerequisiteTarget> targets = await SharedTargetsAsync(ct);
        DiagnosticStage("prerequisites.shared.provision");
        LastSharedSetupReport = await SharedPrerequisites.ProvisionAsync(targets, mode, SharedSetupProgress(), ct);
        SharedSetup = SharedPrerequisites.Read();
        Status = (LastSharedSetupReport.Cancelled ? "Shared setup canceled; completed core results retained" : "Shared setup processed · review the setup result");
        Log("prerequisites.shared.completed", new
        {
            outcomeCount = LastSharedSetupReport.Outcomes.Count,
            Cancelled = LastSharedSetupReport.Cancelled
        });
    }

    private IProgress<SharedPrerequisiteProgress> SharedSetupProgress()
    {
        return new Progress<SharedPrerequisiteProgress>((SharedPrerequisiteProgress progress) =>
        {
            Status = (Backends.FirstOrDefault((BackendChoice b) => b.Id == progress.BackendId)?.Name ?? "Core") + " · " + progress.Kind + " · " + progress.Stage.Replace('_', ' ');
            Progress = ((progress.TotalTargets == 0) ? ((double?)null) : new double?((double)progress.CompletedTargets / (double)progress.TotalTargets));
            Notify();
        });
    }

    private async Task<IReadOnlyList<SharedPrerequisiteTarget>> SharedTargetsAsync(CancellationToken ct)
    {
        List<SharedPrerequisiteTarget> targets = new List<SharedPrerequisiteTarget>();
        List<SetupInstallationIssue> issues = new List<SetupInstallationIssue>();
        foreach (BackendChoice backend in Backends)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (backend.Id == "nxi")
                {
                    await Task.Run(() => NxiProfileMigration.Ensure(Paths.DataRoot, "default"), ct);
                }
                targets.Add(new SharedPrerequisiteTarget
                {
                    Launch = await backend.VerifyAsync(Paths, ct),
                    WorkspaceBase = Paths.DataRoot
                });
            }
            catch (Exception ex) when ((ex is NxException || ex is IOException || ex is UnauthorizedAccessException) ? true : false)
            {
                issues.Add(new SetupInstallationIssue(backend.Id, backend.Revision, SafeCode((ex is NxException ex2) ? ex2.Code : ex.GetType().Name)));
            }
        }
        SharedSetupInstallationIssues = issues;
        return targets;
    }

    private async Task ProvisionSharedBeforeConnectionAsync(BackendLaunch launch, CancellationToken ct)
    {
        SharedSetup = SharedPrerequisites.Read();
        if (SharedSetup.Sources.Count != 0)
        {
            LastSharedSetupReport = await SharedPrerequisites.ProvisionAsync(
            [
                new SharedPrerequisiteTarget
                {
                    Launch = launch,
                    WorkspaceBase = Paths.DataRoot,
                    ProfileId = CurrentProfileId
                }
            ], SharedPrerequisiteProvisionMode.Pending, null, ct);
            SharedSetup = SharedPrerequisites.Read();
        }
    }

    private void ReadDisplayPreference()
    {
        try
        {
            if (!File.Exists(DisplayPreferencePath) || new FileInfo(DisplayPreferencePath).Length > 4096)
            {
                return;
            }
            using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(DisplayPreferencePath));
            GameFullscreenPreference = jsonDocument.RootElement.TryGetProperty("gameFullscreen", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when ((ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) ? true : false)
        {
        }
    }

    private async Task SaveDisplayPreferenceAsync(bool fullscreen, CancellationToken ct)
    {
        string path = DisplayPreferencePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                gameFullscreen = fullscreen
            }), ct);
            File.Move(temporary, path, overwrite: true);
            GameFullscreenPreference = fullscreen;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<RenderWindowState> ChangeGameWindowAsync(NxRuntime runtime, RenderWindowChange change, CancellationToken ct = default(CancellationToken))
    {
        if (change.Visible == true && change.Fullscreen.HasValue)
        {
            RenderWindowState? gameWindow = GameWindow;
            if ((object)gameWindow == null || !gameWindow.Visible)
            {
                await runtime.SetRenderWindowAsync(new RenderWindowChange
                {
                    Visible = true
                }, ct);
            }
        }
        RenderWindowState renderWindowState = await runtime.SetRenderWindowAsync(change, ct);
        if (_runtime == runtime)
        {
            GameWindow = renderWindowState;
        }
        bool? fullscreen = change.Fullscreen;
        if (fullscreen.HasValue)
        {
            bool valueOrDefault = fullscreen == true;
            if (renderWindowState.Fullscreen != valueOrDefault || renderWindowState.FullscreenConfirmed == false || renderWindowState.FullscreenRequestAccepted == false || renderWindowState.FullscreenSynchronizationCompleted == false)
            {
                throw new NxException("window_transition_unconfirmed", "The window system did not confirm the requested game display mode. Its actual state is shown in Graphics & display.");
            }
        }
        return renderWindowState;
    }

    public Task RefreshGameWindowAsync()
    {
        return RunAsync("Reading game display state", async (CancellationToken ct) =>
        {
            GameWindow = await (_runtime ?? throw new NxException("invalid_state", "Launch a game or initialize the selected core to read its window.")).GetRenderWindowAsync(ct);
            Status = "Native game-window state read";
        });
    }

    public Task SetGameFullscreenAsync(bool fullscreen)
    {
        return RunAsync(fullscreen ? "Selecting game fullscreen" : "Selecting a windowed game", async (CancellationToken ct) =>
        {
            NxRuntime runtime = _runtime;
            if (runtime != null && HasGame)
            {
                if (!runtime.Supports("window.set"))
                {
                    throw new NxException("unsupported", "This core does not expose game-window controls.");
                }
                await ChangeGameWindowAsync(runtime, new RenderWindowChange
                {
                    Visible = true,
                    Fullscreen = fullscreen
                }, ct);
                if (runtime.State == RuntimeState.Paused)
                {
                    await runtime.ResumeAsync(ct);
                }
                if (runtime.Supports("input.routing.set"))
                {
                    await runtime.SetInputTargetAsync(guest: true, ct);
                }
                _guestRequested = true;
                GuestOwnsInput = runtime.State == RuntimeState.Running && !_interactionOwnsInput;
            }
            await SaveDisplayPreferenceAsync(fullscreen, ct);
            Status = (HasGame ? "Game display mode read back" : "Game display preference saved for the next launch");
        });
    }

    private void QueueNativeMenu(NxRuntime runtime)
    {
        if (_runtime == runtime && !GameWindowClosing && HasGame)
        {
            _guestRequested = false;
            GuestOwnsInput = false;
            if (_menuRuntimes.TryAdd(runtime, 0))
            {
                _ = CompleteNativeMenuAsync(runtime);
            }
        }
    }

    private async Task CompleteNativeMenuAsync(NxRuntime runtime)
    {
        await _gate.WaitAsync();
        try
        {
            bool flag = _disposed || _runtime != runtime || GameWindowClosing;
            if (!flag)
            {
                RuntimeState state = runtime.State;
                bool flag2 = (uint)(state - 3) <= 1u;
                flag = !flag2;
            }
            if (!flag)
            {
                _operationSequence++;
                _diagnosticOperation = "game.pause";
                _diagnosticStage = "menu.request";
                _diagnosticBackend = Backends.FirstOrDefault((BackendChoice b) => b.Id == runtime.Backend.BackendId);
                Busy = true;
                Status = "Opening game menu";
                Notify();
                await PauseInternalAsync(runtime, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            RecordFailure((ex is NxException ex2) ? ex2.Code : ex.GetType().Name, ex.Message);
        }
        finally
        {
            Busy = false;
            _menuRuntimes.TryRemove(runtime, out var _);
            _gate.Release();
            Notify();
        }
    }

    private bool HasTerminalSession(NxRuntime runtime)
    {
        bool flag = _runtime != runtime || _closingRuntimes.ContainsKey(runtime);
        if (!flag)
        {
            RuntimeState state = runtime.State;
            bool flag2 = (uint)(state - 5) <= 2u;
            flag = flag2;
        }
        if (!flag)
        {
            return runtime.WorkerDiagnostics.HasExited;
        }
        return true;
    }

    private bool StopIfTerminal(NxRuntime runtime)
    {
        if (_runtime != runtime)
        {
            return true;
        }
        if (!HasTerminalSession(runtime))
        {
            return false;
        }
        _guestRequested = false;
        GuestOwnsInput = false;
        if (_runtime == runtime)
        {
            QueueNativeClosure(runtime, (runtime.State == RuntimeState.Stopped) ? "native_session_stopped" : "native_connection_ended");
        }
        return true;
    }

    private async Task PauseInternalAsync(NxRuntime runtime, CancellationToken ct)
    {
        _guestRequested = false;
        GuestOwnsInput = false;
        if (StopIfTerminal(runtime))
        {
            return;
        }
        RuntimeState state = runtime.State;
        if ((uint)(state - 3) > 1u)
        {
            throw new NxException("invalid_state", "There is no running game to pause.");
        }
        try
        {
            DiagnosticStage("input.release");
            if (runtime.Supports("input.routing.set"))
            {
                await runtime.SetInputTargetAsync(guest: false, ct);
            }
            if (StopIfTerminal(runtime))
            {
                return;
            }
            DiagnosticStage("runtime.pause");
            if (runtime.State == RuntimeState.Running)
            {
                await runtime.PauseAsync(ct);
            }
            if (!StopIfTerminal(runtime))
            {
                DiagnosticStage("window.hide");
                if (runtime.Supports("window.set"))
                {
                    await ChangeGameWindowAsync(runtime, new RenderWindowChange
                    {
                        Visible = false
                    }, ct);
                }
                if (!StopIfTerminal(runtime))
                {
                    Status = "Paused";
                    MenuRequested?.Invoke();
                }
            }
        }
        catch (NxException ex) when (((Func<bool>)delegate
        {
            // Could not convert BlockContainer to single expression
            bool flag = default;
            switch (ex.Code)
            {
                case "core_exited":
                case "connection_faulted":
                case "invalid_state":
                    flag = true;
                    break;
                default:
                    flag = false;
                    break;
            }
            return flag && HasTerminalSession(runtime);
        }).Invoke())
        {
            StopIfTerminal(runtime);
        }
    }

    private void QueueNativeClosure(NxRuntime runtime, string reason)
    {
        if (_runtime != runtime)
        {
            return;
        }
        _guestRequested = false;
        GuestOwnsInput = false;
        if (!_closingRuntimes.TryAdd(runtime, 0))
        {
            return;
        }
        LastGameExitReason = reason;
        Status = "Closing game and releasing its resources";
        foreach (PendingInteraction value in _pendingInteractions.Values)
        {
            try
            {
                value.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        Notify();
        _ = CompleteNativeClosureAsync(runtime);
    }

    private async Task CompleteNativeClosureAsync(NxRuntime runtime)
    {
        await _gate.WaitAsync();
        try
        {
            if (_runtime != runtime || _disposed)
            {
                return;
            }
            Busy = true;
            Status = "Closing game and releasing its resources";
            Notify();
            bool unexpected = runtime.State == RuntimeState.Faulted || runtime.WorkerDiagnostics.FailureCode != null;
            if (unexpected)
            {
                _operationSequence++;
                _diagnosticOperation = "game.session";
                _diagnosticStage = "runtime.exit";
                _diagnosticBackend = Backends.FirstOrDefault((BackendChoice b) => b.Id == runtime.Backend.BackendId);
                WorkerDiagnosticSnapshot workerDiagnostics = runtime.WorkerDiagnostics;
                InfinitySession infinitySession = this;
                string? code = workerDiagnostics.FailureCode ?? "core_exited";
                string exitCodeHex = workerDiagnostics.ExitCodeHex;
                infinitySession.RecordFailure(code, "The native core connection ended" + ((exitCodeHex != null) ? (" (process exit " + exitCodeHex + ")") : "") + ". Private worker details are retained locally in diagnostics; public exports contain only safe process evidence.");
            }
            await StopInternalAsync();
            if (unexpected)
            {
                Status = "Game closed unexpectedly; open Last issue for details.";
            }
        }
        catch (Exception ex)
        {
            RecordFailure((ex is NxException ex2) ? ex2.Code : ex.GetType().Name, ex.Message);
            Status = "Game closed; shutdown needs review";
        }
        finally
        {
            Busy = false;
            _closingRuntimes.TryRemove(runtime, out var _);
            _gate.Release();
            Notify();
        }
    }

    private bool ObserveWindowEvent(NxRuntime runtime, RuntimeEvent observation)
    {
        string name = observation.Name;
        if ((name == "window.close_requested" || name == "window.closed") ? true : false)
        {
            QueueNativeClosure(runtime, (observation.Name == "window.close_requested") ? "native_window_close" : "native_window_closed");
            return true;
        }
        if (observation.Name == "window.menu_requested")
        {
            QueueNativeMenu(runtime);
            return true;
        }
        if (observation.Name == "window.changed")
        {
            try
            {
                GameWindow = RenderWindowTelemetry.ReadChangedEvent(observation);
            }
            catch (NxException ex)
            {
                Log("window.observation_rejected", new
                {
                    code = ex.Code
                });
            }
            return true;
        }
        if (observation.Name == "window.error")
        {
            RecordFailure("window_transition_failed", "The native game window could not complete its display transition. Open Graphics & display to read its actual state and retry.");
            return true;
        }
        bool flag;
        switch (observation.Name)
        {
            case "state":
            case "state.changed":
            case "lifecycle":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag && observation.Data.ValueKind == JsonValueKind.Object && observation.Data.TryGetProperty("state", out var value) && value.ValueKind == JsonValueKind.String)
        {
            name = value.GetString();
            if ((name == "stopped" || name == "faulted") ? true : false)
            {
                QueueNativeClosure(runtime, (value.GetString() == "stopped") ? "native_session_stopped" : "native_session_faulted");
            }
            else if (value.GetString() == "paused" && _guestRequested)
            {
                QueueNativeMenu(runtime);
            }
            return true;
        }
        return false;
    }

    public async Task RunBackgroundMaintenanceAsync(Func<Task> action)
    {
        if (Busy || HasGame || OrbitEnabled)
        {
            return;
        }
        IsBackgroundMaintenance = true;
        Notify();
        try
        {
            await action();
        }
        finally
        {
            IsBackgroundMaintenance = false;
            Notify();
        }
    }
}
