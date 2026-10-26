using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NxInfinity.Runtime;

/// <summary>A single source-built core session. Dispose before selecting another backend.</summary>
public sealed partial class NxRuntime : IAsyncDisposable
{
    private readonly BackendLaunch _launch;
    private readonly WorkerConnection _worker;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<string, JsonElement> _persisted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _deferredSettings = new(StringComparer.Ordinal);
    private Dictionary<string, SettingSchema>? _schema;
    private FileStream? _clientLease;
    private string? _marker;
    private bool _ownsMarker;
    private byte[]? _previousMarker;
    private bool _initializeAcknowledged;
    private int _everStarted;
    private bool _disposed;
    private int _state;
    private long _nativeStateGeneration;
    private readonly object _stateGate = new();

    private NxRuntime(BackendLaunch launch, WorkerConnection worker, BackendInfo backend)
    {
        _launch = launch; _worker = worker; Backend = backend;
        _worker.NativeStateChanged = observed =>
        {
            lock (_stateGate)
            {
                if (_disposed || State is RuntimeState.Disposed or RuntimeState.Faulted) return;
                // A late pause/resume event must not resurrect a terminal session.
                if (State == RuntimeState.Stopped && observed is RuntimeState.Running or RuntimeState.Paused) return;
                if (observed == RuntimeState.Running) Volatile.Write(ref _everStarted, 1);
                State = observed; _nativeStateGeneration++;
            }
        };
    }

    public BackendInfo Backend { get; }
    /// <summary>Process owned by this runtime, for optional local window integrations.</summary>
    public int WorkerProcessId => _worker.ProcessId;
    /// <summary>
    /// SHA-256 of the exact local installation manifest whose complete artifact
    /// inventory was verified at load and rechecked immediately before startup.
    /// Direct launches have no verified installation identity and return null.
    /// </summary>
    public string? BackendArtifactManifestSha256 => _launch.VerifiedInstallation?.ManifestSha256;
    public RuntimeState State { get => (RuntimeState)Volatile.Read(ref _state); private set => Volatile.Write(ref _state, (int)value); }
    public string? BackendDataRoot { get; private set; }
    public ContentInfo? LoadedContent { get; private set; }
    public ShutdownReport? Shutdown { get; private set; }
    public long DroppedEventCount => _worker.DroppedEvents;

    public bool Supports(string operation) => Backend.Capabilities.Contains(operation, StringComparer.Ordinal);

