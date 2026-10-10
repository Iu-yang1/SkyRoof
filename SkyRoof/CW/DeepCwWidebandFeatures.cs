using System.Collections.Concurrent;

namespace SkyRoof.CW
{
  public readonly record struct CwPhysicalLaneEvidence(
    double LocalSnrDb,
    double KeyingDepthDb,
    double DutyCycle,
    int KeyingTransitions);

  /// <summary>
  /// Wideband STFT whose physical time/frequency grid is calibrated to the
  /// DeepCW model frontend. For 48 kHz PCM and a 3.2 kHz DeepCW model:
  /// 3840 samples = 80 ms, 720 samples = 15 ms hop, and FFT bins are 12.5 Hz.
  ///
  /// This preserves the original receive bandwidth while allowing all lanes
  /// to share one FFT. Magnitudes are scaled by modelFFT/sourceFFT so a tone
  /// has approximately the same magnitude as the model-rate reference path.
  /// </summary>
  public sealed class DeepCwWidebandFeatureWindow
  {
    private static readonly ConcurrentDictionary<int, double[]> hannCache = new();
    private readonly DeepCwModelMetadata metadata;
    private readonly float[] magnitudes;
    private readonly int fullBins;
    private readonly double magnitudeScale;

    public int SourceSampleRate { get; }
    public int FftSize { get; }
    public int HopSize { get; }
    public int FrameCount { get; }
    public double BinHz => SourceSampleRate / (double)FftSize;
    public double FrameIntervalSeconds =>
      HopSize / (double)SourceSampleRate;
    public double DurationSeconds { get; }

    private DeepCwWidebandFeatureWindow(
      DeepCwModelMetadata metadata,
      int sourceSampleRate,
      int fftSize,
      int hopSize,
      int frameCount,
      int sampleCount,
      float[] magnitudes)
    {
      this.metadata = metadata;
      SourceSampleRate = sourceSampleRate;
      FftSize = fftSize;
      HopSize = hopSize;
      FrameCount = frameCount;
      DurationSeconds = sampleCount / (double)sourceSampleRate;
      this.magnitudes = magnitudes;
      fullBins = fftSize / 2 + 1;
      magnitudeScale = metadata.FftLength / (double)fftSize;
    }

    public static DeepCwWidebandFeatureWindow Create(
      ReadOnlySpan<float> audio,
      int sourceSampleRate,
      DeepCwModelMetadata metadata) =>
      Create(audio, sourceSampleRate, metadata, null, null);

    internal static DeepCwWidebandFeatureWindow Create(
      ReadOnlySpan<float> audio,
      int sourceSampleRate,
      DeepCwModelMetadata metadata,
      long? endSampleIndex,
      CwStftMagnitudeCache? frameCache)
    {
      if (sourceSampleRate < 1000)
        throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
      ArgumentNullException.ThrowIfNull(metadata);
      metadata.Validate();
      if (audio.Length < 2)
        throw new ArgumentException(
          "Wideband DeepCW audio window is too short.", nameof(audio));

      if (sourceSampleRate % metadata.SampleRate != 0)
        throw new NotSupportedException(
          "CW wideband frontend currently requires an integer sample-rate " +
          "ratio to the DeepCW model. Normalize capture PCM to 48 kHz.");

      int ratio = sourceSampleRate / metadata.SampleRate;
      if (ratio < 1)
        throw new NotSupportedException(
          "Source sample rate must not be below the DeepCW model rate.");

      int fftSize = checked(metadata.FftLength * ratio);
      int hopSize = checked(metadata.HopLength * ratio);
      int pad = fftSize / 2;
      int paddedLength = checked(audio.Length + 2 * pad);
      int frameCount = 1 + (paddedLength - fftSize) / hopSize;
      int fullBins = fftSize / 2 + 1;
      float[] magnitude = new float[checked(frameCount * fullBins)];
      using var fft = CwRealFft.Rent(fftSize);

      // Match the periodic Hann used by DeepCwFeatureWindow, but sampled at
      // the wideband rate over the same physical 80 ms interval.
      double[] hann = hannCache.GetOrAdd(fftSize, size =>
        Enumerable.Range(0, size)
          .Select(i => 0.5 - 0.5 *
            Math.Cos(2 * Math.PI * i / size))
          .ToArray());
      Span<float> fftInput = fft.Input;

      // Cache only whole interior frames: reflected boundary samples
      // depend on each individual six-second window and must not be reused.
      // A null/invalid absolute time (e.g. a direct offline model call)
      // deliberately disables cross-window reuse.
      long absoluteOrigin = endSampleIndex.HasValue &&
        endSampleIndex.Value >= audio.Length
          ? endSampleIndex.Value - audio.Length : -1;

      for (int frame = 0; frame < frameCount; frame++)
      {
        int start = frame * hopSize - pad;
        int dest = frame * fullBins;
        Span<float> row = magnitude.AsSpan(dest, fullBins);
        bool interior = start >= 0 &&
          (long)start + fftSize <= audio.Length;
        long absoluteStart = absoluteOrigin + start;
        if (interior && absoluteOrigin >= 0 &&
            frameCache != null &&
            frameCache.TryCopy(
              absoluteStart, sourceSampleRate, fftSize, row))
          continue;

        for (int i = 0; i < fftSize; i++)
        {
          int sourceIndex = ReflectIndex(
            start + i, audio.Length);
          fftInput[i] = (float)(
            audio[sourceIndex] * hann[i]);
        }

        fft.Forward();
        for (int bin = 0; bin < fullBins; bin++)
          row[bin] = (float)fft.Magnitude(bin);

        if (interior && absoluteOrigin >= 0 &&
            frameCache != null)
          frameCache.Save(
            absoluteStart, sourceSampleRate, fftSize, row);
      }

      return new(
        metadata,
        sourceSampleRate,
        fftSize,
        hopSize,
        frameCount,
        audio.Length,
        magnitude);
    }

