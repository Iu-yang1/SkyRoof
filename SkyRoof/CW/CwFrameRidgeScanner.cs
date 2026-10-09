using MathNet.Numerics.IntegralTransforms;
using System.Numerics;

namespace SkyRoof.CW
{
  public enum CwRidgeObservationScale
  {
    Fast,
    Precision
  }

  /// <summary>
  /// One time-frequency ridge observation on the monotonic PCM sample axis.
  /// Fast observations are activity/continuity evidence only. Precision
  /// observations are the only measurements intended for the Kalman tracker.
  /// </summary>
  public readonly record struct CwRidgeObservation(
    long CenterSampleIndex,
    double FrequencyHz,
    double SnrDb,
    double MeasurementSigmaHz,
    double ResolutionHz,
    double ActivityProbability,
    CwRidgeObservationScale Scale,
    int RidgePortionId,
    bool KalmanEligible)
  {
    public CwSignalCandidate ToCandidate() =>
      new(
        FrequencyHz,
        SnrDb,
        MeasurementSigmaHz,
        ResolutionHz,
        ActivityProbability);
  }

  /// <summary>
  /// Short mutually-consistent ridge segment. This intentionally follows the
  /// RRP idea of building reliable local portions before long-term tracking,
  /// without porting the reference MATLAB basin/spline machinery.
  /// </summary>
  public readonly record struct CwRidgePortion(
    int Id,
    long FirstSampleIndex,
    long LastSampleIndex,
    double FirstFrequencyHz,
    double LastFrequencyHz,
    double SlopeHzPerSecond,
    double MeanSnrDb,
    double ActivityProbability,
    int FrameCount);

  public readonly record struct CwRidgeObservationBatch(
    long CenterSampleIndex,
    IReadOnlyList<CwRidgeObservation> Observations);

  public sealed class CwFrameRidgeScanResult
  {
    public IReadOnlyList<CwRidgePortion> RidgePortions { get; }
    public IReadOnlyList<CwRidgeObservation> FastObservations { get; }
    public IReadOnlyList<CwRidgeObservationBatch> PrecisionBatches { get; }

    public CwFrameRidgeScanResult(
      IReadOnlyList<CwRidgePortion> ridgePortions,
      IReadOnlyList<CwRidgeObservation> fastObservations,
      IReadOnlyList<CwRidgeObservationBatch> precisionBatches)
    {
      RidgePortions = ridgePortions;
      FastObservations = fastObservations;
      PrecisionBatches = precisionBatches;
    }
  }

  public sealed class CwFrameRidgeScannerOptions
  {
    public int SampleRate { get; init; } = SdrConst.AUDIO_SAMPLING_RATE;

    // Fast path mirrors the DeepCW physical frontend: 80 ms / 15 ms.
    public double FastWindowSeconds { get; init; } = 0.080;
    public double FastHopSeconds { get; init; } = 0.015;

    // Precision observations intentionally update much more slowly. The
    // default 240 ms / 120 ms pair gives ~4.17 Hz physical bins at 48 kHz
    // while limiting overlap to 50%.
    public double PrecisionWindowSeconds { get; init; } = 0.240;
    public double PrecisionHopSeconds { get; init; } = 0.120;

    public double MinFrequencyHz { get; init; } = 300;
    public double MaxFrequencyHz { get; init; } = 3000;
    public double FastMinimumSnrDb { get; init; } = 3.0;
    public double PrecisionMinimumSnrDb { get; init; } = 3.0;
    public double PeakDeduplicationHz { get; init; } = 3.0;
    public int MaxPeaksPerFrame { get; init; } = 16;

    public int MinimumPortionFrames { get; init; } = 4;
    public int MaximumPortionGapFrames { get; init; } = 1;
    public double MinimumRidgeSeedSnrDb { get; init; } = 12.0;
    public double MinimumPortionMeanSnrDb { get; init; } = 7.0;
    public double MinimumPortionContinuity { get; init; } = 0.60;
    public double MinimumPortionActivityProbability { get; init; } = 0.58;
    public double MaximumPortionFitResidualBins { get; init; } = 0.45;

    // Keyed CW creates a comb of weaker spectral maxima sharing almost the
    // same on/off envelope. Correlate short-portion power histories instead
    // of blindly widening the Hz de-duplication gate; independent close
    // stations are then still allowed.
    public double SidebandCorrelationMaxHz { get; init; } = 160;
    public double SidebandCorrelationThreshold { get; init; } = 0.90;
    public double SidebandDominanceDb { get; init; } = 2.5;

    public double MaxRidgeSlopeHzPerSecond { get; init; } = 120;
    public double PrecisionAssociationGateHz { get; init; } = 28;
  }

