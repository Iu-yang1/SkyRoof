using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// User-facing SSTV WAV input adapter. Transcodes common 8/11.025/22.05/
  /// 44.1/48/96-kHz mono/stereo PCM or IEEE-float WAV to the decoder's
  /// 48-kHz signed 16-bit mono stream. No audio playback or CAT/PTT.
  /// </summary>
  public static class SstvWavNormalizer
  {
    public const int DecoderRate = 48000;
    public const double MaxSeconds = 550;

    public static bool NeedsConversion(string file)
    {
      using var reader = new WaveFileReader(file);
      Validate(reader);
      return reader.WaveFormat.SampleRate != DecoderRate ||
        reader.WaveFormat.Channels != 1 ||
        reader.WaveFormat.BitsPerSample != 16 ||
        reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm;
    }

    public static void Normalize(string inputPath, string outputPath,
      CancellationToken cancellationToken = default)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
      ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
      string sourcePath = Path.GetFullPath(inputPath);
      string destPath = Path.GetFullPath(outputPath);
      if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("SSTV WAV source and destination must differ.");

      using var reader = new WaveFileReader(sourcePath);
      Validate(reader);

      ISampleProvider samples = reader.ToSampleProvider();
      if (reader.WaveFormat.Channels == 2)
      {
        samples = new StereoToMonoSampleProvider(samples)
        {
          LeftVolume = 0.5f,
          RightVolume = 0.5f
        };
      }
      if (samples.WaveFormat.SampleRate != DecoderRate)
        samples = new WdlResamplingSampleProvider(samples, DecoderRate);
      if (samples.WaveFormat.Channels != 1)
        throw new InvalidDataException("无法将录音转换成单声道 SSTV 音频。");

      Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
      // File is created only after all input constraints have been checked.
      using var writer = new WaveFileWriter(destPath,
        new WaveFormat(DecoderRate, 16, 1));
      var asPcm = new SampleToWaveProvider16(samples);
      byte[] output = new byte[16384];
      int count;
      while ((count = asPcm.Read(output, 0, output.Length)) > 0)
      {
        cancellationToken.ThrowIfCancellationRequested();
        writer.Write(output, 0, count);
      }
      cancellationToken.ThrowIfCancellationRequested();
      writer.Flush();
    }

    private static void Validate(WaveFileReader source)
    {
      WaveFormat fmt = source.WaveFormat;
      if (fmt.SampleRate < 8000 || fmt.SampleRate > 192000 ||
          fmt.Channels is < 1 or > 2 ||
          fmt.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) ||
          fmt.BitsPerSample is not (8 or 16 or 24 or 32 or 64) ||
          source.TotalTime.TotalSeconds is < .5 or > MaxSeconds)
        throw new InvalidDataException(
          "请选择 8–192 kHz、单/双声道、PCM/浮点 WAV，时长不超过 550 秒。");
    }
  }
}
