using System;
using System.Drawing;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvImageSlantCorrectionTests
  {
    [Fact]
    public void ResampledToneRasterPreservesEndpointsAndTransition()
    {
      float[] input = { 1500, 1500, 1500, 2300, 2300, 2300 };
      float[] result = SstvOfflineRasterDecoder.ResampleFrequencyLine(input, 12);
      Assert.Equal(12, result.Length);
      Assert.InRange(result[0], 1499, 1501);
      Assert.InRange(result[^1], 2299, 2301);
      Assert.True(result[5] < result[6]);
      Assert.All(result, value => Assert.InRange(value, 1499, 2301));
    }

    [Fact]
    public void RejectsImpossibleClockCorrections()
    {
      var mode = Assert.Single(SstvTxModeProfile.All,
        m => m.Name == "Robot BW 8");
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        SstvOfflineRasterDecoder.ResampleFrequencyLine(
          new float[] { 1200, 1300 }, -1));
    }

    [Fact]
    public void AdjustingCaptureClockRestoresRisingRasterEdgeAfterPpmDrift()
    {
      var mode = Assert.Single(SstvTxModeProfile.All,
        m => m.Name == "Robot BW 8");
      string directory = Path.Combine(Path.GetTempPath(),
        "SstvSlant-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      string clean = Path.Combine(directory, "reference.wav");
      string drifted = Path.Combine(directory, "drift.wav");
      try
      {
        using (var img = new Bitmap(320, 240))
        {
          using (var g = Graphics.FromImage(img))
          {
            g.Clear(Color.Black);
            g.FillRectangle(Brushes.White, 160, 0, 160, 240);
          }
          SstvRgbWaveExporter.Export(img, mode, clean);
        }

        using (var reader = new WaveFileReader(clean))
        using (var writer = new WaveFileWriter(drifted,
          new WaveFormat(48000, 16, 1)))
        {
          var pcm = new byte[checked((int)reader.Length)];
          int total = 0, count;
          while (total < pcm.Length &&
                 (count = reader.Read(pcm, total, pcm.Length - total)) > 0)
            total += count;
          const double stretch = 1.001;
          int samples = total / 2;
          int outputSamples = (int)Math.Round(samples * stretch);
          var output = new byte[outputSamples * 2];
          for (int i = 0; i < outputSamples; i++)
          {
            double at = i / stretch;
            int left = Math.Clamp((int)at, 0, samples - 1);
            int right = Math.Min(left + 1, samples - 1);
            double frac = at - left;
            int lo = left * 2, hi = right * 2;
            short a = (short)(pcm[lo] | pcm[lo + 1] << 8);
            short b = (short)(pcm[hi] | pcm[hi + 1] << 8);
            short value = (short)Math.Round(a * (1 - frac) + b * frac);
            output[i * 2] = (byte)value;
            output[i * 2 + 1] = (byte)(value >> 8);
          }
          writer.Write(output, 0, output.Length);
        }

        using var uncorrected = SstvOfflineRasterDecoder.Decode(
          drifted, mode);
        using var corrected = SstvOfflineRasterDecoder.Decode(
          drifted, mode, sampleClockPpm: 1000);

        static int FindEdge(Bitmap bitmap)
        {
          int y = bitmap.Height - 12;
          for (int x = 24; x < bitmap.Width - 24; x++)
            if (bitmap.GetPixel(x, y).R >= 140)
              return x;
          return -1;
        }
        int originalEdge = FindEdge(uncorrected);
        int repairedEdge = FindEdge(corrected);
        Assert.InRange(repairedEdge, 143, 179);
        Assert.True(originalEdge < 0 ||
          Math.Abs(repairedEdge - 160) + 10 < Math.Abs(originalEdge - 160),
          $"Raster edge uncorrected/corrected: {originalEdge}/{repairedEdge}");
      }
      finally
      {
        try { Directory.Delete(directory, true); } catch (IOException) { }
      }
    }
  }
}
