using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NxInfinity.Runtime;

namespace NxInfinity.Runtime.Services;

public enum EnhancementStatus
{
    [JsonStringEnumMemberName("research_candidate")] ResearchCandidate,
    Experimental, Qualified, Unsupported
}
public enum EnhancementChangeKind
{
    Settings,
    [JsonStringEnumMemberName("game_patch")] GamePatch,
    [JsonStringEnumMemberName("unsupported_platform")] UnsupportedPlatform,
    [JsonStringEnumMemberName("separately_supplied_content")] SeparatelySuppliedContent
}
public enum EnhancementReceiptStatus
{
    Prepared, Applied, RolledBack, Failed,
    [JsonStringEnumMemberName("recovery_required")] RecoveryRequired
}

public sealed record EnhancementHardwareConstraint
{
    public string? VendorId { get; init; }
    public string? DeviceId { get; init; }
    /// <summary>Exact accepted versions. Empty means no driver restriction; wildcards/ranges are not inferred.</summary>
    public IReadOnlyList<string> DriverVersions { get; init; } = [];
}
public sealed record EnhancementHardwareInfo(string? VendorId = null, string? DeviceId = null, string? DriverVersion = null);
public sealed record EnhancementSettingChange(string SettingId, JsonElement Value);

public sealed record EnhancementChange
{
    public string ChangeId { get; init; } = "";
    public EnhancementChangeKind Kind { get; init; }
    public EnhancementStatus Status { get; init; } = EnhancementStatus.ResearchCandidate;
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";
    public string Evidence { get; init; } = "";
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];
    public EnhancementSettingChange? Setting { get; init; }
    public string? PatchReceiptId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GamePatchRecipe? GamePatch { get; init; }
    public string? ContentId { get; init; }
}

public sealed record EnhancementProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string ProfileId { get; init; } = "";
    public string Version { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string TitleId { get; init; } = "";
    /// <summary>Null is allowed only for a non-applicable research/unsupported template.</summary>
    public string? BuildId { get; init; }
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackendAdapterVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackendArtifactManifestSha256 { get; init; }
    public EnhancementStatus Status { get; init; } = EnhancementStatus.ResearchCandidate;
    public EnhancementHardwareConstraint? Hardware { get; init; }
    public string Source { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string OriginalBehavior { get; init; } = "";
    public string RollbackNotes { get; init; } = "";
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];
    public IReadOnlyList<EnhancementChange> Changes { get; init; } = [];
}

/// <summary>Caller attestation. An SDK coordinator must derive identity and active state from the actual session.</summary>
public sealed record EnhancementContext
{
    public string? TitleId { get; init; }
    public string? BuildId { get; init; }
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackendAdapterVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackendArtifactManifestSha256 { get; init; }
    public bool IdentityVerified { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? GamePatchAdapters { get; init; }
    public EnhancementHardwareInfo? Hardware { get; init; }
    public bool HardwareVerified { get; init; }
    public IReadOnlyList<string> ActiveProfileIds { get; init; } = [];
    public IReadOnlyList<string> ActivePatchReceiptIds { get; init; } = [];
}

public sealed record EnhancementIssue(string Code, string Detail, string? ChangeId = null);
public sealed record EnhancementPlan(EnhancementProfile Profile, string ProfileSha256, EnhancementContext Context,
    IReadOnlyList<EnhancementChange> Changes, IReadOnlyList<EnhancementIssue> Issues, bool AllowExperimental)
{
    public bool Applicable => Issues.Count == 0;
    public IReadOnlyList<string> SelectedChangeIds => Changes.Select(c => c.ChangeId).ToArray();
}
/// <summary>Exact scalar state captured by the coordinator before mutation. This is not gameplay evidence.</summary>
public sealed record EnhancementOriginalSetting(string SettingId, JsonElement Effective, JsonElement? Persisted = null);
public sealed record EnhancementReceiptEvent(EnhancementReceiptStatus Status, DateTimeOffset CreatedUtc, string Evidence);
public sealed record EnhancementApplicationReceipt
{
    public int SchemaVersion { get; init; } = 1;
    public string ReceiptId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public EnhancementProfile Profile { get; init; } = new();
    public string ProfileSha256 { get; init; } = "";
    public EnhancementContext Context { get; init; } = new();
    public IReadOnlyList<string> SelectedChangeIds { get; init; } = [];
    public IReadOnlyList<EnhancementOriginalSetting> OriginalSettings { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GamePatchAcknowledgement>? NativePatches { get; init; }
    public bool ExperimentalOptIn { get; init; }
    public EnhancementReceiptStatus Status { get; init; } = EnhancementReceiptStatus.Prepared;
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public IReadOnlyList<EnhancementReceiptEvent> History { get; init; } = [];
    public bool GameplayVerified => false;
}
internal sealed record EnhancementCatalogRecord(int SchemaVersion, string Sha256, EnhancementProfile Profile);
internal sealed record EnhancementReceiptRecord(int SchemaVersion, string Sha256, EnhancementApplicationReceipt Receipt);

/// <summary>Immutable reviewed catalogs and caller-attested transaction receipts. Never changes a core or downloads content.</summary>
public sealed class EnhancementStore
{
    // Hash-bound metadata must have the same bytes on Windows and Linux.
    private static readonly JsonSerializerOptions CanonicalJson = new(StoreIo.Json) { NewLine = "\n" };
    private static readonly JsonSerializerOptions LegacyWindowsJson = new(StoreIo.Json) { NewLine = "\r\n" };
    private readonly NxWorkspace _workspace;
    private readonly string _profiles;
    private readonly string _receipts;