    public static async Task<NxRuntime> ConnectAsync(BackendLaunch launch, CancellationToken cancellationToken = default)
    {
        // Snapshot caller-owned mutable arrays/dictionaries before the first await.
        launch = VerifiedBackendInstallation.Snapshot(launch);
        var connection = await WorkerConnection.StartAsync(launch, cancellationToken);
        try
        {
            var response = await connection.CallAsync("hello", null, launch.StartupTimeout, cancellationToken);
            var backend = NxJson.Read<BackendInfo>(response);
            if (backend.Capabilities is null || backend.Limitations is null || backend.Capabilities.Length > 4096 ||
                backend.Capabilities.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128))
                throw new NxException("protocol_mismatch", "Worker advertised invalid capability metadata.");
            if (backend.ProtocolVersion != NxVersion.Protocol || backend.BackendId != launch.BackendId ||
                !string.Equals(backend.BackendRevision, launch.Revision, StringComparison.OrdinalIgnoreCase))
                throw new NxException("version_mismatch", "Worker identity/revision does not match the selected installation.");
            if (!launch.AllowUnqualified && backend.Qualification != "qualified")
                throw new NxException("unqualified_backend", "This backend has not passed release qualification. Explicit experimental selection is required.");
            foreach (var required in new[] { "initialize", "load", "start", "stop", "dispose" })
                if (!backend.Capabilities.Contains(required, StringComparer.Ordinal))
                    throw new NxException("unsupported", $"Backend cannot provide the required {required} operation.");
            return new NxRuntime(launch, connection, backend);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task InitializeAsync(RuntimeOptions options, CancellationToken cancellationToken = default)
    {
        bool initializeSent = false;
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Created);
            SafePaths.ValidateIdentifier(options.ProfileId, nameof(options.ProfileId));
            if (_ldnConfigured && options.ProfileId == "default")
                throw new NxException("online_profile_required", "Local Wireless uses a separate profile so online sessions cannot overwrite offline saves.");
            if (options.Width is < 64 or > 16384 || options.Height is < 64 or > 16384 ||
                options.WindowTitle.Length > 128 || options.GraphicsBackend is not ("vulkan" or "opengl" or "null"))
                throw new NxException("invalid_argument", "Invalid render-surface dimensions, title or graphics backend.");
            if (options.RendererThreading is not (null or "single_gpu_thread" or "threaded_renderer"))
                throw new NxException("invalid_argument", "Choose a supported explicit renderer threading mode.");
            if (options.RendererThreading is not null && !Supports("initialize.renderer_threading"))
                throw new NxException("unsupported", "This adapter does not expose constructor-bound renderer threading.");
            var root = SafePaths.Absolute(options.DataRoot);
            SafePaths.RejectLinks(root);
            if (Backend.BackendId == "nxi") NxiProfileMigration.Ensure(root, options.ProfileId);
            BackendDataRoot = Path.Combine(root, "backends", Backend.BackendId, options.ProfileId);
            SafePaths.RejectLinks(BackendDataRoot);
            Directory.CreateDirectory(BackendDataRoot);
            SafePaths.RejectLinks(BackendDataRoot);
            try
            {
                SafePaths.RejectLinks(Path.Combine(BackendDataRoot, ".nx-client.lock"));
                _clientLease = new FileStream(Path.Combine(BackendDataRoot, ".nx-client.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e) { throw new NxException("data_in_use", "Another NX client owns this backend/profile data root.", e); }
            // Validate the complete persisted request before native construction
            // or writing an active-session marker. Constructor-bound settings
            // cannot be replayed as live changes after initialize.
            var persistedSettings = await ReadPersistedSettingsAsync(cancellationToken);
            string? rendererThreading = options.RendererThreading;
            if (rendererThreading is null && persistedSettings.TryGetValue("graphics.backend_threading", out var savedThreading))
                rendererThreading = savedThreading.GetString();
            if (rendererThreading is not null && (!Supports("initialize.renderer_threading") ||
                rendererThreading is not ("single_gpu_thread" or "threaded_renderer")))
                throw new NxException("settings_migration_required", "The saved renderer mode is unavailable in this exact adapter.");
            _marker = Path.Combine(BackendDataRoot, ".nx-unclean.json");
            var runtimeLock = Path.Combine(BackendDataRoot, ".nx-runtime.lock");
            SafePaths.RejectLinks(runtimeLock);
            FileStream probe;
            try { probe = new FileStream(runtimeLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) { throw new NxException("data_in_use", "A core worker or save transaction still owns this data root.", e); }
            using (probe)
            {
                var transactionMarker = Path.Combine(BackendDataRoot, ".nx-save-transaction.json");
                SafePaths.RejectLinks(transactionMarker);
                SafePaths.RejectLinks(_marker);
                if (File.Exists(transactionMarker))
                    throw new NxException("recovery_required", "An interrupted save transaction must be recovered before starting the core.");
                if (File.Exists(_marker) && !options.AcknowledgeUncleanShutdown)
                    throw new NxException("unclean_shutdown", "The previous core did not close cleanly. Inspect/back up its data and explicitly acknowledge recovery.");
                if (File.Exists(_marker))
                {
                    if (new FileInfo(_marker).Length > 65536) throw new NxException("invalid_argument", "Previous session marker is invalid.");
                    _previousMarker = await File.ReadAllBytesAsync(_marker, cancellationToken);
                }
                await WriteJsonAtomicallyAsync(_marker, new
                {
                    nxVersion = NxVersion.Sdk, backendId = Backend.BackendId, revision = Backend.BackendRevision,
                    workerPid = _worker.ProcessId, startedUtc = DateTimeOffset.UtcNow,
                    previousMarker = _previousMarker is null ? null : Convert.ToBase64String(_previousMarker),
                    note = "Active session or unclean termination. Removed only after a clean core disposal."
                }, cancellationToken);
                _ownsMarker = true;
            }
            // Release the probe before the worker acquires the same runtime lease.
            // An intervening save operation causes explicit initialization failure.
            initializeSent = true;
            var initialization = new Dictionary<string, object?>
            {
                ["dataRoot"] = BackendDataRoot, ["graphicsBackend"] = options.GraphicsBackend,
                ["width"] = options.Width, ["height"] = options.Height,
                ["title"] = options.WindowTitle, ["headless"] = options.Headless
            };
            // Optional means omitted, not JSON null: native adapters preserve
            // their original default when no renderer mode was requested.
            if (rendererThreading is not null) initialization["rendererThreading"] = rendererThreading;
            await LifecycleAsync("initialize", initialization, RuntimeState.Initialized, cancellationToken);
            _initializeAcknowledged = true;
            await LoadPersistedSettingsAsync(persistedSettings, rendererThreading, cancellationToken);
        }
        catch
        {
            // Retain the session lease if a worker may have partially initialized.
            // DisposeAsync will terminate it before releasing the lease.
            if (!initializeSent)
            {
                _clientLease?.Dispose(); _clientLease = null;
                _marker = null; _previousMarker = null; _ownsMarker = false;
            }
            if (_worker.IsFaulted) State = RuntimeState.Faulted;
            throw;
        }
        finally { _operations.Release(); }
    }

    public async Task<ContentInfo> LoadAsync(string contentPath, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Initialized);
            var path = SafePaths.Absolute(contentPath);
            if (!File.Exists(path) && !Directory.Exists(path)) throw new NxException("invalid_argument", "User-supplied content does not exist.");
            var data = await LifecycleAsync("load", new { contentPath = path }, RuntimeState.Loaded, cancellationToken);
            LoadedContent = NxJson.Read<ContentInfo>(data);
            return LoadedContent;
        }
        catch (NxException e) when (e.Code is "core_error" or "io_error") { State = RuntimeState.Faulted; throw; }
        finally { _operations.Release(); }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync("start", RuntimeState.Running, [RuntimeState.Loaded], cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync("pause", RuntimeState.Paused, [RuntimeState.Running], cancellationToken);
    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync("resume", RuntimeState.Running, [RuntimeState.Paused], cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync("stop", RuntimeState.Stopped, [RuntimeState.Initialized, RuntimeState.Loaded, RuntimeState.Running, RuntimeState.Paused], cancellationToken);

    private async Task TransitionAsync(string operation, RuntimeState expected, RuntimeState[] allowed, CancellationToken ct)
    {
        await _operations.WaitAsync(ct);
        try
        {
            // Native window events can finish an otherwise valid stop/pause while
            // this caller waits for the command gate. These boundaries are idempotent.
            if (operation is "stop" or "pause" && State == expected) return;
            Require(allowed);
            // A start can execute guest code even if its acknowledgement is lost.
            if (operation is "start" or "resume") Volatile.Write(ref _everStarted, 1);
            await LifecycleAsync(operation, null, expected, ct);
        }
        finally { _operations.Release(); }
    }

    private async Task<JsonElement> LifecycleAsync(string operation, object? args, RuntimeState expected, CancellationToken ct)
    {
        long generation;
        lock (_stateGate) generation = _nativeStateGeneration;
        var data = await CallSupportedAsync(operation, args, ct);
        if (!data.TryGetProperty("state", out var state) ||
            !Enum.TryParse<RuntimeState>(state.GetString(), true, out var actual) || actual != expected)
        {
            State = RuntimeState.Faulted;
            throw new NxException("protocol_mismatch", $"Core did not acknowledge {expected} after {operation}.");
        }
        lock (_stateGate) { if (_nativeStateGeneration == generation) State = actual; }
        return data;
    }

    public async Task<IReadOnlyList<SettingSchema>> GetSettingSchemasAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try { await EnsureSchemasAsync(cancellationToken); return _schema!.Values.ToArray(); }
        finally { _operations.Release(); }
    }

    private async Task EnsureSchemasAsync(CancellationToken ct)
    {
        EnsureUsable();
        if (_schema is not null) return;
        var data = await CallSupportedAsync("settings.list", null, ct);
        var schemas = NxJson.Read<SettingSchema[]>(data.GetProperty("settings"));
        if (schemas.Length > 4096 || schemas.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 128) ||
            schemas.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != schemas.Length)
            throw new NxException("protocol_mismatch", "Invalid or duplicate backend setting identifiers.");
        if (schemas.Any(x => x.Apply is not ("live" or "paused" or "before_load" or "before_initialize" or "restart") ||
            x.Kind is not ("boolean" or "integer" or "number" or "string" or "enum") || x.Values is null || x.Dependencies is null))
            throw new NxException("protocol_mismatch", "Unknown settings lifecycle or value semantics require adapter review.");
        _schema = schemas.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    public async Task<SettingState> GetSettingAsync(string id, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            await EnsureSchemasAsync(cancellationToken);
            if (!_schema!.ContainsKey(id)) throw new NxException("unsupported", "Setting is not exposed by this pinned adapter.");
            return await ReadSettingStateUnlockedAsync(id, cancellationToken);
        }
        finally { _operations.Release(); }
    }

    public async Task<SettingState> SetSettingAsync<T>(string id, T value, bool persist = true, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToElement(value, NxJson.Options);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Initialized, RuntimeState.Loaded, RuntimeState.Running, RuntimeState.Paused);
            await EnsureSchemasAsync(cancellationToken);
            if (!_schema!.TryGetValue(id, out var schema)) throw new NxException("unsupported", "Setting is not exposed by this pinned adapter.");
            schema.Validate(json);
            SettingState state;
            bool deferred = schema.Apply == "before_initialize" ||
                schema.Apply is "restart" or "before_load" && State != RuntimeState.Initialized;
            if (deferred)
            {
                var effective = Supports("settings.get")
                    ? NxJson.Read<SettingState>(await CallSupportedAsync("settings.get", new { id }, cancellationToken))
                    : new SettingState { Id = id };
                ValidateSettingResult(id, effective);
                state = IsSettingConsumed(effective, json) ? effective with { Requested = json } :
                    effective with { Requested = json, Applied = false, RequiresRestart = true,
                        Reason = "Requested for the next eligible load or runtime initialization; the active native value remains unchanged." };
            }
            else
            {
                if (schema.Apply == "paused" && State == RuntimeState.Running)
                    throw new NxException("invalid_state", "Pause emulation before changing this setting.");
                state = NxJson.Read<SettingState>(await CallSupportedAsync("settings.set", new { id, value = json }, cancellationToken));
                ValidateSettingResult(id, state);
            }
            if (persist)
            {
                var staged = new Dictionary<string, JsonElement>(_persisted, StringComparer.Ordinal) { [id] = json.Clone() };
                await WriteJsonAtomicallyAsync(Path.Combine(BackendDataRoot!, "nx-settings.json"), new
                { backendId = Backend.BackendId, revision = Backend.BackendRevision, values = staged }, cancellationToken);
                _persisted[id] = json.Clone();
            }
            // A failed write or persistence operation cannot erase an earlier
            // staged request. Only successful staging, or an actual consumer
            // acknowledgment of the requested value, changes this map.
            if (IsSettingConsumed(state, json)) _deferredSettings.Remove(id);
            else if (deferred || state.RequiresRestart) _deferredSettings[id] = json.Clone();
            return ProjectSettingState(id, state);
        }
        finally { _operations.Release(); }
    }