  /// <summary>
  /// Dual-resolution frame-level CW ridge scanner.
  ///
  /// The 80 ms fast STFT is deliberately NOT sent to the Kalman tracker every
  /// 15 ms; those highly-overlapped frames only establish activity and short
  /// relevant ridge portions. A 240 ms precision STFT, normally every 120 ms,
  /// produces the frequency measurements. Its reported sigma is inflated for
  /// overlap and for residual chirp across the long window.
  ///
  /// A known common Doppler rate may be de-chirped in the STFT itself. Returned
  /// frequencies are always mapped back to the original AF frequency axis.
  /// </summary>
  public sealed class CwFrameRidgeScanner
  {
    private readonly CwFrameRidgeScannerOptions options;
    private readonly int fastWindow;
    private readonly int fastHop;
    private readonly int precisionWindow;
    private readonly int precisionHop;

    private sealed class MutablePortion
    {
      public int Id;
      public readonly List<FramePeak> Peaks = new();
      public int LastFrameIndex;
      public int MissedFrames;
    }

    private readonly record struct FramePeak(
      int FrameIndex,
      long CenterSampleIndex,
      double FrequencyHz,
      double SnrDb,
      double MeasurementSigmaHz,
      double ResolutionHz,
      double ActivityProbability,
      double Power);

    private readonly record struct PortionReference(
      CwRidgePortion Portion,
      double PredictedFrequencyHz);

    public CwFrameRidgeScanner(
      CwFrameRidgeScannerOptions? options = null)
    {
      this.options = options ?? new CwFrameRidgeScannerOptions();
      ValidateOptions(this.options);

      fastWindow = ToSamples(
        this.options.FastWindowSeconds,
        this.options.SampleRate);
      fastHop = ToSamples(
        this.options.FastHopSeconds,
        this.options.SampleRate);
      precisionWindow = ToSamples(
        this.options.PrecisionWindowSeconds,
        this.options.SampleRate);
      precisionHop = ToSamples(
        this.options.PrecisionHopSeconds,
        this.options.SampleRate);

      if (fastHop > fastWindow ||
          precisionHop > precisionWindow)
        throw new ArgumentOutOfRangeException(
          nameof(options),
          "CW ridge STFT hop must not exceed its window.");
    }

    public CwFrameRidgeScannerOptions Options => options;
    public int FastWindowSamples => fastWindow;
    public int FastHopSamples => fastHop;
    public int PrecisionWindowSamples => precisionWindow;
    public int PrecisionHopSamples => precisionHop;

    public CwFrameRidgeScanResult Scan(
      CwAudioSnapshot snapshot,
      double knownDopplerRateHzPerSecond = 0)
    {
      if (snapshot.SampleRate != options.SampleRate)
        throw new ArgumentException(
          "CW ridge scanner and snapshot sample rates must match.",
          nameof(snapshot));
      if (snapshot.Samples == null)
        throw new ArgumentException(
          "CW ridge snapshot samples are missing.",
          nameof(snapshot));
      if (!double.IsFinite(knownDopplerRateHzPerSecond) ||
          Math.Abs(knownDopplerRateHzPerSecond) > 500)
        throw new ArgumentOutOfRangeException(
          nameof(knownDopplerRateHzPerSecond));

      if (snapshot.Samples.Length < precisionWindow)
      {
        return new(
          Array.Empty<CwRidgePortion>(),
          Array.Empty<CwRidgeObservation>(),
          Array.Empty<CwRidgeObservationBatch>());
      }

      List<List<FramePeak>> fastFrames = ComputePeakFrames(
        snapshot,
        fastWindow,
        fastHop,
        options.FastMinimumSnrDb,
        CwRidgeObservationScale.Fast,
        knownDopplerRateHzPerSecond);

      (List<CwRidgePortion> portions,
       Dictionary<(int Frame, long Sample, long FrequencyMilliHz), int>
         fastPortionIds) =
        BuildRidgePortions(fastFrames);

      var fastObservations = new List<CwRidgeObservation>();
      foreach (List<FramePeak> frame in fastFrames)
      {
        foreach (FramePeak peak in frame)
        {
          var key = PeakKey(peak);
          if (!fastPortionIds.TryGetValue(key, out int portionId))
            continue;

          CwRidgePortion portion =
            portions.First(p => p.Id == portionId);
          fastObservations.Add(new(
            peak.CenterSampleIndex,
            peak.FrequencyHz,
            peak.SnrDb,
            peak.MeasurementSigmaHz,
            peak.ResolutionHz,
            portion.ActivityProbability,
            CwRidgeObservationScale.Fast,
            portionId,
            KalmanEligible: false));
        }
      }

      List<List<FramePeak>> precisionFrames = ComputePeakFrames(
        snapshot,
        precisionWindow,
        precisionHop,
        options.PrecisionMinimumSnrDb,
        CwRidgeObservationScale.Precision,
        knownDopplerRateHzPerSecond);

      long snapshotStartSample =
        snapshot.EndSampleIndex - snapshot.Samples.Length;
      long firstPrecisionCenter =
        snapshotStartSample + precisionWindow / 2;

      List<CwRidgeObservationBatch> precision =
        BuildPrecisionObservations(
          portions,
          precisionFrames,
          firstPrecisionCenter,
          knownDopplerRateHzPerSecond);

      return new(
        portions,
        fastObservations
          .OrderBy(x => x.CenterSampleIndex)
          .ThenBy(x => x.FrequencyHz)
          .ToArray(),
        precision);
    }

