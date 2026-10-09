using MathNet.Numerics.IntegralTransforms;
using System.Numerics;

namespace SkyRoof.CW
{
  public sealed class CwDetectorOptions
  {
    public int SampleRate { get; init; } = SdrConst.AUDIO_SAMPLING_RATE;
    public int FftSize { get; init; } = 4096;
    public int HopSize { get; init; } = 1024;
    public double MinFrequencyHz { get; init; } = 100;
    public double MaxFrequencyHz { get; init; } = 2000;
    public double MinimumSnrDb { get; init; } = 6;
    public double ActivitySnrDb { get; init; } = 4;
    public double MinActiveFraction { get; init; } = 0.08;
    public double MaxActiveFraction { get; init; } = 0.94;
    public int MinimumActivityTransitions { get; init; } = 2;
    public double MinimumSeparationHz { get; init; } = 25;
    public int MaxCandidates { get; init; } = 16;
  }

  /// <summary>
  /// Local multi-peak CW detector. It intentionally does not use the optional
  /// web DeepCW detector model, so the desktop receiver can be built from the
  /// independently published deepcw-engine text model plus conventional DSP.
  /// </summary>
  public sealed class CwCandidateDetector
  {
    private readonly CwDetectorOptions options;
    private readonly double[] window;

    public CwCandidateDetector(CwDetectorOptions? options = null)
    {
      this.options = options ?? new CwDetectorOptions();
      ValidateOptions(this.options);
      window = Enumerable.Range(0, this.options.FftSize)
        .Select(i => 0.5 - 0.5 * Math.Cos(
          2 * Math.PI * i / (this.options.FftSize - 1)))
        .ToArray();
    }

    public IReadOnlyList<CwSignalCandidate> Detect(ReadOnlySpan<float> samples)
    {
      if (samples.Length < options.FftSize)
        return Array.Empty<CwSignalCandidate>();

      int frameCount = 1 + (samples.Length - options.FftSize) / options.HopSize;
      double binHz = options.SampleRate / (double)options.FftSize;
      int firstBin = Math.Max(1, (int)Math.Ceiling(options.MinFrequencyHz / binHz));
      int lastBin = Math.Min(options.FftSize / 2 - 1,
        (int)Math.Floor(options.MaxFrequencyHz / binHz));
      int binCount = lastBin - firstBin + 1;
      if (binCount < 3 || frameCount < 2)
        return Array.Empty<CwSignalCandidate>();

      var powers = new double[frameCount, binCount];
      var frameNoise = new double[frameCount];
      var fft = new Complex[options.FftSize];
      var noiseScratch = new double[binCount];

      for (int frame = 0; frame < frameCount; frame++)
      {
        int offset = frame * options.HopSize;
        for (int i = 0; i < options.FftSize; i++)
          fft[i] = new Complex(samples[offset + i] * window[i], 0);

        Fourier.Forward(fft, FourierOptions.Matlab);

        for (int b = 0; b < binCount; b++)
        {
          Complex value = fft[firstBin + b];
          double power = value.Real * value.Real + value.Imaginary * value.Imaginary;
          powers[frame, b] = power;
          noiseScratch[b] = power;
        }

        Array.Sort(noiseScratch);
        frameNoise[frame] = Math.Max(
          MedianSorted(noiseScratch), 1e-20);
      }

      var meanPower = new double[binCount];
      var snrDb = new double[binCount];
      var activeFractions = new double[binCount];
      var transitions = new int[binCount];
      double activityRatio = Math.Pow(10, options.ActivitySnrDb / 10.0);

      for (int b = 0; b < binCount; b++)
      {
        double signalSum = 0;
        double noiseSum = 0;
        int active = 0;
        int transitionCount = 0;
        bool? previousActive = null;

        for (int frame = 0; frame < frameCount; frame++)
        {
          double power = powers[frame, b];
          double noise = frameNoise[frame];
          signalSum += power;
          noiseSum += noise;

          bool isActive = power >= noise * activityRatio;
          if (isActive) active++;
          if (previousActive.HasValue && previousActive.Value != isActive)
            transitionCount++;
          previousActive = isActive;
        }

        meanPower[b] = signalSum / frameCount;
        snrDb[b] = 10 * Math.Log10(
          Math.Max(signalSum, 1e-20) / Math.Max(noiseSum, 1e-20));
        activeFractions[b] = active / (double)frameCount;
        transitions[b] = transitionCount;
      }

      var peaks = new List<CwSignalCandidate>();
      for (int b = 1; b < binCount - 1; b++)
      {
        if (snrDb[b] < options.MinimumSnrDb) continue;
        if (activeFractions[b] < options.MinActiveFraction ||
            activeFractions[b] > options.MaxActiveFraction) continue;
        if (transitions[b] < options.MinimumActivityTransitions) continue;
        if (meanPower[b] < meanPower[b - 1] || meanPower[b] <= meanPower[b + 1])
          continue;

        double weightedBin = 0;
        double weight = 0;
        for (int d = -1; d <= 1; d++)
        {
          double p = meanPower[b + d];
          weightedBin += (firstBin + b + d) * p;
          weight += p;
        }

        double frequency = (weight > 0
          ? weightedBin / weight
          : firstBin + b) * binHz;
        peaks.Add(new CwSignalCandidate(frequency, snrDb[b]));
      }

      var selected = new List<CwSignalCandidate>();
      foreach (var peak in peaks
        .OrderByDescending(p => p.SnrDb)
        .ThenBy(p => p.FrequencyHz))
      {
        if (selected.Any(existing =>
          Math.Abs(existing.FrequencyHz - peak.FrequencyHz) <
          options.MinimumSeparationHz))
          continue;

        selected.Add(peak);
        if (selected.Count >= options.MaxCandidates) break;
      }

      return selected.OrderBy(p => p.FrequencyHz).ToArray();
    }

