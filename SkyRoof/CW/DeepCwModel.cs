using System.Text.Json;
using System.Text.Json.Serialization;
using VE3NEA;

namespace SkyRoof.CW
{
  public sealed class DeepCwModelMetadata
  {
    [JsonPropertyName("chars")] public string[] Chars { get; set; } = [];
    [JsonPropertyName("blank_index")] public int BlankIndex { get; set; }
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; }
    [JsonPropertyName("fft_length")] public int FftLength { get; set; }
    [JsonPropertyName("hop_length")] public int HopLength { get; set; }
    [JsonPropertyName("spectrogram_min_freq_hz")] public double MinFrequencyHz { get; set; }
    [JsonPropertyName("spectrogram_max_freq_hz")] public double MaxFrequencyHz { get; set; }
    [JsonPropertyName("spectrogram_frequency_bins")] public int FrequencyBins { get; set; }
    [JsonPropertyName("normalization")] public string Normalization { get; set; } = "";
    [JsonPropertyName("onnx_input_name")] public string InputName { get; set; } = "";
    [JsonPropertyName("onnx_output_name")] public string OutputName { get; set; } = "";
    [JsonPropertyName("onnx_input_layout")] public string[] InputLayout { get; set; } = [];
    [JsonPropertyName("onnx_output_layout")] public string[] OutputLayout { get; set; } = [];
    [JsonPropertyName("channel_count")] public int ChannelCount { get; set; }
    [JsonPropertyName("num_classes")] public int NumClasses { get; set; }
    [JsonPropertyName("onnx_input_dtype")] public string InputDtype { get; set; } = "";
    [JsonPropertyName("onnx_output_dtype")] public string OutputDtype { get; set; } = "";

    public static DeepCwModelMetadata Load(string path) =>
      Parse(File.ReadAllText(path));

    public static DeepCwModelMetadata Parse(string json)
    {
      DeepCwModelMetadata value =
        JsonSerializer.Deserialize<DeepCwModelMetadata>(json) ??
        throw new InvalidDataException("DeepCW metadata is empty.");
      value.Validate();
      return value;
    }

    public void Validate()
    {
      if (Chars.Length == 0 || BlankIndex < 0 ||
          BlankIndex >= NumClasses || Chars.Length > NumClasses)
        throw new InvalidDataException("DeepCW vocabulary/blank metadata is invalid.");
      if (SampleRate < 1000 || FftLength < 64 || HopLength < 1 ||
          HopLength > FftLength)
        throw new InvalidDataException("DeepCW sample-rate/FFT metadata is invalid.");
      if (MinFrequencyHz < 0 || MaxFrequencyHz <= MinFrequencyHz ||
          MaxFrequencyHz >= SampleRate / 2.0)
        throw new InvalidDataException("DeepCW frequency range is invalid.");
      double binHz = SampleRate / (double)FftLength;
      int first = (int)Math.Ceiling(MinFrequencyHz / binHz);
      int stop = (int)Math.Floor(MaxFrequencyHz / binHz) + 1;
      if (stop - first != FrequencyBins)
        throw new InvalidDataException("DeepCW frequency-bin metadata is inconsistent.");
      if (!string.Equals(Normalization, "log1p", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Unsupported DeepCW normalization.");
      if (ChannelCount != 1 || NumClasses <= Chars.Length ||
          !string.Equals(InputDtype, "float32", StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(OutputDtype, "float32", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Unsupported DeepCW tensor metadata.");
      if (InputLayout.Length != 4 ||
          !InputLayout.SequenceEqual(["batch", "channel", "time", "frequency"]) ||
          OutputLayout.Length != 3 ||
          !OutputLayout.SequenceEqual(["batch", "time", "class"]))
        throw new InvalidDataException("Unsupported DeepCW tensor layout.");
      if (string.IsNullOrWhiteSpace(InputName) || string.IsNullOrWhiteSpace(OutputName))
        throw new InvalidDataException("DeepCW ONNX tensor names are missing.");
    }
  }

  public readonly record struct DeepCwInstallProgress(
    string Stage, long BytesReceived, long? TotalBytes);

  public static class DeepCwModelManager
  {
    public const string Revision = "8e264d243bbd4467bd19f3f28292219405b47e0e";
    public const string ModelFileName = "model.onnx";
    public const string MetadataFileName = "model.onnx.json";
    public static string ModelUrl =>
      $"https://raw.githubusercontent.com/e04/deepcw-engine/{Revision}/{ModelFileName}";
    public static string MetadataUrl =>
      $"https://raw.githubusercontent.com/e04/deepcw-engine/{Revision}/{MetadataFileName}";

    public static string GetDirectory() =>
      Path.Combine(Utils.GetUserDataFolder(), "cwmodel");

    public static string GetModelPath() =>
      Path.Combine(GetDirectory(), ModelFileName);

    public static string GetMetadataPath() =>
      Path.Combine(GetDirectory(), MetadataFileName);

    public static bool IsInstalled()
    {
      try
      {
        if (!File.Exists(GetModelPath()) || !File.Exists(GetMetadataPath()))
          return false;
        if (new FileInfo(GetModelPath()).Length < 10_000_000)
          return false;
        DeepCwModelMetadata.Load(GetMetadataPath());
        return true;
      }
      catch
      {
        return false;
      }
    }

    public static async Task InstallAsync(
      IProgress<DeepCwInstallProgress>? progress = null,
      CancellationToken cancellationToken = default)
    {
      Directory.CreateDirectory(GetDirectory());
      string metadataTemp = GetMetadataPath() + ".tmp";
      string modelTemp = GetModelPath() + ".tmp";

      using var client = new HttpClient
      {
        Timeout = TimeSpan.FromMinutes(5)
      };
      client.DefaultRequestHeaders.UserAgent.ParseAdd("SkyRoof-DeepCW/1.0");

      try
      {
        await DownloadAsync(client, MetadataUrl, metadataTemp, "metadata",
          progress, cancellationToken);
        DeepCwModelMetadata.Load(metadataTemp);

        await DownloadAsync(client, ModelUrl, modelTemp, "model",
          progress, cancellationToken);
        if (new FileInfo(modelTemp).Length < 10_000_000)
          throw new InvalidDataException("Downloaded DeepCW model is unexpectedly small.");

        File.Move(metadataTemp, GetMetadataPath(), true);
        File.Move(modelTemp, GetModelPath(), true);
      }
      finally
      {
        TryDelete(metadataTemp);
        TryDelete(modelTemp);
      }
    }

    private static async Task DownloadAsync(
      HttpClient client, string url, string destination, string stage,
      IProgress<DeepCwInstallProgress>? progress,
      CancellationToken cancellationToken)
    {
      using HttpResponseMessage response = await client.GetAsync(
        url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      response.EnsureSuccessStatusCode();
      long? total = response.Content.Headers.ContentLength;
      await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
      await using var output = new FileStream(
        destination, FileMode.Create, FileAccess.Write, FileShare.None,
        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

      byte[] buffer = new byte[128 * 1024];
      long received = 0;
      while (true)
      {
        int count = await input.ReadAsync(buffer, cancellationToken);
        if (count == 0) break;
        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        received += count;
        progress?.Report(new(stage, received, total));
      }
      await output.FlushAsync(cancellationToken);
    }

    private static void TryDelete(string path)
    {
      try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
  }
}
