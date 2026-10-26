namespace NxInfinity.Runtime;

/// <summary>
/// A bounded rolling capture bound to one exact NxRuntime object and revision.
/// Feed telemetry from that runtime. Replayed packets are deduplicated by native
/// segment and interval number; missing history is reported without interpolation.
/// For an A/B or user-requested measurement boundary, call StartFromNextInterval
/// before Observe. Without a baseline the first native ring includes recent history.
/// </summary>
public sealed class PerformanceCapture
{
    public const int MaximumCapacity = 65_536;
    private readonly NxRuntime _runtime;
    private readonly object _gate = new();
    private readonly Queue<PerformanceInterval> _samples = new();
    private readonly Dictionary<long, double> _recent = new();
    private readonly Queue<long> _recentOrder = new();
    private readonly string _captureId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly int _capacity;
    private DateTimeOffset? _lastObservedUtc;
    private NativePresentTiming? _latest;
    private PerformanceTelemetryResult _result = new()
    {
        Availability = PerformanceAvailability.Unsupported, Code = "not_observed",
        Message = "No active native presentation timing packet has been observed."
    };
    private string? _timingSource;
    private string? _api;
    private string? _measurementPoint;
    private long? _segment;
    private long _sequence;
    private long _accepted, _evicted, _initialOmitted, _missing, _skipped, _duplicates;
    private long _stalePackets, _invalidPackets, _unsupportedPackets, _inactivePackets, _segments;
    private bool _countersSaturated;
    private bool _observed;
    private long? _baselineSegment, _baselineSequence;
    private long? _boundarySequence;
    private long _boundaryExcluded;
    private bool _currentSegmentCounted;

