using FluentAssertions;
using SkyRoof;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwSatelliteTransmitInterlockTests
  {
    [Fact]
    public void DopplerOnlyChange_DoesNotInvalidateArmedContext()
    {
      CwTransmitInterlockSnapshot armed =
        Snapshot(
          noDopplerHz: 435000000,
          correctedHz: 435001200,
          expectedCatHz: 435001200);

      CwTransmitInterlockSnapshot later =
        Snapshot(
          noDopplerHz: 435000000,
          correctedHz: 435001470,
          expectedCatHz: 435001470);

      Action compare =
        () =>
          CwSatelliteTransmitInterlock
            .EnsureSameOperatorContext(
              armed,
              later);

      compare.Should().NotThrow();
    }

    [Fact]
    public void OperatorUplinkChange_InvalidatesArmedContext()
    {
      CwTransmitInterlockSnapshot armed =
        Snapshot(
          noDopplerHz: 435000000,
          correctedHz: 435001200,
          expectedCatHz: 435001200);

      CwTransmitInterlockSnapshot changed =
        Snapshot(
          noDopplerHz: 435000025,
          correctedHz: 435001225,
          expectedCatHz: 435001225);

      Action compare =
        () =>
          CwSatelliteTransmitInterlock
            .EnsureSameOperatorContext(
              armed,
              changed);

      compare.Should()
        .Throw<InvalidOperationException>()
        .WithMessage(
          "*operator uplink tuning position changed*");
    }

    [Fact]
    public void SatelliteOrTransmitterChange_InvalidatesArmedContext()
    {
      CwTransmitInterlockSnapshot armed =
        Snapshot();

      Action satellite =
        () =>
          CwSatelliteTransmitInterlock
            .EnsureSameOperatorContext(
              armed,
              Snapshot(
                satelliteId: "SAT-2"));

      Action transmitter =
        () =>
          CwSatelliteTransmitInterlock
            .EnsureSameOperatorContext(
              armed,
              Snapshot(
                transmitterId: "TX-2"));

      satellite.Should()
        .Throw<InvalidOperationException>();
      transmitter.Should()
        .Throw<InvalidOperationException>();
    }

    [Fact]
    public void TxModeOrTransverterMappingChange_InvalidatesArmedContext()
    {
      CwTransmitInterlockSnapshot armed =
        Snapshot();

      Action mode =
        () =>
          CwSatelliteTransmitInterlock
            .EnsureSameOperatorContext(
              armed,
              Snapshot(
                uplinkMode:
                  Slicer.Mode.USB));

      Action mapping =
        () =>
          CwSatelliteTransmitInterlock
            .EnsureSameOperatorContext(
              armed,
              Snapshot(
                loOffsetHz:
                  116000000));

      mode.Should()
        .Throw<InvalidOperationException>();
      mapping.Should()
        .Throw<InvalidOperationException>();
    }

    [Fact]
    public void SingleFrequencyUplink_UsesCorrectedBasePlusMinusFiveKhz()
    {
      var link = new RadioLink
      {
        TxCust =
          new TransmitterCustomization
          {
            UplinkBaseOffset = 1200
          }
      };
      var tx =
        new SatnogsDbTransmitter
        {
          uplink_low = 435000000,
          uplink_high = null
        };

      Action inside =
        () =>
          CwSatelliteTransmitInterlock
            .ValidatePublishedUplinkPassband(
              link,
              tx,
              435006199);

      Action outside =
        () =>
          CwSatelliteTransmitInterlock
            .ValidatePublishedUplinkPassband(
              link,
              tx,
              435006201);

      inside.Should().NotThrow();
      outside.Should()
        .Throw<InvalidOperationException>()
        .WithMessage(
          "*single-frequency uplink*");
    }

    [Fact]
    public void LinearUplink_UsesBaseCorrectedPublishedPassband()
    {
      var link = new RadioLink
      {
        TxCust =
          new TransmitterCustomization
          {
            UplinkBaseOffset = -500
          }
      };
      var tx =
        new SatnogsDbTransmitter
        {
          uplink_low = 145900000,
          uplink_high = 146000000
        };

      Action inside =
        () =>
          CwSatelliteTransmitInterlock
            .ValidatePublishedUplinkPassband(
              link,
              tx,
              145899400);

      Action outside =
        () =>
          CwSatelliteTransmitInterlock
            .ValidatePublishedUplinkPassband(
              link,
              tx,
              145899399);

      inside.Should().NotThrow();
      outside.Should()
        .Throw<InvalidOperationException>()
        .WithMessage(
          "*outside the selected transmitter passband*");
    }

    private static CwTransmitInterlockSnapshot
      Snapshot(
        string satelliteId = "SAT-1",
        string transmitterId = "TX-1",
        double noDopplerHz = 435000000,
        double correctedHz = 435001200,
        long expectedCatHz = 435001200,
        double loOffsetHz = 0,
        Slicer.Mode uplinkMode =
          Slicer.Mode.CW) =>
      new(
        IsSatellite: true,
        SatelliteId: satelliteId,
        TransmitterId: transmitterId,
        UplinkWithoutDopplerHz:
          noDopplerHz,
        CorrectedUplinkHz:
          correctedHz,
        ExpectedCatTxHz:
          expectedCatHz,
        CatLoOffsetHz:
          loOffsetHz,
        FrequencyToleranceHz: 100,
        UplinkMode: uplinkMode);
  }
}