    private static void ValidateSettingResult(string id, SettingState result)
    {
        if (result.Id != id || (result.EffectiveKnown && result.Effective is null))
            throw new NxException("protocol_mismatch", "Inconsistent backend setting acknowledgment.");
    }

    // Caller must hold _operations. Persisted preferences alone are not pending
    // mutations: an explicit runtime-only override can legitimately differ.
    private async Task<SettingState> ReadSettingStateUnlockedAsync(string id, CancellationToken ct)
    {
        var result = NxJson.Read<SettingState>(await CallSupportedAsync("settings.get", new { id }, ct));
        ValidateSettingResult(id, result);
        return ProjectSettingState(id, result);
    }

    private static bool IsSettingConsumed(SettingState state, JsonElement requested) =>
        state.Applied && state.EffectiveKnown && !state.RequiresRestart &&
        state.Effective is { } effective && JsonElement.DeepEquals(requested, effective);

    private SettingState ProjectSettingState(string id, SettingState state)
    {
        var observed = state with { Persisted = _persisted.TryGetValue(id, out var saved) ? saved : state.Persisted };
        if (!_deferredSettings.TryGetValue(id, out var requested)) return observed;
        if (IsSettingConsumed(observed, requested))
        {
            _deferredSettings.Remove(id);
            return observed;
        }
        return observed with
        {
            Requested = requested, Applied = false, RequiresRestart = true,
            Reason = "Requested for the next eligible load or runtime initialization; the active native value remains unchanged."
        };
    }

