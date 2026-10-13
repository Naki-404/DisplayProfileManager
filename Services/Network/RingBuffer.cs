namespace Visor.Services;

/// <summary>Fixed-capacity FIFO that overwrites the oldest item. Thread-safe for one writer + readers via snapshots.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly object _gate = new();
    private int _head;   // index of the oldest item
    private int _count;
    private long _total; // items ever added (survives overwrite)

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count { get { lock (_gate) return _count; } }

    /// <summary>Items added over the lifetime (including those already overwritten).</summary>
    public long TotalAdded { get { lock (_gate) return _total; } }

    public void Add(T item)
    {
        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_head + _count) % _items.Length] = item;
                _count++;
            }
            else
            {
                _items[_head] = item;
                _head = (_head + 1) % _items.Length;
            }
            _total++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Copy, oldest first.</summary>
    public T[] ToArray()
    {
        lock (_gate)
        {
            var result = new T[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _items[(_head + i) % _items.Length];
            return result;
        }
    }

    /// <summary>Newest item or default when empty.</summary>
    public T? Last
    {
        get
        {
            lock (_gate)
                return _count == 0 ? default : _items[(_head + _count - 1) % _items.Length];
        }
    }
}
