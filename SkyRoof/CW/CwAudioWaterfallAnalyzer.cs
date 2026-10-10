namespace SkyRoof.CW
{
  public readonly record struct CwAudioSpectrumFrame(
    DateTime EndUtc,
    long EndSampleIndex,
    double MinFrequencyHz,
    double MaxFrequencyHz,
    float[] PowerDb)
  {
    public double BinHz =>
      PowerDb.Length > 1
        ? (MaxFrequencyHz -
           MinFrequencyHz) /
          (PowerDb.Length - 1)
        : 0;
  }

  /// <summary>
  /// Lightweight display-only spectrum frontend for the CW Console waterfall.
  /// It is intentionally independent from the detector/tracker STFT so UI
  /// refresh cost can never alter tracking statistics or covariance.
  /// </summary>
  public sealed class CwAudioWaterfallAnalyzer : IDisposable
  {
    private readonly int sampleRate;
    private readonly int fftSize;
    private readonly double minFrequencyHz;
    private readonly double maxFrequencyHz;
    private readonly int outputBins;
    private readonly double[] window;
    private readonly CwRealFft fft;

    public CwAudioWaterfallAnalyzer(
      int sampleRate = SdrConst.AUDIO_SAMPLING_RATE,
      int fftSize = 4096,
      double minFrequencyHz = 100,
      double maxFrequencyHz = 2000,
      int outputBins = 384)
    {
      if (sampleRate < 1000 ||
          sampleRate > 384000)
        throw new ArgumentOutOfRangeException(
          nameof(sampleRate));
      if (fftSize < 256 ||
          (fftSize & (fftSize - 1)) != 0)
        throw new ArgumentOutOfRangeException(
          nameof(fftSize));
      if (!double.IsFinite(
            minFrequencyHz) ||
          !double.IsFinite(
            maxFrequencyHz) ||
          minFrequencyHz < 0 ||
          maxFrequencyHz <=
            minFrequencyHz ||
          maxFrequencyHz >=
            sampleRate / 2.0)
        throw new ArgumentOutOfRangeException(
          nameof(maxFrequencyHz));
      if (outputBins is < 32 or > 4096)
        throw new ArgumentOutOfRangeException(
          nameof(outputBins));

      this.sampleRate = sampleRate;
      this.fftSize = fftSize;
      this.minFrequencyHz =
        minFrequencyHz;
      this.maxFrequencyHz =
        maxFrequencyHz;
      this.outputBins = outputBins;

      window =
        Enumerable.Range(0, fftSize)
          .Select(i =>
            0.5 -
            0.5 *
            Math.Cos(
              2 * Math.PI * i /
              fftSize))
          .ToArray();
      fft = new CwRealFft(fftSize);
    }

    public int SampleRate => sampleRate;
    public int FftSize => fftSize;
    public double MinFrequencyHz =>
      minFrequencyHz;
    public double MaxFrequencyHz =>
      maxFrequencyHz;
    public int OutputBins => outputBins;

    public CwAudioSpectrumFrame Analyze(
      CwAudioSnapshot snapshot)
    {
      if (snapshot.SampleRate !=
          sampleRate)
        throw new ArgumentException(
          "Waterfall analyzer and PCM sample rates must match.",
          nameof(snapshot));
      if (snapshot.Samples.Length <
          fftSize)
        throw new ArgumentException(
          "CW waterfall snapshot is shorter than one FFT.",
          nameof(snapshot));

      int start =
        snapshot.Samples.Length -
        fftSize;

      for (int i = 0;
           i < fftSize;
           i++)
      {
        fft.Input[i] =
          (float)(snapshot.Samples[start + i] *
            window[i]);
      }

      fft.Forward();

      double binHz =
        sampleRate /
        (double)fftSize;
      var result =
        new float[outputBins];

      for (int i = 0;
           i < outputBins;
           i++)
      {
        double fraction =
          outputBins == 1
            ? 0
            : i /
              (double)(outputBins - 1);
        double frequency =
          minFrequencyHz +
          fraction *
          (maxFrequencyHz -
           minFrequencyHz);
        double sourceBin =
          frequency / binHz;

        int lower =
          Math.Clamp(
            (int)Math.Floor(sourceBin),
            0,
            fftSize / 2);
        int upper =
          Math.Min(
            fftSize / 2,
            lower + 1);
        double mix =
          sourceBin - lower;

        double p0 = fft.Power(lower);
        double p1 = fft.Power(upper);
        double power =
          p0 * (1 - mix) +
          p1 * mix;

        result[i] =
          (float)(
            10 *
            Math.Log10(
              Math.Max(
                power,
                1e-20)));
      }

      return new(
        snapshot.EndUtc,
        snapshot.EndSampleIndex,
        minFrequencyHz,
        maxFrequencyHz,
        result);
    }

    private static double Power(
      Complex value) =>
      value.Real * value.Real +
      value.Imaginary *
      value.Imaginary;
  }
}
