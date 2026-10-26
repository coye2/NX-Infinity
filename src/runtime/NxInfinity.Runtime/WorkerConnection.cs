using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NxInfinity.Runtime;

internal sealed partial class WorkerConnection : IAsyncDisposable
{
    private const int MaximumLineBytes = 1024 * 1024;
    private readonly Process _process;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<RuntimeEvent> _events = Channel.CreateBounded<RuntimeEvent>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false });
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly Task _exit;
    private long _nextId;
    private long _droppedEvents;
    private Exception? _failure;
    private volatile bool _disposed;

    internal bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }
    internal bool IsFaulted => _failure is not null;
    internal long DroppedEvents => Interlocked.Read(ref _droppedEvents);
    internal ChannelReader<RuntimeEvent> Events => _events.Reader;
    internal int ProcessId => _process.Id;
    internal Action<RuntimeState>? NativeStateChanged { get; set; }

    private WorkerConnection(Process process)
    {
        _process = process;
        _stdout = ReadProtocolAsync();
        _stderr = DrainLogsAsync();
        _exit = ObserveExitAsync();
    }

    internal static async Task<WorkerConnection> StartAsync(BackendLaunch launch, CancellationToken ct)
    {
        launch.VerifiedInstallation?.ValidateLaunch(launch);
        SafePaths.ValidateIdentifier(launch.BackendId, nameof(launch.BackendId));
        if (launch.Revision.Length != 40 || !launch.Revision.All(Uri.IsHexDigit))
            throw new NxException("invalid_argument", "A pinned 40-digit backend source revision is required.");
        if (launch.CommandTimeout <= TimeSpan.Zero || launch.StartupTimeout <= TimeSpan.Zero ||
            launch.ContentTimeout is { } contentTimeout && contentTimeout <= TimeSpan.Zero ||
            launch.InitializationTimeout is { } initializationTimeout && initializationTimeout <= TimeSpan.Zero)
            throw new NxException("invalid_argument", "Timeouts must be positive.");
        var exe = SafePaths.Absolute(launch.ExecutablePath);
        if (!File.Exists(exe)) throw new NxException("missing_dependency", "NX core host executable was not found. Build that backend first.");
        foreach (var (artifactPath, expectedHash) in launch.ArtifactSha256)
        {
            var full = SafePaths.Absolute(artifactPath);
            SafePaths.RejectLinks(full);
            if (!File.Exists(full)) throw new NxException("missing_dependency", "A verified core artifact was removed before startup.");
            await using var artifact = File.OpenRead(full);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(artifact, ct));
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new NxException("version_mismatch", "A core artifact changed between installation verification and startup.");
        }
        if (launch.ExecutableSha256 is { } expected)
        {
            await using var input = File.OpenRead(exe);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new NxException("version_mismatch", "Core-host executable hash does not match its installation manifest.");
        }
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkingDirectory is { } wd ? SafePaths.Absolute(wd) : Path.GetDirectoryName(exe)!
        };
        foreach (var argument in launch.Arguments) info.ArgumentList.Add(argument);
        info.Environment["NX_API_PROTOCOL"] = "1";
        // Inventory verification precedes this final manifest check. The exact
        // validated bytes, not a caller-supplied digest, bind a fresh start.
        if (launch.VerifiedInstallation is { } verified)
            await verified.VerifyManifestAsync(ct);
        ct.ThrowIfCancellationRequested();
        try
        {
            var process = Process.Start(info) ?? throw new NxException("missing_dependency", "Core worker could not start.");
            return new WorkerConnection(process);
        }
        catch (Exception e) when (e is not NxException)
        {
            throw new NxException("missing_dependency", "NX core host could not start; inspect its runtime dependencies.", e);
        }
    }

    internal async Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        // A blocked applet must be answerable while load/start is outstanding.
        // Only this allowlisted interaction acknowledgment bypasses command order.
        bool serializeCommand = method != "interaction.respond";
        if (serializeCommand) await _serial.WaitAsync(ct);
        try
        {
            if (_disposed) throw new NxException("core_exited", "The core connection was disposed.");
            if (HasExited)
            {
                CaptureExitCode();
                var exited = new NxException("core_exited", "The core worker has exited.");
                Fault(exited); throw exited;
            }
            if (_failure is { } failure) throw new NxException("connection_faulted", "The core connection is faulted; dispose and create a fresh session.", failure);
            long id = 0;
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(timeout);
            try
            {
                await _writer.WaitAsync(deadline.Token);
                try
                {
                    id = Interlocked.Increment(ref _nextId);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, id, method, @params = parameters ?? new { } }, NxJson.Options);
                    if (bytes.Length >= MaximumLineBytes) throw new NxException("invalid_argument", "Protocol request exceeds 1 MiB.");
                    _pending[id] = completion;
                    // A rejected call after a crash must not replace the actual
                    // last protocol command with a cleanup stop/dispose attempt.
                    RememberCommand(method);
                    await _process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token);
                    await _process.StandardInput.BaseStream.WriteAsync("\n"u8.ToArray(), deadline.Token);
                    await _process.StandardInput.BaseStream.FlushAsync(deadline.Token);
                }
                finally { _writer.Release(); }
                return await completion.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException e)
            {
                var error = new NxException(ct.IsCancellationRequested ? "cancelled" : "timeout",
                    "Core command did not complete. Its runtime effect is unknown; the connection is faulted.", e);
                Fault(error);
                throw error;
            }
            catch (IOException e)
            {
                var error = new NxException("core_exited", "The core worker pipe closed during a command.", e);
                Fault(error);
                throw error;
            }
            finally { _pending.TryRemove(id, out _); }
        }
        finally { if (serializeCommand) _serial.Release(); }
    }

    private async Task ReadProtocolAsync()
    {
        var buffer = new byte[8192];
        using var lines = new ProtocolLineBuffer(MaximumLineBytes);
        Action<ReadOnlyMemory<byte>> consume = HandleLine;
        try
        {
            while (true)
            {
                // Protocol replies must continue while the frontend is drawing,
                // resizing, or waiting for another UI continuation.
                var read = await _process.StandardOutput.BaseStream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (read == 0) break;
                lines.Append(buffer.AsMemory(0, read), consume);
            }
            lines.Complete();
            if (!_disposed) Fault(new NxException("core_exited", "Core worker closed its output stream."));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { Fault(e is NxException ? e : new NxException("protocol_mismatch", "Invalid core response.", e)); }
        finally { _events.Writer.TryComplete(); }
    }

    private void HandleLine(ReadOnlyMemory<byte> line)
    {
        if (line.IsEmpty) throw new NxException("protocol_mismatch", "Empty worker response.");
        // The framing callback is synchronous. This memory remains valid until
        // the document is disposed; escaping events/results are cloned below.
        using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var version) || !version.TryGetInt32(out var v) || v != 1)
            throw new NxException("protocol_mismatch", "Worker protocol version mismatch.");
        if (root.TryGetProperty("event", out var name))
        {
            if (name.ValueKind != JsonValueKind.String || !root.TryGetProperty("data", out var data))
                throw new NxException("protocol_mismatch", "Invalid core event.");
            if (name.GetString() is "state" or "state.changed" or "lifecycle" && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("state", out var runtimeState) && runtimeState.ValueKind == JsonValueKind.String &&
                Enum.TryParse<RuntimeState>(runtimeState.GetString(), true, out var state) &&
                state is RuntimeState.Running or RuntimeState.Paused or RuntimeState.Stopped or RuntimeState.Faulted)
                NativeStateChanged?.Invoke(state); // Internal state observer only; no user callbacks run here.
            if (!_events.Writer.TryWrite(new RuntimeEvent(name.GetString()!, data.Clone())))
            {
                if (name.GetString()!.StartsWith("diagnostic", StringComparison.Ordinal) || name.GetString()!.StartsWith("log", StringComparison.Ordinal))
                    Interlocked.Increment(ref _droppedEvents);
                else throw new NxException("event_overflow", "A required core event could not be delivered. Consume the event stream continuously.");
            }
            return;
        }
        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || !_pending.TryGetValue(id, out var target))
            throw new NxException("protocol_mismatch", "Core response has an unknown request ID.");
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new NxException("protocol_mismatch", "Core response is missing status.");
        if (ok.GetBoolean())
        {
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new NxException("protocol_mismatch", "Core response is missing a result object.");
            if (!target.TrySetResult(result.Clone())) throw new NxException("protocol_mismatch", "Duplicate response.");
        }
        else
        {
            var error = root.GetProperty("error");
            var code = error.GetProperty("code").GetString() ?? "core_error";
            // Error messages are local; export reports omit them to avoid sensitive paths.
            var message = error.GetProperty("message").GetString() ?? "Core operation failed.";
            if (!target.TrySetException(new NxException(code, message))) throw new NxException("protocol_mismatch", "Duplicate response.");
        }
    }

    private async Task DrainLogsAsync()
    {
        // Drain continuously so native logging cannot block protocol progress.
        // Keep only a bounded private tail. Public reports export counters,
        // process status and fixed allowlisted classifications, never this text.
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false)) > 0)
                AppendPrivateStderr(buffer.AsSpan(0, read));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) when (_disposed) { }
    }

    private void Fault(Exception error)
    {
        Exception? first;
        lock (_diagnosticLock)
        {
            first = Interlocked.CompareExchange(ref _failure, error, null);
            if (first is null) _lastCommandAtFailure = _lastCommand;
        }
        var failure = first ?? error;
        foreach (var entry in _pending) entry.Value.TrySetException(failure);
        if (first is null)
        {
            // A dead worker must become terminal without waiting for the next
            // pause/input/telemetry command. This observer only updates SDK state.
            NativeStateChanged?.Invoke(RuntimeState.Faulted);
            _events.Writer.TryComplete();
        }
    }

    internal async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        if (HasExited) return true;
        using var deadline = new CancellationTokenSource(timeout);
        try { await _process.WaitForExitAsync(deadline.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    internal async Task<bool> TerminateAsync()
    {
        if (HasExited) return true;
        lock (_diagnosticLock) _terminationRequested = true;
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        return await WaitForExitAsync(TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // A naturally exited worker can still have unread bytes in its stderr
        // pipe. Preserve that tail before cancelling the reader; never wait
        // indefinitely for a descendant which inherited the pipe handle.
        if (HasExited)
        {
            try { await _stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        _lifetime.Cancel();
        try { _process.StandardInput.Close(); } catch (IOException) { }
        if (!HasExited) await TerminateAsync();
        try { await Task.WhenAll(_stdout, _stderr, _exit).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        CaptureExitCode();
        _events.Writer.TryComplete();
        _process.Dispose();
        _lifetime.Dispose();
    }
}
