using System;
using System.Linq;
using SkyRoof.CW;

namespace SkyRoof.AFC
{
  /// <summary>
  /// Display-only 48-kHz real PCM analyzer for the IC-9700 12-kHz IF.
  /// Reuses the existing FFTW3f-backed CwRealFft. Its results must never
  /// be passed to AfcController: visual smoothing/flattening has no place
  /// in the tracking error, confidence, CAT, or Doppler calculations.
  /// </summary>
  public sealed class AfcIfDisplayAnalyzer : IDisposable
  {
    public const int InputSampleRate = 48000;
    public const int FftSize = 8192;
    public const int BinCount = 2049;
    public const double BinHz = (double)InputSampleRate / FftSize;
    public const double MinOffsetHz = -6000;
    public const double MaxOffsetHz = 6000;
    private const int FirstBin = 1024; // (12 kHz - 6 kHz) / BinHz

    private readonly CwRealFft fft = new(FftSize);
    private readonly float[] window = new float[FftSize];
    private readonly double[] averagePower = new double[BinCount];
    private double windowSum;
    private bool initialized;
    private double floorDb = double.NaN;
    private DateTime previousUtc;
    private long previousGeneration = long.MinValue;
    private bool blackmanHarris = true;
    private bool haloReduction;
    private double smoothingMs = 200;
    private bool disposed;

    public void Configure(bool useBlackmanHarris, bool enableHaloReduction,
      double timeConstantMs)
    {
      if (disposed) throw new ObjectDisposedException(nameof(AfcIfDisplayAnalyzer));
      if (blackmanHarris != useBlackmanHarris)
      {
        blackmanHarris = useBlackmanHarris;
        RebuildWindow();
        Reset();
      }
      haloReduction = enableHaloReduction;
      smoothingMs = Math.Clamp(timeConstantMs, 0, 2000);
    }

    public AfcIfDisplayAnalyzer()
    {
      RebuildWindow();
    }

    private void RebuildWindow()
    {
      windowSum = 0;
      for (int i = 0; i < FftSize; i++)
      {
        double phase = 2 * Math.PI * i / (FftSize - 1);
        // Four-term Blackman-Harris gives much better strong-CW sidelobes
        // than Hann. The operator can choose Hann for a narrower main lobe.
        double w = blackmanHarris
          ? .35875 - .48829 * Math.Cos(phase) +
            .14128 * Math.Cos(2 * phase) - .01168 * Math.Cos(3 * phase)
          : .5 - .5 * Math.Cos(phase);
        window[i] = (float)w;
        windowSum += w;
      }
    }

    public void Reset()
    {
      initialized = false;
      previousGeneration = long.MinValue;
      previousUtc = default;
      floorDb = double.NaN;
      Array.Clear(averagePower);
    }

