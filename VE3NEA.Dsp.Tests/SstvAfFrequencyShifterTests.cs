using System;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvAfFrequencyShifterTests
  {
    [Theory]
    [InlineData(2100, -200, 1900, 2300)]
    [InlineData(1700, 200, 1900, 1500)]
    [InlineData(1900, 0, 1900, 1900)]
    public void AnalyticAfCorrectsToneWithoutSpuriousMirror(
      double inputHz, double correctionHz, double wantedHz, double unwantedHz)
    {
      const int fs = 48000;
      const int count = 12000;
      var shifter = new SstvAfFrequencyShifter();
      double targetRe = 0, targetIm = 0, imageRe = 0, imageIm = 0;
      for (int n = 0; n < count; n++)
      {
        float sample = (float)(.65 * Math.Cos(2 * Math.PI * inputHz * n / fs));
        double output = shifter.Process(sample, correctionHz);
        if (n < 256) continue;
        double t = 2 * Math.PI * wantedHz * n / fs;
        targetRe += output * Math.Cos(t);
        targetIm += output * Math.Sin(t);
        double imageT = 2 * Math.PI * unwantedHz * n / fs;
        imageRe += output * Math.Cos(imageT);
        imageIm += output * Math.Sin(imageT);
      }
      double wanted = Math.Sqrt(targetRe * targetRe + targetIm * targetIm);
      Assert.True(wanted > 1200, "The corrected SSTV tone must remain measurable");
      if (correctionHz != 0)
      {
        double image = Math.Sqrt(imageRe * imageRe + imageIm * imageIm);
        Assert.True(wanted > 7 * image, "Analytic NCO must suppress the unwanted real-mixer image");
      }
    }

    [Fact]
    public void InvalidCorrectionIsRejected()
    {
      var shifter = new SstvAfFrequencyShifter();
      Assert.Throws<ArgumentOutOfRangeException>(() => shifter.Process(.2f, 600));
      Assert.Throws<ArgumentOutOfRangeException>(() => shifter.Process(.2f, double.NaN));
      shifter.Reset();
      Assert.Equal(0f, shifter.Process(0, 0));
    }
  }
}
