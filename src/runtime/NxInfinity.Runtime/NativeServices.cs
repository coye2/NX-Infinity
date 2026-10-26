using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NxInfinity.Runtime;

public sealed record ContentIssue(string Code, string Severity, string Message);
public sealed record ContentEntry
{
    public string? TitleId { get; init; }
    public string? Kind { get; init; }
    public string? Name { get; init; }
    public string? ContentId { get; init; }
    public string? Version { get; init; }
}
public sealed record ContentArtwork(string MimeType, string DataBase64)
{
    public byte[] Decode()
    {
        if (MimeType is not ("image/png" or "image/jpeg") || DataBase64.Length > 350000)
            throw new NxException("protocol_mismatch", "Unsupported or oversized content artwork.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(DataBase64); }
        catch (FormatException e) { throw new NxException("protocol_mismatch", "Invalid content artwork encoding.", e); }
        if (bytes.Length is < 8 or > 262144) throw new NxException("protocol_mismatch", "Invalid content artwork size.");
        return bytes;
    }
}
public sealed record PrerequisiteReport
{
    public bool KeysPresent { get; init; }
    public bool HeaderKeyUsable { get; init; }
    public string? FirmwareVersion { get; init; }
    public FirmwarePackageValidation? FirmwareValidation { get; init; }
    public ContentIssue[] Issues { get; init; } = [];
}

/// <summary>Private local package identity. Do not include package digests in public diagnostics.</summary>
public sealed record FirmwarePackageExpectation
{
    public required string PackageDigest { get; init; }
    public required string Version { get; init; }
    public required int ArchiveCount { get; init; }

    public bool Matches(FirmwarePackageValidation? actual) => actual is not null &&
        PackageDigest.Equals(actual.PackageDigest, StringComparison.OrdinalIgnoreCase) &&
        Version == actual.Version && ArchiveCount == actual.ArchiveCount;
}

/// <summary>
/// Evidence from a native package validator. Reference hashes, complete CNMT closure and native
/// section reads are separate from Nintendo signature authentication and retail qualification.
/// This record contains a private local content digest; publish only a sanitized summary.
/// </summary>
public sealed record FirmwarePackageValidation
{
    public const string NativeIntegrityScope = "native-nca-section-cnmt-sha256-v1";
    public string Scope { get; init; } = "";
    public string PackageDigest { get; init; } = "";
    public string Version { get; init; } = "";
    public int ArchiveCount { get; init; }
    public int MetadataCount { get; init; }
    public int ValidatedArchiveCount { get; init; }
    public bool PackageComplete { get; init; }
    public bool InstalledInventoryVerified { get; init; }
    public string? ReceiptId { get; init; }
    public bool SignatureAuthenticated { get; init; }
    public bool RetailQualified { get; init; }
    public string BackendId { get; init; } = "";
    public string BackendRevision { get; init; } = "";
    public string AdapterVersion { get; init; } = "";

