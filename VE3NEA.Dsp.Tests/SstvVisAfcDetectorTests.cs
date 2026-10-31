using System;
using System.Collections.Generic;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvVisAfcDetectorTests
  {
    private static float[] Header(int visByte, double offsetHz,
      bool breakHeader = false, double amp = 0.75)
    {
      var samples = new List<float>(48000);
      double phase = 0;
      void Tone(double hz, int milliseconds)
      {
        int count = 48 * milliseconds;
        for (int i = 0; i < count; i++)
        {
          phase += 2 * Math.PI * (hz + offsetHz) / 48000;
          if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
          samples.Add((float)(amp * Math.Sin(phase)));
        }
      }
      Tone(1900, 300);
      Tone(breakHeader ? 1700 : 1200, 10);
      Tone(1900, 300);
      Tone(1200, 30);
      for (int bit = 0; bit < 8; bit++)
        Tone((visByte & (1 << bit)) != 0 ? 1100 : 1300, 30);
      Tone(1200, 30);
      return samples.ToArray();
    }

    [Theory]
    [InlineData(0x88, 0)]
    [InlineData(0x88, 45)]
    [InlineData(0x88, -65)]
    [InlineData(0x0C, 100)]
    public void GoodVisHeaderMeasuresSignedAfOffset(int vis, double offset)
    {
      var detector = new SstvVisAfcDetector();
      detector.Reset(7);
      var locks = new List<SstvVisAfcLock>();
      detector.VisLocked += locks.Add;
      float[] data = Header(vis, offset);
      for (int start = 0; start < data.Length; start += 397)
        detector.Process(data.AsSpan(start, Math.Min(397, data.Length - start)));
      var hit = Assert.Single(locks);
      Assert.Equal(vis, hit.VisByte);
      Assert.Equal(7, hit.Epoch);
      Assert.InRange(hit.OffsetHz, offset - 8, offset + 8);
    }

    [Fact]
    public void OddParityOrBrokenLeaderNeverAuthorizesAfc()
    {
      var detector = new SstvVisAfcDetector();
      var locks = new List<SstvVisAfcLock>();
      detector.VisLocked += locks.Add;
      detector.Process(Header(0x08, 0));
      detector.Reset(1);
      detector.Process(Header(0x88, 0, breakHeader: true));
      Assert.Empty(locks);
    }

    [Fact]
    public void NoiseOrLowAmplitudeCannotMasqueradeAsVis()
    {
      var detector = new SstvVisAfcDetector();
      var locks = new List<SstvVisAfcLock>();
      detector.VisLocked += locks.Add;
      detector.Process(Header(0x88, 0, amp: 0.01));
      detector.Reset(2);
      var random = new Random(7);
      var noise = new float[80000];
      for (int i = 0; i < noise.Length; i++)
        noise[i] = (float)(random.NextDouble() - 0.5);
      detector.Process(noise);
      Assert.Empty(locks);
    }
  }
}
