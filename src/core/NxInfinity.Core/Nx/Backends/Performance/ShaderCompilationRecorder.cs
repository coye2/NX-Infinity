using System.Diagnostics;

namespace Nx.Backends.Performance;

internal sealed class ShaderCompilationRecorder
{
    private readonly object _gate = new object();

    private bool? _requested;

    private bool _returned;

    private long _callbacks;

    private long _enteredAt;

    private string _stage;

    private string _nativeStage;

    private long? _completed;

    private long? _total;

    private double? _durationMs;

    private string _publishedStage;

    private long? _publishedCompleted;

    private long? _publishedTotal;

    private long _lastProgressAt;

    internal void BeginCacheLoad(bool cacheRequested)
    {
        lock (_gate)
        {
            _requested = cacheRequested;
            _enteredAt = Stopwatch.GetTimestamp();
        }
    }

    internal void CacheLoadReturned()
    {
        lock (_gate)
        {
            _durationMs = Stopwatch.GetElapsedTime(_enteredAt).TotalMilliseconds;
            _returned = true;
        }
    }

    internal bool NativeStateChanged(string state, int completed, int total)
    {
        lock (_gate)
        {
            _callbacks++;
            _nativeStage = state;
            _stage = state switch
            {
                "Start" => "prepare",
                "Loading" => "build",
                "Packaging" => "package",
                "Loaded" => "complete",
                _ => null,
            };
            _completed = ((completed >= 0) ? new long?(completed) : ((long?)null));
            _total = ((total >= 0) ? new long?(total) : ((long?)null));
            bool flag = _stage != _publishedStage;
            bool flag2 = _completed != _publishedCompleted || _total != _publishedTotal;
            int num;
            if (!flag)
            {
                if (flag2)
                {
                    num = ((Stopwatch.GetElapsedTime(_lastProgressAt).TotalMilliseconds >= 100.0) ? 1 : 0);
                    if (num != 0)
                    {
                        goto IL_016c;
                    }
                }
                else
                {
                    num = 0;
                }
                goto IL_019b;
            }
            num = 1;
            goto IL_016c;
        IL_016c:
            _publishedStage = _stage;
            _publishedCompleted = _completed;
            _publishedTotal = _total;
            _lastProgressAt = Stopwatch.GetTimestamp();
            goto IL_019b;
        IL_019b:
            return (byte)num != 0;
        }
    }

    internal object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                schemaVersion = 1,
                source = "native_shader_cache_state_changed",
                cacheRequested = _requested,
                cacheConsumerObserved = (_callbacks > 0),
                cacheLoadReturned = _returned,
                cacheCallbackCount = _callbacks,
                cacheStage = _stage,
                nativeCacheStage = _nativeStage,
                cacheCompleted = _completed,
                cacheTotal = _total,
                cacheLoadDurationMs = _durationMs,
                activityIndicator = (long?)null,
                activityMeaning = "unavailable"
            };
        }
    }
}