    public float SampleCalibratedMagnitude(
      int frame,
      double frequencyHz)
    {
      if ((uint)frame >= (uint)FrameCount)
        throw new ArgumentOutOfRangeException(nameof(frame));
      if (!double.IsFinite(frequencyHz) ||
          frequencyHz < 0 ||
          frequencyHz > SourceSampleRate / 2.0)
        return 0;

      double sourceBin = frequencyHz / BinHz;
      int lower = (int)Math.Floor(sourceBin);
      if (lower >= fullBins - 1)
        return magnitudes[frame * fullBins + fullBins - 1] *
          (float)magnitudeScale;

      int upper = lower + 1;
      double fraction = sourceBin - lower;
      int row = frame * fullBins;
      float lo = magnitudes[row + lower];
      float hi = magnitudes[row + upper];
      return (float)((lo * (1 - fraction) + hi * fraction) *
        magnitudeScale);
    }

    public float[] GetRidgeMagnitudeSeries(CwSignalTrack track)
    {
      float[] result = new float[FrameCount];
      for (int frame = 0; frame < FrameCount; frame++)
      {
        double relativeToEnd =
          FrameRelativeToEndSeconds(frame);
        double ridgeHz = track.FrequencyHz +
          track.DriftHzPerSecond * relativeToEnd;

        // Combine the center and immediate neighboring bins to reduce
        // sensitivity to scalloping while preserving the 12.5 Hz resolution.
        double d = BinHz;
        float center = SampleCalibratedMagnitude(frame, ridgeHz);
        float lower = SampleCalibratedMagnitude(frame, ridgeHz - d);
        float upper = SampleCalibratedMagnitude(frame, ridgeHz + d);
        result[frame] = (float)Math.Sqrt(
          center * center +
          0.25 * lower * lower +
          0.25 * upper * upper);
      }
      return result;
    }