    public AfcIfDisplayFrame Analyze(ReadOnlySpan<float> pcm, DateTime utc,
      long generation)
    {
      if (disposed) throw new ObjectDisposedException(nameof(AfcIfDisplayAnalyzer));
      if (pcm.Length < FftSize)
        throw new ArgumentException("Not enough real IF PCM for a display FFT.",
          nameof(pcm));

      if (generation != previousGeneration || utc <= previousUtc)
      {
        initialized = false;
        floorDb = double.NaN;
        Array.Clear(averagePower);
      }
      double dtMs = initialized
        ? Math.Clamp((utc - previousUtc).TotalMilliseconds, 1, 1000)
        : 100;
      double alpha = smoothingMs <= 0 ? 1 :
        1 - Math.Exp(-dtMs / smoothingMs);

      int offset = pcm.Length - FftSize;
      double rmsSum = 0;
      int clipping = 0;
      for (int i = 0; i < FftSize; i++)
      {
        float sample = pcm[offset + i];
        fft.Input[i] = sample * window[i];
        rmsSum += sample * (double)sample;
        if (Math.Abs(sample) >= 0.995f) clipping++;
      }
      fft.Forward();

      // An unnormalised FFT of a full-scale sine has magnitude
      // windowSum/2. The reference is stable across frames, unlike
      // the old per-frame MAX=0 dB normalization.
      double norm = 4.0 / Math.Max(1, windowSum * windowSum);
      var rawDb = new float[BinCount];
      var trace = new float[BinCount];
      for (int i = 0; i < BinCount; i++)
      {
        double p = Math.Max(1e-20, fft.Power(FirstBin + i) * norm);
        averagePower[i] = initialized
          ? averagePower[i] + alpha * (p - averagePower[i])
          : p;
        rawDb[i] = (float)(10 * Math.Log10(p));
        trace[i] = (float)(10 * Math.Log10(Math.Max(1e-20, averagePower[i])));
      }
      initialized = true;
      previousUtc = utc;
      previousGeneration = generation;

      double measuredFloor = Percentile(trace, .35);
      // Never let exact zero PCM or numerical sidelobes establish a
      // -200 dBFS "noise floor". With a synthetic CW carrier and silence,
      // that would saturate all sidelobes and hide weaker CW signals.
      measuredFloor = Math.Max(-120, measuredFloor);
      floorDb = double.IsFinite(floorDb)
        ? .90 * floorDb + .10 * measuredFloor
        : measuredFloor;
      float strongest = trace.Max();
      // For clean single-carrier input, extend the display baseline only
      // as far as needed to keep the main lobe within a 38-dB viewport.
      // Noise-only input is NEVER peak-normalized to zero dBFS.
      double reference = Math.Max(floorDb, strongest - 38);

      if (haloReduction)
        FlattenNoiseBaseline(trace);

      for (int i = 0; i < trace.Length; i++)
        trace[i] = (float)Math.Clamp(trace[i] - reference, -12, 90);

      return new AfcIfDisplayFrame(
        utc, generation, trace, rawDb, (float)floorDb,
        Math.Sqrt(rmsSum / FftSize), clipping / (double)FftSize);
    }

    private static void FlattenNoiseBaseline(float[] values)
    {
      const int blockSize = 64;
      int blocks = (values.Length + blockSize - 1) / blockSize;
      var floors = new double[blocks];
      for (int b = 0; b < blocks; b++)
      {
        int start = b * blockSize;
        int count = Math.Min(blockSize, values.Length - start);
        floors[b] = Percentile(values.AsSpan(start, count), .30);
      }
      double globalFloor = Percentile(floors, .50);
      for (int i = 0; i < values.Length; i++)
      {
        double position = (i + .5) / blockSize - .5;
        int low = Math.Clamp((int)Math.Floor(position), 0, blocks - 1);
        int high = Math.Min(blocks - 1, low + 1);
        double fraction = Math.Clamp(position - low, 0, 1);
        double local = floors[low] + (floors[high] - floors[low]) * fraction;
        // Display-only operation. Preserve positive energy, and limit the
        // dark speckles from a colored IF noise-floor profile.
        values[i] = (float)(globalFloor + Math.Max(-2, values[i] - local));
      }
    }

    private static double Percentile(ReadOnlySpan<float> values, double q)
    {
      var copy = values.ToArray();
      Array.Sort(copy);
      return copy[Math.Clamp((int)(q * (copy.Length - 1)), 0, copy.Length - 1)];
    }

    private static double Percentile(ReadOnlySpan<double> values, double q)
    {
      var copy = values.ToArray();
      Array.Sort(copy);
      return copy[Math.Clamp((int)(q * (copy.Length - 1)), 0, copy.Length - 1)];
    }

    public void Dispose()
    {
      if (disposed) return;
      disposed = true;
      fft.Dispose();
    }
  }

  public readonly record struct AfcIfDisplayFrame(
    DateTime EndUtc,
    long Generation,
    float[] RelativeDb,
    float[] RawDb,
    float NoiseFloorDb,
    double InputRms,
    double ClippingFraction)
  {
    public double MinHz => AfcIfDisplayAnalyzer.MinOffsetHz;
    public double MaxHz => AfcIfDisplayAnalyzer.MaxOffsetHz;
    public double StepHz => AfcIfDisplayAnalyzer.BinHz;
  }
}
