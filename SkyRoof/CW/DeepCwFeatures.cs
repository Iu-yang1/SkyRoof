using MathNet.Numerics.IntegralTransforms;
using System.Numerics;

namespace SkyRoof.CW
{
  public readonly record struct DeepCwTensor(float[] Data, int[] Dimensions)
  {
    public int TimeSteps => Dimensions.Length == 4 ? Dimensions[2] : 0;
    public int FrequencyBins => Dimensions.Length == 4 ? Dimensions[3] : 0;
  }

  /// <summary>
  /// Resamples one shared receive window once, computes the full RFFT magnitude
  /// once, then produces independently isolated DeepCW tensors for each lane.
  /// </summary>
  public sealed class DeepCwFeatureWindow
  {
    private readonly DeepCwModelMetadata metadata;
    private readonly float[] magnitudes;
    private readonly int fullBins;
    private readonly int firstModelBin;
    private readonly double binHz;

    public int FrameCount { get; }
    public int ResampledSampleCount { get; }
    public double DurationSeconds =>
      ResampledSampleCount / (double)metadata.SampleRate;

    private DeepCwFeatureWindow(
      DeepCwModelMetadata metadata,
      float[] magnitudes,
      int frameCount,
      int fullBins,
      int resampledSampleCount)
    {
      this.metadata = metadata;
      this.magnitudes = magnitudes;
      FrameCount = frameCount;
      this.fullBins = fullBins;
      ResampledSampleCount = resampledSampleCount;
      binHz = metadata.SampleRate / (double)metadata.FftLength;
      firstModelBin = (int)Math.Ceiling(metadata.MinFrequencyHz / binHz);
    }

    public static DeepCwFeatureWindow Create(
      ReadOnlySpan<float> audio,
      int sourceSampleRate,
      DeepCwModelMetadata metadata)
    {
      if (sourceSampleRate < 1000)
        throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
      ArgumentNullException.ThrowIfNull(metadata);
      metadata.Validate();
      if (audio.Length < 2)
        throw new ArgumentException("DeepCW audio window is too short.", nameof(audio));

      float[] resampled = CwWindowedSincResampler.Resample(
        audio, sourceSampleRate, metadata.SampleRate);

      int n = metadata.FftLength;
      int pad = n / 2;
      int paddedLength = checked(resampled.Length + 2 * pad);
      int frameCount = 1 + (paddedLength - n) / metadata.HopLength;
      int fullBins = n / 2 + 1;
      float[] magnitude = new float[checked(frameCount * fullBins)];
      var fft = new Complex[n];
      double[] hann = Enumerable.Range(0, n)
        .Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n))
        .ToArray();

      for (int frame = 0; frame < frameCount; frame++)
      {
        int start = frame * metadata.HopLength - pad;
        for (int i = 0; i < n; i++)
        {
          int sourceIndex = ReflectIndex(start + i, resampled.Length);
          fft[i] = new Complex(resampled[sourceIndex] * hann[i], 0);
        }

        Fourier.Forward(fft, FourierOptions.Matlab);
        int dest = frame * fullBins;
        for (int bin = 0; bin < fullBins; bin++)
        {
          Complex value = fft[bin];
          magnitude[dest + bin] =
            (float)Math.Sqrt(value.Real * value.Real +
                             value.Imaginary * value.Imaginary);
        }
      }