    private List<List<FramePeak>> ComputePeakFrames(
      CwAudioSnapshot snapshot,
      int windowSamples,
      int hopSamples,
      double minimumSnrDb,
      CwRidgeObservationScale scale,
      double knownDopplerRateHzPerSecond)
    {
      int frameCount = 1 +
        (snapshot.Samples.Length - windowSamples) / hopSamples;
      var frames = new List<List<FramePeak>>(frameCount);
      double[] window = Enumerable.Range(0, windowSamples)
        .Select(i => 0.5 - 0.5 *
          Math.Cos(2 * Math.PI * i / windowSamples))
        .ToArray();
      var fft = new Complex[windowSamples];

      double binHz =
        options.SampleRate / (double)windowSamples;
      int firstBin = Math.Max(
        1,
        (int)Math.Ceiling(options.MinFrequencyHz / binHz));
      int lastBin = Math.Min(
        windowSamples / 2 - 1,
        (int)Math.Floor(options.MaxFrequencyHz / binHz));
      int binCount = Math.Max(0, lastBin - firstBin + 1);
      if (binCount < 3)
        return frames;

      double overlapInflation = scale ==
        CwRidgeObservationScale.Precision
        ? Math.Sqrt(Math.Max(
            1.0,
            windowSamples / (double)hopSamples))
        : 1.0;

      long snapshotStartSample =
        snapshot.EndSampleIndex - snapshot.Samples.Length;
      var powers = new double[binCount];
      var noiseScratch = new double[binCount];

      for (int frameIndex = 0;
           frameIndex < frameCount;
           frameIndex++)
      {
        int offset = frameIndex * hopSamples;
        long centerSample =
          snapshotStartSample +
          offset +
          windowSamples / 2;
        double centerTimeFromEnd =
          (centerSample - snapshot.EndSampleIndex) /
          (double)options.SampleRate;

        for (int i = 0; i < windowSamples; i++)
        {
          long globalSample =
            snapshotStartSample + offset + i;
          double t =
            (globalSample - snapshot.EndSampleIndex) /
            (double)options.SampleRate;
          double dechirpPhase =
            Math.PI *
            knownDopplerRateHzPerSecond *
            t * t;
          double value =
            snapshot.Samples[offset + i] * window[i];
          fft[i] = new Complex(
            value * Math.Cos(-dechirpPhase),
            value * Math.Sin(-dechirpPhase));
        }

        Fourier.Forward(fft, FourierOptions.Matlab);

        for (int b = 0; b < binCount; b++)
        {
          Complex value = fft[firstBin + b];
          double power =
            value.Real * value.Real +
            value.Imaginary * value.Imaginary;
          powers[b] = power;
          noiseScratch[b] = power;
        }

        Array.Sort(noiseScratch);
        double noisePower = Math.Max(
          MedianSorted(noiseScratch), 1e-20);

        var rawPeaks = new List<FramePeak>();
        for (int b = 1; b < binCount - 1; b++)
        {
          double power = powers[b];
          if (power <= powers[b - 1] ||
              power < powers[b + 1])
            continue;

          double snrDb = 10 * Math.Log10(
            Math.Max(power, 1e-20) / noisePower);
          if (snrDb < minimumSnrDb)
            continue;

          double y0 = Math.Log(
            Math.Max(powers[b - 1], 1e-30));
          double y1 = Math.Log(
            Math.Max(power, 1e-30));
          double y2 = Math.Log(
            Math.Max(powers[b + 1], 1e-30));
          double denominator = y0 - 2 * y1 + y2;
          double delta = Math.Abs(denominator) < 1e-12
            ? 0
            : 0.5 * (y0 - y2) / denominator;
          delta = Math.Clamp(delta, -0.5, 0.5);

          double correctedFrequency =
            (firstBin + b + delta) * binHz;
          double rawFrequency =
            correctedFrequency +
            knownDopplerRateHzPerSecond *
            centerTimeFromEnd;

          double snrLinear = Math.Pow(
            10, snrDb / 10.0);
          double baseSigma = Math.Max(
            0.15 * binHz,
            binHz / Math.Sqrt(
              Math.Max(snrLinear, 1.0)));
          double sigma =
            baseSigma * overlapInflation;
          double activity = 1.0 /
            (1.0 + Math.Exp(
              -(snrDb - minimumSnrDb) / 2.0));

          rawPeaks.Add(new(
            frameIndex,
            centerSample,
            rawFrequency,
            snrDb,
            sigma,
            binHz,
            activity,
            power));
        }

        // A conservative profile (e.g. legacy 20–30 Hz
        // de-duplication) explicitly says that nearby spectral structure is
        // not intended to become separate carriers. In that mode broaden the
        // rejection radius to suppress periodic Morse keying side-lobe
        // families. High-resolution profiles (2–4 Hz) keep the requested
        // narrow radius so a real 15 Hz doublet remains separable.
        double resolutionGuard = 2.0 * binHz;
        bool conservativeProfile =
          options.PeakDeduplicationHz >= resolutionGuard;
        double suppressionHz = conservativeProfile
          ? 2.0 * options.PeakDeduplicationHz
          : Math.Max(
              options.PeakDeduplicationHz,
              0.55 * binHz);

        var selected = new List<FramePeak>();
        foreach (FramePeak peak in rawPeaks
          .OrderByDescending(x => x.SnrDb)
          .ThenBy(x => x.FrequencyHz))
        {
          if (selected.Any(x =>
            Math.Abs(
              x.FrequencyHz - peak.FrequencyHz) <
            suppressionHz))
            continue;

          selected.Add(peak);
          if (selected.Count >= options.MaxPeaksPerFrame)
            break;
        }

        frames.Add(
          selected
            .OrderBy(x => x.FrequencyHz)
            .ToList());
      }

      return frames;
    }

