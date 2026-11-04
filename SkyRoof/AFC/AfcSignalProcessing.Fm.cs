using System;
using System.Linq;
using System.Numerics;

namespace SkyRoof.AFC
{
  // Mode-specific estimators share the validated real-IF downconverter
  // and deterministic FFT helpers through this partial type.
  public sealed partial class AfcSignalProcessing
  {
    private AfcObservation EstimateFm(Complex[] bb, DateTime utc,
      long generation, AfcAcquisitionPreset preset)
    {
      var primary = EstimateFmSingle(bb, utc, generation);
      // Once more independent time information arrives, run a second
      // overlapping short-time estimator. Agreement may refine the offset;
      // disagreement REDUCES certainty, never creates a false lock.
      // Both analyses share samples; never treat them as independent CAT
      // confirmations or inflate the confidence score.
      if (preset != AfcAcquisitionPreset.Adaptive || bb.Length < 11500 ||
          !primary.HasAbsoluteReference)
        return primary;
      int half = bb.Length / 2;
      var recent = new Complex[half];
      Array.Copy(bb, half, recent, 0, half);
      var shortResult = EstimateFmSingle(recent, utc, generation);
      if (!shortResult.HasAbsoluteReference)
        return primary;
      double difference = Math.Abs(primary.IfOffsetHz - shortResult.IfOffsetHz);
      if (difference > Math.Max(90, 3 * Math.Max(primary.SigmaHz,
          shortResult.SigmaHz)))
        return primary with
        {
          SigmaHz = Math.Max(primary.SigmaHz, difference * .5),
          Confidence = primary.Confidence * .6,
          HasAbsoluteReference = false,
          Ambiguous = true
        };
      double weight = Math.Clamp(
        primary.SigmaHz / Math.Max(1, primary.SigmaHz + shortResult.SigmaHz),
        .10, .30);
      return primary with
      {
        IfOffsetHz = primary.IfOffsetHz * (1 - weight) +
          shortResult.IfOffsetHz * weight,
        // Overlap makes the measurements correlated: no spurious reduction
        // in sigma below the reliable single-window lower bound.
        SigmaHz = Math.Max(5, primary.SigmaHz),
        Confidence = Math.Min(primary.Confidence, shortResult.Confidence)
      };
    }