    public EnhancementStore(NxWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        var root = Paths.EnsureDirectory(workspace.DataPath, "enhancements");
        _profiles = Paths.EnsureDirectory(root, "profiles");
        _receipts = Paths.EnsureDirectory(root, "receipts");
    }

    /// <summary>Adds an immutable id/version. Changing it requires a new version, including qualification changes.</summary>
    public EnhancementProfile Put(EnhancementProfile profile)
    {
        var canonical = CanonicalProfile(profile);
        var hash = Fingerprint(canonical);
        using var lease = _workspace.Acquire("enhancements");
        var path = ProfilePath(canonical.ProfileId, canonical.Version);
        if (File.Exists(path))
        {
            var old = ReadProfileUnlocked(canonical.ProfileId, canonical.Version);
            if (Fingerprint(old) != hash)
                throw new ServiceException(ServiceError.Conflict, "An enhancement profile id/version is immutable. Publish an explicitly reviewed new version.");
            return old;
        }
        StoreIo.AtomicJson(path, new EnhancementCatalogRecord(1, hash, canonical), CanonicalJson);
        return canonical;
    }

    public EnhancementProfile Get(string profileId, string version)
    {
        using var lease = _workspace.Acquire("enhancements");
        return ReadProfileUnlocked(profileId, version);
    }

    /// <summary>Reads one strict JSON profile from this workspace's imports tree; never fetches its source URL.</summary>
    public EnhancementProfile ImportFromInbox(string relativeProfilePath) =>
        Put(StoreIo.ReadJson<EnhancementProfile>(Paths.Under(_workspace.ImportsPath, relativeProfilePath)));

    public IReadOnlyList<EnhancementProfile> List()
    {
        using var lease = _workspace.Acquire("enhancements");
        var result = new List<EnhancementProfile>();
        foreach (var path in Paths.EnumerateFiles(_profiles, _workspace.Limits with { MaximumFiles = 1000 }))
        {
            if (AtomicTemporary(Path.GetFileName(path))) continue;
            var parts = path.Split('/');
            if (parts.Length != 2 || !parts[1].EndsWith(".json", StringComparison.Ordinal))
                throw new ServiceException(ServiceError.CorruptData, "Unexpected file in the enhancement catalog.");
            result.Add(ReadProfileUnlocked(parts[0], parts[1][..^5]));
        }
        return result;
    }

    public EnhancementPlan Evaluate(string profileId, string version, EnhancementContext context,
        IReadOnlyList<string>? selectedChangeIds = null, bool allowExperimental = false)
    {
        using var lease = _workspace.Acquire("enhancements");
        return EvaluateUnlocked(ReadProfileUnlocked(profileId, version), context, selectedChangeIds, allowExperimental);
    }

