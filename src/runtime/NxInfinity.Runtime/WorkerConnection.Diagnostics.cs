using System.Text;
using System.Text.RegularExpressions;

namespace NxInfinity.Runtime;

/// <summary>Bounded safe process evidence; contains no paths or raw stderr.</summary>
public sealed record WorkerDiagnosticSnapshot(bool HasExited, int? ExitCode, string? ExitCodeHex,
    string? FailureCode, string? FailureType, string? LastCommand, bool TerminationRequested,
    long StderrCharactersObserved, int StderrCharactersRetained, bool StderrTruncated)
{
    /// <summary>The last attempted protocol write when the first connection failure was observed; not a causal claim.</summary>
    public string? LastCommandAtFailure { get; init; }
    public WorkerFailureEvidence FailureEvidence { get; init; } = WorkerFailureEvidence.Parse(null);
}

internal sealed partial class WorkerConnection
{
    private const int PrivateStderrLimit = WorkerFailureEvidence.MaximumTailCharacters;
    private readonly object _diagnosticLock = new();
    private readonly StringBuilder _stderrTail = new();
    private long _stderrCharacters;
    private int? _exitCode;
    private bool _exitObserved;
    private bool _terminationRequested;
    private string? _lastCommand;
    private string? _lastCommandAtFailure;

    internal WorkerDiagnosticSnapshot Diagnostics
    {
        get
        {
            CaptureExitCode();
            lock (_diagnosticLock)
            {
                var failure = _failure;
                return new(_exitObserved, _exitCode, _exitCode is { } code ? $"0x{unchecked((uint)code):X8}" : null,
                    Token(failure is NxException nx ? nx.Code : null), Token(failure?.GetType().Name),
                    _lastCommand, _terminationRequested, _stderrCharacters, _stderrTail.Length,
                    _stderrCharacters > _stderrTail.Length)
                {
                    LastCommandAtFailure = _lastCommandAtFailure,
                    FailureEvidence = WorkerFailureEvidence.Parse(_stderrTail.ToString(), _stderrCharacters > _stderrTail.Length)
                };
            }
        }
    }

    internal string PrivateStderr { get { lock (_diagnosticLock) return _stderrTail.ToString(); } }
    private static string? Token(string? value) => value is { Length: > 0 and <= 128 } &&
        Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_.-]*$") ? value : null;
    private void RememberCommand(string method) { lock (_diagnosticLock) _lastCommand = Token(method); }
    private void AppendPrivateStderr(ReadOnlySpan<char> value)
    {
        lock (_diagnosticLock)
        {
            _stderrCharacters = _stderrCharacters > long.MaxValue - value.Length ? long.MaxValue : _stderrCharacters + value.Length;
            if (value.Length >= PrivateStderrLimit) { _stderrTail.Clear(); value = value[^PrivateStderrLimit..]; }
            int excess = _stderrTail.Length + value.Length - PrivateStderrLimit;
            if (excess > 0) _stderrTail.Remove(0, excess);
            _stderrTail.Append(value);
        }
    }

    private void CaptureExitCode()
    {
        try
        {
            if (_exitObserved) return;
            if (!_process.HasExited) return;
            int code = _process.ExitCode;
            lock (_diagnosticLock) { _exitCode = code; _exitObserved = true; }
        }
        catch (InvalidOperationException) { /* Cached evidence survives Process.Dispose. */ }
        catch (System.ComponentModel.Win32Exception) { /* Exit status was unavailable; do not invent it. */ }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            CaptureExitCode();
            // A worker may exit immediately after flushing its final response.
            // Let the protocol reader consume that response before failing pending
            // calls. The bound also handles inherited pipes held by a grandchild.
            try { await _stdout.WaitAsync(TimeSpan.FromSeconds(2), _lifetime.Token).ConfigureAwait(false); }
            catch (TimeoutException) { }
            if (!_disposed) Fault(new NxException("core_exited", "The native core process exited. Its exit status is retained in Diagnostics."));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (InvalidOperationException) when (_disposed) { }
        catch (System.ComponentModel.Win32Exception) { /* EOF/command handling remains active. */ }
    }
}

