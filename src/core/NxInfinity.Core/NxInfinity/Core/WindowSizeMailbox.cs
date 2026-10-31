namespace NxInfinity.Core;

internal sealed class WindowSizeMailbox
{
    internal readonly record struct Size(int Width, int Height);

    internal readonly record struct Work(long Requests, long Coalesced, long DuplicatesSkipped, long UpdatesTaken, bool Pending);

    private readonly object _gate = new object();

    private Size _latest;

    private Size _lastTaken;

    private bool _pending;

    private bool _force;

    private bool _hasTaken;

    private bool _closed;

    private long _requests;

    private long _coalesced;

    private long _duplicates;

    private long _updatesTaken;

    public void Request(int width, int height, bool force = false)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }
        lock (_gate)
        {
            if (!_closed)
            {
                _requests++;
                if (_pending)
                {
                    _coalesced++;
                }
                _latest = new Size(width, height);
                _force |= force;
                _pending = true;
            }
        }
    }

    public bool TryTake(out Size size)
    {
        lock (_gate)
        {
            size = default;
            if (_closed || !_pending)
            {
                return false;
            }
            bool force = _force;
            _pending = false;
            _force = false;
            if (!force && _hasTaken && _latest == _lastTaken)
            {
                _duplicates++;
                return false;
            }
            size = _latest;
            _lastTaken = size;
            _hasTaken = true;
            _updatesTaken++;
            return true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _pending = false;
            _force = false;
        }
    }

    public Work Snapshot()
    {
        lock (_gate)
        {
            return new Work(_requests, _coalesced, _duplicates, _updatesTaken, _pending);
        }
    }
}
