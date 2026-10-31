using System;
using System.Drawing;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvLiveRasterReceiverTests
  {
    [Theory]
    [InlineData("SC2 60")]
    [InlineData("MP73")]
    [InlineData("MP73-N")]
    public void LiveAfReceiverRestoresImageAfterStandardExtendedOrNvis(
      string modeName)
    {
      var mode = Assert.Single(SstvTxModeProfile.All, x => x.Name == modeName);
      string file = Path.Combine(Path.GetTempPath(),
        "SkyRoofLiveSstv-" + Guid.NewGuid().ToString("N") + ".wav");
      try
      {
        using (var source = new Bitmap(40, 32))
        {
          using (Graphics g = Graphics.FromImage(source))
          {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 0, 0, 20, 32);
          }
          SstvRgbWaveExporter.Export(source, mode, file);
        }

        using var receiver = new SstvLiveRasterReceiver();
        Bitmap? result = null;
        int completed = 0, previews = 0;
        receiver.ImageReady += (picture, name, final, offset) =>
        {
          Assert.Equal(mode.Name, name);
          Assert.InRange(offset, -20, 20);
          previews++;
          if (final)
          {
            result = new Bitmap(picture);
            completed++;
          }
          picture.Dispose();
        };

        using (var wave = new WaveFileReader(file))
        {
          byte[] pcm = new byte[4096];
          int length;
          while ((length = wave.Read(pcm, 0, pcm.Length)) > 0)
          {
            float[] samples = new float[length / 2];
            for (int i = 0; i < samples.Length; i++)
              samples[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768f;
            receiver.Process(samples);
          }
        }

        Assert.Equal(1, completed);
        Assert.True(previews >= 2);
        using (result)
        {
          Assert.NotNull(result);
          Assert.Equal(mode.Width, result!.Width);
          Assert.Equal(mode.Height, result.Height);
          Color left = result.GetPixel(mode.Width / 5, mode.Height / 2);
          Color right = result.GetPixel(mode.Width * 4 / 5, mode.Height / 2);
          Assert.True(left.R + 70 < right.R,
            $"Mode {mode.Name}: decoded dark/bright pixels {left.R}/{right.R}");
        }
      }
      finally
      {
        try { File.Delete(file); } catch (IOException) { }
      }
    }
  }
}
