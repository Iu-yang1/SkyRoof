using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SkyRoof;
using SkyRoof.IF;
using VE3NEA;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  /// <summary>
  /// Synthetic IC-9700-like 48-kS/s, real 12-kHz IF acceptance tests.
  /// Includes AWGN, 16-bit PCM quantization, gain and opposite-sideband
  /// interferers. Does NOT claim any hardware sensitivity, NF or ENOB.
  /// </summary>
  public sealed class IfAfSimulatedHardwareTests
  {
    const int Fs = 48000;
    private static readonly Random Seed = new(37815);
    private static float[] Synthetic(
      double wantedHz, double wantedAmp, double oppositeAmp,
      double noiseRms, double ifOffset = 0, bool quantize16 = true)
    {
      var data = new float[Fs];
      var rng = new Random(37815);
      for (int n = 0; n < data.Length; n++)
      {
        double time = n / (double)Fs;
        double v = wantedAmp * Math.Cos(2 * Math.PI *
            (12000 + ifOffset + wantedHz) * time) +
          oppositeAmp * Math.Cos(2 * Math.PI *
            (12000 + ifOffset - wantedHz) * time);
        if (noiseRms > 0)
        {
          double u1 = Math.Max(1e-12, rng.NextDouble());
          v += noiseRms * Math.Sqrt(-2 * Math.Log(u1)) *
            Math.Cos(2 * Math.PI * rng.NextDouble());
        }
        v = Math.Clamp(v, -.99997, .99997);
        data[n] = quantize16 ? (float)(Math.Round(v * 32767) / 32767) : (float)v;
      }
      return data;
    }
    private static float[] Demod(float[] data, IfAfMode mode)
    {
      var d = new IfAfDemodulator(mode);
      float[] result = new float[data.Length];
      for (int i = 0; i < data.Length; i += 1600)
        d.Process(data.AsSpan(i, Math.Min(1600, data.Length - i)),
          result.AsSpan(i, Math.Min(1600, data.Length - i)));
      return result;
    }
    private static double Strength(float[] y, double f)
    {
      double re = 0, im = 0;
      const int start = Fs / 4;
      for (int n = start; n < y.Length; n++)
      {
        double a = 2 * Math.PI * f * n / Fs;
        re += y[n] * Math.Cos(a);
        im += y[n] * Math.Sin(a);
      }
      return 2 * Math.Sqrt(re * re + im * im) / (y.Length - start);
    }
    [Theory]
    [InlineData(.35, .0, .005)]
    [InlineData(.05, .0, .02)]
    [InlineData(.05, .35, .02)]
    public void Quantized48kIfPreservesWantedToneUnderAwgnAndImage(
      double amp, double image, double noise)
    {
      var x = Synthetic(1550, amp, image, noise);
      var y = Demod(x, IfAfMode.Usb);
      Assert.InRange(Strength(y, 1550), amp * .82, amp * 1.18);
      Assert.All(y, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void OppositeSidebandInterfererDoesNotContributeToAudio()
    {
      var wanted = Demod(Synthetic(1700, .1, 0, 0), IfAfMode.Usb);
      var blocked = Demod(Synthetic(1700, .1, .6, 0), IfAfMode.Usb);
      double a = Strength(wanted, 1700);
      double b = Strength(blocked, 1700);
      Assert.InRange(Math.Abs(a - b), 0, .012);
    }

    [Fact]
    public void LowLevel16BitIFStillHasResolvableCWAudio()
    {
      var x = Synthetic(700, .005, 0, .0004);
      var y = Demod(x, IfAfMode.CwUsb);
      Assert.InRange(Strength(y, 700), .0040, .0060);
    }

    [Fact]
    public void ModeMappingIncludesInvertedAndDataSidebands()
    {
      Assert.Equal(IfAfMode.Lsb,
        IfAfSettings.Resolve(IfAfSelection.FollowRadio, Slicer.Mode.LSB_D));
      Assert.Equal(IfAfMode.Usb,
        IfAfSettings.Resolve(IfAfSelection.FollowRadio, Slicer.Mode.USB_D));
      Assert.Equal(IfAfMode.Fm,
        IfAfSettings.Resolve(IfAfSelection.FollowRadio, Slicer.Mode.FM_D));
      Assert.Equal(IfAfMode.CwLsb,
        IfAfSettings.Resolve(IfAfSelection.CwLsb, Slicer.Mode.CW));
      Assert.Equal(IfAfMode.Fm,
        IfAfSettings.Resolve(IfAfSelection.FollowRadio, Slicer.Mode.FM));
      Assert.Equal(IfAfMode.Lsb,
        IfAfSettings.Resolve(IfAfSelection.Lsb, Slicer.Mode.USB));
    }

    private sealed class StereoSource : ISampleProvider
    {
      private readonly float[] values;
      private int read;
      public StereoSource(params float[] interleaved) => values = interleaved;
      public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
      public int Read(float[] dest, int offset, int count)
      {
        int take = Math.Min(count, values.Length - read);
        Array.Copy(values, read, dest, offset, take);
        read += take;
        return take;
      }
    }
    [Fact]
    public void OppositePhaseStereoMixWouldCancelIFButExplicitChannelDoesNot()
    {
      var left = new IfChannelSelectSampleProvider(
        new StereoSource(.5f, -.5f, .25f, -.25f), AudioChannelChoice.Left);
      var right = new IfChannelSelectSampleProvider(
        new StereoSource(.5f, -.5f, .25f, -.25f), AudioChannelChoice.Right);
      var mix = new IfChannelSelectSampleProvider(
        new StereoSource(.5f, -.5f, .25f, -.25f), AudioChannelChoice.Mix);
      float[] a = new float[2], b = new float[2], c = new float[2];
      Assert.Equal(2, left.Read(a, 0, 2));
      Assert.Equal(2, right.Read(b, 0, 2));
      Assert.Equal(2, mix.Read(c, 0, 2));
      Assert.Equal(new[] { .5f, .25f }, a);
      Assert.Equal(new[] { -.5f, -.25f }, b);
      Assert.Equal(new[] { 0f, 0f }, c);
    }
  }
}