    public FirmwarePackageExpectation ToExpectation() => new()
        { PackageDigest = PackageDigest, Version = Version, ArchiveCount = ArchiveCount };
}

public sealed record FirmwareRecoveryResult
{
    public string Outcome { get; init; } = "";
    public bool Recovered { get; init; }
    public string? ReceiptId { get; init; }
}
public sealed record GameContentConfiguration
{
    public required string TitleId { get; init; }
    public string? UpdatePath { get; init; }
    public string[] DlcPaths { get; init; } = [];
    public bool Configured { get; init; }
    public bool Consumed { get; init; }
    public bool RequiresRestart { get; init; }
    public string[] Evidence { get; init; } = [];
}
public sealed record NativeModMount(string Id, string SourcePath, bool Enabled);
public sealed record NativeModState
{
    public string? TitleId { get; init; }
    public NativeModMount[] Mounts { get; init; } = [];
    public bool LoaderObserved { get; init; }
    public bool RequiresRestart { get; init; }
    public string Status { get; init; } = "unknown";
    public string? Reason { get; init; }
    public string[] Evidence { get; init; } = [];
}
public sealed record NativeSaveDescriptor
{
    public string BackendId { get; init; } = "";
    public string TitleId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string SaveDataId { get; init; } = "";
    public string Type { get; init; } = "";
    public string FormatId { get; init; } = "";
    public string FormatVersion { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool Existing { get; init; }
    public bool AdapterVerified { get; init; }
    public string Provenance { get; init; } = "";
}
public sealed record NativeSaveCatalog
{
    public NativeSaveDescriptor[] Entries { get; init; } = [];
    public string[] Limitations { get; init; } = [];
}
public sealed record RenderWindowState
{
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Fullscreen { get; init; }
    public bool Visible { get; init; }
    public bool? Minimized { get; init; }
    public int? PixelWidth { get; init; }
    public int? PixelHeight { get; init; }
    public bool? FullscreenRequested { get; init; }
    public bool? FullscreenRequestAccepted { get; init; }
    public bool? FullscreenSynchronizationCompleted { get; init; }
    public bool? FullscreenConfirmed { get; init; }
    public string? FullscreenEffectiveSource { get; init; }
    public bool? PresentationTimingSuspended { get; init; }
}
public sealed record RenderWindowChange
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool? Fullscreen { get; init; }
    public bool? Visible { get; init; }
}
public sealed record CoreInputDevice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public bool Connected { get; init; }
    public int? Player { get; init; }
}
public sealed record CoreInputDevices
{
    public CoreInputDevice[] Devices { get; init; } = [];
    public string InputTarget { get; init; } = "ui";
    public string? PhysicalObservation { get; init; }
    public bool KeyboardAvailable { get; init; }
    public bool KeyboardActive { get; init; }
    public string? KeyboardMapping { get; init; }
}
public sealed record CoreInputRouting
{
    public string InputTarget { get; init; } = "ui";
    public bool RequiresNeutral { get; init; }
}

public sealed partial class NxRuntime
{
    private static string ValidateTitle(string titleId)
    {
        if (!Regex.IsMatch(titleId, "^[0-9A-Fa-f]{16}$") || titleId.All(c => c == '0'))
            throw new NxException("invalid_argument", "A nonzero exact 16-hex title ID is required.");
        return titleId.ToUpperInvariant();
    }

    /// <summary>Inspects user-selected bytes with the native loader. Artwork is opt-in and bounded.</summary>
    public async Task<ContentInfo> InspectContentAsync(string contentPath, bool includeArtwork,
        string? dataRoot = null, CancellationToken cancellationToken = default)
    {
        var path = SafePaths.Absolute(contentPath);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new NxException("invalid_argument", "Content does not exist.");
        await _operations.WaitAsync(cancellationToken);
        try
        {
            EnsureUsable();
            var result = NxJson.Read<ContentInfo>(await CallSupportedAsync("content.inspect", new
            {
                contentPath = path, includeArtwork,
                dataRoot = dataRoot is null ? BackendDataRoot : SafePaths.Absolute(dataRoot)
            }, cancellationToken));
            if (result.Artwork is not null) _ = result.Artwork.Decode();
            if (result.Issues is null || result.Entries is null || result.Issues.Any(i => i is null) || result.Entries.Any(i => i is null) ||
                result.Issues.Length > 256 || result.Entries.Length > 4096)
                throw new NxException("protocol_mismatch", "Oversized native content metadata.");
            return result;
        }
        finally { _operations.Release(); }
    }

    /// <summary>Only checks local prerequisites. Never downloads or exports keys or firmware.</summary>
    public async Task<PrerequisiteReport> GetPrerequisitesAsync(string? dataRoot = null, CancellationToken cancellationToken = default) =>
        ValidatePrerequisiteReport(await NativeReadAsync<PrerequisiteReport>("prerequisites.get",
            new { dataRoot = dataRoot is null ? BackendDataRoot : SafePaths.Absolute(dataRoot) }, cancellationToken));

