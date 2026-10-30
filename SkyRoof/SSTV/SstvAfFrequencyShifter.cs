using System;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Streaming real-audio pitch translator. A 129-tap windowed discrete
  /// Hilbert filter forms an analytic AF representation; the complex NCO
  /// then translates it by a signed frequency, without the unwanted mirrored
  /// sideband produced by multiplication with a real cosine.
  /// </summary>
  public sealed class SstvAfFrequencyShifter
  {
    private const int Length = 129;
    private const int Half = (Length - 1) / 2;
    private const double SampleRate = 48000.0;
    private readonly float[] history = new float[Length];
    private readonly double[] hilbert = new double[Length];
    private int next;
    private double phase;

    public SstvAfFrequencyShifter()
    {
      for (int i = 0; i < Length; i++)
      {
        int t = i - Half;
        if (t == 0 || (t & 1) == 0) continue;
        double window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (Length - 1));
        hilbert[i] = 2.0 / (Math.PI * t) * window;
      }
    }

    public float Process(float sample, double correctionHz)
    {
      if (!double.IsFinite(sample) || !double.IsFinite(correctionHz) ||
        Math.Abs(correctionHz) > 500.0)
        throw new ArgumentOutOfRangeException(nameof(correctionHz));

      history[next] = sample;
      int delayedIndex = next - Half;
      if (delayedIndex < 0) delayedIndex += Length;
      double inPhase = history[delayedIndex];
      double quadrature = 0;
      int read = next;
      for (int i = 0; i < Length; i++)
      {
        quadrature += hilbert[i] * history[read];
        if (--read < 0) read = Length - 1;
      }
      if (++next == Length) next = 0;
      phase += 2 * Math.PI * correctionHz / SampleRate;
      if (phase > Math.PI) phase -= 2 * Math.PI;
      else if (phase < -Math.PI) phase += 2 * Math.PI;
      return (float)(inPhase * Math.Cos(phase) - quadrature * Math.Sin(phase));
    }

    public void Reset()
    {
      Array.Clear(history);
      next = 0;
      phase = 0;
    }
  }
}
