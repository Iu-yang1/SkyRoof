using System;
using System.Collections.Generic;
using SkyRoof.SSTV;
using VE3NEA.SkySSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvNativeLineSyncAfcTests
  {
    private static float[] Picture(int lines, double syncOffsetHz,
      bool realSync)
    {
      var samples = new List<float>(48000 * 5);
      double phase = 0;
      void Tone(double hz, int milliseconds)
      {
        for (int n = 0; n < 48 * milliseconds; n++)
        {
          if (hz > 0)
          {
            phase += 2 * Math.PI * hz / 48000;
            if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
          }
          samples.Add(hz == 0 ? 0 : (float)(.7 * Math.Sin(phase)));
        }
      }
      Tone(0, 910);
      for (int i = 0; i < lines; i++)
      {
        Tone(realSync ? 1200 + syncOffsetHz : 1800, 9);
        Tone(1800, 141);
      }
      return samples.ToArray();
    }

    [Theory]
    [InlineData(25.0)]
    [InlineData(-35.0)]
    public void RobotHorizontalSyncCorrectsFrequencyDriftAfterVis(
      double trueOffsetHz)
    {
      var tracker = new SstvNativeLineSyncAfc();
      tracker.Begin(new SstvVisAfcLock(
        0x88, 0, 3, 900 * 48, .9),
        SstvModes.Get(SstvMode.Robot36));
      float[] audio = Picture(25, trueOffsetHz, true);
      int updates = 0;
      tracker.OffsetUpdated += _ => updates++;
      for (int i = 0; i < audio.Length; i += 1600)
        tracker.Process(audio.AsSpan(
          i, Math.Min(1600, audio.Length - i)));

      Assert.True(tracker.AcceptedSyncs >= 15,
        $"Received only {tracker.AcceptedSyncs} valid horizontal syncs.");
      Assert.Equal(tracker.AcceptedSyncs, updates);
      Assert.InRange(tracker.EstimatedOffsetHz,
        trueOffsetHz - 12, trueOffsetHz + 12);
    }

    [Fact]
    public void MissingHorizontalSyncMustHoldTheLastVerifiedOffset()
    {
      var tracker = new SstvNativeLineSyncAfc();
      tracker.Begin(new SstvVisAfcLock(
        0x88, 45, 1, 900 * 48, .9),
        SstvModes.Get(SstvMode.Robot36));
      tracker.Process(Picture(10, 0, false));
      Assert.Equal(0, tracker.AcceptedSyncs);
      Assert.Equal(45, tracker.EstimatedOffsetHz);
      tracker.Reset();
      Assert.False(tracker.Active);
    }
  }
}
