using System;
using System.Drawing;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using VE3NEA.SkySSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvRgbWaveExporterTests
  {
    [Theory]
    [InlineData("Scottie 1", 428.22, 0x3c)]
    [InlineData("Scottie 2", 277.692, 0xb8)]
    [InlineData("Scottie DX", 1050.3, 0xcc)]
    [InlineData("Martin 1", 446.446, 0xac)]
    [InlineData("Martin 2", 226.798, 0x28)]
    [InlineData("SC2 60", 240.3846, 0xbb)]
    [InlineData("SC2 120", 475.52248, 0x3f)]
    [InlineData("SC2 180", 711.0437, 0xb7)]
    public void ModeTimingsAndVisMatchMmsstvSource(
      string name, double lineMs, int visByte)
    {
      SstvRgbTxSpec mode = Assert.Single(SstvRgbTxSpec.All,
        x => x.Name == name);
      Assert.InRange(Math.Abs(mode.LineMs - lineMs), 0, 0.0001);
      Assert.Equal(visByte, SstvModes.EvenParityByte(mode.VisCode7));
      Assert.Equal(320, mode.Width);
      Assert.Equal(256, mode.Height);
    }

    [Theory]
    [InlineData("Scottie 2")]
    [InlineData("Martin 2")]
    [InlineData("SC2 60")]
    public void EncodedRgbWaveHasExactContinuousDurationAndPcmFormat(
      string name)
    {
      SstvRgbTxSpec mode = Assert.Single(SstvRgbTxSpec.All,
        x => x.Name == name);
      string file = Path.Combine(Path.GetTempPath(),
        "SstvRgb-" + Guid.NewGuid().ToString("N") + ".wav");
      try
      {
        using var input = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(input))
          g.Clear(Color.Orange);
        SstvRgbWaveExporter.Export(input, mode, file);
        using var wav = new WaveFileReader(file);
        Assert.Equal(48000, wav.WaveFormat.SampleRate);
        Assert.Equal(1, wav.WaveFormat.Channels);
        Assert.Equal(16, wav.WaveFormat.BitsPerSample);
        double expected = (SstvTones.VisHeaderMs +
          (mode.Layout == SstvRgbTxLayout.Scottie ? 9 : 0) +
          mode.Height * mode.LineMs) / 1000;
        Assert.InRange(Math.Abs(wav.TotalTime.TotalSeconds - expected),
          0, .002);
      }
      finally
      {
        try { File.Delete(file); } catch (IOException) { }
      }
    }
  }
}
