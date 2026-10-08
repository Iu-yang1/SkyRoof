using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class RotatorManualControlTests
{
    [Fact]
    public void LiveTrackedPassLocksManualControl()
    {
        Assert.True(
            RotatorWidget.ShouldLockManualControl(
                isTracking: true,
                passIsActive: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PrePositioningOrTrackOffKeepsManualControlAvailable(
        bool isTracking,
        bool passIsActive)
    {
        Assert.False(
            RotatorWidget.ShouldLockManualControl(
                isTracking,
                passIsActive));
    }

    [Fact]
    public void ClampManualTargetUsesConfiguredRotatorLimits()
    {
        var settings = new RotatorSettings
        {
            MinAzimuth = 0,
            MaxAzimuth = 450,
            MinElevation = 0,
            MaxElevation = 180
        };

        var target =
            RotatorWidget.ClampManualTarget(
                472.5,
                -12.0,
                settings);

        Assert.Equal(450, target.AzimuthDeg);
        Assert.Equal(0, target.ElevationDeg);
    }

    [Fact]
    public void ClampManualTargetAlsoHandlesReversedLimits()
    {
        var settings = new RotatorSettings
        {
            MinAzimuth = 450,
            MaxAzimuth = 0,
            MinElevation = 180,
            MaxElevation = 0
        };

        var target =
            RotatorWidget.ClampManualTarget(
                123.4,
                56.7,
                settings);

        Assert.Equal(123.4, target.AzimuthDeg, 6);
        Assert.Equal(56.7, target.ElevationDeg, 6);
    }
}
