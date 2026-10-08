using System.Drawing;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class ThemePaletteTests
{
    [Fact]
    public void LightThemeKeepsRequestedBrandColorsExact()
    {
        Assert.Equal(
            Color.FromArgb(0x5B, 0xCE, 0xFA),
            Theme.BrandBlue);
        Assert.Equal(
            Color.FromArgb(0xF5, 0xA9, 0xB8),
            Theme.BrandPink);
        Assert.Equal(
            Color.White,
            Theme.BrandWhite);
    }

    [Fact]
    public void DockThemeUsesBluePinkWhiteHierarchy()
    {
        using var theme =
            new PinkBlueLightDockTheme();

        Assert.Equal(
            Theme.BrandWhite,
            theme.ColorPalette.MainWindowActive.Background);
        Assert.Equal(
            Theme.BrandBlue,
            theme.ColorPalette.ToolWindowCaptionActive.Background);
        Assert.Equal(
            Theme.BrandPink,
            theme.ColorPalette.ToolWindowTabSelectedActive.Background);
        Assert.Equal(
            Theme.PinkWash,
            theme.ColorPalette.ToolWindowCaptionInactive.Background);
        Assert.Equal(
            Theme.BrandWhite,
            theme.ColorPalette.ToolWindowTabUnselected.Background);
    }

    [Fact]
    public void MainToolStripPaletteMatchesDockTheme()
    {
        var table =
            new PinkBlueToolStripColorTable();

        Assert.Equal(
            Theme.BrandWhite,
            table.MenuStripGradientBegin);
        Assert.Equal(
            Theme.BlueWash,
            table.MenuItemSelected);
        Assert.Equal(
            Theme.PinkWash,
            table.MenuItemPressedGradientBegin);
        Assert.Equal(
            Theme.BrandPink,
            table.StatusStripGradientBegin);
    }
}
