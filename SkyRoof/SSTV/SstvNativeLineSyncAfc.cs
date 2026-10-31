using System;
using VE3NEA.SkySSTV;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Track the 1200-Hz horizontal sync tone throughout a Robot/PD picture.
  /// The header gives only an initial AF offset; the Doppler residual can
  /// continue to drift over a several-minute linear-satellite picture.
  /// This conservative observation-only tracker adjusts the software
  /// baseband translator, never RF CAT or transmitter tuning.
  /// </summary>
  public sealed class SstvNativeLineSyncAfc
  {
    private const int Rate = 48000;
    private const int FrameSamples = 240;
    private readonly float[] frame = new float[FrameSamples];
    private int filled;
    private long totalSamples;
    private SstvModeSpec? spec;
    private double headerEndSample;
    private int lineIndex;
    private double estimatedOffsetHz;
    private int accepted;

    public event Action<double>? OffsetUpdated;
    public bool Active => spec != null;
    public int AcceptedSyncs => accepted;
    public double EstimatedOffsetHz => estimatedOffsetHz;

    public void Begin(SstvVisAfcLock detected, SstvModeSpec mode)
    {
      ArgumentNullException.ThrowIfNull(mode);
      if (detected.SampleCount <= 0 ||
          !double.IsFinite(detected.OffsetHz) ||
          Math.Abs(detected.OffsetHz) > 160)
        return;
      spec = mode;
      lineIndex = accepted = 0;
      estimatedOffsetHz = detected.OffsetHz;
      // The conservative VIS detector accepts four 5-ms frames into
      // the 30-ms stop bit. 10 ms remain before the first raster sync.
      headerEndSample = detected.SampleCount + 10 * Rate / 1000.0;
    }

    public void Disable() => spec = null;

    public void Reset()
    {
      spec = null;
      filled = 0;
      totalSamples = 0;
      lineIndex = accepted = 0;
      estimatedOffsetHz = headerEndSample = 0;
    }

    public void Process(ReadOnlySpan<float> af)
    {
      for (int n = 0; n < af.Length; n++)
      {
        frame[filled++] = af[n];
        totalSamples++;
        if (filled != FrameSamples) continue;
        filled = 0;
        if (spec == null) continue;

        double start = totalSamples - FrameSamples;
        double expected = headerEndSample +
          lineIndex * spec.LinePeriodMs * Rate / 1000.0;
        // A 5-ms FFT/zero-crossing window may overlap the trailing edge
        // of a short 9-ms Robot sync. Use the first complete frame whose
        // beginning is within 5 ms after the predicted raster-line start.
        while (spec != null && start >= expected + FrameSamples)
        {
          lineIndex++;
          if (lineIndex >= spec.LineCount)
          {
            spec = null;
            break;
          }
          expected = headerEndSample +
            lineIndex * spec.LinePeriodMs * Rate / 1000.0;
        }
        if (spec == null || start < expected ||
            start >= expected + FrameSamples)
          continue;

        double toneHz = SstvVisAfcDetector.EstimatePitch(frame);
        double residual = toneHz - 1200.0 - estimatedOffsetHz;
        // A lost/faded sync must HOLD the last usable correction rather
        // than chase an arbitrary pixel tone or nearby voice component.
        if (double.IsFinite(residual) && Math.Abs(residual) <= 70)
        {
          estimatedOffsetHz = Math.Clamp(
            estimatedOffsetHz + .22 * residual, -400, 400);
          accepted++;
          OffsetUpdated?.Invoke(estimatedOffsetHz);
        }
        lineIndex++;
        if (lineIndex >= spec.LineCount) spec = null;
      }
    }
  }
}
