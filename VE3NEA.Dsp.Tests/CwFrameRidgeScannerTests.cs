using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwFrameRidgeScannerTests
  {
    private const int SampleRate = 48000;
    private static readonly DateTime T0 =
      new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Scanner_UsesFastFramesOnlyAsNonKalmanEvidence()
    {
      float[] audio = MakeKeyedTone(
        seconds: 1.8,
        endFrequencyHz: 820,
        driftHzPerSecond: 0,
        amplitude: 0.32f,
        keyPeriodSeconds: 0.12,
        duty: 0.58);

      long startSample = 1_000_000;
      var snapshot = new CwAudioSnapshot(
        SampleRate,
        T0,
        startSample + audio.Length,
        audio);
      CwFrameRidgeScanner scanner =
        NewScanner(500, 1200);

      CwFrameRidgeScanResult result =
        scanner.Scan(snapshot);

      result.FastObservations.Should().NotBeEmpty();
      result.FastObservations
        .Should().OnlyContain(x =>
          x.Scale == CwRidgeObservationScale.Fast &&
          !x.KalmanEligible);

      result.PrecisionBatches.Should().NotBeEmpty();
      CwRidgeObservation[] precision =
        result.PrecisionBatches
          .SelectMany(x => x.Observations)
          .ToArray();
      precision.Should().NotBeEmpty();
      precision.Should().OnlyContain(x =>
        x.Scale == CwRidgeObservationScale.Precision &&
        x.KalmanEligible);

      long min = startSample;
      long max = startSample + audio.Length;
      result.FastObservations
        .Should().OnlyContain(x =>
          x.CenterSampleIndex >= min &&
          x.CenterSampleIndex <= max);
      precision.Should().OnlyContain(x =>
        x.CenterSampleIndex >= min &&
        x.CenterSampleIndex <= max);

      long[] centers = result.PrecisionBatches
        .Select(x => x.CenterSampleIndex)
        .ToArray();
      centers.Should().BeInAscendingOrder();
      centers.Zip(
          centers.Skip(1),
          (a, b) => b - a)
        .Should().OnlyContain(
          delta =>
            delta >= scanner.PrecisionHopSamples);

      double minimumInflatedSigma =
        precision[0].ResolutionHz *
        0.15 *
        Math.Sqrt(
          scanner.PrecisionWindowSamples /
          (double)scanner.PrecisionHopSamples);
      precision.Should().OnlyContain(x =>
        x.MeasurementSigmaHz >=
        minimumInflatedSigma * 0.95);
    }

    [Fact]
    public void PrecisionScanner_ResolvesFifteenHertzDoublet()
    {
      const double seconds = 1.6;
      int count =
        (int)Math.Round(seconds * SampleRate);
      float[] audio = MakeNoise(count, 0.0015f);

      for (int n = 0; n < count; n++)
      {
        double t = n / (double)SampleRate;
        audio[n] +=
          0.30f *
          (float)Math.Sin(
            2 * Math.PI * 800 * t);
        audio[n] +=
          0.26f *
          (float)Math.Sin(
            2 * Math.PI * 815 * t + 0.3);
      }

      var snapshot = new CwAudioSnapshot(
        SampleRate,
        T0,
        count,
        audio);
      CwFrameRidgeScanner scanner =
        NewScanner(650, 950);

      CwFrameRidgeScanResult result =
        scanner.Scan(snapshot);

      result.PrecisionBatches.Should().Contain(batch =>
        batch.Observations.Any(x =>
          Math.Abs(x.FrequencyHz - 800) < 5) &&
        batch.Observations.Any(x =>
          Math.Abs(x.FrequencyHz - 815) < 5));

      result.PrecisionBatches
        .SelectMany(x => x.Observations)
        .Where(x =>
          x.FrequencyHz > 790 &&
          x.FrequencyHz < 825)
        .Should().Contain(x =>
          x.ResolutionHz < 5);
    }

    [Fact]
    public void KnownDopplerRate_ReducesLongWindowMeasurementUncertainty()
    {
      float[] audio = MakeKeyedTone(
        seconds: 2.0,
        endFrequencyHz: 900,
        driftHzPerSecond: 20,
        amplitude: 0.34f,
        keyPeriodSeconds: 0.10,
        duty: 0.70);

      var snapshot = new CwAudioSnapshot(
        SampleRate,
        T0,
        audio.Length,
        audio);
      CwFrameRidgeScanner scanner =
        NewScanner(750, 1000);

      CwFrameRidgeScanResult uncompensated =
        scanner.Scan(snapshot, 0);
      CwFrameRidgeScanResult compensated =
        scanner.Scan(snapshot, 20);

      double sigmaWithout =
        uncompensated.PrecisionBatches
          .SelectMany(x => x.Observations)
          .Where(x =>
            x.FrequencyHz > 850 &&
            x.FrequencyHz < 920)
          .Average(x => x.MeasurementSigmaHz);
      double sigmaWith =
        compensated.PrecisionBatches
          .SelectMany(x => x.Observations)
          .Where(x =>
            x.FrequencyHz > 850 &&
            x.FrequencyHz < 920)
          .Average(x => x.MeasurementSigmaHz);

      sigmaWith.Should().BeLessThan(
        sigmaWithout * 0.90);
    }

    [Fact]
    public void Scanner_NoiseOnly_DoesNotCreateKalmanMeasurements()
    {
      const double seconds = 2.0;
      int count =
        (int)Math.Round(seconds * SampleRate);
      float[] audio = MakeNoise(count, 0.002f);

      var snapshot = new CwAudioSnapshot(
        SampleRate,
        T0,
        count,
        audio);
      CwFrameRidgeScanner scanner =
        NewScanner(500, 1400);

      CwFrameRidgeScanResult result =
        scanner.Scan(snapshot);

      result.PrecisionBatches
        .SelectMany(x => x.Observations)
        .Should().BeEmpty();
      result.PrecisionBatches
        .Should().Contain(x =>
          x.Observations.Count == 0);
    }

    [Fact]
    public void ConservativeProfile_DoesNotSplitOneKeyedCarrierIntoSidebands()
    {
      float[] audio = MakeKeyedTone(
        seconds: 2.0,
        endFrequencyHz: 800,
        driftHzPerSecond: 0,
        amplitude: 0.34f,
        keyPeriodSeconds: 0.14,
        duty: 0.50);

      var snapshot = new CwAudioSnapshot(
        SampleRate,
        T0,
        audio.Length,
        audio);

      var scanner = new CwFrameRidgeScanner(
        new CwFrameRidgeScannerOptions
        {
          SampleRate = SampleRate,
          MinFrequencyHz = 600,
          MaxFrequencyHz = 1000,
          FastMinimumSnrDb = 6,
          PrecisionMinimumSnrDb = 6,
          PeakDeduplicationHz = 30,
          MaxPeaksPerFrame = 8,
          MinimumPortionMeanSnrDb = 7,
          MinimumPortionContinuity = 0.60,
          MinimumPortionActivityProbability = 0.58
        });

      CwFrameRidgeScanResult result =
        scanner.Scan(snapshot);

      CwRidgeObservation[] observations =
        result.PrecisionBatches
          .SelectMany(x => x.Observations)
          .ToArray();
      observations.Should().NotBeEmpty();
      observations.Should().OnlyContain(x =>
        Math.Abs(x.FrequencyHz - 800) < 30);
      result.PrecisionBatches.Should().OnlyContain(
        batch => batch.Observations.Count <= 1);
    }

    [Fact]
    public void Scanner_EmitsEmptyPrecisionBatchesThroughSilence()
    {
      const double seconds = 1.8;
      int count =
        (int)Math.Round(seconds * SampleRate);
      float[] audio = MakeNoise(count, 0.0015f);

      // Keyed CW exists only in the first 600 ms; the remaining window is
      // deliberate silence/noise. Precision time must continue advancing.
      int keyedSamples =
        (int)Math.Round(0.60 * SampleRate);
      double phase = 0;
      for (int n = 0; n < keyedSamples; n++)
      {
        phase += 2 * Math.PI * 830 / SampleRate;
        double t = n / (double)SampleRate;
        if ((t % 0.11) < 0.065)
          audio[n] +=
            0.30f * (float)Math.Sin(phase);
      }

      var snapshot = new CwAudioSnapshot(
        SampleRate,
        T0,
        count,
        audio);
      CwFrameRidgeScanner scanner =
        NewScanner(700, 1000);

      CwFrameRidgeScanResult result =
        scanner.Scan(snapshot);

      result.PrecisionBatches.Should().NotBeEmpty();
      result.PrecisionBatches.Should().Contain(x =>
        x.CenterSampleIndex >
          (long)(1.0 * SampleRate) &&
        x.Observations.Count == 0);
    }

    [Fact]
    public void FrontEnd_DoesNotReplayPrecisionFramesFromOverlappingSnapshot()
    {
      CwDetectorOptions detectorOptions = new()
      {
        SampleRate = SampleRate,
        FftSize = 4096,
        HopSize = 1024,
        MinFrequencyHz = 500,
        MaxFrequencyHz = 1200,
        MinimumSnrDb = 6,
        MinimumKeyingDepthDb = 2,
        MinActiveFraction = 0.05,
        MaxActiveFraction = 0.98,
        MinimumActivityTransitions = 1,
        PeakDeduplicationHz = 20,
        MaxCandidates = 8
      };

      var frontEnd = new CwPileupFrontEnd(
        SampleRate,
        1.5,
        detectorOptions,
        new CwPileupTrackManager(
          confirmationDelay:
            TimeSpan.FromMilliseconds(240)));

      float[] audio = MakeKeyedTone(
        seconds: 1.5,
        endFrequencyHz: 780,
        driftHzPerSecond: 0,
        amplitude: 0.30f,
        keyPeriodSeconds: 0.11,
        duty: 0.62);
      frontEnd.AddSamples(
        audio, audio.Length, T0);

      IReadOnlyList<CwSignalTrack> first =
        frontEnd.Analyze();
      IReadOnlyList<CwSignalTrack> replay =
        frontEnd.Analyze();

      first.Should().NotBeEmpty();
      replay.Select(x => x.Id)
        .Should().Equal(first.Select(x => x.Id));
      replay.Select(x => x.FrequencyHz)
        .Should().Equal(
          first.Select(x => x.FrequencyHz));
    }

    private static CwFrameRidgeScanner NewScanner(
      double minHz,
      double maxHz) =>
      new(new CwFrameRidgeScannerOptions
      {
        SampleRate = SampleRate,
        MinFrequencyHz = minHz,
        MaxFrequencyHz = maxHz,
        FastMinimumSnrDb = 4,
        PrecisionMinimumSnrDb = 4,
        PeakDeduplicationHz = 2.5,
        MaxPeaksPerFrame = 12,
        MinimumPortionFrames = 3,
        MaximumPortionGapFrames = 1,
        MaxRidgeSlopeHzPerSecond = 80,
        PrecisionAssociationGateHz = 28
      });

    private static float[] MakeKeyedTone(
      double seconds,
      double endFrequencyHz,
      double driftHzPerSecond,
      float amplitude,
      double keyPeriodSeconds,
      double duty)
    {
      int count =
        (int)Math.Round(seconds * SampleRate);
      float[] data = MakeNoise(count, 0.0015f);
      double phase = 0;

      for (int n = 0; n < count; n++)
      {
        double t = n / (double)SampleRate;
        double relativeToEnd =
          (n - (count - 1)) /
          (double)SampleRate;
        double frequency =
          endFrequencyHz +
          driftHzPerSecond *
          relativeToEnd;
        phase += 2 * Math.PI *
          frequency / SampleRate;

        bool keyDown =
          (t % keyPeriodSeconds) <
          keyPeriodSeconds * duty;
        if (keyDown)
          data[n] +=
            amplitude *
            (float)Math.Sin(phase);
      }

      return data;
    }

    private static float[] MakeNoise(
      int count,
      float amplitude)
    {
      var random = new Random(74291);
      var data = new float[count];
      for (int i = 0; i < count; i++)
        data[i] =
          amplitude *
          (float)(2 * random.NextDouble() - 1);
      return data;
    }
  }
}
