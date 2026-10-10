namespace SkyRoof.CW
{
  /// <summary>
  /// One inference session's bounded, exact-sample STFT memoization.
  /// Rows are immutable copies, indexed by the absolute start sample and
  /// transform contract. Never use across a receiver timeline reset.
  /// </summary>
  internal sealed class CwStftMagnitudeCache
  {
    private readonly record struct Key(
      long AbsoluteStart, int SampleRate, int FftSize);
    private readonly Dictionary<Key, float[]> rows = new();
    private readonly Queue<Key> fifo = new();
    private readonly int capacity;
    private long hits;
    private long misses;

    internal CwStftMagnitudeCache(int capacity = 1600)
    {
      if (capacity < 1 || capacity > 8192)
        throw new ArgumentOutOfRangeException(nameof(capacity));
      this.capacity = capacity;
    }

    internal long Hits => hits;
    internal long Misses => misses;
    internal int Count => rows.Count;

    internal bool TryCopy(
      long absoluteStart,
      int sampleRate,
      int fftSize,
      Span<float> destination)
    {
      var key = new Key(absoluteStart, sampleRate, fftSize);
      if (rows.TryGetValue(key, out float[]? row) &&
          row.Length == destination.Length)
      {
        row.AsSpan().CopyTo(destination);
        hits++;
        return true;
      }
      misses++;
      return false;
    }

    internal void Save(
      long absoluteStart,
      int sampleRate,
      int fftSize,
      ReadOnlySpan<float> magnitude)
    {
      var key = new Key(absoluteStart, sampleRate, fftSize);
      if (rows.ContainsKey(key))
        return;
      rows.Add(key, magnitude.ToArray());
      fifo.Enqueue(key);
      while (fifo.Count > capacity)
        rows.Remove(fifo.Dequeue());
    }

    internal void Clear()
    {
      rows.Clear();
      fifo.Clear();
      hits = 0;
      misses = 0;
    }
  }
}
