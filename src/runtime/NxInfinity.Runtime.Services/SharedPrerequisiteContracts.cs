namespace NxInfinity.Runtime.Services;

/// <summary>Inputs only. Never serialize this object into diagnostics; launch and workspace paths are private.</summary>
public sealed record SharedPrerequisiteTarget
{
    public required BackendLaunch Launch { get; init; }
    public required string WorkspaceBase { get; init; }
    public string ProfileId { get; init; } = "default";
}

public enum SharedPrerequisiteProvisionMode
{
    /// <summary>Provision new revisions/installations and retry deferred busy targets. Retain other failures for explicit review.</summary>
    Pending,
    /// <summary>Retry recorded failures only. Successful or never-attempted targets are not written.</summary>
    RetryFailures,
    /// <summary>Explicitly re-import the selected retained revisions, including previously successful targets.</summary>
    Reapply
}

/// <summary>A private local source has been retained. This receipt does not validate any key or firmware.</summary>
public sealed record SharedPrerequisiteRevision(string Id, string Kind, string Slot,
    string? PreviousRevisionId, DateTimeOffset StoredUtc, string SourceForm);

/// <summary>Sanitized observation. No source/profile paths, key bytes or key hashes are exposed.</summary>
public sealed record SharedPrerequisiteOutcome
{
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    public string AdapterVersion { get; init; } = "";
    /// <summary>Hash of the selected executable, declared artifact binding and invocation; never a key/content hash.</summary>
    public string InstallationFingerprint { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Slot { get; init; } = "";
    public string SourceRevisionId { get; init; } = "";
    public string Status { get; init; } = "pending";
    public string? ErrorCode { get; init; }
    public string Message { get; init; } = "No import has been attempted.";
    public bool? KeysPresent { get; init; }
    public bool? HeaderKeyUsable { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? FirmwareValidationScope { get; init; }
    public int? FirmwareArchiveCount { get; init; }
    public int? FirmwareMetadataCount { get; init; }
    public string? FirmwareReceiptId { get; init; }
    public bool? FirmwarePackageComplete { get; init; }
    public bool? FirmwareInstalledInventoryVerified { get; init; }
    public bool? FirmwareSignatureAuthenticated { get; init; }
    public bool? FirmwareRetailQualified { get; init; }
    public string RollbackStatus { get; init; } = "not_imported";
    public bool Attempted { get; init; }
    public bool AlreadyProvisioned { get; init; }
    public bool NativeImportAccepted { get; init; }
    public bool FreshWorkerObserved { get; init; }
    public DateTimeOffset? ObservedUtc { get; init; }
    public IReadOnlyList<string> IssueCodes { get; init; } = Array.Empty<string>();
    public string ObservationScope { get; init; } = "Local native prerequisite observations only; each game's key coverage and firmware requirements remain unverified.";
}

public sealed record SharedPrerequisiteProgress(string BackendId, string Kind, string Slot,
    string Stage, int CompletedTargets, int TotalTargets);

public sealed record SharedPrerequisiteStatus(IReadOnlyList<SharedPrerequisiteRevision> Sources,
    IReadOnlyList<SharedPrerequisiteOutcome> Outcomes);

public sealed record SharedPrerequisiteProvisionReport(string OperationId,
    IReadOnlyList<SharedPrerequisiteOutcome> Outcomes, bool Cancelled, DateTimeOffset CompletedUtc,
    string RollbackScope);
