using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NxInfinity.Runtime.Services;

public sealed partial class SharedPrerequisiteStore
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "pending", "applying", "verified", "imported", "imported_unusable", "unsupported",
        "failed", "deferred", "recovery_required", "verification_failed", "cancelled",
        "interrupted", "rollback_unavailable"
    };
    private static readonly HashSet<string> KnownIssueCodes = new(StringComparer.Ordinal)
    {
        "keys_not_installed", "keys_missing", "header_key_missing", "header_key_unavailable",
        "header_key_parsed", "key_coverage_unverified", "firmware_missing", "firmware_version_unknown",
        "installed_version_archive", "restart_required", "firmware_unreadable", "keys_unreadable",
        "firmware_package_integrity_verified", "firmware_package_validation_failed", "firmware_receipt_mismatch"
    };
    private static readonly HashSet<string> KnownErrorCodes = new(StringComparer.Ordinal)
    {
        "unsupported", "invalid_argument", "invalid_state", "data_in_use", "missing_dependency", "io_error",
        "core_error", "version_mismatch", "protocol_mismatch", "timeout", "cancelled", "worker_exited",
        "unqualified_backend", "recovery_required", "unclean_shutdown", "not_found", "native_error",
        "unsafe_path", "stored_source_invalid", "launch_invalid", "interrupted", "core_exited", "connection_faulted",
        "unsupported_platform"
    };
    private static readonly HashSet<string> RollbackStates = new(StringComparer.Ordinal)
    {
        "not_imported", "previous_shared_revision_available", "native_backup_only", "restored_shared_revision",
        "unavailable_unmanaged_or_different_baseline", "no_previous_shared_revision", "unknown"
    };
    private static readonly string[] RecoveryMarkers = [".nx-unclean.json", ".nx-save-transaction.json",
        ".nx-firmware-transaction.json", ".nx-firmware-transaction.json.new", ".nx-mod-transaction.json", ".nx-content-transaction.json"];

    private sealed record BoundTarget(BackendLaunch Launch, string WorkspaceBase, string ProfileId,
        string ProfileRoot, string Fingerprint, string PrivateIdentity);

    public async Task<SharedPrerequisiteProvisionReport> ProvisionAsync(IEnumerable<SharedPrerequisiteTarget> targets,
        SharedPrerequisiteProvisionMode mode = SharedPrerequisiteProvisionMode.Pending,
        IProgress<SharedPrerequisiteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ServiceException(ServiceError.InvalidInput, "The prerequisite provision mode is invalid.");
        var bound = BindTargets(targets);
        using var lease = _workspace.Acquire("shared-prerequisites");
        var state = ReadState();
        return await ProvisionCoreAsync(state, SelectedSources(state), bound, mode, progress, cancellationToken);
    }

    public async Task<SharedPrerequisiteProvisionReport> RollbackAsync(string revisionId,
        IEnumerable<SharedPrerequisiteTarget> targets, IProgress<SharedPrerequisiteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        revisionId = Ids.GuidId(revisionId);
        var bound = BindTargets(targets);
        using var lease = _workspace.Acquire("shared-prerequisites");
        var state = ReadState();
        var current = ReadSource(revisionId);
        if (state.ActiveSources.GetValueOrDefault(current.Receipt.Slot) != revisionId)
            throw new ServiceException(ServiceError.Conflict, "That source revision is no longer selected. Review the current source before rollback.");
        if (current.Receipt.PreviousRevisionId is null)
        {
            var unavailable = bound.Select(t => NewOutcome(t, current) with
            {
                Status = "rollback_unavailable", RollbackStatus = "no_previous_shared_revision"
            }).Select(PublicOutcome).ToArray();
            return Report(unavailable, false);
        }
        var previous = ReadSource(current.Receipt.PreviousRevisionId);
        if (previous.Receipt.Kind != current.Receipt.Kind || previous.Receipt.Slot != current.Receipt.Slot)
            throw new ServiceException(ServiceError.CorruptData, "The previous source belongs to another prerequisite slot.");
        cancellationToken.ThrowIfCancellationRequested();
        VerifySource(previous);
        // Restore the retained-source selection only. Native target mutation below still
        // requires an exact successful prior shared receipt and the normal profile leases.
        state.ActiveSources[current.Receipt.Slot] = previous.Receipt.Id;
        WriteState(state);
        return await ProvisionCoreAsync(state, [previous], bound, SharedPrerequisiteProvisionMode.Reapply,
            progress, cancellationToken, rollbackFrom: revisionId);
    }

    private async Task<SharedPrerequisiteProvisionReport> ProvisionCoreAsync(State state, SourceRecord[] sources,
        BoundTarget[] targets, SharedPrerequisiteProvisionMode mode, IProgress<SharedPrerequisiteProgress>? progress,
        CancellationToken ct, string? rollbackFrom = null)
    {
        var outcomes = new List<SharedPrerequisiteOutcome>();
        var verifiedSources = new HashSet<string>(StringComparer.Ordinal);
        bool cancelled = false;
        for (int index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            FileStream? clientLease = null;
            string? profileBlock = null;
            try
            {
                foreach (var source in sources)
                {
                    var key = RecordKey(target, source.Receipt.Slot);
                    state.Targets.TryGetValue(key, out var previousRecord);
                    var previous = previousRecord?.Outcome;
                    bool sameRevision = previous?.SourceRevisionId == source.Receipt.Id;
                    if (rollbackFrom is not null && !(previous?.SourceRevisionId == rollbackFrom &&
                        previous.NativeImportAccepted && previousRecord!.PreviousSourceRevisionId == source.Receipt.Id))
                    {
                        var blocked = PublicOutcome(NewOutcome(target, source) with { Status = "rollback_unavailable",
                            RollbackStatus = "unavailable_unmanaged_or_different_baseline" });
                        state.Targets[key] = new(blocked, previousRecord?.PreviousSourceRevisionId);
                        WriteState(state); outcomes.Add(blocked); continue;
                    }
                    if (mode == SharedPrerequisiteProvisionMode.RetryFailures &&
                        (previous is null || SuccessStates.Contains(previous.Status) || previous.Status == "pending"))
                    {
                        outcomes.Add(PublicOutcome(sameRevision ? previous! with { Attempted = false,
                            AlreadyProvisioned = SuccessStates.Contains(previous!.Status) } : NewOutcome(target, source)));
                        continue;
                    }
                    if (mode == SharedPrerequisiteProvisionMode.Pending && sameRevision && previous!.Status != "deferred")
                    {
                        outcomes.Add(PublicOutcome(previous with { Attempted = false,
                            AlreadyProvisioned = SuccessStates.Contains(previous.Status),
                            Status = previous.Status == "applying" ? "interrupted" : previous.Status }));
                        continue;
                    }
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        outcomes.Add(PublicOutcome(NewOutcome(target, source) with { Status = "cancelled", ErrorCode = "cancelled" }));
                        continue;
                    }
                    var basis = NewOutcome(target, source);
                    string? priorManaged = previousRecord is not null && SuccessStates.Contains(previousRecord.Outcome.Status)
                        ? previousRecord.Outcome.SourceRevisionId : previousRecord?.PreviousSourceRevisionId;
                    if (priorManaged == source.Receipt.Id) priorManaged = previousRecord?.PreviousSourceRevisionId;
                    SharedPrerequisiteOutcome outcome;
                    bool importDispatched = false;
                    bool importAccepted = false;
                    bool recoveryDispatched = false;
                    try
                    {
                        Progress("checking_profile");
                        if (clientLease is null && profileBlock is null)
                        {
                            try { clientLease = AcquireProfile(target, allowFirmwareRecovery:
                                mode == SharedPrerequisiteProvisionMode.RetryFailures && target.Launch.BackendId is "eden" or "citron"); }
                            catch (ServiceException error) when (error.Code is ServiceError.Busy or ServiceError.RecoveryRequired)
                            { profileBlock = error.Code == ServiceError.Busy ? "deferred" : "recovery_required"; }
                        }
                        if (profileBlock is not null)
                        {
                            outcome = basis with { Status = profileBlock, ErrorCode = profileBlock == "deferred" ? "data_in_use" : "recovery_required" };
                        }
                        else
                        {
                            if (!verifiedSources.Contains(source.Receipt.Id))
                            {
                                VerifySource(source);
                                verifiedSources.Add(source.Receipt.Id);
                            }
                            ct.ThrowIfCancellationRequested();
                            Progress("connecting");
                            FirmwarePackageValidation? expectedFirmware = null;
                            PrerequisiteReport? importedReport = null;
                            await using (var runtime = await NxRuntime.ConnectAsync(target.Launch, ct))
                            {
                                basis = basis with { AdapterVersion = SafeAdapter(runtime.Backend.AdapterVersion) };
                                if (HasFirmwareTransaction(target))
                                {
                                    if (mode != SharedPrerequisiteProvisionMode.RetryFailures || !runtime.Supports("prerequisites.firmware.recover"))
                                        throw new NxException("recovery_required", "This exact core has no supported native recovery route for the pending firmware transaction.");
                                    Progress("recovering_firmware");
                                    ct.ThrowIfCancellationRequested();
                                    recoveryDispatched = true;
                                    await runtime.RecoverFirmwareForProfileAsync(target.WorkspaceBase, target.ProfileId, ct);
                                    RequireProfileClean(target);
                                    ct.ThrowIfCancellationRequested();
                                }
                                if (!runtime.Supports("prerequisites.import"))
                                    throw new NxException("unsupported", "Native prerequisite import is unavailable.");
                                using var preparedSource = await PrepareFirmwareSourceAsync(source, runtime, ct);
                                if (source.Receipt.Kind == "firmware" && runtime.Supports("prerequisites.firmware.inspect"))
                                {
                                    if (!runtime.Supports("prerequisites.import.firmware"))
                                        throw new NxException("unsupported", "This exact core does not expose native firmware import.");
                                    Progress("validating_firmware_source");
                                    expectedFirmware = await runtime.InspectFirmwareForProfileAsync(preparedSource.Source,
                                        target.WorkspaceBase, target.ProfileId, ct);
                                }
                                // A normalized directory identifies the inspected package;
                                // its retained source must still be the selected revision.
                                VerifySource(source);
                                // An older umbrella-only importer can still be used, but
                                // a readable version alone never becomes package verification.
                                state.Targets[key] = new(basis with { Status = "applying", Attempted = true }, priorManaged);
                                WriteState(state);
                                Progress("importing");
                                ct.ThrowIfCancellationRequested();
                                importDispatched = true;
                                importedReport = expectedFirmware is null
                                    ? await runtime.ImportPrerequisiteForProfileAsync(source.Receipt.Kind, preparedSource.Source,
                                        target.WorkspaceBase, target.ProfileId, ct)
                                    : await runtime.ImportFirmwareForProfileAsync(preparedSource.Source, target.WorkspaceBase,
                                        expectedFirmware.ToExpectation(), target.ProfileId, ct);
                                importAccepted = true;
                            }
                            Progress("verifying_fresh_worker");
                            await using var observer = await NxRuntime.ConnectAsync(target.Launch, ct);
                            if (SafeAdapter(observer.Backend.AdapterVersion) != basis.AdapterVersion)
                                throw new NxException("version_mismatch", "Fresh-worker adapter identity changed.");
                            var observation = await observer.GetPrerequisitesForProfileAsync(target.WorkspaceBase, target.ProfileId, ct);
                            ct.ThrowIfCancellationRequested();
                            // The native commit may already have happened. A changed
                            // retained source makes acknowledgment uncertain, never verified.
                            VerifySource(source);
                            var version = SafeFirmware(observation.FirmwareVersion);
                            var firmware = observation.FirmwareValidation;
                            bool firmwareBound = expectedFirmware is not null &&
                                expectedFirmware.ToExpectation().Matches(importedReport?.FirmwareValidation) &&
                                expectedFirmware.ToExpectation().Matches(firmware) &&
                                importedReport!.FirmwareValidation!.InstalledInventoryVerified && firmware!.InstalledInventoryVerified &&
                                importedReport.FirmwareValidation.ReceiptId == firmware.ReceiptId;
                            string status = source.Receipt.Kind == "keys"
                                ? source.Receipt.Slot == "prod.keys" ? observation.HeaderKeyUsable ? "verified" : "imported_unusable" : "imported"
                                : expectedFirmware is not null ? firmwareBound ? "verified" : "verification_failed"
                                : version is not null ? "imported" : "verification_failed";
                            outcome = basis with { Status = status, Attempted = true, NativeImportAccepted = true,
                                FreshWorkerObserved = true, KeysPresent = observation.KeysPresent,
                                HeaderKeyUsable = observation.HeaderKeyUsable, FirmwareVersion = version,
                                FirmwareValidationScope = firmwareBound ? firmware!.Scope : null,
                                FirmwareArchiveCount = firmwareBound ? firmware!.ArchiveCount : null,
                                FirmwareMetadataCount = firmwareBound ? firmware!.MetadataCount : null,
                                FirmwareReceiptId = firmwareBound ? firmware!.ReceiptId : null,
                                FirmwarePackageComplete = firmwareBound ? true : null,
                                FirmwareInstalledInventoryVerified = firmwareBound ? true : null,
                                FirmwareSignatureAuthenticated = firmwareBound ? false : null,
                                FirmwareRetailQualified = firmwareBound ? false : null,
                                IssueCodes = SafeIssues(expectedFirmware is not null && !firmwareBound
                                    ? observation.Issues.Append(new ContentIssue("firmware_receipt_mismatch", "error", "Firmware source/import/fresh receipt binding failed."))
                                    : observation.Issues), ObservedUtc = DateTimeOffset.UtcNow,
                                RollbackStatus = rollbackFrom is not null && SuccessStates.Contains(status) ? "restored_shared_revision"
                                    : priorManaged is not null ? "previous_shared_revision_available" : "native_backup_only" };
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        outcome = basis with { Status = importDispatched || recoveryDispatched ? "interrupted" : "cancelled", ErrorCode = "cancelled",
                            Attempted = importDispatched, NativeImportAccepted = importAccepted,
                            RollbackStatus = priorManaged is not null ? "previous_shared_revision_available" : "unknown" };
                    }
                    catch (NxException error)
                    {
                        var code = KnownErrorCodes.Contains(error.Code) ? error.Code : "native_error";
                        if (code == "cancelled") cancelled = true;
                        var status = code is "unsupported" or "unsupported_platform" ? "unsupported"
                            : code == "recovery_required" ? "recovery_required"
                            : code == "cancelled" ? importDispatched || recoveryDispatched ? "interrupted" : "cancelled"
                            : importAccepted ? "verification_failed"
                            : (code is "timeout" or "worker_exited" or "core_exited" or "connection_faulted" or "io_error" or "protocol_mismatch") &&
                                (importDispatched || recoveryDispatched) ? "interrupted" : "failed";
                        outcome = basis with { Status = status, ErrorCode = code, Attempted = importDispatched,
                            NativeImportAccepted = importAccepted, RollbackStatus = importAccepted
                                ? priorManaged is not null ? "previous_shared_revision_available" : "native_backup_only"
                                : status == "interrupted" ? "unknown" : "not_imported" };
                    }
                    catch (ServiceException error)
                    {
                        outcome = basis with { Status = importAccepted ? "verification_failed"
                            : importDispatched || recoveryDispatched ? "interrupted"
                            : error.Code == ServiceError.Busy ? "deferred"
                            : error.Code == ServiceError.RecoveryRequired ? "recovery_required" : "failed",
                            ErrorCode = error.Code == ServiceError.Busy ? "data_in_use"
                                : error.Code == ServiceError.RecoveryRequired ? "recovery_required" : "stored_source_invalid",
                            Attempted = importDispatched, NativeImportAccepted = importAccepted,
                            RollbackStatus = importAccepted
                                ? priorManaged is not null ? "previous_shared_revision_available" : "native_backup_only"
                                : importDispatched || recoveryDispatched ? "unknown" : "not_imported" };
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        outcome = basis with { Status = importDispatched || recoveryDispatched ? "interrupted" : "failed", ErrorCode = "io_error",
                            Attempted = importDispatched, NativeImportAccepted = importAccepted };
                    }
                    outcome = PublicOutcome(outcome);
                    state.Targets[key] = new(outcome, priorManaged); WriteState(state);
                    outcomes.Add(outcome);
                    Progress(outcome.Status);
                    if (cancelled) profileBlock = "cancelled";

                    void Progress(string stage) => progress?.Report(new(target.Launch.BackendId,
                        source.Receipt.Kind, source.Receipt.Slot, stage, index, targets.Length));
                }
            }
            finally
            {
                clientLease?.Dispose();
                progress?.Report(new(target.Launch.BackendId, "", "", "target_complete", index + 1, targets.Length));
            }
        }
        return Report(outcomes, cancelled);
    }

    private SharedPrerequisiteProvisionReport Report(IEnumerable<SharedPrerequisiteOutcome> outcomes, bool cancelled) =>
        new(Guid.NewGuid().ToString("N"), Array.AsReadOnly(outcomes.Select(PublicOutcome).ToArray()), cancelled,
            DateTimeOffset.UtcNow, RollbackNotice);

    private string SourcePayload(SourceRecord source) => Paths.Under(Paths.Under(SourceDirectory(source.Receipt.Id), "payload"), source.PayloadEntry);
    private void VerifySource(SourceRecord source) => StoreIo.VerifyTree(Paths.Under(SourceDirectory(source.Receipt.Id), "payload"), source.Files, _workspace.Limits);
    private static string RecordKey(BoundTarget target, string slot) => HashText(target.PrivateIdentity + "\n" + slot);

    private static bool IsFirmwareMarker(string marker) => marker is ".nx-firmware-transaction.json" or ".nx-firmware-transaction.json.new";
    private static bool MarkerExists(BoundTarget target, string marker)
    {
        var path = Paths.Under(target.ProfileRoot, marker);
        return File.Exists(path) || Directory.Exists(path);
    }
    private static bool HasFirmwareTransaction(BoundTarget target) => RecoveryMarkers.Any(marker => IsFirmwareMarker(marker) && MarkerExists(target, marker));
    private static void RequireProfileClean(BoundTarget target)
    {
        if (RecoveryMarkers.Any(marker => MarkerExists(target, marker)))
            throw new ServiceException(ServiceError.RecoveryRequired, "The target still contains an interrupted or unclean operation.");
    }

    private static FileStream AcquireProfile(BoundTarget target, bool allowFirmwareRecovery = false)
    {
        Paths.RejectLinks(target.ProfileRoot);
        Directory.CreateDirectory(target.ProfileRoot);
        var lease = StoreIo.AcquireLock(Paths.Under(target.ProfileRoot, ".nx-client.lock"));
        try
        {
            foreach (var marker in RecoveryMarkers)
                if (MarkerExists(target, marker) && !(allowFirmwareRecovery && IsFirmwareMarker(marker)))
                    throw new ServiceException(ServiceError.RecoveryRequired, "The target profile has an interrupted or unclean operation requiring review.");
            // Do not hold the worker's lease while asking that worker to import.
            // The retained client lease blocks SDK initialization, and the native
            // import obtains its own authoritative lease for the complete mutation.
            using (StoreIo.AcquireLock(Paths.Under(target.ProfileRoot, ".nx-runtime.lock"))) { }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static BoundTarget[] BindTargets(IEnumerable<SharedPrerequisiteTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var list = targets.Take(65).ToArray();
        if (list.Length > 64) throw new ServiceException(ServiceError.InvalidInput, "Provision at most 64 explicitly identified core profiles at a time.");
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return list.Select(target =>
        {
            if (target?.Launch is null) throw new ServiceException(ServiceError.InvalidInput, "Each target requires an exact installed-core launch.");
            var original = target.Launch;
            Ids.Slug(original.BackendId); Ids.Slug(target.ProfileId);
            var revision = Ids.Hex(original.Revision, 40, "Backend revision").ToLowerInvariant();
            if (revision.All(c => c == '0')) throw new ServiceException(ServiceError.InvalidInput, "An unknown backend revision cannot provision a shared source.");
            var executable = Absolute(original.ExecutablePath);
            if (!File.Exists(executable)) throw new ServiceException(ServiceError.Unavailable, "The selected installed-core executable is unavailable.");
            string executableHash;
            using (var stream = File.OpenRead(executable)) executableHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (original.ExecutableSha256 is not null && !Ids.Hex(original.ExecutableSha256, 64, "Executable hash").Equals(executableHash, StringComparison.OrdinalIgnoreCase))
                throw new ServiceException(ServiceError.Conflict, "The installed executable differs from its selected artifact binding.");
            var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (original.ArtifactSha256 is null || original.Arguments is null || original.Arguments.Any(x => x is null))
                throw new ServiceException(ServiceError.InvalidInput, "The installed-core invocation is invalid.");
            foreach (var artifact in original.ArtifactSha256)
            {
                var artifactPath = Absolute(artifact.Key);
                var expected = Ids.Hex(artifact.Value, 64, "Artifact hash").ToLowerInvariant();
                if (!File.Exists(artifactPath))
                    throw new ServiceException(ServiceError.Unavailable, "An installed-core artifact is unavailable.");
                using var stream = File.OpenRead(artifactPath);
                var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (!actual.Equals(expected, StringComparison.Ordinal))
                    throw new ServiceException(ServiceError.Conflict, "An installed-core artifact differs from its selected binding.");
                artifacts.Add(artifactPath, expected);
            }
            var workingDirectory = original.WorkingDirectory is null ? Path.GetDirectoryName(executable)! : Absolute(original.WorkingDirectory);
            var launch = original with { Revision = revision, ExecutablePath = executable, ExecutableSha256 = executableHash,
                Arguments = original.Arguments.ToArray(), ArtifactSha256 = artifacts, WorkingDirectory = workingDirectory };
            var fingerprint = HashText(JsonSerializer.Serialize(new { launch.BackendId, revision, executableHash,
                workingDirectory, arguments = launch.Arguments, artifacts = artifacts.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() }));
            var workspaceBase = Absolute(target.WorkspaceBase);
            var profile = Paths.Under(workspaceBase, "backends/" + launch.BackendId + "/" + target.ProfileId);
            if (!seen.Add(profile)) throw new ServiceException(ServiceError.InvalidInput, "The batch contains duplicate or conflicting target profile paths.");
            var canonical = OperatingSystem.IsWindows() ? profile.ToUpperInvariant() : profile;
            return new BoundTarget(launch, workspaceBase, target.ProfileId, profile, fingerprint,
                HashText(launch.BackendId + "\n" + revision + "\n" + fingerprint + "\n" + canonical));
        }).ToArray();
    }

    private static SharedPrerequisiteOutcome NewOutcome(BoundTarget target, SourceRecord source) => new()
    {
        BackendId = target.Launch.BackendId, BackendRevision = target.Launch.Revision,
        InstallationFingerprint = target.Fingerprint, ProfileId = target.ProfileId,
        Kind = source.Receipt.Kind, Slot = source.Receipt.Slot, SourceRevisionId = source.Receipt.Id
    };

    private static string SafeAdapter(string? value) => value is not null && value.Length <= 64 &&
        Regex.IsMatch(value, @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}(?:[-+][0-9A-Za-z.-]{1,48})?$", RegexOptions.CultureInvariant)
        ? value : "unknown";
    private static string? SafeFirmware(string? value) => value is not null && value.Length <= 48 &&
        Regex.IsMatch(value, @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}(?:[-+][0-9A-Za-z.-]{1,32})?$", RegexOptions.CultureInvariant)
        ? value : null;
    private static IReadOnlyList<string> SafeIssues(IEnumerable<ContentIssue>? issues) => Array.AsReadOnly(
        (issues ?? []).Where(x => x is not null && KnownIssueCodes.Contains(x.Code)).Select(x => x.Code)
        .Distinct(StringComparer.Ordinal).Take(32).ToArray());

    private static SharedPrerequisiteOutcome PublicOutcome(SharedPrerequisiteOutcome outcome)
    {
        var status = outcome.Status == "applying" ? "interrupted" : outcome.Status;
        bool firmwareVerified = outcome.Kind == "firmware" && status == "verified" &&
            outcome.NativeImportAccepted && outcome.FreshWorkerObserved &&
            outcome.FirmwareValidationScope == FirmwarePackageValidation.NativeIntegrityScope &&
            outcome.FirmwareArchiveCount is >= 3 and <= 4096 && outcome.FirmwareMetadataCount >= 2 &&
            outcome.FirmwareMetadataCount <= outcome.FirmwareArchiveCount &&
            outcome.FirmwarePackageComplete == true && outcome.FirmwareInstalledInventoryVerified == true &&
            outcome.FirmwareSignatureAuthenticated == false && outcome.FirmwareRetailQualified == false &&
            outcome.FirmwareReceiptId is { Length: 32 } receipt && Regex.IsMatch(receipt, "^[0-9a-f]{32}$");
        // Older saved outcomes used any readable version as "verified". Keep the
        // import acknowledgement while removing that overbroad historical claim.
        if (outcome.Kind == "firmware" && status == "verified" && !firmwareVerified) status = "imported";
        return outcome with
        {
            Status = status,
            AdapterVersion = SafeAdapter(outcome.AdapterVersion),
            ErrorCode = outcome.ErrorCode is not null && KnownErrorCodes.Contains(outcome.ErrorCode) ? outcome.ErrorCode : null,
            FirmwareVersion = SafeFirmware(outcome.FirmwareVersion),
            FirmwareValidationScope = firmwareVerified ? FirmwarePackageValidation.NativeIntegrityScope : null,
            FirmwareArchiveCount = firmwareVerified ? outcome.FirmwareArchiveCount : null,
            FirmwareMetadataCount = firmwareVerified ? outcome.FirmwareMetadataCount : null,
            FirmwareReceiptId = firmwareVerified ? outcome.FirmwareReceiptId : null,
            FirmwarePackageComplete = firmwareVerified ? true : null,
            FirmwareInstalledInventoryVerified = firmwareVerified ? true : null,
            FirmwareSignatureAuthenticated = firmwareVerified ? false : null,
            FirmwareRetailQualified = firmwareVerified ? false : null,
            RollbackStatus = RollbackStates.Contains(outcome.RollbackStatus) ? outcome.RollbackStatus : "unknown",
            IssueCodes = Array.AsReadOnly(outcome.IssueCodes.Where(KnownIssueCodes.Contains).Distinct(StringComparer.Ordinal).Take(32).ToArray()),
            ObservationScope = firmwareVerified
                ? "Exact native source identity, import receipt and fresh-worker archive inventory match. Package integrity/completeness only; Nintendo signature authentication and retail gameplay are unverified."
                : "Last local native prerequisite observation only; package completeness, each game's key coverage and firmware requirements remain unverified.",
            Message = status switch
            {
                "verified" => outcome.Kind == "keys" ? "The native importer accepted this file and a fresh worker parsed a nonzero header key. Game-specific key coverage remains unverified."
                    : "Native validation checked the complete referenced package and its archive hashes. The import receipt and a fresh worker match that exact source, version and inventory. Game requirements and retail behavior remain unverified.",
                "imported" => outcome.Kind == "keys"
                    ? "The native importer accepted this key file and a fresh worker checked the profile. This does not verify every key or encrypted title."
                    : "The native importer accepted the firmware source and a fresh worker read an installed version. This core did not bind complete package validation to the selected source; completeness and integrity are unverified.",
                "imported_unusable" => "The native importer accepted the production key file, but a fresh worker found no usable header key. Review the source or restore the prior shared revision.",
                "unsupported" => outcome.ErrorCode == "unsupported_platform"
                    ? "This firmware targets an unsupported platform. Switch 2/Ounce firmware requires platform functionality this Switch core does not provide. The local source remains retained."
                    : "This exact core does not support the requested prerequisite kind, package variant or operation. The shared source remains retained for supported or future cores.",
                "deferred" => "This core profile is in use. Its prerequisites were not changed; provision again after the active session closes.",
                "recovery_required" => "This profile has an unclean session or interrupted transaction. Review recovery before applying shared prerequisites.",
                "verification_failed" => "The native import was accepted, but a fresh worker could not verify the resulting prerequisite state. Success is not claimed.",
                "cancelled" => "Provisioning was cancelled before a native import was dispatched for this target.",
                "interrupted" => "An import may have been interrupted. Its effective state is unknown; review the profile before an explicit reapply or rollback.",
                "rollback_unavailable" => "No exact prior shared-source baseline is available for this target. Unmanaged setup was not restored or removed.",
                "failed" => "The requested native import or stored-source validation failed. Inspect the bounded error code and selected local source; no success is claimed.",
                _ => "No import has been attempted for this shared revision and exact installed core."
            }
        };
    }
}
