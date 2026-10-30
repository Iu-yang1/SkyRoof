using System;
using System.Drawing;
using System.Threading;
using SkyRoof;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class AfcRsBa1PaletteTests
{
  [Fact]
  public void ReferenceLevel_DefaultIsPositiveFifteenAndFinite()
  {
    var settings = new AfcSettings();
    Assert.Equal(15.0, settings.SpectrumRefLevelDb);
    using var view = new AfcSpectrumView();
    Assert.Equal(15.0, view.ReferenceLevelDb);
    view.SetReferenceLevel(double.NaN);
    Assert.Equal(15.0, view.ReferenceLevelDb);
    view.SetReferenceLevel(1000);
    Assert.Equal(60.0, view.ReferenceLevelDb);
    view.SetReferenceLevel(-1000);
    Assert.Equal(-40.0, view.ReferenceLevelDb);
  }

  [Fact]
  public void WaterfallLevelsUseExactNativeIcomClassicPalette()
  {
    int[] native = IcomLanSpectrumView.BuildPalette(
      IcomScopeWaterfallPalette.Classic);
    Assert.Equal(161, native.Length);
    foreach (double power in new[] { -20.0, 0, 10, 20, 30, 50, 70 })
    {
      int index = AfcSpectrumView.MapWaterfallLevel(power, 15);
      Assert.InRange(index, 0, 160);
      Assert.Equal(native[index],
        AfcSpectrumView.WaterfallArgbAt(power, 15));
    }
    Assert.Equal(native[0], AfcSpectrumView.WaterfallArgbAt(-100, 15));
    Assert.Equal(native[160], AfcSpectrumView.WaterfallArgbAt(100, 15));
  }

  [Fact]
  public void IncreasingReferenceLevelReducesDisplayedSignalIntensity()
  {
    const double sourcePower = 24;
    int levelLow = AfcSpectrumView.MapWaterfallLevel(sourcePower, 0);
    int levelDefault = AfcSpectrumView.MapWaterfallLevel(sourcePower, 15);
    int levelHigh = AfcSpectrumView.MapWaterfallLevel(sourcePower, 30);
    Assert.True(levelLow > levelDefault && levelDefault > levelHigh,
      "REF LEVEL must change display headroom, not the sample power");
  }

  [Fact]
  public void ThemeAwareScopeRetainsNavyBackgroundAndCanRecolorHistory()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        using var view = new AfcSpectrumView { Size = new Size(800, 350) };
        float[] spectrum = new float[AfcIfDisplayAnalyzer.BinCount];
        Array.Fill(spectrum, 0);
        spectrum[1065] = 38;
        var frame = new AfcIfDisplayFrame(
          DateTime.UtcNow, 1, spectrum, (float[])spectrum.Clone(),
          -45, .05, 0);
        foreach (ThemeMode mode in new[] {
          ThemeMode.Light, ThemeMode.Dark, ThemeMode.GitHubLight,
          ThemeMode.GitHubDark, ThemeMode.System })
        {
          view.ApplyPalette(AfcThemePalette.For(mode, true));
          view.SetDisplayFrame(frame, 250, 250);
          view.SetReferenceLevel(0);
          view.SetReferenceLevel(15);
          view.SetReferenceLevel(30);
          using var bmp = new Bitmap(800, 350);
          view.DrawToBitmap(bmp, new Rectangle(0, 0, 800, 350));
          Color pixel = bmp.GetPixel(0, 0);
          Assert.True(pixel.B >= pixel.R,
            "RS-BA1 IF instrument background must remain navy in all themes");
        }
      }
      catch (Exception e) { failure = e; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)),
      "WinForms palette regression must not hang");
    if (failure != null)
      throw new Xunit.Sdk.XunitException(failure.ToString());
  }
}
