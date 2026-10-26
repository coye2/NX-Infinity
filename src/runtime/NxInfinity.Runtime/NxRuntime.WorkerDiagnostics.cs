using System.Text.Json;
using System.Text.Json.Nodes;

namespace NxInfinity.Runtime;

public sealed partial class NxRuntime
{
    public WorkerDiagnosticSnapshot WorkerDiagnostics => _worker.Diagnostics;

    /// <summary>Explicit local-only diagnostic write. Raw stderr must never be added to public exports.</summary>
    public async Task SavePrivateWorkerDiagnosticAsync(string destination, CancellationToken cancellationToken = default)
    {
        var path = SafePaths.Absolute(destination);
        SafePaths.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, new
                {
                    schemaVersion = 1, createdUtc = DateTimeOffset.UtcNow,
                    backendId = Backend.BackendId, backendRevision = Backend.BackendRevision,
                    process = WorkerDiagnostics, stderrTail = _worker.PrivateStderr,
                    privacy = "LOCAL ONLY: may contain private paths or player input. Not included in exported diagnostics."
                }, NxJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Existing SDK report plus safe worker evidence, including after disposal.</summary>
    public async Task<string> CreateWorkerDiagnosticReportAsync(CancellationToken cancellationToken = default)
    {
        var report = JsonNode.Parse(await CreateDiagnosticReportAsync(cancellationToken))!.AsObject();
        report["worker"] = JsonSerializer.SerializeToNode(WorkerDiagnostics, NxJson.Options);
        return report.ToJsonString(new JsonSerializerOptions(NxJson.Options) { WriteIndented = true });
    }
}
