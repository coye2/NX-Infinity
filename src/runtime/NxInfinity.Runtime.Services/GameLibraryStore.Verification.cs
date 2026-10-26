using System.Security.Cryptography;
using System.Text;

namespace NxInfinity.Runtime.Services;

public sealed partial class GameLibraryStore
{
    private sealed record ContentVerificationReceipt(int SchemaVersion, string Sha256, long Size, string Version);
    private readonly Func<FileStream, string?> _fileVersion = ContentFileVersion.Read;

    // The test seam exercises cache invalidation without pretending Linux is NTFS.
    internal GameLibraryStore(NxWorkspace workspace, GameLibraryLimits? limits, Func<FileStream, string?> fileVersion)
        : this(workspace, limits) => _fileVersion = fileVersion;

    private string VerificationPath(string path) => Paths.Under(Paths.Under(_root, "verification"),
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".json");

    private bool HasVerifiedVersion(RegisteredContentFile content, string? version)
    {
        if (version is null) return false;
        try
        {
            var receipt = StoreIo.ReadJson<ContentVerificationReceipt>(VerificationPath(content.AbsolutePath), 4096);
            return receipt.SchemaVersion == 1 && receipt.Size == content.Size && receipt.Sha256 == content.Sha256 && receipt.Version == version;
        }
        catch (Exception error) when (error is ServiceException or IOException or UnauthorizedAccessException)
        { return false; } // Optional acceleration never turns damaged receipts into trusted content.
    }

    private void RememberVerifiedVersion(RegisteredContentFile content, string? version)
    {
        if (version is null) return;
        try
        {
            Paths.EnsureDirectory(_root, "verification");
            StoreIo.AtomicJson(VerificationPath(content.AbsolutePath), new ContentVerificationReceipt(1, content.Sha256, content.Size, version));
        }
        catch (Exception error) when (error is ServiceException or IOException or UnauthorizedAccessException) { }
    }

    private async Task<RegisteredContentFile> VerifyContentAsync(RegisteredContentFile content,
        IProgress<ContentHashProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = ReadableFile(ContentPath(content.AbsolutePath));
        // FileShare.Read excludes cooperating writers and replacements while the
        // receipt is checked. The native loader still verifies the loaded title.
        using (var file = new FileStream(content.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var version = _fileVersion(file);
            if (file.Length == content.Size && info.LastWriteTimeUtc == content.LastWriteUtc.UtcDateTime &&
                HasVerifiedVersion(content, version) && version == _fileVersion(file))
            { ct.ThrowIfCancellationRequested(); return content; }
        }
        return await HashContentAsync(content.AbsolutePath, progress, ct).ConfigureAwait(false);
    }
}
