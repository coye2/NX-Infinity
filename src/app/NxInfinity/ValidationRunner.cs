using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NxInfinity.Runtime;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public static class ValidationRunner
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 32
    };

    private static readonly string[] KnownSettings = new string[8] { "audio.volume", "system.docked", "graphics.resolution_scale", "graphics.vsync", "graphics.max_anisotropy", "graphics.shader_cache", "graphics.texture_recompression", "cpu.ptc" };

    public static bool IsRequested(string[] args)
    {
        return args.Contains("--validate", StringComparer.Ordinal);
    }

    public static async Task<int> RunAsync(MainWindow window, InfinitySession session, string[] args, CancellationToken cancellationToken)
    {
        ValidationOptions options = null;
        BackendChoice selected = null;
        string manifestBefore = null;
        string manifestAfter = null;
        string appBefore = null;
        string appAfter = null;
        string sdkBefore = null;
        string sdkAfter = null;
        string processHash = null;
        string processHashAfter = null;
        string registeredHash = null;
        string finalSdk = null;
        BackendInfo observedBackend = null;
        ContentInfo identity = null;
        ShutdownReport shutdown = null;
        ValidationRunGuard runGuard = null;
        List<object> samples = new List<object>();
        List<string> failures = new List<string>();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Stopwatch elapsed = Stopwatch.StartNew();
        bool verified = false;
        bool launchAcknowledged = false;
        bool cleanupCompleted = false;
        string endReason = "validation_setup_failed";
        string validationPhase = "setup";
        int exit = 10;
        bool cleanupAttempted;
        try
        {
            _ = 9;
            try
            {
                options = ValidationOptions.Parse(args);
                if (!SamePath(options.DataRoot, session.Paths.DataRoot) || (options.InstallRoot != null && !SamePath(options.InstallRoot, session.Paths.InstallRoot)))
                {
                    throw new NxException("validation_paths_mismatch", "The validation arguments must match the actual frontend workspace.");
                }
                RefuseLinks(options.ReportPath);
                if (File.Exists(options.ReportPath) || Directory.Exists(options.ReportPath))
                {
                    throw new IOException("The requested report already exists.");
                }
                if (options.ContentPath != null)
                {
                    RefuseLinks(options.ContentPath);
                    if (!File.Exists(options.ContentPath))
                    {
                        throw new NxException("missing_content", "The explicitly selected local file is missing.");
                    }
                }
                using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(TimeSpan.FromSeconds(options.Seconds + 1200));
                using (bounded.Token.Register(session.Cancel))
                {
                    CancellationToken ct = bounded.Token;
                    window.Title = "NX Infinity · Local validation · " + options.Backend;
                    appBefore = await HashAssemblyFileAsync(typeof(ValidationRunner).Assembly, ct);
                    sdkBefore = await HashAssemblyFileAsync(typeof(NxRuntime).Assembly, ct);
                    processHash = await HashFileAsync(Environment.ProcessPath ?? throw new IOException("The running executable identity is unavailable."), ct, 268435456L);
                    await session.SelectBackendAsync(options.Backend);
                    CheckOperation(ct);
                    selected = session.SelectedBackend ?? throw new NxException("missing_dependency", "The requested backend is absent.");
                    if (selected.Id != options.Backend || Hex(selected.Revision, 40) == null)
                    {
                        throw new NxException("version_mismatch", "The selected backend identity is incomplete.");
                    }
                    manifestBefore = await HashFileAsync(selected.ManifestPath, ct, 4194304L);
                    validationPhase = "prerequisites";
                    await session.CheckPrerequisitesAsync();
                    CheckOperation(ct);
                    observedBackend = session.LastBackendInfo;
                    verified = Matches(observedBackend, selected);
                    if (!verified)
                    {
                        throw new NxException("version_mismatch", "The observed worker does not match the selected installation.");
                    }
                    ShutdownReport lastShutdown = session.LastShutdown;
                    if ((object)lastShutdown == null || !lastShutdown.Graceful || lastShutdown.ForcedTermination)
                    {
                        throw new NxException("unclean_shutdown", "The prerequisite worker did not shut down cleanly.");
                    }
                    runGuard = new ValidationRunGuard(session.LaunchGeneration, session.OperationFailureSequence);
                    if (options.Probe)
                    {
                        endReason = "probe_completed";
                    }
                    else
                    {
                        validationPhase = "registration";
                        await session.AddFilesAsync([options.ContentPath]);
                        CheckOperation(ct);
                        GameLibraryEntry gameLibraryEntry = session.Library.List(new LibraryQuery(null, FavoritesOnly: false, null, LibrarySort.Name, 0, 2000)).SingleOrDefault((GameLibraryEntry item) => SamePath(item.Content.AbsolutePath, options.ContentPath)) ?? throw new NxException("content_registration_failed", "The explicitly selected file was not registered.");
                        string entryId = gameLibraryEntry.EntryId;
                        registeredHash = Hex(gameLibraryEntry.Content.Sha256, 64);
                        string text = InfinitySession.TitleOf(gameLibraryEntry);
                        if (text != null && session.Profiles.Resolve(text).Backend.Id != options.Backend)
                        {
                            throw new NxException("validation_route_mismatch", "The game's stored core override differs from the requested test backend.");
                        }
                        window.RenderCurrentPage();
                        runGuard = new ValidationRunGuard(checked(session.LaunchGeneration + 1), session.OperationFailureSequence);
                        validationPhase = "launch";
                        await session.LaunchAsync(gameLibraryEntry.EntryId);
                        CheckOperation(ct);
                        identity = session.LoadedIdentity ?? session.LastLoadedIdentity;
                        observedBackend = session.ActiveBackend ?? session.LastBackendInfo;
                        if (!Matches(observedBackend, selected))
                        {
                            throw new NxException("version_mismatch", "The launch selected a different backend.");
                        }
                        launchAcknowledged = session.StartupSeconds.HasValue;
                        if (!launchAcknowledged)
                        {
                            throw new NxException("launch_not_acknowledged", "The frontend did not receive a completed start acknowledgement.");
                        }
                        runGuard = new ValidationRunGuard(session.LaunchGeneration, session.OperationFailureSequence);
                        Stopwatch runtimeElapsed = Stopwatch.StartNew();
                        validationPhase = "gameplay_observation";
                        endReason = "duration_completed";
                        TelemetrySnapshot previous = null;
                        while (runtimeElapsed.Elapsed.TotalSeconds < (double)options.Seconds)
                        {
                            CheckOperation(ct);
                            if ((object)identity == null)
                            {
                                identity = session.LoadedIdentity ?? session.LastLoadedIdentity;
                            }
                            if (!(session.SelectedBackendId != options.Backend))
                            {
                                GameLibraryEntry activeGame = session.ActiveGame;
                                if ((object)activeGame == null || !(activeGame.EntryId != entryId))
                                {
                                    RuntimeState? state = session.State;
                                    if (state.HasValue && state == RuntimeState.Faulted)
                                    {
                                        throw new NxException("core_exited", "The runtime entered a faulted state.");
                                    }
                                    bool flag;
                                    switch (session.State)
                                    {
                                        case null:
                                        case RuntimeState.Stopped:
                                        case RuntimeState.Disposed:
                                            flag = true;
                                            break;
                                        default:
                                            flag = false;
                                            break;
                                    }
                                    if (flag)
                                    {
                                        endReason = "session_ended";
                                        break;
                                    }
                                    await session.PollTelemetryAsync(ct);
                                    CheckOperation(ct);
                                    TelemetrySnapshot telemetry = session.Telemetry;
                                    if ((object)telemetry != null && (object)telemetry != previous && samples.Count < 3602)
                                    {
                                        samples.Add(SafeTelemetry(telemetry, runtimeElapsed.Elapsed.TotalSeconds));
                                        previous = telemetry;
                                    }
                                    await Task.Delay(TimeSpan.FromSeconds(1L), ct);
                                    continue;
                                }
                            }
                            throw new NxException("validation_session_changed", "A different game or backend was selected during this run.");
                        }
                        CheckOperation(ct);
                    }
                    exit = 0;
                }
            }
            catch (OperationCanceledException)
            {
                Failure("validation_canceled");
                endReason = "canceled";
                exit = 4;
            }
            catch (Exception ex2)
            {
                string? code;
                if (ex2 is NxException ex3)
                {
                    code = ex3.Code;
                }
                else if (ex2 is ServiceException { Code: var code2 })
                {
                    code = code2.ToString();
                }
                else
                {
                    code = ((ex2 is ArgumentException) ? "invalid_argument" : "validation_failed");
                }
                Failure(code);
                endReason = "validation_failed";
                exit = 3;
            }
        }
        finally
        {
            CaptureContinuityFailure();
            if ((object)identity == null)
            {
                identity = session.LoadedIdentity ?? session.LastLoadedIdentity;
            }
            if ((object)observedBackend == null)
            {
                observedBackend = session.ActiveBackend ?? session.LastBackendInfo;
            }
            cleanupAttempted = true;
            try
            {
                await session.StopAsync();
                string errorCode = session.ErrorCode;
                if (errorCode != null)
                {
                    Failure(errorCode);
                    exit = 3;
                }
                await session.DisposeAsync();
                CaptureContinuityFailure();
                shutdown = session.LastShutdown;
                finalSdk = session.GetLastSdkReport();
                cleanupCompleted = true;
                if (verified && ((object)shutdown == null || !shutdown.Graceful || shutdown.ForcedTermination))
                {
                    Failure("unclean_shutdown");
                    exit = 3;
                }
            }
            catch (Exception ex5)
            {
                Failure((ex5 is NxException ex6) ? ex6.Code : "cleanup_failed");
                exit = 3;
            }
            try
            {
                appAfter = await HashAssemblyFileAsync(typeof(ValidationRunner).Assembly, CancellationToken.None);
                sdkAfter = await HashAssemblyFileAsync(typeof(NxRuntime).Assembly, CancellationToken.None);
                string processPath = Environment.ProcessPath;
                if (processPath != null)
                {
                    processHashAfter = await HashFileAsync(processPath, CancellationToken.None, 268435456L);
                }
                if ((object)selected != null)
                {
                    manifestAfter = await HashFileAsync(selected.ManifestPath, CancellationToken.None, 4194304L);
                }
                if ((appBefore != null && appAfter != appBefore) || (sdkBefore != null && sdkAfter != sdkBefore) || (manifestBefore != null && manifestAfter != manifestBefore) || (processHash != null && processHashAfter != processHash))
                {
                    Failure("installation_changed");
                    exit = 3;
                }
            }
            catch
            {
                Failure("installation_integrity_unavailable");
                exit = 3;
            }
        }
        string runId = Guid.NewGuid().ToString("N");
        DateTimeOffset startedUtc = started;
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        string application = InfinityVersion.Application;
        string actualPlatform;
        if (OperatingSystem.IsWindows())
        {
            actualPlatform = "windows";
        }
        else
        {
            actualPlatform = (OperatingSystem.IsLinux() ? "linux" : "other");
        }
        var value = new
        {
            schemaVersion = 1,
            kind = "nx-infinity-local-validation",
            runId = runId,
            startedUtc = startedUtc,
            completedUtc = utcNow,
            application = "NX Infinity",
            version = application,
            sdkVersion = "0.3.2-dev",
            actualPlatform = actualPlatform,
            guiEventLoopUsed = true,
            automaticGameplayPass = false,
            requestedBackend = options?.Backend,
            mode = ((options?.Probe ?? false) ? "probe-only" : "explicit-user-content"),
            requestedSeconds = options?.Seconds,
            contentExplicitlySelected = (options?.ContentPath != null),
            registeredContentSha256 = registeredHash,
            appAssemblySha256 = appBefore,
            appAssemblySha256After = appAfter,
            processExecutableSha256 = processHash,
            processExecutableSha256After = processHashAfter,
            appAssemblyHashScope = ((appBefore == null) ? "individual-assembly-file-unavailable" : "individual-assembly-file"),
            sdkAssemblySha256 = sdkBefore,
            sdkAssemblySha256After = sdkAfter,
            sdkAssemblyHashScope = ((sdkBefore == null) ? "individual-assembly-file-unavailable" : "individual-assembly-file"),
            manifest = new
            {
                backendId = selected?.Id,
                revision = Hex(selected?.Revision, 40),
                qualification = Token(selected?.Qualification),
                sha256 = manifestBefore,
                sha256After = manifestAfter,
                unchanged = (manifestBefore != null && manifestBefore == manifestAfter)
            },
            installationVerification = new
            {
                method = "public-BackendInstallation-and-worker-handshake",
                succeeded = verified,
                observedBackendId = Token(observedBackend?.BackendId),
                observedRevision = Hex(observedBackend?.BackendRevision, 40),
                adapterVersion = Token(observedBackend?.AdapterVersion),
                qualification = Token(observedBackend?.Qualification)
            },
            loadedIdentity = SafeIdentity(identity),
            startupSeconds = Finite(session.StartupSeconds),
            elapsedSeconds = Finite(elapsed.Elapsed.TotalSeconds),
            launchAcknowledged = launchAcknowledged,
            endReason = endReason,
            exitStatus = exit,
            errorCodes = failures.ToArray(),
            launchPhases = session.LastLaunchPhases,
            validationPhase = validationPhase,
            windowCloseReason = window.ValidationCloseReason,
            externalCancellationRequested = cancellationToken.IsCancellationRequested,
            lastFailure = session.LastFailure,
            prerequisiteIssueCodes = (session.Prerequisites?.Issues.Select((ContentIssue issue) => Token(issue.Code)).Where((string text2) => text2 != null).Distinct()
                .Take(64)
                .ToArray() ?? Array.Empty<string>()),
            telemetrySamples = samples,
            telemetryScope = "Approximately one-second backend telemetry observations, not a per-present frame-time trace or upstream-overhead benchmark.",
            settings = ReadSettings(finalSdk),
            sdkReportSha256 = ((finalSdk == null) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(finalSdk)))),
            shutdown = new
            {
                cleanupAttempted = cleanupAttempted,
                cleanupCompleted = cleanupCompleted,
                graceful = shutdown?.Graceful,
                forcedTermination = shutdown?.ForcedTermination,
                failureCode = ((shutdown?.Failure == null) ? null : (Token(shutdown.Failure) ?? "sdk_shutdown_failure"))
            },
            claims = new
            {
                visualVerified = false,
                physicalAudioVerified = false,
                physicalControllerVerified = false,
                saveLoadVerified = false,
                commercialGameQualified = false,
                targetHardwareQualified = false,
                officialUpgradeParity = false
            },
            limitations = new string[6] { "Only this process's actual GUI, SDK and core observations are recorded.", "A probe never initializes a game, renderer or audio output.", "No automated input was injected by validation mode.", "Visual, audible, physical-controller and save/load observations require separate user reporting.", "Single-file publishing may make individual app/SDK assembly hashes unavailable; the process executable hash identifies the actual bundle.", "No raw logs, selected file paths, library notes, game bytes, keys or firmware are included." }
        };
        try
        {
            if ((object)options == null)
            {
                throw new ArgumentException("No valid report destination was supplied.");
            }
            RefuseLinks(options.ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath));
            await using FileStream output = new FileStream(options.ReportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync((Stream)output, value, Json, default(CancellationToken));
            await output.FlushAsync();
            output.Flush(flushToDisk: true);
        }
        catch
        {
            Console.Error.WriteLine("validation_report_failed: Existing reports are preserved.");
            return 10;
        }
        if (exit != 0)
        {
            Console.Error.WriteLine("validation_failed: " + string.Join(",", failures));
        }
        return exit;
        void CaptureContinuityFailure()
        {
            string text2 = runGuard?.Observe(session.LaunchGeneration, session.OperationFailureSequence);
            if (text2 != null)
            {
                Failure(text2);
                if (session.OperationFailureSequence != runGuard.OperationFailureSequence)
                {
                    Failure(session.LastOperationFailureCode);
                }
                exit = 3;
            }
            string errorCode2 = session.ErrorCode;
            if (errorCode2 != null)
            {
                Failure(errorCode2);
                exit = 3;
            }
            if (session.State == RuntimeState.Faulted)
            {
                Failure("core_exited");
                exit = 3;
            }
        }
        void CheckOperation(CancellationToken cancellationToken2)
        {
            cancellationToken2.ThrowIfCancellationRequested();
            string text2 = runGuard?.Observe(session.LaunchGeneration, session.OperationFailureSequence);
            if (text2 != null)
            {
                throw new NxException(text2, "The selected launch or its failure history changed during validation.");
            }
            string errorCode2 = session.ErrorCode;
            if (errorCode2 != null)
            {
                throw new NxException(Token(errorCode2) ?? "frontend_operation_failed", "The frontend operation failed; its private details stay in the local UI.");
            }
            if (session.State == RuntimeState.Faulted)
            {
                throw new NxException("core_exited", "The runtime entered a faulted state.");
            }
        }
        void Failure(string? value2)
        {
            string item = Token(value2) ?? "validation_failed";
            if (!failures.Contains(item))
            {
                failures.Add(item);
            }
        }
    }

    private static bool Matches(BackendInfo? info, BackendChoice choice)
    {
        if ((object)info != null && info.BackendId == choice.Id)
        {
            return string.Equals(info.BackendRevision, choice.Revision, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool SamePath(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string? Token(string? value)
    {
        if (value != null)
        {
            int length = value.Length;
            if (length > 0 && length <= 96 && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            {
                return value;
            }
        }
        return null;
    }

    private static string? Hex(string? value, int length)
    {
        if (value == null || value.Length != length || !value.All(Uri.IsHexDigit) || !value.Any((char c) => c != '0'))
        {
            return null;
        }
        return value.ToUpperInvariant();
    }

    private static double? Finite(double? value)
    {
        if (value.HasValue)
        {
            double valueOrDefault = value.GetValueOrDefault();
            if (double.IsFinite(valueOrDefault) && valueOrDefault >= 0.0)
            {
                return valueOrDefault;
            }
        }
        return null;
    }

    private static object? SafeIdentity(ContentInfo? value)
    {
        if ((object)value != null)
        {
            string? titleId = Hex(value.TitleId, 16);
            string? buildId = Hex(value.BuildId, 64);
            bool identityVerified = value.IdentityVerified;
            string? runtimeProgramId = Hex(value.RuntimeProgramId, 16);
            string? nacpTitleId = Hex(value.NacpTitleId, 16);
            string? buildIdSource = Token(value.BuildIdSource);
            string? format = Token(value.Format);
            string? kind = Token(value.Kind);
            string? nativeLoaderStatus = Token(value.NativeLoaderStatus);
            string displayVersion = value.DisplayVersion;
            return new
            {
                titleId = titleId,
                buildId = buildId,
                IdentityVerified = identityVerified,
                runtimeProgramId = runtimeProgramId,
                nacpTitleId = nacpTitleId,
                buildIdSource = buildIdSource,
                format = format,
                kind = kind,
                nativeLoaderStatus = nativeLoaderStatus,
                displayVersion = ((displayVersion != null && displayVersion.Length <= 48 && Regex.IsMatch(displayVersion, "^[0-9]+(\\.[0-9]+){0,3}([-+][A-Za-z0-9.-]{1,20})?$")) ? displayVersion : null)
            };
        }
        return null;
    }

    private static object SafeTelemetry(TelemetrySnapshot value, double elapsed)
    {
        double? elapsedSeconds = Finite(elapsed);
        DateTimeOffset? sampleTimeUtc = value.SampleTimeUtc;
        string? state = Token(value.State);
        string? source = Token(value.Source);
        double? emulationFps = Finite(value.EmulationFps);
        double? frameTimeMs = Finite(value.FrameTimeMs);
        long? frameCount = value.FrameCount;
        long? frameCount2 = ((frameCount.HasValue && frameCount.GetValueOrDefault() >= 0) ? value.FrameCount : ((long?)null));
        frameCount = value.MemoryBytes;
        return new
        {
            elapsedSeconds = elapsedSeconds,
            sampleTimeUtc = sampleTimeUtc,
            state = state,
            source = source,
            emulationFps = emulationFps,
            frameTimeMs = frameTimeMs,
            frameCount = frameCount2,
            memoryBytes = ((frameCount.HasValue && frameCount.GetValueOrDefault() >= 0) ? value.MemoryBytes : ((long?)null))
        };
    }

    private static object[] ReadSettings(string? report)
    {
        if (report == null || report.Length > 4194304)
        {
            return Array.Empty<object>();
        }
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(report, new JsonDocumentOptions
            {
                MaxDepth = 32
            });
            if (!jsonDocument.RootElement.TryGetProperty("settings", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<object>();
            }
            return (from row in value.EnumerateArray().Take(128)
                    where row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out var value2) && value2.ValueKind == JsonValueKind.String && KnownSettings.Contains(value2.GetString(), StringComparer.Ordinal)
                    select row).Select((Func<JsonElement, object>)((JsonElement row) => new
                    {
                        id = row.GetProperty("id").GetString(),
                        requested = Scalar(row, "requested"),
                        persisted = Scalar(row, "persisted"),
                        effective = Scalar(row, "effective"),
                        effectiveKnown = Scalar(row, "effectiveKnown"),
                        applied = Scalar(row, "applied"),
                        requiresRestart = Scalar(row, "requiresRestart")
                    })).ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<object>();
        }
        static object? Scalar(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out var value2))
            {
                return null;
            }
            switch (value2.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String:
                    return Token(value2.GetString());
                case JsonValueKind.Number:
                    {
                        if (value2.TryGetDouble(out var value3) && double.IsFinite(value3))
                        {
                            return value3;
                        }
                        break;
                    }
            }
            return null;
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct, long maximum = 268435456L)
    {
        RefuseLinks(path);
        string result;
        await using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (file.Length <= 0 || file.Length > maximum)
            {
                throw new IOException("The selected installation file exceeds the validation bound.");
            }
            result = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        }
        return result;
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty bundled assembly path is explicitly reported as unavailable; it is never treated as an individual file hash.")]
    private static async Task<string?> HashAssemblyFileAsync(Assembly assembly, CancellationToken ct)
    {
        return (!string.IsNullOrWhiteSpace(assembly.Location)) ? (await HashFileAsync(assembly.Location, ct, 268435456L)) : null;
    }

    private static void RefuseLinks(string path)
    {
        for (string text = Path.GetFullPath(path); text != null; text = Path.GetDirectoryName(text))
        {
            if ((File.Exists(text) || Directory.Exists(text)) && (File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Validation inputs and reports cannot use linked paths.");
            }
        }
    }
}
