using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class RotatorParkSequenceTests
{
    private static readonly DateTime Start =
      new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TwoFreshArrivalsAtEachWaypointAreRequired()
    {
        var seq = new RotatorParkSequence(
          new[] { (90.0, 0.0), (0.0, 0.0) }, Start);

        Assert.Equal(ParkSequenceProgress.Waiting,
          seq.Observe(Start.AddSeconds(1), Start.AddSeconds(1), 90, 0, true));
        Assert.Equal(0, seq.Index);
        Assert.Equal(ParkSequenceProgress.Waiting,
          seq.Observe(Start.AddSeconds(2), Start.AddSeconds(1), 90, 0, true));
        Assert.Equal(0, seq.Index);

        Assert.Equal(ParkSequenceProgress.NextWaypoint,
          seq.Observe(Start.AddSeconds(3), Start.AddSeconds(3), 90.5, 0.5, true));
        Assert.Equal(1, seq.Index);
        Assert.Equal(0, seq.Current.Az);

        Assert.Equal(ParkSequenceProgress.Waiting,
          seq.Observe(Start.AddSeconds(4), Start.AddSeconds(4), 0.3, 0, true));
        Assert.Equal(ParkSequenceProgress.Completed,
          seq.Observe(Start.AddSeconds(5), Start.AddSeconds(5), 0, 0, true));
    }

    [Fact]
    public void MechanicalAzimuthDoesNotWrapBetweenZeroAnd360()
    {
        var seq = new RotatorParkSequence(new[] { (0.0, 0.0) }, Start);
        Assert.Equal(ParkSequenceProgress.Waiting,
          seq.Observe(Start.AddSeconds(1), Start.AddSeconds(1), 360, 0, true));
        Assert.Equal(ParkSequenceProgress.Waiting,
          seq.Observe(Start.AddSeconds(2), Start.AddSeconds(2), 360, 0, true));
        Assert.Equal(0, seq.Index);
    }

    [Fact]
    public void InterveningOutOfToleranceSampleResetsArrivalCount()
    {
        var seq = new RotatorParkSequence(new[] { (90.0, 0.0) }, Start);
        Assert.Equal(ParkSequenceProgress.Waiting,
          seq.Observe(Start.AddSeconds(1), Start.AddSeconds(1), 90, 0, true));
        Assert.Equal(ParkSequenceProgress.Waiting,
          seq.Observe(Start.AddSeconds(2), Start.AddSeconds(2), 85, 0, true));
        Assert.Equal(ParkSequenceProgress.Waiting,
          seq.Observe(Start.AddSeconds(3), Start.AddSeconds(3), 90, 0, true));
        Assert.Equal(ParkSequenceProgress.Completed,
          seq.Observe(Start.AddSeconds(4), Start.AddSeconds(4), 90, 0, true));
    }

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(true, 30, 1)]
    [InlineData(true, 310, 310)]
    public void ConnectionFeedbackAndTimeoutAreFailClosed(
      bool connected, int nowSeconds, int readSeconds)
    {
        var seq = new RotatorParkSequence(new[] { (90.0, 0.0) }, Start);
        Assert.Equal(ParkSequenceProgress.Aborted,
          seq.Observe(Start.AddSeconds(nowSeconds),
            Start.AddSeconds(readSeconds), 90, 0, connected));
    }

    [Fact]
    public void InvalidWaypointsAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
          new RotatorParkSequence(Array.Empty<(double, double)>(), Start));
        Assert.Throws<ArgumentException>(() =>
          new RotatorParkSequence(new[] { (double.NaN, 0.0) }, Start));
    }
}