    private (
      List<CwRidgePortion> Portions,
      Dictionary<(int Frame, long Sample, long FrequencyMilliHz), int>
        PeakPortionIds)
      BuildRidgePortions(
        IReadOnlyList<List<FramePeak>> frames)
    {
      var active = new List<MutablePortion>();
      var all = new List<MutablePortion>();
      int nextPortionId = 1;

      for (int frameIndex = 0;
           frameIndex < frames.Count;
           frameIndex++)
      {
        List<FramePeak> peaks = frames[frameIndex];

        foreach (MutablePortion portion in active)
        {
          if (portion.LastFrameIndex < frameIndex)
            portion.MissedFrames++;
        }

        MutablePortion[] candidates = active
          .Where(p =>
            p.MissedFrames <=
            options.MaximumPortionGapFrames + 1)
          .ToArray();

        var portionBest =
          new Dictionary<MutablePortion, int>();
        var peakBest =
          new Dictionary<int, MutablePortion>();

        foreach (MutablePortion portion in candidates)
        {
          FramePeak last = portion.Peaks[^1];
          double dt = Math.Max(
            1.0 / options.SampleRate,
            (peaks.Count > 0
              ? peaks[0].CenterSampleIndex
              : last.CenterSampleIndex +
                FastHopSamples) -
            last.CenterSampleIndex) /
            options.SampleRate;

          double slope = EstimateRecentSlope(portion);
          double predicted =
            last.FrequencyHz + slope * dt;
          // The previous 1.5-bin gate allowed a noise maximum
          // to random-walk by almost 19 Hz every 15 ms. Gate on the actual
          // measurement uncertainty plus physically allowed ridge motion.
          double uncertaintyGate = Math.Clamp(
            3.0 * last.MeasurementSigmaHz,
            0.30 * last.ResolutionHz,
            0.65 * last.ResolutionHz);
          double gate =
            uncertaintyGate +
            options.MaxRidgeSlopeHzPerSecond * dt;

          int bestIndex = -1;
          double bestDistance = double.PositiveInfinity;
          for (int i = 0; i < peaks.Count; i++)
          {
            double distance =
              Math.Abs(peaks[i].FrequencyHz - predicted);
            if (distance > gate ||
                distance >= bestDistance)
              continue;
            bestDistance = distance;
            bestIndex = i;
          }

          if (bestIndex >= 0)
            portionBest[portion] = bestIndex;
        }

        for (int peakIndex = 0;
             peakIndex < peaks.Count;
             peakIndex++)
        {
          MutablePortion? best = null;
          double bestDistance = double.PositiveInfinity;
          foreach (MutablePortion portion in candidates)
          {
            if (!portionBest.TryGetValue(
                  portion, out int proposed) ||
                proposed != peakIndex)
              continue;

            FramePeak last = portion.Peaks[^1];
            double distance = Math.Abs(
              peaks[peakIndex].FrequencyHz -
              last.FrequencyHz);
            if (distance < bestDistance)
            {
              bestDistance = distance;
              best = portion;
            }
          }

          if (best != null)
            peakBest[peakIndex] = best;
        }

        var usedPeaks = new HashSet<int>();
        var extended = new HashSet<MutablePortion>();
        foreach (var pair in peakBest)
        {
          int peakIndex = pair.Key;
          MutablePortion portion = pair.Value;
          if (!portionBest.TryGetValue(
                portion, out int bestPeak) ||
              bestPeak != peakIndex)
            continue;

          portion.Peaks.Add(peaks[peakIndex]);
          portion.LastFrameIndex = frameIndex;
          portion.MissedFrames = 0;
          usedPeaks.Add(peakIndex);
          extended.Add(portion);
        }

        for (int i = 0; i < peaks.Count; i++)
        {
          if (usedPeaks.Contains(i)) continue;

          // Hysteretic ridge birth: a low-threshold peak may continue an
          // existing portion, but it may not start a new identity until it
          // crosses the stronger statistical seed threshold.
          if (peaks[i].SnrDb <
              options.MinimumRidgeSeedSnrDb)
            continue;

          var portion = new MutablePortion
          {
            Id = nextPortionId++,
            LastFrameIndex = frameIndex,
            MissedFrames = 0
          };
          portion.Peaks.Add(peaks[i]);
          active.Add(portion);
          all.Add(portion);
        }

        active.RemoveAll(p =>
          frameIndex - p.LastFrameIndex >
          options.MaximumPortionGapFrames + 1);
      }

      var portions = new List<CwRidgePortion>();
      var ids =
        new Dictionary<(int Frame, long Sample, long FrequencyMilliHz), int>();

      foreach (MutablePortion mutable in all)
      {
        if (mutable.Peaks.Count <
            options.MinimumPortionFrames)
          continue;

        FramePeak first = mutable.Peaks[0];
        FramePeak last = mutable.Peaks[^1];
        double dt =
          (last.CenterSampleIndex -
           first.CenterSampleIndex) /
          (double)options.SampleRate;
        double slope = dt <= 0
          ? 0
          : (last.FrequencyHz -
             first.FrequencyHz) / dt;
        slope = Math.Clamp(
          slope,
          -options.MaxRidgeSlopeHzPerSecond,
          options.MaxRidgeSlopeHzPerSecond);

        double meanSnr =
          mutable.Peaks.Average(x => x.SnrDb);
        double meanActivity =
          mutable.Peaks.Average(
            x => x.ActivityProbability);
        double expectedFrames =
          1 +
          (last.CenterSampleIndex -
           first.CenterSampleIndex) /
          (double)Math.Max(1, FastHopSamples);
        double continuity =
          Math.Clamp(
            mutable.Peaks.Count /
            Math.Max(expectedFrames, 1),
            0, 1);
        double activity =
          Math.Clamp(
            meanActivity * (0.6 + 0.4 * continuity),
            0, 1);

        // Per-frame noise maxima are common even several dB above the median
        // periodogram floor. A Kalman-eligible carrier must first survive as
        // a reliable short ridge portion. This is the RRP-inspired
        // false-alarm barrier: continuity, accumulated SNR and activity are
        // evaluated jointly instead of promoting every local maximum.
        double fitResidualHz =
          LinearFitResidualHz(mutable.Peaks);
        double maxResidualHz =
          options.MaximumPortionFitResidualBins *
          first.ResolutionHz;

        if (meanSnr <
              options.MinimumPortionMeanSnrDb ||
            continuity <
              options.MinimumPortionContinuity ||
            activity <
              options.MinimumPortionActivityProbability ||
            fitResidualHz > maxResidualHz)
          continue;

        portions.Add(new(
          mutable.Id,
          first.CenterSampleIndex,
          last.CenterSampleIndex,
          first.FrequencyHz,
          last.FrequencyHz,
          slope,
          meanSnr,
          activity,
          mutable.Peaks.Count));

        foreach (FramePeak peak in mutable.Peaks)
          ids[PeakKey(peak)] = mutable.Id;
      }

      HashSet<int> sidebandIds =
        FindCorrelatedSidebandPortions(
          portions, all);

      if (sidebandIds.Count > 0)
      {
        portions.RemoveAll(p =>
          sidebandIds.Contains(p.Id));

        foreach (var key in ids
          .Where(x =>
            sidebandIds.Contains(x.Value))
          .Select(x => x.Key)
          .ToArray())
          ids.Remove(key);
      }

      return (
        portions
          .OrderBy(x => x.FirstSampleIndex)
          .ThenBy(x => x.FirstFrequencyHz)
          .ToList(),
        ids);
    }