    public CwPhysicalLaneEvidence EvaluatePhysicalEvidence(
      CwSignalTrack track,
      ReadOnlySpan<float> activity)
    {
      if (activity.Length != FrameCount)
        throw new ArgumentException(
          "CW activity timeline must match the wideband frame count.",
          nameof(activity));

      var localSnr =
        new double[FrameCount];
      var centerMagnitude =
        new double[FrameCount];
      double[] offsets =
        [-120, -90, -60, 60, 90, 120];

      for (int frame = 0;
           frame < FrameCount;
           frame++)
      {
        double relativeToEnd =
          FrameRelativeToEndSeconds(frame);
        double ridgeHz =
          track.FrequencyHz +
          track.DriftHzPerSecond *
          relativeToEnd;
        double center =
          Math.Max(
            SampleCalibratedMagnitude(
              frame,
              ridgeHz),
            1e-12f);
        centerMagnitude[frame] = center;

        var side = new List<double>(offsets.Length);
        foreach (double offset in offsets)
        {
          double hz = ridgeHz + offset;
          if (hz <= BinHz ||
              hz >= SourceSampleRate / 2.0 - BinHz)
            continue;
          side.Add(
            Math.Max(
              SampleCalibratedMagnitude(
                frame,
                hz),
              1e-12f));
        }

        if (side.Count == 0)
        {
          localSnr[frame] = 0;
          continue;
        }

        side.Sort();
        double noise =
          side.Count % 2 == 0
            ? 0.5 *
              (side[side.Count / 2 - 1] +
               side[side.Count / 2])
            : side[side.Count / 2];
        localSnr[frame] =
          20.0 * Math.Log10(
            center /
            Math.Max(noise, 1e-12));
      }

      Array.Sort(localSnr);
      Array.Sort(centerMagnitude);
      double snr75 =
        QuantileSorted(
          localSnr,
          0.75);
      double low =
        Math.Max(
          QuantileSorted(
            centerMagnitude,
            0.15),
          1e-12);
      double high =
        Math.Max(
          QuantileSorted(
            centerMagnitude,
            0.85),
          low);
      double keyingDepthDb =
        Math.Clamp(
          20.0 * Math.Log10(
            high / low),
          0,
          60);

      int onFrames = 0;
      int transitions = 0;
      bool? state = null;
      for (int i = 0;
           i < activity.Length;
           i++)
      {
        float p =
          Math.Clamp(
            activity[i],
            0,
            1);
        if (p >= 0.5f)
          onFrames++;

        bool? next =
          p >= 0.65f
            ? true
            : p <= 0.35f
              ? false
              : state;
        if (next.HasValue)
        {
          if (state.HasValue &&
              next.Value != state.Value)
            transitions++;
          state = next;
        }
      }

      double duty =
        activity.Length == 0
          ? 0
          : onFrames /
            (double)activity.Length;

      return new(
        snr75,
        keyingDepthDb,
        duty,
        transitions);
    }

    public IReadOnlyDictionary<int, float[]> EstimateActivities(
      IReadOnlyList<CwSignalTrack> tracks,
      CwCarrierActivityEstimator estimator)
    {
      ArgumentNullException.ThrowIfNull(tracks);
      ArgumentNullException.ThrowIfNull(estimator);
      var result = new Dictionary<int, float[]>();
      foreach (CwSignalTrack track in tracks)
      {
        float[] ridge = GetRidgeMagnitudeSeries(track);
        result[track.Id] = estimator.EstimateFrameProbabilities(
          ridge, FrameIntervalSeconds);
      }
      return result;
    }

    /// <summary>
    /// Builds one model-faithful DeepCW tensor from the original wideband
    /// spectrogram. Track activity probabilities gate each component in the
    /// competing soft mask. allTracks is deliberately independent from the
    /// subset selected for ONNX inference.
    /// </summary>
    public DeepCwTensor BuildLaneTensor(
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
          "Target must be present in the interference set.",
          nameof(allTracks));
      if (!activityByTrack.TryGetValue(
            target.Id, out float[]? targetActivity) ||
          targetActivity.Length != FrameCount)
        throw new ArgumentException(
          "Target activity timeline is missing or misaligned.",
          nameof(activityByTrack));

      double modelBinHz =
        metadata.SampleRate / (double)metadata.FftLength;
      double halfSupport = supportBandwidthHz / 2.0;
      if (supportBandwidthHz < 40 ||
          halfSupport > (metadata.MaxFrequencyHz -
                         metadata.MinFrequencyHz) / 2.0)
        throw new ArgumentOutOfRangeException(nameof(supportBandwidthHz));
      if (targetCenterHz - halfSupport < metadata.MinFrequencyHz ||
          targetCenterHz + halfSupport > metadata.MaxFrequencyHz)
        throw new ArgumentOutOfRangeException(nameof(targetCenterHz));
      if (maskFloor <= 0 || maskFloor > 1)
        throw new ArgumentOutOfRangeException(nameof(maskFloor));
      if (maskSigmaHz < 4 || maskSigmaHz > halfSupport)
        throw new ArgumentOutOfRangeException(nameof(maskSigmaHz));

      float[] data =
        new float[checked(FrameCount * metadata.FrequencyBins)];
      double targetSigma = Math.Clamp(
        Math.Sqrt(maskSigmaHz * maskSigmaHz +
          4 * target.FrequencySigmaHz * target.FrequencySigmaHz),
        6, halfSupport);

