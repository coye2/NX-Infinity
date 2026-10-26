using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NxInfinity.Runtime.Services;

public sealed partial class SaveMigrationStore
{
    private sealed record VerifiedSource(string SourceId, string Origin, string OriginalDirectory,
        string CloneDirectory, SaveDirectoryMapping Mapping, string AdapterVersion,
        IReadOnlyList<ContentFile> Files, IReadOnlyList<string> Directories, string Fingerprint);
    private sealed record BoundMigrationTarget(SaveMigrationTarget Target, BackendLaunch Launch,
        string ProfileId, string TitleId, string UserId);

    private async Task<SaveMigrationReport> AutoImportPlainAsync(IEnumerable<SaveMigrationTarget> targets,
        SaveMigrationDiscoveryOptions? options = null, IProgress<SaveMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var selected = targets.Take(65).ToArray();
        if (selected.Length > 64) throw new ServiceException(ServiceError.InvalidInput, "Use at most 64 explicit native save targets.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bound = selected.Select(target =>
        {
            if (target?.Launch is null || target.Observation is null)
                throw new ServiceException(ServiceError.InvalidInput, "An exact installed core and native save observation are required.");
            var launch = BindLaunch(target.Launch); string profileId = Ids.Slug(target.ProfileId);
            string title = Ids.Hex(target.Observation.TitleId, 16, "Title ID"), user = Ids.Hex(target.Observation.UserId, 32, "User ID");
            if (!seen.Add(launch.BackendId + ":" + profileId + ":" + title + ":" + user))
                throw new ServiceException(ServiceError.InvalidInput, "The batch contains a duplicate native save target.");
            return new BoundMigrationTarget(target, launch, profileId, title, user);
        }).ToArray();
        var checkedOptions = ValidateOptions(options);
        using var serviceLease = _workspace.Acquire("save-migration");
        var state = ReadState();
        var discovery = DiscoverCore(state, checkedOptions, cancellationToken);
        var outcomes = new List<SaveMigrationOutcome>();
        bool cancelled = false;
        for (int index = 0; index < selected.Length; index++)
        {
            var (target, launch, profileId, title, user) = bound[index];
            void Progress(string stage) => progress?.Report(new(launch.BackendId, title, stage, index, selected.Length));
            SaveMigrationOutcome Outcome(string status, string? error = null, string? receipt = null, bool nativeVerified = false) =>
                Sanitize(new(launch.BackendId, profileId, title, user, status, "", error, receipt,
                    status == "imported", nativeVerified, status == "imported"));
            string profileRelative = "backends/" + launch.BackendId + "/" + profileId;
            string profileRoot = Paths.Under(_workspace.RootPath, profileRelative);
            string probeRoot = Paths.Under(_root, "probe-" + Guid.NewGuid().ToString("N"));
            string? inbox = null;
            string? preparingReceiptId = null;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                { cancelled = true; outcomes.Add(Outcome("cancelled", "cancelled")); continue; }
                var prior = state.Receipts.LastOrDefault(x => x.Receipt.BackendId == launch.BackendId &&
                    x.Receipt.ProfileId == profileId && x.Receipt.TitleId == title && x.Receipt.UserId == user);
                if (prior is not null)
                {
                    string status = prior.Receipt.Status == "imported" ? "already_imported" : prior.Receipt.Status == "rolled_back" ? "rolled_back" : "interrupted";
                    outcomes.Add(Outcome(status, receipt: prior.Receipt.ReceiptId)); continue;
                }
                if (!target.Observation.Existing)
                { outcomes.Add(Outcome("target_allocation_unavailable")); continue; }
                var mapping = NativeSaveMappings.FromNativeObservation(target.Observation, launch.BackendId, launch.Revision);
                if (launch.BackendId is not ("eden" or "citron") || mapping.Identity.FormatId != launch.BackendId + "-native" ||
                    mapping.Identity.FormatVersion != "1" || !mapping.RelativeDirectory.StartsWith("nand/user/save/", StringComparison.Ordinal))
                { outcomes.Add(Outcome("format_verification_unavailable")); continue; }
                if (!Directory.Exists(profileRoot) || !Directory.Exists(Paths.Under(profileRoot, mapping.RelativeDirectory)))
                { outcomes.Add(Outcome("target_allocation_unavailable")); continue; }
                Progress("checking_target");
                using var clientLease = StoreIo.AcquireLock(Paths.Under(profileRoot, ".nx-client.lock"));
                CheckRecovery(profileRoot);
                using (StoreIo.AcquireLock(Paths.Under(profileRoot, ".nx-runtime.lock")))
                {
                    var live = Paths.Under(profileRoot, mapping.RelativeDirectory);
                    if (Directory.EnumerateFileSystemEntries(live).Any())
                    { outcomes.Add(Outcome("target_conflict")); continue; }
                }
                var applicableRoots = discovery.Roots.Where(root => root.Plain && root.Candidates.Any(candidate =>
                    candidate.UserId == user && candidate.Type == target.Observation.Type && candidate.TitleId is not null &&
                    (candidate.TitleId == title || candidate.Layout == "plain_directory_future" && candidate.TitleId == title[..14] + "00"))).ToArray();
                if (applicableRoots.Length == 0)
                {
                    outcomes.Add(Outcome(discovery.Report.Locations.Any(location => location.Status == "deferred_busy") ? "source_busy" : "no_matching_source")); continue;
                }
                var verified = new List<VerifiedSource>();
                foreach (var sourceRoot in applicableRoots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Progress("copying_private_source");
                    if (SourceBusy(sourceRoot.Location)) throw new ServiceException(ServiceError.Busy, "The source emulator is active.");
                    var sourceLeasePath = Path.Combine(sourceRoot.Location.RootPath, ".nx-runtime.lock");
                    using var sourceLease = File.Exists(sourceLeasePath) ? StoreIo.AcquireLock(sourceLeasePath) : null;
                    string work = Paths.EnsureDirectory(probeRoot, Guid.NewGuid().ToString("N"));
                    string cloneProfile = Paths.EnsureDirectory(work, "backends/" + launch.BackendId + "/probe");
                    var clonedTree = await CopyCheckedAsync(sourceRoot.SaveRoot, Paths.EnsureDirectory(cloneProfile, "nand/user/save"), cancellationToken);
                    await Task.Delay(checkedOptions.StabilityWindow, cancellationToken);
                    VerifyUnchanged(sourceRoot.SaveRoot, clonedTree);
                    Progress("native_source_verification");
                    var native = await ObserveCloneAsync(launch, work, title, cancellationToken);
                    VerifyUnchanged(sourceRoot.SaveRoot, clonedTree);
                    if (SourceBusy(sourceRoot.Location, checkLease: false)) throw new ServiceException(ServiceError.Busy, "The source emulator became active during verification.");
                    foreach (var entry in native.Catalog.Entries.Where(entry => entry.TitleId.Equals(title, StringComparison.OrdinalIgnoreCase) &&
                        entry.UserId.Equals(user, StringComparison.OrdinalIgnoreCase) && entry.Type == target.Observation.Type))
                    {
                        var sourceMapping = NativeSaveMappings.FromNativeObservation(new(entry.BackendId, launch.Revision,
                            entry.TitleId, entry.UserId, entry.SaveDataId, entry.Type, entry.FormatId, entry.FormatVersion,
                            entry.RelativePath, entry.Existing, entry.AdapterVerified, entry.Provenance), launch.BackendId, launch.Revision);
                        if (sourceMapping.Identity != mapping.Identity || !sourceMapping.RelativeDirectory.StartsWith("nand/user/save/", StringComparison.Ordinal))
                            throw new ServiceException(ServiceError.UnverifiedSaveMapping, "The native source mapping is incompatible with the exact target identity and format.");
                        string cloneDirectory = Paths.Under(cloneProfile, sourceMapping.RelativeDirectory);
                        string sourceRelative = sourceMapping.RelativeDirectory["nand/user/save/".Length..];
                        string original = Paths.Under(sourceRoot.SaveRoot, sourceRelative);
                        var inventory = await InventoryAsync(cloneDirectory, cancellationToken);
                        if (inventory.Files.Count == 0) continue;
                        VerifyUnchanged(original, inventory);
                        string id = SourceId(state, sourceRoot.Location.Origin, original);
                        verified.Add(new(id, sourceRoot.Location.Origin, original, cloneDirectory, sourceMapping, native.AdapterVersion,
                            inventory.Files, inventory.Directories, TreeFingerprint(inventory)));
                    }
                }
                if (verified.Count != 1)
                { outcomes.Add(Outcome(verified.Count == 0 ? "no_matching_source" : "source_conflict", nativeVerified: verified.Count > 0)); continue; }
                var source = verified.Single();
                Progress("retaining_checked_snapshot");
                string backupId = Guid.NewGuid().ToString("N");
                inbox = "migration-" + backupId;
                var inboxRoot = Paths.EnsureDirectory(_workspace.ImportsPath, inbox);
                var payload = await CopyCheckedAsync(source.CloneDirectory, Paths.EnsureDirectory(inboxRoot, "payload"), cancellationToken);
                if (TreeFingerprint(payload) != source.Fingerprint)
                    throw new ServiceException(ServiceError.CorruptData, "The native-verified source snapshot changed before staging.");
                VerifyUnchanged(source.OriginalDirectory, payload);
                var manifest = new SaveBackupManifest(1, backupId, mapping.Identity, DateTimeOffset.UtcNow,
                    source.Mapping.RelativeDirectory, source.Mapping.VerificationEvidence, payload.Files, payload.Directories);
                StoreIo.AtomicJson(Paths.Under(inboxRoot, "manifest.json"), manifest);
                cancellationToken.ThrowIfCancellationRequested();
                string receiptId = Guid.NewGuid().ToString("N");
                preparingReceiptId = receiptId;
                var receipt = new SaveMigrationReceipt(receiptId, launch.BackendId, launch.Revision, SafeAdapter(source.AdapterVersion),
                    LaunchFingerprint(launch), profileId, title, user, source.SourceId, source.Origin,
                    mapping.Identity.FormatId, mapping.Identity.FormatVersion, DateTimeOffset.UtcNow, "applying", false);
                var record = new ReceiptRecord(receipt, mapping, profileRelative, new string('0', 32), backupId, new string('0', 32),
                    source.OriginalDirectory, source.Fingerprint);
                state.Receipts.Add(record); WriteState(state);
                Progress("importing_empty_target");
                // The final empty check, source retention and directory transaction
                // share SaveStore's authoritative runtime lease. Cancellation is
                // observed before this commit; an in-progress transaction finishes.
                var imported = await Task.Run(() => new SaveStore(_workspace, profileRelative).ImportIntoEmptyFromInbox(mapping, inbox));
                record = record with { Receipt = receipt with { Status = "imported", CanRollback = true },
                    TransactionId = imported.TransactionId, ImportedBackupId = imported.ImportedBackupId, PreservedBackupId = imported.PreservedBackupId };
                state.Receipts[^1] = record; WriteState(state);
                outcomes.Add(Outcome("imported", receipt: receiptId, nativeVerified: true));
            }
            catch (OperationCanceledException)
            { cancelled = true; outcomes.Add(Outcome("cancelled", "cancelled")); }
            catch (NxException error)
            {
                if (error.Code == "cancelled") cancelled = true;
                outcomes.Add(Outcome(error.Code == "cancelled" ? "cancelled" : "native_verification_unavailable", SafeError(error.Code)));
            }
            catch (ServiceException error)
            {
                if (preparingReceiptId is not null && error.Code is ServiceError.Busy or ServiceError.Conflict or ServiceError.NotFound)
                    state.Receipts.RemoveAll(record => record.Receipt.ReceiptId == preparingReceiptId && record.Receipt.Status == "applying" &&
                        record.TransactionId.All(character => character == '0'));
                outcomes.Add(Outcome(error.Code switch
                {
                    ServiceError.Busy => "deferred_busy", ServiceError.RecoveryRequired => "recovery_required",
                    ServiceError.Conflict => "target_conflict", ServiceError.CorruptData => "source_unstable",
                    ServiceError.UnsafePath => "unsafe_source", ServiceError.UnverifiedSaveMapping => "identity_unverified",
                    ServiceError.NotFound => "target_allocation_unavailable", _ => "failed"
                }, error.Code.ToString().ToLowerInvariant()));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { outcomes.Add(Outcome("failed", "io_error")); }
            finally
            {
                if (inbox is not null && Directory.Exists(Paths.Under(_workspace.ImportsPath, inbox)))
                    Directory.Delete(Paths.Under(_workspace.ImportsPath, inbox), recursive: true);
                if (Directory.Exists(probeRoot)) TryDeleteProbes(probeRoot);
                progress?.Report(new(launch.BackendId, title, "target_complete", index + 1, selected.Length));
            }
        }
        state.LastOutcomes.Clear(); state.LastOutcomes.AddRange(outcomes.Select(Sanitize)); WriteState(state);
        return new(Array.AsReadOnly(outcomes.ToArray()), cancelled, DateTimeOffset.UtcNow, Scope);
    }

