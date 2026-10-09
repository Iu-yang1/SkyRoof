using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class CustomTransmitterTests
{
    [Theory]
    [InlineData("", null)]
    [InlineData("145.900000", 145900000L)]
    [InlineData("435.250", 435250000L)]
    public void FrequencyParserAcceptsBlankOrMhz(
        string text,
        long? expected)
    {
        Assert.True(
            CustomTransmitterDialog.TryParseFrequency(
                text,
                out long? frequency));

        Assert.Equal(
            expected,
            frequency);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-145.9")]
    [InlineData("not-a-frequency")]
    public void FrequencyParserRejectsInvalidValues(
        string text)
    {
        Assert.False(
            CustomTransmitterDialog.TryParseFrequency(
                text,
                out _));
    }

    [Fact]
    public void UplinkOnlyLocalTransmitterIsSafeForRadioLink()
    {
        var satellite =
            new SatnogsDbSatellite
            {
                sat_id = "TEST-SAT",
                norad_cat_id = 12345,
                name = "TEST"
            };

        var definition =
            new CustomTransmitterDefinition
            {
                uuid = "local-test",
                description = "Uplink only",
                uplink_hz = 435250000,
                mode = "FM"
            };

        SatnogsDbTransmitter tx =
            SatnogsDb.CreateCustomTransmitter(
                definition,
                satellite);

        Assert.True(tx.local_custom);
        Assert.Null(tx.downlink_low);
        Assert.Equal(435250000, tx.uplink_low);
        Assert.True(tx.IsUhf());

        var link =
            new RadioLink
            {
                IsTerrestrial = false,
                Tx = tx,
                SatCust = new SatelliteCustomization(),
                TxCust = new TransmitterCustomization()
            };

        link.ComputeFrequencies();

        Assert.False(link.HasDownlink);
        Assert.True(link.HasUplink);
        Assert.Equal(0, link.DownlinkFrequency);
        Assert.Equal(0, link.CorrectedDownlinkFrequency);
        Assert.Equal(435250000, link.UplinkFrequency);
    }

    [Fact]
    public void LocalModeIsPreservedAndMapped()
    {
        var satellite =
            new SatnogsDbSatellite
            {
                sat_id = "TEST-SAT",
                norad_cat_id = 12345,
                name = "TEST"
            };

        var definition =
            new CustomTransmitterDefinition
            {
                uuid = "local-ft4",
                description = "FT4 downlink",
                downlink_hz = 435620000,
                mode = "FT4"
            };

        SatnogsDbTransmitter tx =
            SatnogsDb.CreateCustomTransmitter(
                definition,
                satellite);

        Assert.Equal("FT4", tx.mode);
        Assert.Equal("FT4", tx.DownlinkMode);

        var settings =
            new SatelliteSettings();
        TransmitterCustomization customization =
            settings.GetOrCreateTransmitterCustomization(
                tx);

        Assert.Equal(
            Slicer.Mode.USB_D,
            customization.DownlinkMode);
    }

    [Fact]
    public void EditingLocalTransmitterKeepsUuidAndExistingObject()
    {
        var satellite = new SatnogsDbSatellite {
          sat_id = "TEST-SAT", norad_cat_id = 12345, name = "TEST"
        };
        var original = new CustomTransmitterDefinition {
          uuid = "local-stable", description = "Old",
          downlink_hz = 436210000L, mode = "FM"
        };
        SatnogsDbTransmitter tx =
          SatnogsDb.CreateCustomTransmitter(original, satellite);
        var reference = tx;

        var replacement = new CustomTransmitterDefinition {
          uuid = "local-stable", description = "Digital downlink",
          downlink_hz = 436220000L,
          uplink_hz = 145850000L,
          mode = "FM_D",
          updated_utc = DateTime.UtcNow
        };
        SatnogsDb.UpdateCustomTransmitterRuntime(tx, replacement);

        Assert.Same(reference, tx);
        Assert.Equal("local-stable", tx.uuid);
        Assert.Equal("Digital downlink", tx.description);
        Assert.Equal(436220000L, tx.downlink_low);
        Assert.Equal(145850000L, tx.uplink_low);
        Assert.Equal("FM_D", tx.DownlinkMode);
        Assert.Equal(Slicer.Mode.FM_D,
          SatnogsDbTransmitter.ModeMnemonic.ToSlicerMode(
            null, tx.DownlinkMode, false));
    }

    [Theory]
    [InlineData("FM_D")]
    [InlineData("FM-D")]
    [InlineData("FM DATA")]
    public void FmDigitalNamesMapToFmDigitalRadioMode(string mode)
    {
        Assert.Equal(Slicer.Mode.FM_D,
          SatnogsDbTransmitter.ModeMnemonic.ToSlicerMode(null, mode, false));
    }

    [Fact]
    public void EditingRejectsDifferentUuid()
    {
        var satellite = new SatnogsDbSatellite { sat_id = "TEST" };
        var original = new CustomTransmitterDefinition {
          uuid = "local-a", description = "A", downlink_hz = 145900000
        };
        SatnogsDbTransmitter tx = SatnogsDb.CreateCustomTransmitter(original, satellite);
        Assert.Throws<InvalidOperationException>(() =>
          SatnogsDb.UpdateCustomTransmitterRuntime(tx,
            new CustomTransmitterDefinition {
              uuid = "local-b", description = "B", downlink_hz = 145910000
            }));
    }

    [Fact]
    public void RemovingLocalDefinitionOnlyDeletesMatchingUuid()
    {
        var definitions =
            new CustomTransmitterDefinitionList
            {
                new()
                {
                    uuid = "local-a",
                    description = "A",
                    downlink_hz = 145900000
                },
                new()
                {
                    uuid = "local-b",
                    description = "B",
                    uplink_hz = 435250000
                }
            };

        Assert.True(
            SatnogsDb.RemoveCustomTransmitterDefinition(
                definitions,
                "LOCAL-A"));

        Assert.Single(definitions);
        Assert.Equal(
            "local-b",
            definitions[0].uuid);
    }

    [Fact]
    public void RemovingUnknownLocalDefinitionDoesNothing()
    {
        var definitions =
            new CustomTransmitterDefinitionList
            {
                new()
                {
                    uuid = "local-a",
                    description = "A",
                    downlink_hz = 145900000
                }
            };

        Assert.False(
            SatnogsDb.RemoveCustomTransmitterDefinition(
                definitions,
                "local-missing"));

        Assert.Single(definitions);
    }

    [Fact]
    public void RemovingTransmitterCustomizationClearsSelectedLocalId()
    {
        var satellite =
            new SatnogsDbSatellite
            {
                sat_id = "TEST-SAT",
                norad_cat_id = 12345,
                name = "TEST"
            };

        var settings =
            new SatelliteSettings();
        settings.TransmitterCustomizations["local-a"] =
            new TransmitterCustomization
            {
                uuid = "local-a"
            };
        settings.SatelliteCustomizations["TEST-SAT"] =
            new SatelliteCustomization
            {
                SelectedTransmitterId = "local-a"
            };

        settings.RemoveTransmitterCustomization(
            satellite,
            "local-a");

        Assert.False(
            settings.TransmitterCustomizations.ContainsKey(
                "local-a"));
        Assert.Null(
            settings.SatelliteCustomizations["TEST-SAT"]
                .SelectedTransmitterId);
    }

    [Fact]
    public void BothDirectionsArePreserved()
    {
        var satellite =
            new SatnogsDbSatellite
            {
                sat_id = "TEST-SAT",
                norad_cat_id = 12345,
                name = "TEST"
            };

        var definition =
            new CustomTransmitterDefinition
            {
                uuid = "local-both",
                description = "FM repeater",
                downlink_hz = 435800000,
                uplink_hz = 145800000,
                mode = "FM"
            };

        SatnogsDbTransmitter tx =
            SatnogsDb.CreateCustomTransmitter(
                definition,
                satellite);

        Assert.Equal("FM repeater", tx.description);
        Assert.Equal(435800000, tx.downlink_low);
        Assert.Equal(145800000, tx.uplink_low);
        Assert.Equal("FM", tx.mode);
        Assert.Equal("Transceiver", tx.type);
    }
}
