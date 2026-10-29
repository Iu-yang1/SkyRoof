using System.Drawing;
using SkyRoof;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class AfcThemePaletteTests
{
  [Theory]
  [InlineData(ThemeMode.Light, false)]
  [InlineData(ThemeMode.Dark, true)]
  [InlineData(ThemeMode.GitHubLight, false)]
  [InlineData(ThemeMode.GitHubDark, true)]
  public void AllNativeSkyRoofThemesGiveReadableAfcColors(
    ThemeMode mode, bool dark)
  {
    var palette = AfcThemePalette.For(mode);
    Assert.Equal(dark, palette.Dark);
    Assert.NotEqual(palette.Background, palette.Text);
    Assert.NotEqual(palette.Plot, palette.Trace);
    Assert.NotEqual(palette.Accent, palette.Surface);
    Assert.NotEqual(Color.Empty, palette.Warning);
  }

  [Fact]
  public void GithubThemesUseExistingApplicationPalette()
  {
    Assert.Equal(GitHubThemeColors.Dark.CanvasDefault,
      AfcThemePalette.For(ThemeMode.GitHubDark).Background);
    Assert.Equal(GitHubThemeColors.Light.CanvasDefault,
      AfcThemePalette.For(ThemeMode.GitHubLight).Background);
    Assert.Equal(GitHubThemeColors.Dark.Accent,
      AfcThemePalette.For(ThemeMode.GitHubDark).Trace);
  }

  [Fact]
  public void SystemThemeRespectsFrameworkDarkDetection()
  {
    Assert.True(AfcThemePalette.For(ThemeMode.System, true).Dark);
    Assert.False(AfcThemePalette.For(ThemeMode.System, false).Dark);
  }
}
