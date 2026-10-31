using System;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvIfAudioDemodulatorTests
  {
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Real12kIfRecoversSstvToneFromEitherSideband(int sideband)
    {
      const int fs = 48000;
      const double toneHz = 1900;
      var demod = new SstvIfAudioDemodulator();
      var output = new double[4800];
      for (int n = 0; n < output.Length; n++)
      {
        float sample = (float)(.6 * Math.Cos(
          2 * Math.PI * (12000 + sideband * toneHz) * n / fs));
        output[n] = demod.Process(sample);
      }

      // Skip the 129-tap FIR transient, verify the recovered tone energy
      // in a matched 1900 Hz pair of Fourier coefficients.
      double real = 0, imag = 0, energy = 0;
      for (int i = 256; i < output.Length; i++)
      {
        double phi = 2 * Math.PI * toneHz * i / fs;
        real += output[i] * Math.Cos(phi);
        imag += output[i] * Math.Sin(phi);
        energy += output[i] * output[i];
      }
      double coherent = Math.Sqrt(real * real + imag * imag);
      Assert.InRange(coherent / (output.Length - 256), .15, .4);
      Assert.True(energy > 100);
    }

    [Fact]
    public void ResetClearsMixerHistory()
    {
      var demod = new SstvIfAudioDemodulator();
      for (int n = 0; n < 300; n++)
        demod.Process((float)Math.Cos(2 * Math.PI * 13900 * n / 48000.0));
      demod.Reset();
      for (int n = 0; n < 130; n++)
        Assert.Equal(0, demod.Process(0), precision: 10);
    }
  }
}