    public async Task<SaveMigrationReport> RollbackAsync(string receiptId, CancellationToken cancellationToken = default)
    {
        receiptId = Ids.GuidId(receiptId); cancellationToken.ThrowIfCancellationRequested();
        using var lease = _workspace.Acquire("save-migration"); var state = ReadState();
        int index = state.Receipts.FindIndex(record => record.Receipt.ReceiptId == receiptId);
        if (index < 0) throw new ServiceException(ServiceError.NotFound, "That migration receipt was not found.");
        var record = state.Receipts[index]; var receipt = record.Receipt;
        SaveMigrationOutcome Result(string status, string? error = null) => Sanitize(new(receipt.BackendId, receipt.ProfileId,
            receipt.TitleId, receipt.UserId, status, "", error, receiptId));
        SaveMigrationOutcome outcome;
        if (receipt.Status == "rolled_back") outcome = Result("rolled_back");
        else if (receipt.Status != "imported") outcome = Result("interrupted");
        else
        {
            string profile = Paths.Under(_workspace.RootPath, record.ProfileRelative);
            try
            {
                using var client = StoreIo.AcquireLock(Paths.Under(profile, ".nx-client.lock")); CheckRecovery(profile);
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(() => new SaveStore(_workspace, record.ProfileRelative).RollbackImportIfUnchanged(record.Mapping,
                    record.TransactionId, record.ImportedBackupId, record.PreservedBackupId));
                state.Receipts[index] = record with { Receipt = receipt with { Status = "rolled_back", CanRollback = false } };
                outcome = Result("rolled_back");
            }
            catch (ServiceException error)
            { outcome = Result(error.Code == ServiceError.Busy ? "deferred_busy" : error.Code == ServiceError.Conflict ? "rollback_conflict" : "recovery_required", error.Code.ToString().ToLowerInvariant()); }
        }
        state.LastOutcomes.Clear(); state.LastOutcomes.Add(outcome); WriteState(state);
        return new([outcome], false, DateTimeOffset.UtcNow, Scope);
    }

