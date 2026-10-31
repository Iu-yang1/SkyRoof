using System;
using System.IO;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvQsoLogTests
  {
    [Fact]
    public void ManualUtcQsoIsPersistedWithPictureAndExportedToAdif()
    {
      string file = Path.Combine(Path.GetTempPath(),
        "SkyRoofSstvQso-" + Guid.NewGuid().ToString("N") + ".jsonl");
      try
      {
        var q = new SstvQsoRecord(
          new DateTime(2026, 10, 31, 1, 2, 3, DateTimeKind.Utc),
          "BG5JSU", "BG5JSU", "Robot 36",
          "RS-44", 145965000, 435625000,
          "SSTV_image.png", "SSTV image received")
        {
          RstSent = "595", RstReceived = "579",
          ContactName = "Dan", ContactQth = "Switzerland"
        };
        SstvQsoLogStore.Append(file, q);
        SstvQsoRecord loaded = Assert.Single(SstvQsoLogStore.Read(file));
        Assert.Equal(q, loaded);
        string adif = SstvQsoLogStore.ExportAdif(new[] { loaded });
        Assert.Contains("<MODE:4>SSTV", adif);
        Assert.Contains("<RST_SENT:3>595", adif);
        Assert.Contains("<RST_RCVD:3>579", adif);
        Assert.Contains("<NAME:3>Dan", adif);
        Assert.Contains("<QTH:11>Switzerland", adif);
        Assert.Contains("<QSO_DATE:8>20261031", adif);
        Assert.Contains("<TIME_ON:6>010203", adif);
        Assert.Contains("<PROP_MODE:3>SAT", adif);
        Assert.Contains("<SAT_NAME:5>RS-44", adif);
        Assert.Contains("<FREQ:10>145.965000", adif);
        Assert.Contains("<FREQ_RX:10>435.625000", adif);
        Assert.Contains("<EOR>", adif);
      }
      finally { try { File.Delete(file); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("BAD CALL")]
    [InlineData("X")]
    [InlineData("!@#")]
    public void InvalidCallsignCannotBecomeConfirmedQso(string call)
    {
      Assert.Throws<ArgumentException>(() => SstvQsoLogStore.Append(
        Path.Combine(Path.GetTempPath(), "never-written.jsonl"),
        new SstvQsoRecord(DateTime.UtcNow, call, "BG5JSU",
          "Robot 36", "", 0, 0, "", "")));
    }
  }
}