    /// <summary>Persists original settings before a coordinator mutates them. Prepared/recovery state blocks another preparation for this session.</summary>
    public EnhancementApplicationReceipt Prepare(EnhancementPlan plan, string sessionId,
        IReadOnlyList<EnhancementOriginalSetting> originals, IReadOnlyList<GamePatchAcknowledgement>? nativePatches = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Ids.Slug(sessionId);
        using var lease = _workspace.Acquire("enhancements");
        var profile = ReadProfileUnlocked(plan.Profile.ProfileId, plan.Profile.Version);
        if (Fingerprint(profile) != plan.ProfileSha256 || Fingerprint(CanonicalProfile(plan.Profile)) != plan.ProfileSha256)
            throw new ServiceException(ServiceError.CorruptData, "The prepared enhancement plan does not match its immutable catalog entry.");
        var existing = ReadReceiptsUnlocked().Where(r => r.SessionId == sessionId).ToArray();
        if (existing.Any(r => r.Status is EnhancementReceiptStatus.Prepared or EnhancementReceiptStatus.RecoveryRequired))
            throw new ServiceException(ServiceError.RecoveryRequired, "This session has an unfinished enhancement transaction; inspect its original-value receipt first.");
        var context = CanonicalContext(plan.Context) with
        {
            ActiveProfileIds = plan.Context.ActiveProfileIds.Concat(existing.Where(r => r.Status == EnhancementReceiptStatus.Applied)
                .Select(r => r.Profile.ProfileId)).Distinct(StringComparer.Ordinal).ToArray()
        };
        var checkedPlan = EvaluateUnlocked(profile, context, plan.SelectedChangeIds, plan.AllowExperimental);
        if (!checkedPlan.Applicable)
            throw new ServiceException(ServiceError.Unavailable, "The enhancement is not applicable: " + string.Join(", ", checkedPlan.Issues.Select(i => i.Code)));
        var requestedSettings = checkedPlan.Changes.Where(c => c.Setting is not null).Select(c => c.Setting!.SettingId).ToHashSet(StringComparer.Ordinal);
        foreach (var active in existing.Where(r => r.Status == EnhancementReceiptStatus.Applied))
        {
            if (active.Profile.ConflictsWith.Contains(profile.ProfileId, StringComparer.Ordinal))
                throw new ServiceException(ServiceError.Conflict, "An active profile declares a conflict with the requested profile.");
            if (active.OriginalSettings.Any(s => requestedSettings.Contains(s.SettingId)))
                throw new ServiceException(ServiceError.Conflict, "An active enhancement already owns one of the requested settings; roll it back first.");
            if (active.NativePatches is { Count: > 0 } && checkedPlan.Changes.Any(change => change.GamePatch is not null))
                throw new ServiceException(ServiceError.Conflict, "An active native timing repair already owns this session's module. Roll it back or restart original first.");
            if (active.Context.TitleId != context.TitleId || active.Context.BuildId != context.BuildId ||
                active.Context.BackendId != context.BackendId || active.Context.BackendRevision != context.BackendRevision ||
                active.Context.BackendAdapterVersion != context.BackendAdapterVersion ||
                !string.Equals(active.Context.BackendArtifactManifestSha256, context.BackendArtifactManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new ServiceException(ServiceError.Conflict, "An application session ID cannot be reused across different runtime identities.");
        }
        var originalValues = ValidateOriginals(originals, checkedPlan.Changes);
        var now = DateTimeOffset.UtcNow;
        var receipt = new EnhancementApplicationReceipt
        {
            ReceiptId = Guid.NewGuid().ToString("N"), SessionId = sessionId, Profile = profile,
            ProfileSha256 = checkedPlan.ProfileSha256, Context = context, SelectedChangeIds = checkedPlan.SelectedChangeIds,
            OriginalSettings = originalValues, ExperimentalOptIn = plan.AllowExperimental, CreatedUtc = now, UpdatedUtc = now,
            NativePatches = ValidateNativePatches(nativePatches, checkedPlan.Changes, allowDeferred: true),
            History = [new(EnhancementReceiptStatus.Prepared, now, "Coordinator-supplied original scalar values recorded before mutation; no gameplay assertion.")]
        };
        WriteReceipt(receipt);
        return receipt;
    }

    /// <summary>Durably binds native preparation/readback before the final transaction outcome.</summary>
    public EnhancementApplicationReceipt RecordNativePatches(string receiptId, IReadOnlyList<GamePatchAcknowledgement> patches)
    {
        using var lease = _workspace.Acquire("enhancements");
        var receipt = ReadReceiptUnlocked(receiptId);
        if (receipt.Status != EnhancementReceiptStatus.Prepared)
            throw new ServiceException(ServiceError.Conflict, "Only a prepared transaction can record native patch evidence.");
        var selected = receipt.Profile.Changes.Where(change => receipt.SelectedChangeIds.Contains(change.ChangeId)).ToArray();
        var validated = ValidateNativePatches(patches, selected, allowDeferred: false);
        if (receipt.NativePatches is { Count: > 0 } old && (validated is null || old.Count != validated.Count ||
            old.Where((ack, index) => ack.TransactionId != validated[index].TransactionId || ack.RecipeSha256 != validated[index].RecipeSha256).Any()))
            throw new ServiceException(ServiceError.Conflict, "Native transaction identity changed after preparation.");
        var updated = receipt with { NativePatches = validated };
        WriteReceipt(updated); return updated;
    }

    /// <summary>Records the coordinator's observed outcome; the storage service does not assert it performed a runtime mutation.</summary>
    public EnhancementApplicationReceipt RecordOutcome(string receiptId, EnhancementReceiptStatus status, string evidence)
    {
        Plain(evidence, 4096, "Outcome evidence");
        using var lease = _workspace.Acquire("enhancements");
        var receipt = ReadReceiptUnlocked(receiptId);
        if (!TransitionAllowed(receipt.Status, status)) throw new ServiceException(ServiceError.Conflict, "Invalid enhancement receipt outcome transition.");
        if (status == EnhancementReceiptStatus.Applied)
        {
            var patchChanges = receipt.Profile.Changes.Where(change => receipt.SelectedChangeIds.Contains(change.ChangeId) && change.GamePatch is not null).ToArray();
            if (patchChanges.Length != 0 && (receipt.NativePatches is null || receipt.NativePatches.Count != patchChanges.Length ||
                receipt.NativePatches.Any(patch => patch.Status != "applied")))
                throw new ServiceException(ServiceError.Conflict, "A typed game patch needs native applied/readback evidence before the transaction can be marked applied.");
        }
        if (receipt.History.Count >= 1024) throw new ServiceException(ServiceError.InvalidInput, "The receipt history limit was reached.");
        var now = DateTimeOffset.UtcNow;
        if (now < receipt.UpdatedUtc) now = receipt.UpdatedUtc;
        var updated = receipt with { Status = status, UpdatedUtc = now, History = receipt.History.Append(new EnhancementReceiptEvent(status, now, evidence)).ToArray() };
        WriteReceipt(updated);
        return updated;
    }

    public EnhancementApplicationReceipt GetReceipt(string receiptId)
    {
        using var lease = _workspace.Acquire("enhancements");
        return ReadReceiptUnlocked(receiptId);
    }

    public IReadOnlyList<EnhancementApplicationReceipt> ListReceipts(string? sessionId = null)
    {
        if (sessionId is not null) Ids.Slug(sessionId);
        using var lease = _workspace.Acquire("enhancements");
        return ReadReceiptsUnlocked().Where(r => sessionId is null || r.SessionId == sessionId).ToArray();
    }

    private EnhancementProfile ReadProfileUnlocked(string id, string version)
    {
        var data = StoreIo.ReadJson<EnhancementCatalogRecord>(ProfilePath(id, version));
        try
        {
            var profile = CanonicalProfile(data.Profile);
            if (data.SchemaVersion != 1 || profile.ProfileId != id || profile.Version != version || !MatchesFingerprint(profile, data.Sha256))
                throw new ServiceException(ServiceError.CorruptData, "Enhancement catalog identity or content hash mismatch.");
            return profile;
        }
        catch (ServiceException ex) when (ex.Code == ServiceError.InvalidInput)
        { throw new ServiceException(ServiceError.CorruptData, "Enhancement catalog metadata is invalid.", ex); }
    }

    private IReadOnlyList<EnhancementApplicationReceipt> ReadReceiptsUnlocked()
    {
        var result = new List<EnhancementApplicationReceipt>();
        foreach (var name in Paths.EnumerateFiles(_receipts, _workspace.Limits))
        {
            if (!name.Contains('/') && AtomicTemporary(name)) continue;
            if (name.Contains('/') || !name.EndsWith(".json", StringComparison.Ordinal))
                throw new ServiceException(ServiceError.CorruptData, "Unexpected enhancement receipt file.");
            result.Add(ReadReceiptUnlocked(name[..^5]));
        }
        return result;
    }

    private EnhancementApplicationReceipt ReadReceiptUnlocked(string id)
    {
        var record = StoreIo.ReadJson<EnhancementReceiptRecord>(ReceiptPath(id));
        var value = record.Receipt;
        try
        {
            if (record.SchemaVersion != 1 || value is null ||
                value.SchemaVersion != 1 || value.ReceiptId != id || value.Profile is null || value.Context is null ||
                !Enum.IsDefined(value.Status) || value.History is null || value.History.Count is < 1 or > 1024 || value.History.Any(h => h is null))
                throw new ServiceException(ServiceError.CorruptData, "Enhancement receipt is invalid.");
            Ids.Slug(value.SessionId);
            var profile = CanonicalProfile(value.Profile);
            if (!MatchesFingerprint(profile, value.ProfileSha256))
                throw new ServiceException(ServiceError.CorruptData, "The enhancement receipt's profile hash is invalid.");
            CanonicalContext(value.Context);
            var selected = SelectChanges(profile, value.SelectedChangeIds);
            ValidateOriginals(value.OriginalSettings, selected);
            ValidateNativePatches(value.NativePatches, selected, allowDeferred: value.Status != EnhancementReceiptStatus.Applied);
            if (value.Status == EnhancementReceiptStatus.Applied && value.NativePatches?.Any(patch => patch.Status != "applied") == true)
                throw new ServiceException(ServiceError.CorruptData, "An applied enhancement contains uncommitted native patch evidence.");
            if (value.History[0].Status != EnhancementReceiptStatus.Prepared || value.History[^1].Status != value.Status ||
                value.CreatedUtc == default || value.UpdatedUtc < value.CreatedUtc ||
                value.History[0].CreatedUtc != value.CreatedUtc || value.History[^1].CreatedUtc != value.UpdatedUtc)
                throw new ServiceException(ServiceError.CorruptData, "Enhancement receipt history is inconsistent.");
            for (var index = 0; index < value.History.Count; index++)
            {
                var item = value.History[index];
                if (item is null || !Enum.IsDefined(item.Status)) throw new ServiceException(ServiceError.CorruptData, "Invalid receipt event.");
                Plain(item.Evidence, 4096, "Receipt evidence");
                if (index > 0 && (item.CreatedUtc < value.History[index - 1].CreatedUtc || !TransitionAllowed(value.History[index - 1].Status, item.Status)))
                    throw new ServiceException(ServiceError.CorruptData, "Invalid receipt event ordering.");
            }
            if (!MatchesFingerprint(value, record.Sha256)) throw new ServiceException(ServiceError.CorruptData, "Enhancement receipt content hash mismatch.");
            return value;
        }
        catch (ServiceException ex) when (ex.Code == ServiceError.InvalidInput)
        { throw new ServiceException(ServiceError.CorruptData, "Enhancement receipt metadata is invalid.", ex); }
    }

    private static EnhancementPlan EvaluateUnlocked(EnhancementProfile profile, EnhancementContext supplied,
        IReadOnlyList<string>? selectedIds, bool allowExperimental)
    {
        var context = CanonicalContext(supplied);
        var selected = SelectChanges(profile, selectedIds ?? profile.Changes.Select(c => c.ChangeId).ToArray());
        var issues = new List<EnhancementIssue>();
        void Issue(string code, string detail, string? id = null) => issues.Add(new(code, detail, id));
        if (!context.IdentityVerified) Issue("identity_unverified", "A coordinator must verify the actual runtime title/build/backend identity.");
        if (!ExactHex(context.TitleId, 16) || context.TitleId!.All(c => c == '0') || !string.Equals(context.TitleId, profile.TitleId, StringComparison.OrdinalIgnoreCase))
            Issue("title_mismatch_or_unknown", "The runtime must report the exact profile title ID.");
        if (profile.BuildId is null || !ExactHex(context.BuildId, 64) || context.BuildId!.All(c => c == '0') || !string.Equals(context.BuildId, profile.BuildId, StringComparison.OrdinalIgnoreCase))
            Issue("build_mismatch_or_unknown", "An exact full build ID is mandatory; a template, display version, or prefix cannot match.");
        if (context.BackendId != profile.BackendId || !ExactHex(context.BackendRevision, 40) ||
            !context.BackendRevision.Equals(profile.BackendRevision, StringComparison.OrdinalIgnoreCase))
            Issue("backend_mismatch", "Backend stable ID and exact source revision must match.");
        if (profile.BackendAdapterVersion is null)
            Issue("backend_adapter_unbound", "This profile has no exact NX adapter version binding. Legacy metadata remains readable but cannot authorize settings.");
        else if (!AdapterToken(context.BackendAdapterVersion) ||
            !string.Equals(profile.BackendAdapterVersion, context.BackendAdapterVersion, StringComparison.Ordinal))
            Issue("backend_adapter_mismatch_or_unknown", "The exact case-sensitive NX adapter version must match.");
        if (profile.BackendArtifactManifestSha256 is null)
            Issue("backend_artifact_manifest_unbound", "This profile has no exact verified installation manifest binding. An upstream source revision alone cannot qualify an adapter or compiler variant.");
        else if (!ExactHex(context.BackendArtifactManifestSha256, 64) || context.BackendArtifactManifestSha256!.All(c => c == '0') ||
            !string.Equals(profile.BackendArtifactManifestSha256, context.BackendArtifactManifestSha256, StringComparison.OrdinalIgnoreCase))
            Issue("backend_artifact_manifest_mismatch_or_unknown", "The SDK-verified installation manifest SHA256 must match. Direct/custom launches without that provenance remain available in original mode.");
        CheckStatus(profile.Status, null);
        if (context.ActiveProfileIds.Contains(profile.ProfileId, StringComparer.Ordinal))
            Issue("profile_already_active", "This profile is already recorded active for the session.");
        if (profile.Hardware is { } constraint)
        {
            if (!context.HardwareVerified) Issue("hardware_unverified", "Hardware-specific profiles require a verified active-device identity.");
            if (constraint.VendorId is { } vendor && !string.Equals(vendor, context.Hardware?.VendorId, StringComparison.OrdinalIgnoreCase))
                Issue("hardware_vendor_mismatch_or_unknown", "The required hardware vendor was not positively matched.");
            if (constraint.DeviceId is { } device && !string.Equals(device, context.Hardware?.DeviceId, StringComparison.OrdinalIgnoreCase))
                Issue("hardware_device_mismatch_or_unknown", "The required device was not positively matched.");
            if (constraint.DriverVersions.Count != 0 && !constraint.DriverVersions.Contains(context.Hardware?.DriverVersion, StringComparer.OrdinalIgnoreCase))
                Issue("hardware_driver_mismatch_or_unknown", "An explicitly accepted driver version was not positively matched.");
        }
        var chosen = selected.Select(c => c.ChangeId).ToHashSet(StringComparer.Ordinal);
        bool Active(string token, bool local) => token.StartsWith("patch:", StringComparison.Ordinal)
            ? context.ActivePatchReceiptIds.Contains(token[6..], StringComparer.Ordinal)
            : local ? chosen.Contains(token) : context.ActiveProfileIds.Contains(token, StringComparer.Ordinal);
        void Requirements(IReadOnlyList<string> dependencies, IReadOnlyList<string> conflicts, bool local, string? change)
        {
            foreach (var token in dependencies) if (!Active(token, local)) Issue("dependency_missing", "Required selection is absent: " + token, change);
            foreach (var token in conflicts) if (Active(token, local)) Issue("conflict", "Conflicting selection is active: " + token, change);
        }
        Requirements(profile.DependsOn, profile.ConflictsWith, false, null);
        if (selected.Count == 0) Issue("no_changes_selected", "Choose at least one reviewed change explicitly.");
        var settings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in selected)
        {
            CheckStatus(change.Status, change.ChangeId);
            Requirements(change.DependsOn, change.ConflictsWith, true, change.ChangeId);
            switch (change.Kind)
            {
                case EnhancementChangeKind.Settings:
                    if (change.Setting is null) Issue("setting_payload_missing", "A research placeholder has no reviewed setting value.", change.ChangeId);
                    else if (!settings.Add(change.Setting.SettingId)) Issue("setting_conflict", "Two selected changes target the same setting.", change.ChangeId);
                    break;
                case EnhancementChangeKind.GamePatch:
                    if (change.GamePatch is not { } patch)
                        Issue("game_patch_adapter_unintegrated", "A legacy patch receipt alone is not a typed executable repair. Supply a complete guarded recipe.", change.ChangeId);
                    else
                    {
                        if (context.GamePatchAdapters?.Contains(patch.AdapterId, StringComparer.Ordinal) != true)
                            Issue("game_patch_adapter_unavailable", "This exact native adapter does not expose the recipe's typed operation.", change.ChangeId);
                        if (context.DisplayVersion != patch.DisplayVersion)
                            Issue("game_patch_update_mismatch_or_unknown", "The loaded update display version must match in addition to the full main build ID.", change.ChangeId);
                    }
                    break;
                case EnhancementChangeKind.UnsupportedPlatform:
                    Issue("unsupported_platform", "This improvement is classified for a platform outside this runtime integration.", change.ChangeId); break;
                case EnhancementChangeKind.SeparatelySuppliedContent:
                    Issue("separately_supplied_content", "Required content needs its own verified installation route; a profile does not supply or install it.", change.ChangeId); break;
            }
        }
        if (selected.Count(change => change.GamePatch is not null) > 1)
            Issue("game_patch_batch_conflict", "Use one atomic bounded native recipe per profile; combine its guarded writes explicitly.");
        return new(profile, Fingerprint(profile), context, selected, issues, allowExperimental);
        void CheckStatus(EnhancementStatus status, string? change)
        {
            if (status == EnhancementStatus.ResearchCandidate) Issue("research_candidate", "Research candidates are never applicable, including after experimental opt-in.", change);
            else if (status == EnhancementStatus.Unsupported) Issue("unsupported", "This item is explicitly unsupported in the current integration.", change);
            else if (status == EnhancementStatus.Experimental && !allowExperimental) Issue("experimental_opt_in_required", "Experimental changes need explicit selection for this evaluation/application.", change);
        }
    }

    private static EnhancementProfile CanonicalProfile(EnhancementProfile profile)
    {
        if (profile is null || profile.SchemaVersion != 1 || !Enum.IsDefined(profile.Status)) Invalid("Invalid enhancement profile schema or status.");
        Ids.Slug(profile!.ProfileId); Ids.Slug(profile.Version); Ids.Slug(profile.BackendId);
        Plain(profile.DisplayName, 160, "Display name"); Source(profile.Source); Plain(profile.Evidence, 4096, "Evidence");
        Plain(profile.OriginalBehavior, 4096, "Original behavior"); Plain(profile.RollbackNotes, 4096, "Rollback notes");
        var title = Ids.Hex(profile.TitleId, 16, "Title ID");
        var revision = Ids.Hex(profile.BackendRevision, 40, "Backend revision");
        if (profile.BackendAdapterVersion is not null && !AdapterToken(profile.BackendAdapterVersion))
            Invalid("An adapter binding must be an exact 1..128 character ASCII version token.");
        var manifest = profile.BackendArtifactManifestSha256 is null ? null :
            Ids.Hex(profile.BackendArtifactManifestSha256, 64, "Backend artifact manifest SHA256");
        if (manifest is not null && manifest.All(c => c == '0'))
            Invalid("An all-zero manifest digest is unknown and cannot bind a profile.");
        if (profile.BuildId is null && profile.Status is not (EnhancementStatus.ResearchCandidate or EnhancementStatus.Unsupported))
            Invalid("An applicable or experimental profile must specify a full build ID.");
        var build = profile.BuildId is null ? null : Ids.Hex(profile.BuildId, 64, "Full build ID");
        if (profile.Status is EnhancementStatus.Experimental or EnhancementStatus.Qualified &&
            (title.All(c => c == '0') || build!.All(c => c == '0')))
            Invalid("All-zero title or build identities are unknown and cannot bind an applicable profile.");
        var depends = Tokens(profile.DependsOn); var conflicts = Tokens(profile.ConflictsWith);
        if (depends.Intersect(conflicts, StringComparer.Ordinal).Any() || depends.Contains(profile.ProfileId) || conflicts.Contains(profile.ProfileId))
            Invalid("A profile cannot depend on/conflict with itself or declare contradictory requirements.");
        EnhancementHardwareConstraint? hardware = null;
        if (profile.Hardware is { } h)
        {
            if (h.DriverVersions is null || h.DriverVersions.Count > 32) Invalid("Invalid driver allowlist.");
            var vendor = h.VendorId is null ? null : Ids.Hex(h.VendorId, 4, "PCI vendor ID");
            var device = h.DeviceId is null ? null : Ids.Hex(h.DeviceId, 4, "PCI device ID");
            if (device is not null && vendor is null) Invalid("A device constraint requires its vendor ID.");
            foreach (var driver in h.DriverVersions!) Driver(driver);
            if (h.DriverVersions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != h.DriverVersions.Count ||
                (vendor is null && device is null && h.DriverVersions.Count == 0)) Invalid("Hardware constraints must be explicit and nonduplicate.");
            hardware = h with { VendorId = vendor, DeviceId = device, DriverVersions = h.DriverVersions.ToArray() };
        }
        if (profile.Changes is null || profile.Changes.Count is < 1 or > 128) Invalid("A profile needs 1..128 classified changes.");
        var changes = new List<EnhancementChange>();
        foreach (var c in profile.Changes!)
        {
            if (c is null || !Enum.IsDefined(c.Kind) || !Enum.IsDefined(c.Status)) Invalid("Invalid change classification/status.");
            Ids.Slug(c!.ChangeId); Plain(c.Description, 4096, "Change description"); Source(c.Source); Plain(c.Evidence, 4096, "Change evidence");
            var cDepends = Tokens(c.DependsOn); var cConflicts = Tokens(c.ConflictsWith);
            if (cDepends.Intersect(cConflicts, StringComparer.Ordinal).Any() || cDepends.Contains(c.ChangeId) || cConflicts.Contains(c.ChangeId))
                Invalid("Invalid self dependency/conflict or contradictory requirements.");
            EnhancementSettingChange? setting = null;
            if (c.Setting is { } s)
            {
                if (c.Kind != EnhancementChangeKind.Settings) Invalid("Only settings changes can carry setting values.");
                Ids.Slug(s.SettingId); Scalar(s.Value);
                setting = s with { Value = s.Value.Clone() };
            }
            if (c.Kind == EnhancementChangeKind.Settings && setting is null && c.Status is not (EnhancementStatus.ResearchCandidate or EnhancementStatus.Unsupported))
                Invalid("A reviewed setting change requires its exact scalar value.");
            if (c.PatchReceiptId is not null && c.Kind != EnhancementChangeKind.GamePatch) Invalid("Only a game_patch change can name a patch receipt.");
            GamePatchRecipe? patch = null;
            if (c.GamePatch is not null)
            {
                if (c.Kind != EnhancementChangeKind.GamePatch) Invalid("Only a game_patch change can carry typed executable writes.");
                try { patch = GamePatchValidation.Canonicalize(c.GamePatch); }
                catch (NxException error) { throw new ServiceException(ServiceError.InvalidInput, "The typed game patch recipe is invalid: " + error.Code, error); }
                if (patch.TitleId != title || patch.BuildId != build || patch.BackendId != profile.BackendId ||
                    !string.Equals(patch.BackendRevision, revision, StringComparison.OrdinalIgnoreCase))
                    Invalid("A typed patch must bind the same exact game/main build/core as its profile.");
            }
            if (c.ContentId is not null && c.Kind != EnhancementChangeKind.SeparatelySuppliedContent) Invalid("Only separately_supplied_content can name external content.");
            if (c.ContentId is not null) Ids.Slug(c.ContentId);
            changes.Add(c with { DependsOn = cDepends, ConflictsWith = cConflicts, Setting = setting, GamePatch = patch,
                PatchReceiptId = c.PatchReceiptId is null ? null : Ids.GuidId(c.PatchReceiptId) });
        }
        if (changes.Select(c => c.ChangeId).Distinct(StringComparer.Ordinal).Count() != changes.Count) Invalid("Duplicate enhancement change IDs.");
        var ids = changes.Select(c => c.ChangeId).ToHashSet(StringComparer.Ordinal);
        foreach (var c in changes)
            if (c.DependsOn.Concat(c.ConflictsWith).Any(t => !t.StartsWith("patch:", StringComparison.Ordinal) && !ids.Contains(t)))
                Invalid("Change requirements must name another change in this profile or a patch:receipt ID.");
        var visiting = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) Invalid("Cyclic enhancement change dependencies.");
            foreach (var dependency in changes.Single(c => c.ChangeId == id).DependsOn.Where(t => !t.StartsWith("patch:", StringComparison.Ordinal))) Visit(dependency);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in ids) Visit(id);
        return profile with { TitleId = title, BuildId = build, BackendRevision = revision, BackendArtifactManifestSha256 = manifest, Hardware = hardware,
            DependsOn = depends, ConflictsWith = conflicts, Changes = changes.ToArray() };
    }

