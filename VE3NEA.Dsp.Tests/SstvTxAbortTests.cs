using System;
using System.Drawing;
using System.IO;
using System.Threading;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvTxAbortTests
  {
    [Theory]
    [InlineData("Robot 36")]
    [InlineData("Martin 1")]
    [InlineData("MP73")]
    [InlineData("MC110-N")]
    public void StopDuringWavePreparationPreventsCompletedTxFile(string modeName)
    {
      var mode = Assert.Single(SstvTxModeProfile.All, x => x.Name == modeName);
      using var image = new Bitmap(16, 16);
      using var cancellation = new CancellationTokenSource();
      cancellation.Cancel();
      string path = Path.Combine(Path.GetTempPath(),
        "SkyRoof-SstvAbort-" + Guid.NewGuid().ToString("N") + ".wav");
      try
      {
        Assert.Throws<OperationCanceledException>(
          () => SstvRgbWaveExporter.Export(image, mode, path, cancellation.Token));
      }
      finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void ManualRxModeCanRestoreImageWithoutVisPreamble()
    {
      SstvTxModeProfile mode = Assert.Single(SstvTxModeProfile.All,
        x => x.Name == "Robot BW 8");
      string path = Path.Combine(Path.GetTempPath(),
        "SkyRoof-SstvManual-" + Guid.NewGuid().ToString("N") + ".wav");
      try
      {
        using (var input = new Bitmap(32, 32))
        {
          using var g = Graphics.FromImage(input);
          g.Clear(Color.White);
          g.FillRectangle(Brushes.Black, 0, 0, 16, 32);
          SstvRgbWaveExporter.Export(input, mode, path);
        }
        using var wave = new WaveFileReader(path);
        wave.Position = (long)Math.Round(mode.Extra!.HeaderMs * 48) * 2;
        using var receiver = new SstvLiveRasterReceiver();
        receiver.ForceMode(mode);
        Bitmap? frame = null;
        receiver.ImageReady += (bitmap, _, final, _) =>
        {
          if (final) frame = new Bitmap(bitmap);
          bitmap.Dispose();
        };
        var bytes = new byte[4800];
        int count;
        while ((count = wave.Read(bytes, 0, bytes.Length)) > 0)
        {
          float[] audio = new float[count / 2];
          for (int i = 0; i < audio.Length; i++)
            audio[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8) / 32768f;
          receiver.Process(audio);
        }
        Assert.NotNull(frame);
        using (frame)
        {
          Color a = frame!.GetPixel(40, 100), b = frame.GetPixel(290, 100);
          Assert.True(a.R + 60 < b.R,
            $"Manual Robot BW8 contrast: {a.R}/{b.R}");
        }
      }
      finally { try { File.Delete(path); } catch (IOException) { } }
    }
  }
}
