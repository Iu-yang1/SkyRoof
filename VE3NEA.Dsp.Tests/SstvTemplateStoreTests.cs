using System;
using System.IO;
using SkyRoof.SSTV;
using Xunit;
namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvTemplateStoreTests
  {
    [Fact]
    public void TemplateNamesStylesAndChineseOverlayPersistAcrossReload()
    {
      string path = Path.Combine(Path.GetTempPath(),
        "SkyRoof-template-" + Guid.NewGuid().ToString("N"), "templates.json");
      try
      {
        var first = new SstvUserTemplate("首次通联", "QSL 卡片",
          "CQ SSTV 73", DateTime.UtcNow);
        SstvTemplateStore.Save(path, first);
        Assert.Equal(first, Assert.Single(SstvTemplateStore.Read(path)));
        var updated = first with { OverlayText = "BG5JSU · 73" };
        SstvTemplateStore.Save(path, updated);
        Assert.Equal(updated, Assert.Single(SstvTemplateStore.Read(path)));
        var second = new SstvUserTemplate("卫星图", "卫星横幅", "ARISS", DateTime.UtcNow);
        SstvTemplateStore.Save(path, second);
        Assert.Equal(2, SstvTemplateStore.Read(path).Count);
      }
      finally
      {
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        catch (IOException) { }
      }
    }

    [Fact]
    public void InvalidLayoutCannotBeSaved()
    {
      Assert.Throws<ArgumentException>(() =>
        SstvTemplateStore.Save(
          Path.Combine(Path.GetTempPath(), "skyroof-unused-template.json"),
          new SstvUserTemplate("Too long" + new string('x', 100),
            "Invalid Style", "73", DateTime.UtcNow)));
    }
  }
}
