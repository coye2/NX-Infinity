using System.Text.Json;

namespace NxInfinity.Runtime.Services;

public enum LibraryContentKind { Game, Update, DownloadableContent, Unidentified }
public enum LibrarySort { Name, RecentlyPlayed, RecentlyAdded }
public enum LibraryLaunchOutcome { Started, Completed, Failed, Interrupted }

public sealed record GameLibraryLimits(int MaximumEntries = 2000, int MaximumDiscoveryEntries = 10000,
    long MaximumContentBytes = 128L * 1024 * 1024 * 1024, int MaximumArtworkBytes = 8 * 1024 * 1024,
    int MaximumArtworkDimension = 8192, int MaximumLaunchesPerEntry = 32, int MaximumSettingsPerBackend = 256);
public sealed record ContentHashProgress(long BytesRead, long TotalBytes);
public sealed record ContentRegistrationRequest(string AbsolutePath, LibraryContentKind Kind = LibraryContentKind.Game,
    string? DisplayName = null, string? RelatedGameEntryId = null);
public sealed record RegisteredContentFile(string AbsolutePath, string Sha256, long Size, DateTimeOffset LastWriteUtc);
public sealed record LocalContentCandidate(string AbsolutePath, string FileName, string Extension, long Size,
    DateTimeOffset LastWriteUtc);
public sealed record LibraryDiscoveryIssue(string RelativePath, string Code);
public sealed record LibraryDiscoveryResult(IReadOnlyList<LocalContentCandidate> Candidates,
    IReadOnlyList<LibraryDiscoveryIssue> Issues, bool Truncated);

/// <summary>An adapter caller's observation bound to registered bytes. Storage does not authenticate the caller.</summary>
public sealed record LibraryCoreObservation(string BackendId, string BackendRevision, string ContentSha256,
    string? TitleId = null, string? BuildId = null, string? DisplayName = null, string? DisplayVersion = null,
    bool LoaderAccepted = false, string Provenance = "", DateTimeOffset? ObservedUtc = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ContentKind = null);
public sealed record LibraryArtwork(string Sha256, string MediaType, int Width, int Height, int Bytes);
public sealed record LibraryArtworkData(LibraryArtwork Metadata, byte[] Bytes);
public sealed record LibraryLaunch(string LaunchId, string BackendId, string BackendRevision,
    DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, LibraryLaunchOutcome Outcome, string? ErrorCode);
public sealed record LibrarySettingRequests(string BackendId, string BackendRevision,
    IReadOnlyDictionary<string, JsonElement> Values, DateTimeOffset UpdatedUtc);
public sealed record LibrarySettingRequestView(LibrarySettingRequests? Stored, bool RequiresReview);
public sealed record GameLibraryEntry(int SchemaVersion, string EntryId, LibraryContentKind Kind,
    RegisteredContentFile Content, string DisplayName, bool Favorite, IReadOnlyList<string> Tags, string? Notes,
    LibraryArtwork? Artwork, DateTimeOffset AddedUtc, DateTimeOffset UpdatedUtc,
    IReadOnlyList<LibraryCoreObservation> Observations, IReadOnlyList<LibraryLaunch> Launches,
    IReadOnlyList<LibrarySettingRequests> SettingRequests, string? RelatedGameEntryId = null);
public sealed record LibraryQuery(string? Search = null, bool FavoritesOnly = false,
    LibraryContentKind? Kind = null, LibrarySort Sort = LibrarySort.Name, int Offset = 0, int Limit = 200);
public sealed record LibraryLaunchTarget(GameLibraryEntry Entry, string AbsolutePath,
    string VerifiedSha256, DateTimeOffset VerifiedUtc);

