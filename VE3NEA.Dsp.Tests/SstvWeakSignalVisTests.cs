using System;
using System.Collections.Generic;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvWeakSignalVisTests
  {
    private static float[] AudioHeader(int visByte, double offsetHz,
      double signalToNoiseDb, int seed)
    {
      var result = new List<float>();
      var random = new Random(seed);
      double phase = 0;
      double std = .75 / Math.Sqrt(2) /
        Math.Pow(10, signalToNoiseDb / 20);
      double Gaussian()
      {
        double u = Math.Max(1e-10, random.NextDouble());
        double v = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
      }
      void Tone(double frequency, int ms, bool silent = false)
      {
        for (int i = 0; i < ms * 48; i++)
        {
          phase += 2 * Math.PI * (frequency + offsetHz) / 48000;
          if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
          result.Add(silent ? 0 : (float)(.75 * Math.Sin(phase) + std * Gaussian()));
        }
      }
      Tone(0, 200, silent: true);
      Tone(1900, 300);
      Tone(1200, 10);
      Tone(1900, 300);
      Tone(1200, 30);
      for (int bit = 0; bit < 8; bit++)
        Tone((visByte & (1 << bit)) != 0 ? 1100 : 1300, 30);
      Tone(1200, 30);
      Tone(1500, 200);
      return result.ToArray();
    }

    [Theory]
    [InlineData(0x88, 45.0, 20.0, 3)]
    [InlineData(0x88, -55.0, 22.0, 5)]
    [InlineData(0xAC, 0.0, 20.0, 7)]
    public void VisIdentifierSurvivesModerateWidebandAwgn(
      int visCode, double offset, double snrDb, int seed)
    {
      float[] audio = AudioHeader(visCode, offset, snrDb, seed);
      var result = SstvVisModeIdentifier.DetectAudio(audio);
      Assert.True(result.HasValue, "Valid noisy VIS should still identify a mode.");
      Assert.Equal(visCode == 0xAC ? "Martin 1" : "Robot 36",
        result.Value.Profile.Name);
      Assert.InRange(result.Value.LeadingAudio.TotalMilliseconds, 190, 210);
      Assert.InRange(result.Value.FrequencyOffsetHz,
        offset - 18, offset + 18);
    }

    [Fact]
    public void NoiseOnlyCannotTriggerModeSelection()
    {
      var random = new Random(19);
      var hiss = new float[48000 * 3];
      for (int i = 0; i < hiss.Length; i++)
        hiss[i] = (float)(random.NextDouble() * .8 - .4);
      Assert.Null(SstvVisModeIdentifier.DetectAudio(hiss));
    }
  }
}
