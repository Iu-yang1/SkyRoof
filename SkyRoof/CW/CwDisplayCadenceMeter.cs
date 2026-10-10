using System.Diagnostics;

namespace SkyRoof.CW
{
  internal readonly record struct CwDisplayCadenceSnapshot(
    double ActualFps,
    double P95IntervalMs,
    double MaximumIntervalMs,
    int RecentFrames);

  /// <summary>
  /// UI-only delivered-frame meter. Timestamps use the monotonic
  /// Stopwatch clock: neither Windows timer settings nor queued FFT
  /// jobs are treated as proof that a frame was actually drawn.
  /// </summary>
  internal sealed class CwDisplayCadenceMeter
  {
    private readonly long[] delivered = new long[256];
    private int next;
    private int count;

    internal void Reset()
    {
      next = 0;
      count = 0;
    }

    internal void Record(long timestamp = 0)
    {
      if (timestamp == 0)
        timestamp = Stopwatch.GetTimestamp();
      delivered[next] = timestamp;
      next = (next + 1) % delivered.Length;
      if (count < delivered.Length)
        count++;
    }

    internal CwDisplayCadenceSnapshot Snapshot(long now = 0)
    {
      if (now == 0)
        now = Stopwatch.GetTimestamp();
      if (count == 0)
        return default;

      long minTimestamp = now - 2 * Stopwatch.Frequency;
      long[] recent = new long[count];
      int recentCount = 0;
      int oldest = (next - count + delivered.Length) % delivered.Length;
      for (int i = 0; i < count; i++)
      {
        long t = delivered[(oldest + i) % delivered.Length];
        if (t >= minTimestamp && t <= now)
          recent[recentCount++] = t;
      }
      if (recentCount < 2)
        return new(0, 0, 0, recentCount);

      var intervals = new double[recentCount - 1];
      for (int i = 1; i < recentCount; i++)
        intervals[i - 1] =
          1000.0 * (recent[i] - recent[i - 1]) /
            Stopwatch.Frequency;

      Array.Sort(intervals);
      double spanSeconds =
        (recent[recentCount - 1] - recent[0]) /
        (double)Stopwatch.Frequency;
      double fps = spanSeconds > 0
        ? (recentCount - 1) / spanSeconds : 0;
      int p95Index = (int)Math.Ceiling(0.95 * intervals.Length) - 1;
      return new(
        fps,
        intervals[Math.Clamp(p95Index, 0, intervals.Length - 1)],
        intervals[^1],
        recentCount);
    }
  }
}
