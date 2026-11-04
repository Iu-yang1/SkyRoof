using System;
using System.Linq;
using System.Numerics;

namespace SkyRoof.AFC
{
  // Mode-specific estimators share the validated real-IF downconverter
  // and deterministic FFT helpers through this partial type.
  public sealed partial class AfcSignalProcessing
  {
    private AfcObservation EstimateSsbRelative(Complex[] bb,
      DateTime utc, long generation)
    {
      const int n = 8192, halfBins = 1024;
      if (bb.Length < n)
        return new(AfcSignalMode.SSB, utc, generation, 0, 9999, 0, false);
      var spec = new Complex[n];
      for (int i = 0; i < n; i++)
      {
        double win = .5 - .5 * Math.Cos(2 * Math.PI * i / (n - 1));
        spec[i] = bb[bb.Length - n + i] * win;
      }
      Fourier(spec);
      var profile = new double[2 * halfBins + 1];
      double mean = 0;
      for (int k = -halfBins; k <= halfBins; k++)
      {
        double v = Math.Log(1e-12 + Power(spec, k));
        profile[k + halfBins] = v;
        mean += v;
      }
      mean /= profile.Length;
      double norm = 0;
      for (int i = 0; i < profile.Length; i++)
      {
        profile[i] -= mean;
        norm += profile[i] * profile[i];
      }
      if (lastSsbSpectrum == null || lastSsbGeneration != generation ||
          norm < 1e-8)
      {
        lastSsbSpectrum = profile;
        lastSsbGeneration = generation;
        return new(AfcSignalMode.SSB, utc, generation, 0, 9999, 0, false);
      }
      double best = -1;
      int bestShift = 0;
      // +/-75 Hz relative drift; speech-content changes often fail the
      // correlation-quality gate, which correctly produces no measurement.
      for (int shift = -25; shift <= 25; shift++)
      {
        double cross = 0, a = 0, b = 0;
        for (int k = 40; k < profile.Length - 40; k++)
        {
          double x = lastSsbSpectrum[k], y = profile[k + shift];
          cross += x * y;
          a += x * x;
          b += y * y;
        }
        double corr = cross / Math.Sqrt(Math.Max(1e-15, a * b));
        if (corr > best) { best = corr; bestShift = shift; }
      }
      lastSsbSpectrum = profile;
      double confidence = Math.Clamp((best - .55) / .4, 0, 1);
      return new(AfcSignalMode.SSB, utc, generation,
        bestShift * (sampleRate / 2.0 / n), 15, confidence, false);
    }

  }
}
