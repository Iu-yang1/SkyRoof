namespace SkyRoof.CW
{
  public readonly record struct CwAudioSnapshot(
    int SampleRate,
    DateTime EndUtc,
    long EndSampleIndex,
    float[] Samples);

  /// <summary>
  /// Thread-safe bounded PCM history used by CW detection/decoding. Audio
  /// callbacks only copy samples into a fixed ring; neural/DSP work always runs
  /// on snapshots outside the capture thread.
  /// </summary>
  public sealed class CwAudioHub
  {
    private readonly object sync = new();
    private readonly float[] ring;
    private int writePosition;
    private int sampleCount;
    private long totalSamplesWritten;
    private DateTime? lastBlockUtc;

    public int SampleRate { get; }
    public int CapacitySamples => ring.Length;
    public double CapacitySeconds => CapacitySamples / (double)SampleRate;

    public long TotalSamplesWritten
    {
      get { lock (sync) return totalSamplesWritten; }
    }

    public int BufferedSamples
    {
      get { lock (sync) return sampleCount; }
    }

    public CwAudioHub(int sampleRate, double capacitySeconds = 12)
    {
      if (sampleRate < 1000 || sampleRate > 384000)
        throw new ArgumentOutOfRangeException(nameof(sampleRate));
      if (!double.IsFinite(capacitySeconds) || capacitySeconds <= 0 || capacitySeconds > 120)
        throw new ArgumentOutOfRangeException(nameof(capacitySeconds));

      SampleRate = sampleRate;
      int capacity = checked((int)Math.Ceiling(sampleRate * capacitySeconds));
      ring = new float[Math.Max(capacity, 1)];
    }

    public void Append(float[] samples, int count, DateTime blockUtc)
    {
      ArgumentNullException.ThrowIfNull(samples);
      if (count < 0 || count > samples.Length)
        throw new ArgumentOutOfRangeException(nameof(count));
      Append(samples.AsSpan(0, count), blockUtc);
    }

    public void Append(ReadOnlySpan<float> samples, DateTime blockUtc)
    {
      if (blockUtc.Kind != DateTimeKind.Utc)
        throw new ArgumentException("CW audio timestamps must be UTC.", nameof(blockUtc));
      if (samples.Length == 0) return;

      lock (sync)
      {
        if (lastBlockUtc is DateTime previous && blockUtc < previous)
          throw new ArgumentException("CW audio timestamps must be monotonic.", nameof(blockUtc));

        if (samples.Length >= ring.Length)
        {
          samples[^ring.Length..].CopyTo(ring);
          writePosition = 0;
          sampleCount = ring.Length;
        }
        else
        {
          int first = Math.Min(samples.Length, ring.Length - writePosition);
          samples[..first].CopyTo(ring.AsSpan(writePosition, first));
          int remaining = samples.Length - first;
          if (remaining > 0)
            samples[first..].CopyTo(ring.AsSpan(0, remaining));

          writePosition = (writePosition + samples.Length) % ring.Length;
          sampleCount = Math.Min(ring.Length, sampleCount + samples.Length);
        }

        totalSamplesWritten += samples.Length;
        lastBlockUtc = blockUtc;
      }
    }

    public bool TrySnapshot(double seconds, out CwAudioSnapshot snapshot)
    {
      if (!double.IsFinite(seconds) || seconds <= 0 || seconds > CapacitySeconds)
        throw new ArgumentOutOfRangeException(nameof(seconds));

      int requested = checked((int)Math.Round(seconds * SampleRate));
      requested = Math.Max(requested, 1);

      lock (sync)
      {
        if (sampleCount < requested || lastBlockUtc == null)
        {
          snapshot = default;
          return false;
        }

        float[] data = new float[requested];
        int start = writePosition - requested;
        if (start < 0) start += ring.Length;

        int first = Math.Min(requested, ring.Length - start);
        Array.Copy(ring, start, data, 0, first);
        int remaining = requested - first;
        if (remaining > 0)
          Array.Copy(ring, 0, data, first, remaining);

        snapshot = new CwAudioSnapshot(
          SampleRate, lastBlockUtc.Value, totalSamplesWritten, data);
        return true;
      }
    }

    public void Reset()
    {
      lock (sync)
      {
        Array.Clear(ring);
        writePosition = 0;
        sampleCount = 0;
        totalSamplesWritten = 0;
        lastBlockUtc = null;
      }
    }
  }
}
