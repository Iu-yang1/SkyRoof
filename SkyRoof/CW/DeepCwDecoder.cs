using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SkyRoof.CW
{
  public readonly record struct DeepCwDecodedSymbol(
    char Character,
    int OutputFrame,
    double Confidence = 1);

  public sealed class DeepCwDecodedText
  {
    public string Text { get; }
    public IReadOnlyList<DeepCwDecodedSymbol> Symbols { get; }
    public int OutputFrameCount { get; }

    public DeepCwDecodedText(
      string text,
      IReadOnlyList<DeepCwDecodedSymbol> symbols,
      int outputFrameCount = 0)
    {
      Text = text;
      Symbols = symbols;
      OutputFrameCount =
        outputFrameCount > 0
          ? outputFrameCount
          : (symbols.Count == 0
              ? 0
              : symbols.Max(x => x.OutputFrame) + 1);
    }
  }

  public static class DeepCwCtcDecoder
  {
    public static DeepCwDecodedText Decode(
      ReadOnlySpan<float> logits,
      IReadOnlyList<int> dimensions,
      DeepCwModelMetadata metadata)
    {
      ArgumentNullException.ThrowIfNull(metadata);
      metadata.Validate();
      if (dimensions.Count != 3 || dimensions[0] != 1)
        throw new ArgumentException(
          "DeepCW output must be [1,time,class].", nameof(dimensions));

      int timeSteps = dimensions[1];
      int classes = dimensions[2];
      if (classes != metadata.NumClasses ||
          logits.Length != timeSteps * classes)
        throw new ArgumentException(
          "DeepCW output dimensions do not match metadata.", nameof(logits));

      var chars = new List<char>();
      var symbols = new List<DeepCwDecodedSymbol>();
      int previous = -1;

      for (int t = 0; t < timeSteps; t++)
      {
        int offset = t * classes;
        int best = 0;
        int second = -1;
        float bestValue = logits[offset];
        float secondValue = float.NegativeInfinity;

        for (int c = 1; c < classes; c++)
        {
          float value = logits[offset + c];
          if (value > bestValue)
          {
            second = best;
            secondValue = bestValue;
            best = c;
            bestValue = value;
          }
          else if (value > secondValue)
          {
            second = c;
            secondValue = value;
          }
        }

        if (second < 0)
          secondValue = bestValue - 20;

        if (best == metadata.BlankIndex)
        {
          previous = -1;
          continue;
        }

        if (best != previous && best < metadata.Chars.Length)
        {
          string token = metadata.Chars[best];
          if (token.Length > 0)
          {
            char ch = token[0];

            // The model output is named log_probs, but we intentionally expose
            // a pairwise top-vs-runner-up confidence rather than claiming this
            // value is a calibrated posterior probability.
            double margin =
              Math.Clamp(
                bestValue - secondValue,
                -30f, 30f);
            double confidence =
              1.0 / (1.0 + Math.Exp(-margin));

            chars.Add(ch);
            symbols.Add(new(
              ch,
              t,
              confidence));
          }
        }

        previous = best;
      }

      return new(
        new string(chars.ToArray()),
        symbols,
        timeSteps);
    }
  }

  public interface IDeepCwTensorDecoder
  {
    DeepCwDecodedText Decode(DeepCwTensor tensor);
  }

  public sealed class DeepCwOnnxDecoder : IDeepCwTensorDecoder, IDisposable
  {
    private readonly InferenceSession session;
    public DeepCwModelMetadata Metadata { get; }

    public DeepCwOnnxDecoder(
      string modelPath,
      DeepCwModelMetadata metadata)
    {
      if (!File.Exists(modelPath))
        throw new FileNotFoundException("DeepCW model not found.", modelPath);
      Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
      Metadata.Validate();

      var options = new SessionOptions
      {
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
      };
      session = new InferenceSession(modelPath, options);

      if (!session.InputMetadata.ContainsKey(Metadata.InputName))
        throw new InvalidDataException(
          $"DeepCW model does not expose input '{Metadata.InputName}'.");
      if (!session.OutputMetadata.ContainsKey(Metadata.OutputName))
        throw new InvalidDataException(
          $"DeepCW model does not expose output '{Metadata.OutputName}'.");
    }

    public static DeepCwOnnxDecoder OpenInstalled()
    {
      if (!DeepCwModelManager.IsInstalled())
        throw new InvalidOperationException(
          "DeepCW model is not installed.");
      return new(
        DeepCwModelManager.GetModelPath(),
        DeepCwModelMetadata.Load(DeepCwModelManager.GetMetadataPath()));
    }

    public DeepCwDecodedText Decode(DeepCwTensor tensor)
    {
      if (tensor.Dimensions.Length != 4 ||
          tensor.Dimensions[0] != 1 ||
          tensor.Dimensions[1] != Metadata.ChannelCount ||
          tensor.Dimensions[3] != Metadata.FrequencyBins ||
          tensor.Data.Length != tensor.Dimensions.Aggregate(1, (a, b) => a * b))
        throw new ArgumentException("DeepCW input tensor shape is invalid.", nameof(tensor));

      var inputTensor = new DenseTensor<float>(
        tensor.Data,
        tensor.Dimensions);

      using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
        session.Run(
        [
          NamedOnnxValue.CreateFromTensor(
            Metadata.InputName, inputTensor)
        ]);

      DisposableNamedOnnxValue output = results.FirstOrDefault(
        item => string.Equals(
          item.Name, Metadata.OutputName, StringComparison.Ordinal))
        ?? throw new InvalidDataException(
          $"DeepCW output '{Metadata.OutputName}' is missing.");

      Tensor<float> resultTensor = output.AsTensor<float>();
      int[] dims = resultTensor.Dimensions.ToArray();
      return DeepCwCtcDecoder.Decode(
        resultTensor.ToArray(), dims, Metadata);
    }

    public void Dispose() => session.Dispose();
  }

  public readonly record struct DeepCwLaneResult(
    int TrackId,
    double InputFrequencyHz,
    string Text,
    bool Active,
    bool Ambiguous,
    DateTime WindowEndUtc,
    int AssociationHintId = 0,
    IReadOnlyList<DeepCwDecodedSymbol>? Symbols = null,
    int OutputFrameCount = 0,
    double WindowDurationSeconds = 0);

  /// <summary>
  /// Runs one calibrated wideband STFT, models activity/interference for every
  /// confirmed track, and invokes DeepCW only for the resource-selected lanes.
  /// Detection bandwidth is therefore independent from the model's 3.2 kHz
  /// sample rate.
  /// </summary>
  public sealed class DeepCwMultiLaneDecoder
  {
    private readonly DeepCwModelMetadata metadata;
    private readonly IDeepCwTensorDecoder decoder;
    private readonly CwCarrierActivityEstimator activityEstimator;
    private readonly CwLaneExtractor laneExtractor;

    public int MaxLanes { get; set; } = 5;
    public double LaneBandwidthHz { get; set; } = 240;
    public double TargetCenterHz { get; set; } = 800;

    /// <summary>
    /// Null preserves the default shared wideband activity-aware mask path.
    /// A non-null backend enables the experimental per-lane time-domain path:
    /// DDC/isolation -> optional denoise -> model-faithful DeepCW frontend.
    /// </summary>
    public ICwAudioDenoiser? LaneDenoiser { get; set; }

    public double DenoiseWet { get; set; } = 1.0;

    public DeepCwMultiLaneDecoder(
      DeepCwModelMetadata metadata,
      IDeepCwTensorDecoder decoder,
      CwCarrierActivityEstimator? activityEstimator = null,
      CwLaneExtractor? laneExtractor = null)
    {
      this.metadata = metadata ??
        throw new ArgumentNullException(nameof(metadata));
      this.decoder = decoder ??
        throw new ArgumentNullException(nameof(decoder));
      this.activityEstimator =
        activityEstimator ?? new CwCarrierActivityEstimator();
      this.laneExtractor =
        laneExtractor ?? new CwLaneExtractor();
      metadata.Validate();
    }

    public IReadOnlyList<DeepCwLaneResult> Decode(
      ReadOnlySpan<float> audio,
      int sourceSampleRate,
      DateTime windowEndUtc,
      IEnumerable<CwSignalTrack> tracks)
    {
      ArgumentNullException.ThrowIfNull(tracks);
      if (windowEndUtc.Kind != DateTimeKind.Utc)
        throw new ArgumentException(
          "CW decode timestamps must be UTC.", nameof(windowEndUtc));
      if (MaxLanes is < 1 or > 8)
        throw new InvalidOperationException("CW lane count must be 1..8.");

      CwSignalTrack[] allDetectedTracks = tracks
        .Where(t => t.Confirmed)
        .Where(t => double.IsFinite(t.FrequencyHz))
        .Where(t => t.FrequencyHz > 0 &&
                    t.FrequencyHz < sourceSampleRate / 2.0)
        .OrderBy(t => t.FrequencyHz)
        .ToArray();

      CwSignalTrack[] decodeSelectedTracks = allDetectedTracks
        .OrderByDescending(t => t.Active)
        .ThenByDescending(t => t.SnrDb)
        .ThenBy(t => t.FrequencyHz)
        .Take(MaxLanes)
        .ToArray();
      if (decodeSelectedTracks.Length == 0)
        return Array.Empty<DeepCwLaneResult>();

      if (!double.IsFinite(DenoiseWet) ||
          DenoiseWet < 0 ||
          DenoiseWet > 1)
        throw new InvalidOperationException(
          "CW denoise wet mix must be in the range 0..1.");

      DeepCwWidebandFeatureWindow? widebandFeatures = null;
      IReadOnlyDictionary<int, float[]>? activityByTrack = null;

      if (LaneDenoiser == null)
      {
        widebandFeatures =
          DeepCwWidebandFeatureWindow.Create(
            audio,
            sourceSampleRate,
            metadata);

        activityByTrack =
          widebandFeatures.EstimateActivities(
            allDetectedTracks,
            activityEstimator);
      }

      // allDetectedTracks is intentionally not truncated to MaxLanes. On the
      // default wideband path, every confirmed station still participates in
      // the competing soft mask even if it is not selected for ONNX.
      var results =
        new List<DeepCwLaneResult>(
          decodeSelectedTracks.Length);

      foreach (CwSignalTrack track
        in decodeSelectedTracks)
      {
        DeepCwTensor tensor;
        double durationSeconds;

        if (LaneDenoiser == null)
        {
          tensor =
            widebandFeatures!.BuildLaneTensor(
              track,
              allDetectedTracks,
              activityByTrack!,
              TargetCenterHz,
              LaneBandwidthHz);
          durationSeconds =
            widebandFeatures.DurationSeconds;
        }
        else
        {
          CwLaneSignal lane =
            laneExtractor.Extract(
              audio,
              sourceSampleRate,
              track,
              LaneDenoiser.SampleRate,
              TargetCenterHz);

          float[] processed =
            LaneDenoiser.Process(
              lane.Audio,
              lane.SampleRate,
              DenoiseWet);

          DeepCwFeatureWindow laneFeatures =
            DeepCwFeatureWindow.Create(
              processed,
              LaneDenoiser.SampleRate,
              metadata);

          tensor =
            laneFeatures.BuildStandardTensor();
          durationSeconds =
            laneFeatures.DurationSeconds;
        }

        DeepCwDecodedText text =
          decoder.Decode(tensor);

        results.Add(new(
          track.Id,
          track.FrequencyHz,
          text.Text,
          track.Active,
          track.Ambiguous,
          windowEndUtc,
          track.AssociationHintId,
          text.Symbols,
          text.OutputFrameCount,
          durationSeconds));
      }

      return results;
    }
  }
}
