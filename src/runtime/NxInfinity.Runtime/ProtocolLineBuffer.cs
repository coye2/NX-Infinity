namespace NxInfinity.Runtime;

/// <summary>
/// Bounded newline framing for a single protocol reader. Complete frames borrow
/// the input memory only during the synchronous callback. Only split frames are
/// copied into retained storage; consumers must clone anything they keep.
/// </summary>
internal sealed class ProtocolLineBuffer : IDisposable
{
    private readonly int _maximumBytes;
    private readonly MemoryStream _partial = new();

    internal ProtocolLineBuffer(int maximumBytes)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    internal void Append(ReadOnlyMemory<byte> chunk, Action<ReadOnlyMemory<byte>> consume)
    {
        while (!chunk.IsEmpty)
        {
            int end = chunk.Span.IndexOf((byte)'\n');
            int count = end < 0 ? chunk.Length : end;
            if (count > _maximumBytes - _partial.Length)
                throw new NxException("protocol_mismatch", "Core response exceeds 1 MiB.");

            if (end >= 0 && _partial.Length == 0)
            {
                consume(chunk[..count]);
            }
            else
            {
                _partial.Write(chunk.Span[..count]);
                if (end >= 0)
                {
                    consume(_partial.GetBuffer().AsMemory(0, (int)_partial.Length));
                    _partial.SetLength(0);
                }
            }
            if (end < 0) return;
            chunk = chunk[(end + 1)..];
        }
    }

    internal void Complete()
    {
        if (_partial.Length > 0)
            throw new NxException("protocol_mismatch", "Core ended with an incomplete protocol frame.");
    }

    public void Dispose() => _partial.Dispose();
}