    private static void ValidateFirmwareExpectation(FirmwarePackageExpectation expected)
    {
        if (expected.PackageDigest is null || !Regex.IsMatch(expected.PackageDigest, "^[0-9A-Fa-f]{64}$") ||
            expected.Version is null || !Regex.IsMatch(expected.Version, @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$") ||
            expected.ArchiveCount is < 3 or > 4096)
            throw new NxException("invalid_argument", "A bounded exact native firmware package identity is required.");
    }

    private FirmwarePackageValidation ValidateFirmwareEvidence(FirmwarePackageValidation evidence, bool? installed = null)
    {
        if (evidence.Scope != FirmwarePackageValidation.NativeIntegrityScope ||
            evidence.PackageDigest is null || !Regex.IsMatch(evidence.PackageDigest, "^[0-9a-f]{64}$") ||
            evidence.Version is null || !Regex.IsMatch(evidence.Version, @"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$") ||
            evidence.ArchiveCount is < 3 or > 4096 || evidence.MetadataCount < 2 || evidence.MetadataCount > evidence.ArchiveCount ||
            evidence.ValidatedArchiveCount != evidence.ArchiveCount || !evidence.PackageComplete ||
            evidence.SignatureAuthenticated || evidence.RetailQualified ||
            (installed.HasValue && installed.Value != evidence.InstalledInventoryVerified) ||
            (evidence.InstalledInventoryVerified
                ? evidence.ReceiptId is null || !Regex.IsMatch(evidence.ReceiptId, "^[0-9a-f]{32}$")
                : evidence.ReceiptId is not null) ||
            evidence.BackendId != Backend.BackendId || evidence.BackendRevision != Backend.BackendRevision ||
            evidence.AdapterVersion != Backend.AdapterVersion)
            throw new NxException("protocol_mismatch", "Native firmware validation evidence is incomplete, unsupported or bound to a different worker.");
        return evidence;
    }

    private PrerequisiteReport ValidatePrerequisiteReport(PrerequisiteReport report)
    {
        if (report.Issues is null || report.Issues.Length > 256 || report.Issues.Any(issue => issue is null))
            throw new NxException("protocol_mismatch", "Native prerequisite issues are invalid or oversized.");
        if (report.FirmwareValidation is { } evidence)
        {
            ValidateFirmwareEvidence(evidence, installed: true);
            if (report.FirmwareVersion != evidence.Version)
                throw new NxException("protocol_mismatch", "Native firmware version and validated package identity disagree.");
        }
        return report;
    }

    private string ProfilePath(string workspaceBase, string profileId)
    {
        SafePaths.ValidateIdentifier(profileId, nameof(profileId));
        if (Backend.BackendId == "nxi") NxiProfileMigration.Ensure(workspaceBase, profileId);
        var path = Path.Combine(SafePaths.Absolute(workspaceBase), "backends", Backend.BackendId, profileId);
        SafePaths.RejectLinks(path);
        return path;
    }

    /// <summary>First-run inspection without creating a GPU or audio device.</summary>
    public Task<ContentInfo> InspectContentForProfileAsync(string contentPath, string workspaceBase,
        string profileId = "default", bool includeArtwork = false, CancellationToken cancellationToken = default) =>
        InspectContentAsync(contentPath, includeArtwork, ProfilePath(workspaceBase, profileId), cancellationToken);

    public Task<PrerequisiteReport> GetPrerequisitesForProfileAsync(string workspaceBase,
        string profileId = "default", CancellationToken cancellationToken = default) =>
        GetPrerequisitesAsync(ProfilePath(workspaceBase, profileId), cancellationToken);

    /// <summary>Native read-only validation using this exact isolated profile's existing keys.</summary>
    public async Task<FirmwarePackageValidation> InspectFirmwareForProfileAsync(string sourcePath, string workspaceBase,
        string profileId = "default", CancellationToken cancellationToken = default)
    {
        var source = SafePaths.Absolute(sourcePath);
        SafePaths.RejectLinks(source);
        if (!File.Exists(source) && !Directory.Exists(source)) throw new NxException("invalid_argument", "The local firmware source does not exist.");
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Created);
            var evidence = NxJson.Read<FirmwarePackageValidation>(await CallSupportedAsync("prerequisites.firmware.inspect",
                new { sourcePath = source, dataRoot = ProfilePath(workspaceBase, profileId) }, cancellationToken));
            return ValidateFirmwareEvidence(evidence, installed: false);
        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// Explicit recovery under the native profile lease, before opening native factories.
    /// Ambiguous or changed state is preserved and returns recovery_required.
    /// </summary>
    public async Task<FirmwareRecoveryResult> RecoverFirmwareForProfileAsync(string workspaceBase,
        string profileId = "default", CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Created);
            var result = NxJson.Read<FirmwareRecoveryResult>(await CallSupportedAsync("prerequisites.firmware.recover",
                new { dataRoot = ProfilePath(workspaceBase, profileId) }, cancellationToken));
            if (result.Outcome is not ("no_transaction" or "committed" or "rolled_back") ||
                result.Recovered != (result.Outcome != "no_transaction") ||
                (result.Recovered ? result.ReceiptId is null || !Regex.IsMatch(result.ReceiptId, "^[0-9a-f]{32}$") : result.ReceiptId is not null))
                throw new NxException("protocol_mismatch", "Native firmware recovery returned an invalid bounded outcome.");
            return result;
        }
        finally { _operations.Release(); }
    }

    /// <summary>Created-state import uses a transient native data lease; it never starts emulation.</summary>
    public Task<PrerequisiteReport> ImportPrerequisiteForProfileAsync(string kind, string sourcePath,
        string workspaceBase, string profileId = "default", CancellationToken cancellationToken = default) =>
        ImportPrerequisiteForProfileCoreAsync(kind, sourcePath, workspaceBase, profileId, null, cancellationToken);

    /// <summary>
    /// Import bound to prior native inspection. The caller must compare the returned receipt
    /// and a fresh worker's readback before reporting an effective verified installation.
    /// </summary>
    public Task<PrerequisiteReport> ImportFirmwareForProfileAsync(string sourcePath, string workspaceBase,
        FirmwarePackageExpectation expectation, string profileId = "default", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ValidateFirmwareExpectation(expectation);
        return ImportPrerequisiteForProfileCoreAsync("firmware", sourcePath, workspaceBase, profileId, expectation, cancellationToken);
    }

    private async Task<PrerequisiteReport> ImportPrerequisiteForProfileCoreAsync(string kind, string sourcePath,
        string workspaceBase, string profileId, FirmwarePackageExpectation? firmwareExpectation, CancellationToken cancellationToken)
    {
        if (kind is not ("keys" or "firmware")) throw new NxException("invalid_argument", "Choose keys or firmware.");
        var source = SafePaths.Absolute(sourcePath);
        if (!File.Exists(source) && !Directory.Exists(source)) throw new NxException("invalid_argument", "The local prerequisite does not exist.");
        var path = ProfilePath(workspaceBase, profileId);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Require(RuntimeState.Created);
            return ValidatePrerequisiteReport(NxJson.Read<PrerequisiteReport>(await CallSupportedAsync("prerequisites.import",
                new { kind, sourcePath = source, dataRoot = path, firmwareExpectation }, cancellationToken)));
        }
        finally { _operations.Release(); }
    }

    /// <summary>Explicitly imports user-supplied prerequisites into the initialized, isolated core profile.</summary>
    public async Task<PrerequisiteReport> ImportPrerequisiteAsync(string kind, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (kind is not ("keys" or "firmware")) throw new NxException("invalid_argument", "Choose keys or firmware.");
        var path = SafePaths.Absolute(sourcePath);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new NxException("invalid_argument", "The selected local prerequisite does not exist.");
        return ValidatePrerequisiteReport(await NativeBeforeLoadAsync<PrerequisiteReport>("prerequisites.import", new { kind, sourcePath = path }, cancellationToken));
    }

    public Task<GameContentConfiguration> GetContentConfigurationAsync(string titleId, CancellationToken cancellationToken = default) =>
        NativeReadAsync<GameContentConfiguration>("content.configuration.get", new { titleId = ValidateTitle(titleId) }, cancellationToken);

    public Task<GameContentConfiguration> SetContentConfigurationAsync(string titleId, string? updatePath,
        IEnumerable<string> dlcPaths, CancellationToken cancellationToken = default)
    {
        var paths = dlcPaths.Select(SafePaths.Absolute).ToArray();
        if (paths.Length > 512 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new NxException("invalid_argument", "Use at most 512 distinct local DLC paths.");
        var update = updatePath is null ? null : SafePaths.Absolute(updatePath);
        if (paths.Any(p => !File.Exists(p)) || (update is not null && !File.Exists(update)))
            throw new NxException("invalid_argument", "Selected update/DLC content must exist locally.");
        return NativeBeforeLoadAsync<GameContentConfiguration>("content.configuration.set",
            new { titleId = ValidateTitle(titleId), updatePath = update, dlcPaths = paths }, cancellationToken);
    }

    public Task<NativeModState> GetNativeModsAsync(string titleId, CancellationToken cancellationToken = default) =>
        NativeReadAsync<NativeModState>("mods.get",
            new { titleId = ValidateTitle(titleId) }, cancellationToken);

    /// <summary>Mount configuration is distinct from observed loader consumption and visible game effect.</summary>
    public Task<NativeModState> ConfigureNativeModsAsync(string titleId, IEnumerable<NativeModMount> mounts,
        CancellationToken cancellationToken = default) => ConfigureNativeModsCoreAsync(titleId, mounts, null, null, cancellationToken);

    /// <summary>Native loaders must verify the exact selected main executable before any bound patch is consumed.</summary>
    public Task<NativeModState> ConfigureNativeModsAsync(string titleId, IEnumerable<NativeModMount> mounts,
        string expectedBuildId, string backendRevision, CancellationToken cancellationToken = default)
    {
        if (expectedBuildId.Length != 64 || !expectedBuildId.All(Uri.IsHexDigit) || expectedBuildId.All(c => c == '0') ||
            backendRevision.Length != 40 || !backendRevision.All(Uri.IsHexDigit) ||
            !backendRevision.Equals(Backend.BackendRevision, StringComparison.OrdinalIgnoreCase))
            throw new NxException("version_mismatch", "Native mod bindings require the full nonzero main build ID and this exact backend revision.");
        return ConfigureNativeModsCoreAsync(titleId, mounts, expectedBuildId.ToUpperInvariant(), backendRevision.ToLowerInvariant(), cancellationToken);
    }

    private Task<NativeModState> ConfigureNativeModsCoreAsync(string titleId, IEnumerable<NativeModMount> mounts,
        string? expectedBuildId, string? backendRevision, CancellationToken cancellationToken)
    {
        var selected = mounts.ToArray();
        if (selected.Length > 128 || selected.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new NxException("invalid_argument", "Use at most 128 uniquely identified native mods.");
        foreach (var mount in selected)
        {
            SafePaths.ValidateIdentifier(mount.Id, nameof(mount.Id));
            var path = SafePaths.Absolute(mount.SourcePath);
            SafePaths.RejectLinks(path);
            if (!Directory.Exists(path)) throw new NxException("invalid_argument", "A mod source must be an existing local directory.");
        }
        return NativeBeforeLoadAsync<NativeModState>("mods.set",
            new { titleId = ValidateTitle(titleId), mounts = selected, expectedBuildId, backendRevision }, cancellationToken);
    }

    public async Task<NativeSaveCatalog> DiscoverNativeSavesAsync(string? titleId = null, CancellationToken cancellationToken = default)
    {
        if (BackendDataRoot is null || State == RuntimeState.Created) throw new NxException("invalid_state", "Save discovery requires an initialized core profile.");
        string? expectedTitle = titleId is null ? null : ValidateTitle(titleId);
        var catalog = await NativeReadAsync<NativeSaveCatalog>("saves.list",
            new { titleId = expectedTitle }, cancellationToken);
        if (catalog.Entries is null || catalog.Limitations is null || catalog.Entries.Any(e => e is null) || catalog.Entries.Length > 4096 || catalog.Limitations.Length > 256)
            throw new NxException("protocol_mismatch", "Invalid or oversized save catalog.");
        var entries = new NativeSaveDescriptor[catalog.Entries.Length];
        for (int index = 0; index < catalog.Entries.Length; index++)
        {
            var save = catalog.Entries[index];
            if (expectedTitle is not null && !expectedTitle.Equals(save.TitleId, StringComparison.OrdinalIgnoreCase))
                throw new NxException("protocol_mismatch", "Native save discovery returned a title outside the requested identity.");
            if (save.BackendId != Backend.BackendId)
                throw new NxException("protocol_mismatch", "Native save discovery returned an unsafe or unrelated mapping.");
            string relative = CanonicalNativeSavePath(save.RelativePath, Backend.BackendId);
            if (BackendDataRoot is null) throw new NxException("invalid_state", "Save discovery requires an initialized core profile.");
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(BackendDataRoot)) + Path.DirectorySeparatorChar;
            string mapped = Path.GetFullPath(Path.Combine(BackendDataRoot, relative));
            if (!mapped.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new NxException("protocol_mismatch", "Native save discovery escaped its isolated profile.");
            SafePaths.RejectLinks(mapped);
            entries[index] = relative == save.RelativePath ? save : save with { RelativePath = relative };
        }
        return catalog with { Entries = entries };
    }

    private static string CanonicalNativeSavePath(string? value, string backendId)
    {
        string? relative = value;
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || Path.IsPathRooted(relative) ||
            relative.Contains('\\') || relative.Any(char.IsControl) ||
            relative.Split('/').Any(part => part is ".." or "." or "" || part.Contains(':') || part.EndsWith('.') || part.EndsWith(' ')))
            throw new NxException("protocol_mismatch", "Native save discovery returned an unsafe or unrelated mapping.");
        return relative;
    }

    public async Task<RenderWindowState> GetRenderWindowAsync(CancellationToken cancellationToken = default) =>
        RenderWindowTelemetry.Validate(await NativeReadAsync<RenderWindowState>("window.get", null, cancellationToken));

    public async Task<RenderWindowState> SetRenderWindowAsync(RenderWindowChange change, CancellationToken cancellationToken = default)
    {
        if (change.Width is < 64 or > 16384 || change.Height is < 64 or > 16384)
            throw new NxException("invalid_argument", "Render dimensions must be 64..16384.");
        return RenderWindowTelemetry.Validate(await NativeReadAsync<RenderWindowState>("window.set", change, cancellationToken));
    }

    public Task<CoreInputDevices> GetCoreInputDevicesAsync(CancellationToken cancellationToken = default) =>
        NativeReadAsync<CoreInputDevices>("input.devices", null, cancellationToken);

    public Task<CoreInputDevices> AssignCoreInputAsync(int player, string? deviceId, CancellationToken cancellationToken = default)
    {
        if (player is < 0 or > 7 || deviceId is { Length: > 128 }) throw new NxException("invalid_argument", "Invalid controller assignment.");
        return NativeReadAsync<CoreInputDevices>("input.assign", new { player, deviceId }, cancellationToken);
    }

    public Task<CoreInputRouting> SetInputTargetAsync(bool guest, CancellationToken cancellationToken = default) =>
        NativeReadAsync<CoreInputRouting>("input.routing.set", new { target = guest ? "guest" : "ui" }, cancellationToken);

    private async Task<T> NativeReadAsync<T>(string operation, object? data, CancellationToken ct)
    {
        await _operations.WaitAsync(ct);
        try { EnsureUsable(); return NxJson.Read<T>(await CallSupportedAsync(operation, data, ct)); }
        finally { _operations.Release(); }
    }

    private async Task<T> NativeBeforeLoadAsync<T>(string operation, object? data, CancellationToken ct)
    {
        await _operations.WaitAsync(ct);
        try { Require(RuntimeState.Initialized); return NxJson.Read<T>(await CallSupportedAsync(operation, data, ct)); }
        finally { _operations.Release(); }
    }
}
