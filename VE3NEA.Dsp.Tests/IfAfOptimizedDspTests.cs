using System;
using System.Numerics;
using SkyRoof.IF;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IfAfOptimizedDspTests
  {
    private const int Rate = 48000, Taps = 257;

    private static double[] Blackman(int size, double cutoff)
    {
      var h = new double[size];
      double sum = 0;
      for (int k = 0; k < size; k++)
      {
        int d = k - (size - 1) / 2;
        double sinc = d == 0 ? 2 * cutoff / Rate :
          Math.Sin(2 * Math.PI * cutoff * d / Rate) / (Math.PI * d);
        double window = .42 - .5 * Math.Cos(2 * Math.PI * k / (size - 1)) +
          .08 * Math.Cos(4 * Math.PI * k / (size - 1));
        sum += h[k] = sinc * window;
      }
      for (int k = 0; k < size; k++) h[k] /= sum;
      return h;
    }

    private static double At(double[] taps, double hz)
    {
      double m = (taps.Length - 1) / 2.0, re = 0;
      for (int k = 0; k < taps.Length; k++)
        re += taps[k] * Math.Cos(2 * Math.PI * hz * (k - m) / Rate);
      return Math.Abs(re);
    }

    private static Complex[] ReferenceTaps(int low, int high, bool lsb)
    {
      double mid = (low + high) / 2.0, target = (high - low) / 2.0;
      double a = Math.Max(1, target - 1200), b = Math.Min(Rate / 2.0 - 1, target + 1200);
      for (int it = 0; it < 24; it++)
      {
        double c = (a + b) / 2;
        if (At(Blackman(Taps, c), target) > 1 / Math.Sqrt(2)) b = c;
        else a = c;
      }
      var lowpass = Blackman(Taps, (a + b) / 2);
      var result = new Complex[Taps];
      for (int k = 0; k < Taps; k++)
      {
        double w = 2 * Math.PI * (lsb ? -mid : mid) *
          (k - (Taps - 1) / 2.0) / Rate;
        result[k] = lowpass[k] * new Complex(Math.Cos(w), Math.Sin(w));
      }
      return result;
    }

    [Theory]
    [InlineData(IfAfMode.Usb, 180, 2880, false)]
    [InlineData(IfAfMode.Lsb, 180, 2880, true)]
    [InlineData(IfAfMode.UsbDigital, 100, 3500, false)]
    [InlineData(IfAfMode.LsbDigital, 100, 3500, true)]
    public void FourPhaseRealFirMatchesIndependentComplexReference(
      IfAfMode mode, int low, int high, bool lsb)
    {
      var taps = ReferenceTaps(low, high, lsb);
      var demod = new IfAfDemodulator(mode, ssbLowHz: 180, ssbHighHz: 2880);
      var ring = new Complex[Taps];
      int write = 0, total = 4096;
      for (int i = 0; i < total; i++)
      {
        double t = i / (double)Rate;
        double frequency = 12000 + (lsb ? -1 : 1) * 1200;
        float sample = (float)(.24 * Math.Cos(2 * Math.PI * frequency * t) +
          .05 * Math.Cos(2 * Math.PI * 14000 * t));
        Complex mixer = (i & 3) switch
        {
          0 => new Complex(2 * sample, 0),
          1 => new Complex(0, -2 * sample),
          2 => new Complex(-2 * sample, 0),
          _ => new Complex(0, 2 * sample)
        };
        ring[write] = mixer;
        Complex expected = Complex.Zero;
        int idx = write;
        for (int k = 0; k < Taps; k++)
        {
          expected += taps[k] * ring[idx];
          if (--idx < 0) idx = Taps - 1;
        }
        float actual = demod.Process(sample);
        Assert.InRange(Math.Abs(actual - expected.Real), 0, 3e-6);
        if (++write == Taps) write = 0;
      }
    }

    [Fact]
    public void PeakProtectionReleasesWithoutSuddenFullGainJump()
    {
      var limiter = new IfAfAudioLeveler(new IfAfSettings());
      float[] strong = new float[480];
      for (int i = 0; i < strong.Length; i++)
        strong[i] = 16 * (float)Math.Sin(2 * Math.PI * 1000 * i / Rate);
      var first = limiter.Process(strong, IfAfMode.Fm);
      Assert.True(first.PeakLimited);
      Assert.True(first.PeakReductionDb > 5);
      double previousReduction = first.PeakReductionDb;
      for (int block = 0; block < 50; block++)
      {
        var weak = new float[480];
        for (int i = 0; i < weak.Length; i++)
          weak[i] = .25f * (float)Math.Sin(2 * Math.PI * 1000 * i / Rate);
        var result = limiter.Process(weak, IfAfMode.Fm);
        Assert.False(result.PeakLimited);
        Assert.True(result.PeakReductionDb <= previousReduction + .001);
        Assert.All(weak, x => Assert.InRange(Math.Abs(x), 0f, .891f));
        previousReduction = result.PeakReductionDb;
      }
      Assert.True(previousReduction < first.PeakReductionDb * .2);
    }
  }
}
