using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace NxInfinity.Runtime;

/// <summary>
/// SDK-owned provenance for one verified local installation. This is a content
/// identity, not a publisher signature or continuous process attestation.
/// </summary>
internal sealed class VerifiedBackendInstallation
{
    private readonly string _manifestPath;
    private readonly string _backendId, _revision, _executable;
    private readonly string? _workingDirectory, _executableSha256;
    private readonly string[] _arguments;
    private readonly Dictionary<string, string> _artifacts;

    internal string ManifestSha256 { get; }

    internal VerifiedBackendInstallation(string manifestPath, string manifestSha256, BackendLaunch launch)
    {
        _manifestPath = manifestPath;
        ManifestSha256 = manifestSha256;
        _backendId = launch.BackendId;
        _revision = launch.Revision;
        _executable = launch.ExecutablePath;
        _workingDirectory = launch.WorkingDirectory;
        _executableSha256 = launch.ExecutableSha256;
        _arguments = launch.Arguments.ToArray();
        _artifacts = new Dictionary<string, string>(launch.ArtifactSha256, StringComparer.Ordinal);
    }

    internal static BackendLaunch Snapshot(BackendLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.Arguments is null || launch.ArtifactSha256 is null || launch.Arguments.Any(x => x is null))
            throw new NxException("invalid_argument", "Backend launch arguments and artifact inventory are required.");
        var snapshot = launch with
        {
            Arguments = launch.Arguments.ToArray(),
            ArtifactSha256 = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(launch.ArtifactSha256, StringComparer.Ordinal))
        };
        snapshot.VerifiedInstallation?.ValidateLaunch(snapshot);
        return snapshot;
    }

    internal void ValidateLaunch(BackendLaunch launch)
    {
        bool matches = launch.BackendId == _backendId &&
            string.Equals(launch.Revision, _revision, StringComparison.OrdinalIgnoreCase) &&
            launch.ExecutablePath == _executable && launch.WorkingDirectory == _workingDirectory &&
            string.Equals(launch.ExecutableSha256, _executableSha256, StringComparison.OrdinalIgnoreCase) &&
            launch.Arguments.SequenceEqual(_arguments, StringComparer.Ordinal) &&
            launch.ArtifactSha256.Count == _artifacts.Count &&
            _artifacts.All(entry => launch.ArtifactSha256.TryGetValue(entry.Key, out var value) &&
                string.Equals(value, entry.Value, StringComparison.OrdinalIgnoreCase));
        if (!matches)
            throw new NxException("version_mismatch", "Verified installation launch identity was changed; reload its manifest before selecting a different host.");
    }

    internal async Task VerifyManifestAsync(CancellationToken cancellationToken)
    {
        SafePaths.RejectLinks(_manifestPath);
        if (!File.Exists(_manifestPath))
            throw new NxException("missing_dependency", "The verified installation manifest was removed before startup.");
        if (new FileInfo(_manifestPath).Length > 4 * 1024 * 1024)
            throw new NxException("version_mismatch", "The verified installation manifest changed before startup.");
        await using var stream = File.OpenRead(_manifestPath);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, ManifestSha256, StringComparison.Ordinal))
            throw new NxException("version_mismatch", "The verified installation manifest changed before startup.");
    }
}