    private async Task<(NativeSaveCatalog Catalog, string AdapterVersion)> ObserveCloneAsync(BackendLaunch launch,
        string workspace, string title, CancellationToken ct)
    {
        var runtime = await NxRuntime.ConnectAsync(launch, ct);
        NativeSaveCatalog catalog;
        string adapter = SafeAdapter(runtime.Backend.AdapterVersion);
        try
        {
            if (!runtime.Supports("saves.list") ||
                !runtime.Supports("window.get") || !runtime.Supports("window.set"))
                throw new NxException("unsupported", "Native hidden source-save inspection is unavailable.");
            await runtime.InitializeAsync(new RuntimeOptions { DataRoot = workspace, ProfileId = "probe", GraphicsBackend = "vulkan",
                Headless = false, Width = 320, Height = 240, WindowTitle = "NX private save verification" }, ct);
            var window = await runtime.SetRenderWindowAsync(new RenderWindowChange { Visible = false }, ct);
            if (window.Visible) throw new NxException("unsupported", "The native source verification surface could not remain hidden.");
            catalog = await runtime.DiscoverNativeSavesAsync(title, ct);
        }
        finally { await runtime.DisposeAsync(); }
        if (runtime.Shutdown?.Graceful != true) throw new NxException("core_error", "The native source verifier did not dispose cleanly.");
        using (StoreIo.AcquireLock(Path.Combine(workspace, "backends", launch.BackendId, "probe", ".nx-runtime.lock"))) { }
        return (catalog, adapter);
    }