    private static EnhancementContext CanonicalContext(EnhancementContext context)
    {
        if (context is null) Invalid("An enhancement context is required.");
        Ids.Slug(context!.BackendId);
        if (context.BackendRevision is null || context.BackendRevision.Length > 64 || context.TitleId?.Length > 64 || context.BuildId?.Length > 128)
            Invalid("Invalid context identity fields.");
        if (context.BackendAdapterVersion?.Length > 128 || context.BackendArtifactManifestSha256?.Length > 128 ||
            context.BackendAdapterVersion?.Any(char.IsControl) == true || context.BackendArtifactManifestSha256?.Any(char.IsControl) == true)
            Invalid("Invalid context adapter or manifest identity fields.");
        if (context.ActiveProfileIds is null || context.ActivePatchReceiptIds is null ||
            context.ActiveProfileIds.Count > 1024 || context.ActivePatchReceiptIds.Count > 1024) Invalid("Invalid active enhancement context.");
        foreach (var id in context.ActiveProfileIds!) Ids.Slug(id);
        if (context.GamePatchAdapters is { } adapters && (adapters.Count > 8 || adapters.Any(adapter => !AdapterToken(adapter))))
            Invalid("Invalid native patch adapter capability list.");
        if (context.DisplayVersion is { } display && (display.Length is < 1 or > 48 || display.Any(char.IsControl)))
            Invalid("Invalid loaded update version.");
        var patches = context.ActivePatchReceiptIds!.Select(Ids.GuidId).Distinct(StringComparer.Ordinal).ToArray();
        if (context.Hardware is { } hardware)
        {
            if (hardware.VendorId is not null) Ids.Hex(hardware.VendorId, 4, "Hardware vendor ID");
            if (hardware.DeviceId is not null) Ids.Hex(hardware.DeviceId, 4, "Hardware device ID");
            if (hardware.DriverVersion is not null) Driver(hardware.DriverVersion);
        }
        return context with { TitleId = context.TitleId?.ToUpperInvariant(), BuildId = context.BuildId?.ToUpperInvariant(),
            BackendRevision = context.BackendRevision.ToUpperInvariant(), BackendArtifactManifestSha256 = context.BackendArtifactManifestSha256?.ToUpperInvariant(),
            ActiveProfileIds = context.ActiveProfileIds.Distinct(StringComparer.Ordinal).ToArray(),
            ActivePatchReceiptIds = patches, GamePatchAdapters = context.GamePatchAdapters?.Distinct(StringComparer.Ordinal).ToArray() };
    }

