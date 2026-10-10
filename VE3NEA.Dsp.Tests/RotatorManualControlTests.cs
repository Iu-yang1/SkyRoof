using System.Drawing;
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

    [Theory]
    [InlineData(1.0f, 0.5)]
    [InlineData(5.0f, 2.5)]
    [InlineData(12.0f, 6.0)]
    public void TrackingStepChangesCommandTriggerThreshold(
        float stepDegrees,
        double expectedTriggerDegrees)
    {
        double radians =
            RotatorWidget.TrackingTriggerAngleRadians(
                stepDegrees);

        Assert.Equal(
            expectedTriggerDegrees,
            radians / VE3NEA.Geo.RinD,
            6);
    }

    [Fact]
    public void ManualPopupIsKeptInsideWorkingArea()
    {
        var workingArea = new Rectangle(100, 50, 800, 600);
        var popupSize = new Size(320, 390);

        var location =
            MainForm.ClampPopupLocation(
                workingArea,
                popupSize,
                new Point(850, 620));

        Assert.Equal(new Point(580, 260), location);
    }

    [Fact]
    public void ManualPopupKeepsRequestedPointWhenItAlreadyFits()
    {
        var workingArea = new Rectangle(100, 50, 800, 600);
        var popupSize = new Size(320, 390);

        var location =
            MainForm.ClampPopupLocation(
                workingArea,
                popupSize,
                new Point(200, 120));

        Assert.Equal(new Point(200, 120), location);
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

    [Fact]
    public void ParkEditorBoundsStayInsideOwnerMonitorWhenNearBottomRight()
    {
        Rectangle workingArea = new(1920, 0, 1920, 1040);
        Rectangle window = new(3530, 920, 490, 345);

        Rectangle actual = ParkPositionDialog.ClampToWorkingArea(
            window, workingArea);

        Assert.Equal(new Rectangle(3350, 695, 490, 345), actual);
    }

    [Fact]
    public void ParkEditorClampSupportsNegativeMonitorCoordinates()
    {
        Rectangle workingArea = new(-1920, -1080, 1920, 1040);
        Rectangle window = new(-2180, -1260, 490, 345);

        Rectangle actual = ParkPositionDialog.ClampToWorkingArea(
            window, workingArea);

        Assert.Equal(new Rectangle(-1920, -1080, 490, 345), actual);
    }

    [Fact]
    public void ParkEditorKeepsValidCenteredBoundsUnchanged()
    {
        Rectangle workingArea = new(0, 0, 1920, 1040);
        Rectangle centered = new(715, 347, 490, 345);

        Assert.Equal(centered,
            ParkPositionDialog.ClampToWorkingArea(centered, workingArea));
    }

    [Fact]
    public void ParkEditorShrinkFitsVerySmallWorkingArea()
    {
        Rectangle workingArea = new(0, 0, 400, 300);
        Rectangle original = new(300, 250, 490, 345);

        Assert.Equal(new Rectangle(0, 0, 400, 300),
            ParkPositionDialog.ClampToWorkingArea(original, workingArea));
    }
}