    private static void CheckRecovery(string profile)
    {
        foreach (var marker in RecoveryMarkers)
            if (File.Exists(Paths.Under(profile, marker))) throw new ServiceException(ServiceError.RecoveryRequired, "A save profile has an interrupted or active transaction.");
    }
    private static BackendLaunch BindLaunch(BackendLaunch launch)
    {
        Ids.Slug(launch.BackendId); string revision = Ids.Hex(launch.Revision, 40, "Backend revision").ToLowerInvariant();
        if (revision.All(c => c == '0')) throw new ServiceException(ServiceError.InvalidInput, "Unknown native revisions cannot verify save mappings.");
        string executable = Absolute(launch.ExecutablePath); Paths.RejectLinks(executable);
        if (!File.Exists(executable)) throw new ServiceException(ServiceError.Unavailable, "The selected native executable is unavailable.");
        using var stream = File.OpenRead(executable); string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (launch.ExecutableSha256 is not null && !hash.Equals(launch.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException(ServiceError.Conflict, "The native executable no longer matches its installation binding.");
        if (launch.Arguments is null || launch.ArtifactSha256 is null || launch.Arguments.Any(argument => argument is null))
            throw new ServiceException(ServiceError.InvalidInput, "The installed native invocation is invalid.");
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in launch.ArtifactSha256)
        {
            string path = Absolute(artifact.Key); Paths.RejectLinks(path);
            string expected = Ids.Hex(artifact.Value, 64, "Artifact binding").ToLowerInvariant();
            if (!File.Exists(path)) throw new ServiceException(ServiceError.Unavailable, "A native installation artifact is unavailable.");
            using var input = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != expected)
                throw new ServiceException(ServiceError.Conflict, "The native artifact changed after installation verification.");
            artifacts.Add(path, expected);
        }
        return launch with { Revision = revision, ExecutablePath = executable, ExecutableSha256 = hash,
            Arguments = launch.Arguments.ToArray(), ArtifactSha256 = artifacts };
    }
    private static string LaunchFingerprint(BackendLaunch launch) => HashText(JsonSerializer.Serialize(new
    { launch.BackendId, launch.Revision, launch.ExecutableSha256, launch.WorkingDirectory, launch.Arguments,
        artifacts = launch.ArtifactSha256.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() }));
    private static string SafeAdapter(string? value) => value is not null && value.Length <= 64 &&
        Regex.IsMatch(value, @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}(?:[-+][0-9A-Za-z.-]{1,48})?$", RegexOptions.CultureInvariant) ? value : "unknown";
    private static string SafeError(string value) => value is "unsupported" or "missing_dependency" or "invalid_argument" or "core_error" or
        "io_error" or "cancelled" or "timeout" or "data_in_use" or "protocol_mismatch" or "version_mismatch" ? value : "native_error";

