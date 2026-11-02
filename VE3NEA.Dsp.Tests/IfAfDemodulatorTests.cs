using System;
using SkyRoof.IF;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IfAfDemodulatorTests
  {
    private const int Rate = 48000;
    private static float[] Generate(double rfAudioHz, double oppositeHz = 0,
      double oppositeAmplitude = 0, double offset = 0, bool lsb = false)
    {
      float[] pcm = new float[Rate];
      double sign = lsb ? -1 : 1;
      for (int n = 0; n < pcm.Length; n++)
      {
        double t = n / (double)Rate;
        pcm[n] = (float)(.42 * Math.Cos(2 * Math.PI *
          (12000 + offset + sign * rfAudioHz) * t) +
          oppositeAmplitude * Math.Cos(2 * Math.PI *
          (12000 + offset - sign * oppositeHz) * t));
      }
      return pcm;
    }
    private static double ToneAmplitude(float[] pcm, double hz,
      int discard = 7500)
    {
      double re = 0, im = 0;
      for (int n = discard; n < pcm.Length; n++)
      {
        double a = 2 * Math.PI * hz * n / Rate;
        re += pcm[n] * Math.Cos(a);
        im += pcm[n] * Math.Sin(a);
      }
      return 2 * Math.Sqrt(re * re + im * im) / (pcm.Length - discard);
    }
    private static float[] Decode(IfAfMode mode, float[] signal, int chunk = 1600)
    {
      var d = new IfAfDemodulator(mode);
      float[] output = new float[signal.Length];
      for (int i = 0; i < signal.Length; i += chunk)
      {
        int length = Math.Min(chunk, signal.Length - i);
        d.Process(signal.AsSpan(i, length), output.AsSpan(i, length));
      }
      return output;
    }
    [Theory]
    [InlineData(IfAfMode.Usb, false)]
    [InlineData(IfAfMode.Lsb, true)]
    public void RecoversWantedSidebandAndRejectsOpposite(IfAfMode mode, bool lsb)
    {
      float[] x = Generate(1350, 2200, .42, lsb: lsb);
      float[] af = Decode(mode, x);
      Assert.InRange(ToneAmplitude(af, 1350), .30, .51);
      Assert.True(ToneAmplitude(af, 2200) < .023,
        "opposite sideband must be at least 25 dB below wanted");
    }
    [Fact]
    public void ReversingSidebandRejectsWantedFrequency()
    {
      float[] x = Generate(1500, lsb: false);
      var wrong = Decode(IfAfMode.Lsb, x);
      Assert.True(ToneAmplitude(wrong, 1500) < .016);
    }
    [Fact]
    public void StreamingBlockPartitionDoesNotChangeWaveform()
    {
      float[] x = Generate(1550, 0, 0, 45);
      float[] a = Decode(IfAfMode.Usb, x, 193);
      float[] b = Decode(IfAfMode.Usb, x, 1600);
      for (int n = 0; n < x.Length; n++)
        Assert.InRange(Math.Abs(a[n] - b[n]), 0, 1e-6);
    }
    [Theory]
    [InlineData(IfAfMode.CwUsb, 750)]
    [InlineData(IfAfMode.CwLsb, 750)]
    public void NarrowCwPassbandStillRecoversWeakTone(IfAfMode mode, int tone)
    {
      var af = Decode(mode, Generate(tone, lsb: mode == IfAfMode.CwLsb));
      Assert.InRange(ToneAmplitude(af, tone), .29, .53);
    }
    [Theory]
    [InlineData(2500, 1200)]
    [InlineData(5000, 1900)]
    public void FMRecoversModulatedToneAndRejectsCarrierOffset(
      double deviation, double tone)
    {
      float[] x = new float[Rate];
      double phase = 0;
      for (int n = 0; n < x.Length; n++)
      {
        double t = n / (double)Rate;
        phase += 2 * Math.PI / Rate *
          (12000 + 175 + deviation * .55 * Math.Sin(2 * Math.PI * tone * t));
        x[n] = (float)(.6 * Math.Cos(phase));
      }
      var dsp = new IfAfDemodulator(IfAfMode.Fm, deviation);
      var af = new float[x.Length];
      dsp.Process(x, af);
      Assert.InRange(ToneAmplitude(af, tone), .36, .70);
      double dc = 0;
      for (int n = 12000; n < af.Length; n++) dc += af[n];
      Assert.InRange(Math.Abs(dc / (af.Length - 12000)), 0, .01);
    }
    [Fact]
    public void GarbageSampleIsSanitizedWithoutBurst()
    {
      var dsp = new IfAfDemodulator();
      float[] x = Generate(1100);
      x[123] = float.NaN;
      x[124] = float.PositiveInfinity;
      var y = new float[x.Length];
      dsp.Process(x, y);
      Assert.Equal(2, dsp.NonFiniteSamples);
      Assert.All(y, s => Assert.True(float.IsFinite(s)));
    }
    [Fact]
    public void SyntheticWeakSignalHasNoUnexpectedNumericalSNRPenalty()
    {
      var random = new Random(1738);
      float[] x = Generate(1500);
      // Add deterministic RF-side Gaussian noise to a reference IC-9700-like
      // 12-kHz IF. This is NOT a hardware NF / sensitivity measurement.
      for (int n = 0; n < x.Length; n++)
      {
        double u1 = Math.Max(1e-12, random.NextDouble()), u2 = random.NextDouble();
        x[n] += (float)(.07 * Math.Sqrt(-2 * Math.Log(u1)) *
          Math.Cos(2 * Math.PI * u2));
      }
      var y = Decode(IfAfMode.Usb, x);
      Assert.InRange(ToneAmplitude(y, 1500), .30, .51);
      Assert.All(y, v => Assert.True(float.IsFinite(v)));
    }
    [Fact]
    public void NormalLowLevelUsbSignalDoesNotCauseDdcOutputClipping()
    {
      // A weak IF input such as ~-38 dBFS must not acquire 30+ dB of
      // unexplained gain in the SSB DDC. This isolates FIR/NCO from any
      // host-device glitch or gain setting that might cause real clipping.
      var x = Generate(1500);
      for (int i = 0; i < x.Length; i++) x[i] *= 0.045f;
      var af = Decode(IfAfMode.Usb, x, 480);
      double maxAbs = 0;
      foreach (float sample in af) maxAbs = Math.Max(maxAbs, Math.Abs(sample));
      Assert.InRange(maxAbs, 0.01, 0.05);
    }

    [Fact]
    public void ConfigurableSsbPassbandSuppressesAudioOutsideSelectedUpperEdge()
    {
      var x = Generate(3200);
      var dsp = new IfAfDemodulator(IfAfMode.Usb, 2500, 200, 2300);
      var y = new float[x.Length];
      dsp.Process(x, y);
      Assert.True(ToneAmplitude(y, 3200) < .020);
    }

  }
}
