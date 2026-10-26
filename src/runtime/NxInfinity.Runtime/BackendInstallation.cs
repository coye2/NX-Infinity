using System.Security.Cryptography;
using System.Text.Json;

namespace NxInfinity.Runtime;

/// <summary>Local build output manifest; never downloaded or silently promoted by the SDK.</summary>
public sealed record BackendInstallation
{
    public int ManifestVersion { get; init; } = 1;
    public string BackendId { get; init; } = "";
    public string BackendName { get; init; } = "";
    public string Revision { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string Qualification { get; init; } = "unqualified";
    public string EntryPoint { get; init; } = "";
    public bool ManagedAssembly { get; init; }
    public Dictionary<string, string> Files { get; init; } = [];

    public static async Task<BackendLaunch> LoadAsync(string manifestPath, string? dotnetPath = null,
        bool allowUnqualified = false, CancellationToken cancellationToken = default)
    {
        manifestPath = SafePaths.Absolute(manifestPath);
        SafePaths.RejectLinks(manifestPath);
        if (!File.Exists(manifestPath)) throw new NxException("missing_dependency", "Backend build manifest does not exist.");
        if (new FileInfo(manifestPath).Length > 4 * 1024 * 1024) throw new NxException("invalid_argument", "Backend manifest is too large.");
        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        if (bytes.Length > 4 * 1024 * 1024) throw new NxException("invalid_argument", "Backend manifest is too large.");
        var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        static void RejectDuplicates(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                { if (!names.Add(property.Name)) throw new NxException("invalid_argument", "Duplicate manifest fields."); RejectDuplicates(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
        }
        RejectDuplicates(document.RootElement);
        var data = JsonSerializer.Deserialize<BackendInstallation>(bytes, NxJson.Options)
            ?? throw new NxException("invalid_argument", "Invalid backend installation manifest.");
        if (data.ManifestVersion != 1 || data.Files is null || data.Files.Count is < 1 or > 10000 ||
            string.IsNullOrWhiteSpace(data.BackendId) || string.IsNullOrWhiteSpace(data.Revision) ||
            string.IsNullOrWhiteSpace(data.Qualification) || string.IsNullOrWhiteSpace(data.EntryPoint))
            throw new NxException("version_mismatch", "Unsupported backend manifest version or file count.");
        SafePaths.ValidateIdentifier(data.BackendId, nameof(data.BackendId));
        if (data.BackendId != "nxi")
            throw new NxException("unsupported", "This NX Infinity installation uses only NX Infinity Core.");
        if (data.Qualification != "qualified" && !allowUnqualified)
            throw new NxException("unqualified_backend", "Explicit selection of this unqualified backend is required.");
        var directory = Path.GetDirectoryName(manifestPath)!;
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        static string Contained(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
                relative.Split('/').Any(x => x is ".." or "." or "" || x.EndsWith('.') || x.EndsWith(' ') || x.Any(c => c < 32 || "<>:\"|?*".Contains(c))))
                throw new NxException("invalid_argument", "Backend artifact path must be a safe relative path.");
            var resolved = Path.GetFullPath(Path.Combine(root, relative));
            SafePaths.RejectLinks(resolved);
            return resolved;
        }
        foreach (var (relative, expected) in data.Files)
        {
            if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new NxException("invalid_argument", "Artifact SHA-256 must contain exactly 64 hexadecimal characters.");
            var path = Contained(directory, relative);
            if (!File.Exists(path)) throw new NxException("missing_dependency", $"Required backend artifact missing: {relative}.");
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new NxException("version_mismatch", $"Backend artifact hash mismatch: {relative}.");
            artifacts.Add(path, expected);
        }
        var entry = Contained(directory, data.EntryPoint);
        if (!data.Files.TryGetValue(data.EntryPoint, out var entryHash))
            throw new NxException("invalid_argument", "Backend entry point must be included in the integrity manifest.");
        if (data.ManagedAssembly && dotnetPath is null)
            throw new NxException("missing_dependency", "An explicit absolute dotnet executable path is required for a DLL host.");
        string executable = data.ManagedAssembly ? SafePaths.Absolute(dotnetPath!) : entry;
        string executableHash = entryHash;
        if (data.ManagedAssembly)
        {
            // Bind the selected managed host before creating SDK-owned launch
            // provenance. Later services can recheck this same hash without
            // changing the verified invocation or discarding its identity.
            SafePaths.RejectLinks(executable);
            if (!File.Exists(executable))
                throw new NxException("missing_dependency", "The selected managed core host executable was not found.");
            await using var host = File.OpenRead(executable);
            executableHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(host, cancellationToken));
        }
        var launch = new BackendLaunch
        {
            BackendId = data.BackendId, Revision = data.Revision,
            ExecutablePath = executable,
            Arguments = data.ManagedAssembly ? [entry] : [],
            ExecutableSha256 = executableHash,
            ArtifactSha256 = artifacts,
            WorkingDirectory = directory, AllowUnqualified = allowUnqualified
        };
        var verified = new VerifiedBackendInstallation(manifestPath, manifestSha256, launch);
        // Recheck the exact original manifest after the inventory was hashed.
        // A mutable path alone must never confer enhancement identity.
        await verified.VerifyManifestAsync(cancellationToken);
        return launch with { VerifiedInstallation = verified };
    }
}
