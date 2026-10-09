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
        new("sep40_equal", 800, 40, 0, 0, -2),
        new("sep25_8db", 800, 25, 8, 0, -2),
        new("sep15_8db_drift5", 800, 15, 8, 5, -2),
        new("sep10_10db_drift10", 800, 10, 10, 10, -2),
        new("sep5_10db_drift20", 800, 5, 10, 20, -2),
        new("wideband2600_sep25", 2600, 25, 6, 10, -2)
      ];

      var results = new List<BenchmarkResult>();
      foreach (BenchmarkScenario scenario in scenarios)
      {
        const string truthA = "CQ DE K1ABC K1ABC";
        const string truthB = "CQ DE W9XYZ W9XYZ";

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
        double cerA = CharacterErrorRate(
          truthA, textA);
        double cerB = CharacterErrorRate(
          truthB, textB);

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
          truthB,
          textB,
          cerB,
          sw.Elapsed.TotalSeconds,
          sw.Elapsed.TotalSeconds /
            DurationSeconds);
        results.Add(result);

        output.WriteLine(
          JsonSerializer.Serialize(result));
      }

      results.Should().HaveCount(scenarios.Length);
      results.Should().OnlyContain(x =>
        double.IsFinite(x.CerA) &&
        double.IsFinite(x.CerB) &&
        double.IsFinite(x.RealTimeFactor));

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
              scenarios = results
            },
            new JsonSerializerOptions
            {
              WriteIndented = true
            }));
      }
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
      double ampA = 0.30;
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
      for (int i = 0; i < count; i++)
      {
        double gaussian =
          NextGaussian(random);
        mix[i] = laneA[i] + laneB[i] +
          (float)(gaussian * fullBandNoiseRms);
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
      string TruthB,
      string DecodedB,
      double CerB,
      double ProcessingSeconds,
      double RealTimeFactor);
  }
}
