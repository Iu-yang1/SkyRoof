using System;
using SGPdotNET.Parsers;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class OrbitElementTests
{
    private const string CelestrakCsv =
        "OBJECT_NAME,OBJECT_ID,EPOCH,MEAN_MOTION,ECCENTRICITY,INCLINATION,RA_OF_ASC_NODE,ARG_OF_PERICENTER,MEAN_ANOMALY,EPHEMERIS_TYPE,CLASSIFICATION_TYPE,NORAD_CAT_ID,ELEMENT_SET_NO,REV_AT_EPOCH,BSTAR,MEAN_MOTION_DOT,MEAN_MOTION_DDOT\n" +
        "TEST-OMM,2026-001A,2026-10-08T00:00:00.000000,15.50000000,0.0010000,51.6000,120.0000,80.0000,25.0000,0,U,100001,999,12345,0.0001,0.00001,0.0\n";

    private const string CelestrakJson =
        "[{\"OBJECT_NAME\":\"TEST-OMM\",\"OBJECT_ID\":\"2026-001A\",\"EPOCH\":\"2026-10-08T00:00:00.000000\",\"MEAN_MOTION\":15.5,\"ECCENTRICITY\":0.001,\"INCLINATION\":51.6,\"RA_OF_ASC_NODE\":120.0,\"ARG_OF_PERICENTER\":80.0,\"MEAN_ANOMALY\":25.0,\"EPHEMERIS_TYPE\":0,\"CLASSIFICATION_TYPE\":\"U\",\"NORAD_CAT_ID\":100001,\"ELEMENT_SET_NO\":999,\"REV_AT_EPOCH\":12345,\"BSTAR\":0.0001,\"MEAN_MOTION_DOT\":0.00001,\"MEAN_MOTION_DDOT\":0.0}]";

    [Fact]
    public void DetectsCelestrakOmmCsvAndJson()
    {
        Assert.True(SatnogsDb.LooksLikeOmmCsv(CelestrakCsv));
        Assert.True(SatnogsDb.LooksLikeOmmJson(CelestrakJson));
        Assert.False(SatnogsDb.LooksLikeOmmCsv("ISS\n1 25544U ...\n2 25544 ..."));
        Assert.False(SatnogsDb.LooksLikeOmmJson("[{\"tle1\":\"1 25544U ...\"}]"));
    }

    [Fact]
    public void OmmCsvKeepsSixDigitNoradAndPropagatesDirectly()
    {
        var parsed = new OmmCsvParser().Parse(CelestrakCsv);
        var records = SatnogsDb.TlesFromOmm(parsed, "CelesTrak OMM CSV");

        var record = Assert.Single(records);
        Assert.Equal(100001, record.norad_cat_id);
        Assert.NotNull(record.omm);
        Assert.Empty(record.tle1);
        Assert.Empty(record.tle2);

        var tracker = new SatelliteTracker(record);
        Assert.True(tracker.Enabled);
        Assert.Equal((uint)100001, tracker.Tle!.NoradNumber);
        Assert.NotNull(tracker.Predict(new DateTime(2026, 10, 8, 0, 10, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void OmmJsonKeepsSixDigitNorad()
    {
        var parsed = new OmmJsonParser().Parse(CelestrakJson);
        var record = Assert.Single(SatnogsDb.TlesFromOmm(parsed, "CelesTrak OMM JSON"));

        Assert.Equal(100001, record.norad_cat_id);
        Assert.Equal("TEST-OMM", record.tle0);
        Assert.Equal(new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc), record.updated);
    }

    [Fact]
    public void ManualOrbitWinsForExactlyThreeDaysThenFallsBack()
    {
        var now = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var automatic = new SatnogsDbTle
        {
            tle0 = "AUTO",
            tle_source = "CelesTrak OMM CSV",
            norad_cat_id = 25544,
            updated = now
        };
        var manual = new SatnogsDbTle
        {
            tle0 = "MANUAL",
            tle_source = "Manual file",
            norad_cat_id = 25544,
            updated = now
        };

        var sat = new SatnogsDbSatellite();
        sat.SetAutomaticTle(automatic, now);
        sat.SetManualTle(manual, now + SatnogsDb.ManualOrbitPriorityLifetime, now);

        Assert.Same(manual, sat.Tle);
        Assert.True(sat.HasActiveManualOrbit(now.AddDays(2).AddHours(23)));

        Assert.True(sat.RefreshOrbitSelection(now.AddDays(3)));
        Assert.Same(automatic, sat.Tle);
        Assert.Null(sat.ManualTle);
        Assert.Null(sat.ManualTleExpiresUtc);
    }

    [Fact]
    public void AutomaticFallbackCanRefreshWhileManualOrbitRemainsSelected()
    {
        var now = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var oldAuto = new SatnogsDbTle { tle0 = "OLD", norad_cat_id = 25544 };
        var newCelestrak = new SatnogsDbTle { tle0 = "CELESTRAK", norad_cat_id = 25544 };
        var manual = new SatnogsDbTle { tle0 = "MANUAL", norad_cat_id = 25544 };
        var sat = new SatnogsDbSatellite();

        sat.SetAutomaticTle(oldAuto, now);
        sat.SetManualTle(manual, now.AddDays(3), now);
        sat.SetAutomaticTle(newCelestrak, now.AddDays(1));

        Assert.Same(manual, sat.Tle);
        Assert.Same(newCelestrak, sat.AutomaticTle);

        sat.RefreshOrbitSelection(now.AddDays(3));
        Assert.Same(newCelestrak, sat.Tle);
    }
}
