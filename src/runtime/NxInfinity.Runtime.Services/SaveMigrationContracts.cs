namespace NxInfinity.Runtime.Services;

/// <summary>Input only. RootPath is private and must not be exported.</summary>
public sealed record SaveMigrationLocation(string Origin, string RootPath);

public sealed record SaveMigrationDiscoveryOptions
{
    public bool IncludeKnownLocations { get; init; } = true;
    public IReadOnlyList<SaveMigrationLocation> AdditionalLocations { get; init; } = Array.Empty<SaveMigrationLocation>();
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Input only. The observation must come from actual native discovery of this exact target installation.</summary>
public sealed record SaveMigrationTarget
{
    public required BackendLaunch Launch { get; init; }
    public string ProfileId { get; init; } = "default";
    public required NativeSaveObservation Observation { get; init; }
}

public sealed record SaveMigrationCandidate(string Id, string Origin, string Layout,
    string? TitleId, string? UserId, string? Type, string Status, string Message, bool CanAutoImport = false);

public sealed record SaveMigrationLocationStatus(string Origin, string Status, int CandidateCount, string Message);
public sealed record SaveMigrationDiscoveryReport(IReadOnlyList<SaveMigrationCandidate> Candidates,
    IReadOnlyList<SaveMigrationLocationStatus> Locations, DateTimeOffset ObservedUtc, string Scope);

public sealed record SaveMigrationReceipt(string ReceiptId, string BackendId, string BackendRevision,
    string AdapterVersion, string InstallationFingerprint, string ProfileId, string TitleId, string UserId,
    string SourceId, string SourceOrigin, string FormatId, string FormatVersion, DateTimeOffset CreatedUtc,
    string Status, bool CanRollback);

public sealed record SaveMigrationOutcome(string BackendId, string ProfileId, string TitleId, string UserId,
    string Status, string Message, string? ErrorCode = null, string? ReceiptId = null,
    bool Imported = false, bool NativeSourceVerified = false, bool CanAutoImport = false);

public sealed record SaveMigrationProgress(string BackendId, string TitleId, string Stage,
    int CompletedTargets, int TotalTargets);
public sealed record SaveMigrationReport(IReadOnlyList<SaveMigrationOutcome> Outcomes, bool Cancelled,
    DateTimeOffset CompletedUtc, string Scope);
public sealed record SaveMigrationStatus(IReadOnlyList<SaveMigrationReceipt> Receipts,
    IReadOnlyList<SaveMigrationOutcome> LastOutcomes);
