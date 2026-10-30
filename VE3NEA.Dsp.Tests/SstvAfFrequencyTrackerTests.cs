using System;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class SstvAfFrequencyTrackerTests
  {
    private static readonly DateTime StartUtc =
      new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

    private static SstvAfFrequencyObservation Sample(
      int seconds, double toneOffsetHz, long epoch = 1, double confidence = 1.0) =>
      new(StartUtc.AddSeconds(seconds), epoch, toneOffsetHz, confidence);

    [Fact]
    public void DoesNotApplyCorrectionBeforeThreeConsistentObservations()
    {
      var tracker = new SstvAfFrequencyTracker();
      Assert.True(tracker.TryObserve(Sample(0, 125)));
      Assert.True(tracker.TryObserve(Sample(1, 125)));

      var acquiring = tracker.GetSnapshot(StartUtc.AddSeconds(1));
      Assert.False(acquiring.Locked);
      Assert.Equal(0.0, acquiring.CorrectionHz);

      Assert.True(tracker.TryObserve(Sample(2, 125)));
      var locked = tracker.GetSnapshot(StartUtc.AddSeconds(2));
      Assert.True(locked.Locked);
      Assert.Equal(-125.0, locked.CorrectionHz, precision: 6);
    }

    [Fact]
    public void IgnoresLowConfidenceInvalidAndOutOfOrderMeasurements()
    {
      var tracker = new SstvAfFrequencyTracker();
      Assert.False(tracker.TryObserve(Sample(0, 100, confidence: 0.5)));
      Assert.False(tracker.TryObserve(Sample(0, double.NaN)));
      Assert.True(tracker.TryObserve(Sample(1, 100)));
      Assert.False(tracker.TryObserve(Sample(1, 99)));
      Assert.False(tracker.TryObserve(Sample(0, 99)));
      Assert.False(tracker.TryObserve(Sample(2, 3000)));

      var snapshot = tracker.GetSnapshot(StartUtc.AddSeconds(2));
      Assert.Equal(1, snapshot.AcceptedObservations);
      Assert.False(snapshot.Locked);
    }

    [Fact]
    public void RejectsIsolatedLargeJumpAndResetsAfterNewSignalEpoch()
    {
      var tracker = new SstvAfFrequencyTracker();
      Assert.True(tracker.TryObserve(Sample(0, 100)));
      Assert.True(tracker.TryObserve(Sample(1, 100)));
      Assert.True(tracker.TryObserve(Sample(2, 100)));
      Assert.False(tracker.TryObserve(Sample(3, 450)));
      Assert.Equal(3, tracker.GetSnapshot(StartUtc.AddSeconds(3)).AcceptedObservations);

      Assert.True(tracker.TryObserve(Sample(4, -50, epoch: 2)));
      var snapshot = tracker.GetSnapshot(StartUtc.AddSeconds(4));
      Assert.Equal(2L, snapshot.Epoch);
      Assert.Equal(1, snapshot.AcceptedObservations);
      Assert.False(snapshot.Locked);
      Assert.Equal(0.0, snapshot.CorrectionHz);
      Assert.Equal(-50.0, snapshot.EstimatedOffsetHz, precision: 6);
    }

    [Fact]
    public void DoesNotUseStaleLockAndReacquiresAfterGap()
    {
      var tracker = new SstvAfFrequencyTracker();
      for (int i = 0; i < 5; i++)
        Assert.True(tracker.TryObserve(Sample(i, 70 + 2 * i)));

      Assert.True(tracker.GetSnapshot(StartUtc.AddSeconds(4)).Locked);
      Assert.True(tracker.GetSnapshot(StartUtc.AddSeconds(4)).RateHzPerSecond > 0);

      var stale = tracker.GetSnapshot(StartUtc.AddSeconds(9));
      Assert.False(stale.Locked);
      Assert.Equal(0.0, stale.CorrectionHz);

      Assert.True(tracker.TryObserve(Sample(9, 88)));
      var reacquiring = tracker.GetSnapshot(StartUtc.AddSeconds(9));
      Assert.Equal(1, reacquiring.AcceptedObservations);
      Assert.False(reacquiring.Locked);
    }

    [Fact]
    public void RejectsLocalClockTimestamps()
    {
      var tracker = new SstvAfFrequencyTracker();
      Assert.False(tracker.TryObserve(
        new SstvAfFrequencyObservation(
          DateTime.SpecifyKind(StartUtc, DateTimeKind.Local), 1, 10, 1.0)));
      Assert.Throws<ArgumentException>(() =>
        tracker.GetSnapshot(DateTime.SpecifyKind(StartUtc, DateTimeKind.Unspecified)));
    }
  }
}
