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
  public sealed class AfcSignalProcessing
  {
    private readonly double[] taps;
    private readonly int sampleRate;
    private readonly double ifReferenceHz;
    private double[]? lastSsbSpectrum;
    private long lastSsbGeneration = -1;
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
      DateTime utc, long generation, double expectedOffsetHz = 0, bool ssbPilot = false)
    {
      if (pcm.Length < sampleRate / 3)
        return new(mode, utc, generation, 0, double.PositiveInfinity, 0, false);
      // Work on the last two seconds only. Never process an unlimited backlog.
      pcm = pcm.Slice(Math.Max(0, pcm.Length - 2 * sampleRate));
      var bb = Downconvert(pcm);
      return mode switch
      {
        AfcSignalMode.FM => EstimateFm(bb, utc, generation),
        AfcSignalMode.CW => EstimateTone(bb, mode, utc, generation, expectedOffsetHz),
        AfcSignalMode.SSB when ssbPilot =>
          EstimateTone(bb, mode, utc, generation, expectedOffsetHz),
        AfcSignalMode.SSB => EstimateSsbRelative(bb, utc, generation),
        _ => new(mode, utc, generation, 0, double.PositiveInfinity, 0, false)
      };
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
      for (int n = 0; n < samples.Length; n++)
      {
        double phase = phaseStep * n;
        mixed[n] = new Complex(
          samples[n] * Math.Cos(phase),
          samples[n] * Math.Sin(phase));
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

    private AfcObservation EstimateFm(Complex[] bb, DateTime utc, long generation)
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
      double confidence = Math.Clamp(
        Math.Min(1.0, power * 200) *
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
      bool valid = double.IsFinite(median) && Math.Abs(median) < 8000 &&
        power > 1e-8 && confidence >= 0.4;
      return new(AfcSignalMode.FM, utc, generation, median, sigma,
        valid ? confidence : 0, valid);
    }

    private AfcObservation EstimateTone(Complex[] bb, AfcSignalMode mode,
      DateTime utc, long generation, double expectedHz)
    {
      // 8192 points at 24 kS/s: 2.93-Hz bins. Windowed-FFT interpolation is
      // refined by phase slope over successive key-down sections later.
      const int fftSize = 8192;
      if (bb.Length < fftSize)
        return new(mode, utc, generation, 0, 9999, 0, false);
      var spectrum = new Complex[fftSize];
      int start = bb.Length - fftSize;
      for (int i = 0; i < fftSize; i++)
      {
        double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (fftSize - 1));
        spectrum[i] = bb[start + i] * hann;
      }
      Fourier(spectrum);
      double binHz = sampleRate / 2.0 / fftSize;
      // Search the target's local interval, never the strongest unrelated
      // station across the whole IF. CW initial selection can be adjusted
      // in the UI to follow individual pileup signals.
      // Explicitly selected IF targets get a narrow admission gate; a nearby
      // stronger pileup station must not silently become the new target.
      double span = mode == AfcSignalMode.CW ? 55 : 50;
      int low = (int)Math.Ceiling((expectedHz - span) / binHz);
      int high = (int)Math.Floor((expectedHz + span) / binHz);
      double best = -1, side = 0;
      int bestBin = 0, count = 0;
      for (int b = low; b <= high; b++)
      {
        int pos = (b % fftSize + fftSize) % fftSize;
        double p = spectrum[pos].Magnitude * spectrum[pos].Magnitude;
        side += p;
        count++;
        if (p > best) { best = p; bestBin = b; }
      }
      if (best <= 0 || count <= 3)
        return new(mode, utc, generation, 0, 9999, 0, false);
      double noise = Math.Max(1e-18, (side - best) / Math.Max(1, count - 1));
      double ratio = best / noise;
      double y0 = Math.Log(Math.Max(1e-18, Power(spectrum, bestBin - 1)));
      double y1 = Math.Log(Math.Max(1e-18, Power(spectrum, bestBin)));
      double y2 = Math.Log(Math.Max(1e-18, Power(spectrum, bestBin + 1)));
      double curvature = y0 - 2 * y1 + y2;
      double fraction = Math.Abs(curvature) > 1e-7
        ? Math.Clamp(0.5 * (y0 - y2) / curvature, -0.5, 0.5)
        : 0;
      double hz = (bestBin + fraction) * binHz;
      double sigma = Math.Max(0.5, binHz / Math.Sqrt(Math.Max(1, ratio)));
      double confidence = Math.Clamp((10 * Math.Log10(Math.Max(1, ratio)) - 5) / 18, 0, 1);
      bool valid = confidence >= 0.5 && double.IsFinite(hz);
      return new(mode, utc, generation, hz, sigma, valid ? confidence : 0, valid);
    }


    private double FmSpectralCenter(Complex[] bb)
    {
      const int n = 8192;
      if (bb.Length < n) return double.NaN;
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