      for (int frame = 0; frame < FrameCount; frame++)
      {
        double relativeToEnd =
          FrameRelativeToEndSeconds(frame);
        double targetRidge = target.FrequencyHz +
          target.DriftHzPerSecond * relativeToEnd;
        double pTarget = Math.Clamp(
          targetActivity[frame], 0, 1);
        int dst = frame * metadata.FrequencyBins;

        for (int b = 0; b < metadata.FrequencyBins; b++)
        {
          double outputHz =
            metadata.MinFrequencyHz + b * modelBinHz;
          double modelOffset = outputHz - targetCenterHz;
          if (Math.Abs(modelOffset) > halfSupport)
          {
            data[dst + b] = 0;
            continue;
          }

          double sourceHz = targetRidge + modelOffset;
          float magnitude =
            SampleCalibratedMagnitude(frame, sourceHz);

          double numerator = pTarget *
            GaussianWeight(modelOffset, targetSigma);
          double denominator = maskFloor;

          foreach (CwSignalTrack competitor in allTracks)
          {
            if (!competitor.Confirmed) continue;
            if (!activityByTrack.TryGetValue(
                  competitor.Id,
                  out float[]? competitorActivity) ||
                competitorActivity.Length != FrameCount)
              continue;

            double competitorRidge =
              competitor.FrequencyHz +
              competitor.DriftHzPerSecond * relativeToEnd;
            double competitorSigma = Math.Clamp(
              Math.Sqrt(maskSigmaHz * maskSigmaHz +
                4 * competitor.FrequencySigmaHz *
                competitor.FrequencySigmaHz),
              6, halfSupport);
            double pOn = Math.Clamp(
              competitorActivity[frame], 0, 1);
            denominator += pOn * GaussianWeight(
              sourceHz - competitorRidge,
              competitorSigma);
          }

          double mask = numerator /
            Math.Max(denominator, 1e-12);
          double taper = 0.5 *
            (1 + Math.Cos(
              Math.PI * Math.Abs(modelOffset) / halfSupport));
          data[dst + b] = MathF.Log(
            1 + magnitude * (float)(mask * taper));
        }
      }

      return new(
        data,
        [1, 1, FrameCount, metadata.FrequencyBins]);
    }

    public DeepCwTensor BuildUnmaskedLaneTensor(
      CwSignalTrack target,
      double targetCenterHz = 800)
    {
      double modelBinHz =
        metadata.SampleRate / (double)metadata.FftLength;
      float[] data =
        new float[checked(FrameCount * metadata.FrequencyBins)];

      for (int frame = 0; frame < FrameCount; frame++)
      {
        double relativeToEnd =
          FrameRelativeToEndSeconds(frame);
        double targetRidge = target.FrequencyHz +
          target.DriftHzPerSecond * relativeToEnd;
        int dst = frame * metadata.FrequencyBins;

        for (int b = 0; b < metadata.FrequencyBins; b++)
        {
          double outputHz =
            metadata.MinFrequencyHz + b * modelBinHz;
          double sourceHz =
            targetRidge + (outputHz - targetCenterHz);
          float magnitude =
            SampleCalibratedMagnitude(frame, sourceHz);
          data[dst + b] = MathF.Log(1 + magnitude);
        }
      }

      return new(
        data,
        [1, 1, FrameCount, metadata.FrequencyBins]);
    }

    private static double QuantileSorted(
      IReadOnlyList<double> values,
      double quantile)
    {
      if (values.Count == 0)
        return 0;
      double position =
        Math.Clamp(
          quantile,
          0,
          1) *
        (values.Count - 1);
      int lower =
        (int)Math.Floor(position);
      int upper =
        Math.Min(
          values.Count - 1,
          lower + 1);
      double fraction =
        position - lower;
      return
        values[lower] * (1 - fraction) +
        values[upper] * fraction;
    }

    private double FrameRelativeToEndSeconds(int frame) =>
      frame * HopSize / (double)SourceSampleRate -
      DurationSeconds;

    private static double GaussianWeight(
      double offsetHz,
      double sigmaHz)
    {
      double x = offsetHz / sigmaHz;
      if (Math.Abs(x) > 6) return 0;
      return Math.Exp(-0.5 * x * x);
    }

    private static int ReflectIndex(
      int index,
      int length)
    {
      if (length <= 1) return 0;
      while (index < 0 || index >= length)
      {
        if (index < 0) index = -index;
        if (index >= length)
          index = 2 * length - 2 - index;
      }
      return index;
    }
  }
}
