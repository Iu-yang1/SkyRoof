using System.Drawing;
using FluentAssertions;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IcomScopeRxPassbandTests
  {
    private const long Dial = 435_600_000;

    [Theory]
    [InlineData(Slicer.Mode.USB, 300, 2700)]
    [InlineData(Slicer.Mode.LSB, -2700, -300)]
    [InlineData(Slicer.Mode.USB_D, 900, 2100)]
    [InlineData(Slicer.Mode.LSB_D, -2100, -900)]
    [InlineData(Slicer.Mode.CW, -250, 250)]
    [InlineData(Slicer.Mode.FM, -7500, 7500)]
    [InlineData(Slicer.Mode.FM_D, -7500, 7500)]
    public void Ic9700NominalRxFilter_IsNotSkyRoofSdrSlicerBandwidth(
      Slicer.Mode mode, long lowHz, long highHz)
    {
      IcomScopeRxPassbandEstimator.TryEstimate(
        mode, Dial, new IcomLanSpectrumSettings(),
        out IcomScopeRxPassband result).Should().BeTrue();
      result.LowerHz.Should().Be(Dial + lowHz);
      result.UpperHz.Should().Be(Dial + highHz);
    }

    [Fact]
    public void FilterOverride_ChangesEstimatedWidthAndPreservesSidebandSense()
    {
      var settings = new IcomLanSpectrumSettings
      {
        RxSsbEstimatedBandwidthHz = 1800,
        RxCwEstimatedBandwidthHz = 250,
        RxFmEstimatedBandwidthHz = 7000
      };
      IcomScopeRxPassbandEstimator.TryEstimate(
        Slicer.Mode.USB, Dial, settings, out var usb).Should().BeTrue();
      IcomScopeRxPassbandEstimator.TryEstimate(
        Slicer.Mode.LSB, Dial, settings, out var lsb).Should().BeTrue();
      usb.WidthHz.Should().Be(1800);
      lsb.WidthHz.Should().Be(1800);
      usb.LowerHz.Should().BeGreaterThan(Dial);
      lsb.UpperHz.Should().BeLessThan(Dial);

      IcomScopeRxPassbandEstimator.TryEstimate(
        Slicer.Mode.CW, Dial, settings, out var cw).Should().BeTrue();
      cw.WidthHz.Should().Be(250);
      IcomScopeRxPassbandEstimator.TryEstimate(
        Slicer.Mode.FM, Dial, settings, out var fm).Should().BeTrue();
      fm.WidthHz.Should().Be(7000);
    }

    [Fact]
    public void UnavailableOrInvalidModeAndUntrustedSettings_DoNotPaintFakePassband()
    {
      var settings = new IcomLanSpectrumSettings();
      IcomScopeRxPassbandEstimator.TryEstimate(
        null, Dial, settings, out _).Should().BeFalse();
      IcomScopeRxPassbandEstimator.TryEstimate(
        Slicer.Mode.USB, 0, settings, out _).Should().BeFalse();
      IcomScopeRxPassbandEstimator.TryEstimate(
        (Slicer.Mode)200, Dial, settings, out _).Should().BeFalse();
      settings.RxSsbEstimatedBandwidthHz = 0;
      IcomScopeRxPassbandEstimator.TryEstimate(
        Slicer.Mode.USB, Dial, settings, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(25000L, 1)]
    [InlineData(25000L, 2)]
    [InlineData(50000L, 1)]
    [InlineData(50000L, 4)]
    [InlineData(100000L, 1)]
    [InlineData(100000L, 8)]
    public void SpanAndZoom_ProduceCorrectRxWidthInPixels(
      long span, int zoom)
    {
      var frame = new IcomScopeFrame
      {
        Scope = 0,
        Mode = (byte)IcomScopeMode.Center,
        FrequencyAHz = Dial,
        FrequencyBHz = span
      };
      var plot = new Rectangle(30, 10, 801, 120);
      IcomScopeRxPassbandEstimator.TryEstimate(
        Slicer.Mode.USB, Dial, new IcomLanSpectrumSettings(),
        out var band).Should().BeTrue();

      IcomLanSpectrumView.TryGetPassbandPlotBounds(
        frame.Geometry, 0, band, plot, zoom, 0.5,
        out Rectangle result).Should().BeTrue();
      double expected = 2400.0 / span * zoom * (plot.Width - 1);
      ((double)result.Width).Should().BeApproximately(expected, 1.1);
      result.Left.Should().BeGreaterThan(plot.Left);
      result.Right.Should().BeLessThan(plot.Right);
    }

    [Fact]
    public void FixedScopeAndDopplerOrTransverterOffset_UseSameAxisAsTrace()
    {
      const long offset = 28_000_000;
      var fixedFrame = new IcomScopeFrame
      {
        Scope = 0,
        Mode = (byte)IcomScopeMode.Fixed,
        FrequencyAHz = Dial - 50_000,
        FrequencyBHz = Dial + 50_000
      };
      // The visible radio axis is 28 MHz below SkyRoof's corrected
      // RF frequency when a transverter CAT offset is in use.
      var corrected = new IcomScopeRxPassband(
        Dial + offset + 300, Dial + offset + 2700);
      var plot = new Rectangle(20, 30, 1001, 100);

      IcomLanSpectrumView.TryGetPassbandPlotBounds(
        fixedFrame.Geometry, offset, corrected, plot, 2, 0.5,
        out Rectangle draw).Should().BeTrue();
      ((double)draw.Width).Should().BeApproximately(48, 1);
      draw.Left.Should().BeGreaterThan(plot.Left + plot.Width / 2);
    }

    [Fact]
    public void EntirelyClippedPassbandMustNotDrawAOnePixelRedArtifact()
    {
      var frame = new IcomScopeFrame
      {
        Mode = (byte)IcomScopeMode.Center,
        FrequencyAHz = Dial,
        FrequencyBHz = 25_000
      };
      var plot = new Rectangle(10, 5, 801, 80);
      var farAway = new IcomScopeRxPassband(
        Dial + 30_000, Dial + 32_400);
      IcomLanSpectrumView.TryGetPassbandPlotBounds(
        frame.Geometry, 0, farAway, plot, 1, 0.5,
        out _).Should().BeFalse();
    }
  }
}
