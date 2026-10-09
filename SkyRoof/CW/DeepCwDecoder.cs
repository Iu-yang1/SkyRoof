using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SkyRoof.CW
{
  public readonly record struct DeepCwDecodedSymbol(
    char Character,
    int OutputFrame);

  public sealed class DeepCwDecodedText
  {
    public string Text { get; }
    public IReadOnlyList<DeepCwDecodedSymbol> Symbols { get; }

    public DeepCwDecodedText(
      string text,
      IReadOnlyList<DeepCwDecodedSymbol> symbols)
    {
      Text = text;
      Symbols = symbols;
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
        float bestValue = logits[offset];
        for (int c = 1; c < classes; c++)
        {
          if (logits[offset + c] <= bestValue) continue;
          best = c;
          bestValue = logits[offset + c];
        }

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
            chars.Add(ch);
            symbols.Add(new(ch, t));
          }
        }

        previous = best;
      }

      return new(new string(chars.ToArray()), symbols);
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
    DateTime WindowEndUtc);

  /// <summary>
  /// Runs one shared FFT/resampling pass and then an independent DeepCW tensor
  /// + CTC inference for every selected Pileup lane.
  /// </summary>
  public sealed class DeepCwMultiLaneDecoder
  {
    private readonly DeepCwModelMetadata metadata;
    private readonly IDeepCwTensorDecoder decoder;

    public int MaxLanes { get; set; } = 5;
    public double LaneBandwidthHz { get; set; } = 180;
    public double TargetCenterHz { get; set; } = 800;

    public DeepCwMultiLaneDecoder(
      DeepCwModelMetadata metadata,
      IDeepCwTensorDecoder decoder)
    {
      this.metadata = metadata ??
        throw new ArgumentNullException(nameof(metadata));
      this.decoder = decoder ??
        throw new ArgumentNullException(nameof(decoder));
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

      var selected = tracks
        .Where(t => t.Confirmed)
        .OrderByDescending(t => t.Active)
        .ThenByDescending(t => t.SnrDb)
        .ThenBy(t => t.FrequencyHz)
        .Take(MaxLanes)
        .ToArray();
      if (selected.Length == 0)
        return Array.Empty<DeepCwLaneResult>();

      DeepCwFeatureWindow features =
        DeepCwFeatureWindow.Create(audio, sourceSampleRate, metadata);
      var results = new List<DeepCwLaneResult>(selected.Length);
      foreach (CwSignalTrack track in selected)
      {
        if (track.FrequencyHz <= 0 ||
            track.FrequencyHz >= metadata.SampleRate / 2.0)
          continue;

        DeepCwTensor tensor = features.BuildSeparatedLaneTensor(
          track, selected, LaneBandwidthHz, TargetCenterHz);
        DeepCwDecodedText text = decoder.Decode(tensor);
        results.Add(new(
          track.Id,
          track.FrequencyHz,
          text.Text,
          track.Active,
          track.Ambiguous,
          windowEndUtc));
      }

      return results;
    }
  }
}