    private AfcObservation EstimateFmSingle(Complex[] bb, DateTime utc,
      long generation)
    {
      const int segments = 6;
      if (bb.Length < 1024) return new(AfcSignalMode.FM, utc, generation, 0, 9999, 0, false);
      double[] freq = new double[segments];
      double[] energy = new double[segments];
      int step = bb.Length / segments;
      for (int k = 0; k < segments; k++)
      {
        double phase = 0, amp = 0;
        int lo = Math.Max(1, k * step);
        int hi = Math.Min(bb.Length, (k + 1) * step);
        for (int i = lo; i < hi; i++)
        {
          Complex d = bb[i] * Complex.Conjugate(bb[i - 1]);
          if (d.Magnitude < 1e-14) continue;
          // Arithmetic mean of instantaneous angular velocity is equivalent
          // to the phase-slope estimate on unwrapped adjacent phase steps.
          phase += Math.Atan2(d.Imaginary, d.Real);
          amp += bb[i].Magnitude * bb[i].Magnitude;
        }
        freq[k] = phase / Math.Max(1, hi - lo) * (sampleRate / 2.0) / (2 * Math.PI);
        energy[k] = amp / Math.Max(1, hi - lo);
      }
      double median = Median(freq);
      double mad = Median(freq.Select(v => Math.Abs(v - median)).ToArray());
      // The mean modulation can be nonzero for a finite speech interval.
      // High inter-segment variation is an explicit uncertainty indication;
      // it must not turn into an aggressive CAT correction.
      double sigma = Math.Max(5, 1.4826 * mad / Math.Sqrt(segments));
      double power = Median(energy);
      // Frequency confidence should primarily depend on relative signal
      // quality, not the operator's Windows audio gain setting.
      double snrRatio = EstimateFmSpectralQuality(bb);
      double confidence = Math.Clamp(
        (snrRatio / (snrRatio + 2.0)) *
        Math.Max(0, 1 - mad / 500.0), 0, 1);
      // Independent spectral-centroid estimate: detect unusually biased
      // short FM discriminator windows or strong off-center interference.
      // Agreement alone is not proof of an unbiased FM signal.
      double spectralHz = FmSpectralCenter(bb);
      if (double.IsFinite(spectralHz))
      {
        double disagreement = Math.Abs(median - spectralHz);
        confidence *= Math.Clamp(250.0 / Math.Max(250, disagreement), 0, 1);
        sigma = Math.Max(sigma, disagreement * 0.25);
      }
      // A weak-but-narrow FM candidate may enter temporal corroboration.
      // It is NOT a CAT-ready reference by itself: the controller requires
      // three consistent fresh measurement epochs before upgrading it.
      bool valid = double.IsFinite(median) && Math.Abs(median) < 6800 &&
        power > 1e-8 && snrRatio >= .65 &&
        confidence >= .22 && sigma <= 150;
      // Keep the measured candidate and its numerical quality even when
      // confidence / IF ambiguity rejects it as an AFC reference. Previously
      // a rejected nonzero FM offset was reported as 0% without any reason,
      // making a real carrier shift indistinguishable from missing PCM.
      // HasAbsoluteReference remains false, so the CAT servo cannot use it.
      return new(AfcSignalMode.FM, utc, generation, median, sigma,
        confidence, valid);
    }

    private double EstimateFmSpectralQuality(Complex[] bb)
    {
      int n = bb.Length >= 8192 ? 8192 :
        bb.Length >= 4096 ? 4096 : 0;
      if (n == 0) return 0;
      var s = new Complex[n];
      for (int i = 0; i < n; i++)
      {
        double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        s[i] = bb[bb.Length - n + i] * w;
      }
      Fourier(s);
      double signal = 0, noise = 0;
      int signalN = 0, noiseN = 0;
      double bin = sampleRate / 2.0 / n;
      for (int k = -3200; k <= 3200; k++)
      {
        double freq = Math.Abs(k * bin);
        double p = Power(s, k);
        if (freq < 6000)
        {
          signal += p;
          signalN++;
        }
        else if (freq >= 7500 && freq <= 8400)
        {
          noise += p;
          noiseN++;
        }
      }
      if (signalN < 1 || noiseN < 1) return 0;
      double basePower = Math.Max(1e-16, noise / noiseN);
      return Math.Max(0, (signal / signalN) / basePower - 1);
    }

    private double FmSpectralCenter(Complex[] bb)
    {
      int n = bb.Length >= 8192 ? 8192 :
        bb.Length >= 4096 ? 4096 : 0;
      if (n == 0) return double.NaN;
      var spectrum = new Complex[n];
      for (int i = 0; i < n; i++)
      {
        double win = .5 - .5 * Math.Cos(2 * Math.PI * i / (n - 1));
        spectrum[i] = bb[bb.Length - n + i] * win;
      }
      Fourier(spectrum);
      double binHz = sampleRate / 2.0 / n, sum = 0, weighted = 0;
      for (int k = -2400; k <= 2400; k++)
      {
        double hz = k * binHz;
        if (Math.Abs(hz) > 6800) continue;
        double power = Power(spectrum, k);
        sum += power;
        weighted += hz * power;
      }
      return sum > 1e-12 ? weighted / sum : double.NaN;
    }

    /// <summary>
    /// Reference-free SSB has no unique absolute carrier. Frame-to-frame
    /// spectral displacement is useful as a suggestion only. HasAbsoluteReference
    /// intentionally stays false, so no CAT writes can result.
    /// </summary>
  }
}
