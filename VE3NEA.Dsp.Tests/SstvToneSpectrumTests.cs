using System;
using System.Linq;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvToneSpectrumTests
  {
    [Theory]
    [InlineData(1200)]
    [InlineData(1500)]
    [InlineData(1900)]
    [InlineData(2300)]
    public void TuningSpectrumLabelsCorrectAfFrequency(int hz)
    {
      const int rate = 48000;
      var audio = new float[4096];
      for (int i = 0; i < audio.Length; i++)
        audio[i] = (float)(0.75 * Math.Sin(2 * Math.PI * hz * i / rate));
      float[] spectrum = SstvToneSpectrum.Measure(audio);
      Assert.Equal(SstvToneSpectrum.BinCount, spectrum.Length);
      int strongest = Array.IndexOf(spectrum, spectrum.Max());
      int strongestHz = SstvToneSpectrum.MinHz +
        SstvToneSpectrum.BinStepHz * strongest;
      Assert.InRange(strongestHz, hz - 25, hz + 25);
      Assert.True(spectrum[strongest] > 0.2f);
    }

    [Fact]
    public void SilenceAndInvalidPcmNeverPaintAFalseSstvTone()
    {
      Assert.All(SstvToneSpectrum.Measure(new float[2048]),
        level => Assert.Equal(0, level));
      var invalid = new float[2048];
      invalid[32] = float.NaN;
      Assert.All(SstvToneSpectrum.Measure(invalid),
        level => Assert.Equal(0, level));
    }
  }
}