      return new(metadata, magnitude, frameCount, fullBins, resampled.Length);
    }

    public DeepCwTensor BuildStandardTensor()
    {
      float[] data = new float[checked(FrameCount * metadata.FrequencyBins)];
      for (int frame = 0; frame < FrameCount; frame++)
      {
        int src = frame * fullBins + firstModelBin;
        int dst = frame * metadata.FrequencyBins;
        for (int b = 0; b < metadata.FrequencyBins; b++)
          data[dst + b] = MathF.Log(1 + magnitudes[src + b]);
      }
      return new(data, [1, 1, FrameCount, metadata.FrequencyBins]);
    }


    /// <summary>
    /// Applies an activity-aware competing mask to a lane that has already
    /// been DDC-isolated and remodulated to targetCenterHz. Every confirmed
    /// track may contribute interference weight, even if that track is not
    /// selected for ONNX inference.
    /// </summary>
    public DeepCwTensor BuildActivityAwareTensor(
      CwSignalTrack target,
      IReadOnlyList<CwSignalTrack> allTracks,
      IReadOnlyDictionary<int, float[]> activityByTrack,
      double targetCenterHz = 800,
      double supportBandwidthHz = 240,
      double maskFloor = 0.04,
      double maskSigmaHz = 24)
    {
      ArgumentNullException.ThrowIfNull(allTracks);
      ArgumentNullException.ThrowIfNull(activityByTrack);
      if (!allTracks.Any(t => t.Id == target.Id))
        throw new ArgumentException(
          "Target track must be in the interference set.",
          nameof(allTracks));
      if (!activityByTrack.TryGetValue(target.Id, out float[]? targetActivity) ||
          targetActivity.Length != FrameCount)
        throw new ArgumentException(
          "Target activity timeline does not match DeepCW frames.",
          nameof(activityByTrack));
      if (targetCenterHz < metadata.MinFrequencyHz ||
          targetCenterHz > metadata.MaxFrequencyHz)
        throw new ArgumentOutOfRangeException(nameof(targetCenterHz));
      if (supportBandwidthHz < 40 ||
          supportBandwidthHz > metadata.MaxFrequencyHz - metadata.MinFrequencyHz)
        throw new ArgumentOutOfRangeException(nameof(supportBandwidthHz));
      if (maskFloor <= 0 || maskFloor > 1)
        throw new ArgumentOutOfRangeException(nameof(maskFloor));
      if (maskSigmaHz < 4 || maskSigmaHz > supportBandwidthHz / 2.0)
        throw new ArgumentOutOfRangeException(nameof(maskSigmaHz));

      float[] data =
        new float[checked(FrameCount * metadata.FrequencyBins)];
      double halfSupport = supportBandwidthHz / 2.0;
      double duration = DurationSeconds;
      double targetSigma = Math.Clamp(
        Math.Sqrt(maskSigmaHz * maskSigmaHz +
                  4 * target.FrequencySigmaHz * target.FrequencySigmaHz),
        6, halfSupport);

      for (int frame = 0; frame < FrameCount; frame++)
      {
        double frameSeconds = frame * metadata.HopLength /
          (double)metadata.SampleRate;
        double relativeToEnd = frameSeconds - duration;
        double pTarget = Math.Clamp(targetActivity[frame], 0, 1);
        int row = frame * fullBins;
        int dst = frame * metadata.FrequencyBins;

        for (int b = 0; b < metadata.FrequencyBins; b++)
        {
          double outputHz = metadata.MinFrequencyHz + b * binHz;
          double targetOffset = outputHz - targetCenterHz;
          if (Math.Abs(targetOffset) > halfSupport)
          {
            data[dst + b] = 0;
            continue;
          }

          double numerator = pTarget *
            GaussianWeight(targetOffset, targetSigma);
          double denominator = maskFloor;

          foreach (CwSignalTrack competitor in allTracks)
          {
            if (!competitor.Confirmed) continue;
            if (!activityByTrack.TryGetValue(
                  competitor.Id, out float[]? competitorActivity) ||
                competitorActivity.Length != FrameCount)
              continue;

            double relativeHz =
              (competitor.FrequencyHz - target.FrequencyHz) +
              (competitor.DriftHzPerSecond -
               target.DriftHzPerSecond) * relativeToEnd;
            double competitorCenter = targetCenterHz + relativeHz;
            double competitorSigma = Math.Clamp(
              Math.Sqrt(maskSigmaHz * maskSigmaHz +
                        4 * competitor.FrequencySigmaHz *
                        competitor.FrequencySigmaHz),
              6, halfSupport);
            double pOn = Math.Clamp(
              competitorActivity[frame], 0, 1);
            denominator += pOn * GaussianWeight(
              outputHz - competitorCenter,
              competitorSigma);
          }

          double mask = numerator /
            Math.Max(denominator, 1e-12);
          double taper = 0.5 *
            (1 + Math.Cos(
              Math.PI * Math.Abs(targetOffset) / halfSupport));
          float magnitude = magnitudes[row + firstModelBin + b];
          magnitude *= (float)(mask * taper);
          data[dst + b] = MathF.Log(1 + magnitude);
        }
      }

      return new(
        data,
        [1, 1, FrameCount, metadata.FrequencyBins]);
    }

    /// <summary>
    /// Isolate one carrier in the already-computed magnitude spectrogram and
    /// translate that narrow slice to targetCenterHz inside the model passband.
    /// This avoids real-audio mixing images and lets all Pileup lanes share the
    /// expensive resampling/FFT pass.
    /// </summary>
    public DeepCwTensor BuildLaneTensor(
      double laneFrequencyHz,
      double bandwidthHz = 180,
      double targetCenterHz = 800)
    {
      var synthetic = new CwSignalTrack(
        1, laneFrequencyHz, 0, 0,
        DateTime.UnixEpoch, DateTime.UnixEpoch,
        Confirmed: true, Active: true);
      return BuildSeparatedLaneTensor(
        synthetic, [synthetic], bandwidthHz, targetCenterHz);
    }

    /// <summary>
    /// Ridge-aware soft time-frequency separation for one Pileup lane.
    ///
    /// Each track contributes a Gaussian likelihood around its predicted
    /// frequency at every frame. The target lane receives a Wiener-like
    /// normalized mask w_i/(floor + sum_j w_j), followed by translation to the
    /// DeepCW model center. This lets near-by lanes compete for shared TF
    /// energy rather than duplicating the same hard rectangular slice.
    /// </summary>
    public DeepCwTensor BuildSeparatedLaneTensor(
      CwSignalTrack target,
      IReadOnlyList<CwSignalTrack> allTracks,
      double bandwidthHz = 180,
      double targetCenterHz = 800,
      double maskFloor = 0.05,
      double maskSigmaHz = 24)
    {
      ArgumentNullException.ThrowIfNull(allTracks);
      if (!allTracks.Any(t => t.Id == target.Id))
        throw new ArgumentException(
          "Target track must be included in the separation set.",
          nameof(allTracks));
      if (!double.IsFinite(target.FrequencyHz) ||
          target.FrequencyHz <= 0 ||
          target.FrequencyHz >= metadata.SampleRate / 2.0)
        throw new ArgumentOutOfRangeException(nameof(target));
      if (!double.IsFinite(bandwidthHz) ||
          bandwidthHz < 25 || bandwidthHz > 600)
        throw new ArgumentOutOfRangeException(nameof(bandwidthHz));
      if (targetCenterHz < metadata.MinFrequencyHz ||
          targetCenterHz > metadata.MaxFrequencyHz)
        throw new ArgumentOutOfRangeException(nameof(targetCenterHz));
      if (!double.IsFinite(maskFloor) || maskFloor <= 0 || maskFloor > 1)
        throw new ArgumentOutOfRangeException(nameof(maskFloor));
      if (!double.IsFinite(maskSigmaHz) || maskSigmaHz < 6 ||
          maskSigmaHz > bandwidthHz / 2.0)
        throw new ArgumentOutOfRangeException(nameof(maskSigmaHz));

      float[] data =
        new float[checked(FrameCount * metadata.FrequencyBins)];
      double targetSigma = Math.Clamp(
        Math.Sqrt(maskSigmaHz * maskSigmaHz +
                  4 * target.FrequencySigmaHz * target.FrequencySigmaHz),
        8, bandwidthHz / 2.0);
      double half = bandwidthHz / 2.0;
      double duration = DurationSeconds;

      for (int frame = 0; frame < FrameCount; frame++)
      {
        int dst = frame * metadata.FrequencyBins;
        int row = frame * fullBins;
        double frameSeconds = frame * metadata.HopLength /
          (double)metadata.SampleRate;
        double relativeToEnd = frameSeconds - duration;
        double targetRidgeHz = target.FrequencyHz +
          target.DriftHzPerSecond * relativeToEnd;

        for (int b = 0; b < metadata.FrequencyBins; b++)
        {
          double outputHz = metadata.MinFrequencyHz + b * binHz;
          double delta = outputHz - targetCenterHz;
          if (Math.Abs(delta) > half)
          {
            data[dst + b] = 0;
            continue;
          }

          double sourceHz = targetRidgeHz + delta;
          double sourceBin = sourceHz / binHz;
          if (sourceBin < 0 || sourceBin > fullBins - 1)
          {
            data[dst + b] = 0;
            continue;
          }

          double targetWeight = GaussianWeight(
            sourceHz - targetRidgeHz, targetSigma);
          double denominator = maskFloor;
          foreach (CwSignalTrack competitor in allTracks)
          {
            if (!competitor.Confirmed) continue;
            double competitorRidge = competitor.FrequencyHz +
              competitor.DriftHzPerSecond * relativeToEnd;
            double competitorSigma = Math.Clamp(
              Math.Sqrt(maskSigmaHz * maskSigmaHz +
                        4 * competitor.FrequencySigmaHz *
                        competitor.FrequencySigmaHz),
              8, bandwidthHz / 2.0);
            denominator += GaussianWeight(
              sourceHz - competitorRidge, competitorSigma);
          }

          double mask = targetWeight / Math.Max(denominator, 1e-12);

          int lower = (int)Math.Floor(sourceBin);
          int upper = Math.Min(fullBins - 1, lower + 1);
          double fraction = sourceBin - lower;
          float lo = magnitudes[row + lower];
          float hi = magnitudes[row + upper];
          float value =
            (float)(lo * (1 - fraction) + hi * fraction);

          // Smooth the finite extraction support instead of a rectangular BPF.
          double taper = 0.5 *
            (1 + Math.Cos(Math.PI * Math.Abs(delta) / half));
          value *= (float)(mask * taper);
          data[dst + b] = MathF.Log(1 + value);
        }
      }

      return new(data,
        [1, 1, FrameCount, metadata.FrequencyBins]);
    }

    private static double GaussianWeight(double offsetHz, double sigmaHz)
    {
      double x = offsetHz / sigmaHz;
      if (Math.Abs(x) > 6) return 0;
      return Math.Exp(-0.5 * x * x);
    }

    private static int ReflectIndex(int index, int length)
    {
      if (length <= 1) return 0;
      while (index < 0 || index >= length)
      {
        if (index < 0) index = -index;
        if (index >= length) index = 2 * length - 2 - index;
      }
      return index;
    }
  }

  public static class CwWindowedSincResampler
  {
    public static float[] Resample(
      ReadOnlySpan<float> input,
      int sourceRate,
      int targetRate,
      int halfTaps = 32)
    {
      if (sourceRate < 1000 || targetRate < 1000)
        throw new ArgumentOutOfRangeException(nameof(sourceRate));
      if (halfTaps < 8 || halfTaps > 128)
        throw new ArgumentOutOfRangeException(nameof(halfTaps));
      if (input.Length == 0) return [];
      if (sourceRate == targetRate) return input.ToArray();

      // Promote before multiplication. Multi-second 48 kHz windows can
      // exceed Int32 when multiplied by a 9.6 kHz target rate.
      int outputLength = Math.Max(1,
        checked((int)Math.Round(
          (double)input.Length *
          targetRate /
          sourceRate)));
      float[] output = new float[outputLength];
      double ratio = sourceRate / (double)targetRate;
      double cutoff = 0.45 * Math.Min(1.0, targetRate / (double)sourceRate);

      for (int i = 0; i < outputLength; i++)
      {
        double center = (i + 0.5) * ratio - 0.5;
        int nearest = (int)Math.Floor(center);
        double sum = 0;
        double weightSum = 0;

        for (int tap = -halfTaps + 1; tap <= halfTaps; tap++)
        {
          int sourceIndex = nearest + tap;
          if ((uint)sourceIndex >= (uint)input.Length) continue;

          double x = sourceIndex - center;
          double sincArg = 2 * cutoff * x;
          double sinc = Math.Abs(sincArg) < 1e-12
            ? 1
            : Math.Sin(Math.PI * sincArg) / (Math.PI * sincArg);
          double normalized = x / halfTaps;
          if (Math.Abs(normalized) >= 1) continue;
          double window = 0.5 + 0.5 * Math.Cos(Math.PI * normalized);
          double weight = 2 * cutoff * sinc * window;
          sum += input[sourceIndex] * weight;
          weightSum += weight;
        }

        output[i] = weightSum == 0 ? 0 : (float)(sum / weightSum);
      }

      return output;
    }
  }
}
