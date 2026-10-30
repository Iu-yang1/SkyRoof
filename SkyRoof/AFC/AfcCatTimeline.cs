using System;
using System.Collections.Generic;

namespace SkyRoof.AFC
{
  public enum AfcCatWriteSource { SatelliteModel, Afc, Operator, Calibration }

  /// <summary>
  /// Bounded CAT chronology. A request to write a frequency is NOT a readback
  /// acknowledgment. All readings are timestamped on the CAT polling thread,
  /// not when a delayed DSP result is delivered to WinForms.
  /// </summary>
  public sealed class AfcCatTimeline
  {
    private readonly object sync = new();
    private readonly Queue<(DateTime Utc, long CatHz)> readbacks = new();
    private readonly Queue<(DateTime Utc, long CatHz, AfcCatWriteSource Source)> requests = new();
    private long sequence;
    public long RequestSequence { get { lock (sync) return sequence; } }

    public void RecordReadback(long catHz, DateTime utc)
    {
      if (catHz <= 0 || utc.Kind != DateTimeKind.Utc) return;
      lock (sync)
      {
        readbacks.Enqueue((utc, catHz));
        while (readbacks.Count > 256) readbacks.Dequeue();
      }
    }

    public long RecordRequest(long catHz, DateTime utc, AfcCatWriteSource source)
    {
      lock (sync)
      {
        requests.Enqueue((utc, catHz, source));
        while (requests.Count > 128) requests.Dequeue();
        return ++sequence;
      }
    }

    /// <summary>
    /// Return CAT state near the END of the IF PCM window. Future CAT reads
    /// cannot be used as historical evidence, and an old readback is not
    /// silently treated as current state.
    /// </summary>
    public bool TryReadbackAt(DateTime sampleUtc, out long catHz,
      double maximumAgeSeconds = 1.2)
    {
      catHz = 0;
      if (sampleUtc.Kind != DateTimeKind.Utc) return false;
      lock (sync)
      {
        DateTime newest = default;
        foreach (var entry in readbacks)
        {
          if (entry.Utc > sampleUtc || entry.Utc <= newest) continue;
          newest = entry.Utc;
          catHz = entry.CatHz;
        }
        if (newest == default ||
            (sampleUtc - newest).TotalSeconds > maximumAgeSeconds)
        {
          catHz = 0;
          return false;
        }
        return true;
      }
    }

    /// <summary>
    /// Diagnostic only: newest actual CAT RX read and its age. Not a
    /// substitute for TryReadbackAt when making IF/CAT control decisions.
    /// </summary>
    public bool TryLatestReadback(DateTime nowUtc, out long catHz,
      out double ageSeconds)
    {
      catHz = 0;
      ageSeconds = double.PositiveInfinity;
      if (nowUtc.Kind != DateTimeKind.Utc) return false;
      lock (sync)
      {
        DateTime newest = default;
        foreach (var entry in readbacks)
          if (entry.Utc <= nowUtc && entry.Utc > newest)
          {
            newest = entry.Utc;
            catHz = entry.CatHz;
          }
        if (newest == default) return false;
        ageSeconds = (nowUtc - newest).TotalSeconds;
        return true;
      }
    }

    /// <summary>
    /// Source attribution for CAT readback, bounded by a short temporal
    /// window. Never labels an arbitrary front-panel move as AFC.
    /// </summary>
    public AfcCatWriteSource? ExpectedWriteSource(long catHz, DateTime sampleUtc,
      long frequencyToleranceHz = 3)
    {
      lock (sync)
      {
        AfcCatWriteSource? source = null;
        foreach (var entry in requests)
          if (entry.Utc <= sampleUtc && (sampleUtc - entry.Utc).TotalSeconds <= 4 &&
              Math.Abs(entry.CatHz - catHz) <= frequencyToleranceHz)
            source = entry.Source;
        return source;
      }
    }

    public void Clear()
    {
      lock (sync)
      {
        readbacks.Clear();
        requests.Clear();
        sequence++;
      }
    }
  }
}