    private double LinearFitResidualHz(
      IReadOnlyList<FramePeak> peaks)
    {
      if (peaks.Count < 2)
        return 0;

      double origin =
        peaks[0].CenterSampleIndex;
      double meanT = peaks.Average(x =>
        (x.CenterSampleIndex - origin) /
        (double)options.SampleRate);
      double meanF =
        peaks.Average(x => x.FrequencyHz);

      double covariance = 0;
      double timeVariance = 0;
      foreach (FramePeak peak in peaks)
      {
        double t =
          (peak.CenterSampleIndex - origin) /
          (double)options.SampleRate;
        double dt = t - meanT;
        covariance +=
          dt * (peak.FrequencyHz - meanF);
        timeVariance += dt * dt;
      }

      double slope = timeVariance <= 1e-15
        ? 0
        : covariance / timeVariance;
      slope = Math.Clamp(
        slope,
        -options.MaxRidgeSlopeHzPerSecond,
        options.MaxRidgeSlopeHzPerSecond);
      double intercept =
        meanF - slope * meanT;

      double squared = 0;
      foreach (FramePeak peak in peaks)
      {
        double t =
          (peak.CenterSampleIndex - origin) /
          (double)options.SampleRate;
        double residual =
          peak.FrequencyHz -
          (intercept + slope * t);
        squared += residual * residual;
      }

      return Math.Sqrt(
        squared / peaks.Count);
    }

