using System;
using System.Linq;
using System.Numerics;

namespace SkyRoof.AFC
{
  // Mode-specific estimators share the validated real-IF downconverter
  // and deterministic FFT helpers through this partial type.
  public sealed partial class AfcSignalProcessing
  {
    private AfcObservation EstimateTone(Complex[] bb, AfcSignalMode mode,
      DateTime utc, long generation, double expectedHz,
      AfcAcquisitionPreset preset)
    {
      // Prefer an ~171-ms 4096-point FFT for short Morse key-downs.
      // A second ~341-ms window has finer bins for weaker longer carriers.
      // Both are locally gated around the operator-selected tone; neither
      // is allowed to jump to an unrelated strong CW pileup station.
      var fast = EstimateToneWindow(bb, mode, utc, generation,
        expectedHz, 4096);
      if (fast.HasAbsoluteReference && fast.Confidence >= .88 &&
          fast.SigmaHz <= 4)
        return fast;
      var sensitive = EstimateToneWindow(bb, mode, utc, generation,
        expectedHz, 8192);
      var best = sensitive.HasAbsoluteReference &&
        (!fast.HasAbsoluteReference ||
         sensitive.Confidence >= fast.Confidence - .05)
          ? sensitive : fast;
      // Optional third, longer resolution for an already available weak
      // carrier: no extra time-to-first-fix penalty in Adaptive mode.
      if ((preset is AfcAcquisitionPreset.Adaptive or
          AfcAcquisitionPreset.Sensitive) && bb.Length >= 16384)
      {
        var deep = EstimateToneWindow(bb, mode, utc, generation,
          expectedHz, 16384);
        if (deep.HasAbsoluteReference &&
            (!best.HasAbsoluteReference ||
              deep.Confidence >= best.Confidence - .08) &&
            (best.HasAbsoluteReference
              ? Math.Abs(deep.IfOffsetHz - best.IfOffsetHz) <= 12 : true))
          best = deep;
      }
      return best;
    }

    private AfcObservation EstimateToneWindow(Complex[] bb,
      AfcSignalMode mode, DateTime utc, long generation,
      double expectedHz, int fftSize)
    {
      if (bb.Length < fftSize)
        return new(mode, utc, generation, 0, 9999, 0, false);
      var spectrum = new Complex[fftSize];
      int start = bb.Length - fftSize;
      for (int i = 0; i < fftSize; i++)
      {
        double hann = .5 - .5 * Math.Cos(2 * Math.PI * i / (fftSize - 1));
        spectrum[i] = bb[start + i] * hann;
      }
      Fourier(spectrum);
      double binHz = sampleRate / 2.0 / fftSize;
      double span = mode == AfcSignalMode.CW ? 55 : 50;
      int low = (int)Math.Ceiling((expectedHz - span) / binHz);
      int high = (int)Math.Floor((expectedHz + span) / binHz);
      double best = -1;
      int bestBin = 0, count = 0;
      for (int b = low; b <= high; b++)
      {
        double power = Power(spectrum, b);
        count++;
        if (power > best) { best = power; bestBin = b; }
      }
      if (best <= 0 || count <= 12)
        return new(mode, utc, generation, 0, 9999, 0, false);
      double noiseSum = 0, secondPeak = 0;
      int noiseCount = 0;
      for (int b = low; b <= high; b++)
      {
        if (Math.Abs(b - bestBin) <= (fftSize == 4096 ? 2 : 4)) continue;
        double power = Power(spectrum, b);
        noiseSum += power;
        secondPeak = Math.Max(secondPeak, power);
        noiseCount++;
      }
      double noise = Math.Max(1e-18, noiseSum / Math.Max(1, noiseCount));
      double ratio = best / noise;
      double y0 = Math.Log(Math.Max(1e-18, Power(spectrum, bestBin - 1)));
      double y1 = Math.Log(Math.Max(1e-18, Power(spectrum, bestBin)));
      double y2 = Math.Log(Math.Max(1e-18, Power(spectrum, bestBin + 1)));
      double curvature = y0 - 2 * y1 + y2;
      double fraction = Math.Abs(curvature) > 1e-7
        ? Math.Clamp(.5 * (y0 - y2) / curvature, -.5, .5) : 0;
      double hz = (bestBin + fraction) * binHz;
      double sigma = Math.Max(.5, binHz / Math.Sqrt(Math.Max(1, ratio)));
      double confidence = Math.Clamp((10 * Math.Log10(Math.Max(1, ratio)) - 5) / 18, 0, 1);
      bool ambiguous = secondPeak > best * .40 && secondPeak > noise * 12;
      bool valid = confidence >= .5 && double.IsFinite(hz) && !ambiguous;
      return new(mode, utc, generation, hz, sigma,
        valid ? confidence : 0, valid, ambiguous);
    }

  }
}
