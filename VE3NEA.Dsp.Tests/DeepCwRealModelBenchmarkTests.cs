using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using SkyRoof.CW;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.Dsp.Tests
{
  /// <summary>
  /// Optional real-model benchmark. Normal CI compiles this test but returns
  /// immediately unless SKYROOF_RUN_DEEPCW_BENCHMARK=1. The dedicated manual
  /// workflow supplies the pinned ONNX model and uploads the JSON metrics.
  ///
  /// This suite deliberately uses oracle tracks: it measures separator/model
  /// CER independently from future detector/tracker ID-switch metrics.
  /// </summary>
  public sealed class DeepCwRealModelBenchmarkTests
  {
    private const int SampleRate = 48000;
    private const double DurationSeconds = 8.0;
    private readonly ITestOutputHelper output;

    public DeepCwRealModelBenchmarkTests(
      ITestOutputHelper output) =>
      this.output = output;

    [Fact]
    public void RealDeepCw_PileupSeparatorBaseline()
    {
      if (!string.Equals(
            Environment.GetEnvironmentVariable(
              "SKYROOF_RUN_DEEPCW_BENCHMARK"),
            "1",
            StringComparison.Ordinal))
      {
        output.WriteLine(
          "Real DeepCW benchmark disabled in normal CI.");
        return;
      }

      string modelPath = RequiredEnvironment(
        "DEEPCW_MODEL_PATH");
      string metadataPath = RequiredEnvironment(
        "DEEPCW_METADATA_PATH");
      string? reportPath =
        Environment.GetEnvironmentVariable(
          "DEEPCW_BENCHMARK_OUTPUT");

      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Load(metadataPath);
      using var onnx =
        new DeepCwOnnxDecoder(modelPath, metadata);
      var decoder =
        new DeepCwMultiLaneDecoder(metadata, onnx)
        {
          MaxLanes = 2,
          LaneBandwidthHz = 240,
          TargetCenterHz = 800
        };

      BenchmarkScenario[] scenarios =
      [
        new("static_equal_sep40", 800, 40, 0, 0, -2),
        new("static_equal_sep25", 800, 25, 0, 0, -2),
        new("static_equal_sep15", 800, 15, 0, 0, -2),
        new("static_equal_sep10", 800, 10, 0, 0, -2),
        new("static_equal_sep5", 800, 5, 0, 0, -2),

        new("static_8db_sep40", 800, 40, 8, 0, -2),
        new("static_8db_sep25", 800, 25, 8, 0, -2),
        new("static_8db_sep15", 800, 15, 8, 0, -2),
        new("static_8db_sep10", 800, 10, 8, 0, -2),
        new("static_8db_sep5", 800, 5, 8, 0, -2),

        new("doppler5_sep25_8db", 800, 25, 8, 5, -2),
        new("doppler10_sep25_8db", 800, 25, 8, 10, -2),
        new("doppler20_sep25_8db", 800, 25, 8, 20, -2),

        new("wideband2600_sep25_d10", 2600, 25, 6, 10, -2)
      ];

      var results = new List<BenchmarkResult>();
      foreach (BenchmarkScenario scenario in scenarios)
      {
        const string truthA = "CQ DE K1ABC";
        const string truthB = "CQ DE W9XYZ";

        GeneratedMix mix = GenerateMix(
          scenario,
          truthA,
          truthB,
          wpmA: 24,
          wpmB: 27);

        var tracks = new[]
        {
          MakeTrack(
            1,
            mix.EndFrequencyA,
            14,
            scenario.DriftHzPerSecond),
          MakeTrack(
            2,
            mix.EndFrequencyB,
            14 + scenario.PowerDeltaDb,
            -scenario.DriftHzPerSecond)
        };

        var sw = Stopwatch.StartNew();
        IReadOnlyList<DeepCwLaneResult> decoded =
          decoder.Decode(
            mix.Audio,
            SampleRate,
            new DateTime(
              2026, 10, 9, 0, 0, 0,
              DateTimeKind.Utc),
            tracks);
        sw.Stop();

        string textA = decoded
          .FirstOrDefault(x => x.TrackId == 1).Text ?? "";
        string textB = decoded
          .FirstOrDefault(x => x.TrackId == 2).Text ?? "";

        // Oracle-track unmasked reference. This isolates the gain/loss from
        // the activity-aware competitive mask rather than conflating it with
        // the ONNX model itself.
        DeepCwWidebandFeatureWindow wide =
          DeepCwWidebandFeatureWindow.Create(
            mix.Audio, SampleRate, metadata);
        var unmaskedSw = Stopwatch.StartNew();
        string unmaskedA = onnx.Decode(
          wide.BuildUnmaskedLaneTensor(tracks[0])).Text;
        string unmaskedB = onnx.Decode(
          wide.BuildUnmaskedLaneTensor(tracks[1])).Text;
        unmaskedSw.Stop();

        double cerA = CharacterErrorRate(truthA, textA);
        double cerB = CharacterErrorRate(truthB, textB);
        double unmaskedCerA =
          CharacterErrorRate(truthA, unmaskedA);
        double unmaskedCerB =
          CharacterErrorRate(truthB, unmaskedB);

        var result = new BenchmarkResult(
          scenario.Name,
          scenario.CenterFrequencyHz,
          scenario.SeparationHz,
          scenario.PowerDeltaDb,
          scenario.DriftHzPerSecond,
          scenario.Snr2500HzDb,
          truthA,
          textA,
          cerA,
          WordErrorRate(truthA, textA),
          ContainsCallsign(textA, "K1ABC"),
          unmaskedA,
          unmaskedCerA,
          WordErrorRate(truthA, unmaskedA),
          truthB,
          textB,
          cerB,
          WordErrorRate(truthB, textB),
          ContainsCallsign(textB, "W9XYZ"),
          unmaskedB,
          unmaskedCerB,
          WordErrorRate(truthB, unmaskedB),
          sw.Elapsed.TotalSeconds,
          sw.Elapsed.TotalSeconds / DurationSeconds,
          unmaskedSw.Elapsed.TotalSeconds,
          unmaskedSw.Elapsed.TotalSeconds / DurationSeconds);
        results.Add(result);

        output.WriteLine(
          JsonSerializer.Serialize(result));
      }

      results.Should().HaveCount(scenarios.Length);
      results.Should().OnlyContain(x =>
        double.IsFinite(x.CerA) &&
        double.IsFinite(x.CerB) &&
        double.IsFinite(x.UnmaskedCerA) &&
        double.IsFinite(x.UnmaskedCerB) &&
        double.IsFinite(x.RealTimeFactor) &&
        double.IsFinite(x.UnmaskedRealTimeFactor));

      // Hard sanity/regression gates are deliberately limited to clearly
      // resolvable reference cases. Dense 5–25 Hz Pileup remains a measured
      // research curve rather than an artificial "must decode" assertion.
      BenchmarkResult easy = results.Single(
        x => x.Scenario == "static_equal_sep40");
      easy.CerA.Should().BeLessThanOrEqualTo(0.20);
      easy.CerB.Should().BeLessThanOrEqualTo(0.20);

      BenchmarkResult wideband = results.Single(
        x => x.Scenario == "wideband2600_sep25_d10");
      wideband.CerA.Should().BeLessThanOrEqualTo(0.25);
      wideband.CerB.Should().BeLessThanOrEqualTo(0.25);

      results.Max(x => x.RealTimeFactor)
        .Should().BeLessThan(1.0);

      StreamingBenchmarkResult streaming =
        RunStreamingTranscriptBenchmark(
          metadata,
          decoder);

      output.WriteLine(
        "STREAMING " +
        JsonSerializer.Serialize(streaming));

      streaming.StreamingCerA.Should()
        .BeLessThan(streaming.NaiveConcatCerA);
      streaming.StreamingCerB.Should()
        .BeLessThan(streaming.NaiveConcatCerB);
      streaming.StreamingCerA.Should()
        .BeLessThanOrEqualTo(0.45);
      streaming.StreamingCerB.Should()
        .BeLessThanOrEqualTo(0.45);
      streaming.RealTimeFactor.Should()
        .BeLessThan(1.0);

      if (!string.IsNullOrWhiteSpace(reportPath))
      {
        string? directory =
          Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
          Directory.CreateDirectory(directory);

        File.WriteAllText(
          reportPath,
          JsonSerializer.Serialize(
            new
            {
              generatedUtc =
                DateTime.UtcNow,
              modelRevision =
                DeepCwModelManager.Revision,
              referenceNoiseBandwidthHz = 2500,
              keyedSignalPowerIncludesDutyCycle = true,
              oracleTracks = true,
              scenarios = results,
              streamingTranscript = streaming
            },
            new JsonSerializerOptions
            {
              WriteIndented = true
            }));
      }
    }

    [Fact]
    public void RealDeepCw_HamNoiseClassicV2_AB()
    {
      if (!string.Equals(
            Environment.GetEnvironmentVariable(
              "SKYROOF_RUN_DEEPCW_BENCHMARK"),
            "1",
            StringComparison.Ordinal))
      {
        output.WriteLine(
          "HamNoise A/B benchmark disabled in normal CI.");
        return;
      }

      HamNoiseLaneDenoiser.IsAvailable()
        .Should().BeTrue(
          "the dedicated benchmark workflow builds the pinned HamNoise bridge");

      string modelPath =
        RequiredEnvironment(
          "DEEPCW_MODEL_PATH");
      string metadataPath =
        RequiredEnvironment(
          "DEEPCW_METADATA_PATH");
      string baselineReport =
        RequiredEnvironment(
          "DEEPCW_BENCHMARK_OUTPUT");
      string reportPath =
        Path.Combine(
          Path.GetDirectoryName(
            baselineReport) ??
            Environment.CurrentDirectory,
          "cw-hamnoise-ab.json");

      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Load(
          metadataPath);
      using var onnx =
        new DeepCwOnnxDecoder(
          modelPath,
          metadata);

      BenchmarkScenario[] scenarios =
      [
        new("static_equal_sep40", 800, 40, 0, 0, -2),
        new("static_equal_sep25", 800, 25, 0, 0, -2),
        new("static_equal_sep15", 800, 15, 0, 0, -2),
        new("static_equal_sep10", 800, 10, 0, 0, -2),
        new("static_equal_sep5", 800, 5, 0, 0, -2),

        new("static_8db_sep40", 800, 40, 8, 0, -2),
        new("static_8db_sep25", 800, 25, 8, 0, -2),
        new("static_8db_sep15", 800, 15, 8, 0, -2),
        new("static_8db_sep10", 800, 10, 8, 0, -2),
        new("static_8db_sep5", 800, 5, 8, 0, -2),

        new("doppler5_sep25_8db", 800, 25, 8, 5, -2),
        new("doppler10_sep25_8db", 800, 25, 8, 10, -2),
        new("doppler20_sep25_8db", 800, 25, 8, 20, -2),

        new("wideband2600_sep25_d10", 2600, 25, 6, 10, -2)
      ];

      CwDenoiseMode[] modes =
      [
        CwDenoiseMode.Bypass,
        CwDenoiseMode.HamNoiseClassic,
        CwDenoiseMode.HamNoiseV2
      ];

      const string truthA =
        "CQ DE K1ABC";
      const string truthB =
        "CQ DE W9XYZ";

      var results =
        new List<DenoiseAbResult>();

      foreach (CwDenoiseMode mode in modes)
      {
        ICwLaneDenoiser? denoiser =
          mode ==
            CwDenoiseMode.Bypass
            ? null
            : new HamNoiseLaneDenoiser(
                mode);

        var decoder =
          new DeepCwMultiLaneDecoder(
            metadata,
            onnx)
          {
            MaxLanes = 2,
            LaneBandwidthHz = 240,
            TargetCenterHz = 800,
            LaneDenoiser = denoiser,
            DenoiseWet = 1.0
          };

        foreach (BenchmarkScenario scenario
          in scenarios)
        {
          GeneratedMix mix =
            GenerateMix(
              scenario,
              truthA,
              truthB,
              wpmA: 24,
              wpmB: 27);

          CwSignalTrack[] tracks =
          [
            MakeTrack(
              1,
              mix.EndFrequencyA,
              14,
              scenario.DriftHzPerSecond),
            MakeTrack(
              2,
              mix.EndFrequencyB,
              14 +
                scenario.PowerDeltaDb,
              -scenario.DriftHzPerSecond)
          ];

          var sw =
            Stopwatch.StartNew();

          IReadOnlyList<DeepCwLaneResult>
            decoded =
              decoder.Decode(
                mix.Audio,
                SampleRate,
                new DateTime(
                  2026, 10, 9,
                  0, 0, 0,
                  DateTimeKind.Utc),
                tracks);

          sw.Stop();

          string textA =
            decoded
              .FirstOrDefault(
                x => x.TrackId == 1)
              .Text ?? "";
          string textB =
            decoded
              .FirstOrDefault(
                x => x.TrackId == 2)
              .Text ?? "";

          var result =
            new DenoiseAbResult(
              Mode:
                mode.ToString(),
              Scenario:
                scenario.Name,
              SeparationHz:
                scenario.SeparationHz,
              PowerDeltaDb:
                scenario.PowerDeltaDb,
              DriftHzPerSecond:
                scenario.DriftHzPerSecond,
              CenterFrequencyHz:
                scenario.CenterFrequencyHz,
              TruthA:
                truthA,
              DecodedA:
                textA,
              CerA:
                CharacterErrorRate(
                  truthA,
                  textA),
              WerA:
                WordErrorRate(
                  truthA,
                  textA),
              CallsignA:
                ContainsCallsign(
                  textA,
                  "K1ABC"),
              TruthB:
                truthB,
              DecodedB:
                textB,
              CerB:
                CharacterErrorRate(
                  truthB,
                  textB),
              WerB:
                WordErrorRate(
                  truthB,
                  textB),
              CallsignB:
                ContainsCallsign(
                  textB,
                  "W9XYZ"),
              ProcessingSeconds:
                sw.Elapsed.TotalSeconds,
              RealTimeFactor:
                sw.Elapsed.TotalSeconds /
                DurationSeconds);

          results.Add(result);
          output.WriteLine(
            "HAMNOISE_AB " +
            JsonSerializer.Serialize(
              result));
        }
      }

      results.Should().HaveCount(
        scenarios.Length *
        modes.Length);
      results.Should().OnlyContain(x =>
        double.IsFinite(x.CerA) &&
        double.IsFinite(x.CerB) &&
        double.IsFinite(x.WerA) &&
        double.IsFinite(x.WerB) &&
        double.IsFinite(
          x.RealTimeFactor));

      var summary =
        results
          .GroupBy(x => x.Mode)
          .Select(group => new
          {
            mode = group.Key,
            meanCer =
              group.Average(x =>
                (x.CerA + x.CerB) /
                2.0),
            meanWer =
              group.Average(x =>
                (x.WerA + x.WerB) /
                2.0),
            callsigns =
              group.Sum(x =>
                (x.CallsignA ? 1 : 0) +
                (x.CallsignB ? 1 : 0)),
            totalCallsigns =
              group.Count() * 2,
            maxRealTimeFactor =
              group.Max(x =>
                x.RealTimeFactor),
            meanRealTimeFactor =
              group.Average(x =>
                x.RealTimeFactor)
          })
          .ToArray();

      Directory.CreateDirectory(
        Path.GetDirectoryName(
          reportPath)!);

      File.WriteAllText(
        reportPath,
        JsonSerializer.Serialize(
          new
          {
            generatedUtc =
              DateTime.UtcNow,
            hamNoiseRevision =
              "1af3a77b2ff18dada2149f36686430cdae7cf13c",
            deepCwRevision =
              DeepCwModelManager.Revision,
            denoiseWet = 1.0,
            architecture =
              "Bypass=wideband activity-aware soft mask; HamNoise=per-lane DDC at 9.6kHz then denoise then DeepCW frontend",
            summary,
            scenarios = results
          },
          new JsonSerializerOptions
          {
            WriteIndented = true
          }));

      output.WriteLine(
        "HAMNOISE_SUMMARY " +
        JsonSerializer.Serialize(
          summary));
    }

    private StreamingBenchmarkResult
      RunStreamingTranscriptBenchmark(
        DeepCwModelMetadata metadata,
        DeepCwMultiLaneDecoder decoder)
    {
      const double totalSeconds = 10.0;
      const double windowSeconds = 6.0;
      const double hopSeconds = 1.0;
      const string truthA = "CQ DE K1ABC";
      const string truthB = "CQ DE W9XYZ";

      int count =
        (int)Math.Round(
          totalSeconds * SampleRate);
      float[] envelopeA =
        BuildMorseEnvelope(
          truthA,
          24,
          count,
          startSeconds: 1.20);
      float[] envelopeB =
        BuildMorseEnvelope(
          truthB,
          27,
          count,
          startSeconds: 1.40);

      float[] laneA =
        SynthesizeLane(
          envelopeA,
          0.12,
          780,
          0);
      float[] laneB =
        SynthesizeLane(
          envelopeB,
          0.12,
          820,
          0);

      // Keep this benchmark focused on transcript reconciliation rather than
      // absolute sensitivity. +4 dB in 2.5 kHz still exercises the 40 Hz
      // two-lane separator while producing repeatable overlapping decodes.
      double signalRms =
        Rms(laneA);
      double noiseRms2500 =
        signalRms /
        Math.Sqrt(
          Math.Pow(10, 4.0 / 10.0));
      double fullBandNoiseRms =
        noiseRms2500 *
        Math.Sqrt(
          (SampleRate / 2.0) /
          2500.0);

      var random =
        new Random(20261009);
      float[] audio =
        new float[count];
      float peak = 0;
      for (int i = 0; i < count; i++)
      {
        float value =
          laneA[i] +
          laneB[i] +
          (float)(
            NextGaussian(random) *
            fullBandNoiseRms);
        audio[i] = value;
        peak = Math.Max(
          peak,
          Math.Abs(value));
      }

      if (peak > 0.92f)
      {
        float scale =
          0.92f / peak;
        for (int i = 0; i < audio.Length; i++)
          audio[i] *= scale;
      }

      DateTime origin =
        new(
          2026, 10, 9,
          0, 0, 0,
          DateTimeKind.Utc);

      CwSignalTrack trackA =
        new(
          101,
          780,
          16,
          0,
          origin,
          origin,
          Confirmed: true,
          Active: true,
          Ambiguous: false,
          FrequencySigmaHz: 2,
          MergeGroupId: 0,
          IdentityConfidence: 1,
          AssociationHintId: 1001);

      CwSignalTrack trackB =
        new(
          202,
          820,
          16,
          0,
          origin,
          origin,
          Confirmed: true,
          Active: true,
          Ambiguous: false,
          FrequencySigmaHz: 2,
          MergeGroupId: 0,
          IdentityConfidence: 1,
          AssociationHintId: 2002);

      var transcripts =
        new CwIncrementalTranscriptCoordinator(
          new CwTranscriptOptions
          {
            SymbolMatchToleranceSeconds =
              0.26,
            CommitLagSeconds = 0.9,
            AbandonAfterSeconds = 2.8,
            MinimumConfirmations = 2,
            MinimumConsensus = 0.58,
            MinimumAverageConfidence = 0.50,
            CommittedMatchRetentionSeconds = 8
          });
      var continuous =
        new CwContinuousDeepCwDecoder(
          decoder,
          transcripts);

      var rawA = new List<string>();
      var rawB = new List<string>();
      var stopwatch =
        Stopwatch.StartNew();
      int windowSamples =
        (int)Math.Round(
          windowSeconds * SampleRate);

      for (double endSeconds = windowSeconds;
           endSeconds <= totalSeconds + 1e-9;
           endSeconds += hopSeconds)
      {
        int endSample =
          (int)Math.Round(
            endSeconds * SampleRate);
        int startSample =
          endSample - windowSamples;

        CwContinuousDecodeBatch batch =
          continuous.Decode(
            audio.AsSpan(
              startSample,
              windowSamples),
            SampleRate,
            origin.AddSeconds(endSeconds),
            [trackA, trackB]);

        DeepCwLaneResult a =
          batch.LaneResults.Single(
            x => x.AssociationHintId == 1001);
        DeepCwLaneResult b =
          batch.LaneResults.Single(
            x => x.AssociationHintId == 2002);
        rawA.Add(a.Text);
        rawB.Add(b.Text);
      }

      stopwatch.Stop();

      CwTranscriptSnapshot finalA =
        continuous.Transcripts.Get(
          trackA.Id,
          trackA.AssociationHintId)
        ?? throw new InvalidOperationException(
          "Streaming transcript A missing.");
      CwTranscriptSnapshot finalB =
        continuous.Transcripts.Get(
          trackB.Id,
          trackB.AssociationHintId)
        ?? throw new InvalidOperationException(
          "Streaming transcript B missing.");

      string naiveA =
        string.Concat(rawA);
      string naiveB =
        string.Concat(rawB);
      string stableA =
        finalA.Text;
      string stableB =
        finalB.Text;

      return new(
        WindowSeconds:
          windowSeconds,
        HopSeconds:
          hopSeconds,
        WindowCount:
          rawA.Count,
        TruthA:
          truthA,
        RawWindowsA:
          rawA.ToArray(),
        NaiveConcatA:
          naiveA,
        NaiveConcatCerA:
          CharacterErrorRate(
            truthA,
            naiveA),
        StreamingA:
          stableA,
        StreamingCerA:
          CharacterErrorRate(
            truthA,
            stableA),
        StreamingWerA:
          WordErrorRate(
            truthA,
            stableA),
        CallsignA:
          ContainsCallsign(
            stableA,
            "K1ABC"),
        TruthB:
          truthB,
        RawWindowsB:
          rawB.ToArray(),
        NaiveConcatB:
          naiveB,
        NaiveConcatCerB:
          CharacterErrorRate(
            truthB,
            naiveB),
        StreamingB:
          stableB,
        StreamingCerB:
          CharacterErrorRate(
            truthB,
            stableB),
        StreamingWerB:
          WordErrorRate(
            truthB,
            stableB),
        CallsignB:
          ContainsCallsign(
            stableB,
            "W9XYZ"),
        ProcessingSeconds:
          stopwatch.Elapsed.TotalSeconds,
        RealTimeFactor:
          stopwatch.Elapsed.TotalSeconds /
          totalSeconds);
    }

    private static string RequiredEnvironment(
      string name) =>
      Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException(
        $"Required benchmark environment variable {name} is missing.");

    private static CwSignalTrack MakeTrack(
      int id,
      double endFrequencyHz,
      double snrDb,
      double driftHzPerSecond) =>
      new(
        id,
        endFrequencyHz,
        snrDb,
        driftHzPerSecond,
        new DateTime(
          2026, 10, 9, 0, 0, 0,
          DateTimeKind.Utc).AddSeconds(-DurationSeconds),
        new DateTime(
          2026, 10, 9, 0, 0, 0,
          DateTimeKind.Utc),
        Confirmed: true,
        Active: true,
        Ambiguous: false,
        FrequencySigmaHz: 2);

    private static GeneratedMix GenerateMix(
      BenchmarkScenario scenario,
      string textA,
      string textB,
      double wpmA,
      double wpmB)
    {
      int count =
        (int)Math.Round(DurationSeconds * SampleRate);
      float[] envelopeA = BuildMorseEnvelope(
        textA, wpmA, count, startSeconds: 0.35);
      float[] envelopeB = BuildMorseEnvelope(
        textB, wpmB, count, startSeconds: 0.55);

      double endA =
        scenario.CenterFrequencyHz -
        scenario.SeparationHz / 2.0;
      double endB =
        scenario.CenterFrequencyHz +
        scenario.SeparationHz / 2.0;
      double ampA = 0.12;
      double ampB = ampA *
        Math.Pow(10, scenario.PowerDeltaDb / 20.0);

      float[] laneA = SynthesizeLane(
        envelopeA,
        ampA,
        endA,
        scenario.DriftHzPerSecond);
      float[] laneB = SynthesizeLane(
        envelopeB,
        ampB,
        endB,
        -scenario.DriftHzPerSecond);

      // SNR is referenced to noise power in a 2500 Hz bandwidth and includes
      // the actual Morse keying duty cycle in the signal RMS.
      double signalRmsA = Rms(laneA);
      double noiseRms2500 =
        signalRmsA /
        Math.Sqrt(Math.Pow(
          10, scenario.Snr2500HzDb / 10.0));
      double fullBandNoiseRms =
        noiseRms2500 *
        Math.Sqrt((SampleRate / 2.0) / 2500.0);

      var random = new Random(
        StableHash(scenario.Name));
      float[] mix = new float[count];
      float peak = 0;
      for (int i = 0; i < count; i++)
      {
        double gaussian =
          NextGaussian(random);
        mix[i] = laneA[i] + laneB[i] +
          (float)(gaussian * fullBandNoiseRms);
        peak = Math.Max(peak, Math.Abs(mix[i]));
      }

      // DeepCW's WAV reference path maps PCM to roughly [-1, 1]. Normalize
      // only when needed; this preserves all relative powers and SNRs.
      if (peak > 0.92f)
      {
        float scale = 0.92f / peak;
        for (int i = 0; i < mix.Length; i++)
          mix[i] *= scale;
      }

      return new(
        mix,
        endA,
        endB);
    }

    private static float[] BuildMorseEnvelope(
      string text,
      double wpm,
      int sampleCount,
      double startSeconds)
    {
      if (wpm <= 0)
        throw new ArgumentOutOfRangeException(nameof(wpm));

      var map = MorseMap;
      float[] envelope = new float[sampleCount];
      double ditSeconds = 1.2 / wpm;
      int cursor =
        (int)Math.Round(startSeconds * SampleRate);
      int rampSamples =
        Math.Max(1, (int)Math.Round(
          0.004 * SampleRate));

      string normalized =
        NormalizeText(text);
      bool previousWasCharacter = false;

      foreach (char ch in normalized)
      {
        if (ch == ' ')
        {
          if (previousWasCharacter)
            cursor += (int)Math.Round(
              4 * ditSeconds * SampleRate);
          previousWasCharacter = false;
          continue;
        }

        if (!map.TryGetValue(ch, out string? code))
          continue;

        for (int symbolIndex = 0;
             symbolIndex < code.Length;
             symbolIndex++)
        {
          int units =
            code[symbolIndex] == '-' ? 3 : 1;
          int toneSamples =
            (int)Math.Round(
              units * ditSeconds * SampleRate);
          ApplyTone(
            envelope,
            cursor,
            toneSamples,
            rampSamples);
          cursor += toneSamples;

          if (symbolIndex + 1 < code.Length)
            cursor += (int)Math.Round(
              ditSeconds * SampleRate);
        }

        cursor += (int)Math.Round(
          3 * ditSeconds * SampleRate);
        previousWasCharacter = true;
        if (cursor >= sampleCount) break;
      }

      return envelope;
    }

    private static void ApplyTone(
      float[] envelope,
      int start,
      int length,
      int rampSamples)
    {
      int stop = Math.Min(
        envelope.Length, start + length);
      for (int i = Math.Max(0, start);
           i < stop;
           i++)
      {
        int local = i - start;
        int fromEnd = stop - 1 - i;
        double gain = 1;

        if (local < rampSamples)
        {
          double x =
            (local + 1) /
            (double)(rampSamples + 1);
          gain *= 0.5 -
            0.5 * Math.Cos(Math.PI * x);
        }
        if (fromEnd < rampSamples)
        {
          double x =
            (fromEnd + 1) /
            (double)(rampSamples + 1);
          gain *= 0.5 -
            0.5 * Math.Cos(Math.PI * x);
        }

        envelope[i] =
          Math.Max(envelope[i], (float)gain);
      }
    }

    private static float[] SynthesizeLane(
      float[] envelope,
      double amplitude,
      double endFrequencyHz,
      double driftHzPerSecond)
    {
      float[] result = new float[envelope.Length];
      double phase = 0;
      for (int i = 0; i < result.Length; i++)
      {
        double relativeToEnd =
          (i - (result.Length - 1)) /
          (double)SampleRate;
        double frequency =
          endFrequencyHz +
          driftHzPerSecond * relativeToEnd;
        phase += 2 * Math.PI *
          frequency / SampleRate;
        result[i] =
          (float)(amplitude *
                  envelope[i] *
                  Math.Sin(phase));
      }
      return result;
    }

    private static double CharacterErrorRate(
      string truth,
      string decoded)
    {
      string a = NormalizeText(truth);
      string b = NormalizeText(decoded);
      int[,] d =
        new int[a.Length + 1, b.Length + 1];

      for (int i = 0; i <= a.Length; i++)
        d[i, 0] = i;
      for (int j = 0; j <= b.Length; j++)
        d[0, j] = j;

      for (int i = 1; i <= a.Length; i++)
      {
        for (int j = 1; j <= b.Length; j++)
        {
          int cost =
            a[i - 1] == b[j - 1] ? 0 : 1;
          d[i, j] = Math.Min(
            Math.Min(
              d[i - 1, j] + 1,
              d[i, j - 1] + 1),
            d[i - 1, j - 1] + cost);
        }
      }

      return d[a.Length, b.Length] /
        (double)Math.Max(1, a.Length);
    }

    private static string NormalizeText(
      string value) =>
      string.Join(
        ' ',
        value
          .ToUpperInvariant()
          .Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries));

    private static double WordErrorRate(
      string truth,
      string decoded)
    {
      string[] a = NormalizeText(truth)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
      string[] b = NormalizeText(decoded)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
      int[,] d = new int[a.Length + 1, b.Length + 1];

      for (int i = 0; i <= a.Length; i++)
        d[i, 0] = i;
      for (int j = 0; j <= b.Length; j++)
        d[0, j] = j;

      for (int i = 1; i <= a.Length; i++)
      {
        for (int j = 1; j <= b.Length; j++)
        {
          int cost = string.Equals(
            a[i - 1], b[j - 1],
            StringComparison.Ordinal) ? 0 : 1;
          d[i, j] = Math.Min(
            Math.Min(
              d[i - 1, j] + 1,
              d[i, j - 1] + 1),
            d[i - 1, j - 1] + cost);
        }
      }

      return d[a.Length, b.Length] /
        (double)Math.Max(1, a.Length);
    }

    private static bool ContainsCallsign(
      string decoded,
      string callsign) =>
      NormalizeText(decoded)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains(
          callsign.ToUpperInvariant(),
          StringComparer.Ordinal);

    private static double Rms(
      IReadOnlyList<float> values)
    {
      double sum = 0;
      for (int i = 0; i < values.Count; i++)
        sum += values[i] * values[i];
      return Math.Sqrt(
        sum / Math.Max(1, values.Count));
    }

    private static double NextGaussian(
      Random random)
    {
      double u1 = Math.Max(
        random.NextDouble(), 1e-12);
      double u2 = random.NextDouble();
      return Math.Sqrt(-2 * Math.Log(u1)) *
        Math.Cos(2 * Math.PI * u2);
    }

    private static int StableHash(
      string value)
    {
      unchecked
      {
        int hash = 17;
        foreach (char ch in value)
          hash = hash * 31 + ch;
        return hash;
      }
    }

    private static readonly IReadOnlyDictionary<char, string>
      MorseMap =
        new Dictionary<char, string>
        {
          ['A'] = ".-",
          ['B'] = "-...",
          ['C'] = "-.-.",
          ['D'] = "-..",
          ['E'] = ".",
          ['F'] = "..-.",
          ['G'] = "--.",
          ['H'] = "....",
          ['I'] = "..",
          ['J'] = ".---",
          ['K'] = "-.-",
          ['L'] = ".-..",
          ['M'] = "--",
          ['N'] = "-.",
          ['O'] = "---",
          ['P'] = ".--.",
          ['Q'] = "--.-",
          ['R'] = ".-.",
          ['S'] = "...",
          ['T'] = "-",
          ['U'] = "..-",
          ['V'] = "...-",
          ['W'] = ".--",
          ['X'] = "-..-",
          ['Y'] = "-.--",
          ['Z'] = "--..",
          ['0'] = "-----",
          ['1'] = ".----",
          ['2'] = "..---",
          ['3'] = "...--",
          ['4'] = "....-",
          ['5'] = ".....",
          ['6'] = "-....",
          ['7'] = "--...",
          ['8'] = "---..",
          ['9'] = "----."
        };

    private readonly record struct StreamingBenchmarkResult(
      double WindowSeconds,
      double HopSeconds,
      int WindowCount,
      string TruthA,
      string[] RawWindowsA,
      string NaiveConcatA,
      double NaiveConcatCerA,
      string StreamingA,
      double StreamingCerA,
      double StreamingWerA,
      bool CallsignA,
      string TruthB,
      string[] RawWindowsB,
      string NaiveConcatB,
      double NaiveConcatCerB,
      string StreamingB,
      double StreamingCerB,
      double StreamingWerB,
      bool CallsignB,
      double ProcessingSeconds,
      double RealTimeFactor);

    private readonly record struct DenoiseAbResult(
      string Mode,
      string Scenario,
      double SeparationHz,
      double PowerDeltaDb,
      double DriftHzPerSecond,
      double CenterFrequencyHz,
      string TruthA,
      string DecodedA,
      double CerA,
      double WerA,
      bool CallsignA,
      string TruthB,
      string DecodedB,
      double CerB,
      double WerB,
      bool CallsignB,
      double ProcessingSeconds,
      double RealTimeFactor);

    private readonly record struct BenchmarkScenario(
      string Name,
      double CenterFrequencyHz,
      double SeparationHz,
      double PowerDeltaDb,
      double DriftHzPerSecond,
      double Snr2500HzDb);

    private readonly record struct GeneratedMix(
      float[] Audio,
      double EndFrequencyA,
      double EndFrequencyB);

    private readonly record struct BenchmarkResult(
      string Scenario,
      double CenterFrequencyHz,
      double SeparationHz,
      double PowerDeltaDb,
      double DriftHzPerSecond,
      double Snr2500HzDb,
      string TruthA,
      string DecodedA,
      double CerA,
      double WerA,
      bool CallsignA,
      string UnmaskedDecodedA,
      double UnmaskedCerA,
      double UnmaskedWerA,
      string TruthB,
      string DecodedB,
      double CerB,
      double WerB,
      bool CallsignB,
      string UnmaskedDecodedB,
      double UnmaskedCerB,
      double UnmaskedWerB,
      double ProcessingSeconds,
      double RealTimeFactor,
      double UnmaskedProcessingSeconds,
      double UnmaskedRealTimeFactor);
  }
}
