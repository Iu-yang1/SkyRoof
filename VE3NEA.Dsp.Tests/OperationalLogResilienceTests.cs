using System;
using System.Net.WebSockets;
using SkyRoof;
using VE3NEA;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class OperationalLogResilienceTests
{
    [Theory]
    [InlineData("Athene-1;;;137.500/848.000/2441.750::::unknown")]
    [InlineData("GHGSAT C18 Eleanor;;;;;;unknown")]
    [InlineData("PTS-1:::::::unknown")]
    [InlineData("GNOMES-10;;;;;;unknown")]
    public void IncompleteJe9pelRowsAreSkippedWithoutIndexExceptions(string input)
    {
        var row = new JE9PELtransmitter(input);
        Assert.NotNull(row.Name);
        Assert.NotNull(row.Status);
        Assert.False(JE9PELtransmitter.TryParse(input, out _));
    }

    [Fact]
    public void Je9pelValidNoradAndShortOptionalTailArePreserved()
    {
        const string row = "RS-44;44909;145.935-145.995;435.670-435.610;435.605;SSB CW";
        Assert.True(JE9PELtransmitter.TryParse(row, out var transmitter));
        Assert.NotNull(transmitter);
        Assert.Equal(44909, transmitter!.NoradId);
        Assert.Equal("RS-44", transmitter.Name);
        Assert.Equal("145.935-145.995", transmitter.Uplink);
        Assert.Equal("435.670-435.610", transmitter.Downlink);
        Assert.Equal("SSB CW", transmitter.Mode);
        Assert.Equal(string.Empty, transmitter.Call);
        Assert.Equal(string.Empty, transmitter.Status);
    }

    [Fact]
    public void LegacyAutoTleRedirectMigratesWithoutReplacingUserCustomizedUrl()
    {
        Assert.Equal(OrbitSourceSettings.DefaultAutoTleUrl,
            OrbitSourceSettings.ResolveAutoTleUrl(
                OrbitSourceSettings.LegacyAutoTleUrl));
        Assert.Equal("https://example.net/custom.tle",
            OrbitSourceSettings.ResolveAutoTleUrl(
                " https://example.net/custom.tle "));
        Assert.Equal(string.Empty,
            OrbitSourceSettings.ResolveAutoTleUrl(""));
    }

    [Fact]
    public void OrbitFailureQuarantineIsElementSpecificNotNoradWide()
    {
        string testId = Guid.NewGuid().ToString("N");
        var oldElement = new SatnogsDbTle
        {
            norad_cat_id = 44909, tle1 = "1 " + testId,
            tle2 = "2 " + testId
        };
        var replacedElement = new SatnogsDbTle
        {
            norad_cat_id = 44909, tle1 = "1 " + testId,
            tle2 = "2 " + testId + " updated"
        };

        string oldKey = SatelliteTracker.BuildOrbitElementKey(oldElement);
        string freshKey = SatelliteTracker.BuildOrbitElementKey(replacedElement);
        Assert.NotEqual(oldKey, freshKey);
        Assert.True(SatelliteTracker.QuarantineOrbitKey(oldKey));
        Assert.False(SatelliteTracker.QuarantineOrbitKey(oldKey));
        Assert.True(SatelliteTracker.QuarantineOrbitKey(freshKey));
    }

    [Fact]
    public void PrematureWebSocketClosureIsDiagnosedWithoutHidingOtherSocketErrors()
    {
        var close = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        Assert.True(ExceptionLogger.IsExpectedWebSocketClose(close));
        Assert.True(ExceptionLogger.IsExpectedWebSocketClose(
            new AggregateException(close)));
        Assert.False(ExceptionLogger.IsExpectedWebSocketClose(
            new System.Net.Sockets.SocketException(10054)));
    }
}
