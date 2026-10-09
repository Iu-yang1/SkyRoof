using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class DeepCwPipelineTests
  {
    private const int SourceRate = 48000;
    private static readonly DateTime T0 =
      new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    private const string MetadataJson = """
{
  "chars": [",",".","/","0","1","2","3","4","5","6","7","8","9","?","A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z"," "],
  "blank_index": 41,
  "sample_rate": 3200,
  "fft_length": 256,
  "hop_length": 48,
  "spectrogram_min_freq_hz": 400.0,
  "spectrogram_max_freq_hz": 1200.0,
  "spectrogram_frequency_bins": 65,
  "normalization": "log1p",
  "onnx_input_name": "spectrogram",
  "onnx_output_name": "log_probs",
  "onnx_input_layout": ["batch","channel","time","frequency"],
  "onnx_output_layout": ["batch","time","class"],
  "channel_count": 1,
  "num_classes": 42,
  "onnx_input_dtype": "float32",
  "onnx_output_dtype": "float32"
}
""";

    [Fact]
    public void Metadata_ParsesPublishedDeepCwContract()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);

      metadata.SampleRate.Should().Be(3200);
      metadata.FftLength.Should().Be(256);
      metadata.HopLength.Should().Be(48);
      metadata.FrequencyBins.Should().Be(65);
      metadata.BlankIndex.Should().Be(41);
      metadata.Chars.Should().HaveCount(41);
    }

    [Fact]
    public void CtcDecoder_CollapsesRepeatsAndBlankSeparatesSymbols()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);
      int c = Array.IndexOf(metadata.Chars, "C");
      int q = Array.IndexOf(metadata.Chars, "Q");
      int space = Array.IndexOf(metadata.Chars, " ");
      int blank = metadata.BlankIndex;
      int[] path = [c, c, blank, q, q, blank, space, space, blank, c];

      float[] logits = Enumerable.Repeat(
        -10f, path.Length * metadata.NumClasses).ToArray();
      for (int t = 0; t < path.Length; t++)
        logits[t * metadata.NumClasses + path[t]] = 10f;

      DeepCwDecodedText decoded = DeepCwCtcDecoder.Decode(
        logits, [1, path.Length, metadata.NumClasses], metadata);

      decoded.Text.Should().Be("CQ C");
      decoded.Symbols.Select(x => x.Character)
        .Should().Equal('C', 'Q', ' ', 'C');
    }

    [Fact]
    public void FeatureWindow_IsolatesDifferentPileupLanesAtModelCenter()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);
      float[] audio = MakeKeyedAudio(
        6.0,
        (620, 0.34f, 0.14, 0.50),
        (980, 0.22f, 0.18, 0.46));

      DeepCwFeatureWindow features =
        DeepCwFeatureWindow.Create(audio, SourceRate, metadata);
      DeepCwTensor lane620 = features.BuildLaneTensor(620);
      DeepCwTensor lane980 = features.BuildLaneTensor(980);
      DeepCwTensor emptyLane = features.BuildLaneTensor(1350);

      int center = (int)Math.Round(
        (800 - metadata.MinFrequencyHz) /
        (metadata.SampleRate / (double)metadata.FftLength));

      StrongestBin(lane620).Should().BeInRange(center - 2, center + 2);
      StrongestBin(lane980).Should().BeInRange(center - 2, center + 2);

      MeanBin(lane620, center).Should()
        .BeGreaterThan(MeanBin(emptyLane, center) * 3);
      MeanBin(lane980, center).Should()
        .BeGreaterThan(MeanBin(emptyLane, center) * 3);
    }


    [Fact]
    public void RidgeSoftMask_SuppressesStrongerNeighborInWeakLane()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);
      float[] audio = MakeKeyedAudio(
        6.0,
        (700, 0.40f, 0.14, 0.50),
        (740, 0.15f, 0.17, 0.47));

      DeepCwFeatureWindow features =
        DeepCwFeatureWindow.Create(audio, SourceRate, metadata);
      CwSignalTrack strong = Track(1, 700, 18);
      CwSignalTrack weak = Track(2, 740, 7);
      CwSignalTrack[] tracks = [strong, weak];

      DeepCwTensor weakLane = features.BuildSeparatedLaneTensor(
        weak, tracks, bandwidthHz: 180, targetCenterHz: 800,
        maskFloor: 0.05, maskSigmaHz: 24);

      double binHz = metadata.SampleRate /
        (double)metadata.FftLength;
      int center = (int)Math.Round(
        (800 - metadata.MinFrequencyHz) / binHz);
      int strongLeakBin = center -
        (int)Math.Round((740 - 700) / binHz);

      // Despite the neighboring carrier being ~8.5 dB stronger, the weak
      // lane's translated ridge should dominate its own separated tensor.
      MeanBin(weakLane, center).Should()
        .BeGreaterThan(MeanBin(weakLane, strongLeakBin));
    }

    [Fact]
    public void WidebandFrontend_IsCalibratedToModelRateReference()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);
      float[] audio = MakeKeyedAudio(
        2.0, (800, 0.35f, 0.14, 0.52));

      DeepCwWidebandFeatureWindow wide =
        DeepCwWidebandFeatureWindow.Create(
          audio, SourceRate, metadata);
      DeepCwTensor wideTensor =
        wide.BuildUnmaskedLaneTensor(Track(1, 800, 15));

      float[] modelRate = CwWindowedSincResampler.Resample(
        audio, SourceRate, metadata.SampleRate);
      DeepCwTensor reference =
        DeepCwFeatureWindow.Create(
          modelRate, metadata.SampleRate, metadata)
        .BuildStandardTensor();

      wideTensor.Dimensions.Should().Equal(reference.Dimensions);
      int center = (int)Math.Round(
        (800 - metadata.MinFrequencyHz) /
        (metadata.SampleRate / (double)metadata.FftLength));

      double a = MeanBin(wideTensor, center);
      double b = MeanBin(reference, center);
      Math.Abs(a - b) / Math.Max(b, 1e-9)
        .Should().BeLessThan(0.18);
    }

    [Fact]
    public void WidebandFrontend_PreservesLaneAboveModelNyquist()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);
      float[] audio = MakeKeyedAudio(
        2.0, (2600, 0.32f, 0.15, 0.52));

      DeepCwWidebandFeatureWindow wide =
        DeepCwWidebandFeatureWindow.Create(
          audio, SourceRate, metadata);
      DeepCwTensor lane =
        wide.BuildUnmaskedLaneTensor(Track(7, 2600, 14));

      int center = (int)Math.Round(
        (800 - metadata.MinFrequencyHz) /
        (metadata.SampleRate / (double)metadata.FftLength));
      StrongestBin(lane).Should().BeInRange(
        center - 1, center + 1);
      MeanBin(lane, center).Should().BeGreaterThan(0.25);
    }

    [Fact]
    public void CarrierActivityHmm_SeparatesKeyDownAndKeyUpFrames()
    {
      var estimator = new CwCarrierActivityEstimator();
      float[] ridge = Enumerable.Repeat(0.01f, 20)
        .Concat(Enumerable.Repeat(1.0f, 24))
        .Concat(Enumerable.Repeat(0.01f, 24))
        .ToArray();

      float[] p = estimator.EstimateFrameProbabilities(
        ridge, 0.015, 0.06);

      p.Skip(26).Take(10).Average()
        .Should().BeGreaterThan(0.75);
      p.TakeLast(8).Average()
        .Should().BeLessThan(0.25);
    }

    [Fact]
    public void ActivityAwareMask_DoesNotLetKeyUpTrackStealEnergy()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);
      float[] audio = MakeKeyedAudio(
        2.0,
        (800, 0.30f, 0.14, 0.50),
        (840, 0.26f, 0.14, 0.50));
      DeepCwWidebandFeatureWindow wide =
        DeepCwWidebandFeatureWindow.Create(
          audio, SourceRate, metadata);

      CwSignalTrack target = Track(1, 800, 16);
      CwSignalTrack neighbor = Track(2, 840, 14);
      CwSignalTrack[] tracks = [target, neighbor];
      int frames = wide.FrameCount;

      var neighborOff = new Dictionary<int, float[]>
      {
        [target.Id] = Enumerable.Repeat(1f, frames).ToArray(),
        [neighbor.Id] = new float[frames]
      };
      var neighborOn = new Dictionary<int, float[]>
      {
        [target.Id] = Enumerable.Repeat(1f, frames).ToArray(),
        [neighbor.Id] = Enumerable.Repeat(1f, frames).ToArray()
      };

      DeepCwTensor offTensor = wide.BuildLaneTensor(
        target, tracks, neighborOff);
      DeepCwTensor onTensor = wide.BuildLaneTensor(
        target, tracks, neighborOn);

      int center = (int)Math.Round(
        (800 - metadata.MinFrequencyHz) /
        (metadata.SampleRate / (double)metadata.FftLength));
      MeanBin(offTensor, center).Should()
        .BeGreaterThan(MeanBin(onTensor, center));
    }

    [Fact]
    public void UnselectedTrack_StillParticipatesInInterferenceMask()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);
      float[] audio = MakeKeyedAudio(
        2.0,
        (800, 0.32f, 0.14, 0.50),
        (840, 0.25f, 0.14, 0.50));

      CwSignalTrack target = Track(1, 800, 20);
      CwSignalTrack neighbor = Track(2, 840, 10);

      var withNeighborCapture = new CapturingTensorDecoder("A");
      var withNeighbor =
        new DeepCwMultiLaneDecoder(metadata, withNeighborCapture)
        {
          MaxLanes = 1
        };
      withNeighbor.Decode(
        audio, SourceRate, T0, [target, neighbor]);

      var targetOnlyCapture = new CapturingTensorDecoder("A");
      var targetOnly =
        new DeepCwMultiLaneDecoder(metadata, targetOnlyCapture)
        {
          MaxLanes = 1
        };
      targetOnly.Decode(
        audio, SourceRate, T0, [target]);

      withNeighborCapture.Tensors.Should().ContainSingle();
      targetOnlyCapture.Tensors.Should().ContainSingle();

      int neighborBin = (int)Math.Round(
        (840 - metadata.MinFrequencyHz) /
        (metadata.SampleRate / (double)metadata.FftLength));
      MeanBin(withNeighborCapture.Tensors[0], neighborBin)
        .Should().BeLessThan(
          MeanBin(targetOnlyCapture.Tensors[0], neighborBin));
    }

    [Fact]
    public void WindowedSincResampler_PreservesLowFrequencyTone()
    {
      float[] input = MakeKeyedAudio(
        2.0, (800, 0.4f, 0.12, 0.55));
      float[] output = CwWindowedSincResampler.Resample(
        input, SourceRate, 3200);

      output.Length.Should().Be(6400);
      Rms(output).Should().BeGreaterThan(0.05);
    }

    [Fact]
    public void MultiLaneDecoder_InvokesIndependentInferenceForSelectedTracks()
    {
      DeepCwModelMetadata metadata =
        DeepCwModelMetadata.Parse(MetadataJson);
      var fake = new QueueTensorDecoder("HIGH", "MID");
      var decoder = new DeepCwMultiLaneDecoder(metadata, fake)
      {
        MaxLanes = 2
      };
      float[] audio = MakeKeyedAudio(
        1.5,
        (650, 0.3f, 0.14, 0.5),
        (850, 0.2f, 0.16, 0.5),
        (1050, 0.1f, 0.18, 0.5));

      CwSignalTrack[] tracks =
      [
        Track(1, 650, 8),
        Track(2, 850, 15),
        Track(3, 1050, 3)
      ];

      IReadOnlyList<DeepCwLaneResult> result =
        decoder.Decode(audio, SourceRate, T0, tracks);

      result.Should().HaveCount(2);
      result.Select(x => x.TrackId).Should().Equal(2, 1);
      result.Select(x => x.Text).Should().Equal("HIGH", "MID");
      fake.Calls.Should().Be(2);
    }

    [Fact]
    public void ModelManager_PinsImmutableDeepCwRevision()
    {
      DeepCwModelManager.Revision.Should()
        .Be("8e264d243bbd4467bd19f3f28292219405b47e0e");
      DeepCwModelManager.ModelUrl.Should().Contain(
        DeepCwModelManager.Revision);
      DeepCwModelManager.MetadataUrl.Should().Contain(
        DeepCwModelManager.Revision);
    }

    private static CwSignalTrack Track(
      int id, double frequency, double snr) =>
      new(
        id, frequency, snr, 0,
        T0.AddSeconds(-2), T0,
        Confirmed: true,
        Active: true);

    private sealed class CapturingTensorDecoder :
      IDeepCwTensorDecoder
    {
      private readonly string text;
      public List<DeepCwTensor> Tensors { get; } = [];

      public CapturingTensorDecoder(string text) =>
        this.text = text;

      public DeepCwDecodedText Decode(DeepCwTensor tensor)
      {
        Tensors.Add(tensor);
        return new(
          text,
          Array.Empty<DeepCwDecodedSymbol>());
      }
    }

    private sealed class QueueTensorDecoder : IDeepCwTensorDecoder
    {
      private readonly Queue<string> texts;
      public int Calls { get; private set; }

      public QueueTensorDecoder(params string[] texts) =>
        this.texts = new(texts);

      public DeepCwDecodedText Decode(DeepCwTensor tensor)
      {
        Calls++;
        tensor.Dimensions.Should().HaveCount(4);
        return new(
          texts.Count > 0 ? texts.Dequeue() : "",
          Array.Empty<DeepCwDecodedSymbol>());
      }
    }

    private static int StrongestBin(DeepCwTensor tensor)
    {
      int bins = tensor.FrequencyBins;
      double best = double.NegativeInfinity;
      int index = -1;
      for (int b = 0; b < bins; b++)
      {
        double mean = MeanBin(tensor, b);
        if (mean <= best) continue;
        best = mean;
        index = b;
      }
      return index;
    }

    private static double MeanBin(DeepCwTensor tensor, int bin)
    {
      int bins = tensor.FrequencyBins;
      double sum = 0;
      for (int t = 0; t < tensor.TimeSteps; t++)
        sum += tensor.Data[t * bins + bin];
      return sum / Math.Max(1, tensor.TimeSteps);
    }

    private static double Rms(float[] data)
    {
      double sum = 0;
      foreach (float value in data) sum += value * value;
      return Math.Sqrt(sum / data.Length);
    }

    private static float[] MakeKeyedAudio(
      double seconds,
      params (double Hz, float Amplitude, double KeyPeriod, double Duty)[] tones)
    {
      int count = (int)Math.Round(seconds * SourceRate);
      var random = new Random(34567);
      var data = new float[count];

      for (int n = 0; n < count; n++)
      {
        double t = n / (double)SourceRate;
        float sample = 0.003f *
          (float)(2 * random.NextDouble() - 1);
        foreach (var tone in tones)
        {
          if ((t % tone.KeyPeriod) < tone.KeyPeriod * tone.Duty)
            sample += tone.Amplitude *
              (float)Math.Sin(2 * Math.PI * tone.Hz * t);
        }
        data[n] = sample;
      }

      return data;
    }
  }
}
