using System.Diagnostics;
using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwDisplayCadenceMeterTests
  {
    [Fact]
    public void ThirtyFpsDelivery_ReportsMeasuredFpsAndSteadyP95()
    {
      var meter = new CwDisplayCadenceMeter();
      long step = Stopwatch.Frequency / 30;
      long start = 10 * Stopwatch.Frequency;
      for (int i = 0; i <= 60; i++)
        meter.Record(start + i * step);

      var metrics = meter.Snapshot(start + 60 * step);
      metrics.ActualFps.Should().BeApproximately(30, 0.02);
      metrics.P95IntervalMs.Should().BeApproximately(
        1000.0 / 30, 0.05);
      metrics.RecentFrames.Should().Be(61);
    }

    [Fact]
    public void MissedUiPaint_IsVisibleInP95AndNotCountedAsNewFrame()
    {
      var meter = new CwDisplayCadenceMeter();
      long step = Stopwatch.Frequency / 30;
      long start = 10 * Stopwatch.Frequency;
      // The UI misses three presentation opportunities twice: this
      // should be reflected in actual FPS, not masked by 33-ms timer.
      long current = start;
      for (int i = 0; i < 30; i++)
      {
        current += step * (i is 8 or 23 ? 4 : 1);
        meter.Record(current);
      }
      var metrics = meter.Snapshot(current);
      metrics.P95IntervalMs.Should().BeGreaterThan(100);
      metrics.ActualFps.Should().BeLessThan(27);
      metrics.MaximumIntervalMs.Should().BeGreaterThan(120);
    }

    [Fact]
    public void NoRecentFrames_ReportsZeroRatherThanStaleFps()
    {
      var meter = new CwDisplayCadenceMeter();
      long first = 10 * Stopwatch.Frequency;
      meter.Record(first);
      meter.Record(first + Stopwatch.Frequency / 30);
      meter.Snapshot(first + 4 * Stopwatch.Frequency)
        .ActualFps.Should().Be(0);
      meter.Reset();
      meter.Snapshot(first + 5 * Stopwatch.Frequency)
        .RecentFrames.Should().Be(0);
    }

    [Fact]
    public void MeterKeepsBoundedRingDuringLongReception()
    {
      var meter = new CwDisplayCadenceMeter();
      long step = Stopwatch.Frequency / 30;
      long start = 10 * Stopwatch.Frequency;
      for (int i = 0; i < 5000; i++)
        meter.Record(start + i * step);
      var metrics = meter.Snapshot(start + 4999 * step);
      metrics.RecentFrames.Should().BeInRange(59, 62);
      metrics.ActualFps.Should().BeApproximately(30, 0.02);
    }
  }
}
