using System.Security.Cryptography;
using System.Text.Json;

namespace NxInfinity.Runtime;

/// <summary>A review token binds explicit recovery to one unchanged session marker.</summary>
public sealed record SessionRecoveryReview(string BackendId, string ProfileId, bool Required, string? MarkerSha256);
public sealed record SessionRecoveryReceipt(string BackendId, string ProfileId, string MarkerSha256,
    string ArchiveFileName, DateTimeOffset AcknowledgedUtc);

/// <summary>Archives an abandoned session marker. Never edits saves or repairs transactions.</summary>
public static class SessionRecovery
{
    private const int MaximumMarkerBytes = 65536;
    private static readonly string[] Transactions =
    [
        ".nx-save-transaction.json", ".nx-firmware-transaction.json",
        ".nx-mod-transaction.json", ".nx-content-transaction.json"
    ];

    public static SessionRecoveryReview Inspect(string dataRoot, string backendId, string profileId = "default")
    {
        var root = ProfileRoot(dataRoot, backendId, profileId);
        if (!Directory.Exists(root)) return new(backendId, profileId, false, null);
        using var client = Lease(root, ".nx-client.lock");
        using var runtime = Lease(root, ".nx-runtime.lock");
        CheckTransactions(root);
        var bytes = ReadMarker(root, backendId);
        return new(backendId, profileId, bytes is not null, bytes is null ? null : Hash(bytes));
    }

    /// <summary>Requires a fresh review and an explicit user decision. No permanent bypass is installed.</summary>
    public static SessionRecoveryReceipt Acknowledge(string dataRoot, SessionRecoveryReview review, bool acknowledged)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!acknowledged) throw new NxException("recovery_not_acknowledged", "Recovery needs an explicit user decision.");
        if (!review.Required || review.MarkerSha256 is not { Length: 64 } || !review.MarkerSha256.All(Uri.IsHexDigit))
            throw new NxException("recovery_review_required", "Review the current session marker before recovery.");
        var root = ProfileRoot(dataRoot, review.BackendId, review.ProfileId);
        if (!Directory.Exists(root)) throw new NxException("recovery_changed", "The reviewed profile no longer exists.");
        using var client = Lease(root, ".nx-client.lock");
        using var runtime = Lease(root, ".nx-runtime.lock");
        CheckTransactions(root);
        var bytes = ReadMarker(root, review.BackendId);
        if (bytes is null || !string.Equals(Hash(bytes), review.MarkerSha256, StringComparison.OrdinalIgnoreCase))
            throw new NxException("recovery_changed", "The session marker changed after review. Nothing was removed; review it again.");

        var archive = Path.Combine(root, "recovery", "sessions");
        SafePaths.RejectLinks(archive);
        Directory.CreateDirectory(archive);
        SafePaths.RejectLinks(archive);
        var utc = DateTimeOffset.UtcNow;
        var name = utc.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N") + ".json";
        var destination = Path.Combine(archive, name);
        SafePaths.RejectLinks(destination);
        // Same-profile rename preserves the original bytes, without a delete-before-copy
        // window. Both NX leases remain held until the move completes. A failure leaves
        // the original marker in place. Save/firmware/mod/content data is never opened.
        File.Move(Path.Combine(root, ".nx-unclean.json"), destination, overwrite: false);
        return new(review.BackendId, review.ProfileId, review.MarkerSha256, name, utc);
    }

    private static string ProfileRoot(string dataRoot, string backendId, string profileId)
    {
        SafePaths.ValidateIdentifier(backendId, nameof(backendId));
        SafePaths.ValidateIdentifier(profileId, nameof(profileId));
        var root = Path.Combine(SafePaths.Absolute(dataRoot), "backends", backendId, profileId);
        SafePaths.RejectLinks(root);
        return root;
    }

    private static FileStream Lease(string root, string name)
    {
        var path = Path.Combine(root, name);
        SafePaths.RejectLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new NxException("data_in_use", "Close NX Infinity and this core before recovery. An active profile lock was not bypassed.", e); }
    }

    private static void CheckTransactions(string root)
    {
        foreach (var name in Transactions)
            foreach (var suffix in new[] { "", ".new" })
            {
                var path = Path.Combine(root, name + suffix);
                SafePaths.RejectLinks(path);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new NxException("recovery_required", "This profile has an interrupted data transaction. Session recovery cannot clear it; all data was preserved.");
            }
    }

    private static byte[]? ReadMarker(string root, string backendId)
    {
        var path = Path.Combine(root, ".nx-unclean.json");
        SafePaths.RejectLinks(path);
        if (Directory.Exists(path)) throw new NxException("recovery_marker_invalid", "The session marker is not a regular file.");
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumMarkerBytes)
            throw new NxException("recovery_marker_invalid", "The session marker is empty or too large; it was preserved.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("backendId", out var backend) ||
                backend.ValueKind != JsonValueKind.String || backend.GetString() != backendId)
                throw new NxException("recovery_marker_invalid", "The session marker does not match this core; it was preserved.");
        }
        catch (JsonException e) { throw new NxException("recovery_marker_invalid", "The session marker is invalid JSON; it was preserved.", e); }
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
