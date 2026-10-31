using System;
using System.Drawing;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvOfflineRasterDecoderTests
  {
    [Theory]
    [InlineData(1100)]
    [InlineData(1500)]
    [InlineData(1900)]
    [InlineData(2300)]
    public void AnalyticAfDiscriminatorTracksStandardSubcarrier(double frequencyHz)
    {
      var demod = new SstvAfToneDiscriminator();
      double sum = 0;
      int count = 0;
      for (int n = 0; n < 9600; n++)
      {
        float sample = (float)(0.7 * Math.Sin(
          2 * Math.PI * frequencyHz * n / 48000));
        double hz = demod.Process(sample);
        if (n > 8000) { sum += hz; count++; }
      }
      Assert.InRange(sum / count, frequencyHz - 20, frequencyHz + 20);
    }

    [Theory]
    [InlineData("Robot BW 8")]
    [InlineData("SC2 60")]
    public void GeneratedLegacyImageCanBeReconstructedOffline(string name)
    {
      SstvTxModeProfile mode = Assert.Single(SstvTxModeProfile.All,
        m => m.Name == name);
      string file = Path.Combine(Path.GetTempPath(),
        "SkyRoofRaster-" + Guid.NewGuid().ToString("N") + ".wav");
      try
      {
        using var source = new Bitmap(48, 48);
        using (Graphics g = Graphics.FromImage(source))
        {
          g.Clear(Color.White);
          g.FillRectangle(Brushes.Black, 0, 0, 24, 48);
        }
        SstvRgbWaveExporter.Export(source, mode, file);
        using var decoded = SstvOfflineRasterDecoder.Decode(file, mode);
        Assert.Equal(mode.Width, decoded.Width);
        Assert.Equal(mode.Height, decoded.Height);
        Color dark = decoded.GetPixel(mode.Width / 5, mode.Height / 2);
        Color light = decoded.GetPixel(4 * mode.Width / 5, mode.Height / 2);
        Assert.True(dark.R < light.R - 70,
          $"SSTV {name}: left {dark.R}, right {light.R}");
      }
      finally
      {
        try { File.Delete(file); } catch (IOException) { }
      }
    }
  }
}
