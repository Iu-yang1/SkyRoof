using System;
using SkyRoof.SSTV;
using Xunit;
namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvTextMacrosTests
  {
    [Fact]
    public void ExpandsStationGridSatelliteAndTimeInPicture()
    {
      var data = new SstvMacroContext("BG5JSU", "OL79WQ", "HB9Q",
        "RS-44", "Martin 1", 145965000, 435625000,
        new DateTime(2026, 10, 31, 1, 2, 3, DateTimeKind.Utc));
      string rendered = SstvTextMacros.Expand(
        "{MYCALL} {MYGRID} {HISCALL} {SAT} {MODE} {UTC} {DATE} {TXHZ} {RXHZ}", data);
      Assert.Contains("BG5JSU OL79WQ HB9Q RS-44 Martin 1 01:02 UTC 2026-10-31", rendered);
      Assert.Contains("145965000", rendered);
      Assert.Contains("435625000", rendered);
      Assert.Equal("{UNKNOWN}", SstvTextMacros.Expand("{UNKNOWN}", data));
    }

    [Fact]
    public void ExpandsRsvAndContactMacrosWithBackwardCompatibleRstAliases()
    {
      var data = new SstvMacroContext("BG5JSU", "OL79WQ", "HB9Q",
        "RS-44", "Robot 36", 145965000, 435625000,
        new DateTime(2026, 10, 31, 1, 2, 3, DateTimeKind.Utc))
      {
        RstSent = "595", RstReceived = "575",
        ContactName = "Dan", ContactQth = "JN47", Notes = "73 via moon"
      };
      string text = SstvTextMacros.Expand(
        "{RSV_TX}|{RSV_RX}|{RST_TX}|{RST_RX}|{NAME}|{QTH}|{NOTE}|{HHMMSS}|{TXMHZ}|{RXMHZ}",
        data);
      Assert.Equal("595|575|595|575|Dan|JN47|73 via moon|01:02:03|145.965000|435.625000", text);
      Assert.Contains(SstvTextMacros.Catalog,
        item => item.Token == "{RSV_TX}");
      Assert.DoesNotContain(SstvTextMacros.Catalog,
        item => item.Token == "{RST_TX}");
    }

    [Fact]
    public void RejectsOverlongOrNonUtcPictureMacros()
    {
      var utc = new SstvMacroContext("", "", "", "", "", 0, 0, DateTime.UtcNow);
      Assert.Throws<ArgumentException>(() =>
        SstvTextMacros.Expand(new string('x', 513), utc));
      var unspecified = utc with { Utc = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified) };
      Assert.Throws<ArgumentException>(() => SstvTextMacros.Expand("{UTC}", unspecified));
    }
  }
}
