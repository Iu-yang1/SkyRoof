using System;
using System.Numerics;
using System.Linq;

namespace SkyRoof.AFC
{
  /// <summary>
  /// Offline-compatible, real 12-kHz IF PCM DSP. No Icom scope data is used as
  /// IQ; this is a true WASAPI/PCM path. Public Analyze is deterministic for
  /// captured PCM and synthetic tests. The 48-kHz input is mixed to baseband,
  /// 81-tap low-pass filtered, then decimated by two to 24 kcomplex/s.
  /// </summary>
  public sealed partial class AfcSignalProcessing
  {
    private readonly double[] taps;
    private readonly int sampleRate;
    private readonly double ifReferenceHz;
    private double[]? lastSsbSpectrum;
    private long lastSsbGeneration = -1;
    private long carrierGeneration = -1;
    private AfcSignalMode carrierMode;
    private double carrierSearchHz;
    private bool carrierTracked;
    private bool fmCandidateAcquired;
    public int SampleRate => sampleRate;

    public AfcSignalProcessing(int sampleRate = 48000, double ifReferenceHz = 12000)
    {
      if (sampleRate < 32000 || sampleRate > 192000 ||
          !double.IsFinite(ifReferenceHz) || ifReferenceHz <= 0 ||
          ifReferenceHz + 9000 >= sampleRate / 2.0)
        throw new ArgumentOutOfRangeException(nameof(sampleRate), "IF and full FM passband must fit below Nyquist.");
      this.sampleRate = sampleRate;
      this.ifReferenceHz = ifReferenceHz;
      taps = LowPass(81, 8800, sampleRate);
    }

    private static double[] LowPass(int count, double cutoffHz, double rate)
    {
      var h = new double[count];
      int mid = count / 2;
      double sum = 0;
      for (int i = 0; i < count; i++)
      {
        int k = i - mid;
        double ideal = k == 0 ? 2 * cutoffHz / rate :
          Math.Sin(2 * Math.PI * cutoffHz * k / rate) / (Math.PI * k);
        double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (count - 1)) +
          0.08 * Math.Cos(4 * Math.PI * i / (count - 1));
        h[i] = ideal * window;
        sum += h[i];
      }
      for (int i = 0; i < count; i++) h[i] /= sum;
      return h;
    }

    public AfcObservation Analyze(ReadOnlySpan<float> pcm, AfcSignalMode mode,
      DateTime utc, long generation, double expectedOffsetHz = double.NaN,
      bool ssbPilot = false,
      AfcAcquisitionPreset acquisitionPreset = AfcAcquisitionPreset.Balanced)
    {
      var profile = AfcTrackingProfile.For(mode, ssbPilot, acquisitionPreset);
      if (pcm.Length < profile.MinimumSamples)
        return new(mode, utc, generation, 0, double.PositiveInfinity, 0, false);
      // Only the newest mode-specific window contributes to this estimate.
      pcm = pcm.Slice(Math.Max(0, pcm.Length - profile.WindowSamples));
      var bb = Downconvert(pcm);
      bool toneMode = mode == AfcSignalMode.CW || (mode == AfcSignalMode.SSB && ssbPilot);
      if (carrierGeneration != generation || carrierMode != mode)
      {
        carrierGeneration = generation;
        carrierMode = mode;
        carrierSearchHz = expectedOffsetHz;
        carrierTracked = false;
        fmCandidateAcquired = false;
      }
      // Tone defaults to 0 Hz, but unselected FM must remain unrestricted.
      double search = toneMode && carrierTracked ? carrierSearchHz :
        double.IsFinite(expectedOffsetHz) ? expectedOffsetHz : 0;
      AfcObservation result = mode switch
      {
        AfcSignalMode.FM => EstimateFm(bb, utc, generation, acquisitionPreset),
        AfcSignalMode.CW => EstimateTone(bb, mode, utc, generation,
          search, acquisitionPreset),
        AfcSignalMode.SSB when ssbPilot =>
          EstimateTone(bb, mode, utc, generation, search, acquisitionPreset),
        AfcSignalMode.SSB => EstimateSsbRelative(bb, utc, generation),
        _ => new(mode, utc, generation, 0, double.PositiveInfinity, 0, false)
      };
      if (mode == AfcSignalMode.FM &&
          double.IsFinite(expectedOffsetHz) && !fmCandidateAcquired)
      {
        // An operator click is only an initial acquisition hint; it never
        // directly authorizes CAT. After the first qualified match the
        // servo may move the FM carrier away from the original click.
        if (Math.Abs(result.IfOffsetHz - expectedOffsetHz) > 1500)
          result = result with { HasAbsoluteReference = false,
            Ambiguous = true };
        else if (result.HasAbsoluteReference && !result.Ambiguous)
          fmCandidateAcquired = true;
      }
      if (toneMode && result.HasAbsoluteReference && !result.Ambiguous)
      {
        carrierSearchHz = result.IfOffsetHz;
        carrierTracked = true;
      }
      // Display FFT runs independently at its own cadence through
      // AfcIfDisplayAnalyzer, not through the CAT/measurement scheduler.
      // Avoid an extra 4096-point FFT on this control-critical path.
      return result;
    }