    private static SaveMigrationOutcome Sanitize(SaveMigrationOutcome outcome) => outcome with { ErrorCode = outcome.ErrorCode is null ? null :
        outcome.ErrorCode is "busy" or "conflict" or "corruptdata" or "unsafepath" or "unverifiedsavemapping" or "recoveryrequired" or "notfound" ? outcome.ErrorCode : SafeError(outcome.ErrorCode),
        Message = outcome.Status switch
        {
            "imported" => "A private source copy was opened by the exact native adapter and imported into the empty matching save. The source and both snapshots were preserved; game-level save reopening remains unverified.",
            "already_imported" => "This title/user already has an automatic migration receipt. Its current save was preserved.",
            "rolled_back" => "The automatic migration was rolled back. Automatic setup will preserve this decision.",
            "rollback_conflict" => "The save or its import transaction changed after migration. Rollback preserved the current data.",
            "target_conflict" => "The destination contains data or changed during review. Automatic migration did not overwrite it.",
            "source_conflict" => "More than one native-verified source matches this exact title and user. Choose a source through explicit save import.",
            "target_allocation_unavailable" => "This core has no existing native-verified empty save directory. Automatic target allocation is unsupported; create the native save before retrying.",
            "format_verification_unavailable" => "Automatic verification of this native save-container format is unavailable. No directory conversion or identifier mapping was guessed.",
            "native_verification_unavailable" => "The selected core could not verify a private source copy. Its native inspection, GPU, or prerequisites may be unavailable; no import was completed.",
            "identity_unverified" => "The native source or target does not have an exact verified title, user and compatible format mapping.",
            "source_unstable" => "The source changed or did not match its checked private snapshot. No verified import was completed.",
            "unsafe_source" => "The source contains a link or unsafe path. It was not imported.",
            "source_busy" or "deferred_busy" => "A source emulator, target session or save operation is active. Migration is deferred without interrupting it.",
            "recovery_required" => "A save profile has an interrupted operation requiring recovery before migration.",
            "no_matching_source" => "No nonempty native-verifiable source matches this exact title and user in the supported known locations.",
            "cancelled" => "Migration was cancelled before its next local transaction. Existing saves were preserved.",
            "interrupted" => "A migration may have been interrupted. Review the native save transaction and retained snapshots before changing its data.",
            _ => "Migration did not complete. No save compatibility or successful import is claimed."
        } };
}
