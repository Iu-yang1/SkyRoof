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
