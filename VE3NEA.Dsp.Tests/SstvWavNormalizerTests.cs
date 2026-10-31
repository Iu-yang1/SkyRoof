using System;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvWavNormalizerTests
  {
    [Theory]
    [InlineData(8000, 1)]
    [InlineData(44100, 2)]
    [InlineData(96000, 1)]
    public void DecoderImportPreserves1900HzVisLeaderAcrossSampleRates(
      int sampleRate, int channels)
    {
      string directory = Path.Combine(Path.GetTempPath(),
        $"SkyRoofSstvNormalize-{Guid.NewGuid():N}");
      Directory.CreateDirectory(directory);
      string input = Path.Combine(directory, "input.wav");
      string output = Path.Combine(directory, "decoder.wav");
      try
      {
        using (var wave = new WaveFileWriter(input,
          new WaveFormat(sampleRate, 16, channels)))
        {
          byte[] pcm = new byte[checked(sampleRate * channels * 2)];
          for (int n = 0; n < sampleRate; n++)
          {
            double phase = 2 * Math.PI * 1900 * n / sampleRate;
            short value = (short)Math.Round(.7 * 32767 * Math.Sin(phase));
            for (int channel = 0; channel < channels; channel++)
            {
              int at = (n * channels + channel) * 2;
              pcm[at] = (byte)value;
              pcm[at + 1] = (byte)(value >> 8);
            }
          }
          wave.Write(pcm, 0, pcm.Length);
        }

        Assert.True(SstvWavNormalizer.NeedsConversion(input));
        SstvWavNormalizer.Normalize(input, output);
        Assert.False(SstvWavNormalizer.NeedsConversion(output));

        using var normalized = new WaveFileReader(output);
        Assert.Equal(48000, normalized.WaveFormat.SampleRate);
        Assert.Equal(1, normalized.WaveFormat.Channels);
        Assert.Equal(16, normalized.WaveFormat.BitsPerSample);
        Assert.InRange(normalized.TotalTime.TotalSeconds, .99, 1.01);
        byte[] frame = new byte[480];
        normalized.Position = 48000;
        Assert.Equal(frame.Length, normalized.Read(frame, 0, frame.Length));
        var audio = new float[240];
        for (int i = 0; i < audio.Length; i++)
          audio[i] = (short)(frame[i * 2] | frame[i * 2 + 1] << 8) / 32768f;
        Assert.InRange(SstvVisAfcDetector.EstimatePitch(audio), 1875, 1925);
      }
      finally
      {
        try { Directory.Delete(directory, true); } catch (IOException) { }
      }
    }

    [Fact]
    public void NormalizedPcm48kRequiresNoConversion()
    {
      string file = Path.Combine(Path.GetTempPath(),
        $"SkyRoofNormalize48k-{Guid.NewGuid():N}.wav");
      try
      {
        using (var wave = new WaveFileWriter(file,
          new WaveFormat(48000, 16, 1)))
          wave.Write(new byte[96000], 0, 96000);
        Assert.False(SstvWavNormalizer.NeedsConversion(file));
      }
      finally { try { File.Delete(file); } catch (IOException) { } }
    }
  }
}
