using System;
using System.Diagnostics;

namespace Nx.Backends.Performance;

internal sealed class PresentTimingRecorder
{
    internal const int Capacity = 512;

    private readonly object _gate = new object();

    private readonly Func<long> _clock;

    private readonly long _frequency;

    private readonly double[] _intervals = new double[512];

    private int _next;

    private int _count;

    private bool _running;

    private bool _hasPresent;

    private long _lastPresent;

    private long _segment;

    private long _sequence;

    private long _totalPresented;

    internal PresentTimingRecorder()
        : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    internal PresentTimingRecorder(Func<long> clock, long frequency)
    {
        ArgumentNullException.ThrowIfNull(clock, "clock");
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException("frequency");
        }
        _clock = clock;
        _frequency = frequency;
    }

    internal void SetRunning(bool running)
    {
        lock (_gate)
        {
            if (_running != running)
            {
                _running = running;
                if (running)
                {
                    _segment++;
                    _sequence = 0L;
                }
                _next = 0;
                _count = 0;
                _hasPresent = false;
            }
        }
    }

    internal void RecordPresentReturn()
    {
        long num = _clock();
        lock (_gate)
        {
            _totalPresented++;
            if (!_running)
            {
                return;
            }
            if (_hasPresent)
            {
                if (num <= _lastPresent)
                {
                    return;
                }
                double num2 = (double)(num - _lastPresent) * 1000.0 / (double)_frequency;
                if (!double.IsFinite(num2) || num2 <= 0.0)
                {
                    return;
                }
                _intervals[_next] = num2;
                _next = (_next + 1) % 512;
                _count = Math.Min(_count + 1, 512);
                _sequence++;
            }
            _lastPresent = num;
            _hasPresent = true;
        }
    }

    internal object Snapshot(bool? shaderCacheEnabled, bool? cpuCacheEnabled, string rendererThreading = "single_gpu_thread")
    {
        lock (_gate)
        {
            double[] array = new double[_count];
            int num = (_next - _count + 512) % 512;
            for (int i = 0; i < _count; i++)
            {
                array[i] = _intervals[(num + i) % 512];
            }
            double? lastPresentAgeMs = (_hasPresent ? new double?(Math.Max(0.0, (double)(_clock() - _lastPresent) * 1000.0 / (double)_frequency)) : ((double?)null));
            return new
            {
                schemaVersion = 1,
                timingSource = "present_call_return",
                measurementPoint = "renderer_window_present_return",
                segmentId = _segment,
                sequence = _sequence,
                intervalsMs = array,
                totalPresentedFrames = _totalPresented,
                lastPresentAgeMs = lastPresentAgeMs,
                shaderCacheEnabled = shaderCacheEnabled,
                cpuCacheEnabled = cpuCacheEnabled,
                rendererThreading = rendererThreading,
                api = "vulkan"
            };
        }
    }
}
