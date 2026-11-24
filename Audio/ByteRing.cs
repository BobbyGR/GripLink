namespace GripLink.Audio;

/// <summary>
/// Small thread-safe audio FIFO that keeps latency bounded: if the writer runs ahead,
/// the oldest audio is dropped; if the reader runs dry, it gets silence.
/// </summary>
internal sealed class ByteRing
{
    private readonly byte[] _buf;
    private readonly int _target, _max, _align;
    private int _read, _count;
    private bool _primed;
    private readonly object _lock = new();

    /// <param name="capacity">Total buffer size in bytes.</param>
    /// <param name="targetBytes">Fill level to start playing at (and trim back to).</param>
    /// <param name="maxBytes">Above this the buffer is trimmed back to the target.</param>
    /// <param name="align">Frame size in bytes; trimming keeps frames intact.</param>
    public ByteRing(int capacity, int targetBytes, int maxBytes, int align)
    {
        _buf = new byte[capacity]; _target = targetBytes; _max = maxBytes; _align = align;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            foreach (var b in data)
            {
                if (_count == _buf.Length) { _read = (_read + 1) % _buf.Length; _count--; }
                _buf[(_read + _count) % _buf.Length] = b;
                _count++;
            }
            if (_count > _max)
            {
                int drop = (_count - _target) / _align * _align;
                _read = (_read + drop) % _buf.Length; _count -= drop;
            }
            if (_count >= _target) _primed = true;
        }
    }

    /// <summary>Fills dst completely (silence where there is no audio).</summary>
    public void Read(Span<byte> dst)
    {
        lock (_lock)
        {
            int n = 0;
            if (_primed)
            {
                n = Math.Min(dst.Length, _count);
                for (int i = 0; i < n; i++) dst[i] = _buf[(_read + i) % _buf.Length];
                _read = (_read + n) % _buf.Length; _count -= n;
                if (_count == 0) _primed = false; // underrun: wait to refill
            }
            dst[n..].Clear();
        }
    }

    public void Clear() { lock (_lock) { _read = _count = 0; _primed = false; } }
}
