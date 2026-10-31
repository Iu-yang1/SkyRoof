using System;
using System.IO;
using System.Linq;
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
    public void TextLayerStylesAndCallsignsPersistAndUserTemplatesCanBeDeleted()
    {
      string path = Path.Combine(Path.GetTempPath(),
        "SkyRoof-template-editor-" + Guid.NewGuid().ToString("N"), "templates.json");
      try
      {
        var entry = new SstvUserTemplate("EME QSO", "QSL 卡片",
          "QSO {HISCALL}", DateTime.UtcNow)
        {
          MyCallText = "BG5JSU",
          TheirCallText = "HB9Q",
          MyCallStyle = new SstvTextLayer(.22f, .38f, 12, unchecked((int)0xFFFF0000)),
          TheirCallStyle = new SstvTextLayer(.48f, .6f, 9, unchecked((int)0xFF00FF00)),
          OverlayStyle = new SstvTextLayer(.07f, .82f, 6, unchecked((int)0xFF0080FF))
        };
        SstvTemplateStore.Save(path, entry);
        var loaded = Assert.Single(SstvTemplateStore.Read(path));
        Assert.Equal(entry, loaded);
        Assert.Equal("BG5JSU", loaded.MyCallText);
        Assert.Equal("HB9Q", loaded.TheirCallText);
        Assert.Equal(.22f, loaded.MyCallStyle!.X);
        Assert.Equal(12f, loaded.MyCallStyle.SizePercent);
        Assert.Equal(unchecked((int)0xFF00FF00), loaded.TheirCallStyle!.ColorArgb);
        Assert.True(SstvTemplateStore.Delete(path, "eme qso"));
        Assert.Empty(SstvTemplateStore.Read(path));
        Assert.False(SstvTemplateStore.Delete(path, "no template"));
      }
      finally
      {
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        catch (IOException) { }
      }
    }

    [Fact]
    public void AdditionalCaptionLayersPersistAcrossTemplateReload()
    {
      string path = Path.Combine(Path.GetTempPath(),
        "SkyRoof-multilayer-" + Guid.NewGuid().ToString("N"), "templates.json");
      try
      {
        var extra = new SstvAdditionalCaption[]
        {
          new("{MYCALL} / {HISCALL}",
            new SstvTextLayer(.17f, .4f, 9f, unchecked((int)0xFFFF00FF))),
          new("{RSV_TX} {RSV_RX}",
            new SstvTextLayer(.67f, .77f, 5f, unchecked((int)0xFF11FF22)))
        };
        var entry = new SstvUserTemplate("Multiple", "经典呼号",
          "{SAT}", DateTime.UtcNow) { ExtraCaptions = extra };
        SstvTemplateStore.Save(path, entry);
        var loaded = Assert.Single(SstvTemplateStore.Read(path));
        Assert.NotNull(loaded.ExtraCaptions);
        Assert.Equal(2, loaded.ExtraCaptions!.Length);
        Assert.Equal(extra[0], loaded.ExtraCaptions[0]);
        Assert.Equal(extra[1], loaded.ExtraCaptions[1]);
        var bad = entry with
        {
          ExtraCaptions = Enumerable.Range(0, 6)
            .Select(x => extra[0]).ToArray()
        };
        Assert.Throws<ArgumentException>(() => SstvTemplateStore.Save(path, bad));
      }
      finally
      {
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        catch (IOException) { }
      }
    }

    [Fact]
    public void OlderTemplateJsonAndLayerCoordinateNormalizationRemainCompatible()
    {
      string path = Path.Combine(Path.GetTempPath(),
        "SkyRoof-template-old-" + Guid.NewGuid().ToString("N"), "templates.json");
      try
      {
        SstvTemplateStore.Save(path,
          new SstvUserTemplate("Legacy", "经典呼号", "CQ SSTV", DateTime.UtcNow));
        var old = Assert.Single(SstvTemplateStore.Read(path));
        Assert.Null(old.MyCallStyle);
        Assert.Null(old.MyCallText);
        var normalized = new SstvTextLayer(-3, 2, 300, -1).Normalized();
        Assert.Equal(0, normalized.X);
        Assert.Equal(.99f, normalized.Y);
        Assert.Equal(25, normalized.SizePercent);
        normalized = new SstvTextLayer(float.NaN, float.PositiveInfinity, -4, -1)
          .Normalized();
        Assert.Equal(0, normalized.X);
        Assert.Equal(0, normalized.Y);
        Assert.Equal(2, normalized.SizePercent);
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