    public PerformanceCapture(NxRuntime runtime, int capacity = 4096)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (capacity is < 1 or > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity), $"Capture capacity must be 1..{MaximumCapacity} actual intervals.");
        _runtime = runtime; _capacity = capacity;
    }

    /// <summary>
    /// Exclude the whole existing native ring and the first same-segment interval
    /// that may cross this observation boundary, then collect later intervals.
    /// Use a fresh telemetry read after a settings change. This must be the first
    /// observation on this capture; unavailable timing is not replaced by FPS.
    /// </summary>
    public PerformanceTelemetryResult StartFromNextInterval(NxRuntime runtime, TelemetrySnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(baseline);
        if (!ReferenceEquals(runtime, _runtime))
            throw new NxException("performance_runtime_mismatch", "A capture baseline must come from its exact runtime.");
        var read = PerformanceTelemetry.Read(baseline);
        lock (_gate)
        {
            if (_observed) throw new NxException("invalid_state", "The baseline must precede every other capture observation.");
            _observed = true; _lastObservedUtc = DateTimeOffset.UtcNow; _result = read;
            if (read.Availability == PerformanceAvailability.Invalid)
            { Add(ref _invalidPackets, 1); return _result; }
            if (read.Packet is not { } packet)
            { Add(ref _unsupportedPackets, 1); return _result; }
            _segment = _baselineSegment = packet.SegmentId; _sequence = packet.Sequence;
            _boundarySequence = packet.Sequence == long.MaxValue ? null : packet.Sequence + 1;
            _baselineSequence = packet.Sequence; _initialOmitted = packet.Sequence;
            _timingSource = packet.TimingSource; _api = packet.Api; _measurementPoint = packet.MeasurementPoint; _latest = packet;
            if (runtime.State == RuntimeState.Running && string.Equals(baseline.State, "running", StringComparison.OrdinalIgnoreCase))
            { _segments = 1; _currentSegmentCounted = true; }
            for (int i = 0; i < packet.IntervalsMs.Count; i++)
            {
                long sequence = packet.Sequence - packet.IntervalsMs.Count + 1 + i;
                _recent[sequence] = packet.IntervalsMs[i]; _recentOrder.Enqueue(sequence);
            }
            return _result;
        }
    }

    public PerformanceTelemetryResult Observe(NxRuntime runtime, TelemetrySnapshot telemetry)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(telemetry);
        if (!ReferenceEquals(runtime, _runtime))
            throw new NxException("performance_runtime_mismatch", "A performance capture belongs to one exact runtime; begin another capture for another session or core.");
        var read = PerformanceTelemetry.Read(telemetry);
        lock (_gate)
        {
            _observed = true;
            _lastObservedUtc = DateTimeOffset.UtcNow;
            _result = read;
            if (read.Availability == PerformanceAvailability.Invalid)
            { Add(ref _invalidPackets, 1); return _result; }
            if (read.Packet is not { } packet)
            { Add(ref _unsupportedPackets, 1); return _result; }
            if (_segment is { } previousSegment && (packet.SegmentId < previousSegment ||
                packet.SegmentId == previousSegment && packet.Sequence < _sequence))
            { Add(ref _stalePackets, 1); return _result; }
            if (_timingSource is not null && (packet.TimingSource != _timingSource || packet.Api != _api || packet.MeasurementPoint != _measurementPoint))
                return Reject("timing_scope_changed");
            if (_latest is { } latest && packet.TotalPresentedFrames < latest.TotalPresentedFrames)
                return Reject("timing_counter_regressed");

            if (runtime.State != RuntimeState.Running || !string.Equals(telemetry.State, "running", StringComparison.OrdinalIgnoreCase))
            {
                // The native ring may be cleared at pause/minimize. Its retained
                // sequence still proves how many active intervals were discarded.
                // Count them once without fabricating a pause interval or deriving
                // intervals from total frames (which can include inactive presents).
                Add(ref _inactivePackets, 1);
                if (_segment != packet.SegmentId)
                {
                    if (_segment is { } previous)
                    { Add(ref _skipped, packet.SegmentId - previous - 1); Add(ref _missing, packet.Sequence); }
                    else Add(ref _initialOmitted, packet.Sequence);
                    _segment = packet.SegmentId;
                    _boundarySequence = null;
                    _currentSegmentCounted = false;
                    _recent.Clear(); _recentOrder.Clear();
                }
                else
                {
                    long missing = packet.Sequence - _sequence;
                    if (_boundarySequence is { } inactiveBoundary && packet.Sequence >= inactiveBoundary)
                    { missing--; Add(ref _boundaryExcluded, 1); _boundarySequence = null; }
                    Add(ref _missing, missing);
                }
                _sequence = packet.Sequence;
                _timingSource = packet.TimingSource; _api = packet.Api; _measurementPoint = packet.MeasurementPoint; _latest = packet;
                if (_countersSaturated) return Reject("timing_counter_overflow");
                return _result;
            }

            int count = packet.IntervalsMs.Count;
            long first = count == 0 ? packet.Sequence : packet.Sequence - count + 1;
            bool newSegment = _segment != packet.SegmentId;
            long? excludedBoundary = !newSegment && _boundarySequence is { } boundary && packet.Sequence >= boundary ? boundary : null;
            if (!newSegment)
            {
                // A native ring must not rewrite a previously observed interval.
                // Retain a separate 512-entry check window even for tiny captures.
                for (int i = 0; i < count; i++)
                    if (_recent.TryGetValue(first + i, out double observed) && observed != packet.IntervalsMs[i])
                        return Reject("timing_interval_rewritten");
            }
            if (newSegment)
            {
                if (_segment is { } previous)
                {
                    Add(ref _skipped, packet.SegmentId - previous - 1);
                    Add(ref _missing, packet.Sequence - count);
                }
                else Add(ref _initialOmitted, packet.Sequence - count);
                _segment = packet.SegmentId; _sequence = 0;
                _boundarySequence = null;
                _recent.Clear(); _recentOrder.Clear();
                _currentSegmentCounted = false;
            }
            else
            {
                long accountedSequence = excludedBoundary is { } excluded ? Math.Max(_sequence, excluded) : _sequence;
                long missing = count == 0 ? packet.Sequence - accountedSequence : Math.Max(0, first - accountedSequence - 1);
                Add(ref _missing, missing);
            }
            if (excludedBoundary is not null)
            { Add(ref _boundaryExcluded, 1); _boundarySequence = null; }
            if (!_currentSegmentCounted) { Add(ref _segments, 1); _currentSegmentCounted = true; }
            for (int i = 0; i < count; i++)
            {
                long sequence = first + i;
                if (sequence <= _sequence)
                { Add(ref _duplicates, 1); continue; }
                if (sequence == excludedBoundary) continue;
                double interval = packet.IntervalsMs[i];
                _samples.Enqueue(new PerformanceInterval(packet.SegmentId, sequence, interval));
                Add(ref _accepted, 1);
                if (_samples.Count > _capacity)
                { _samples.Dequeue(); Add(ref _evicted, 1); }
                _recent[sequence] = interval; _recentOrder.Enqueue(sequence);
                if (_recentOrder.Count > PerformanceTelemetry.MaximumPacketIntervals)
                    _recent.Remove(_recentOrder.Dequeue());
            }
            _sequence = packet.Sequence;
            _timingSource = packet.TimingSource; _api = packet.Api; _measurementPoint = packet.MeasurementPoint; _latest = packet;
            if (_countersSaturated) return Reject("timing_counter_overflow");
            return _result;
        }
    }

    public PerformanceCaptureSummary Snapshot()
    {
        lock (_gate)
        {
            var samples = _samples.ToArray();
            var sorted = samples.Select(sample => sample.Milliseconds).Order().ToArray();
            double? Rank(double percentile) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
            return new PerformanceCaptureSummary
            {
                CaptureId = _captureId, BackendId = _runtime.Backend.BackendId, BackendRevision = _runtime.Backend.BackendRevision,
                AdapterVersion = _runtime.Backend.AdapterVersion,
                StartedUtc = _startedUtc, LastObservedUtc = _lastObservedUtc, Capacity = _capacity,
                StartedFromBaseline = _baselineSegment is not null,
                BaselineSegmentId = _baselineSegment, BaselineSequence = _baselineSequence,
                BoundaryIntervalsExcluded = _boundaryExcluded,
                TimingSource = _timingSource, MeasurementPoint = _measurementPoint, MeasurementScope = PerformanceTelemetry.MeasurementScope,
                Availability = _result.Availability, Code = _result.Code, Message = _result.Message,
                SampleCount = sorted.Length, AcceptedIntervals = _accepted, EvictedIntervals = _evicted,
                InitialHistoryOmitted = _initialOmitted, MissingIntervals = _missing, SkippedSegments = _skipped,
                DuplicateIntervals = _duplicates, StalePackets = _stalePackets, InvalidPackets = _invalidPackets,
                UnsupportedPackets = _unsupportedPackets, InactivePackets = _inactivePackets,
                SegmentCount = _segments, CountersSaturated = _countersSaturated,
                LatestSegmentId = _segment, LatestSequence = _segment is null ? null : _sequence,
                HasGaps = _missing != 0 || _skipped != 0 || _invalidPackets != 0 || _countersSaturated ||
                    _accepted != 0 && _unsupportedPackets != 0,
                Samples = Array.AsReadOnly(samples), P50Ms = Rank(.50), P95Ms = Rank(.95), P99Ms = Rank(.99), P999Ms = Rank(.999),
                MaxMs = sorted.Length == 0 ? null : sorted[^1], MeanMs = sorted.Length == 0 ? null : sorted.Average(),
                HitchesOver33Point3Ms = sorted.Count(value => value > 33.3),
                HitchesOver33Ms = sorted.Count(value => value > 33),
                HitchesOver50Ms = sorted.Count(value => value > 50), HitchesOver100Ms = sorted.Count(value => value > 100),
                LastPresentAgeMs = _latest?.LastPresentAgeMs, TotalPresentedFrames = _latest?.TotalPresentedFrames,
                ShaderCacheEnabled = _latest?.ShaderCacheEnabled, CpuCacheEnabled = _latest?.CpuCacheEnabled,
                RendererThreading = _latest?.RendererThreading, Api = _latest?.Api
            };
        }
    }

    private PerformanceTelemetryResult Reject(string code)
    {
        Add(ref _invalidPackets, 1);
        return _result = PerformanceTelemetry.Invalid(code);
    }

    private void Add(ref long counter, long increment)
    {
        if (increment < 0 || long.MaxValue - counter < increment)
        { counter = long.MaxValue; _countersSaturated = true; }
        else counter += increment;
    }
}
