using System;
using System.Drawing;
using System.IO;
using System.Linq;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvExtraModeTests
  {
    [Fact]
    public void CatalogContainsEveryNamedReferenceTxMode()
    {
      Assert.Equal(26, SstvExtraTxSpec.All.Count);
      Assert.Equal(43, SstvTxModeProfile.All.Count);
      Assert.Equal(43, SstvTxModeProfile.All.Select(x => x.Name).Distinct().Count());
      Assert.All(SstvTxModeProfile.All, x =>
      {
        Assert.True(x.Width >= 320 && x.Width <= 800);
        Assert.True(x.Height >= 240 && x.Height <= 616);
      });
    }

    [Theory]
    [InlineData("P3", 0x71, 409.375)]
    [InlineData("P7", 0x73, 818.750)]
    [InlineData("MP73", 0x2523, 570.0)]
    [InlineData("MR73", 0x4523, 286.3)]
    [InlineData("ML180", 0x8523, 363.3)]
    [InlineData("MP73-N", 0x02, 570.0)]
    [InlineData("MC110-N", 0x14, 428.5)]
    [InlineData("Robot BW 8", 0x02, 66.89709)]
    [InlineData("Robot 24", 0x04, 200.0)]
    public void TimingAndVisDefinitionsMatchMmsstv(
      string name, int vis, double lineMs)
    {
      SstvExtraTxSpec spec = Assert.Single(SstvExtraTxSpec.All,
        x => x.Name == name);
      Assert.Equal(vis, spec.VisCode);
      Assert.InRange(Math.Abs(spec.LineMs - lineMs), 0, .0005);
    }

    [Theory]
    [InlineData("Robot BW 8")]
    [InlineData("MP73")]
    [InlineData("MC110-N")]
    public void EncoderEmitsExpectedWavLength(string name)
    {
      SstvExtraTxSpec mode = Assert.Single(SstvExtraTxSpec.All,
        x => x.Name == name);
      string path = Path.Combine(Path.GetTempPath(),
        "SkyRoof-Extra-" + Guid.NewGuid().ToString("N") + ".wav");
      try
      {
        using var image = new Bitmap(32, 24);
        using (Graphics g = Graphics.FromImage(image))
          g.Clear(Color.Aquamarine);
        SstvExtraWaveExporter.Export(image, mode, path);
        using var wav = new WaveFileReader(path);
        Assert.Equal(48000, wav.WaveFormat.SampleRate);
        Assert.Equal(16, wav.WaveFormat.BitsPerSample);
        Assert.Equal(1, wav.WaveFormat.Channels);
        double expected = (mode.HeaderMs + mode.LineCount * mode.LineMs) / 1000;
        Assert.InRange(Math.Abs(wav.TotalTime.TotalSeconds - expected),
          0, .003);
      }
      finally
      {
        try { File.Delete(path); } catch (IOException) { }
      }
    }
  }
}