    private Complex[] Downconvert(ReadOnlySpan<float> samples)
    {
      int count = (samples.Length - taps.Length) / 2;
      if (count <= 0) return Array.Empty<Complex>();
      var output = new Complex[count];
      double phaseStep = -2 * Math.PI * ifReferenceHz / sampleRate;
      // Mix at the original sample rate. FIR rejects the negative-frequency
      // image before decimation: no direct 12-kHz real-PCM-as-IQ shortcut.
      var mixed = new Complex[samples.Length];
      if (Math.Abs(ifReferenceHz * 4 - sampleRate) < 1e-7)
      {
        // IC-9700 nominal 12 kHz / 48 kS/s is exactly a quarter-cycle
        // mixer. This removes ~2 trigonometric evaluations per IF sample
        // and cumulative NCO phase error while retaining the original
        // FIR image rejection and carrier frequency convention.
        for (int n = 0; n < samples.Length; n++)
          mixed[n] = (n & 3) switch
          {
            0 => new Complex(samples[n], 0),
            1 => new Complex(0, -samples[n]),
            2 => new Complex(-samples[n], 0),
            _ => new Complex(0, samples[n])
          };
      }
      else
      {
        // Custom IF reference frequencies retain full NCO precision.
        for (int n = 0; n < samples.Length; n++)
        {
          double phase = phaseStep * n;
          mixed[n] = new Complex(
            samples[n] * Math.Cos(phase),
            samples[n] * Math.Sin(phase));
        }
      }
      for (int i = 0; i < count; i++)
      {
        int p = 2 * i + taps.Length - 1;
        Complex sum = Complex.Zero;
        for (int k = 0; k < taps.Length; k++)
          sum += mixed[p - k] * taps[k];
        output[i] = sum;
      }
      return output;
    }

    private static double Power(Complex[] spec, int b)
    {
      int i = (b % spec.Length + spec.Length) % spec.Length;
      return spec[i].Real * spec[i].Real + spec[i].Imaginary * spec[i].Imaginary;
    }

    private static double Median(double[] values)
    {
      var ordered = (double[])values.Clone();
      Array.Sort(ordered);
      return ordered[ordered.Length / 2];
    }

    private static void Fourier(Complex[] a)
    {
      int n = a.Length;
      for (int i = 1, j = 0; i < n; i++)
      {
        int bit = n >> 1;
        for (; (j & bit) != 0; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) (a[i], a[j]) = (a[j], a[i]);
      }
      for (int len = 2; len <= n; len <<= 1)
      {
        double angle = -2 * Math.PI / len;
        Complex wn = new(Math.Cos(angle), Math.Sin(angle));
        for (int i = 0; i < n; i += len)
        {
          Complex w = Complex.One;
          for (int k = 0; k < len / 2; k++)
          {
            Complex u = a[i + k], v = w * a[i + k + len / 2];
            a[i + k] = u + v;
            a[i + k + len / 2] = u - v;
            w *= wn;
          }
        }
      }
    }
  }
}