    private static IReadOnlyList<EnhancementChange> SelectChanges(EnhancementProfile profile, IReadOnlyList<string> ids)
    {
        if (ids is null || ids.Count > 128 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) Invalid("Invalid selected changes.");
        foreach (var id in ids!) Ids.Slug(id);
        var chosen = ids.ToHashSet(StringComparer.Ordinal);
        if (chosen.Any(id => !profile.Changes.Any(c => c.ChangeId == id))) Invalid("A selected change does not exist in this profile.");
        // Stable dependency-first order; selecting a dependency is explicit, never silently enabled.
        var ordered = new List<EnhancementChange>(); var visited = new HashSet<string>(StringComparer.Ordinal);
        void Add(EnhancementChange change)
        {
            if (!visited.Add(change.ChangeId)) return;
            foreach (var dependency in change.DependsOn.Where(chosen.Contains)) Add(profile.Changes.Single(c => c.ChangeId == dependency));
            ordered.Add(change);
        }
        foreach (var change in profile.Changes.Where(c => chosen.Contains(c.ChangeId))) Add(change);
        return ordered;
    }

    private static IReadOnlyList<EnhancementOriginalSetting> ValidateOriginals(IReadOnlyList<EnhancementOriginalSetting> originals,
        IReadOnlyList<EnhancementChange> changes)
    {
        changes = changes.Where(change => change.Kind == EnhancementChangeKind.Settings).ToArray();
        if (originals is null || originals.Count != changes.Count || originals.Any(o => o is null) ||
            originals.Select(o => o.SettingId).Distinct(StringComparer.Ordinal).Count() != originals.Count ||
            changes.Any(c => c.Kind != EnhancementChangeKind.Settings || c.Setting is null) ||
            !originals.Select(o => o.SettingId).ToHashSet(StringComparer.Ordinal).SetEquals(changes.Select(c => c.Setting!.SettingId)))
            Invalid("Original values must cover every selected setting exactly once.");
        foreach (var item in originals!)
        {
            Ids.Slug(item.SettingId); Scalar(item.Effective);
            if (item.Persisted is { } value) Scalar(value);
        }
        return originals.Select(o => o with { Effective = o.Effective.Clone(), Persisted = o.Persisted?.Clone() }).ToArray();
    }