    private HashSet<int> FindCorrelatedSidebandPortions(
      IReadOnlyList<CwRidgePortion> portions,
      IReadOnlyList<MutablePortion> mutablePortions)
    {
      var suppressed = new HashSet<int>();
      var mutableById =
        mutablePortions.ToDictionary(x => x.Id);

      foreach (CwRidgePortion weak in portions
        .OrderBy(x => x.MeanSnrDb))
      {
        if (suppressed.Contains(weak.Id))
          continue;

        double weakCenter =
          0.5 * (weak.FirstFrequencyHz +
                 weak.LastFrequencyHz);

        foreach (CwRidgePortion strong in portions
          .Where(x =>
            x.Id != weak.Id &&
            x.MeanSnrDb >=
              weak.MeanSnrDb +
              options.SidebandDominanceDb)
          .OrderByDescending(x => x.MeanSnrDb))
        {
          if (suppressed.Contains(strong.Id))
            continue;

          double strongCenter =
            0.5 * (strong.FirstFrequencyHz +
                   strong.LastFrequencyHz);
          if (Math.Abs(
                strongCenter - weakCenter) >
              options.SidebandCorrelationMaxHz)
            continue;

          if (!mutableById.TryGetValue(
                weak.Id, out MutablePortion? weakMutable) ||
              !mutableById.TryGetValue(
                strong.Id, out MutablePortion? strongMutable))
            continue;

          double correlation =
            CommonFrameLogPowerCorrelation(
              weakMutable, strongMutable);

          if (correlation >=
              options.SidebandCorrelationThreshold)
          {
            suppressed.Add(weak.Id);
            break;
          }
        }
      }

      return suppressed;
    }

    private static double CommonFrameLogPowerCorrelation(
      MutablePortion a,
      MutablePortion b)
    {
      var bByFrame = b.Peaks.ToDictionary(
        x => x.FrameIndex,
        x => Math.Log(Math.Max(x.Power, 1e-30)));
      var pairs =
        new List<(double A, double B)>();

      foreach (FramePeak peak in a.Peaks)
      {
        if (bByFrame.TryGetValue(
              peak.FrameIndex,
              out double other))
        {
          pairs.Add((
            Math.Log(Math.Max(peak.Power, 1e-30)),
            other));
        }
      }

      if (pairs.Count < 5)
        return 0;

      double meanA = pairs.Average(x => x.A);
      double meanB = pairs.Average(x => x.B);
      double covariance = 0;
      double varianceA = 0;
      double varianceB = 0;

      foreach ((double x, double y) in pairs)
      {
        double da = x - meanA;
        double db = y - meanB;
        covariance += da * db;
        varianceA += da * da;
        varianceB += db * db;
      }

      // Nearly constant ridges (including the 15 Hz continuous-doublet unit
      // test) contain no keying-envelope evidence and must never be removed by
      // this heuristic.
      if (varianceA < 1e-4 ||
          varianceB < 1e-4)
        return 0;

      return covariance /
        Math.Sqrt(varianceA * varianceB);
    }