    private async Task<Dictionary<string, JsonElement>> ReadPersistedSettingsAsync(CancellationToken ct)
    {
        var staged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var path = Path.Combine(BackendDataRoot!, "nx-settings.json");
        SafePaths.RejectLinks(path);
        if (!File.Exists(path)) return staged;
        if (!Supports("settings.list") || !Supports("settings.set"))
            throw new NxException("settings_migration_required", "The selected adapter cannot review this profile's saved setting requests.");
        if (new FileInfo(path).Length > 1024 * 1024) throw new NxException("invalid_argument", "Settings file exceeds size limit.");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var root = doc.RootElement;
        if (root.GetProperty("backendId").GetString() != Backend.BackendId || root.GetProperty("revision").GetString() != Backend.BackendRevision)
            throw new NxException("settings_migration_required", "Saved settings belong to another core revision. Review them before importing into this core.");
        await EnsureSchemasAsync(ct);
        foreach (var property in root.GetProperty("values").EnumerateObject())
        {
            if (!_schema!.TryGetValue(property.Name, out var schema))
                throw new NxException("settings_migration_required", "Persisted settings include an unavailable operation.");
            schema.Validate(property.Value);
            if (schema.Apply == "before_initialize" && (schema.Id != "graphics.backend_threading" ||
                schema.Kind != "enum" || !Supports("initialize.renderer_threading")))
                throw new NxException("settings_migration_required", "A saved initialization setting has no supported constructor contract.");
            if (!staged.TryAdd(property.Name, property.Value.Clone()))
                throw new NxException("invalid_argument", "Duplicate persisted setting identifiers.");
        }
        return staged;
    }

