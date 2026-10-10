using System.Diagnostics;
using FluentAssertions;
using System.Text.Json;
using NAudio.Wave;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  internal sealed class CwCorpusManifest
  {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public CwCorpusCase[] Cases { get; set; } = [];

    public static CwCorpusManifest Load(string path)
    {
      CwCorpusManifest value =
        JsonSerializer.Deserialize<CwCorpusManifest>(
          File.ReadAllText(path),
          new JsonSerializerOptions
          {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
          }) ??
        throw new InvalidDataException(
          "CW corpus manifest is empty.");

      value.Validate(
        Path.GetDirectoryName(
          Path.GetFullPath(path))!);
      return value;
    }

    public void Validate(string root)
    {
      if (Version != 1)
        throw new InvalidDataException(
          $"Unsupported CW corpus manifest version {Version}.");
      if (string.IsNullOrWhiteSpace(Name))
        throw new InvalidDataException(
          "CW corpus name is required.");
      if (Cases.Length == 0)
        throw new InvalidDataException(
          "CW corpus must contain at least one case.");

      var names =
        new HashSet<string>(
          StringComparer.OrdinalIgnoreCase);

      foreach (CwCorpusCase item in Cases)
      {
        item.Validate(root);
        if (!names.Add(item.Name))
          throw new InvalidDataException(
            $"Duplicate CW corpus case '{item.Name}'.");
      }
    }
  }

  internal sealed class CwCorpusCase
  {
    public string Name { get; set; } = string.Empty;
    public string Wav { get; set; } = string.Empty;
    public double KnownDopplerRateHzPerSecond { get; set; }
    public double MinFrequencyHz { get; set; } = 100;
    public double MaxFrequencyHz { get; set; } = 2000;
    public double MinimumSnrDb { get; set; } = 6;
    public double AnalysisSeconds { get; set; } = 2.4;
    public double DecodeWindowSeconds { get; set; } = 6.0;
    public double DecodeHopSeconds { get; set; } = 1.0;
    public int MaxDecodeLanes { get; set; } = 8;
    public CwCorpusTruthLane[] Lanes { get; set; } = [];

    public void Validate(string root)
    {
      if (string.IsNullOrWhiteSpace(Name))
        throw new InvalidDataException(
          "Every CW corpus case requires a name.");
      if (string.IsNullOrWhiteSpace(Wav) ||
          Path.IsPathRooted(Wav) ||
          Wav.Contains("..", StringComparison.Ordinal))
        throw new InvalidDataException(
          $"CW corpus case '{Name}' has an unsafe WAV path.");

      string full =
        Path.GetFullPath(
          Path.Combine(root, Wav));
      string normalizedRoot =
        Path.GetFullPath(root)
          .TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) +
          Path.DirectorySeparatorChar;
      if (!full.StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException(
          $"CW corpus case '{Name}' escapes the corpus root.");
      if (!File.Exists(full))
        throw new FileNotFoundException(
          $"CW corpus WAV not found for '{Name}'.",
          full);

      if (!double.IsFinite(
            KnownDopplerRateHzPerSecond) ||
          Math.Abs(
            KnownDopplerRateHzPerSecond) > 200)
        throw new InvalidDataException(
          $"CW corpus case '{Name}' Doppler rate is invalid.");
      if (!double.IsFinite(MinFrequencyHz) ||
          !double.IsFinite(MaxFrequencyHz) ||
          MinFrequencyHz < 20 ||
          MaxFrequencyHz <= MinFrequencyHz ||
          MaxFrequencyHz > 10000)
        throw new InvalidDataException(
          $"CW corpus case '{Name}' AF range is invalid.");
      if (!double.IsFinite(MinimumSnrDb) ||
          MinimumSnrDb < -10 ||
          MinimumSnrDb > 40)
        throw new InvalidDataException(
          $"CW corpus case '{Name}' SNR threshold is invalid.");
      if (!double.IsFinite(AnalysisSeconds) ||
          AnalysisSeconds < 0.5 ||
          AnalysisSeconds > 10)
        throw new InvalidDataException(
          $"CW corpus case '{Name}' analysis window is invalid.");
      if (!double.IsFinite(DecodeWindowSeconds) ||
          DecodeWindowSeconds < 1 ||
          DecodeWindowSeconds > 30 ||
          !double.IsFinite(DecodeHopSeconds) ||
          DecodeHopSeconds <= 0 ||
          DecodeHopSeconds > DecodeWindowSeconds)
        throw new InvalidDataException(
          $"CW corpus case '{Name}' decode cadence is invalid.");
      if (MaxDecodeLanes is < 1 or > 8)
        throw new InvalidDataException(
          $"CW corpus case '{Name}' max lane count must be 1..8.");
      if (Lanes.Length == 0 ||
          Lanes.Length > 8)
        throw new InvalidDataException(
          $"CW corpus case '{Name}' must define 1..8 truth lanes.");

      var ids =
        new HashSet<string>(
          StringComparer.OrdinalIgnoreCase);

      foreach (CwCorpusTruthLane lane in Lanes)
      {
        lane.Validate(this);
        if (!ids.Add(lane.Id))
          throw new InvalidDataException(
            $"Duplicate truth lane '{lane.Id}' in case '{Name}'.");
      }
    }
  }

  internal sealed class CwCorpusTruthLane
  {
    public string Id { get; set; } = string.Empty;
    public double ReferenceFrequencyHz { get; set; }
    public double FrequencyToleranceHz { get; set; } = 60;
    public string Text { get; set; } = string.Empty;
    public string? Callsign { get; set; }

    public void Validate(CwCorpusCase owner)
    {
      if (string.IsNullOrWhiteSpace(Id))
        throw new InvalidDataException(
          $"A truth lane in '{owner.Name}' is missing its id.");
      if (!double.IsFinite(
            ReferenceFrequencyHz) ||
          ReferenceFrequencyHz <
            owner.MinFrequencyHz ||
          ReferenceFrequencyHz >
            owner.MaxFrequencyHz)
        throw new InvalidDataException(
          $"Truth lane '{Id}' in '{owner.Name}' has an out-of-range reference frequency.");
      if (!double.IsFinite(
            FrequencyToleranceHz) ||
          FrequencyToleranceHz <= 0 ||
          FrequencyToleranceHz > 500)
        throw new InvalidDataException(
          $"Truth lane '{Id}' in '{owner.Name}' has an invalid frequency tolerance.");
      if (string.IsNullOrWhiteSpace(Text))
        throw new InvalidDataException(
          $"Truth lane '{Id}' in '{owner.Name}' requires expected text.");
    }
  }

  public sealed class CwRecordedCorpusBenchmarkTests
  {
    private static readonly DateTime Origin =
      new(
        2026, 10, 9,
        0, 0, 0,
        DateTimeKind.Utc);

    [Fact]
    public void Manifest_RejectsTraversalAndInvalidTruth()
    {
      string root =
        Path.Combine(
          Path.GetTempPath(),
          "skyroof-cw-corpus-" +
          Guid.NewGuid()
            .ToString("N"));
      Directory.CreateDirectory(root);

      try
      {
        File.WriteAllBytes(
          Path.Combine(root, "ok.wav"),
          [0, 1, 2, 3]);

        var manifest =
          new CwCorpusManifest
          {
            Name = "unit",
            Cases =
            [
              new()
              {
                Name = "bad",
                Wav = "../outside.wav",
                Lanes =
                [
                  new()
                  {
                    Id = "A",
                    ReferenceFrequencyHz = 800,
                    Text = "CQ"
                  }
                ]
              }
            ]
          };

        Assert.Throws<InvalidDataException>(
          () => manifest.Validate(root));
      }
      finally
      {
        Directory.Delete(
          root,
          recursive: true);
      }
    }

    [Fact]
    public void RecordedCorpus_EndToEnd()
    {
      if (!string.Equals(
            Environment.GetEnvironmentVariable(
              "SKYROOF_RUN_CW_RECORDED_CORPUS"),
            "1",
            StringComparison.Ordinal))
        return;

      string corpusRoot =
        RequiredEnvironment(
          "CW_CORPUS_ROOT");
      string modelPath =
        RequiredEnvironment(
          "DEEPCW_MODEL_PATH");
      string metadataPath =
        RequiredEnvironment(
          "DEEPCW_METADATA_PATH");
      string outputPath =
        RequiredEnvironment(
          "CW_CORPUS_OUTPUT");

      string manifestPath =
        Path.Combine(
          corpusRoot,
          "manifest.json");

      CwCorpusManifest manifest =
        CwCorpusManifest.Load(
          manifestPath);
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Load(
          metadataPath);

      using var onnx =
        new DeepCwOnnxDecoder(
          modelPath,
          metadata);

      var caseReports =
        new List<object>();
      var allLaneReports =
        new List<CwCorpusLaneReport>();

      foreach (CwCorpusCase item
        in manifest.Cases)
      {
        CwCorpusCaseReport report =
          RunCase(
            corpusRoot,
            item,
            metadata,
            onnx);

        caseReports.Add(report);
        allLaneReports.AddRange(
          report.Lanes);

        Console.WriteLine(
          "CW_CORPUS_CASE " +
          JsonSerializer.Serialize(
            report));
      }

      int truthLanes =
        allLaneReports.Count;
      int matched =
        allLaneReports.Count(
          x => x.Matched);
      CwCorpusLaneReport[] scored =
        allLaneReports
          .Where(x => x.Matched)
          .ToArray();
      CwCorpusLaneReport[] callsignTruth =
        allLaneReports
          .Where(x =>
            !string.IsNullOrWhiteSpace(
              x.Callsign))
          .ToArray();

      var summary = new
      {
        manifest = manifest.Name,
        cases = manifest.Cases.Length,
        truthLanes,
        matchedLanes = matched,
        laneRecall =
          truthLanes == 0
            ? 0
            : matched /
              (double)truthLanes,
        meanCer =
          scored.Length == 0
            ? 1
            : scored.Average(
              x => x.Cer),
        meanWer =
          scored.Length == 0
            ? 1
            : scored.Average(
              x => x.Wer),
        callsigns =
          callsignTruth.Count(
            x => x.CallsignMatched),
        totalCallsigns =
          callsignTruth.Length,
        callsignRate =
          callsignTruth.Length == 0
            ? 1
            : callsignTruth.Count(
                x => x.CallsignMatched) /
              (double)callsignTruth.Length,
        maxRealTimeFactor =
          caseReports
            .Cast<CwCorpusCaseReport>()
            .Max(x =>
              x.RealTimeFactor)
      };

      Directory.CreateDirectory(
        Path.GetDirectoryName(
          Path.GetFullPath(
            outputPath))!);

      File.WriteAllText(
        outputPath,
        JsonSerializer.Serialize(
          new
          {
            generatedUtc =
              DateTime.UtcNow,
            deepCwRevision =
              DeepCwModelManager.Revision,
            pipeline =
              "WAV -> Frame Ridge Scanner -> fixed-lag MHT -> Kalman/GNN -> wideband DeepCW -> Incremental Transcript",
            scoring =
              "truth referenceFrequencyHz is used only after decoding to assign output lanes; it is not supplied to detection/tracking",
            summary,
            cases = caseReports
          },
          new JsonSerializerOptions
          {
            WriteIndented = true
          }));

      Console.WriteLine(
        "CW_CORPUS_SUMMARY " +
        JsonSerializer.Serialize(
          summary));

      truthLanes.Should().BeGreaterThan(0);
      double.IsFinite(
          summary.meanCer)
        .Should().BeTrue();
      double.IsFinite(
          summary.meanWer)
        .Should().BeTrue();
      double.IsFinite(
          summary.maxRealTimeFactor)
        .Should().BeTrue();
    }

    private static CwCorpusCaseReport
      RunCase(
        string corpusRoot,
        CwCorpusCase item,
        DeepCwModelMetadata metadata,
        IDeepCwTensorDecoder onnx)
    {
      string path =
        Path.Combine(
          corpusRoot,
          item.Wav);
      RecordedWave wave =
        LoadWave(path);

      if (wave.DurationSeconds <
          item.DecodeWindowSeconds)
        throw new InvalidDataException(
          $"Corpus case '{item.Name}' is only " +
          $"{wave.DurationSeconds:F2}s; decodeWindowSeconds is " +
          $"{item.DecodeWindowSeconds:F2}s.");
      if (item.MaxFrequencyHz >=
          wave.SampleRate / 2.0)
        throw new InvalidDataException(
          $"Corpus case '{item.Name}' maxFrequencyHz exceeds WAV Nyquist.");

      var detectorOptions =
        new CwDetectorOptions
        {
          SampleRate =
            wave.SampleRate,
          MinFrequencyHz =
            item.MinFrequencyHz,
          MaxFrequencyHz =
            item.MaxFrequencyHz,
          MinimumSnrDb =
            item.MinimumSnrDb,
          PeakDeduplicationHz = 8,
          MaxCandidates = 24
        };

      var frontEnd =
        new CwPileupFrontEnd(
          wave.SampleRate,
          item.AnalysisSeconds,
          detectorOptions);

      var decoder =
        new DeepCwMultiLaneDecoder(
          metadata,
          onnx)
        {
          MaxLanes =
            item.MaxDecodeLanes
        };

      var transcripts =
        new CwIncrementalTranscriptCoordinator();

      var frequencyByIdentity =
        new Dictionary<int, List<double>>();

      int trackingHop =
        Math.Max(
          1,
          (int)Math.Round(
            wave.SampleRate *
            0.12));
      int decodeWindow =
        checked((int)Math.Round(
          wave.SampleRate *
          item.DecodeWindowSeconds));
      int decodeHop =
        checked((int)Math.Round(
          wave.SampleRate *
          item.DecodeHopSeconds));
      int nextDecodeEnd =
        decodeWindow;

      var stopwatch =
        Stopwatch.StartNew();

      for (int start = 0;
           start < wave.Samples.Length;
           start += trackingHop)
      {
        int count =
          Math.Min(
            trackingHop,
            wave.Samples.Length -
            start);
        int end =
          start + count;

        float[] block =
          wave.Samples
            .AsSpan(
              start,
              count)
            .ToArray();

        DateTime endUtc =
          Origin +
          TimeSpan.FromSeconds(
            end /
            (double)wave.SampleRate);

        frontEnd.AddSamples(
          block,
          block.Length,
          endUtc);

        IReadOnlyList<CwSignalTrack>
          tracks =
            frontEnd.Analyze(
              item.KnownDopplerRateHzPerSecond);

        if (end < nextDecodeEnd)
          continue;

        int windowStart =
          end -
          decodeWindow;
        if (windowStart < 0)
          continue;

        float[] window =
          wave.Samples
            .AsSpan(
              windowStart,
              decodeWindow)
            .ToArray();

        IReadOnlyList<DeepCwLaneResult>
          lanes =
            decoder.Decode(
              window,
              wave.SampleRate,
              endUtc,
              tracks);

        transcripts.Push(lanes);

        foreach (DeepCwLaneResult lane
          in lanes)
        {
          int identity =
            LaneIdentity(lane);
          if (!frequencyByIdentity
              .TryGetValue(
                identity,
                out List<double>? values))
          {
            values = [];
            frequencyByIdentity[
              identity] = values;
          }

          values.Add(
            lane.InputFrequencyHz);
        }

        while (nextDecodeEnd <= end)
          nextDecodeEnd += decodeHop;
      }

      stopwatch.Stop();

      CwCorpusObservedLane[] observed =
        transcripts.GetAll()
          .Select(snapshot =>
          {
            int identity =
              snapshot.AssociationHintId != 0
                ? snapshot.AssociationHintId
                : -snapshot.TrackId;

            frequencyByIdentity
              .TryGetValue(
                identity,
                out List<double>? values);

            return new CwCorpusObservedLane(
              identity,
              values == null ||
              values.Count == 0
                ? double.NaN
                : Median(values),
              snapshot.Text);
          })
          .Where(x =>
            double.IsFinite(
              x.ReferenceFrequencyHz))
          .OrderBy(x =>
            x.ReferenceFrequencyHz)
          .ToArray();

      Dictionary<int, int> matches =
        MatchByFrequency(
          item.Lanes,
          observed);

      CwCorpusLaneReport[] lanesReport =
        item.Lanes
          .Select((truth, index) =>
          {
            if (!matches.TryGetValue(
                  index,
                  out int observedIndex))
            {
              return new CwCorpusLaneReport(
                truth.Id,
                truth.ReferenceFrequencyHz,
                truth.Text,
                truth.Callsign,
                Matched: false,
                ObservedFrequencyHz:
                  null,
                DecodedText:
                  string.Empty,
                Cer: 1,
                Wer: 1,
                CallsignMatched:
                  false);
            }

            CwCorpusObservedLane actual =
              observed[observedIndex];
            string decoded =
              NormalizeText(
                actual.Text);
            string expected =
              NormalizeText(
                truth.Text);
            string? callsign =
              string.IsNullOrWhiteSpace(
                truth.Callsign)
                ? null
                : NormalizeText(
                  truth.Callsign);

            return new CwCorpusLaneReport(
              truth.Id,
              truth.ReferenceFrequencyHz,
              truth.Text,
              truth.Callsign,
              Matched: true,
              ObservedFrequencyHz:
                actual.ReferenceFrequencyHz,
              DecodedText:
                decoded,
              Cer:
                CharacterErrorRate(
                  expected,
                  decoded),
              Wer:
                WordErrorRate(
                  expected,
                  decoded),
              CallsignMatched:
                callsign == null ||
                decoded.Contains(
                  callsign,
                  StringComparison.Ordinal));
          })
          .ToArray();

      return new CwCorpusCaseReport(
        item.Name,
        item.Wav,
        wave.SampleRate,
        wave.DurationSeconds,
        item.KnownDopplerRateHzPerSecond,
        observed.Length,
        lanesReport,
        stopwatch.Elapsed.TotalSeconds /
          Math.Max(
            wave.DurationSeconds,
            1e-9));
    }

    private static Dictionary<int, int>
      MatchByFrequency(
        IReadOnlyList<CwCorpusTruthLane> truth,
        IReadOnlyList<CwCorpusObservedLane> observed)
    {
      CwCorpusTruthLane[] t =
        truth
          .Select((value, index) =>
            (value, index))
          .OrderBy(x =>
            x.value.ReferenceFrequencyHz)
          .Select(x =>
            x.value)
          .ToArray();

      int[] originalTruthIndex =
        truth
          .Select((value, index) =>
            (value, index))
          .OrderBy(x =>
            x.value.ReferenceFrequencyHz)
          .Select(x =>
            x.index)
          .ToArray();

      CwCorpusObservedLane[] o =
        observed
          .OrderBy(x =>
            x.ReferenceFrequencyHz)
          .ToArray();

      double[,] memo =
        new double[
          t.Length + 1,
          o.Length + 1];
      byte[,] action =
        new byte[
          t.Length + 1,
          o.Length + 1];

      for (int i = 0;
           i <= t.Length;
           i++)
        for (int j = 0;
             j <= o.Length;
             j++)
          memo[i, j] =
            double.NaN;

      double Solve(
        int i,
        int j)
      {
        if (i >= t.Length)
          return 0;
        if (!double.IsNaN(
              memo[i, j]))
          return memo[i, j];

        double unmatchedCost =
          t[i].FrequencyToleranceHz *
          1.25;

        double best =
          unmatchedCost +
          Solve(
            i + 1,
            j);
        byte bestAction = 1;

        if (j < o.Length)
        {
          double skip =
            Solve(
              i,
              j + 1);
          if (skip < best)
          {
            best = skip;
            bestAction = 2;
          }

          double distance =
            Math.Abs(
              t[i].ReferenceFrequencyHz -
              o[j].ReferenceFrequencyHz);
          if (distance <=
              t[i].FrequencyToleranceHz)
          {
            double match =
              distance +
              Solve(
                i + 1,
                j + 1);
            if (match <= best)
            {
              best = match;
              bestAction = 3;
            }
          }
        }

        memo[i, j] = best;
        action[i, j] =
          bestAction;
        return best;
      }

      _ = Solve(0, 0);

      var result =
        new Dictionary<int, int>();
      int ti = 0;
      int oj = 0;

      while (ti < t.Length)
      {
        byte selected =
          action[ti, oj];

        if (selected == 3)
        {
          int originalObservedIndex =
            IndexOfObserved(
              observed,
              o[oj]);
          result[
            originalTruthIndex[ti]] =
              originalObservedIndex;
          ti++;
          oj++;
        }
        else if (selected == 2 &&
                 oj < o.Length)
        {
          oj++;
        }
        else
        {
          ti++;
        }
      }

      return result;
    }

    private static int IndexOfObserved(
      IReadOnlyList<CwCorpusObservedLane> values,
      CwCorpusObservedLane target)
    {
      for (int i = 0;
           i < values.Count;
           i++)
      {
        if (values[i].Identity ==
            target.Identity)
          return i;
      }

      throw new InvalidOperationException(
        "Observed CW lane identity disappeared during scoring.");
    }

    private static int LaneIdentity(
      DeepCwLaneResult lane) =>
      lane.AssociationHintId != 0
        ? lane.AssociationHintId
        : -lane.TrackId;

    private static RecordedWave LoadWave(
      string path)
    {
      using var reader =
        new WaveFileReader(path);
      ISampleProvider provider =
        reader.ToSampleProvider();

      int sampleRate =
        provider.WaveFormat.SampleRate;
      int channels =
        provider.WaveFormat.Channels;

      if (sampleRate < 3000 ||
          sampleRate > 192000 ||
          channels < 1 ||
          channels > 16)
        throw new InvalidDataException(
          $"Unsupported CW corpus WAV format: {sampleRate} Hz, {channels} channels.");

      var samples =
        new List<float>();
      float[] buffer =
        new float[8192 * channels];

      while (true)
      {
        int read =
          provider.Read(
            buffer,
            0,
            buffer.Length);
        if (read <= 0)
          break;

        int frames =
          read / channels;
        for (int frame = 0;
             frame < frames;
             frame++)
        {
          double sum = 0;
          int offset =
            frame * channels;

          for (int channel = 0;
               channel < channels;
               channel++)
            sum +=
              buffer[offset + channel];

          float value =
            (float)(sum / channels);
          if (!float.IsFinite(value))
            value = 0;
          samples.Add(value);
        }
      }

      return new(
        sampleRate,
        samples.ToArray());
    }

    private static double Median(
      IReadOnlyList<double> values)
    {
      double[] sorted =
        values
          .OrderBy(x => x)
          .ToArray();
      int middle =
        sorted.Length / 2;

      return sorted.Length % 2 == 0
        ? (sorted[middle - 1] +
           sorted[middle]) / 2.0
        : sorted[middle];
    }

    private static string NormalizeText(
      string value) =>
      string.Join(
        ' ',
        value
          .ToUpperInvariant()
          .Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries));

    private static double
      CharacterErrorRate(
        string expected,
        string actual) =>
      EditDistance(
        expected.ToCharArray(),
        actual.ToCharArray()) /
      (double)Math.Max(
        1,
        expected.Length);

    private static double WordErrorRate(
      string expected,
      string actual)
    {
      string[] a =
        expected.Split(
          ' ',
          StringSplitOptions.RemoveEmptyEntries);
      string[] b =
        actual.Split(
          ' ',
          StringSplitOptions.RemoveEmptyEntries);

      return EditDistance(
          a,
          b) /
        (double)Math.Max(
          1,
          a.Length);
    }

    private static int EditDistance<T>(
      IReadOnlyList<T> expected,
      IReadOnlyList<T> actual)
      where T : notnull
    {
      int[] previous =
        Enumerable.Range(
          0,
          actual.Count + 1)
        .ToArray();
      int[] current =
        new int[
          actual.Count + 1];

      for (int i = 1;
           i <= expected.Count;
           i++)
      {
        current[0] = i;

        for (int j = 1;
             j <= actual.Count;
             j++)
        {
          int cost =
            EqualityComparer<T>
              .Default
              .Equals(
                expected[i - 1],
                actual[j - 1])
              ? 0
              : 1;

          current[j] =
            Math.Min(
              Math.Min(
                current[j - 1] + 1,
                previous[j] + 1),
              previous[j - 1] +
                cost);
        }

        (previous, current) =
          (current, previous);
      }

      return previous[
        actual.Count];
    }

    private static string RequiredEnvironment(
      string name) =>
      Environment.GetEnvironmentVariable(
        name) ??
      throw new InvalidOperationException(
        $"Required environment variable '{name}' is missing.");

    private readonly record struct
      RecordedWave(
        int SampleRate,
        float[] Samples)
    {
      public double DurationSeconds =>
        Samples.Length /
        (double)SampleRate;
    }

    private readonly record struct
      CwCorpusObservedLane(
        int Identity,
        double ReferenceFrequencyHz,
        string Text);

    public readonly record struct
      CwCorpusLaneReport(
        string Id,
        double ReferenceFrequencyHz,
        string ExpectedText,
        string? Callsign,
        bool Matched,
        double? ObservedFrequencyHz,
        string DecodedText,
        double Cer,
        double Wer,
        bool CallsignMatched);

    public readonly record struct
      CwCorpusCaseReport(
        string Name,
        string Wav,
        int SampleRate,
        double DurationSeconds,
        double KnownDopplerRateHzPerSecond,
        int ObservedLanes,
        CwCorpusLaneReport[] Lanes,
        double RealTimeFactor);
  }
}
