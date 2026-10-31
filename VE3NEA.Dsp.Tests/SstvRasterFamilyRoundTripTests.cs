using System;
using System.Drawing;
using System.IO;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  /// <summary>
  /// Independent rasters test all non-Robot scan layouts against a
  /// recognisable black/white image. This is encoder-decoder regression,
  /// NOT a substitute for external MMSSTV reference golden recordings.
  /// </summary>
  public sealed class SstvRasterFamilyRoundTripTests
  {
    [Theory]
    [InlineData("Scottie 2")]
    [InlineData("Martin 2")]
    [InlineData("P3")]
    [InlineData("MR73")]
    [InlineData("AVT 90")]
    [InlineData("MC110-N")]
    public void ColorRasterFamilyProducesRecognizablePicture(string name)
    {
      var mode = Assert.Single(SstvTxModeProfile.All, x => x.Name == name);
      string wav = Path.Combine(Path.GetTempPath(),
        "SstvFamily-" + Guid.NewGuid().ToString("N") + ".wav");
      try
      {
        using (var original = new Bitmap(64, 48))
        {
          using (var g = Graphics.FromImage(original))
          {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 0, 0, 32, 48);
          }
          SstvRgbWaveExporter.Export(original, mode, wav);
        }

        using var restored = SstvOfflineRasterDecoder.Decode(wav, mode);
        Assert.Equal(mode.Width, restored.Width);
        Assert.Equal(mode.Height, restored.Height);
        foreach (int y in new[] { mode.Height / 4, mode.Height / 2, 3 * mode.Height / 4 })
        {
          Color left = restored.GetPixel(mode.Width / 5, y);
          Color right = restored.GetPixel(mode.Width * 4 / 5, y);
          Assert.True(left.R + 65 < right.R,
            $"{name}, row={y}, black/white signal R={left.R}/{right.R}");
          Assert.True(left.G + 65 < right.G,
            $"{name}, row={y}, black/white signal G={left.G}/{right.G}");
          Assert.True(left.B + 65 < right.B,
            $"{name}, row={y}, black/white signal B={left.B}/{right.B}");
        }
      }
      finally { try { File.Delete(wav); } catch (IOException) { } }
    }
  }
}
