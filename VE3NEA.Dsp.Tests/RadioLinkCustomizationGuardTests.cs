using System;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class RadioLinkCustomizationGuardTests
{
  [Fact]
  public void MissingTransmitterCustomizationFailsBeforeReadOrWrite()
  {
    var link = new RadioLink();

    var readError = Assert.Throws<InvalidOperationException>(
      () => _ = link.DownlinkBaseOffset);
    Assert.Contains("Transmitter customization", readError.Message);

    var writeError = Assert.Throws<InvalidOperationException>(
      () => link.UplinkBaseOffset = 125);
    Assert.Contains("Transmitter customization", writeError.Message);
  }

  [Fact]
  public void MissingSatelliteCustomizationFailsBeforeDopplerStateUse()
  {
    var link = new RadioLink();

    var readError = Assert.Throws<InvalidOperationException>(
      () => _ = link.DownlinkDopplerCorrectionEnabled);
    Assert.Contains("Satellite customization", readError.Message);

    var writeError = Assert.Throws<InvalidOperationException>(
      () => link.UplinkManualCorrectionEnabled = false);
    Assert.Contains("Satellite customization", writeError.Message);
  }

  [Fact]
  public void InitializedCustomizationRetainsExistingObjectAndValues()
  {
    var transmitter = new TransmitterCustomization();
    var satellite = new SatelliteCustomization();
    var link = new RadioLink { TxCust = transmitter, SatCust = satellite };

    link.DownlinkBaseOffset = 2500;
    link.UplinkBaseOffset = -1250;
    link.TransponderOffset = 175.5;
    link.DownlinkDopplerCorrectionEnabled = true;
    link.UplinkDopplerCorrectionEnabled = false;
    link.DownlinkManualCorrection = 27.8;

    Assert.Same(transmitter, link.TxCust);
    Assert.Same(satellite, link.SatCust);
    Assert.Equal(2500, transmitter.DownlinkBaseOffset);
    Assert.Equal(-1250, transmitter.UplinkBaseOffset);
    Assert.Equal(175.5, link.TransponderOffset);
    Assert.True(link.DownlinkDopplerCorrectionEnabled);
    Assert.False(link.UplinkDopplerCorrectionEnabled);
    Assert.Equal(27, link.DownlinkManualCorrection);
  }
}