    private List<CwRidgeObservationBatch>
      BuildPrecisionObservations(
        IReadOnlyList<CwRidgePortion> portions,
        IReadOnlyList<List<FramePeak>> precisionFrames,
        long firstPrecisionCenterSample,
        double knownDopplerRateHzPerSecond)
    {
      var result = new List<CwRidgeObservationBatch>();
      long halfPrecisionWindow =
        precisionWindow / 2;
      double precisionBinHz =
        options.SampleRate / (double)precisionWindow;

      for (int frameIndex = 0;
           frameIndex < precisionFrames.Count;
           frameIndex++)
      {
        List<FramePeak> peaks =
          precisionFrames[frameIndex];
        long center =
          firstPrecisionCenterSample +
          (long)frameIndex * precisionHop;

        PortionReference[] references = portions
          .Where(p =>
            p.LastSampleIndex >=
              center - halfPrecisionWindow &&
            p.FirstSampleIndex <=
              center + halfPrecisionWindow)
          .Select(p => new PortionReference(
            p,
            PredictPortionFrequency(p, center)))
          .OrderByDescending(x => x.Portion.MeanSnrDb)
          .ToArray();

        if (references.Length == 0 ||
            peaks.Count == 0)
        {
          // Empty precision frames are still meaningful tracker time. The
          // frontend forwards this empty batch so coast/hold/Active state
          // advances during simultaneous key-up or signal loss.
          result.Add(new(
            center,
            Array.Empty<CwRidgeObservation>()));
          continue;
        }

        // Multiple Morse elements on the same carrier can create several fast
        // RRP-like portions inside one precision window. Collapse those
        // references so they do not manufacture duplicate tracks.
        var uniqueReferences = new List<PortionReference>();
        foreach (PortionReference reference in references)
        {
          if (uniqueReferences.Any(x =>
            Math.Abs(
              x.PredictedFrequencyHz -
              reference.PredictedFrequencyHz) <
            Math.Max(
              options.PeakDeduplicationHz,
              precisionBinHz)))
            continue;
          uniqueReferences.Add(reference);
        }

        var observations = new List<CwRidgeObservation>();
        var usedPeaks = new HashSet<int>();

        // One broad fast portion may contain more than one close component.
        // Therefore precision peaks are matched independently to the nearest
        // reliable portion; multiple distinct precision maxima may legitimately
        // share the same portion ID and seed separate tracks.
        var pairs = new List<(
          int PeakIndex,
          PortionReference Reference,
          double Distance)>();

        for (int i = 0; i < peaks.Count; i++)
        {
          foreach (PortionReference reference in uniqueReferences)
          {
            double distance = Math.Abs(
              peaks[i].FrequencyHz -
              reference.PredictedFrequencyHz);
            double gate = Math.Max(
              options.PrecisionAssociationGateHz,
              2.0 * peaks[i].ResolutionHz);
            if (distance <= gate)
              pairs.Add((i, reference, distance));
          }
        }

        foreach (var pair in pairs
          .OrderBy(x => x.Distance)
          .ThenByDescending(x => peaks[x.PeakIndex].SnrDb))
        {
          if (usedPeaks.Contains(pair.PeakIndex))
            continue;

          FramePeak peak = peaks[pair.PeakIndex];
          CwRidgePortion portion = pair.Reference.Portion;

          double residualSlope =
            portion.SlopeHzPerSecond -
            knownDopplerRateHzPerSecond;
          residualSlope = Math.Clamp(
            residualSlope,
            -options.MaxRidgeSlopeHzPerSecond,
            options.MaxRidgeSlopeHzPerSecond);
          double smearSigma =
            Math.Abs(residualSlope) *
            options.PrecisionWindowSeconds /
            Math.Sqrt(12.0);
          double sigma = Math.Sqrt(
            peak.MeasurementSigmaHz *
            peak.MeasurementSigmaHz +
            smearSigma * smearSigma);
          double activity = Math.Clamp(
            Math.Sqrt(
              Math.Max(0,
                peak.ActivityProbability *
                portion.ActivityProbability)),
            0, 1);

          observations.Add(new(
            peak.CenterSampleIndex,
            peak.FrequencyHz,
            peak.SnrDb,
            sigma,
            peak.ResolutionHz,
            activity,
            CwRidgeObservationScale.Precision,
            portion.Id,
            KalmanEligible: true));
          usedPeaks.Add(pair.PeakIndex);
        }

        result.Add(new(
          center,
          observations
            .OrderBy(x => x.FrequencyHz)
            .ToArray()));
      }

      return result;
    }

    private double EstimateRecentSlope(
      MutablePortion portion)
    {
      if (portion.Peaks.Count < 2)
        return 0;

      FramePeak a =
        portion.Peaks[Math.Max(
          0, portion.Peaks.Count - 3)];
      FramePeak b = portion.Peaks[^1];
      double dt =
        (b.CenterSampleIndex -
         a.CenterSampleIndex) /
        (double)options.SampleRate;
      if (dt <= 0) return 0;

      return Math.Clamp(
        (b.FrequencyHz - a.FrequencyHz) / dt,
        -options.MaxRidgeSlopeHzPerSecond,
        options.MaxRidgeSlopeHzPerSecond);
    }

    private double PredictPortionFrequency(
      CwRidgePortion portion,
      long sampleIndex)
    {
      double dt =
        (sampleIndex - portion.LastSampleIndex) /
        (double)options.SampleRate;
      return portion.LastFrequencyHz +
        portion.SlopeHzPerSecond * dt;
    }

