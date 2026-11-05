using System;
using System.Web;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class AmsatReportUrlTests
  {
    [Fact]
    public void ReportRequestPreservesFieldsAndClockMargin()
    {
      var timestamp = new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);
      string url = AmsatStatusLoader.BuildAmsatReportUrl(
        "ISS_[FM Voice]", "Telemetry Only", "bg5jsu", "ol79wq", timestamp);

      Assert.StartsWith("https://www.amsat.org/status/submit.php?", url);
      var query = HttpUtility.ParseQueryString(new Uri(url).Query);
      Assert.Equal("yes", query["SatSubmit"]);
      Assert.Equal("yes", query["Confirm"]);
      Assert.Equal("ISS_[FM Voice]", query["SatName"]);
      Assert.Equal("2026", query["SatYear"]);
      Assert.Equal("11", query["SatMonth"]);
      Assert.Equal("05", query["SatDay"]);
      Assert.Equal("07", query["SatHour"]);
      Assert.Equal("3", query["SatPeriod"]);
      Assert.Equal("BG5JSU", query["SatCall"]);
      Assert.Equal("Telemetry Only", query["SatReport"]);
      Assert.Equal("OL79WQ", query["SatGridSquare"]);
      Assert.Equal("SkyRoof", query["App"]);
    }

    [Fact]
    public void ReportRequestClockMarginCrossesYearBoundary()
    {
      var timestamp = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      string url = AmsatStatusLoader.BuildAmsatReportUrl(
        "RS-44", "Heard", "BG5JSU", "OL79WQ", timestamp);
      var query = HttpUtility.ParseQueryString(new Uri(url).Query);

      Assert.Equal("2026", query["SatYear"]);
      Assert.Equal("12", query["SatMonth"]);
      Assert.Equal("31", query["SatDay"]);
      Assert.Equal("23", query["SatHour"]);
      Assert.Equal("3", query["SatPeriod"]);
    }
  }
}
