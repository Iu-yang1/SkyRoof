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


    [Fact]
    public void ThemeModeKeepsLegacyNumericValuesStable()
    {
        Assert.Equal(
            0,
            (int)ThemeMode.System);
        Assert.Equal(
            1,
            (int)ThemeMode.Light);
        Assert.Equal(
            2,
            (int)ThemeMode.Dark);
        Assert.Equal(
            3,
            (int)ThemeMode.GitHubLight);
        Assert.Equal(
            4,
            (int)ThemeMode.GitHubDark);
    }

    [Fact]
    public void GitHubLightPaletteUsesPrimerStyleCoreColors()
    {
        GitHubPalette p =
            GitHubThemeColors.Light;

        Assert.Equal(
            Color.FromArgb(0xFF, 0xFF, 0xFF),
            p.CanvasDefault);
        Assert.Equal(
            Color.FromArgb(0xF6, 0xF8, 0xFA),
            p.CanvasSubtle);
        Assert.Equal(
            Color.FromArgb(0x24, 0x29, 0x2F),
            p.TextPrimary);
        Assert.Equal(
            Color.FromArgb(0x09, 0x69, 0xDA),
            p.Accent);
        Assert.Equal(
            Color.FromArgb(0xD0, 0xD7, 0xDE),
            p.BorderDefault);
    }

    [Fact]
    public void GitHubDarkPaletteUsesPrimerStyleCoreColors()
    {
        GitHubPalette p =
            GitHubThemeColors.Dark;

        Assert.Equal(
            Color.FromArgb(0x0D, 0x11, 0x17),
            p.CanvasDefault);
        Assert.Equal(
            Color.FromArgb(0x16, 0x1B, 0x22),
            p.CanvasSubtle);
        Assert.Equal(
            Color.FromArgb(0xC9, 0xD1, 0xD9),
            p.TextPrimary);
        Assert.Equal(
            Color.FromArgb(0x58, 0xA6, 0xFF),
            p.Accent);
        Assert.Equal(
            Color.FromArgb(0x30, 0x36, 0x3D),
            p.BorderDefault);
    }

    [Fact]
    public void GitHubLightDockThemeUsesNeutralCanvasAndBlueFocus()
    {
        using var theme =
            new GitHubLightDockTheme();

        Assert.Equal(
            GitHubThemeColors.Light.CanvasDefault,
            theme.ColorPalette.MainWindowActive.Background);
        Assert.Equal(
            GitHubThemeColors.Light.AccentEmphasis,
            theme.ColorPalette.ToolWindowCaptionActive.Background);
        Assert.Equal(
            GitHubThemeColors.Light.AccentMuted,
            theme.ColorPalette.ToolWindowTabSelectedActive.Background);
        Assert.Equal(
            GitHubThemeColors.Light.BorderDefault,
            theme.ColorPalette.ToolWindowBorder);
    }

    [Fact]
    public void GitHubDarkDockThemeUsesGitHubDarkCanvasAndBlueFocus()
    {
        using var theme =
            new GitHubDarkDockTheme();

        Assert.Equal(
            GitHubThemeColors.Dark.CanvasDefault,
            theme.ColorPalette.MainWindowActive.Background);
        Assert.Equal(
            GitHubThemeColors.Dark.AccentEmphasis,
            theme.ColorPalette.ToolWindowCaptionActive.Background);
        Assert.Equal(
            GitHubThemeColors.Dark.AccentMuted,
            theme.ColorPalette.ToolWindowTabSelectedActive.Background);
        Assert.Equal(
            GitHubThemeColors.Dark.BorderDefault,
            theme.ColorPalette.ToolWindowBorder);
    }

    [Fact]
    public void GitHubToolStripPaletteMatchesSelectedGitHubPalette()
    {
        var light =
            new GitHubToolStripColorTable(
                GitHubThemeColors.Light);
        var dark =
            new GitHubToolStripColorTable(
                GitHubThemeColors.Dark);

        Assert.Equal(
            GitHubThemeColors.Light.CanvasInset,
            light.MenuStripGradientBegin);
        Assert.Equal(
            GitHubThemeColors.Light.AccentMuted,
            light.MenuItemSelected);
        Assert.Equal(
            GitHubThemeColors.Light.CanvasSubtle,
            light.StatusStripGradientBegin);

        Assert.Equal(
            GitHubThemeColors.Dark.CanvasInset,
            dark.MenuStripGradientBegin);
        Assert.Equal(
            GitHubThemeColors.Dark.AccentMuted,
            dark.MenuItemSelected);
        Assert.Equal(
            GitHubThemeColors.Dark.CanvasSubtle,
            dark.StatusStripGradientBegin);
    }
}