    private static IReadOnlyList<GamePatchAcknowledgement>? ValidateNativePatches(IReadOnlyList<GamePatchAcknowledgement>? supplied,
        IReadOnlyList<EnhancementChange> changes, bool allowDeferred)
    {
        var recipes = changes.Where(change => change.GamePatch is not null).Select(change => change.GamePatch!).ToArray();
        if ((supplied is null || supplied.Count == 0) && (allowDeferred || recipes.Length == 0)) return null;
        if (supplied is null || supplied.Count != recipes.Length) Invalid("Native preparation must cover every selected typed patch.");
        for (int i = 0; i < recipes.Length; i++)
        {
            var ack = supplied![i];
            if (ack is null || !Guid.TryParseExact(ack.TransactionId, "N", out _) || ack.Status is not ("prepared" or "applied"))
                Invalid("Invalid native patch preparation.");
            try { GamePatchValidation.ValidateAcknowledgement(new(ack!.TransactionId, GamePatchValidation.Fingerprint(recipes[i]), recipes[i]), ack, ack.Status); }
            catch (NxException error) { throw new ServiceException(ServiceError.InvalidInput, "Native patch evidence did not match its recipe.", error); }
        }
        return supplied!.ToArray();
    }

    private string ProfilePath(string id, string version) => Paths.Under(_profiles, Ids.Slug(id) + "/" + Ids.Slug(version) + ".json");
    private string ReceiptPath(string id) => Paths.Under(_receipts, Ids.GuidId(id) + ".json");
    private void WriteReceipt(EnhancementApplicationReceipt receipt) => StoreIo.AtomicJson(ReceiptPath(receipt.ReceiptId), new EnhancementReceiptRecord(1, Fingerprint(receipt), receipt), CanonicalJson);
    // Earlier Windows builds hashed CRLF. Accept that exact historical encoding
    // without rewriting immutable profiles or relaxing any content checks.
    private static bool MatchesFingerprint<T>(T value, string expected) =>
        Fingerprint(value) == expected || Fingerprint(value, LegacyWindowsJson) == expected;
    private static string Fingerprint<T>(T value, JsonSerializerOptions? options = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options ?? CanonicalJson);
        if (bytes.Length > 8 * 1024 * 1024) Invalid("Enhancement metadata exceeds the 8 MiB storage limit.");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    private static bool TransitionAllowed(EnhancementReceiptStatus from, EnhancementReceiptStatus to) => from switch
    {
        EnhancementReceiptStatus.Prepared => to is EnhancementReceiptStatus.Applied or EnhancementReceiptStatus.RolledBack or EnhancementReceiptStatus.Failed or EnhancementReceiptStatus.RecoveryRequired,
        EnhancementReceiptStatus.Applied => to is EnhancementReceiptStatus.RolledBack or EnhancementReceiptStatus.RecoveryRequired,
        EnhancementReceiptStatus.RecoveryRequired => to is EnhancementReceiptStatus.RolledBack or EnhancementReceiptStatus.RecoveryRequired,
        _ => false
    };
    private static bool ExactHex(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
    private static bool AdapterToken(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-');
    private static bool AtomicTemporary(string name) => name.StartsWith(".write-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) &&
        name.Length == 43 && Guid.TryParseExact(name.AsSpan(7, 32), "N", out _);
    [DoesNotReturn]
    private static void Invalid(string message) => throw new ServiceException(ServiceError.InvalidInput, message);
    private static void Plain(string text, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            Invalid(name + " is missing, oversized, or contains control characters.");
    }
    private static void Source(string value)
    {
        Plain(value, 2048, "Source");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            Invalid("Sources must be absolute HTTP(S) references without credentials; no source is fetched by this service.");
    }
    private static void Driver(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            Invalid("Driver constraints must be exact version tokens; wildcards, paths, and inferred ranges are unsupported.");
    }
    private static IReadOnlyList<string> Tokens(IReadOnlyList<string> values)
    {
        if (values is null || values.Count > 128) Invalid("Invalid dependencies or conflicts.");
        var result = values!.Select(t => t is not null && t.StartsWith("patch:", StringComparison.Ordinal)
            ? "patch:" + Ids.GuidId(t[6..]) : Ids.Slug(t!)).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) Invalid("Duplicate dependencies or conflicts.");
        return result;
    }
    private static void Scalar(JsonElement value)
    {
        bool valid = value.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => true,
            JsonValueKind.Number => value.TryGetDouble(out var number) && double.IsFinite(number),
            JsonValueKind.String => value.GetString() is { Length: > 0 and <= 256 } text && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+'),
            _ => false
        };
        if (!valid) Invalid("Enhancement settings and original values must be finite scalar values, not objects, null, or commands.");
    }
}
