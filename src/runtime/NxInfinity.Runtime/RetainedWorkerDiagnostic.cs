using System.Text.Json;

namespace NxInfinity.Runtime;

/// <summary>A safe summary of a previous failed worker, which may predate the current session.</summary>
public sealed record RetainedWorkerDiagnostic(string BackendId, string Status,
    DateTimeOffset? CreatedUtc = null, string? BackendRevision = null,
    int? ExitCode = null, string? ExitCodeHex = null, WorkerFailureEvidence? FailureEvidence = null)
{
    private const int MaximumFileBytes = 512 * 1024;

    /// <summary>
    /// Reads only the named core's app-owned retained diagnostic. The private
    /// file is never changed and its contents are never returned verbatim.
    /// </summary>
    public static async Task<RetainedWorkerDiagnostic> ReadAsync(string dataRoot, string backendId,
        CancellationToken cancellationToken = default)
    {
        if (backendId is not ("nxi"))
            throw new NxException("invalid_argument", "A supported core ID is required.");
        try
        {
            var path = Path.Combine(SafePaths.Absolute(dataRoot), "diagnostics", "last-worker-" + backendId + ".private.json");
            SafePaths.RejectLinks(path);
            if (!File.Exists(path)) return new(backendId, "missing");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > MaximumFileBytes) return new(backendId, "invalid");
            var bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            if (input.ReadByte() != -1) return new(backendId, "invalid");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1 ||
                !root.TryGetProperty("backendId", out var core) || core.ValueKind != JsonValueKind.String || core.GetString() != backendId ||
                !root.TryGetProperty("backendRevision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.String ||
                revisionValue.GetString() is not { Length: 40 } revision || !revision.All(Uri.IsHexDigit) ||
                !root.TryGetProperty("createdUtc", out var date) || date.ValueKind != JsonValueKind.String || !date.TryGetDateTimeOffset(out var createdUtc) ||
                !root.TryGetProperty("stderrTail", out var stderr) || stderr.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("process", out var process) || process.ValueKind != JsonValueKind.Object)
                return new(backendId, "invalid");
            int? exitCode = process.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out int code) ? code : null;
            bool partialFirstLine = process.TryGetProperty("stderrTruncated", out var truncated) && truncated.ValueKind == JsonValueKind.True;
            return new(backendId, "captured", createdUtc, revision, exitCode,
                exitCode is { } status ? $"0x{unchecked((uint)status):X8}" : null,
                WorkerFailureEvidence.Parse(stderr.GetString(), partialFirstLine));
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return new(backendId, "invalid"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NxException or InvalidOperationException)
        { return new(backendId, "unavailable"); }
    }
}