    private static double MedianSorted(double[] values)
    {
      int middle = values.Length / 2;
      return values.Length % 2 == 0
        ? (values[middle - 1] + values[middle]) * 0.5
        : values[middle];
    }

    private static void ValidateOptions(CwDetectorOptions value)
    {
      if (value.SampleRate < 1000 || value.SampleRate > 384000)
        throw new ArgumentOutOfRangeException(nameof(value.SampleRate));
      if (value.FftSize < 256 || (value.FftSize & (value.FftSize - 1)) != 0)
        throw new ArgumentOutOfRangeException(nameof(value.FftSize));
      if (value.HopSize < 1 || value.HopSize > value.FftSize)
        throw new ArgumentOutOfRangeException(nameof(value.HopSize));
      if (value.MinFrequencyHz <= 0 ||
          value.MaxFrequencyHz <= value.MinFrequencyHz ||
          value.MaxFrequencyHz >= value.SampleRate / 2.0)
        throw new ArgumentOutOfRangeException(nameof(value.MaxFrequencyHz));
      if (value.MinActiveFraction < 0 || value.MinActiveFraction >= 1 ||
          value.MaxActiveFraction <= value.MinActiveFraction ||
          value.MaxActiveFraction > 1)
        throw new ArgumentOutOfRangeException(nameof(value.MaxActiveFraction));
      if (value.MinimumSeparationHz <= 0 || value.MaxCandidates < 1)
        throw new ArgumentOutOfRangeException(nameof(value.MaxCandidates));
    }
  }

  /// <summary>
  /// Complete pre-neural Pileup frontend: bounded PCM history -> multi-carrier
  /// detection -> stable Doppler/QSB-aware lane identities.
  /// </summary>
  public sealed class CwPileupFrontEnd
  {
    public CwAudioHub Audio { get; }
    public CwCandidateDetector Detector { get; }
    public CwPileupTrackManager Tracks { get; }
    public double AnalysisSeconds { get; }

    public CwPileupFrontEnd(
      int sampleRate = SdrConst.AUDIO_SAMPLING_RATE,
      double analysisSeconds = 2.4,
      CwDetectorOptions? detectorOptions = null,
      CwPileupTrackManager? trackManager = null)
    {
      if (!double.IsFinite(analysisSeconds) || analysisSeconds < 0.5 || analysisSeconds > 10)
        throw new ArgumentOutOfRangeException(nameof(analysisSeconds));

      CwDetectorOptions opts = detectorOptions ?? new CwDetectorOptions
      {
        SampleRate = sampleRate
      };
      if (opts.SampleRate != sampleRate)
        throw new ArgumentException("Detector and PCM sample rates must match.", nameof(detectorOptions));

      AnalysisSeconds = analysisSeconds;
      Audio = new CwAudioHub(sampleRate, Math.Max(analysisSeconds * 2, 6));
      Detector = new CwCandidateDetector(opts);
      Tracks = trackManager ?? new CwPileupTrackManager();
    }

    public void AddSamples(float[] data, int count, DateTime utc) =>
      Audio.Append(data, count, utc);

    public IReadOnlyList<CwSignalTrack> Analyze()
    {
      if (!Audio.TrySnapshot(AnalysisSeconds, out CwAudioSnapshot snapshot))
        return Array.Empty<CwSignalTrack>();

      IReadOnlyList<CwSignalCandidate> candidates =
        Detector.Detect(snapshot.Samples);
      return Tracks.Update(snapshot.EndUtc, candidates);
    }

    public void Reset()
    {
      Audio.Reset();
      Tracks.Reset();
    }
  }
}