    private async Task LoadPersistedSettingsAsync(IReadOnlyDictionary<string, JsonElement> staged, string? rendererThreading, CancellationToken ct)
    {
        try
        {
            if (Supports("initialize.renderer_threading"))
            {
                await EnsureSchemasAsync(ct);
                if (!_schema!.TryGetValue("graphics.backend_threading", out var threading) || threading.Apply != "before_initialize")
                    throw new NxException("protocol_mismatch", "Renderer initialization capability has no matching setting lifecycle.");
                var actual = NxJson.Read<SettingState>(await CallSupportedAsync("settings.get", new { id = threading.Id }, ct));
                ValidateSettingResult(threading.Id, actual);
                string expected = rendererThreading ?? threading.DefaultValue.GetString()
                    ?? throw new NxException("protocol_mismatch", "Renderer setting has no default constructor mode.");
                if (!actual.Applied || !actual.EffectiveKnown || actual.RequiresRestart || actual.Effective is not { ValueKind: JsonValueKind.String } effective || effective.GetString() != expected)
                    throw new NxException("effective_state_mismatch", "The native renderer did not report the requested constructor mode.");
            }
            foreach (var (id, value) in staged)
            {
                if (_schema![id].Apply != "before_initialize")
                {
                    var result = NxJson.Read<SettingState>(await CallSupportedAsync("settings.set", new { id, value }, ct));
                    ValidateSettingResult(id, result);
                }
                _persisted[id] = value;
            }
        }
        catch { State = RuntimeState.Faulted; throw; }
    }

