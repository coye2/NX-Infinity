namespace NxInfinity.Runtime.Services;

/// <summary>Single-owner cache. Referenced resources stay valid; idle least-recently-used entries make room for new data.</summary>
public sealed class LeasedResourceCache<T> : IDisposable where T : class, IDisposable
{
    private sealed class Entry(string key, T value, long bytes, long stamp)
    { public string Key = key; public T Value = value; public long Bytes = bytes; public long Stamp = stamp; public int Pins; }
    public sealed class Lease : IDisposable
    {
        private Action? _release;
        public T Value { get; }
        internal Lease(T value, Action release) { Value = value; _release = release; }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _stamp;
    private bool _disposed;
    public long BudgetBytes { get; private set; }
    public long RetainedBytes { get; private set; }
    public int Count => _entries.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public LeasedResourceCache(long budgetBytes) => SetBudget(budgetBytes);
    public void SetBudget(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        BudgetBytes = bytes; Trim(bytes);
    }
    public Lease? TryAcquire(string key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(key, out var entry)) return null;
        Hits++; entry.Stamp = ++_stamp; entry.Pins++;
        return new(entry.Value, () => Release(entry));
    }
    public Lease? Acquire(string key, long maximumBytes, Func<(T Value, long Bytes)> create)
    {
        if (TryAcquire(key) is { } reused) return reused;
        Misses++;
        if (maximumBytes <= 0 || maximumBytes > BudgetBytes) return null;
        Trim(BudgetBytes - maximumBytes);
        if (RetainedBytes > BudgetBytes - maximumBytes) return null; // No hidden unbounded decode when all entries are referenced.
        var resource = create();
        if (resource.Bytes <= 0 || resource.Bytes > maximumBytes)
        { resource.Value.Dispose(); throw new InvalidOperationException("Decoded resource exceeded its reserved byte budget."); }
        var entry = new Entry(key, resource.Value, resource.Bytes, ++_stamp) { Pins = 1 };
        _entries.Add(key, entry); RetainedBytes += entry.Bytes;
        return new(entry.Value, () => Release(entry));
    }
    private void Release(Entry entry)
    {
        if (entry.Pins <= 0) return;
        entry.Pins--;
        if (_disposed && entry.Pins == 0) Remove(entry);
        else Trim(BudgetBytes);
    }
    public void TrimIdle() => Trim(0);
    private void Trim(long target)
    {
        foreach (var entry in _entries.Values.Where(e => e.Pins == 0).OrderBy(e => e.Stamp).ToArray())
        { if (RetainedBytes <= target) break; Remove(entry); }
    }
    private void Remove(Entry entry)
    {
        if (!_entries.Remove(entry.Key)) return;
        RetainedBytes -= entry.Bytes; entry.Value.Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Trim(0); // Outstanding leases release their own resources safely.
    }
}