    private static (
      int Frame,
      long Sample,
      long FrequencyMilliHz)
      PeakKey(FramePeak peak) =>
      (
        peak.FrameIndex,
        peak.CenterSampleIndex,
        (long)Math.Round(
          peak.FrequencyHz * 1000));

    private static int ToSamples(
      double seconds,
      int sampleRate) =>
      Math.Max(
        1,
        checked((int)Math.Round(
          seconds * sampleRate)));

    private static double MedianSorted(
      double[] values)
    {
      int middle = values.Length / 2;
      return values.Length % 2 == 0
        ? (values[middle - 1] +
           values[middle]) * 0.5
        : values[middle];
    }

    private static void ValidateOptions(
      CwFrameRidgeScannerOptions value)
    {
      if (value.SampleRate < 1000 ||
          value.SampleRate > 384000)
        throw new ArgumentOutOfRangeException(
          nameof(value.SampleRate));

      foreach (double seconds in new[]
      {
        value.FastWindowSeconds,
        value.FastHopSeconds,
        value.PrecisionWindowSeconds,
        value.PrecisionHopSeconds
      })
      {
        if (!double.IsFinite(seconds) ||
            seconds <= 0 ||
            seconds > 2)
          throw new ArgumentOutOfRangeException(
            nameof(value.FastWindowSeconds));
      }

      if (value.FastWindowSeconds < 0.04 ||
          value.PrecisionWindowSeconds <=
            value.FastWindowSeconds)
        throw new ArgumentOutOfRangeException(
          nameof(value.PrecisionWindowSeconds));
      if (value.PrecisionHopSeconds <
          value.FastHopSeconds)
        throw new ArgumentOutOfRangeException(
          nameof(value.PrecisionHopSeconds));
      if (value.MinFrequencyHz <= 0 ||
          value.MaxFrequencyHz <=
            value.MinFrequencyHz ||
          value.MaxFrequencyHz >=
            value.SampleRate / 2.0)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxFrequencyHz));
      if (value.FastMinimumSnrDb < -10 ||
          value.PrecisionMinimumSnrDb < -10)
        throw new ArgumentOutOfRangeException(
          nameof(value.FastMinimumSnrDb));
      if (!double.IsFinite(
            value.PeakDeduplicationHz) ||
          value.PeakDeduplicationHz <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.PeakDeduplicationHz));
      if (value.MaxPeaksPerFrame is < 1 or > 64)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxPeaksPerFrame));
      if (value.MinimumPortionFrames < 2 ||
          value.MinimumPortionFrames > 32)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumPortionFrames));
      if (value.MaximumPortionGapFrames is < 0 or > 8)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaximumPortionGapFrames));
      if (!double.IsFinite(value.MinimumRidgeSeedSnrDb) ||
          value.MinimumRidgeSeedSnrDb <
            value.FastMinimumSnrDb)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumRidgeSeedSnrDb));
      if (!double.IsFinite(value.MinimumPortionMeanSnrDb) ||
          value.MinimumPortionMeanSnrDb < -10)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumPortionMeanSnrDb));
      if (!double.IsFinite(value.MinimumPortionContinuity) ||
          value.MinimumPortionContinuity <= 0 ||
          value.MinimumPortionContinuity > 1)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumPortionContinuity));
      if (!double.IsFinite(
            value.MinimumPortionActivityProbability) ||
          value.MinimumPortionActivityProbability < 0 ||
          value.MinimumPortionActivityProbability > 1)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumPortionActivityProbability));
      if (!double.IsFinite(
            value.MaximumPortionFitResidualBins) ||
          value.MaximumPortionFitResidualBins <= 0 ||
          value.MaximumPortionFitResidualBins > 2)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaximumPortionFitResidualBins));
      if (!double.IsFinite(value.SidebandCorrelationMaxHz) ||
          value.SidebandCorrelationMaxHz <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.SidebandCorrelationMaxHz));
      if (!double.IsFinite(value.SidebandCorrelationThreshold) ||
          value.SidebandCorrelationThreshold <= 0 ||
          value.SidebandCorrelationThreshold > 1)
        throw new ArgumentOutOfRangeException(
          nameof(value.SidebandCorrelationThreshold));
      if (!double.IsFinite(value.SidebandDominanceDb) ||
          value.SidebandDominanceDb < 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.SidebandDominanceDb));
      if (!double.IsFinite(
            value.MaxRidgeSlopeHzPerSecond) ||
          value.MaxRidgeSlopeHzPerSecond <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxRidgeSlopeHzPerSecond));
      if (!double.IsFinite(
            value.PrecisionAssociationGateHz) ||
          value.PrecisionAssociationGateHz <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.PrecisionAssociationGateHz));
    }
  }
}