    public async Task<TelemetrySnapshot> GetTelemetryAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        return NxJson.Read<TelemetrySnapshot>(await CallSupportedAsync("telemetry.get", null, cancellationToken));
    }

    public async Task SubmitInputAsync(InputSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Require(RuntimeState.Running, RuntimeState.Paused);
        snapshot.Validate();
        await CallSupportedAsync("input.update", snapshot, cancellationToken);
    }

    public Task<ContentInfo> InspectContentAsync(string contentPath, CancellationToken cancellationToken = default) =>
        InspectContentAsync(contentPath, includeArtwork: false, cancellationToken: cancellationToken);

    /// <summary>Explicit opt-in to an advertised backend namespace; never arbitrary internal calls.</summary>
    public Task<JsonElement> InvokeExtensionAsync(string operation, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (!operation.StartsWith(Backend.BackendId + ".", StringComparison.Ordinal))
            throw new NxException("invalid_argument", "Extension names must use the active backend's namespace.");
        return CallSupportedAsync(operation, parameters, cancellationToken);
    }

    public async IAsyncEnumerable<RuntimeEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _worker.Events.ReadAllAsync(cancellationToken)) yield return item;
    }

    /// <summary>May run concurrently with a blocked load/start. Never invokes unmanaged UI.</summary>
    public async Task RespondToInteractionAsync(InteractionResponse response, CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (string.IsNullOrWhiteSpace(response.InteractionId) || response.InteractionId.Length > 128 ||
            response.Text is { Length: > 4096 } || response.ButtonIndex is < 0 || response.CursorBegin is < 0 || response.CursorEnd is < 0 ||
            response.Generation is < 0 || response.CursorBegin > response.Text?.Length || response.CursorEnd > response.Text?.Length ||
            response.Key is not (null or "" or "enter" or "escape" or "backspace"))
            throw new NxException("invalid_argument", "Invalid or oversized interaction response.");
        await CallSupportedAsync("interaction.respond", response, cancellationToken);
    }

    public async Task<CommandLatency> MeasureCommandLatencyAsync(int samples = 25, CancellationToken cancellationToken = default)
    {
        if (samples is < 5 or > 1000) throw new NxException("invalid_argument", "Use 5..1000 latency samples.");
        EnsureUsable();
        var times = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await _worker.CallAsync("hello", null, _launch.CommandTimeout, cancellationToken);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(times);
        return new CommandLatency(times[samples / 2], times[(int)Math.Ceiling(samples * .95) - 1], samples,
            "Local command round-trip wall time; includes host dispatch. Does not measure guest FPS, GPU time, or gameplay overhead.");
    }

    /// <summary>Returns an allowlisted report. It excludes paths, raw logs, keys and content bytes.</summary>
    public async Task<string> CreateDiagnosticReportAsync(CancellationToken cancellationToken = default)
    {
        TelemetrySnapshot? telemetry = null;
        string? telemetryErrorCode = null;
        var settings = new List<object>();
        string? settingsErrorCode = null;
        if (Supports("telemetry.get") && !_disposed)
        {
            try { telemetry = await GetTelemetryAsync(cancellationToken); }
            catch (NxException e) { telemetryErrorCode = e.Code; }
        }
        if (Supports("settings.list") && Supports("settings.get") && !_disposed)
        {
            try
            {
                foreach (var schema in await GetSettingSchemasAsync(cancellationToken))
                {
                    // Free-form string settings may contain paths or credentials.
                    // Export only schema-validated numbers, booleans and enums.
                    if (schema.Kind is not ("boolean" or "integer" or "number" or "enum")) continue;
                    var state = await GetSettingAsync(schema.Id, cancellationToken);
                    JsonElement? SafeValue(JsonElement? value)
                    {
                        if (value is null) return null;
                        try { schema.Validate(value.Value); return value; }
                        catch (NxException) { return null; }
                    }
                    settings.Add(new { id = schema.Id, requested = SafeValue(state.Requested),
                        persisted = SafeValue(state.Persisted), effective = SafeValue(state.Effective),
                        state.EffectiveKnown, state.Applied, state.RequiresRestart, schema.Apply,
                        effectiveSource = state.EffectiveSource is { Length: > 0 and <= 128 } source &&
                            System.Text.RegularExpressions.Regex.IsMatch(source, @"^[A-Za-z0-9_.-]+$") ? source : null });
                }
            }
            catch (NxException e) { settingsErrorCode = e.Code; }
        }
        string? assemblySha256 = null;
        try
        {
            using var assembly = File.OpenRead(typeof(NxRuntime).Assembly.Location);
            assemblySha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(assembly));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        var graphics = telemetry is null ? null : GraphicsDeviceTelemetry.Read(telemetry);
        var shaders = telemetry is null ? null : ShaderCompilationTelemetry.Read(telemetry);
        // Do not serialize arbitrary backendMetrics or raw exceptions; either may expose paths.
        return JsonSerializer.Serialize(new
        {
            reportVersion = 2, createdUtc = DateTimeOffset.UtcNow, nxVersion = NxVersion.Sdk, nxAssemblySha256 = assemblySha256,
            backend = new { Backend.BackendId, Backend.BackendName, Backend.BackendRevision, Backend.AdapterVersion, Backend.Qualification,
                artifactManifestSha256 = BackendArtifactManifestSha256 },
            state = State, titleId = LoadedContent?.TitleId, gameBuildId = LoadedContent?.BuildId,
            gameDisplayVersion = LoadedContent?.DisplayVersion is { Length: <= 48 } version &&
                System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+(\.[0-9]+){0,3}([-+][A-Za-z0-9.-]{1,20})?$") ? version : null,
            platform = new { os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), cpuCount = Environment.ProcessorCount },
            gpu = graphics, driver = graphics?.Snapshot?.DriverIdentityKey ?? graphics?.Snapshot?.RendererVersionLabel, activeMods = (string[]?)null,
            telemetry = telemetry is null ? null : new { telemetry.EmulationFps, telemetry.FrameTimeMs, telemetry.FrameCount, telemetry.MemoryBytes, telemetry.SampleTimeUtc, telemetry.Source },
            telemetryErrorCode, shaderCompilation = shaders, settings, settingsErrorCode, droppedEvents = DroppedEventCount,
            shutdown = Shutdown is null ? null : new { Shutdown.Graceful, Shutdown.ForcedTermination },
            limitations = new[] { graphics?.Snapshot?.SelectedDeviceVerified == true ? "Selected GPU and native driver identity are observed; rendering correctness and sustained gameplay still require validation." : "Complete active GPU/driver identity is unavailable; native labels never manufacture numeric IDs.",
                "Active game-mod evidence requires adapter receipt correlation.", "Report remains local unless the caller explicitly shares it." }
        }, new JsonSerializerOptions(NxJson.Options) { WriteIndented = true });
    }

    private async Task<JsonElement> CallSupportedAsync(string operation, object? args, CancellationToken ct)
    {
        EnsureUsable();
        if (!Supports(operation)) throw new NxException("unsupported", $"{Backend.BackendId} does not expose {operation} at this revision.");
        var timeout = operation switch
        {
            "load" or "content.inspect" or "prerequisites.import" or "prerequisites.get" or
                "prerequisites.firmware.inspect" or "prerequisites.firmware.recover" => _launch.ContentTimeout ?? _launch.CommandTimeout,
            "initialize" => _launch.InitializationTimeout ?? _launch.CommandTimeout,
            _ => _launch.CommandTimeout
        };
        try { return await _worker.CallAsync(operation, args, timeout, ct); }
        catch { if (_worker.IsFaulted) State = RuntimeState.Faulted; throw; }
    }

    private void EnsureUsable()
    {
        if (_disposed || State == RuntimeState.Disposed) throw new NxException("invalid_state", "Session was disposed.");
        if (State == RuntimeState.Faulted || _worker.IsFaulted) throw new NxException("connection_faulted", "Create a fresh session after disposing the faulted worker.");
    }

    private void Require(params RuntimeState[] allowed)
    {
        EnsureUsable();
        if (!allowed.Contains(State)) throw new NxException("invalid_state", $"Operation is invalid while session is {State}.");
    }

    internal static async Task WriteJsonAtomicallyAsync(string path, object data, CancellationToken ct)
    {
        SafePaths.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, data, NxJson.Options, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            bool graceful = false, forced = false;
            string? failure = null;
            if (!_worker.HasExited && !_worker.IsFaulted)
            {
                try
                {
                    var result = await _worker.CallAsync("dispose", null, TimeSpan.FromSeconds(15), CancellationToken.None);
                    graceful = result.TryGetProperty("state", out var state) && state.GetString() == "disposed";
                    if (!graceful) failure = "Core did not acknowledge disposal.";
                }
                catch (Exception e) { failure = e is NxException ne ? ne.Code : e.GetType().Name; }
            }
            else if (_worker.IsFaulted) failure = "Connection faulted before disposal.";
            if (!await _worker.WaitForExitAsync(TimeSpan.FromSeconds(3)))
            {
                forced = true;
                graceful = false;
                if (!await _worker.TerminateAsync()) failure = "Worker termination could not be verified; data lease retained.";
            }
            var exited = _worker.HasExited;
            if (graceful && exited && _ownsMarker && _marker is not null)
            {
                if (!_initializeAcknowledged && _previousMarker is not null)
                {
                    var previous = _marker + ".restore-" + Guid.NewGuid().ToString("N");
                    await File.WriteAllBytesAsync(previous, _previousMarker);
                    File.Move(previous, _marker, overwrite: true);
                }
                else if (File.Exists(_marker)) File.Delete(_marker);
            }
            Shutdown = new ShutdownReport(graceful, forced, failure);
            _disposed = true;
            State = RuntimeState.Disposed;
            if (exited)
            {
                _clientLease?.Dispose();
                _clientLease = null;
                await _worker.DisposeAsync();
            }
        }
        finally { _operations.Release(); }
    }
}

